using System.Runtime.InteropServices;
using System.Text;
using VOS.Core;
using VOS.Platform.Native;

namespace VOS.Platform.Desktop;

/// <summary>Registers a surface as a Windows AppBar so maximized windows do not cover it.</summary>
public sealed class AppBar : IDisposable
{
    private readonly IntPtr _hwnd;
    private readonly uint _callback;
    private readonly WindowSubclass _subclass;
    private bool _registered;
    private uint _edge;
    private int _thickness;
    private Win32.RECT _monitor;

    /// <summary>Raised with the final bar rectangle (physical pixels) after (re)positioning.</summary>
    public event Action<Win32.RECT>? Positioned;
    public event Action<bool>? FullscreenAppChanged;

    public AppBar(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _callback = Win32.RegisterWindowMessageW("VOS.AppBar.Callback");
        _subclass = new WindowSubclass(hwnd, OnMessage);
    }

    public void Register(uint edge, int thickness, Win32.RECT monitorBounds)
    {
        _edge = edge; _thickness = thickness; _monitor = monitorBounds;
        var abd = Data();
        if (!_registered) { Win32.SHAppBarMessage(Win32.ABM_NEW, ref abd); _registered = true; }
        Reposition();
    }

    private Win32.APPBARDATA Data() => new() { cbSize = Marshal.SizeOf<Win32.APPBARDATA>(), hWnd = _hwnd, uCallbackMessage = _callback };

    public void Reposition()
    {
        if (!_registered) return;
        var abd = Data();
        abd.uEdge = _edge;
        abd.rc = _monitor;
        switch (_edge)
        {
            case Win32.ABE_TOP: abd.rc.Bottom = abd.rc.Top + _thickness; break;
            case Win32.ABE_BOTTOM: abd.rc.Top = abd.rc.Bottom - _thickness; break;
            case Win32.ABE_LEFT: abd.rc.Right = abd.rc.Left + _thickness; break;
            case Win32.ABE_RIGHT: abd.rc.Left = abd.rc.Right - _thickness; break;
        }
        Win32.SHAppBarMessage(Win32.ABM_QUERYPOS, ref abd);
        switch (_edge)
        {
            case Win32.ABE_TOP: abd.rc.Bottom = abd.rc.Top + _thickness; break;
            case Win32.ABE_BOTTOM: abd.rc.Top = abd.rc.Bottom - _thickness; break;
            case Win32.ABE_LEFT: abd.rc.Right = abd.rc.Left + _thickness; break;
            case Win32.ABE_RIGHT: abd.rc.Left = abd.rc.Right - _thickness; break;
        }
        Win32.SHAppBarMessage(Win32.ABM_SETPOS, ref abd);
        Positioned?.Invoke(abd.rc);
    }

    private IntPtr? OnMessage(uint msg, IntPtr w, IntPtr l)
    {
        if (msg == _callback)
        {
            switch ((int)w)
            {
                case Win32.ABN_POSCHANGED: Reposition(); break;
                case Win32.ABN_FULLSCREENAPP: FullscreenAppChanged?.Invoke(l != IntPtr.Zero); break;
            }
            return IntPtr.Zero;
        }
        if (msg == 0x0006 /*WM_ACTIVATE*/ && _registered) { var abd = Data(); Win32.SHAppBarMessage(Win32.ABM_ACTIVATE, ref abd); }
        if (msg == 0x0047 /*WM_WINDOWPOSCHANGED*/ && _registered) { var abd = Data(); Win32.SHAppBarMessage(Win32.ABM_WINDOWPOSCHANGED, ref abd); }
        return null;
    }

    public void Unregister()
    {
        if (!_registered) return;
        var abd = Data();
        Win32.SHAppBarMessage(Win32.ABM_REMOVE, ref abd);
        _registered = false;
    }

    public void Dispose() { Unregister(); _subclass.Dispose(); }
}

/// <summary>Everything immersive mode changes, recorded before changing it so recovery can always restore it.</summary>
public sealed class ImmersiveRecord
{
    public bool Active { get; set; }
    public int ShellProcessId { get; set; }
    public bool TaskbarWasAutoHide { get; set; }
    public bool TaskbarHidden { get; set; }
    public bool DesktopIconsHidden { get; set; }
    public DateTimeOffset Since { get; set; }
}

/// <summary>
/// Reversible changes to the Windows desktop (companion: taskbar auto-hide; immersive: taskbar and icons hidden).
/// Hidden windows are not persisted by Windows, but taskbar auto-hide is an Explorer setting that survives restarts,
/// so the original value is recorded first and restored on exit, by the watchdog, at the next start, by the
/// recovery hotkey, by Launcher › Recovery and by uninstall.
/// </summary>
public static class DesktopMode
{
    private static string RecordPath => Path.Combine(VosPaths.State, "immersive.json");

    public static ImmersiveRecord? ReadRecord() => VosJson.TryDeserialize<ImmersiveRecord>(AtomicFile.TryReadAllText(RecordPath));
    private static void WriteRecord(ImmersiveRecord r) => AtomicFile.WriteAllText(RecordPath, VosJson.Serialize(r));

    private static IEnumerable<IntPtr> Taskbars()
    {
        var primary = Win32.FindWindowW("Shell_TrayWnd", null);
        if (primary != IntPtr.Zero) yield return primary;
        IntPtr h = IntPtr.Zero;
        while ((h = Win32.FindWindowExW(IntPtr.Zero, h, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero) yield return h;
    }

    public static bool TaskbarAutoHide
    {
        get { var abd = new Win32.APPBARDATA { cbSize = Marshal.SizeOf<Win32.APPBARDATA>() }; return ((int)Win32.SHAppBarMessage(Win32.ABM_GETSTATE, ref abd) & Win32.ABS_AUTOHIDE) != 0; }
        set
        {
            var abd = new Win32.APPBARDATA { cbSize = Marshal.SizeOf<Win32.APPBARDATA>(), lParam = value ? Win32.ABS_AUTOHIDE : Win32.ABS_ALWAYSONTOP };
            Win32.SHAppBarMessage(Win32.ABM_SETSTATE, ref abd);
        }
    }

    public static void SetTaskbarVisible(bool visible)
    {
        foreach (var t in Taskbars()) Win32.ShowWindow(t, visible ? Win32.SW_SHOW : Win32.SW_HIDE);
    }

    private static IntPtr DesktopListView()
    {
        IntPtr defView = IntPtr.Zero;
        var progman = Win32.FindWindowW("Progman", null);
        defView = Win32.FindWindowExW(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (defView == IntPtr.Zero)
        {
            IntPtr w = IntPtr.Zero;
            while (defView == IntPtr.Zero && (w = Win32.FindWindowExW(IntPtr.Zero, w, "WorkerW", null)) != IntPtr.Zero)
                defView = Win32.FindWindowExW(w, IntPtr.Zero, "SHELLDLL_DefView", null);
        }
        return defView == IntPtr.Zero ? IntPtr.Zero : Win32.FindWindowExW(defView, IntPtr.Zero, "SysListView32", null);
    }

    public static bool DesktopIconsVisible => DesktopListView() is var lv && lv != IntPtr.Zero && Win32.IsWindowVisible(lv);

    public static void SetDesktopIconsVisible(bool visible)
    {
        var lv = DesktopListView();
        if (lv != IntPtr.Zero) Win32.ShowWindow(lv, visible ? Win32.SW_SHOW : Win32.SW_HIDE);
    }

    /// <summary>
    /// Applies the desired Windows desktop state (companion: taskbar auto-hide; immersive: taskbar and icons hidden),
    /// recording the original state the first time so recovery can always restore it.
    /// </summary>
    public static void Enter(bool hideTaskbarWindow, bool autoHideTaskbar, bool hideIcons)
    {
        var existing = ReadRecord();
        var record = existing is { Active: true } ? existing : new ImmersiveRecord { TaskbarWasAutoHide = TaskbarAutoHide, Since = DateTimeOffset.Now };
        record.Active = true;
        record.ShellProcessId = Environment.ProcessId;
        record.TaskbarHidden = hideTaskbarWindow;
        record.DesktopIconsHidden = hideIcons;
        WriteRecord(record);
        TaskbarAutoHide = autoHideTaskbar || hideTaskbarWindow || record.TaskbarWasAutoHide;
        SetTaskbarVisible(!hideTaskbarWindow);
        SetDesktopIconsVisible(!hideIcons);
        Log.Info("Desktop state applied", new { hideTaskbarWindow, autoHideTaskbar, hideIcons });
    }

    /// <summary>Restores Windows UI from the record. Safe to call any number of times, from any process.</summary>
    public static bool Restore()
    {
        var r = ReadRecord();
        SetTaskbarVisible(true);
        SetDesktopIconsVisible(true);
        if (r is { Active: true })
        {
            TaskbarAutoHide = r.TaskbarWasAutoHide;
            r.Active = false;
            WriteRecord(r);
            Log.Info("Windows desktop restored from immersive mode");
            return true;
        }
        return false;
    }

    public static string? CurrentWallpaper()
    {
        var sb = new StringBuilder(520);
        return Win32.SystemParametersInfoW(Win32.SPI_GETDESKWALLPAPER, (uint)sb.Capacity, sb, 0) ? sb.ToString() : null;
    }

    public static bool SetWallpaper(string imagePath) =>
        File.Exists(imagePath) && Win32.SystemParametersInfoW(Win32.SPI_SETDESKWALLPAPER, 0, imagePath, Win32.SPIF_UPDATEINIFILE | Win32.SPIF_SENDCHANGE);
}
