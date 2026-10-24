using System.Globalization;
using System.Windows.Threading;

namespace GhostyNav.Services.Assistant;

public enum RoutineAction { Remind, OpenApp, OpenWebsite, SearchWeb, SearchYouTube }
public enum RoutineRepeat { Once, Daily, Weekdays, Weekly, Interval }

/// <summary>Something the assistant was asked to do later, or regularly.</summary>
public sealed class Routine
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public RoutineAction Action { get; set; }
    /// <summary>Reminder text, app name, URL or search query, depending on <see cref="Action"/>.</summary>
    public string Value { get; set; } = "";
    public RoutineRepeat Repeat { get; set; }
    /// <summary>Time of day for daily/weekday/weekly routines.</summary>
    public TimeSpan TimeOfDay { get; set; }
    public List<DayOfWeek> Days { get; set; } = [];
    public int IntervalMinutes { get; set; }
    public DateTime NextRun { get; set; }
    public DateTime Created { get; set; } = DateTime.Now;

    /// <summary>The first run strictly after <paramref name="after"/>, or null when it won't run again.</summary>
    public DateTime? NextAfter(DateTime after)
    {
        switch (Repeat)
        {
            case RoutineRepeat.Once:
                return null;
            case RoutineRepeat.Interval:
                var step = TimeSpan.FromMinutes(Math.Max(1, IntervalMinutes));
                // Keep the original rhythm (every 30 min from 9:00 stays on :00 and :30).
                long missed = Math.Max(1, (long)Math.Ceiling((after - NextRun) / step));
                var next = NextRun + step * missed;
                return next > after ? next : next + step;
            default:
                for (int d = 0; d <= 7; d++)
                {
                    var day = after.Date.AddDays(d);
                    var at = day + TimeOfDay;
                    if (at > after && RunsOn(day.DayOfWeek)) return at;
                }
                return null;
        }
    }

    private bool RunsOn(DayOfWeek day) => Repeat switch
    {
        RoutineRepeat.Weekdays => day is not (DayOfWeek.Saturday or DayOfWeek.Sunday),
        RoutineRepeat.Weekly => Days.Contains(day),
        _ => true
    };

    /// <summary>"Every day at 9:00", "Mon, Wed at 18:30", "Every 30 min", "Once · Sat 24 Oct, 15:00".</summary>
    public string DescribeSchedule()
    {
        var culture = CultureInfo.CurrentCulture;
        string time = DateTime.Today.Add(TimeOfDay).ToString("t", culture);
        return Repeat switch
        {
            RoutineRepeat.Daily => $"Every day at {time}",
            RoutineRepeat.Weekdays => $"Weekdays at {time}",
            RoutineRepeat.Weekly => $"{string.Join(", ", Days.OrderBy(d => ((int)d + 6) % 7).Select(d => culture.DateTimeFormat.GetAbbreviatedDayName(d)))} at {time}",
            RoutineRepeat.Interval => IntervalMinutes % 60 == 0 && IntervalMinutes >= 60
                ? $"Every {IntervalMinutes / 60} h" : $"Every {IntervalMinutes} min",
            _ => $"Once · {NextRun.ToString(NextRun.Date == DateTime.Today ? "t" : "ddd d MMM, t", culture)}"
        };
    }

    public string DescribeAction() => Action switch
    {
        RoutineAction.Remind => $"Remind: {Value}",
        RoutineAction.OpenApp => $"Open {Value}",
        RoutineAction.OpenWebsite => $"Open {Value}",
        RoutineAction.SearchWeb => $"Search: {Value}",
        RoutineAction.SearchYouTube => $"YouTube: {Value}",
        _ => Value
    };
}

/// <summary>
/// Keeps routines in routines.json and fires them on time. Wakes at most every 30 s, and only
/// while there are routines, so sleep, hibernation and clock changes are picked up promptly.
/// </summary>
public sealed class RoutineScheduler
{
    public const int MaxRoutines = 20, MinIntervalMinutes = 5;
    // Late by more than this (the PC was asleep or off): actions are skipped, reminders still shown.
    private static readonly TimeSpan Grace = TimeSpan.FromMinutes(15);

    private readonly List<Routine> _routines = Store.Load<List<Routine>>("routines.json");
    private readonly DispatcherTimer _timer = new();

    /// <summary>A routine is due. The flag says it's late (missed while the PC was off or asleep).</summary>
    public event Action<Routine, bool>? Due;
    public event Action? Changed;

    public IReadOnlyList<Routine> Routines => _routines;

    public RoutineScheduler()
    {
        _timer.Tick += (_, _) => Check();
    }

    public void Start() => Check();

    public Routine Add(Routine routine)
    {
        if (_routines.Count >= MaxRoutines)
            throw new InvalidOperationException($"You already have {MaxRoutines} routines. Remove one in Settings first.");
        routine.Id = NewId();
        _routines.Add(routine);
        Commit();
        return routine;
    }

    public bool Remove(string id)
    {
        if (_routines.RemoveAll(r => r.Id.Equals(id.Trim(), StringComparison.OrdinalIgnoreCase)) == 0) return false;
        Commit();
        return true;
    }

    private void Commit()
    {
        Store.Save("routines.json", _routines);
        Changed?.Invoke();
        Check();
    }

    private void Check()
    {
        _timer.Stop();
        var now = DateTime.Now;
        bool changed = false;
        foreach (var routine in _routines.Where(r => r.NextRun <= now).ToList())
        {
            bool late = now - routine.NextRun > Grace;
            if (!late || routine.Action == RoutineAction.Remind) Due?.Invoke(routine, late);
            if (routine.NextAfter(now) is { } next) routine.NextRun = next;
            else _routines.Remove(routine);
            changed = true;
        }
        if (changed)
        {
            Store.Save("routines.json", _routines);
            Changed?.Invoke();
        }
        if (_routines.Count == 0) return; // nothing scheduled: no timer at all

        var wait = _routines.Min(r => r.NextRun) - DateTime.Now;
        _timer.Interval = wait < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1)
            : wait > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : wait;
        _timer.Start();
    }

    private string NewId()
    {
        const string chars = "abcdefghjkmnpqrstuvwxyz23456789";
        string id;
        do id = new string(Enumerable.Range(0, 4).Select(_ => chars[Random.Shared.Next(chars.Length)]).ToArray());
        while (_routines.Any(r => r.Id == id));
        return id;
    }

    /// <summary>The routines as the assistant sees them, so it can answer questions and delete by id.</summary>
    public string DescribeForModel()
    {
        if (_routines.Count == 0) return "The user has no routines.";
        return "The user's routines:\n" + string.Join("\n", _routines.Select(r =>
            $"- id {r.Id}: \"{r.Title}\" ({r.DescribeAction()}), {r.DescribeSchedule()}, next {r.NextRun:ddd yyyy-MM-dd HH:mm}"));
    }
}
