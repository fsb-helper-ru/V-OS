using System.Globalization;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using VOS.Core;
using VOS.Core.Settings;
using VOS.Platform.Shell;
using VOS.UI;
using VOS.UI.Theme;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;
using Windows.UI.Core;
using FileAttributes = System.IO.FileAttributes;

namespace VOS.Apps.Files;

public sealed class FileEntry
{
    public required string Path { get; init; }
    public required string Name { get; init; }
    public bool IsDir { get; init; }
    public long Size { get; init; }
    public DateTime Modified { get; init; }
    public DateTime Created { get; init; }
    public FileAttributes Attributes { get; init; }
    public string Extension => IsDir ? "" : System.IO.Path.GetExtension(Name).ToLowerInvariant();
    public bool IsHidden => (Attributes & FileAttributes.Hidden) != 0;
}

/// <summary>
/// V-OS Files: a fast file manager on the Windows shell. Folders load off the UI thread into a virtualized list,
/// copy/move/delete go through the shell (progress, conflicts, undo, Recycle Bin), the clipboard and drag and drop
/// interoperate with Explorer, and the folder refreshes itself when something changes on disk.
/// </summary>
public sealed class FilesApp : IAppModule
{
    public AppFrame Frame { get; }
    private static FilesSettings S => SettingsStore.Instance.Current.Files;

    private readonly Grid _content = new();
    private readonly StackPanel _crumbs = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _address = new() { Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _search;
    private readonly Grid _listHost = new();
    private readonly Grid _header;
    private readonly TextBlock _status = Ui.Text("", 12.5, "Vos.TextSecondary");
    private readonly Border _preview = new() { Width = 300, Padding = new Thickness(18, 16, 18, 16) };
    private readonly InfoBar _info = new() { IsOpen = false, IsClosable = true, Margin = new Thickness(0, 0, 0, 8) };
    private readonly Button _back, _forward, _up;
    private VirtualList<FileEntry>? _list;
    private readonly Stack<string> _history = new(), _future = new();
    private readonly HashSet<string> _selected = new(StringComparer.OrdinalIgnoreCase);
    private int _anchor = -1;
    private string _location = "home";
    private List<FileEntry> _all = [];
    private List<FileEntry> _shown = [];
    private string _sort = "name";
    private bool _descending;
    private string? _searchResultsFor;
    private FileSystemWatcher? _watcher;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _refreshDebounce;
    private CancellationTokenSource? _loadCts;
    private readonly Dictionary<string, string> _extIcon = new(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> s_ownIcon = new(StringComparer.OrdinalIgnoreCase) { ".exe", ".ico", ".lnk", ".url", ".msi", ".appref-ms", ".cpl", ".scr" };
    private static readonly HashSet<string> s_thumbs = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".heic", ".tif", ".tiff", ".mp4", ".mkv", ".mov", ".avi", ".wmv", ".pdf", ".psd" };
    private static readonly HashSet<string> s_images = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".ico", ".tif", ".tiff" };
    private static readonly HashSet<string> s_text = new(StringComparer.OrdinalIgnoreCase) { ".txt", ".md", ".log", ".json", ".xml", ".csv", ".ini", ".cfg", ".conf", ".yaml", ".yml", ".toml", ".cs", ".js", ".ts", ".py", ".java", ".c", ".cpp", ".h", ".ps1", ".bat", ".cmd", ".sh", ".html", ".css", ".sql", ".gitignore", ".editorconfig" };

    public FilesApp()
    {
        Frame = new AppFrame(VosApp.Find("files")!, "Files", 820, 520, 1180, 760);
        _refreshDebounce = UiThread.Timer(TimeSpan.FromMilliseconds(400), () => _ = LoadAsync(keepSelection: true), repeating: false);

        // ---- sidebar
        var side = new StackPanel { Spacing = 1, Padding = new Thickness(10, 2, 10, 12) };
        void Place(string label, string glyph, string target) => side.Children.Add(SideItem(label, glyph, () => Navigate(target)));
        Place("Home", Glyphs.Home, "home");
        Place("Desktop", Glyphs.Desktop, Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
        Place("Downloads", Glyphs.Download, ShellNative.Downloads);
        Place("Documents", Glyphs.Document, Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        Place("Pictures", Glyphs.Picture, Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
        Place("Music", Glyphs.Music, Environment.GetFolderPath(Environment.SpecialFolder.MyMusic));
        Place("Videos", Glyphs.Video, Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));
        var favs = S.Favorites ?? [];
        if (favs.Count > 0)
        {
            side.Children.Add(SideHeader("Favourites"));
            foreach (var f in favs) Place(f.Name, Glyphs.Star, f.Path);
        }
        side.Children.Add(SideHeader("This PC"));
        foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady))
            Place(string.IsNullOrWhiteSpace(d.VolumeLabel) ? $"Local disk ({d.Name.TrimEnd('\\')})" : $"{d.VolumeLabel} ({d.Name.TrimEnd('\\')})", d.DriveType == DriveType.Removable ? Glyphs.Device : Glyphs.Disk, d.RootDirectory.FullName);
        side.Children.Add(SideItem("Recycle Bin", Glyphs.Trash, () => ShellOps.Launch("shell:RecycleBinFolder")));
        var sideScroll = new ScrollViewer { Content = side, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        // ---- toolbar
        _back = Buttons.Icon(Glyphs.Back, Back, 34, 13, "Back (Alt+Left)");
        _forward = Buttons.Icon(Glyphs.Forward, Forward, 34, 13, "Forward (Alt+Right)");
        _up = Buttons.Icon(Glyphs.Up, Up, 34, 13, "Up (Alt+Up)");
        var refresh = Buttons.Icon(Glyphs.Refresh, () => _ = LoadAsync(true), 34, 13, "Refresh (F5)");
        var addressHost = new Grid { MinHeight = 34, MinWidth = 160, CornerRadius = new CornerRadius(Tokens.RadiusControl), Background = Ui.B("Vos.Control"), Padding = new Thickness(6, 0, 6, 0) };
        var crumbScroll = new ScrollViewer { Content = _crumbs, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollMode = ScrollMode.Disabled };
        addressHost.Children.Add(crumbScroll);
        _crumbs.SizeChanged += (_, _) => crumbScroll.ChangeView(crumbScroll.ScrollableWidth, null, null, disableAnimation: true); // keep the current folder in view
        addressHost.Children.Add(_address);
        addressHost.Tapped += (_, e) => { if (e.OriginalSource is Grid or ScrollViewer or StackPanel) EditAddress(); };
        Buttons.AutomationName(_address, "Address");
        _address.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter) { var p = Environment.ExpandEnvironmentVariables(_address.Text.Trim().Trim('"')); CloseAddress(); if (Directory.Exists(p)) Navigate(p); else if (File.Exists(p)) ShellOps.Launch(p); e.Handled = true; }
            else if (e.Key == VirtualKey.Escape) { CloseAddress(); e.Handled = true; }
        };
        _address.LostFocus += (_, _) => CloseAddress();
        _search = new TextBox { PlaceholderText = "Search this folder", Width = 230, VerticalAlignment = VerticalAlignment.Center };
        Buttons.AutomationName(_search, "Search this folder");
        _search.TextChanged += (_, _) => { if (_searchResultsFor == null) ApplyView(); };
        _search.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter && _search.Text.Trim().Length > 1) { _ = DeepSearchAsync(_search.Text.Trim()); e.Handled = true; }
            else if (e.Key == VirtualKey.Escape) { _search.Text = ""; if (_searchResultsFor != null) { _searchResultsFor = null; _ = LoadAsync(false); } e.Handled = true; }
        };
        var newMenu = new MenuFlyout();
        newMenu.Items.Add(MenuItem("Folder", Glyphs.Folder, () => _ = NewFolderAsync()));
        newMenu.Items.Add(MenuItem("Text document", Glyphs.Document, () => _ = NewTextFileAsync()));
        var newButton = new DropDownButton { Content = Kit.Row(6, Ui.Icon(Glyphs.Add, 13), Ui.Text("New", 13.5)), Flyout = newMenu, Padding = new Thickness(10, 6, 8, 6) };
        Buttons.AutomationName(newButton, "New");
        var viewToggle = Buttons.Icon(S.View == "grid" ? Glyphs.List : Glyphs.Grid, null, 34, 14, "Switch view");
        viewToggle.Click += (_, _) => { SettingsStore.Instance.Update(s => s.Files.View = s.Files.View == "grid" ? "details" : "grid"); viewToggle.Content = Ui.Icon(S.View == "grid" ? Glyphs.List : Glyphs.Grid, 14); BuildList(); ApplyView(); };
        var previewToggle = Buttons.Icon(Glyphs.Reader, () => { SettingsStore.Instance.Update(s => s.Files.ShowPreview = !s.Files.ShowPreview); _preview.Visibility = S.ShowPreview && _roomForPreview ? Visibility.Visible : Visibility.Collapsed; UpdatePreview(); }, 34, 14, "Preview pane");
        var toolbar = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 0, 0, 10) };
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.Children.Add(Kit.Row(2, _back, _forward, _up, refresh));
        Grid.SetColumn(addressHost, 1); toolbar.Children.Add(addressHost);
        var right = Kit.Row(6, _search, newButton, viewToggle, previewToggle);
        Grid.SetColumn(right, 2); toolbar.Children.Add(right);

        // ---- list area
        _header = DetailsColumns(true);
        _header.Margin = new Thickness(4, 0, 4, 2);
        _header.Children.Add(SortHeader("Name", 1, "name"));
        _header.Children.Add(SortHeader("Date modified", 2, "date"));
        _header.Children.Add(SortHeader("Type", 3, "type"));
        _header.Children.Add(SortHeader("Size", 4, "size"));
        _listHost.AllowDrop = true;
        _listHost.DragOver += OnDragOver;
        _listHost.Drop += OnDrop;
        _listHost.RightTapped += (_, e) => { if (!e.Handled && _location != "home") { ClearSelection(); BackgroundMenu().ShowAt(_listHost, e.GetPosition(_listHost)); e.Handled = true; } };
        _listHost.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

        _content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _content.Padding = new Thickness(18, 12, 18, 10);
        _content.Children.Add(toolbar);
        Grid.SetRow(_info, 1); _content.Children.Add(_info);
        Grid.SetRow(_header, 2); _content.Children.Add(_header);
        Grid.SetRow(_listHost, 3); _content.Children.Add(_listHost);
        _status.Margin = new Thickness(4, 8, 0, 0);
        Grid.SetRow(_status, 4); _content.Children.Add(_status);

        var layer = new Grid();
        layer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layer.Children.Add(_content);
        _preview.BorderBrush = Ui.B("Vos.StrokeSubtle");
        _preview.BorderThickness = new Thickness(1, 0, 0, 0);
        _preview.Visibility = S.ShowPreview ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumn(_preview, 1); layer.Children.Add(_preview);
        var layerBorder = new Border { Background = Ui.B("Vos.Layer"), BorderBrush = Ui.B("Vos.StrokeSubtle"), BorderThickness = new Thickness(1, 1, 0, 0), CornerRadius = new CornerRadius(Tokens.RadiusWindow, 0, 0, 0), Child = layer };
        layer.SizeChanged += (_, e) => Responsive(e.NewSize.Width);
        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(sideScroll);
        Grid.SetColumn(layerBorder, 1); root.Children.Add(layerBorder);
        Frame.Body.Content = root;
        Frame.Root.KeyDown += OnKey;
        Frame.Closed += () => { _watcher?.Dispose(); _loadCts?.Cancel(); };
        BuildList();
        Navigate("home", addToHistory: false);
    }

    public void HandleArgs(AppArgs args)
    {
        var path = args.Get("path") ?? args.All("open").FirstOrDefault();
        if (path == null) return;
        path = Environment.ExpandEnvironmentVariables(path.Trim('"'));
        if (Directory.Exists(path)) Navigate(path);
        else if (File.Exists(path)) { Navigate(Path.GetDirectoryName(path)!); _selected.Clear(); _selected.Add(path); _pendingReveal = path; }
    }

    private string? _pendingReveal;

    // ------------------------------------------------------------------ navigation

    private void Navigate(string target, bool addToHistory = true)
    {
        if (target != "home" && !Directory.Exists(target)) { ShowInfo("That folder is not available", target, InfoBarSeverity.Warning); return; }
        if (addToHistory && !string.Equals(target, _location, StringComparison.OrdinalIgnoreCase)) { _history.Push(_location); _future.Clear(); }
        _location = target;
        _searchResultsFor = null;
        _search.Text = "";
        _selected.Clear();
        _anchor = -1;
        _info.IsOpen = false;
        Watch();
        _ = LoadAsync(false);
    }

    private void Back() { if (_history.Count == 0) return; _future.Push(_location); Navigate(_history.Pop(), addToHistory: false); }
    private void Forward() { if (_future.Count == 0) return; _history.Push(_location); Navigate(_future.Pop(), addToHistory: false); }
    private void Up()
    {
        if (_location == "home") return;
        var parent = Directory.GetParent(_location.TrimEnd('\\'))?.FullName;
        Navigate(parent ?? "home");
    }

    private void Watch()
    {
        _watcher?.Dispose();
        _watcher = null;
        if (_location == "home") return;
        try
        {
            _watcher = new FileSystemWatcher(_location) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size, IncludeSubdirectories = false };
            FileSystemEventHandler h = (_, _) => UiThread.Post(() => { if (_searchResultsFor == null) { _refreshDebounce.Stop(); _refreshDebounce.Start(); } });
            _watcher.Changed += h; _watcher.Created += h; _watcher.Deleted += h;
            _watcher.Renamed += (_, _) => UiThread.Post(() => { _refreshDebounce.Stop(); _refreshDebounce.Start(); });
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) { Log.Debug("Folder watch unavailable", ex.Message); }
    }

    private async Task LoadAsync(bool keepSelection)
    {
        _back.IsEnabled = _history.Count > 0;
        _forward.IsEnabled = _future.Count > 0;
        _up.IsEnabled = _location != "home";
        BuildCrumbs();
        Frame.Title = _location == "home" ? "Files" : $"{DisplayName(_location)} — Files";
        if (_location == "home") { ShowHome(); return; }
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        var location = _location;
        bool hidden = S.ShowHidden;
        try
        {
            var items = await Task.Run(() => Enumerate(location, hidden, cts.Token), cts.Token);
            if (cts.IsCancellationRequested || location != _location) return;
            _all = items;
            if (!keepSelection) _selected.Clear();
            else _selected.RemoveWhere(p => !items.Any(i => string.Equals(i.Path, p, StringComparison.OrdinalIgnoreCase)));
            if (_pendingReveal != null) _selected.Add(_pendingReveal);
            ShowList();
            ApplyView();
            if (_pendingReveal != null) { int i = _shown.FindIndex(x => string.Equals(x.Path, _pendingReveal, StringComparison.OrdinalIgnoreCase)); _pendingReveal = null; if (i >= 0) { _anchor = i; _list?.ScrollIntoView(i); } }
        }
        catch (OperationCanceledException) { }
        catch (UnauthorizedAccessException) { _all = []; ShowList(); ApplyView(); ShowInfo("You don't have permission to open this folder", "Windows denied access. Folders that belong to other users or to the system can be opened in Explorer as administrator.", InfoBarSeverity.Warning); }
        catch (Exception ex) { _all = []; ShowList(); ApplyView(); ShowInfo("The folder could not be read", ex.Message, InfoBarSeverity.Error); }
    }

    private static List<FileEntry> Enumerate(string folder, bool showHidden, CancellationToken ct)
    {
        var options = new EnumerationOptions { IgnoreInaccessible = true, ReturnSpecialDirectories = false, AttributesToSkip = showHidden ? 0 : FileAttributes.Hidden | FileAttributes.System };
        var list = new List<FileEntry>();
        foreach (var fsi in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", options))
        {
            ct.ThrowIfCancellationRequested();
            bool dir = (fsi.Attributes & FileAttributes.Directory) != 0;
            list.Add(new FileEntry { Path = fsi.FullName, Name = fsi.Name, IsDir = dir, Size = dir ? 0 : ((FileInfo)fsi).Length, Modified = fsi.LastWriteTime, Created = fsi.CreationTime, Attributes = fsi.Attributes });
        }
        return list;
    }

    private void ApplyView()
    {
        var q = _search.Text.Trim();
        IEnumerable<FileEntry> items = _all;
        if (q.Length > 0 && _searchResultsFor == null) items = items.Where(e => e.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase));
        Func<FileEntry, object> key = _sort switch { "date" => e => e.Modified, "type" => e => TypeName(e), "size" => e => e.Size, _ => e => e.Name };
        var ordered = items.OrderByDescending(e => e.IsDir);
        ordered = _sort == "name"
            ? (_descending ? ordered.ThenByDescending(e => e.Name, NaturalComparer.Instance) : ordered.ThenBy(e => e.Name, NaturalComparer.Instance))
            : (_descending ? ordered.ThenByDescending(key) : ordered.ThenBy(key)).ThenBy(e => e.Name, NaturalComparer.Instance);
        _shown = ordered.ToList();
        _list?.SetItems(_shown);
        UpdateStatus();
        UpdatePreview();
    }

    private void UpdateStatus()
    {
        if (_location == "home") { _status.Text = ""; return; }
        var sel = _shown.Where(e => _selected.Contains(e.Path)).ToList();
        string selText = sel.Count == 0 ? "" : $" · {sel.Count} selected" + (sel.All(e => !e.IsDir) ? $" ({Fmt.Bytes(sel.Sum(e => e.Size))})" : "");
        _status.Text = (_searchResultsFor != null ? $"Search results for “{_searchResultsFor}” · " : "") + $"{_shown.Count} item{(_shown.Count == 1 ? "" : "s")}{selText}";
    }

    // ------------------------------------------------------------------ list views

    private sealed class RowParts { public required Grid Root; public required Image Icon; public required TextBlock Name, Date, Type, Size; }

    private void BuildList()
    {
        _listHost.Children.Clear();
        bool grid = S.View == "grid";
        _header.Visibility = grid ? Visibility.Collapsed : Visibility.Visible;
        _list = grid
            ? new VirtualList<FileEntry>(CreateTile, BindTile, VirtualList<FileEntry>.GridLayout(112, 124, 6))
            : new VirtualList<FileEntry>(CreateRow, BindRow, new StackLayout { Spacing = 0 });
        _listHost.Children.Add(_list);
        _list.SetItems(_shown);
    }

    private bool _showType = true, _showDate = true, _roomForPreview = true;

    /// <summary>Fits the window: the preview pane steps aside when space is short (the setting is kept), then less important columns.</summary>
    private void Responsive(double width)
    {
        _roomForPreview = width >= 980;
        _preview.Visibility = S.ShowPreview && _roomForPreview ? Visibility.Visible : Visibility.Collapsed;
        double content = width - (_preview.Visibility == Visibility.Visible ? 300 : 0);
        bool type = content >= 780, date = content >= 580;
        _search.Width = content >= 860 ? 230 : 170;
        if (type == _showType && date == _showDate) return;
        _showType = type; _showDate = date;
        ApplyColumns(_header);
        _list?.RefreshVisible();
    }

    private void ApplyColumns(Grid g)
    {
        if (g.ColumnDefinitions.Count < 5) return;
        g.ColumnDefinitions[2].Width = new GridLength(_showDate ? 150 : 0);
        g.ColumnDefinitions[3].Width = new GridLength(_showType ? 130 : 0);
        foreach (var child in g.Children.OfType<FrameworkElement>())
        {
            int c = Grid.GetColumn(child);
            if (c == 2) child.Visibility = _showDate ? Visibility.Visible : Visibility.Collapsed;
            if (c == 3) child.Visibility = _showType ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void ShowList()
    {
        if (_list == null || !_listHost.Children.Contains(_list)) BuildList();
        foreach (var child in _listHost.Children.ToList()) if (child != _list) _listHost.Children.Remove(child);
        _header.Visibility = S.View == "grid" ? Visibility.Collapsed : Visibility.Visible;
        if (_all.Count == 0 && _searchResultsFor == null)
            _listHost.Children.Add(Kit.Empty(Glyphs.FolderOpen, "This folder is empty", "Drop files here, paste with Ctrl+V, or create something new."));
    }

    private static Grid DetailsColumns(bool header)
    {
        var g = new Grid { ColumnSpacing = 12, Padding = new Thickness(10, header ? 0 : 5, 10, header ? 0 : 5) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 160 });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
        return g;
    }

    private Button SortHeader(string text, int column, string key)
    {
        var b = Buttons.Subtle(Ui.Text(text, 12.5, "Vos.TextSecondary", Tokens.Text, FontWeights.SemiBold), () => { if (_sort == key) _descending = !_descending; else { _sort = key; _descending = key is "date" or "size"; } ApplyView(); }, 6, new Thickness(0, 4, 8, 4));
        b.HorizontalAlignment = HorizontalAlignment.Left;
        Grid.SetColumn(b, column);
        return b;
    }

    private FrameworkElement CreateRow()
    {
        var g = DetailsColumns(false);
        g.CornerRadius = new CornerRadius(Tokens.RadiusSmall);
        g.Background = Transparent();
        var icon = new Image { Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Center };
        var name = Ui.Text("", 13.5); name.VerticalAlignment = VerticalAlignment.Center;
        var date = Ui.Text("", 12.5, "Vos.TextSecondary"); date.VerticalAlignment = VerticalAlignment.Center;
        var type = Ui.Text("", 12.5, "Vos.TextSecondary"); type.VerticalAlignment = VerticalAlignment.Center;
        var size = Ui.Text("", 12.5, "Vos.TextSecondary"); size.VerticalAlignment = VerticalAlignment.Center; size.HorizontalAlignment = HorizontalAlignment.Right;
        g.Children.Add(icon);
        Grid.SetColumn(name, 1); g.Children.Add(name);
        Grid.SetColumn(date, 2); g.Children.Add(date);
        Grid.SetColumn(type, 3); g.Children.Add(type);
        Grid.SetColumn(size, 4); g.Children.Add(size);
        g.Tag = new RowParts { Root = g, Icon = icon, Name = name, Date = date, Type = type, Size = size };
        WireItem(g);
        return g;
    }

    private void BindRow(FrameworkElement e, FileEntry f, int index)
    {
        var p = (RowParts)e.Tag;
        e.DataContext = f;
        p.Name.Text = DisplayFileName(f);
        p.Name.Opacity = f.IsHidden ? 0.6 : 1;
        p.Date.Text = f.Modified.ToString("g", CultureInfo.CurrentCulture);
        p.Type.Text = TypeName(f);
        p.Size.Text = f.IsDir ? "" : Fmt.Bytes(f.Size);
        Buttons.AutomationName(e, f.Name);
        ApplyColumns(p.Root);
        Images.LoadIconFor(p.Icon, IconSource(f), 32);
        Highlight(e, f);
    }

    private sealed class TileParts { public required Grid Root; public required Image Thumb; public required TextBlock Name; }

    private FrameworkElement CreateTile()
    {
        var g = new Grid { CornerRadius = new CornerRadius(Tokens.RadiusControl), Padding = new Thickness(8), RowSpacing = 6, Background = Transparent() };
        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(72) });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var thumb = new Image { Width = 72, Height = 72, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center };
        var name = new TextBlock { FontSize = 12.5, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Ui.B("Vos.TextPrimary") };
        g.Children.Add(thumb);
        Grid.SetRow(name, 1); g.Children.Add(name);
        g.Tag = new TileParts { Root = g, Thumb = thumb, Name = name };
        WireItem(g);
        return g;
    }

    private void BindTile(FrameworkElement e, FileEntry f, int index)
    {
        var p = (TileParts)e.Tag;
        e.DataContext = f;
        p.Name.Text = DisplayFileName(f);
        p.Name.Opacity = f.IsHidden ? 0.6 : 1;
        Buttons.AutomationName(e, f.Name);
        if (!f.IsDir && s_thumbs.Contains(f.Extension)) LoadThumb(p.Thumb, f.Path, 144);
        else Images.LoadIconFor(p.Thumb, IconSource(f), 96);
        Highlight(e, f);
    }

    private static void LoadThumb(Image target, string path, int px)
    {
        target.Tag = path;
        target.Source = null;
        _ = IconService.GetAsync(path, px, thumbnail: true).ContinueWith(t =>
        {
            if (t.Status == TaskStatus.RanToCompletion && t.Result is { } img) UiThread.Post(() => { if (Equals(target.Tag, path)) target.Source = Images.ToBitmap(img); });
        }, TaskScheduler.Default);
    }

    /// <summary>Icons depend on the file type except for executables and shortcuts, so one file per extension stands in for all.</summary>
    private string IconSource(FileEntry f)
    {
        if (f.IsDir || s_ownIcon.Contains(f.Extension) || f.Extension.Length == 0) return f.Path;
        if (!_extIcon.TryGetValue(f.Extension, out var rep)) { rep = f.Path; _extIcon[f.Extension] = rep; }
        return rep;
    }

    private void Highlight(FrameworkElement e, FileEntry f)
    {
        ((Panel)e).Background = _selected.Contains(f.Path) ? Ui.B("Vos.AccentSubtle") : Transparent();
    }

    private void WireItem(Grid g)
    {
        g.PointerEntered += (_, _) => { if (g.DataContext is FileEntry f && !_selected.Contains(f.Path)) g.Background = Ui.B("Vos.ControlHover"); };
        g.PointerExited += (_, _) => { if (g.DataContext is FileEntry f && !_selected.Contains(f.Path)) g.Background = Transparent(); };
        g.Tapped += (_, e) => { if (g.DataContext is FileEntry f) { Select(f); e.Handled = true; } };
        g.DoubleTapped += (_, e) => { if (g.DataContext is FileEntry f) { Open(f); e.Handled = true; } };
        g.RightTapped += (_, e) =>
        {
            if (g.DataContext is not FileEntry f) return;
            if (!_selected.Contains(f.Path)) { _selected.Clear(); _selected.Add(f.Path); _anchor = _shown.IndexOf(f); _list?.RefreshVisible(); UpdateStatus(); UpdatePreview(); }
            ItemMenu().ShowAt(g, e.GetPosition(g));
            e.Handled = true;
        };
        g.CanDrag = true;
        g.DragStarting += async (_, args) =>
        {
            if (g.DataContext is not FileEntry f) return;
            if (!_selected.Contains(f.Path)) { _selected.Clear(); _selected.Add(f.Path); }
            var deferral = args.GetDeferral();
            try
            {
                var items = await StorageItemsAsync(_selected.ToList());
                args.Data.SetStorageItems(items);
                args.Data.RequestedOperation = DataPackageOperation.Copy | DataPackageOperation.Move;
                args.AllowedOperations = DataPackageOperation.Copy | DataPackageOperation.Move;
            }
            finally { deferral.Complete(); }
        };
    }

    private static bool KeyDown(VirtualKey k) => InputKeyboardSource.GetKeyStateForCurrentThread(k).HasFlag(CoreVirtualKeyStates.Down);

    private void Select(FileEntry f)
    {
        int index = _shown.IndexOf(f);
        if (KeyDown(VirtualKey.Shift) && _anchor >= 0)
        {
            if (!KeyDown(VirtualKey.Control)) _selected.Clear();
            int a = Math.Min(_anchor, index), b = Math.Max(_anchor, index);
            for (int i = a; i <= b; i++) _selected.Add(_shown[i].Path);
        }
        else if (KeyDown(VirtualKey.Control))
        {
            if (!_selected.Remove(f.Path)) _selected.Add(f.Path);
            _anchor = index;
        }
        else
        {
            _selected.Clear();
            _selected.Add(f.Path);
            _anchor = index;
        }
        _list?.RefreshVisible();
        UpdateStatus();
        UpdatePreview();
    }

    private void ClearSelection() { _selected.Clear(); _list?.RefreshVisible(); UpdateStatus(); UpdatePreview(); }

    private List<FileEntry> Selection => _shown.Where(e => _selected.Contains(e.Path)).ToList();

    private void Open(FileEntry f)
    {
        if (f.IsDir) { Navigate(f.Path); return; }
        if (!ShellOps.Launch(f.Path)) ShowInfo("Windows could not open this file", "Choose an app with Open with.", InfoBarSeverity.Warning);
    }

    // ------------------------------------------------------------------ home

    private void ShowHome()
    {
        _header.Visibility = Visibility.Collapsed;
        _listHost.Children.Clear();
        var page = Kit.Stack(6, Ui.Text("Home", 26, "Vos.TextPrimary", Tokens.Display, FontWeights.SemiBold));
        page.Children.Add(Kit.Section("Folders"));
        var folders = new (string name, string glyph, string path)[]
        {
            ("Desktop", Glyphs.Desktop, Environment.GetFolderPath(Environment.SpecialFolder.Desktop)), ("Downloads", Glyphs.Download, ShellNative.Downloads),
            ("Documents", Glyphs.Document, Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)), ("Pictures", Glyphs.Picture, Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)),
            ("Music", Glyphs.Music, Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)), ("Videos", Glyphs.Video, Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)),
        };
        var tiles = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, ItemWidth = 200, ItemHeight = 70 };
        foreach (var (name, glyph, path) in folders.Concat((S.Favorites ?? []).Select(f => (f.Name, Glyphs.Star, f.Path))))
        {
            var t = Kit.ListRow(Kit.GlyphBadge(glyph, 38), name, path, null, () => Navigate(path));
            t.Margin = new Thickness(0, 0, 8, 8);
            t.Background = Ui.B("Vos.Card");
            tiles.Children.Add(t);
        }
        page.Children.Add(tiles);
        page.Children.Add(Kit.Section("Drives"));
        var drives = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, ItemWidth = 260, ItemHeight = 86 };
        foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady))
        {
            double used = d.TotalSize > 0 ? 1 - (double)d.AvailableFreeSpace / d.TotalSize : 0;
            var info = Kit.Stack(5, Ui.Text(string.IsNullOrWhiteSpace(d.VolumeLabel) ? $"Local disk ({d.Name.TrimEnd('\\')})" : $"{d.VolumeLabel} ({d.Name.TrimEnd('\\')})", 13.5),
                Kit.Meter(used, 170, 5, used > 0.9 ? "Vos.Critical" : "Vos.Accent"), Ui.Text($"{Fmt.Bytes(d.AvailableFreeSpace)} free of {Fmt.Bytes(d.TotalSize)}", 11.5, "Vos.TextSecondary"));
            var root = d.RootDirectory.FullName;
            var b = Buttons.Subtle(Kit.Row(12, Ui.Icon(Glyphs.Disk, 24), info), () => Navigate(root), Tokens.RadiusControl, new Thickness(14, 10, 14, 10));
            b.Background = Ui.B("Vos.Card"); b.Margin = new Thickness(0, 0, 8, 8); b.HorizontalAlignment = HorizontalAlignment.Stretch; b.HorizontalContentAlignment = HorizontalAlignment.Left;
            drives.Children.Add(b);
        }
        page.Children.Add(drives);
        if (SettingsStore.Instance.Current.Privacy.RecordRecentFiles)
        {
            page.Children.Add(Kit.Section("Recent"));
            var recent = WindowsSearch.Recent("", 14).ToList();
            if (recent.Count == 0) page.Children.Add(Ui.Text("Files you open will appear here.", 13, "Vos.TextSecondary"));
            var rows = new StackPanel { Spacing = 1 };
            foreach (var r in recent)
            {
                var icon = new Image { Width = 20, Height = 20 };
                Images.LoadIcon(icon, r.Path, 32);
                var path = r.Path;
                rows.Children.Add(Kit.ListRow(icon, r.Name, r.Modified.ToString("g", CultureInfo.CurrentCulture), null, () => ShellOps.Launch(path), 44));
            }
            page.Children.Add(rows);
        }
        _listHost.Children.Add(new ScrollViewer { Content = page, Padding = new Thickness(6, 4, 6, 4), VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        _status.Text = "";
        _all = []; _shown = [];
        UpdatePreview();
    }

    // ------------------------------------------------------------------ breadcrumbs / address

    private void BuildCrumbs()
    {
        _crumbs.Children.Clear();
        if (_location == "home") { _crumbs.Children.Add(Crumb("Home", "home")); return; }
        var parts = new List<(string name, string path)>();
        var dir = new DirectoryInfo(_location);
        while (dir != null) { parts.Insert(0, (dir.Parent == null ? dir.FullName.TrimEnd('\\') : dir.Name, dir.FullName)); dir = dir.Parent; }
        _crumbs.Children.Add(Crumb("Home", "home"));
        foreach (var (name, path) in parts)
        {
            _crumbs.Children.Add(new FontIcon { Glyph = Glyphs.ChevronRight, FontSize = 10, Foreground = Ui.B("Vos.TextTertiary"), Margin = new Thickness(2, 0, 2, 0) });
            _crumbs.Children.Add(Crumb(name, path));
        }
    }

    private Button Crumb(string text, string target)
    {
        var b = Buttons.Subtle(Ui.Text(text, 13), () => Navigate(target), 6, new Thickness(8, 4, 8, 4));
        return b;
    }

    private void EditAddress()
    {
        if (_location == "home") return;
        _address.Text = _location;
        _address.Visibility = Visibility.Visible;
        _crumbs.Visibility = Visibility.Collapsed;
        _address.Focus(FocusState.Programmatic);
        _address.SelectAll();
    }

    private void CloseAddress()
    {
        _address.Visibility = Visibility.Collapsed;
        _crumbs.Visibility = Visibility.Visible;
    }

    // ------------------------------------------------------------------ preview

    private CancellationTokenSource? _previewCts;

    private void UpdatePreview()
    {
        if (!S.ShowPreview || _preview.Visibility != Visibility.Visible) return;
        _previewCts?.Cancel();
        var cts = _previewCts = new CancellationTokenSource();
        var sel = Selection;
        var stack = new StackPanel { Spacing = 10 };
        _preview.Child = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        if (sel.Count != 1)
        {
            string title = sel.Count > 1 ? $"{sel.Count} items selected" : _location == "home" ? "Home" : DisplayName(_location);
            string detail = sel.Count > 1 ? (sel.All(e => !e.IsDir) ? Fmt.Bytes(sel.Sum(e => e.Size)) : "") : _location == "home" ? "" : $"{_all.Count} items";
            stack.Children.Add(new FontIcon { Glyph = sel.Count > 1 ? Glyphs.Copy : Glyphs.FolderOpen, FontSize = 64, Foreground = Ui.B("Vos.TextTertiary"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 40, 0, 6) });
            stack.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Foreground = Ui.B("Vos.TextPrimary") });
            stack.Children.Add(new TextBlock { Text = detail, FontSize = 12.5, TextAlignment = TextAlignment.Center, Foreground = Ui.B("Vos.TextSecondary") });
            return;
        }
        var f = sel[0];
        var visual = new Image { Height = 200, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center };
        stack.Children.Add(new Border { Height = 210, CornerRadius = new CornerRadius(Tokens.RadiusControl), Background = Ui.B("Vos.Card"), Child = visual, Padding = new Thickness(6) });
        if (s_images.Contains(f.Extension)) _ = LoadImageAsync(visual, f.Path, cts.Token);
        else if (!f.IsDir && s_thumbs.Contains(f.Extension)) LoadThumb(visual, f.Path, 256);
        else Images.LoadIconFor(visual, f.Path, 128);
        stack.Children.Add(new TextBlock { Text = f.Name, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Foreground = Ui.B("Vos.TextPrimary"), IsTextSelectionEnabled = true });
        var props = Kit.Stack(4,
            Prop("Type", TypeName(f)),
            f.IsDir ? Prop("Contains", "…") : Prop("Size", $"{Fmt.Bytes(f.Size)} ({f.Size.ToString("N0", CultureInfo.CurrentCulture)} bytes)"),
            Prop("Modified", f.Modified.ToString("g", CultureInfo.CurrentCulture)),
            Prop("Created", f.Created.ToString("g", CultureInfo.CurrentCulture)),
            Prop("Location", Path.GetDirectoryName(f.Path) ?? ""));
        stack.Children.Add(props);
        if (f.IsDir)
        {
            var countLine = (TextBlock)((StackPanel)props.Children[1]).Children[1];
            _ = Task.Run(() =>
            {
                int files = 0, dirs = 0;
                try { foreach (var e in new DirectoryInfo(f.Path).EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true })) { if (cts.IsCancellationRequested) return; if ((e.Attributes & FileAttributes.Directory) != 0) dirs++; else files++; if (files + dirs > 100000) break; } }
                catch { }
                UiThread.Post(() => { if (!cts.IsCancellationRequested) countLine.Text = $"{files} files, {dirs} folders"; });
            });
        }
        if (s_text.Contains(f.Extension) && f.Size > 0)
        {
            var text = new TextBlock { FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Foreground = Ui.B("Vos.TextSecondary"), IsTextSelectionEnabled = true };
            stack.Children.Add(Kit.Card(text, new Thickness(10)));
            _ = Task.Run(async () =>
            {
                try
                {
                    using var fs = new FileStream(f.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    var buffer = new byte[6144];
                    int n = await fs.ReadAsync(buffer, cts.Token);
                    var s = System.Text.Encoding.UTF8.GetString(buffer, 0, n);
                    UiThread.Post(() => { if (!cts.IsCancellationRequested) text.Text = s + (fs.Length > n ? "\n…" : ""); });
                }
                catch { }
            });
        }
        var actions = Kit.Row(8, Buttons.Accent("Open", () => Open(f)), Buttons.Filled("Show in Explorer", () => ShellOps.ShowInExplorer(f.Path)));
        stack.Children.Add(actions);
    }

    private static StackPanel Prop(string label, string value) => Kit.Stack(1, Ui.Text(label, 11.5, "Vos.TextTertiary"), new TextBlock { Text = value, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Foreground = Ui.B("Vos.TextPrimary"), IsTextSelectionEnabled = true });

    private static async Task LoadImageAsync(Image target, string path, CancellationToken ct)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(path, ct);
            if (ct.IsCancellationRequested) return;
            var bmp = await Images.FromBytesAsync(bytes, 520);
            if (!ct.IsCancellationRequested && bmp != null) target.Source = bmp;
        }
        catch { }
    }

    // ------------------------------------------------------------------ menus and commands

    private MenuFlyout ItemMenu()
    {
        var sel = Selection;
        var menu = new MenuFlyout();
        bool single = sel.Count == 1;
        var first = sel.FirstOrDefault();
        if (first == null) return menu;
        menu.Items.Add(MenuItem(single && first.IsDir ? "Open" : "Open", Glyphs.FolderOpen, () => { foreach (var f in sel.Take(10)) Open(f); }));
        if (single && !first.IsDir) menu.Items.Add(MenuItem("Open with…", Glyphs.Apps, () => FileOps.OpenWith(first.Path)));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(MenuItem("Cut", Glyphs.Cut, () => _ = SetClipboardAsync(move: true), "Ctrl+X"));
        menu.Items.Add(MenuItem("Copy", Glyphs.Copy, () => _ = SetClipboardAsync(move: false), "Ctrl+C"));
        if (single) menu.Items.Add(MenuItem("Rename", Glyphs.Rename, () => _ = RenameAsync(first), "F2"));
        menu.Items.Add(MenuItem("Delete", Glyphs.Delete, () => _ = DeleteAsync(), "Del"));
        menu.Items.Add(new MenuFlyoutSeparator());
        if (single && first.Extension == ".zip") menu.Items.Add(MenuItem("Extract here", Glyphs.Archive, () => _ = ExtractAsync(first)));
        menu.Items.Add(MenuItem("Compress to ZIP", Glyphs.Archive, () => _ = CompressAsync(sel)));
        menu.Items.Add(MenuItem("Copy path", Glyphs.Link, () => { var dp = new DataPackage(); dp.SetText(string.Join("\r\n", sel.Select(s => s.Path))); Clipboard.SetContent(dp); }));
        if (single && first.IsDir)
        {
            bool fav = (S.Favorites ?? []).Any(x => string.Equals(x.Path, first.Path, StringComparison.OrdinalIgnoreCase));
            menu.Items.Add(MenuItem(fav ? "Remove from favourites" : "Add to favourites", Glyphs.Star, () => SettingsStore.Instance.Update(s =>
            {
                s.Files.Favorites ??= [];
                if (fav) s.Files.Favorites.RemoveAll(x => string.Equals(x.Path, first.Path, StringComparison.OrdinalIgnoreCase));
                else s.Files.Favorites.Add(new FavoriteFolder { Name = first.Name, Path = first.Path });
            })));
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(MenuItem("Show in Explorer", Glyphs.Folder, () => ShellOps.ShowInExplorer(first.Path)));
        if (single) menu.Items.Add(MenuItem("Properties", Glyphs.Info, () => FileOps.ShowProperties(first.Path, Frame.Hwnd)));
        return menu;
    }

    private MenuFlyout BackgroundMenu()
    {
        var menu = new MenuFlyout();
        menu.Items.Add(MenuItem("New folder", Glyphs.Folder, () => _ = NewFolderAsync(), "Ctrl+Shift+N"));
        menu.Items.Add(MenuItem("New text document", Glyphs.Document, () => _ = NewTextFileAsync()));
        var paste = MenuItem("Paste", Glyphs.Paste, () => _ = PasteAsync(), "Ctrl+V");
        try { paste.IsEnabled = Clipboard.GetContent().Contains(StandardDataFormats.StorageItems); } catch { paste.IsEnabled = false; }
        menu.Items.Add(paste);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(MenuItem("Refresh", Glyphs.Refresh, () => _ = LoadAsync(true), "F5"));
        menu.Items.Add(MenuItem("Open in Terminal", Glyphs.Keyboard, OpenTerminal));
        menu.Items.Add(MenuItem("Show in Explorer", Glyphs.Folder, () => ShellOps.Launch(_location)));
        menu.Items.Add(MenuItem("Properties", Glyphs.Info, () => FileOps.ShowProperties(_location, Frame.Hwnd)));
        return menu;
    }

    private static MenuFlyoutItem MenuItem(string text, string glyph, Action a, string? accel = null)
    {
        var i = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        if (accel != null) i.KeyboardAcceleratorTextOverride = accel;
        i.Click += (_, _) => a();
        return i;
    }

    private void OpenTerminal()
    {
        if (_location == "home") return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("wt.exe", $"-d \"{_location}\"") { UseShellExecute = true }); }
        catch { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powershell.exe") { WorkingDirectory = _location, UseShellExecute = true }); } catch (Exception ex) { Log.Warn("Terminal failed", ex); } }
    }

    private async Task NewFolderAsync()
    {
        if (_location == "home") return;
        var path = FileOps.UniqueName(_location, "New folder");
        try { Directory.CreateDirectory(path); }
        catch (Exception ex) { ShowInfo("The folder could not be created", ex.Message, InfoBarSeverity.Error); return; }
        await LoadAsync(true);
        var entry = _all.FirstOrDefault(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));
        if (entry != null) { _selected.Clear(); _selected.Add(entry.Path); ApplyView(); await RenameAsync(entry); }
    }

    private async Task NewTextFileAsync()
    {
        if (_location == "home") return;
        var path = FileOps.UniqueName(_location, "New text document", ".txt");
        try { await File.WriteAllTextAsync(path, ""); }
        catch (Exception ex) { ShowInfo("The file could not be created", ex.Message, InfoBarSeverity.Error); return; }
        await LoadAsync(true);
        var entry = _all.FirstOrDefault(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));
        if (entry != null) { _selected.Clear(); _selected.Add(entry.Path); ApplyView(); await RenameAsync(entry); }
    }

    private async Task RenameAsync(FileEntry f)
    {
        var box = new TextBox { Text = f.Name };
        var dlg = new ContentDialog { XamlRoot = Frame.Root.XamlRoot, Title = "Rename", Content = box, PrimaryButtonText = "Rename", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary, RequestedTheme = ThemeService.Current.ElementTheme };
        box.Loaded += (_, _) => { box.Focus(FocusState.Programmatic); int dot = f.IsDir ? -1 : f.Name.LastIndexOf('.'); box.Select(0, dot > 0 ? dot : f.Name.Length); };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return; // Enter triggers the default (primary) button
        var error = FileOps.Rename(f.Path, box.Text);
        if (error != null) { ShowInfo("Rename failed", error, InfoBarSeverity.Warning); return; }
        var newPath = Path.Combine(Path.GetDirectoryName(f.Path.TrimEnd('\\'))!, box.Text.Trim());
        _selected.Clear(); _selected.Add(newPath);
        await LoadAsync(true);
    }

    private async Task DeleteAsync()
    {
        var sel = Selection;
        if (sel.Count == 0) return;
        if (S.ConfirmDelete && !await Dialogs.ConfirmAsync(Frame.Root.XamlRoot, sel.Count == 1 ? $"Move “{sel[0].Name}” to the Recycle Bin?" : $"Move {sel.Count} items to the Recycle Bin?", "You can restore them from the Recycle Bin.", "Move to Recycle Bin"))
            return;
        if (!await FileOps.RecycleAsync(sel.Select(s => s.Path).ToList(), Frame.Hwnd)) ShowInfo("Some items were not deleted", "They may be open in another app or you may not have permission.", InfoBarSeverity.Warning);
        _selected.Clear();
        await LoadAsync(true);
    }

    private async Task CompressAsync(List<FileEntry> sel)
    {
        if (sel.Count == 0) return;
        var name = sel.Count == 1 ? Path.GetFileNameWithoutExtension(sel[0].Name) : DisplayName(_location);
        var zip = FileOps.UniqueName(_location, name, ".zip");
        ShowInfo("Compressing…", Path.GetFileName(zip), InfoBarSeverity.Informational);
        try
        {
            await FileOps.CompressAsync(sel.Select(s => s.Path).ToList(), zip, new Progress<double>(p => _info.Message = $"{Path.GetFileName(zip)} · {p:P0}"));
            _info.IsOpen = false;
            _selected.Clear(); _selected.Add(zip);
            await LoadAsync(true);
        }
        catch (Exception ex) { ShowInfo("Compression failed", ex.Message, InfoBarSeverity.Error); }
    }

    private async Task ExtractAsync(FileEntry zip)
    {
        ShowInfo("Extracting…", zip.Name, InfoBarSeverity.Informational);
        try
        {
            var folder = await FileOps.ExtractAsync(zip.Path, new Progress<double>(p => _info.Message = $"{zip.Name} · {p:P0}"));
            _info.IsOpen = false;
            _selected.Clear(); _selected.Add(folder);
            await LoadAsync(true);
        }
        catch (InvalidDataException) { ShowInfo("This archive is damaged or not a ZIP file", zip.Name, InfoBarSeverity.Error); }
        catch (Exception ex) { ShowInfo("Extraction failed", ex.Message, InfoBarSeverity.Error); }
    }

    // ------------------------------------------------------------------ clipboard, drag and drop

    private static async Task<List<IStorageItem>> StorageItemsAsync(IEnumerable<string> paths)
    {
        var items = new List<IStorageItem>();
        foreach (var p in paths)
        {
            try { items.Add(Directory.Exists(p) ? await StorageFolder.GetFolderFromPathAsync(p) : await StorageFile.GetFileFromPathAsync(p)); }
            catch (Exception ex) { Log.Debug("Storage item unavailable", ex.Message); }
        }
        return items;
    }

    private async Task SetClipboardAsync(bool move)
    {
        var sel = Selection;
        if (sel.Count == 0) return;
        var dp = new DataPackage { RequestedOperation = move ? DataPackageOperation.Move : DataPackageOperation.Copy };
        dp.SetStorageItems(await StorageItemsAsync(sel.Select(s => s.Path)));
        dp.SetText(string.Join("\r\n", sel.Select(s => s.Path)));
        Clipboard.SetContent(dp);
        Clipboard.Flush();
        ShowInfo(move ? "Cut" : "Copied", $"{sel.Count} item{(sel.Count == 1 ? "" : "s")} · paste with Ctrl+V here or in Explorer", InfoBarSeverity.Informational, autoClose: true);
    }

    private async Task PasteAsync()
    {
        if (_location == "home") return;
        DataPackageView content;
        try { content = Clipboard.GetContent(); } catch { return; }
        if (!content.Contains(StandardDataFormats.StorageItems)) return;
        var items = await content.GetStorageItemsAsync();
        var paths = items.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
        if (paths.Count == 0) return;
        bool move = content.RequestedOperation.HasFlag(DataPackageOperation.Move);
        bool ok = move ? await FileOps.MoveAsync(paths, _location, Frame.Hwnd) : await FileOps.CopyAsync(paths, _location, Frame.Hwnd);
        if (move && ok) Clipboard.Clear();
        await LoadAsync(true);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (_location == "home" || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        bool move = e.Modifiers.HasFlag(Windows.ApplicationModel.DataTransfer.DragDrop.DragDropModifiers.Shift);
        e.AcceptedOperation = move ? DataPackageOperation.Move : DataPackageOperation.Copy;
        e.DragUIOverride.Caption = (move ? "Move to " : "Copy to ") + DisplayName(_location);
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (_location == "home" || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var paths = items.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p) && !string.Equals(Path.GetDirectoryName(p.TrimEnd('\\')), _location.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)).ToList();
            if (paths.Count == 0) return;
            bool move = e.AcceptedOperation == DataPackageOperation.Move;
            if (move) await FileOps.MoveAsync(paths, _location, Frame.Hwnd); else await FileOps.CopyAsync(paths, _location, Frame.Hwnd);
        }
        finally { deferral.Complete(); }
        await LoadAsync(true);
    }

    // ------------------------------------------------------------------ search

    private CancellationTokenSource? _searchCts;

    private async Task DeepSearchAsync(string query)
    {
        if (_location == "home") return;
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        _searchResultsFor = query;
        ShowInfo("Searching…", $"“{query}” in {DisplayName(_location)} and its subfolders. Press Esc to go back.", InfoBarSeverity.Informational);
        var location = _location;
        bool hidden = S.ShowHidden;
        var results = await Task.Run(() =>
        {
            var list = new List<FileEntry>();
            var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive, AttributesToSkip = hidden ? 0 : FileAttributes.Hidden | FileAttributes.System };
            try
            {
                foreach (var fsi in new DirectoryInfo(location).EnumerateFileSystemInfos("*" + query + "*", opts))
                {
                    if (cts.IsCancellationRequested || list.Count >= 1000) break;
                    bool dir = (fsi.Attributes & FileAttributes.Directory) != 0;
                    list.Add(new FileEntry { Path = fsi.FullName, Name = fsi.Name, IsDir = dir, Size = dir ? 0 : ((FileInfo)fsi).Length, Modified = fsi.LastWriteTime, Created = fsi.CreationTime, Attributes = fsi.Attributes });
                }
            }
            catch (Exception ex) { Log.Debug("Search stopped", ex.Message); }
            return list;
        }, cts.Token);
        if (cts.IsCancellationRequested || location != _location) return;
        _all = results;
        _selected.Clear();
        ShowList();
        ApplyView();
        ShowInfo(results.Count == 0 ? "Nothing found" : $"{results.Count}{(results.Count >= 1000 ? "+" : "")} results", $"for “{query}” in {DisplayName(location)}. Press Esc to go back to the folder.", results.Count == 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
    }

    // ------------------------------------------------------------------ keyboard

    private void OnKey(object sender, KeyRoutedEventArgs e)
    {
        bool inText = e.OriginalSource is TextBox;
        bool ctrl = KeyDown(VirtualKey.Control), alt = KeyDown(VirtualKey.Menu), shift = KeyDown(VirtualKey.Shift);
        switch (e.Key)
        {
            case VirtualKey.Left when alt: Back(); break;
            case VirtualKey.Right when alt: Forward(); break;
            case VirtualKey.Up when alt: Up(); break;
            case VirtualKey.Back when !inText: Back(); break;
            case VirtualKey.F5: _ = LoadAsync(true); break;
            case VirtualKey.L when ctrl: EditAddress(); break;
            case VirtualKey.F when ctrl: _search.Focus(FocusState.Keyboard); break;
            case VirtualKey.N when ctrl && shift: _ = NewFolderAsync(); break;
            case VirtualKey.A when ctrl && !inText: foreach (var f in _shown) _selected.Add(f.Path); _list?.RefreshVisible(); UpdateStatus(); UpdatePreview(); break;
            case VirtualKey.C when ctrl && !inText: _ = SetClipboardAsync(false); break;
            case VirtualKey.X when ctrl && !inText: _ = SetClipboardAsync(true); break;
            case VirtualKey.V when ctrl && !inText: _ = PasteAsync(); break;
            case VirtualKey.Delete when !inText: _ = DeleteAsync(); break;
            case VirtualKey.F2 when !inText && Selection.Count == 1: _ = RenameAsync(Selection[0]); break;
            case VirtualKey.Enter when !inText && Selection.Count > 0: foreach (var f in Selection.Take(10)) Open(f); break;
            case VirtualKey.Escape when !inText: ClearSelection(); break;
            case VirtualKey.Down when !inText && !alt: MoveSelection(+1, shift); break;
            case VirtualKey.Up when !inText && !alt: MoveSelection(-1, shift); break;
            case VirtualKey.Home when !inText: MoveSelection(int.MinValue, shift); break;
            case VirtualKey.End when !inText: MoveSelection(int.MaxValue, shift); break;
            default: return;
        }
        e.Handled = true;
    }

    /// <summary>Arrow-key selection (Shift extends from the anchor), keeping the focused item in view.</summary>
    private void MoveSelection(int delta, bool extend)
    {
        if (_shown.Count == 0) return;
        int current = _anchor >= 0 ? _anchor : -1;
        if (!extend && _selected.Count > 0) current = _shown.FindLastIndex(e => _selected.Contains(e.Path));
        int target = delta switch { int.MinValue => 0, int.MaxValue => _shown.Count - 1, _ => Math.Clamp(current + delta, 0, _shown.Count - 1) };
        if (extend && _anchor >= 0)
        {
            _selected.Clear();
            for (int i = Math.Min(_anchor, target); i <= Math.Max(_anchor, target); i++) _selected.Add(_shown[i].Path);
        }
        else { _selected.Clear(); _selected.Add(_shown[target].Path); _anchor = target; }
        _list?.ScrollIntoView(target);
        _list?.RefreshVisible();
        UpdateStatus();
        UpdatePreview();
    }

    // ------------------------------------------------------------------ helpers

    private Button SideItem(string label, string glyph, Action a)
    {
        var b = Kit.ListRow(Ui.Icon(glyph, 15), label, null, null, a, 36);
        b.Padding = new Thickness(12, 6, 10, 6);
        return b;
    }

    private static TextBlock SideHeader(string text) => new() { Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Ui.B("Vos.TextTertiary"), Margin = new Thickness(12, 14, 0, 4) };

    private static SolidColorBrush Transparent() => new(Microsoft.UI.Colors.Transparent);

    private void ShowInfo(string title, string message, InfoBarSeverity severity, bool autoClose = false)
    {
        _info.Title = title; _info.Message = message; _info.Severity = severity; _info.IsOpen = true;
        if (autoClose) { var t = UiThread.Timer(TimeSpan.FromSeconds(3), () => { if (_info.Title == title) _info.IsOpen = false; }, repeating: false); t.Start(); }
    }

    private static string DisplayName(string path) => path == "home" ? "Home" : Path.GetFileName(path.TrimEnd('\\')) is { Length: > 0 } n ? n : path.TrimEnd('\\');

    private static string DisplayFileName(FileEntry f) => f.IsDir || S.ShowExtensions ? f.Name : Path.GetFileNameWithoutExtension(f.Name) is { Length: > 0 } n ? n : f.Name;

    private static string TypeName(FileEntry f)
    {
        if (f.IsDir) return "Folder";
        return f.Extension switch
        {
            "" => "File",
            ".txt" => "Text document", ".md" => "Markdown", ".pdf" => "PDF document", ".doc" or ".docx" => "Word document", ".xls" or ".xlsx" => "Excel workbook",
            ".ppt" or ".pptx" => "PowerPoint presentation", ".zip" => "ZIP archive", ".7z" => "7-Zip archive", ".rar" => "RAR archive",
            ".exe" => "Application", ".msi" => "Windows installer", ".lnk" => "Shortcut", ".url" => "Internet shortcut", ".dll" => "Library",
            ".jpg" or ".jpeg" => "JPEG image", ".png" => "PNG image", ".gif" => "GIF image", ".webp" => "WebP image", ".svg" => "SVG image", ".bmp" => "Bitmap image", ".ico" => "Icon", ".heic" => "HEIC image",
            ".mp3" => "MP3 audio", ".flac" => "FLAC audio", ".wav" => "WAV audio", ".m4a" => "M4A audio", ".ogg" => "Ogg audio",
            ".mp4" => "MP4 video", ".mkv" => "Matroska video", ".mov" => "QuickTime video", ".avi" => "AVI video",
            ".json" => "JSON file", ".xml" => "XML file", ".html" or ".htm" => "Web page", ".css" => "Stylesheet", ".js" => "JavaScript", ".ts" => "TypeScript",
            ".cs" => "C# source", ".py" => "Python script", ".ps1" => "PowerShell script", ".bat" or ".cmd" => "Batch file", ".ini" or ".cfg" => "Settings file", ".log" => "Log file", ".csv" => "CSV file",
            _ => f.Extension.TrimStart('.').ToUpperInvariant() + " file",
        };
    }
}

/// <summary>Natural sort: "file2" before "file10", like Explorer.</summary>
public sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();
    [System.Runtime.InteropServices.DllImport("shlwapi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string a, string b);
    public int Compare(string? x, string? y) => StrCmpLogicalW(x ?? "", y ?? "");
}
