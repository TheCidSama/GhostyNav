using System.Windows.Threading;
using Windows.ApplicationModel.Appointments;

namespace GhostyNav.Services;

public sealed record CalendarEvent(string Title, DateTime Start, TimeSpan Duration, string Location, bool AllDay);

/// <summary>
/// Upcoming events from the Windows calendar (the accounts added in the Calendar or Outlook app).
/// Read every 15 minutes; a heads-up is raised five minutes before each one starts.
/// </summary>
public sealed class CalendarService
{
    private static readonly TimeSpan HeadsUp = TimeSpan.FromMinutes(5);
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMinutes(15) };
    private readonly DispatcherTimer _alarm = new();
    private readonly HashSet<string> _announced = [];
    private AppointmentStore? _store;

    /// <summary>The next 48 hours, soonest first.</summary>
    public IReadOnlyList<CalendarEvent> Upcoming { get; private set; } = [];
    public bool Available { get; private set; }

    public event Action? Changed;
    /// <summary>An event starts in about five minutes.</summary>
    public event Action<CalendarEvent>? StartingSoon;

    public CalendarService()
    {
        _refresh.Tick += async (_, _) => await RefreshAsync();
        _alarm.Tick += (_, _) =>
        {
            _alarm.Stop();
            var now = DateTime.Now;
            foreach (var e in Upcoming.Where(e => !e.AllDay && e.Start - now <= HeadsUp + TimeSpan.FromSeconds(30) && e.Start > now))
                if (_announced.Add(Key(e))) StartingSoon?.Invoke(e);
            ArmAlarm();
        };
    }

    public async Task StartAsync()
    {
        await RefreshAsync();
        _refresh.Start();
    }

    public async Task RefreshAsync()
    {
        try
        {
            _store ??= await AppointmentManager.RequestStoreAsync(AppointmentStoreAccessType.AllCalendarsReadOnly);
            var options = new FindAppointmentsOptions { MaxCount = 30 };
            foreach (var p in new[] { AppointmentProperties.Subject, AppointmentProperties.StartTime, AppointmentProperties.Duration,
                         AppointmentProperties.Location, AppointmentProperties.AllDay })
                options.FetchProperties.Add(p);
            var found = await _store.FindAppointmentsAsync(DateTimeOffset.Now.AddHours(-1), TimeSpan.FromHours(49), options);
            var now = DateTime.Now;
            Upcoming = found
                .Select(a => new CalendarEvent(string.IsNullOrWhiteSpace(a.Subject) ? "Busy" : a.Subject.Trim(),
                    a.StartTime.LocalDateTime, a.Duration, a.Location ?? "", a.AllDay))
                .Where(e => e.Start + e.Duration > now)
                .OrderBy(e => e.Start)
                .ToList();
            Available = true;
        }
        catch (Exception ex)
        {
            // No calendar access (Settings › Privacy › Calendar) or no accounts set up.
            System.Diagnostics.Debug.WriteLine($"Calendar unavailable: {ex.Message}");
            Upcoming = [];
            Available = false;
        }
        ArmAlarm();
        Changed?.Invoke();
    }

    /// <summary>The next event that hasn't ended, today or tomorrow.</summary>
    public CalendarEvent? Next => Upcoming.FirstOrDefault(e => !e.AllDay) ?? Upcoming.FirstOrDefault();

    /// <summary>For the assistant: what's coming up.</summary>
    public string DescribeForModel() => Upcoming.Count == 0
        ? "Calendar: nothing in the next two days."
        : "Calendar: " + string.Join("; ", Upcoming.Take(8).Select(e =>
            e.AllDay ? $"{e.Title} (all day {e.Start:ddd})" : $"{e.Title} ({e.Start:ddd HH:mm}-{e.Start + e.Duration:HH:mm})")) + ".";

    public static string When(CalendarEvent e)
    {
        if (e.AllDay) return e.Start.Date == DateTime.Today ? "All day today" : $"All day {e.Start:dddd}";
        var now = DateTime.Now;
        if (e.Start <= now) return $"Now · until {e.Start + e.Duration:t}";
        var until = e.Start - now;
        if (until < TimeSpan.FromHours(1)) return $"In {Math.Max(1, (int)Math.Ceiling(until.TotalMinutes))} min · {e.Start:t}";
        return e.Start.Date == DateTime.Today ? $"Today {e.Start:t}" : $"{e.Start:ddd} {e.Start:t}";
    }

    private void ArmAlarm()
    {
        _alarm.Stop();
        var now = DateTime.Now;
        var next = Upcoming.Where(e => !e.AllDay && !_announced.Contains(Key(e)) && e.Start > now)
            .Select(e => e.Start - HeadsUp).OrderBy(t => t).FirstOrDefault();
        if (next == default || next - now > _refresh.Interval) return; // the next refresh re-arms it
        _alarm.Interval = next > now ? next - now : TimeSpan.FromSeconds(1);
        _alarm.Start();
    }

    private static string Key(CalendarEvent e) => $"{e.Title}|{e.Start:O}";
}
