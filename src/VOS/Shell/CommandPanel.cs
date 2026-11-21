using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using VOS.Core;
using VOS.Core.Search;
using VOS.Core.Settings;
using VOS.Platform.Shell;
using VOS.Platform.Windowing;
using VOS.UI;
using VOS.UI.Theme;
using Windows.System;

namespace VOS.Shell;

/// <summary>V-Command: one box for apps, files, windows, settings, commands, maths, conversions and the web.</summary>
public sealed class CommandPanel : PanelWindow
{
    private sealed record Result(string Title, string Subtitle, string Group, int Score, Func<Task> Run, string? Glyph = null, VosApp? App = null, string? IconSource = null, bool Confirm = false);

    private readonly TextBox _box;
    private readonly StackPanel _results = new() { Spacing = 2 };
    private readonly ScrollViewer _scroll;
    private readonly Grid _library = new();
    private List<Result> _current = [];
    private int _selected;
    private CancellationTokenSource? _fileSearch;

    private static readonly (string title, string section, string keywords)[] SettingsPages =
    [
        ("Appearance", "appearance", "theme dark light accent color wallpaper transparency"),
        ("Desktop and Dock", "desktop", "dock widgets icons size magnification auto-hide clock"),
        ("Performance", "performance", "quality balanced minimal adaptive effects animations battery"),
        ("Workspaces", "workspaces", "workspace gaming work study layout"),
        ("Notifications and Focus", "notifications", "focus do not disturb quiet banners sounds"),
        ("Keyboard shortcuts", "shortcuts", "hotkeys keys shortcut"),
        ("Weather", "weather", "location units celsius fahrenheit"),
        ("Privacy", "privacy", "data location consent clipboard history"),
        ("Assistant", "assistant", "ai claude ollama"),
        ("Updates", "updates", "update version release rollback"),
        ("Backups and restore points", "snapshots", "backup snapshot restore"),
        ("About V-OS", "about", "version diagnostics logs"),
    ];

    private static readonly (string title, string page, string keywords)[] WindowsPages =
    [
        ("Bluetooth & devices", "bluetooth", "bluetooth pair devices mouse keyboard headphones"),
        ("Display", "display", "resolution scale monitor night light"),
        ("Sound", "sound", "audio speakers microphone output input"),
        ("Network & internet", "network", "wifi wi-fi ethernet vpn proxy"),
        ("Power & battery", "powersleep", "battery sleep power energy"),
        ("Apps & features", "appsfeatures", "uninstall installed apps"),
        ("Windows Update", "windowsupdate", "update windows"),
        ("Privacy & security", "privacy", "permissions camera location microphone"),
        ("Storage", "storagesense", "disk space storage cleanup"),
        ("Date & time", "dateandtime", "clock timezone date time"),
    ];

    public CommandPanel(ShellHost host) : base(host, "V-OS Command", 640, 500)
    {
        Placement = PanelPlacement.TopCenter;
        var grid = new Grid { Padding = new Thickness(14, 12, 14, 12), RowSpacing = 10 };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var searchRow = new Grid { ColumnSpacing = 10, Padding = new Thickness(6, 0, 6, 0) };
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var icon = Ui.Icon(Glyphs.Search, 17, "Vos.TextSecondary");
        icon.VerticalAlignment = VerticalAlignment.Center;
        searchRow.Children.Add(icon);
        _box = new TextBox
        {
            PlaceholderText = "Search apps, files and settings, or type a command",
            FontSize = 18, FontFamily = Tokens.Display, BorderThickness = new Thickness(0), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(0, 6, 0, 6),
        };
        foreach (var key in new[] { "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused", "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused" })
            _box.Resources[key] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        Buttons.AutomationName(_box, "Search");
        _box.TextChanged += (_, _) => Update();
        _box.KeyDown += OnKey;
        Grid.SetColumn(_box, 1);
        searchRow.Children.Add(_box);
        grid.Children.Add(searchRow);
        var content = new Grid();
        _scroll = new ScrollViewer { Content = _results, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        content.Children.Add(_scroll);
        content.Children.Add(_library);
        Grid.SetRow(content, 1);
        grid.Children.Add(content);
        Body.Children.Add(grid);
        Body.Children.Insert(0, new Border { Height = 1, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(18, 58, 18, 0), Background = Ui.B("Vos.StrokeSubtle") });
    }

    protected override void OnOpening()
    {
        _box.Text = "";
        Update();
    }

    protected override void OnOpened() => _box.Focus(FocusState.Programmatic);

    private void OnKey(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Down: Select(_selected + 1); e.Handled = true; break;
            case VirtualKey.Up: Select(_selected - 1); e.Handled = true; break;
            case VirtualKey.Enter: if (_current.Count > 0) _ = RunAsync(_current[Math.Clamp(_selected, 0, _current.Count - 1)]); e.Handled = true; break;
        }
    }

    private void Update()
    {
        var q = _box.Text.Trim();
        _fileSearch?.Cancel();
        if (q.Length == 0) { ShowLibrary(); return; }
        _library.Visibility = Visibility.Collapsed;
        _scroll.Visibility = Visibility.Visible;
        var results = new List<Result>();
        var intent = CommandParser.Parse(q);
        if (intent.Kind != IntentKind.None && intent.Kind != IntentKind.OpenApp && intent.Kind != IntentKind.FindFiles)
            results.Add(new Result(intent.Title, intent.Detail.Length > 0 ? intent.Detail : "Command", "Top", 2000, () => Host.Commands.RunIntentAsync(intent, Root.XamlRoot), GlyphFor(intent.Kind), Confirm: intent.RequiresConfirmation));

        string name = intent.Kind == IntentKind.OpenApp ? intent.Arg("name") : q;
        foreach (var a in VosApp.All)
        {
            int s = Math.Max(Fuzzy.Score(name, a.Name), Fuzzy.Score(name, a.Description) / 2);
            if (s > 0) results.Add(new Result(a.Name, a.Description, "V-OS", s + 120, () => { Host.Commands.OpenApp(a.Id); return Task.CompletedTask; }, App: a));
        }
        foreach (var app in AppCatalog.Get())
        {
            int s = Fuzzy.Score(name, app.Name);
            if (s > 0) results.Add(new Result(app.Name, app.IsPackaged ? "App" : "Desktop app", "Apps", s, () => { ShellOps.LaunchApp(app); return Task.CompletedTask; }, IconSource: app.ShellPath));
        }
        foreach (var w in Host.Tracker.Windows)
        {
            int s = Fuzzy.Score(q, w.Title);
            if (s > 300) results.Add(new Result(w.Title, "Switch to window", "Windows", s - 100, () => { WindowTracker.Activate(w.Hwnd); return Task.CompletedTask; }, Glyphs.Window));
        }
        foreach (var ws in Host.Settings.Current.Workspaces)
        {
            int s = Fuzzy.Score(q, ws.Name + " workspace");
            if (s > 400) results.Add(new Result($"{ws.Name} workspace", "Switch environment", "Workspaces", s - 150, () => { Host.Workspaces.Switch(ws.Id); return Task.CompletedTask; }, TopBar.WorkspaceGlyph(ws.Icon)));
        }
        foreach (var (title, section, keywords) in SettingsPages)
        {
            int s = Math.Max(Fuzzy.Score(q, title), keywords.Split(' ').Max(k => Fuzzy.Score(q, k)) - 100);
            if (s > 400) results.Add(new Result(title, "V-OS Settings", "Settings", s - 200, () => { Host.Commands.OpenApp("settings", section); return Task.CompletedTask; }, Glyphs.Settings));
        }
        foreach (var (title, page, keywords) in WindowsPages)
        {
            int s = Math.Max(Fuzzy.Score(q, title), keywords.Split(' ').Max(k => Fuzzy.Score(q, k)) - 100);
            if (s > 450) results.Add(new Result(title, "Windows Settings", "Settings", s - 260, () => { ShellOps.OpenWindowsSettings(page); return Task.CompletedTask; }, Glyphs.Settings));
        }
        results.Add(new Result($"Search the web for “{q}”", SettingsStore.Instance.Current.Browser.SearchEngine, "Web", 1, () => { ShellOps.OpenUrl(CommandRunner.SearchUrl(q)); return Task.CompletedTask; }, Glyphs.Globe));
        _current = results.OrderByDescending(r => r.Score).Take(14).ToList();
        Render();
        // Files come from the Windows Search index asynchronously.
        var term = intent.Kind == IntentKind.FindFiles ? intent.Arg("query") : q;
        if (term.Length >= 2)
        {
            var cts = _fileSearch = new CancellationTokenSource();
            _ = Task.Run(async () =>
            {
                await Task.Delay(140, cts.Token);
                var hits = WindowsSearch.Query(term, 8, cts.Token);
                if (cts.IsCancellationRequested || hits.Count == 0) return;
                UiThread.Post(() =>
                {
                    if (cts.IsCancellationRequested) return;
                    int insert = Math.Min(_current.Count - 1, Math.Max(0, _current.FindIndex(r => r.Group is "Settings" or "Web")));
                    if (insert < 0) insert = _current.Count;
                    _current.InsertRange(insert, hits.Select(h => new Result(h.Name, Path.GetDirectoryName(h.Path) ?? h.Path, "Files", 300,
                        () => { ShellOps.Launch(h.Path); return Task.CompletedTask; }, IconSource: h.Path)));
                    Render();
                });
            }, cts.Token);
        }
    }

    private static string GlyphFor(IntentKind k) => k switch
    {
        IntentKind.Calculate => "", IntentKind.Convert => "", IntentKind.Timer => Glyphs.Timer, IntentKind.Theme => Glyphs.Moon,
        IntentKind.Workspace => Glyphs.Layers, IntentKind.System => Glyphs.Power, IntentKind.Volume => Glyphs.Volume, IntentKind.Brightness => Glyphs.Brightness,
        IntentKind.Focus => Glyphs.Focus, IntentKind.Media => Glyphs.Play, IntentKind.Weather => Glyphs.Weather, IntentKind.Translate => Glyphs.Translate,
        IntentKind.FindLatestDownload => Glyphs.Download, IntentKind.Url => Glyphs.Globe, IntentKind.Path => Glyphs.Folder, IntentKind.Note => Glyphs.Note,
        IntentKind.WebSearch => Glyphs.Globe, IntentKind.Settings => Glyphs.Settings, IntentKind.Performance => Glyphs.Speed, _ => Glyphs.ChevronRight,
    };

    private void Render()
    {
        _results.Children.Clear();
        string? lastGroup = null;
        for (int i = 0; i < _current.Count; i++)
        {
            var r = _current[i];
            if (r.Group != lastGroup && r.Group != "Top")
            {
                _results.Children.Add(new TextBlock { Text = r.Group, FontSize = 11.5, Foreground = Ui.B("Vos.TextTertiary"), Margin = new Thickness(12, lastGroup == null ? 2 : 10, 0, 4) });
                lastGroup = r.Group;
            }
            _results.Children.Add(Row(r, i));
        }
        Select(0);
    }

    private Border Row(Result r, int index)
    {
        var g = new Grid { ColumnSpacing = 12 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        FrameworkElement icon;
        if (r.App != null) icon = new AppIconView(r.App, 28);
        else if (r.IconSource != null)
        {
            var img = new Image { Width = 28, Height = 28 };
            int px = (int)Math.Round(28 * Scale);
            var src = r.IconSource;
            Images.LoadIcon(img, src, Math.Max(32, px));
            icon = img;
        }
        else icon = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(8), Background = Ui.B("Vos.Control"), Child = Ui.Icon(r.Glyph ?? Glyphs.ChevronRight, 14, "Vos.Accent") };
        g.Children.Add(icon);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Ui.Text(r.Title, r.Group == "Top" ? 15 : 14, "Vos.TextPrimary", Tokens.Text, r.Group == "Top" ? FontWeights.SemiBold : FontWeights.Normal));
        if (r.Subtitle.Length > 0) text.Children.Add(Ui.Text(r.Subtitle, 12, "Vos.TextTertiary"));
        Grid.SetColumn(text, 1);
        g.Children.Add(text);
        var border = new Border { Child = g, Padding = new Thickness(10, 7, 10, 7), CornerRadius = new CornerRadius(10), Tag = index, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        border.PointerEntered += (_, _) => Select(index, scroll: false);
        border.Tapped += (_, _) => _ = RunAsync(r);
        Buttons.AutomationName(border, r.Title);
        return border;
    }

    private void Select(int index, bool scroll = true)
    {
        if (_current.Count == 0) return;
        _selected = Math.Clamp(index, 0, _current.Count - 1);
        foreach (var b in _results.Children.OfType<Border>())
        {
            bool sel = b.Tag is int i && i == _selected;
            b.Background = sel ? Ui.B("Vos.ControlHover") : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            if (sel && scroll) b.StartBringIntoView();
        }
    }

    private async Task RunAsync(Result r)
    {
        if (!r.Confirm) Dismiss();
        try { await r.Run(); }
        catch (Exception ex) { Log.Error("Command failed", ex); }
        if (r.Confirm) Dismiss();
    }

    /// <summary>Empty query: the app library (V-OS apps first, then every installed app A–Z).</summary>
    private void ShowLibrary()
    {
        _scroll.Visibility = Visibility.Collapsed;
        _library.Visibility = Visibility.Visible;
        if (_library.Children.Count > 0) return;
        var stack = new StackPanel { Spacing = 10, Padding = new Thickness(6, 2, 6, 6) };
        stack.Children.Add(Ui.Text("V-OS", 11.5, "Vos.TextTertiary"));
        var grid = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, ItemWidth = 98, ItemHeight = 92 };
        foreach (var a in VosApp.All)
        {
            var cell = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center };
            cell.Children.Add(new AppIconView(a, 44) { HorizontalAlignment = HorizontalAlignment.Center });
            var t = Ui.Text(a.Name, 12, "Vos.TextSecondary");
            t.HorizontalAlignment = HorizontalAlignment.Center;
            cell.Children.Add(t);
            var b = Buttons.Subtle(cell, () => { Dismiss(); Host.Commands.OpenApp(a.Id); }, 14, new Thickness(4, 8, 4, 6));
            b.Width = 92;
            Buttons.AutomationName(b, a.Name);
            grid.Children.Add(b);
        }
        stack.Children.Add(grid);
        stack.Children.Add(Ui.Text("All apps", 11.5, "Vos.TextTertiary"));
        var list = new StackPanel { Spacing = 1 };
        foreach (var app in AppCatalog.Get())
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            var img = new Image { Width = 22, Height = 22 };
            row.Children.Add(img);
            row.Children.Add(Ui.Text(app.Name, 13.5));
            var b = Buttons.Subtle(row, () => { Dismiss(); ShellOps.LaunchApp(app); }, 8, new Thickness(10, 6, 10, 6));
            b.HorizontalAlignment = HorizontalAlignment.Stretch;
            b.HorizontalContentAlignment = HorizontalAlignment.Left;
            var path = app.ShellPath;
            b.Loaded += (_, _) =>
            {
                if (img.Source != null) return;
                int px = (int)Math.Round(22 * Scale);
                Images.LoadIcon(img, path, Math.Max(24, px));
            };
            list.Children.Add(b);
        }
        stack.Children.Add(list);
        _library.Children.Add(new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
    }
}
