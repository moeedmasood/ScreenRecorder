namespace ScreenRecorder.Core;

public enum WebcamSize { Thumbnail, Quarter, Half }

public enum WebcamShape { Rectangle, Rounded, Circle }

public enum WebcamCorner { TopLeft, TopRight, BottomLeft, BottomRight }

public static class WebcamLayout
{
    public const int Margin = 16;

    /// <summary>
    /// Rectangle (in output pixels) the webcam occupies. Thumbnail = 20% width, Quarter = 50% x 50%,
    /// Half = 50% width x full height (left or right side by corner). Dimensions are even.
    /// </summary>
    public static PixelRect Compute(int outW, int outH, WebcamSize size, WebcamCorner corner)
    {
        int w, h;
        switch (size)
        {
            case WebcamSize.Half: w = outW / 2; h = outH; break;
            case WebcamSize.Quarter: w = outW / 2; h = outH / 2; break;
            default: w = (int)(outW * 0.20); h = (int)(outH * 0.20); break;
        }
        w &= ~1; h &= ~1;
        bool right = corner is WebcamCorner.TopRight or WebcamCorner.BottomRight;
        bool bottom = corner is WebcamCorner.BottomLeft or WebcamCorner.BottomRight;
        int m = size == WebcamSize.Half ? 0 : Margin;
        int x = right ? outW - w - m : m;
        int y = size == WebcamSize.Half ? 0 : bottom ? outH - h - m : m;
        return new PixelRect(Math.Max(0, x), Math.Max(0, y), w, h);
    }
}
