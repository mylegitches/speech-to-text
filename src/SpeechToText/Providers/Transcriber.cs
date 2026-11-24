using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using SpeechToText.Config;

namespace SpeechToText.Providers;

/// <summary>
/// Models are listed most accurate first; that first one is the default. An entry may carry a
/// note after " — " (e.g. "whisper-large-v3 — best accuracy"); only the part before it is sent.
/// </summary>
public sealed record ProviderInfo(string Id, string Name, string BaseUrl, string[] Models, bool CustomUrl = false)
{
    public string DefaultModel => Models.Length > 0 ? ModelId(Models[0]) : "";

    public static string ModelId(string entry) => entry.Split(" — ")[0].Trim();

    /// <summary>The dropdown entry for a stored model id, so its quality note shows.</summary>
    public string Entry(string modelId) =>
        Models.FirstOrDefault(m => ModelId(m) == modelId) ?? modelId;
}

/// <summary>
/// OpenAI, Groq and any self-hosted server (Speaches, LocalAI, faster-whisper-server…) all speak the
/// same /audio/transcriptions API, so one class covers them.
/// </summary>
public static class Transcriber
{
    public static readonly ProviderInfo[] All =
    [
        new("groq", "Groq", "https://api.groq.com/openai/v1",
            ["whisper-large-v3 — best accuracy", "whisper-large-v3-turbo — faster, slightly less accurate"]),
        new("openai", "OpenAI", "https://api.openai.com/v1",
            ["gpt-4o-transcribe — best accuracy", "gpt-4o-mini-transcribe — faster and cheaper, less accurate"]),
        new("custom", "Any OpenAI-compatible endpoint", "http://localhost:8000/v1", [], CustomUrl: true),
    ];

    /// <summary>
    /// Whisper-family models copy the style of the prompt. A clean, punctuated sentence makes them
    /// punctuate and capitalize properly instead of returning run-on lowercase text.
    /// </summary>
    const string StylePrompt = "Hello, welcome. This is a dictation, written with proper punctuation, capitalization and full sentences.";

    public static ProviderInfo Get(string id) => All.FirstOrDefault(p => p.Id == id) ?? All[0];

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(90) };

    public static async Task<string> TranscribeAsync(AppSettings settings, byte[] wav, CancellationToken ct = default)
    {
        var info = Get(settings.Provider);
        var ps = settings.For(info.Id);
        var baseUrl = (info.CustomUrl && ps.BaseUrl.Length > 0 ? ps.BaseUrl : info.BaseUrl).TrimEnd('/');
        var model = ps.Model.Length > 0 ? ProviderInfo.ModelId(ps.Model) : info.DefaultModel;
        var key = SecretStore.Get(info.Id);
        if (string.IsNullOrEmpty(key) && !info.CustomUrl)
            throw new InvalidOperationException($"There is no {info.Name} API key yet. Add one in Settings → Transcription.");

        using var form = new MultipartFormDataContent();
        var audio = new ByteArrayContent(wav);
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(audio, "file", "audio.wav");
        if (model.Length > 0) form.Add(new StringContent(model), "model");
        if (settings.Language.Length > 0) form.Add(new StringContent(settings.Language), "language");
        // The style prompt is English; for other languages it would bias the output, so skip it.
        if (settings.Language is "" or "en") form.Add(new StringContent(StylePrompt), "prompt");
        form.Add(new StringContent("json"), "response_format");

        using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/audio/transcriptions") { Content = form };
        if (!string.IsNullOrEmpty(key)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        using var resp = await Http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"{info.Name}: {ErrorMessage(body) ?? resp.ReasonPhrase}");

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("text", out var t) ? (t.GetString() ?? "").Trim() : "";
    }

    static string? ErrorMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var e))
                return e.ValueKind == JsonValueKind.Object && e.TryGetProperty("message", out var m) ? m.GetString() : e.ToString();
        }
        catch (JsonException) { }
        return body.Length > 200 ? body[..200] : body;
    }
}
