using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GhostyNav.Controls;
using GhostyNav.Services;

namespace GhostyNav.Pages;

public partial class ControlsPage : UserControl, IIslandPage, IResizingPage
{
    private readonly AudioService _audio;
    private readonly RadioService _radios;
    private readonly MediaService _media;
    private readonly MediaCard _mediaCard;
    private readonly IIslandHost _host;
    private bool _visible;
    private bool _spotifyInstalled = SpotifyApp.IsInstalled;

    // The media card only takes room while something is playing; otherwise a slim Spotify launcher may.
    public Size PreferredSize => new(430, _media.HasSession ? 256 : _spotifyInstalled ? 202 : 146);
    public event Action? PreferredSizeChanged;

    public ControlsPage(AudioService audio, RadioService radios, MediaService media, IIslandHost host)
    {
        InitializeComponent();
        _host = host;
        _audio = audio;
        _radios = radios;
        _media = media;
        _mediaCard = new MediaCard(media);
        MediaHost.Content = _mediaCard;
        UpdateMediaArea();
        _media.Changed += UpdateMediaArea;

        _radios.Changed += () => { if (_visible) RenderRadios(); };

        // A ripple from where the tile was clicked: orange when it turns on, grey when off.
        var grey = Color.FromRgb(0x8A, 0x8A, 0x8F);
        foreach (var tile in new[] { WifiTile, BluetoothTile, AudioTile, NightTile, DarkTile })
            tile.Click += (_, e) =>
            {
                if (e.Source != tile) return; // the chevron button inside the tile
                Fx.Ripple(tile, tile.IsChecked == true ? ((SolidColorBrush)FindResource("AccentBrush")).Color : grey, Mouse.GetPosition(tile));
            };

        // Drag anywhere on the bar, not just on a thumb.
        VolumeSlider.PreviewMouseLeftButtonDown += (_, e) =>
        {
            VolumeSlider.CaptureMouse();
            SetVolumeFromPointer(e);
            e.Handled = true;
        };
        VolumeSlider.PreviewMouseMove += (_, e) =>
        {
            if (VolumeSlider.IsMouseCaptured) SetVolumeFromPointer(e);
        };
        VolumeSlider.PreviewMouseLeftButtonUp += (_, e) =>
        {
            VolumeSlider.ReleaseMouseCapture();
            e.Handled = true;
        };
        VolumeSlider.PreviewMouseWheel += (_, e) =>
        {
            SetVolume(VolumeSlider.Value + (e.Delta > 0 ? 4 : -4));
            e.Handled = true; // don't also switch tabs
        };
    }

    public void OnShow()
    {
        _visible = true;
        _spotifyInstalled = SpotifyApp.IsInstalled; // may have been installed or removed since
        UpdateMediaArea();
        _audio.Refresh(); // default device may have changed since last time
        RenderVolume();
        RenderRadios();
        RenderToggles();
        _mediaCard.SetActive(true);
    }

    public void OnHide()
    {
        _visible = false;
        DevicePicker.Visibility = Visibility.Collapsed;
        _mediaCard.SetActive(false);
    }

    private void UpdateMediaArea()
    {
        bool playing = _media.HasSession;
        var media = playing ? Visibility.Visible : Visibility.Collapsed;
        var spotify = !playing && _spotifyInstalled ? Visibility.Visible : Visibility.Collapsed;
        if (MediaHost.Visibility == media && SpotifyCard.Visibility == spotify) return;
        MediaHost.Visibility = media;
        SpotifyCard.Visibility = spotify;
        PreferredSizeChanged?.Invoke();
    }

    // Spotify's window taking focus collapses the island, like clicking anywhere else.
    private void OnOpenSpotify(object sender, RoutedEventArgs e) => SpotifyApp.Open();

    // ---------- Volume / audio ----------

    private void SetVolumeFromPointer(MouseEventArgs e) =>
        SetVolume(e.GetPosition(VolumeSlider).X / VolumeSlider.ActualWidth * 100);

    private void SetVolume(double value)
    {
        if (!_audio.IsAvailable) return;
        value = Math.Clamp(value, 0, 100);
        _audio.Volume = value;
        if (_audio.IsMuted && value > 0) _audio.IsMuted = false;
        RenderVolume();
    }

    private void OnAudioClick(object sender, RoutedEventArgs e)
    {
        if (_audio.IsAvailable) _audio.IsMuted = !_audio.IsMuted;
        RenderVolume();
    }

    private void RenderVolume()
    {
        bool available = _audio.IsAvailable;
        VolumeSlider.IsEnabled = AudioTile.IsEnabled = available;
        double v = _audio.Volume;
        bool muted = _audio.IsMuted;
        VolumeSlider.Value = muted ? 0 : v;
        VolumeText.Text = muted ? "Muted" : $"{Math.Round(v)}%";
        string glyph = muted || v < 0.5 ? "\uE74F" : "\uE767";
        VolumeIcon.Text = AudioIcon.Text = glyph;
        AudioTile.IsChecked = available && !muted;
        AudioState.Text = available ? _audio.DeviceName : "No output";
    }

    private void OnAudioMore(object sender, RoutedEventArgs e)
    {
        DeviceList.Children.Clear();
        foreach (var device in _audio.GetOutputDevices())
        {
            bool current = device.Id == _audio.DeviceId;
            var row = new Button
            {
                Style = (Style)FindResource("FlatButton"),
                Background = current ? (Brush)FindResource("AccentDimBrush") : Brushes.Transparent,
                Foreground = current ? (Brush)FindResource("AccentBrush") : (Brush)FindResource("TextBrush"),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 1, 0, 1),
                Content = new TextBlock { Text = device.Name, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 330 }
            };
            row.Click += (_, _) =>
            {
                _audio.SetDefaultDevice(device.Id);
                DevicePicker.Visibility = Visibility.Collapsed;
                RenderVolume();
            };
            DeviceList.Children.Add(row);
        }
        DevicePicker.Visibility = Visibility.Visible;
    }

    private void OnCloseDevices(object sender, RoutedEventArgs e) => DevicePicker.Visibility = Visibility.Collapsed;

    // ---------- Radios ----------

    private async void OnWifiClick(object sender, RoutedEventArgs e)
    {
        await _radios.SetWifiAsync(WifiTile.IsChecked == true);
        RenderRadios();
    }

    private async void OnBluetoothClick(object sender, RoutedEventArgs e)
    {
        await _radios.SetBluetoothAsync(BluetoothTile.IsChecked == true);
        RenderRadios();
    }

    private void OnWifiMore(object sender, RoutedEventArgs e) => Launch("ms-availablenetworks:");

    private void RenderRadios()
    {
        WifiTile.IsEnabled = _radios.HasWifi;
        WifiTile.IsChecked = _radios.WifiOn;
        bool onWifi = _radios.SignalBars >= 0;
        WifiState.Text = !_radios.HasWifi ? (_radios.NetworkName is { Length: > 0 } n ? n : "Unavailable")
            : !_radios.WifiOn ? "Off"
            : onWifi ? _radios.NetworkName
            : "Not connected";
        WifiIcon.Text = !onWifi ? "\uE701" : _radios.SignalBars switch
        {
            >= 4 => "\uE701",
            3 => "\uE874",
            2 => "\uE873",
            _ => "\uE872"
        };

        BluetoothTile.IsEnabled = _radios.HasBluetooth;
        BluetoothTile.IsChecked = _radios.BluetoothOn;
        BluetoothState.Text = !_radios.HasBluetooth ? "Unavailable" : _radios.BluetoothOn ? "On" : "Off";
    }

    // ---------- Night light / dark mode ----------

    private void RenderToggles()
    {
        bool? night = SystemToggles.IsNightLightOn;
        NightTile.IsChecked = night == true;
        NightState.Text = night switch { true => "On", false => "Off", null => "Set up in Settings" };

        bool dark = SystemToggles.IsDarkMode;
        DarkTile.IsChecked = dark;
        DarkState.Text = dark ? "On" : "Off";
    }

    private void OnNightClick(object sender, RoutedEventArgs e)
    {
        // Windows only creates the state blob once Night Light has been used; send people there first.
        if (!SystemToggles.SetNightLight(NightTile.IsChecked == true)) Launch("ms-settings:nightlight");
        RenderToggles();
    }

    private void OnNightMore(object sender, RoutedEventArgs e) => Launch("ms-settings:nightlight");

    private void OnSettings(object sender, RoutedEventArgs e) => _host.OpenSettings();

    private async void OnDarkClick(object sender, RoutedEventArgs e)
    {
        bool dark = DarkTile.IsChecked == true;
        DarkState.Text = dark ? "On" : "Off";
        await SystemToggles.SetDarkModeAsync(dark);
    }

    private static void Launch(string uri)
    {
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
        catch (Exception ex) { Debug.WriteLine($"Launch {uri} failed: {ex.Message}"); }
    }
}
