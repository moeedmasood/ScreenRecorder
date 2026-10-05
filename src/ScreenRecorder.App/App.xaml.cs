using System.Windows;
using System.Windows.Threading;
using ScreenRecorder.Core;

namespace ScreenRecorder.App;

public partial class App : Application
{
    public static LicenseService License { get; } = new(ProductConfig.LicensePublicKey);

    protected override void OnStartup(StartupEventArgs e)
    {
        Log.Prune();
        Log.Info($"Screen Recorder {AppInfo.Version} starting");
        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (_, a) =>
        {
            if (a.ExceptionObject is Exception ex) TryWrite(ex, "AppDomain");
        };
        TaskScheduler.UnobservedTaskException += (_, a) => { Log.Error("Unobserved task exception", a.Exception); a.SetObserved(); };
        License.Load();
        base.OnStartup(e);
    }

    private static void TryWrite(Exception ex, string source)
    {
        try { CrashReporter.Write(ex, source); } catch { }
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        TryWrite(e.Exception, "Dispatcher");
        MessageBox.Show("Screen Recorder hit an unexpected error and will close. Any recording in progress can be recovered the next time you start the app.\n\nDetails were saved to the log (Help > Export logs).",
            "Screen Recorder", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
        Shutdown(1);
    }
}
