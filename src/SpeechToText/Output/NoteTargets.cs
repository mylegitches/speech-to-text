using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using SpeechToText.Config;

namespace SpeechToText.Output;

public enum NoteKind { Voice, Text }

public sealed record Note(DateTime Time, string Text, NoteKind Kind);

/// <summary>
/// Sends a note to every enabled target. One failing target doesn't stop the others.
///   QuickNotes – my_notes.txt, "yyyyMMddHHmmssV text" per line (same format as the old quick-notes script)
///   Markdown   – Obsidian / Logseq / any .md file, path and line are templates
///   Notion     – appends a block to a page via the Notion API
///   Joplin     – creates a note via the Joplin desktop Web Clipper API
///   Webhook    – POSTs JSON (Zapier, Make, n8n → Evernote, OneNote, Todoist, …)
/// </summary>
public static partial class NoteTargets
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public static bool AnyEnabled(NotesSettings s) =>
        s.QuickNotesEnabled || s.MarkdownEnabled || s.NotionEnabled || s.JoplinEnabled || s.WebhookEnabled;

    /// <summary>Returns the names of targets that failed, with their error.</summary>
    public static async Task<List<string>> SendAsync(NotesSettings s, Note note)
    {
        var jobs = new List<(string Name, Task Task)>();
        if (s.QuickNotesEnabled) jobs.Add(("QuickNotes", QuickNotes(s, note)));
        if (s.MarkdownEnabled) jobs.Add(("Markdown", Markdown(s, note)));
        if (s.NotionEnabled) jobs.Add(("Notion", Notion(s, note)));
        if (s.JoplinEnabled) jobs.Add(("Joplin", Joplin(s, note)));
        if (s.WebhookEnabled) jobs.Add(("Webhook", Webhook(s, note)));

        var failed = new List<string>();
        foreach (var (name, task) in jobs)
        {
            try { await task; }
            catch (Exception ex) { failed.Add($"{name}: {ex.Message}"); }
        }
        return failed;
    }

    static Task QuickNotes(NotesSettings s, Note n)
    {
        var line = $"{n.Time:yyyyMMddHHmmss}{(n.Kind == NoteKind.Voice ? 'V' : 'T')} {OneLine(n.Text)}";
        return AppendLine(Environment.ExpandEnvironmentVariables(s.QuickNotesFile), line);
    }

    static Task Markdown(NotesSettings s, Note n)
    {
        var path = Fill(Environment.ExpandEnvironmentVariables(s.MarkdownPath), n);
        return AppendLine(path, Fill(s.MarkdownLine, n));
    }

    static async Task Notion(NotesSettings s, Note n)
    {
        var token = SecretStore.Get("notion") ?? throw new InvalidOperationException("no integration token set");
        var pageId = PageId(s.NotionPageId) ?? throw new InvalidOperationException("no page set");
        using var req = new HttpRequestMessage(HttpMethod.Patch, $"https://api.notion.com/v1/blocks/{pageId}/children")
        {
            Content = JsonContent.Create(new
            {
                children = new[]
                {
                    new
                    {
                        @object = "block", type = "bulleted_list_item",
                        bulleted_list_item = new { rich_text = new[] { new { type = "text", text = new { content = $"{n.Time:yyyy-MM-dd HH:mm} {n.Text}" } } } },
                    },
                },
            }),
        };
        req.Headers.Add("Authorization", "Bearer " + token);
        req.Headers.Add("Notion-Version", "2022-06-28");
        await Ensure(await Http.SendAsync(req));
    }

    static async Task Joplin(NotesSettings s, Note n)
    {
        var token = SecretStore.Get("joplin") ?? throw new InvalidOperationException("no API token set");
        var title = OneLine(n.Text);
        if (title.Length > 60) title = title[..60] + "…";
        var payload = new Dictionary<string, string> { ["title"] = title, ["body"] = n.Text };
        if (s.JoplinFolderId.Length > 0) payload["parent_id"] = s.JoplinFolderId;
        await Ensure(await Http.PostAsJsonAsync($"http://127.0.0.1:41184/notes?token={Uri.EscapeDataString(token)}", payload));
    }

    static async Task Webhook(NotesSettings s, Note n)
    {
        if (string.IsNullOrWhiteSpace(s.WebhookUrl)) throw new InvalidOperationException("no URL set");
        await Ensure(await Http.PostAsJsonAsync(s.WebhookUrl, new
        {
            text = n.Text, timestamp = n.Time.ToString("o"), type = n.Kind == NoteKind.Voice ? "voice" : "text", source = "SpeechToText",
        }));
    }

    // ---- helpers ----

    static async Task AppendLine(string path, string line)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Don't glue onto a last line that lacks a newline.
        var prefix = File.Exists(path) && new FileInfo(path).Length > 0 && !EndsWithNewline(path) ? Environment.NewLine : "";
        await File.AppendAllTextAsync(path, prefix + line + Environment.NewLine);
    }

    static bool EndsWithNewline(string path)
    {
        using var f = File.OpenRead(path);
        f.Seek(-1, SeekOrigin.End);
        return f.ReadByte() == '\n';
    }

    /// <summary>Replaces {text} and any {date format} token, e.g. {yyyy-MM-dd} or {HH:mm}.</summary>
    public static string Fill(string template, Note n) =>
        TokenRx().Replace(template, m => m.Groups[1].Value == "text" ? n.Text : n.Time.ToString(m.Groups[1].Value));

    static string OneLine(string s) => Regex.Replace(s, @"\s*\r?\n\s*", " ").Trim();

    /// <summary>Accepts a raw id or a full Notion page URL.</summary>
    static string? PageId(string input)
    {
        var m = NotionIdRx().Match(input.Replace("-", ""));
        return m.Success ? m.Value : null;
    }

    static async Task Ensure(HttpResponseMessage r)
    {
        using (r)
            if (!r.IsSuccessStatusCode)
            {
                var body = await r.Content.ReadAsStringAsync();
                throw new InvalidOperationException($"{(int)r.StatusCode} {(body.Length > 160 ? body[..160] : body)}");
            }
    }

    [GeneratedRegex(@"\{([^{}]+)\}")] private static partial Regex TokenRx();
    [GeneratedRegex(@"[0-9a-fA-F]{32}(?=[^0-9a-fA-F]*$)")] private static partial Regex NotionIdRx();
}
