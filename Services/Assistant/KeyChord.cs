using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace GhostyNav.Services.Assistant;

/// <summary>The two keys held for push-to-talk.</summary>
public enum Chord { CtrlAlt, CtrlWin, AltWin }

/// <summary>
/// Push-to-talk on two modifier keys held by themselves (Ctrl+Alt unless chosen otherwise).
/// RegisterHotKey can't express a modifier-only chord, so this watches keys with a low-level hook
/// (observing only; every key still reaches its app). Holding both for <see cref="HoldDelay"/>
/// without another key raises <see cref="Pressed"/>, so shortcuts such as Ctrl+Alt+Space or AltGr
/// typing never trigger it.
/// </summary>
public sealed class KeyChord : IDisposable
{
    private enum Key { None, Ctrl, Alt, Win }

    private static readonly TimeSpan HoldDelay = TimeSpan.FromMilliseconds(300);
    private const int WH_KEYBOARD_LL = 13, WM_KEYDOWN = 0x100, WM_SYSKEYDOWN = 0x104, WM_QUIT = 0x12;
    private const int VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3, VK_LMENU = 0xA4, VK_RMENU = 0xA5, VK_CONTROL = 0x11, VK_MENU = 0x12;
    private const int VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_MASK = 0xE8; // 0xE8 is unassigned: a key no app reacts to
    // Marks the key we inject ourselves, so the hook doesn't take it for the user joining a shortcut.
    private static readonly nint OwnInput = 0x47484F53;

    private readonly Dispatcher _ui;
    private readonly DispatcherTimer _arm;
    private readonly Thread _thread;
    private HookProc? _proc; // must outlive the hook
    private uint _threadId;
    private bool _first, _second, _spoiled, _active;
    private Chord _chord;

    /// <summary>The chord has been held long enough: start listening.</summary>
    public event Action? Pressed;
    /// <summary>Ctrl or Alt was let go after <see cref="Pressed"/>.</summary>
    public event Action? Released;
    /// <summary>Another key joined the chord after <see cref="Pressed"/> (it was a shortcut after all).</summary>
    public event Action? Cancelled;

    public KeyChord(Dispatcher ui, Chord chord)
    {
        _ui = ui;
        _chord = chord;
        _arm = new DispatcherTimer(DispatcherPriority.Input, ui) { Interval = HoldDelay };
        _arm.Tick += (_, _) =>
        {
            _arm.Stop();
            if (!_first || !_second || _spoiled) return;
            _active = true;
            // The Start menu opens when Win is let go on its own; a press of a key that does nothing stops that.
            if (Uses(Key.Win)) SendMaskKey();
            Pressed?.Invoke();
        };
        _thread = new Thread(HookThread) { IsBackground = true, Name = "KeyChord" };
        _thread.Start();
    }

    public Chord Chord
    {
        get => _chord;
        set
        {
            if (value == _chord) return;
            _chord = value;
            _arm.Stop();
            _first = _second = _active = false;
        }
    }

    public string Name => NameOf(_chord);

    public static string NameOf(Chord chord) => chord switch
    {
        Chord.CtrlWin => "Ctrl+Win",
        Chord.AltWin => "Alt+Win",
        _ => "Ctrl+Alt"
    };

    private (Key First, Key Second) Keys => _chord switch
    {
        Chord.CtrlWin => (Key.Ctrl, Key.Win),
        Chord.AltWin => (Key.Alt, Key.Win),
        _ => (Key.Ctrl, Key.Alt)
    };

    private bool Uses(Key key) => Keys.First == key || Keys.Second == key;

    /// <summary>True while both keys are physically down; used to recover from missed key-ups.</summary>
    public bool IsHeld() => IsDown(Keys.First) && IsDown(Keys.Second);

    private static bool IsDown(Key key) => key switch
    {
        Key.Ctrl => (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0,
        Key.Alt => (GetAsyncKeyState(VK_MENU) & 0x8000) != 0,
        Key.Win => (GetAsyncKeyState(VK_LWIN) & 0x8000) != 0 || (GetAsyncKeyState(VK_RWIN) & 0x8000) != 0,
        _ => false
    };

    private static Key Classify(int vk) => vk switch
    {
        VK_LCONTROL or VK_RCONTROL or VK_CONTROL => Key.Ctrl,
        VK_LMENU or VK_RMENU or VK_MENU => Key.Alt,
        VK_LWIN or VK_RWIN => Key.Win,
        _ => Key.None
    };

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
            if (!fakeCtrl && info.dwExtraInfo != OwnInput)
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
        var key = Classify(vk);
        var (first, second) = Keys;

        if (key != Key.None && (key == first || key == second))
        {
            bool wasChord = _first && _second;
            if (key == first) _first = down;
            else _second = down;

            if (down && !wasChord && _first && _second)
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

    private static void SendMaskKey()
    {
        INPUT[] inputs =
        [
            new() { type = 1, ki = new KEYBDINPUT { wVk = VK_MASK, dwExtraInfo = OwnInput } },
            new() { type = 1, ki = new KEYBDINPUT { wVk = VK_MASK, dwFlags = 2 /* KEYUP */, dwExtraInfo = OwnInput } }
        ];
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public KEYBDINPUT ki;
    }

    [DllImport("user32.dll")] private static extern uint SendInput(uint count, INPUT[] inputs, int size);

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
