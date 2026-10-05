using System.Windows;
using System.Windows.Input;

namespace ScreenRecorder.App;

/// <summary>Floating start/pause/stop bar shown while recording. Excluded from capture.</summary>
public partial class ControlBarWindow : Window
{
    public event Action? PauseClicked, StopClicked, DrawClicked, CamClicked;
    public void ShowCamButton(bool on) => CamBtn.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    private void OnCam(object sender, RoutedEventArgs e) => CamClicked?.Invoke();

    public ControlBarWindow(ScreenRecorder.Core.PixelRect bounds)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => NativeWindow.ExcludeFromCapture(this);
        Loaded += (_, _) => NativeWindow.CenterOn(this, bounds, 60, fromBottom: true);
    }

    public void Update(string time, bool paused, string size)
    {
        TimerText.Text = time;
        PauseBtn.Content = paused ? "Resume" : "Pause";
        RecDot.Opacity = paused ? 0.3 : 1;
        SizeText.Text = size;
    }

    public void SetLevels(float system, float mic) { SysMeter.Value = system; MicMeter.Value = mic; }

    public void SetDrawActive(bool on) => DrawBtn.IsChecked = on;

    private void OnDrag(object sender, MouseButtonEventArgs e) { if (e.OriginalSource is System.Windows.Controls.Border) DragMove(); }
    private void OnPause(object sender, RoutedEventArgs e) => PauseClicked?.Invoke();
    private void OnStop(object sender, RoutedEventArgs e) => StopClicked?.Invoke();
    private void OnDraw(object sender, RoutedEventArgs e) => DrawClicked?.Invoke();
}
