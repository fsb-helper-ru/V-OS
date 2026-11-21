using System.Globalization;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using VOS.Core;
using VOS.Core.Settings;
using VOS.Platform.Shell;
using VOS.Platform.Sys;
using VOS.UI;
using VOS.UI.Theme;

namespace VOS.Apps.Gaming;

/// <summary>
/// V-OS Gaming: every installed game from Steam, Epic and GOG (plus games you add) in one library, launched through
/// their own launchers. Game mode switches to the Gaming workspace (minimal effects, Focus). Play time is counted
/// while this app is open; V-OS never injects into games or touches anti-cheat.
/// </summary>
public sealed class GamingApp : IAppModule
{
    private sealed class Stats { public DateTimeOffset? LastPlayed { get; set; } public double Minutes { get; set; } }

    public AppFrame Frame { get; }
    private static GamingSettings GS => SettingsStore.Instance.Current.Gaming;
    private readonly Grid _content = new();
    private readonly string _statsPath = Path.Combine(VosPaths.Data, "gaming.json");
    private Dictionary<string, Stats> _stats;
    private List<Game> _games = [];
    private string _filter = "", _source = "all";
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _tracker;
    private readonly ProcessSampler _procs = new();
    private Game? _playing;

    public GamingApp()
    {
        Frame = new AppFrame(VosApp.Find("gaming")!, "Gaming", 820, 560, 1180, 800);
        _stats = VosJson.TryDeserialize<Dictionary<string, Stats>>(AtomicFile.TryReadAllText(_statsPath)) ?? [];
        var layer = new Border { Background = Ui.B("Vos.Layer"), BorderBrush = Ui.B("Vos.StrokeSubtle"), BorderThickness = new Thickness(0, 1, 0, 0), Child = _content };
        Frame.Body.Content = layer;
        _tracker = UiThread.Timer(TimeSpan.FromSeconds(20), Track, repeating: true);
        _tracker.Start();
        Frame.Closed += () => { _tracker.Stop(); SaveStats(); };
        Render(loading: true);
        _ = LoadAsync();
    }

    public void HandleArgs(AppArgs args) { }

    private async Task LoadAsync()
    {
        var found = await Task.Run(GameLibrary.Scan);
        var manual = (GS.ManualGames ?? []).Where(m => File.Exists(m.Path)).Select(m => new Game("manual:" + m.Path.ToLowerInvariant(), m.Name, "Added by you", m.Path, Path.GetDirectoryName(m.Path), m.Path, null));
        _games = found.Concat(manual).OrderByDescending(g => Stat(g).LastPlayed ?? DateTimeOffset.MinValue).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        Render(loading: false);
    }

    private Stats Stat(Game g) => _stats.TryGetValue(g.Key, out var s) ? s : new Stats();

    private void Render(bool loading)
    {
        _content.Children.Clear();
        var root = new Grid { Padding = new Thickness(30, 18, 30, 16), RowSpacing = 14 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var head = new Grid();
        head.Children.Add(Kit.Stack(2, Ui.Text("Library", 28, "Vos.TextPrimary", Tokens.Display, FontWeights.SemiBold),
            Ui.Text(loading ? "Looking for games…" : $"{_games.Count} games · Steam, Epic Games, GOG and games you add", 12.5, "Vos.TextSecondary")));
        bool gameMode = SettingsStore.Instance.Current.ActiveWorkspaceId == "gaming";
        var modeToggle = new ToggleSwitch { IsOn = gameMode, OnContent = "Game mode on", OffContent = "Game mode", MinWidth = 0 };
        modeToggle.Toggled += (_, _) => AppHost.ShellCommand("workspace:" + (modeToggle.IsOn ? "gaming" : "personal"));
        ToolTipService.SetToolTip(modeToggle, "Switches to the Gaming workspace: minimal effects and Focus while you play.");
        var add = Buttons.Filled(Kit.Row(6, Ui.Icon(Glyphs.Add, 13), Ui.Text("Add a game", 13.5)), () => _ = AddGameAsync());
        var actions = Kit.Row(14, modeToggle, add);
        actions.HorizontalAlignment = HorizontalAlignment.Right;
        actions.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(actions);
        root.Children.Add(head);

        var sources = new List<(string, string)> { ("all", "All") };
        sources.AddRange(_games.Select(g => g.Source).Distinct().Select(s => (s, s)));
        var filters = new Grid();
        filters.Children.Add(Kit.Segments(sources, _source, s => { _source = s; Render(false); }));
        var search = Kit.SearchBox("Search games", q => { _filter = q.Trim(); Render(false); }, 240);
        search.Text = _filter;
        search.HorizontalAlignment = HorizontalAlignment.Right;
        filters.Children.Add(search);
        Grid.SetRow(filters, 1);
        root.Children.Add(filters);

        var list = _games.Where(g => (_source == "all" || g.Source == _source) && (_filter.Length == 0 || g.Name.Contains(_filter, StringComparison.CurrentCultureIgnoreCase))).ToList();
        UIElement body;
        if (loading) body = new ProgressRing { IsActive = true, Width = 32, Height = 32 };
        else if (_games.Count == 0) body = Kit.Empty(Glyphs.Game, "No games found", "V-OS looks in Steam, Epic Games and GOG. Add any other game with “Add a game”.", ("Add a game", () => _ = AddGameAsync()));
        else
        {
            var vl = new VirtualList<Game>(CreateTile, BindTile, VirtualList<Game>.GridLayout(176, 300, 14));
            vl.SetItems(list);
            body = vl;
        }
        Grid.SetRow((FrameworkElement)body, 2);
        root.Children.Add(body);
        _content.Children.Add(root);
    }

    private sealed class TileParts { public required Image Cover; public required TextBlock Name, Detail; public required Grid Placeholder; public required TextBlock Letter; }

    private FrameworkElement CreateTile()
    {
        var cover = new Image { Stretch = Stretch.UniformToFill };
        var letter = new TextBlock { FontSize = 44, FontWeight = FontWeights.SemiBold, Foreground = Ui.B("Vos.TextSecondary"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var placeholder = new Grid { Background = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(1, 1), GradientStops = { new GradientStop { Color = Colors2.WithAlpha(ThemeService.Current.Accent, 0x50), Offset = 0 }, new GradientStop { Color = Colors2.WithAlpha(ThemeService.Current.Accent, 0x12), Offset = 1 } } } };
        placeholder.Children.Add(letter);
        var art = new Grid { Height = 232, CornerRadius = new CornerRadius(Tokens.RadiusControl) };
        art.Children.Add(placeholder);
        art.Children.Add(cover);
        var name = Ui.Text("", 13.5, "Vos.TextPrimary", Tokens.Text, FontWeights.SemiBold);
        var detail = Ui.Text("", 11.5, "Vos.TextSecondary");
        var stack = Kit.Stack(6, art, Kit.Stack(1, name, detail));
        var b = Buttons.Subtle(stack, null, Tokens.RadiusCard, new Thickness(6));
        b.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        b.Tag = new TileParts { Cover = cover, Name = name, Detail = detail, Placeholder = placeholder, Letter = letter };
        b.Click += (_, _) => { if (b.DataContext is Game g) Launch(g); };
        b.RightTapped += (_, e) =>
        {
            if (b.DataContext is not Game g) return;
            var m = new MenuFlyout();
            var play = new MenuFlyoutItem { Text = "Play", Icon = new FontIcon { Glyph = Glyphs.Play } }; play.Click += (_, _) => Launch(g); m.Items.Add(play);
            if (g.InstallDir != null) { var folder = new MenuFlyoutItem { Text = "Open install folder", Icon = new FontIcon { Glyph = Glyphs.Folder } }; folder.Click += (_, _) => AppHost.Open("files", ["path=" + g.InstallDir]); m.Items.Add(folder); }
            if (g.Source == "Added by you") { var remove = new MenuFlyoutItem { Text = "Remove from library", Icon = new FontIcon { Glyph = Glyphs.Delete } }; remove.Click += (_, _) => { SettingsStore.Instance.Update(s => s.Gaming.ManualGames?.RemoveAll(x => string.Equals(x.Path, g.Exe, StringComparison.OrdinalIgnoreCase))); _ = LoadAsync(); }; m.Items.Add(remove); }
            m.ShowAt(b, e.GetPosition(b));
            e.Handled = true;
        };
        return b;
    }

    private void BindTile(FrameworkElement e, Game g, int index)
    {
        var p = (TileParts)e.Tag;
        e.DataContext = g;
        p.Name.Text = g.Name;
        var s = Stat(g);
        p.Detail.Text = g.Source + (s.LastPlayed is { } lp ? " · played " + Fmt.Relative(lp).ToLowerInvariant() : "") + (s.Minutes >= 1 ? $" · {Fmt.DurationWords(TimeSpan.FromMinutes(s.Minutes))}" : "");
        p.Letter.Text = g.Name.Length > 0 ? char.ToUpper(g.Name[0], CultureInfo.CurrentCulture).ToString() : "?";
        p.Cover.Source = null;
        if (g.Cover != null) { try { p.Cover.Source = new BitmapImage(new Uri(g.Cover)) { DecodePixelWidth = 340 }; } catch { } }
        else if (g.Exe != null) { p.Cover.Stretch = Stretch.None; Images.LoadIconFor(p.Cover, g.Exe, 96); return; }
        p.Cover.Stretch = Stretch.UniformToFill;
        Buttons.AutomationName(e, "Play " + g.Name);
    }

    private void Launch(Game g)
    {
        if (!ShellOps.Launch(g.LaunchUri)) { _ = Dialogs.InfoAsync(Frame.Root.XamlRoot, "The game could not be started", $"{g.Source} may need to be installed or signed in."); return; }
        _playing = g;
        if (GS.AutoGamingWorkspace) AppHost.ShellCommand("workspace:gaming");
        var s = Stat(g);
        s.LastPlayed = DateTimeOffset.Now;
        _stats[g.Key] = s;
        SaveStats();
    }

    /// <summary>Counts play time while a process runs from the game's folder (sampled every 20 s; no hooks, no injection).</summary>
    private void Track()
    {
        if (!GS.TrackPlaytime || _playing?.InstallDir is not { } dir) return;
        var game = _playing;
        _ = Task.Run(() =>
        {
            bool running = _procs.Sample().Any(p => p.Path != null && p.Path.StartsWith(dir, StringComparison.OrdinalIgnoreCase));
            UiThread.Post(() =>
            {
                if (!running) { if (DateTimeOffset.Now - (Stat(game).LastPlayed ?? DateTimeOffset.Now) > TimeSpan.FromMinutes(3)) _playing = null; return; }
                var s = Stat(game);
                s.Minutes += 20 / 60.0;
                _stats[game.Key] = s;
                SaveStats();
            });
        });
    }

    private void SaveStats() { try { AtomicFile.WriteAllText(_statsPath, VosJson.Serialize(_stats)); } catch { } }

    private async Task AddGameAsync()
    {
        var exe = await Pickers.FileAsync(Frame.Hwnd, Windows.Storage.Pickers.PickerLocationId.ComputerFolder, ".exe", ".lnk", ".url");
        if (exe == null) return;
        var name = Path.GetFileNameWithoutExtension(exe);
        SettingsStore.Instance.Update(s => { s.Gaming.ManualGames ??= []; if (s.Gaming.ManualGames.All(m => !string.Equals(m.Path, exe, StringComparison.OrdinalIgnoreCase))) s.Gaming.ManualGames.Add(new ManualGame { Name = name, Path = exe }); });
        await LoadAsync();
    }
}
