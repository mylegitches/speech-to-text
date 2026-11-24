using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SpeechToText.Config;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace SpeechToText.Local;

public sealed record LocalModel(string Id, string Label, long Bytes, string Sha256)
{
    public string Url => $"https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-{Id}.bin";
    public string FileName => $"ggml-{Id}.bin";
    public string Size => Bytes >= 1L << 30 ? $"{Bytes / (double)(1L << 30):0.0} GB" : $"{Bytes / (1 << 20)} MB";
}

/// <summary>
/// Offline transcription with whisper.cpp through Whisper.net.
/// Nothing heavy ships inside the exe: the native runtime for the chosen device (CPU ≈ 2 MB,
/// Vulkan ≈ 57 MB, CUDA 13 ≈ 150 MB, CUDA 12 ≈ 540 MB) and the model are downloaded on request
/// into %LOCALAPPDATA%\SpeechToText\whisper and verified. The model is unloaded after 5 idle minutes.
/// </summary>
public static partial class LocalWhisper
{
    /// <summary>Most accurate first; the first is the default. Checksums are Hugging Face's published SHA-256.</summary>
    public static readonly LocalModel[] Models =
    [
        new("large-v3", "large-v3 — best accuracy", 3_095_033_483,
            "64d182b440b98d5203c4f9bd541544d84c605196c4f7b845dfa11fb23594d1e2"),
        new("large-v3-turbo-q8_0", "large-v3-turbo — nearly as accurate, several times faster", 874_188_075,
            "317eb69c11673c9de1e1f0d459b253999804ec71ac4c23c17ecf5fbe24e259a1"),
    ];

    public static LocalModel Model(string? id) => Models.FirstOrDefault(m => m.Id == id) ?? Models[0];

    // Must match the Whisper.net PackageReference version.
    const string WhisperNetVersion = "1.9.1";

    // Local, not Roaming AppData: models are gigabytes and must never sync with a roaming profile.
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpeechToText", "whisper");
    static string ModelPath(LocalModel m) => Path.Combine(Root, "models", m.FileName);
    public static bool IsDownloaded(LocalModel m) => File.Exists(ModelPath(m));

    static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    // ---- native runtimes ----

    static (string Package, string Folder) RuntimePackage(LocalDevice d, Hardware hw) => d switch
    {
        LocalDevice.Cuda when hw.CudaMajor == 12 => ("whisper.net.runtime.cuda12.windows", "cuda12"),
        LocalDevice.Cuda => ("whisper.net.runtime.cuda.windows", "cuda"),
        LocalDevice.Vulkan => ("whisper.net.runtime.vulkan", "vulkan"),
        _ => ("whisper.net.runtime", ""),
    };

    static string RuntimeDir(string folder) => Path.Combine(Root, "runtimes", folder, "win-x64");

    public static bool RuntimeReady(LocalDevice d) =>
        File.Exists(Path.Combine(RuntimeDir(RuntimePackage(d, GpuDetect.Detect()).Folder), "whisper.dll"));

    /// <summary>Downloads the native runtime for a device from nuget.org, plus the CPU one as a fallback.</summary>
    public static async Task EnsureRuntimeAsync(LocalDevice device, IProgress<(string, double)>? progress, CancellationToken ct)
    {
        var hw = GpuDetect.Detect();
        foreach (var d in new[] { LocalDevice.Cpu, device }.Distinct())
        {
            var (package, folder) = RuntimePackage(d, hw);
            var dir = RuntimeDir(folder);
            if (File.Exists(Path.Combine(dir, "whisper.dll"))) continue;

            var url = $"https://api.nuget.org/v3-flatcontainer/{package}/{WhisperNetVersion}/{package}.{WhisperNetVersion}.nupkg";
            var tmp = Path.Combine(Root, $"{package}.nupkg.part");
            var label = d == LocalDevice.Cpu ? "CPU runtime" : $"{d.ToString().ToUpperInvariant()} runtime";
            await DownloadAsync(url, tmp, null, p => progress?.Report((label, p)), ct);

            var staging = dir + ".tmp";
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            Directory.CreateDirectory(staging);
            using (var zip = ZipFile.OpenRead(tmp))
                foreach (var e in zip.Entries.Where(e => e.FullName.StartsWith("build/win-x64/") && e.Name.EndsWith(".dll")))
                    e.ExtractToFile(Path.Combine(staging, e.Name));
            File.Delete(tmp);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.Move(staging, dir);
        }
    }

    // ---- models ----

    public static Task DownloadModelAsync(LocalModel m, IProgress<(string, double)>? progress, CancellationToken ct) =>
        DownloadAsync(m.Url, ModelPath(m), m.Sha256, p => progress?.Report(($"{m.Id} model", p)), ct);

    public static void DeleteModel(LocalModel m)
    {
        Unload();
        File.Delete(ModelPath(m));
    }

    /// <summary>Streams to a .part file, checks the SHA-256 when one is given, then moves it into place.</summary>
    static async Task DownloadAsync(string url, string dest, string? sha256, Action<double> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        var part = dest + ".part";
        using (var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? -1, done = 0;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var src = await resp.Content.ReadAsStreamAsync(ct))
            await using (var dst = File.Create(part))
            {
                var buf = new byte[1 << 20];
                int n;
                var tick = Stopwatch.StartNew();
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    hash.AppendData(buf, 0, n);
                    done += n;
                    if (total > 0 && tick.ElapsedMilliseconds > 150) { progress(done / (double)total); tick.Restart(); }
                }
            }
            if (sha256 != null && !Convert.ToHexStringLower(hash.GetHashAndReset()).Equals(sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(part);
                throw new InvalidOperationException("The download was damaged (checksum mismatch). Please try again.");
            }
        }
        File.Move(part, dest, overwrite: true);
        progress(1);
    }

    // ---- transcription ----

    static readonly SemaphoreSlim Gate = new(1, 1);
    static WhisperFactory? _factory;
    static string? _factoryModel;
    static bool _runtimeConfigured;
    static readonly System.Timers.Timer IdleTimer = new(TimeSpan.FromMinutes(5)) { AutoReset = false };

    static LocalWhisper() => IdleTimer.Elapsed += (_, _) => Unload();

    /// <summary>The runtime whisper.cpp actually loaded (e.g. Vulkan), once something has run.</summary>
    public static string? LoadedRuntime => RuntimeOptions.LoadedLibrary?.ToString();

    public static async Task<string> TranscribeAsync(AppSettings s, byte[] wav, string? prompt, CancellationToken ct = default)
    {
        var model = Model(s.For("local").Model);
        if (!IsDownloaded(model))
            throw new InvalidOperationException($"The {model.Id} model isn't downloaded yet. Download it in Settings → Transcription.");
        var hw = GpuDetect.Detect();
        var device = hw.Resolve(s.LocalDevice);
        if (!RuntimeReady(device) || !RuntimeReady(LocalDevice.Cpu))
            throw new InvalidOperationException("The on-device runtime isn't downloaded yet. Press Download in Settings → Transcription.");

        await Gate.WaitAsync(ct);
        try
        {
            IdleTimer.Stop();
            ConfigureRuntimeOnce(device, hw);
            if (_factory == null || _factoryModel != model.Id)
            {
                _factory?.Dispose();
                _factory = WhisperFactory.FromPath(ModelPath(model), new WhisperFactoryOptions { UseGpu = device != LocalDevice.Cpu });
                _factoryModel = model.Id;
            }

            var builder = _factory.CreateBuilder()
                .WithLanguage(s.Language.Length > 0 ? s.Language : "auto")
                .WithThreads(Math.Max(1, Environment.ProcessorCount)); // whisper.cpp defaults to only a few threads
            if (prompt != null) builder = builder.WithPrompt(prompt);
            await using var processor = builder.Build();

            var text = new StringBuilder();
            await using var audio = new MemoryStream(wav);
            await foreach (var segment in processor.ProcessAsync(audio, ct))
                text.Append(segment.Text);
            return Clean(text.ToString());
        }
        finally
        {
            IdleTimer.Start();
            Gate.Release();
        }
    }

    /// <summary>
    /// whisper.cpp picks its native library once per process. Point it at our download folder and
    /// put the chosen device first, with CPU as the fallback.
    /// </summary>
    static void ConfigureRuntimeOnce(LocalDevice device, Hardware hw)
    {
        if (_runtimeConfigured) return;
        RuntimeOptions.LibraryPath = Path.Combine(Root, "whisper.dll"); // only its folder is used: <Root>\runtimes\...
        var first = device switch
        {
            LocalDevice.Cuda when hw.CudaMajor == 12 => RuntimeLibrary.Cuda12,
            LocalDevice.Cuda => RuntimeLibrary.Cuda,
            LocalDevice.Vulkan => RuntimeLibrary.Vulkan,
            _ => RuntimeLibrary.Cpu,
        };
        RuntimeOptions.RuntimeLibraryOrder = first == RuntimeLibrary.Cpu ? [RuntimeLibrary.Cpu] : [first, RuntimeLibrary.Cpu];
        _runtimeConfigured = true;
    }

    /// <summary>True when the device was changed after a runtime was already loaded (needs an app restart).</summary>
    public static bool NeedsRestartFor(LocalDevice device) => _runtimeConfigured && LoadedRuntime is { } loaded &&
        !loaded.StartsWith(device == LocalDevice.Cpu ? "Cpu" : device.ToString(), StringComparison.OrdinalIgnoreCase);

    public static void Unload()
    {
        Gate.Wait();
        try { _factory?.Dispose(); _factory = null; _factoryModel = null; }
        finally { Gate.Release(); }
    }

    /// <summary>Drops whisper.cpp's non-speech markers such as [BLANK_AUDIO] or [Music].</summary>
    static string Clean(string s) => Regex.Replace(MarkerRx().Replace(s, " "), @"\s{2,}", " ").Trim();

    [GeneratedRegex(@"\[[^\]]{1,40}\]")] private static partial Regex MarkerRx();
}
