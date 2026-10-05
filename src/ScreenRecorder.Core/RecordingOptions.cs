namespace ScreenRecorder.Core;

public enum RecordingState { Idle, Recording, Paused, Stopping }

public sealed record RecordingOptions
{
    public string OutputFolder { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "ScreenRecorder");
    public string FileNameTemplate { get; init; } = "Recording_{date}_{time}";
    public int FramesPerSecond { get; init; } = 30;
    public int VideoBitrate { get; init; } = 8_000_000;
    public int AudioBitrate { get; init; } = 192_000;
    public bool CaptureSystemAudio { get; init; } = true;
    public bool CaptureMicrophone { get; init; } = true;
    public string? MicrophoneDeviceId { get; init; }
    public float SystemGain { get; init; } = 1.0f;
    public float MicrophoneGain { get; init; } = 1.0f;
    public bool CaptureCursor { get; init; } = true;
    public CaptureTarget Target { get; init; } = CaptureTarget.PrimaryMonitor;
    public QualityPreset Quality { get; init; } = QualityPreset.Standard;
    public bool CaptureWebcam { get; init; }
    public string? WebcamDeviceId { get; init; }
    public WebcamSize WebcamSize { get; init; } = WebcamSize.Thumbnail;
    public WebcamCorner WebcamCorner { get; init; } = WebcamCorner.BottomRight;
    public bool WebcamMirror { get; init; } = true;
    /// <summary>Use Desktop Duplication instead of Windows Graphics Capture (for exclusive-fullscreen games).</summary>
    public bool ForceCompatibilityCapture { get; init; }
    public WebcamShape WebcamShape { get; init; } = WebcamShape.Rectangle;
}

public static class OutputPaths
{
    public static string BuildFinalPath(RecordingOptions o, DateTime now)
    {
        string name = o.FileNameTemplate
            .Replace("{date}", now.ToString("yyyy-MM-dd"))
            .Replace("{time}", now.ToString("HH-mm-ss"));
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return Path.Combine(o.OutputFolder, name + ".mp4");
    }
}

