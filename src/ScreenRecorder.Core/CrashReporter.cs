using System.Text;

namespace ScreenRecorder.Core;

/// <summary>
/// Crash reports are always written locally. They are uploaded only if the user opted in and an endpoint is configured.
/// A report contains the exception text, app/OS version and nothing else (no recordings, file names or screen content).
/// </summary>
public static class CrashReporter
{
    public static string Write(Exception ex, string source)
    {
        Log.Error("Unhandled exception (" + source + ")", ex);
        var sb = new StringBuilder()
            .AppendLine($"App version: {AppInfo.Version}")
            .AppendLine($"OS: {Environment.OSVersion}")
            .AppendLine($"Source: {source}")
            .AppendLine($"Time (UTC): {DateTime.UtcNow:O}")
            .AppendLine().AppendLine(ex.ToString());
        Directory.CreateDirectory(Log.LogDirectory);
        var path = Path.Combine(Log.LogDirectory, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        File.WriteAllText(path, sb.ToString());
        return path;
    }

    public static IReadOnlyList<string> Pending() =>
        Directory.Exists(Log.LogDirectory)
            ? Directory.GetFiles(Log.LogDirectory, "crash-*.txt").Where(f => !File.Exists(f + ".sent")).ToList()
            : new List<string>();

    /// <summary>Uploads pending reports when <paramref name="enabled"/> and <paramref name="endpoint"/> is an https URL. Returns the number sent.</summary>
    public static async Task<int> UploadPendingAsync(bool enabled, string endpoint, CancellationToken ct = default)
    {
        if (!enabled || !endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return 0;
        int sent = 0;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        foreach (var f in Pending())
        {
            try
            {
                using var resp = await http.PostAsync(endpoint, new StringContent(File.ReadAllText(f), Encoding.UTF8, "text/plain"), ct);
                if (resp.IsSuccessStatusCode) { File.WriteAllText(f + ".sent", ""); sent++; }
            }
            catch (Exception e) { Log.Warn("Crash upload failed: " + e.Message); break; }
        }
        return sent;
    }
}
