using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ScreenRecorder.Integration.Tests;

public sealed record MediaInfo(double Duration, int Width, int Height, double Fps, bool HasAudio, double VideoStart, double AudioStart)
{
    /// <summary>Finds ffmpeg via FFMPEG_PATH, then PATH, then the imageio_ffmpeg Python package.</summary>
    public static string? FindFfmpeg()
    {
        var env = Environment.GetEnvironmentVariable("FFMPEG_PATH");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try { var p = Path.Combine(dir.Trim(), "ffmpeg.exe"); if (File.Exists(p)) return p; } catch { }
        }
        try
        {
            var o = Run("python", "-c \"import imageio_ffmpeg;print(imageio_ffmpeg.get_ffmpeg_exe())\"").Trim();
            if (File.Exists(o)) return o;
        }
        catch { }
        return null;
    }

    private static string Run(string exe, string args)
    {
        var psi = new ProcessStartInfo(exe, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        var o = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return o + err.Result;
    }

    /// <summary>Parses the stream summary printed by <c>ffmpeg -i</c>.</summary>
    public static MediaInfo Probe(string ffmpeg, string file) => Parse(Run(ffmpeg, $"-hide_banner -i \"{file}\""));

    public static MediaInfo Parse(string text)
    {
        static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);
        var dur = Regex.Match(text, @"Duration: (\d+):(\d+):([\d.]+)");
        double duration = dur.Success ? D(dur.Groups[1].Value) * 3600 + D(dur.Groups[2].Value) * 60 + D(dur.Groups[3].Value) : 0;
        var vid = Regex.Match(text, @"Stream #0:(\d+)[^\n]*Video:[^\n]*?, (\d{2,5})x(\d{2,5})[^\n]*?(\d+(?:\.\d+)?) fps");
        bool audio = Regex.IsMatch(text, @"Stream #0:\d+[^\n]*Audio:");
        var start = Regex.Matches(text, @"start: ([\d.]+)");
        return new MediaInfo(duration,
            vid.Success ? int.Parse(vid.Groups[2].Value) : 0, vid.Success ? int.Parse(vid.Groups[3].Value) : 0,
            vid.Success ? D(vid.Groups[4].Value) : 0, audio,
            start.Count > 0 ? D(start[0].Groups[1].Value) : 0,
            start.Count > 1 ? D(start[1].Groups[1].Value) : 0);
    }
}
