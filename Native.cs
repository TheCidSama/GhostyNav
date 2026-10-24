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
