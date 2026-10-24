using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using Windows.Devices.Geolocation;

namespace GhostyNav.Services;

public sealed record Weather(double Temperature, string Unit, string Description, string Glyph);

/// <summary>Current weather from Open-Meteo (free, no API key) for the Windows location.</summary>
public sealed class WeatherService(AppSettings settings)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private Weather? _cached;
    private DateTime _fetched;

    public string? Error { get; private set; }

    /// <summary>Cached for 30 minutes so opening the tab repeatedly doesn't hit the network.</summary>
    /// <param name="locate">
    /// Ask Windows for the location. Only on an explicit click: it can raise a system permission prompt.
    /// Otherwise the last saved location is used, and nothing happens until there is one.
    /// </param>
    public async Task<Weather?> GetAsync(bool locate)
    {
        if (!locate && _cached != null && DateTime.Now - _fetched < TimeSpan.FromMinutes(30)) return _cached;
        if (!locate && settings.Location == null)
        {
            Error = "Click for weather";
            return null;
        }
        try
        {
            var (lat, lon) = locate ? await LocateAsync() : settings.Location!.Value;
            bool metric = RegionInfo.CurrentRegion.IsMetric;
            string url = string.Create(CultureInfo.InvariantCulture,
                $"https://api.open-meteo.com/v1/forecast?latitude={lat:0.00}&longitude={lon:0.00}" +
                $"&current=temperature_2m,weather_code,is_day&temperature_unit={(metric ? "celsius" : "fahrenheit")}");
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
            var current = doc.RootElement.GetProperty("current");
            int code = current.GetProperty("weather_code").GetInt32();
            bool day = current.GetProperty("is_day").GetInt32() == 1;
            var (text, glyph) = Describe(code, day);
            _cached = new Weather(current.GetProperty("temperature_2m").GetDouble(), metric ? "°C" : "°F", text, glyph);
            _fetched = DateTime.Now;
            Error = null;
        }
        catch (UnauthorizedAccessException)
        {
            Error = "Location is off";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Weather failed: {ex.Message}");
            Error = "Weather unavailable";
        }
        return _cached;
    }

    private async Task<(double, double)> LocateAsync()
    {
        try
        {
            if (await Geolocator.RequestAccessAsync() != GeolocationAccessStatus.Allowed)
                throw new UnauthorizedAccessException();
            var position = await new Geolocator { DesiredAccuracy = PositionAccuracy.Default }
                .GetGeopositionAsync(TimeSpan.FromMinutes(30), TimeSpan.FromSeconds(8));
            // City-level precision is all weather needs; remember it for when location is unavailable.
            settings.Location = (Math.Round(position.Coordinate.Point.Position.Latitude, 2),
                                 Math.Round(position.Coordinate.Point.Position.Longitude, 2));
            settings.Save();
        }
        catch when (settings.Location != null)
        {
            // Fall back to the last known location.
        }
        return settings.Location ?? throw new UnauthorizedAccessException();
    }

    // WMO weather interpretation codes, mapped onto the few weather glyphs Segoe Fluent Icons has.
    private static (string, string) Describe(int code, bool day) => code switch
    {
        0 => (day ? "Clear" : "Clear night", day ? "\uE706" : "\uE708"),
        1 => ("Mostly clear", day ? "\uE706" : "\uE708"),
        2 => ("Partly cloudy", "\uE753"),
        3 => ("Cloudy", "\uE753"),
        45 or 48 => ("Fog", "\uE753"),
        >= 51 and <= 57 => ("Drizzle", "\uEB42"),
        >= 61 and <= 67 or >= 80 and <= 82 => ("Rain", "\uEB42"),
        >= 71 and <= 77 or 85 or 86 => ("Snow", "\uE9CA"),
        >= 95 => ("Thunderstorm", "\uE945"),
        _ => ("—", "\uE753")
    };
}
