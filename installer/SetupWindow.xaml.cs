using System;
using System.ComponentModel;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace GhostyNav.Setup;

public partial class SetupWindow : Window
{
    private enum Stage { Ready, Busy, Done, Failed }

    private static readonly string Version =
        typeof(SetupWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
    private static readonly Brush StatusBrush = new SolidColorBrush(Color.FromRgb(0xB4, 0xB4, 0xBC));
    private static readonly Brush ErrorBrush = new SolidColorBrush(Color.FromRgb(0xF2, 0x9C, 0x92));

    private readonly byte[]? _payload;
    private readonly bool _uninstall;
    private Stage _stage = Stage.Ready;

    internal SetupWindow(byte[]? payload, bool uninstall)
    {
        InitializeComponent();
        _payload = payload;
        _uninstall = uninstall;
        if (uninstall) ShowUninstallStart();
        else ShowInstallStart();
    }

    private void ShowInstallStart()
    {
        Footer.Text = $"Version {Version}  ·  MIT license";
        if (Installer.IsInstalled)
        {
            var current = Installer.CurrentOptions();
            StartupBox.IsChecked = current.StartWithWindows;
            PathBox.IsChecked = current.AddToPath;
            DesktopBox.IsChecked = current.DesktopShortcut;
            var installed = Installer.InstalledVersion;
            bool same = installed == null || installed == Version;
            Tagline.Text = same ? $"Version {Version} is already installed." : $"Update from version {installed} to {Version}.";
            Primary.Content = same ? "Reinstall" : "Update";
        }

        Note.Text = Installer.HasDesktopRuntime()
            ? "Installs just for you, in %LOCALAPPDATA%\\Programs\\GhostyNav. No admin rights needed."
            : "GhostyNav runs on Microsoft's .NET 10 Desktop Runtime, so Setup will also download it " +
              "(about 60 MB). Windows will ask for permission to install it.";
    }

    private void ShowUninstallStart()
    {
        InstallChoices.Visibility = Visibility.Collapsed;
        var installed = Installer.InstalledVersion;
        Footer.Text = installed == null ? "" : $"Version {installed}";
        if (!Installer.IsInstalled && installed == null)
        {
            Heading.Text = "GhostyNav isn't installed";
            Tagline.Text = "There's nothing to remove.";
            Primary.Content = "Close";
            _stage = Stage.Done;
            return;
        }

        UninstallChoices.Visibility = Visibility.Visible;
        Heading.Text = "Uninstall GhostyNav";
        Tagline.Text = "Removes the app, its shortcuts and its PATH entry.";
        Note.Text = "Your settings stay unless you tick the box, so a reinstall picks up where you left off.";
        Primary.Content = "Uninstall";
        Primary.Background = new SolidColorBrush(Color.FromRgb(0x32, 0x14, 0x14));
        Primary.Foreground = ErrorBrush;
    }

    private async void OnPrimary(object sender, RoutedEventArgs e)
    {
        switch (_stage)
        {
            case Stage.Ready:
            case Stage.Failed:
                await RunAsync();
                break;
            case Stage.Done:
                if (!_uninstall) Installer.Launch();
                Close();
                break;
        }
    }

    private async Task RunAsync()
    {
        _stage = Stage.Busy;
        InstallChoices.IsEnabled = UninstallChoices.IsEnabled = false;
        CloseButton.IsEnabled = Primary.IsEnabled = false;
        Primary.Content = _uninstall ? "Uninstalling…" : "Installing…";
        Note.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Visible;
        Status.Foreground = StatusBrush;
        var progress = new Progress<(double Done, string Text)>(p =>
        {
            Status.Text = p.Text;
            MoveBar(p.Done);
        });

        try
        {
            if (_uninstall)
            {
                bool purge = PurgeBox.IsChecked == true;
                await Task.Run(() => Installer.Uninstall(purge, progress));
                ShowUninstalled(purge);
            }
            else
            {
                var options = new InstallOptions
                {
                    StartWithWindows = StartupBox.IsChecked == true,
                    AddToPath = PathBox.IsChecked == true,
                    DesktopShortcut = DesktopBox.IsChecked == true,
                };
                await Task.Run(() => Installer.Install(_payload!, options, progress));
                ShowInstalled(options);
            }
        }
        catch (Exception ex)
        {
            _stage = Stage.Failed;
            Status.Text = ex is SetupException ? ex.Message : "Something went wrong: " + ex.Message;
            Status.Foreground = ErrorBrush;
            Primary.Content = "Try again";
            InstallChoices.IsEnabled = UninstallChoices.IsEnabled = true;
        }
        finally
        {
            CloseButton.IsEnabled = Primary.IsEnabled = true;
        }
    }

    private void ShowInstalled(InstallOptions options)
    {
        _stage = Stage.Done;
        Heading.Text = "GhostyNav is ready";
        Tagline.Text = "Click the notch at the top of your screen, or hold Ctrl+Alt to talk to the ghost.";
        InstallChoices.Visibility = ProgressPanel.Visibility = Visibility.Collapsed;
        Note.Visibility = Visibility.Visible;
        Note.TextAlignment = TextAlignment.Center;
        Note.Text = options.AddToPath
            ? "It's in your Start menu, and typing ghostynav in a new terminal starts it too."
            : "It's in your Start menu.";
        Primary.Content = "Open GhostyNav";
    }

    private void ShowUninstalled(bool purged)
    {
        _stage = Stage.Done;
        Heading.Text = "GhostyNav is uninstalled";
        Tagline.Text = "The ghost waves goodbye.";
        UninstallChoices.Visibility = ProgressPanel.Visibility = Visibility.Collapsed;
        Note.Visibility = Visibility.Visible;
        Note.TextAlignment = TextAlignment.Center;
        Note.Text = purged
            ? "Your settings were deleted too."
            : "Your settings are still in %LOCALAPPDATA%\\GhostyNav, in case you come back.";
        Primary.Content = "Close";
    }

    private void MoveBar(double fraction) =>
        BarScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(Math.Max(0, Math.Min(1, fraction)), TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Stopping halfway would leave a half-copied install behind
        if (_stage == Stage.Busy) e.Cancel = true;
        base.OnClosing(e);
    }
}
