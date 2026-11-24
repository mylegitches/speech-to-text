using System.Runtime.InteropServices;
using System.Windows;
using SpeechToText.Input;

namespace SpeechToText.Output;

/// <summary>Pastes text at the caret via clipboard + Ctrl+V, then puts the old clipboard back.</summary>
public static class Paster
{
    public static async Task PasteAsync(string text)
    {
        var saved = Snapshot();
        if (!await Retry(() => Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, text), true))) return;

        // A still-held Ctrl/Alt/Shift/Win from the hotkey would turn Ctrl+V into something else.
        for (int i = 0; i < 30 && KeyboardHook.AnyModifierDown(); i++) await Task.Delay(50);

        NativeInput.Send([
            NativeInput.Key(NativeInput.VK_CONTROL, false), NativeInput.Key(NativeInput.VK_V, false),
            NativeInput.Key(NativeInput.VK_V, true), NativeInput.Key(NativeInput.VK_CONTROL, true),
        ]);

        // The target app reads the clipboard asynchronously; give it a moment before restoring.
        await Task.Delay(400);
        if (saved != null) await Retry(() => Clipboard.SetDataObject(saved, true));
    }

    static DataObject? Snapshot()
    {
        try
        {
            var current = Clipboard.GetDataObject();
            if (current == null) return null;
            var copy = new DataObject();
            foreach (var f in current.GetFormats(false))
            {
                try { if (current.GetData(f) is { } d) copy.SetData(f, d); }
                catch { /* some formats (metafiles, delayed renders) can't be copied */ }
            }
            return copy;
        }
        catch (COMException) { return null; }
    }

    static async Task<bool> Retry(Action a)
    {
        for (int i = 0; i < 10; i++)
        {
            try { a(); return true; }
            catch (COMException) { await Task.Delay(30); } // another app has the clipboard open
        }
        return false;
    }
}
