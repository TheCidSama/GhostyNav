using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace GhostyNav.Services;

/// <summary>
/// Whether another app is using the microphone or camera right now, like the dots on a phone.
/// Windows records each app's use under ConsentStore (a use that hasn't stopped has a zero stop
/// time); a background thread sleeps until that part of the registry changes, so nothing polls.
/// GhostyNav's own microphone use (the assistant) doesn't count.
/// </summary>
public sealed class PrivacyIndicator : IDisposable
{
    private const string Root = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";
    private readonly ManualResetEvent _stop = new(false);
    private Thread? _thread;

    public bool MicInUse { get; private set; }
    public bool CameraInUse { get; private set; }

    /// <summary>Raised on a background thread when either changes.</summary>
    public event Action? Changed;

    public void Start()
    {
        if (_thread != null) return;
        Read();
        _thread = new Thread(Watch) { IsBackground = true, Name = "PrivacyIndicator" };
        _thread.Start();
    }

    private void Watch()
    {
        using var key = Registry.CurrentUser.OpenSubKey(Root);
        if (key == null) return;
        using var changed = new AutoResetEvent(false);
        const int REG_NOTIFY_CHANGE_NAME = 1, REG_NOTIFY_CHANGE_LAST_SET = 4;
        while (true)
        {
            if (RegNotifyChangeKeyValue(key.Handle, true, REG_NOTIFY_CHANGE_NAME | REG_NOTIFY_CHANGE_LAST_SET,
                    changed.SafeWaitHandle, true) != 0) return;
            if (WaitHandle.WaitAny([changed, _stop]) == 1) return;
            Thread.Sleep(150); // a start or stop writes several values; read once they've settled
            bool mic = MicInUse, camera = CameraInUse;
            Read();
            if (mic != MicInUse || camera != CameraInUse) Changed?.Invoke();
        }
    }

    private void Read()
    {
        MicInUse = InUse("microphone");
        CameraInUse = InUse("webcam");
    }

    private static bool InUse(string capability)
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"{Root}\{capability}");
        return key != null && AnyActive(key);

        static bool AnyActive(RegistryKey parent)
        {
            foreach (var name in parent.GetSubKeyNames())
            {
                using var app = parent.OpenSubKey(name);
                if (app == null) continue;
                if (name == "NonPackaged" && AnyActive(app)) return true;
                // Desktop apps are listed by path, with '#' for '\'.
                if (name.EndsWith("#GhostyNav.exe", StringComparison.OrdinalIgnoreCase)) continue;
                if (app.GetValue("LastUsedTimeStart") is long start && start != 0 &&
                    app.GetValue("LastUsedTimeStop") is long stop && stop == 0)
                    return true;
            }
            return false;
        }
    }

    public void Dispose() => _stop.Set();

    [DllImport("advapi32.dll")]
    private static extern int RegNotifyChangeKeyValue(SafeRegistryHandle key, bool watchSubtree, int filter,
        SafeWaitHandle evt, bool asynchronous);
}
