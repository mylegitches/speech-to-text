using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Threading;

namespace SpeechToText.Input;

/// <summary>
/// Low-level keyboard hook. Unlike RegisterHotKey it sees key-up, which hold-to-talk needs.
/// The callback runs on the UI thread and must stay cheap; work is posted to the dispatcher.
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    sealed class Binding(Hotkey hotkey, Action pressed, Action released)
    {
        public Hotkey Hotkey = hotkey;
        public readonly Action Pressed = pressed, Released = released;
        public bool Down;
    }

    readonly List<Binding> _bindings = new();
    readonly Dispatcher _dispatcher;
    readonly LowLevelKeyboardProc _proc; // keep delegate alive
    IntPtr _hook;

    /// <summary>When set, Esc is swallowed and this runs instead (used to cancel a recording).</summary>
    public Action? EscapeHandler { get; set; }

    /// <summary>Pause all hotkeys, e.g. while the settings window is capturing a new one.</summary>
    public bool Suspended { get; set; }

    public KeyboardHook()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _proc = HookProc;
        using var module = Process.GetCurrentProcess().MainModule!;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(module.ModuleName), 0);
        if (_hook == IntPtr.Zero) throw new InvalidOperationException("Could not install keyboard hook.");
    }

    public IDisposable Register(Hotkey hotkey, Action pressed, Action released)
    {
        var b = new Binding(hotkey, pressed, released);
        _bindings.Add(b);
        return new Unregister(() => _bindings.Remove(b));
    }

    IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0 || Suspended) return CallNextHookEx(_hook, nCode, wParam, lParam);

        var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
        if ((data.flags & LLKHF_INJECTED) != 0) return CallNextHookEx(_hook, nCode, wParam, lParam);

        int msg = (int)wParam;
        bool down = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
        bool up = msg is WM_KEYUP or WM_SYSKEYUP;
        int vk = (int)data.vkCode;

        if (vk == VK_ESCAPE && EscapeHandler is { } esc)
        {
            if (down) _dispatcher.BeginInvoke(esc);
            return 1;
        }

        foreach (var b in _bindings)
        {
            if (vk != b.Hotkey.VirtualKey) continue;

            if (down)
            {
                if (b.Down) return 1; // auto-repeat while held
                if (CurrentModifiers() != b.Hotkey.Modifiers) continue;
                b.Down = true;
                // Alt/Win released on their own would open a menu bar / the Start menu.
                if ((b.Hotkey.Modifiers & (ModifierKeys.Alt | ModifierKeys.Windows)) != 0) SendMaskKey();
                _dispatcher.BeginInvoke(b.Pressed);
                return 1;
            }
            if (up && b.Down)
            {
                b.Down = false;
                _dispatcher.BeginInvoke(b.Released);
                return 1;
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    static ModifierKeys CurrentModifiers()
    {
        var m = ModifierKeys.None;
        if (IsDown(VK_CONTROL)) m |= ModifierKeys.Control;
        if (IsDown(VK_MENU)) m |= ModifierKeys.Alt;
        if (IsDown(VK_SHIFT)) m |= ModifierKeys.Shift;
        if (IsDown(VK_LWIN) || IsDown(VK_RWIN)) m |= ModifierKeys.Windows;
        return m;
    }

    public static bool AnyModifierDown() => CurrentModifiers() != ModifierKeys.None;

    static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    static void SendMaskKey()
    {
        // 0xE8 is unassigned; tapping it makes Windows treat the modifier as "used".
        var inputs = new[] { NativeInput.Key(0xE8, false), NativeInput.Key(0xE8, true) };
        NativeInput.Send(inputs);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
    }

    sealed class Unregister(Action a) : IDisposable { public void Dispose() => a(); }

    const int WH_KEYBOARD_LL = 13, WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105;
    const int VK_ESCAPE = 0x1B, VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;
    const uint LLKHF_INJECTED = 0x10;

    delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc fn, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
}
