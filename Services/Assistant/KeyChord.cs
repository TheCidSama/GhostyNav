using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace GhostyNav.Services.Assistant;

/// <summary>
/// Push-to-talk on Ctrl+Alt held by itself. RegisterHotKey can't express a modifier-only chord,
/// so this watches keys with a low-level hook (observing only; every key still reaches its app).
/// Holding both for <see cref="HoldDelay"/> without another key raises <see cref="Pressed"/>, so
/// shortcuts such as Ctrl+Alt+Space or AltGr typing never trigger it.
/// </summary>
public sealed class KeyChord : IDisposable
{
    private static readonly TimeSpan HoldDelay = TimeSpan.FromMilliseconds(300);
    private const int WH_KEYBOARD_LL = 13, WM_KEYDOWN = 0x100, WM_SYSKEYDOWN = 0x104, WM_QUIT = 0x12;
    private const int VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3, VK_LMENU = 0xA4, VK_RMENU = 0xA5, VK_CONTROL = 0x11, VK_MENU = 0x12;

    private readonly Dispatcher _ui;
    private readonly DispatcherTimer _arm;
    private readonly Thread _thread;
    private HookProc? _proc; // must outlive the hook
    private uint _threadId;
    private bool _ctrl, _alt, _spoiled, _active;

    /// <summary>The chord has been held long enough: start listening.</summary>
    public event Action? Pressed;
    /// <summary>Ctrl or Alt was let go after <see cref="Pressed"/>.</summary>
    public event Action? Released;
    /// <summary>Another key joined the chord after <see cref="Pressed"/> (it was a shortcut after all).</summary>
    public event Action? Cancelled;

    public KeyChord(Dispatcher ui)
    {
        _ui = ui;
        _arm = new DispatcherTimer(DispatcherPriority.Input, ui) { Interval = HoldDelay };
        _arm.Tick += (_, _) =>
        {
            _arm.Stop();
            if (!_ctrl || !_alt || _spoiled) return;
            _active = true;
            Pressed?.Invoke();
        };
        _thread = new Thread(HookThread) { IsBackground = true, Name = "KeyChord" };
        _thread.Start();
    }

    /// <summary>True while both keys are physically down; used to recover from missed key-ups.</summary>
    public static bool IsHeld() => (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0 && (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;

    /// <summary>Ends the current hold as if the keys were released (e.g. it was held too long).</summary>
    public void Reset()
    {
        _arm.Stop();
        _active = false;
        _spoiled = true;
    }

    private void HookThread()
    {
        _threadId = GetCurrentThreadId();
        _proc = HookCallback;
        nint hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        // A low-level hook is called on the installing thread, which therefore needs a message loop.
        while (GetMessage(out var msg, 0, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
        if (hook != 0) UnhookWindowsHookEx(hook);
    }

    private nint HookCallback(int code, nint wParam, nint lParam)
    {
        if (code >= 0)
        {
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            bool down = wParam is WM_KEYDOWN or WM_SYSKEYDOWN;
            // AltGr arrives as a fake left Ctrl (scan code with bit 0x200) plus right Alt: not our chord.
            bool fakeCtrl = info.vkCode == VK_LCONTROL && (info.scanCode & 0x200) != 0;
            if (!fakeCtrl)
            {
                int vk = (int)info.vkCode;
                // Keep the hook itself instant: the decision happens on the UI thread.
                _ui.BeginInvoke(DispatcherPriority.Input, () => OnKey(vk, down));
            }
        }
        return CallNextHookEx(0, code, wParam, lParam);
    }

    private void OnKey(int vk, bool down)
    {
        bool isCtrl = vk is VK_LCONTROL or VK_RCONTROL or VK_CONTROL;
        bool isAlt = vk is VK_LMENU or VK_RMENU or VK_MENU;

        if (isCtrl || isAlt)
        {
            bool wasChord = _ctrl && _alt;
            if (isCtrl) _ctrl = down;
            else _alt = down;

            if (down && !wasChord && _ctrl && _alt)
            {
                _spoiled = false;
                _arm.Start();
            }
            else if (!down && wasChord)
            {
                _arm.Stop();
                if (_active)
                {
                    _active = false;
                    Released?.Invoke();
                }
            }
            return;
        }

        if (!down) return;
        // Any other key turns the chord into a shortcut.
        _spoiled = true;
        _arm.Stop();
        if (_active)
        {
            _active = false;
            Cancelled?.Invoke();
        }
    }

    public void Dispose()
    {
        _arm.Stop();
        if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, 0, 0);
    }

    private delegate nint HookProc(int code, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode, scanCode, flags, time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam, lParam;
        public uint time;
        public int x, y;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookEx(int id, HookProc proc, nint module, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, nint hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern nint DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint threadId, uint msg, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
}
