using Windows.Devices.Enumeration;
using Windows.System.Power;

namespace GhostyNav.Services;

/// <summary>
/// Power and Bluetooth moments worth a peek: a laptop plugged in or running low, and Bluetooth
/// audio (earbuds, headphones) connecting, with its battery level when it reports one. Event-driven.
/// </summary>
public sealed class DeviceWatch
{
    private const string BatteryProperty = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
    private const string Connected = "System.Devices.Aep.IsConnected";
    // Classic Bluetooth endpoints (where headphones live)
    private const string BluetoothEndpoints = "System.Devices.Aep.ProtocolId:=\"{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}\"";
    private static readonly int[] LowLevels = [20, 10];

    private readonly Dictionary<string, (string Name, bool Connected)> _devices = [];
    private DeviceWatcher? _watcher;
    private bool _enumerated, _charging;
    private int _lastWarned = 101;

    /// <summary>(glyph, title, text), raised on a background thread.</summary>
    public event Action<string, string, string>? Moment;

    public void Start()
    {
        if (PowerManager.BatteryStatus != BatteryStatus.NotPresent)
        {
            _charging = PowerManager.PowerSupplyStatus != PowerSupplyStatus.NotPresent;
            PowerManager.PowerSupplyStatusChanged += (_, _) => OnPower();
            PowerManager.RemainingChargePercentChanged += (_, _) => OnCharge();
        }

        try
        {
            _watcher = DeviceInformation.CreateWatcher(BluetoothEndpoints, [Connected, "System.Devices.Aep.IsPaired"],
                DeviceInformationKind.AssociationEndpoint);
            _watcher.Added += (_, d) =>
            {
                lock (_devices) _devices[d.Id] = (d.Name, IsConnected(d.Properties));
            };
            _watcher.Updated += (_, u) => OnUpdated(u);
            _watcher.Removed += (_, u) => { lock (_devices) _devices.Remove(u.Id); };
            _watcher.EnumerationCompleted += (_, _) => _enumerated = true;
            _watcher.Start();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Bluetooth watch unavailable: {ex.Message}");
        }
    }

    private static bool IsConnected(IReadOnlyDictionary<string, object> properties) =>
        properties.TryGetValue(Connected, out var value) && value is true;

    private void OnUpdated(DeviceInformationUpdate update)
    {
        if (!update.Properties.ContainsKey(Connected)) return;
        bool now = IsConnected(update.Properties);
        string name;
        lock (_devices)
        {
            if (!_devices.TryGetValue(update.Id, out var known)) return;
            _devices[update.Id] = (known.Name, now);
            if (known.Connected == now || !now || !_enumerated) return;
            name = known.Name;
        }
        _ = AnnounceAsync(name);
    }

    private async Task AnnounceAsync(string name)
    {
        // The battery level arrives a moment after the connection.
        await Task.Delay(2500);
        string text = "Connected";
        try
        {
            var devices = await DeviceInformation.FindAllAsync("", [BatteryProperty], DeviceInformationKind.Device);
            int battery = devices
                .Where(d => d.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase) && d.Properties.TryGetValue(BatteryProperty, out var v) && v is IConvertible)
                .Select(d => Convert.ToInt32(d.Properties[BatteryProperty])).FirstOrDefault();
            if (battery > 0) text = $"Connected · {battery}% battery";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Battery level unavailable: {ex.Message}");
        }
        Moment?.Invoke("\uE7F6", name, text);
    }

    private void OnPower()
    {
        bool charging = PowerManager.PowerSupplyStatus != PowerSupplyStatus.NotPresent;
        if (charging == _charging) return;
        _charging = charging;
        int percent = PowerManager.RemainingChargePercent;
        if (charging) _lastWarned = 101;
        Moment?.Invoke(charging ? "\uE83E" : "\uE850", charging ? "Charging" : "On battery", $"{percent}%");
    }

    private void OnCharge()
    {
        if (_charging) return;
        int percent = PowerManager.RemainingChargePercent;
        foreach (int level in LowLevels)
        {
            if (percent > level || _lastWarned <= level) continue;
            _lastWarned = level;
            Moment?.Invoke("\uE850", "Battery low", $"{percent}% left. Plug in soon.");
            return;
        }
    }
}
