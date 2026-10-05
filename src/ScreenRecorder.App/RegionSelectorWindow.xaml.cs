using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ScreenRecorder.Core;

namespace ScreenRecorder.App;

/// <summary>Full-screen overlay on the chosen monitor; the result is in physical pixels relative to that monitor.</summary>
public partial class RegionSelectorWindow : Window
{
    private Point? _start;
    private Rect _selection;

    public PixelRect? Result { get; private set; }

    public RegionSelectorWindow(PixelRect monitor)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => NativeWindow.PlaceOn(this, monitor);
        Loaded += (_, _) => { NativeWindow.PlaceOn(this, monitor); Activate(); };
        Loaded += (_, _) => UpdateDim();
        SizeChanged += (_, _) => UpdateDim();
    }

    private void UpdateDim()
    {
        var full = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight));
        Dim.Data = _selection.IsEmpty || _selection.Width < 1
            ? full
            : new CombinedGeometry(GeometryCombineMode.Exclude, full, new RectangleGeometry(_selection));
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        _start = e.GetPosition(this);
        CaptureMouse();
        Hint.Visibility = Visibility.Collapsed;
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (_start is not { } s) return;
        var p = e.GetPosition(this);
        _selection = new Rect(s, p);
        Canvas.SetLeft(Frame, _selection.X); Canvas.SetTop(Frame, _selection.Y);
        Frame.Width = _selection.Width; Frame.Height = _selection.Height;
        Frame.Visibility = Visibility.Visible;
        var dpi = VisualTreeHelper.GetDpi(this);
        SizeText.Text = $"{(int)(_selection.Width * dpi.DpiScaleX)} x {(int)(_selection.Height * dpi.DpiScaleY)}";
        Canvas.SetLeft(SizeLabel, _selection.X); Canvas.SetTop(SizeLabel, Math.Max(0, _selection.Y - 26));
        SizeLabel.Visibility = Visibility.Visible;
        UpdateDim();
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        ReleaseMouseCapture();
        if (_start is null) return;
        _start = null;
        var dpi = VisualTreeHelper.GetDpi(this);
        var r = new PixelRect((int)(_selection.X * dpi.DpiScaleX), (int)(_selection.Y * dpi.DpiScaleY),
            (int)(_selection.Width * dpi.DpiScaleX), (int)(_selection.Height * dpi.DpiScaleY));
        if (r.Width >= 32 && r.Height >= 32) { Result = r; DialogResult = true; }
        else
        {
            _selection = Rect.Empty; Frame.Visibility = SizeLabel.Visibility = Visibility.Collapsed;
            Hint.Visibility = Visibility.Visible; UpdateDim();
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Result = null; DialogResult = false; }
    }
}

