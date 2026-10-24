using System.Windows.Controls;
using System.Windows.Threading;

namespace GhostyNav.Controls;

public enum MascotMood { Listening, Thinking, Happy, Sad }

/// <summary>
/// The assistant's pixel ghost. Animates in pixel-art steps (~6 fps): the hem ripples, it bobs a
/// pixel up and down, blinks now and then, mouths along while you talk, and glances around while
/// thinking. The timer only runs while <see cref="Animate"/> is on.
/// </summary>
public partial class Mascot : UserControl
{
    private const int Scale = 2;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(160) };
    private MascotMood _mood = MascotMood.Listening;
    private double _level;
    private int _frame, _blinkAt = 20, _hopUntil;
    private DateTime _speakingUntil;

    public Mascot()
    {
        InitializeComponent();
        _tick.Tick += (_, _) =>
        {
            _frame++;
            Draw();
        };
        Draw();
    }

    public void Animate(bool on)
    {
        if (on) _tick.Start();
        else _tick.Stop();
    }

    public void SetMood(MascotMood mood)
    {
        _mood = mood;
        if (mood != MascotMood.Listening) _level = 0;
        if (mood == MascotMood.Happy) _hopUntil = _frame + 4;
        Draw();
    }

    /// <summary>Microphone loudness 0..1: the ghost's mouth opens with your voice.</summary>
    public void SetLevel(double level) => _level = 0.5 * _level + 0.5 * level;

    /// <summary>The ghost mouths along while its reply is read aloud.</summary>
    public void Speak(TimeSpan duration) => _speakingUntil = DateTime.Now + duration;

    private void Draw()
    {
        bool blink = _frame >= _blinkAt;
        if (_frame > _blinkAt)
            _blinkAt = _frame + 15 + Random.Shared.Next(18); // every ~2.5-5.5 s

        var (eyes, mouth, arms) = _mood switch
        {
            MascotMood.Happy => (GhostEyes.Happy, 0, true),
            MascotMood.Sad => (blink ? GhostEyes.Closed : GhostEyes.Sad, -1, false),
            // Glance left, up, right while it thinks.
            MascotMood.Thinking => (blink ? GhostEyes.Closed : (_frame / 5 % 3) switch
            {
                0 => GhostEyes.LookLeft,
                1 => GhostEyes.LookUp,
                _ => GhostEyes.LookRight
            }, 0, false),
            _ => (blink ? GhostEyes.Closed : GhostEyes.Open, _level < 0.12 ? 1 : _level < 0.45 ? 2 : 3, false)
        };
        if (DateTime.Now < _speakingUntil) mouth = (_frame % 4) switch { 0 => 1, 1 => 2, 2 => 3, _ => 2 };
        Sprite.Source = GhostSprite.Frame(_frame, eyes, mouth, arms);

        // Bob one sprite pixel every few frames; a quick double-height hop when it's happy.
        int lift = _frame < _hopUntil ? (_frame % 2 == 0 ? 2 : 1) : (_frame / 4 % 2);
        Float.Y = -lift * Scale;
    }
}
