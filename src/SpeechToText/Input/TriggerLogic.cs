using System.Diagnostics;
using SpeechToText.Config;

namespace SpeechToText.Input;

/// <summary>
/// Turns raw press/release of a hotkey into start/stop/cancel according to the mode:
///   Hold   – record while held; a very short tap is ignored.
///   Toggle – press to start, press again to stop.
///   Smart  – tap to toggle, or hold to talk and release to stop.
/// </summary>
public sealed class TriggerLogic(Func<HotkeyMode> mode)
{
    const int AccidentalTapMs = 250;
    const int SmartTapMs = 400;

    readonly Stopwatch _held = new();
    bool _active, _latched;

    public event Action? Start, Stop, Cancel;

    /// <summary>Call when the session ends for another reason (Esc, time limit, error).</summary>
    public void Reset() { _active = false; _latched = false; }

    public void Pressed()
    {
        if (_active)
        {
            if (_latched) { Reset(); Stop?.Invoke(); }
            return;
        }
        _active = true;
        _latched = mode() == HotkeyMode.Toggle;
        _held.Restart();
        Start?.Invoke();
    }

    public void Released()
    {
        if (!_active || _latched) return;
        var ms = _held.ElapsedMilliseconds;
        switch (mode())
        {
            case HotkeyMode.Hold when ms < AccidentalTapMs:
                Reset(); Cancel?.Invoke(); break;
            case HotkeyMode.Smart when ms < SmartTapMs:
                _latched = true; break; // tap: keep recording until the next press
            default:
                Reset(); Stop?.Invoke(); break;
        }
    }
}
