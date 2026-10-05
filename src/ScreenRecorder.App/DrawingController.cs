namespace ScreenRecorder.App;

/// <summary>Owns the draw overlay and palette; Toggle() switches between draw mode and pass-through.</summary>
public sealed class DrawingController
{
    private DrawOverlayWindow? _overlay;
    private DrawPaletteWindow? _palette;
    private ScreenRecorder.Core.PixelRect _bounds;

    /// <summary>Supplies the display to draw on; read each time draw mode starts.</summary>
    public Func<ScreenRecorder.Core.PixelRect> BoundsProvider { get; set; } = () => new(0, 0, 1920, 1080);

    public bool IsActive { get; private set; }
    public event Action<bool>? ActiveChanged;

    public void Toggle() => SetActive(!IsActive);

    public void SetActive(bool on)
    {
        if (on == IsActive) return;
        if (on)
        {
            var bounds = BoundsProvider();
            if (_overlay is not null && bounds != _bounds) Shutdown(); // display changed: annotations on the old one are dropped
            _bounds = bounds;
            if (_overlay is null)
            {
                _overlay = new DrawOverlayWindow(bounds);
                _palette = new DrawPaletteWindow(_overlay, bounds);
                _palette.DoneRequested += () => SetActive(false);
                _overlay.Show();
            }
            _overlay.SetDrawing(true);
            _palette!.Show();
        }
        else
        {
            _overlay?.SetDrawing(false);
            _palette?.Hide();
        }
        IsActive = on;
        ActiveChanged?.Invoke(on);
    }

    public void Clear() => _overlay?.Clear();

    public void Shutdown()
    {
        _palette?.Close();
        _overlay?.Close();
        _palette = null; _overlay = null;
        IsActive = false;
    }
}
