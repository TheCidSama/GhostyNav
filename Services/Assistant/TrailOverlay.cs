using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace GhostyNav.Services.Assistant;

/// <summary>
/// Click-through, screen-sized layer shown while the assistant listens. The cursor leaves a
/// fading lavender trail; when the path closes a loop, the loop stays highlighted and the screen
/// under it is captured for the assistant to look at. Excluded from screen capture itself.
/// </summary>
public sealed class TrailOverlay : Window
{
    private readonly TrailLayer _layer = new();
    private readonly Grid? _halo;
    private readonly ScaleTransform _leftReach = new(), _rightReach = new(), _bottomReach = new();
    private double _level;
    private const double RestGlow = 0.5; // visible while silent, so it's clear it's listening

    /// <param name="halo">Show the blue glow along the left, right and bottom edges that follows the voice.</param>
    public TrailOverlay(bool halo)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = null;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        IsHitTestVisible = false;
        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;
        var root = new Grid();
        if (halo)
        {
            _halo = BuildHalo();
            root.Children.Add(_halo);
        }
        root.Children.Add(_layer);
        Content = root;
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            const int WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
            nint ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE);
            Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, ex | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
            ScreenCapture.ExcludeFromCapture(this, true);
        };
    }

    /// <summary>JPEG crops of each circled region, oldest first.</summary>
    public IReadOnlyList<byte[]> Captures => _layer.Captures;

    /// <summary>A loop was just closed (and captured).</summary>
    public event Action? Circled
    {
        add => _layer.Circled += value;
        remove => _layer.Circled -= value;
    }

    public void StartTracking()
    {
        _layer.Start();
        _halo?.BeginAnimation(OpacityProperty, new DoubleAnimation(0, RestGlow, TimeSpan.FromMilliseconds(300)));
    }

    /// <summary>Stops drawing new trail and lets the halo die down; circled regions stay until the overlay closes.</summary>
    public void StopTracking()
    {
        _layer.Stop();
        _halo?.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(300)));
    }

    /// <summary>Microphone loudness 0..1: the halo brightens and reaches further in as you speak.</summary>
    public void SetLevel(double level)
    {
        if (_halo == null) return;
        _level = 0.55 * _level + 0.45 * level; // smooth syllable-to-syllable jitter
        var duration = TimeSpan.FromMilliseconds(120);
        Animate(_halo, OpacityProperty, RestGlow + (1 - RestGlow) * _level, duration);
        double reach = 0.7 + 0.7 * _level;
        Animate(_leftReach, ScaleTransform.ScaleXProperty, reach, duration);
        Animate(_rightReach, ScaleTransform.ScaleXProperty, reach, duration);
        Animate(_bottomReach, ScaleTransform.ScaleYProperty, reach, duration);
    }

    private static void Animate(IAnimatable target, DependencyProperty property, double to, TimeSpan duration)
    {
        var animation = new DoubleAnimation(to, duration);
        Timeline.SetDesiredFrameRate(animation, 30); // a soft glow needn't redraw the screen at 60 fps
        target.BeginAnimation(property, animation);
    }

    private Grid BuildHalo()
    {
        const double depth = 120;
        Color hot = Color.FromArgb(0xE0, 0x7F, 0xB8, 0xFF), glow = Color.FromArgb(0x80, 0x3B, 0x82, 0xF6);
        Color fade = Color.FromArgb(0x00, 0x3B, 0x82, 0xF6);

        LinearGradientBrush Edge(Point from, Point to) => new(new GradientStopCollection
        {
            new(hot, 0), new(glow, 0.22), new(fade, 1)
        }, from, to);

        // The sides fade out towards the top, so the glow cups the screen from below.
        var sideMask = new LinearGradientBrush(new GradientStopCollection
        {
            new(Colors.Transparent, 0), new(Colors.Black, 0.55)
        }, new Point(0, 0), new Point(0, 1));

        var grid = new Grid { Opacity = 0, IsHitTestVisible = false };
        grid.Children.Add(new Rectangle
        {
            Width = depth, HorizontalAlignment = HorizontalAlignment.Left, Fill = Edge(new(0, 0), new(1, 0)),
            OpacityMask = sideMask, RenderTransformOrigin = new Point(0, 0.5), RenderTransform = _leftReach
        });
        grid.Children.Add(new Rectangle
        {
            Width = depth, HorizontalAlignment = HorizontalAlignment.Right, Fill = Edge(new(1, 0), new(0, 0)),
            OpacityMask = sideMask, RenderTransformOrigin = new Point(1, 0.5), RenderTransform = _rightReach
        });
        grid.Children.Add(new Rectangle
        {
            Height = depth, VerticalAlignment = VerticalAlignment.Bottom, Fill = Edge(new(0, 1), new(0, 0)),
            RenderTransformOrigin = new Point(0.5, 1), RenderTransform = _bottomReach
        });
        return grid;
    }

    public void FadeAndClose()
    {
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(250));
        fade.Completed += (_, _) => Close();
        ((UIElement)Content).BeginAnimation(OpacityProperty, fade);
    }

    protected override void OnClosed(EventArgs e)
    {
        _halo?.BeginAnimation(OpacityProperty, null);
        _layer.Detach();
        base.OnClosed(e);
    }

    private sealed class TrailLayer : FrameworkElement
    {
        private const double TrailLife = 0.55, PathMemory = 3.0, MinStep = 2.5, MaxStep = 250;
        // A loop must be roughly round: a circle fills ~78% of its bounding box, a thin crescent
        // left by a sharp back-and-forth only a fraction of that.
        private const double MinLoopArea = 900, MinLoopSide = 30, MinRoundness = 0.3;
        private const int MaxAreas = 3;

        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly List<(Point P, double T)> _trail = [];
        private readonly List<(Point P, double T)> _path = []; // since the last loop, for loop detection
        private readonly List<Area> _areas = [];
        private bool _tracking, _subscribed;
        private Point? _last;

        public IReadOnlyList<byte[]> Captures => _areas.ConvertAll(a => a.Capture);
        public event Action? Circled;

        /// <summary>Something circled on screen. Going round it again updates it rather than adding another.</summary>
        private sealed class Area
        {
            public required BitmapSource Ring;    // the loop the mouse drew around it, as a neon image
            public required Rect RingBounds;
            public required Rect Bounds;          // everything the loops around it covered
            public required byte[] Capture;
            public double Born;
        }

        public void Start()
        {
            _tracking = true;
            Subscribe(true);
        }

        public void Stop() => _tracking = false;

        public void Detach() => Subscribe(false);

        private void Subscribe(bool on)
        {
            if (on == _subscribed) return;
            _subscribed = on;
            if (on) CompositionTarget.Rendering += OnFrame;
            else CompositionTarget.Rendering -= OnFrame;
        }

        private void OnFrame(object? sender, EventArgs e)
        {
            double now = _clock.Elapsed.TotalSeconds;
            bool changed = false;

            if (_tracking && GetCursorPos(out var cursor))
            {
                // The process is per-monitor DPI aware: the cursor is in physical pixels.
                var dpi = VisualTreeHelper.GetDpi(this);
                var p = new Point(cursor.X / dpi.DpiScaleX, cursor.Y / dpi.DpiScaleY);
                double step = _last is { } last ? (p - last).Length : double.MaxValue;
                if (step >= MinStep)
                {
                    // A jump (cursor warped, or moved to another monitor) isn't a stroke the user drew.
                    if (step > MaxStep && _last != null)
                    {
                        _trail.Clear();
                        _path.Clear();
                    }
                    _last = p;
                    _trail.Add((p, now));
                    _path.Add((p, now));
                    DetectLoop(now);
                    changed = true;
                }
            }

            // Keep one expired sample ahead of the first live one: the tail end is interpolated
            // between them, so it glides back instead of jumping a whole sample at a time.
            while (_trail.Count >= 2 && now - _trail[1].T >= TrailLife)
            {
                _trail.RemoveAt(0);
                changed = true;
            }
            if (_trail.Count == 1 && now - _trail[0].T >= TrailLife)
            {
                _trail.Clear();
                changed = true;
            }
            while (_path.Count > 0 && now - _path[0].T > PathMemory) _path.RemoveAt(0);

            bool animating = _trail.Count > 0 || _areas.Any(a => now - a.Born < 0.4);
            if (changed || animating) InvalidateVisual();
            // Nothing moving and not tracking: stop the per-frame callback until closed.
            if (!_tracking && !animating) Subscribe(false);
        }

        /// <summary>Does the newest segment cross an earlier part of the path? Then the part between is a loop.</summary>
        private void DetectLoop(double now)
        {
            int n = _path.Count;
            if (n < 4) return;
            Point a = _path[n - 2].P, b = _path[n - 1].P;
            for (int i = n - 4; i >= 0; i--)
            {
                if (!Intersect(a, b, _path[i].P, _path[i + 1].P, out var x)) continue;

                var points = new List<Point> { x };
                for (int j = i + 1; j <= n - 2; j++) points.Add(_path[j].P);
                double area = Math.Abs(PolygonArea(points));
                var bounds = Bounds(points);
                _path.RemoveRange(0, n - 1); // start fresh from the current point
                if (area < MinLoopArea || bounds.Width < MinLoopSide || bounds.Height < MinLoopSide ||
                    area < MinRoundness * bounds.Width * bounds.Height)
                    return;

                // Keep just the stroke that went round the area, from where the path crossed itself
                // back to that point. No run-up, no fill.
                var (ring, ringBounds) = NeonStroke.RenderLoop(NeonStroke.BuildLoop(points), VisualTreeHelper.GetDpi(this));

                // Mostly the same place as an earlier loop: the same thing, circled again.
                var same = _areas.FirstOrDefault(a => Overlap(a.Bounds, bounds) > 0.5);
                var region = same == null ? bounds : Rect.Union(same.Bounds, bounds);
                if (Capture(region) is not { } jpeg) return;
                if (same != null)
                {
                    (same.Ring, same.RingBounds, same.Bounds, same.Capture, same.Born) = (ring, ringBounds, region, jpeg, now);
                }
                else
                {
                    if (_areas.Count == MaxAreas) _areas.RemoveAt(0);
                    _areas.Add(new Area { Ring = ring, RingBounds = ringBounds, Bounds = region, Capture = jpeg, Born = now });
                }
                Circled?.Invoke();
                return;
            }
        }

        private byte[]? Capture(Rect bounds)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            const double pad = 16;
            int left = (int)Math.Max(0, (bounds.Left - pad) * dpi.DpiScaleX);
            int top = (int)Math.Max(0, (bounds.Top - pad) * dpi.DpiScaleY);
            int right = (int)Math.Min(SystemParameters.PrimaryScreenWidth * dpi.DpiScaleX, (bounds.Right + pad) * dpi.DpiScaleX);
            int bottom = (int)Math.Min(SystemParameters.PrimaryScreenHeight * dpi.DpiScaleY, (bounds.Bottom + pad) * dpi.DpiScaleY);
            return ScreenCapture.CaptureJpeg(new Int32Rect(left, top, right - left, bottom - top));
        }

        protected override void OnRender(DrawingContext dc)
        {
            double now = _clock.Elapsed.TotalSeconds;
            foreach (var area in _areas)
            {
                // The loop around it stays, blooming briefly as it closes (the same glow again, fading).
                dc.DrawImage(area.Ring, area.RingBounds);
                double flash = 1 - (now - area.Born) / 0.4;
                if (flash <= 0) continue;
                dc.PushOpacity(flash);
                dc.DrawImage(area.Ring, area.RingBounds);
                dc.Pop();
            }
            if (_trail.Count < 2) return;

            var points = new List<Point>(_trail.Count);
            var (tail, tailTime) = _trail[0];
            var (next, nextTime) = _trail[1];
            double cutoff = now - TrailLife;
            points.Add(tailTime < cutoff && nextTime > tailTime
                ? tail + (next - tail) * Math.Min(1, (cutoff - tailTime) / (nextTime - tailTime))
                : tail);
            for (int i = 1; i < _trail.Count; i++) points.Add(_trail[i].P);
            NeonStroke.DrawTrail(dc, points);
        }

        private static bool Intersect(Point p1, Point p2, Point p3, Point p4, out Point x)
        {
            x = default;
            double d = (p2.X - p1.X) * (p4.Y - p3.Y) - (p2.Y - p1.Y) * (p4.X - p3.X);
            if (Math.Abs(d) < 1e-9) return false;
            double t = ((p3.X - p1.X) * (p4.Y - p3.Y) - (p3.Y - p1.Y) * (p4.X - p3.X)) / d;
            double u = ((p3.X - p1.X) * (p2.Y - p1.Y) - (p3.Y - p1.Y) * (p2.X - p1.X)) / d;
            if (t < 0 || t > 1 || u < 0 || u > 1) return false;
            x = new Point(p1.X + t * (p2.X - p1.X), p1.Y + t * (p2.Y - p1.Y));
            return true;
        }

        /// <summary>How much two rectangles share, relative to the smaller one (1 = one inside the other).</summary>
        private static double Overlap(Rect a, Rect b)
        {
            var shared = Rect.Intersect(a, b);
            if (shared.IsEmpty) return 0;
            return shared.Width * shared.Height / Math.Min(a.Width * a.Height, b.Width * b.Height);
        }

        private static double PolygonArea(List<Point> points)
        {
            double sum = 0;
            for (int i = 0; i < points.Count; i++)
            {
                var (a, b) = (points[i], points[(i + 1) % points.Count]);
                sum += a.X * b.Y - b.X * a.Y;
            }
            return sum / 2;
        }

        private static Rect Bounds(List<Point> points)
        {
            double l = points.Min(p => p.X), t = points.Min(p => p.Y), r = points.Max(p => p.X), b = points.Max(p => p.Y);
            return new Rect(l, t, r - l, b - t);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
}
