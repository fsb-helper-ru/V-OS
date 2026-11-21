using System.Globalization;

namespace VOS.Core.Settings;

public enum ThemeMode { Dark, Light, Auto, Sun }
public enum AccentSource { Preset, Windows, Wallpaper, Custom }
public enum ShellMode { Companion, Immersive }
public enum DockPosition { Bottom, Left, Right }
public enum PerformanceProfile { Adaptive, Quality, Balanced, Performance, Minimal }
public enum TemperatureUnit { Celsius, Fahrenheit }
public enum WindUnit { Kmh, Mph, Ms, Knots }
public enum PrecipUnit { Mm, Inch }
public enum ClockFormat { Auto, H24, H12 }

/// <summary>Root of all user preferences. Serialized as settings.json in the V-OS data folder.</summary>
public sealed class VosSettings
{
    public const int CurrentSchema = 1;
    public int SchemaVersion { get; set; } = CurrentSchema;
    public bool OnboardingCompleted { get; set; }
    public string UserName { get; set; } = "";
    public AppearanceSettings Appearance { get; set; } = new();
    public ShellSettings Shell { get; set; } = new();
    public DockSettings Dock { get; set; } = new();
    public DesktopSettings Desktop { get; set; } = new();
    public PerformanceSettings Performance { get; set; } = new();
    public WeatherSettings Weather { get; set; } = new();
    public MusicSettings Music { get; set; } = new();
    public BrowserSettings Browser { get; set; } = new();
    public FilesSettings Files { get; set; } = new();
    public NotificationSettings Notifications { get; set; } = new();
    public ClipboardSettings Clipboard { get; set; } = new();
    public PrivacySettings Privacy { get; set; } = new();
    public AssistantSettings Assistant { get; set; } = new();
    public TranslateSettings Translate { get; set; } = new();
    public UpdateSettings Updates { get; set; } = new();
    public HotkeySettings Hotkeys { get; set; } = new();
    public GamingSettings Gaming { get; set; } = new();
    public List<Workspace> Workspaces { get; set; } = Workspace.Defaults();
    public string ActiveWorkspaceId { get; set; } = "personal";

    public Workspace? ActiveWorkspace => Workspaces.FirstOrDefault(w => w.Id == ActiveWorkspaceId) ?? Workspaces.FirstOrDefault();

    /// <summary>Fills gaps left by older or hand-edited files so the rest of the system can trust the object.</summary>
    public void Normalize()
    {
        Appearance ??= new(); Shell ??= new(); Dock ??= new(); Desktop ??= new(); Performance ??= new();
        Weather ??= new(); Music ??= new(); Browser ??= new(); Files ??= new(); Notifications ??= new();
        Clipboard ??= new(); Privacy ??= new(); Assistant ??= new(); Translate ??= new(); Updates ??= new();
        Hotkeys ??= new(); Gaming ??= new();
        Workspaces ??= [];
        if (Workspaces.Count == 0) Workspaces = Workspace.Defaults();
        if (Workspaces.All(w => w.Id != ActiveWorkspaceId)) ActiveWorkspaceId = Workspaces[0].Id;
        Dock.Pinned ??= DockSettings.DefaultPinned();
        Dock.IconSize = Math.Clamp(Dock.IconSize, 32, 80);
        Dock.MagnificationScale = Math.Clamp(Dock.MagnificationScale, 1.0, 2.0);
        Desktop.Widgets ??= DesktopSettings.DefaultWidgets();
        Weather.Locations ??= [];
        Music.LibraryFolders ??= [];
        Clipboard.ExcludedApps ??= [];
        Browser.SearchEngine ??= "duckduckgo";
        Files.Favorites ??= [];
        Notifications.MutedApps ??= [];
        Notifications.FocusAllowedApps ??= [];
        Gaming.ManualGames ??= [];
        Hotkeys.Normalize();
        SchemaVersion = CurrentSchema;
    }
}

public sealed class AppearanceSettings
{
    public ThemeMode Theme { get; set; } = ThemeMode.Dark;
    public AccentSource AccentSource { get; set; } = AccentSource.Preset;
    public string AccentPreset { get; set; } = "aurora";
    public string CustomAccent { get; set; } = "#8EA2FF";
    /// <summary>Tint the capsule and ambient lighting with the colors of the playing album art.</summary>
    public bool AccentFollowsMedia { get; set; } = true;
    /// <summary>"ambient:&lt;scene&gt;", "color:#RRGGBB" or an absolute image path.</summary>
    public string Wallpaper { get; set; } = "ambient:aurora";
    public bool Transparency { get; set; } = true;
    public bool ReduceMotion { get; set; }
    public bool SyncWindowsTheme { get; set; }
}

public sealed class ShellSettings
{
    public ShellMode Mode { get; set; } = ShellMode.Companion;
    public bool StartWithWindows { get; set; } = true;
    public bool HideTaskbarInImmersive { get; set; } = true;
    /// <summary>Companion mode: auto-hide the Windows taskbar so the V-OS dock is the primary launcher (restored on exit).</summary>
    public bool AutoHideTaskbar { get; set; } = true;
    public bool HideDesktopIconsInImmersive { get; set; } = true;
    public bool WinKeyOpensLauncher { get; set; } = true;
    public bool TopBarReservesSpace { get; set; } = true;
    public bool ShowSeconds { get; set; }
    public ClockFormat ClockFormat { get; set; } = ClockFormat.Auto;
    public bool SnapZones { get; set; } = true;
    public string SnapLayout { get; set; } = "thirds";
    public bool ActivityTimeline { get; set; } = true;
    public int IdleScreenMinutes { get; set; }

    public bool Use24Hour => ClockFormat switch
    {
        ClockFormat.H24 => true,
        ClockFormat.H12 => false,
        _ => !CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern.Contains('t'),
    };
}

public sealed class DockItem
{
    /// <summary>"vos" (built-in V-OS app), "app" (installed app via Apps folder), "path" (file/folder/exe).</summary>
    public string Kind { get; set; } = "vos";
    /// <summary>V-OS app id, Apps-folder parsing name, or absolute path.</summary>
    public string Target { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Arguments { get; set; }
    /// <summary>Resolved executable path, used to match running windows.</summary>
    public string? ExePath { get; set; }
    public string? AppUserModelId { get; set; }

    public static DockItem Vos(string id, string name) => new() { Kind = "vos", Target = id, Name = name, AppUserModelId = "VOS.App." + id };
    public string Key => $"{Kind}:{Target}".ToLowerInvariant();
}

public sealed class DockSettings
{
    public DockPosition Position { get; set; } = DockPosition.Bottom;
    public double IconSize { get; set; } = 46;
    public bool Magnification { get; set; } = true;
    public double MagnificationScale { get; set; } = 1.4;
    public bool AutoHide { get; set; }
    public bool ShowRecent { get; set; } = true;
    public bool ShowTrash { get; set; } = true;
    public bool ReserveSpace { get; set; } = true;
    public List<DockItem>? Pinned { get; set; } = DefaultPinned();

    public static List<DockItem> DefaultPinned() =>
    [
        DockItem.Vos("files", "Files"),
        DockItem.Vos("browser", "Browser"),
        DockItem.Vos("music", "Music"),
        DockItem.Vos("weather", "Weather"),
        DockItem.Vos("notes", "Notes"),
        DockItem.Vos("time", "Time"),
        DockItem.Vos("monitor", "Monitor"),
        DockItem.Vos("store", "Store"),
        DockItem.Vos("settings", "Settings"),
    ];
}

public sealed class WidgetPlacement
{
    public string Kind { get; set; } = "clock";
    public bool Enabled { get; set; } = true;
    public int Order { get; set; }
}

public sealed class DesktopSettings
{
    public bool ShowHeroClock { get; set; } = true;
    public bool WeatherAmbience { get; set; } = true;
    public bool MusicReactive { get; set; } = true;
    public bool DayNightCycle { get; set; } = true;
    public bool ShowWidgets { get; set; } = true;
    public List<WidgetPlacement>? Widgets { get; set; } = DefaultWidgets();

    public static List<WidgetPlacement> DefaultWidgets() =>
    [
        new() { Kind = "weather", Order = 0 },
        new() { Kind = "media", Order = 1 },
        new() { Kind = "agenda", Order = 2 },
        new() { Kind = "system", Order = 3 },
        new() { Kind = "notes", Order = 4, Enabled = false },
    ];
}

public sealed class PerformanceSettings
{
    public PerformanceProfile Profile { get; set; } = PerformanceProfile.Adaptive;
    public bool PauseAmbientWhenCovered { get; set; } = true;
    public bool FocusWhenFullscreen { get; set; } = true;
    // Optional per-effect overrides (null = follow the profile).
    public bool? Backdrops { get; set; }
    public bool? Animations { get; set; }
    public bool? Ambient { get; set; }
    public bool? LiveThumbnails { get; set; }
    public bool? AudioVisualizer { get; set; }
    public int? AmbientFps { get; set; }
}

public sealed class WeatherLocation
{
    public string Name { get; set; } = "";
    public string Region { get; set; } = "";
    public string Country { get; set; } = "";
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string? TimeZone { get; set; }
    public bool IsDeviceLocation { get; set; }
    public string Key => string.Create(CultureInfo.InvariantCulture, $"{Latitude:0.###},{Longitude:0.###}");
    public string Display => string.IsNullOrEmpty(Region) || Region == Name ? Name : $"{Name}, {Region}";
}

public sealed class WeatherSettings
{
    public bool Enabled { get; set; } = true;
    public TemperatureUnit TemperatureUnit { get; set; } = RegionInfo.CurrentRegion.IsMetric ? TemperatureUnit.Celsius : TemperatureUnit.Fahrenheit;
    public WindUnit WindUnit { get; set; } = RegionInfo.CurrentRegion.IsMetric ? WindUnit.Kmh : WindUnit.Mph;
    public PrecipUnit PrecipUnit { get; set; } = RegionInfo.CurrentRegion.IsMetric ? PrecipUnit.Mm : PrecipUnit.Inch;
    public List<WeatherLocation>? Locations { get; set; } = [];
    public int SelectedIndex { get; set; }
    public int RefreshMinutes { get; set; } = 30;
    /// <summary>Base URL of the forecast provider; allows switching to a commercial Open-Meteo endpoint.</summary>
    public string ForecastEndpoint { get; set; } = "https://api.open-meteo.com/v1/forecast";
    public string? ApiKey { get; set; }

    public WeatherLocation? Selected => Locations is { Count: > 0 } l ? l[Math.Clamp(SelectedIndex, 0, l.Count - 1)] : null;
}

public sealed class MusicSettings
{
    public List<string>? LibraryFolders { get; set; } = [];
    public double Volume { get; set; } = 0.8;
    public bool Shuffle { get; set; }
    public string Repeat { get; set; } = "off";
    public string? OutputDeviceId { get; set; }
    public double[] Equalizer { get; set; } = new double[10];
    public bool EqualizerEnabled { get; set; }
    public string? SpotifyClientId { get; set; }
    public bool ShowVisualizer { get; set; } = true;
    public bool Crossfade { get; set; }
}

public sealed class BrowserSettings
{
    public string SearchEngine { get; set; } = "duckduckgo";
    public string HomePage { get; set; } = "vos://newtab";
    public string TrackingPrevention { get; set; } = "balanced";
    public bool RestoreSession { get; set; } = true;
    public bool VerticalTabs { get; set; } = true;
    public bool PasswordAutosave { get; set; } = true;
    public bool AutofillEnabled { get; set; } = true;
    public bool ExtensionsEnabled { get; set; }
    public bool SendDoNotTrack { get; set; } = true;
    public string Profile { get; set; } = "Default";
    public string TranslateTarget { get; set; } = "en";
}

public sealed class FavoriteFolder
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
}

public sealed class FilesSettings
{
    public string View { get; set; } = "details";
    public bool ShowHidden { get; set; }
    public bool ShowExtensions { get; set; } = true;
    public bool ShowPreview { get; set; } = true;
    public bool ConfirmDelete { get; set; } = true;
    public bool DualPane { get; set; }
    public List<FavoriteFolder>? Favorites { get; set; } = [];
    public bool RestoreTabs { get; set; } = true;
}

public sealed class NotificationSettings
{
    public bool Focus { get; set; }
    public bool FocusDuringGames { get; set; } = true;
    public bool FocusOnSchedule { get; set; }
    public string FocusStart { get; set; } = "22:00";
    public string FocusEnd { get; set; } = "07:00";
    public bool ShowBanners { get; set; } = true;
    public bool Sounds { get; set; } = true;
    public List<string>? MutedApps { get; set; } = [];
    public List<string>? FocusAllowedApps { get; set; } = [];
    public int HistoryDays { get; set; } = 7;
}

public sealed class ClipboardSettings
{
    public bool Enabled { get; set; } = true;
    public bool CaptureImages { get; set; } = true;
    public int RetentionDays { get; set; } = 7;
    public int MaxItems { get; set; } = 400;
    public List<string>? ExcludedApps { get; set; } = ["KeePass.exe", "KeePassXC.exe", "1Password.exe", "Bitwarden.exe"];
}

public sealed class PrivacySettings
{
    /// <summary>Explicit, revocable consents. Nothing is ever sent before the matching flag is true.</summary>
    public bool LocationConsent { get; set; }
    public bool CloudAssistantConsent { get; set; }
    public bool OnlineTranslationConsent { get; set; }
    public bool WeatherNetworkConsent { get; set; } = true;
    public bool UpdateChecks { get; set; } = true;
    public bool CrashReportsLocalOnly { get; set; } = true;
    public bool RecordRecentFiles { get; set; } = true;
}

public sealed class AssistantSettings
{
    /// <summary>"local", "claude" or "ollama".</summary>
    public string Provider { get; set; } = "local";
    public string ClaudeModel { get; set; } = "claude-opus-5-5";
    /// <summary>DPAPI-protected API key ("dpapi:...").</summary>
    public string? ClaudeApiKey { get; set; }
    public string OllamaEndpoint { get; set; } = "http://localhost:11434";
    public string OllamaModel { get; set; } = "llama3.2";
    public bool AllowSystemActions { get; set; } = true;
}

public sealed class TranslateSettings
{
    /// <summary>"mymemory", "deepl" or "libretranslate".</summary>
    public string Provider { get; set; } = "mymemory";
    public string TargetLanguage { get; set; } = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName is { Length: 2 } l && l != "iv" ? l : "en";
    public string? DeepLKey { get; set; }
    public string LibreTranslateUrl { get; set; } = "https://libretranslate.com";
    public string? LibreTranslateKey { get; set; }
}

public sealed class UpdateSettings
{
    public string Channel { get; set; } = "stable";
    /// <summary>Overrides the build-time feed URL (vos-release.json location).</summary>
    public string? FeedUrl { get; set; }
    public bool AutoCheck { get; set; } = true;
    public DateTimeOffset? LastCheck { get; set; }
    public string? SkippedVersion { get; set; }
}

public sealed class HotkeyBinding
{
    public string Action { get; set; } = "";
    /// <summary>e.g. "Alt+Space"; alternatives separated by "|" are tried in order until one is free.</summary>
    public string Gesture { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

public sealed class HotkeySettings
{
    public List<HotkeyBinding> Bindings { get; set; } = Defaults();

    public static List<HotkeyBinding> Defaults() =>
    [
        new() { Action = "command", Gesture = "Alt+Space|Ctrl+Alt+Space|Win+Alt+Space" },
        new() { Action = "clipboard", Gesture = "Win+Alt+V" },
        new() { Action = "switcher", Gesture = "Alt+Q" },
        new() { Action = "control", Gesture = "Win+Alt+C" },
        new() { Action = "activity", Gesture = "Win+Alt+N" },
        new() { Action = "capture.region", Gesture = "Ctrl+Alt+S" },
        new() { Action = "capture.screen", Gesture = "Ctrl+Alt+P" },
        new() { Action = "capture.window", Gesture = "Ctrl+Alt+W" },
        new() { Action = "layout", Gesture = "Win+Alt+Z" },
        new() { Action = "drop", Gesture = "Win+Alt+D|Win+Alt+O|Ctrl+Alt+D" },
        new() { Action = "translate.clipboard", Gesture = "Win+Alt+T|Win+Alt+Y|Ctrl+Alt+T" },
        new() { Action = "lock", Gesture = "Win+Alt+L" },
        new() { Action = "recovery", Gesture = "Ctrl+Alt+Shift+F12" },
    ];

    public void Normalize()
    {
        Bindings ??= Defaults();
        foreach (var d in Defaults())
            if (Bindings.All(b => b.Action != d.Action)) Bindings.Add(d);
    }

    public string? GestureFor(string action) => Bindings.FirstOrDefault(b => b.Action == action && b.Enabled)?.Gesture;
}

public sealed class ManualGame
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string? Arguments { get; set; }
}

public sealed class GamingSettings
{
    public bool AutoGamingWorkspace { get; set; }
    public bool SwitchPowerPlan { get; set; }
    public bool MinimalEffectsWhilePlaying { get; set; } = true;
    public bool TrackPlaytime { get; set; } = true;
    public List<ManualGame>? ManualGames { get; set; } = [];
}

public sealed class WorkspaceWindow
{
    public string ExePath { get; set; } = "";
    public string? AppUserModelId { get; set; }
    public string Title { get; set; } = "";
    public int Left { get; set; }
    public int Top { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    /// <summary>1 normal, 2 minimized, 3 maximized (SW_* values).</summary>
    public int ShowState { get; set; } = 1;
}

public sealed class Workspace
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Icon { get; set; } = "layers";
    public string? Accent { get; set; }
    public string? Wallpaper { get; set; }
    public ThemeMode? Theme { get; set; }
    public PerformanceProfile? Profile { get; set; }
    public bool Focus { get; set; }
    public bool HideOtherWindows { get; set; } = true;
    public bool LaunchApps { get; set; } = true;
    public List<DockItem>? Dock { get; set; }
    public List<WorkspaceWindow> Windows { get; set; } = [];

    public static List<Workspace> Defaults() =>
    [
        new() { Id = "personal", Name = "Personal", Icon = "home", HideOtherWindows = false, LaunchApps = false },
        new() { Id = "work", Name = "Work", Icon = "briefcase", Accent = "glacier" },
        new() { Id = "study", Name = "Study", Icon = "book", Accent = "lume", Focus = true },
        new() { Id = "gaming", Name = "Gaming", Icon = "gamepad", Accent = "ember", Profile = PerformanceProfile.Minimal, Focus = true, Wallpaper = "ambient:nebula" },
        new() { Id = "media", Name = "Media", Icon = "music", Accent = "rose", Wallpaper = "ambient:horizon" },
        new() { Id = "minimal", Name = "Minimal", Icon = "leaf", Accent = "graphite", Profile = PerformanceProfile.Performance, Wallpaper = "ambient:still" },
    ];
}
