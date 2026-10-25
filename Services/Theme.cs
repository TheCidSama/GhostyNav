using System.Windows;
using System.Windows.Media;

namespace GhostyNav.Services;

/// <summary>The accent colour: tab highlights, switches, buttons and the today marker.</summary>
public static class Theme
{
    public sealed record Accent(string Name, Color Main, Color Dim, Color On);

    public static readonly Accent[] Accents =
    [
        new("Orange", Rgb(0xFF8A57), Rgb(0x3A2217), Rgb(0x1E0F07)),
        new("Lavender", Rgb(0xA78BFA), Rgb(0x2A2342), Rgb(0x170F2E)),
        new("Blue", Rgb(0x5AA9FF), Rgb(0x16283D), Rgb(0x061529)),
        new("Green", Rgb(0x7BD88F), Rgb(0x183020), Rgb(0x07200E)),
        new("Pink", Rgb(0xFF7AB6), Rgb(0x3D1A2B), Rgb(0x2A0718)),
    ];

    public static Accent Find(string? name) => Accents.FirstOrDefault(a => a.Name == name) ?? Accents[0];

    /// <summary>Swaps the shared brushes; everything uses them as DynamicResource, so what's on screen follows.</summary>
    public static void Apply(string? name)
    {
        var accent = Find(name);
        Set("AccentBrush", accent.Main);
        Set("AccentDimBrush", accent.Dim);
        Set("OnAccentBrush", accent.On);
    }

    private static void Set(string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        Application.Current.Resources[key] = brush;
    }

    private static Color Rgb(int rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
}
