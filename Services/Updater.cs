using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GhostyNav.Services;

public sealed record UpdateInfo(Version Version, string DownloadUrl, string Sha256, string PageUrl);

/// <summary>
/// Looks for a newer GhostyNav on GitHub (once a day, if allowed in Settings) and installs it: the
/// release's GhostyNav-Setup.exe is downloaded, checked against the SHA-256 GitHub publishes for it,
/// and run quietly, which closes this copy and starts the new one.
/// </summary>
public static partial class Updater
{
    private const string LatestRelease = "https://api.github.com/repos/TheCidSama/GhostyNav/releases/latest";
    private const string AssetName = "GhostyNav-Setup.exe";

    public static Version Current { get; } = Assembly.GetExecutingAssembly().GetName().Version is { } v
        ? new Version(v.Major, v.Minor, Math.Max(v.Build, 0))
        : new Version(0, 0, 0);

    public static string CurrentText => $"{Current.Major}.{Current.Minor}.{Current.Build}";

    // After Current: static fields are set up in the order they're written, and the client's name uses it.
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"GhostyNav/{CurrentText}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <returns>The newer release, or null when this is the latest (or GitHub can't be reached).</returns>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(LatestRelease, ct));
            var root = doc.RootElement;
            if (!Version.TryParse(root.GetProperty("tag_name").GetString()?.TrimStart('v', 'V'), out var latest)) return null;
            latest = new Version(latest.Major, latest.Minor, Math.Max(latest.Build, 0));
            if (latest <= Current) return null;

            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                if (asset.GetProperty("name").GetString() != AssetName) continue;
                // GitHub's own digest of the upload; else the checksum written in the release notes.
                string sha = asset.TryGetProperty("digest", out var d) && d.GetString() is { } digest && digest.StartsWith("sha256:")
                    ? digest["sha256:".Length..]
                    : Sha256Pattern().Match(root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "").Value;
                if (sha.Length != 64) return null; // can't be verified, so it isn't offered
                return new UpdateInfo(latest, asset.GetProperty("browser_download_url").GetString()!, sha.ToLowerInvariant(),
                    root.GetProperty("html_url").GetString() ?? "");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            Debug.WriteLine($"Update check failed: {ex.Message}");
        }
        return null;
    }

    /// <summary>Downloads and verifies the installer, then starts it; the caller should exit right after.</summary>
    /// <returns>null when the installer started, else what went wrong.</returns>
    public static async Task<string?> InstallAsync(UpdateInfo update, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        string file = Path.Combine(Path.GetTempPath(), $"GhostyNav-Setup-{update.Version}.exe");
        try
        {
            using (var response = await Http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                long total = response.Content.Headers.ContentLength ?? 0;
                await using var input = await response.Content.ReadAsStreamAsync(ct);
                await using var output = File.Create(file);
                var buffer = new byte[81920];
                long done = 0;
                for (int n; (n = await input.ReadAsync(buffer, ct)) > 0;)
                {
                    await output.WriteAsync(buffer.AsMemory(0, n), ct);
                    done += n;
                    if (total > 0) progress?.Report((double)done / total);
                }
            }

            string actual;
            await using (var stream = File.OpenRead(file))
                actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
            if (actual != update.Sha256)
            {
                File.Delete(file);
                return "The download didn't match its checksum, so it wasn't run.";
            }

            Process.Start(new ProcessStartInfo(file, "/quiet /launch") { UseShellExecute = true })?.Dispose();
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or System.ComponentModel.Win32Exception)
        {
            Debug.WriteLine($"Update failed: {ex.Message}");
            return "Couldn't download the update. Try again later.";
        }
    }

    [GeneratedRegex("(?<![0-9a-fA-F])[0-9a-fA-F]{64}(?![0-9a-fA-F])")]
    private static partial Regex Sha256Pattern();
}
