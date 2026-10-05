using ScreenRecorder.Core;
using ScreenRecorder.Encoding;

namespace ScreenRecorder.Capture;

/// <summary>Coordinates screen capture, audio capture and the MP4 writer for one recording.</summary>
public sealed class RecordingSession : IDisposable
{
    private readonly RecordingOptions _options;
    private readonly MasterClock _clock = new();
    private IScreenSource? _screen;
    private AudioMixerCapture? _audio;
    private WebcamCapturer? _webcam;
    private Mp4Writer? _writer;
    private Thread? _videoThread;
    private volatile bool _stop;
    private string _tempPath = "", _finalPath = "";

    public RecordingState State { get; private set; } = RecordingState.Idle;
    public TimeSpan Elapsed => TimeSpan.FromTicks(_clock.NowHns);
    public long VideoFramesWritten { get; private set; }
    public event Action<string>? Warning;
    public float SystemLevel => _audio?.SystemLevel ?? 0;
    public float MicLevel => _audio?.MicLevel ?? 0;

    /// <summary>Raised on a background thread when recording must end (low disk, write failure, window closed).</summary>
    public event Action<string>? StopRequested;

    private const long WarnFreeBytes = 1L << 30;
    private const long MinFreeBytes = 200L << 20;
    private const long StartFreeBytes = 500L << 20;

    public RecordingSession(RecordingOptions options) => _options = options;

    public void Start()
    {
        if (State != RecordingState.Idle) throw new InvalidOperationException("Already recording.");
        bool compat = _options.ForceCompatibilityCapture || !ScreenCapturer.IsSupported;
        if (compat && _options.Target.Mode == CaptureMode.Window) throw new PlatformNotSupportedException("Windows Graphics Capture is required to record a single window.");

        Directory.CreateDirectory(_options.OutputFolder);
        long free = FreeSpace(_options.OutputFolder);
        if (free >= 0 && free < StartFreeBytes)
            throw new IOException($"Not enough free disk space ({free / (1 << 20)} MB). At least 500 MB is required.");
        // Fail early if the folder is not writable.
        string probePath = Path.Combine(_options.OutputFolder, ".write-test-" + Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(probePath, Array.Empty<byte>());
        File.Delete(probePath);
        _finalPath = UniquePath(OutputPaths.BuildFinalPath(_options, DateTime.Now));
        _tempPath = Path.ChangeExtension(_finalPath, ".tmp.mp4");

        if (!compat && !TryStartScreen(new ScreenCapturer()))
        {
            if (_options.Target.Mode == CaptureMode.Window) { Cleanup(); throw new TimeoutException("No frames from screen capture."); }
            Warning?.Invoke("Standard capture produced no frames; switched to compatibility capture.");
            compat = true;
        }
        if (compat && !TryStartScreen(new DuplicationCapturer())) { Cleanup(); throw new TimeoutException("No frames from screen capture."); }

        if (_options.CaptureWebcam)
        {
            try
            {
                var cam = new WebcamCapturer();
                cam.Start(_options.WebcamDeviceId);
                _webcam = cam;
            }
            catch (Exception ex) { Warning?.Invoke("Camera unavailable: " + ex.Message); }
        }

        _audio = new AudioMixerCapture(_clock);
        _audio.DeviceLost += m => Warning?.Invoke(m);
        TryAdd(() => { if (_options.CaptureSystemAudio) _audio.AddSystemAudio(_options.SystemGain); }, "System audio unavailable");
        TryAdd(() => { if (_options.CaptureMicrophone) _audio.AddMicrophone(_options.MicrophoneDeviceId, _options.MicrophoneGain); }, "Microphone unavailable");

        _writer = new Mp4Writer(_tempPath, _screen.Width, _screen.Height, _options.FramesPerSecond,
            VideoQuality.Bitrate(_options.Quality, _screen.Width, _screen.Height, _options.FramesPerSecond), _options.AudioBitrate, AudioMixerCapture.SampleRate, AudioMixerCapture.Channels,
            withAudio: true);
        var writer = _writer;
        _audio.Chunk += (data, count, pts, dur) => Guarded(() => writer.WriteAudio(data, count, pts, dur));

        _clock.Start();
        _audio.Start();
        _stop = false;
        _videoThread = new Thread(VideoLoop) { IsBackground = true, Name = "VideoLoop", Priority = ThreadPriority.AboveNormal };
        _videoThread.Start();
        State = RecordingState.Recording;
    }

    private void TryAdd(Action add, string message)
    {
        try { add(); } catch (Exception ex) { Warning?.Invoke($"{message}: {ex.Message}"); }
    }

    private int _failed;

    private void Guarded(Action write)
    {
        try { write(); }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref _failed, 1) == 0) StopRequested?.Invoke("Writing the recording failed: " + ex.Message);
        }
    }

    private bool TryStartScreen(IScreenSource source)
    {
        _screen?.Dispose();
        _screen = source;
        try
        {
            source.SourceClosed += () => StopRequested?.Invoke("The captured window was closed.");
            source.Start(_options.Target, _options.CaptureCursor);
        }
        catch when (source is ScreenCapturer && _options.Target.Mode != CaptureMode.Window) { return false; }
        var probe = new byte[source.Width * source.Height * 4];
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!source.TryCopyLatest(probe))
        {
            if (DateTime.UtcNow > deadline) return false;
            Thread.Sleep(10);
        }
        return true;
    }

    private static long FreeSpace(string folder)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))!).AvailableFreeSpace; }
        catch { return -1; }
    }

    private PixelRect _camRect;
    private readonly object _camLock = new();

    public int OutputWidth => _screen?.Width ?? 0;
    public int OutputHeight => _screen?.Height ?? 0;
    public bool HasWebcam => _webcam is not null;

    /// <summary>Current webcam rectangle in output pixels.</summary>
    public PixelRect WebcamRect { get { lock (_camLock) return _camRect; } }

    /// <summary>Moves/resizes the webcam while recording. The rectangle is clamped to the frame (square for Circle).</summary>
    public void SetWebcamRect(PixelRect r)
    {
        if (_screen is null) return;
        int w = Math.Clamp(r.Width, 64, _screen.Width) & ~1, h = Math.Clamp(r.Height, 64, _screen.Height) & ~1;
        if (_options.WebcamShape == WebcamShape.Circle) w = h = Math.Min(w, h);
        int x = Math.Clamp(r.X, 0, _screen.Width - w), y = Math.Clamp(r.Y, 0, _screen.Height - h);
        lock (_camLock) _camRect = new PixelRect(x, y, w, h);
    }

    private void VideoLoop()
    {
        long frameDur = 10_000_000L / _options.FramesPerSecond;
        long index = 0;
        var frame = new byte[_screen!.Width * _screen.Height * 4];
        var cam = _webcam;
        byte[]? camFrame = cam is null ? null : new byte[cam.Width * cam.Height * 4];
        var layoutRect = WebcamLayout.Compute(_screen.Width, _screen.Height, _options.WebcamSize, _options.WebcamCorner);
        if (_options.WebcamShape == WebcamShape.Circle)
        {
            int side = Math.Min(layoutRect.Width, layoutRect.Height) & ~1;
            layoutRect = new PixelRect(layoutRect.X + (layoutRect.Width - side) / 2, layoutRect.Y + (layoutRect.Height - side) / 2, side, side);
        }
        _camRect = layoutRect;
        long nextDiskCheck = 0;
        bool warned = false;
        while (!_stop && _failed == 0)
        {
            long now = _clock.NowHns;
            if (now >= nextDiskCheck)
            {
                nextDiskCheck = now + 20_000_000;
                long free = FreeSpace(_options.OutputFolder);
                if (free >= 0 && free < MinFreeBytes)
                {
                    if (Interlocked.Exchange(ref _failed, 1) == 0) StopRequested?.Invoke("Disk space is almost full; recording stopped to protect the file.");
                    break;
                }
                if (free >= 0 && free < WarnFreeBytes && !warned) { warned = true; Warning?.Invoke("Low disk space (under 1 GB)."); }
            }
            long target = index * frameDur;
            if (now < target) { Thread.Sleep(1); continue; }
            if (_clock.IsPaused) { Thread.Sleep(5); continue; }
            if (_screen.TryCopyLatest(frame))
            {
                if (cam is not null && cam.TryCopyLatest(camFrame!))
                    WebcamCompositor.Blend(frame, _screen.Width, _screen.Height, camFrame!, cam.Width, cam.Height, WebcamRect, _options.WebcamMirror, _options.WebcamShape);
                Guarded(() => _writer!.WriteVideo(frame, target, frameDur));
                VideoFramesWritten++;
            }
            index++;
            // If we fall far behind (e.g. heavy load) skip ahead instead of bursting.
            long behind = (_clock.NowHns - index * frameDur) / frameDur;
            if (behind > 5) index += behind;
        }
    }
    public void Pause()
    {
        if (State != RecordingState.Recording) return;
        _clock.Pause();
        State = RecordingState.Paused;
    }

    public void Resume()
    {
        if (State != RecordingState.Paused) return;
        _audio?.FlushBuffers();
        _clock.Resume();
        State = RecordingState.Recording;
    }

    /// <summary>Stops, finalises the file and returns its final path.</summary>
    public string Stop()
    {
        if (State is RecordingState.Idle or RecordingState.Stopping) return _finalPath;
        State = RecordingState.Stopping;
        _stop = true;
        Exception? first = null;
        void Try(Action a) { try { a(); } catch (Exception ex) { first ??= ex; } }
        Try(() => _videoThread?.Join(3000));
        Try(() => _audio?.Stop());
        Try(() => _webcam?.Dispose());
        Try(() => _screen?.Dispose());
        Try(() => _writer?.Dispose());
        _writer = null;
        _clock.Stop();
        try
        {
            if (File.Exists(_tempPath)) File.Move(_tempPath, _finalPath, overwrite: false);
        }
        catch (Exception ex) { first ??= ex; }
        State = RecordingState.Idle;
        if (first is not null && !File.Exists(_finalPath))
            throw new IOException("Recording could not be finalised; the partial file will be recovered on next start.", first);
        return _finalPath;
    }

    private void Cleanup()
    {
        _audio?.Dispose();
        _webcam?.Dispose();
        _screen?.Dispose();
        _writer?.Dispose();
    }

    /// <summary>Renames leftover *.tmp.mp4 files from a crashed session so they can be played. Returns the recovered files.</summary>
    public static List<string> RecoverOrphans(string folder)
    {
        var recovered = new List<string>();
        if (!Directory.Exists(folder)) return recovered;
        foreach (string tmp in Directory.GetFiles(folder, "*.tmp.mp4"))
        {
            try
            {
                if (new FileInfo(tmp).Length == 0) { File.Delete(tmp); continue; }
                string baseName = Path.GetFileName(tmp);
                baseName = baseName.Substring(0, baseName.Length - ".tmp.mp4".Length);
                string target = UniquePath(Path.Combine(folder, baseName + " (recovered).mp4"));
                File.Move(tmp, target);
                recovered.Add(target);
            }
            catch (IOException) { /* in use by another running instance */ }
        }
        return recovered;
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path)!, name = Path.GetFileNameWithoutExtension(path);
        for (int i = 2; ; i++)
        {
            string p = Path.Combine(dir, $"{name} ({i}).mp4");
            if (!File.Exists(p)) return p;
        }
    }

    public void Dispose()
    {
        if (State is RecordingState.Recording or RecordingState.Paused) Stop();
    }
}

