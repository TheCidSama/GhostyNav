using System.Diagnostics;
using System.Windows.Threading;
using GhostyNav.Controls;

namespace GhostyNav.Services.Assistant;

/// <summary>What the voice assistant needs from the island window.</summary>
public interface IAssistantView
{
    AssistantPanel Panel { get; }
    void BeginAssistant();
    void SetAssistantStatus(string text);
    void SetAssistantLevel(double level);
    /// <summary>Close the assistant after a while (not while the pointer is on its card).</summary>
    void EndAssistant(TimeSpan after);
    void DismissAssistant();
    /// <summary>The ghost mouths along for this long while the reply is read aloud.</summary>
    void SpeakingFor(TimeSpan duration);
}

/// <summary>
/// Hold Ctrl+Alt and speak: the words are transcribed live, anything circled with the mouse is
/// captured, and on release Gemini decides what to do. Circling something without saying anything
/// sends nothing: the selection waits for the next hold, where the instructions come. Runs on the
/// UI thread.
/// </summary>
public sealed class Assistant : IDisposable
{
    private static readonly TimeSpan MaxHold = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PendingTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ConfirmTimeout = TimeSpan.FromSeconds(20);
    // Follow-ups only make sense shortly after: an old exchange would just confuse a new question.
    private static readonly TimeSpan HistoryLifetime = TimeSpan.FromMinutes(5);
    private const int HistoryTurns = 3;
    private const int LongAnswerChars = 100;

    private readonly Dispatcher _ui;
    private readonly IAssistantView _view;
    private readonly AppSettings _settings;
    private readonly RoutineScheduler _routines;
    private readonly ActionContext _tools;
    private readonly Speaker _speaker = new();
    private readonly KeyChord _chord;
    private readonly List<AgentTurn> _history = [];
    private DateTime _lastTurn;
    private readonly DispatcherTimer _holdWatch = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly DispatcherTimer _pendingTimer = new() { Interval = PendingTimeout };
    private Session? _session;
    // Areas circled with nothing said yet: kept on screen until the next hold says what to do with them.
    private TrailOverlay? _pending;

    private sealed class Session
    {
        public required string Key;
        public required TrailOverlay Overlay;
        public required LiveTranscriber Transcriber;
        public Task Connect = Task.CompletedTask;
        public Microphone? Mic;
        public readonly CancellationTokenSource Cts = new();
        public readonly Stopwatch Clock = Stopwatch.StartNew();
        public bool Listening;
        /// <summary>The circled areas are waiting for instructions: leave the overlay up.</summary>
        public bool KeepOverlay;
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Cts.Cancel();
            Mic?.Dispose();
            if (!KeepOverlay) Overlay.FadeAndClose();
            _ = Transcriber.DisposeAsync().AsTask();
        }
    }

    /// <summary>More facts for the model, such as the next calendar events.</summary>
    public Func<string>? ExtraContext { get; set; }

    /// <summary>The push-to-talk keys, as shown to the user ("Ctrl+Alt").</summary>
    public string ChordName => _chord.Name;

    public Assistant(Dispatcher ui, IAssistantView view, ActionContext tools)
    {
        _ui = ui;
        _view = view;
        _tools = tools;
        _settings = tools.Settings;
        _routines = tools.Routines;
        _chord = new KeyChord(ui, _settings.AssistantChord);
        _chord.Pressed += Begin;
        _chord.Released += () => _ = FinishAsync();
        _chord.Cancelled += () =>
        {
            Stop();
            _view.DismissAssistant();
        };
        // A key-up can go missing (Ctrl+Alt+Del, a hung app); trust the physical key state too.
        _holdWatch.Tick += (_, _) =>
        {
            if (_session is not { Listening: true } s) return;
            if (_chord.IsHeld() && s.Clock.Elapsed < MaxHold) return;
            _chord.Reset();
            _ = FinishAsync();
        };
        // Nobody came back to say what to do with the circled areas: drop them, unsent.
        _pendingTimer.Tick += (_, _) =>
        {
            Stop();
            _view.DismissAssistant();
        };
    }

    public bool IsListening => _session?.Listening == true;

    /// <summary>Switches the push-to-talk keys.</summary>
    public void SetChord(Chord chord) => _chord.Chord = chord;

    private void Begin()
    {
        var carried = TakePending(); // areas circled last time, now getting their instructions
        Stop();
        _view.BeginAssistant();

        string? key = GeminiKey.Load();
        if (key == null)
        {
            carried?.FadeAndClose();
            _view.SetAssistantStatus("No API key");
            _view.Panel.ShowError("Add your Gemini API key",
                "Click the notch, tap the gear in Controls and paste your key.");
            _view.EndAssistant(TimeSpan.FromSeconds(8));
            return;
        }
        _ = AppIndex.GetAsync(); // warm the app list while the user speaks

        var session = new Session
        {
            Key = key, Overlay = carried ?? new TrailOverlay(_settings.AssistantHalo), Transcriber = new LiveTranscriber()
        };
        session.Overlay.Circled += () =>
        {
            if (_session == session) _view.Panel.SetCircled(session.Overlay.Captures.Count);
        };
        if (carried == null) session.Overlay.Show();
        session.Overlay.StartTracking();
        _view.Panel.SetCircled(session.Overlay.Captures.Count);

        session.Transcriber.TranscriptChanged += (final, interim) => _ui.BeginInvoke(() =>
        {
            if (_session == session && session.Listening) _view.Panel.SetTranscript(final, interim);
        });
        session.Connect = session.Transcriber.StartAsync(key);
        // Observed here so an early failure (offline) never goes unobserved; reported on release.
        session.Connect.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);

        session.Mic = new Microphone();
        session.Mic.Chunk += (pcm, level) =>
        {
            session.Transcriber.SendAudio(pcm);
            _ui.BeginInvoke(() =>
            {
                if (_session != session || !session.Listening) return;
                _view.SetAssistantLevel(level);
                session.Overlay.SetLevel(level);
            });
        };
        _session = session;
        try
        {
            session.Mic.Start();
        }
        catch (InvalidOperationException ex)
        {
            Debug.WriteLine(ex.Message);
            Stop();
            _view.SetAssistantStatus("No microphone");
            _view.Panel.ShowError("Couldn't use the microphone",
                "Check Settings › Privacy & security › Microphone › Let desktop apps access your microphone.");
            _view.EndAssistant(TimeSpan.FromSeconds(7));
            return;
        }
        session.Listening = true;
        _view.SetAssistantStatus("Listening");
        _holdWatch.Start();
    }

    private async Task FinishAsync()
    {
        if (_session is not { Listening: true } s) return;
        s.Listening = false;
        _holdWatch.Stop();
        s.Mic?.Dispose();
        s.Mic = null;
        s.Overlay.StopTracking();
        _view.SetAssistantLevel(0);
        _view.SetAssistantStatus("Thinking");
        _view.Panel.ShowThinking();
        _ = SayStillThinkingAsync(s);

        try
        {
            try
            {
                await s.Connect;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !s.Cts.IsCancellationRequested)
            {
                Debug.WriteLine($"Transcription connect failed: {ex.Message}");
                throw new AgentException("Couldn't reach Gemini. Check your connection.");
            }
            string text = await s.Transcriber.FinishAsync();
            if (_session != s) return;
            _view.Panel.SetTranscript(text, "");

            var images = s.Overlay.Captures.ToList();
            if (text.Trim().Length == 0 && images.Count == 0)
            {
                _view.SetAssistantStatus("Didn't hear you");
                _view.Panel.ShowError("I didn't catch that.", $"Hold {ChordName} while you speak, then let go.");
                _view.EndAssistant(TimeSpan.FromSeconds(3.5));
                return;
            }
            if (text.Trim().Length == 0)
            {
                // Circled, but nothing asked: send nothing yet, and wait to be told what to do with it.
                s.KeepOverlay = true;
                _pending = s.Overlay;
                _pendingTimer.Start();
                _view.SetAssistantStatus("Waiting");
                _view.Panel.ShowWaiting(images.Count, ChordName);
                return;
            }
            if (DateTime.Now - _lastTurn > HistoryLifetime) _history.Clear();

            List<AppEntry> apps;
            try { apps = await AppIndex.GetAsync().WaitAsync(TimeSpan.FromSeconds(3), s.Cts.Token); }
            catch (TimeoutException) { apps = AppIndex.Apps; }
            var names = apps.Select(a => a.Name).Take(400).ToList();

            string context = $"Current time: {DateTime.Now:dddd yyyy-MM-dd HH:mm}.\n{_routines.DescribeForModel()}\n{DescribeTodos()}";
            if (ExtraContext?.Invoke() is { Length: > 0 } extra) context += "\n" + extra;
            var reply = await GeminiAgent.AskAsync(s.Key, text, images, names, context, _history, s.Cts.Token);
            if (_session != s) return;
            string asked = images.Count > 0 ? $"(Circled {images.Count} region(s) on screen.) {text.Trim()}" : text.Trim();

            if (reply.Calls.Count > 0)
            {
                var calls = reply.Calls.ToList();
                // Text inside a circled area could be an instruction in disguise: ask before acting on it.
                if (images.Count > 0 && calls.Any(AssistantActions.NeedsConfirmation))
                {
                    _view.SetAssistantStatus("Check");
                    string question = string.Join(" ", calls.Where(AssistantActions.NeedsConfirmation).Select(AssistantActions.Describe));
                    bool allowed = await _view.Panel.ConfirmAsync(question, "You circled something, so I'm checking first.",
                        ConfirmTimeout, s.Cts.Token);
                    if (_session != s) return;
                    if (!allowed) calls.RemoveAll(AssistantActions.NeedsConfirmation);
                    if (calls.Count == 0)
                    {
                        _view.Panel.ShowResults([new ActionResult("\uE711", "Okay, I won't")]);
                        _view.SetAssistantStatus("Cancelled");
                        Remember(asked, "(Cancelled by the user.)");
                        _view.EndAssistant(TimeSpan.FromSeconds(2.5));
                        return;
                    }
                }

                var results = new List<ActionResult>();
                foreach (var call in calls) results.Add(await AssistantActions.RunAsync(call, _tools));
                if (_session != s) return;
                Remember(asked, "Done: " + string.Join("; ", results.Select(r => r.Text)));
                _view.Panel.ShowResults(results);
                _view.SetAssistantStatus(results.All(r => r.Ok) ? "Done" : "Hmm");
                await SpeakThenEndAsync(string.Join(". ", results.Select(r => r.Text)), TimeSpan.FromSeconds(3.5));
            }
            else if (reply.Text.Length > 0)
            {
                Remember(asked, reply.Text);
                _view.Panel.ShowAnswer(reply.Text);
                _view.SetAssistantStatus("Answered");
                // 5 s to read a short answer; 10 s for one longer than about two lines on the card.
                await SpeakThenEndAsync(reply.Text, TimeSpan.FromSeconds(reply.Text.Length > LongAnswerChars ? 10 : 5));
            }
            else
            {
                const string unsure = "I'm not sure how to help with that.";
                _view.Panel.ShowError(unsure);
                _view.SetAssistantStatus("Hmm");
                await SpeakThenEndAsync(unsure, TimeSpan.FromSeconds(4));
            }
        }
        catch (AgentException ex)
        {
            if (_session != s) return;
            _view.Panel.ShowError(ex.Message);
            _view.SetAssistantStatus("Oops");
            await SpeakThenEndAsync(ex.Message, TimeSpan.FromSeconds(5));
        }
        catch (OperationCanceledException) when (s.Cts.IsCancellationRequested)
        {
            // Superseded by a new press, or dismissed.
        }
        finally
        {
            if (_session == s) _session = null;
            s.Dispose();
        }
    }

    private void Remember(string user, string model)
    {
        _history.Add(new AgentTurn(user, model));
        if (_history.Count > HistoryTurns) _history.RemoveAt(0);
        _lastTurn = DateTime.Now;
    }

    private string DescribeTodos()
    {
        var todos = _settings.Todos;
        if (todos.Count == 0) return "To-do list: empty.";
        return "To-do list: " + string.Join("; ", todos.Select(t => (t.Done ? "[done] " : "[ ] ") + t.Text)) + ".";
    }

    /// <summary>Reads text aloud in the chosen voice (for routine reminders); returns how long it lasts.</summary>
    public Task<TimeSpan> SayAsync(string text) => _speaker.SpeakAsync(text, _settings.AssistantVoice);

    /// <summary>
    /// Reads the reply aloud (if the voice is on), then closes the card after
    /// <paramref name="readingTime"/>, or once the voice has finished if it talks for longer.
    /// </summary>
    private async Task SpeakThenEndAsync(string text, TimeSpan readingTime)
    {
        // Quotes around search terms read awkwardly aloud.
        var spoken = await _speaker.SpeakAsync(text.Replace("“", "").Replace("”", ""), _settings.AssistantVoice);
        if (spoken > TimeSpan.Zero) _view.SpeakingFor(spoken);
        var heard = spoken + TimeSpan.FromSeconds(0.5);
        _view.EndAssistant(heard > readingTime ? heard : readingTime);
    }

    private async Task SayStillThinkingAsync(Session s)
    {
        await Task.Delay(TimeSpan.FromSeconds(6));
        if (_session == s && !s.Cts.IsCancellationRequested) _view.Panel.ShowThinking("Still thinking");
    }

    /// <summary>
    /// Abandons the current session, and any circled areas still waiting for instructions. Nothing
    /// more is sent (audio already streamed for transcription has been sent).
    /// </summary>
    public void Stop()
    {
        _holdWatch.Stop();
        _speaker.Stop();
        TakePending()?.FadeAndClose();
        var s = _session;
        _session = null;
        s?.Dispose();
    }

    private TrailOverlay? TakePending()
    {
        _pendingTimer.Stop();
        var pending = _pending;
        _pending = null;
        return pending;
    }

    public void Dispose()
    {
        Stop();
        _speaker.Dispose();
        _chord.Dispose();
    }
}
