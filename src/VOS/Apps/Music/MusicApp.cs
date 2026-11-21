using System.Globalization;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using VOS.Core;
using VOS.Core.Data;
using VOS.Core.Settings;
using VOS.Platform.Shell;
using VOS.UI;
using VOS.UI.Theme;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;

namespace VOS.Apps.Music;

public sealed class Track
{
    public required string Path { get; init; }
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    public string AlbumArtist { get; set; } = "";
    public int Number { get; set; }
    public int Year { get; set; }
    public string Genre { get; set; } = "";
    public TimeSpan Duration { get; set; }
    public long Size { get; set; }
    public long Modified { get; set; }
    public string DisplayArtist => Artist.Length > 0 ? Artist : AlbumArtist.Length > 0 ? AlbumArtist : "Unknown artist";
    public string DisplayAlbum => Album.Length > 0 ? Album : "Unknown album";
    public string GroupArtist => AlbumArtist.Length > 0 ? AlbumArtist : DisplayArtist;
}

/// <summary>Music library index: tags are read once with the Windows property system and cached in SQLite; only new or changed files are read again.</summary>
public sealed class MusicLibrary
{
    private static readonly HashSet<string> s_ext = new(StringComparer.OrdinalIgnoreCase) { ".mp3", ".m4a", ".aac", ".flac", ".wav", ".wma", ".ogg", ".opus", ".aiff", ".aif", ".alac" };
    private readonly Db _db = Db.ForModule("music");

    public MusicLibrary()
    {
        _db.Migrate("CREATE TABLE tracks(path TEXT PRIMARY KEY, title TEXT, artist TEXT, album TEXT, albumartist TEXT, number INTEGER, year INTEGER, genre TEXT, duration INTEGER, size INTEGER, modified INTEGER);");
    }

    public static IReadOnlyList<string> Folders()
    {
        var configured = SettingsStore.Instance.Current.Music.LibraryFolders ?? [];
        return configured.Count > 0 ? configured : [Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)];
    }

    public List<Track> Load() => _db.Query("SELECT * FROM tracks", r => new Track
    {
        Path = Db.Str(r, "path")!, Title = Db.Str(r, "title") ?? "", Artist = Db.Str(r, "artist") ?? "", Album = Db.Str(r, "album") ?? "", AlbumArtist = Db.Str(r, "albumartist") ?? "",
        Number = (int)Db.Long(r, "number"), Year = (int)Db.Long(r, "year"), Genre = Db.Str(r, "genre") ?? "", Duration = TimeSpan.FromMilliseconds(Db.Long(r, "duration")),
        Size = Db.Long(r, "size"), Modified = Db.Long(r, "modified"),
    });

    /// <summary>Brings the index up to date with the folders. Reports (done, total) while reading tags.</summary>
    public async Task<List<Track>> ScanAsync(IProgress<(int done, int total)>? progress, CancellationToken ct)
    {
        var known = Load().ToDictionary(t => t.Path, StringComparer.OrdinalIgnoreCase);
        var files = await Task.Run(() =>
        {
            var list = new List<FileInfo>();
            var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = System.IO.FileAttributes.System };
            foreach (var folder in Folders().Where(Directory.Exists))
                foreach (var f in new DirectoryInfo(folder).EnumerateFiles("*", opts))
                    if (s_ext.Contains(f.Extension)) list.Add(f);
            return list;
        }, ct);
        var changed = files.Where(f => !known.TryGetValue(f.FullName, out var t) || t.Size != f.Length || t.Modified != f.LastWriteTimeUtc.Ticks).ToList();
        int done = 0;
        var gate = new SemaphoreSlim(4);
        var read = await Task.WhenAll(changed.Select(async f =>
        {
            await gate.WaitAsync(ct);
            try { return await ReadAsync(f); }
            finally { gate.Release(); progress?.Report((Interlocked.Increment(ref done), changed.Count)); }
        }));
        var present = new HashSet<string>(files.Select(f => f.FullName), StringComparer.OrdinalIgnoreCase);
        _db.InTransaction((c, tx) =>
        {
            foreach (var t in read)
                Db.Exec(c, "INSERT OR REPLACE INTO tracks(path,title,artist,album,albumartist,number,year,genre,duration,size,modified) VALUES($p,$t,$a,$al,$aa,$n,$y,$g,$d,$s,$m)", tx,
                    ("$p", t.Path), ("$t", t.Title), ("$a", t.Artist), ("$al", t.Album), ("$aa", t.AlbumArtist), ("$n", t.Number), ("$y", t.Year), ("$g", t.Genre),
                    ("$d", (long)t.Duration.TotalMilliseconds), ("$s", t.Size), ("$m", t.Modified));
            foreach (var gone in known.Keys.Where(k => !present.Contains(k)))
                Db.Exec(c, "DELETE FROM tracks WHERE path=$p", tx, ("$p", gone));
        });
        return Load();
    }

    private static async Task<Track> ReadAsync(FileInfo f)
    {
        var t = new Track { Path = f.FullName, Title = Path.GetFileNameWithoutExtension(f.Name), Size = f.Length, Modified = f.LastWriteTimeUtc.Ticks };
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(f.FullName);
            var m = await file.Properties.GetMusicPropertiesAsync();
            if (!string.IsNullOrWhiteSpace(m.Title)) t.Title = m.Title.Trim();
            t.Artist = (m.Artist ?? "").Trim();
            t.Album = (m.Album ?? "").Trim();
            t.AlbumArtist = (m.AlbumArtist ?? "").Trim();
            t.Number = (int)m.TrackNumber;
            t.Year = (int)m.Year;
            t.Genre = m.Genre.FirstOrDefault() ?? "";
            t.Duration = m.Duration;
        }
        catch (Exception ex) { Log.Debug("Tags unreadable: " + f.Name, ex.Message); }
        return t;
    }
}

/// <summary>
/// V-OS Music: your local library with a queue, shuffle and repeat. Playback goes through Windows media (with the
/// system media controls), so the track also appears in the V-OS capsule and on the lock screen.
/// </summary>
public sealed class MusicApp : IAppModule
{
    public AppFrame Frame { get; }
    private static MusicSettings MS => SettingsStore.Instance.Current.Music;
    private readonly MusicLibrary _library = new();
    private readonly MediaPlayer _player = new() { AudioCategory = MediaPlayerAudioCategory.Media };
    private readonly NavShell _nav = new(220);
    private List<Track> _tracks = [];
    private List<Track> _queue = [];
    private int _index = -1;
    private string _filter = "";
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _positionTimer;
    private bool _seeking;
    private CancellationTokenSource? _scanCts;

    // now-playing bar
    private readonly Image _barArt = new() { Width = 48, Height = 48, Stretch = Stretch.UniformToFill };
    private readonly TextBlock _barTitle = Ui.Text("Nothing playing", 14, "Vos.TextPrimary", Tokens.Text, FontWeights.SemiBold);
    private readonly TextBlock _barArtist = Ui.Text("", 12.5, "Vos.TextSecondary");
    private readonly Button _play, _shuffle, _repeat;
    private readonly Slider _seek = new() { Minimum = 0, Maximum = 1, IsThumbToolTipEnabled = false, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _elapsed = Ui.Text("0:00", 11.5, "Vos.TextTertiary", Tokens.Instrument), _total = Ui.Text("0:00", 11.5, "Vos.TextTertiary", Tokens.Instrument);
    private readonly TextBlock _status = Ui.Text("", 12, "Vos.TextTertiary");

    public MusicApp()
    {
        Frame = new AppFrame(VosApp.Find("music")!, "Music", 820, 560, 1180, 780);
        _player.Volume = Math.Clamp(MS.Volume, 0, 1);
        _player.IsLoopingEnabled = MS.Repeat == "one";
        _player.MediaEnded += (_, _) => UiThread.Post(OnEnded);
        _player.MediaFailed += (_, e) => UiThread.Post(() => { _status.Text = "This file could not be played: " + e.ErrorMessage; Next(); });
        _player.PlaybackSession.PlaybackStateChanged += (_, _) => UiThread.Post(UpdatePlayButton);
        _player.CommandManager.NextReceived += (_, e) => { e.Handled = true; UiThread.Post(Next); };
        _player.CommandManager.PreviousReceived += (_, e) => { e.Handled = true; UiThread.Post(Previous); };
        _player.CommandManager.NextBehavior.EnablingRule = MediaCommandEnablingRule.Always;
        _player.CommandManager.PreviousBehavior.EnablingRule = MediaCommandEnablingRule.Always;

        _nav.Add(new NavShell.Item("songs", "Songs", Glyphs.Music));
        _nav.Add(new NavShell.Item("albums", "Albums", Glyphs.Grid));
        _nav.Add(new NavShell.Item("artists", "Artists", Glyphs.Mic));
        _nav.Add(new NavShell.Item("queue", "Now playing", Glyphs.List));
        _nav.Add(new NavShell.Item("folders", "Library folders", Glyphs.Folder), footer: true);
        var search = Kit.SearchBox("Search music", q => { _filter = q.Trim(); _nav.Invalidate("songs"); _nav.Invalidate("albums"); _nav.Invalidate("artists"); if (_nav.Current is "songs" or "albums" or "artists") _nav.Select(_nav.Current!); });
        _nav.SidebarTop.Children.Add(search);
        _nav.PageFactory = Page;
        _nav.CachePages = true;

        _play = Buttons.Accent(Ui.Icon(Glyphs.Play, 16, "Vos.OnAccent"), PlayPause, 20, new Thickness(0));
        _play.Width = _play.Height = 40;
        Buttons.AutomationName(_play, "Play or pause");
        _shuffle = Buttons.Icon(Glyphs.Shuffle, ToggleShuffle, 34, 14, "Shuffle");
        _repeat = Buttons.Icon(Glyphs.Repeat, CycleRepeat, 34, 14, "Repeat");
        StyleModeButtons();

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(_nav);
        var bar = NowPlayingBar();
        Grid.SetRow(bar, 1);
        root.Children.Add(bar);
        Frame.Body.Content = root;

        _positionTimer = UiThread.Timer(TimeSpan.FromMilliseconds(500), UpdatePosition, repeating: true);
        _positionTimer.Start();
        Frame.Closed += () => { _positionTimer.Stop(); _scanCts?.Cancel(); _player.Pause(); _player.Dispose(); };
        _tracks = _library.Load();
        _nav.Select(_tracks.Count > 0 ? "songs" : "folders");
        _ = ScanAsync();
    }

    public void HandleArgs(AppArgs args)
    {
        var files = args.All("open").Where(File.Exists).ToList();
        if (files.Count == 0) return;
        var tracks = files.Select(f => _tracks.FirstOrDefault(t => string.Equals(t.Path, f, StringComparison.OrdinalIgnoreCase)) ?? new Track { Path = f, Title = Path.GetFileNameWithoutExtension(f) }).ToList();
        PlayList(tracks, 0);
    }

    private async Task ScanAsync()
    {
        _scanCts?.Cancel();
        var cts = _scanCts = new CancellationTokenSource();
        _status.Text = "Updating your library…";
        try
        {
            var tracks = await _library.ScanAsync(new Progress<(int done, int total)>(p => _status.Text = p.total > 0 ? $"Reading tags… {p.done} of {p.total}" : "Updating your library…"), cts.Token);
            if (cts.IsCancellationRequested) return;
            bool changed = tracks.Count != _tracks.Count;
            _tracks = tracks;
            _status.Text = $"{_tracks.Count} songs";
            if (changed || _nav.Current == "folders") { foreach (var p in new[] { "songs", "albums", "artists", "folders" }) _nav.Invalidate(p); _nav.Select(_tracks.Count > 0 && _nav.Current == "folders" && changed ? "songs" : _nav.Current ?? "songs"); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Warn("Music scan failed", ex); _status.Text = "The library could not be updated."; }
    }

    // ------------------------------------------------------------------ pages

    private IEnumerable<Track> Filtered => _filter.Length == 0 ? _tracks : _tracks.Where(t =>
        t.Title.Contains(_filter, StringComparison.CurrentCultureIgnoreCase) || t.DisplayArtist.Contains(_filter, StringComparison.CurrentCultureIgnoreCase) || t.Album.Contains(_filter, StringComparison.CurrentCultureIgnoreCase));

    private UIElement Page(string id) => id switch
    {
        "albums" => Albums(),
        "artists" => Artists(),
        "queue" => Kit.Scroll(Queue()),
        "folders" => Kit.Scroll(Folders()),
        _ => Songs(Filtered.OrderBy(t => t.DisplayArtist, StringComparer.CurrentCultureIgnoreCase).ThenBy(t => t.Album).ThenBy(t => t.Number).ThenBy(t => t.Title).ToList(), "Songs"),
    };

    private sealed class SongRow { public required Grid Root; public required TextBlock Title, Artist, Album, Time; public required FontIcon Playing; }

    private Grid Songs(List<Track> list, string title, string? subtitle = null)
    {
        var root = new Grid { Padding = new Thickness(28, 20, 28, 10), RowSpacing = 10 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var head = new Grid();
        head.Children.Add(Kit.Stack(2, Ui.Text(title, 28, "Vos.TextPrimary", Tokens.Display, FontWeights.SemiBold), Ui.Text(subtitle ?? $"{list.Count} songs · {Fmt.DurationWords(TimeSpan.FromTicks(list.Sum(t => t.Duration.Ticks)))}", 12.5, "Vos.TextSecondary")));
        var actions = Kit.Row(8, Buttons.Accent(Kit.Row(6, Ui.Icon(Glyphs.Play, 12, "Vos.OnAccent"), Ui.Text("Play", 13.5, "Vos.OnAccent")), () => PlayList(list, 0)),
            Buttons.Filled(Kit.Row(6, Ui.Icon(Glyphs.Shuffle, 12), Ui.Text("Shuffle", 13.5)), () => { if (!MS.Shuffle) ToggleShuffle(); PlayList(list, Random.Shared.Next(Math.Max(1, list.Count))); }));
        actions.HorizontalAlignment = HorizontalAlignment.Right;
        actions.VerticalAlignment = VerticalAlignment.Center;
        Buttons.AutomationName(actions.Children[0], "Play all");
        Buttons.AutomationName(actions.Children[1], "Shuffle all");
        head.Children.Add(actions);
        root.Children.Add(head);
        if (list.Count == 0)
        {
            var empty = _tracks.Count == 0 ? Kit.Empty(Glyphs.Music, "No music yet", "Add a folder that contains your music. V-OS reads it on this PC only.", ("Add a folder", () => _ = AddFolderAsync())) : Kit.Empty(Glyphs.Search, "Nothing matches", "Try another search.");
            Grid.SetRow(empty, 1); root.Children.Add(empty);
            return root;
        }
        VirtualList<Track>? vl = null;
        vl = new VirtualList<Track>(() =>
        {
            var g = new Grid { ColumnSpacing = 14, Padding = new Thickness(12, 7, 12, 7), CornerRadius = new CornerRadius(Tokens.RadiusSmall), Background = Clear() };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.3, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.3, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
            var playing = new FontIcon { Glyph = Glyphs.Volume3, FontSize = 12, Foreground = Ui.B("Vos.AccentText"), VerticalAlignment = VerticalAlignment.Center };
            var t = Ui.Text("", 13.5); var a = Ui.Text("", 13, "Vos.TextSecondary"); var al = Ui.Text("", 13, "Vos.TextSecondary"); var d = Ui.Text("", 12.5, "Vos.TextSecondary", Tokens.Instrument);
            foreach (var (e, c) in new (FrameworkElement, int)[] { (playing, 0), (t, 1), (a, 2), (al, 3), (d, 4) }) { e.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(e, c); g.Children.Add(e); }
            d.HorizontalAlignment = HorizontalAlignment.Right;
            g.Tag = new SongRow { Root = g, Title = t, Artist = a, Album = al, Time = d, Playing = playing };
            g.PointerEntered += (_, _) => g.Background = Ui.B("Vos.ControlHover");
            g.PointerExited += (_, _) => g.Background = Clear();
            g.DoubleTapped += (_, _) => { if (g.DataContext is Track tr) PlayList(vl!.Items.ToList(), vl.Items.ToList().IndexOf(tr)); };
            g.RightTapped += (_, e) =>
            {
                if (g.DataContext is not Track tr) return;
                var m = new MenuFlyout();
                var play = new MenuFlyoutItem { Text = "Play", Icon = new FontIcon { Glyph = Glyphs.Play } }; play.Click += (_, _) => PlayList(vl!.Items.ToList(), vl.Items.ToList().IndexOf(tr)); m.Items.Add(play);
                var next = new MenuFlyoutItem { Text = "Play next", Icon = new FontIcon { Glyph = Glyphs.Next } }; next.Click += (_, _) => Enqueue(tr, next: true); m.Items.Add(next);
                var add = new MenuFlyoutItem { Text = "Add to queue", Icon = new FontIcon { Glyph = Glyphs.Add } }; add.Click += (_, _) => Enqueue(tr, next: false); m.Items.Add(add);
                m.Items.Add(new MenuFlyoutSeparator());
                var show = new MenuFlyoutItem { Text = "Show in Files", Icon = new FontIcon { Glyph = Glyphs.Folder } }; show.Click += (_, _) => AppHost.Open("files", ["path=" + tr.Path]); m.Items.Add(show);
                m.ShowAt(g, e.GetPosition(g));
                e.Handled = true;
            };
            return g;
        }, (e, tr, i) =>
        {
            var r = (SongRow)e.Tag;
            e.DataContext = tr;
            r.Title.Text = tr.Title;
            r.Artist.Text = tr.DisplayArtist;
            r.Album.Text = tr.DisplayAlbum;
            r.Time.Text = Time(tr.Duration);
            bool current = Current?.Path == tr.Path;
            r.Playing.Visibility = current ? Visibility.Visible : Visibility.Collapsed;
            r.Title.Foreground = Ui.B(current ? "Vos.AccentText" : "Vos.TextPrimary");
            Buttons.AutomationName(e, $"{tr.Title} by {tr.DisplayArtist}");
        });
        vl.SetItems(list);
        _visibleList = vl;
        Grid.SetRow(vl, 1);
        root.Children.Add(vl);
        return root;
    }

    private VirtualList<Track>? _visibleList;

    private Grid Albums()
    {
        var albums = Filtered.GroupBy(t => (t.DisplayAlbum, t.GroupArtist)).Select(g => g.OrderBy(t => t.Number).ToList()).OrderBy(g => g[0].GroupArtist, StringComparer.CurrentCultureIgnoreCase).ThenBy(g => g[0].DisplayAlbum).ToList();
        var root = new Grid { Padding = new Thickness(28, 20, 28, 10), RowSpacing = 12 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(Kit.Stack(2, Ui.Text("Albums", 28, "Vos.TextPrimary", Tokens.Display, FontWeights.SemiBold), Ui.Text($"{albums.Count} albums", 12.5, "Vos.TextSecondary")));
        var vl = new VirtualList<List<Track>>(() =>
        {
            var art = new Image { Width = 150, Height = 150, Stretch = Stretch.UniformToFill };
            var frame = ArtFrame(art, 150, Tokens.RadiusControl);
            var t = Ui.Text("", 13.5, "Vos.TextPrimary", Tokens.Text, FontWeights.SemiBold); t.Width = 150;
            var a = Ui.Text("", 12, "Vos.TextSecondary"); a.Width = 150;
            var s = Kit.Stack(6, frame, Kit.Stack(1, t, a));
            var b = Buttons.Subtle(s, null, Tokens.RadiusControl, new Thickness(8));
            b.Tag = (art, t, a);
            b.Click += (_, _) => { if (b.DataContext is List<Track> album) ShowAlbum(album); };
            return b;
        }, (e, album, i) =>
        {
            var (art, t, a) = ((Image, TextBlock, TextBlock))e.Tag;
            e.DataContext = album;
            t.Text = album[0].DisplayAlbum;
            a.Text = album[0].GroupArtist + (album[0].Year > 0 ? " · " + album[0].Year : "");
            LoadArt(art, album[0].Path, 300);
            Buttons.AutomationName(e, t.Text);
        }, VirtualList<List<Track>>.GridLayout(166, 214, 8));
        vl.SetItems(albums);
        Grid.SetRow(vl, 1); root.Children.Add(vl);
        if (albums.Count == 0) { var empty = Kit.Empty(Glyphs.Music, "No albums", _tracks.Count == 0 ? "Add a music folder first." : "Nothing matches."); Grid.SetRow(empty, 1); root.Children.Add(empty); }
        return root;
    }

    private void ShowAlbum(List<Track> album)
    {
        var page = Songs(album, album[0].DisplayAlbum, $"{album[0].GroupArtist} · {album.Count} songs · {Fmt.DurationWords(TimeSpan.FromTicks(album.Sum(t => t.Duration.Ticks)))}");
        var art = new Image { Width = 120, Height = 120, Stretch = Stretch.UniformToFill };
        LoadArt(art, album[0].Path, 240);
        var back = Buttons.Filled(Kit.Row(6, Ui.Icon(Glyphs.Back, 12), Ui.Text("Albums", 13)), () => _nav.Select("albums"));
        var header = (Grid)page.Children[0];
        var stack = (StackPanel)header.Children[0];
        stack.Children.Insert(0, back);
        var frame = ArtFrame(art, 120, Tokens.RadiusControl);
        frame.Margin = new Thickness(0, 10, 0, 8);
        frame.HorizontalAlignment = HorizontalAlignment.Left;
        stack.Children.Insert(1, frame);
        _nav.SetContent(page);
    }

    private UIElement Artists()
    {
        var artists = Filtered.GroupBy(t => t.GroupArtist).OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase).ToList();
        var page = Kit.Stack(4, Kit.PageHeader("Artists", $"{artists.Count} artists"));
        var rows = new StackPanel { Spacing = 1 };
        foreach (var a in artists)
        {
            var songs = a.OrderBy(t => t.Album).ThenBy(t => t.Number).ToList();
            var badge = Kit.GlyphBadge(Glyphs.Mic, 38);
            var name = a.Key;
            rows.Children.Add(Kit.ListRow(badge, name, $"{songs.Select(s => s.DisplayAlbum).Distinct().Count()} albums · {songs.Count} songs", Ui.Icon(Glyphs.ChevronRight, 12, "Vos.TextTertiary"),
                () => _nav.SetContent(Songs(songs, name)), 56));
        }
        if (artists.Count == 0) rows.Children.Add(Kit.Empty(Glyphs.Mic, "No artists", _tracks.Count == 0 ? "Add a music folder first." : "Nothing matches."));
        page.Children.Add(rows);
        return Kit.Scroll(page);
    }

    private StackPanel Queue()
    {
        var page = Kit.Stack(4);
        var t = Current;
        if (t == null) { page.Children.Add(Kit.Empty(Glyphs.Music, "Nothing playing", "Pick a song, an album or an artist.")); return page; }
        var hero = new Grid { ColumnSpacing = 28 };
        hero.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        hero.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var art = new Image { Width = 240, Height = 240, Stretch = Stretch.UniformToFill };
        LoadArt(art, t.Path, 480);
        hero.Children.Add(ArtFrame(art, 240, Tokens.RadiusCard));
        var info = Kit.Stack(6, Ui.Text("NOW PLAYING", 11.5, "Vos.AccentText", Tokens.Text, FontWeights.SemiBold), Ui.Text(t.Title, 28, "Vos.TextPrimary", Tokens.Display, FontWeights.SemiBold, TextWrapping.Wrap),
            Ui.Text(t.DisplayArtist, 16, "Vos.TextSecondary"), Ui.Text(t.DisplayAlbum + (t.Year > 0 ? " · " + t.Year : ""), 13, "Vos.TextTertiary"));
        info.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(info, 1); hero.Children.Add(info);
        page.Children.Add(hero);
        page.Children.Add(Kit.Section($"Up next · {Math.Max(0, _queue.Count - _index - 1)} songs"));
        var rows = new StackPanel { Spacing = 1 };
        for (int i = _index + 1; i < Math.Min(_queue.Count, _index + 51); i++)
        {
            var track = _queue[i];
            int at = i;
            rows.Children.Add(Kit.ListRow(null, track.Title, track.DisplayArtist, Ui.Text(Time(track.Duration), 12, "Vos.TextSecondary", Tokens.Instrument), () => PlayAt(at), 46));
        }
        if (rows.Children.Count == 0) rows.Children.Add(Ui.Text("The queue ends after this song.", 13, "Vos.TextSecondary"));
        page.Children.Add(rows);
        return page;
    }

    private StackPanel Folders()
    {
        var page = Kit.Stack(4, Kit.PageHeader("Library folders", "V-OS indexes these folders on this PC. Nothing is uploaded."));
        var configured = MS.LibraryFolders ?? [];
        var rows = MusicLibrary.Folders().Select(f => (UIElement)Kit.Setting(Path.GetFileName(f.TrimEnd('\\')) is { Length: > 0 } n ? n : f, f + (configured.Count == 0 ? " (default)" : ""),
            configured.Count == 0 ? null : Buttons.Icon(Glyphs.Delete, () => { SettingsStore.Instance.Update(s => s.Music.LibraryFolders!.Remove(f)); _ = ScanAsync(); _nav.Invalidate("folders"); _nav.Select("folders"); }, 32, 13, "Remove folder"), Glyphs.Folder)).ToList();
        page.Children.Add(Kit.Group([.. rows]));
        var add = Buttons.Accent("Add a folder", () => _ = AddFolderAsync());
        var rescan = Buttons.Filled("Scan again", () => _ = ScanAsync());
        var row = Kit.Row(8, add, rescan);
        row.Margin = new Thickness(0, 10, 0, 0);
        page.Children.Add(row);
        page.Children.Add(new Border { Height = 6 });
        page.Children.Add(_status.Parent == null ? _status : Ui.Text(_status.Text, 12, "Vos.TextTertiary"));
        return page;
    }

    private async Task AddFolderAsync()
    {
        var path = await Pickers.FolderAsync(Frame.Hwnd);
        if (path == null) return;
        SettingsStore.Instance.Update(s => { s.Music.LibraryFolders ??= []; if (!s.Music.LibraryFolders.Contains(path, StringComparer.OrdinalIgnoreCase)) s.Music.LibraryFolders.Add(path); });
        _nav.Invalidate("folders");
        await ScanAsync();
    }

    // ------------------------------------------------------------------ playback

    private Track? Current => _index >= 0 && _index < _queue.Count ? _queue[_index] : null;

    private void PlayList(List<Track> list, int start)
    {
        if (list.Count == 0) return;
        _queue = list.ToList();
        start = Math.Clamp(start, 0, _queue.Count - 1);
        if (MS.Shuffle)
        {
            var first = _queue[start];
            var rest = _queue.Where((t, i) => i != start).OrderBy(_ => Random.Shared.Next()).ToList();
            _queue = [first, .. rest];
            start = 0;
        }
        PlayAt(start);
    }

    private void Enqueue(Track t, bool next)
    {
        if (Current == null) { PlayList([t], 0); return; }
        _queue.Insert(next ? _index + 1 : _queue.Count, t);
        _nav.Invalidate("queue");
    }

    private async void PlayAt(int index)
    {
        if (index < 0 || index >= _queue.Count) return;
        _index = index;
        var t = _queue[index];
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(t.Path);
            var item = new MediaPlaybackItem(MediaSource.CreateFromStorageFile(file));
            var props = item.GetDisplayProperties();
            props.Type = MediaPlaybackType.Music;
            props.MusicProperties.Title = t.Title;
            props.MusicProperties.Artist = t.DisplayArtist;
            props.MusicProperties.AlbumTitle = t.DisplayAlbum;
            props.MusicProperties.AlbumArtist = t.GroupArtist;
            try
            {
                var thumb = await file.GetThumbnailAsync(ThumbnailMode.MusicView, 300, ThumbnailOptions.UseCurrentScale);
                if (thumb != null && thumb.Type == ThumbnailType.Image) props.Thumbnail = RandomAccessStreamReference.CreateFromStream(thumb);
            }
            catch { }
            item.ApplyDisplayProperties(props);
            _player.Source = item;
            _player.Play();
            _status.Text = "";
        }
        catch (Exception ex)
        {
            Log.Warn("Could not play " + t.Path, ex);
            _status.Text = $"“{t.Title}” could not be played.";
            return;
        }
        _barTitle.Text = t.Title;
        _barArtist.Text = $"{t.DisplayArtist} · {t.DisplayAlbum}";
        LoadArt(_barArt, t.Path, 96);
        _total.Text = Time(t.Duration);
        Frame.Title = $"{t.Title} — Music";
        _visibleList?.RefreshVisible();
        _nav.Invalidate("queue");
        if (_nav.Current == "queue") _nav.Select("queue");
    }

    private void PlayPause()
    {
        if (Current == null) { var list = _visibleList?.Items.ToList() ?? _tracks; if (list.Count > 0) PlayList(list, 0); return; }
        if (_player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing) _player.Pause(); else _player.Play();
    }

    private void Next()
    {
        if (_queue.Count == 0) return;
        if (_index + 1 < _queue.Count) PlayAt(_index + 1);
        else if (MS.Repeat == "all") PlayAt(0);
        else { _player.Pause(); _player.PlaybackSession.Position = TimeSpan.Zero; }
    }

    private void Previous()
    {
        if (_player.PlaybackSession.Position > TimeSpan.FromSeconds(3) || _index <= 0) { _player.PlaybackSession.Position = TimeSpan.Zero; return; }
        PlayAt(_index - 1);
    }

    private void OnEnded() { if (MS.Repeat != "one") Next(); }

    private void ToggleShuffle()
    {
        SettingsStore.Instance.Update(s => s.Music.Shuffle = !s.Music.Shuffle);
        StyleModeButtons();
    }

    private void CycleRepeat()
    {
        var next = MS.Repeat switch { "off" => "all", "all" => "one", _ => "off" };
        SettingsStore.Instance.Update(s => s.Music.Repeat = next);
        _player.IsLoopingEnabled = next == "one";
        StyleModeButtons();
    }

    private void StyleModeButtons()
    {
        _shuffle.Content = Ui.Icon(Glyphs.Shuffle, 14, MS.Shuffle ? "Vos.AccentText" : "Vos.TextSecondary");
        _repeat.Content = Ui.Icon(MS.Repeat == "one" ? Glyphs.RepeatOne : Glyphs.Repeat, 14, MS.Repeat == "off" ? "Vos.TextSecondary" : "Vos.AccentText");
        ToolTipService.SetToolTip(_repeat, MS.Repeat switch { "all" => "Repeat all", "one" => "Repeat one", _ => "Repeat off" });
    }

    private void UpdatePlayButton()
    {
        bool playing = _player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;
        _play.Content = Ui.Icon(playing ? Glyphs.Pause : Glyphs.Play, 16, "Vos.OnAccent");
    }

    private void UpdatePosition()
    {
        if (Current == null || _seeking) return;
        var s = _player.PlaybackSession;
        var total = s.NaturalDuration > TimeSpan.Zero ? s.NaturalDuration : Current.Duration;
        _seek.Maximum = Math.Max(1, total.TotalSeconds);
        _seek.Value = Math.Min(_seek.Maximum, s.Position.TotalSeconds);
        _elapsed.Text = Time(s.Position);
        _total.Text = Time(total);
    }

    // ------------------------------------------------------------------ now-playing bar

    private Border NowPlayingBar()
    {
        var g = new Grid { ColumnSpacing = 18, Padding = new Thickness(16, 10, 20, 10) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 220 });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 160 });
        var art = ArtFrame(_barArt, 48, 8);
        var titles = Kit.Stack(1, _barTitle, _barArtist);
        titles.VerticalAlignment = VerticalAlignment.Center;
        var left = Kit.Row(12, art, titles);
        left.Tapped += (_, _) => _nav.Select("queue");
        g.Children.Add(left);

        var controls = Kit.Row(6, _shuffle, Buttons.Icon(Glyphs.Previous, Previous, 36, 14, "Previous"), _play, Buttons.Icon(Glyphs.Next, Next, 36, 14, "Next"), _repeat);
        controls.HorizontalAlignment = HorizontalAlignment.Center;
        var seekRow = new Grid { ColumnSpacing = 10 };
        seekRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        seekRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        seekRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        seekRow.Children.Add(_elapsed);
        Grid.SetColumn(_seek, 1); seekRow.Children.Add(_seek);
        Grid.SetColumn(_total, 2); seekRow.Children.Add(_total);
        Buttons.AutomationName(_seek, "Position");
        _seek.AddHandler(UIElement.PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => _seeking = true), true);
        _seek.AddHandler(UIElement.PointerReleasedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => { _player.PlaybackSession.Position = TimeSpan.FromSeconds(_seek.Value); _seeking = false; }), true);
        _seek.AddHandler(UIElement.PointerCaptureLostEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => { if (_seeking) { _player.PlaybackSession.Position = TimeSpan.FromSeconds(_seek.Value); _seeking = false; } }), true);
        var center = Kit.Stack(2, controls, seekRow);
        Grid.SetColumn(center, 1); g.Children.Add(center);

        var volume = new Slider { Minimum = 0, Maximum = 100, Value = _player.Volume * 100, Width = 120, VerticalAlignment = VerticalAlignment.Center };
        Buttons.AutomationName(volume, "Volume");
        volume.ValueChanged += (_, e) => { _player.Volume = e.NewValue / 100; };
        volume.PointerCaptureLost += (_, _) => SettingsStore.Instance.Update(s => s.Music.Volume = Math.Round(_player.Volume, 2));
        var right = Kit.Row(8, Ui.Icon(Glyphs.Volume2, 14, "Vos.TextSecondary"), volume);
        right.HorizontalAlignment = HorizontalAlignment.Right;
        right.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(right, 2); g.Children.Add(right);
        return new Border { Child = g, Background = Ui.B("Vos.Layer"), BorderBrush = Ui.B("Vos.StrokeSubtle"), BorderThickness = new Thickness(0, 1, 0, 0) };
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Album art only (never the file-type icon); the placeholder behind the image shows when a file has none.</summary>
    private static void LoadArt(Image target, string path, int px)
    {
        target.Tag = path;
        target.Source = null;
        _ = IconService.GetThumbnailOnlyAsync(path, px).ContinueWith(t =>
        {
            if (t.Status == TaskStatus.RanToCompletion && t.Result is { } img) UiThread.Post(() => { if (Equals(target.Tag, path)) target.Source = Images.ToBitmap(img); });
        }, TaskScheduler.Default);
    }

    /// <summary>Square art frame: a soft gradient with a note behind the album art image.</summary>
    private static Border ArtFrame(Image art, double size, double radius)
    {
        var g = new Grid();
        g.Children.Add(new Border
        {
            Background = new LinearGradientBrush
            {
                StartPoint = new(0, 0), EndPoint = new(1, 1),
                GradientStops = { new GradientStop { Color = Colors2.WithAlpha(ThemeService.Current.Accent, 0x55), Offset = 0 }, new GradientStop { Color = Colors2.WithAlpha(ThemeService.Current.Accent, 0x18), Offset = 1 } },
            },
        });
        g.Children.Add(new FontIcon { Glyph = Glyphs.Music, FontSize = size * 0.34, Foreground = Ui.B("Vos.TextSecondary"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        g.Children.Add(art);
        return new Border { Width = size, Height = size, CornerRadius = new CornerRadius(radius), Child = g };
    }

    private static string Time(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : t.ToString(@"m\:ss", CultureInfo.InvariantCulture);

    private static SolidColorBrush Clear() => new(Microsoft.UI.Colors.Transparent);
}
