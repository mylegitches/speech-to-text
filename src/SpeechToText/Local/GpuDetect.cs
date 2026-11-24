using System.IO;
using Microsoft.Win32;
using SpeechToText.Config;

namespace SpeechToText.Local;

/// <summary>What this PC can run Whisper on.</summary>
public sealed record Hardware(
    IReadOnlyList<string> Gpus,
    bool Vulkan,
    int CudaMajor)          // 13 or 12 when an NVIDIA driver and a CUDA runtime are installed, otherwise 0
{
    public bool Cuda => CudaMajor > 0;

    /// <summary>Best device for Auto: CUDA, then Vulkan, then CPU.</summary>
    public LocalDevice Best => Cuda ? LocalDevice.Cuda : Vulkan && Gpus.Count > 0 ? LocalDevice.Vulkan : LocalDevice.Cpu;

    public LocalDevice Resolve(LocalDevice wanted) => wanted switch
    {
        LocalDevice.Auto => Best,
        LocalDevice.Cuda when !Cuda => Best,
        LocalDevice.Vulkan when !Vulkan => Best,
        _ => wanted,
    };

    public string Describe(LocalDevice device) => device switch
    {
        LocalDevice.Cuda => $"{Gpus.FirstOrDefault(g => g.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) ?? "NVIDIA GPU"} (CUDA {CudaMajor})",
        LocalDevice.Vulkan => $"{Gpus.FirstOrDefault() ?? "GPU"} (Vulkan)",
        _ => "CPU",
    };
}

public static class GpuDetect
{
    static Hardware? _cached;

    public static Hardware Detect() => _cached ??= Probe();

    static Hardware Probe()
    {
        var sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var gpus = DisplayAdapters();
        bool vulkan = File.Exists(Path.Combine(sys, "vulkan-1.dll"));

        int cuda = 0;
        if (File.Exists(Path.Combine(sys, "nvcuda.dll"))) // NVIDIA driver present
        {
            // Whisper.net's CUDA builds also need the CUDA runtime from the CUDA Toolkit.
            if (FindOnSearchPath("cudart64_13*.dll")) cuda = 13;
            else if (FindOnSearchPath("cudart64_12*.dll")) cuda = 12;
        }
        return new Hardware(gpus, vulkan, cuda);
    }

    /// <summary>Real GPUs from the display adapter class, skipping virtual/basic/USB display drivers.</summary>
    static List<string> DisplayAdapters()
    {
        var names = new List<string>();
        using var cls = Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
        if (cls == null) return names;
        foreach (var sub in cls.GetSubKeyNames().Where(n => n.All(char.IsDigit)))
        {
            using var k = cls.OpenSubKey(sub);
            if (k?.GetValue("DriverDesc") is not string desc) continue;
            if (desc.Contains("Basic", StringComparison.OrdinalIgnoreCase)
                || desc.Contains("DisplayLink", StringComparison.OrdinalIgnoreCase)
                || desc.Contains("Remote", StringComparison.OrdinalIgnoreCase)
                || desc.Contains("Virtual", StringComparison.OrdinalIgnoreCase)) continue;
            if (!names.Contains(desc)) names.Add(desc);
        }
        // Prefer a discrete NVIDIA/AMD card first when several are present.
        return names.OrderBy(n => n.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || n.Contains("Radeon", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToList();
    }

    static bool FindOnSearchPath(string pattern)
    {
        var dirs = new List<string>();
        if (Environment.GetEnvironmentVariable("CUDA_PATH") is { Length: > 0 } cudaPath)
        {
            dirs.Add(Path.Combine(cudaPath, "bin"));
            dirs.Add(Path.Combine(cudaPath, "bin", "x64"));
        }
        dirs.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries));
        foreach (var d in dirs.Distinct())
        {
            try { if (Directory.Exists(d) && Directory.EnumerateFiles(d, pattern).Any()) return true; }
            catch { /* unreadable PATH entry */ }
        }
        return false;
    }
}
