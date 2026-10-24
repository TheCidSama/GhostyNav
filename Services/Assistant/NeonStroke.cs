using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GhostyNav.Services.Assistant;

/// <summary>
/// Neon lavender strokes for the circling trail. The cursor samples are joined by a spline, and
/// each glow layer is drawn as one filled shape rather than many overlapping segments, so joints
/// can't double up into visible beads. Layers stack from a wide faint glow to a near-white core.
/// </summary>
internal static class NeonStroke
{
    private sealed record Layer(double HalfWidth, SolidColorBrush Brush, Pen Pen, bool Core);

    private static readonly Layer[] Layers = BuildLayers();

    /// <summary>How far the glow reaches from the centre line.</summary>
    public const double Reach = 16;
    private const double Spacing = 4; // spline resolution, in pixels
    private const double FullLength = 28; // shorter trails shrink, so a stopping trail fades rather than pops

    /// <summary>
    /// The glow is nested bands 1.5 px apart (outermost first) whose combined opacity follows a bell
    /// curve, so each step is a tiny change of a few percent: it reads as one continuous falloff.
    /// Then a lavender-white tube and a white-hot core on top.
    /// </summary>
    private static Layer[] BuildLayers()
    {
        const double outer = Reach, inner = 3, sigma = 7, peak = 0.6;
        Color edge = Color.FromRgb(0xA7, 0x8B, 0xFA), centre = Color.FromRgb(0xC2, 0xAE, 0xFF);
        var layers = new List<Layer>();
        double covered = 0; // combined opacity of the bands so far
        for (double h = outer; h >= inner; h -= 1.5)
        {
            double target = peak * Math.Exp(-Math.Pow((h - 1) / sigma, 2)); // opacity just inside this band
            double alpha = 1 - (1 - target) / (1 - covered);
            covered = target;
            double k = (outer - h) / (outer - inner);
            var color = Color.FromRgb((byte)(edge.R + (centre.R - edge.R) * k), (byte)(edge.G + (centre.G - edge.G) * k),
                (byte)(edge.B + (centre.B - edge.B) * k));
            layers.Add(Make(alpha, color, h, core: false));
        }
        layers.Add(Make(0.8, Color.FromRgb(0xD8, 0xCC, 0xFF), 2.5, core: true));
        layers.Add(Make(1, Color.FromRgb(0xFB, 0xF9, 0xFF), 1.15, core: true));
        return [.. layers];
    }

    private static Layer Make(double alpha, Color color, double halfWidth, bool core)
    {
        var brush = new SolidColorBrush(Color.FromArgb((byte)Math.Clamp(Math.Round(alpha * 255), 1, 255), color.R, color.G, color.B));
        brush.Freeze();
        var pen = new Pen(brush, halfWidth * 2)
        {
            StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round
        };
        pen.Freeze();
        return new Layer(halfWidth, brush, pen, core);
    }

    /// <summary>
    /// The moving trail, oldest sample first. Rounded at the head; towards the tail the bright core
    /// thins out first and the glow narrows to a point, so it ends in a soft wisp.
    /// </summary>
    public static void DrawTrail(DrawingContext dc, IReadOnlyList<Point> samples)
    {
        if (samples.Count < 2) return;
        var c = Smooth(samples, closed: false);
        int n = c.Count;

        var along = new double[n];
        for (int i = 1; i < n; i++) along[i] = along[i - 1] + (c[i] - c[i - 1]).Length;
        double length = along[n - 1];
        if (length < 0.5) return;

        var normals = new Vector[n];
        for (int i = 0; i < n; i++)
        {
            var tangent = c[Math.Min(i + 1, n - 1)] - c[Math.Max(i - 1, 0)];
            normals[i] = tangent.Length < 1e-6
                ? (i > 0 ? normals[i - 1] : new Vector(0, 1))
                : new Vector(-tangent.Y, tangent.X) / tangent.Length;
        }

        double scale = Math.Min(1, length / FullLength);
        var left = new Point[n];
        var right = new Point[n];
        foreach (var layer in Layers)
        {
            double head = layer.HalfWidth * scale;
            for (int i = 0; i < n; i++)
            {
                double f = along[i] / length; // 0 at the tail, 1 at the head
                double half = head * (layer.Core ? SmoothStep(0.25, 1, f) : Math.Pow(f, 0.7));
                left[i] = c[i] + normals[i] * half;
                right[i] = c[i] - normals[i] * half;
                if (i == 0) continue;
                // On the inside of a tight bend the offset edge would fold back on itself: hold it instead.
                var forward = c[i] - c[i - 1];
                if ((left[i] - left[i - 1]) * forward < 0) left[i] = left[i - 1];
                if ((right[i] - right[i - 1]) * forward < 0) right[i] = right[i - 1];
            }

            // One geometry per layer: quads plus a round head, all wound the same way, so where they
            // overlap (joints, or the trail crossing itself) Nonzero fills the area once.
            var shape = new StreamGeometry { FillRule = FillRule.Nonzero };
            using (var ctx = shape.Open())
            {
                for (int i = 0; i + 1 < n; i++)
                {
                    ctx.BeginFigure(left[i], isFilled: true, isClosed: true);
                    ctx.LineTo(left[i + 1], false, false);
                    ctx.LineTo(right[i + 1], false, false);
                    ctx.LineTo(right[i], false, false);
                }
                var tip = c[n - 1];
                ctx.BeginFigure(new Point(tip.X + head, tip.Y), isFilled: true, isClosed: true);
                ctx.ArcTo(new Point(tip.X - head, tip.Y), new Size(head, head), 0, false, SweepDirection.Counterclockwise, false, false);
                ctx.ArcTo(new Point(tip.X + head, tip.Y), new Size(head, head), 0, false, SweepDirection.Counterclockwise, false, false);
            }
            shape.Freeze();
            dc.DrawGeometry(layer.Brush, null, shape);
        }
    }

    /// <summary>A smooth closed curve through the points of a circled loop.</summary>
    public static StreamGeometry BuildLoop(IReadOnlyList<Point> points)
    {
        var c = Smooth(points, closed: true);
        var loop = new StreamGeometry();
        using (var ctx = loop.Open())
        {
            ctx.BeginFigure(c[0], isFilled: false, isClosed: true);
            for (int i = 1; i < c.Count; i++) ctx.LineTo(c[i], isStroked: true, isSmoothJoin: true);
        }
        loop.Freeze();
        return loop;
    }

    /// <summary>A circled loop as an even neon ring.</summary>
    public static void DrawLoop(DrawingContext dc, Geometry loop)
    {
        foreach (var layer in Layers) dc.DrawGeometry(null, layer.Pen, loop);
    }

    /// <summary>
    /// The loop's neon ring drawn once into an image (at the screen's DPI, on whole device pixels).
    /// A loop never changes, and re-stroking every glow band each frame would cost several ms.
    /// </summary>
    public static (BitmapSource Image, Rect Bounds) RenderLoop(Geometry loop, DpiScale dpi)
    {
        var outline = loop.Bounds;
        double sx = dpi.DpiScaleX, sy = dpi.DpiScaleY, margin = Reach + 2;
        double left = Math.Floor((outline.Left - margin) * sx) / sx, top = Math.Floor((outline.Top - margin) * sy) / sy;
        int width = (int)Math.Ceiling((outline.Right + margin - left) * sx), height = (int)Math.Ceiling((outline.Bottom + margin - top) * sy);
        var bounds = new Rect(left, top, width / sx, height / sy);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new TranslateTransform(-left, -top));
            DrawLoop(dc, loop);
            dc.Pop();
        }
        var image = new RenderTargetBitmap(width, height, 96 * sx, 96 * sy, PixelFormats.Pbgra32);
        image.Render(visual);
        image.Freeze();
        return (image, bounds);
    }

    /// <summary>Centripetal Catmull-Rom through the points: smooth, and never overshoots into loops of its own.</summary>
    private static List<Point> Smooth(IReadOnlyList<Point> pts, bool closed)
    {
        int n = pts.Count;
        var result = new List<Point>(n * 4);
        if (n < 2)
        {
            result.AddRange(pts);
            return result;
        }
        int segments = closed ? n : n - 1;
        for (int i = 0; i < segments; i++)
        {
            Point p1 = pts[i], p2 = pts[(i + 1) % n];
            // Open ends get mirrored control points, so the curve runs straight into them.
            Point p0 = closed ? pts[(i - 1 + n) % n] : i > 0 ? pts[i - 1] : p1 + (p1 - p2);
            Point p3 = closed ? pts[(i + 2) % n] : i + 2 < n ? pts[i + 2] : p2 + (p2 - p1);
            int steps = Math.Max(1, (int)Math.Ceiling((p2 - p1).Length / Spacing));
            for (int k = 0; k < steps; k++) result.Add(CatmullRom(p0, p1, p2, p3, (double)k / steps));
        }
        if (!closed) result.Add(pts[n - 1]);
        return result;
    }

    // Barry-Goldman evaluation with centripetal knots (alpha = 0.5).
    private static Point CatmullRom(Point p0, Point p1, Point p2, Point p3, double u)
    {
        double t1 = Knot(p0, p1), t2 = t1 + Knot(p1, p2), t3 = t2 + Knot(p2, p3);
        double t = t1 + (t2 - t1) * u;
        Point a1 = Mix(p0, p1, (t1 - t) / t1, t / t1);
        Point a2 = Mix(p1, p2, (t2 - t) / (t2 - t1), (t - t1) / (t2 - t1));
        Point a3 = Mix(p2, p3, (t3 - t) / (t3 - t2), (t - t2) / (t3 - t2));
        Point b1 = Mix(a1, a2, (t2 - t) / t2, t / t2);
        Point b2 = Mix(a2, a3, (t3 - t) / (t3 - t1), (t - t1) / (t3 - t1));
        return Mix(b1, b2, (t2 - t) / (t2 - t1), (t - t1) / (t2 - t1));
    }

    private static double Knot(Point a, Point b) => Math.Max(Math.Sqrt((b - a).Length), 1e-3);

    private static Point Mix(Point a, Point b, double wa, double wb) => new(a.X * wa + b.X * wb, a.Y * wa + b.Y * wb);

    private static double SmoothStep(double from, double to, double x)
    {
        double t = Math.Clamp((x - from) / (to - from), 0, 1);
        return t * t * (3 - 2 * t);
    }
}
