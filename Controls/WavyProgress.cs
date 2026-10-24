using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace GhostyNav.Controls;

/// <summary>
/// Media timeline in the Android "squiggly" style: the played part is a travelling sine wave that
/// flattens into a straight line while paused; the rest is a plain track.
/// </summary>
public sealed class WavyProgress : FrameworkElement
{
    private const double Wavelength = 18, MaxAmplitude = 2.6, Thickness = 3, Step = 2;
    private const double TaperLength = 10; // the wave settles onto the centre line just before the playhead

    public static readonly DependencyProperty ValueProperty = Register(nameof(Value), 0.0);
    public static readonly DependencyProperty PhaseProperty = Register(nameof(Phase), 0.0);
    public static readonly DependencyProperty AmplitudeProperty = Register(nameof(Amplitude), 0.0);
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(WavyProgress),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((WavyProgress)d)._pens = null));
    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(
        nameof(Track), typeof(Brush), typeof(WavyProgress),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((WavyProgress)d)._pens = null));

    private static DependencyProperty Register(string name, double initial) => DependencyProperty.Register(
        name, typeof(double), typeof(WavyProgress), new FrameworkPropertyMetadata(initial, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>0..1</summary>
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Phase { get => (double)GetValue(PhaseProperty); set => SetValue(PhaseProperty, value); }
    public double Amplitude { get => (double)GetValue(AmplitudeProperty); set => SetValue(AmplitudeProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public Brush Track { get => (Brush)GetValue(TrackProperty); set => SetValue(TrackProperty, value); }

    private bool _moving;
    // Rebuilt only when a brush changes; the wave redraws 24 times a second while playing.
    private (Pen Wave, Pen Track)? _pens;
    private readonly List<Point> _points = [];

    /// <summary>
    /// Waves and travels only while playing and on screen; otherwise it eases flat and stops
    /// redrawing, so a paused or hidden player costs nothing.
    /// </summary>
    public void SetMoving(bool moving)
    {
        if (moving == _moving) return;
        _moving = moving;

        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(AmplitudeProperty, new DoubleAnimation(moving ? MaxAmplitude : 0, TimeSpan.FromMilliseconds(350)) { EasingFunction = ease });

        double phase = Phase;
        if (moving)
        {
            // One wavelength per cycle; sin is periodic, so restarting each cycle is seamless.
            var travel = new DoubleAnimation(phase, phase + 2 * Math.PI, TimeSpan.FromSeconds(1.3))
            {
                RepeatBehavior = RepeatBehavior.Forever
            };
            Timeline.SetDesiredFrameRate(travel, 24); // the wave moves ~14px/s; 24 fps is smooth and well under half of 60
            BeginAnimation(PhaseProperty, travel);
        }
        else
        {
            BeginAnimation(PhaseProperty, null); // freeze the wave where it is
            Phase = phase;
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth, mid = ActualHeight / 2;
        if (width <= 0) return;
        double head = Math.Clamp(Value, 0, 1) * width;
        var (wavePen, trackPen) = _pens ??= (MakePen(Stroke), MakePen(Track));

        if (head < width) dc.DrawLine(trackPen, new Point(head, mid), new Point(width, mid));
        if (head <= 0) return;

        double amplitude = Amplitude, phase = Phase, k = 2 * Math.PI / Wavelength;
        _points.Clear();
        for (double x = Step; x < head; x += Step) _points.Add(new Point(x, WaveY(x)));
        _points.Add(new Point(head, mid));

        var wave = new StreamGeometry();
        using (var ctx = wave.Open())
        {
            ctx.BeginFigure(new Point(0, WaveY(0)), isFilled: false, isClosed: false);
            ctx.PolyLineTo(_points, isStroked: true, isSmoothJoin: true);
        }
        wave.Freeze();
        dc.DrawGeometry(null, wavePen, wave);

        // Travels towards the playhead; tapers to the centre line over the last few pixels.
        double WaveY(double x) => mid + amplitude * Math.Min(1, (head - x) / TaperLength) * Math.Sin(k * x - phase);
    }

    private static Pen MakePen(Brush brush)
    {
        var pen = new Pen(brush, Thickness)
        {
            StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round
        };
        pen.Freeze();
        return pen;
    }
}
