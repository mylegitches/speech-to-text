using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using SpeechToText.Config;
using WpfBlock = System.Windows.Documents.Block;
using WpfInline = System.Windows.Documents.Inline;

namespace SpeechToText.Output;

/// <summary>
/// Notes are stored as Markdown; the editor and the notes manager show them as a rich FlowDocument.
/// This converts both ways, turns pasted web content (HTML) into Markdown, and keeps note images in
/// a "my_notes_assets" folder next to the QuickNotes file, linked relatively from the note.
/// Supported: paragraphs, line breaks, bold, italic, inline code, links, headings, bullet and
/// numbered lists (nested), quotes, code blocks and images.
/// </summary>
public static partial class NoteMarkdown
{
    // Single newlines are real line breaks in a note, as the person typed them.
    static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UseSoftlineBreakAsHardlineBreak().Build();

    public const string AssetsFolder = "my_notes_assets";

    public static string AssetsDir(NotesSettings s) =>
        Path.Combine(Path.GetDirectoryName(Environment.ExpandEnvironmentVariables(s.QuickNotesFile)) ?? ".", AssetsFolder);

    /// <summary>Absolute file path for a relative image link, or null for remote/data links.</summary>
    public static string? LocalImagePath(NotesSettings s, string url) =>
        url.Contains("://") || url.StartsWith("data:") ? null
            : Path.Combine(Path.GetDirectoryName(AssetsDir(s))!, Uri.UnescapeDataString(url).Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Saves an image into the assets folder and returns the relative Markdown link target.</summary>
    public static string SaveImage(NotesSettings s, BitmapSource image)
    {
        var dir = AssetsDir(s);
        Directory.CreateDirectory(dir);
        var name = $"{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid().ToString("N")[..4]}.png";
        using (var f = File.Create(Path.Combine(dir, name)))
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(image));
            enc.Save(f);
        }
        return $"{AssetsFolder}/{name}";
    }

    public static string ToPlainText(string markdown) => Markdown.ToPlainText(markdown, Pipeline).Trim();

    public static IEnumerable<string> ImageLinks(string markdown) =>
        ImageRx().Matches(markdown).Select(m => m.Groups["url"].Value);

    /// <summary>Rewrites every image link through the given function (used to copy images to other targets).</summary>
    public static string MapImages(string markdown, Func<string, string, string> replace) =>
        ImageRx().Replace(markdown, m => replace(m.Groups["alt"].Value, m.Groups["url"].Value));

    [GeneratedRegex(@"!\[(?<alt>[^\]]*)\]\((?<url>[^)\s]+)\)")] private static partial Regex ImageRx();

    // =====================================================================================
    // HTML (pasted from a browser) → Markdown
    // =====================================================================================

    public static string FromHtml(string clipboardHtml)
    {
        // CF_HTML wraps the copied part in <!--StartFragment--> … <!--EndFragment-->.
        var html = clipboardHtml;
        int s = html.IndexOf("<!--StartFragment-->", StringComparison.OrdinalIgnoreCase);
        int e = html.IndexOf("<!--EndFragment-->", StringComparison.OrdinalIgnoreCase);
        if (s >= 0 && e > s) html = html[(s + "<!--StartFragment-->".Length)..e];

        // ReverseMarkdown 6.3 marks these flat properties obsolete in favour of grouped options but
        // still honours them; the package ships no docs for the new shape, so keep the known ones.
#pragma warning disable CS0618
        var converter = new ReverseMarkdown.Converter(new ReverseMarkdown.Config
        {
            UnknownTags = ReverseMarkdown.Config.UnknownTagsOption.Bypass,
            GithubFlavored = true,
            RemoveComments = true,
            SmartHrefHandling = true,
        });
#pragma warning restore CS0618
        var md = converter.Convert(html);

        // Remote images become links: fetching them would silently reach out to other sites.
        md = MapImages(md, (alt, url) => url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? $"[{(alt.Length > 0 ? alt : "image")}]({url})"
            : $"![{alt}]({url})");
        return Regex.Replace(md, @"\n{3,}", "\n\n").Trim();
    }

    // =====================================================================================
    // Markdown → FlowDocument
    // =====================================================================================

    public sealed record RenderOptions(NotesSettings Notes, double MaxImageWidth = 420, int DecodeWidth = 0, bool ClickableLinks = true);

    public static FlowDocument ToFlow(string markdown, RenderOptions o)
    {
        var doc = Styled(new FlowDocument());
        foreach (var b in Markdown.Parse(markdown, Pipeline)) AddBlock(doc.Blocks, b, o);
        return doc;
    }

    /// <summary>
    /// FlowDocument defaults to a serif font, justified text and dark-blue links. Apply the brand
    /// sans font, left alignment, text colour and accent-text links instead.
    /// </summary>
    public static FlowDocument Styled(FlowDocument doc)
    {
        var res = Application.Current.Resources;
        doc.PagePadding = new Thickness(0);
        doc.TextAlignment = TextAlignment.Left;
        doc.FontFamily = (FontFamily)res["Sans"];
        doc.FontSize = 13.5;
        doc.Foreground = (Brush)res["Text"];
        var link = new Style(typeof(Hyperlink));
        link.Setters.Add(new Setter(TextElement.ForegroundProperty, res["AccentText"]));
        doc.Resources[typeof(Hyperlink)] = link;
        return doc;
    }

    // Headings are drawn at these sizes. Pasting through the editor drops Paragraph.Tag, so a bold
    // paragraph at exactly one of these sizes is read back as that heading level.
    static readonly double[] HeadingSizes = [20, 17, 15];

    static int HeadingLevel(Paragraph p)
    {
        if (p.Tag is string t && t.Length == 2 && t[0] == 'h') return t[1] - '0';
        if (p.ReadLocalValue(TextElement.FontSizeProperty) is double size && p.FontWeight.ToOpenTypeWeight() >= 600)
        {
            int i = Array.FindIndex(HeadingSizes, s => Math.Abs(s - size) < 0.01);
            if (i >= 0) return i + 1;
        }
        return 0;
    }

    static void AddBlock(BlockCollection target, Markdig.Syntax.Block block, RenderOptions o)
    {
        switch (block)
        {
            case HeadingBlock h:
                int level = Math.Clamp(h.Level, 1, 3);
                var hp = new Paragraph { FontWeight = FontWeights.Bold, FontSize = HeadingSizes[level - 1], Tag = "h" + level, Margin = new Thickness(0, 4, 0, 4) };
                AddInlines(hp.Inlines, h.Inline, o);
                target.Add(hp);
                break;
            case ParagraphBlock p:
                var para = new Paragraph { Margin = new Thickness(0, 0, 0, 6) };
                AddInlines(para.Inlines, p.Inline, o);
                target.Add(para);
                break;
            case ListBlock l:
                var list = new System.Windows.Documents.List
                {
                    MarkerStyle = l.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                    Margin = new Thickness(0, 0, 0, 6), Padding = new Thickness(20, 0, 0, 0),
                };
                foreach (var item in l.OfType<ListItemBlock>())
                {
                    var li = new ListItem();
                    foreach (var child in item) AddBlock(li.Blocks, child, o);
                    foreach (var pp in li.Blocks.OfType<Paragraph>()) pp.Margin = new Thickness(0);
                    list.ListItems.Add(li);
                }
                target.Add(list);
                break;
            case QuoteBlock q:
                var section = new Section { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(10, 0, 0, 0), Tag = "quote" };
                foreach (var child in q) AddBlock(section.Blocks, child, o);
                target.Add(section);
                break;
            case CodeBlock c:
                var code = new Paragraph(new Run(string.Join("\n", c.Lines.Lines.Take(c.Lines.Count).Select(x => x.ToString()))))
                    { FontFamily = new FontFamily("JetBrains Mono, Consolas"), Tag = "code", Margin = new Thickness(0, 0, 0, 6) };
                target.Add(code);
                break;
            case ThematicBreakBlock:
                target.Add(new Paragraph(new Run("———")) { Tag = "hr" });
                break;
            case ContainerBlock cb:
                foreach (var child in cb) AddBlock(target, child, o);
                break;
        }
    }

    static void AddInlines(InlineCollection target, ContainerInline? container, RenderOptions o)
    {
        if (container == null) return;
        foreach (var inline in container) AddInline(target, inline, o);
    }

    static void AddInline(InlineCollection target, Markdig.Syntax.Inlines.Inline inline, RenderOptions o)
    {
        switch (inline)
        {
            case LiteralInline lit:
                target.Add(new Run(lit.Content.ToString()));
                break;
            case LineBreakInline:
                target.Add(new LineBreak());
                break;
            case CodeInline ci:
                target.Add(new Run(ci.Content) { FontFamily = new FontFamily("JetBrains Mono, Consolas"), Tag = "code" });
                break;
            case EmphasisInline em:
                Span span = em.DelimiterCount >= 2 ? new Bold() : new Italic();
                AddInlines(span.Inlines, em, o);
                target.Add(span);
                break;
            case LinkInline { IsImage: true } img:
                if (Image(img.Url ?? "", img.FirstChild?.ToString() ?? "", o) is { } ui) target.Add(ui);
                break;
            case LinkInline link:
                var h = MakeLink(link.Url, o);
                AddInlines(h.Inlines, link, o);
                if (h.Inlines.Count == 0) h.Inlines.Add(new Run(link.Url));
                target.Add(h);
                break;
            case AutolinkInline auto:
                var a = MakeLink(auto.Url, o);
                a.Inlines.Add(new Run(auto.Url));
                target.Add(a);
                break;
            case ContainerInline c:
                AddInlines(target, c, o);
                break;
            case HtmlInline html:
                target.Add(new Run(html.Tag));
                break;
        }
    }

    static Hyperlink MakeLink(string? url, RenderOptions o)
    {
        var h = new Hyperlink();
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto")
        {
            h.NavigateUri = uri;
            h.ToolTip = uri.AbsoluteUri;
            if (o.ClickableLinks)
                h.RequestNavigate += (_, e) => Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        return h;
    }

    static InlineUIContainer? Image(string url, string alt, RenderOptions o)
    {
        var source = LoadImage(url, o);
        if (source == null) return null;
        var img = new System.Windows.Controls.Image
        {
            Source = source, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly,
            MaxWidth = o.MaxImageWidth, Tag = url, ToolTip = alt.Length > 0 ? alt : null,
        };
        return new InlineUIContainer(img) { BaselineAlignment = BaselineAlignment.Bottom };
    }

    static BitmapSource? LoadImage(string url, RenderOptions o)
    {
        try
        {
            if (url.StartsWith("data:image", StringComparison.OrdinalIgnoreCase))
            {
                var comma = url.IndexOf(',');
                using var ms = new MemoryStream(Convert.FromBase64String(url[(comma + 1)..]));
                return BitmapFrame.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            }
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad; // don't keep the file locked
            if (o.DecodeWidth > 0) bi.DecodePixelWidth = o.DecodeWidth;
            if (LocalImagePath(o.Notes, url) is { } path)
            {
                if (!File.Exists(path)) return null;
                bi.UriSource = new Uri(path);
            }
            else bi.UriSource = new Uri(url);
            bi.EndInit();
            return bi;
        }
        catch { return null; } // a broken image never breaks the note
    }

    // =====================================================================================
    // FlowDocument → Markdown
    // =====================================================================================

    /// <summary>
    /// Writes the editor's document as Markdown. Images that came from an existing link keep it;
    /// new ones (pasted) are saved through <paramref name="saveImage"/>.
    /// </summary>
    public static string FromFlow(FlowDocument doc, Func<BitmapSource, string> saveImage)
    {
        var sb = new StringBuilder();
        WriteBlocks(sb, doc.Blocks, "", saveImage);
        return Regex.Replace(sb.ToString(), @"\n{3,}", "\n\n").Trim();
    }

    static void WriteBlocks(StringBuilder sb, IEnumerable<WpfBlock> blocks, string indent, Func<BitmapSource, string> save)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case Paragraph p:
                    int level = HeadingLevel(p);
                    var text = InlinesToMd(p.Inlines, save, p.Tag is "code", ignoreBold: level > 0);
                    if (text.Trim().Length == 0 && !text.Contains("![")) { sb.Append('\n'); break; }
                    var prefix = level > 0 ? new string('#', level) + " " : "";
                    if (p.Tag is "code") { sb.Append($"{indent}```\n{text}\n{indent}```\n\n"); break; }
                    sb.Append(indent).Append(prefix).Append(text.Replace("\n", "\n" + indent)).Append("\n\n");
                    break;
                case System.Windows.Documents.List list:
                    int n = 1;
                    foreach (var item in list.ListItems)
                    {
                        var marker = list.MarkerStyle is TextMarkerStyle.Decimal or TextMarkerStyle.LowerLatin or TextMarkerStyle.UpperLatin
                            or TextMarkerStyle.LowerRoman or TextMarkerStyle.UpperRoman ? $"{n++}. " : "- ";
                        var inner = new StringBuilder();
                        WriteBlocks(inner, item.Blocks, "", save);
                        var lines = inner.ToString().Trim().Split('\n').Where(l => l.Length > 0).ToList();
                        if (lines.Count == 0) continue;
                        sb.Append(indent).Append(marker).Append(lines[0]).Append('\n');
                        foreach (var l in lines.Skip(1)) sb.Append(indent).Append(new string(' ', marker.Length)).Append(l).Append('\n');
                    }
                    sb.Append('\n');
                    break;
                case Section s:
                    if (s.Tag is "quote")
                    {
                        var inner = new StringBuilder();
                        WriteBlocks(inner, s.Blocks, "", save);
                        foreach (var l in inner.ToString().Trim().Split('\n')) sb.Append(indent).Append("> ").Append(l).Append('\n');
                        sb.Append('\n');
                    }
                    else WriteBlocks(sb, s.Blocks, indent, save);
                    break;
                case Table t:
                    foreach (var row in t.RowGroups.SelectMany(g => g.Rows))
                    {
                        var cells = row.Cells.Select(c => { var cb = new StringBuilder(); WriteBlocks(cb, c.Blocks, "", save); return cb.ToString().Trim().Replace("\n", " "); });
                        sb.Append(indent).Append(string.Join(" · ", cells)).Append('\n');
                    }
                    sb.Append('\n');
                    break;
                case BlockUIContainer { Child: System.Windows.Controls.Image img }:
                    if (ImageMd(img, save) is { } md) sb.Append(indent).Append(md).Append("\n\n");
                    break;
            }
        }
    }

    sealed record Piece(string Text, bool Bold, bool Italic, bool Code, string? Link);

    static string InlinesToMd(InlineCollection inlines, Func<BitmapSource, string> save, bool raw, bool ignoreBold = false)
    {
        var pieces = new List<Piece>();
        Flatten(inlines, pieces, save, null, raw);
        if (ignoreBold) pieces = pieces.Select(p => p with { Bold = false }).ToList(); // headings are bold already

        // Merge neighbours with the same formatting so "**a****b**" becomes "**ab**".
        var merged = new List<Piece>();
        foreach (var p in pieces)
        {
            if (merged.Count > 0 && merged[^1] is var last && last.Text != "\n" && p.Text != "\n" && !p.Text.StartsWith("![")
                && !last.Text.StartsWith("![") && last.Bold == p.Bold && last.Italic == p.Italic && last.Code == p.Code && last.Link == p.Link)
                merged[^1] = last with { Text = last.Text + p.Text };
            else merged.Add(p);
        }

        var sb = new StringBuilder();
        foreach (var p in merged)
        {
            if (p.Text == "\n" || p.Text.StartsWith("![") || raw) { sb.Append(p.Text); continue; }
            var core = p.Text.Trim();
            if (core.Length == 0) { sb.Append(p.Text); continue; }
            var lead = p.Text[..(p.Text.Length - p.Text.TrimStart().Length)];
            var trail = p.Text[p.Text.TrimEnd().Length..];
            var t = p.Code ? $"`{core}`" : Escape(core);
            if (p.Bold && p.Italic && !p.Code) t = $"***{t}***";
            else if (p.Bold && !p.Code) t = $"**{t}**";
            else if (p.Italic && !p.Code) t = $"*{t}*";
            if (p.Link != null) t = $"[{t}]({p.Link})";
            sb.Append(lead).Append(t).Append(trail);
        }
        return sb.ToString();
    }

    static void Flatten(IEnumerable<WpfInline> inlines, List<Piece> into, Func<BitmapSource, string> save, string? link, bool raw)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case Run r when r.Text.Length > 0:
                    bool code = raw || r.Tag is "code" || r.FontFamily.Source.Contains("Consolas") || r.FontFamily.Source.Contains("Courier")
                                || r.FontFamily.Source.Contains("Mono");
                    into.Add(new Piece(r.Text.Replace("\r\n", "\n").Replace("\r", "\n"),
                        r.FontWeight.ToOpenTypeWeight() >= 600, r.FontStyle == FontStyles.Italic, code, link));
                    break;
                case LineBreak:
                    into.Add(new Piece("\n", false, false, false, null));
                    break;
                case Hyperlink h:
                    Flatten(h.Inlines, into, save, h.NavigateUri?.OriginalString ?? link, raw);
                    break;
                case Span s:
                    Flatten(s.Inlines, into, save, link, raw);
                    break;
                case InlineUIContainer { Child: System.Windows.Controls.Image img }:
                    if (ImageMd(img, save) is { } md) into.Add(new Piece(md, false, false, false, null));
                    break;
            }
        }
    }

    static string? ImageMd(System.Windows.Controls.Image img, Func<BitmapSource, string> save)
    {
        if (img.Tag is string url && url.Length > 0 && !url.StartsWith("data:")) return $"![image]({url})";
        return img.Source is BitmapSource bmp ? $"![image]({save(bmp)})" : null;
    }

    static string Escape(string s) => Regex.Replace(s, @"([*`\[\]\\])", @"\$1");
}
