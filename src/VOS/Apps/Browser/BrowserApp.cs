using System.Globalization;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using VOS.Core;
using VOS.Core.Data;
using VOS.Core.Notifications;
using VOS.Core.Settings;
using VOS.UI;
using VOS.UI.Theme;
using Windows.System;
using Windows.UI.Core;

namespace VOS.Apps.Browser;

/// <summary>Bookmarks and history in SQLite (history is never written by private windows).</summary>
public sealed class BrowserStore
{
    private readonly Db _db = Db.ForModule("browser");

    public BrowserStore()
    {
        _db.Migrate(
            "CREATE TABLE bookmarks(url TEXT PRIMARY KEY, title TEXT NOT NULL, created TEXT NOT NULL);" +
            "CREATE TABLE history(url TEXT PRIMARY KEY, title TEXT NOT NULL, visited TEXT NOT NULL, visits INTEGER NOT NULL DEFAULT 1);" +
            "CREATE INDEX history_visited ON history(visited);");
        _db.Execute("DELETE FROM history WHERE visited < $cut", ("$cut", DateTimeOffset.Now.AddDays(-90).ToString("o")));
    }

    public void Visit(string url, string title) =>
        _db.Execute("INSERT INTO history(url,title,visited,visits) VALUES($u,$t,$v,1) ON CONFLICT(url) DO UPDATE SET title=CASE WHEN $t <> '' THEN $t ELSE title END, visited=$v, visits=visits+1",
            ("$u", url), ("$t", title), ("$v", DateTimeOffset.Now.ToString("o")));

    public List<(string url, string title)> TopSites(int n) => _db.Query("SELECT url,title FROM history ORDER BY visits DESC, visited DESC LIMIT $n", r => (Db.Str(r, "url")!, Db.Str(r, "title") ?? ""), ("$n", n));
    public List<(string url, string title, DateTimeOffset visited)> History(string q, int n) =>
        _db.Query("SELECT url,title,visited FROM history WHERE url LIKE $q OR title LIKE $q ORDER BY visited DESC LIMIT $n",
            r => (Db.Str(r, "url")!, Db.Str(r, "title") ?? "", DateTimeOffset.Parse(Db.Str(r, "visited")!, CultureInfo.InvariantCulture)), ("$q", "%" + q + "%"), ("$n", n));
    public void ClearHistory() => _db.Execute("DELETE FROM history");
    public bool IsBookmarked(string url) => _db.Scalar<long>("SELECT COUNT(*) FROM bookmarks WHERE url=$u", ("$u", url)) > 0;
    public void Bookmark(string url, string title) => _db.Execute("INSERT OR REPLACE INTO bookmarks(url,title,created) VALUES($u,$t,$c)", ("$u", url), ("$t", title), ("$c", DateTimeOffset.Now.ToString("o")));
    public void Unbookmark(string url) => _db.Execute("DELETE FROM bookmarks WHERE url=$u", ("$u", url));
    public List<(string url, string title)> Bookmarks() => _db.Query("SELECT url,title FROM bookmarks ORDER BY created DESC", r => (Db.Str(r, "url")!, Db.Str(r, "title") ?? ""));
}

/// <summary>
/// V-OS Browser: WebView2 (the Chromium engine that ships with Windows) with vertical tabs, a native new-tab page,
/// bookmarks, history, tracking prevention, downloads that show in the capsule, find, zoom, print and private windows.
/// Runs in its own process so a heavy page can never slow the other apps.
/// </summary>
public sealed class BrowserApp : IAppModule
{
    private sealed class Tab
    {
        public required WebView2 View;
        public required Grid Row;
        public required TextBlock Title;
        public required Image Icon;
        public string Url = "vos://newtab";
        public bool Ready;
        public bool Audio;
    }

    public AppFrame Frame { get; }
    private static BrowserSettings S => SettingsStore.Instance.Current.Browser;
    private readonly BrowserStore _store = new();
    private readonly bool _private;
    private readonly List<Tab> _tabs = [];
    private Tab? _current;
    private readonly StackPanel _tabList = new() { Spacing = 2 };
    private readonly Grid _viewHost = new();
    private readonly Grid _newTabPage = new();
    private readonly TextBox _address = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _back, _forward, _reload, _star;
    private readonly ProgressBar _progress = new() { IsIndeterminate = true, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Bottom, Height = 2, MinHeight = 2 };
    private static CoreWebView2Environment? s_env;
    private static readonly SemaphoreSlim s_envGate = new(1, 1);

    public BrowserApp() : this(isPrivate: false) { }

    public BrowserApp(bool isPrivate)
    {
        _private = isPrivate;
        Frame = new AppFrame(VosApp.Find("browser")!, isPrivate ? "Private browsing" : "Browser", 760, 520, 1280, 820);

        // ---- tab sidebar
        var side = new Grid { Padding = new Thickness(8, 2, 6, 10), RowSpacing = 6 };
        side.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        side.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        side.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var newTab = Kit.ListRow(Ui.Icon(Glyphs.Add, 14), "New tab", null, Ui.Text("Ctrl+T", 11, "Vos.TextTertiary"), () => _ = NewTabAsync(null), 38);
        side.Children.Add(newTab);
        var tabScroll = new ScrollViewer { Content = _tabList, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(tabScroll, 1); side.Children.Add(tabScroll);
        if (_private)
        {
            var badge = Kit.Card(Kit.Row(8, Ui.Icon(Glyphs.Shield, 14, "Vos.AccentText"), Ui.Text("Private: no history, cookies cleared on close", 11.5, "Vos.TextSecondary", wrap: TextWrapping.Wrap)), new Thickness(10, 8, 10, 8));
            Grid.SetRow(badge, 2); side.Children.Add(badge);
        }

        // ---- toolbar
        _back = Buttons.Icon(Glyphs.Back, () => { if (_current?.View.CanGoBack == true) _current.View.GoBack(); }, 34, 13, "Back (Alt+Left)");
        _forward = Buttons.Icon(Glyphs.Forward, () => { if (_current?.View.CanGoForward == true) _current.View.GoForward(); }, 34, 13, "Forward (Alt+Right)");
        _reload = Buttons.Icon(Glyphs.Refresh, () => _current?.View.Reload(), 34, 13, "Reload (F5)");
        _star = Buttons.Icon(Glyphs.Star, ToggleBookmark, 34, 14, "Bookmark this page (Ctrl+D)");
        var menuButton = Buttons.Icon(Glyphs.More, null, 34, 14, "Menu");
        menuButton.Flyout = Menu();
        _address.PlaceholderText = "Search or enter address";
        Buttons.AutomationName(_address, "Address");
        _address.GotFocus += (_, _) => _address.SelectAll();
        _address.KeyDown += (s, e) => { if (e.Key == VirtualKey.Enter) { Go(_address.Text); e.Handled = true; } else if (e.Key == VirtualKey.Escape) { _address.Text = Display(_current?.Url); _current?.View.Focus(FocusState.Programmatic); e.Handled = true; } };
        var bar = new Grid { ColumnSpacing = 6, Padding = new Thickness(10, 0, 10, 8) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.Children.Add(Kit.Row(2, _back, _forward, _reload));
        var addressHost = new Grid();
        addressHost.Children.Add(_address);
        addressHost.Children.Add(_progress);
        Grid.SetColumn(addressHost, 1); bar.Children.Add(addressHost);
        var right = Kit.Row(2, _star, Buttons.Icon(Glyphs.Download, () => _current?.View.CoreWebView2?.OpenDefaultDownloadDialog(), 34, 14, "Downloads (Ctrl+J)"), menuButton);
        Grid.SetColumn(right, 2); bar.Children.Add(right);

        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        content.Children.Add(bar);
        var pageHost = new Grid();
        pageHost.Children.Add(_viewHost);
        pageHost.Children.Add(_newTabPage);
        var layer = new Border { Background = Ui.B("Vos.Layer"), BorderBrush = Ui.B("Vos.StrokeSubtle"), BorderThickness = new Thickness(1, 1, 0, 0), CornerRadius = new CornerRadius(Tokens.RadiusWindow, 0, 0, 0), Child = pageHost };
        Grid.SetRow(layer, 1); content.Children.Add(layer);

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(S.VerticalTabs ? 236 : 0) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(side);
        Grid.SetColumn(content, 1); root.Children.Add(content);
        Frame.Body.Content = root;
        Frame.Root.KeyDown += OnKey;
        Frame.Closed += OnClosed;
        _ = StartAsync();
    }

    public void HandleArgs(AppArgs args)
    {
        foreach (var url in args.All("url").Concat(args.All("open")))
            _ = NewTabAsync(url.StartsWith("http", StringComparison.OrdinalIgnoreCase) || url.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ? url : File.Exists(url) ? new Uri(url).AbsoluteUri : url);
    }

    private async Task StartAsync()
    {
        try { await EnvironmentAsync(); }
        catch (Exception ex)
        {
            Log.Error("WebView2 runtime unavailable", ex);
            _newTabPage.Children.Clear();
            _newTabPage.Children.Add(Kit.Empty(Glyphs.Warning, "The web engine is not available", "V-OS Browser uses Microsoft Edge WebView2, which is part of Windows 11. Install or repair the WebView2 Runtime from Microsoft, then open Browser again.",
                ("Get WebView2", () => VOS.Platform.Shell.ShellOps.OpenUrl("https://developer.microsoft.com/microsoft-edge/webview2/"))));
            return;
        }
        var session = S.RestoreSession && !_private ? VosJson.TryDeserialize<List<string>>(AtomicFile.TryReadAllText(SessionPath)) : null;
        if (session is { Count: > 0 }) foreach (var u in session.Take(20)) await NewTabAsync(u);
        else if (_tabs.Count == 0) await NewTabAsync(null);
    }

    private static string SessionPath => Path.Combine(VosPaths.State, "browser-session.json");

    private static async Task<CoreWebView2Environment> EnvironmentAsync()
    {
        await s_envGate.WaitAsync();
        try
        {
            if (s_env != null) return s_env;
            var options = new CoreWebView2EnvironmentOptions { AreBrowserExtensionsEnabled = false, Language = CultureInfo.CurrentUICulture.Name };
            s_env = await CoreWebView2Environment.CreateWithOptionsAsync(null, Path.Combine(VosPaths.Data, "browser"), options);
            return s_env;
        }
        finally { s_envGate.Release(); }
    }

    // ------------------------------------------------------------------ tabs

    private async Task NewTabAsync(string? url)
    {
        var view = new WebView2 { Visibility = Visibility.Collapsed, DefaultBackgroundColor = ThemeService.Current.Palette.Base };
        var icon = new Image { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
        var title = Ui.Text("New tab", 13);
        title.VerticalAlignment = VerticalAlignment.Center;
        var row = new Grid { ColumnSpacing = 10, Padding = new Thickness(10, 7, 4, 7), CornerRadius = new CornerRadius(Tokens.RadiusControl), Background = Clear() };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(icon);
        Grid.SetColumn(title, 1); row.Children.Add(title);
        var tab = new Tab { View = view, Row = row, Title = title, Icon = icon };
        var close = Buttons.Icon(Glyphs.Close, () => CloseTab(tab), 26, 10, "Close tab (Ctrl+W)");
        Grid.SetColumn(close, 2); row.Children.Add(close);
        row.Tapped += (_, _) => Activate(tab);
        row.PointerEntered += (_, _) => { if (tab != _current) row.Background = Ui.B("Vos.ControlHover"); };
        row.PointerExited += (_, _) => { if (tab != _current) row.Background = Clear(); };
        Buttons.AutomationName(row, "Tab");
        _tabs.Add(tab);
        _tabList.Children.Add(row);
        _viewHost.Children.Add(view);
        Activate(tab);

        try
        {
            var env = await EnvironmentAsync();
            if (_private)
            {
                var options = env.CreateCoreWebView2ControllerOptions();
                options.IsInPrivateModeEnabled = true;
                await view.EnsureCoreWebView2Async(env, options);
            }
            else await view.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex) { Log.Warn("Tab could not start", ex); return; }
        var core = view.CoreWebView2;
        Configure(core);
        tab.Ready = true;
        core.DocumentTitleChanged += (_, _) => { tab.Title.Text = core.DocumentTitle.Length > 0 ? core.DocumentTitle : Display(core.Source); if (tab == _current) Frame.Title = $"{tab.Title.Text} — {(_private ? "Private browsing" : "Browser")}"; Buttons.AutomationName(row, tab.Title.Text); };
        core.FaviconChanged += async (_, _) =>
        {
            try
            {
                using var stream = await core.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png);
                var bmp = new BitmapImage();
                await bmp.SetSourceAsync(stream);
                tab.Icon.Source = bmp;
            }
            catch { }
        };
        core.SourceChanged += (_, _) => { tab.Url = core.Source; if (tab == _current) UpdateChrome(); };
        core.NavigationStarting += (s, e) => { if (tab == _current) _progress.Visibility = Visibility.Visible; };
        core.NavigationCompleted += (s, e) =>
        {
            if (tab == _current) { _progress.Visibility = Visibility.Collapsed; UpdateChrome(); }
            if (e.IsSuccess && !_private && core.Source.StartsWith("http", StringComparison.OrdinalIgnoreCase)) _store.Visit(core.Source, core.DocumentTitle);
        };
        core.HistoryChanged += (_, _) => { if (tab == _current) UpdateChrome(); };
        core.NewWindowRequested += (s, e) => { e.Handled = true; _ = NewTabAsync(e.Uri); };
        core.IsDocumentPlayingAudioChanged += (_, _) => { tab.Audio = core.IsDocumentPlayingAudio; tab.Title.FontStyle = tab.Audio ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal; };
        core.DownloadStarting += (s, e) => TrackDownload(e.DownloadOperation);
        core.ContainsFullScreenElementChanged += (_, _) => Frame.AppWindow.SetPresenter(core.ContainsFullScreenElement ? Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen : Microsoft.UI.Windowing.AppWindowPresenterKind.Default);
        core.ProcessFailed += (s, e) => { Log.Warn("Browser process failed", null, new { e.ProcessFailedKind }); if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessExited) view.Reload(); };
        if (!string.IsNullOrEmpty(url) && url != "vos://newtab") Navigate(tab, url);
        else ShowNewTab(tab);
    }

    private void Configure(CoreWebView2 core)
    {
        var st = core.Settings;
        st.IsPasswordAutosaveEnabled = S.PasswordAutosave && !_private;
        st.IsGeneralAutofillEnabled = S.AutofillEnabled && !_private;
        st.AreDefaultContextMenusEnabled = true;
        st.AreBrowserAcceleratorKeysEnabled = true;
        st.IsStatusBarEnabled = true;
        st.AreDevToolsEnabled = true;
        core.Profile.PreferredColorScheme = ThemeService.Current.IsDark ? CoreWebView2PreferredColorScheme.Dark : CoreWebView2PreferredColorScheme.Light;
        core.Profile.PreferredTrackingPreventionLevel = S.TrackingPrevention switch
        {
            "basic" => CoreWebView2TrackingPreventionLevel.Basic,
            "strict" => CoreWebView2TrackingPreventionLevel.Strict,
            _ => CoreWebView2TrackingPreventionLevel.Balanced,
        };
        if (S.SendDoNotTrack)
        {
            // Ask sites not to track: DNT and Global Privacy Control headers on top-level documents only (cheap).
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.Document);
            core.WebResourceRequested += (s, e) => { try { e.Request.Headers.SetHeader("DNT", "1"); e.Request.Headers.SetHeader("Sec-GPC", "1"); } catch { } };
        }
    }

    private void Activate(Tab tab)
    {
        _current = tab;
        foreach (var t in _tabs)
        {
            bool on = t == tab;
            t.View.Visibility = on && t.Url != "vos://newtab" ? Visibility.Visible : Visibility.Collapsed;
            t.Row.Background = on ? Ui.B("Vos.Control") : Clear();
        }
        _newTabPage.Visibility = tab.Url == "vos://newtab" ? Visibility.Visible : Visibility.Collapsed;
        if (tab.Url == "vos://newtab") ShowNewTab(tab);
        UpdateChrome();
        Frame.Title = $"{tab.Title.Text} — {(_private ? "Private browsing" : "Browser")}";
    }

    private void CloseTab(Tab tab)
    {
        int i = _tabs.IndexOf(tab);
        if (i < 0) return;
        _tabs.RemoveAt(i);
        _tabList.Children.Remove(tab.Row);
        _viewHost.Children.Remove(tab.View);
        try { tab.View.Close(); } catch { }
        if (_tabs.Count == 0) { Frame.Window.Close(); return; }
        if (tab == _current) Activate(_tabs[Math.Min(i, _tabs.Count - 1)]);
    }

    private void UpdateChrome()
    {
        var tab = _current;
        bool page = tab != null && tab.Url != "vos://newtab";
        _back.IsEnabled = page && tab!.Ready && tab.View.CanGoBack;
        _forward.IsEnabled = page && tab!.Ready && tab.View.CanGoForward;
        _reload.IsEnabled = page;
        _star.IsEnabled = page && !_private;
        if (!ReferenceEquals(FocusManager.GetFocusedElement(Frame.Root.XamlRoot), _address)) _address.Text = page ? Display(tab!.Url) : "";
        bool marked = page && _store.IsBookmarked(tab!.Url);
        _star.Content = Ui.Icon(marked ? Glyphs.StarFilled : Glyphs.Star, 14, marked ? "Vos.AccentText" : "Vos.TextPrimary");
    }

    // ------------------------------------------------------------------ navigation

    private void Go(string input)
    {
        if (_current == null) { _ = NewTabAsync(input); return; }
        Navigate(_current, input);
        _current.View.Focus(FocusState.Programmatic);
    }

    private void Navigate(Tab tab, string input)
    {
        var url = Resolve(input);
        tab.Url = url;
        tab.View.Visibility = tab == _current ? Visibility.Visible : Visibility.Collapsed;
        if (tab == _current) _newTabPage.Visibility = Visibility.Collapsed;
        if (tab.Ready) { try { tab.View.CoreWebView2.Navigate(url); } catch (ArgumentException) { tab.View.CoreWebView2.Navigate(SearchUrl(input)); } }
        else tab.View.Source = new Uri(url);
        UpdateChrome();
    }

    /// <summary>Address or search: anything that looks like a host, a URL or a local path is opened; the rest is searched.</summary>
    public static string Resolve(string input)
    {
        var s = input.Trim();
        if (s.Length == 0) return "about:blank";
        if (Uri.TryCreate(s, UriKind.Absolute, out var abs) && abs.Scheme is "http" or "https" or "file" or "edge" or "about") return abs.AbsoluteUri;
        if (System.IO.Path.IsPathRooted(s) && (File.Exists(s) || Directory.Exists(s))) return new Uri(s).AbsoluteUri;
        bool hostLike = !s.Contains(' ') && (s.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) || System.Text.RegularExpressions.Regex.IsMatch(s, @"^[\w-]+(\.[\w-]+)+(:\d+)?(/.*)?$"));
        return hostLike ? "https://" + s : SearchUrl(s);
    }

    private static string SearchUrl(string q) => S.SearchEngine switch
    {
        "google" => "https://www.google.com/search?q=",
        "bing" => "https://www.bing.com/search?q=",
        "brave" => "https://search.brave.com/search?q=",
        "startpage" => "https://www.startpage.com/do/search?q=",
        "ecosia" => "https://www.ecosia.org/search?q=",
        _ => "https://duckduckgo.com/?q=",
    } + Uri.EscapeDataString(q);

    private static string Display(string? url)
    {
        if (string.IsNullOrEmpty(url) || url == "vos://newtab") return "";
        return url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? url[8..].TrimEnd('/') : url;
    }

    // ------------------------------------------------------------------ new tab page

    private void ShowNewTab(Tab tab)
    {
        if (tab != _current) return;
        _newTabPage.Children.Clear();
        var page = new StackPanel { Spacing = 22, MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(24) };
        var hour = DateTime.Now.Hour;
        var name = SettingsStore.Instance.Current.UserName;
        page.Children.Add(new TextBlock { Text = (hour < 12 ? "Good morning" : hour < 18 ? "Good afternoon" : "Good evening") + (name.Length > 0 ? ", " + name : ""), FontSize = 30, FontFamily = Tokens.Display, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Foreground = Ui.B("Vos.TextPrimary") });
        var search = new TextBox { PlaceholderText = $"Search with {EngineName()} or enter an address", FontSize = 16, Padding = new Thickness(16, 12, 16, 12), CornerRadius = new CornerRadius(24) };
        Buttons.AutomationName(search, "Search the web");
        Kit.OnEnter(search, () => { if (search.Text.Trim().Length > 0) Navigate(tab, search.Text); });
        page.Children.Add(search);
        var shortcuts = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, ItemWidth = 112, ItemHeight = 96, HorizontalAlignment = HorizontalAlignment.Center };
        var sites = _store.Bookmarks().Take(8).ToList();
        if (sites.Count < 8) sites.AddRange(_store.TopSites(16).Where(t => sites.All(s => s.url != t.url)).Take(8 - sites.Count));
        foreach (var (url, title) in sites)
        {
            var host = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host.Replace("www.", "") : url;
            var letter = new Grid { Width = 44, Height = 44 };
            letter.Children.Add(new Border { CornerRadius = new CornerRadius(14), Background = Ui.B("Vos.AccentSubtle") });
            letter.Children.Add(new TextBlock { Text = host.Length > 0 ? char.ToUpperInvariant(host[0]).ToString() : "?", FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = Ui.B("Vos.AccentText"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            var label = new TextBlock { Text = title.Length > 0 ? title : host, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center, Width = 96, Foreground = Ui.B("Vos.TextSecondary") };
            var target = url;
            var b = Buttons.Subtle(Kit.Stack(8, letter, label), () => Navigate(tab, target), 14, new Thickness(6, 10, 6, 8));
            ((StackPanel)b.Content).Children[0].SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            Buttons.AutomationName(b, title.Length > 0 ? title : host);
            shortcuts.Children.Add(b);
        }
        if (sites.Count > 0) page.Children.Add(shortcuts);
        else page.Children.Add(new TextBlock { Text = "Sites you visit often and your bookmarks will appear here.", FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, Foreground = Ui.B("Vos.TextTertiary") });
        _newTabPage.Children.Add(page);
        _newTabPage.Visibility = Visibility.Visible;
        search.Loaded += (_, _) => search.Focus(FocusState.Programmatic);
    }

    private static string EngineName() => S.SearchEngine switch { "google" => "Google", "bing" => "Bing", "brave" => "Brave", "startpage" => "Startpage", "ecosia" => "Ecosia", _ => "DuckDuckGo" };

    // ------------------------------------------------------------------ menu, bookmarks, history

    private MenuFlyout Menu()
    {
        var m = new MenuFlyout();
        MenuFlyoutItem Item(string text, string glyph, Action a, string? accel = null) { var i = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph }, KeyboardAcceleratorTextOverride = accel ?? "" }; i.Click += (_, _) => a(); m.Items.Add(i); return i; }
        Item("New tab", Glyphs.Add, () => _ = NewTabAsync(null), "Ctrl+T");
        if (!_private) Item("New private window", Glyphs.Shield, () => AppHost.Open("browser-private", []), "Ctrl+Shift+N");
        m.Items.Add(new MenuFlyoutSeparator());
        Item("Bookmarks", Glyphs.Bookmark, () => _ = ShowListAsync("Bookmarks", _store.Bookmarks().Select(b => (b.url, b.title, "")).ToList()));
        if (!_private) Item("History", Glyphs.History, () => _ = ShowListAsync("History", _store.History("", 200).Select(h => (h.url, h.title, h.visited.LocalDateTime.ToString("g", CultureInfo.CurrentCulture))).ToList()), "Ctrl+H");
        Item("Downloads", Glyphs.Download, () => _current?.View.CoreWebView2?.OpenDefaultDownloadDialog(), "Ctrl+J");
        m.Items.Add(new MenuFlyoutSeparator());
        Item("Find on page", Glyphs.Search, FindOnPage, "Ctrl+F");
        Item("Print…", Glyphs.Document, () => _current?.View.CoreWebView2?.ShowPrintUI(CoreWebView2PrintDialogKind.Browser), "Ctrl+P");
        m.Items.Add(new MenuFlyoutSeparator());
        Item("Clear browsing data…", Glyphs.Delete, () => _ = ClearDataAsync());
        Item("Developer tools", Glyphs.Wand, () => _current?.View.CoreWebView2?.OpenDevToolsWindow(), "F12");
        Item("Browser settings", Glyphs.Settings, () => AppHost.Open("settings", ["section=apps"]));
        return m;
    }

    private void ToggleBookmark()
    {
        if (_current is not { } t || t.Url == "vos://newtab" || _private) return;
        if (_store.IsBookmarked(t.Url)) _store.Unbookmark(t.Url); else _store.Bookmark(t.Url, t.Title.Text);
        UpdateChrome();
    }

    private async Task ShowListAsync(string title, List<(string url, string title, string detail)> items)
    {
        var search = new TextBox { PlaceholderText = "Search " + title.ToLowerInvariant() };
        var list = new StackPanel { Spacing = 1 };
        ContentDialog? dlg = null;
        void Fill(string q)
        {
            list.Children.Clear();
            foreach (var (url, t, d) in items.Where(i => q.Length == 0 || i.url.Contains(q, StringComparison.OrdinalIgnoreCase) || i.title.Contains(q, StringComparison.OrdinalIgnoreCase)).Take(150))
            {
                var target = url;
                list.Children.Add(Kit.ListRow(null, t.Length > 0 ? t : url, d.Length > 0 ? $"{Display(url)} · {d}" : Display(url), null, () => { dlg?.Hide(); _ = NewTabAsync(target); }, 44));
            }
            if (list.Children.Count == 0) list.Children.Add(Ui.Text(items.Count == 0 ? $"No {title.ToLowerInvariant()} yet." : "Nothing matches.", 13, "Vos.TextSecondary"));
        }
        search.TextChanged += (_, _) => Fill(search.Text.Trim());
        Fill("");
        dlg = new ContentDialog
        {
            XamlRoot = Frame.Root.XamlRoot, Title = title, CloseButtonText = "Close", RequestedTheme = ThemeService.Current.ElementTheme,
            Content = Kit.Stack(10, search, new ScrollViewer { Content = list, MaxHeight = 460, Width = 520 }),
        };
        if (title == "History") { dlg.SecondaryButtonText = "Clear history"; dlg.SecondaryButtonClick += (_, _) => _store.ClearHistory(); }
        await dlg.ShowAsync();
    }

    private async Task ClearDataAsync()
    {
        var core = _current?.View.CoreWebView2;
        if (core == null) return;
        var cookies = new CheckBox { Content = "Cookies and site data (signs you out of websites)", IsChecked = false };
        var cache = new CheckBox { Content = "Cached images and files", IsChecked = true };
        var history = new CheckBox { Content = "Browsing history", IsChecked = true };
        var passwords = new CheckBox { Content = "Saved passwords and form data", IsChecked = false };
        var dlg = new ContentDialog { XamlRoot = Frame.Root.XamlRoot, Title = "Clear browsing data", Content = Kit.Stack(8, history, cache, cookies, passwords), PrimaryButtonText = "Clear", CloseButtonText = "Cancel", RequestedTheme = ThemeService.Current.ElementTheme };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        CoreWebView2BrowsingDataKinds kinds = 0;
        if (cache.IsChecked == true) kinds |= CoreWebView2BrowsingDataKinds.DiskCache | CoreWebView2BrowsingDataKinds.CacheStorage;
        if (cookies.IsChecked == true) kinds |= CoreWebView2BrowsingDataKinds.AllSite;
        if (passwords.IsChecked == true) kinds |= CoreWebView2BrowsingDataKinds.PasswordAutosave | CoreWebView2BrowsingDataKinds.GeneralAutofill;
        if (history.IsChecked == true) { kinds |= CoreWebView2BrowsingDataKinds.BrowsingHistory; _store.ClearHistory(); }
        if (kinds != 0) await core.Profile.ClearBrowsingDataAsync(kinds);
    }

    private void FindOnPage()
    {
        var core = _current?.View.CoreWebView2;
        if (core == null || _current!.Url == "vos://newtab") return;
        try
        {
            var options = core.Environment.CreateFindOptions();
            options.FindTerm = "";
            options.SuppressDefaultFindDialog = false;
            _ = core.Find.StartAsync(options);
        }
        catch (Exception ex) { Log.Debug("Find unavailable", ex.Message); }
    }

    // ------------------------------------------------------------------ downloads → capsule

    private void TrackDownload(CoreWebView2DownloadOperation op)
    {
        var id = "download-" + Guid.NewGuid().ToString("N")[..8];
        var name = Path.GetFileName(op.ResultFilePath);
        void Update()
        {
            double? p = op.TotalBytesToReceive is long total && total > 0 ? (double)op.BytesReceived / total : null;
            AppHost.UpdateActivity(new LiveActivity { Id = id, AppId = "browser", Kind = ActivityKind.Download, Title = name, Subtitle = p is double d ? $"{d:P0} · {Fmt.Bytes(op.BytesReceived)}" : Fmt.Bytes(op.BytesReceived), Progress = p, Glyph = Glyphs.Download });
        }
        DateTime last = DateTime.MinValue;
        op.BytesReceivedChanged += (_, _) => { if (DateTime.Now - last > TimeSpan.FromMilliseconds(500)) { last = DateTime.Now; Update(); } };
        op.StateChanged += (_, _) =>
        {
            if (op.State == CoreWebView2DownloadState.InProgress) return;
            AppHost.EndActivity(id);
            if (op.State == CoreWebView2DownloadState.Completed)
                AppHost.Notify(new VosNotification
                {
                    AppId = "browser", AppName = "Browser", Title = "Download complete", Body = name, Glyph = Glyphs.Download,
                    Command = "file:" + op.ResultFilePath,
                    Actions = [new NotificationAction { Label = "Open", Command = "file:" + op.ResultFilePath }, new NotificationAction { Label = "Show in folder", Command = "reveal:" + op.ResultFilePath }],
                });
        };
        Update();
    }

    // ------------------------------------------------------------------ keyboard, lifecycle

    private static bool Down(VirtualKey k) => InputKeyboardSource.GetKeyStateForCurrentThread(k).HasFlag(CoreVirtualKeyStates.Down);

    private void OnKey(object sender, KeyRoutedEventArgs e)
    {
        bool ctrl = Down(VirtualKey.Control), shift = Down(VirtualKey.Shift), alt = Down(VirtualKey.Menu);
        switch (e.Key)
        {
            case VirtualKey.T when ctrl: _ = NewTabAsync(null); break;
            case VirtualKey.W when ctrl: if (_current != null) CloseTab(_current); break;
            case VirtualKey.N when ctrl && shift && !_private: AppHost.Open("browser-private", []); break;
            case VirtualKey.L when ctrl: _address.Focus(FocusState.Keyboard); _address.SelectAll(); break;
            case VirtualKey.D when ctrl: ToggleBookmark(); break;
            case VirtualKey.F when ctrl: FindOnPage(); break;
            case VirtualKey.Tab when ctrl && _tabs.Count > 1 && _current != null: Activate(_tabs[(_tabs.IndexOf(_current) + (shift ? _tabs.Count - 1 : 1)) % _tabs.Count]); break;
            case VirtualKey.Left when alt: if (_current?.View.CanGoBack == true) _current.View.GoBack(); break;
            case VirtualKey.Right when alt: if (_current?.View.CanGoForward == true) _current.View.GoForward(); break;
            case VirtualKey.F5: _current?.View.Reload(); break;
            default: return;
        }
        e.Handled = true;
    }

    private void OnClosed()
    {
        if (!_private && S.RestoreSession)
        {
            var urls = _tabs.Select(t => t.Url).Where(u => u != "vos://newtab" && u.StartsWith("http", StringComparison.OrdinalIgnoreCase)).ToList();
            try { AtomicFile.WriteAllText(SessionPath, VosJson.Serialize(urls)); } catch { }
        }
        if (_private) _ = ClearPrivateAsync(_tabs.FirstOrDefault()?.View.CoreWebView2);
        foreach (var t in _tabs) { try { t.View.Close(); } catch { } }
    }

    private static async Task ClearPrivateAsync(CoreWebView2? core)
    {
        // InPrivate data lives in memory and is discarded with the last private controller; this is a belt-and-braces clear.
        try { if (core != null) await core.Profile.ClearBrowsingDataAsync(); } catch { }
    }

    private static SolidColorBrush Clear() => new(Microsoft.UI.Colors.Transparent);
}
