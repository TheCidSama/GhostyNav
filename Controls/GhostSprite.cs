using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GhostyNav.Controls;

public enum GhostEyes { Open, Closed, LookLeft, LookRight, LookUp, Happy, Sad }

/// <summary>
/// Pixel-art ghost generated procedurally: a round head, a body with a wavy hem, a 1px dark
/// outline and lavender shading lit from the top left. Frames differ in hem phase and face.
/// </summary>
public static class GhostSprite
{
    public const int Width = 24, Height = 22;
    public const int HemFrames = 6;

    private const uint Clear = 0x00000000, Outline = 0xFF2A2845, Eye = 0xFF1E1C33, Blush = 0xFFF2B4D2;
    private static readonly uint[] Tones = [0xFFFFFFFF, 0xFFE6E4F7, 0xFFBDB9E3, 0xFF928DC8];

    private const double Cx = 12, HeadY = 9.3, Radius = 7.6, HemY = 18.2, HemAmp = 1.35, HemWave = 6;

    private static readonly Dictionary<(int, GhostEyes, int, bool), BitmapSource> Cache = [];

    /// <param name="hem">0..HemFrames-1, the hem's wave position.</param>
    /// <param name="mouth">0 none, 1-3 small to wide open, -1 frown.</param>
    /// <param name="arms">Little arms raised, for celebrating.</param>
    public static BitmapSource Frame(int hem, GhostEyes eyes, int mouth, bool arms)
    {
        var key = (hem % HemFrames, eyes, mouth, arms);
        if (Cache.TryGetValue(key, out var cached)) return cached;
        var pixels = Render(key.Item1 * 2 * Math.PI / HemFrames, eyes, mouth, arms);
        var bitmap = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgra32, null, pixels, Width * 4);
        bitmap.Freeze();
        Cache[key] = bitmap;
        return bitmap;
    }

    public static uint[] Render(double phase, GhostEyes eyes, int mouth, bool arms)
    {
        var px = new uint[Width * Height];
        bool Inside(int x, int y)
        {
            if (x < 0 || y < 0 || x >= Width || y >= Height) return false;
            double fx = x + 0.5, fy = y + 0.5;
            if (arms && Arm(fx, fy)) return true;
            if (fy <= HeadY) return Sq(fx - Cx) + Sq(fy - HeadY) <= Radius * Radius;
            double halfWidth = Radius + (fy - HeadY) * 0.1; // slight flare towards the hem
            return Math.Abs(fx - Cx) <= halfWidth && fy < Hem(fx, phase);
        }

        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            if (!Inside(x, y)) continue;
            double nx = (x + 0.5 - Cx) / 8, ny = (y + 0.5 - 2) / 18;
            double shade = 0.45 * nx + 0.75 * ny;
            int tone = shade < 0.33 ? 0 : shade < 0.6 ? 1 : shade < 0.84 ? 2 : 3;
            // Cloth folds: slightly darker columns above each hem valley.
            if (y > 12 && Math.Cos((x + 0.5 - Cx) * 2 * Math.PI / HemWave + phase) > 0.7) tone = Math.Min(3, tone + 1);
            // Rim shade where the body meets the outline on the right or bottom.
            if (tone < 2 && y > 6 && (!Inside(x + 1, y) || !Inside(x, y + 1))) tone = 2;
            px[y * Width + x] = Tones[tone];
        }

        // 1px outline: empty pixels that touch the body.
        var outline = new List<int>();
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
            if (px[y * Width + x] == Clear && (Inside(x - 1, y) || Inside(x + 1, y) || Inside(x, y - 1) || Inside(x, y + 1)))
                outline.Add(y * Width + x);
        foreach (int i in outline) px[i] = Outline;

        DrawFace(px, eyes, mouth);
        return px;
    }

    private static void DrawFace(uint[] px, GhostEyes eyes, int mouth)
    {
        void Put(int x, int y, uint c) => px[y * Width + x] = c;
        void EyePair(int dx, int dy, int h)
        {
            for (int y = 0; y < h; y++)
            {
                Put(8 + dx, 8 + dy + y, Eye); Put(9 + dx, 8 + dy + y, Eye);
                Put(14 + dx, 8 + dy + y, Eye); Put(15 + dx, 8 + dy + y, Eye);
            }
        }

        switch (eyes)
        {
            case GhostEyes.Open: EyePair(0, 0, 3); break;
            case GhostEyes.LookLeft: EyePair(-1, 0, 3); break;
            case GhostEyes.LookRight: EyePair(1, 0, 3); break;
            case GhostEyes.LookUp: EyePair(1, -1, 3); break;
            case GhostEyes.Closed: EyePair(0, 2, 1); break;
            case GhostEyes.Sad: EyePair(0, 1, 2); break;
            case GhostEyes.Happy:
                foreach (int ox in new[] { 0, 6 })
                {
                    Put(7 + ox, 10, Eye); Put(8 + ox, 9, Eye); Put(9 + ox, 9, Eye); Put(10 + ox, 10, Eye);
                }
                Put(6, 12, Blush); Put(7, 12, Blush); Put(16, 12, Blush); Put(17, 12, Blush);
                break;
        }

        if (mouth == -1)
        {
            Put(10, 14, Eye); Put(11, 13, Eye); Put(12, 13, Eye); Put(13, 14, Eye);
        }
        else
        {
            // Opens downwards from row 13, like a little "o".
            for (int y = 0; y < mouth; y++)
            {
                Put(11, 13 + y - (mouth > 1 ? 1 : 0), Eye);
                Put(12, 13 + y - (mouth > 1 ? 1 : 0), Eye);
            }
        }
    }

    private static bool Arm(double fx, double fy)
    {
        // Raised nubs either side of the body, tilted outwards.
        foreach (double side in new[] { -1.0, 1.0 })
        {
            double ax = Cx + side * 8.6, ay = 11.2;
            double dx = fx - ax, dy = fy - ay;
            double rx = dx * 0.7 - dy * 0.7 * side, ry = dx * 0.7 * side + dy * 0.7;
            if (Sq(rx / 1.3) + Sq(ry / 2.5) <= 1) return true;
        }
        return false;
    }

    private static double Hem(double fx, double phase) => HemY + HemAmp * Math.Sin((fx - Cx) * 2 * Math.PI / HemWave + phase);

    private static double Sq(double v) => v * v;
}
