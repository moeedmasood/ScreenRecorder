using System.Text.Json;

namespace ScreenRecorder.Core;

/// <summary>Result of comparing the running version with a published update manifest.</summary>
public sealed record UpdateManifest(string Version, string DownloadUrl, string? Notes, string? Sha256);

public static class UpdateChecker
{
    public static UpdateManifest? Parse(string json)
    {
        try { return JsonSerializer.Deserialize<UpdateManifest>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
        catch { return null; }
    }

    public static bool IsNewer(string candidate, string current) =>
        Version.TryParse(candidate, out var c) && Version.TryParse(current, out var r) && c > r;

    /// <summary>Downloads the manifest and returns it only if it is newer and the URL is https. Returns null when disabled or on any error.</summary>
    public static async Task<UpdateManifest?> CheckAsync(string manifestUrl, string currentVersion, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(manifestUrl) || !manifestUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var m = Parse(await http.GetStringAsync(manifestUrl, ct));
            return m is not null && IsNewer(m.Version, currentVersion) && m.DownloadUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? m : null;
        }
        catch (Exception ex) { Log.Warn("Update check failed: " + ex.Message); return null; }
    }
}
