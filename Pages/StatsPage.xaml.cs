using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using GhostyNav.Controls;
using GhostyNav.Services;

namespace GhostyNav.Pages;

public partial class StatsPage : UserControl, IIslandPage
{
    private const double BarWidth = 128;

    private readonly AppSettings _settings;
    private readonly IIslandHost _host;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private SystemStats? _stats;
    private Task? _sampling;
    private int _tickCount;
    private bool _visible, _fill, _gpuKnown = true;
    // Each top app's bar width, so a new reading grows or shrinks the bar from where it was.
    private readonly Dictionary<string, double> _barWidths = [];

    private bool _testing;
    private readonly DispatcherTimer _countTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private double? _downTarget, _upTarget;
    private double _downShown, _upShown;

    public Size PreferredSize => new(540, 214);

    public StatsPage(AppSettings settings, IIslandHost host)
    {
        InitializeComponent();
        _settings = settings;
        _host = host;
        _tick.Tick += (_, _) => _ = SampleAsync();
        _countTimer.Tick += (_, _) => CountTick();

        // The percentages count along with the rings as they fill.
        CpuRing.ValueChanged += v => CpuText.Text = $"{v * 100:0}%";
        RamRing.ValueChanged += v => RamText.Text = $"{v * 100:0}%";
        GpuRing.ValueChanged += v => GpuText.Text = _gpuKnown ? $"{v * 100:0}%" : "—";
    }

    public void OnShow()
    {
        _visible = true;
        _tickCount = 0;
        // Start from empty: the first reading fills everything up to where it is.
        _fill = true;
        foreach (var ring in new[] { CpuRing, GpuRing, RamRing })
        {
            ring.BeginAnimation(Ring.ValueProperty, null);
            ring.Value = 0;
        }
        _barWidths.Clear();
        foreach (var bar in TopList.Children.OfType<Grid>().Select(FindBar).OfType<Border>())
        {
            bar.BeginAnimation(WidthProperty, null);
            bar.Width = 0;
        }
        _ = SampleAsync();
        RenderSpeed();
        _tick.Start();
    }

    public void OnHide()
    {
        _visible = false;
        _tick.Stop();
    }

    /// <summary>
    /// Reads the counters on a worker thread: they can take tens of milliseconds (hundreds the
    /// first time), which would otherwise stall animations.
    /// </summary>
    private async Task SampleAsync()
    {
        if (_sampling is { IsCompleted: false }) return; // the previous reading is still going
        bool processes = _tickCount++ % 2 == 0; // the process snapshot is the costly part
        var stats = _stats;
        _sampling = Task.Run(async () =>
        {
            if (stats == null)
            {
                stats = new SystemStats();
                await Task.Delay(250); // rate counters need an interval to measure over
            }
            stats.Sample(includeProcesses: processes);
        });
        await _sampling;
        _stats ??= stats;
        if (!_visible || stats == null) return;
        CpuName.Text = stats.CpuName;
        GpuName.Text = stats.GpuName;
        Render(stats);
    }

    private void Render(SystemStats s)
    {
        bool fill = _fill;
        _fill = false;

        AnimateRing(CpuRing, s.CpuPercent / 100, fill, 0);
        CpuGhz.Text = s.CpuGhz > 0 ? $"{s.CpuGhz:0.0} GHz" : "— GHz";
        ProcCount.Text = $"{s.ProcessCount} proc";

        _gpuKnown = s.GpuPercent != null;
        AnimateRing(GpuRing, (s.GpuPercent ?? 0) / 100, fill, 90);
        GpuMem.Text = s.GpuMemoryTotal > 0
            ? $"{s.GpuMemoryUsed / 1073741824.0:0.0} / {s.GpuMemoryTotal / 1073741824.0:0.0} GB"
            : SystemStats.FormatBytes(s.GpuMemoryUsed);

        double ramFraction = s.RamTotal > 0 ? (double)s.RamUsed / s.RamTotal : 0;
        AnimateRing(RamRing, ramFraction, fill, 180);
        RamUsed.Text = $"{s.RamUsed / 1073741824.0:0.0} / {s.RamTotal / 1073741824.0:0.0} GB";
        RamFree.Text = $"{(s.RamTotal - s.RamUsed) / 1073741824.0:0.0} GB free";

        DiskLabel.Text = $"DISK {s.DiskName}";
        DiskText.Text = $"{s.DiskUsed / 1073741824.0:0} / {s.DiskTotal / 1073741824.0:0} GB";
        NetText.Text = $"↓ {Rate(s.NetDownBps)}  ↑ {Rate(s.NetUpBps)}";

        var up = TimeSpan.FromMilliseconds(Environment.TickCount64);
        UptimeText.Text = up.TotalDays >= 1 ? $"{(int)up.TotalDays}d {up.Hours}h {up.Minutes}m" : $"{up.Hours}h {up.Minutes}m";

        RenderTop(fill);
    }

    /// <summary>Opening the tab fills the ring from empty; later readings glide from the last one.</summary>
    private static void AnimateRing(Ring ring, double to, bool fill, int delayMs)
    {
        to = Math.Clamp(to, 0, 1);
        var animation = new DoubleAnimation(to, TimeSpan.FromMilliseconds(fill ? 900 : 500))
        {
            BeginTime = TimeSpan.FromMilliseconds(fill ? delayMs : 0),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        ring.BeginAnimation(Ring.ValueProperty, animation);
    }

    private static string Rate(double bytesPerSecond) => SystemStats.FormatBytes(bytesPerSecond) + "/s";

    private void RenderTop(bool fill = false)
    {
        if (_stats == null) return;
        bool byCpu = _settings.TopAppsByCpu;
        TopTitle.Text = byCpu ? "Top apps • CPU" : "Top apps • RAM";
        TopToggle.Content = byCpu ? "RAM ⇄" : "CPU ⇄";

        var apps = byCpu ? _stats.TopByCpu : _stats.TopByRam;
        double max = apps.Count > 0 ? Math.Max(apps.Max(a => a.Value), 1e-9) : 1;
        var barBrush = byCpu ? (Brush)FindResource("AccentBrush") : (Brush)FindResource("RamBrush");
        var sub = (Brush)FindResource("SubTextBrush");
        int pixels = (int)Math.Round(18 * VisualTreeHelper.GetDpi(this).DpiScaleX);

        TopList.Children.Clear();
        int index = 0;
        foreach (var app in apps)
        {
            var row = new Grid { Margin = new Thickness(0, 2, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(AppAvatar(app, pixels));

            var info = new StackPanel();
            var line = new DockPanel();
            var value = new TextBlock
            {
                Text = byCpu ? $"{app.Value:0.0}%" : SystemStats.FormatBytes(app.Value, 0),
                FontSize = 9.5, Foreground = sub
            };
            DockPanel.SetDock(value, Dock.Right);
            line.Children.Add(value);
            line.Children.Add(new TextBlock
            {
                Text = app.Name, FontSize = 10.5, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis
            });
            info.Children.Add(line);

            var bar = new Grid { Height = 3, Margin = new Thickness(0, 3, 0, 0) };
            bar.Children.Add(new Border { Background = (Brush)FindResource("TrackBrush"), CornerRadius = new CornerRadius(1.5) });
            double width = BarWidth * Math.Clamp(app.Value / max, 0.03, 1);
            var filled = new Border
            {
                Background = barBrush, CornerRadius = new CornerRadius(1.5), HorizontalAlignment = HorizontalAlignment.Left,
                Width = _barWidths.GetValueOrDefault(app.Name)
            };
            bar.Children.Add(filled);
            filled.BeginAnimation(WidthProperty, new DoubleAnimation(width, TimeSpan.FromMilliseconds(fill ? 800 : 500))
            {
                BeginTime = TimeSpan.FromMilliseconds(fill ? 120 + index * 70 : 0),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
            _barWidths[app.Name] = width;
            info.Children.Add(bar);

            Grid.SetColumn(info, 1);
            row.Children.Add(info);
            TopList.Children.Add(row);
            index++;
        }
        if (apps.Count == 0)
            TopList.Children.Add(new TextBlock { Text = "Measuring…", FontSize = 10, Foreground = sub, Margin = new Thickness(0, 4, 0, 0) });
    }

    /// <summary>The program's own icon, with its first letter until the icon has loaded (or if it has none).</summary>
    private Border AppAvatar(TopApp app, int pixels)
    {
        var avatar = new Border
        {
            Width = 18, Height = 18, CornerRadius = new CornerRadius(5),
            Background = new SolidColorBrush(Color.FromRgb(0x24, 0x24, 0x28)),
            Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = app.Name[..1].ToUpperInvariant(), FontSize = 9, FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            }
        };
        if (app.Pid == 0) return avatar;

        void Show(ImageSource icon)
        {
            avatar.Background = null;
            var image = new Image { Source = icon, Width = 18, Height = 18 };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            avatar.Child = image;
        }
        var load = IconLoader.ForProcessAsync(app.Pid, app.Name, pixels);
        if (IconLoader.Cached(load) is { } ready) Show(ready);
        else load.ContinueWith(t =>
        {
            if (t.Result is { } icon) Show(icon);
        }, TaskScheduler.FromCurrentSynchronizationContext());
        return avatar;
    }

    private static Border? FindBar(Grid row) =>
        row.Children.OfType<StackPanel>().FirstOrDefault()?.Children.OfType<Grid>().FirstOrDefault()?.Children.OfType<Border>().Skip(1).FirstOrDefault();

    private void OnToggleTop(object sender, RoutedEventArgs e)
    {
        _settings.TopAppsByCpu = !_settings.TopAppsByCpu;
        _settings.Save();
        _barWidths.Clear(); // a different list: fill it up afresh
        RenderTop(fill: true);
    }

    // ---------- Speed test ----------

    private void RenderSpeed()
    {
        var last = _settings.LastSpeed;
        if (_testing) return;
        if (last == null)
        {
            SpeedMeta.Text = "not run yet";
            return;
        }
        DownText.Text = FormatMbps(last.DownMbps);
        UpText.Text = FormatMbps(last.UpMbps);
        var ago = DateTime.Now - last.When;
        string when = ago.TotalMinutes < 1 ? "just now"
            : ago.TotalHours < 1 ? $"{(int)ago.TotalMinutes} min ago"
            : ago.TotalDays < 1 ? $"{(int)ago.TotalHours} h ago"
            : $"{(int)ago.TotalDays} d ago";
        SpeedMeta.Text = string.IsNullOrEmpty(last.City) ? $"last: {when}" : $"last: {when} • {last.City}";
    }

    private async void OnSpeedTest(object sender, RoutedEventArgs e)
    {
        if (_testing) return;
        _testing = true;
        SpeedButton.IsEnabled = false;
        SpeedButtonText.Text = "Testing…";
        // The old result goes: both read as dashes until each is measured.
        _downTarget = _upTarget = null;
        _downShown = _upShown = 0;
        DownText.Text = UpText.Text = "—";
        SpeedMeta.Text = "download…";
        _countTimer.Start();

        var progress = new Progress<SpeedReading>(reading =>
        {
            if (!_testing) return;
            if (reading.Upload) _upTarget = reading.Mbps;
            else _downTarget = reading.Mbps;
            if (reading.Final) SpeedMeta.Text = reading.Upload ? "" : "upload…";
        });

        // Stay open for the whole test; the host closes the island 5 s after it finishes.
        using var hold = _host.HoldOpen();
        try
        {
            _settings.LastSpeed = await SystemStats.RunSpeedTestAsync(progress);
            _settings.Save();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Speed test failed: {ex.Message}");
            _countTimer.Stop();
            DownText.Text = UpText.Text = "—";
            SpeedMeta.Text = "test failed";
        }
        finally
        {
            _testing = false;
            SpeedButton.IsEnabled = true;
            SpeedButtonText.Text = "Run speed test";
        }
    }

    /// <summary>
    /// The numbers count up towards the live readings instead of jumping, and settle on the
    /// measured results; then the saved result (with its age and city) takes over.
    /// </summary>
    private void CountTick()
    {
        bool moving = Count(ref _downShown, _downTarget, DownText) | Count(ref _upShown, _upTarget, UpText);
        if (!_testing && !moving)
        {
            _countTimer.Stop();
            if (_settings.LastSpeed?.When > DateTime.Now.AddSeconds(-60)) RenderSpeed();
        }
    }

    private static bool Count(ref double shown, double? target, TextBlock text)
    {
        if (target is not double goal) return false; // not measured yet: stays a dash
        double next = shown + (goal - shown) * 0.22;
        if (Math.Abs(goal - next) < 0.05) next = goal;
        shown = next;
        text.Text = FormatMbps(next);
        return next != goal;
    }

    private static string FormatMbps(double mbps) => mbps < 10 ? $"{mbps:0.0}" : $"{mbps:0}";
}
