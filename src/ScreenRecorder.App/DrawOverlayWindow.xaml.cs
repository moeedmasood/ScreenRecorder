using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;

namespace ScreenRecorder.App;

public enum DrawTool { Pen, Highlighter, Eraser, Line, Arrow, Rectangle, Ellipse, Text }

/// <summary>
/// Transparent full-screen ink surface on the chosen monitor. In draw mode it takes input; otherwise
/// clicks pass through to the apps below while existing annotations stay visible (and are recorded).
/// Pen, highlighter, shapes and text all go through one undo/redo history.
/// </summary>
public partial class DrawOverlayWindow : Window
{
    private DrawTool _tool = DrawTool.Pen;
    private Color _color = Colors.Red;
    private double _thickness = 4;

    private readonly List<object> _undo = new(), _redo = new();
    private Point _dragStart;
    private Stroke? _preview;
    private bool _dragging;
    private TextBox? _editor;

    public bool IsDrawing { get; private set; }

    public DrawOverlayWindow(ScreenRecorder.Core.PixelRect bounds)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => { NativeWindow.SetClickThrough(this, true); NativeWindow.PlaceOn(this, bounds); };
        Loaded += (_, _) => NativeWindow.PlaceOn(this, bounds);
        Ink.StrokeCollected += (_, e) => Record(e.Stroke);
        Ink.MouseLeftButtonDown += OnDown;
        Ink.MouseMove += OnMove;
        Ink.MouseLeftButtonUp += OnUp;
        ApplyTool();
    }

    public void SetDrawing(bool on)
    {
        if (!on) CommitText();
        IsDrawing = on;
        NativeWindow.SetClickThrough(this, !on);
        Ink.IsHitTestVisible = on;
    }

    public void SetTool(DrawTool tool) { CommitText(); _tool = tool; ApplyTool(); }
    public void SetColor(Color c) { _color = c; ApplyTool(); }
    public void SetThickness(double t) { _thickness = t; ApplyTool(); }

    public void Clear()
    {
        CancelText();
        Ink.Strokes.Clear();
        Ink.Children.Clear();
        _undo.Clear(); _redo.Clear();
    }

    public void Undo()
    {
        CommitText();
        while (_undo.Count > 0)
        {
            var item = _undo[^1]; _undo.RemoveAt(_undo.Count - 1);
            if (!Remove(item)) continue; // already erased; try the previous item
            _redo.Add(item);
            return;
        }
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;
        var item = _redo[^1]; _redo.RemoveAt(_redo.Count - 1);
        if (item is Stroke s) Ink.Strokes.Add(s); else Ink.Children.Add((UIElement)item);
        _undo.Add(item);
    }

    private bool Remove(object item)
    {
        if (item is Stroke s) return Ink.Strokes.Contains(s) && Ink.Strokes.Remove(s);
        var el = (UIElement)item;
        if (!Ink.Children.Contains(el)) return false;
        Ink.Children.Remove(el);
        return true;
    }

    private void Record(object item) { _undo.Add(item); _redo.Clear(); }

    private bool IsShape => _tool is DrawTool.Line or DrawTool.Arrow or DrawTool.Rectangle or DrawTool.Ellipse;

    private DrawingAttributes Attributes(bool highlighter) => new()
    {
        Color = _color,
        IsHighlighter = highlighter,
        FitToCurve = highlighter || _tool == DrawTool.Pen,
        IgnorePressure = true,
        Width = highlighter ? _thickness * 4 : _thickness,
        Height = highlighter ? _thickness * 4 : _thickness
    };

    private void ApplyTool()
    {
        switch (_tool)
        {
            case DrawTool.Eraser: Ink.EditingMode = InkCanvasEditingMode.EraseByStroke; break;
            case DrawTool.Pen:
            case DrawTool.Highlighter:
                Ink.EditingMode = InkCanvasEditingMode.Ink;
                Ink.DefaultDrawingAttributes = Attributes(_tool == DrawTool.Highlighter);
                break;
            default:
                Ink.EditingMode = InkCanvasEditingMode.None;
                Ink.Cursor = _tool == DrawTool.Text ? Cursors.IBeam : Cursors.Cross;
                break;
        }
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (_tool == DrawTool.Text) { BeginText(e.GetPosition(Ink)); e.Handled = true; return; }
        if (!IsShape) return;
        _dragStart = e.GetPosition(Ink);
        _dragging = true;
        Ink.CaptureMouse();
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        SetPreview(BuildShape(_dragStart, e.GetPosition(Ink)));
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        Ink.ReleaseMouseCapture();
        var end = e.GetPosition(Ink);
        SetPreview(null);
        if ((end - _dragStart).Length < 3) return;
        var stroke = BuildShape(_dragStart, end);
        Ink.Strokes.Add(stroke);
        Record(stroke);
    }

    private void SetPreview(Stroke? s)
    {
        if (_preview is not null) Ink.Strokes.Remove(_preview);
        _preview = s;
        if (s is not null) Ink.Strokes.Add(s);
    }

    private Stroke BuildShape(Point a, Point b)
    {
        var pts = new List<Point>();
        switch (_tool)
        {
            case DrawTool.Line: pts.Add(a); pts.Add(b); break;
            case DrawTool.Arrow: pts.AddRange(ArrowPoints(a, b, Math.Max(14, _thickness * 4))); break;
            case DrawTool.Rectangle:
                pts.AddRange(new[] { a, new Point(b.X, a.Y), b, new Point(a.X, b.Y), a });
                break;
            default:
                double cx = (a.X + b.X) / 2, cy = (a.Y + b.Y) / 2, rx = Math.Abs(b.X - a.X) / 2, ry = Math.Abs(b.Y - a.Y) / 2;
                for (int i = 0; i <= 72; i++)
                {
                    double t = i * Math.PI * 2 / 72;
                    pts.Add(new Point(cx + rx * Math.Cos(t), cy + ry * Math.Sin(t)));
                }
                break;
        }
        var spc = new System.Windows.Input.StylusPointCollection(pts.Select(p => new StylusPoint(p.X, p.Y)));
        return new Stroke(spc, Attributes(false));
    }

    /// <summary>Shaft a→b, then an open arrowhead drawn as b→wing1→b→wing2.</summary>
    private static IEnumerable<Point> ArrowPoints(Point a, Point b, double head)
    {
        var dir = b - a;
        double len = dir.Length;
        if (len < 1) return new[] { a, b };
        dir /= len;
        double ang = Math.PI / 7;
        Point Wing(double s)
        {
            double c = Math.Cos(s * ang), sn = Math.Sin(s * ang);
            var d = new Vector(dir.X * c - dir.Y * sn, dir.X * sn + dir.Y * c);
            return new Point(b.X - d.X * head, b.Y - d.Y * head);
        }
        return new[] { a, b, Wing(1), b, Wing(-1) };
    }

    // --- text ---

    private void BeginText(Point at)
    {
        CommitText();
        var tb = new TextBox
        {
            MinWidth = 120,
            FontSize = 14 + _thickness * 2,
            Foreground = new SolidColorBrush(_color),
            CaretBrush = new SolidColorBrush(_color),
            Background = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)),
            BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1),
            FontWeight = FontWeights.SemiBold,
            AcceptsReturn = true
        };
        InkCanvas.SetLeft(tb, at.X); InkCanvas.SetTop(tb, at.Y);
        tb.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { CancelText(); e.Handled = true; }
            else if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0) { CommitText(); e.Handled = true; }
        };
        Ink.Children.Add(tb);
        _editor = tb;
        NativeWindow.SetActivatable(this, true);
        Activate();
        tb.Focus();
        Keyboard.Focus(tb);
    }

    private void CommitText()
    {
        var tb = _editor;
        if (tb is null) return;
        _editor = null;
        Ink.Children.Remove(tb);
        NativeWindow.SetActivatable(this, false);
        if (string.IsNullOrWhiteSpace(tb.Text)) return;
        var label = new TextBlock
        {
            Text = tb.Text, FontSize = tb.FontSize, FontWeight = tb.FontWeight, Foreground = tb.Foreground,
            IsHitTestVisible = false
        };
        InkCanvas.SetLeft(label, InkCanvas.GetLeft(tb) + 2); InkCanvas.SetTop(label, InkCanvas.GetTop(tb) + 1);
        Ink.Children.Add(label);
        Record(label);
    }

    private void CancelText()
    {
        if (_editor is null) return;
        Ink.Children.Remove(_editor);
        _editor = null;
        NativeWindow.SetActivatable(this, false);
    }
}