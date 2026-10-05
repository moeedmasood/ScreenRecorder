using System.Diagnostics;
using System.Windows;
using ScreenRecorder.Core;

namespace ScreenRecorder.App;

public partial class AboutWindow : Window
{
    private readonly AppSettings _settings;
    private readonly Action _save;

    public AboutWindow(AppSettings settings, Action save)
    {
        InitializeComponent();
        _settings = settings; _save = save;
        VersionText.Text = "Screen Recorder " + AppInfo.Version;
        CrashCheck.IsChecked = settings.CrashReportsEnabled;
        UpdateCheck.IsChecked = settings.CheckForUpdates;
        CrashCheck.Click += (_, _) => { _settings.CrashReportsEnabled = CrashCheck.IsChecked == true; _save(); };
        UpdateCheck.Click += (_, _) => { _settings.CheckForUpdates = UpdateCheck.IsChecked == true; _save(); };
        Refresh();
    }

    private void Refresh()
    {
        var l = App.License;
        LicenseText.Text = l.Status switch
        {
            LicenseStatus.Valid => $"Pro licence active for {l.Current!.Licensee}" + (l.Current.ExpiresUtc is { } e ? $" (expires {e:d})" : ""),
            LicenseStatus.Expired => $"Licence for {l.Current!.Licensee} expired on {l.Current.ExpiresUtc:d}. Running as Free.",
            LicenseStatus.Invalid => "The saved licence key is not valid. Running as Free.",
            _ => "Free edition"
        };
    }

    private void OnActivate(object sender, RoutedEventArgs e)
    {
        var status = App.License.Activate(KeyBox.Text);
        Refresh();
        if (status == LicenseStatus.Valid) KeyBox.Clear();
        else MessageBox.Show(this, status == LicenseStatus.Expired ? "This licence has expired." : "That licence key is not valid.", "Licence");
    }

    private void OnRemove(object sender, RoutedEventArgs e) { App.License.Deactivate(); Refresh(); }

    private void OnExport(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = $"ScreenRecorder-logs-{DateTime.Now:yyyyMMdd}.zip", Filter = "Zip (*.zip)|*.zip" };
        if (dlg.ShowDialog(this) != true) return;
        try { Log.Export(dlg.FileName); MessageBox.Show(this, "Logs exported. They contain error text and system version only.", "Export logs"); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Export failed"); }
    }

    private void OnOpenLogs(object sender, RoutedEventArgs e)
    {
        System.IO.Directory.CreateDirectory(Log.LogDirectory);
        Process.Start("explorer.exe", $"\"{Log.LogDirectory}\"");
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
