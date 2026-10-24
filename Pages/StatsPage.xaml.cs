using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using GhostyNav.Services;

namespace GhostyNav.Pages;

public partial class StatsPage : UserControl, IIslandPage
{
    private readonly AppSettings _settings;
    private readonly IIslandHost _host;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private SystemStats? _stats;
    private int _tickCount;
    private bool _testing;

    public Size PreferredSize => new(540, 214);

    public StatsPage(AppSettings settings, IIslandHost host)
    {
        InitializeComponent();
        _settings = settings;
        _host = host;
        _tick.Tick += (_, _) => Sample();
    }

    public void OnShow()
    {
        if (_stats == null)
        {
            _stats = new SystemStats();
            CpuName.Text = _stats.CpuName;
            GpuName.Text = _stats.GpuName;
        }
        _tickCount = 0;
        Sample();
        RenderSpeed();
        _tick.Start();
    }

    public void OnHide() => _tick.Stop();

    private void Sample()
    {
        if (_stats == null) return;
        // Processes every other tick: it's the one reading that walks every process.
        _stats.Sample(includeProcesses: _tickCount++ % 2 == 0);
        var s = _stats;

        CpuRing.Value = s.CpuPercent / 100;
        CpuText.Text = $"{s.CpuPercent:0}%";
        CpuGhz.Text = s.CpuGhz > 0 ? $"{s.CpuGhz:0.0} GHz" : "— GHz";
        ProcCount.Text = $"{s.ProcessCount} proc";

        if (s.GpuPercent is double gpu)
        {
            GpuRing.Value = gpu / 100;
            GpuText.Text = $"{gpu:0}%";
        }
        else
        {
            GpuRing.Value = 0;
            GpuText.Text = "—";
        }
        GpuMem.Text = s.GpuMemoryTotal > 0
            ? $"{s.GpuMemoryUsed / 1073741824.0:0.0} / {s.GpuMemoryTotal / 1073741824.0:0.0} GB"
            : SystemStats.FormatBytes(s.GpuMemoryUsed);

        double ramFraction = s.RamTotal > 0 ? (double)s.RamUsed / s.RamTotal : 0;
        RamRing.Value = ramFraction;
        RamText.Text = $"{ramFraction * 100:0}%";
        RamUsed.Text = $"{s.RamUsed / 1073741824.0:0.0} / {s.RamTotal / 1073741824.0:0.0} GB";
        RamFree.Text = $"{(s.RamTotal - s.RamUsed) / 1073741824.0:0.0} GB free";

        DiskLabel.Text = $"DISK {s.DiskName}";
        DiskText.Text = $"{s.DiskUsed / 1073741824.0:0} / {s.DiskTotal / 1073741824.0:0} GB";
        NetText.Text = $"↓ {Rate(s.NetDownBps)}  ↑ {Rate(s.NetUpBps)}";

        var up = TimeSpan.FromMilliseconds(Environment.TickCount64);
        UptimeText.Text = up.TotalDays >= 1 ? $"{(int)up.TotalDays}d {up.Hours}h {up.Minutes}m" : $"{up.Hours}h {up.Minutes}m";

        RenderTop();
    }

    private static string Rate(double bytesPerSecond) => SystemStats.FormatBytes(bytesPerSecond) + "/s";

    private void RenderTop()
    {
        if (_stats == null) return;
        bool byCpu = _settings.TopAppsByCpu;
        TopTitle.Text = byCpu ? "Top apps • CPU" : "Top apps • RAM";
        TopToggle.Content = byCpu ? "RAM ⇄" : "CPU ⇄";

        var apps = byCpu ? _stats.TopByCpu : _stats.TopByRam;
        double max = apps.Count > 0 ? Math.Max(apps.Max(a => a.Value), 1e-9) : 1;
        var barBrush = byCpu ? (Brush)FindResource("AccentBrush") : (Brush)FindResource("RamBrush");
        var sub = (Brush)FindResource("SubTextBrush");

        TopList.Children.Clear();
        foreach (var app in apps)
        {
            var row = new Grid { Margin = new Thickness(0, 2, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            var avatar = new Border
            {
                Width = 18, Height = 18, CornerRadius = new CornerRadius(5), Background = new SolidColorBrush(Color.FromRgb(0x24, 0x24, 0x28)),
                Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = app.Name[..1].ToUpperInvariant(), FontSize = 9, FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                }
            };
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
            bar.Children.Add(new Border
            {
                Background = barBrush, CornerRadius = new CornerRadius(1.5), HorizontalAlignment = HorizontalAlignment.Left,
                Width = 128 * Math.Clamp(app.Value / max, 0.03, 1)
            });
            info.Children.Add(bar);

            Grid.SetColumn(info, 1);
            row.Children.Add(avatar);
            row.Children.Add(info);
            TopList.Children.Add(row);
        }
        if (apps.Count == 0)
            TopList.Children.Add(new TextBlock { Text = "Measuring…", FontSize = 10, Foreground = sub, Margin = new Thickness(0, 4, 0, 0) });
    }

    private void OnToggleTop(object sender, RoutedEventArgs e)
    {
        _settings.TopAppsByCpu = !_settings.TopAppsByCpu;
        _settings.Save();
        RenderTop();
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
        DownText.Text = $"{last.DownMbps:0}";
        UpText.Text = $"{last.UpMbps:0}";
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
        SpeedMeta.Text = "";
        // Stay open for the whole test; the host closes the island 5 s after it finishes.
        using var hold = _host.HoldOpen();
        try
        {
            _settings.LastSpeed = await SystemStats.RunSpeedTestAsync();
            _settings.Save();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Speed test failed: {ex.Message}");
            SpeedMeta.Text = "test failed";
        }
        finally
        {
            _testing = false;
            SpeedButton.IsEnabled = true;
            SpeedButtonText.Text = "Run speed test";
        }
        if (_settings.LastSpeed?.When > DateTime.Now.AddSeconds(-60)) RenderSpeed();
    }
}
