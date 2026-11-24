using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SpeechToText.Output;

namespace SpeechToText.UI;

/// <summary>
/// Rich note editor: type, dictate, or paste. Pasting keeps formatting (Word/Outlook via RTF, browsers
/// via HTML → Markdown) and images (screenshots, copied images, image files). Saved as Markdown.
/// </summary>
public partial class TextNoteWindow : Window
{
    enum MicState { Idle, Recording, Busy }

    static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff"];

    readonly App _app;
    readonly Func<string, bool, Task> _save;
    MicState _mic = MicState.Idle;
    bool _dictated;

    /// <param name="save">Receives the note as Markdown and whether any of it was dictated.</param>
    /// <param name="markdown">An existing note to edit, or null for a new one.</param>
    public TextNoteWindow(App app, Func<string, bool, Task> save, string? markdown = null)
    {
        InitializeComponent();
        _app = app;
        _save = save;
        if (markdown != null)
        {
            Title = "Edit note · SpeechToText";
            Body.Document = NoteMarkdown.ToFlow(markdown, Render);
        }
        else NoteMarkdown.Styled(Body.Document);
        Body.Document.PagePadding = new Thickness(4);
        DataObject.AddPastingHandler(Body, OnPaste);

        Loaded += (_, _) => { Activate(); Body.Focus(); Body.CaretPosition = Body.Document.ContentEnd; };
        PreviewKeyDown += (_, e) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.Control) return;
            if (e.Key == Key.Enter) { e.Handled = true; Save_Click(this, e); }
            else if (e.Key == Key.D) { e.Handled = true; Mic_Click(this, e); }
        };
        Closing += (_, _) => { if (_mic == MicState.Recording) _app.CancelInlineDictation(); };
    }

    NoteMarkdown.RenderOptions Render => new(_app.Settings.Notes, MaxImageWidth: 480, ClickableLinks: false);

    // ---- paste ----

    void OnPaste(object sender, DataObjectPastingEventArgs e)
    {
        var data = e.DataObject;

        // 1. Image files copied in Explorer.
        if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files
            && files.Any(f => ImageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())))
        {
            e.CancelCommand();
            foreach (var f in files.Where(f => ImageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())))
                if (LoadFile(f) is { } bmp) InsertImage(bmp);
            return;
        }

        // 2. A picture with no real text alongside it (screenshot, "Copy image" in a browser).
        string? html = data.GetDataPresent(DataFormats.Html) ? data.GetData(DataFormats.Html) as string : null;
        bool htmlHasText = html != null && System.Text.RegularExpressions.Regex.Replace(
            NoteMarkdown.FromHtml(html), @"!?\[[^\]]*\]\([^)]*\)", "").Trim().Length > 0;
        if (!htmlHasText && ClipboardImage(data) is { } image)
        {
            e.CancelCommand();
            InsertImage(image);
            return;
        }

        // 3. Office apps provide RTF, which WPF pastes with full formatting on its own.
        if (data.GetDataPresent(DataFormats.Rtf)) return;

        // 4. Browsers provide HTML only: convert through Markdown so the same subset is kept everywhere.
        if (html != null)
        {
            e.CancelCommand();
            InsertDocument(NoteMarkdown.ToFlow(NoteMarkdown.FromHtml(html), Render));
        }
        // 5. Otherwise plain text: default paste.
    }

    static BitmapSource? ClipboardImage(IDataObject data)
    {
        try
        {
            // "PNG" keeps transparency and is what screenshot tools and browsers put there.
            if (data.GetDataPresent("PNG") && data.GetData("PNG") is MemoryStream png)
                return BitmapFrame.Create(png, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            if (data.GetDataPresent(DataFormats.Bitmap) && data.GetData(DataFormats.Bitmap) is BitmapSource bmp)
                return bmp;
        }
        catch { /* some apps put unreadable image formats on the clipboard */ }
        return null;
    }

    static BitmapSource? LoadFile(string path)
    {
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.UriSource = new Uri(path);
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch { return null; }
    }

    void InsertImage(BitmapSource bmp)
    {
        Body.Selection.Text = "";
        var img = new Image { Source = bmp, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, MaxWidth = 480 };
        var at = Body.CaretPosition.GetInsertionPosition(LogicalDirection.Forward);
        var container = new InlineUIContainer(img, at) { BaselineAlignment = BaselineAlignment.Bottom };
        Body.CaretPosition = container.ElementEnd;
        Body.Focus();
    }

    /// <summary>Inserts a rendered document at the caret, keeping its formatting.</summary>
    void InsertDocument(FlowDocument doc)
    {
        using var ms = new MemoryStream();
        new TextRange(doc.ContentStart, doc.ContentEnd).Save(ms, DataFormats.XamlPackage);
        ms.Position = 0;
        Body.Selection.Load(ms, DataFormats.XamlPackage);
        Body.CaretPosition = Body.Selection.End;
        Body.Focus();
    }

    // ---- dictation ----

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

        Body.Selection.Text = "";
        var at = Body.CaretPosition;
        var before = at.GetTextInRun(LogicalDirection.Backward);
        var insert = (before.Length > 0 && !char.IsWhiteSpace(before[^1]) ? " " : "") + text;
        at.InsertTextInRun(insert);
        Body.CaretPosition = at.GetPositionAtOffset(insert.Length, LogicalDirection.Forward) ?? Body.Document.ContentEnd;
        _dictated = true;
    }

    void SetMic(MicState state)
    {
        _mic = state;
        MicBtn.IsEnabled = state != MicState.Busy;
        (MicGlyph.Text, MicLabel.Text) = state switch
        {
            MicState.Recording => ("", "Stop"),
            MicState.Busy => ("", "Transcribing…"),
            _ => ("", "Dictate"),
        };
        MicGlyph.Foreground = (Brush)FindResource(state == MicState.Recording ? "Danger" : "Text");
    }

    // ---- save ----

    async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_mic != MicState.Idle) return; // wait for the dictation to land
        var notes = _app.Settings.Notes;
        var md = NoteMarkdown.FromFlow(Body.Document, bmp => NoteMarkdown.SaveImage(notes, bmp));
        if (md.Length == 0) { Close(); return; }
        SaveBtn.IsEnabled = false;
        Hide();
        await _save(md, _dictated);
        Close();
    }
}
