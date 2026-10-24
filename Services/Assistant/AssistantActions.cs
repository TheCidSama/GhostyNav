using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;

namespace GhostyNav.Services.Assistant;

/// <summary>What an action did, for the result line under the transcript.</summary>
public sealed record ActionResult(string Glyph, string Text, bool Ok = true);

/// <summary>Carries out the tool calls the agent asks for.</summary>
public static class AssistantActions
{
    public static ActionResult Run(AgentCall call, RoutineScheduler routines)
    {
        switch (call.Name)
        {
            case "create_routine":
                return CreateRoutine(call, routines);

            case "delete_routine":
                string id = call.Arg("id");
                var existing = routines.Routines.FirstOrDefault(r => r.Id.Equals(id.Trim(), StringComparison.OrdinalIgnoreCase));
                return existing != null && routines.Remove(existing.Id)
                    ? new ActionResult("\uE74D", $"Removed the routine “{existing.Title}”")
                    : new ActionResult("\uE7BA", "Couldn't find that routine", Ok: false);

            case "open_app":
                string name = call.Arg("name");
                if (AppIndex.Find(name) is not { } app)
                    return new ActionResult("\uE7BA", $"Couldn't find an app called “{name}”", Ok: false);
                AppIndex.Launch(app);
                return new ActionResult("\uE8A7", $"Opening {app.Name}");

            case "search_youtube":
                string video = call.Arg("query");
                return Open("https://www.youtube.com/results?search_query=" + Uri.EscapeDataString(video))
                    ? new ActionResult("\uE714", $"Searching YouTube for “{video}”")
                    : Failed();

            case "search_web":
                string query = call.Arg("query");
                return Open("https://www.google.com/search?q=" + Uri.EscapeDataString(query))
                    ? new ActionResult("\uE721", $"Searching for “{query}”")
                    : Failed();

            case "open_website":
                string url = call.Arg("url").Trim();
                if (!url.Contains("://")) url = "https://" + url;
                // Only web addresses: a model-supplied string must never launch a local file or program.
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                    return new ActionResult("\uE7BA", "That doesn't look like a website address", Ok: false);
                return Open(uri.AbsoluteUri) ? new ActionResult("\uE774", $"Opening {uri.Host}") : Failed();

            default:
                return new ActionResult("\uE7BA", $"I can't do “{call.Name}” yet", Ok: false);
        }
    }

    /// <summary>Runs a routine's scheduled action (reminders are shown by the island instead).</summary>
    public static ActionResult RunRoutine(Routine routine, RoutineScheduler routines) => routine.Action switch
    {
        RoutineAction.OpenApp => Run(new AgentCall("open_app", new JsonObject { ["name"] = routine.Value }), routines),
        RoutineAction.OpenWebsite => Run(new AgentCall("open_website", new JsonObject { ["url"] = routine.Value }), routines),
        RoutineAction.SearchWeb => Run(new AgentCall("search_web", new JsonObject { ["query"] = routine.Value }), routines),
        RoutineAction.SearchYouTube => Run(new AgentCall("search_youtube", new JsonObject { ["query"] = routine.Value }), routines),
        _ => new ActionResult("\uE823", routine.Value)
    };

    private static ActionResult CreateRoutine(AgentCall call, RoutineScheduler routines)
    {
        static ActionResult Problem(string text) => new("\uE7BA", text, Ok: false);

        string title = call.Arg("title").Trim(), value = call.Arg("value").Trim();
        if (value.Length == 0) return Problem("The routine needs something to do");
        if (title.Length == 0) title = value;
        if (title.Length > 60) title = title[..60];

        RoutineAction? action = call.Arg("action") switch
        {
            "remind" => RoutineAction.Remind,
            "open_app" => RoutineAction.OpenApp,
            "open_website" => RoutineAction.OpenWebsite,
            "search_web" => RoutineAction.SearchWeb,
            "search_youtube" => RoutineAction.SearchYouTube,
            _ => null
        };
        if (action == null) return Problem("I can't schedule that kind of action");

        // Check now what can be checked, so a routine doesn't fail silently when it's due.
        if (action == RoutineAction.OpenApp)
        {
            if (AppIndex.Find(value) is not { } app) return Problem($"Couldn't find an app called “{value}”");
            value = app.Name;
        }
        if (action == RoutineAction.OpenWebsite)
        {
            string url = value.Contains("://") ? value : "https://" + value;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                return Problem("That doesn't look like a website address");
            value = uri.AbsoluteUri;
        }

        var routine = new Routine { Title = title, Action = action.Value, Value = value };
        var now = DateTime.Now;
        TimeSpan? time = ParseTime(call.Arg("time"));
        int? minutes = call.Int("minutes");

        switch (call.Arg("repeat"))
        {
            case "once":
                routine.Repeat = RoutineRepeat.Once;
                if (minutes is > 0) routine.NextRun = now.AddMinutes(minutes.Value);
                else if (time is { } at)
                {
                    var day = DateTime.TryParseExact(call.Arg("date"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                        ? d.Date : now.Date;
                    routine.NextRun = day + at;
                    // "At 9" when it's already past 9 today means tomorrow, unless a date was given.
                    if (routine.NextRun <= now && call.Arg("date").Length == 0) routine.NextRun = routine.NextRun.AddDays(1);
                    if (routine.NextRun <= now) return Problem("That time has already passed");
                }
                else return Problem("When should it happen?");
                break;

            case "daily" or "weekdays" or "weekly":
                if (time is not { } t) return Problem("What time of day?");
                routine.TimeOfDay = t;
                routine.Repeat = call.Arg("repeat") switch
                {
                    "daily" => RoutineRepeat.Daily,
                    "weekdays" => RoutineRepeat.Weekdays,
                    _ => RoutineRepeat.Weekly
                };
                if (routine.Repeat == RoutineRepeat.Weekly)
                {
                    routine.Days = call.List("days").Select(ParseDay).OfType<DayOfWeek>().Distinct().ToList();
                    if (routine.Days.Count == 0) return Problem("Which days of the week?");
                }
                if (routine.NextAfter(now) is not { } first) return Problem("Couldn't work out when that runs");
                routine.NextRun = first;
                break;

            case "interval":
                if (minutes is not > 0) return Problem("How often should it repeat?");
                // A floor, so a routine (or a prompt-injected one) can't fire nonstop.
                routine.IntervalMinutes = Math.Max(minutes.Value, RoutineScheduler.MinIntervalMinutes);
                routine.Repeat = RoutineRepeat.Interval;
                routine.NextRun = now.AddMinutes(routine.IntervalMinutes);
                break;

            default:
                return Problem("How often should it run?");
        }

        try
        {
            routines.Add(routine);
        }
        catch (InvalidOperationException ex)
        {
            return Problem(ex.Message);
        }
        return new ActionResult("\uE823", $"Routine added: {routine.Title} · {routine.DescribeSchedule()}");
    }

    private static TimeSpan? ParseTime(string text) =>
        DateTime.TryParseExact(text.Trim(), ["H:mm", "HH:mm", "H:mm:ss", "HH:mm:ss", "H"], CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var t) ? t.TimeOfDay : null;

    private static DayOfWeek? ParseDay(string text) => text.Trim().ToLowerInvariant() switch
    {
        var d when d.StartsWith("mon") => DayOfWeek.Monday,
        var d when d.StartsWith("tue") => DayOfWeek.Tuesday,
        var d when d.StartsWith("wed") => DayOfWeek.Wednesday,
        var d when d.StartsWith("thu") => DayOfWeek.Thursday,
        var d when d.StartsWith("fri") => DayOfWeek.Friday,
        var d when d.StartsWith("sat") => DayOfWeek.Saturday,
        var d when d.StartsWith("sun") => DayOfWeek.Sunday,
        _ => null
    };

    /// <summary>Opens a web address in the default browser.</summary>
    private static bool Open(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Debug.WriteLine($"Open {url} failed: {ex.Message}");
            return false;
        }
    }

    private static ActionResult Failed() => new("\uE7BA", "Couldn't open the browser", Ok: false);
}

/// <summary>
/// The Gemini API key: the one saved in Settings (encrypted in a file next to the other settings,
/// never in the repo), else the GEMINI_API_KEY environment variable.
/// </summary>
public static class GeminiKey
{
    private const string Purpose = "gemini-api-key";
    public static readonly string FilePath = Path.Combine(Store.Folder, "gemini-api-key.dat");
    // Where earlier versions kept the key in plain text; moved into the encrypted file on first read.
    private static readonly string PlainFilePath = Path.Combine(Store.Folder, "gemini-api-key.txt");

    public static string? Saved
    {
        get
        {
            EncryptPlainFile();
            return Read(File.Exists(FilePath) ? Protect.Decrypt(File.ReadAllBytes(FilePath), Purpose) : null);
        }
    }

    public static string? FromEnvironment => Read(Environment.GetEnvironmentVariable("GEMINI_API_KEY"));

    public static string? Load() => Saved ?? FromEnvironment;

    public static void Save(string key)
    {
        Directory.CreateDirectory(Store.Folder);
        File.WriteAllBytes(FilePath, Protect.Encrypt(key.Trim(), Purpose));
    }

    public static void Remove()
    {
        if (File.Exists(FilePath)) File.Delete(FilePath);
        if (File.Exists(PlainFilePath)) File.Delete(PlainFilePath);
    }

    private static void EncryptPlainFile()
    {
        if (!File.Exists(PlainFilePath)) return;
        if (Read(File.ReadAllText(PlainFilePath)) is { } key && !File.Exists(FilePath)) Save(key);
        File.Delete(PlainFilePath);
    }

    /// <summary>Enough of the key to recognise it, not enough to use it: "AQ.Xy12…9zQw".</summary>
    public static string Mask(string key) => key.Length <= 12 ? new string('•', key.Length) : $"{key[..7]}…{key[^4..]}";

    private static string? Read(string? value)
    {
        value = value?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }
}
