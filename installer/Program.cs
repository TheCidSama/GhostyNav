using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;

namespace GhostyNav.Setup;

/// <summary>
/// GhostyNav-Setup.exe installs; the same exe without the app appended is uninstall.exe.
/// Command line: /quiet installs (or uninstalls) without a window. With /quiet, /nostartup, /nopath,
/// /desktop and /launch change the install, and /purge also deletes settings on uninstall.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var flags = new HashSet<string>(args.Select(a => a.TrimStart('/', '-').ToLowerInvariant()));
        bool uninstall = flags.Contains("uninstall")
            || string.Equals(Path.GetFileName(Installer.SelfPath), "uninstall.exe", StringComparison.OrdinalIgnoreCase);
        byte[]? payload = uninstall ? null : Payload.Read(Installer.SelfPath);

        int exitCode;
        if (flags.Contains("quiet") || flags.Contains("s"))
        {
            exitCode = RunQuiet(payload, uninstall, flags);
        }
        else if (!uninstall && payload == null)
        {
            MessageBox.Show("This installer is incomplete. Download GhostyNav-Setup.exe again.", "GhostyNav Setup",
                MessageBoxButton.OK, MessageBoxImage.Error);
            exitCode = 1;
        }
        else
        {
            exitCode = new Application().Run(new SetupWindow(payload, uninstall));
        }

        Installer.CleanUpAfterExit();
        return exitCode;
    }

    private static int RunQuiet(byte[]? payload, bool uninstall, HashSet<string> flags)
    {
        var progress = new Progress<(double Done, string Text)>();
        try
        {
            if (uninstall)
            {
                Installer.Uninstall(flags.Contains("purge"), progress);
            }
            else
            {
                if (payload == null) throw new SetupException("This installer is incomplete. Download it again.");
                bool wasRunning = Installer.IsRunning;
                Installer.Install(payload, new InstallOptions
                {
                    StartWithWindows = !flags.Contains("nostartup"),
                    AddToPath = !flags.Contains("nopath"),
                    DesktopShortcut = flags.Contains("desktop"),
                }, progress);
                if (wasRunning || flags.Contains("launch")) Installer.Launch();
            }
            return 0;
        }
        catch (Exception e)
        {
            // No window to show it in: leave the reason where a script can find it
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "GhostyNav-Setup.log"), $"{DateTime.Now:s} {e}");
            return 1;
        }
    }
}
