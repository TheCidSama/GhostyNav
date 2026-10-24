using System.Runtime.InteropServices;

namespace GhostyNav.Services;

public sealed record AudioDevice(string Id, string Name);

/// <summary>
/// Master volume and output-device switching for the default playback device via Core Audio
/// (no NuGet dependency).
/// </summary>
public sealed class AudioService
{
    private const int eRender = 0, eMultimedia = 1, DEVICE_STATE_ACTIVE = 1, CLSCTX_ALL = 23;
    private static readonly PropertyKey FriendlyName = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);

    private IAudioEndpointVolume? _endpoint;
    private Guid _context = Guid.Empty;

    public string DeviceId { get; private set; } = "";
    public string DeviceName { get; private set; } = "";

    /// <summary>Re-binds to the current default device (it changes when headphones are plugged in).</summary>
    public void Refresh()
    {
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(eRender, eMultimedia, out var device));
            var iid = typeof(IAudioEndpointVolume).GUID;
            Marshal.ThrowExceptionForHR(device.Activate(ref iid, CLSCTX_ALL, 0, out object endpoint));
            _endpoint = (IAudioEndpointVolume)endpoint;
            device.GetId(out string id);
            DeviceId = id;
            DeviceName = ReadName(device);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"No audio endpoint: {ex.Message}");
            _endpoint = null;
            DeviceId = DeviceName = "";
        }
    }

    public bool IsAvailable => _endpoint != null;

    /// <summary>0..100</summary>
    public double Volume
    {
        get => _endpoint != null && _endpoint.GetMasterVolumeLevelScalar(out float v) == 0 ? v * 100 : 0;
        set => _endpoint?.SetMasterVolumeLevelScalar((float)Math.Clamp(value / 100, 0, 1), ref _context);
    }

    public bool IsMuted
    {
        get => _endpoint != null && _endpoint.GetMute(out bool m) == 0 && m;
        set => _endpoint?.SetMute(value, ref _context);
    }

    public List<AudioDevice> GetOutputDevices()
    {
        var list = new List<AudioDevice>();
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            Marshal.ThrowExceptionForHR(enumerator.EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE, out var devices));
            devices.GetCount(out uint count);
            for (uint i = 0; i < count; i++)
            {
                if (devices.Item(i, out var device) != 0) continue;
                device.GetId(out string id);
                list.Add(new AudioDevice(id, ReadName(device)));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Could not list audio devices: {ex.Message}");
        }
        return list;
    }

    /// <summary>Makes a device the default for all roles, like picking it in the Windows sound flyout.</summary>
    public bool SetDefaultDevice(string id)
    {
        try
        {
            var policy = (IPolicyConfig)new PolicyConfigCom();
            for (int role = 0; role < 3; role++) Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(id, role));
            Refresh();
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Could not switch audio device: {ex.Message}");
            return false;
        }
    }

    private static string ReadName(IMMDevice device)
    {
        if (device.OpenPropertyStore(0 /* STGM_READ */, out var store) != 0) return "Speakers";
        var key = FriendlyName;
        if (store.GetValue(ref key, out var value) != 0) return "Speakers";
        try
        {
            const ushort VT_LPWSTR = 31;
            return value.vt == VT_LPWSTR ? Marshal.PtrToStringUni(value.p1) ?? "Speakers" : "Speakers";
        }
        finally
        {
            PropVariantClear(ref value);
        }
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pv);

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey(Guid fmtid, int pid)
    {
        public Guid fmtid = fmtid;
        public int pid = pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort vt, r1, r2, r3;
        public nint p1, p2;
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorCom { }

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    private class PolicyConfigCom { }

    // COM interfaces: methods must stay in vtable order, including the ones we never call.

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, nint activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(nint notify);
        [PreserveSig] int UnregisterControlChangeNotify(nint notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    // Undocumented but stable since Windows 7; it's what the Windows sound flyout uses to switch devices.
    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(nint a, nint b);
        [PreserveSig] int GetDeviceFormat(nint a, int b, nint c);
        [PreserveSig] int ResetDeviceFormat(nint a);
        [PreserveSig] int SetDeviceFormat(nint a, nint b, nint c);
        [PreserveSig] int GetProcessingPeriod(nint a, int b, nint c, nint d);
        [PreserveSig] int SetProcessingPeriod(nint a, nint b);
        [PreserveSig] int GetShareMode(nint a, nint b);
        [PreserveSig] int SetShareMode(nint a, nint b);
        [PreserveSig] int GetPropertyValue(nint a, nint b, nint c);
        [PreserveSig] int SetPropertyValue(nint a, nint b, nint c);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
    }
}
