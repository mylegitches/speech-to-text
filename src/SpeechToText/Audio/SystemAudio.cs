using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace SpeechToText.Audio;

/// <summary>
/// Records what an output device is playing (WASAPI loopback), so calls and videos can be
/// transcribed together with the microphone.
/// WASAPI sends no loopback data while nothing plays, which would make the recording shorter than
/// the mic and throw the two out of sync. A silent stream is played on the same device to keep
/// the timeline continuous.
/// </summary>
// NAudio 3 marks WasapiLoopbackCapture/WasapiOut obsolete in favour of new builder APIs. They still
// work and are the documented, well-tested path; switch once the builders are documented.
#pragma warning disable CS0618
public sealed class SystemAudio : IDisposable
{
    readonly MemoryStream _raw = new();
    readonly object _lock = new();
    WasapiLoopbackCapture? _capture;
    WasapiOut? _keepAlive;
    TaskCompletionSource? _stopped;

    public event Action<float>? Level;
    public bool IsRecording => _capture != null;

    public sealed record OutputDevice(string Id, string Name);

    public static IReadOnlyList<OutputDevice> Devices()
    {
        using var e = new MMDeviceEnumerator();
        return e.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .Select(d => new OutputDevice(d.ID, d.FriendlyName)).ToList();
    }

    /// <param name="deviceId">An output device ID, or empty for the Windows default.</param>
    public void Start(string deviceId)
    {
        if (_capture != null) return;
        lock (_lock) _raw.SetLength(0);

        using var e = new MMDeviceEnumerator();
        var device = deviceId.Length > 0 ? TryGet(e, deviceId) ?? e.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                                          : e.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

        _capture = new WasapiLoopbackCapture(device);
        _stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _capture.DataAvailable += OnData;
        _capture.RecordingStopped += (_, _) => _stopped?.TrySetResult();

        // Silent stream on the same device keeps loopback packets flowing during silence.
        _keepAlive = new WasapiOut(device, AudioClientShareMode.Shared, true, 100);
        _keepAlive.Init(new SilenceProvider(WaveFormat.CreateIeeeFloatWaveFormat(_capture.WaveFormat.SampleRate, 2)));
        _keepAlive.Play();

        _capture.StartRecording();
    }

    static MMDevice? TryGet(MMDeviceEnumerator e, string id)
    {
        try { var d = e.GetDevice(id); return d.State == DeviceState.Active ? d : null; }
        catch { return null; } // unplugged headset etc. → fall back to the default
    }

    void OnData(object? sender, WaveInEventArgs a)
    {
        lock (_lock) _raw.Write(a.Buffer, 0, a.BytesRecorded);
        if (_capture?.WaveFormat.Encoding == WaveFormatEncoding.IeeeFloat || _capture?.WaveFormat.BitsPerSample == 32)
        {
            float peak = 0;
            for (int i = 0; i + 3 < a.BytesRecorded; i += 4) peak = Math.Max(peak, Math.Abs(BitConverter.ToSingle(a.Buffer, i)));
            Level?.Invoke(Math.Min(1, peak));
        }
    }

    /// <summary>Stops and returns the recording as 16 kHz mono float samples.</summary>
    public async Task<float[]> StopAsync()
    {
        var capture = _capture;
        if (capture == null) return [];
        capture.StopRecording();
        await _stopped!.Task;
        var format = capture.WaveFormat;
        capture.Dispose();
        _capture = null;
        _keepAlive?.Stop();
        _keepAlive?.Dispose();
        _keepAlive = null;

        byte[] raw;
        lock (_lock) raw = _raw.ToArray();
        return raw.Length == 0 ? [] : To16kMono(raw, format);
    }

    static float[] To16kMono(byte[] raw, WaveFormat format)
    {
        ISampleProvider source = new RawSourceWaveStream(new MemoryStream(raw), format).ToSampleProvider();
        if (source.WaveFormat.Channels > 1) source = new DownmixToMono(source);
        if (source.WaveFormat.SampleRate != 16000) source = new WdlResamplingSampleProvider(source, 16000);

        var result = new List<float>(raw.Length / format.BlockAlign);
        var buf = new float[16000];
        int n;
        while ((n = source.Read(buf.AsSpan())) > 0) result.AddRange(buf.AsSpan(0, n));
        return result.ToArray();
    }

    /// <summary>Averages any number of channels into one (StereoToMono only handles two).</summary>
    sealed class DownmixToMono(ISampleProvider source) : ISampleProvider
    {
        readonly int _ch = source.WaveFormat.Channels;
        float[] _buf = [];
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);

        public int Read(Span<float> buffer)
        {
            int need = buffer.Length * _ch;
            if (_buf.Length < need) _buf = new float[need];
            int got = source.Read(_buf.AsSpan(0, need));
            int frames = got / _ch;
            for (int f = 0; f < frames; f++)
            {
                float sum = 0;
                for (int c = 0; c < _ch; c++) sum += _buf[f * _ch + c];
                buffer[f] = sum / _ch;
            }
            return frames;
        }
    }

    public void Dispose()
    {
        _capture?.Dispose();
        _keepAlive?.Dispose();
    }
}
