using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace GhostyNav.Controls;

/// <summary>
/// Little celebratory effects for widgets: a liquid splash, a ripple, a sparkle burst, a pop.
/// Bursts are drawn on one layer over the island (clipped to its shape), each a single
/// self-drawing element that animates for under a second and then removes itself.
/// </summary>
public static class Fx
{
    /// <summary>The canvas the bursts are drawn on. Set by the island window.</summary>
    public static Canvas? Layer { get; set; }

    public static readonly Color Green = Color.FromRgb(0x9B, 0xD4, 0x5A);
    public static readonly Color Orange = Color.FromRgb(0xFF, 0x8A, 0x57);
    public static readonly Color Gold = Color.FromRgb(0xFF, 0xD2, 0x6B);

    /// <summary>Droplets of liquid splashing out of the element, with a ring and a wet flash.</summary>
    public static void Splash(FrameworkElement at, Color color) => Spawn(at, null, color, BurstKind.Splash);

    /// <summary>A ring spreading out from a point in the element (its centre by default).</summary>
    public static void Ripple(FrameworkElement at, Color color, Point? local = null) => Spawn(at, local, color, BurstKind.Ripple);

    /// <summary>Twinkling four-point stars bursting out of the element.</summary>
    public static void Sparkle(FrameworkElement at, Color color) => Spawn(at, null, color, BurstKind.Sparkle);

    /// <summary>The element springs up from a little smaller, overshooting slightly.</summary>
    public static void Pop(UIElement element, double from = 0.6)
    {
        if (element.RenderTransform is not ScaleTransform scale)
        {
            scale = new ScaleTransform();
            element.RenderTransform = scale;
            element.RenderTransformOrigin = new Point(0.5, 0.5);
        }
        var spring = new DoubleAnimation(from, 1, TimeSpan.FromMilliseconds(520))
        {
            EasingFunction = new ElasticEase { Oscillations = 1, Springiness = 5, EasingMode = EasingMode.EaseOut }
        };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, spring);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, spring);
    }

    private static void Spawn(FrameworkElement at, Point? local, Color color, BurstKind kind)
    {
        if (Layer is not { } layer || !at.IsVisible) return;
        Point center;
        try
        {
            center = at.TransformToVisual(layer).Transform(local ?? new Point(at.ActualWidth / 2, at.ActualHeight / 2));
        }
        catch (InvalidOperationException)
        {
            return; // not in the same visual tree (already replaced)
        }
        double radius = Math.Max(4, Math.Min(at.ActualWidth, at.ActualHeight) / 2);
        if (local != null) radius = 6;
        layer.Children.Add(new Burst(kind, center, radius, color));
    }

    private enum BurstKind { Splash, Ripple, Sparkle }

    private sealed class Burst : FrameworkElement
    {
        private sealed class Particle
        {
            public Point P;
            public Vector V;
            public double Size, Drag, Delay, Spin;
        }

        private readonly BurstKind _kind;
        private readonly Point _center;
        private readonly double _radius, _duration;
        private readonly Color _color;
        private readonly List<Particle> _particles = [];
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly Dictionary<int, SolidColorBrush> _brushes = [];
        private double _lastT;

        public Burst(BurstKind kind, Point center, double radius, Color color)
        {
            (_kind, _center, _radius, _color) = (kind, center, radius, color);
            IsHitTestVisible = false;
            _duration = kind switch { BurstKind.Splash => 0.75, BurstKind.Sparkle => 0.9, _ => 0.55 };

            var random = Random.Shared;
            int count = kind switch { BurstKind.Splash => 12, BurstKind.Sparkle => 7, _ => 0 };
            for (int i = 0; i < count; i++)
            {
                double angle = (i + random.NextDouble() * 0.7) * 2 * Math.PI / count;
                double speed = kind == BurstKind.Splash ? 70 + random.NextDouble() * 110 : 40 + random.NextDouble() * 45;
                var direction = new Vector(Math.Cos(angle), Math.Sin(angle));
                _particles.Add(new Particle
                {
                    P = center + direction * radius * 0.8,
                    V = direction * speed,
                    // Liquid: a few big heavy drops that stop short, many small quick ones.
                    Size = kind == BurstKind.Splash ? (i % 4 == 0 ? 3.4 : 1.4 + random.NextDouble() * 1.4) : 3 + random.NextDouble() * 2,
                    Drag = kind == BurstKind.Splash ? (i % 4 == 0 ? 9 : 4.5) : 2.5,
                    Delay = kind == BurstKind.Sparkle ? random.NextDouble() * 0.12 : 0,
                    Spin = random.NextDouble() * Math.PI
                });
            }
            CompositionTarget.Rendering += OnFrame;
        }

        private void OnFrame(object? sender, EventArgs e)
        {
            double t = _clock.Elapsed.TotalSeconds, dt = Math.Min(t - _lastT, 0.05);
            _lastT = t;
            if (t >= _duration)
            {
                CompositionTarget.Rendering -= OnFrame;
                (Parent as Panel)?.Children.Remove(this);
                return;
            }
            foreach (var p in _particles)
            {
                if (t < p.Delay) continue;
                p.V *= Math.Exp(-p.Drag * dt);
                if (_kind == BurstKind.Splash) p.V.Y += 160 * dt; // droplets droop a little, like a liquid
                p.P += p.V * dt;
            }
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            double t = _lastT, k = Math.Clamp(t / _duration, 0, 1);

            if (_kind != BurstKind.Sparkle)
            {
                // Ring: grows outwards quickly, thins and fades.
                double ringT = Math.Clamp(t / (_duration * 0.85), 0, 1);
                double ease = 1 - Math.Pow(1 - ringT, 3);
                double reach = _kind == BurstKind.Ripple ? 26 : 14;
                var ring = new Pen(Brush(1 - ringT, 0.9), Math.Max(0.4, 3 * (1 - ringT)));
                dc.DrawEllipse(null, ring, _center, _radius + reach * ease, _radius + reach * ease);
                if (_kind == BurstKind.Ripple && t > 0.08)
                {
                    double second = Math.Clamp((t - 0.08) / (_duration * 0.85), 0, 1);
                    double r2 = _radius + reach * 0.7 * (1 - Math.Pow(1 - second, 3));
                    dc.DrawEllipse(null, new Pen(Brush(1 - second, 0.5), Math.Max(0.4, 2 * (1 - second))), _center, r2, r2);
                }
            }

            if (_kind == BurstKind.Splash)
            {
                // A wet flash where the liquid burst from.
                double flash = Math.Clamp(1 - t / 0.22, 0, 1);
                if (flash > 0) dc.DrawEllipse(Brush(flash, 0.45), null, _center, _radius * (1 + 0.5 * (1 - flash)), _radius * (1 + 0.5 * (1 - flash)));

                foreach (var p in _particles)
                {
                    double life = 1 - k;
                    double size = p.Size * (0.35 + 0.65 * life);
                    // Each drop trails a tapering streak behind it: reads as liquid, not confetti.
                    var tail = p.P - p.V * 0.035;
                    var pen = new Pen(Brush(life, 0.95), size * 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                    dc.DrawLine(pen, tail, p.P);
                    dc.DrawEllipse(Brush(life * 0.6, 1), null, p.P + new Vector(-size * 0.3, -size * 0.3), size * 0.35, size * 0.35);
                }
            }
            else if (_kind == BurstKind.Sparkle)
            {
                foreach (var p in _particles)
                {
                    if (t < p.Delay) continue;
                    double local = Math.Clamp((t - p.Delay) / (_duration - p.Delay), 0, 1);
                    double twinkle = Math.Sin(local * Math.PI); // in, then out
                    double s = p.Size * twinkle;
                    var brush = Brush(twinkle, 1);
                    var pen = new Pen(brush, Math.Max(0.6, s * 0.35)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                    var a = new Vector(Math.Cos(p.Spin), Math.Sin(p.Spin)) * s;
                    var b = new Vector(-a.Y, a.X);
                    dc.DrawLine(pen, p.P - a, p.P + a);
                    dc.DrawLine(pen, p.P - b, p.P + b);
                }
            }
        }

        /// <summary>The burst's colour at an opacity, from a small cache (16 steps is plenty for a fade).</summary>
        private SolidColorBrush Brush(double opacity, double max)
        {
            int level = (int)Math.Round(Math.Clamp(opacity * max, 0, 1) * 16);
            if (_brushes.TryGetValue(level, out var brush)) return brush;
            brush = new SolidColorBrush(Color.FromArgb((byte)(level * 255 / 16), _color.R, _color.G, _color.B));
            brush.Freeze();
            return _brushes[level] = brush;
        }
    }
}
