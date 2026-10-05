using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using ScreenRecorder.Core;

namespace ScreenRecorder.App;

/// <summary>
/// Draggable frame shown over the webcam while recording so it can be moved and resized live.
/// Excluded from capture. Works in physical screen pixels; <c>origin</c> is where output pixel (0,0) sits on screen.
/// </summary>
public partial class WebcamAdjustWindow : Window
{
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    private readonly (int X, int Y) _origin;
    private readonly int _outW, _outH;
    private readonly bool _square;
    private readonly Action<PixelRect> _changed;
    private PixelRect _rect;
    private PixelRect _startRect;
    private POINT _startCursor;
    private bool _resizing, _dragging;

    public WebcamAdjustWindow((int X, int Y) origin, int outW, int outH, PixelRect rect, bool square, Action<PixelRect> changed)
    {
        InitializeComponent();
        _origin = origin; _outW = outW; _outH = outH; _square = square; _changed = changed; _rect = rect;
        SourceInitialized += (_, _) => { NativeWindow.ExcludeFromCapture(this); Apply(); };
        Loaded += (_, _) => Apply();
    }

    private void Apply() =>
        NativeWindow.PlaceOn(this, new PixelRect(_origin.X + _rect.X, _origin.Y + _rect.Y, _rect.Width, _rect.Height));

    private void OnDone(object sender, RoutedEventArgs e) => Close();

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        GetCursorPos(out _startCursor);
        _startRect = _rect;
        _resizing = e.OriginalSource is FrameworkElement fe && fe == Grip;
        _dragging = !_resizing && e.OriginalSource is not System.Windows.Controls.Button;
        if (_resizing || _dragging) Root.CaptureMouse();
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (!_dragging && !_resizing) return;
        GetCursorPos(out var c);
        int dx = c.X - _startCursor.X, dy = c.Y - _startCursor.Y;
        if (_resizing)
        {
            int w = Math.Clamp(_startRect.Width + dx, 64, _outW - _startRect.X);
            int h = Math.Clamp(_startRect.Height + dy, 64, _outH - _startRect.Y);
            if (_square) w = h = Math.Min(w, h);
            _rect = new PixelRect(_startRect.X, _startRect.Y, w & ~1, h & ~1);
        }
        else
        {
            int x = Math.Clamp(_startRect.X + dx, 0, _outW - _startRect.Width);
            int y = Math.Clamp(_startRect.Y + dy, 0, _outH - _startRect.Height);
            _rect = _startRect with { X = x, Y = y };
        }
        Apply();
        _changed(_rect);
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        _dragging = _resizing = false;
        Root.ReleaseMouseCapture();
    }
}
