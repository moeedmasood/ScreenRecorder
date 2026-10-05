using System.Diagnostics;

namespace ScreenRecorder.Core;

/// <summary>Single recording clock in 100 ns units that excludes paused time.</summary>
public sealed class MasterClock
{
    private readonly Stopwatch _watch = new();
    private long _pausedTicks;
    private long _pauseStart = -1;

    public bool IsRunning => _watch.IsRunning && _pauseStart < 0;
    public bool IsPaused => _pauseStart >= 0;

    public void Start() => _watch.Start();

    public void Pause()
    {
        if (_pauseStart < 0) _pauseStart = _watch.ElapsedTicks;
    }

    public void Resume()
    {
        if (_pauseStart < 0) return;
        _pausedTicks += _watch.ElapsedTicks - _pauseStart;
        _pauseStart = -1;
    }

    public void Stop() => _watch.Stop();

    /// <summary>Elapsed recording time in 100 ns units (frozen while paused).</summary>
    public long NowHns
    {
        get
        {
            long ticks = (_pauseStart >= 0 ? _pauseStart : _watch.ElapsedTicks) - _pausedTicks;
            return (long)(ticks * (10_000_000.0 / Stopwatch.Frequency));
        }
    }
}
