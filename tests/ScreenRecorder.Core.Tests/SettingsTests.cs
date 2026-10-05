using ScreenRecorder.Core;
using Xunit;

namespace ScreenRecorder.Core.Tests;

public class SettingsTests
{
    private static string Temp() => Path.Combine(Path.GetTempPath(), "srtest_" + Guid.NewGuid().ToString("N"), "settings.json");

    [Fact]
    public void RoundTrip_PreservesValues()
    {
        var svc = new SettingsService(Temp());
        svc.Save(new AppSettings { FramesPerSecond = 60, Quality = QualityPreset.High, Mode = CaptureMode.Region,
            LastRegion = new PixelRect(1, 2, 300, 200), MicrophoneDeviceId = "mic1" });
        var s = svc.Load();
        Assert.Equal(60, s.FramesPerSecond);
        Assert.Equal(QualityPreset.High, s.Quality);
        Assert.Equal(CaptureMode.Region, s.Mode);
        Assert.Equal(new PixelRect(1, 2, 300, 200), s.LastRegion);
        Assert.Equal("mic1", s.MicrophoneDeviceId);
    }

    [Fact]
    public void Load_MissingFile_ReturnsDefaults() =>
        Assert.Equal(30, new SettingsService(Temp()).Load().FramesPerSecond);

    [Fact]
    public void Load_CorruptFile_ReturnsDefaults()
    {
        string p = Temp();
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, "{ not json");
        Assert.Equal(30, new SettingsService(p).Load().FramesPerSecond);
    }

    [Fact]
    public void Load_InvalidValues_AreNormalised()
    {
        string p = Temp();
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, "{\"FramesPerSecond\":7,\"Quality\":\"High\",\"OutputFolder\":\"\"}");
        var s = new SettingsService(p).Load();
        Assert.Equal(30, s.FramesPerSecond);
        Assert.False(string.IsNullOrEmpty(s.OutputFolder));
    }

    [Fact]
    public void Bitrate_ScalesWithResolutionFpsAndPreset()
    {
        Assert.Equal(8_000_000, VideoQuality.Bitrate(QualityPreset.Standard, 1920, 1080, 30));
        Assert.Equal(16_000_000, VideoQuality.Bitrate(QualityPreset.Standard, 1920, 1080, 60));
        Assert.Equal(4_000_000, VideoQuality.Bitrate(QualityPreset.Low, 1920, 1080, 30));
        Assert.Equal(1_000_000, VideoQuality.Bitrate(QualityPreset.Low, 100, 100, 30));
    }
}
