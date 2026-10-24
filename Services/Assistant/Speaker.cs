using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Media;
using Windows.Media.SpeechSynthesis;

namespace GhostyNav.Services.Assistant;

public enum AssistantVoice { Off, Female, Male }

/// <summary>
/// Reads the assistant's replies aloud with the voices built into Windows: instant, offline, and
/// no Gemini quota spent. One utterance at a time; a new one (or <see cref="Stop"/>) cuts it off.
/// </summary>
public sealed class Speaker : IDisposable
{
    private SpeechSynthesizer? _synth;
    private SoundPlayer? _player;
    private int _generation;

    /// <returns>How long the speech lasts; zero when the voice is off or nothing could be spoken.</returns>
    public async Task<TimeSpan> SpeakAsync(string text, AssistantVoice voice)
    {
        Stop();
        if (voice == AssistantVoice.Off || string.IsNullOrWhiteSpace(text)) return TimeSpan.Zero;
        int generation = _generation;
        try
        {
            _synth ??= new SpeechSynthesizer();
            if (PickVoice(voice) is { } info) _synth.Voice = info;
            _synth.Options.SpeakingRate = 1.08;
            using var stream = await _synth.SynthesizeTextToStreamAsync(text);
            var wav = new MemoryStream();
            await stream.AsStreamForRead().CopyToAsync(wav);
            if (generation != _generation) return TimeSpan.Zero; // superseded while synthesizing
            wav.Position = 0;
            _player = new SoundPlayer(wav);
            _player.Play(); // plays on its own thread
            return Duration(wav);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Speech failed: {ex.Message}");
            return TimeSpan.Zero;
        }
    }

    public void Stop()
    {
        _generation++;
        _player?.Stop();
        _player?.Dispose();
        _player = null;
    }

    /// <summary>The installed voice of the wanted gender, preferring the display language, then English.</summary>
    private static VoiceInformation? PickVoice(AssistantVoice voice)
    {
        var gender = voice == AssistantVoice.Male ? VoiceGender.Male : VoiceGender.Female;
        string language = CultureInfo.CurrentUICulture.Name;
        return SpeechSynthesizer.AllVoices
            .Where(v => v.Gender == gender)
            .OrderByDescending(v => v.Language.Equals(language, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(v => v.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
    }

    /// <summary>Length of a PCM WAV from its header (byte rate and data chunk size).</summary>
    private static TimeSpan Duration(MemoryStream wav)
    {
        var bytes = wav.GetBuffer();
        int length = (int)wav.Length, byteRate = 0;
        for (int pos = 12; pos + 8 <= length;)
        {
            string id = System.Text.Encoding.ASCII.GetString(bytes, pos, 4);
            int size = BitConverter.ToInt32(bytes, pos + 4);
            if (id == "fmt " && size >= 12) byteRate = BitConverter.ToInt32(bytes, pos + 16);
            if (id == "data") return byteRate > 0 ? TimeSpan.FromSeconds((double)size / byteRate) : TimeSpan.Zero;
            pos += 8 + size + (size & 1);
        }
        return TimeSpan.Zero;
    }

    public void Dispose()
    {
        Stop();
        _synth?.Dispose();
    }
}
