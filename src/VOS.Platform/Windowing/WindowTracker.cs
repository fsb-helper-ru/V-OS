using System.Diagnostics;
using VOS.Core;
using VOS.Platform.Native;
using VOS.Platform.Shell;

namespace VOS.Platform.Windowing;

/// <summary>A top-level application window as the user sees it in Alt+Tab.</summary>
public sealed class AppWindowInfo
{
    public IntPtr Hwnd { get; init; }
    public string Title { get; set; } = "";
    public uint ProcessId { get; init; }
    public string? ExePath { get; init; }
    public string? AppId { get; init; }
    public bool IsMinimized { get; set; }
    public bool IsForeground { get; set; }
    public DateTime LastActivated { get; set; }

    /// <summary>Grouping key: AppUserModelID when set, else executable path.</summary>
    public string GroupKey => (AppId ?? ExePath ?? "pid:" + ProcessId).ToLowerInvariant();
    public string ProcessName => ExePath != null ? Path.GetFileNameWithoutExtension(ExePath) : "";
}

/// <summary>
/// Tracks application windows using WinEvent hooks (no polling). Callbacks arrive on the thread that called
/// <see cref="Start"/> (it must pump messages); refreshes are debounced and raised on a worker thread.
/// </summary>
public sealed class WindowTracker : IDisposable
{
    private readonly List<IntPtr> _hooks = [];
    private readonly Win32.WinEventProc _proc;
    private readonly Timer _debounce;
    private readonly Dictionary<IntPtr, AppWindowInfo> _known = [];
    private readonly Dictionary<IntPtr, DateTime> _activation = [];
    private readonly object _gate = new();
    private readonly HashSet<uint> _ignoredPids = [(uint)Environment.ProcessId];
    private IReadOnlyList<AppWindowInfo> _windows = [];

    public event Action? Changed;
    public event Action<IntPtr>? ForegroundChanged;
    public event Action<IntPtr, bool>? MoveSize; // hwnd, started

    public IReadOnlyList<AppWindowInfo> Windows { get { lock (_gate) return _windows; } }
    public IntPtr Foreground { get; private set; }

    public WindowTracker()
    {
        _proc = OnEvent;
        _debounce = new Timer(_ => Refresh(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Windows of these processes are never listed (the shell itself; app windows of V-OS are listed).</summary>
    public void IgnoreProcess(int pid) { lock (_gate) _ignoredPids.Add((uint)pid); }

    public void Start()
    {
        void Hook(uint min, uint max) =>
            _hooks.Add(Win32.SetWinEventHook(min, max, IntPtr.Zero, _proc, 0, 0, Win32.WINEVENT_OUTOFCONTEXT));
        Hook(Win32.EVENT_SYSTEM_FOREGROUND, Win32.EVENT_SYSTEM_FOREGROUND);
        Hook(Win32.EVENT_SYSTEM_MOVESIZESTART, Win32.EVENT_SYSTEM_MOVESIZEEND);
        Hook(Win32.EVENT_SYSTEM_MINIMIZESTART, Win32.EVENT_SYSTEM_MINIMIZEEND);
        Hook(Win32.EVENT_OBJECT_CREATE, Win32.EVENT_OBJECT_HIDE);
        Hook(Win32.EVENT_OBJECT_NAMECHANGE, Win32.EVENT_OBJECT_NAMECHANGE);
        Hook(Win32.EVENT_OBJECT_CLOAKED, Win32.EVENT_OBJECT_UNCLOAKED);
        Foreground = Win32.GetForegroundWindow();
        Refresh();
    }

    private void OnEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != 0 || hwnd == IntPtr.Zero) return; // OBJID_WINDOW only
        switch (evt)
        {
            case Win32.EVENT_SYSTEM_FOREGROUND:
                Foreground = hwnd;
                lock (_gate) _activation[hwnd] = DateTime.UtcNow;
                ForegroundChanged?.Invoke(hwnd);
                Schedule(30);
                break;
            case Win32.EVENT_SYSTEM_MOVESIZESTART: MoveSize?.Invoke(hwnd, true); break;
            case Win32.EVENT_SYSTEM_MOVESIZEEND: MoveSize?.Invoke(hwnd, false); break;
            case Win32.EVENT_OBJECT_NAMECHANGE:
                lock (_gate) if (!_known.ContainsKey(hwnd)) return;
                Schedule(250);
                break;
            default:
                if (Win32.GetAncestor(hwnd, 2 /*GA_ROOT*/) != hwnd) return;
                Schedule(120);
                break;
        }
    }

    private void Schedule(int ms) => _debounce.Change(ms, Timeout.Infinite);

    public void Refresh()
    {
        var list = new List<AppWindowInfo>();
        var fg = Win32.GetForegroundWindow();
        Win32.EnumWindows((h, _) =>
        {
            if (!IsAppWindow(h)) return true;
            Win32.GetWindowThreadProcessId(h, out uint pid);
            lock (_gate) if (_ignoredPids.Contains(pid)) return true;
            AppWindowInfo? info;
            lock (_gate) _known.TryGetValue(h, out info);
            if (info == null || info.ProcessId != pid)
            {
                var exe = Win32.GetProcessPath(pid);
                string? appId = ShellNative.WindowAppId(h);
                if (appId == null && exe != null && exe.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase)) appId = UwpAppId(h);
                info = new AppWindowInfo { Hwnd = h, ProcessId = pid, ExePath = exe, AppId = appId };
            }
            info.Title = Win32.GetText(h);
            info.IsMinimized = Win32.IsIconic(h);
            info.IsForeground = h == fg;
            lock (_gate) info.LastActivated = _activation.TryGetValue(h, out var t) ? t : info.LastActivated;
            list.Add(info);
            return true;
        }, IntPtr.Zero);

        bool changed;
        lock (_gate)
        {
            changed = list.Count != _windows.Count || list.Where((w, i) => w.Hwnd != _windows[i].Hwnd || w.Title != _windows[i].Title || w.IsMinimized != _windows[i].IsMinimized || w.IsForeground != _windows[i].IsForeground).Any();
            _known.Clear();
            foreach (var w in list) _known[w.Hwnd] = w;
            foreach (var stale in _activation.Keys.Where(k => !_known.ContainsKey(k)).ToList()) _activation.Remove(stale);
            _windows = list;
        }
        if (changed) Changed?.Invoke();
    }

    /// <summary>Alt+Tab eligibility rules.</summary>
    public static bool IsAppWindow(IntPtr h)
    {
        if (!Win32.IsWindowVisible(h) || Win32.IsCloaked(h)) return false;
        long ex = Win32.ExStyle(h);
        IntPtr owner = Win32.GetWindow(h, 4 /*GW_OWNER*/);
        bool appWindow = (ex & Win32.WS_EX_APPWINDOW) != 0;
        if ((ex & Win32.WS_EX_TOOLWINDOW) != 0 && !appWindow) return false;
        if ((ex & Win32.WS_EX_NOACTIVATE) != 0 && !appWindow) return false;
        if (owner != IntPtr.Zero && !appWindow && Win32.IsWindowVisible(owner)) return false;
        var cls = Win32.GetClass(h);
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Windows.UI.Core.CoreWindow" or "XamlExplorerHostIslandWindow") return false;
        if (Win32.GetWindowTextLengthW(h) == 0 && cls != "ApplicationFrameWindow") return false;
        return true;
    }

    private static string? UwpAppId(IntPtr frame)
    {
        string? id = null;
        Win32.EnumChildWindows(frame, (c, _) =>
        {
            if (Win32.GetClass(c) == "Windows.UI.Core.CoreWindow") { id = ShellNative.WindowAppId(c); return id == null; }
            return true;
        }, IntPtr.Zero);
        return id;
    }

    public static void Activate(IntPtr hwnd) => Win32.Activate(hwnd);
    public static void Minimize(IntPtr hwnd) => Win32.ShowWindowAsync(hwnd, Win32.SW_MINIMIZE);
    public static void Close(IntPtr hwnd) => Win32.PostMessageW(hwnd, Win32.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

    public void Dispose()
    {
        foreach (var h in _hooks) Win32.UnhookWinEvent(h);
        _hooks.Clear();
        _debounce.Dispose();
    }
}

/// <summary>Detects fullscreen apps and presentation mode (used to pause effects and enable Focus).</summary>
public static class FullscreenState
{
    public enum State { Normal, Busy, D3DFullscreen, Presentation, QuietTime, App }

    public static State Query()
    {
        if (Win32.SHQueryUserNotificationState(out int s) != 0) return State.Normal;
        return s switch { 2 => State.Busy, 3 => State.D3DFullscreen, 4 => State.Presentation, 6 => State.QuietTime, 7 => State.App, _ => State.Normal };
    }

    public static bool IsFullscreenActive() => Query() is State.Busy or State.D3DFullscreen or State.Presentation;

    /// <summary>True when the foreground window covers its whole monitor (games, video, presentations).</summary>
    public static bool ForegroundCoversMonitor()
    {
        var fg = Win32.GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == Win32.GetShellWindow()) return false;
        var cls = Win32.GetClass(fg);
        if (cls is "Progman" or "WorkerW") return false;
        if (!Win32.GetWindowRect(fg, out var r)) return false;
        var mon = Win32.MonitorFromWindow(fg, 2);
        var mi = new Win32.MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32.MONITORINFOEX>() };
        return Win32.GetMonitorInfoW(mon, ref mi) && r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top && r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
    }
}

/// <summary>Monitor enumeration with per-monitor DPI.</summary>
public static class Displays
{
    public sealed record Monitor(IntPtr Handle, Win32.RECT Bounds, Win32.RECT WorkArea, bool IsPrimary, double Scale, string Device);

    public static List<Monitor> All()
    {
        var list = new List<Monitor>();
        Win32.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr hdc, ref Win32.RECT r, IntPtr d) =>
        {
            var mi = new Win32.MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32.MONITORINFOEX>() };
            if (Win32.GetMonitorInfoW(h, ref mi))
            {
                double scale = Win32.GetDpiForMonitor(h, 0, out uint dx, out _) == 0 ? dx / 96.0 : 1.0;
                list.Add(new Monitor(h, mi.rcMonitor, mi.rcWork, (mi.dwFlags & 1) != 0, scale, mi.szDevice));
            }
            return true;
        }, IntPtr.Zero);
        return list.OrderByDescending(m => m.IsPrimary).ToList();
    }

    public static Monitor Primary => All().FirstOrDefault() ?? new Monitor(IntPtr.Zero, new Win32.RECT(0, 0, 1920, 1080), Win32.WorkArea(), true, 1, "");
}
