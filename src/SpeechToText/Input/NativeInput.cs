using System.Runtime.InteropServices;

namespace SpeechToText.Input;

/// <summary>Thin SendInput wrapper.</summary>
static class NativeInput
{
    public const ushort VK_CONTROL = 0x11, VK_V = 0x56;

    public static INPUT Key(ushort vk, bool up) => new()
    {
        type = 1, // INPUT_KEYBOARD
        u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? 2u : 0u } },
    };

    public static void Send(INPUT[] inputs) => SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT { public uint type; public InputUnion u; }

    // Union must be sized for MOUSEINPUT (the largest member) or SendInput rejects the struct size.
    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint n, INPUT[] inputs, int size);
}
