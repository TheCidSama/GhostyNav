using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace GhostyNav.Services.Assistant;

/// <summary>
/// Streaming speech-to-text over the Gemini Live API (gemini-3.5-transcribe-live). Audio is
/// 16 kHz 16-bit mono PCM. One instance per utterance: start, feed audio, finish.
/// </summary>
public sealed class LiveTranscriber : IAsyncDisposable
{
    private const string Endpoint =
        "wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent";
    private const string Model = "models/gemini-3.5-transcribe-live";

    private readonly ClientWebSocket _socket = new();
    // ClientWebSocket allows one send at a time; a single writer drains this queue.
    private readonly Channel<byte[]> _outgoing = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _complete = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _cts = new();
    private readonly StringBuilder _final = new();
    private string _interim = "";
    private Task? _sender, _receiver;
    private readonly Queue<byte[]> _early = new(); // audio captured before the turn opened
    private bool _started; // guarded by _early

    /// <summary>(final text so far, interim text of the segment in progress). Raised on a worker thread.</summary>
    public event Action<string, string>? TranscriptChanged;

    public string Text => Join(_final.ToString(), _interim);

    /// <summary>Connects and opens the speech turn. Audio sent before this completes is queued, not lost.</summary>
    public async Task StartAsync(string apiKey)
    {
        _socket.Options.SetRequestHeader("x-goog-api-key", apiKey);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await _socket.ConnectAsync(new Uri(Endpoint), timeout.Token);
        _receiver = Task.Run(ReceiveLoopAsync);

        // Sent directly: nothing is queued before the turn opens, so this can't overlap the writer.
        await SendNowAsync(new JsonObject
        {
            ["setup"] = new JsonObject
            {
                ["model"] = Model,
                ["generationConfig"] = new JsonObject { ["responseModalities"] = new JsonArray("TEXT") },
                // Push-to-talk: we say when speech starts and ends, rather than the server guessing.
                ["realtimeInputConfig"] = new JsonObject
                {
                    ["automaticActivityDetection"] = new JsonObject { ["disabled"] = true }
                },
                // SMART cleans up fillers and punctuates; it works best on a whole manually-ended turn.
                ["inputAudioTranscription"] = new JsonObject { ["mode"] = "SMART" }
            }
        });
        await _ready.Task.WaitAsync(TimeSpan.FromSeconds(10), _cts.Token);
        _sender = Task.Run(SendLoopAsync);

        // Open the turn, then flush audio captured while connecting, in order.
        lock (_early)
        {
            _outgoing.Writer.TryWrite(Encode(new JsonObject { ["realtimeInput"] = new JsonObject { ["activityStart"] = new JsonObject() } }));
            while (_early.Count > 0) _outgoing.Writer.TryWrite(_early.Dequeue());
            _started = true;
        }
    }

    public void SendAudio(byte[] pcm)
    {
        var message = Encode(new JsonObject
        {
            ["realtimeInput"] = new JsonObject
            {
                ["audio"] = new JsonObject { ["data"] = Convert.ToBase64String(pcm), ["mimeType"] = "audio/pcm;rate=16000" }
            }
        });
        lock (_early)
        {
            if (_started) _outgoing.Writer.TryWrite(message);
            else _early.Enqueue(message);
        }
    }

    /// <summary>Ends the speech turn and waits for the final transcript.</summary>
    public async Task<string> FinishAsync()
    {
        lock (_early)
        {
            if (!_started) return Text;
        }
        _outgoing.Writer.TryWrite(Encode(new JsonObject { ["realtimeInput"] = new JsonObject { ["activityEnd"] = new JsonObject() } }));
        // The server answers activityEnd with the final transcription and then generationComplete,
        // but sends nothing at all when it heard no speech: don't wait long for that.
        var wait = TimeSpan.FromSeconds(Text.Length > 0 ? 4 : 2);
        try { await _complete.Task.WaitAsync(wait); }
        catch (TimeoutException) { /* use whatever arrived */ }
        return Text;
    }

    private async Task SendNowAsync(JsonObject json) =>
        await _socket.SendAsync(Encode(json), WebSocketMessageType.Text, true, _cts.Token);

    private static byte[] Encode(JsonObject json) => Encoding.UTF8.GetBytes(json.ToJsonString());

    private async Task SendLoopAsync()
    {
        try
        {
            await foreach (var message in _outgoing.Reader.ReadAllAsync(_cts.Token))
                await _socket.SendAsync(message, WebSocketMessageType.Text, true, _cts.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or InvalidOperationException)
        {
            // Socket closed or session cancelled.
        }
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[16 * 1024];
        var message = new MemoryStream();
        try
        {
            while (_socket.State == WebSocketState.Open)
            {
                var result = await _socket.ReceiveAsync(buffer, _cts.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Fail(new IOException(string.IsNullOrEmpty(_socket.CloseStatusDescription)
                        ? "Transcription service closed the connection."
                        : _socket.CloseStatusDescription));
                    break;
                }
                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage) continue;
                Handle(message.ToArray());
                message.SetLength(0);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
            Fail(ex);
        }
    }

    // Server messages arrive as binary frames holding JSON.
    private void Handle(byte[] utf8)
    {
        using var doc = JsonDocument.Parse(utf8);
        var root = doc.RootElement;
        if (root.TryGetProperty("setupComplete", out _))
        {
            _ready.TrySetResult();
            return;
        }
        if (!root.TryGetProperty("serverContent", out var content)) return;

        if (content.TryGetProperty("interimInputTranscription", out var interim) && interim.TryGetProperty("text", out var it))
        {
            // Each interim message is the whole current segment, not a delta.
            _interim = it.GetString() ?? "";
            TranscriptChanged?.Invoke(_final.ToString(), _interim);
        }
        if (content.TryGetProperty("inputTranscription", out var final) && final.TryGetProperty("text", out var ft))
        {
            string text = ft.GetString() ?? "";
            if (text.Length > 0)
            {
                if (_final.Length > 0) _final.Append(' ');
                _final.Append(text.Trim());
            }
            _interim = "";
            TranscriptChanged?.Invoke(_final.ToString(), "");
        }
        if (content.TryGetProperty("generationComplete", out var done) && done.ValueKind == JsonValueKind.True)
            _complete.TrySetResult();
    }

    private void Fail(Exception ex)
    {
        _ready.TrySetException(ex);
        _complete.TrySetResult();
    }

    private static string Join(string a, string b) =>
        a.Length == 0 ? b : b.Length == 0 ? a : a + " " + b;

    public async ValueTask DisposeAsync()
    {
        _outgoing.Writer.TryComplete();
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", timeout.Token);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException) { }
        _cts.Cancel();
        if (_sender != null) await Task.WhenAny(_sender, Task.Delay(500));
        if (_receiver != null) await Task.WhenAny(_receiver, Task.Delay(500));
        _socket.Dispose();
        _cts.Dispose();
    }
}
