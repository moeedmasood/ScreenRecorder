using System.IO.Compression;
using System.Text;

namespace ScreenRecorder.Core;

/// <summary>Minimal rolling file log under %LOCALAPPDATA%\ScreenRecorder\logs. Never logs file contents or capture data.</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string? _dir;

    public static string LogDirectory => _dir ??= Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenRecorder", "logs");

    /// <summary>Overrides the log folder (used by tests).</summary>
    public static void UseDirectory(string? dir) => _dir = dir;

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", ex is null ? message : message + Environment.NewLine + ex);

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogDirectory);
                var file = Path.Combine(LogDirectory, $"app-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(file, $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch { /* logging must never throw */ }
    }

    /// <summary>Deletes log and crash files older than <paramref name="keepDays"/>.</summary>
    public static void Prune(int keepDays = 14)
    {
        try
        {
            if (!Directory.Exists(LogDirectory)) return;
            foreach (var f in Directory.GetFiles(LogDirectory))
                if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-keepDays)) File.Delete(f);
        }
        catch { }
    }

    /// <summary>Zips all logs and crash reports plus a small system summary for support.</summary>
    public static string Export(string zipPath)
    {
        if (File.Exists(zipPath)) File.Delete(zipPath);
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        if (Directory.Exists(LogDirectory))
            foreach (var f in Directory.GetFiles(LogDirectory))
            {
                try
                {
                    using var src = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var dst = zip.CreateEntry(Path.GetFileName(f)).Open();
                    src.CopyTo(dst);
                }
                catch { }
            }
        var info = zip.CreateEntry("system.txt");
        using var w = new StreamWriter(info.Open(), Encoding.UTF8);
        w.WriteLine($"App version: {AppInfo.Version}");
        w.WriteLine($"OS: {Environment.OSVersion}");
        w.WriteLine($"64-bit: {Environment.Is64BitOperatingSystem}");
        w.WriteLine($"Processors: {Environment.ProcessorCount}");
        w.WriteLine($".NET: {Environment.Version}");
        return zipPath;
    }
}

public static class AppInfo
{
    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
