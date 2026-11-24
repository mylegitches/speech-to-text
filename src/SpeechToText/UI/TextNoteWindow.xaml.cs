using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace SpeechToText.UI;

/// <summary>Quick note: type, dictate into it with the mic button, or mix both, then save.</summary>
public partial class TextNoteWindow : Window
{
    enum MicState { Idle, Recording, Busy }

    readonly App _app;
    readonly Func<string, bool, Task> _save;
    MicState _mic = MicState.Idle;
    bool _dictated;

    /// <param name="save">Receives the note text and whether any of it was dictated.</param>
    public TextNoteWindow(App app, Func<string, bool, Task> save)
    {
        InitializeComponent();
        _app = app;
        _save = save;
        Loaded += (_, _) => { Activate(); Body.Focus(); };
        PreviewKeyDown += (_, e) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.Control) return;
            if (e.Key == Key.Enter) { e.Handled = true; Save_Click(this, e); }
            else if (e.Key == Key.D) { e.Handled = true; Mic_Click(this, e); }
        };
        Closing += (_, _) => { if (_mic == MicState.Recording) _app.CancelInlineDictation(); };
    }

    void Mic_Click(object sender, RoutedEventArgs e)
    {
        switch (_mic)
        {
            case MicState.Idle:
                if (_app.StartInlineDictation(OnDictated)) SetMic(MicState.Recording);
                break;
            case MicState.Recording:
                SetMic(MicState.Busy);
                _app.StopInlineDictation();
                break;
        }
    }

    void OnDictated(string? text)
    {
        SetMic(MicState.Idle);
        Body.Focus();
        if (string.IsNullOrWhiteSpace(text)) return;

        // Insert at the caret, with a space if it would otherwise glue onto the previous word.
        int at = Body.CaretIndex;
        bool needsSpace = at > 0 && !char.IsWhiteSpace(Body.Text[at - 1]);
        var insert = (needsSpace ? " " : "") + text;
        Body.Text = Body.Text.Insert(at, insert);
        Body.CaretIndex = at + insert.Length;
        _dictated = true;
    }

    void SetMic(MicState state)
    {
        _mic = state;
        MicBtn.IsEnabled = state != MicState.Busy;
        (MicGlyph.Text, MicLabel.Text) = state switch
        {
            MicState.Recording => ("", "Stop"),        // stop square
            MicState.Busy => ("", "Transcribing…"),
            _ => ("", "Dictate"),
        };
        MicGlyph.Foreground = state == MicState.Recording
            ? (Brush)FindResource("Danger")
            : (Brush)FindResource("Text");
    }

    async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_mic != MicState.Idle) return; // wait for the dictation to land
        var text = Body.Text.Trim();
        if (text.Length == 0) { Close(); return; }
        SaveBtn.IsEnabled = false;
        Hide();
        await _save(text, _dictated);
        Close();
    }
}
