using System.Diagnostics;
using System.IO;

namespace GhostyNav.Services;

public sealed record AppEntry(string Name, string Id, bool IsGame);

/// <summary>
/// The real apps from the Start menu's "All apps" list (desktop and Store apps), read from the
/// shell's AppsFolder: no admin tools, command-line shells, SDK tools, docs, links or uninstallers.
/// Indexed once on a background STA thread, refreshed on demand.
/// </summary>
public static class AppIndex
{
    private static readonly string[] GameHints =
        ["steam://", "steamapps", "epic games", "riot", "ubisoft", "ea games", "xbox games", "gog galaxy\\games", "minecraft", "roblox"];
    private static readonly string[] Junk =
        ["uninstall", "readme", "help", "documentation", "website", "release notes", "license", "manual", "installer", "tutorial"];

    private static readonly string WindowsFolder = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    // System32 programs are admin tools and consoles (Event Viewer, Services, Command Prompt...), except these.
    private static readonly HashSet<string> KeptWindowsPrograms = new(StringComparer.OrdinalIgnoreCase) { "taskmgr.exe", "mstsc.exe" };
    // Shells that are windowed programs, so the console check alone wouldn't catch them.
    private static readonly HashSet<string> Shells = new(StringComparer.OrdinalIgnoreCase) { "git-bash.exe", "bash.exe", "mintty.exe" };
    private const string FileExplorer = "::{52205FD8-5DFB-447D-801A-D0B52F2E83E1}", ControlPanel = "::{5399E694-6CE5-4D6C-8FCE-1D8870FDCBA0}";

    private static Task<List<AppEntry>>? _loading;
    private static DateTime _loaded;

    /// <summary>The last loaded list (empty until the first load finishes).</summary>
    public static List<AppEntry> Apps { get; private set; } = [];

    /// <summary>
    /// Shared by the Apps tab and the assistant. Rescans at most every 10 minutes to pick up
    /// newly installed apps; concurrent callers share one scan.
    /// </summary>
    public static Task<List<AppEntry>> GetAsync()
    {
        if (_loading != null && (!_loading.IsCompleted || DateTime.Now - _loaded < TimeSpan.FromMinutes(10)))
            return _loading;
        _loading = LoadAsync().ContinueWith(t =>
        {
            Apps = t.Result;
            _loaded = DateTime.Now;
            return t.Result;
        }, TaskScheduler.Default);
        return _loading;
    }

    /// <summary>Best match for a spoken app name, or null.</summary>
    public static AppEntry? Find(string name)
    {
        name = name.Trim();
        if (name.Length == 0) return null;
        return Apps.Select(a => (App: a, Score: a.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ? 10 : Score(a.Name, name)))
                   .Where(m => m.Score >= 2)
                   .OrderByDescending(m => m.Score).ThenBy(m => m.App.Name.Length)
                   .Select(m => m.App).FirstOrDefault();
    }

    private static Task<List<AppEntry>> LoadAsync()
    {
        var tcs = new TaskCompletionSource<List<AppEntry>>();
        var thread = new Thread(() =>
        {
            try { tcs.SetResult(Enumerate()); }
            catch (Exception ex)
            {
                Debug.WriteLine($"App index failed: {ex.Message}");
                tcs.SetResult([]);
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); // shell COM objects need STA
        thread.Start();
        return tcs.Task;
    }

    private static List<AppEntry> Enumerate()
    {
        var type = Type.GetTypeFromProgID("Shell.Application") ?? throw new InvalidOperationException("No shell");
        dynamic shell = Activator.CreateInstance(type)!;
        dynamic folder = shell.NameSpace("shell:AppsFolder");
        var apps = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (dynamic item in folder.Items())
        {
            string name = item.Name;
            string id = item.Path;
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(id)) continue;

            string lowerName = name.ToLowerInvariant(), lowerId = id.ToLowerInvariant();
            string target = "";
            try { target = item.ExtendedProperty("System.Link.TargetParsingPath") as string ?? ""; }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
            if (!IsApp(lowerName, lowerId, target)) continue;

            bool isGame = GameHints.Any(h => lowerId.Contains(h) || lowerName.Contains(h))
                          && lowerName != "steam" && !lowerName.Contains("launcher");
            apps.TryAdd(name, new AppEntry(name, id, isGame));
        }
        return apps.Values.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Is this Start-menu entry an app someone would open, rather than a tool, shell or document?</summary>
    private static bool IsApp(string lowerName, string lowerId, string target)
    {
        if (Junk.Any(lowerName.Contains) || lowerName.EndsWith(".exe") || lowerId.StartsWith("http")) return false;
        // Store apps have no file target.
        if (target.Length == 0) return true;
        if (target.StartsWith("::")) return target.Equals(FileExplorer, StringComparison.OrdinalIgnoreCase) ||
                                             target.Equals(ControlPanel, StringComparison.OrdinalIgnoreCase);
        if (target.Contains("://")) return !target.StartsWith("http", StringComparison.OrdinalIgnoreCase); // steam://, other launchers

        // Only programs: not .msc consoles, documents, web links, scripts, installers, archives or folders.
        string extension = Path.GetExtension(target);
        if (!extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".appref-ms", StringComparison.OrdinalIgnoreCase))
            return false;
        string file = Path.GetFileName(target);
        if (target.StartsWith(WindowsFolder, StringComparison.OrdinalIgnoreCase)) return KeptWindowsPrograms.Contains(file);
        if (target.Contains(@"\Windows Kits\", StringComparison.OrdinalIgnoreCase)) return false; // SDK tools
        if (Shells.Contains(file)) return false;
        return !IsConsoleProgram(target);
    }

    /// <summary>A command-line program (node.exe, python.exe, mysql.exe...), from its PE header.</summary>
    private static bool IsConsoleProgram(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            var header = new byte[1024];
            int read = file.Read(header, 0, header.Length);
            if (read < 0x40 || header[0] != 'M' || header[1] != 'Z') return false;
            int pe = BitConverter.ToInt32(header, 0x3C);
            if (pe <= 0 || pe + 0x5E > read || header[pe] != 'P' || header[pe + 1] != 'E') return false;
            const ushort IMAGE_SUBSYSTEM_WINDOWS_CUI = 3;
            return BitConverter.ToUInt16(header, pe + 0x5C) == IMAGE_SUBSYSTEM_WINDOWS_CUI;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false; // can't tell: keep it
        }
    }

    public static void Launch(AppEntry app)
    {
        try
        {
            // AppsFolder parsing names launch both Win32 shortcuts and packaged apps.
            Process.Start(new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{app.Id}") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Launch {app.Name} failed: {ex.Message}");
        }
    }

    /// <summary>Higher is better; 0 means no match.</summary>
    public static double Score(string name, string query)
    {
        if (query.Length == 0) return 1;
        int index = name.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (index == 0) return 4;
        if (index > 0) return name[index - 1] == ' ' ? 3 : 2;

        // Initials ("vsc" -> Visual Studio Code) and in-order subsequences.
        var initials = string.Concat(name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => w[0]));
        if (initials.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 2.5;
        int q = 0;
        foreach (char c in name)
            if (q < query.Length && char.ToLowerInvariant(c) == char.ToLowerInvariant(query[q])) q++;
        return q == query.Length ? 0.5 : 0;
    }
}
