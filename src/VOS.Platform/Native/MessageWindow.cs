using System.Runtime.InteropServices;

namespace VOS.Platform.Native;

/// <summary>
/// Hidden top-level tool window owned by the UI thread. Receives hotkeys, clipboard updates and the broadcast
/// messages (settings, display, power, session) that message-only windows never get.
/// </summary>
public sealed class MessageWindow : IDisposable
{
    private static readonly Win32.WndProc s_proc = StaticProc;
    private static readonly Dictionary<IntPtr, MessageWindow> s_windows = [];
    private static bool s_registered;
    private const string ClassName = "VOS.MessageWindow";

    public IntPtr Handle { get; }

    /// <summary>Return true to mark a message handled.</summary>
    public event Func<uint, IntPtr, IntPtr, bool>? Message;

    [DllImport("wtsapi32.dll")] private static extern bool WTSRegisterSessionNotification(IntPtr h, int flags);
    [DllImport("wtsapi32.dll")] private static extern bool WTSUnRegisterSessionNotification(IntPtr h);

    public MessageWindow(string name = "V-OS")
    {
        if (!s_registered)
        {
            var wc = new Win32.WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<Win32.WNDCLASSEXW>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(s_proc),
                hInstance = Win32.GetModuleHandleW(null),
                lpszClassName = ClassName,
            };
            Win32.RegisterClassExW(ref wc);
            s_registered = true;
        }
        Handle = Win32.CreateWindowExW((uint)Win32.WS_EX_TOOLWINDOW, ClassName, name, (uint)Win32.WS_POPUP, 0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, Win32.GetModuleHandleW(null), IntPtr.Zero);
        if (Handle == IntPtr.Zero) throw new InvalidOperationException("Could not create the V-OS message window.");
        s_windows[Handle] = this;
        WTSRegisterSessionNotification(Handle, 0 /*NOTIFY_FOR_THIS_SESSION*/);
    }

    private static IntPtr StaticProc(IntPtr h, uint msg, IntPtr w, IntPtr l)
    {
        if (s_windows.TryGetValue(h, out var self) && self.Message is { } handler)
        {
            try
            {
                foreach (Func<uint, IntPtr, IntPtr, bool> d in handler.GetInvocationList())
                    if (d(msg, w, l)) return IntPtr.Zero;
            }
            catch (Exception ex) { VOS.Core.Log.Error("Message handler failed", ex); }
        }
        return Win32.DefWindowProcW(h, msg, w, l);
    }

    public void Dispose()
    {
        WTSUnRegisterSessionNotification(Handle);
        s_windows.Remove(Handle);
        Win32.DestroyWindow(Handle);
    }
}

/// <summary>Subclasses an existing window (for example a WinUI surface) to observe its messages.</summary>
public sealed class WindowSubclass : IDisposable
{
    private delegate IntPtr SubclassProc(IntPtr h, uint msg, IntPtr w, IntPtr l, UIntPtr id, IntPtr data);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(IntPtr h, SubclassProc proc, UIntPtr id, IntPtr data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(IntPtr h, SubclassProc proc, UIntPtr id);
    [DllImport("comctl32.dll")] private static extern IntPtr DefSubclassProc(IntPtr h, uint msg, IntPtr w, IntPtr l);

    private static nuint s_nextId = 1;
    private readonly IntPtr _hwnd;
    private readonly SubclassProc _proc;
    private readonly UIntPtr _id;
    private readonly Func<uint, IntPtr, IntPtr, IntPtr?> _handler;

    /// <param name="handler">Return a value to swallow the message, or null to pass it on.</param>
    public WindowSubclass(IntPtr hwnd, Func<uint, IntPtr, IntPtr, IntPtr?> handler)
    {
        _hwnd = hwnd;
        _handler = handler;
        _proc = Proc;
        _id = (UIntPtr)s_nextId++;
        SetWindowSubclass(hwnd, _proc, _id, IntPtr.Zero);
    }

    private IntPtr Proc(IntPtr h, uint msg, IntPtr w, IntPtr l, UIntPtr id, IntPtr data)
    {
        try { if (_handler(msg, w, l) is { } r) return r; }
        catch (Exception ex) { VOS.Core.Log.Error("Subclass handler failed", ex); }
        return DefSubclassProc(h, msg, w, l);
    }

    public void Dispose() => RemoveWindowSubclass(_hwnd, _proc, _id);
}
