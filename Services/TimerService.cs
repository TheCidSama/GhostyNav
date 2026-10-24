using System.Windows.Threading;

namespace GhostyNav.Services;

/// <summary>Countdown timer that keeps running while the island is collapsed.</summary>
public sealed class TimerService
{
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private DateTime _endsAt;
    private TimeSpan _pausedRemaining;

    public TimeSpan Duration { get; private set; } = TimeSpan.FromMinutes(5);
    public bool IsRunning { get; private set; }
    public bool IsActive => IsRunning || _pausedRemaining > TimeSpan.Zero && _pausedRemaining < Duration;

    public TimeSpan Remaining => IsRunning
        ? Max(_endsAt - DateTime.Now, TimeSpan.Zero)
        : _pausedRemaining;

    /// <summary>Fires ~4x a second while running, and on every state change.</summary>
    public event Action? Changed;
    public event Action? Finished;

    public TimerService()
    {
        _pausedRemaining = Duration;
        _tick.Tick += (_, _) =>
        {
            if (Remaining <= TimeSpan.Zero)
            {
                IsRunning = false;
                _tick.Stop();
                _pausedRemaining = Duration;
                Changed?.Invoke();
                Finished?.Invoke();
                return;
            }
            Changed?.Invoke();
        };
    }

    public void SetDuration(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) return;
        IsRunning = false;
        _tick.Stop();
        Duration = _pausedRemaining = duration;
        Changed?.Invoke();
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

    public void Reset()
    {
        IsRunning = false;
        _tick.Stop();
        _pausedRemaining = Duration;
        Changed?.Invoke();
    }

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
