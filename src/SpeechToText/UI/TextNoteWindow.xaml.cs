using System.Windows;
using System.Windows.Input;

namespace SpeechToText.UI;

public partial class TextNoteWindow : Window
{
    readonly Func<string, Task> _save;

    public TextNoteWindow(Func<string, Task> save)
    {
        InitializeComponent();
        _save = save;
        Loaded += (_, _) => { Activate(); Body.Focus(); };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; Save_Click(this, e); }
        };
    }

    async void Save_Click(object sender, RoutedEventArgs e)
    {
        var text = Body.Text.Trim();
        if (text.Length == 0) { Close(); return; }
        SaveBtn.IsEnabled = false;
        Hide();
        await _save(text);
        Close();
    }
}
