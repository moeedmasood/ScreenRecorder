using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace ScreenRecorder.App;

/// <summary>Global hotkeys on a window handle, reporting which ones could not be registered.</summary>
public sealed class HotkeyService : IDisposable
{
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_NOREPEAT = 0x4000;

    private readonly IntPtr _hwnd;
    private readonly Dictionary<int, Action> _actions = new();
    private int _nextId = 1;

    public List<string> Conflicts { get; } = new();

    public HotkeyService(IntPtr hwnd)
    {
        _hwnd = hwnd;
        HwndSource.FromHwnd(hwnd).AddHook(WndProc);
    }

    /// <summary>Registers Ctrl+Shift+key. Failure (key owned by another app) is recorded in <see cref="Conflicts"/>.</summary>
    public bool Register(char key, string name, Action action)
    {
        int id = _nextId++;
        if (!RegisterHotKey(_hwnd, id, MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, char.ToUpperInvariant(key)))
        {
            Conflicts.Add($"Ctrl+Shift+{char.ToUpperInvariant(key)} ({name})");
            return false;
        }
        _actions[id] = action;
        return true;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var action))
        {
            action();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        foreach (int id in _actions.Keys) UnregisterHotKey(_hwnd, id);
        _actions.Clear();
    }
}
