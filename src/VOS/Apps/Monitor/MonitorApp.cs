using System.Diagnostics;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Win32;
using VOS.Core;
using VOS.Platform.Shell;
using VOS.Platform.Sys;
using VOS.UI;
using VOS.UI.Theme;

namespace VOS.Apps.Monitor;

/// <summary>
/// V-OS Monitor: live system load, processes (with safe ending), storage and startup apps. Sampling runs only
/// while the window is open and not minimised: once a second for the overview, every two seconds for processes.
/// </summary>
public sealed class MonitorApp : IAppModule
{
    public AppFrame Frame { get; }
    private readonly NavShell _nav = new();
    private readonly MetricsSampler _metrics = new(includeGpuAndDisk: true);
    private readonly ProcessSampler _processes = new();
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _timer;
    private int _tick;
    private bool _sampling;
    private Action<SystemSample>? _onSample;
    private Action<IReadOnlyList<ProcessInfo>>? _onProcesses;

    public MonitorApp()
    {
        Frame = new AppFrame(VosApp.Find("monitor")!, "Monitor", 760, 520, 1100, 760);
        _nav.Add(new NavShell.Item("overview", "Overview", Glyphs.Gauge));
        _nav.Add(new NavShell.Item("processes", "Processes", Glyphs.List));
        _nav.Add(new NavShell.Item("storage", "Storage", Glyphs.Disk));
        _nav.Add(new NavShell.Item("startup", "Startup apps", Glyphs.Power));
        _nav.CachePages = true;
        _nav.PageFactory = id => id switch
        {
            "processes" => ProcessesPage(),
            "storage" => Kit.Scroll(StoragePage()),
            "startup" => Kit.Scroll(StartupPage()),
            _ => Kit.Scroll(OverviewPage()),
        };
        _nav.Navigated += id => { if (id is "storage" or "startup") _nav.Invalidate(id); };
        Frame.Body.Content = _nav;
        _timer = UiThread.Timer(TimeSpan.FromSeconds(1), Tick, repeating: true);
        _timer.Start();
        Frame.Closed += () => { _timer.Stop(); _metrics.Dispose(); };
        _nav.Select("overview");
    }

    public void HandleArgs(AppArgs args) { if (args.Get("section") is { } s) _nav.Select(s); }

    private bool Minimized => Frame.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized };

    private void Tick()
    {
        if (_sampling || Minimized) return;
        _sampling = true;
        bool wantProcesses = _nav.Current == "processes" && _tick++ % 2 == 0;
        Task.Run(() =>
        {
            var s = _metrics.Sample();
            var procs = wantProcesses ? _processes.Sample() : null;
            UiThread.Post(() =>
            {
                _sampling = false;
                _onSample?.Invoke(s);
                if (procs != null) _onProcesses?.Invoke(procs);
            });
        });
    }

    // ------------------------------------------------------------------ overview

    private sealed class Gauge
    {
        public readonly TextBlock Value = new() { FontSize = 30, FontFamily = Tokens.Instrument, FontWeight = FontWeights.SemiBold, Foreground = Ui.B("Vos.TextPrimary") };
        public readonly TextBlock Detail = new() { FontSize = 12.5, Foreground = Ui.B("Vos.TextSecondary"), TextTrimming = TextTrimming.CharacterEllipsis };
        public readonly Sparkline Chart;
        public readonly Border Card;

        public Gauge(string glyph, string title, string brush, bool autoScale = false)
        {
            Chart = new Sparkline(60, brush) { Height = 64, AutoScale = autoScale, Margin = new Thickness(0, 8, 0, 0) };
            var head = Kit.Row(8, Ui.Icon(glyph, 14, "Vos.TextSecondary"), Ui.Text(title, 13, "Vos.TextSecondary", Tokens.Text, FontWeights.SemiBold));
            Card = Kit.Card(Kit.Stack(4, head, Value, Detail, Chart), new Thickness(18, 16, 18, 14));
        }
    }

    private StackPanel OverviewPage()
    {
        var page = Kit.Stack(4, Kit.PageHeader("Overview", "Live load, updated every second while this window is open."));
        var cpu = new Gauge(Glyphs.Cpu, "Processor", "Vos.Accent");
        var mem = new Gauge(Glyphs.Memory, "Memory", "Vos.Success");
        var gpu = new Gauge(Glyphs.Monitor, "Graphics", "Vos.Warning");
        var disk = new Gauge(Glyphs.Disk, "Disk", "Vos.AccentText");
        var net = new Gauge(Glyphs.Network, "Network", "Vos.Critical", autoScale: true);
        var grid = new Grid { ColumnSpacing = 14, RowSpacing = 14 };
        grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition());
        var cards = new[] { cpu.Card, mem.Card, gpu.Card, disk.Card, net.Card };
        for (int i = 0; i < cards.Length; i++)
        {
            if (i / 2 >= grid.RowDefinitions.Count) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetColumn(cards[i], i % 2); Grid.SetRow(cards[i], i / 2);
            grid.Children.Add(cards[i]);
        }
        page.Children.Add(grid);

        page.Children.Add(Kit.Section("System"));
        var uptime = Ui.Text("", 14);
        var procCount = Ui.Text("", 14);
        page.Children.Add(Kit.Group(
            Kit.Setting("Up time", null, uptime, Glyphs.Clock),
            Kit.Setting("Processes", null, procCount, Glyphs.List)));

        _onSample = s =>
        {
            cpu.Value.Text = $"{s.CpuPercent:0}%"; cpu.Detail.Text = $"{Environment.ProcessorCount} logical processors"; cpu.Chart.Push(s.CpuPercent);
            mem.Value.Text = $"{s.MemoryPercent:0}%"; mem.Detail.Text = $"{s.MemoryUsedGb:0.0} of {s.MemoryTotalGb:0.0} GB in use"; mem.Chart.Push(s.MemoryPercent);
            gpu.Value.Text = $"{s.GpuPercent:0}%"; gpu.Detail.Text = "3D engine, all adapters"; gpu.Chart.Push(s.GpuPercent);
            disk.Value.Text = $"{s.DiskActivePercent:0}%"; disk.Detail.Text = $"Read {Fmt.BytesPerSecond(s.DiskReadBps)} · write {Fmt.BytesPerSecond(s.DiskWriteBps)}"; disk.Chart.Push(s.DiskActivePercent);
            net.Value.Text = Fmt.BytesPerSecond(s.NetDownBps + s.NetUpBps); net.Detail.Text = $"Down {Fmt.BytesPerSecond(s.NetDownBps)} · up {Fmt.BytesPerSecond(s.NetUpBps)}"; net.Chart.Push(s.NetDownBps + s.NetUpBps);
            uptime.Text = Fmt.DurationWords(TimeSpan.FromMilliseconds(Environment.TickCount64));
            if (_tick % 5 == 0) procCount.Text = Process.GetProcesses().Length.ToString(System.Globalization.CultureInfo.CurrentCulture);
        };
        return page;
    }

    // ------------------------------------------------------------------ processes

    private sealed class Row
    {
        public required Grid Root;
        public required Image Icon;
        public required TextBlock Name, Pid, Cpu, Memory;
    }

    private string _sort = "cpu";
    private string _filter = "";
    private ProcessInfo? _selected;
    private IReadOnlyList<ProcessInfo> _last = [];

    private Grid ProcessesPage()
    {
        VirtualList<ProcessInfo> list = null!;
        TextBlock summary = null!;
        Button endTask = null!;
        var root = new Grid { Padding = new Thickness(28, 22, 28, 16), RowSpacing = 10 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new Grid();
        header.Children.Add(Ui.Text("Processes", 28, "Vos.TextPrimary", Tokens.Display, FontWeights.SemiBold));
        var search = Kit.SearchBox("Search processes", q => { _filter = q.Trim(); Apply(); }, 260);
        search.HorizontalAlignment = HorizontalAlignment.Right;
        search.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(search);
        root.Children.Add(header);

        var columns = Columns();
        void SortBy(string key) { _sort = key; Apply(); }
        columns.Children.Add(HeaderButton("Name", 1, () => SortBy("name")));
        columns.Children.Add(HeaderButton("PID", 2, () => SortBy("pid")));
        columns.Children.Add(HeaderButton("CPU", 3, () => SortBy("cpu")));
        columns.Children.Add(HeaderButton("Memory", 4, () => SortBy("memory")));
        Grid.SetRow(columns, 1);
        root.Children.Add(columns);

        list = new VirtualList<ProcessInfo>(CreateRow, BindRow);
        var card = Kit.Card(list, new Thickness(4));
        Grid.SetRow(card, 2);
        root.Children.Add(card);

        var footer = new Grid();
        summary = Ui.Text("Collecting…", 12.5, "Vos.TextSecondary");
        summary.VerticalAlignment = VerticalAlignment.Center;
        footer.Children.Add(summary);
        endTask = Buttons.Filled(Kit.Row(8, Ui.Icon(Glyphs.Close, 12), Ui.Text("End task", 13.5)), EndSelected);
        endTask.HorizontalAlignment = HorizontalAlignment.Right;
        endTask.IsEnabled = false;
        footer.Children.Add(endTask);
        Grid.SetRow(footer, 3);
        root.Children.Add(footer);

        void Apply()
        {
            IEnumerable<ProcessInfo> q = _last.Where(p => p.Id != 0);
            if (_filter.Length > 0) q = q.Where(p => p.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase) || p.Id.ToString().Contains(_filter, StringComparison.Ordinal));
            q = _sort switch
            {
                "name" => q.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase),
                "pid" => q.OrderBy(p => p.Id),
                "memory" => q.OrderByDescending(p => p.PrivateBytes),
                _ => q.OrderByDescending(p => p.CpuPercent).ThenByDescending(p => p.PrivateBytes),
            };
            list.SetItems(q.ToList());
            double cpu = _last.Where(p => p.Id != 0).Sum(p => p.CpuPercent);
            summary.Text = $"{_last.Count - 1} processes · {cpu:0}% CPU · {Fmt.Bytes(_last.Sum(p => p.PrivateBytes))} committed";
            if (_selected != null) _selected = _last.FirstOrDefault(p => p.Id == _selected.Id && p.Started == _selected.Started);
            endTask.IsEnabled = _selected != null && !ProcessSampler.IsCritical(_selected);
        }

        async void EndSelected()
        {
            if (_selected is not { } p) return;
            if (!await Dialogs.ConfirmAsync(Frame.Root.XamlRoot, $"End {p.Name}?", "Unsaved work in this app will be lost.", "End task", destructive: true)) return;
            var error = ProcessSampler.End(p);
            if (error != null) await Dialogs.InfoAsync(Frame.Root.XamlRoot, "Could not end the task", error);
            _selected = null;
            endTask.IsEnabled = false;
        }

        FrameworkElement CreateRow()
        {
            var g = Columns();
            g.Padding = new Thickness(10, 6, 10, 6);
            g.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
            var icon = new Image { Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Center };
            g.Children.Add(icon);
            var name = Ui.Text("", 13.5); name.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(name, 1); g.Children.Add(name);
            var pid = Ui.Text("", 13, "Vos.TextSecondary"); pid.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(pid, 2); g.Children.Add(pid);
            var cpu = Ui.Text("", 13, "Vos.TextPrimary", Tokens.Instrument); cpu.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(cpu, 3); g.Children.Add(cpu);
            var mem = Ui.Text("", 13, "Vos.TextPrimary", Tokens.Instrument); mem.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(mem, 4); g.Children.Add(mem);
            g.Tag = new Row { Root = g, Icon = icon, Name = name, Pid = pid, Cpu = cpu, Memory = mem };
            g.CornerRadius = new CornerRadius(Tokens.RadiusSmall);
            g.PointerEntered += (_, _) => { if (g.DataContext != _selected) g.Background = Ui.B("Vos.ControlHover"); };
            g.PointerExited += (_, _) => { if (g.DataContext != _selected) g.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent); };
            g.Tapped += (_, _) =>
            {
                _selected = g.DataContext as ProcessInfo;
                endTask.IsEnabled = _selected != null && !ProcessSampler.IsCritical(_selected);
                list.RefreshVisible();
            };
            return g;
        }

        void BindRow(FrameworkElement e, ProcessInfo p, int index)
        {
            var r = (Row)e.Tag;
            e.DataContext = p;
            r.Name.Text = p.Name;
            r.Pid.Text = p.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            r.Cpu.Text = p.CpuPercent >= 0.05 ? $"{p.CpuPercent:0.0}%" : "0%";
            r.Memory.Text = Fmt.Bytes(p.PrivateBytes);
            r.Cpu.Foreground = Ui.B(p.CpuPercent >= 25 ? "Vos.Warning" : "Vos.TextPrimary");
            bool selected = _selected != null && _selected.Id == p.Id;
            r.Root.Background = selected ? Ui.B("Vos.AccentSubtle") : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
            Images.LoadIconFor(r.Icon, p.Path, 32);
        }

        _onProcesses = procs => { _last = procs; Apply(); };
        Task.Run(() => _processes.Sample()); // prime CPU deltas
        return root;
    }

    private static Grid Columns()
    {
        var g = new Grid { ColumnSpacing = 12, Padding = new Thickness(10, 0, 10, 0) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        return g;
    }

    private static Button HeaderButton(string text, int column, Action sort)
    {
        var b = Buttons.Subtle(Ui.Text(text, 12.5, "Vos.TextSecondary", Tokens.Text, FontWeights.SemiBold), sort, 6, new Thickness(0, 4, 8, 4));
        b.HorizontalAlignment = HorizontalAlignment.Left;
        Grid.SetColumn(b, column);
        if (column == 1) Grid.SetColumnSpan(b, 1);
        return b;
    }

    // ------------------------------------------------------------------ storage

    private StackPanel StoragePage()
    {
        var page = Kit.Stack(4, Kit.PageHeader("Storage", "Drives connected to this PC."));
        var rows = new List<UIElement>();
        foreach (var d in DriveInfo.GetDrives())
        {
            if (!d.IsReady) continue;
            double used = d.TotalSize > 0 ? 1 - (double)d.AvailableFreeSpace / d.TotalSize : 0;
            string label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? d.DriveType switch { DriveType.Removable => "Removable drive", DriveType.Network => "Network drive", DriveType.CDRom => "Disc", _ => "Local disk" } : d.VolumeLabel;
            var info = Kit.Stack(6,
                Kit.Row(8, Ui.Text($"{label} ({d.Name.TrimEnd('\\')})", 14), Ui.Text(d.DriveFormat, 12, "Vos.TextTertiary")),
                Kit.Meter(used, double.NaN, 6, used > 0.9 ? "Vos.Critical" : used > 0.75 ? "Vos.Warning" : "Vos.Accent"),
                Ui.Text($"{Fmt.Bytes(d.AvailableFreeSpace)} free of {Fmt.Bytes(d.TotalSize)}", 12, "Vos.TextSecondary"));
            var g = new Grid { Padding = new Thickness(16, 14, 16, 14), ColumnSpacing = 16 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(new FontIcon { Glyph = d.DriveType == DriveType.Removable ? Glyphs.Device : Glyphs.Disk, FontSize = 22, FontFamily = Tokens.Icons, VerticalAlignment = VerticalAlignment.Center });
            Grid.SetColumn(info, 1); g.Children.Add(info);
            var root = d.RootDirectory.FullName;
            var open = Buttons.Filled("Open", () => AppHost.Open("files", ["path=" + root]));
            open.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(open, 2); g.Children.Add(open);
            rows.Add(g);
        }
        page.Children.Add(Kit.Group([.. rows]));
        var cleanup = Buttons.Filled("Free up space with Storage Sense", () => ShellOps.OpenWindowsSettings("storagesense"));
        cleanup.Margin = new Thickness(0, 10, 0, 0);
        page.Children.Add(cleanup);
        return page;
    }

    // ------------------------------------------------------------------ startup

    private sealed record StartupEntry(string Name, string Command, string Source, bool Enabled);

    private StackPanel StartupPage()
    {
        var page = Kit.Stack(4, Kit.PageHeader("Startup apps", "Apps that start when you sign in. Turning them on or off is done in Windows Settings, which keeps Windows' own record."));
        var entries = ReadStartup();
        var rows = entries.Select(e =>
        {
            var exe = ExtractExe(e.Command);
            var icon = new Image { Width = 24, Height = 24 };
            if (exe != null) Images.LoadIcon(icon, exe, 48);
            var row = Kit.Setting(e.Name, $"{e.Source} · {e.Command}", Ui.Text(e.Enabled ? "On" : "Off", 13, e.Enabled ? "Vos.AccentText" : "Vos.TextTertiary"));
            row.Children.Insert(0, new Border { Child = icon, VerticalAlignment = VerticalAlignment.Center });
            return (UIElement)row;
        }).ToList();
        if (rows.Count == 0) rows.Add(Kit.Setting("No startup apps found", null));
        page.Children.Add(Kit.Group([.. rows]));
        var manage = Buttons.Accent("Manage in Windows Settings", () => ShellOps.OpenWindowsSettings("startupapps"));
        manage.Margin = new Thickness(0, 10, 0, 0);
        page.Children.Add(manage);
        return page;
    }

    private static List<StartupEntry> ReadStartup()
    {
        var list = new List<StartupEntry>();
        const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";
        void FromKey(RegistryKey hive, string path, string approvedPath, string source)
        {
            try
            {
                using var k = hive.OpenSubKey(path);
                using var a = hive.OpenSubKey(Approved + approvedPath);
                if (k == null) return;
                foreach (var name in k.GetValueNames())
                {
                    if (k.GetValue(name) is not string cmd || cmd.Length == 0) continue;
                    bool enabled = a?.GetValue(name) is not byte[] b || b.Length == 0 || (b[0] & 1) == 0;
                    list.Add(new StartupEntry(name, cmd, source, enabled));
                }
            }
            catch (Exception ex) { Log.Debug("Startup key unreadable: " + path, ex.Message); }
        }
        FromKey(Registry.CurrentUser, Run, "Run", "Your account");
        FromKey(Registry.LocalMachine, Run, "Run", "All users");
        FromKey(Registry.LocalMachine, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", "Run32", "All users (32-bit)");
        foreach (var (folder, source) in new[] { (Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Your Startup folder"), (Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), "Common Startup folder") })
        {
            try { foreach (var f in Directory.EnumerateFiles(folder).Where(f => !f.EndsWith("desktop.ini", StringComparison.OrdinalIgnoreCase))) list.Add(new StartupEntry(Path.GetFileNameWithoutExtension(f), f, source, true)); }
            catch { }
        }
        return list.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? ExtractExe(string command)
    {
        var c = Environment.ExpandEnvironmentVariables(command.Trim());
        if (c.StartsWith('"')) { int end = c.IndexOf('"', 1); return end > 1 ? c[1..end] : null; }
        int exe = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe > 0) return c[..(exe + 4)];
        return File.Exists(c) ? c : null;
    }
}
