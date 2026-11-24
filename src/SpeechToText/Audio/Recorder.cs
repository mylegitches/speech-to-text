using System.IO;
using NAudio.Wave;

namespace SpeechToText.Audio;

/// <summary>Records 16 kHz mono PCM into memory — the format every speech API accepts.</summary>
public sealed class Recorder : IDisposable
{
    const int SampleRate = 16000;

    readonly MemoryStream _pcm = new();
    readonly object _lock = new();
    WaveIn? _wave;
    TaskCompletionSource? _stopped;

    /// <summary>Peak level 0..1 for each ~50 ms buffer. Raised on a background thread.</summary>
    public event Action<float>? Level;

    public bool IsRecording => _wave != null;

    public static IReadOnlyList<string> Devices()
    {
        var list = new List<string>();
        for (int i = 0; i < WaveIn.DeviceCount; i++)
            list.Add(WaveIn.GetCapabilities(i).ProductName);
        return list;
    }

    public void Start(int device)
    {
        if (_wave != null) return;
        lock (_lock) _pcm.SetLength(0);
        _stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _wave = new WaveIn
        {
            DeviceNumber = device, // -1 = WAVE_MAPPER (Windows default)
            WaveFormat = new WaveFormat(SampleRate, 16, 1),
            BufferMilliseconds = 50,
        };
        _wave.DataAvailable += OnData;
        _wave.RecordingStopped += (_, _) => _stopped?.TrySetResult();
        _wave.StartRecording();
    }

    void OnData(object? sender, WaveInEventArgs e)
    {
        lock (_lock) _pcm.Write(e.Buffer, 0, e.BytesRecorded);
        int peak = 0;
        for (int i = 0; i + 1 < e.BytesRecorded; i += 2)
            peak = Math.Max(peak, Math.Abs((int)BitConverter.ToInt16(e.Buffer, i)));
        Level?.Invoke(peak / 32768f);
    }

    /// <summary>Stops and returns a WAV file, or null when nothing useful was captured.</summary>
    public async Task<byte[]?> StopAsync()
    {
        var wave = _wave;
        if (wave == null) return null;
        wave.StopRecording();
        await _stopped!.Task;
        wave.Dispose();
        _wave = null;

        byte[] pcm;
        lock (_lock) pcm = _pcm.ToArray();
        return pcm.Length < SampleRate * 2 * 3 / 10 ? null : ToWav(pcm); // < 0.3 s → discard
    }

    public static byte[] ToWav(byte[] pcm)
    {
        using var ms = new MemoryStream(44 + pcm.Length);
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(36 + pcm.Length); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(SampleRate); w.Write(SampleRate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(pcm.Length); w.Write(pcm);
        return ms.ToArray();
    }

    public void Dispose() { _wave?.Dispose(); _wave = null; }
}
