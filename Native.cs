using System.Runtime.InteropServices;
using System.Text;

namespace GhostyNav;

internal static partial class Native
{
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WM_MOUSEHWHEEL = 0x020E;
    public const int WM_HOTKEY = 0x0312;
    public const int WM_CLIPBOARDUPDATE = 0x031D;
    public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_NOREPEAT = 0x4000;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static partial nint GetWindowLongPtr(nint hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static partial nint SetWindowLongPtr(nint hWnd, int nIndex, nint value);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint hWnd, out RECT rect);

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromWindow(nint hWnd, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(nint hMonitor, ref MONITORINFO info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hWnd, StringBuilder name, int max);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool LockWorkStation();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RegisterHotKey(nint hWnd, int id, uint modifiers, uint vk);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AddClipboardFormatListener(nint hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(nint hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindow(nint hWnd);

    [LibraryImport("powrprof.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetSuspendState([MarshalAs(UnmanagedType.Bool)] bool hibernate,
        [MarshalAs(UnmanagedType.Bool)] bool force, [MarshalAs(UnmanagedType.Bool)] bool disableWakeEvent);

    // ---------- Foreground tracking ----------

    public delegate void WinEventProc(nint hook, uint evt, nint hwnd, int idObject, int idChild, uint thread, uint time);
    public const uint EVENT_SYSTEM_FOREGROUND = 3;

    [DllImport("user32.dll")]
    public static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint module, WinEventProc proc,
        uint processId, uint threadId, uint flags);

    // ---------- Synthesized Ctrl+V ----------

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public nint dwExtraInfo;
    }

    // INPUT is a tagged union; on 64-bit the union starts at offset 8 and the struct is 40 bytes.
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public KEYBDINPUT ki;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    public static void SendCtrlV()
    {
        const ushort VK_CONTROL = 0x11, VK_V = 0x56;
        const uint KEYUP = 0x2;
        static INPUT Key(ushort vk, uint flags) => new() { type = 1, ki = new KEYBDINPUT { wVk = vk, dwFlags = flags } };
        INPUT[] inputs = [Key(VK_CONTROL, 0), Key(VK_V, 0), Key(VK_V, KEYUP), Key(VK_CONTROL, KEYUP)];
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    /// <summary>Types text into the focused window as keyboard input, without touching the clipboard.</summary>
    public static void SendText(string text)
    {
        const ushort VK_RETURN = 0x0D, VK_TAB = 0x09;
        const uint KEYUP = 0x2, UNICODE = 0x4;
        var inputs = new List<INPUT>(text.Length * 2);
        foreach (char c in text)
        {
            switch (c)
            {
                case '\r':
                    continue;
                case '\n' or '\t':
                    ushort vk = c == '\n' ? VK_RETURN : VK_TAB;
                    inputs.Add(new INPUT { type = 1, ki = new KEYBDINPUT { wVk = vk } });
                    inputs.Add(new INPUT { type = 1, ki = new KEYBDINPUT { wVk = vk, dwFlags = KEYUP } });
                    break;
                default:
                    inputs.Add(new INPUT { type = 1, ki = new KEYBDINPUT { wScan = c, dwFlags = UNICODE } });
                    inputs.Add(new INPUT { type = 1, ki = new KEYBDINPUT { wScan = c, dwFlags = UNICODE | KEYUP } });
                    break;
            }
        }
        // In batches, so a long text doesn't overflow the target app's input queue.
        const int Batch = 200;
        for (int i = 0; i < inputs.Count; i += Batch)
        {
            var batch = inputs.GetRange(i, Math.Min(Batch, inputs.Count - i)).ToArray();
            SendInput((uint)batch.Length, batch, Marshal.SizeOf<INPUT>());
            Thread.Sleep(5);
        }
    }

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int vk);

    /// <summary>True while Ctrl, Alt, Shift or Win is held, which would turn typed letters into shortcuts.</summary>
    public static bool ModifiersDown() =>
        new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(vk => (GetAsyncKeyState(vk) & 0x8000) != 0);

    // ---------- Screens ----------

    public sealed record Screen(string Device, RECT Bounds, bool Primary, uint Dpi);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor, rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    private delegate bool MonitorEnumProc(nint monitor, nint dc, ref RECT rect, nint data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorEnumProc proc, nint data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    private static extern bool GetMonitorInfoEx(nint monitor, ref MONITORINFOEX info);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint monitor, int type, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);

    /// <summary>The connected screens, main one first, in physical pixels.</summary>
    public static List<Screen> Screens()
    {
        var list = new List<Screen>();
        EnumDisplayMonitors(0, 0, (nint monitor, nint _, ref RECT _, nint _) =>
        {
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfoEx(monitor, ref info))
            {
                uint dpi = GetDpiForMonitor(monitor, 0 /* MDT_EFFECTIVE_DPI */, out uint x, out _) == 0 ? x : 96;
                list.Add(new Screen(info.szDevice, info.rcMonitor, (info.dwFlags & 1) != 0, dpi));
            }
            return true;
        }, 0);
        return list.OrderByDescending(s => s.Primary).ThenBy(s => s.Bounds.Left).ToList();
    }

    // ---------- Brightness changes ----------

    public const int WM_POWERBROADCAST = 0x0218, PBT_POWERSETTINGCHANGE = 0x8013;
    public static readonly Guid GUID_VIDEO_CURRENT_MONITOR_BRIGHTNESS = new("8ffee2c6-2d01-46be-adb9-398addc5b4ff");

    [StructLayout(LayoutKind.Sequential)]
    public struct POWERBROADCAST_SETTING
    {
        public Guid PowerSetting;
        public uint DataLength;
        public uint Data; // a DWORD for brightness
    }

    [DllImport("user32.dll")]
    public static extern nint RegisterPowerSettingNotification(nint recipient, ref Guid setting, int flags);

    // ---------- Settings broadcast (dark mode) ----------

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SendMessageTimeout(nint hWnd, uint msg, nint wParam, string lParam,
        uint flags, uint timeout, out nint result);

    public static void BroadcastSettingChange(string area)
    {
        const nint HWND_BROADCAST = 0xFFFF;
        const uint WM_SETTINGCHANGE = 0x1A, SMTO_ABORTIFHUNG = 0x2;
        SendMessageTimeout(HWND_BROADCAST, WM_SETTINGCHANGE, 0, area, SMTO_ABORTIFHUNG, 200, out _);
    }

    // ---------- Memory / CPU ----------

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile,
            ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX status);

    [LibraryImport("ntdll.dll")]
    public static partial int NtQuerySystemInformation(int infoClass, nint buffer, int length, out int returnLength);

    public static void MakeToolWindow(nint hwnd)
    {
        nint ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW);
    }

    /// <summary>
    /// True when the foreground window (a game, a fullscreen video) covers the whole
    /// monitor the island lives on.
    /// </summary>
    public static bool IsFullscreenAppActive(nint islandHwnd)
    {
        nint fg = GetForegroundWindow();
        if (fg == 0 || fg == islandHwnd) return false;

        // The desktop itself is monitor-sized; it doesn't count.
        var cls = new StringBuilder(32);
        GetClassName(fg, cls, cls.Capacity);
        string c = cls.ToString();
        if (c is "Progman" or "WorkerW" or "Shell_TrayWnd") return false;

        nint monitor = MonitorFromWindow(islandHwnd, MONITOR_DEFAULTTONEAREST);
        if (MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST) != monitor) return false;

        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref mi) || !GetWindowRect(fg, out RECT r)) return false;

        return r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top &&
               r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
    }
}
