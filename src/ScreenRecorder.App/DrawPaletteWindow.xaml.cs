using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ScreenRecorder.App;

/// <summary>Floating tool palette for the draw overlay. Excluded from capture.</summary>
public partial class DrawPaletteWindow : Window
{
    private readonly DrawOverlayWindow _overlay;
    public event Action? DoneRequested;

    public DrawPaletteWindow(DrawOverlayWindow overlay, ScreenRecorder.Core.PixelRect bounds)
    {
        InitializeComponent();
        _overlay = overlay;
        foreach (var c in new[] { Colors.Red, Colors.Yellow, Colors.LimeGreen, Colors.DeepSkyBlue, Colors.White, Colors.Black })
        {
            var color = c;
            var sw = new Ellipse { Width = 20, Height = 20, Fill = new SolidColorBrush(color), Stroke = Brushes.Gray, Margin = new Thickness(2), Cursor = Cursors.Hand };
            sw.MouseLeftButtonDown += (_, e) => { _overlay.SetColor(color); e.Handled = true; };
            Swatches.Children.Add(sw);
        }
        SourceInitialized += (_, _) =>
        {
            NativeWindow.ExcludeFromCapture(this);
        };
        Loaded += (_, _) => NativeWindow.CenterOn(this, bounds, 12, fromBottom: false);
    }

    private void OnDrag(object sender, MouseButtonEventArgs e) { if (e.OriginalSource is Border) DragMove(); }

    private void OnTool(object sender, RoutedEventArgs e)
    {
        if (_overlay is null) return;
        _overlay.SetTool(
            HlBtn.IsChecked == true ? DrawTool.Highlighter :
            EraseBtn.IsChecked == true ? DrawTool.Eraser :
            LineBtn.IsChecked == true ? DrawTool.Line :
            ArrowBtn.IsChecked == true ? DrawTool.Arrow :
            RectBtn.IsChecked == true ? DrawTool.Rectangle :
            OvalBtn.IsChecked == true ? DrawTool.Ellipse :
            TextBtn.IsChecked == true ? DrawTool.Text : DrawTool.Pen);
    }

    private void OnThickness(object sender, RoutedPropertyChangedEventArgs<double> e) => _overlay?.SetThickness(e.NewValue);
    private void OnUndo(object sender, RoutedEventArgs e) => _overlay.Undo();
    private void OnRedo(object sender, RoutedEventArgs e) => _overlay.Redo();
    private void OnClear(object sender, RoutedEventArgs e) => _overlay.Clear();
    private void OnDone(object sender, RoutedEventArgs e) => DoneRequested?.Invoke();
}
