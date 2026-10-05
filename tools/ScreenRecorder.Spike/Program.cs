using ScreenRecorder.Capture;
using ScreenRecorder.Core;

// Usage: Spike <seconds> [folder] [--mic] [--window <title substring>] [--region x,y,w,h] [--list] [--recover]
if (args.Contains("--list"))
{
    foreach (var w in WindowEnumerator.List()) Console.WriteLine($"{w.Handle}\t{w.Title}");
    foreach (var c in WebcamCapturer.List()) Console.WriteLine($"camera\t{c.Name}\t{c.Id}");
    return;
}
int seconds = args.Length > 0 && int.TryParse(args[0], out int s) ? s : 5;
string folder = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : Path.Combine(Path.GetTempPath(), "ScreenRecorderSpike");
if (args.Contains("--recover"))
{
    foreach (var r in RecordingSession.RecoverOrphans(folder)) Console.WriteLine("Recovered: " + r);
    return;
}
string? Arg(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }

CaptureTarget target = CaptureTarget.PrimaryMonitor;
if (Arg("--window") is { } title)
{
    var w = WindowEnumerator.List().FirstOrDefault(x => x.Title.Contains(title, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException("No window matches " + title);
    Console.WriteLine("Window: " + w.Title);
    target = new CaptureTarget(CaptureMode.Window, w.Handle);
}
else if (Arg("--region") is { } reg)
{
    var p = reg.Split(',').Select(int.Parse).ToArray();
    target = new CaptureTarget(CaptureMode.Region, 0, new PixelRect(p[0], p[1], p[2], p[3]));
}

var session = new RecordingSession(new RecordingOptions
{
    OutputFolder = folder, CaptureMicrophone = args.Contains("--mic"), Target = target,
    CaptureWebcam = args.Contains("--webcam"), ForceCompatibilityCapture = args.Contains("--compat"),
    WebcamSize = args.Contains("--cam-half") ? WebcamSize.Half : args.Contains("--cam-quarter") ? WebcamSize.Quarter : WebcamSize.Thumbnail,
    WebcamShape = args.Contains("--cam-circle") ? WebcamShape.Circle : args.Contains("--cam-round") ? WebcamShape.Rounded : WebcamShape.Rectangle,
                WebcamCorner = args.Contains("--cam-left") ? WebcamCorner.BottomLeft : WebcamCorner.BottomRight,
    WebcamDeviceId = args.SkipWhile(a => a != "--cam-id").Skip(1).FirstOrDefault()
});
session.Warning += w => Console.WriteLine("WARN: " + w);
session.StopRequested += r => Console.WriteLine("STOP REQUESTED: " + r);
session.Start();
var proc = System.Diagnostics.Process.GetCurrentProcess();
var cpu0 = proc.TotalProcessorTime; var wall = System.Diagnostics.Stopwatch.StartNew(); long peakWs = 0, startWs = 0;
Console.WriteLine($"Recording {seconds}s...");
for (int i = 0; i < seconds * 5; i++) { Thread.Sleep(200); if (i % 5 == 0) { proc.Refresh(); peakWs = Math.Max(peakWs, proc.WorkingSet64); if (startWs == 0) startWs = proc.WorkingSet64; }
    if (i % 5 == 0 && (!args.Contains("--quiet") || i % 300 == 0)) Console.WriteLine($"levels sys={session.SystemLevel:F2} mic={session.MicLevel:F2}"); }
string path = session.Stop();
Console.WriteLine($"Saved: {path}  frames={session.VideoFramesWritten}  size={new FileInfo(path).Length} bytes");

proc.Refresh();
double cpuPct = (proc.TotalProcessorTime - cpu0).TotalSeconds / wall.Elapsed.TotalSeconds / Environment.ProcessorCount * 100;
Console.WriteLine($"STATS cpu={cpuPct:F1}% of machine, ws start={startWs / 1048576}MB peak={peakWs / 1048576}MB, frames={session.VideoFramesWritten} (~{session.VideoFramesWritten / wall.Elapsed.TotalSeconds:F1} fps)");
