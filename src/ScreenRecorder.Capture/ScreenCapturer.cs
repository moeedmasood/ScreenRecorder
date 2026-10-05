using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using ScreenRecorder.Core;
using WinRT;

namespace ScreenRecorder.Capture;

/// <summary>
/// Captures a monitor with Windows.Graphics.Capture and keeps the newest frame as a top-down
/// BGRA byte array (even width/height) that the recorder samples at a fixed frame rate.
/// </summary>
public sealed class ScreenCapturer : IScreenSource
{
    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow(IntPtr window, ref Guid iid);
        IntPtr CreateForMonitor(IntPtr monitor, ref Guid iid);
    }

    [ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface(ref Guid iid);
    }

    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(IntPtr classId, ref Guid iid, out IntPtr factory);
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string s, int length, out IntPtr hstring);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(IntPtr hstring);
    [DllImport("d3d11.dll")] private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    private static readonly Guid IidGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid IidTexture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private ID3D11Texture2D? _staging;
    private IDirect3DDevice? _winrtDevice;
    private int _cropX, _cropY, _contentW, _contentH;
    private uint _stagingW, _stagingH;
    private int _poolW, _poolH;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private GraphicsCaptureItem? _item;
    private readonly object _frameLock = new();
    private byte[]? _latest;
    private long _frameCounter;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public long FramesReceived => Interlocked.Read(ref _frameCounter);

    /// <summary>Raised when the captured window is closed.</summary>
    public event Action? SourceClosed;

    public static bool IsSupported => GraphicsCaptureSession.IsSupported();

    /// <summary>
    /// The hardware H.264 encoder crashes unless the width is a multiple of 16. Round up (padding with
    /// black) when there is room, otherwise down.
    /// </summary>
    internal static int AlignWidth(int width, int available)
    {
        int up = (width + 15) & ~15;
        return up <= available ? up : width & ~15;
    }

    public void Start(CaptureTarget target, bool captureCursor)
    {
        if (target.Mode == CaptureMode.Window)
        {
            _item = CreateItem(target.Handle, isWindow: true);
            _item.Closed += (_, _) => SourceClosed?.Invoke();
        }
        else _item = CreateItem(target.Handle != 0 ? (IntPtr)target.Handle : MonitorFromPoint(new POINT(), 1 /* MONITOR_DEFAULTTOPRIMARY */), isWindow: false);

        _contentW = _item.Size.Width;
        _contentH = _item.Size.Height;
        if (target.Mode == CaptureMode.Region)
        {
            var r = target.Region;
            _cropX = Math.Clamp(r.X, 0, Math.Max(_contentW - 2, 0));
            _cropY = Math.Clamp(r.Y, 0, Math.Max(_contentH - 2, 0));
            int w = Math.Min(r.Width, _contentW - _cropX), h = Math.Min(r.Height, _contentH - _cropY);
            Width = AlignWidth(w, _contentW - _cropX);
            Height = h & ~1;
        }
        else if (target.Mode == CaptureMode.Window) { Width = AlignWidth(_contentW, int.MaxValue); Height = _contentH & ~1; }
        else { Width = AlignWidth(_contentW, _contentW); Height = _contentH & ~1; }
        if (Width < 2 || Height < 2) throw new ArgumentException("Capture area is too small.");
        _latest = new byte[Width * Height * 4];
        _poolW = _contentW; _poolH = _contentH;

        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 }, out _device, out _, out _context).CheckError();
        using var dxgi = _device!.QueryInterface<IDXGIDevice>();
        Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out IntPtr winrtDevicePtr));
        _winrtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(winrtDevicePtr);
        Marshal.Release(winrtDevicePtr);

        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 3, _item.Size);
        _pool.FrameArrived += OnFrameArrived;
        _session = _pool.CreateCaptureSession(_item);
        _session.IsCursorCaptureEnabled = captureCursor;
        _session.StartCapture();
    }
    private static GraphicsCaptureItem CreateItem(long handleValue, bool isWindow) => CreateItem((IntPtr)handleValue, isWindow);

    private static GraphicsCaptureItem CreateItem(IntPtr handle, bool isWindow)
    {
        const string cls = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Marshal.ThrowExceptionForHR(WindowsCreateString(cls, cls.Length, out IntPtr hs));
        try
        {
            Guid interopIid = typeof(IGraphicsCaptureItemInterop).GUID;
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(hs, ref interopIid, out IntPtr factoryPtr));
            try
            {
                var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factoryPtr);
                Guid iid = IidGraphicsCaptureItem;
                IntPtr itemPtr = isWindow ? interop.CreateForWindow(handle, ref iid) : interop.CreateForMonitor(handle, ref iid);
                try { return GraphicsCaptureItem.FromAbi(itemPtr); }
                finally { Marshal.Release(itemPtr); }
            }
            finally { Marshal.Release(factoryPtr); }
        }
        finally { WindowsDeleteString(hs); }
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        var content = CopyFrame(sender);
        if (content is not { } c) return;

        // Larger windows need a bigger pool, otherwise frames are clipped by the system.
        // Done after the frame is released; recreating while a frame is alive is unsafe.
        if (_pool is not null && _winrtDevice is not null && (c.Width != _poolW || c.Height != _poolH))
        {
            _poolW = c.Width; _poolH = c.Height;
            _pool.Recreate(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 3, c);
        }
    }

    private Windows.Graphics.SizeInt32? CopyFrame(Direct3D11CaptureFramePool sender)
    {
        using var frame = sender.TryGetNextFrame();
        if (frame is null || _context is null || _device is null || _latest is null) return null;
        var content = frame.ContentSize;
        _contentW = content.Width;
        _contentH = content.Height;
        int texW = 0, texH = 0;

        IntPtr surfacePtr = MarshalInterface<IDirect3DSurface>.FromManaged(frame.Surface);
        try
        {
            var access = (IDirect3DDxgiInterfaceAccess)Marshal.GetObjectForIUnknown(surfacePtr);
            Guid iid = IidTexture2D;
            IntPtr texPtr = access.GetInterface(ref iid);
            using var tex = new ID3D11Texture2D(texPtr);
            var desc = tex.Description;
            texW = (int)desc.Width; texH = (int)desc.Height;
            if (_staging is null || _stagingW != desc.Width || _stagingH != desc.Height)
            {
                _staging?.Dispose();
                _staging = _device.CreateTexture2D(desc with
                {
                    Usage = ResourceUsage.Staging,
                    BindFlags = BindFlags.None,
                    CPUAccessFlags = CpuAccessFlags.Read,
                    MiscFlags = ResourceOptionFlags.None
                });
                _stagingW = desc.Width;
                _stagingH = desc.Height;
            }
            _context.CopyResource(_staging, tex);
        }
        finally { Marshal.Release(surfacePtr); }
        var map = _context.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            int rowBytes = Width * 4;
            int copyW = Math.Max(0, Math.Min(Width, Math.Min(_contentW, texW) - _cropX));
            int copyH = Math.Max(0, Math.Min(Height, Math.Min(_contentH, texH) - _cropY));
            lock (_frameLock)
            {
                // The window may be smaller than the output (after a resize): pad with black.
                if (copyW < Width || copyH < Height) Array.Clear(_latest);
                for (int y = 0; y < copyH; y++)
                    Marshal.Copy(IntPtr.Add(map.DataPointer, (_cropY + y) * (int)map.RowPitch + _cropX * 4),
                        _latest, y * rowBytes, copyW * 4);
            }
        }
        finally { _context.Unmap(_staging!, 0); }
        Interlocked.Increment(ref _frameCounter);
        return content;
    }

    /// <summary>Copies the newest frame into <paramref name="destination"/>; false if none arrived yet.</summary>
    public bool TryCopyLatest(byte[] destination)
    {
        if (_latest is null || FramesReceived == 0) return false;
        lock (_frameLock) System.Buffer.BlockCopy(_latest, 0, destination, 0, _latest.Length);
        return true;
    }

    public void Dispose()
    {
        _session?.Dispose();
        if (_pool is not null) { _pool.FrameArrived -= OnFrameArrived; _pool.Dispose(); }
        _staging?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
        _session = null; _pool = null;
    }
}







