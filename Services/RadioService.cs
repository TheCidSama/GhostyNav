using System.Windows.Threading;
using Windows.Devices.Radios;
using Windows.Networking.Connectivity;

namespace GhostyNav.Services;

/// <summary>Wi-Fi / Bluetooth radio toggles and the current network name.</summary>
public sealed class RadioService
{
    private readonly Dispatcher _ui;
    private Radio? _wifi;
    private Radio? _bluetooth;

    public event Action? Changed;

    public bool HasWifi => _wifi != null;
    public bool HasBluetooth => _bluetooth != null;
    public bool WifiOn => _wifi?.State == RadioState.On;
    public bool BluetoothOn => _bluetooth?.State == RadioState.On;

    /// <summary>SSID, "Ethernet", or empty when offline.</summary>
    public string NetworkName { get; private set; } = "";
    /// <summary>0-5 for Wi-Fi, -1 when not on Wi-Fi.</summary>
    public int SignalBars { get; private set; } = -1;

    public RadioService(Dispatcher ui) => _ui = ui;

    public async Task InitializeAsync()
    {
        try
        {
            if (await Radio.RequestAccessAsync() == RadioAccessStatus.Allowed)
            {
                foreach (var radio in await Radio.GetRadiosAsync())
                {
                    if (radio.Kind == RadioKind.WiFi) _wifi ??= radio;
                    else if (radio.Kind == RadioKind.Bluetooth) _bluetooth ??= radio;
                }
                if (_wifi != null) _wifi.StateChanged += (_, _) => RaiseChanged();
                if (_bluetooth != null) _bluetooth.StateChanged += (_, _) => RaiseChanged();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Radios unavailable: {ex.Message}");
        }

        NetworkInformation.NetworkStatusChanged += _ => RaiseChanged();
        RaiseChanged();
    }

    public Task SetWifiAsync(bool on) => SetAsync(_wifi, on);
    public Task SetBluetoothAsync(bool on) => SetAsync(_bluetooth, on);

    private static async Task SetAsync(Radio? radio, bool on)
    {
        if (radio == null) return;
        try { await radio.SetStateAsync(on ? RadioState.On : RadioState.Off); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Radio toggle failed: {ex.Message}"); }
    }

    private void RaiseChanged() => _ui.InvokeAsync(() =>
    {
        UpdateNetwork();
        Changed?.Invoke();
    });

    private void UpdateNetwork()
    {
        try
        {
            var profile = NetworkInformation.GetInternetConnectionProfile();
            if (profile == null)
            {
                NetworkName = "";
                SignalBars = -1;
            }
            else if (profile.IsWlanConnectionProfile)
            {
                // ProfileName is the SSID for Wi-Fi and, unlike GetConnectedSsid(),
                // doesn't need location permission on Windows 11 24H2+.
                NetworkName = profile.ProfileName;
                SignalBars = profile.GetSignalBars() ?? 0;
            }
            else
            {
                NetworkName = profile.IsWwanConnectionProfile ? "Cellular" : "Ethernet";
                SignalBars = -1;
            }
        }
        catch
        {
            NetworkName = "";
            SignalBars = -1;
        }
    }
}
