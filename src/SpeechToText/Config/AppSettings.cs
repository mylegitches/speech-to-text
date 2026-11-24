using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpeechToText.Config;

public enum HotkeyMode { Smart, Hold, Toggle }
public enum DictateOutput { Paste, Notes, Both }
public enum TextStyle { Raw, Polished }

public sealed class ProviderSettings
{
    public string Model { get; set; } = "";
    /// <summary>Only used by the custom OpenAI-compatible provider.</summary>
    public string BaseUrl { get; set; } = "";
}

/// <summary>Where notes go. Several can be on at once. Tokens live in SecretStore, not here.</summary>
public sealed class NotesSettings
{
    public bool QuickNotesEnabled { get; set; } = true;
    public string QuickNotesFile { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "my_notes.txt");

    /// <summary>Obsidian, Logseq or any markdown file. Path and line accept {date tokens}.</summary>
    public bool MarkdownEnabled { get; set; }
    public string MarkdownPath { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Obsidian", "Daily", "{yyyy-MM-dd}.md");
    public string MarkdownLine { get; set; } = "- {HH:mm} {text}";

    public bool NotionEnabled { get; set; }
    public string NotionPageId { get; set; } = "";

    public bool JoplinEnabled { get; set; }
    public string JoplinFolderId { get; set; } = "";

    public bool WebhookEnabled { get; set; }
    public string WebhookUrl { get; set; } = "";
}

public sealed class AppSettings
{
    public string Provider { get; set; } = "groq";
    public Dictionary<string, ProviderSettings> Providers { get; set; } = new();

    public string DictateHotkey { get; set; } = "Ctrl+Alt+Space";
    public HotkeyMode DictateMode { get; set; } = HotkeyMode.Smart;
    public DictateOutput DictateOutput { get; set; } = DictateOutput.Paste;
    /// <summary>Leave the transcript on the clipboard instead of restoring what was there before.</summary>
    public bool KeepInClipboard { get; set; }

    public string NoteHotkey { get; set; } = "Ctrl+Delete";
    public HotkeyMode NoteMode { get; set; } = HotkeyMode.Hold;

    public NotesSettings Notes { get; set; } = new();

    /// <summary>Raw = exactly what the speech model returned. Polished = an LLM fixes grammar, punctuation and flow.</summary>
    public TextStyle TextStyle { get; set; } = TextStyle.Raw;
    public string PolishProvider { get; set; } = "ollama-cloud";
    public Dictionary<string, ProviderSettings> PolishProviders { get; set; } = new();

    public ProviderSettings PolishFor(string providerId)
    {
        if (!PolishProviders.TryGetValue(providerId, out var p))
            PolishProviders[providerId] = p = new ProviderSettings();
        return p;
    }

    /// <summary>ISO-639-1 code, or empty for auto-detect.</summary>
    public string Language { get; set; } = "";
    public int MicDevice { get; set; } = -1; // -1 = Windows default
    public int MaxSeconds { get; set; } = 300;
    public bool Sounds { get; set; } = true;

    public ProviderSettings For(string providerId)
    {
        if (!Providers.TryGetValue(providerId, out var p))
            Providers[providerId] = p = new ProviderSettings();
        return p;
    }

    // ---- persistence ----

    public static string Dir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpeechToText");
    static string FilePath => Path.Combine(Dir, "settings.json");

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new();
        }
        catch { /* corrupt file: fall back to defaults rather than refusing to start */ }
        return new();
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, FilePath, overwrite: true);
    }
}
