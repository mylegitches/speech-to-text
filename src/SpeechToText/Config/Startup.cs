using Microsoft.Win32;

namespace SpeechToText.Config;

/// <summary>"Start with Windows" via the per-user Run key.</summary>
public static class Startup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "SpeechToText";
    /// <summary>Passed when Windows starts the app at sign-in, so it stays in the tray.</summary>
    public const string BackgroundArg = "--background";

    public static bool IsEnabled
    {
        get { using var k = Registry.CurrentUser.OpenSubKey(RunKey); return k?.GetValue(Name) != null; }
    }

    public static void Set(bool on)
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (k == null) return;
        if (on) k.SetValue(Name, $"\"{Environment.ProcessPath}\" {BackgroundArg}");
        else if (k.GetValue(Name) != null) k.DeleteValue(Name);
    }
}
