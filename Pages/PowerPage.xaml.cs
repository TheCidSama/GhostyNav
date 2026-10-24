using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace GhostyNav.Pages;

public partial class PowerPage : UserControl, IIslandPage
{
    private readonly IIslandHost _host;
    private readonly DispatcherTimer _disarm = new() { Interval = TimeSpan.FromSeconds(3) };
    private Button? _armed;

    public Size PreferredSize => new(430, 76);

    public PowerPage(IIslandHost host)
    {
        InitializeComponent();
        _host = host;
        _disarm.Tick += (_, _) => Disarm();
    }

    public void OnShow() { }

    public void OnHide() => Disarm();

    private void OnLock(object sender, RoutedEventArgs e)
    {
        _host.Collapse();
        Native.LockWorkStation();
    }

    private void OnSleep(object sender, RoutedEventArgs e)
    {
        _host.Collapse();
        Native.SetSuspendState(false, false, false);
    }

    // Restart and shut down need a second click within 3 s, so a stray click can't lose anyone's work.
    private void OnRestart(object sender, RoutedEventArgs e)
    {
        if (Confirm(RestartButton, RestartText, "Click to confirm")) Shutdown("/r /t 0");
    }

    private void OnShutdown(object sender, RoutedEventArgs e)
    {
        if (Confirm(ShutdownButton, ShutdownText, "Click to confirm")) Shutdown("/s /t 0");
    }

    private bool Confirm(Button button, TextBlock label, string prompt)
    {
        if (_armed == button) return true;
        Disarm();
        _armed = button;
        label.Text = prompt;
        // A dark warning red rather than a bright fill: clear, without glaring.
        button.Background = new SolidColorBrush(Color.FromRgb(0x32, 0x14, 0x14));
        button.Foreground = new SolidColorBrush(Color.FromRgb(0xF2, 0x9C, 0x92));
        _disarm.Start();
        return false;
    }

    private void Disarm()
    {
        _disarm.Stop();
        _armed = null;
        RestartText.Text = "Restart";
        ShutdownText.Text = "Shut down";
        foreach (var b in new[] { RestartButton, ShutdownButton })
        {
            b.ClearValue(BackgroundProperty);
            b.ClearValue(ForegroundProperty);
        }
    }

    private static void Shutdown(string args)
    {
        try { Process.Start(new ProcessStartInfo("shutdown.exe", args) { CreateNoWindow = true, UseShellExecute = false }); }
        catch (Exception ex) { Debug.WriteLine($"shutdown {args} failed: {ex.Message}"); }
    }
}
