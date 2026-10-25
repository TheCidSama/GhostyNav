using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GhostyNav.Services.Assistant;

/// <summary>What an action did, for the result line under the transcript.</summary>
public sealed record ActionResult(string Glyph, string Text, bool Ok = true);

/// <summary>Everything the assistant's tools can reach.</summary>
public sealed record ActionContext(
    MediaService Media,
    AudioService Audio,
    RadioService Radios,
    TimerService Timer,
    AppSettings Settings,
    RoutineScheduler Routines,
    Action<string> TypeText); // types into the app the user was working in

/// <summary>Carries out the tool calls the agent asks for.</summary>
public static class AssistantActions
{
    private const int MaxTypedChars = 4000;

    /// <summary>Tools that act on what the model read: asked about first when the request came with circled areas.</summary>
    public static bool NeedsConfirmation(AgentCall call) => call.Name is "open_website" or "type_text";

    /// <summary>What a confirmation asks, e.g. "Open example.com?".</summary>
    public static string Describe(AgentCall call) => call.Name switch
    {
        "open_website" => $"Open {(Uri.TryCreate(WithScheme(call.Arg("url")), UriKind.Absolute, out var u) ? u.Host : call.Arg("url"))}?",
        "type_text" => $"Type “{Shorten(call.Arg("text"), 60)}”?",
        _ => $"{call.Name}?"
    };

    public static async Task<ActionResult> RunAsync(AgentCall call, ActionContext context)
    {
        try
        {
            switch (call.Name)
            {
                case "media": return await MediaAsync(call, context.Media);
                case "volume": return Volume(call, context.Audio);
                case "set_setting": return await SettingAsync(call, context.Radios);
                case "timer": return Timer(call, context.Timer);
                case "todo": return Todo(call, context.Settings);
                case "copy_text": return Copy(call.Arg("text"));
                case "type_text":
                    string text = call.Arg("text");
                    if (text.Length == 0) return new ActionResult("\uE7BA", "There was nothing to type", Ok: false);
                    if (text.Length > MaxTypedChars) text = text[..MaxTypedChars];
                    context.TypeText(text);
                    return new ActionResult("\uE765", $"Typed “{Shorten(text, 40)}”");
                default: return Run(call, context.Routines);
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"{call.Name} failed: {ex.Message}");
            return new ActionResult("\uE7BA", "That didn't work", Ok: false);
        }
    }

    private static async Task<ActionResult> MediaAsync(AgentCall call, MediaService media)
    {
        if (!media.HasSession) return new ActionResult("\uE7BA", "Nothing is playing", Ok: false);
        string action = call.Arg("action");
        switch (action)
        {
            case "play" or "pause":
                if (media.IsPlaying != (action == "play")) await media.PlayPauseAsync();
                return new ActionResult(action == "play" ? "\uE768" : "\uE769", action == "play" ? $"Playing {media.Title}" : "Paused");
            case "next":
                await media.NextAsync();
                return new ActionResult("\uE893", "Skipped to the next track");
            case "previous":
                await media.PreviousAsync();
                return new ActionResult("\uE892", "Back to the previous track");
            default:
                return new ActionResult("\uE7BA", "I can't do that with the music", Ok: false);
        }
    }

    private static ActionResult Volume(AgentCall call, AudioService audio)
    {
        audio.Refresh();
        if (!audio.IsAvailable) return new ActionResult("\uE7BA", "There's no sound output", Ok: false);
        switch (call.Arg("action"))
        {
            case "mute":
                audio.IsMuted = true;
                return new ActionResult("\uE74F", "Muted");
            case "unmute":
                audio.IsMuted = false;
                return new ActionResult("\uE767", $"Unmuted, at {Math.Round(audio.Volume)}%");
        }
        double? target = call.Arg("action") switch
        {
            "up" => Math.Min(100, audio.Volume + 10),
            "down" => Math.Max(0, audio.Volume - 10),
            _ => call.Int("level")
        };
        if (target is not double level || level < 0) return new ActionResult("\uE7BA", "What level?", Ok: false);
        audio.Volume = Math.Min(level, 100);
        if (audio.IsMuted && level > 0) audio.IsMuted = false;
        return new ActionResult("\uE767", $"Volume {Math.Round(audio.Volume)}%");
    }

    private static async Task<ActionResult> SettingAsync(AgentCall call, RadioService radios)
    {
        if (call.Bool("on") is not bool on) return new ActionResult("\uE7BA", "On or off?", Ok: false);
        string state = on ? "on" : "off";
        switch (call.Arg("setting"))
        {
            case "wifi":
                if (!radios.HasWifi) return new ActionResult("\uE7BA", "This PC has no Wi-Fi", Ok: false);
                await radios.SetWifiAsync(on);
                return new ActionResult("\uE701", $"Wi-Fi {state}");
            case "bluetooth":
                if (!radios.HasBluetooth) return new ActionResult("\uE7BA", "This PC has no Bluetooth", Ok: false);
                await radios.SetBluetoothAsync(on);
                return new ActionResult("\uE702", $"Bluetooth {state}");
            case "dark_mode":
                await SystemToggles.SetDarkModeAsync(on);
                return new ActionResult("\uE708", $"Dark mode {state}");
            case "night_light":
                return SystemToggles.SetNightLight(on)
                    ? new ActionResult("\uE706", $"Night Light {state}")
                    : new ActionResult("\uE7BA", "Turn Night Light on once in Windows Settings first", Ok: false);
            default:
                return new ActionResult("\uE7BA", "I can't change that setting", Ok: false);
        }
    }

    private static ActionResult Timer(AgentCall call, TimerService timer)
    {
        switch (call.Arg("action"))
        {
            case "start":
                double minutes = call.Number("minutes") ?? 0;
                if (minutes <= 0) return new ActionResult("\uE7BA", "For how long?", Ok: false);
                var length = TimeSpan.FromSeconds(Math.Max(1, Math.Round(Math.Min(minutes, 24 * 60) * 60)));
                timer.SetDuration(length);
                timer.Start();
                return new ActionResult("\uE916", $"Timer set for {Describe(length)}");
            case "focus":
                timer.StartFocus();
                return new ActionResult("\uE916", $"Focus for {TimerService.FocusLength.TotalMinutes:0} minutes, then a break");
            case "pause":
                if (!timer.IsRunning) return new ActionResult("\uE7BA", "No timer is running", Ok: false);
                timer.Pause();
                return new ActionResult("\uE769", $"Timer paused at {TimerService.Format(timer.Remaining)}");
            case "resume":
                if (timer.IsRunning || !timer.IsActive) return new ActionResult("\uE7BA", "No timer is paused", Ok: false);
                timer.Start();
                return new ActionResult("\uE768", "Timer resumed");
            case "cancel":
                if (!timer.IsActive) return new ActionResult("\uE7BA", "No timer is running", Ok: false);
                timer.Reset();
                return new ActionResult("\uE711", "Timer cancelled");
            default:
                return new ActionResult("\uE7BA", "I can't do that with the timer", Ok: false);
        }
    }

    private static string Describe(TimeSpan t) =>
        t.TotalMinutes >= 1 && t.Seconds == 0 ? $"{t.TotalMinutes:0} minute{(t.TotalMinutes == 1 ? "" : "s")}"
        : t.TotalMinutes < 1 ? $"{t.TotalSeconds:0} seconds"
        : TimerService.Format(t);

    private static ActionResult Todo(AgentCall call, AppSettings settings)
    {
        string text = call.Arg("text").Trim();
        if (text.Length == 0) return new ActionResult("\uE7BA", "Which to-do?", Ok: false);
        if (call.Arg("action") == "add")
        {
            if (text.Length > 120) text = text[..120];
            settings.Todos.Add(new TodoItem { Text = text });
            settings.Save();
            return new ActionResult("\uE710", $"Added “{text}” to your to-dos");
        }

        var item = settings.Todos.FirstOrDefault(t => t.Text.Equals(text, StringComparison.OrdinalIgnoreCase))
                   ?? settings.Todos.FirstOrDefault(t => t.Text.Contains(text, StringComparison.OrdinalIgnoreCase));
        if (item == null) return new ActionResult("\uE7BA", $"“{text}” isn't on your to-do list", Ok: false);
        if (call.Arg("action") == "remove") settings.Todos.Remove(item);
        else item.Done = true;
        settings.Save();
        return call.Arg("action") == "remove"
            ? new ActionResult("\uE74D", $"Removed “{item.Text}”")
            : new ActionResult("\uE73E", $"Ticked off “{item.Text}”");
    }

    private static ActionResult Copy(string text)
    {
        if (text.Length == 0) return new ActionResult("\uE7BA", "There was nothing to copy", Ok: false);
        System.Windows.Clipboard.SetText(text);
        return new ActionResult("\uE8C8", $"Copied “{Shorten(text, 40)}”");
    }

    private static string Shorten(string text, int max)
    {
        text = text.ReplaceLineEndings(" ").Trim();
        return text.Length <= max ? text : text[..(max - 1)].TrimEnd() + "…";
    }

    private static string WithScheme(string url) => url.Contains("://") ? url.Trim() : "https://" + url.Trim();

    /// <summary>Actions that need nothing but the app list and the routines: what routines can run.</summary>
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
                var (searchUrl, label) = WebSearch(call.Arg("query"), call.Arg("site"));
                return Open(searchUrl) ? new ActionResult("\uE721", label) : Failed();

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

    private static readonly Regex Operator = new(@"\b(?:site|intitle|inurl|intext|filetype):(\S+)", RegexOptions.IgnoreCase);

    /// <summary>
    /// The search to open: the named site's own search when GhostyNav knows it (Amazon, Flipkart...),
    /// else Google. The query is cleaned of what a model tends to add but a person wouldn't type:
    /// quotation marks and operators like site:, which show up in the search box as noise.
    /// </summary>
    public static (string Url, string Label) WebSearch(string query, string site)
    {
        site = site.Trim().ToLowerInvariant();
        if (site.Length == 0 && Regex.Match(query, @"\bsite:(\S+)", RegexOptions.IgnoreCase) is { Success: true } m)
            site = m.Groups[1].Value.ToLowerInvariant();
        // Only fixed addresses: the model picks a site by name, it never supplies a URL here.
        string name = site.Replace("www.", "").Split('.')[0];

        query = Operator.Replace(query, "");
        query = Regex.Replace(query, "[\"“”„]", "");
        // "amazon Milton wallet" with Amazon as the site: the name has done its job.
        if (name.Length > 0) query = Regex.Replace(query, $@"^\s*{Regex.Escape(name)}\b|\b(on\s+)?{Regex.Escape(name)}\s*$", "", RegexOptions.IgnoreCase);
        query = Regex.Replace(query, @"\s+", " ").Trim();
        string q = Uri.EscapeDataString(query);

        (string Name, string Url)? known = name switch
        {
            "" or "google" => null,
            "amazon" => ("Amazon", $"https://www.{AmazonHost(site)}/s?k={q}"),
            "flipkart" => ("Flipkart", $"https://www.flipkart.com/search?q={q}"),
            "ebay" => ("eBay", $"https://www.ebay.com/sch/i.html?_nkw={q}"),
            "wikipedia" => ("Wikipedia", $"https://en.wikipedia.org/w/index.php?search={q}"),
            "reddit" => ("Reddit", $"https://www.reddit.com/search/?q={q}"),
            "github" => ("GitHub", $"https://github.com/search?q={q}"),
            "youtube" => ("YouTube", $"https://www.youtube.com/results?search_query={q}"),
            "stackoverflow" => ("Stack Overflow", $"https://stackoverflow.com/search?q={q}"),
            "maps" or "google maps" => ("Google Maps", $"https://www.google.com/maps/search/{q}"),
            _ => null
        };
        if (known is { } k) return (k.Url, $"Searching {k.Name} for “{query}”");
        // A site GhostyNav doesn't know: its name goes in as an ordinary word.
        if (name.Length > 0) query = $"{query} {name}";
        return ($"https://www.google.com/search?q={Uri.EscapeDataString(query)}", $"Searching for “{query}”");
    }

    /// <summary>The Amazon store the model named (amazon.in), else the one for the user's region.</summary>
    private static string AmazonHost(string site)
    {
        string host = site.Replace("www.", "");
        if (Regex.IsMatch(host, @"^amazon\.(com|in|co\.uk|de|fr|it|es|ca|co\.jp|com\.au|com\.br|com\.mx|nl|se|pl|ae|sa|sg|com\.tr)$"))
            return host;
        return RegionInfo.CurrentRegion.TwoLetterISORegionName switch
        {
            "IN" => "amazon.in",
            "GB" => "amazon.co.uk",
            "DE" or "AT" => "amazon.de",
            "FR" => "amazon.fr",
            "IT" => "amazon.it",
            "ES" => "amazon.es",
            "CA" => "amazon.ca",
            "JP" => "amazon.co.jp",
            "AU" => "amazon.com.au",
            _ => "amazon.com"
        };
    }

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
