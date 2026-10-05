using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using ScreenRecorder.Core;

namespace ScreenRecorder.Capture;

/// <summary>
/// Captures system audio (WASAPI loopback) and microphone, converts both to 48 kHz stereo float,
/// mixes them, and emits PCM16 chunks timed against the master clock so silence gaps never cause drift.
/// </summary>
public sealed class AudioMixerCapture : IDisposable
{
    public const int SampleRate = 48000;
    public const int Channels = 2;

    private sealed class Source : IDisposable
    {
        public required IWaveIn Capture;
        public required BufferedWaveProvider Buffer;
        public required ISampleProvider Output;
        public float Gain;
        public volatile float Peak;
        public long LastDataTicks;
        public bool IsMic;
        public string Name = "";
        public volatile bool Lost;
        public void Dispose() { Capture.StopRecording(); Capture.Dispose(); }
    }

    private readonly List<Source> _sources = new();
    private readonly MasterClock _clock;
    private Thread? _pump;
    private volatile bool _stop;
    private long _framesProduced;

    /// <summary>pcm16 bytes, byte count, pts (hns), duration (hns).</summary>
    public event Action<byte[], int, long, long>? Chunk;

    public AudioMixerCapture(MasterClock clock) => _clock = clock;

    public bool HasSources => _sources.Count > 0;

    /// <summary>Raised (background thread) when a capture device stops delivering audio, e.g. it was unplugged.</summary>
    public event Action<string>? DeviceLost;

    public void AddSystemAudio(float gain)
    {
        var cap = new WasapiLoopbackCapture();
        Register(cap, gain, false, "System audio");
    }

    public void AddMicrophone(string? deviceId, float gain)
    {
        using var enumerator = new MMDeviceEnumerator();
        MMDevice dev = deviceId is null
            ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications)
            : enumerator.GetDevice(deviceId);
        Register(new WasapiCapture(dev), gain, true, "Microphone");
    }

    private void Register(IWaveIn cap, float gain, bool isMic, string name)
    {
        var buffer = new BufferedWaveProvider(cap.WaveFormat)
        {
            ReadFully = true,
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromSeconds(2)
        };
        ISampleProvider s = buffer.ToSampleProvider();
        if (s.WaveFormat.Channels == 1) s = new MonoToStereoSampleProvider(s);
        else if (s.WaveFormat.Channels > 2) s = new MultiplexingSampleProvider(new[] { s }, 2);
        if (s.WaveFormat.SampleRate != SampleRate) s = new WdlResamplingSampleProvider(s, SampleRate);

        var src = new Source { Capture = cap, Buffer = buffer, Output = s, Gain = gain, IsMic = isMic, Name = name };
        cap.DataAvailable += (_, e) =>
        {
            src.LastDataTicks = Environment.TickCount64;
            buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
            src.Peak = Math.Max(src.Peak * 0.9f, PeakOf(e.Buffer, e.BytesRecorded, cap.WaveFormat));
        };
        cap.RecordingStopped += (_, e) =>
        {
            if (_stop || e.Exception is null) return;
            src.Lost = true; src.Peak = 0;
            DeviceLost?.Invoke(name + " disconnected; continuing with silence for it.");
        };
        _sources.Add(src);
    }

    private static float LevelOf(Source? s) =>
        s is null || s.Lost || Environment.TickCount64 - s.LastDataTicks > 250 ? 0 : Math.Min(1f, s.Peak);

    public float MicLevel => LevelOf(_sources.FirstOrDefault(s => s.IsMic));

    private static float PeakOf(byte[] data, int count, WaveFormat fmt)
    {
        if (fmt.Encoding != WaveFormatEncoding.IeeeFloat && fmt.BitsPerSample != 32) return 0;
        float peak = 0;
        for (int i = 0; i + 4 <= count; i += 4) peak = Math.Max(peak, Math.Abs(BitConverter.ToSingle(data, i)));
        return peak;
    }

    public float SystemLevel => LevelOf(_sources.FirstOrDefault(s => !s.IsMic));

    public void Start()
    {
        foreach (var s in _sources) s.Capture.StartRecording();
        _stop = false;
        _pump = new Thread(Pump) { IsBackground = true, Name = "AudioPump", Priority = ThreadPriority.AboveNormal };
        _pump.Start();
    }

    /// <summary>Drops audio buffered while paused so it is not played back late.</summary>
    public void FlushBuffers()
    {
        foreach (var s in _sources) s.Buffer.ClearBuffer();
    }

    private void Pump()
    {
        var mix = new float[SampleRate * Channels]; // up to 1 s per read
        var tmp = new float[mix.Length];
        var pcm = new byte[mix.Length * 2];
        while (!_stop)
        {
            long due = AudioMath.FramesDue(_clock.NowHns, SampleRate) - _framesProduced;
            if (due >= SampleRate / 100) // emit in >= 10 ms chunks
            {
                int frames = (int)Math.Min(due, SampleRate);
                int samples = frames * Channels;
                Array.Clear(mix, 0, samples);
                foreach (var s in _sources)
                {
                    int read = s.Output.Read(tmp, 0, samples);
                    if (read < samples) Array.Clear(tmp, read, samples - read);
                    AudioMath.MixInto(mix, tmp, samples, s.Gain);
                }
                AudioMath.Clip(mix, samples);
                AudioMath.ToPcm16(mix, samples, pcm);
                long pts = _framesProduced * 10_000_000 / SampleRate;
                long dur = frames * 10_000_000L / SampleRate;
                _framesProduced += frames;
                Chunk?.Invoke(pcm, samples * 2, pts, dur);
            }
            else Thread.Sleep(5);
        }
    }

    public void Stop()
    {
        _stop = true;
        _pump?.Join(2000);
        foreach (var s in _sources) s.Dispose();
        _sources.Clear();
    }

    public void Dispose() => Stop();
}
