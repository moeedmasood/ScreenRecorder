using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace ScreenRecorder.App;

internal static class NativeWindow
{
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr hWnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(IntPtr hWnd, int index, int value);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    private const uint WDA_NONE = 0, WDA_EXCLUDEFROMCAPTURE = 0x11;

    /// <summary>Hides a control surface from screen capture so it never appears in recordings.</summary>
    public static void ExcludeFromCapture(System.Windows.Window w) =>
        SetWindowDisplayAffinity(new WindowInteropHelper(w).EnsureHandle(), WDA_EXCLUDEFROMCAPTURE);

    public static void IncludeInCapture(System.Windows.Window w) =>
        SetWindowDisplayAffinity(new WindowInteropHelper(w).EnsureHandle(), WDA_NONE);

    /// <summary>Lets a normally non-activating overlay take keyboard focus (needed for text entry).</summary>
    public static void SetActivatable(System.Windows.Window w, bool on)
    {
        var h = new WindowInteropHelper(w).EnsureHandle();
        int ex = GetWindowLong(h, GWL_EXSTYLE);
        SetWindowLong(h, GWL_EXSTYLE, on ? ex & ~WS_EX_NOACTIVATE : ex | WS_EX_NOACTIVATE);
    }

    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    /// <summary>Moves and sizes a window to exact physical-pixel bounds (WPF's DIP properties are wrong across mixed-DPI displays).</summary>
    public static void PlaceOn(System.Windows.Window w, ScreenRecorder.Core.PixelRect r) =>
        SetWindowPos(new WindowInteropHelper(w).EnsureHandle(), new IntPtr(-1), r.X, r.Y, r.Width, r.Height, 0x10 /* NOACTIVATE */);

    /// <summary>Centres a window horizontally on a display, at a top or bottom offset (physical pixels).</summary>
    public static void CenterOn(System.Windows.Window w, ScreenRecorder.Core.PixelRect screen, int offset, bool fromBottom)
    {
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(w);
        int pw = (int)Math.Round(w.ActualWidth * dpi.DpiScaleX), ph = (int)Math.Round(w.ActualHeight * dpi.DpiScaleY);
        int x = screen.X + (screen.Width - pw) / 2;
        int y = fromBottom ? screen.Y + screen.Height - ph - offset : screen.Y + offset;
        SetWindowPos(new WindowInteropHelper(w).EnsureHandle(), new IntPtr(-1), x, y, 0, 0, 0x1 | 0x10 /* NOSIZE | NOACTIVATE */);
    }

    public static void SetClickThrough(System.Windows.Window w, bool on)
    {
        var h = new WindowInteropHelper(w).EnsureHandle();
        int ex = GetWindowLong(h, GWL_EXSTYLE) | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        ex = on ? ex | WS_EX_TRANSPARENT : ex & ~WS_EX_TRANSPARENT;
        SetWindowLong(h, GWL_EXSTYLE, ex);
    }
}
