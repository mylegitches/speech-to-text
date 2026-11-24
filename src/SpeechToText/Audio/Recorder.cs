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
    SystemAudio? _system;
    float _systemLevel;

    /// <summary>Why computer audio couldn't be captured on the last start, or null.</summary>
    public string? SystemAudioError { get; private set; }

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

    /// <param name="device">Microphone index, -1 for the Windows default.</param>
    /// <param name="systemAudioDevice">Also record this output device (empty = Windows default), or null for mic only.</param>
    public void Start(int device, string? systemAudioDevice = null)
    {
        if (_wave != null) return;
        lock (_lock) _pcm.SetLength(0);
        SystemAudioError = null;
        _systemLevel = 0;
        if (systemAudioDevice != null)
        {
            try
            {
                _system = new SystemAudio();
                _system.Level += l => _systemLevel = l;
                _system.Start(systemAudioDevice);
            }
            catch (Exception ex)
            {
                // Never lose the dictation over this: carry on with the mic alone and report it.
                SystemAudioError = ex.Message;
                _system?.Dispose();
                _system = null;
            }
        }
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
        Level?.Invoke(Math.Max(peak / 32768f, _systemLevel)); // the meter shows whichever is louder
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

        if (_system is { } system)
        {
            _system = null;
            try { pcm = Mix(pcm, await system.StopAsync()); }
            catch (Exception ex) { SystemAudioError = ex.Message; } // keep the mic recording
            finally { system.Dispose(); }
        }
        return pcm.Length < SampleRate * 2 * 3 / 10 ? null : ToWav(pcm); // < 0.3 s → discard
    }

    /// <summary>
    /// Adds computer audio to the mic track (both 16 kHz mono). The mic's length is the timeline.
    /// If the sum would clip, the whole track is scaled down instead of distorting.
    /// </summary>
    static byte[] Mix(byte[] micPcm, float[] system)
    {
        if (system.Length == 0) return micPcm;
        int n = micPcm.Length / 2;
        var mixed = new float[n];
        float peak = 0;
        for (int i = 0; i < n; i++)
        {
            float s = BitConverter.ToInt16(micPcm, i * 2) / 32768f + (i < system.Length ? system[i] : 0);
            mixed[i] = s;
            peak = Math.Max(peak, Math.Abs(s));
        }
        float scale = peak > 0.99f ? 0.99f / peak : 1f;
        var outPcm = new byte[n * 2];
        for (int i = 0; i < n; i++)
            BitConverter.TryWriteBytes(outPcm.AsSpan(i * 2), (short)Math.Round(mixed[i] * scale * 32767));
        return outPcm;
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

    public void Dispose() { _wave?.Dispose(); _wave = null; _system?.Dispose(); _system = null; }
}
