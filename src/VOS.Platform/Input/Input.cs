using System.Runtime.InteropServices;
using VOS.Core;
using VOS.Platform.Native;

namespace VOS.Platform.Input;

public readonly record struct Gesture(uint Modifiers, uint Key)
{
    public static bool TryParse(string? text, out Gesture g)
    {
        g = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        uint mods = 0, key = 0;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= Win32.MOD_CONTROL; break;
                case "alt": mods |= Win32.MOD_ALT; break;
                case "shift": mods |= Win32.MOD_SHIFT; break;
                case "win": case "windows": case "meta": mods |= Win32.MOD_WIN; break;
                default:
                    if (key != 0) return false;
                    key = KeyCode(raw);
                    if (key == 0) return false;
                    break;
            }
        }
        if (key == 0) return false;
        g = new Gesture(mods, key);
        return true;
    }

    public static uint KeyCode(string k)
    {
        var u = k.ToUpperInvariant();
        if (u.Length == 1 && (char.IsLetterOrDigit(u[0]))) return u[0];
        if (u.Length >= 2 && u[0] == 'F' && int.TryParse(u[1..], out int f) && f is >= 1 and <= 24) return (uint)(0x70 + f - 1);
        return u switch
        {
            "SPACE" => 0x20, "TAB" => 0x09, "ENTER" or "RETURN" => 0x0D, "ESC" or "ESCAPE" => 0x1B, "BACKSPACE" => 0x08,
            "LEFT" => 0x25, "UP" => 0x26, "RIGHT" => 0x27, "DOWN" => 0x28, "HOME" => 0x24, "END" => 0x23, "PAGEUP" => 0x21,
            "PAGEDOWN" => 0x22, "INSERT" => 0x2D, "DELETE" or "DEL" => 0x2E, "PRINTSCREEN" or "PRTSC" => 0x2C, "`" or "BACKTICK" => 0xC0,
            "," => 0xBC, "." => 0xBE, "/" => 0xBF, ";" => 0xBA, "'" => 0xDE, "[" => 0xDB, "]" => 0xDD, "\\" => 0xDC, "-" => 0xBD, "=" => 0xBB,
            _ => 0,
        };
    }
}

/// <summary>System-wide hotkeys (RegisterHotKey). Conflicts are reported, never forced.</summary>
public sealed class HotkeyManager : IDisposable
{
    private readonly MessageWindow _window;
    private readonly Dictionary<int, string> _actions = [];
    private int _nextId = 0x5000;

    public event Action<string>? Pressed;

    public HotkeyManager(MessageWindow window)
    {
        _window = window;
        _window.Message += OnMessage;
    }

    /// <summary>Returns false (and logs) when the gesture is invalid or already taken by another app.</summary>
    public bool Register(string action, string gesture)
    {
        if (!Gesture.TryParse(gesture, out var g)) { Log.Warn($"Invalid hotkey '{gesture}' for {action}"); return false; }
        int id = _nextId++;
        if (!Win32.RegisterHotKey(_window.Handle, id, g.Modifiers | Win32.MOD_NOREPEAT, g.Key))
        {
            Log.Warn($"Hotkey {gesture} for {action} is in use by another application", null, new { error = Marshal.GetLastWin32Error() });
            return false;
        }
        _actions[id] = action;
        return true;
    }

    public void UnregisterAll()
    {
        foreach (var id in _actions.Keys) Win32.UnregisterHotKey(_window.Handle, id);
        _actions.Clear();
    }

    private bool OnMessage(uint msg, IntPtr w, IntPtr l)
    {
        if (msg != Win32.WM_HOTKEY) return false;
        if (_actions.TryGetValue((int)w, out var action)) Pressed?.Invoke(action);
        return true;
    }

    public void Dispose() { UnregisterAll(); _window.Message -= OnMessage; }
}

/// <summary>
/// Optional (immersive mode only): a tap of the Windows key opens the V-OS launcher instead of Start.
/// Runs on a dedicated thread so a busy UI thread can never stall keyboard input. Win+key combinations are untouched.
/// </summary>
public sealed class WinKeyInterceptor : IDisposable
{
    private Thread? _thread;
    private uint _threadId;
    private IntPtr _hook;
    private Win32.LowLevelKeyboardProc? _proc;
    private bool _winDown, _otherPressed;
    public event Action? Tapped;

    public void Start()
    {
        if (_thread != null) return;
        _thread = new Thread(Run) { IsBackground = true, Name = "V-OS Win key" };
        _thread.Start();
    }

    private void Run()
    {
        _threadId = Win32.GetCurrentThreadId();
        _proc = Hook;
        _hook = Win32.SetWindowsHookExW(13 /*WH_KEYBOARD_LL*/, _proc, Win32.GetModuleHandleW(null), 0);
        if (_hook == IntPtr.Zero) { Log.Warn("Win key hook unavailable"); return; }
        while (Win32.GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            Win32.TranslateMessage(ref msg);
            Win32.DispatchMessageW(ref msg);
        }
        Win32.UnhookWindowsHookEx(_hook);
    }

    private IntPtr Hook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var k = Marshal.PtrToStructure<Win32.KBDLLHOOKSTRUCT>(lParam);
            bool injected = (k.flags & 0x10) != 0;
            bool isWin = k.vkCode is 0x5B or 0x5C;
            int m = (int)wParam;
            bool down = m is 0x100 or 0x104, up = m is 0x101 or 0x105;
            if (!injected)
            {
                if (isWin && down) { if (!_winDown) { _winDown = true; _otherPressed = false; } }
                else if (!isWin && down && _winDown) _otherPressed = true;
                else if (isWin && up && _winDown)
                {
                    _winDown = false;
                    if (!_otherPressed)
                    {
                        // Insert an unassigned key so Windows sees a "combination" and does not open Start.
                        var inputs = new Win32.INPUT[2];
                        inputs[0].type = inputs[1].type = Win32.INPUT_KEYBOARD;
                        inputs[0].u.ki.wVk = inputs[1].u.ki.wVk = 0xE8;
                        inputs[1].u.ki.dwFlags = Win32.KEYEVENTF_KEYUP;
                        Win32.SendInput(2, inputs, Marshal.SizeOf<Win32.INPUT>());
                        ThreadPool.QueueUserWorkItem(_ => Tapped?.Invoke());
                    }
                }
            }
        }
        return Win32.CallNextHookEx(_hook, code, wParam, lParam);
    }

    public void Dispose()
    {
        if (_threadId != 0) Win32.PostThreadMessageW(_threadId, 0x0012 /*WM_QUIT*/, IntPtr.Zero, IntPtr.Zero);
        _thread = null;
    }
}

public static class KeyboardSender
{
    /// <summary>Sends Ctrl+V to the foreground window (used by clipboard history after restoring a clip).</summary>
    public static void Paste() => Chord(0x11, 0x56);

    public static void Chord(params ushort[] keys)
    {
        var list = new List<Win32.INPUT>();
        foreach (var k in keys) list.Add(Key(k, false));
        foreach (var k in keys.Reverse()) list.Add(Key(k, true));
        Win32.SendInput((uint)list.Count, list.ToArray(), Marshal.SizeOf<Win32.INPUT>());
    }

    public static void MediaKey(ushort vk) => Chord(vk);

    private static Win32.INPUT Key(ushort vk, bool up) =>
        new() { type = Win32.INPUT_KEYBOARD, u = new Win32.INPUTUNION { ki = new Win32.KEYBDINPUT { wVk = vk, dwFlags = up ? Win32.KEYEVENTF_KEYUP : 0 } } };
}
