using System.Windows;
using ScreenRecorder.Core;

namespace ScreenRecorder.App;

/// <summary>Large "3, 2, 1" shown before recording starts. Excluded from capture and never takes focus.</summary>
public partial class CountdownWindow : Window
{
    private readonly PixelRect _screen;
    private readonly int _seconds;

    public CountdownWindow(PixelRect screen, int seconds)
    {
        InitializeComponent();
        _screen = screen; _seconds = seconds;
        SourceInitialized += (_, _) =>
        {
            NativeWindow.ExcludeFromCapture(this);
            NativeWindow.SetClickThrough(this, true);
        };
    }

    public async Task RunAsync()
    {
        NumberText.Text = _seconds.ToString();
        Show();
        NativeWindow.PlaceOn(this, new PixelRect(_screen.X + _screen.Width / 2 - 100, _screen.Y + _screen.Height / 2 - 100, 200, 200));
        for (int i = _seconds; i > 0; i--)
        {
            NumberText.Text = i.ToString();
            await Task.Delay(1000);
        }
        Close();
        await Task.Delay(150); // let the window leave the screen before the first frame
    }
}
