using System.Runtime.InteropServices;
using ScreenRecorder.Core;

namespace ScreenRecorder.Capture;

/// <summary>A display. Bounds are physical pixels in virtual-desktop coordinates.</summary>
public sealed record MonitorInfo(long Handle, string DeviceName, PixelRect Bounds, bool IsPrimary, int Index)
{
    public override string ToString() =>
        $"Display {Index} ({Bounds.Width}x{Bounds.Height}){(IsPrimary ? " - Primary" : "")}";
}

public static class MonitorEnumerator
{
    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFOEX info);

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int Size; public RECT Monitor, Work; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }

    /// <summary>Lists displays with the primary first.</summary>
    public static List<MonitorInfo> List()
    {
        var found = new List<(IntPtr h, string dev, PixelRect r, bool primary)>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (m, _, _, _) =>
        {
            var info = new MONITORINFOEX { Size = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(m, ref info))
                found.Add((m, info.Device, new PixelRect(info.Monitor.Left, info.Monitor.Top,
                    info.Monitor.Right - info.Monitor.Left, info.Monitor.Bottom - info.Monitor.Top), (info.Flags & 1) != 0));
            return true;
        }, IntPtr.Zero);
        return found.OrderByDescending(f => f.primary).ThenBy(f => f.dev, StringComparer.Ordinal)
            .Select((f, i) => new MonitorInfo((long)f.h, f.dev, f.r, f.primary, i + 1)).ToList();
    }

    /// <summary>Finds the display by device name, falling back to the primary one.</summary>
    public static MonitorInfo Resolve(string? deviceName)
    {
        var all = List();
        return all.FirstOrDefault(m => m.DeviceName == deviceName) ?? all.First(m => m.IsPrimary);
    }
}
