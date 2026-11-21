using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using VOS.Core;
using VOS.Core.Notifications;
using VOS.Platform.Shell;
using VOS.UI;
using VOS.UI.Theme;

namespace VOS.Apps.Store;

/// <summary>
/// V-OS Store: discover, install, update and remove apps through the Windows Package Manager (winget), the same
/// trusted catalogue Microsoft maintains. Nothing installs until you click; progress shows in the capsule too.
/// </summary>
public sealed class StoreApp : IAppModule
{
    private sealed record Featured(string Id, string Name, string About, string Category, string Glyph);

    private static readonly Featured[] s_featured =
    [
        new("Mozilla.Firefox", "Firefox", "Fast, private web browser", "Browsers", Glyphs.Globe),
        new("Google.Chrome", "Google Chrome", "Web browser by Google", "Browsers", Glyphs.Globe),
        new("Brave.Brave", "Brave", "Browser that blocks ads and trackers", "Browsers", Glyphs.Shield),
        new("Microsoft.PowerToys", "PowerToys", "Power-user utilities for Windows", "Utilities", Glyphs.Wand),
        new("7zip.7zip", "7-Zip", "File archiver with high compression", "Utilities", Glyphs.Archive),
        new("voidtools.Everything", "Everything", "Instant file name search", "Utilities", Glyphs.Search),
        new("ShareX.ShareX", "ShareX", "Screen capture and recording", "Utilities", Glyphs.Camera),
        new("Microsoft.VisualStudioCode", "Visual Studio Code", "Code editor", "Development", Glyphs.Document),
        new("Git.Git", "Git", "Version control", "Development", Glyphs.Flow),
        new("Microsoft.WindowsTerminal", "Windows Terminal", "Modern terminal", "Development", Glyphs.Keyboard),
        new("Notepad++.Notepad++", "Notepad++", "Lightweight text and code editor", "Development", Glyphs.Note),
        new("VideoLAN.VLC", "VLC media player", "Plays almost any media file", "Media", Glyphs.Video),
        new("Spotify.Spotify", "Spotify", "Music streaming", "Media", Glyphs.Music),
        new("OBSProject.OBSStudio", "OBS Studio", "Recording and live streaming", "Media", Glyphs.Camera),
        new("GIMP.GIMP", "GIMP", "Image editor", "Media", Glyphs.Picture),
        new("Valve.Steam", "Steam", "PC games", "Gaming", Glyphs.Game),
        new("EpicGames.EpicGamesLauncher", "Epic Games Launcher", "PC games", "Gaming", Glyphs.Game),
        new("Discord.Discord", "Discord", "Voice, video and text chat", "Communication", Glyphs.Mic),
        new("Telegram.TelegramDesktop", "Telegram", "Messaging", "Communication", Glyphs.Send),
        new("Zoom.Zoom", "Zoom", "Video meetings", "Communication", Glyphs.Video),
        new("TheDocumentFoundation.LibreOffice", "LibreOffice", "Office suite", "Productivity", Glyphs.Document),
        new("Obsidian.Obsidian", "Obsidian", "Knowledge base on local Markdown files", "Productivity", Glyphs.Book),
        new("KeePassXCTeam.KeePassXC", "KeePassXC", "Offline password manager", "Productivity", Glyphs.Lock),
        new("SumatraPDF.SumatraPDF", "SumatraPDF", "Lightweight PDF reader", "Productivity", Glyphs.Reader),
    ];

    public AppFrame Frame { get; }
    private readonly NavShell _nav = new();
    private Dictionary<string, WinGetPackage> _installed = new(StringComparer.OrdinalIgnoreCase);
    private List<WinGetPackage> _upgrades = [];
    private readonly HashSet<string> _busy = new(StringComparer.OrdinalIgnoreCase);
    private string _query = "";
    private List<WinGetPackage>? _results;
    private bool _available = true, _loaded;

    public StoreApp()
    {
        Frame = new AppFrame(VosApp.Find("store")!, "Store", 780, 520, 1100, 760);
        var search = Kit.SearchBox("Search apps", q => _query = q.Trim());
        Kit.OnEnter(search, () => { if (_query.Length > 1) { _results = null; _nav.Invalidate("search"); _nav.Select("search"); _ = SearchAsync(); } });
        _nav.SidebarTop.Children.Add(search);
        _nav.Add(new NavShell.Item("discover", "Discover", Glyphs.Star));
        _nav.Add(new NavShell.Item("search", "Search results", Glyphs.Search));
        _nav.Add(new NavShell.Item("updates", "Updates", Glyphs.Download));
        _nav.Add(new NavShell.Item("installed", "Installed", Glyphs.Apps));
        _nav.CachePages = false;
        _nav.PageFactory = Page;
        Frame.Body.Content = _nav;
        _nav.Select("discover");
        _ = LoadAsync();
    }

    public void HandleArgs(AppArgs args)
    {
        if (args.Get("search") is { Length: > 1 } q) { _query = q; _results = null; _nav.Select("search"); _ = SearchAsync(); }
        else if (args.Get("section") is { } s) _nav.Select(s);
    }

    private async Task LoadAsync()
    {
        _available = await WinGet.IsAvailableAsync();
        if (!_available) { _loaded = true; _nav.Select(_nav.Current ?? "discover"); return; }
        try
        {
            var installed = WinGet.InstalledAsync();
            var upgrades = WinGet.UpgradesAsync();
            _installed = (await installed).GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            _upgrades = await upgrades;
        }
        catch (Exception ex) { Log.Warn("winget list failed", ex); }
        _loaded = true;
        _nav.Select(_nav.Current ?? "discover");
    }

    private async Task SearchAsync()
    {
        var q = _query;
        try { var r = await WinGet.SearchAsync(q); if (q == _query) { _results = r; if (_nav.Current == "search") _nav.Select("search"); } }
        catch (Exception ex) { Log.Warn("winget search failed", ex); _results = []; if (_nav.Current == "search") _nav.Select("search"); }
    }

    // ------------------------------------------------------------------ pages

    private UIElement Page(string id)
    {
        if (!_available) return Kit.Empty(Glyphs.Store, "Windows Package Manager is not installed", "V-OS Store uses winget, which comes with App Installer from Microsoft. Install or update App Installer, then open Store again.",
            ("Get App Installer", () => ShellOps.Launch("ms-windows-store://pdp/?productid=9NBLGGH4NNS1")));
        return id switch
        {
            "search" => Kit.Scroll(SearchPage()),
            "updates" => Kit.Scroll(UpdatesPage()),
            "installed" => Kit.Scroll(InstalledPage()),
            _ => Kit.Scroll(DiscoverPage()),
        };
    }

    private StackPanel DiscoverPage()
    {
        var page = Kit.Stack(4, Kit.PageHeader("Discover", "Popular, trusted apps from the winget catalogue."));
        if (!_loaded) page.Children.Add(Kit.Row(8, new ProgressRing { IsActive = true, Width = 16, Height = 16 }, Ui.Text("Checking what is installed…", 12.5, "Vos.TextSecondary")));
        if (_upgrades.Count > 0)
            page.Children.Add(Kit.Info($"{_upgrades.Count} update{(_upgrades.Count == 1 ? "" : "s")} available", "Keep your apps current for security fixes.", InfoBarSeverity.Informational));
        foreach (var group in s_featured.GroupBy(f => f.Category))
        {
            page.Children.Add(Kit.Section(group.Key));
            var grid = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, ItemWidth = 330, ItemHeight = 76 };
            foreach (var f in group) grid.Children.Add(AppTile(f.Id, f.Name, f.About, f.Glyph));
            page.Children.Add(grid);
        }
        return page;
    }

    private StackPanel SearchPage()
    {
        var page = Kit.Stack(4, Kit.PageHeader(_query.Length > 0 ? $"Results for “{_query}”" : "Search", _query.Length == 0 ? "Type in the search box and press Enter." : null));
        if (_query.Length == 0) return page;
        if (_results == null) { page.Children.Add(Kit.Row(8, new ProgressRing { IsActive = true, Width = 16, Height = 16 }, Ui.Text("Searching the catalogue…", 12.5, "Vos.TextSecondary"))); return page; }
        if (_results.Count == 0) { page.Children.Add(Kit.Empty(Glyphs.Search, "No apps found", "Try another name.")); return page; }
        var rows = _results.Take(60).Select(r => (UIElement)PackageRow(r.Id, r.Name, $"{r.Id} · {r.Version}")).ToArray();
        page.Children.Add(Kit.Group(rows));
        return page;
    }

    private StackPanel UpdatesPage()
    {
        var page = Kit.Stack(4, Kit.PageHeader("Updates", "Apps with a newer version in the catalogue."));
        if (!_loaded) { page.Children.Add(Kit.Row(8, new ProgressRing { IsActive = true, Width = 16, Height = 16 }, Ui.Text("Checking…", 12.5, "Vos.TextSecondary"))); return page; }
        if (_upgrades.Count == 0) { page.Children.Add(Kit.Empty(Glyphs.Check, "Everything is up to date", "V-OS checks again every time you open Store.")); return page; }
        var all = Buttons.Accent($"Update all ({_upgrades.Count})", async () => { foreach (var u in _upgrades.ToList()) await ApplyAsync("upgrade", u.Id, u.Name); });
        all.Margin = new Thickness(0, 0, 0, 10);
        page.Children.Add(all);
        page.Children.Add(Kit.Group([.. _upgrades.Select(u => (UIElement)PackageRow(u.Id, u.Name, $"{u.Version} → {u.Available}", upgrade: true))]));
        return page;
    }

    private StackPanel InstalledPage()
    {
        var page = Kit.Stack(4, Kit.PageHeader("Installed", "Apps Windows knows about. Removing an app runs its own uninstaller."));
        if (!_loaded) { page.Children.Add(Kit.Row(8, new ProgressRing { IsActive = true, Width = 16, Height = 16 }, Ui.Text("Reading the list…", 12.5, "Vos.TextSecondary"))); return page; }
        var list = _installed.Values.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        var filter = Kit.SearchBox("Filter installed apps", _ => { }, 320);
        var rows = new StackPanel();
        void Fill(string q)
        {
            rows.Children.Clear();
            var items = list.Where(p => q.Length == 0 || p.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase) || p.Id.Contains(q, StringComparison.OrdinalIgnoreCase)).Take(300).ToList();
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0) rows.Children.Add(new Border { Height = 1, Background = Ui.B("Vos.StrokeSubtle") });
                var p = items[i];
                rows.Children.Add(PackageRow(p.Id, p.Name, $"{p.Version}{(p.Source.Length > 0 ? " · " + p.Source : "")}", uninstall: true));
            }
        }
        filter.TextChanged += (_, _) => Fill(filter.Text.Trim());
        Fill("");
        filter.Margin = new Thickness(0, 0, 0, 10);
        page.Children.Add(filter);
        page.Children.Add(Kit.Card(rows, new Thickness(0)));
        return page;
    }

    // ------------------------------------------------------------------ rows and actions

    private Button AppTile(string id, string name, string about, string glyph)
    {
        var g = new Grid { ColumnSpacing = 12 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(Kit.GlyphBadge(glyph, 40));
        var text = Kit.Stack(1, Ui.Text(name, 14, "Vos.TextPrimary", Tokens.Text, FontWeights.SemiBold), Ui.Text(about, 12, "Vos.TextSecondary"));
        text.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(text, 1); g.Children.Add(text);
        var installedId = MatchInstalled(id, name)?.Id ?? id;
        var action = ActionButton(id, name, upgrade: _upgrades.Any(u => u.Id.Equals(installedId, StringComparison.OrdinalIgnoreCase)), uninstall: false);
        action.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(action, 2); g.Children.Add(action);
        var b = Buttons.Subtle(g, null, Tokens.RadiusControl, new Thickness(12, 10, 12, 10));
        b.Background = Ui.B("Vos.Card");
        b.Margin = new Thickness(0, 0, 8, 8);
        b.HorizontalAlignment = HorizontalAlignment.Stretch;
        b.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        Buttons.AutomationName(b, name);
        return b;
    }

    private Grid PackageRow(string id, string name, string detail, bool upgrade = false, bool uninstall = false)
    {
        var action = ActionButton(id, name, upgrade, uninstall);
        return Kit.Setting(name, detail, action, Glyphs.Apps);
    }

    /// <summary>
    /// The installed package for a catalogue entry: the exact ID, a variant of it (winget may correlate an existing
    /// install to e.g. Google.Chrome.Beta.EXE), or the same display name.
    /// </summary>
    private WinGetPackage? MatchInstalled(string id, string name) =>
        _installed.TryGetValue(id, out var exact) ? exact
        : _installed.Values.FirstOrDefault(p => p.Id.StartsWith(id + ".", StringComparison.OrdinalIgnoreCase))
          ?? _installed.Values.FirstOrDefault(p => p.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase));

    private FrameworkElement ActionButton(string id, string name, bool upgrade, bool uninstall)
    {
        if (_busy.Contains(id)) return Kit.Row(8, new ProgressRing { IsActive = true, Width = 16, Height = 16 }, Ui.Text("Working…", 12.5, "Vos.TextSecondary"));
        var match = MatchInstalled(id, name);
        if (match != null && !uninstall) id = match.Id;
        bool installed = match != null;
        if (uninstall) return Buttons.Filled("Uninstall", async () =>
        {
            if (!await Dialogs.ConfirmAsync(Frame.Root.XamlRoot, $"Uninstall {name}?", "The app's own uninstaller runs. Your files made with it are not deleted.", "Uninstall", destructive: true)) return;
            await ApplyAsync("uninstall", id, name);
        });
        if (upgrade || _upgrades.Any(u => u.Id.Equals(id, StringComparison.OrdinalIgnoreCase))) return Buttons.Accent("Update", () => _ = ApplyAsync("upgrade", id, name));
        if (installed) return Ui.Text("Installed", 12.5, "Vos.TextTertiary");
        return Buttons.Accent("Install", () => _ = ApplyAsync("install", id, name));
    }

    private async Task ApplyAsync(string verb, string id, string name)
    {
        if (!_busy.Add(id)) return;
        _nav.Select(_nav.Current ?? "discover");
        var activity = "store-" + id;
        string title = verb switch { "upgrade" => $"Updating {name}", "uninstall" => $"Removing {name}", _ => $"Installing {name}" };
        AppHost.UpdateActivity(new LiveActivity { Id = activity, AppId = "store", Kind = ActivityKind.Download, Title = title, Subtitle = "Starting…", Glyph = Glyphs.Store });
        var progress = new Progress<double>(p => AppHost.UpdateActivity(new LiveActivity { Id = activity, AppId = "store", Kind = ActivityKind.Download, Title = title, Subtitle = $"{p:P0}", Progress = p, Glyph = Glyphs.Store }));
        var (ok, message) = await WinGet.ApplyAsync(verb, id, progress);
        AppHost.EndActivity(activity);
        AppHost.Notify(new VosNotification
        {
            AppId = "store", AppName = "Store", Glyph = Glyphs.Store, Priority = ok ? NotificationPriority.Normal : NotificationPriority.High,
            Title = ok ? verb switch { "upgrade" => $"{name} updated", "uninstall" => $"{name} removed", _ => $"{name} installed" } : $"{name}: something went wrong",
            Body = ok ? "Ready." : message,
        });
        _busy.Remove(id);
        await LoadAsync();
    }
}
