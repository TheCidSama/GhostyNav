using System.Windows.Threading;

namespace GhostyNav.Services;

public enum TimerPhase { Plain, Focus, Break }

/// <summary>
/// Countdown timer that keeps running while the island is collapsed. It can also run focus
/// sessions: 25-minute focus rounds with 5-minute breaks, and a 15-minute break after every fourth.
/// </summary>
public sealed class TimerService
{
    public static readonly TimeSpan FocusLength = TimeSpan.FromMinutes(25),
        BreakLength = TimeSpan.FromMinutes(5), LongBreakLength = TimeSpan.FromMinutes(15);
    public const int RoundsPerSet = 4;

    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private DateTime _endsAt;
    private TimeSpan _pausedRemaining;

    public TimeSpan Duration { get; private set; } = TimeSpan.FromMinutes(5);
    public bool IsRunning { get; private set; }
    public bool IsActive => IsRunning || _pausedRemaining > TimeSpan.Zero && _pausedRemaining < Duration;
    public TimerPhase Phase { get; private set; } = TimerPhase.Plain;
    /// <summary>Which focus round this is (or the break follows), 1 to <see cref="RoundsPerSet"/>.</summary>
    public int Round { get; private set; } = 1;

    public TimeSpan Remaining => IsRunning
        ? Max(_endsAt - DateTime.Now, TimeSpan.Zero)
        : _pausedRemaining;

    /// <summary>Fires ~4x a second while running, and on every state change.</summary>
    public event Action? Changed;
    /// <summary>A countdown ran out. Carries the phase that ended; in a focus session the next one has already started.</summary>
    public event Action<TimerPhase>? Finished;

    public TimerService()
    {
        _pausedRemaining = Duration;
        _tick.Tick += (_, _) =>
        {
            if (Remaining > TimeSpan.Zero)
            {
                Changed?.Invoke();
                return;
            }
            var ended = Phase;
            IsRunning = false;
            _tick.Stop();
            switch (ended)
            {
                case TimerPhase.Focus:
                    Begin(TimerPhase.Break, Round % RoundsPerSet == 0 ? LongBreakLength : BreakLength);
                    break;
                case TimerPhase.Break:
                    Round = Round % RoundsPerSet + 1;
                    Begin(TimerPhase.Focus, FocusLength);
                    break;
                default:
                    _pausedRemaining = Duration;
                    break;
            }
            Changed?.Invoke();
            Finished?.Invoke(ended);
        };
    }

    public void SetDuration(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) return;
        IsRunning = false;
        _tick.Stop();
        Phase = TimerPhase.Plain;
        Duration = _pausedRemaining = duration;
        Changed?.Invoke();
    }

    /// <summary>Gets a focus session ready (it starts with <see cref="Start"/>).</summary>
    public void SetFocus()
    {
        IsRunning = false;
        _tick.Stop();
        Phase = TimerPhase.Focus;
        Round = 1;
        Duration = _pausedRemaining = FocusLength;
        Changed?.Invoke();
    }

    public void StartFocus()
    {
        SetFocus();
        Start();
    }

    public void Start()
    {
        if (IsRunning || _pausedRemaining <= TimeSpan.Zero) return;
        _endsAt = DateTime.Now + _pausedRemaining;
        IsRunning = true;
        _tick.Start();
        Changed?.Invoke();
    }

    public void Pause()
    {
        if (!IsRunning) return;
        _pausedRemaining = Remaining;
        IsRunning = false;
        _tick.Stop();
        Changed?.Invoke();
    }

    /// <summary>Stops, back to the full length. A focus session ends and the timer is a plain one again.</summary>
    public void Reset()
    {
        IsRunning = false;
        _tick.Stop();
        if (Phase != TimerPhase.Plain)
        {
            Phase = TimerPhase.Plain;
            Duration = FocusLength;
        }
        _pausedRemaining = Duration;
        Changed?.Invoke();
    }

    private void Begin(TimerPhase phase, TimeSpan length)
    {
        Phase = phase;
        Duration = _pausedRemaining = length;
        _endsAt = DateTime.Now + length;
        IsRunning = true;
        _tick.Start();
    }

    /// <summary>"Focus 2 of 4", "Break", or empty for a plain timer.</summary>
    public string PhaseLabel => Phase switch
    {
        TimerPhase.Focus => $"Focus {Round} of {RoundsPerSet}",
        TimerPhase.Break => Round % RoundsPerSet == 0 ? "Long break" : "Break",
        _ => ""
    };

    public static string Format(TimeSpan t)
    {
        // Round up so "0:00" only shows when it's actually done.
        int total = (int)Math.Ceiling(t.TotalSeconds);
        return total >= 3600
            ? $"{total / 3600}:{total / 60 % 60:00}:{total % 60:00}"
            : $"{total / 60:00}:{total % 60:00}";
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}
