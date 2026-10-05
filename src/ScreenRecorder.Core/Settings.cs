using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScreenRecorder.Core;

public enum CaptureMode { Monitor, Window, Region }

public enum QualityPreset { Low, Standard, High }

public enum PostSaveAction { None, ShowInFolder, Play }

public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;
}

/// <summary>What to record. Handle is an HWND for Window mode, or an HMONITOR (0 = primary) for Monitor and Region; Region is relative to that monitor.</summary>
public sealed record CaptureTarget(CaptureMode Mode, long Handle = 0, PixelRect Region = default)
{
    public static CaptureTarget PrimaryMonitor { get; } = new(CaptureMode.Monitor);
}

public static class VideoQuality
{
    /// <summary>Bitrate scaled from a 1080p30 baseline by pixel count and frame rate.</summary>
    public static int Bitrate(QualityPreset preset, int width, int height, int fps)
    {
        double baseline = preset switch { QualityPreset.Low => 4_000_000, QualityPreset.High => 16_000_000, _ => 8_000_000 };
        double scale = (double)width * height / (1920.0 * 1080.0) * Math.Max(fps, 1) / 30.0;
        return (int)Math.Clamp(baseline * scale, 1_000_000, 60_000_000);
    }
}

public sealed class AppSettings
{
    public int Version { get; set; } = 1;
    public string OutputFolder { get; set; } = new RecordingOptions().OutputFolder;
    public string FileNameTemplate { get; set; } = "Recording_{date}_{time}";
    public int FramesPerSecond { get; set; } = 30;
    public QualityPreset Quality { get; set; } = QualityPreset.Standard;
    public bool CaptureSystemAudio { get; set; } = true;
    public bool CaptureMicrophone { get; set; } = true;
    public string? MicrophoneDeviceId { get; set; }
    public bool CaptureCursor { get; set; } = true;
    public CaptureMode Mode { get; set; } = CaptureMode.Monitor;
    public PixelRect LastRegion { get; set; }
    public string? MonitorDevice { get; set; }
    public bool ForceCompatibilityCapture { get; set; }
    public int CountdownSeconds { get; set; } = 3;
    /// <summary>Opt-in: allow anonymous crash reports to be sent. Off by default.</summary>
    public bool CrashReportsEnabled { get; set; }
    public bool CheckForUpdates { get; set; } = true;
    public PostSaveAction AfterSave { get; set; } = PostSaveAction.None;
    public bool CaptureWebcam { get; set; }
    public string? WebcamDeviceId { get; set; }
    public WebcamSize WebcamSize { get; set; } = WebcamSize.Thumbnail;
    public WebcamCorner WebcamCorner { get; set; } = WebcamCorner.BottomRight;
    public bool WebcamMirror { get; set; } = true;
    public WebcamShape WebcamShape { get; set; } = WebcamShape.Rectangle;

    /// <summary>Clamps values loaded from a hand-edited or old file to safe ranges.</summary>
    public void Normalize()
    {
        FramesPerSecond = FramesPerSecond is 24 or 30 or 60 ? FramesPerSecond : 30;
        if (string.IsNullOrWhiteSpace(OutputFolder)) OutputFolder = new AppSettings().OutputFolder;
        if (string.IsNullOrWhiteSpace(FileNameTemplate)) FileNameTemplate = "Recording_{date}_{time}";
        if (!Enum.IsDefined(Quality)) Quality = QualityPreset.Standard;
        if (!Enum.IsDefined(Mode)) Mode = CaptureMode.Monitor;
        if (!Enum.IsDefined(WebcamSize)) WebcamSize = WebcamSize.Thumbnail;
        if (!Enum.IsDefined(WebcamCorner)) WebcamCorner = WebcamCorner.BottomRight;
        if (!Enum.IsDefined(WebcamShape)) WebcamShape = WebcamShape.Rectangle;
        if (!Enum.IsDefined(AfterSave)) AfterSave = PostSaveAction.None;
        CountdownSeconds = CountdownSeconds is 0 or 3 or 5 or 10 ? CountdownSeconds : 3;
    }
}

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public string Path { get; }

    public SettingsService(string? path = null) =>
        Path = path ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScreenRecorder", "settings.json");

    /// <summary>Loads settings; a missing or corrupt file yields defaults rather than an exception.</summary>
    public AppSettings Load()
    {
        try
        {
            if (File.Exists(Path))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path), Json);
                if (s is not null) { s.Normalize(); return s; }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { }
        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        string tmp = Path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Json));
        File.Move(tmp, Path, overwrite: true);
    }
}
