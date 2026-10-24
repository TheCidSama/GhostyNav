using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GhostyNav.Services;

/// <summary>Small JSON files under %LOCALAPPDATA%\GhostyNav.</summary>
public static class Store
{
    public static readonly string Folder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GhostyNav");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    /// <summary>Moves data saved before the app was renamed GhostyNav into the new folder, once.</summary>
    public static void MoveFromOldName()
    {
        string old = Path.Combine(Path.GetDirectoryName(Folder)!, "DynamicIsland");
        if (!Directory.Exists(old) || Directory.Exists(Folder)) return;
        try
        {
            Directory.Move(old, Folder);
        }
        catch (IOException ex)
        {
            System.Diagnostics.Debug.WriteLine($"Couldn't move the old settings folder: {ex.Message}");
        }
    }

    public static T Load<T>(string name) where T : new()
    {
        try
        {
            string path = Path.Combine(Folder, name);
            if (File.Exists(path))
                return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) ?? new T();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Could not read {name}: {ex.Message}");
        }
        return new T();
    }

    public static void Save<T>(string name, T value)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            string path = Path.Combine(Folder, name);
            // Write-then-rename so a crash mid-write can't leave a truncated file.
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(value, Options));
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Could not save {name}: {ex.Message}");
        }
    }
}

public sealed class AppSettings
{
    public List<string> StarredApps { get; set; } = [];
    public List<string> RecentApps { get; set; } = [];
    public List<TodoItem> Todos { get; set; } = [];
    public bool TopAppsByCpu { get; set; }
    public SpeedResult? LastSpeed { get; set; }

    /// <summary>The weather location (rounded to about 1 km), encrypted: see <see cref="Protect"/>.</summary>
    public string? ProtectedLocation { get; set; }

    [JsonIgnore]
    public (double Latitude, double Longitude)? Location
    {
        get => Protect.DecryptText(ProtectedLocation, "location")?.Split(',') is [var a, var b] &&
               double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out double lat) &&
               double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out double lon)
            ? (lat, lon) : null;
        set => ProtectedLocation = value is { } v
            ? Protect.EncryptToText(string.Create(CultureInfo.InvariantCulture, $"{v.Latitude},{v.Longitude}"), "location")
            : null;
    }

    // Plain values written by earlier versions; moved into ProtectedLocation when loaded.
    [JsonPropertyName("Latitude"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? PlainLatitude { get; set; }
    [JsonPropertyName("Longitude"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? PlainLongitude { get; set; }
    /// <summary>Blue glow along the screen edges while the voice assistant listens.</summary>
    public bool AssistantHalo { get; set; } = true;
    public Assistant.AssistantVoice AssistantVoice { get; set; } = Assistant.AssistantVoice.Female;

    public void Save() => Store.Save("settings.json", this);

    public static AppSettings Load()
    {
        var settings = Store.Load<AppSettings>("settings.json");
        if (settings.EncryptPlainValues()) settings.Save();
        return settings;
    }

    /// <summary>Encrypts location data that an earlier version stored in plain text.</summary>
    private bool EncryptPlainValues()
    {
        bool changed = false;
        if (PlainLatitude is double lat && PlainLongitude is double lon)
        {
            Location = (lat, lon);
            changed = true;
        }
        if (PlainLatitude != null || PlainLongitude != null)
        {
            PlainLatitude = PlainLongitude = null;
            changed = true;
        }
        if (LastSpeed is { PlainCity: { } city })
        {
            LastSpeed.City = city;
            LastSpeed.PlainCity = null;
            changed = true;
        }
        return changed;
    }
}

public sealed class TodoItem
{
    public string Text { get; set; } = "";
    public bool Done { get; set; }
}

public sealed class SpeedResult
{
    public double DownMbps { get; set; }
    public double UpMbps { get; set; }
    public DateTime When { get; set; }

    /// <summary>The speed-test server's city, which is roughly the user's: encrypted like the location.</summary>
    public string? ProtectedCity { get; set; }

    [JsonIgnore]
    public string? City
    {
        get => Protect.DecryptText(ProtectedCity, "city");
        set => ProtectedCity = Protect.EncryptToText(value, "city");
    }

    [JsonPropertyName("City"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PlainCity { get; set; }
}
