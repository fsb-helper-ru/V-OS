using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using VOS.Core;
using VOS.Core.Data;
using VOS.Core.Notifications;
using VOS.Core.Perf;
using VOS.Core.Search;
using VOS.Core.Settings;
using VOS.Core.Weather;
using VOS.Platform.Shell;
using VOS.Platform.Sys;
using VOS.Platform.Windowing;
using VOS.UI;
using VOS.UI.Quality;

namespace VOS.Shell;

/// <summary>Unified notification center: history in SQLite, per-app mute, Focus rules, banners through the capsule.</summary>
public sealed class NotificationCenter
{
    private readonly Db _db = Db.ForModule("notifications");
    private readonly List<VosNotification> _items = [];
    private readonly object _gate = new();

    public event Action<VosNotification>? Banner;
    public event Action? Changed;
    public Func<bool> FocusActive { get; set; } = () => false;

    public NotificationCenter()
    {
        _db.Migrate("CREATE TABLE notifications(id TEXT PRIMARY KEY, app_id TEXT, app_name TEXT, title TEXT, body TEXT, glyph TEXT, priority INTEGER, time TEXT, command TEXT, read INTEGER DEFAULT 0);" +
                    "CREATE INDEX idx_notifications_time ON notifications(time);");
    }

    public void Load(int days)
    {
        var cutoff = DateTimeOffset.Now.AddDays(-Math.Max(1, days)).ToString("o", CultureInfo.InvariantCulture);
        _db.Execute("DELETE FROM notifications WHERE time < $c", ("$c", cutoff));
        var rows = _db.Query("SELECT * FROM notifications ORDER BY time DESC LIMIT 300", r => new VosNotification
        {
            Id = Db.Str(r, "id")!, AppId = Db.Str(r, "app_id") ?? "vos", AppName = Db.Str(r, "app_name") ?? "V-OS",
            Title = Db.Str(r, "title") ?? "", Body = Db.Str(r, "body") ?? "", Glyph = Db.Str(r, "glyph") ?? "",
            Priority = (NotificationPriority)Db.Long(r, "priority"), Command = Db.Str(r, "command"), Read = Db.Bool(r, "read"),
            Time = DateTimeOffset.Parse(Db.Str(r, "time")!, CultureInfo.InvariantCulture),
        });
        lock (_gate) { _items.Clear(); _items.AddRange(rows); }
    }

    public IReadOnlyList<VosNotification> Items { get { lock (_gate) return _items.ToList(); } }
    public int Unread { get { lock (_gate) return _items.Count(n => !n.Read); } }

    public void Post(VosNotification n)
    {
        var s = SettingsStore.Instance.Current.Notifications;
        bool muted = s.MutedApps?.Contains(n.AppId, StringComparer.OrdinalIgnoreCase) == true;
        bool focusBlocks = FocusActive() && n.Priority < NotificationPriority.High && s.FocusAllowedApps?.Contains(n.AppId, StringComparer.OrdinalIgnoreCase) != true;
        lock (_gate) { _items.Insert(0, n); if (_items.Count > 300) _items.RemoveAt(_items.Count - 1); }
        try
        {
            _db.Execute("INSERT OR REPLACE INTO notifications(id,app_id,app_name,title,body,glyph,priority,time,command,read) VALUES($i,$a,$an,$t,$b,$g,$p,$tm,$c,0)",
                ("$i", n.Id), ("$a", n.AppId), ("$an", n.AppName), ("$t", n.Title), ("$b", n.Body), ("$g", n.Glyph), ("$p", (int)n.Priority),
                ("$tm", n.Time.ToString("o", CultureInfo.InvariantCulture)), ("$c", n.Command));
        }
        catch (Exception ex) { Log.Warn("Could not store notification", ex); }
        Changed?.Invoke();
        if (!muted && !focusBlocks && !n.Silent && s.ShowBanners) Banner?.Invoke(n);
    }

    public void MarkAllRead()
    {
        lock (_gate) foreach (var n in _items) n.Read = true;
        _db.Execute("UPDATE notifications SET read=1");
        Changed?.Invoke();
    }

    public void Dismiss(string id)
    {
        lock (_gate) _items.RemoveAll(n => n.Id == id);
        _db.Execute("DELETE FROM notifications WHERE id=$i", ("$i", id));
        Changed?.Invoke();
    }

    public void ClearAll()
    {
        lock (_gate) _items.Clear();
        _db.Execute("DELETE FROM notifications");
        Changed?.Invoke();
    }
}

/// <summary>Live activities (downloads, transfers, recordings...) reported by apps; the capsule shows the most relevant one.</summary>
public sealed class ActivityManager
{
    private readonly Dictionary<string, LiveActivity> _items = [];
    public event Action? Changed;
    public IReadOnlyList<LiveActivity> Items { get { lock (_items) return _items.Values.OrderByDescending(a => a.Priority).ThenByDescending(a => a.StartedAt).ToList(); } }

    public void Update(LiveActivity a) { lock (_items) _items[a.Id] = a; Changed?.Invoke(); }
    public void End(string id) { bool removed; lock (_items) removed = _items.Remove(id); if (removed) Changed?.Invoke(); }
}

/// <summary>Recomputes <see cref="EffectiveQuality"/> when power, Windows preferences, the profile or the workspace change.</summary>
public sealed class QualityGovernor
{
    private HardwareSnapshot _hw = new();
    public HardwareSnapshot Hardware => _hw;
    public event Action<bool>? FullscreenChanged;
    public bool FullscreenActive { get; private set; }

    public void Start(SystemStatusService status)
    {
        var (supported, fast) = VisualQuality.Capabilities();
        _hw = HardwareProbe.Probe(supported, fast);
        Log.Info("Hardware profile", new { _hw.GpuName, _hw.GpuDedicatedMb, _hw.LogicalCores, ram = Math.Round(_hw.RamGb, 1), _hw.EffectsFast, tier = QualityResolver.Classify(_hw).ToString() });
        status.BatteryChanged += b => UiThread.Post(() => { _hw = _hw with { OnBattery = b.Present && !b.OnAc, BatterySaver = b.Saver }; Recompute(); });
        Recompute();
    }

    /// <summary>Re-reads Windows animation/transparency/contrast preferences (call on WM_SETTINGCHANGE).</summary>
    public void RefreshWindowsPreferences()
    {
        var fresh = HardwareProbe.Probe(_hw.EffectsSupported, _hw.EffectsFast);
        _hw = _hw with { WindowsAnimations = fresh.WindowsAnimations, WindowsTransparency = fresh.WindowsTransparency, HighContrast = fresh.HighContrast, MonitorCount = fresh.MonitorCount, TotalPixels = fresh.TotalPixels };
        Recompute();
    }

    public void Recompute()
    {
        var s = SettingsStore.Instance.Current;
        var profile = s.ActiveWorkspace?.Profile ?? s.Performance.Profile;
        if (FullscreenActive && s.Gaming.MinimalEffectsWhilePlaying) profile = PerformanceProfile.Minimal;
        var q = QualityResolver.Resolve(profile, _hw, s.Performance, s.Appearance.ReduceMotion || !s.Appearance.Transparency && false);
        if (!s.Appearance.Transparency) q = q with { Glass = false, Mica = false };
        VisualQuality.Set(q);
        try { AtomicFile.WriteAllText(Path.Combine(VosPaths.State, "quality.json"), VosJson.Serialize(q)); } catch { }
    }

    /// <summary>Called on foreground changes: fullscreen apps (games, video) pause effects and may enable Focus.</summary>
    public void CheckFullscreen()
    {
        bool fs = FullscreenState.ForegroundCoversMonitor() || FullscreenState.IsFullscreenActive();
        if (fs == FullscreenActive) return;
        FullscreenActive = fs;
        Recompute();
        FullscreenChanged?.Invoke(fs);
    }
}

/// <summary>Weather for the selected location, refreshed at the quality-dependent interval. Offline-tolerant.</summary>
public sealed class WeatherWatcher
{
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _timer;
    public WeatherReport? Current { get; private set; }
    public event Action? Changed;

    public void Start()
    {
        var loc = SettingsStore.Instance.Current.Weather.Selected;
        if (loc != null) Current = WeatherService.Shared.ReadCache(loc);
        _timer = UiThread.Timer(TimeSpan.FromMinutes(5), () => _ = RefreshAsync(false));
        _timer.Start();
        _ = RefreshAsync(false);
    }

    public async Task RefreshAsync(bool force)
    {
        var s = SettingsStore.Instance.Current;
        var loc = s.Weather.Selected;
        if (!s.Weather.Enabled || !s.Privacy.WeatherNetworkConsent || loc == null) { if (Current != null) { Current = null; Changed?.Invoke(); } return; }
        var maxAge = TimeSpan.FromMinutes(Math.Max(10, Math.Min(s.Weather.RefreshMinutes, VisualQuality.Current.WeatherRefreshMinutes)));
        var r = await WeatherService.Shared.GetAsync(loc, s.Weather, maxAge, force);
        if (r != null && (Current == null || r.FetchedAt != Current.FetchedAt || r.Location.Key != Current.Location.Key)) { Current = r; Changed?.Invoke(); }
    }
}

/// <summary>Runs commands from the command palette, notifications, the assistant and flows. Dangerous actions are confirmed.</summary>
public sealed class CommandRunner(ShellHost host)
{
    public static string VosExe => Path.Combine(AppContext.BaseDirectory, "VOS.exe");

    public void OpenApp(string id, string? args = null)
    {
        try
        {
            var psi = new ProcessStartInfo(VosExe) { UseShellExecute = false };
            psi.ArgumentList.Add("--app");
            psi.ArgumentList.Add(id);
            if (!string.IsNullOrEmpty(args)) foreach (var a in SplitArgs(args)) psi.ArgumentList.Add(a);
            Process.Start(psi);
        }
        catch (Exception ex) { Log.Error($"Could not open {id}", ex); }
    }

    private static IEnumerable<string> SplitArgs(string s) => s.Split('\u001F', StringSplitOptions.RemoveEmptyEntries);
    public static string JoinArgs(params string[] a) => string.Join('\u001F', a);

    /// <summary>Command strings: "app:files", "app:files|path=C:\", "url:https://..", "file:C:\..", "launch:&lt;shell path&gt;", "theme:dark" ...</summary>
    public async Task<bool> RunAsync(string command, XamlRoot? confirmRoot = null)
    {
        var colon = command.IndexOf(':');
        if (colon <= 0) return false;
        var verb = command[..colon].ToLowerInvariant();
        var arg = command[(colon + 1)..];
        switch (verb)
        {
            case "app":
                var parts = arg.Split('|', 2);
                OpenApp(parts[0], parts.Length > 1 ? parts[1] : null);
                return true;
            case "launch": return ShellOps.Launch(arg);
            case "url": return ShellOps.OpenUrl(arg);
            case "file": case "folder": return ShellOps.Launch(arg);
            case "reveal": ShellOps.ShowInExplorer(arg); return true;
            case "ms-settings": ShellOps.OpenWindowsSettings(arg); return true;
            case "settings": OpenApp("settings", arg); return true;
            case "workspace": host.Workspaces.Switch(arg); return true;
            case "theme": SetTheme(arg); return true;
            case "clipboard":
                if (arg == "clear") host.ClipboardHistory?.ClearUnpinned();
                return true;
            case "focus": host.SetFocus(arg switch { "on" => true, "off" => false, _ => !host.FocusOn }); return true;
            case "timer":
                var t = arg.Split(':', 2);
                if (int.TryParse(t[0], out var secs)) host.Timers.Start(TimeSpan.FromSeconds(secs), t.Length > 1 ? t[1] : null);
                return true;
            case "media":
                switch (arg) { case "play": await host.Media.PlayAsync(); break; case "pause": await host.Media.PauseAsync(); break; case "next": await host.Media.NextAsync(); break; case "previous": await host.Media.PreviousAsync(); break; default: await host.Media.PlayPauseAsync(); break; }
                return true;
            case "volume":
                if (arg == "mute") host.Audio.Muted = true;
                else if (arg == "unmute") host.Audio.Muted = false;
                else if (int.TryParse(arg, out var v)) { host.Audio.Muted = false; host.Audio.Volume = v / 100f; }
                return true;
            case "perf":
                if (Enum.TryParse<PerformanceProfile>(arg, true, out var p)) SettingsStore.Instance.Update(s => s.Performance.Profile = p);
                return true;
            case "system": return await SystemAsync(arg, confirmRoot);
        }
        return false;
    }

    public async Task<bool> RunIntentAsync(CommandIntent i, XamlRoot? confirmRoot)
    {
        switch (i.Kind)
        {
            case IntentKind.Url: return ShellOps.OpenUrl(i.Arg("url"));
            case IntentKind.Path: return ShellOps.Launch(Environment.ExpandEnvironmentVariables(i.Arg("path")));
            case IntentKind.Theme: SetTheme(i.Arg("mode")); return true;
            case IntentKind.Workspace: host.Workspaces.Switch(i.Arg("name")); return true;
            case IntentKind.Timer: host.Timers.Start(TimeSpan.FromSeconds(int.Parse(i.Arg("seconds"), CultureInfo.InvariantCulture)), i.Arg("label")); return true;
            case IntentKind.System: return await SystemAsync(i.Arg("action"), confirmRoot);
            case IntentKind.Volume:
                if (i.Args.ContainsKey("mute")) host.Audio.Muted = i.Arg("mute") == "true";
                else if (i.Args.ContainsKey("step")) host.Audio.Volume = Math.Clamp(host.Audio.Volume + int.Parse(i.Arg("step"), CultureInfo.InvariantCulture) / 100f, 0, 1);
                else { host.Audio.Muted = false; host.Audio.Volume = Math.Clamp(int.Parse(i.Arg("level"), CultureInfo.InvariantCulture), 0, 100) / 100f; }
                return true;
            case IntentKind.Brightness: return Brightness.Set(int.Parse(i.Arg("level"), CultureInfo.InvariantCulture));
            case IntentKind.Focus: host.SetFocus(i.Arg("on") == "true"); return true;
            case IntentKind.Media: return await RunAsync("media:" + i.Arg("action"));
            case IntentKind.Weather: OpenApp("weather", i.Arg("city").Length > 0 ? "city=" + i.Arg("city") : null); return true;
            case IntentKind.Translate: OpenApp("translate", JoinArgs("text=" + i.Arg("text"), "to=" + i.Arg("lang"))); return true;
            case IntentKind.FindLatestDownload:
                var f = ShellOps.LatestDownload();
                if (f != null) OpenApp("files", JoinArgs("select=" + f.FullName)); else OpenApp("files", "path=" + ShellNative.Downloads);
                return true;
            case IntentKind.FindFiles: OpenApp("files", "search=" + i.Arg("query")); return true;
            case IntentKind.OpenModule: OpenApp(i.Arg("module"), i.Arg("section").Length > 0 ? "section=" + i.Arg("section") : null); return true;
            case IntentKind.Performance: return await RunAsync("perf:" + i.Arg("profile"));
            case IntentKind.Settings: OpenApp("settings", i.Arg("section")); return true;
            case IntentKind.Note: OpenApp("notes", "new=" + i.Arg("text")); return true;
            case IntentKind.WebSearch: return ShellOps.OpenUrl(SearchUrl(i.Arg("query")));
            case IntentKind.Calculate or IntentKind.Convert:
                host.Clipboard?.SetText(i.Title);
                return true;
        }
        return false;
    }

    public static string SearchUrl(string q)
    {
        var engine = SettingsStore.Instance.Current.Browser.SearchEngine;
        var e = Uri.EscapeDataString(q);
        return engine switch
        {
            "google" => "https://www.google.com/search?q=" + e,
            "bing" => "https://www.bing.com/search?q=" + e,
            "brave" => "https://search.brave.com/search?q=" + e,
            "startpage" => "https://www.startpage.com/do/search?q=" + e,
            _ => "https://duckduckgo.com/?q=" + e,
        };
    }

    private static void SetTheme(string mode) => SettingsStore.Instance.Update(s =>
    {
        bool currentlyDark = VOS.UI.Theme.ThemeService.Current.IsDark;
        s.Appearance.Theme = mode switch { "dark" => ThemeMode.Dark, "light" => ThemeMode.Light, "auto" => ThemeMode.Auto, _ => currentlyDark ? ThemeMode.Light : ThemeMode.Dark };
        if (s.Appearance.SyncWindowsTheme) WindowsTheme.SetAppsTheme(s.Appearance.Theme == ThemeMode.Light);
    });

    private async Task<bool> SystemAsync(string action, XamlRoot? root)
    {
        (string title, string body, string verb)? confirm = action switch
        {
            "sleep" => ("Put this PC to sleep?", "Unsaved work stays open.", "Sleep"),
            "restart" => ("Restart this PC?", "Open apps will close. Save your work first.", "Restart"),
            "shutdown" => ("Shut down this PC?", "Open apps will close. Save your work first.", "Shut down"),
            "signout" => ("Sign out?", "Open apps will close. Save your work first.", "Sign out"),
            "emptybin" => ("Empty the Recycle Bin?", $"{ShellOps.RecycleBinInfo().items} items will be permanently deleted. This cannot be undone.", "Empty Recycle Bin"),
            _ => null,
        };
        if (confirm is { } c)
        {
            if (root == null) return false;
            if (!await Dialogs.ConfirmAsync(root, c.title, c.body, c.verb, destructive: true)) return false;
        }
        switch (action)
        {
            case "lock": ShellOps.Lock(); break;
            case "sleep": ShellOps.Sleep(); break;
            case "restart": host.PrepareForSessionEnd(); ShellOps.Restart(); break;
            case "shutdown": host.PrepareForSessionEnd(); ShellOps.ShutDown(); break;
            case "signout": host.PrepareForSessionEnd(); ShellOps.SignOut(); break;
            case "emptybin": ShellOps.EmptyRecycleBin(); break;
            default: return false;
        }
        return true;
    }
}

/// <summary>Workspaces: switch context (theme, accent, wallpaper, dock, profile, focus) and restore window layouts.</summary>
public sealed class WorkspaceService(ShellHost host)
{
    public Workspace? Active => SettingsStore.Instance.Current.ActiveWorkspace;

    public void Switch(string idOrName)
    {
        var s = SettingsStore.Instance.Current;
        var target = s.Workspaces.FirstOrDefault(w => w.Id.Equals(idOrName, StringComparison.OrdinalIgnoreCase) || w.Name.Equals(idOrName, StringComparison.OrdinalIgnoreCase));
        if (target == null || target.Id == s.ActiveWorkspaceId) return;
        var current = s.ActiveWorkspace;
        if (current != null && current.LaunchApps) Capture(current.Id);
        SettingsStore.Instance.Update(x =>
        {
            x.ActiveWorkspaceId = target.Id;
            if (target.Accent != null) { x.Appearance.AccentSource = AccentSource.Preset; x.Appearance.AccentPreset = target.Accent; }
            if (target.Theme is ThemeMode t) x.Appearance.Theme = t;
            if (target.Wallpaper != null) x.Appearance.Wallpaper = target.Wallpaper;
        });
        host.SetFocus(target.Focus);
        if (target.HideOtherWindows) MinimizeOthers(target);
        if (target.LaunchApps) _ = RestoreAsync(target);
        host.Notifications.Post(new VosNotification { AppId = "workspaces", AppName = "Workspaces", Title = $"{target.Name} workspace", Body = "Your environment has been switched.", Glyph = Glyphs.Layers, Silent = false, Priority = NotificationPriority.Low });
    }

    /// <summary>Records open windows (exe, placement) into the workspace so they can be restored later.</summary>
    public void Capture(string workspaceId)
    {
        var windows = host.Tracker.Windows.Where(w => w.ExePath != null).Select(w =>
        {
            var wp = new VOS.Platform.Native.Win32.WINDOWPLACEMENT { length = Marshal.SizeOf<VOS.Platform.Native.Win32.WINDOWPLACEMENT>() };
            VOS.Platform.Native.Win32.GetWindowPlacement(w.Hwnd, ref wp);
            return new WorkspaceWindow
            {
                ExePath = w.ExePath!, AppUserModelId = w.AppId, Title = w.Title, ShowState = wp.showCmd,
                Left = wp.rcNormalPosition.Left, Top = wp.rcNormalPosition.Top, Width = wp.rcNormalPosition.Width, Height = wp.rcNormalPosition.Height,
            };
        }).ToList();
        SettingsStore.Instance.Update(x => { var ws = x.Workspaces.FirstOrDefault(w => w.Id == workspaceId); if (ws != null) ws.Windows = windows; });
    }

    private void MinimizeOthers(Workspace target)
    {
        var keep = target.Windows.Select(w => w.ExePath.ToLowerInvariant()).ToHashSet();
        foreach (var w in host.Tracker.Windows)
            if (w.ExePath == null || !keep.Contains(w.ExePath.ToLowerInvariant())) WindowTracker.Minimize(w.Hwnd);
    }

    private async Task RestoreAsync(Workspace target)
    {
        foreach (var saved in target.Windows)
        {
            var running = host.Tracker.Windows.FirstOrDefault(w => string.Equals(w.ExePath, saved.ExePath, StringComparison.OrdinalIgnoreCase));
            if (running == null)
            {
                if (!File.Exists(saved.ExePath)) continue;
                ShellOps.Launch(saved.ExePath, null, Path.GetDirectoryName(saved.ExePath));
                for (int i = 0; i < 20 && running == null; i++)
                {
                    await Task.Delay(250);
                    host.Tracker.Refresh();
                    running = host.Tracker.Windows.FirstOrDefault(w => string.Equals(w.ExePath, saved.ExePath, StringComparison.OrdinalIgnoreCase));
                }
            }
            if (running == null || saved.Width <= 0) continue;
            var wp = new VOS.Platform.Native.Win32.WINDOWPLACEMENT
            {
                length = Marshal.SizeOf<VOS.Platform.Native.Win32.WINDOWPLACEMENT>(),
                showCmd = saved.ShowState == 2 ? 2 : saved.ShowState == 3 ? 3 : 1,
                rcNormalPosition = new VOS.Platform.Native.Win32.RECT(saved.Left, saved.Top, saved.Left + saved.Width, saved.Top + saved.Height),
            };
            VOS.Platform.Native.Win32.SetWindowPlacement(running.Hwnd, ref wp);
        }
    }
}
