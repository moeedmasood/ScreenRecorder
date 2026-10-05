using Vortice.MediaFoundation;

namespace ScreenRecorder.Capture;

public sealed record WebcamInfo(string Id, string Name);

/// <summary>Reads BGRA frames from a camera through the Media Foundation source reader.</summary>
public sealed class WebcamCapturer : IDisposable
{
    private IMFMediaSource? _source;
    private IMFSourceReader? _reader;
    private Thread? _thread;
    private volatile bool _stop;
    private readonly object _gate = new();
    private byte[] _latest = Array.Empty<byte>();
    private bool _hasFrame;
    private bool _flip;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public bool HasFrame { get { lock (_gate) return _hasFrame; } }

    public static List<WebcamInfo> List()
    {
        var result = new List<WebcamInfo>();
        MediaFactory.MFStartup().CheckError();
        using var attrs = MediaFactory.MFCreateAttributes(1);
        attrs.Set(CaptureDeviceAttributeKeys.SourceType, CaptureDeviceAttributeKeys.SourceTypeVidcap);
        var devices = MediaFactory.MFEnumDeviceSources(attrs);
        foreach (var d in devices)
        {
            try
            {
                string name = d.GetString(CaptureDeviceAttributeKeys.FriendlyName) ?? "Camera";
                string id = d.GetString(CaptureDeviceAttributeKeys.SourceTypeVidcapSymbolicLink) ?? name;
                result.Add(new WebcamInfo(id, name));
            }
            finally { d.Dispose(); }
        }
        return result;
    }

    public void Start(string? deviceId)
    {
        MediaFactory.MFStartup().CheckError();
        using var attrs = MediaFactory.MFCreateAttributes(2);
        attrs.Set(CaptureDeviceAttributeKeys.SourceType, CaptureDeviceAttributeKeys.SourceTypeVidcap);
        if (!string.IsNullOrEmpty(deviceId))
            attrs.Set(CaptureDeviceAttributeKeys.SourceTypeVidcapSymbolicLink, deviceId);
        else
        {
            var all = List();
            if (all.Count == 0) throw new InvalidOperationException("No camera found.");
            attrs.Set(CaptureDeviceAttributeKeys.SourceTypeVidcapSymbolicLink, all[0].Id);
        }
        _source = MediaFactory.MFCreateDeviceSource(attrs);

        using var readerAttrs = MediaFactory.MFCreateAttributes(2);
        readerAttrs.Set(SourceReaderAttributeKeys.EnableVideoProcessing, 1u);
        _reader = MediaFactory.MFCreateSourceReaderFromMediaSource(_source, readerAttrs);

        using (var type = MediaFactory.MFCreateMediaType())
        {
            type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            type.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32);
            _reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, type);
        }
        using (var cur = _reader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream))
        {
            ulong size = cur.GetUInt64(MediaTypeAttributeKeys.FrameSize);
            Width = (int)(size >> 32);
            Height = (int)(size & 0xFFFFFFFF);
            // A negative default stride means the rows arrive bottom-up.
            try { _flip = (int)cur.GetUInt32(MediaTypeAttributeKeys.DefaultStride) < 0; }
            catch { _flip = true; }
        }
        _latest = new byte[Width * Height * 4];
        _stop = false;
        _thread = new Thread(Loop) { IsBackground = true, Name = "WebcamLoop" };
        _thread.Start();
    }

    private void Loop()
    {
        int stride = Width * 4;
        while (!_stop)
        {
            try
            {
                using var sample = _reader!.ReadSample(SourceReaderIndex.FirstVideoStream, 0, out _, out var flags, out _);
                if (sample is null) { if ((flags & SourceReaderFlag.EndOfStream) != 0) break; continue; }
                using var buffer = sample.ConvertToContiguousBuffer();
                buffer.Lock(out IntPtr ptr, out _, out int len);
                try
                {
                    lock (_gate)
                    {
                        int rows = Math.Min(Height, len / stride);
                        for (int y = 0; y < rows; y++)
                        {
                            int dstRow = _flip ? Height - 1 - y : y;
                            System.Runtime.InteropServices.Marshal.Copy(ptr + y * stride, _latest, dstRow * stride, stride);
                        }
                        _hasFrame = true;
                    }
                }
                finally { buffer.Unlock(); }
            }
            catch when (_stop) { break; }
            catch { Thread.Sleep(50); }
        }
    }

    /// <summary>Copies the latest frame (Width*Height*4 BGRA). Returns false until the first frame arrives.</summary>
    public bool TryCopyLatest(byte[] dest)
    {
        lock (_gate)
        {
            if (!_hasFrame || dest.Length < _latest.Length) return false;
            Buffer.BlockCopy(_latest, 0, dest, 0, _latest.Length);
            return true;
        }
    }

    public void Dispose()
    {
        _stop = true;
        _thread?.Join(1500);
        try { _source?.Shutdown(); } catch { }
        _reader?.Dispose();
        _source?.Dispose();
        _reader = null; _source = null;
    }
}

/// <summary>Blends a camera frame into a screen frame (cover-crop scaling, optional mirror).</summary>
public static class WebcamCompositor
{
    /// <summary>True when pixel (x,y) of a w*h box lies inside the shape (circle = inscribed ellipse; rounded = 12% corner radius).</summary>
    public static bool Inside(ScreenRecorder.Core.WebcamShape shape, int x, int y, int w, int h)
    {
        double px = x + 0.5, py = y + 0.5;
        if (shape == ScreenRecorder.Core.WebcamShape.Circle)
        {
            double dx = (px - w / 2.0) / (w / 2.0), dy = (py - h / 2.0) / (h / 2.0);
            return dx * dx + dy * dy <= 1.0;
        }
        double r = Math.Min(w, h) * 0.12;
        double cx = px < r ? r : px > w - r ? w - r : px, cy = py < r ? r : py > h - r ? h - r : py;
        double ex = px - cx, ey = py - cy;
        return ex * ex + ey * ey <= r * r;
    }

    public static void Blend(byte[] dst, int dstW, int dstH, byte[] cam, int camW, int camH,
        ScreenRecorder.Core.PixelRect rect, bool mirror, ScreenRecorder.Core.WebcamShape shape = ScreenRecorder.Core.WebcamShape.Rectangle)
    {
        int rw = Math.Min(rect.Width, dstW - rect.X), rh = Math.Min(rect.Height, dstH - rect.Y);
        if (rw <= 0 || rh <= 0 || camW <= 0 || camH <= 0) return;

        // Cover: crop the camera frame to the target aspect ratio.
        double targetAspect = (double)rect.Width / rect.Height, camAspect = (double)camW / camH;
        int cropW = camW, cropH = camH;
        if (camAspect > targetAspect) cropW = (int)(camH * targetAspect); else cropH = (int)(camW / targetAspect);
        int cropX = (camW - cropW) / 2, cropY = (camH - cropH) / 2;

        var xMap = new int[rw];
        for (int x = 0; x < rw; x++)
        {
            int sx = cropX + (int)((long)x * cropW / rect.Width);
            xMap[x] = (mirror ? camW - 1 - sx : sx) * 4;
        }
        for (int y = 0; y < rh; y++)
        {
            int sy = cropY + (int)((long)y * cropH / rect.Height);
            int srcRow = sy * camW * 4;
            int dstRow = ((rect.Y + y) * dstW + rect.X) * 4;
            for (int x = 0; x < rw; x++)
            {
                if (shape != ScreenRecorder.Core.WebcamShape.Rectangle && !Inside(shape, x, y, rect.Width, rect.Height)) continue;
                int s = srcRow + xMap[x], d = dstRow + x * 4;
                dst[d] = cam[s]; dst[d + 1] = cam[s + 1]; dst[d + 2] = cam[s + 2]; dst[d + 3] = 255;
            }
        }
    }
}
