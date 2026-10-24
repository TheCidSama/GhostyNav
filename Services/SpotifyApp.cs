using System.Diagnostics;
using System.IO;

namespace GhostyNav.Services;

/// <summary>Finds and opens Spotify, installed either from spotify.com or the Microsoft Store.</summary>
public static class SpotifyApp
{
    private const string StorePackage = "SpotifyAB.SpotifyMusic_zpdnekdrzrea0";

    private static string DesktopExe =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Spotify", "Spotify.exe");

    // Windows creates (and on uninstall removes) a per-user data folder for every Store package.
    private static string StoreDataFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages", StorePackage);

    public static bool IsInstalled => File.Exists(DesktopExe) || Directory.Exists(StoreDataFolder);

    public static void Open()
    {
        try
        {
            var start = File.Exists(DesktopExe)
                ? new ProcessStartInfo(DesktopExe) { UseShellExecute = true }
                : new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{StorePackage}!Spotify") { UseShellExecute = true };
            Process.Start(start);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Could not open Spotify: {ex.Message}");
        }
    }
}
