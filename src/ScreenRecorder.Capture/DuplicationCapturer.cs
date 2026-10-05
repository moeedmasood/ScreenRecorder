using System.Runtime.InteropServices;
using ScreenRecorder.Core;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ScreenRecorder.Capture;

/// <summary>A source of top-down BGRA frames that the recorder samples at a fixed frame rate.</summary>
public interface IScreenSource : IDisposable
{
    int Width { get; }
    int Height { get; }
    long FramesReceived { get; }
    event Action? SourceClosed;
    void Start(CaptureTarget target, bool captureCursor);
    bool TryCopyLatest(byte[] destination);
}

/// <summary>
/// Compatibility capture using DXGI Desktop Duplication, for setups where Windows Graphics Capture
/// returns nothing (some exclusive-fullscreen games, older drivers). Monitor and Region only.
/// The pointer is not part of the duplicated image, so it is drawn on afterwards.
/// </summary>
public sealed class DuplicationCapturer : IScreenSource
{
    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CURSORINFO ci);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr icon, out ICONINFO info);
    [DllImport("user32.dll")] private static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr icon, int w, int h, uint step, IntPtr brush, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct CURSORINFO { public int Size, Flags; public IntPtr Cursor; public POINT Pos; }
    [StructLayout(LayoutKind.Sequential)] private struct ICONINFO { public bool Icon; public int XHot, YHot; public IntPtr Mask, Color; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO { public int Size, Width, Height; public short Planes, BitCount; public int Compression, SizeImage, XPpm, YPpm, ClrUsed, ClrImportant; }

    private const int CursorBox = 96;

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private ID3D11Texture2D? _staging;
    private IDXGIOutput1? _output;
    private IDXGIOutputDuplication? _dup;
    private Thread? _thread;
    private volatile bool _stop;
    private readonly object _lock = new();
    private byte[]? _latest;
    private long _frames;
    private int _cropX, _cropY, _deskX, _deskY, _fullW, _fullH;
    private bool _cursor;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public long FramesReceived => Interlocked.Read(ref _frames);
    public event Action? SourceClosed { add { } remove { } }

    public void Start(CaptureTarget target, bool captureCursor)
    {
        if (target.Mode == CaptureMode.Window) throw new NotSupportedException("Compatibility capture cannot record a single window.");
        _cursor = captureCursor;
        long hmon = target.Handle != 0 ? target.Handle : MonitorEnumerator.List().First(m => m.IsPrimary).Handle;

        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        IDXGIAdapter1? adapter = null; IDXGIOutput? output = null;
        for (uint a = 0; factory.EnumAdapters1(a, out var ad).Success; a++)
        {
            for (uint o = 0; ad.EnumOutputs(o, out var op).Success; o++)
            {
                if ((long)op.Description.Monitor == hmon) { adapter = ad; output = op; break; }
                op.Dispose();
            }
            if (output is not null) break;
            ad.Dispose();
        }
        if (adapter is null || output is null) throw new InvalidOperationException("Display not found for compatibility capture.");
        using (adapter)
        {
            var d = output.Description.DesktopCoordinates;
            _deskX = d.Left; _deskY = d.Top; _fullW = d.Right - d.Left; _fullH = d.Bottom - d.Top;
            D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
                new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 }, out _device, out _, out _context).CheckError();
            _output = output.QueryInterface<IDXGIOutput1>();
            output.Dispose();
        }
        _dup = _output.DuplicateOutput(_device!);

        if (target.Mode == CaptureMode.Region)
        {
            var r = target.Region;
            _cropX = Math.Clamp(r.X, 0, Math.Max(_fullW - 2, 0)); _cropY = Math.Clamp(r.Y, 0, Math.Max(_fullH - 2, 0));
            int w = Math.Min(r.Width, _fullW - _cropX), h = Math.Min(r.Height, _fullH - _cropY);
            Width = ScreenCapturer.AlignWidth(w, _fullW - _cropX); Height = h & ~1;
        }
        else { Width = ScreenCapturer.AlignWidth(_fullW, _fullW); Height = _fullH & ~1; }
        if (Width < 2 || Height < 2) throw new ArgumentException("Capture area is too small.");
        _latest = new byte[Width * Height * 4];
        _thread = new Thread(Loop) { IsBackground = true, Name = "DesktopDuplication" };
        _thread.Start();
    }

    private void Loop()
    {
        while (!_stop)
        {
            try
            {
                var hr = _dup!.AcquireNextFrame(100, out _, out var resource);
                if (hr == Vortice.DXGI.ResultCode.WaitTimeout) continue;
                if (hr == Vortice.DXGI.ResultCode.AccessLost) { Reopen(); continue; }
                hr.CheckError();
                try { CopyFrame(resource); }
                finally { resource.Dispose(); _dup.ReleaseFrame(); }
            }
            catch (Exception) when (_stop) { }
            catch (Exception) { Thread.Sleep(200); try { Reopen(); } catch { Thread.Sleep(500); } }
        }
    }

    // Desktop Duplication is lost on mode changes, UAC prompts and lock screens.
    private void Reopen()
    {
        _dup?.Dispose(); _dup = null;
        for (int i = 0; i < 20 && !_stop; i++)
        {
            try { _dup = _output!.DuplicateOutput(_device!); return; }
            catch { Thread.Sleep(250); }
        }
    }

    private void CopyFrame(IDXGIResource resource)
    {
        using var tex = resource.QueryInterface<ID3D11Texture2D>();
        var desc = tex.Description;
        if (_staging is null || _staging.Description.Width != desc.Width || _staging.Description.Height != desc.Height)
        {
            _staging?.Dispose();
            _staging = _device!.CreateTexture2D(desc with
            {
                Usage = ResourceUsage.Staging, BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read, MiscFlags = ResourceOptionFlags.None
            });
        }
        _context!.CopyResource(_staging, tex);
        var map = _context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            int copyW = Math.Max(0, Math.Min(Width, (int)desc.Width - _cropX));
            int copyH = Math.Max(0, Math.Min(Height, (int)desc.Height - _cropY));
            lock (_lock)
            {
                if (copyW < Width || copyH < Height) Array.Clear(_latest!);
                for (int y = 0; y < copyH; y++)
                    Marshal.Copy(IntPtr.Add(map.DataPointer, (_cropY + y) * (int)map.RowPitch + _cropX * 4), _latest!, y * Width * 4, copyW * 4);
                if (_cursor) DrawCursor();
            }
        }
        finally { _context.Unmap(_staging, 0); }
        Interlocked.Increment(ref _frames);
    }

    /// <summary>Draws the pointer into the frame by letting GDI composite it over the pixels beneath.</summary>
    private void DrawCursor()
    {
        var ci = new CURSORINFO { Size = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref ci) || (ci.Flags & 1) == 0 || ci.Cursor == IntPtr.Zero) return;
        if (!GetIconInfo(ci.Cursor, out var ii)) return;
        if (ii.Mask != IntPtr.Zero) DeleteObject(ii.Mask);
        if (ii.Color != IntPtr.Zero) DeleteObject(ii.Color);
        int x = ci.Pos.X - ii.XHot - _deskX - _cropX, y = ci.Pos.Y - ii.YHot - _deskY - _cropY;
        int x0 = Math.Max(x, 0), y0 = Math.Max(y, 0);
        int x1 = Math.Min(x + CursorBox, Width), y1 = Math.Min(y + CursorBox, Height);
        if (x1 <= x0 || y1 <= y0) return;

        var bmi = new BITMAPINFO { Size = Marshal.SizeOf<BITMAPINFO>(), Width = CursorBox, Height = -CursorBox, Planes = 1, BitCount = 32 };
        IntPtr screen = GetDC(IntPtr.Zero), dc = CreateCompatibleDC(screen);
        IntPtr dib = CreateDIBSection(dc, ref bmi, 0, out IntPtr bits, IntPtr.Zero, 0);
        if (dib == IntPtr.Zero) { DeleteDC(dc); ReleaseDC(IntPtr.Zero, screen); return; }
        IntPtr old = SelectObject(dc, dib);
        try
        {
            int rowBytes = (x1 - x0) * 4;
            for (int yy = y0; yy < y1; yy++)
                Marshal.Copy(_latest!, (yy * Width + x0) * 4, IntPtr.Add(bits, ((yy - y) * CursorBox + (x0 - x)) * 4), rowBytes);
            DrawIconEx(dc, 0, 0, ci.Cursor, 0, 0, 0, IntPtr.Zero, 3 /* DI_NORMAL */);
            for (int yy = y0; yy < y1; yy++)
            {
                Marshal.Copy(IntPtr.Add(bits, ((yy - y) * CursorBox + (x0 - x)) * 4), _latest!, (yy * Width + x0) * 4, rowBytes);
                for (int xx = x0; xx < x1; xx++) _latest![(yy * Width + xx) * 4 + 3] = 255;
            }
        }
        finally { SelectObject(dc, old); DeleteObject(dib); DeleteDC(dc); ReleaseDC(IntPtr.Zero, screen); }
    }

    public bool TryCopyLatest(byte[] destination)
    {
        if (_latest is null || FramesReceived == 0) return false;
        lock (_lock) System.Buffer.BlockCopy(_latest, 0, destination, 0, _latest.Length);
        return true;
    }

    public void Dispose()
    {
        _stop = true;
        _thread?.Join(1000);
        _dup?.Dispose(); _output?.Dispose(); _staging?.Dispose(); _context?.Dispose(); _device?.Dispose();
        _dup = null;
    }
}
