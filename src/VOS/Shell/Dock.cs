using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using VOS.Core;
using VOS.Core.Settings;
using VOS.Platform.Native;
using VOS.Platform.Shell;
using VOS.Platform.Windowing;
using VOS.UI;
using VOS.UI.Quality;
using VOS.UI.Surfaces;
using VOS.UI.Theme;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;

namespace VOS.Shell;

/// <summary>V-Dock: pinned and running apps on a glass slab, with compositor magnification and click-through margins.</summary>
public sealed class DockSurface : SurfaceWindow
{
    private sealed class Entry
    {
        public string Kind = "";          // vos, app, path, running, library, trash
        public string Key = "";
        public string Name = "";
        public VosApp? Vos;
        public InstalledApp? Installed;
        public string? Path;
        public string? AppId;
        public DockItem? Pin;
        public List<AppWindowInfo> Windows = [];
        public Grid Element = null!;
        public StackPanel Dots = null!;
        public int CycleIndex;
    }

    private const double Pad = 10, Gap = 10, Overflow = 90, Head = 70, LabelGap = 6;
    private readonly ShellHost _host;
    private readonly Grid _layer = new() { VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly GlassSurface _slab;
    private readonly StackPanel _items = new() { Orientation = Orientation.Horizontal, Spacing = Gap, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom };
    private readonly Border _label = new() { CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 4, 10, 5), Background = Ui.B("Vos.Ink"), Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
    private readonly TextBlock _labelText = new() { FontSize = 12, Foreground = Ui.B("Vos.InkText") };
    private readonly List<Entry> _entries = [];
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _settle, _hideDelay;
    private readonly SurfaceWindow _trigger;
    private readonly VOS.Platform.Desktop.AppBar _appBar;
    private bool _hovering, _hidden;
    private double _width = 400, _height = 140;

    private double IconSize => _host.Settings.Current.Dock.IconSize;
    private double SlabHeight => IconSize + Pad * 2 + 6;

    public DockSurface(ShellHost host) : base("V-OS Dock", topmost: true, noActivate: true)
    {
        _host = host;
        _slab = new GlassSurface(22);
        _layer.Children.Add(_slab);
        _layer.Children.Add(_items);
        _label.Child = _labelText;
        Root.Children.Add(_layer);
        Root.Children.Add(_label);
        _items.PointerMoved += OnPointerMoved;
        _items.PointerExited += (_, _) => EndHover();
        _items.Padding = new Thickness(Pad, Pad, Pad, Pad + 6);
        _settle = UiThread.Timer(TimeSpan.FromMilliseconds(260), () => { if (!_hovering) FitRegion(false); }, repeating: false);
        _hideDelay = UiThread.Timer(TimeSpan.FromMilliseconds(900), EvaluateHide, repeating: false);

        _trigger = new SurfaceWindow("V-OS Dock Trigger", topmost: true, noActivate: true);
        _trigger.Root.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _appBar = new VOS.Platform.Desktop.AppBar(Hwnd);
        _appBar.Positioned += PlaceAbove;
        _trigger.Root.PointerEntered += (_, _) => Reveal();

        host.Tracker.Changed += () => UiThread.Post(RefreshRunning);
        VisualQuality.Changed += () => UiThread.Post(() => EndHover());
        Rebuild();
        Reposition();
    }

    public void Reposition()
    {
        var mon = Displays.Primary;
        double s = mon.Scale;
        _height = SlabHeight + Head + 34;
        int w = (int)Math.Ceiling(_width * s), h = (int)Math.Ceiling(_height * s);
        var d = _host.Settings.Current.Dock;
        if (d.ReserveSpace && !d.AutoHide)
        {
            // Reserve the slab (not the magnification headroom): maximised windows end above the dock, never under it.
            _appBar.Register(Win32.ABE_BOTTOM, (int)Math.Ceiling((SlabHeight + 16) * s), mon.Bounds);
            // A reserved dock is always visible; the trigger strip would only steal the edge from the auto-hidden taskbar.
            _trigger.Hide();
            if (_hidden) Reveal();
            return;
        }
        _appBar.Unregister();
        int bottom = mon.WorkArea.Bottom;
        if (_host.Settings.Current.Shell.Mode == ShellMode.Immersive) bottom = mon.Bounds.Bottom;
        MoveAndResize(new RectInt32(mon.Bounds.Left + (mon.Bounds.Width - w) / 2, bottom - h - (int)(8 * s), w, h));
        int tw = (int)(mon.Bounds.Width * 0.4);
        _trigger.MoveAndResize(new RectInt32(mon.Bounds.Left + (mon.Bounds.Width - tw) / 2, bottom - 3, tw, 3));
        FitRegion(false);
    }

    private void PlaceAbove(Win32.RECT reserved)
    {
        var mon = Displays.Primary;
        double s = mon.Scale;
        int w = (int)Math.Ceiling(_width * s), h = (int)Math.Ceiling(_height * s);
        MoveAndResize(new RectInt32(mon.Bounds.Left + (mon.Bounds.Width - w) / 2, reserved.Bottom - h - (int)(6 * s), w, h));
        FitRegion(_hovering);
    }

    // ---------------- model ----------------

    public void Rebuild()
    {
        var settings = _host.Settings.Current;
        var pins = settings.ActiveWorkspace?.Dock ?? settings.Dock.Pinned ?? [];
        _entries.Clear();
        foreach (var p in pins)
        {
            var e = new Entry { Kind = p.Kind, Key = p.Key, Name = p.Name, Pin = p, AppId = p.AppUserModelId, Path = p.ExePath };
            if (p.Kind == "vos") { e.Vos = VosApp.Find(p.Target); if (e.Vos == null) continue; e.Name = e.Vos.Name; e.AppId = e.Vos.AppUserModelId; }
            else if (p.Kind == "app") { e.Installed = AppCatalog.FindByAppId(p.Target); e.Path ??= e.Installed?.ExePath; }
            else if (p.Kind == "path") e.Path = p.Target;
            _entries.Add(e);
        }
        _entries.Add(new Entry { Kind = "library", Key = "library", Name = "All apps" });
        if (settings.Dock.ShowTrash) _entries.Add(new Entry { Kind = "trash", Key = "trash", Name = "Recycle Bin" });
        RefreshRunning(rebuildElements: true);
    }

    private void RefreshRunning() => RefreshRunning(false);

    private void RefreshRunning(bool rebuildElements)
    {
        var windows = _host.Tracker.Windows;
        foreach (var e in _entries) e.Windows.Clear();
        var unmatched = new List<AppWindowInfo>();
        foreach (var w in windows)
        {
            var match = _entries.FirstOrDefault(e => e.Kind is "vos" or "app" or "path" && Matches(e, w));
            if (match != null) match.Windows.Add(w); else unmatched.Add(w);
        }
        // Running apps that are not pinned, grouped by app identity.
        var running = _entries.Where(e => e.Kind == "running").ToList();
        var groups = unmatched.GroupBy(w => w.GroupKey).ToList();
        bool changed = rebuildElements;
        foreach (var r in running.Where(r => groups.All(g => g.Key != r.Key)).ToList()) { _entries.Remove(r); changed = true; }
        int insertAt = _entries.FindIndex(e => e.Kind == "library");
        foreach (var g in groups)
        {
            var existing = _entries.FirstOrDefault(e => e.Kind == "running" && e.Key == g.Key);
            if (existing == null)
            {
                var first = g.First();
                var vos = first.AppId?.StartsWith("VOS.App.", StringComparison.Ordinal) == true ? VosApp.Find(first.AppId[8..]) : null;
                existing = new Entry { Kind = "running", Key = g.Key, Name = vos?.Name ?? first.ProcessName, Vos = vos, Path = first.ExePath, AppId = first.AppId };
                _entries.Insert(insertAt++, existing);
                changed = true;
            }
            existing.Windows.AddRange(g);
        }
        if (changed) BuildElements();
        else foreach (var e in _entries) UpdateDots(e);
        EvaluateHide();
    }

    private static bool Matches(Entry e, AppWindowInfo w)
    {
        if (e.AppId != null && w.AppId != null && string.Equals(e.AppId, w.AppId, StringComparison.OrdinalIgnoreCase)) return true;
        if (e.Installed is { IsPackaged: true } pkg && w.AppId != null && string.Equals(pkg.ParsingName, w.AppId, StringComparison.OrdinalIgnoreCase)) return true;
        if (e.Vos != null) return false;
        return e.Path != null && w.ExePath != null && string.Equals(e.Path, w.ExePath, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------- view ----------------

    private void BuildElements()
    {
        _items.Children.Clear();
        double size = IconSize;
        bool separatorAdded = false;
        foreach (var e in _entries)
        {
            if (!separatorAdded && e.Kind is "running" or "library")
            {
                if (_items.Children.Count > 0)
                    _items.Children.Add(new Border { Width = 1, Height = size * 0.7, Margin = new Thickness(2, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center, Background = Ui.B("Vos.Stroke") });
                separatorAdded = true;
            }
            var cell = new Grid { Width = size, Height = size, Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(1, 0, 0, 0)), Tag = e };
            cell.Children.Add(IconFor(e, size));
            var dots = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, -9) };
            cell.Children.Add(dots);
            e.Element = cell;
            e.Dots = dots;
            UpdateDots(e);
            Buttons.AutomationName(cell, e.Name);
            cell.PointerReleased += OnItemReleased;
            cell.AllowDrop = e.Kind is "vos" or "app" or "path" or "running" or "trash";
            cell.DragOver += OnDragOver;
            cell.Drop += OnDrop;
            _items.Children.Add(cell);
        }
        _items.Measure(new global::Windows.Foundation.Size(4000, 400));
        double itemsWidth = _items.DesiredSize.Width;
        _slab.Width = itemsWidth;
        _slab.Height = SlabHeight;
        _slab.VerticalAlignment = VerticalAlignment.Bottom;
        _width = itemsWidth + Overflow * 2;
        _layer.Margin = new Thickness(0, 0, 0, 0);
        Motion.EnableImplicitOffset(_items);
        Reposition();
    }

    private FrameworkElement IconFor(Entry e, double size)
    {
        if (e.Vos != null) return new AppIconView(e.Vos, size);
        if (e.Kind == "library") return Tile(Glyphs.Apps, size, "#3A3F4B", "#22252D");
        if (e.Kind == "trash")
        {
            var (_, items) = ShellOps.RecycleBinInfo();
            return Tile(items > 0 ? "" : "", size, "#4B505C", "#2B2E36", items > 0 ? 1 : 0.75);
        }
        var img = new Image { Width = size, Height = size, Stretch = Stretch.Uniform };
        var source = e.Installed?.ShellPath ?? e.Path;
        if (source != null)
        {
            Images.LoadIcon(img, source, Math.Max(32, (int)Math.Round(size * Scale)));
        }
        return img;
    }

    private static Grid Tile(string glyph, double size, string from, string to, double opacity = 1)
    {
        var g = new Grid { Width = size, Height = size, Opacity = opacity };
        g.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(size * 0.27),
            Background = new LinearGradientBrush
            {
                StartPoint = new global::Windows.Foundation.Point(0, 0), EndPoint = new global::Windows.Foundation.Point(0, 1),
                GradientStops = { new GradientStop { Color = Colors2.Hex(from), Offset = 0 }, new GradientStop { Color = Colors2.Hex(to), Offset = 1 } },
            },
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(global::Windows.UI.Color.FromArgb(0x30, 255, 255, 255)),
        });
        g.Children.Add(new FontIcon { Glyph = glyph, FontFamily = Tokens.Icons, FontSize = size * 0.42, Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(0xF0, 255, 255, 255)) });
        return g;
    }

    private void UpdateDots(Entry e)
    {
        if (e.Dots == null) return;
        e.Dots.Children.Clear();
        if (!_host.Settings.Current.Dock.ShowRecent && e.Kind == "running") return;
        int count = Math.Min(3, e.Windows.Count);
        bool focused = e.Windows.Any(w => w.Hwnd == _host.Tracker.Foreground);
        for (int i = 0; i < count; i++)
            e.Dots.Children.Add(new Border { Width = focused && i == 0 ? 10 : 4, Height = 4, CornerRadius = new CornerRadius(2), Background = Ui.B(focused ? "Vos.Accent" : "Vos.TextSecondary") });
    }

    public void OnForegroundChanged()
    {
        foreach (var e in _entries) UpdateDots(e);
        _hideDelay.Stop();
        _hideDelay.Start();
    }

    // ---------------- magnification ----------------

    private void OnPointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (!_hovering) { _hovering = true; FitRegion(true); }
        double x = args.GetCurrentPoint(_items).Position.X;
        var cells = _items.Children.OfType<Grid>().Where(c => c.Tag is Entry).ToList();
        bool magnify = _host.Settings.Current.Dock.Magnification && VisualQuality.Current.DockMagnification;
        double max = _host.Settings.Current.Dock.MagnificationScale, size = IconSize, sigma = size * 1.5;
        var scales = new double[cells.Count];
        Entry? hovered = null;
        for (int i = 0; i < cells.Count; i++)
        {
            var t = cells[i].TransformToVisual(_items).TransformPoint(default);
            double center = t.X + size / 2, d = x - center;
            if (Math.Abs(d) <= size / 2 + Gap / 2) hovered = (Entry)cells[i].Tag;
            scales[i] = magnify ? 1 + (max - 1) * Math.Exp(-(d * d) / (2 * sigma * sigma)) : (Math.Abs(d) <= size / 2 + Gap / 2 ? 1.08 : 1);
        }
        // Spread neighbours so magnified icons never overlap; the group stays centred.
        double total = scales.Sum(s => (s - 1) * size), acc = 0;
        for (int i = 0; i < cells.Count; i++)
        {
            double extra = (scales[i] - 1) * size;
            double tx = magnify ? acc + extra / 2 - total / 2 : 0;
            acc += extra;
            Motion.ScaleTo(cells[i], (float)scales[i], new Vector3((float)size / 2, (float)size, 0), 0.8f, 38);
            Motion.TranslateTo(cells[i], new Vector3((float)tx, magnify ? 0 : (float)(scales[i] > 1 ? -4 : 0), 0), 0.82f, 40);
        }
        ShowLabel(hovered, cells, scales);
    }

    private void ShowLabel(Entry? e, List<Grid> cells, double[] scales)
    {
        if (e == null) { _label.Visibility = Visibility.Collapsed; return; }
        int i = cells.FindIndex(c => c.Tag == e);
        _labelText.Text = e.Windows.Count > 0 && e.Kind is not "library" and not "trash" && e.Windows.Count == 1 && e.Windows[0].Title.Length > 0 && e.Kind == "running" ? e.Windows[0].Title : e.Name;
        _label.Visibility = Visibility.Visible;
        _label.Measure(new global::Windows.Foundation.Size(600, 60));
        var p = cells[i].TransformToVisual(Root).TransformPoint(default);
        double scaledTop = p.Y + IconSize - IconSize * scales[i];
        _label.Margin = new Thickness(Math.Max(0, p.X + IconSize / 2 - _label.DesiredSize.Width / 2), Math.Max(0, scaledTop - _label.DesiredSize.Height - LabelGap), 0, 0);
    }

    private void EndHover()
    {
        _hovering = false;
        foreach (var c in _items.Children.OfType<Grid>().Where(c => c.Tag is Entry))
        {
            Motion.ScaleTo(c, 1f, new Vector3((float)IconSize / 2, (float)IconSize, 0), 0.85f, 45);
            Motion.TranslateTo(c, Vector3.Zero, 0.85f, 45);
        }
        _label.Visibility = Visibility.Collapsed;
        _settle.Stop();
        _settle.Start();
        _hideDelay.Stop();
        _hideDelay.Start();
    }

    private void FitRegion(bool hovering)
    {
        double slabW = _slab.Width > 0 ? _slab.Width : 300;
        double x = (_width - slabW) / 2, y = _height - SlabHeight;
        if (hovering) SetHitRegion((x - Overflow * 0.8, 0, slabW + Overflow * 1.6, _height, 12));
        else SetHitRegion((x, y, slabW, SlabHeight, 22));
    }

    // ---------------- actions ----------------

    private void OnItemReleased(object sender, PointerRoutedEventArgs args)
    {
        if (sender is not Grid { Tag: Entry e } cell) return;
        var props = args.GetCurrentPoint(cell).Properties;
        if (props.PointerUpdateKind == PointerUpdateKind.RightButtonReleased) { ShowMenu(e, cell); return; }
        if (props.PointerUpdateKind != PointerUpdateKind.LeftButtonReleased) return;
        Activate(e, cell);
    }

    private void Activate(Entry e, FrameworkElement cell)
    {
        switch (e.Kind)
        {
            case "library": _host.Panels.Show(PanelKind.Command); return;
            case "trash": _host.Commands.OpenApp("files", "path=shell:RecycleBinFolder"); return;
        }
        if (e.Windows.Count > 0)
        {
            var fg = _host.Tracker.Foreground;
            var ordered = e.Windows.OrderByDescending(w => w.LastActivated).ToList();
            if (ordered.Count == 1 && ordered[0].Hwnd == fg) { WindowTracker.Minimize(fg); return; }
            if (ordered.Any(w => w.Hwnd == fg))
            {
                e.CycleIndex = (e.CycleIndex + 1) % ordered.Count;
                WindowTracker.Activate(ordered[e.CycleIndex].Hwnd);
            }
            else WindowTracker.Activate(ordered[0].Hwnd);
            return;
        }
        Bounce(cell);
        if (e.Vos != null) _host.Commands.OpenApp(e.Vos.Id);
        else if (e.Installed != null) ShellOps.LaunchApp(e.Installed);
        else if (e.Path != null) ShellOps.Launch(e.Path, null, System.IO.Path.GetDirectoryName(e.Path));
    }

    private void Bounce(FrameworkElement cell)
    {
        if (!Motion.Enabled) return;
        Motion.TranslateTo(cell, new Vector3(0, -14, 0), 0.5f, 60);
        var t = UiThread.Timer(TimeSpan.FromMilliseconds(260), () => Motion.TranslateTo(cell, Vector3.Zero, 0.45f, 60), repeating: false);
        t.Start();
    }

    private void ShowMenu(Entry e, FrameworkElement cell)
    {
        var menu = new MenuFlyout { ShouldConstrainToRootBounds = false, Placement = FlyoutPlacementMode.Top };
        void Item(string text, string glyph, Action a) { var mi = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph, FontFamily = Tokens.Icons } }; mi.Click += (_, _) => a(); menu.Items.Add(mi); }
        foreach (var w in e.Windows.Take(8))
            Item(w.Title.Length > 0 ? (w.Title.Length > 48 ? w.Title[..48] + "…" : w.Title) : e.Name, Glyphs.Window, () => WindowTracker.Activate(w.Hwnd));
        if (e.Windows.Count > 0) menu.Items.Add(new MenuFlyoutSeparator());
        if (e.Kind == "trash")
        {
            Item("Open Recycle Bin", Glyphs.FolderOpen, () => _host.Commands.OpenApp("files", "path=shell:RecycleBinFolder"));
            Item("Empty Recycle Bin…", Glyphs.Delete, () => _ = _host.Commands.RunAsync("system:emptybin", _host.Panels.AnyXamlRoot ?? Root.XamlRoot));
        }
        else if (e.Kind != "library")
        {
            if (e.Pin != null) Item("Unpin from Dock", Glyphs.Unpin, () => Unpin(e));
            else Item("Pin to Dock", Glyphs.Pin, () => Pin(e));
            if (e.Path != null && File.Exists(e.Path)) Item("Show in Files", Glyphs.Folder, () => _host.Commands.OpenApp("files", "select=" + e.Path));
            if (e.Windows.Count > 0) Item(e.Windows.Count > 1 ? "Close all windows" : "Close window", Glyphs.Close, () => { foreach (var w in e.Windows) WindowTracker.Close(w.Hwnd); });
        }
        if (menu.Items.Count == 0) return;
        menu.ShowAt(cell);
    }

    private void Pin(Entry e)
    {
        DockItem item = e.Vos != null ? DockItem.Vos(e.Vos.Id, e.Vos.Name)
            : AppCatalog.Get().FirstOrDefault(a => (a.ExePath != null && string.Equals(a.ExePath, e.Path, StringComparison.OrdinalIgnoreCase)) || (e.AppId != null && a.ParsingName == e.AppId)) is { } app
                ? new DockItem { Kind = "app", Target = app.ParsingName, Name = app.Name, ExePath = app.ExePath, AppUserModelId = app.IsPackaged ? app.ParsingName : e.AppId }
                : new DockItem { Kind = "path", Target = e.Path ?? "", Name = e.Name, ExePath = e.Path, AppUserModelId = e.AppId };
        if (string.IsNullOrEmpty(item.Target)) return;
        _host.Settings.Update(s => { s.Dock.Pinned ??= []; if (s.Dock.Pinned.All(p => p.Key != item.Key)) s.Dock.Pinned.Add(item); });
    }

    private void Unpin(Entry e) => _host.Settings.Update(s => s.Dock.Pinned?.RemoveAll(p => p.Key == e.Key));

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = sender is Grid { Tag: Entry { Kind: "trash" } } ? DataPackageOperation.Move : DataPackageOperation.Copy;
            e.DragUIOverride.Caption = sender is Grid { Tag: Entry en } ? (en.Kind == "trash" ? "Move to Recycle Bin" : "Open with " + en.Name) : "";
        }
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (sender is not Grid { Tag: Entry entry } || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        var paths = items.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)).ToArray();
        if (paths.Length == 0) return;
        if (entry.Kind == "trash") { FileOps.Recycle(paths); return; }
        if (entry.Vos != null) { _host.Commands.OpenApp(entry.Vos.Id, CommandRunner.JoinArgs(paths.Select(p => "open=" + p).ToArray())); return; }
        var exe = entry.Path ?? entry.Installed?.ExePath;
        if (exe != null) ShellOps.Launch(exe, string.Join(' ', paths.Select(p => $"\"{p}\"")), System.IO.Path.GetDirectoryName(exe));
    }

    // ---------------- intellihide ----------------

    private void EvaluateHide()
    {
        var settings = _host.Settings.Current.Dock;
        if (_hovering) return;
        bool reserved = settings.ReserveSpace && !settings.AutoHide;
        bool hide = settings.AutoHide || (!reserved && OverlapsForeground());
        if (hide == _hidden) return;
        _hidden = hide;
        if (hide)
        {
            Motion.TranslateTo(_layer, new Vector3(0, (float)(SlabHeight + 24), 0), 0.9f, 60);
            var t = UiThread.Timer(TimeSpan.FromMilliseconds(Motion.Enabled ? 280 : 1), () => { if (_hidden) { Hide(); _trigger.Show(); } }, repeating: false);
            t.Start();
        }
        else Reveal();
    }

    private void Reveal()
    {
        _hidden = false;
        _trigger.Hide();
        Show();
        Motion.TranslateTo(_layer, Vector3.Zero, 0.78f, 50);
        _hideDelay.Stop();
        _hideDelay.Interval = TimeSpan.FromSeconds(2.5);
        _hideDelay.Start();
    }

    private bool OverlapsForeground()
    {
        var fg = _host.Tracker.Foreground;
        if (fg == IntPtr.Zero || !WindowTracker.IsAppWindow(fg) || Win32.IsIconic(fg)) return false;
        if (Win32.DwmGetWindowAttribute(fg, Win32.DWMWA_EXTENDED_FRAME_BOUNDS, out Win32.RECT r, Marshal.SizeOf<Win32.RECT>()) != 0) Win32.GetWindowRect(fg, out r);
        double s = Scale;
        var b = Bounds;
        int slabW = (int)(_slab.Width * s), slabH = (int)(SlabHeight * s);
        int left = b.X + (b.Width - slabW) / 2, top = b.Y + b.Height - slabH;
        return r.Right > left && r.Left < left + slabW && r.Bottom > top && r.Top < top + slabH;
    }
}

/// <summary>Safe file operations through the Windows shell (Recycle Bin with undo).</summary>
public static class FileOps
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT { public IntPtr hwnd; public uint wFunc; public string pFrom; public string? pTo; public ushort fFlags; public int fAnyOperationsAborted; public IntPtr hNameMappings; public string? lpszProgressTitle; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHFileOperationW(ref SHFILEOPSTRUCT op);

    /// <summary>Moves items to the Recycle Bin (recoverable). Windows shows its own confirmation for permanent cases.</summary>
    public static bool Recycle(IEnumerable<string> paths)
    {
        var op = new SHFILEOPSTRUCT { wFunc = 3 /*FO_DELETE*/, pFrom = string.Join('\0', paths) + "\0\0", fFlags = 0x40 /*FOF_ALLOWUNDO*/ | 0x0400 /*FOF_NOERRORUI*/ };
        return SHFileOperationW(ref op) == 0 && op.fAnyOperationsAborted == 0;
    }
}
