using System.Diagnostics;
using Microsoft.UI.Xaml;
using VOS.Core;
using VOS.Core.Ipc;
using VOS.Core.Notifications;
using VOS.Core.Settings;
using VOS.Core.Timers;
using VOS.Platform.Audio;
using VOS.Platform.Clipboard;
using VOS.Platform.Desktop;
using VOS.Platform.Input;
using VOS.Platform.Media;
using VOS.Platform.Native;
using VOS.Platform.Sys;
using VOS.Platform.Windowing;
using VOS.UI;
using VOS.UI.Quality;
using VOS.UI.Theme;

namespace VOS.Shell;

/// <summary>Composition root of the desktop shell. Owns services and surfaces; everything else asks it.</summary>
public sealed class ShellHost
{
    public static ShellHost Instance { get; private set; } = null!;

    public SettingsStore Settings { get; } = SettingsStore.Instance;
    public MessageWindow Messages { get; private set; } = null!;
    public HotkeyManager Hotkeys { get; private set; } = null!;
    public WindowTracker Tracker { get; } = new();
    public MediaSessionService Media { get; } = new();
    public AudioService Audio { get; private set; } = null!;
    public SystemStatusService Status { get; } = new();
    public TimerStore Timers { get; } = new();
    public NotificationCenter Notifications { get; } = new();
    public ActivityManager Activities { get; } = new();
    public QualityGovernor Quality { get; } = new();
    public WeatherWatcher Weather { get; } = new();
    public CommandRunner Commands { get; }
    public WorkspaceService Workspaces { get; }
    public ClipboardMonitor? Clipboard { get; private set; }
    public ClipboardHistory? ClipboardHistory { get; private set; }

    public TopBar Bar { get; private set; } = null!;
    public PillSurface Pill { get; private set; } = null!;
    public DockSurface Dock { get; private set; } = null!;
    public DesktopSurface? Desktop { get; private set; }
    public Panels Panels { get; private set; } = null!;

    private IpcServer? _ipc;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _warmup;
    private WinKeyInterceptor? _winKey;
    private Process? _watchdog;
    private bool _focusOn;
    private ShellMode _appliedMode = ShellMode.Companion;

    public bool FocusOn => _focusOn || (Settings.Current.Notifications.FocusDuringGames && Quality.FullscreenActive) || InFocusSchedule();
    public event Action? FocusChanged;

    private ShellHost()
    {
        Commands = new CommandRunner(this);
        Workspaces = new WorkspaceService(this);
    }

    public static void Start(string[] args)
    {
        if (IpcClient.IsListening(IpcNames.Shell))
        {
            // Second launch: ask the running shell to open the command panel, then leave.
            _ = IpcClient.SendAsync(IpcNames.Shell, IpcMessage.Create(ShellMessages.Command, "show"));
            Application.Current.Exit();
            return;
        }
        Instance = new ShellHost();
        Instance.Run(args);
    }

    private void Run(string[] args)
    {
        var sw = Stopwatch.StartNew();
        try { File.Delete(Watchdog.CleanExitMarker); } catch { }
        Settings.EnsureFile();
        Settings.Watch();
        Settings.Changed += s => UiThread.Post(() => OnSettingsChanged(s));
        _focusOn = Settings.Current.Notifications.Focus;

        Messages = new MessageWindow("V-OS Shell");
        Messages.Message += OnBroadcast;
        Status.Start();

        try { Audio = new AudioService(); } catch (Exception ex) { Log.Warn("Audio service unavailable", ex); Audio = null!; }
        Tracker.IgnoreProcess(Environment.ProcessId);
        Tracker.Start();
        Tracker.ForegroundChanged += _ => UiThread.Post(() => { Quality.CheckFullscreen(); Bar?.OnForegroundChanged(); Dock?.OnForegroundChanged(); });
        _ = Media.StartAsync();
        Timers.Watch();
        Weather.Start();
        Notifications.FocusActive = () => FocusOn;
        Notifications.Load(Settings.Current.Notifications.HistoryDays);

        Bar = new TopBar(this);
        // Probe GPU capabilities once a compositor exists on this thread (they read as "not fast" before that).
        Quality.Start(Status);
        Quality.FullscreenChanged += fs => UiThread.Post(() => { FocusChanged?.Invoke(); Desktop?.SetPaused(fs); });
        Bar.FullscreenApp += OnFullscreenApp;
        Pill = new PillSurface(this);
        Dock = new DockSurface(this);
        Panels = new Panels(this);
        Bar.Show();
        Pill.Show();
        Dock.Show();

        StartClipboard();
        RegisterHotkeys();
        StartIpc();
        ApplyMode(Settings.Current.Shell.Mode);
        Log.Info("Shell ready", new { ms = sw.ElapsedMilliseconds, quality = VisualQuality.Current.Profile.ToString() });
        // Idle-time warm-up: the panels opened most often are built and rendered once, invisibly.
        _warmup = UiThread.Timer(TimeSpan.FromSeconds(2), () => Panels.Prewarm(PanelKind.Command, PanelKind.Control), repeating: false);
        _warmup.Start();

        if (!Settings.Current.OnboardingCompleted && !args.Contains("--no-onboarding")) Onboarding.Show(this);
        if (args.Contains("--after-crash"))
            Notifications.Post(new VosNotification { AppName = "V-OS", Title = "V-OS restarted", Body = "The shell stopped unexpectedly and was restarted. Your Windows desktop was restored in the meantime.", Glyph = Glyphs.Info });
    }

    private void OnSettingsChanged(VosSettings s)
    {
        ThemeService.Current.ApplySettings(s.Appearance, WindowsTheme.AppsUseLightTheme, WindowsTheme.AccentHex(), Desktop?.WallpaperAccentHex);
        Quality.Recompute();
        if (s.Shell.Mode != _appliedMode) ApplyMode(s.Shell.Mode);
        _focusOn = s.Notifications.Focus;
        FocusChanged?.Invoke();
        Bar.Refresh();
        Dock.Rebuild();
        Desktop?.ApplySettings();
        RegisterHotkeys();
        if (Clipboard != null) Clipboard.CaptureImages = s.Clipboard.CaptureImages;
    }

    private bool OnBroadcast(uint msg, IntPtr w, IntPtr l)
    {
        switch (msg)
        {
            case Win32.WM_SETTINGCHANGE:
                UiThread.Post(() =>
                {
                    Quality.RefreshWindowsPreferences();
                    ThemeService.Current.ApplySettings(Settings.Current.Appearance, WindowsTheme.AppsUseLightTheme, WindowsTheme.AccentHex(), Desktop?.WallpaperAccentHex);
                });
                break;
            case Win32.WM_DISPLAYCHANGE:
                UiThread.Post(() => { Bar.Reposition(); Pill.Reposition(); Dock.Reposition(); Desktop?.Reposition(); Quality.RefreshWindowsPreferences(); });
                break;
            case Win32.WM_WTSSESSION_CHANGE:
                int code = (int)w; // 7 = lock, 8 = unlock
                if (code is 7 or 8) UiThread.Post(() => Desktop?.SetPaused(code == 7));
                break;
        }
        return false;
    }

    private void RegisterHotkeys()
    {
        Hotkeys ??= new HotkeyManager(Messages);
        Hotkeys.UnregisterAll();
        foreach (var b in Settings.Current.Hotkeys.Bindings.Where(b => b.Enabled)) Hotkeys.Register(b.Action, b.Gesture);
        try { AtomicFile.WriteAllText(Path.Combine(VosPaths.State, "hotkeys-active.json"), VosJson.Serialize(Hotkeys.Active)); } catch { }
        Hotkeys.Pressed -= OnHotkey;
        Hotkeys.Pressed += OnHotkey;
    }

    private void OnHotkey(string action)
    {
        switch (action)
        {
            case "command": Panels.Toggle(PanelKind.Command); break;
            case "clipboard": Panels.Toggle(PanelKind.Clipboard); break;
            case "control": Panels.Toggle(PanelKind.Control); break;
            case "activity": Panels.Toggle(PanelKind.Activity); break;
            case "switcher": Panels.Toggle(PanelKind.Switcher); break;
            case "drop": Panels.Toggle(PanelKind.Drop); break;
            case "layout": Panels.Toggle(PanelKind.Layout); break;
            case "capture.region": Commands.OpenApp("capture", "mode=region"); break;
            case "capture.screen": Commands.OpenApp("capture", "mode=screen"); break;
            case "capture.window": Commands.OpenApp("capture", "mode=window"); break;
            case "translate.clipboard": Commands.OpenApp("translate", "clipboard=1"); break;
            case "lock": LockPresentation.Lock(this); break;
            case "recovery": RecoverWindowsDesktop(); break;
        }
    }

    private void StartClipboard()
    {
        try
        {
            ClipboardHistory = new ClipboardHistory();
            Clipboard = new ClipboardMonitor(Messages)
            {
                CaptureImages = Settings.Current.Clipboard.CaptureImages,
                IsExcludedApp = exe => exe != null && Settings.Current.Clipboard.ExcludedApps?.Contains(Path.GetFileName(exe), StringComparer.OrdinalIgnoreCase) == true,
            };
            Clipboard.Captured += c => { if (Settings.Current.Clipboard.Enabled) ClipboardHistory.Add(c); };
            Clipboard.Start();
        }
        catch (Exception ex) { Log.Warn("Clipboard history unavailable", ex); }
    }

    private void StartIpc()
    {
        _ipc = new IpcServer(IpcNames.Shell, async m =>
        {
            switch (m.Type)
            {
                case ShellMessages.Ping: return IpcMessage.Create("pong");
                case ShellMessages.Notify:
                    if (m.Read<VosNotification>() is { } n) UiThread.Post(() => Notifications.Post(n));
                    return null;
                case ShellMessages.ActivityUpdate:
                    if (m.Read<LiveActivity>() is { } a) UiThread.Post(() => Activities.Update(a));
                    return null;
                case ShellMessages.ActivityEnd:
                    if (m.Read<string>() is { } id) UiThread.Post(() => Activities.End(id));
                    return null;
                case ShellMessages.Command:
                    var cmd = m.Read<string>() ?? "";
                    if (cmd == "show") UiThread.Post(() => Panels.Show(PanelKind.Command));
                    else UiThread.Post(() => _ = Commands.RunAsync(cmd, Panels.AnyXamlRoot));
                    return null;
                case ShellMessages.Shutdown:
                    UiThread.Post(() => ExitShell(restoreDesktop: true));
                    return IpcMessage.Create("ok");
                case ShellMessages.Diagnostics:
                    return IpcMessage.Create("diag", ProcessDiagnostics.Capture());
                case ShellMessages.Action:
                    if (m.Read<string>() is not { Length: > 0 } action) return IpcMessage.Create("error", "missing action");
                    UiThread.Post(() => OnHotkey(action));
                    return IpcMessage.Create("ok");
            }
            await Task.CompletedTask;
            return null;
        });
        _ipc.Start();
    }

    /// <summary>Companion (overlay on Explorer) or immersive (taskbar and desktop icons hidden, V-OS desktop, Win key).</summary>
    public void ApplyMode(ShellMode mode)
    {
        var s = Settings.Current.Shell;
        if (mode == ShellMode.Immersive)
        {
            StartWatchdog();
            DesktopMode.Enter(s.HideTaskbarInImmersive, autoHideTaskbar: true, s.HideDesktopIconsInImmersive);
            Desktop ??= new DesktopSurface(this);
            Desktop.Show();
            if (s.WinKeyOpensLauncher)
            {
                _winKey ??= new WinKeyInterceptor();
                _winKey.Tapped -= OnWinKey;
                _winKey.Tapped += OnWinKey;
                _winKey.Start();
            }
        }
        else
        {
            _winKey?.Dispose();
            _winKey = null;
            if (s.AutoHideTaskbar) { StartWatchdog(); DesktopMode.Enter(hideTaskbarWindow: false, autoHideTaskbar: true, hideIcons: false); }
            else if (DesktopMode.ReadRecord() is { Active: true }) DesktopMode.Restore();
            Desktop?.Hide();
        }
        _appliedMode = mode;
        Dock.Reposition();
    }

    private void OnWinKey() => UiThread.Post(() => Panels.Toggle(PanelKind.Command));

    /// <summary>Fullscreen games, videos and presentations get the whole screen: V-OS surfaces step aside.</summary>
    private void OnFullscreenApp(bool fullscreen)
    {
        if (fullscreen) { Pill.Hide(); Dock.Hide(); Bar.Hide(); Desktop?.SetPaused(true); }
        else { Bar.Show(); Pill.Show(); Dock.Show(); Desktop?.SetPaused(false); }
        Quality.CheckFullscreen();
    }

    private void StartWatchdog()
    {
        if (_watchdog is { HasExited: false }) return;
        try
        {
            var psi = new ProcessStartInfo(CommandRunner.VosExe) { UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("--watchdog");
            psi.ArgumentList.Add(Environment.ProcessId.ToString());
            _watchdog = Process.Start(psi);
        }
        catch (Exception ex) { Log.Warn("Watchdog could not start", ex); }
    }

    public void SetFocus(bool on)
    {
        if (Settings.Current.Notifications.Focus != on) Settings.Update(s => s.Notifications.Focus = on);
        _focusOn = on;
        FocusChanged?.Invoke();
    }

    private bool InFocusSchedule()
    {
        var n = Settings.Current.Notifications;
        if (!n.FocusOnSchedule || !TimeSpan.TryParse(n.FocusStart, out var start) || !TimeSpan.TryParse(n.FocusEnd, out var end)) return false;
        var now = DateTime.Now.TimeOfDay;
        return start <= end ? now >= start && now < end : now >= start || now < end;
    }

    /// <summary>Emergency path (Ctrl+Alt+Shift+F12 by default): give the user their Windows desktop back immediately.</summary>
    public void RecoverWindowsDesktop()
    {
        Log.Warn("Recovery hotkey pressed");
        Settings.Update(s => s.Shell.Mode = ShellMode.Companion);
        ApplyMode(ShellMode.Companion);
        DesktopMode.Restore();
        Notifications.Post(new VosNotification { AppName = "V-OS", Title = "Windows desktop restored", Body = "Immersive mode is off. The taskbar and desktop icons are back.", Glyph = Glyphs.Desktop, Priority = NotificationPriority.High });
    }

    public void PrepareForSessionEnd()
    {
        try { File.WriteAllText(Watchdog.CleanExitMarker, DateTime.Now.ToString("o")); } catch { }
        DesktopMode.Restore();
        Log.Flush();
    }

    public void ExitShell(bool restoreDesktop)
    {
        Log.Info("Shell exiting", new { restoreDesktop });
        try { File.WriteAllText(Watchdog.CleanExitMarker, DateTime.Now.ToString("o")); } catch { }
        _winKey?.Dispose();
        if (restoreDesktop) DesktopMode.Restore();
        Bar?.Dispose();
        Hotkeys?.Dispose();
        Clipboard?.Dispose();
        Tracker.Dispose();
        _ipc?.Dispose();
        Log.Flush();
        Application.Current.Exit();
        // Safety net: the desktop is already restored and the clean-exit marker written; never linger.
        new Thread(() => { Thread.Sleep(3000); Environment.Exit(0); }) { IsBackground = true }.Start();
    }
}
