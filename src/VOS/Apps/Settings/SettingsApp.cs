using System.Diagnostics;
using System.Globalization;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Win32;
using VOS.Core;
using VOS.Core.Ipc;
using VOS.Core.Security;
using VOS.Core.Settings;
using VOS.Core.Updates;
using VOS.Core.Weather;
using VOS.Platform.Input;
using VOS.Platform.Sys;
using VOS.Shell;
using VOS.UI;
using VOS.UI.Quality;
using VOS.UI.Theme;

namespace VOS.Apps.Settings;

/// <summary>
/// V-OS Settings: every preference in <see cref="VosSettings"/>, grouped the way people look for them. Changes are
/// saved atomically to settings.json; the shell watches that file and applies them live.
/// </summary>
public sealed class SettingsApp : IAppModule
{
    public AppFrame Frame { get; }
    private readonly NavShell _nav = new();
    private static SettingsStore Store => SettingsStore.Instance;
    private static VosSettings Cur => Store.Current;
    private static void Set(Action<VosSettings> change) => Store.Update(change);
    private XamlRoot Root => Frame.Root.XamlRoot;

    private static readonly (string id, string label, string glyph)[] s_pages =
    [
        ("general", "General", Glyphs.Settings), ("appearance", "Appearance", Glyphs.Palette), ("dock", "Dock", Glyphs.Apps),
        ("desktop", "Desktop and widgets", Glyphs.Desktop), ("performance", "Performance", Glyphs.Speed),
        ("notifications", "Notifications and focus", Glyphs.Bell), ("shortcuts", "Keyboard shortcuts", Glyphs.Keyboard),
        ("weather", "Weather", Glyphs.Weather), ("clipboard", "Clipboard", Glyphs.Clipboard), ("apps", "Apps", Glyphs.Layers),
        ("workspaces", "Workspaces", Glyphs.Briefcase), ("privacy", "Privacy and security", Glyphs.Shield),
        ("snapshots", "Snapshots", Glyphs.History), ("updates", "Updates", Glyphs.Download), ("about", "About V-OS", Glyphs.Info),
    ];

    public SettingsApp()
    {
        Frame = new AppFrame(VosApp.Find("settings")!, "Settings", 860, 560, 1080, 760);
        _nav.CachePages = false; // pages are cheap and must always show current values
        var search = Kit.SearchBox("Find a setting", q => Filter(q));
        _nav.SidebarTop.Children.Add(search);
        foreach (var (id, label, glyph) in s_pages) _nav.Add(new NavShell.Item(id, label, glyph));
        _nav.PageFactory = Build;
        Frame.Body.Content = _nav;
        _nav.Select("general");
    }

    public void HandleArgs(AppArgs args)
    {
        var section = args.Get("section");
        if (section != null && s_pages.Any(p => p.id == section)) _nav.Select(section);
    }

    private void Filter(string query)
    {
        query = query.Trim();
        if (query.Length == 0) return;
        var hit = s_pages.FirstOrDefault(p => p.label.Contains(query, StringComparison.OrdinalIgnoreCase) || Keywords(p.id).Any(k => k.Contains(query, StringComparison.OrdinalIgnoreCase)));
        if (hit.id != null && hit.id != _nav.Current) _nav.Select(hit.id);
    }

    private static string[] Keywords(string page) => page switch
    {
        "general" => ["startup", "start with windows", "immersive", "companion", "taskbar", "clock", "24", "snap", "name"],
        "appearance" => ["theme", "dark", "light", "accent", "colour", "color", "wallpaper", "transparency", "motion"],
        "dock" => ["icon size", "magnification", "pinned", "auto-hide", "trash"],
        "desktop" => ["widgets", "clock", "ambient", "day night"],
        "performance" => ["profile", "quality", "battery", "gpu", "effects", "fps", "animations"],
        "notifications" => ["focus", "do not disturb", "banners", "sounds", "quiet hours"],
        "shortcuts" => ["hotkey", "keyboard", "keys"],
        "weather" => ["location", "units", "celsius", "fahrenheit"],
        "clipboard" => ["history", "paste"],
        "apps" => ["browser", "files", "music", "assistant", "translate", "gaming", "search engine", "api key"],
        "workspaces" => ["profiles", "work", "study", "gaming"],
        "privacy" => ["consent", "network", "data", "location", "telemetry"],
        "snapshots" => ["backup", "restore"],
        "updates" => ["version", "channel"],
        "about" => ["version", "system", "logs", "recovery"],
        _ => [],
    };

    private UIElement Build(string id) => Kit.Scroll(id switch
    {
        "general" => General(),
        "appearance" => Appearance(),
        "dock" => Dock(),
        "desktop" => Desktop(),
        "performance" => Performance(),
        "notifications" => Notifications(),
        "shortcuts" => Shortcuts(),
        "weather" => Weather(),
        "clipboard" => Clipboard(),
        "apps" => Apps(),
        "workspaces" => Workspaces(),
        "privacy" => Privacy(),
        "snapshots" => Snapshots(),
        "updates" => Updates(),
        _ => About(),
    });

    private void Refresh() => _nav.Select(_nav.Current ?? "general");

    // ------------------------------------------------------------------ General

    private StackPanel General()
    {
        var s = Cur;
        var page = Kit.Stack(4, Kit.PageHeader("General", "How V-OS starts and how it fits around Windows."));
        page.Children.Add(Kit.Section("Startup"));
        page.Children.Add(Kit.Group(
            Kit.Setting("Start V-OS with Windows", "Opens the V-OS shell when you sign in.",
                Kit.Toggle(s.Shell.StartWithWindows, v => { Set(x => x.Shell.StartWithWindows = v); Startup.Apply(v); }), Glyphs.Power),
            Kit.Setting("Your name", "Used for greetings on the desktop and the lock screen.",
                Kit.Field(s.UserName, "Name", v => Set(x => x.UserName = v.Trim())), Glyphs.Info)));

        page.Children.Add(Kit.Section("Desktop mode"));
        var mode = Kit.Choice<ShellMode>([(ShellMode.Companion, "Companion"), (ShellMode.Immersive, "Immersive")], s.Shell.Mode, async m =>
        {
            if (m == ShellMode.Immersive && !await Dialogs.ConfirmAsync(Root, "Turn on immersive mode?",
                    "V-OS hides the Windows taskbar and desktop icons and shows its own desktop. Everything is reversible: switch back here, press Ctrl+Alt+Shift+F12, or use V-OS Launcher › Recovery.", "Turn on"))
            { Refresh(); return; }
            Set(x => x.Shell.Mode = m);
            Refresh();
        });
        page.Children.Add(Kit.Group(
            Kit.Setting("Mode", "Companion runs alongside the Windows taskbar and desktop. Immersive replaces them with the V-OS desktop.", mode, Glyphs.Desktop),
            Kit.Setting("Auto-hide the Windows taskbar", "Companion mode: the dock owns the bottom edge; the taskbar slides up when you point at it. Restored when V-OS exits.",
                Kit.Toggle(s.Shell.AutoHideTaskbar, v => Set(x => x.Shell.AutoHideTaskbar = v))),
            Kit.Setting("Top bar reserves space", "Maximised windows end below the top bar instead of under it.",
                Kit.Toggle(s.Shell.TopBarReservesSpace, v => Set(x => x.Shell.TopBarReservesSpace = v))),
            Kit.Setting("Immersive: hide the Windows taskbar", null, Kit.Toggle(s.Shell.HideTaskbarInImmersive, v => Set(x => x.Shell.HideTaskbarInImmersive = v))),
            Kit.Setting("Immersive: hide desktop icons", null, Kit.Toggle(s.Shell.HideDesktopIconsInImmersive, v => Set(x => x.Shell.HideDesktopIconsInImmersive = v))),
            Kit.Setting("Immersive: Windows key opens V-OS search", "A tap of the Windows key opens V-OS search instead of Start. Win+key shortcuts keep working.",
                Kit.Toggle(s.Shell.WinKeyOpensLauncher, v => Set(x => x.Shell.WinKeyOpensLauncher = v)))));

        page.Children.Add(Kit.Section("Clock"));
        page.Children.Add(Kit.Group(
            Kit.Setting("Time format", null, Kit.Choice<ClockFormat>([(ClockFormat.Auto, "Follow Windows"), (ClockFormat.H24, "24-hour"), (ClockFormat.H12, "12-hour")], s.Shell.ClockFormat, v => Set(x => x.Shell.ClockFormat = v)), Glyphs.Clock),
            Kit.Setting("Show seconds in the top bar", "Costs one redraw per second.", Kit.Toggle(s.Shell.ShowSeconds, v => Set(x => x.Shell.ShowSeconds = v)))));

        page.Children.Add(Kit.Section("Windows"));
        page.Children.Add(Kit.Group(
            Kit.Setting("Snap zones", "Show V-OS layout zones while you drag a window.", Kit.Toggle(s.Shell.SnapZones, v => Set(x => x.Shell.SnapZones = v)), Glyphs.Split),
            Kit.Setting("Default layout", null, Kit.Choice<string>([("halves", "Halves"), ("thirds", "Thirds"), ("grid", "Grid of four"), ("focus", "Focus + sidebar")], s.Shell.SnapLayout, v => Set(x => x.Shell.SnapLayout = v))),
            Kit.Setting("Activity timeline", "Remember recently used apps and files for search and the activity center.", Kit.Toggle(s.Shell.ActivityTimeline, v => Set(x => x.Shell.ActivityTimeline = v)), Glyphs.History),
            Kit.Setting("Idle screen", "Show the V-OS clock screen after inactivity (0 = off).",
                Kit.SliderControl(0, 60, s.Shell.IdleScreenMinutes, 5, v => Set(x => x.Shell.IdleScreenMinutes = (int)v), 200, v => v == 0 ? "Off" : $"{v:0} min"))));
        return page;
    }

    // ------------------------------------------------------------------ Appearance

    private StackPanel Appearance()
    {
        var a = Cur.Appearance;
        var page = Kit.Stack(4, Kit.PageHeader("Appearance", "Theme, accent colour, wallpaper and effects."));
        page.Children.Add(Kit.Section("Theme"));
        page.Children.Add(Kit.Group(
            Kit.Setting("Theme", "Sun follows sunrise and sunset at your weather location.",
                Kit.Choice<ThemeMode>([(ThemeMode.Dark, "Dark"), (ThemeMode.Light, "Light"), (ThemeMode.Auto, "Follow Windows"), (ThemeMode.Sun, "Sunrise and sunset")], a.Theme, v => Set(x => x.Appearance.Theme = v)), Glyphs.Moon),
            Kit.Setting("Apply the theme to Windows apps", "Sets the Windows app theme to match V-OS.", Kit.Toggle(a.SyncWindowsTheme, v => Set(x => x.Appearance.SyncWindowsTheme = v)))));

        page.Children.Add(Kit.Section("Accent colour"));
        var swatches = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        foreach (var p in AccentPresets.All)
        {
            bool selected = a.AccentSource == AccentSource.Preset && a.AccentPreset == p.Id;
            var dot = new Grid { Width = 34, Height = 34 };
            dot.Children.Add(new Border { CornerRadius = new CornerRadius(17), Background = new SolidColorBrush(Colors2.Hex(ThemeService.Current.IsDark ? p.Dark : p.Light)) });
            if (selected) dot.Children.Add(new Border { CornerRadius = new CornerRadius(17), BorderThickness = new Thickness(2.5), BorderBrush = Ui.B("Vos.TextPrimary") });
            var b = Buttons.Subtle(dot, () => { Set(x => { x.Appearance.AccentSource = AccentSource.Preset; x.Appearance.AccentPreset = p.Id; }); Refresh(); }, 19, new Thickness(2));
            ToolTipService.SetToolTip(b, p.Name);
            Buttons.AutomationName(b, p.Name + " accent");
            swatches.Children.Add(b);
        }
        var custom = Kit.Field(a.CustomAccent, "#8EA2FF", v =>
        {
            var hex = v.Trim().StartsWith('#') ? v.Trim() : "#" + v.Trim();
            if (hex.Length == 7 && int.TryParse(hex[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
            { Set(x => { x.Appearance.CustomAccent = hex.ToUpperInvariant(); x.Appearance.AccentSource = AccentSource.Custom; }); Refresh(); }
        }, 130);
        page.Children.Add(Kit.Group(
            Kit.Setting("Source", null, Kit.Choice<AccentSource>([(AccentSource.Preset, "V-OS palette"), (AccentSource.Windows, "Windows accent"), (AccentSource.Wallpaper, "From wallpaper"), (AccentSource.Custom, "Custom")], a.AccentSource, v => { Set(x => x.Appearance.AccentSource = v); Refresh(); }), Glyphs.Palette),
            new Grid { Padding = new Thickness(16, 6, 16, 14), Children = { swatches } },
            Kit.Setting("Custom colour", "Hex value, for example #8EA2FF.", custom),
            Kit.Setting("Tint with album art", "While music plays, the capsule and ambient light take colour from the cover.", Kit.Toggle(a.AccentFollowsMedia, v => Set(x => x.Appearance.AccentFollowsMedia = v)))));

        page.Children.Add(Kit.Section("Wallpaper"));
        string wp = a.Wallpaper;
        string kind = wp.StartsWith("ambient:", StringComparison.Ordinal) ? wp : wp.StartsWith("color:", StringComparison.Ordinal) ? "color" : "image";
        var scene = Kit.Choice<string>([("ambient:aurora", "Aurora (animated)"), ("ambient:nebula", "Nebula (animated)"), ("ambient:horizon", "Horizon (animated)"), ("ambient:still", "Still"), ("color", "Solid colour"), ("image", "Picture…")], kind, async v =>
        {
            if (v == "image")
            {
                var file = await Pickers.FileAsync(Frame.Hwnd, Windows.Storage.Pickers.PickerLocationId.PicturesLibrary, ".jpg", ".jpeg", ".png", ".bmp", ".webp");
                if (file != null) Set(x => x.Appearance.Wallpaper = file);
            }
            else if (v == "color") Set(x => x.Appearance.Wallpaper = "color:#14161C");
            else Set(x => x.Appearance.Wallpaper = v);
            Refresh();
        });
        page.Children.Add(Kit.Group(
            Kit.Setting("V-OS desktop background", "Used in immersive mode. Animated scenes pause when the desktop is covered.", scene, Glyphs.Picture),
            Kit.Setting("Current", wp.StartsWith("ambient:", StringComparison.Ordinal) ? Kit.Words(wp[8..]) : wp.StartsWith("color:", StringComparison.Ordinal) ? wp[6..] : Path.GetFileName(wp), null)));

        page.Children.Add(Kit.Section("Effects"));
        page.Children.Add(Kit.Group(
            Kit.Setting("Transparency", "Glass and Mica materials. Off uses solid surfaces (also faster).", Kit.Toggle(a.Transparency, v => Set(x => x.Appearance.Transparency = v)), Glyphs.Layers),
            Kit.Setting("Reduce motion", "Replaces movement with simple fades.", Kit.Toggle(a.ReduceMotion, v => Set(x => x.Appearance.ReduceMotion = v)), Glyphs.Leaf)));
        return page;
    }

    // ------------------------------------------------------------------ Dock

    private StackPanel Dock()
    {
        var d = Cur.Dock;
        var page = Kit.Stack(4, Kit.PageHeader("Dock", "Size, behaviour and the apps pinned to the dock."));
        page.Children.Add(Kit.Group(
            Kit.Setting("Icon size", null, Kit.SliderControl(32, 80, d.IconSize, 2, v => Set(x => x.Dock.IconSize = v), 220, v => $"{v:0} px"), Glyphs.Apps),
            Kit.Setting("Magnification", "Icons grow under the pointer.", Kit.Toggle(d.Magnification, v => Set(x => x.Dock.Magnification = v))),
            Kit.Setting("Magnification amount", null, Kit.SliderControl(1.0, 2.0, d.MagnificationScale, 0.05, v => Set(x => x.Dock.MagnificationScale = Math.Round(v, 2)), 220, v => $"{v:0.00}×")),
            Kit.Setting("Reserve space", "Maximised windows end above the dock.", Kit.Toggle(d.ReserveSpace, v => Set(x => x.Dock.ReserveSpace = v))),
            Kit.Setting("Auto-hide", "Hide the dock until you point at the bottom edge.", Kit.Toggle(d.AutoHide, v => Set(x => x.Dock.AutoHide = v))),
            Kit.Setting("Show running apps", "Apps that are open but not pinned appear after the separator.", Kit.Toggle(d.ShowRecent, v => Set(x => x.Dock.ShowRecent = v))),
            Kit.Setting("Show the Recycle Bin", null, Kit.Toggle(d.ShowTrash, v => Set(x => x.Dock.ShowTrash = v)))));

        page.Children.Add(Kit.Section("Pinned apps"));
        var pins = d.Pinned ?? [];
        var rows = new List<UIElement>();
        for (int i = 0; i < pins.Count; i++)
        {
            int index = i;
            var pin = pins[i];
            var vos = pin.Kind == "vos" ? VosApp.Find(pin.Target) : null;
            UIElement icon = vos != null ? new AppIconView(vos, 28) : Ui.Icon(pin.Kind == "path" ? Glyphs.Folder : Glyphs.Apps, 20);
            var actions = Kit.Row(2,
                Buttons.Icon(Glyphs.Up, () => Move(index, -1), 30, 12, "Move left"),
                Buttons.Icon(Glyphs.Down, () => Move(index, 1), 30, 12, "Move right"),
                Buttons.Icon(Glyphs.Unpin, () => { Set(x => x.Dock.Pinned!.RemoveAt(index)); Refresh(); }, 30, 13, "Unpin"));
            rows.Add(Kit.Setting(pin.Name, pin.Kind == "vos" ? "V-OS app" : pin.Kind == "app" ? "Installed app" : pin.Target, actions));
            ((Grid)rows[^1]).Children.Insert(0, new Border { Child = icon, Margin = new Thickness(0, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        }
        if (rows.Count == 0) rows.Add(Kit.Setting("Nothing pinned", "Right-click an app in the dock or in search and choose Pin to dock."));
        page.Children.Add(Kit.Group([.. rows]));
        var reset = Buttons.Filled("Restore default pins", () => { Set(x => x.Dock.Pinned = DockSettings.DefaultPinned()); Refresh(); });
        reset.Margin = new Thickness(0, 10, 0, 0);
        page.Children.Add(reset);
        return page;

        void Move(int index, int delta)
        {
            Set(x =>
            {
                var list = x.Dock.Pinned!;
                int to = index + delta;
                if (to < 0 || to >= list.Count) return;
                (list[index], list[to]) = (list[to], list[index]);
            });
            Refresh();
        }
    }

    // ------------------------------------------------------------------ Desktop

    private StackPanel Desktop()
    {
        var d = Cur.Desktop;
        var page = Kit.Stack(4, Kit.PageHeader("Desktop and widgets", "What the V-OS desktop shows in immersive mode."));
        page.Children.Add(Kit.Group(
            Kit.Setting("Large clock", null, Kit.Toggle(d.ShowHeroClock, v => Set(x => x.Desktop.ShowHeroClock = v)), Glyphs.Clock),
            Kit.Setting("Weather ambience", "The ambient scene reflects the weather outside.", Kit.Toggle(d.WeatherAmbience, v => Set(x => x.Desktop.WeatherAmbience = v)), Glyphs.Weather),
            Kit.Setting("React to music", "The ambient light follows what is playing.", Kit.Toggle(d.MusicReactive, v => Set(x => x.Desktop.MusicReactive = v)), Glyphs.Music),
            Kit.Setting("Day and night cycle", "Colours shift gently through the day.", Kit.Toggle(d.DayNightCycle, v => Set(x => x.Desktop.DayNightCycle = v)), Glyphs.Sun),
            Kit.Setting("Widgets", null, Kit.Toggle(d.ShowWidgets, v => Set(x => x.Desktop.ShowWidgets = v)), Glyphs.Grid)));
        page.Children.Add(Kit.Section("Widgets on the desktop"));
        var rows = (d.Widgets ?? []).OrderBy(w => w.Order).Select(w =>
        {
            string kind = w.Kind;
            return (UIElement)Kit.Setting(kind switch { "media" => "Now playing", "agenda" => "Timers and agenda", "system" => "System", _ => Kit.Words(char.ToUpperInvariant(kind[0]) + kind[1..]) }, null,
                Kit.Toggle(w.Enabled, v => Set(x => { var t = x.Desktop.Widgets!.FirstOrDefault(z => z.Kind == kind); if (t != null) t.Enabled = v; })));
        }).ToArray();
        page.Children.Add(Kit.Group(rows));
        return page;
    }

    // ------------------------------------------------------------------ Performance

    private StackPanel Performance()
    {
        var p = Cur.Performance;
        var q = VisualQuality.Current;
        var page = Kit.Stack(4, Kit.PageHeader("Performance", "Profiles change real behaviour: materials, animation, ambient rendering and live previews."));
        string Desc(PerformanceProfile pr) => pr switch
        {
            PerformanceProfile.Adaptive => $"Chooses for you from your hardware, power source and battery saver. Now: {q.Profile}.",
            PerformanceProfile.Quality => "Every effect: glass, springs, animated ambience at 60 fps, live previews.",
            PerformanceProfile.Balanced => "All materials, ambience at 30 fps.",
            PerformanceProfile.Performance => "Solid surfaces, short animations, still ambience.",
            _ => "No transparency or animation. The lightest V-OS.",
        };
        var profiles = new List<UIElement>();
        foreach (var pr in Enum.GetValues<PerformanceProfile>())
        {
            var profile = pr;
            var radio = new RadioButton { GroupName = "profile", IsChecked = p.Profile == profile, MinWidth = 0, Content = "" };
            radio.Checked += (_, _) => { if (Cur.Performance.Profile != profile) { Set(x => x.Performance.Profile = profile); } };
            profiles.Add(Kit.Setting(profile == PerformanceProfile.Adaptive ? "Adaptive (recommended)" : profile.ToString(), Desc(profile), radio));
        }
        page.Children.Add(Kit.Group([.. profiles]));

        page.Children.Add(Kit.Section("In effect now"));
        string F(bool b) => b ? "On" : "Off";
        var grid = new Grid { ColumnSpacing = 24, RowSpacing = 16 };
        for (int c = 0; c < 4; c++) grid.ColumnDefinitions.Add(new ColumnDefinition());
        var stats = new (string, string)[]
        {
            ("Profile", q.Profile.ToString()), ("Glass", F(q.Glass)), ("Mica", F(q.Mica)), ("Motion", $"{q.MotionScale:0.#}×"),
            ("Ambient", q.AmbientAnimated ? $"{q.ParticleFps} fps" : F(q.Ambient)), ("Shadows", F(q.Shadows)), ("Live previews", F(q.LiveThumbnails)), ("Dock magnification", F(q.DockMagnification)),
        };
        for (int i = 0; i < stats.Length; i++)
        {
            var st = Kit.Stat(stats[i].Item1, stats[i].Item2);
            Grid.SetColumn(st, i % 4); Grid.SetRow(st, i / 4);
            if (i / 4 >= grid.RowDefinitions.Count) grid.RowDefinitions.Add(new RowDefinition());
            grid.Children.Add(st);
        }
        var effect = Kit.Stack(14, grid);
        if (q.Reason.Length > 0) effect.Children.Add(Ui.Text(q.Reason, 12.5, "Vos.TextSecondary", wrap: TextWrapping.Wrap));
        page.Children.Add(Kit.Card(effect, new Thickness(20)));

        page.Children.Add(Kit.Section("This PC"));
        var hw = HardwareProbe.Probe();
        page.Children.Add(Kit.Group(
            Kit.Setting("Graphics", $"{hw.GpuName}{(hw.GpuDedicatedMb > 0 ? $" · {hw.GpuDedicatedMb / 1024.0:0.#} GB" : "")}", null, Glyphs.Monitor),
            Kit.Setting("Processor and memory", $"{hw.LogicalCores} logical cores · {hw.RamGb:0} GB RAM", null, Glyphs.Cpu),
            Kit.Setting("Power", hw.OnBattery ? $"On battery{(hw.BatterySaver ? " · battery saver" : "")}" : "Plugged in", null, Glyphs.Battery)));

        page.Children.Add(Kit.Section("Fine-tune"));
        ComboBox Tri(bool? value, Action<bool?> set) => Kit.Choice<int>([(0, "Follow profile"), (1, "On"), (2, "Off")], value == null ? 0 : value.Value ? 1 : 2, v => set(v switch { 1 => true, 2 => false, _ => null }), 170);
        page.Children.Add(Kit.Group(
            Kit.Setting("Materials (glass and Mica)", null, Tri(p.Backdrops, v => Set(x => x.Performance.Backdrops = v))),
            Kit.Setting("Animations", null, Tri(p.Animations, v => Set(x => x.Performance.Animations = v))),
            Kit.Setting("Animated ambience", null, Tri(p.Ambient, v => Set(x => x.Performance.Ambient = v))),
            Kit.Setting("Live window previews", "Used by the window switcher.", Tri(p.LiveThumbnails, v => Set(x => x.Performance.LiveThumbnails = v))),
            Kit.Setting("Audio visualizer", null, Tri(p.AudioVisualizer, v => Set(x => x.Performance.AudioVisualizer = v))),
            Kit.Setting("Ambient frame rate", null, Kit.Choice<int>([(0, "Follow profile"), (15, "15 fps"), (30, "30 fps"), (60, "60 fps")], p.AmbientFps ?? 0, v => Set(x => x.Performance.AmbientFps = v == 0 ? null : v), 170)),
            Kit.Setting("Pause ambience when covered", "Stops rendering the desktop when windows cover it.", Kit.Toggle(p.PauseAmbientWhenCovered, v => Set(x => x.Performance.PauseAmbientWhenCovered = v))),
            Kit.Setting("Quiet mode for fullscreen apps", "Turns on Focus and minimal effects while a game or video is fullscreen.", Kit.Toggle(p.FocusWhenFullscreen, v => Set(x => x.Performance.FocusWhenFullscreen = v)))));
        return page;
    }

    // ------------------------------------------------------------------ Notifications

    private StackPanel Notifications()
    {
        var n = Cur.Notifications;
        var page = Kit.Stack(4, Kit.PageHeader("Notifications and focus", "What reaches you, and when V-OS keeps quiet."));
        page.Children.Add(Kit.Group(
            Kit.Setting("Banners", "Show new notifications in the capsule.", Kit.Toggle(n.ShowBanners, v => Set(x => x.Notifications.ShowBanners = v)), Glyphs.Bell),
            Kit.Setting("Sounds", null, Kit.Toggle(n.Sounds, v => Set(x => x.Notifications.Sounds = v)), Glyphs.Volume),
            Kit.Setting("Keep history for", null, Kit.SliderControl(1, 30, n.HistoryDays, 1, v => Set(x => x.Notifications.HistoryDays = (int)v), 200, v => $"{v:0} days"), Glyphs.History)));
        page.Children.Add(Kit.Section("Focus"));
        bool ValidTime(string t) => TimeOnly.TryParseExact(t.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
        page.Children.Add(Kit.Group(
            Kit.Setting("Focus now", "Only priority alerts get through.", Kit.Toggle(n.Focus, v => Set(x => x.Notifications.Focus = v)), Glyphs.Focus),
            Kit.Setting("Focus while gaming", "Turns on when a game is fullscreen.", Kit.Toggle(n.FocusDuringGames, v => Set(x => x.Notifications.FocusDuringGames = v)), Glyphs.Game),
            Kit.Setting("Quiet hours", "Turn on Focus every day between these times.", Kit.Toggle(n.FocusOnSchedule, v => Set(x => x.Notifications.FocusOnSchedule = v)), Glyphs.Moon),
            Kit.Setting("From", "24-hour time, for example 22:00.", Kit.Field(n.FocusStart, "22:00", v => { if (ValidTime(v)) Set(x => x.Notifications.FocusStart = v.Trim()); }, 100)),
            Kit.Setting("Until", null, Kit.Field(n.FocusEnd, "07:00", v => { if (ValidTime(v)) Set(x => x.Notifications.FocusEnd = v.Trim()); }, 100))));
        page.Children.Add(Kit.Section("Muted apps"));
        var muted = n.MutedApps ?? [];
        page.Children.Add(muted.Count == 0
            ? Kit.Group(Kit.Setting("No muted apps", "Mute an app from a notification's menu in the activity center."))
            : Kit.Group([.. muted.Select(m => (UIElement)Kit.Setting(m, null, Buttons.Filled("Unmute", () => { Set(x => x.Notifications.MutedApps!.Remove(m)); Refresh(); })))]));
        return page;
    }

    // ------------------------------------------------------------------ Shortcuts

    private static string ActionLabel(string action) => action switch
    {
        "command" => "Search and commands",
        "clipboard" => "Clipboard history",
        "switcher" => "Window switcher",
        "control" => "Control Center",
        "activity" => "Activity and notifications",
        "capture.region" => "Capture a region",
        "capture.screen" => "Capture the screen",
        "capture.window" => "Capture a window",
        "layout" => "Snap layouts",
        "drop" => "Drop shelf",
        "translate.clipboard" => "Translate the clipboard",
        "lock" => "Lock screen",
        "recovery" => "Restore the Windows desktop",
        _ => action,
    };

    private StackPanel Shortcuts()
    {
        var page = Kit.Stack(4, Kit.PageHeader("Keyboard shortcuts", "Separate alternatives with “|”. V-OS uses the first chord no other app has taken."));
        var active = VosJson.TryDeserialize<Dictionary<string, string>>(AtomicFile.TryReadAllText(Path.Combine(VosPaths.State, "hotkeys-active.json"))) ?? [];
        bool shellRunning = IpcClient.IsListening(IpcNames.Shell);
        if (!shellRunning) page.Children.Add(Kit.Info("V-OS shell is not running", "Shortcuts are registered by the shell. Start V-OS to use them.", InfoBarSeverity.Warning));
        var rows = new List<UIElement>();
        foreach (var b in Cur.Hotkeys.Bindings)
        {
            var action = b.Action;
            string state = !b.Enabled ? "Off" : active.TryGetValue(action, out var g) ? $"Active: {g}" : shellRunning ? "Not available: every chord is taken by another app" : "";
            var field = Kit.Field(b.Gesture, "Ctrl+Alt+K", v =>
            {
                var parts = v.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length == 0 || parts.Any(p => !Gesture.TryParse(p, out _)))
                { _ = Dialogs.InfoAsync(Root, "That shortcut is not valid", "Use modifiers and one key, for example Ctrl+Alt+K or Win+Alt+Space. Separate alternatives with |."); return; }
                Set(x => x.Hotkeys.Bindings.First(z => z.Action == action).Gesture = string.Join("|", parts));
            }, 230);
            var controls = Kit.Row(10, field, Kit.Toggle(b.Enabled, v => Set(x => x.Hotkeys.Bindings.First(z => z.Action == action).Enabled = v)));
            rows.Add(Kit.Setting(ActionLabel(action), state, controls));
        }
        page.Children.Add(Kit.Group([.. rows]));
        var reset = Buttons.Filled("Restore default shortcuts", () => { Set(x => x.Hotkeys.Bindings = HotkeySettings.Defaults()); Refresh(); });
        reset.Margin = new Thickness(0, 10, 0, 0);
        page.Children.Add(reset);
        return page;
    }

    // ------------------------------------------------------------------ Weather

    private StackPanel Weather()
    {
        var w = Cur.Weather;
        var page = Kit.Stack(4, Kit.PageHeader("Weather", "Forecasts come from Open-Meteo. No account, no tracking; only the coordinates of your locations are sent."));
        page.Children.Add(Kit.Group(
            Kit.Setting("Weather", "Show weather in the capsule, the desktop and the Weather app.", Kit.Toggle(w.Enabled && Cur.Privacy.WeatherNetworkConsent, v => Set(x => { x.Weather.Enabled = v; x.Privacy.WeatherNetworkConsent = v; })), Glyphs.Weather),
            Kit.Setting("Temperature", null, Kit.Choice<TemperatureUnit>([(TemperatureUnit.Celsius, "Celsius (°C)"), (TemperatureUnit.Fahrenheit, "Fahrenheit (°F)")], w.TemperatureUnit, v => Set(x => x.Weather.TemperatureUnit = v))),
            Kit.Setting("Wind", null, Kit.Choice<WindUnit>([(WindUnit.Kmh, "km/h"), (WindUnit.Mph, "mph"), (WindUnit.Ms, "m/s"), (WindUnit.Knots, "knots")], w.WindUnit, v => Set(x => x.Weather.WindUnit = v))),
            Kit.Setting("Precipitation", null, Kit.Choice<PrecipUnit>([(PrecipUnit.Mm, "Millimetres"), (PrecipUnit.Inch, "Inches")], w.PrecipUnit, v => Set(x => x.Weather.PrecipUnit = v))),
            Kit.Setting("Refresh every", null, Kit.Choice<int>([(15, "15 minutes"), (30, "30 minutes"), (60, "Hour"), (180, "3 hours")], w.RefreshMinutes, v => Set(x => x.Weather.RefreshMinutes = v)))));

        page.Children.Add(Kit.Section("Locations"));
        var locs = w.Locations ?? [];
        var rows = new List<UIElement>();
        for (int i = 0; i < locs.Count; i++)
        {
            int index = i;
            var l = locs[i];
            bool isDefault = index == Math.Clamp(w.SelectedIndex, 0, Math.Max(0, locs.Count - 1));
            var actions = Kit.Row(6,
                isDefault ? Ui.Text("Default", 12.5, "Vos.AccentText") : Buttons.Filled("Make default", () => { Set(x => x.Weather.SelectedIndex = index); Refresh(); }),
                Buttons.Icon(Glyphs.Delete, () => { Set(x => { x.Weather.Locations!.RemoveAt(index); x.Weather.SelectedIndex = Math.Clamp(x.Weather.SelectedIndex, 0, Math.Max(0, x.Weather.Locations.Count - 1)); }); Refresh(); }, 32, 13, "Remove " + l.Name));
            rows.Add(Kit.Setting(l.Display, $"{l.Country} · {l.Latitude:0.00}, {l.Longitude:0.00}", actions, Glyphs.Pin));
        }
        if (rows.Count == 0) rows.Add(Kit.Setting("No locations yet", "Search for a city below."));
        page.Children.Add(Kit.Group([.. rows]));

        var results = new StackPanel { Spacing = 2, Margin = new Thickness(0, 6, 0, 0) };
        var box = new TextBox { PlaceholderText = "Search for a city", Width = 340 };
        var searchButton = Buttons.Accent("Search", null);
        async void Search()
        {
            var q = box.Text.Trim();
            if (q.Length < 2) return;
            results.Children.Clear();
            results.Children.Add(new ProgressRing { IsActive = true, Width = 20, Height = 20, HorizontalAlignment = HorizontalAlignment.Left });
            try
            {
                var found = await WeatherService.Shared.SearchAsync(q);
                results.Children.Clear();
                if (found.Count == 0) results.Children.Add(Ui.Text("No places found.", 13, "Vos.TextSecondary"));
                foreach (var f in found)
                {
                    var place = f;
                    results.Children.Add(Kit.ListRow(Ui.Icon(Glyphs.Pin, 15, "Vos.TextSecondary"), place.Display, $"{place.Country} · {place.Latitude:0.00}, {place.Longitude:0.00}", Ui.Icon(Glyphs.Add, 13),
                        () => { Set(x => { x.Weather.Locations ??= []; if (x.Weather.Locations.All(z => z.Key != place.Key)) x.Weather.Locations.Add(place); }); Refresh(); }));
                }
            }
            catch (Exception ex)
            {
                results.Children.Clear();
                results.Children.Add(Ui.Text("Search is unavailable right now (offline?).", 13, "Vos.Critical"));
                Log.Warn("Location search failed", ex);
            }
        }
        searchButton.Click += (_, _) => Search();
        Kit.OnEnter(box, Search);
        page.Children.Add(Kit.Card(Kit.Stack(6, Kit.Row(8, box, searchButton), results)));
        return page;
    }

    // ------------------------------------------------------------------ Clipboard

    private StackPanel Clipboard()
    {
        var c = Cur.Clipboard;
        var page = Kit.Stack(4, Kit.PageHeader("Clipboard", "History is kept only on this PC and never leaves it."));
        page.Children.Add(Kit.Group(
            Kit.Setting("Clipboard history", "Open it with the clipboard shortcut.", Kit.Toggle(c.Enabled, v => Set(x => x.Clipboard.Enabled = v)), Glyphs.Clipboard),
            Kit.Setting("Keep images", null, Kit.Toggle(c.CaptureImages, v => Set(x => x.Clipboard.CaptureImages = v)), Glyphs.Picture),
            Kit.Setting("Keep items for", "Pinned items are kept until you unpin them.", Kit.SliderControl(1, 30, c.RetentionDays, 1, v => Set(x => x.Clipboard.RetentionDays = (int)v), 200, v => $"{v:0} days")),
            Kit.Setting("Maximum items", null, Kit.SliderControl(50, 1000, c.MaxItems, 50, v => Set(x => x.Clipboard.MaxItems = (int)v), 200, v => $"{v:0}"))));
        page.Children.Add(Kit.Section("Never record copies from"));
        var excluded = c.ExcludedApps ?? [];
        var rows = excluded.Select(app => (UIElement)Kit.Setting(app, null, Buttons.Icon(Glyphs.Delete, () => { Set(x => x.Clipboard.ExcludedApps!.Remove(app)); Refresh(); }, 32, 13, "Remove " + app))).ToList();
        var add = Kit.Field("", "program.exe", v => { var name = v.Trim(); if (name.Length > 0) { Set(x => { x.Clipboard.ExcludedApps ??= []; if (!x.Clipboard.ExcludedApps.Contains(name, StringComparer.OrdinalIgnoreCase)) x.Clipboard.ExcludedApps.Add(name); }); Refresh(); } }, 200);
        rows.Add(Kit.Setting("Add an app", "Type the program file name and press Enter. Password managers are excluded by default.", add));
        page.Children.Add(Kit.Group([.. rows]));
        var clear = Buttons.Filled("Clear history (keeps pinned items)", async () =>
        {
            if (!await Dialogs.ConfirmAsync(Root, "Clear clipboard history?", "Everything except pinned items is deleted from this PC.", "Clear", destructive: true)) return;
            AppHost.ShellCommand("clipboard:clear");
        });
        clear.Margin = new Thickness(0, 10, 0, 0);
        page.Children.Add(clear);
        return page;
    }

    // ------------------------------------------------------------------ Apps

    private StackPanel Apps()
    {
        var s = Cur;
        var page = Kit.Stack(4, Kit.PageHeader("Apps", "Options for the V-OS apps."));

        page.Children.Add(Kit.Section("Files"));
        page.Children.Add(Kit.Group(
            Kit.Setting("Default view", null, Kit.Choice<string>([("details", "Details"), ("grid", "Icons")], s.Files.View, v => Set(x => x.Files.View = v)), Glyphs.Folder),
            Kit.Setting("Show hidden items", null, Kit.Toggle(s.Files.ShowHidden, v => Set(x => x.Files.ShowHidden = v))),
            Kit.Setting("Show file extensions", null, Kit.Toggle(s.Files.ShowExtensions, v => Set(x => x.Files.ShowExtensions = v))),
            Kit.Setting("Preview pane", null, Kit.Toggle(s.Files.ShowPreview, v => Set(x => x.Files.ShowPreview = v))),
            Kit.Setting("Ask before deleting", "Deleted items go to the Recycle Bin either way.", Kit.Toggle(s.Files.ConfirmDelete, v => Set(x => x.Files.ConfirmDelete = v)))));

        page.Children.Add(Kit.Section("Browser"));
        page.Children.Add(Kit.Group(
            Kit.Setting("Search engine", null, Kit.Choice<string>([("duckduckgo", "DuckDuckGo"), ("google", "Google"), ("bing", "Bing"), ("brave", "Brave Search"), ("startpage", "Startpage"), ("ecosia", "Ecosia")], s.Browser.SearchEngine, v => Set(x => x.Browser.SearchEngine = v)), Glyphs.Globe),
            Kit.Setting("Tracking prevention", null, Kit.Choice<string>([("basic", "Basic"), ("balanced", "Balanced"), ("strict", "Strict")], s.Browser.TrackingPrevention, v => Set(x => x.Browser.TrackingPrevention = v))),
            Kit.Setting("Restore tabs on start", null, Kit.Toggle(s.Browser.RestoreSession, v => Set(x => x.Browser.RestoreSession = v))),
            Kit.Setting("Ask sites not to track me", null, Kit.Toggle(s.Browser.SendDoNotTrack, v => Set(x => x.Browser.SendDoNotTrack = v))),
            Kit.Setting("Offer to save passwords", "Stored by the browser engine, encrypted for your Windows account.", Kit.Toggle(s.Browser.PasswordAutosave, v => Set(x => x.Browser.PasswordAutosave = v))),
            Kit.Setting("Autofill forms", null, Kit.Toggle(s.Browser.AutofillEnabled, v => Set(x => x.Browser.AutofillEnabled = v)))));

        page.Children.Add(Kit.Section("Music"));
        var folders = s.Music.LibraryFolders ?? [];
        var folderRows = folders.Select(f => (UIElement)Kit.Setting(Path.GetFileName(f.TrimEnd('\\')) is { Length: > 0 } n ? n : f, f,
            Buttons.Icon(Glyphs.Delete, () => { Set(x => x.Music.LibraryFolders!.Remove(f)); Refresh(); }, 32, 13, "Remove folder"), Glyphs.Folder)).ToList();
        folderRows.Add(Kit.Setting("Add a music folder", folders.Count == 0 ? "Your Music folder is used until you add one." : null, Buttons.Filled("Add folder", async () =>
        {
            var path = await Pickers.FolderAsync(Frame.Hwnd);
            if (path != null) { Set(x => { x.Music.LibraryFolders ??= []; if (!x.Music.LibraryFolders.Contains(path, StringComparer.OrdinalIgnoreCase)) x.Music.LibraryFolders.Add(path); }); Refresh(); }
        })));
        page.Children.Add(Kit.Group([.. folderRows]));

        page.Children.Add(Kit.Section("Assistant"));
        var keyBox = new PasswordBox { PlaceholderText = s.Assistant.ClaudeApiKey != null ? "Saved (encrypted for your account)" : "sk-ant-…", Width = 260 };
        keyBox.LostFocus += (_, _) =>
        {
            var k = keyBox.Password.Trim();
            if (k.Length == 0) return;
            Set(x => x.Assistant.ClaudeApiKey = Secrets.Protect(k));
            keyBox.Password = "";
            keyBox.PlaceholderText = "Saved (encrypted for your account)";
        };
        page.Children.Add(Kit.Group(
            Kit.Setting("Provider", "On-device answers work offline. Cloud providers need your consent and your own key.",
                Kit.Choice<string>([("local", "On this PC (commands and help)"), ("claude", "Claude (Anthropic)"), ("ollama", "Ollama (local server)")], s.Assistant.Provider, v => Set(x => x.Assistant.Provider = v)), Glyphs.Sparkle),
            Kit.Setting("Allow cloud assistant", "Your questions, and only what you choose to attach, are sent to the provider.", Kit.Toggle(s.Privacy.CloudAssistantConsent, v => Set(x => x.Privacy.CloudAssistantConsent = v))),
            Kit.Setting("Claude API key", "Stored with Windows data protection (DPAPI) for your account only.", Kit.Row(6, keyBox, Buttons.Icon(Glyphs.Delete, () => { Set(x => x.Assistant.ClaudeApiKey = null); Refresh(); }, 32, 13, "Forget key"))),
            Kit.Setting("Claude model", "Opus is the most capable; Haiku is the fastest and cheapest.", Kit.Choice<string>([("claude-opus-5-5", "Claude Opus 5.5"), ("claude-sonnet-5-5", "Claude Sonnet 5.5"), ("claude-haiku-5-5", "Claude Haiku 5.5")], s.Assistant.ClaudeModel, v => Set(x => x.Assistant.ClaudeModel = v))),
            Kit.Setting("Ollama server", "A server that is not on this PC counts as a cloud provider and needs your consent.", Kit.Field(s.Assistant.OllamaEndpoint, "http://localhost:11434", v =>
            {
                var url = v.Trim();
                if (VOS.Core.Assistant.OllamaClient.IsValidEndpoint(url)) Set(x => x.Assistant.OllamaEndpoint = url.TrimEnd('/'));
            })),
            Kit.Setting("Ollama model", null, Kit.Field(s.Assistant.OllamaModel, "llama3.2", v => Set(x => x.Assistant.OllamaModel = v.Trim()), 160)),
            Kit.Setting("Let the assistant run V-OS actions", "Opening apps, changing settings, starting timers. It always asks before anything destructive.", Kit.Toggle(s.Assistant.AllowSystemActions, v => Set(x => x.Assistant.AllowSystemActions = v)))));

        page.Children.Add(Kit.Section("Translate"));
        page.Children.Add(Kit.Group(
            Kit.Setting("Online translation", "Text you translate is sent to the provider below.", Kit.Toggle(s.Privacy.OnlineTranslationConsent, v => Set(x => x.Privacy.OnlineTranslationConsent = v)), Glyphs.Translate),
            Kit.Setting("Provider", null, Kit.Choice<string>([("mymemory", "MyMemory (free, no key)"), ("deepl", "DeepL (your key)"), ("libretranslate", "LibreTranslate")], s.Translate.Provider, v => Set(x => x.Translate.Provider = v))),
            Kit.Setting("Translate into", null, Kit.Choice<string>(Languages, s.Translate.TargetLanguage, v => Set(x => x.Translate.TargetLanguage = v))),
            Kit.Setting("DeepL key", null, Kit.Field(s.Translate.DeepLKey ?? "", "key:fx", v => Set(x => x.Translate.DeepLKey = string.IsNullOrWhiteSpace(v) ? null : v.Trim()))),
            Kit.Setting("LibreTranslate server", null, Kit.Field(s.Translate.LibreTranslateUrl, "https://libretranslate.com", v => Set(x => x.Translate.LibreTranslateUrl = v.Trim())))));

        page.Children.Add(Kit.Section("Gaming"));
        page.Children.Add(Kit.Group(
            Kit.Setting("Switch to the Gaming workspace", "When a game starts from the Gaming app.", Kit.Toggle(s.Gaming.AutoGamingWorkspace, v => Set(x => x.Gaming.AutoGamingWorkspace = v)), Glyphs.Game),
            Kit.Setting("Minimal effects while playing", null, Kit.Toggle(s.Gaming.MinimalEffectsWhilePlaying, v => Set(x => x.Gaming.MinimalEffectsWhilePlaying = v))),
            Kit.Setting("Track play time", "Stored only on this PC.", Kit.Toggle(s.Gaming.TrackPlaytime, v => Set(x => x.Gaming.TrackPlaytime = v)))));
        return page;
    }

    public static readonly (string, string)[] Languages =
    [
        ("en", "English"), ("ru", "Russian"), ("uk", "Ukrainian"), ("de", "German"), ("fr", "French"), ("es", "Spanish"), ("it", "Italian"),
        ("pt", "Portuguese"), ("pl", "Polish"), ("nl", "Dutch"), ("tr", "Turkish"), ("ro", "Romanian"), ("cs", "Czech"), ("sv", "Swedish"),
        ("ja", "Japanese"), ("ko", "Korean"), ("zh", "Chinese"), ("ar", "Arabic"), ("hi", "Hindi"), ("he", "Hebrew"),
    ];

    // ------------------------------------------------------------------ Workspaces

    private StackPanel Workspaces()
    {
        var s = Cur;
        var page = Kit.Stack(4, Kit.PageHeader("Workspaces", "Each workspace can have its own accent, wallpaper, performance profile, focus and dock."));
        foreach (var ws in s.Workspaces)
        {
            var id = ws.Id;
            bool active = id == s.ActiveWorkspaceId;
            var header = Kit.Row(10, Ui.Icon(ws.Icon switch { "home" => Glyphs.Home, "briefcase" => Glyphs.Briefcase, "book" => Glyphs.Book, "gamepad" => Glyphs.Game, "music" => Glyphs.Music, "leaf" => Glyphs.Leaf, _ => Glyphs.Layers }, 16),
                Ui.Text(ws.Name, 15, "Vos.TextPrimary", Tokens.Text, FontWeights.SemiBold));
            var actions = Kit.Row(6,
                active ? Ui.Text("Active", 12.5, "Vos.AccentText") : Buttons.Filled("Switch to", () => { AppHost.ShellCommand("workspace:" + id); Set(x => x.ActiveWorkspaceId = id); Refresh(); }),
                Buttons.Icon(Glyphs.Delete, async () =>
                {
                    if (Cur.Workspaces.Count <= 1) return;
                    if (!await Dialogs.ConfirmAsync(Root, $"Delete “{ws.Name}”?", "Its saved windows and dock layout are removed.", "Delete", destructive: true)) return;
                    Set(x => { x.Workspaces.RemoveAll(z => z.Id == id); if (x.ActiveWorkspaceId == id) x.ActiveWorkspaceId = x.Workspaces[0].Id; });
                    Refresh();
                }, 32, 13, "Delete workspace"));
            var top = new Grid { Padding = new Thickness(16, 12, 16, 8) };
            top.Children.Add(header);
            actions.HorizontalAlignment = HorizontalAlignment.Right;
            top.Children.Add(actions);
            page.Children.Add(Kit.Group(
                top,
                Kit.Setting("Name", null, Kit.Field(ws.Name, "Name", v => { if (v.Trim().Length > 0) Set(x => x.Workspaces.First(z => z.Id == id).Name = v.Trim()); }, 200)),
                Kit.Setting("Accent", null, Kit.Choice<string>([("", "Same as V-OS"), .. AccentPresets.All.Select(p => (p.Id, p.Name))], ws.Accent ?? "", v => Set(x => x.Workspaces.First(z => z.Id == id).Accent = v.Length == 0 ? null : v), 180)),
                Kit.Setting("Performance profile", null, Kit.Choice<int>([(-1, "Same as V-OS"), .. Enum.GetValues<PerformanceProfile>().Select(p => ((int)p, p.ToString()))], ws.Profile is { } pr ? (int)pr : -1,
                    v => Set(x => x.Workspaces.First(z => z.Id == id).Profile = v < 0 ? null : (PerformanceProfile)v), 180)),
                Kit.Setting("Focus", "Turn on Focus while this workspace is active.", Kit.Toggle(ws.Focus, v => Set(x => x.Workspaces.First(z => z.Id == id).Focus = v))),
                Kit.Setting("Hide other windows when switching", null, Kit.Toggle(ws.HideOtherWindows, v => Set(x => x.Workspaces.First(z => z.Id == id).HideOtherWindows = v)))));
            page.Children.Add(new Border { Height = 10 });
        }
        var add = Buttons.Accent("New workspace", () =>
        {
            Set(x => x.Workspaces.Add(new Workspace { Id = "ws-" + Guid.NewGuid().ToString("N")[..6], Name = "New workspace", Icon = "layers" }));
            Refresh();
        });
        page.Children.Add(add);
        return page;
    }

    // ------------------------------------------------------------------ Privacy

    private StackPanel Privacy()
    {
        var p = Cur.Privacy;
        var page = Kit.Stack(4, Kit.PageHeader("Privacy and security", "V-OS collects no telemetry. Nothing leaves this PC unless a switch below allows it."));
        page.Children.Add(Kit.Group(
            Kit.Setting("Weather", "Sends the coordinates of your weather locations to Open-Meteo.", Kit.Toggle(p.WeatherNetworkConsent, v => Set(x => x.Privacy.WeatherNetworkConsent = v)), Glyphs.Weather),
            Kit.Setting("Device location", "Lets Weather use where you are (Windows location services).", Kit.Toggle(p.LocationConsent, v => Set(x => x.Privacy.LocationConsent = v)), Glyphs.Pin),
            Kit.Setting("Cloud assistant", "Questions you ask are sent to the provider you configured.", Kit.Toggle(p.CloudAssistantConsent, v => Set(x => x.Privacy.CloudAssistantConsent = v)), Glyphs.Sparkle),
            Kit.Setting("Online translation", "Text you translate is sent to the translation provider.", Kit.Toggle(p.OnlineTranslationConsent, v => Set(x => x.Privacy.OnlineTranslationConsent = v)), Glyphs.Translate),
            Kit.Setting("Check for updates", "Downloads the signed release manifest. Nothing about you is sent.", Kit.Toggle(p.UpdateChecks, v => Set(x => x.Privacy.UpdateChecks = v)), Glyphs.Download),
            Kit.Setting("Remember recent files", "For search suggestions and the activity center.", Kit.Toggle(p.RecordRecentFiles, v => Set(x => x.Privacy.RecordRecentFiles = v)), Glyphs.History)));

        page.Children.Add(Kit.Section("Connections V-OS makes"));
        var a = Cur.Assistant;
        var endpoints = new List<UIElement>
        {
            Kit.Setting("Open-Meteo (forecast, places, air quality)", "api.open-meteo.com · geocoding-api.open-meteo.com · air-quality-api.open-meteo.com", Status(p.WeatherNetworkConsent)),
            Kit.Setting("Update feed", UpdateClient.ResolveFeed(Cur.Updates.FeedUrl) ?? "Not configured in this build", Status(p.UpdateChecks && UpdateClient.ResolveFeed(Cur.Updates.FeedUrl) != null)),
            Kit.Setting("Claude API", "api.anthropic.com · only when you ask the assistant", Status(p.CloudAssistantConsent && a.Provider == "claude")),
            Kit.Setting("Translation provider", Cur.Translate.Provider switch { "deepl" => "api-free.deepl.com / api.deepl.com", "libretranslate" => Cur.Translate.LibreTranslateUrl, _ => "api.mymemory.translated.net" }, Status(p.OnlineTranslationConsent)),
            Kit.Setting("winget sources", "Only while you use Store", Status(true, "When used")),
            Kit.Setting("Websites you visit", "Browser traffic goes where you browse", Status(true, "When used")),
        };
        page.Children.Add(Kit.Group([.. endpoints]));

        page.Children.Add(Kit.Section("Your data"));
        page.Children.Add(Kit.Group(
            Kit.Setting("Data folder", VosPaths.DataRoot, Buttons.Filled("Open", () => ShellOpen(VosPaths.DataRoot)), Glyphs.Folder),
            Kit.Setting("Caches", $"Icons, thumbnails and forecasts · {Fmt.Bytes(DirSize(VosPaths.Cache))}", Buttons.Filled("Clear", () =>
            {
                foreach (var f in Directory.EnumerateFiles(VosPaths.Cache, "*", SearchOption.AllDirectories)) { try { File.Delete(f); } catch { } }
                Refresh();
            }), Glyphs.Trash),
            Kit.Setting("Crash reports", "Kept on this PC only. Nothing is uploaded.", Buttons.Filled("Open", () => ShellOpen(VosPaths.Crashes)), Glyphs.Warning)));
        return page;

        static TextBlock Status(bool on, string? text = null) => Ui.Text(text ?? (on ? "Allowed" : "Off"), 12.5, on ? "Vos.AccentText" : "Vos.TextTertiary");
    }

    private static long DirSize(string dir)
    {
        try { return new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); } catch { return 0; }
    }

    private static void ShellOpen(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch (Exception ex) { Log.Warn("Could not open " + path, ex); }
    }

    // ------------------------------------------------------------------ Snapshots

    private StackPanel Snapshots()
    {
        var svc = new SnapshotService();
        var page = Kit.Stack(4, Kit.PageHeader("Snapshots", "Save your whole V-OS setup and return to it later. Restoring keeps a snapshot of the current state first."));
        var label = new TextBox { PlaceholderText = "Name, for example “Before experimenting”", Width = 340 };
        var create = Buttons.Accent("Create snapshot", () =>
        {
            svc.Create(string.IsNullOrWhiteSpace(label.Text) ? $"Snapshot {DateTime.Now:g}" : label.Text.Trim());
            Refresh();
        });
        page.Children.Add(Kit.Card(Kit.Row(8, label, create)));
        page.Children.Add(Kit.Section("Saved"));
        var list = svc.List();
        if (list.Count == 0) { page.Children.Add(Kit.Group(Kit.Setting("No snapshots yet", "V-OS also creates one automatically before updates and restores."))); return page; }
        page.Children.Add(Kit.Group([.. list.Select(snap => (UIElement)Kit.Setting(snap.Label, $"{snap.Created.LocalDateTime:g} · V-OS {snap.Version} · {Fmt.Bytes(snap.Size)}",
            Kit.Row(6,
                Buttons.Filled("Restore", async () =>
                {
                    if (!await Dialogs.ConfirmAsync(Root, "Restore this snapshot?", $"Your settings return to “{snap.Label}”. The current setup is saved as a snapshot first.", "Restore")) return;
                    svc.Restore(snap.Id, Store);
                    Refresh();
                }),
                Buttons.Icon(Glyphs.Delete, async () =>
                {
                    if (!await Dialogs.ConfirmAsync(Root, "Delete this snapshot?", "It cannot be recovered.", "Delete", destructive: true)) return;
                    svc.Delete(snap.Id);
                    Refresh();
                }, 32, 13, "Delete snapshot")), Glyphs.History))]));
        return page;
    }

    // ------------------------------------------------------------------ Updates

    private StackPanel Updates()
    {
        var u = Cur.Updates;
        var page = Kit.Stack(4, Kit.PageHeader("Updates", "Updates are signed. V-OS verifies the signature and the package checksum before anything is installed, and keeps the previous version for rollback."));
        var status = Ui.Text(u.LastCheck is { } lc ? $"Last checked {Fmt.Relative(lc)}" : "Not checked yet", 13, "Vos.TextSecondary", wrap: TextWrapping.Wrap);
        var install = Buttons.Accent("Install update", () => StartLauncher("--update"));
        install.Visibility = Visibility.Collapsed;
        var check = Buttons.Filled("Check now", null);
        check.Click += async (_, _) =>
        {
            check.IsEnabled = false;
            status.Text = "Checking…";
            try
            {
                var result = await new UpdateClient().CheckAsync(UpdateClient.ResolveFeed(Cur.Updates.FeedUrl), AppInfo.Version, ReleaseKey.PublicKeyPem);
                status.Text = result.Message + (result.Manifest is { Notes.Length: > 0 } m && result.Available ? "\n\n" + m.Notes : "");
                install.Visibility = result.Available ? Visibility.Visible : Visibility.Collapsed;
                Set(x => x.Updates.LastCheck = DateTimeOffset.Now);
            }
            catch (Exception ex) { status.Text = "The check failed: " + ex.Message; }
            finally { check.IsEnabled = true; }
        };
        var head = new Grid { ColumnSpacing = 16 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(new AppIconView(VosApp.Find("settings")!, 44));
        var info = Kit.Stack(2, Ui.Text($"V-OS {AppInfo.Version}", 18, "Vos.TextPrimary", Tokens.Display, FontWeights.SemiBold), status);
        Grid.SetColumn(info, 1);
        head.Children.Add(info);
        var buttons = Kit.Row(8, install, check);
        Grid.SetColumn(buttons, 2);
        head.Children.Add(buttons);
        page.Children.Add(Kit.Card(head, new Thickness(20)));
        page.Children.Add(Kit.Section("Options"));
        page.Children.Add(Kit.Group(
            Kit.Setting("Check automatically", "Once a day, in the background.", Kit.Toggle(u.AutoCheck && Cur.Privacy.UpdateChecks, v => Set(x => { x.Updates.AutoCheck = v; if (v) x.Privacy.UpdateChecks = true; })), Glyphs.Refresh),
            Kit.Setting("Channel", "Beta builds arrive earlier and may have rough edges.", Kit.Choice<string>([("stable", "Stable"), ("beta", "Beta")], u.Channel, v => Set(x => x.Updates.Channel = v))),
            Kit.Setting("Update source", "Advanced: a different release feed (vos-release.json).", Kit.Field(u.FeedUrl ?? "", AppInfo.DefaultUpdateFeed ?? "https://…/vos-release.json", v => Set(x => x.Updates.FeedUrl = string.IsNullOrWhiteSpace(v) ? null : v.Trim()), 320)),
            Kit.Setting("Repair or roll back", "V-OS Launcher can repair files or return to the previous version.", Buttons.Filled("Open Launcher", () => StartLauncher(null)), Glyphs.Restart)));
        return page;
    }

    private static void StartLauncher(string? arg)
    {
        try
        {
            var psi = new ProcessStartInfo(CommandRunner.VosExe) { UseShellExecute = false };
            psi.ArgumentList.Add("--launcher");
            if (arg != null) psi.ArgumentList.Add(arg);
            Process.Start(psi);
        }
        catch (Exception ex) { Log.Warn("Could not start the launcher", ex); }
    }

    // ------------------------------------------------------------------ About

    private StackPanel About()
    {
        var page = Kit.Stack(4);
        var hero = new Grid { ColumnSpacing = 20 };
        hero.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        hero.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var mark = new TextBlock { Text = "V", FontFamily = Tokens.Instrument, FontSize = 56, FontWeight = FontWeights.SemiBold, Foreground = Ui.B("Vos.Accent"), VerticalAlignment = VerticalAlignment.Center };
        hero.Children.Add(mark);
        var text = Kit.Stack(2,
            Ui.Text("V-OS", 28, "Vos.TextPrimary", Tokens.Display, FontWeights.SemiBold),
            Ui.Text($"Version {AppInfo.Version} · “{AppInfo.Codename}”{(AppInfo.BuildTime != null ? " · built " + AppInfo.BuildTime : "")}", 13, "Vos.TextSecondary"),
            Ui.Text("A desktop environment and app ecosystem on top of Windows.", 13, "Vos.TextSecondary"));
        Grid.SetColumn(text, 1);
        hero.Children.Add(text);
        page.Children.Add(Kit.Card(hero, new Thickness(24)));

        page.Children.Add(Kit.Section("This PC"));
        var hw = HardwareProbe.Probe();
        page.Children.Add(Kit.Group(
            Kit.Setting("Windows", WindowsVersion(), null, Glyphs.Window),
            Kit.Setting("Processor", Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", null) as string ?? $"{hw.LogicalCores} logical cores", null, Glyphs.Cpu),
            Kit.Setting("Memory", $"{hw.RamGb:0} GB", null, Glyphs.Memory),
            Kit.Setting("Graphics", hw.GpuName, null, Glyphs.Monitor)));

        page.Children.Add(Kit.Section("Locations"));
        page.Children.Add(Kit.Group(
            Kit.Setting("Installed in", AppContext.BaseDirectory.TrimEnd('\\'), Buttons.Filled("Open", () => ShellOpen(AppContext.BaseDirectory)), Glyphs.Folder),
            Kit.Setting("Your data", VosPaths.DataRoot, Buttons.Filled("Open", () => ShellOpen(VosPaths.DataRoot)), Glyphs.Folder),
            Kit.Setting("Logs", VosPaths.Logs, Buttons.Filled("Open", () => ShellOpen(VosPaths.Logs)), Glyphs.Document)));

        page.Children.Add(Kit.Section("Recovery"));
        page.Children.Add(Kit.Group(
            Kit.Setting("Restore the Windows desktop", "Shows the Windows taskbar and desktop icons and returns taskbar auto-hide to how it was. Shortcut: Ctrl+Alt+Shift+F12.",
                Buttons.Filled("Restore now", () =>
                {
                    try { Process.Start(new ProcessStartInfo(CommandRunner.VosExe, "--recover") { UseShellExecute = false }); } catch (Exception ex) { Log.Warn("Recovery failed to start", ex); }
                }), Glyphs.Restart)));

        var notices = Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.md");
        page.Children.Add(Kit.Section("Open source"));
        page.Children.Add(Kit.Group(Kit.Setting("Third-party notices", "Windows App SDK, WebView2, Win2D, SQLite, QRCoder and others.",
            File.Exists(notices) ? Buttons.Filled("View", () => ShellOpen(notices)) : null, Glyphs.Book)));
        return page;
    }

    private static string WindowsVersion()
    {
        const string key = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        var product = Registry.GetValue(key, "ProductName", null) as string ?? "Windows";
        var display = Registry.GetValue(key, "DisplayVersion", null) as string;
        var build = Registry.GetValue(key, "CurrentBuild", null) as string;
        var ubr = Registry.GetValue(key, "UBR", null) is int u ? "." + u : "";
        if (Environment.OSVersion.Version.Build >= 22000) product = product.Replace("Windows 10", "Windows 11", StringComparison.Ordinal);
        return $"{product}{(display != null ? " " + display : "")} · build {build}{ubr}";
    }
}
