using System.Diagnostics;
using Microsoft.UI.Xaml;
using VOS.Core;
using VOS.Core.Ipc;
using VOS.Core.Notifications;
using VOS.Core.Settings;
using VOS.Platform.Sys;
using VOS.UI;
using VOS.UI.Quality;
using VOS.UI.Theme;

namespace VOS.Apps;

/// <summary>A V-OS application module hosted by <see cref="AppHost"/>.</summary>
public interface IAppModule
{
    AppFrame Frame { get; }
    /// <summary>Arguments such as path=..., select=..., open=... (repeatable), section=...</summary>
    void HandleArgs(AppArgs args);
}

public sealed class AppArgs
{
    private readonly List<(string key, string value)> _items = [];
    public static AppArgs Parse(IEnumerable<string> raw)
    {
        var a = new AppArgs();
        foreach (var token in raw.SelectMany(r => r.Split('\u001F', StringSplitOptions.RemoveEmptyEntries)))
        {
            int eq = token.IndexOf('=');
            if (eq > 0) a._items.Add((token[..eq], token[(eq + 1)..]));
            else if (token.Length > 0) a._items.Add(("section", token));
        }
        return a;
    }
    public string? Get(string key) => _items.LastOrDefault(i => i.key == key).value;
    public IEnumerable<string> All(string key) => _items.Where(i => i.key == key).Select(i => i.value);
    public bool IsEmpty => _items.Count == 0;
}

/// <summary>
/// Hosts app modules in one process (one UI thread, many windows): instant window opens, shared resources, low memory.
/// The browser runs in its own instance. Extra launches forward their request over IPC and exit.
/// </summary>
public static class AppHost
{
    private sealed class OpenRequest { public string Id { get; set; } = ""; public string[] Args { get; set; } = []; }

    private static readonly Dictionary<string, IAppModule> s_open = [];
    private static readonly Dictionary<string, Func<IAppModule>> s_factories = new(StringComparer.OrdinalIgnoreCase);
    private static IpcServer? s_ipc;
    private static bool s_isolatedBrowser;
    private static Microsoft.UI.Dispatching.DispatcherQueueTimer? s_idle;
    private static bool s_probedWithWindow;

    public static void Register(string id, Func<IAppModule> factory) => s_factories[id] = factory;

    public static void Start(string[] args)
    {
        if (args.Length == 0) { Application.Current.Exit(); return; }
        var id = args[0].ToLowerInvariant();
        var rest = args.Skip(1).Where(a => a != "--isolated").ToArray();
        s_isolatedBrowser = id is "browser" or "browser-private";
        var pipe = s_isolatedBrowser ? IpcNames.Isolated("browser") : IpcNames.Apps;
        if (IpcClient.IsListening(pipe))
        {
            IpcClient.SendAsync(pipe, IpcMessage.Create("open", new OpenRequest { Id = id, Args = rest })).Wait(2000);
            Application.Current.Exit();
            return;
        }
        Modules.RegisterAll();
        var settings = SettingsStore.Instance;
        settings.Watch();
        settings.Changed += s => UiThread.Post(() => ApplySettings(s));
        ApplySettings(settings.Current);
        s_ipc = new IpcServer(pipe, m =>
        {
            if (m.Type == "ping") return Task.FromResult<IpcMessage?>(IpcMessage.Create("pong"));
            if (m.Type == "open" && m.Read<OpenRequest>() is { } req) UiThread.Post(() => Open(req.Id, req.Args));
            return Task.FromResult<IpcMessage?>(null);
        });
        s_ipc.Start();
        s_idle = UiThread.Timer(TimeSpan.FromSeconds(90), () => { if (s_open.Count == 0) { Log.Info("App host idle; exiting"); Log.Flush(); Application.Current.Exit(); } }, repeating: false);
        Open(id, rest);
    }

    private static void ApplySettings(VosSettings s)
    {
        ThemeService.Current.ApplySettings(s.Appearance, WindowsTheme.AppsUseLightTheme, WindowsTheme.AccentHex());
        var (supported, fast) = VisualQuality.Capabilities();
        var hw = HardwareProbe.Probe(supported, fast);
        var q = VOS.Core.Perf.QualityResolver.Resolve(s.ActiveWorkspace?.Profile ?? s.Performance.Profile, hw, s.Performance, s.Appearance.ReduceMotion);
        if (!s.Appearance.Transparency) q = q with { Glass = false, Mica = false };
        VisualQuality.Set(q);
    }

    public static void Open(string id, string[] args)
    {
        id = id.ToLowerInvariant();
        if (id is "browser" or "browser-private" && !s_isolatedBrowser)
        {
            // Hand browser requests to the isolated browser process.
            var psi = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "VOS.exe")) { UseShellExecute = false };
            psi.ArgumentList.Add("--app"); psi.ArgumentList.Add(id);
            foreach (var a in args) psi.ArgumentList.Add(a);
            Process.Start(psi);
            return;
        }
        var parsed = AppArgs.Parse(args);
        if (s_open.TryGetValue(id, out var existing))
        {
            if (!parsed.IsEmpty) existing.HandleArgs(parsed);
            existing.Frame.Activate();
            return;
        }
        if (!s_factories.TryGetValue(id, out var factory))
        {
            Log.Warn($"Unknown app '{id}'");
            if (s_open.Count == 0) s_idle?.Start();
            return;
        }
        try
        {
            var sw = Stopwatch.StartNew();
            var module = factory();
            if (!s_probedWithWindow)
            {
                // GPU capabilities are only reported correctly once a window exists: re-resolve quality now,
                // before the window is shown, so it opens with the right materials (no solid-to-Mica switch).
                s_probedWithWindow = true;
                ApplySettings(SettingsStore.Instance.Current);
            }
            s_open[id] = module;
            module.Frame.Closed += () => { s_open.Remove(id); if (s_open.Count == 0) s_idle?.Start(); };
            module.HandleArgs(parsed);
            module.Frame.Activate();
            s_idle?.Stop();
            Log.Info("App opened", new { id, ms = sw.ElapsedMilliseconds });
        }
        catch (Exception ex)
        {
            CrashReporter.Report(ex, fatal: false);
            s_open.Remove(id);
        }
    }

    /// <summary>Sends a notification to the shell's activity center (no-op when the shell is not running).</summary>
    public static void Notify(VosNotification n) => _ = IpcClient.SendAsync(IpcNames.Shell, IpcMessage.Create(ShellMessages.Notify, n));
    public static void UpdateActivity(LiveActivity a) => _ = IpcClient.SendAsync(IpcNames.Shell, IpcMessage.Create(ShellMessages.ActivityUpdate, a));
    public static void EndActivity(string id) => _ = IpcClient.SendAsync(IpcNames.Shell, IpcMessage.Create(ShellMessages.ActivityEnd, id));
    public static void ShellCommand(string command) => _ = IpcClient.SendAsync(IpcNames.Shell, IpcMessage.Create(ShellMessages.Command, command));
}

/// <summary>Module registry: an explicit list (no reflection), so it stays trim- and AOT-safe (ADR-017).</summary>
public static class Modules
{
    public static void RegisterAll()
    {
        AppHost.Register("settings", () => new Settings.SettingsApp());
        AppHost.Register("weather", () => new Weather.WeatherApp());
        AppHost.Register("monitor", () => new Monitor.MonitorApp());
        AppHost.Register("notes", () => new Notes.NotesApp());
        AppHost.Register("time", () => new Time.TimeApp());
        AppHost.Register("files", () => new Files.FilesApp());
        AppHost.Register("music", () => new Music.MusicApp());
        AppHost.Register("browser", () => new Browser.BrowserApp());
        AppHost.Register("browser-private", () => new Browser.BrowserApp(isPrivate: true));
    }
}
