using ScreenRecorder.Capture;
using ScreenRecorder.Core;

namespace ScreenRecorder.Integration.Tests;

/// <summary>
/// End-to-end recordings of the real desktop, verified with ffmpeg. Requires an interactive desktop session.
/// Set FFMPEG_PATH, or have ffmpeg on PATH; set REQUIRE_FFMPEG=1 in CI to fail instead of skipping.
/// </summary>
[Trait("Category", "Integration")]
public class RecordingIntegrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SRIntegration_" + Guid.NewGuid().ToString("N"));
    private readonly string? _ffmpeg = MediaInfo.FindFfmpeg();

    private bool Unavailable()
    {
        if (_ffmpeg is not null) return false;
        if (Environment.GetEnvironmentVariable("REQUIRE_FFMPEG") == "1") Assert.Fail("ffmpeg not found");
        return true;
    }

    private MediaInfo Record(CaptureTarget target, Func<RecordingSession, int> script, bool compat = false, bool system = true)
    {
        var session = new RecordingSession(new RecordingOptions
        {
            OutputFolder = _dir, Target = target, CaptureMicrophone = false, CaptureSystemAudio = system,
            ForceCompatibilityCapture = compat
        });
        session.Start();
        script(session);
        var path = session.Stop();
        Assert.True(File.Exists(path));
        Assert.False(Directory.GetFiles(_dir, "*.part").Any() || Directory.GetFiles(_dir, "*.tmp").Any(), "temp file left behind");
        return MediaInfo.Probe(_ffmpeg!, path);
    }

    [Fact]
    public void Monitor_recording_has_expected_duration_audio_and_sync()
    {
        if (Unavailable()) return;
        var m = Record(CaptureTarget.PrimaryMonitor, s => { Thread.Sleep(5000); return 0; });
        Assert.InRange(m.Duration, 4.5, 5.6);
        Assert.True(m.Width > 0 && m.Height > 0 && m.Width % 2 == 0 && m.Height % 2 == 0);
        Assert.InRange(m.Fps, 24, 60);
        Assert.True(m.HasAudio, "audio stream missing");
        Assert.True(Math.Abs(m.AudioStart - m.VideoStart) < 0.15, $"A/V start offset {m.AudioStart - m.VideoStart:F3}s");
    }

    [Fact]
    public void Region_recording_matches_selected_size()
    {
        if (Unavailable()) return;
        var m = Record(new CaptureTarget(CaptureMode.Region, 0, new PixelRect(100, 100, 800, 450)), s => { Thread.Sleep(3000); return 0; });
        Assert.Equal(800, m.Width);
        Assert.Equal(450, m.Height);
    }

    [Fact]
    public void Pause_does_not_add_dead_time()
    {
        if (Unavailable()) return;
        var m = Record(CaptureTarget.PrimaryMonitor, s =>
        {
            Thread.Sleep(2000); s.Pause(); Thread.Sleep(3000); s.Resume(); Thread.Sleep(2000); return 0;
        });
        Assert.InRange(m.Duration, 3.5, 4.8);
    }

    [Fact]
    public void Compatibility_capture_produces_video()
    {
        if (Unavailable()) return;
        var m = Record(CaptureTarget.PrimaryMonitor, s => { Thread.Sleep(3000); return 0; }, compat: true);
        Assert.InRange(m.Duration, 2.5, 3.8);
        Assert.True(m.Width > 0);
    }

    [Fact]
    public void Probe_parser_reads_ffmpeg_summary()
    {
        var m = MediaInfo.Parse("  Duration: 00:00:05.02, start: 0.000000, bitrate: 900 kb/s\n  Stream #0:0[0x1]: Video: h264, yuv420p, 1920x1080 [SAR 1:1], 30 fps, 30 tbr\n  Stream #0:1[0x2]: Audio: aac, 48000 Hz, stereo, start: 0.021");
        Assert.Equal(5.02, m.Duration, 2);
        Assert.Equal(1920, m.Width);
        Assert.Equal(30, m.Fps);
        Assert.True(m.HasAudio);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }
}
