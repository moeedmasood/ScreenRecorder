using ScreenRecorder.Core;
using Xunit;

namespace ScreenRecorder.Core.Tests;

public class WebcamLayoutTests
{
    [Theory]
    [InlineData(WebcamSize.Thumbnail, 0.20, 0.20)]
    [InlineData(WebcamSize.Quarter, 0.50, 0.50)]
    [InlineData(WebcamSize.Half, 0.50, 1.00)]
    public void SizesAreWithinOnePercentOfTarget(WebcamSize size, double fw, double fh)
    {
        var r = WebcamLayout.Compute(1920, 1080, size, WebcamCorner.BottomRight);
        Assert.InRange(r.Width / 1920.0, fw - 0.01, fw + 0.01);
        Assert.InRange(r.Height / 1080.0, fh - 0.01, fh + 0.01);
    }

    [Theory]
    [InlineData(WebcamCorner.TopLeft)]
    [InlineData(WebcamCorner.TopRight)]
    [InlineData(WebcamCorner.BottomLeft)]
    [InlineData(WebcamCorner.BottomRight)]
    public void RectStaysInsideFrame(WebcamCorner corner)
    {
        foreach (var size in Enum.GetValues<WebcamSize>())
        {
            var r = WebcamLayout.Compute(1360, 768, size, corner);
            Assert.True(r.X >= 0 && r.Y >= 0 && r.X + r.Width <= 1360 && r.Y + r.Height <= 768);
            Assert.True(r.Width % 2 == 0 && r.Height % 2 == 0);
        }
    }

    [Fact]
    public void BottomRightThumbnailSitsInCornerWithMargin()
    {
        var r = WebcamLayout.Compute(1920, 1080, WebcamSize.Thumbnail, WebcamCorner.BottomRight);
        Assert.Equal(1920 - r.Width - WebcamLayout.Margin, r.X);
        Assert.Equal(1080 - r.Height - WebcamLayout.Margin, r.Y);
    }
}
