using ScreenRecorder.Core;
using Xunit;

namespace ScreenRecorder.Core.Tests;

public class CoreTests
{
    [Fact]
    public void Clock_FreezesWhilePaused()
    {
        var c = new MasterClock();
        c.Start();
        Thread.Sleep(50);
        c.Pause();
        long a = c.NowHns;
        Thread.Sleep(100);
        long b = c.NowHns;
        Assert.Equal(a, b);
        c.Resume();
        Thread.Sleep(20);
        Assert.True(c.NowHns > b);
        Assert.True(c.NowHns < b + 1_000_000, "paused time must be excluded");
    }

    [Fact]
    public void Mix_AppliesGainAndClips()
    {
        var d = new float[] { 0.5f, -0.5f };
        AudioMath.MixInto(d, new float[] { 1f, -1f }, 2, 2f);
        AudioMath.Clip(d, 2);
        Assert.Equal(1f, d[0]);
        Assert.Equal(-1f, d[1]);
    }

    [Fact]
    public void ToPcm16_ConvertsExtremes()
    {
        var b = new byte[4];
        AudioMath.ToPcm16(new float[] { 1f, -1f }, 2, b);
        Assert.Equal(short.MaxValue, BitConverter.ToInt16(b, 0));
        Assert.Equal(-short.MaxValue, BitConverter.ToInt16(b, 2));
    }

    [Fact]
    public void FramesDue_OneSecondIsSampleRate() =>
        Assert.Equal(48000, AudioMath.FramesDue(10_000_000, 48000));

    [Fact]
    public void OutputPath_SubstitutesAndSanitises()
    {
        var o = new RecordingOptions { OutputFolder = @"C:\Out", FileNameTemplate = "a:{date}_{time}" };
        string p = OutputPaths.BuildFinalPath(o, new DateTime(2026, 1, 2, 3, 4, 5));
        Assert.Equal(@"C:\Out\a_2026-01-02_03-04-05.mp4", p);
    }
}
