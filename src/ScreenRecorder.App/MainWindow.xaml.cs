using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using ScreenRecorder.Capture;
using ScreenRecorder.Core;

namespace ScreenRecorder.App;

public partial class MainWindow : Window
{
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);

    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

    private readonly SettingsService _settingsService = new();
    private readonly AppSettings _settings;
    private HotkeyService? _hotkeys;
    private readonly DrawingController _drawing = new();
    private ControlBarWindow? _bar;
    private WebcamCapturer? _preview;
    private System.Windows.Media.Imaging.WriteableBitmap? _previewBitmap;
    private byte[]? _previewBuffer;
    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(66) };
    private RecordingSession? _session;
    private string? _lastFile;
    private bool _loading = true;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };

    public MainWindow()
    {
        InitializeComponent();
        _settings = _settingsService.Load();

        FpsCombo.ItemsSource = new[] { 24, 30, 60 };
        CountdownCombo.ItemsSource = new[] { 0, 3, 5, 10 };
        AfterSaveCombo.ItemsSource = Enum.GetValues<PostSaveAction>();
        QualityCombo.ItemsSource = Enum.GetValues<QualityPreset>();
        MicCombo.ItemsSource = SafeMics();
        OnRefreshWindows(null!, null!);
        CameraCombo.ItemsSource = SafeCameras();
        CamSizeCombo.ItemsSource = Enum.GetValues<WebcamSize>();
        CamCornerCombo.ItemsSource = Enum.GetValues<WebcamCorner>();
        CamShapeCombo.ItemsSource = Enum.GetValues<WebcamShape>();
        MonitorCombo.ItemsSource = MonitorEnumerator.List();
        MonitorCombo.SelectedItem = MonitorEnumerator.Resolve(_settings.MonitorDevice);
        _drawing.BoundsProvider = () => CurrentMonitor().Bounds;

        FolderBox.Text = _settings.OutputFolder;
        FpsCombo.SelectedItem = _settings.FramesPerSecond;
        QualityCombo.SelectedItem = _settings.Quality;
        SystemAudioCheck.IsChecked = _settings.CaptureSystemAudio;
        MicCheck.IsChecked = _settings.CaptureMicrophone;
        CursorCheck.IsChecked = _settings.CaptureCursor;
        CompatCheck.IsChecked = _settings.ForceCompatibilityCapture;
        CountdownCombo.SelectedItem = _settings.CountdownSeconds;
        AfterSaveCombo.SelectedItem = _settings.AfterSave;
        WebcamCheck.IsChecked = _settings.CaptureWebcam;
        MirrorCheck.IsChecked = _settings.WebcamMirror;
        CamSizeCombo.SelectedItem = _settings.WebcamSize;
        CamCornerCombo.SelectedItem = _settings.WebcamCorner;
        CamShapeCombo.SelectedItem = _settings.WebcamShape;
        CameraCombo.SelectedItem = (CameraCombo.ItemsSource as List<WebcamInfo>)?.FirstOrDefault(w => w.Id == _settings.WebcamDeviceId);
        if (CameraCombo.SelectedItem is null && CameraCombo.Items.Count > 0) CameraCombo.SelectedIndex = 0;
        if (CameraCombo.Items.Count == 0) { WebcamCheck.IsEnabled = PreviewButton.IsEnabled = false; WebcamCheck.IsChecked = false; }
        _previewTimer.Tick += OnPreviewTick;
        _drawing.ActiveChanged += on => { DrawButton.IsChecked = on; _bar?.SetDrawActive(on); };
        MicCombo.SelectedItem = (MicCombo.ItemsSource as List<AudioDeviceInfo>)?
            .FirstOrDefault(m => m.Id == _settings.MicrophoneDeviceId);
        if (MicCombo.SelectedItem is null && MicCombo.Items.Count > 0) MicCombo.SelectedIndex = 0;
        (_settings.Mode switch { CaptureMode.Window => ModeWindow, CaptureMode.Region => ModeRegion, _ => ModeMonitor }).IsChecked = true;
        UpdateRegionText();
        _loading = false;
        OnModeChanged(null!, null!);

        _timer.Tick += (_, _) =>
        {
            if (_session is null) return;
            string t = _session.Elapsed.ToString(@"hh\:mm\:ss");
            TimerText.Text = t;
            _bar?.Update(t, _session.State == RecordingState.Paused, "");
            bool live = _session.State == RecordingState.Recording;
            SysMeter.Value = live ? _session.SystemLevel : 0;
            MicMeter.Value = live ? _session.MicLevel : 0;
            _bar?.SetLevels((float)SysMeter.Value, (float)MicMeter.Value);
        };
        Loaded += (_, _) => RecoverOnStartup();
        Loaded += async (_, _) => await BackgroundStartupTasksAsync();
    }

    private static List<WebcamInfo> SafeCameras()
    {
        try { return WebcamCapturer.List(); } catch { return new(); }
    }

    private static List<AudioDeviceInfo> SafeMics()
    {
        try { return AudioDevices.ListMicrophones(); } catch { return new(); }
    }

    private void RecoverOnStartup()
    {
        try
        {
            var recovered = RecordingSession.RecoverOrphans(_settings.OutputFolder);
            if (recovered.Count > 0)
            {
                _lastFile = recovered[^1];
                OpenFolderButton.IsEnabled = true;
                StatusText.Text = $"Recovered {recovered.Count} unfinished recording(s) from a previous session.";
            }
        }
        catch { /* folder unavailable; ignore */ }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);
        _hotkeys = new HotkeyService(hwnd);
        _hotkeys.Register('R', "record/stop", () => ToggleRecording());
        _hotkeys.Register('P', "pause", () => TogglePause());
        _hotkeys.Register('D', "draw", () => _drawing.Toggle());
        _hotkeys.Register('X', "clear drawing", () => _drawing.Clear());
        if (_hotkeys.Conflicts.Count > 0)
            StatusText.Text = "Hotkey already in use by another app: " + string.Join(", ", _hotkeys.Conflicts);
    }
    private void OnModeChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || WindowRow is null) return;
        _settings.Mode = ModeWindow.IsChecked == true ? CaptureMode.Window
            : ModeRegion.IsChecked == true ? CaptureMode.Region : CaptureMode.Monitor;
        MonitorCombo.Visibility = _settings.Mode == CaptureMode.Window || MonitorCombo.Items.Count < 2 ? Visibility.Collapsed : Visibility.Visible;
        WindowRow.Visibility = _settings.Mode == CaptureMode.Window ? Visibility.Visible : Visibility.Collapsed;
        RegionRow.Visibility = _settings.Mode == CaptureMode.Region ? Visibility.Visible : Visibility.Collapsed;
    }

    private MonitorInfo CurrentMonitor() =>
        MonitorCombo.SelectedItem as MonitorInfo ?? MonitorEnumerator.Resolve(_settings.MonitorDevice);

    private void OnRefreshWindows(object sender, RoutedEventArgs e)
    {
        var list = WindowEnumerator.List();
        WindowCombo.ItemsSource = list;
        if (list.Count > 0) WindowCombo.SelectedIndex = 0;
    }

    private void OnSelectRegion(object sender, RoutedEventArgs e)
    {
        Hide();
        var dlg = new RegionSelectorWindow(CurrentMonitor().Bounds);
        bool ok = dlg.ShowDialog() == true;
        Show();
        if (ok && dlg.Result is { } r) { _settings.LastRegion = r; UpdateRegionText(); }
    }

    private void UpdateRegionText()
    {
        var r = _settings.LastRegion;
        RegionText.Text = r.IsEmpty ? "No area selected" : $"{r.Width} x {r.Height} at ({r.X}, {r.Y})";
    }

    private void OnRecordClick(object sender, RoutedEventArgs e) => ToggleRecording();

    private void ToggleRecording()
    {
        if (_session is null) StartRecording(); else StopRecording();
    }

    private CaptureTarget? BuildTarget()
    {
        switch (_settings.Mode)
        {
            case CaptureMode.Window:
                if (WindowCombo.SelectedItem is not WindowInfo w)
                {
                    MessageBox.Show(this, "Choose a window to record.", "Screen Recorder");
                    return null;
                }
                return new CaptureTarget(CaptureMode.Window, w.Handle);
            case CaptureMode.Region:
                if (_settings.LastRegion.IsEmpty)
                {
                    MessageBox.Show(this, "Select an area to record first.", "Screen Recorder");
                    return null;
                }
                return new CaptureTarget(CaptureMode.Region, CurrentMonitor().Handle, _settings.LastRegion);
            default:
                return new CaptureTarget(CaptureMode.Monitor, CurrentMonitor().Handle);
        }
    }

    private bool _counting;

    private async void StartRecording()
    {
        if (_counting) return;
        var target = BuildTarget();
        if (target is null) return;
        CollectSettings();
        if (!EnforceEdition()) return;
        CollectSettings();
        StopPreview();
        Log.Info($"Start recording: mode={_settings.Mode} fps={_settings.FramesPerSecond} quality={_settings.Quality} webcam={_settings.CaptureWebcam} compat={_settings.ForceCompatibilityCapture}");
        if (_settings.CountdownSeconds > 0)
        {
            _counting = true;
            RecordButton.IsEnabled = false;
            WindowState = WindowState.Minimized;
            try { await new CountdownWindow(CurrentMonitor().Bounds, _settings.CountdownSeconds).RunAsync(); }
            finally { _counting = false; RecordButton.IsEnabled = true; WindowState = WindowState.Normal; }
        }
        try
        {
            var options = new RecordingOptions
            {
                OutputFolder = _settings.OutputFolder,
                FileNameTemplate = _settings.FileNameTemplate,
                FramesPerSecond = _settings.FramesPerSecond,
                Quality = _settings.Quality,
                CaptureSystemAudio = _settings.CaptureSystemAudio,
                CaptureMicrophone = _settings.CaptureMicrophone,
                MicrophoneDeviceId = _settings.MicrophoneDeviceId,
                CaptureCursor = _settings.CaptureCursor,
                CaptureWebcam = _settings.CaptureWebcam,
                WebcamDeviceId = _settings.WebcamDeviceId,
                WebcamSize = _settings.WebcamSize,
                WebcamCorner = _settings.WebcamCorner,
                WebcamMirror = _settings.WebcamMirror,
                WebcamShape = _settings.WebcamShape,
                ForceCompatibilityCapture = _settings.ForceCompatibilityCapture,
                Target = target
            };
            var session = new RecordingSession(options);
            session.Warning += w => { Log.Warn(w); Dispatcher.BeginInvoke(() => StatusText.Text = w); };
            session.StopRequested += reason => Dispatcher.BeginInvoke(() =>
            {
                if (_session is not null) { StopRecording(); StatusText.Text = reason + " " + StatusText.Text; }
            });
            session.Start();
            _session = session;
            RecordButton.Content = "Stop";
            PauseButton.IsEnabled = true;
            SetSettingsEnabled(false);
            StatusText.Text = "Recording...";
            _timer.Start();
            ShowControlBar();
        }
        catch (Exception ex)
        {
            Log.Error("Cannot start recording", ex);
            MessageBox.Show(this, ex.Message, "Cannot start recording", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void StopRecording()
    {
        _timer.Stop();
        CloseControlBar();
        var session = _session!;
        _session = null;
        SysMeter.Value = MicMeter.Value = 0;
        try { _lastFile = session.Stop(); StatusText.Text = "Saved: " + _lastFile; OpenFolderButton.IsEnabled = true; RunAfterSave(); }
        catch (Exception ex) { StatusText.Text = "Error while saving: " + ex.Message; }
        RecordButton.Content = "Record";
        PauseButton.Content = "Pause";
        PauseButton.IsEnabled = false;
        SetSettingsEnabled(true);
    }

    private void RunAfterSave()
    {
        if (_lastFile is null) return;
        try
        {
            if (_settings.AfterSave == PostSaveAction.ShowInFolder) Process.Start("explorer.exe", $"/select,\"{_lastFile}\"");
            else if (_settings.AfterSave == PostSaveAction.Play) Process.Start(new ProcessStartInfo(_lastFile) { UseShellExecute = true });
        }
        catch (Exception ex) { StatusText.Text = "Saved, but could not open: " + ex.Message; }
    }

    private void SetSettingsEnabled(bool on)
    {
        TargetGroup.IsEnabled = AudioGroup.IsEnabled = VideoGroup.IsEnabled = WebcamGroup.IsEnabled = on;
    }

    private void CollectSettings()
    {
        _settings.MonitorDevice = (MonitorCombo.SelectedItem as MonitorInfo)?.DeviceName;
        _settings.FramesPerSecond = FpsCombo.SelectedItem is int f ? f : 30;
        _settings.Quality = QualityCombo.SelectedItem is QualityPreset q ? q : QualityPreset.Standard;
        _settings.CaptureSystemAudio = SystemAudioCheck.IsChecked == true;
        _settings.CaptureMicrophone = MicCheck.IsChecked == true;
        _settings.CaptureCursor = CursorCheck.IsChecked == true;
        _settings.ForceCompatibilityCapture = CompatCheck.IsChecked == true;
        _settings.CountdownSeconds = CountdownCombo.SelectedItem is int cd ? cd : 3;
        _settings.AfterSave = AfterSaveCombo.SelectedItem is PostSaveAction pa ? pa : PostSaveAction.None;
        _settings.MicrophoneDeviceId = (MicCombo.SelectedItem as AudioDeviceInfo)?.Id;
        _settings.CaptureWebcam = WebcamCheck.IsChecked == true;
        _settings.WebcamMirror = MirrorCheck.IsChecked == true;
        _settings.WebcamDeviceId = (CameraCombo.SelectedItem as WebcamInfo)?.Id;
        if (CamSizeCombo.SelectedItem is WebcamSize ws) _settings.WebcamSize = ws;
        if (CamCornerCombo.SelectedItem is WebcamCorner wc) _settings.WebcamCorner = wc;
        if (CamShapeCombo.SelectedItem is WebcamShape wsh) _settings.WebcamShape = wsh;
        try { _settingsService.Save(_settings); } catch { /* non-fatal */ }
    }

    private void OnPauseClick(object sender, RoutedEventArgs e) => TogglePause();

    private void TogglePause()
    {
        if (_session is null) return;
        if (_session.State == RecordingState.Recording) { _session.Pause(); PauseButton.Content = "Resume"; StatusText.Text = "Paused"; }
        else { _session.Resume(); PauseButton.Content = "Pause"; StatusText.Text = "Recording..."; }
    }

    private async Task BackgroundStartupTasksAsync()
    {
        await CrashReporter.UploadPendingAsync(_settings.CrashReportsEnabled, ProductConfig.CrashReportUrl);
        if (!_settings.CheckForUpdates) return;
        var update = await UpdateChecker.CheckAsync(ProductConfig.UpdateManifestUrl, AppInfo.Version);
        if (update is null || _session is not null) return;
        if (MessageBox.Show(this, $"Version {update.Version} is available (you have {AppInfo.Version}).\n\n{update.Notes}\n\nOpen the download page?",
                "Update available", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
            Process.Start(new ProcessStartInfo(update.DownloadUrl) { UseShellExecute = true });
    }

    private void OnAboutClick(object sender, RoutedEventArgs e)
    {
        CollectSettings();
        new AboutWindow(_settings, () => { try { _settingsService.Save(_settings); } catch { } }) { Owner = this }.ShowDialog();
    }

    private bool EnforceEdition()
    {
        var lic = App.License;
        if (_settings.FramesPerSecond == 60 && !lic.Has(Feature.Fps60) || _settings.Quality == QualityPreset.High && !lic.Has(Feature.HighQuality))
        {
            var ans = MessageBox.Show(this, "60 fps and the High quality preset are Pro features. Record at 30 fps / Standard quality instead?\n\nChoose No to enter a licence key.",
                "Pro feature", MessageBoxButton.YesNoCancel, MessageBoxImage.Information);
            if (ans == MessageBoxResult.Cancel) return false;
            if (ans == MessageBoxResult.No) { OnAboutClick(this, new RoutedEventArgs()); return false; }
            if (_settings.FramesPerSecond == 60) { _settings.FramesPerSecond = 30; FpsCombo.SelectedItem = 30; }
            if (_settings.Quality == QualityPreset.High) { _settings.Quality = QualityPreset.Standard; QualityCombo.SelectedItem = QualityPreset.Standard; }
        }
        return true;
    }

    private void OnDrawClick(object sender, RoutedEventArgs e) => _drawing.Toggle();

    private void ShowControlBar()
    {
        _bar = new ControlBarWindow(CurrentMonitor().Bounds);
        _bar.PauseClicked += TogglePause;
        _bar.StopClicked += () => { if (_session is not null) StopRecording(); };
        _bar.DrawClicked += () => _drawing.Toggle();
        _bar.CamClicked += OpenCamAdjust;
        _bar.ShowCamButton(_session is { HasWebcam: true } && _settings.Mode != CaptureMode.Window);
        _bar.Show();
        WindowState = WindowState.Minimized;
    }

    private WebcamAdjustWindow? _camAdjust;

    private void OpenCamAdjust()
    {
        if (_session is not { HasWebcam: true } s || _camAdjust is not null) return;
        var mon = CurrentMonitor().Bounds;
        var origin = _settings.Mode == CaptureMode.Region
            ? (mon.X + Math.Clamp(_settings.LastRegion.X, 0, mon.Width), mon.Y + Math.Clamp(_settings.LastRegion.Y, 0, mon.Height))
            : (mon.X, mon.Y);
        _camAdjust = new WebcamAdjustWindow(origin, s.OutputWidth, s.OutputHeight, s.WebcamRect,
            _settings.WebcamShape == WebcamShape.Circle, s.SetWebcamRect);
        _camAdjust.Closed += (_, _) => _camAdjust = null;
        _camAdjust.Show();
    }

    private void CloseControlBar()
    {
        _camAdjust?.Close();
        _bar?.Close();
        _bar = null;
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
    }

    private void OnPreviewClick(object sender, RoutedEventArgs e)
    {
        if (PreviewButton.IsChecked == true) StartPreview(); else StopPreview();
    }

    private void StartPreview()
    {
        try
        {
            var cam = new WebcamCapturer();
            cam.Start((CameraCombo.SelectedItem as WebcamInfo)?.Id);
            _preview = cam;
            _previewBuffer = new byte[cam.Width * cam.Height * 4];
            _previewBitmap = new System.Windows.Media.Imaging.WriteableBitmap(cam.Width, cam.Height, 96, 96, System.Windows.Media.PixelFormats.Bgr32, null);
            PreviewImage.Source = _previewBitmap;
            PreviewImage.Visibility = Visibility.Visible;
            _previewTimer.Start();
        }
        catch (Exception ex)
        {
            PreviewButton.IsChecked = false;
            StatusText.Text = "Camera preview failed: " + ex.Message;
        }
    }

    private void StopPreview()
    {
        _previewTimer.Stop();
        _preview?.Dispose();
        _preview = null;
        PreviewImage.Visibility = Visibility.Collapsed;
        PreviewButton.IsChecked = false;
    }

    private void OnPreviewTick(object? sender, EventArgs e)
    {
        if (_preview is null || _previewBitmap is null || _previewBuffer is null) return;
        if (!_preview.TryCopyLatest(_previewBuffer)) return;
        _previewBitmap.WritePixels(new Int32Rect(0, 0, _preview.Width, _preview.Height), _previewBuffer, _preview.Width * 4, 0);
    }

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { InitialDirectory = _settings.OutputFolder, Title = "Choose save location" };
        if (dlg.ShowDialog(this) == true) { _settings.OutputFolder = dlg.FolderName; FolderBox.Text = dlg.FolderName; }
    }

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        if (_lastFile is not null) Process.Start("explorer.exe", $"/select,\"{_lastFile}\"");
    }

    protected override void OnClosed(EventArgs e)
    {
        _hotkeys?.Dispose();
        StopPreview();
        _drawing.Shutdown();
        CloseControlBar();
        _session?.Dispose();
        CollectSettings();
        base.OnClosed(e);
    }
}
