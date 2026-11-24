using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using SpeechToText.Output;

namespace SpeechToText.UI;

/// <summary>Browse, search, edit and delete the notes in the QuickNotes file.</summary>
public partial class NotesWindow : Window
{
    readonly App _app;
    readonly DispatcherTimer _reload = new() { Interval = TimeSpan.FromMilliseconds(300) };
    readonly DispatcherTimer _undoTimer = new() { Interval = TimeSpan.FromSeconds(8) };
    FileSystemWatcher? _watcher;
    List<NoteItem> _all = [];
    (NoteLine Line, int Index)? _lastDeleted;

    string FilePath => Environment.ExpandEnvironmentVariables(_app.Settings.Notes.QuickNotesFile);

    public NotesWindow(App app)
    {
        InitializeComponent();
        _app = app;
        _reload.Tick += (_, _) => { _reload.Stop(); if (!_all.Any(n => n.IsEditing)) Reload(); };
        _undoTimer.Tick += (_, _) => { _undoTimer.Stop(); UndoBar.Visibility = Visibility.Collapsed; _lastDeleted = null; };
        Loaded += (_, _) => { Reload(); Watch(); Search.Focus(); };
        Closed += (_, _) => _watcher?.Dispose();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && !_all.Any(n => n.IsEditing)) Close();
            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { Search.Focus(); Search.SelectAll(); }
        };
    }

    /// <summary>Re-reads the file (the app may have appended a note while this window is open).</summary>
    public void Reload()
    {
        var path = FilePath;
        _all = QuickNotesFile.Load(path).Select(l => new NoteItem(l)).Reverse().ToList(); // newest first
        ApplyFilter();
        Footer.Text = $"{_all.Count} note{(_all.Count == 1 ? "" : "s")} · {path}";
    }

    void ApplyFilter()
    {
        var q = Search.Text.Trim();
        SearchHint.Visibility = q.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var shown = q.Length == 0 ? _all : _all.Where(n => n.Text.Contains(q, StringComparison.CurrentCultureIgnoreCase)).ToList();
        List.ItemsSource = shown;

        Empty.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Empty.Text = _all.Count == 0
            ? $"No notes yet.\nHold {_app.Settings.NoteHotkey} to record one, or press New note."
            : $"No notes match “{q}”.";
    }

    void Watch()
    {
        var path = FilePath;
        var dir = Path.GetDirectoryName(path);
        if (dir == null || !Directory.Exists(dir)) return;
        _watcher = new FileSystemWatcher(dir, Path.GetFileName(path))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };
        FileSystemEventHandler changed = (_, _) => Dispatcher.BeginInvoke(() => { _reload.Stop(); _reload.Start(); });
        _watcher.Changed += changed;
        _watcher.Created += changed;
        _watcher.Renamed += (s, e) => changed(s, e);
    }

    // ---- actions ----

    void Search_Changed(object sender, TextChangedEventArgs e) => ApplyFilter();

    void NewNote_Click(object sender, RoutedEventArgs e) => _app.ShowTextNote();

    void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var path = FilePath;
        if (File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
        else if (Path.GetDirectoryName(path) is { } dir && Directory.Exists(dir)) Process.Start("explorer.exe", $"\"{dir}\"");
    }

    void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (Item(sender) is { } n) { try { Clipboard.SetText(n.Text); } catch { /* clipboard busy */ } }
    }

    void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Item(sender) is not { } n) return;
        int index = QuickNotesFile.Delete(FilePath, n.Line);
        if (index < 0) { Reload(); return; } // already gone (changed elsewhere)
        _lastDeleted = (n.Line, index);
        UndoBar.Visibility = Visibility.Visible;
        _undoTimer.Stop(); _undoTimer.Start();
        Reload();
    }

    void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (_lastDeleted is { } d) QuickNotesFile.Restore(FilePath, d.Line, d.Index);
        _lastDeleted = null;
        UndoBar.Visibility = Visibility.Collapsed;
        Reload();
    }

    // ---- inline editing: double-click to edit, Enter saves, Esc cancels ----

    void Text_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || Item(sender) is not { } n) return;
        foreach (var other in _all.Where(o => o.IsEditing)) other.IsEditing = false;
        n.EditText = n.Text;
        n.IsEditing = true;
    }

    void Editor_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is TextBox { IsVisible: true } box)
            Dispatcher.BeginInvoke(() => { box.Focus(); box.CaretIndex = box.Text.Length; }, DispatcherPriority.Input);
    }

    void Editor_KeyDown(object sender, KeyEventArgs e)
    {
        if (Item(sender) is not { } n) return;
        if (e.Key == Key.Enter) { e.Handled = true; Commit(n); }
        else if (e.Key == Key.Escape) { e.Handled = true; n.IsEditing = false; }
    }

    void Editor_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (Item(sender) is { IsEditing: true } n) Commit(n);
    }

    void Commit(NoteItem n)
    {
        n.IsEditing = false;
        var text = n.EditText.Trim();
        if (text.Length == 0 || text == n.Text) return;
        if (!QuickNotesFile.Update(FilePath, n.Line, n.Line.WithText(text)))
            MessageBox.Show(this, "That note changed in the file while you were editing it, so your edit wasn't saved. The list has been refreshed.",
                "Notes · SpeechToText", MessageBoxButton.OK, MessageBoxImage.Information);
        Reload();
    }

    static NoteItem? Item(object sender) => (sender as FrameworkElement)?.DataContext as NoteItem;
}

/// <summary>One card in the list.</summary>
public sealed class NoteItem(NoteLine line) : INotifyPropertyChanged
{
    public NoteLine Line { get; } = line;
    public string Text => Line.Text;
    public string Glyph => Line.Kind switch { 'V' => "", 'T' => "", _ => "" }; // mic / keyboard / page
    public string KindLabel => Line.Kind switch { 'V' => "Voice note", 'T' => "Typed note", _ => "Unrecognized line" };

    public bool HasWhen => Line.Time != null;

    public string When
    {
        get
        {
            if (Line.Time is not { } t) return "";
            var c = CultureInfo.CurrentCulture;
            if (t.Date == DateTime.Today) return "Today " + t.ToString("t", c);
            if (t.Date == DateTime.Today.AddDays(-1)) return "Yesterday " + t.ToString("t", c);
            return t.Year == DateTime.Today.Year ? t.ToString("MMM d, ", c) + t.ToString("t", c) : t.ToString("MMM d yyyy, ", c) + t.ToString("t", c);
        }
    }

    bool _editing;
    public bool IsEditing
    {
        get => _editing;
        set { _editing = value; Changed(); Changed(nameof(IsViewing)); }
    }
    public bool IsViewing => !_editing;

    string _editText = "";
    public string EditText { get => _editText; set { _editText = value; Changed(); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
