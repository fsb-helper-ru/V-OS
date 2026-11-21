using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using VOS.Core;
using VOS.Core.Data;
using VOS.Core.Settings;
using VOS.Platform.Clipboard;
using VOS.Platform.Input;
using VOS.Platform.Native;
using VOS.Platform.Shell;
using VOS.Platform.Windowing;
using VOS.UI;
using VOS.UI.Theme;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;

namespace VOS.Shell;

/// <summary>V-Clipboard store: text, links, images and files in SQLite (images as PNG in the cache), with expiry and pins.</summary>
public sealed class ClipboardHistory
{
    public sealed record Clip(string Id, ClipKind Kind, string? Text, string[]? Files, string? ImagePath, string? Source, DateTimeOffset Created, bool Pinned);

    private readonly Db _db = Db.ForModule("clipboard");
    private static string ImageDir => Directory.CreateDirectory(Path.Combine(VosPaths.Cache, "clipboard")).FullName;
    public event Action? Changed;

    public ClipboardHistory()
    {
        _db.Migrate("CREATE TABLE clips(id TEXT PRIMARY KEY, kind INTEGER, text TEXT, files TEXT, image TEXT, source TEXT, created TEXT, pinned INTEGER DEFAULT 0, hash TEXT);" +
                    "CREATE INDEX idx_clips_created ON clips(created); CREATE INDEX idx_clips_hash ON clips(hash);");
        Prune();
    }

    public void Add(ClipContent c)
    {
        string hash = Convert.ToHexString(SHA1.HashData(c.Kind switch
        {
            ClipKind.Image => c.Image!.Bgra.AsSpan(0, Math.Min(c.Image.Bgra.Length, 65536)).ToArray(),
            ClipKind.Files => Encoding.UTF8.GetBytes(string.Join('|', c.Files!)),
            _ => Encoding.UTF8.GetBytes(c.Text ?? ""),
        }));
        var now = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture);
        var existing = _db.Scalar<string>("SELECT id FROM clips WHERE hash=$h", ("$h", hash));
        if (existing != null) { _db.Execute("UPDATE clips SET created=$c WHERE id=$i", ("$c", now), ("$i", existing)); Changed?.Invoke(); return; }
        var id = Guid.NewGuid().ToString("N")[..16];
        string? imagePath = null;
        if (c.Kind == ClipKind.Image && c.Image != null)
        {
            imagePath = Path.Combine(ImageDir, id + ".png");
            File.WriteAllBytes(imagePath, c.Image.ToPng());
        }
        _db.Execute("INSERT INTO clips(id,kind,text,files,image,source,created,hash) VALUES($i,$k,$t,$f,$img,$s,$c,$h)",
            ("$i", id), ("$k", (int)c.Kind), ("$t", c.Text), ("$f", c.Files != null ? string.Join('\n', c.Files) : null), ("$img", imagePath),
            ("$s", c.SourceExe != null ? Path.GetFileName(c.SourceExe) : null), ("$c", now), ("$h", hash));
        Prune();
        Changed?.Invoke();
    }

    public List<Clip> List(string? search = null, int limit = 200)
    {
        var sql = "SELECT * FROM clips" + (string.IsNullOrWhiteSpace(search) ? "" : " WHERE text LIKE $q OR files LIKE $q") + " ORDER BY pinned DESC, created DESC LIMIT " + limit;
        return _db.Query(sql, r => new Clip(Db.Str(r, "id")!, (ClipKind)Db.Long(r, "kind"), Db.Str(r, "text"), Db.Str(r, "files")?.Split('\n'),
            Db.Str(r, "image"), Db.Str(r, "source"), DateTimeOffset.Parse(Db.Str(r, "created")!, CultureInfo.InvariantCulture), Db.Bool(r, "pinned")),
            ("$q", "%" + search + "%"));
    }

    public void SetPinned(string id, bool pinned) { _db.Execute("UPDATE clips SET pinned=$p WHERE id=$i", ("$p", pinned ? 1 : 0), ("$i", id)); Changed?.Invoke(); }

    public void Delete(string id)
    {
        var img = _db.Scalar<string>("SELECT image FROM clips WHERE id=$i", ("$i", id));
        _db.Execute("DELETE FROM clips WHERE id=$i", ("$i", id));
        if (img != null) try { File.Delete(img); } catch { }
        Changed?.Invoke();
    }

    public void ClearUnpinned()
    {
        foreach (var c in List().Where(c => !c.Pinned)) Delete(c.Id);
    }

    private void Prune()
    {
        var s = SettingsStore.Instance.Current.Clipboard;
        var cutoff = DateTimeOffset.Now.AddDays(-Math.Max(1, s.RetentionDays)).ToString("o", CultureInfo.InvariantCulture);
        foreach (var img in _db.Query("SELECT image FROM clips WHERE pinned=0 AND (created < $c OR id NOT IN (SELECT id FROM clips ORDER BY pinned DESC, created DESC LIMIT $m)) AND image IS NOT NULL",
            r => Db.Str(r, "image"), ("$c", cutoff), ("$m", s.MaxItems)))
            if (img != null) try { File.Delete(img); } catch { }
        _db.Execute("DELETE FROM clips WHERE pinned=0 AND (created < $c OR id NOT IN (SELECT id FROM clips ORDER BY pinned DESC, created DESC LIMIT $m))", ("$c", cutoff), ("$m", s.MaxItems));
    }
}

/// <summary>Clipboard history panel: search, pin, delete; choosing an item pastes it into the previous window.</summary>
public sealed class ClipboardPanel : PanelWindow
{
    private readonly TextBox _search = new() { PlaceholderText = "Search clipboard history" };
    private readonly StackPanel _list = new() { Spacing = 6 };

    public ClipboardPanel(ShellHost host) : base(host, "V-OS Clipboard", 420, 540)
    {
        Placement = PanelPlacement.Center;
        var grid = new Grid { Padding = new Thickness(14), RowSpacing = 10 };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var head = new Grid();
        head.Children.Add(Ui.Text("Clipboard", 16, "Vos.TextPrimary", Tokens.Display, FontWeights.SemiBold));
        var clear = Buttons.Subtle(Ui.Text("Clear", 12, "Vos.AccentText"), () => { host.ClipboardHistory?.ClearUnpinned(); Build(); }, 6, new Thickness(6, 2, 6, 2));
        clear.HorizontalAlignment = HorizontalAlignment.Right;
        head.Children.Add(clear);
        grid.Children.Add(head);
        Grid.SetRow(_search, 1);
        _search.TextChanged += (_, _) => Build();
        grid.Children.Add(_search);
        var scroll = new ScrollViewer { Content = _list };
        Grid.SetRow(scroll, 2);
        grid.Children.Add(scroll);
        Body.Children.Add(grid);
    }

    protected override void OnOpening() { _search.Text = ""; Build(); }
    protected override void OnOpened() => _search.Focus(FocusState.Programmatic);

    private void Build()
    {
        _list.Children.Clear();
        var history = Host.ClipboardHistory;
        if (history == null || !Host.Settings.Current.Clipboard.Enabled)
        {
            _list.Children.Add(Ui.Text("Clipboard history is off. Turn it on in Settings › Privacy.", 13, "Vos.TextSecondary", null, null, TextWrapping.Wrap));
            return;
        }
        var clips = history.List(_search.Text);
        if (clips.Count == 0)
        {
            var empty = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 40, 0, 0) };
            empty.Children.Add(Ui.Icon(Glyphs.Clipboard, 22, "Vos.TextTertiary"));
            empty.Children.Add(Ui.Text("Copied text, links, images and files appear here", 13, "Vos.TextSecondary"));
            _list.Children.Add(empty);
            return;
        }
        foreach (var c in clips) _list.Children.Add(Card(c));
    }

    private Grid Card(ClipboardHistory.Clip c)
    {
        var g = new Grid { Padding = new Thickness(12, 10, 6, 10), ColumnSpacing = 8, CornerRadius = new CornerRadius(Tokens.RadiusCard), Background = Ui.B("Vos.Control") };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        FrameworkElement content = c.Kind switch
        {
            ClipKind.Image when c.ImagePath != null => new Image { Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(c.ImagePath)) { DecodePixelWidth = 340 }, MaxHeight = 140, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left },
            ClipKind.Files => Ui.Text(string.Join("\n", c.Files!.Take(4).Select(Path.GetFileName)) + (c.Files!.Length > 4 ? $"\n+{c.Files.Length - 4} more" : ""), 13, "Vos.TextPrimary", null, null, TextWrapping.Wrap),
            _ => Ui.Text(c.Text!.Length > 400 ? c.Text[..400] + "…" : c.Text, 13, c.Kind == ClipKind.Link ? "Vos.AccentText" : "Vos.TextPrimary", null, null, TextWrapping.Wrap),
        };
        if (content is TextBlock tb) tb.MaxLines = 5;
        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(content);
        stack.Children.Add(Ui.Text($"{Fmt.Relative(c.Created)}{(c.Source != null ? " · " + Path.GetFileNameWithoutExtension(c.Source) : "")}", 11, "Vos.TextTertiary"));
        g.Children.Add(stack);
        var actions = new StackPanel { Spacing = 2 };
        actions.Children.Add(Buttons.Icon(c.Pinned ? Glyphs.Unpin : Glyphs.Pin, () => { Host.ClipboardHistory!.SetPinned(c.Id, !c.Pinned); Build(); }, 28, 11, c.Pinned ? "Unpin" : "Pin"));
        actions.Children.Add(Buttons.Icon(Glyphs.Delete, () => { Host.ClipboardHistory!.Delete(c.Id); Build(); }, 28, 11, "Delete"));
        Grid.SetColumn(actions, 1);
        g.Children.Add(actions);
        g.Tapped += async (_, e) => { if (e.OriginalSource is FontIcon) return; await PasteAsync(c); };
        return g;
    }

    private async Task PasteAsync(ClipboardHistory.Clip c)
    {
        var target = PreviousForeground;
        Host.Clipboard?.SuppressNext();
        var package = new DataPackage();
        switch (c.Kind)
        {
            case ClipKind.Image when c.ImagePath != null:
                package.SetBitmap(Windows.Storage.Streams.RandomAccessStreamReference.CreateFromFile(await StorageFile.GetFileFromPathAsync(c.ImagePath)));
                break;
            case ClipKind.Files:
                var items = new List<IStorageItem>();
                foreach (var p in c.Files!)
                {
                    try { items.Add(File.Exists(p) ? await StorageFile.GetFileFromPathAsync(p) : await StorageFolder.GetFolderFromPathAsync(p)); } catch { }
                }
                package.SetStorageItems(items);
                break;
            default:
                package.SetText(c.Text ?? "");
                break;
        }
        Clipboard.SetContent(package);
        Clipboard.Flush();
        Dismiss();
        if (target != IntPtr.Zero)
        {
            WindowTracker.Activate(target);
            await Task.Delay(120);
            KeyboardSender.Paste();
        }
    }
}

/// <summary>Window switcher with live DWM thumbnails (Alt+Q by default).</summary>
public sealed class SwitcherPanel : PanelWindow
{
    private readonly VariableSizedWrapGrid _grid = new() { Orientation = Orientation.Horizontal, ItemWidth = 236, ItemHeight = 188, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly List<(IntPtr thumb, FrameworkElement host, AppWindowInfo window)> _thumbs = [];
    private int _selected;

    public SwitcherPanel(ShellHost host) : base(host, "V-OS Switcher", 980, 440)
    {
        Placement = PanelPlacement.Center;
        Body.Children.Add(new ScrollViewer { Content = _grid, Padding = new Thickness(16), VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Root.KeyDown += (_, e) =>
        {
            int cols = Math.Max(1, (int)((PanelWidth - 32) / _grid.ItemWidth));
            switch (e.Key)
            {
                case VirtualKey.Right: case VirtualKey.Tab: Select(_selected + 1); e.Handled = true; break;
                case VirtualKey.Left: Select(_selected - 1); e.Handled = true; break;
                case VirtualKey.Down: Select(_selected + cols); e.Handled = true; break;
                case VirtualKey.Up: Select(_selected - cols); e.Handled = true; break;
                case VirtualKey.Enter: Choose(_selected); e.Handled = true; break;
            }
        };
        Root.LayoutUpdated += (_, _) => PositionThumbnails();
    }

    protected override void OnOpening()
    {
        ReleaseThumbnails();
        _grid.Children.Clear();
        var windows = Host.Tracker.Windows.OrderByDescending(w => w.LastActivated).ToList();
        int cols = Math.Clamp(windows.Count, 1, 4), rows = Math.Clamp((int)Math.Ceiling(windows.Count / 4.0), 1, 3);
        PanelWidth = cols * _grid.ItemWidth + 32;
        PanelHeight = rows * _grid.ItemHeight + 32;
        if (windows.Count == 0)
        {
            _grid.Children.Add(Ui.Text("No open windows", 14, "Vos.TextSecondary"));
            return;
        }
        foreach (var w in windows)
        {
            var cell = new Grid { Margin = new Thickness(6), Padding = new Thickness(8), CornerRadius = new CornerRadius(Tokens.RadiusCard), RowSpacing = 6, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
            cell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            cell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var thumbHost = new Border { CornerRadius = new CornerRadius(8), Background = Ui.B("Vos.Control") };
            cell.Children.Add(thumbHost);
            var title = Ui.Text(w.Title, 12.5);
            Grid.SetRow(title, 1);
            cell.Children.Add(title);
            int index = _thumbs.Count;
            cell.Tapped += (_, _) => Choose(index);
            cell.PointerEntered += (_, _) => Select(index);
            Buttons.AutomationName(cell, w.Title);
            _grid.Children.Add(cell);
            IntPtr thumb = IntPtr.Zero;
            if (UI.Quality.VisualQuality.Current.LiveThumbnails) Win32.DwmRegisterThumbnail(Hwnd, w.Hwnd, out thumb);
            _thumbs.Add((thumb, thumbHost, w));
        }
        _selected = Math.Min(1, _thumbs.Count - 1);
        Select(_selected);
    }

    private void PositionThumbnails()
    {
        if (!IsOpen) return;
        double s = Scale;
        foreach (var (thumb, host, _) in _thumbs)
        {
            if (thumb == IntPtr.Zero || host.ActualWidth < 4) continue;
            var p = host.TransformToVisual(Root).TransformPoint(default);
            Win32.DwmQueryThumbnailSourceSize(thumb, out var src);
            double aw = host.ActualWidth - 8, ah = host.ActualHeight - 8, ratio = src.cx > 0 && src.cy > 0 ? (double)src.cx / src.cy : 16 / 9.0;
            double tw = aw, th = tw / ratio;
            if (th > ah) { th = ah; tw = th * ratio; }
            double x = p.X + 4 + (aw - tw) / 2, y = p.Y + 4 + (ah - th) / 2;
            var props = new Win32.DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = 0x1 | 0x4 | 0x8 | 0x10,
                rcDestination = new Win32.RECT((int)(x * s), (int)(y * s), (int)((x + tw) * s), (int)((y + th) * s)),
                opacity = 255, fVisible = 1, fSourceClientAreaOnly = 0,
            };
            Win32.DwmUpdateThumbnailProperties(thumb, ref props);
        }
    }

    private void Select(int i)
    {
        if (_thumbs.Count == 0) return;
        _selected = (i % _thumbs.Count + _thumbs.Count) % _thumbs.Count;
        for (int k = 0; k < _grid.Children.Count; k++)
            if (_grid.Children[k] is Grid g) g.Background = k == _selected ? Ui.B("Vos.ControlHover") : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    private void Choose(int i)
    {
        if (i < 0 || i >= _thumbs.Count) return;
        var target = _thumbs[i].window.Hwnd;
        Dismiss();
        WindowTracker.Activate(target);
    }

    protected override void OnClosing() => ReleaseThumbnails();

    private void ReleaseThumbnails()
    {
        foreach (var (thumb, _, _) in _thumbs) if (thumb != IntPtr.Zero) Win32.DwmUnregisterThumbnail(thumb);
        _thumbs.Clear();
    }
}

/// <summary>V-Drop: a temporary shelf for files and text, persisted across restarts; drag items back out into any app.</summary>
public sealed class DropShelfPanel : PanelWindow
{
    private sealed class ShelfItem { public string Kind { get; set; } = "file"; public string Value { get; set; } = ""; public DateTimeOffset Added { get; set; } = DateTimeOffset.Now; }
    private static string StatePath => Path.Combine(VosPaths.State, "drop-shelf.json");
    private readonly List<ShelfItem> _items;
    private readonly StackPanel _list = new() { Spacing = 6 };

    public DropShelfPanel(ShellHost host) : base(host, "V-OS Drop", 340, 500)
    {
        Placement = PanelPlacement.Right;
        _items = VosJson.TryDeserialize<List<ShelfItem>>(AtomicFile.TryReadAllText(StatePath)) ?? [];
        var grid = new Grid { Padding = new Thickness(14), RowSpacing = 10, AllowDrop = true, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(Ui.Text("Drop shelf", 16, "Vos.TextPrimary", Tokens.Display, FontWeights.SemiBold));
        var scroll = new ScrollViewer { Content = _list };
        Grid.SetRow(scroll, 1);
        grid.Children.Add(scroll);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        actions.Children.Add(Buttons.Filled(Ui.Text("Copy paths", 12.5), CopyPaths));
        actions.Children.Add(Buttons.Filled(Ui.Text("Compress", 12.5), () => _ = CompressAsync()));
        actions.Children.Add(Buttons.Subtle(Ui.Text("Clear", 12.5), () => { _items.Clear(); Save(); Build(); }));
        Grid.SetRow(actions, 2);
        grid.Children.Add(actions);
        grid.DragOver += (_, e) => { e.AcceptedOperation = DataPackageOperation.Copy | DataPackageOperation.Link; e.DragUIOverride.Caption = "Add to shelf"; };
        grid.Drop += async (_, e) =>
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
                foreach (var i in await e.DataView.GetStorageItemsAsync()) _items.Add(new ShelfItem { Kind = "file", Value = i.Path });
            else if (e.DataView.Contains(StandardDataFormats.Text))
                _items.Add(new ShelfItem { Kind = "text", Value = await e.DataView.GetTextAsync() });
            Save();
            Build();
        };
        Body.Children.Add(grid);
    }

    protected override void OnOpening() => Build();

    private void Save() => AtomicFile.WriteAllText(StatePath, VosJson.Serialize(_items));

    private void Build()
    {
        _list.Children.Clear();
        _items.RemoveAll(i => i.Kind == "file" && !File.Exists(i.Value) && !Directory.Exists(i.Value));
        if (_items.Count == 0)
        {
            var empty = new Border { Height = 200, CornerRadius = new CornerRadius(Tokens.RadiusCard), BorderThickness = new Thickness(1.5), BorderBrush = Ui.B("Vos.Stroke"), Background = Ui.B("Vos.Control") };
            var s = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            s.Children.Add(Ui.Icon(Glyphs.Download, 22, "Vos.TextTertiary"));
            s.Children.Add(Ui.Text("Drop files or text here", 13, "Vos.TextSecondary"));
            empty.Child = s;
            _list.Children.Add(empty);
            return;
        }
        foreach (var item in _items.ToList())
        {
            var row = new Grid { Padding = new Thickness(10, 8, 6, 8), ColumnSpacing = 10, CornerRadius = new CornerRadius(10), Background = Ui.B("Vos.Control"), CanDrag = true };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(Ui.Icon(item.Kind == "text" ? Glyphs.Document : Directory.Exists(item.Value) ? Glyphs.Folder : Glyphs.Document, 14, "Vos.TextSecondary"));
            var name = Ui.Text(item.Kind == "text" ? item.Value.Replace('\n', ' ') : Path.GetFileName(item.Value), 13);
            Grid.SetColumn(name, 1);
            row.Children.Add(name);
            var remove = Buttons.Icon(Glyphs.Close, () => { _items.Remove(item); Save(); Build(); }, 26, 10, "Remove");
            Grid.SetColumn(remove, 2);
            row.Children.Add(remove);
            row.DragStarting += async (_, e) =>
            {
                var d = e.GetDeferral();
                try
                {
                    if (item.Kind == "text") e.Data.SetText(item.Value);
                    else e.Data.SetStorageItems([File.Exists(item.Value) ? await StorageFile.GetFileFromPathAsync(item.Value) : await StorageFolder.GetFolderFromPathAsync(item.Value)]);
                    e.Data.RequestedOperation = DataPackageOperation.Copy;
                }
                finally { d.Complete(); }
            };
            row.DoubleTapped += (_, _) => { if (item.Kind == "file") ShellOps.Launch(item.Value); };
            _list.Children.Add(row);
        }
    }

    private void CopyPaths()
    {
        var text = string.Join(Environment.NewLine, _items.Select(i => i.Value));
        if (text.Length == 0) return;
        var p = new DataPackage();
        p.SetText(text);
        Clipboard.SetContent(p);
    }

    private async Task CompressAsync()
    {
        var files = _items.Where(i => i.Kind == "file").Select(i => i.Value).ToList();
        if (files.Count == 0) return;
        var target = Path.Combine(ShellNative.Downloads, $"Drop {DateTime.Now:yyyy-MM-dd HHmm}.zip");
        await Task.Run(() =>
        {
            using var zip = ZipFile.Open(target, ZipArchiveMode.Create);
            foreach (var f in files)
            {
                if (File.Exists(f)) zip.CreateEntryFromFile(f, Path.GetFileName(f), CompressionLevel.Optimal);
                else if (Directory.Exists(f))
                    foreach (var sub in Directory.EnumerateFiles(f, "*", SearchOption.AllDirectories))
                        zip.CreateEntryFromFile(sub, Path.Combine(Path.GetFileName(f), Path.GetRelativePath(f, sub)), CompressionLevel.Optimal);
            }
        });
        ShellOps.ShowInExplorer(target);
    }
}

/// <summary>Window layouts: place the window that was in front into a zone of its monitor.</summary>
public sealed class LayoutPanel : PanelWindow
{
    private static readonly (string name, (double x, double y, double w, double h)[] zones)[] Layouts =
    [
        ("Halves", [(0, 0, 0.5, 1), (0.5, 0, 0.5, 1)]),
        ("Thirds", [(0, 0, 1 / 3.0, 1), (1 / 3.0, 0, 1 / 3.0, 1), (2 / 3.0, 0, 1 / 3.0, 1)]),
        ("Focus", [(0, 0, 2 / 3.0, 1), (2 / 3.0, 0, 1 / 3.0, 1)]),
        ("Quarters", [(0, 0, 0.5, 0.5), (0.5, 0, 0.5, 0.5), (0, 0.5, 0.5, 0.5), (0.5, 0.5, 0.5, 0.5)]),
        ("Centre", [(0.15, 0.08, 0.7, 0.84)]),
        ("Full", [(0, 0, 1, 1)]),
    ];

    public LayoutPanel(ShellHost host) : base(host, "V-OS Layouts", 560, 300)
    {
        Placement = PanelPlacement.Center;
        var root = new StackPanel { Padding = new Thickness(18), Spacing = 12 };
        root.Children.Add(Ui.Text("Place the window", 16, "Vos.TextPrimary", Tokens.Display, FontWeights.SemiBold));
        root.Children.Add(Ui.Text("Choose a zone for the window that was in front.", 12.5, "Vos.TextSecondary"));
        var grid = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, ItemWidth = 172, ItemHeight = 108 };
        foreach (var (name, zones) in Layouts)
        {
            var cell = new StackPanel { Spacing = 6, Margin = new Thickness(4) };
            var canvas = new Grid { Width = 150, Height = 72, CornerRadius = new CornerRadius(8), Background = Ui.B("Vos.Control") };
            foreach (var z in zones)
            {
                var zone = new Border
                {
                    Width = z.w * 150 - 6, Height = z.h * 72 - 6, Margin = new Thickness(z.x * 150 + 3, z.y * 72 + 3, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, CornerRadius = new CornerRadius(5),
                    Background = Ui.B("Vos.ControlHover"), BorderThickness = new Thickness(1), BorderBrush = Ui.B("Vos.Stroke"),
                };
                zone.PointerEntered += (_, _) => zone.Background = Ui.B("Vos.Accent");
                zone.PointerExited += (_, _) => zone.Background = Ui.B("Vos.ControlHover");
                var captured = z;
                zone.Tapped += (_, _) => Place(captured);
                canvas.Children.Add(zone);
            }
            cell.Children.Add(canvas);
            cell.Children.Add(Ui.Text(name, 12, "Vos.TextSecondary"));
            grid.Children.Add(cell);
        }
        root.Children.Add(grid);
        Body.Children.Add(root);
    }

    private void Place((double x, double y, double w, double h) z)
    {
        var target = PreviousForeground;
        Dismiss();
        if (target == IntPtr.Zero || !WindowTracker.IsAppWindow(target)) return;
        Win32.ShowWindow(target, Win32.SW_RESTORE);
        var mon = Win32.MonitorFromWindow(target, 2);
        var mi = new Win32.MONITORINFOEX { cbSize = Marshal.SizeOf<Win32.MONITORINFOEX>() };
        Win32.GetMonitorInfoW(mon, ref mi);
        var work = mi.rcWork;
        // Compensate for the invisible resize borders Windows adds around top-level windows.
        Win32.GetWindowRect(target, out var outer);
        Win32.DwmGetWindowAttribute(target, Win32.DWMWA_EXTENDED_FRAME_BOUNDS, out Win32.RECT frame, Marshal.SizeOf<Win32.RECT>());
        int dl = frame.Left - outer.Left, dt = frame.Top - outer.Top, dr = outer.Right - frame.Right, db = outer.Bottom - frame.Bottom;
        int gap = 6;
        int x = work.Left + (int)(z.x * work.Width) + gap, y = work.Top + (int)(z.y * work.Height) + gap;
        int w = (int)(z.w * work.Width) - gap * 2, h = (int)(z.h * work.Height) - gap * 2;
        Win32.SetWindowPos(target, IntPtr.Zero, x - dl, y - dt, w + dl + dr, h + dt + db, Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
        WindowTracker.Activate(target);
    }
}
