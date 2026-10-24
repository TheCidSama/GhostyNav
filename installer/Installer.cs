using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace GhostyNav.Setup;

internal sealed class InstallOptions
{
    public bool StartWithWindows = true;
    public bool AddToPath = true;
    public bool DesktopShortcut;
}

/// <summary>A failure with a message meant for the user.</summary>
internal sealed class SetupException : Exception
{
    public SetupException(string message) : base(message) { }
}

/// <summary>
/// Installs GhostyNav for the current user and removes it again. Nothing here needs admin rights;
/// only Microsoft's .NET runtime installer, when the runtime is missing, asks for them.
/// </summary>
internal static class Installer
{
    public const string AppName = "GhostyNav";
    public const string RuntimePage = "https://dotnet.microsoft.com/download/dotnet/10.0";
    private const string RuntimeUrl = "https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\GhostyNav";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RepoUrl = "https://github.com/TheCidSama/GhostyNav";

    private static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    public static readonly string InstallDir = Path.Combine(LocalAppData, "Programs", AppName);
    public static readonly string ExePath = Path.Combine(InstallDir, AppName + ".exe");
    private static readonly string DataDir = Path.Combine(LocalAppData, AppName);
    public static readonly string SelfPath = Assembly.GetExecutingAssembly().Location;

    private static string StartMenuLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk");
    private static string DesktopLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk");

    private static bool _removeSelfOnExit;

    public static bool IsInstalled => File.Exists(ExePath);

    public static string? InstalledVersion
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
            return key?.GetValue("DisplayVersion") as string;
        }
    }

    public static bool IsRunning => OurSessionsProcesses().Any();

    /// <summary>What the current install is set up with, so a reinstall offers the same choices.</summary>
    public static InstallOptions CurrentOptions()
    {
        using var run = Registry.CurrentUser.OpenSubKey(RunKey);
        using var env = Registry.CurrentUser.OpenSubKey("Environment");
        var path = env?.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
        return new InstallOptions
        {
            StartWithWindows = PointsHere(run?.GetValue(AppName) as string),
            AddToPath = path.Split(';').Any(IsInstallDir),
            DesktopShortcut = File.Exists(DesktopLink),
        };
    }

    // ---------- Install ----------

    public static void Install(byte[] payload, InstallOptions options, IProgress<(double Done, string Text)> progress)
    {
        double start = 0;
        if (!HasDesktopRuntime())
        {
            InstallRuntime(progress);
            start = 0.6;
        }

        progress.Report((start, "Closing GhostyNav…"));
        CloseRunning(installedOnly: false); // any copy: a second one would just exit at start
        Retry(ClearInstallDir);

        Extract(payload, done => progress.Report((start + (0.95 - start) * done, "Copying files…")));

        progress.Report((0.96, "Adding shortcuts…"));
        CreateShortcut(StartMenuLink);
        if (options.DesktopShortcut) CreateShortcut(DesktopLink);
        else File.Delete(DesktopLink);
        SetStartup(options.StartWithWindows);
        SetPath(options.AddToPath);
        Register();
        progress.Report((1, "Done"));
    }

    public static void Launch() =>
        Process.Start(new ProcessStartInfo(ExePath) { UseShellExecute = true, WorkingDirectory = InstallDir })?.Dispose();

    private static void Extract(byte[] payload, Action<double> progress)
    {
        Directory.CreateDirectory(InstallDir);
        var root = InstallDir + Path.DirectorySeparatorChar;
        using var zip = new ZipArchive(new MemoryStream(payload, writable: false), ZipArchiveMode.Read);
        long total = zip.Entries.Sum(e => e.Length), copied = 0;
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.Replace('/', '\\');
            var target = Path.GetFullPath(Path.Combine(InstallDir, name));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new SetupException("This installer is damaged. Download it again.");
            if (name.EndsWith("\\"))
            {
                Directory.CreateDirectory(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using (var input = entry.Open())
            using (var output = File.Create(target))
                input.CopyTo(output);
            copied += entry.Length;
            progress(total == 0 ? 1 : (double)copied / total);
        }
    }

    private static void ClearInstallDir()
    {
        if (!Directory.Exists(InstallDir)) return;
        foreach (var dir in Directory.GetDirectories(InstallDir)) Directory.Delete(dir, recursive: true);
        foreach (var file in Directory.GetFiles(InstallDir)) File.Delete(file);
    }

    /// <summary>Lists GhostyNav in Settings › Apps, with its own uninstaller.</summary>
    private static void Register()
    {
        var uninstaller = Path.Combine(InstallDir, "uninstall.exe");
        long bytes = new DirectoryInfo(InstallDir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKey);
        key.SetValue("DisplayName", AppName);
        key.SetValue("DisplayVersion", FileVersionInfo.GetVersionInfo(ExePath).ProductVersion ?? "");
        key.SetValue("Publisher", "GhostyNav contributors");
        key.SetValue("DisplayIcon", ExePath);
        key.SetValue("InstallLocation", InstallDir);
        key.SetValue("UninstallString", $"\"{uninstaller}\"");
        key.SetValue("QuietUninstallString", $"\"{uninstaller}\" /quiet");
        key.SetValue("URLInfoAbout", RepoUrl);
        key.SetValue("HelpLink", RepoUrl + "/issues");
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
        key.SetValue("EstimatedSize", (int)(bytes / 1024), RegistryValueKind.DWord);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    // ---------- Uninstall ----------

    public static void Uninstall(bool removeData, IProgress<(double Done, string Text)> progress)
    {
        progress.Report((0.1, "Closing GhostyNav…"));
        CloseRunning(installedOnly: true);

        progress.Report((0.3, "Removing shortcuts…"));
        File.Delete(StartMenuLink);
        File.Delete(DesktopLink);
        SetStartup(false);
        SetPath(false);
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);

        if (removeData && Directory.Exists(DataDir))
        {
            progress.Report((0.5, "Deleting your settings…"));
            Retry(() => Directory.Delete(DataDir, recursive: true));
        }

        progress.Report((0.7, "Removing files…"));
        if (Directory.Exists(InstallDir))
        {
            bool runningFromInstall = string.Equals(Path.GetDirectoryName(SelfPath), InstallDir, StringComparison.OrdinalIgnoreCase);
            Retry(() =>
            {
                foreach (var dir in Directory.GetDirectories(InstallDir)) Directory.Delete(dir, recursive: true);
                foreach (var file in Directory.GetFiles(InstallDir))
                    if (!string.Equals(file, SelfPath, StringComparison.OrdinalIgnoreCase)) File.Delete(file);
            });
            if (runningFromInstall) _removeSelfOnExit = true; // a running exe can't delete itself
            else Directory.Delete(InstallDir, recursive: true);
        }
        progress.Report((1, "Done"));
    }

    /// <summary>When the uninstaller ran from the install folder, removes the folder once it has exited.</summary>
    public static void CleanUpAfterExit()
    {
        if (!_removeSelfOnExit) return;
        Process.Start(new ProcessStartInfo("cmd.exe", $"/d /c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"{InstallDir}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetTempPath(),
        })?.Dispose();
    }

    // ---------- Pieces ----------

    private static IEnumerable<Process> OurSessionsProcesses()
    {
        int session = Process.GetCurrentProcess().SessionId;
        foreach (var process in Process.GetProcessesByName(AppName))
        {
            if (process.SessionId == session) yield return process;
            else process.Dispose();
        }
    }

    private static void CloseRunning(bool installedOnly)
    {
        foreach (var process in OurSessionsProcesses())
        {
            using (process)
            {
                if (installedOnly && !IsInstalledCopy(process)) continue;
                try
                {
                    if (!process.CloseMainWindow() || !process.WaitForExit(3000))
                    {
                        process.Kill();
                        process.WaitForExit(5000);
                    }
                }
                catch (InvalidOperationException)
                {
                    // It exited between being listed and being closed, which is what we wanted.
                }
                catch (Win32Exception)
                {
                    throw new SetupException("GhostyNav is running and couldn't be closed. Right-click the notch, choose Exit, and try again.");
                }
            }
        }
    }

    private static bool IsInstalledCopy(Process process)
    {
        try
        {
            return string.Equals(process.MainModule?.FileName, ExePath, StringComparison.OrdinalIgnoreCase);
        }
        catch (Win32Exception)
        {
            return false; // not ours to inspect (elevated), so not ours to close either
        }
    }

    private static void CreateShortcut(string path)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(ExePath);
            link.SetWorkingDirectory(InstallDir);
            link.SetDescription("A notch for your Windows desktop, with a little ghost who listens");
            link.SetIconLocation(ExePath, 0);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            ((IPersistFile)link).Save(path, true);
        }
        finally
        {
            Marshal.ReleaseComObject(link);
        }
    }

    private static void SetStartup(bool enabled)
    {
        using var run = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) run.SetValue(AppName, $"\"{ExePath}\"");
        else if (PointsHere(run.GetValue(AppName) as string)) run.DeleteValue(AppName, throwOnMissingValue: false);
    }

    private static bool PointsHere(string? command) =>
        command != null && command.IndexOf(InstallDir, StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>Adds the install folder to (or removes it from) the user's PATH, leaving the other entries untouched.</summary>
    private static void SetPath(bool add)
    {
        using var env = Registry.CurrentUser.CreateSubKey("Environment");
        var current = env.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        var kind = current == null ? RegistryValueKind.ExpandString : env.GetValueKind("Path");
        var parts = (current ?? "").Split(';').ToList();
        bool listed = parts.Any(IsInstallDir);

        string updated;
        if (add)
        {
            if (listed) return;
            // Keep a trailing separator where there was one, so removing the entry restores PATH exactly
            updated = string.IsNullOrEmpty(current) ? InstallDir
                : current!.EndsWith(";") ? current + InstallDir + ";"
                : current + ";" + InstallDir;
        }
        else
        {
            if (!listed) return;
            parts.RemoveAll(IsInstallDir);
            updated = string.Join(";", parts);
        }

        if (updated.Length == 0) env.DeleteValue("Path", throwOnMissingValue: false);
        else env.SetValue("Path", updated, kind == RegistryValueKind.String ? RegistryValueKind.String : RegistryValueKind.ExpandString);

        // Tell Explorer, so terminals opened from now on see the change
        SendMessageTimeout(new IntPtr(0xFFFF), 0x001A /* WM_SETTINGCHANGE */, IntPtr.Zero, "Environment", 0x0002 /* SMTO_ABORTIFHUNG */, 3000, out _);
    }

    private static bool IsInstallDir(string entry) =>
        string.Equals(Environment.ExpandEnvironmentVariables(entry.Trim().Trim('"')).TrimEnd('\\'), InstallDir, StringComparison.OrdinalIgnoreCase);

    private static void Retry(Action action)
    {
        // Files can stay locked for a moment after the app that used them has exited
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception e) when ((e is IOException || e is UnauthorizedAccessException) && attempt < 20)
            {
                Thread.Sleep(250);
            }
        }
    }

    // ---------- .NET runtime ----------

    public static bool HasDesktopRuntime()
    {
        var roots = new List<string>();
        if (Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } fromEnvironment) roots.Add(fromEnvironment);
        using (var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32))
        using (var key = machine.OpenSubKey(@"SOFTWARE\dotnet\Setup\InstalledVersions\x64"))
            if (key?.GetValue("InstallLocation") is string registered) roots.Add(registered);
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet"));

        return roots.Any(root =>
        {
            var versions = Path.Combine(root, "shared", "Microsoft.WindowsDesktop.App");
            return Directory.Exists(versions) && Directory.GetDirectories(versions, "10.*").Length > 0;
        });
    }

    private static void InstallRuntime(IProgress<(double Done, string Text)> progress)
    {
        const string step = "Downloading the .NET 10 Desktop Runtime from Microsoft";
        var file = Path.Combine(Path.GetTempPath(), "GhostyNav-dotnet-desktop-runtime-10.exe");
        progress.Report((0, step + "…"));
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        try
        {
            using var web = new WebClient();
            web.DownloadProgressChanged += (_, e) => progress.Report((0.45 * e.ProgressPercentage / 100,
                $"{step}… {e.BytesReceived / 1048576} of {Math.Max(e.TotalBytesToReceive, 0) / 1048576} MB"));
            web.DownloadFileTaskAsync(RuntimeUrl, file).GetAwaiter().GetResult();
        }
        catch (WebException)
        {
            throw new SetupException($"Couldn't download the .NET 10 Desktop Runtime. Check your connection, or install it from {RuntimePage} and run Setup again.");
        }

        // It runs with admin rights, so make sure it really is Microsoft's
        if (!IsSignedByMicrosoft(file))
        {
            File.Delete(file);
            throw new SetupException($"The .NET runtime download couldn't be verified. Install it from {RuntimePage} and run Setup again.");
        }

        progress.Report((0.5, "Installing the .NET 10 Desktop Runtime…"));
        Process? runtimeSetup;
        try
        {
            runtimeSetup = Process.Start(new ProcessStartInfo(file, "/install /quiet /norestart") { UseShellExecute = true, Verb = "runas" });
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223) // ERROR_CANCELLED: the permission prompt was declined
        {
            throw new SetupException($"GhostyNav needs the .NET 10 Desktop Runtime. Allow Windows' permission prompt, or install it from {RuntimePage}.");
        }
        using (runtimeSetup)
            runtimeSetup?.WaitForExit();
        Retry(() => File.Delete(file));

        if (!HasDesktopRuntime())
            throw new SetupException($"The .NET 10 Desktop Runtime didn't install. Install it from {RuntimePage} and run Setup again.");
    }

    private static bool IsSignedByMicrosoft(string file)
    {
        var info = new WinTrustFileInfo { cbStruct = Marshal.SizeOf<WinTrustFileInfo>(), pcwszFilePath = file };
        var infoPtr = Marshal.AllocHGlobal(info.cbStruct);
        try
        {
            Marshal.StructureToPtr(info, infoPtr, false);
            var data = new WinTrustData
            {
                cbStruct = Marshal.SizeOf<WinTrustData>(),
                dwUIChoice = 2,    // WTD_UI_NONE
                dwUnionChoice = 1, // WTD_CHOICE_FILE
                pFile = infoPtr,
            };
            if (WinVerifyTrust(new IntPtr(-1), GenericVerifyV2, ref data) != 0) return false;
        }
        finally
        {
            Marshal.DestroyStructure<WinTrustFileInfo>(infoPtr);
            Marshal.FreeHGlobal(infoPtr);
        }
        var signer = X509Certificate.CreateFromSignedFile(file);
        return signer.Subject.IndexOf("O=Microsoft Corporation", StringComparison.Ordinal) >= 0;
    }

    // ---------- Native ----------

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public int cbStruct;
        public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public int cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public int dwUIChoice;
        public int fdwRevocationChecks;
        public int dwUnionChoice;
        public IntPtr pFile;
        public int dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public int dwProvFlags;
        public int dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid action, ref WinTrustData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, int msg, IntPtr wParam, string lParam, int flags, int timeout, out IntPtr result);

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int cch, IntPtr findData, int flags);
        void GetIDList(out IntPtr pidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int cch, out int icon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int icon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pathRel, int reserved);
        void Resolve(IntPtr hwnd, int flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }
}

/// <summary>The zipped app that build.ps1 appends to the setup exe, followed by its length and a marker.</summary>
internal static class Payload
{
    private static readonly byte[] Marker = Encoding.ASCII.GetBytes("GHOSTYZ1");

    public static byte[]? Read(string exe)
    {
        using var file = File.OpenRead(exe);
        if (file.Length < 16) return null;
        var trailer = ReadAt(file, file.Length - 16, 16);
        if (!trailer.Skip(8).SequenceEqual(Marker)) return null;
        long length = BitConverter.ToInt64(trailer, 0);
        if (length <= 0 || length > file.Length - 16) return null;
        return ReadAt(file, file.Length - 16 - length, (int)length);
    }

    private static byte[] ReadAt(Stream stream, long offset, int count)
    {
        stream.Position = offset;
        var buffer = new byte[count];
        for (int read = 0; read < count;)
        {
            int n = stream.Read(buffer, read, count - read);
            if (n == 0) throw new EndOfStreamException();
            read += n;
        }
        return buffer;
    }
}
