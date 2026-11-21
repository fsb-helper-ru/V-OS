using System.Diagnostics;
using System.Globalization;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using VOS.Core;
using VOS.Core.Ipc;
using VOS.Core.Settings;
using VOS.Core.Timers;
using VOS.UI;
using VOS.UI.Theme;

namespace VOS.Apps.Time;

/// <summary>
/// V-OS Time: world clocks, alarms, timers, a stopwatch and a calendar. Alarms and timers are kept in shared files
/// that the shell watches, so they ring and count down even when this window is closed.
/// </summary>
public sealed class TimeApp : IAppModule
{
    public AppFrame Frame { get; }
    private readonly NavShell _nav = new();
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _second;
    private readonly TimerStore _timers = new();
    private readonly SharedJsonList<Alarm> _alarms = new("alarms.json");
    private readonly SharedJsonList<WorldClock> _clocks = new("clocks.json");
    private readonly CalendarStore _calendar = new();
    private Action? _onSecond;
    private static bool H24 => SettingsStore.Instance.Current.Shell.Use24Hour;

    public TimeApp()
    {
        Frame = new AppFrame(VosApp.Find("time")!, "Time", 760, 560, 1080, 760);
        _nav.Add(new NavShell.Item("clocks", "Clocks", Glyphs.Globe));
        _nav.Add(new NavShell.Item("alarms", "Alarms", Glyphs.Bell));
        _nav.Add(new NavShell.Item("timers", "Timers", Glyphs.Timer));
        _nav.Add(new NavShell.Item("stopwatch", "Stopwatch", Glyphs.Clock));
        _nav.Add(new NavShell.Item("calendar", "Calendar", Glyphs.Calendar));
        _nav.CachePages = false;
        _nav.PageFactory = id => { _onSecond = null; return id switch { "alarms" => Kit.Scroll(Alarms()), "timers" => Kit.Scroll(Timers()), "stopwatch" => Kit.Scroll(StopwatchPage()), "calendar" => Calendar(), _ => Kit.Scroll(Clocks()) }; };
        Frame.Body.Content = _nav;
        _second = UiThread.Timer(TimeSpan.FromSeconds(1), () => _onSecond?.Invoke(), repeating: true);
        _second.Start();
        _timers.Watch(); _timers.Changed += () => UiThread.Post(() => { if (_nav.Current == "timers") _nav.Select("timers"); });
        _alarms.Watch(); _alarms.Changed += () => UiThread.Post(() => { if (_nav.Current == "alarms" && !_editingAlarm) _nav.Select("alarms"); });
        Frame.Closed += () => { _second.Stop(); _stopwatchTimer?.Stop(); _timers.Dispose(); _alarms.Dispose(); _clocks.Dispose(); };
        _nav.Select("clocks");
    }

    public void HandleArgs(AppArgs args) { if (args.Get("section") is { } s) _nav.Select(s); }

    private static string Clock(DateTime t, bool seconds = false) => t.ToString(H24 ? (seconds ? "HH:mm:ss" : "HH:mm") : (seconds ? "h:mm:ss tt" : "h:mm tt"), CultureInfo.CurrentCulture);
    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpper(s[0], CultureInfo.CurrentCulture) + s[1..];

    // ------------------------------------------------------------------ clocks

    private static readonly (string city, string zone)[] s_cities =
    [
        ("London", "Europe/London"), ("Paris", "Europe/Paris"), ("Berlin", "Europe/Berlin"), ("Madrid", "Europe/Madrid"), ("Rome", "Europe/Rome"),
        ("Amsterdam", "Europe/Amsterdam"), ("Stockholm", "Europe/Stockholm"), ("Warsaw", "Europe/Warsaw"), ("Prague", "Europe/Prague"), ("Vienna", "Europe/Vienna"),
        ("Athens", "Europe/Athens"), ("Bucharest", "Europe/Bucharest"), ("Chisinau", "Europe/Chisinau"), ("Kyiv", "Europe/Kyiv"), ("Istanbul", "Europe/Istanbul"),
        ("Moscow", "Europe/Moscow"), ("Dubai", "Asia/Dubai"), ("Tbilisi", "Asia/Tbilisi"), ("Yerevan", "Asia/Yerevan"), ("Almaty", "Asia/Almaty"),
        ("Tashkent", "Asia/Tashkent"), ("Karachi", "Asia/Karachi"), ("Mumbai", "Asia/Kolkata"), ("Delhi", "Asia/Kolkata"), ("Bangkok", "Asia/Bangkok"),
        ("Jakarta", "Asia/Jakarta"), ("Singapore", "Asia/Singapore"), ("Hong Kong", "Asia/Hong_Kong"), ("Beijing", "Asia/Shanghai"), ("Shanghai", "Asia/Shanghai"),
        ("Seoul", "Asia/Seoul"), ("Tokyo", "Asia/Tokyo"), ("Sydney", "Australia/Sydney"), ("Melbourne", "Australia/Melbourne"), ("Auckland", "Pacific/Auckland"),
        ("Honolulu", "Pacific/Honolulu"), ("Anchorage", "America/Anchorage"), ("Los Angeles", "America/Los_Angeles"), ("San Francisco", "America/Los_Angeles"),
        ("Vancouver", "America/Vancouver"), ("Denver", "America/Denver"), ("Chicago", "America/Chicago"), ("Mexico City", "America/Mexico_City"),
        ("Toronto", "America/Toronto"), ("New York", "America/New_York"), ("Miami", "America/New_York"), ("Bogota", "America/Bogota"), ("Lima", "America/Lima"),
        ("Santiago", "America/Santiago"), ("Buenos Aires", "America/Argentina/Buenos_Aires"), ("Sao Paulo", "America/Sao_Paulo"), ("Reykjavik", "Atlantic/Reykjavik"),
        ("Lisbon", "Europe/Lisbon"), ("Cairo", "Africa/Cairo"), ("Lagos", "Africa/Lagos"), ("Nairobi", "Africa/Nairobi"), ("Johannesburg", "Africa/Johannesburg"),
        ("Tel Aviv", "Asia/Jerusalem"), ("Riyadh", "Asia/Riyadh"), ("Tehran", "Asia/Tehran"),
    ];

    private static TimeZoneInfo? Zone(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch { }
        try { return TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var w) ? TimeZoneInfo.FindSystemTimeZoneById(w) : null; } catch { return null; }
    }

    private StackPanel Clocks()
    {
        var page = Kit.Stack(4);
        var time = new TextBlock { FontSize = 64, FontFamily = Tokens.Instrument, FontWeight = FontWeights.SemiBold, Foreground = Ui.B("Vos.TextPrimary") };
        var date = Ui.Text("", 15, "Vos.TextSecondary");
        page.Children.Add(Kit.Card(Kit.Stack(2, time, date, Ui.Text(TimeZoneInfo.Local.DisplayName, 12, "Vos.TextTertiary")), new Thickness(28, 22, 28, 22)));

        page.Children.Add(Kit.Section("World clocks"));
        var rows = new StackPanel();
        var clocks = _clocks.List();
        var updates = new List<Action>();
        foreach (var c in clocks)
        {
            var zone = Zone(c.TimeZoneId);
            if (zone == null) continue;
            var value = new TextBlock { FontSize = 22, FontFamily = Tokens.Instrument, FontWeight = FontWeights.SemiBold, Foreground = Ui.B("Vos.TextPrimary") };
            var detail = Ui.Text("", 12, "Vos.TextSecondary");
            var name = c.Name;
            var remove = Buttons.Icon(Glyphs.Delete, () => { _clocks.Mutate(l => l.RemoveAll(x => x.Name == name)); _nav.Select("clocks"); }, 32, 13, "Remove " + name);
            var right = Kit.Row(14, value, remove);
            var row = Kit.Setting(c.Name, null, right, Glyphs.Globe);
            ((StackPanel)((Grid)row).Children.OfType<StackPanel>().First()).Children.Add(detail);
            updates.Add(() =>
            {
                var now = DateTime.Now;
                var there = TimeZoneInfo.ConvertTime(now, TimeZoneInfo.Local, zone);
                value.Text = Clock(there);
                var diff = zone.GetUtcOffset(DateTime.UtcNow) - TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow);
                string day = there.Date == now.Date ? "Today" : there.Date > now.Date ? "Tomorrow" : "Yesterday";
                detail.Text = $"{day} · {(diff == TimeSpan.Zero ? "same time" : (diff > TimeSpan.Zero ? "+" : "−") + Math.Abs(diff.TotalHours).ToString("0.#", CultureInfo.CurrentCulture) + " h")}";
            });
            if (rows.Children.Count > 0) rows.Children.Add(new Border { Height = 1, Background = Ui.B("Vos.StrokeSubtle") });
            rows.Children.Add(row);
        }
        if (rows.Children.Count == 0) rows.Children.Add(Kit.Setting("No world clocks yet", "Add a city below."));
        page.Children.Add(Kit.Card(rows, new Thickness(0)));

        var box = new AutoSuggestBox { PlaceholderText = "Add a city", Width = 320, QueryIcon = new SymbolIcon(Symbol.Add), Margin = new Thickness(0, 10, 0, 0) };
        box.TextChanged += (_, e) =>
        {
            if (e.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
            var q = box.Text.Trim();
            box.ItemsSource = q.Length == 0 ? null : s_cities.Where(c => c.city.Contains(q, StringComparison.OrdinalIgnoreCase)).Select(c => c.city).Take(8).ToList();
        };
        box.QuerySubmitted += (_, e) =>
        {
            var pick = (e.ChosenSuggestion as string) ?? s_cities.FirstOrDefault(c => c.city.Equals(box.Text.Trim(), StringComparison.OrdinalIgnoreCase)).city;
            var match = s_cities.FirstOrDefault(c => c.city == pick);
            if (match.city == null || Zone(match.zone) == null) return;
            _clocks.Mutate(l => { if (l.All(x => x.Name != match.city)) l.Add(new WorldClock { Name = match.city, TimeZoneId = match.zone }); });
            _nav.Select("clocks");
        };
        page.Children.Add(box);

        void Tick()
        {
            var now = DateTime.Now;
            time.Text = Clock(now, seconds: true);
            date.Text = Cap(now.ToString("D", CultureInfo.CurrentCulture));
            foreach (var u in updates) u();
        }
        Tick();
        _onSecond = Tick;
        return page;
    }

    // ------------------------------------------------------------------ alarms

    private bool _editingAlarm;

    private StackPanel Alarms()
    {
        var page = Kit.Stack(4, Kit.PageHeader("Alarms", "Alarms ring through V-OS while it is running, even when this window is closed."));
        if (!IpcClient.IsListening(IpcNames.Shell)) page.Children.Add(Kit.Info("V-OS shell is not running", "Alarms ring only while the V-OS shell is running.", InfoBarSeverity.Warning));
        var list = _alarms.List().OrderBy(a => a.Hour * 60 + a.Minute).ToList();
        var rows = new List<UIElement>();
        foreach (var a in list)
        {
            var id = a.Id;
            var time = new TextBlock { Text = Clock(DateTime.Today.AddHours(a.Hour).AddMinutes(a.Minute)), FontSize = 26, FontFamily = Tokens.Instrument, FontWeight = FontWeights.SemiBold, Foreground = Ui.B(a.Enabled ? "Vos.TextPrimary" : "Vos.TextTertiary"), MinWidth = 120 };
            var next = a.Next(DateTime.Now);
            var info = Kit.Stack(2, Ui.Text(a.Label, 14), Ui.Text(a.DaysText + (next is { } n ? " · rings " + Relative(n) : ""), 12, "Vos.TextSecondary"));
            var g = new Grid { Padding = new Thickness(18, 12, 16, 12), ColumnSpacing = 16 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(time);
            Grid.SetColumn(info, 1); info.VerticalAlignment = VerticalAlignment.Center; g.Children.Add(info);
            var controls = Kit.Row(8,
                Kit.Toggle(a.Enabled, on => _alarms.Mutate(l => { if (l.FirstOrDefault(x => x.Id == id) is { } x) { x.Enabled = on; x.SnoozedUntil = null; } })),
                Buttons.Icon(Glyphs.Delete, () => _alarms.Mutate(l => l.RemoveAll(x => x.Id == id)), 32, 13, "Delete alarm"));
            controls.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(controls, 2); g.Children.Add(controls);
            rows.Add(g);
        }
        if (rows.Count == 0) rows.Add(Kit.Setting("No alarms", "Add one below."));
        page.Children.Add(Kit.Group([.. rows]));

        page.Children.Add(Kit.Section("New alarm"));
        var picker = new TimePicker { ClockIdentifier = H24 ? "24HourClock" : "12HourClock", Time = new TimeSpan(7, 0, 0), MinuteIncrement = 1 };
        var label = new TextBox { PlaceholderText = "Label", Width = 220, Text = "" };
        int days = 0;
        var dayRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var names = CultureInfo.CurrentCulture.DateTimeFormat.AbbreviatedDayNames;
        int first = (int)CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        for (int i = 0; i < 7; i++)
        {
            int d = (first + i) % 7;
            var t = new ToggleButton { Content = Cap(names[d]), MinWidth = 44, CornerRadius = new CornerRadius(16) };
            t.Checked += (_, _) => days |= 1 << d;
            t.Unchecked += (_, _) => days &= ~(1 << d);
            dayRow.Children.Add(t);
        }
        picker.GotFocus += (_, _) => _editingAlarm = true;
        label.GotFocus += (_, _) => _editingAlarm = true;
        var save = Buttons.Accent("Add alarm", () =>
        {
            _editingAlarm = false;
            var alarm = new Alarm { Hour = picker.Time.Hours, Minute = picker.Time.Minutes, Days = days, Label = string.IsNullOrWhiteSpace(label.Text) ? "Alarm" : label.Text.Trim() };
            _alarms.Mutate(l => l.Add(alarm));
            _nav.Select("alarms");
        });
        page.Children.Add(Kit.Card(Kit.Stack(12, Kit.Row(12, picker, label), Ui.Text("Repeat (leave empty for once)", 12, "Vos.TextSecondary"), dayRow, save)));
        return page;
    }

    private static string Relative(DateTime t)
    {
        var d = t - DateTime.Now;
        if (d < TimeSpan.FromMinutes(1)) return "in less than a minute";
        if (d < TimeSpan.FromHours(24)) return "in " + Fmt.DurationWords(d);
        return t.ToString("ddd HH:mm", CultureInfo.CurrentCulture);
    }

    // ------------------------------------------------------------------ timers

    private StackPanel Timers()
    {
        var page = Kit.Stack(4, Kit.PageHeader("Timers", "Running timers also show in the capsule and keep going when this window is closed."));
        var presets = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var m in new[] { 1, 3, 5, 10, 15, 25, 30, 45, 60 })
            presets.Children.Add(Kit.Chip(m == 60 ? "1 h" : $"{m} min", false, () => { _timers.Start(TimeSpan.FromMinutes(m), m == 25 ? "Focus session" : null); }));
        page.Children.Add(Kit.Card(Kit.Stack(10, Ui.Text("Quick start", 13, "Vos.TextSecondary"), new ScrollViewer { Content = presets, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollMode = ScrollMode.Disabled })));

        var h = new NumberBox { Header = "Hours", Minimum = 0, Maximum = 23, Value = 0, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, Width = 110 };
        var mi = new NumberBox { Header = "Minutes", Minimum = 0, Maximum = 59, Value = 5, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, Width = 110 };
        var s = new NumberBox { Header = "Seconds", Minimum = 0, Maximum = 59, Value = 0, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, Width = 110 };
        var label = new TextBox { Header = "Label", PlaceholderText = "Tea, laundry, focus…", Width = 220 };
        var start = Buttons.Accent("Start", () =>
        {
            var d = new TimeSpan((int)(double.IsNaN(h.Value) ? 0 : h.Value), (int)(double.IsNaN(mi.Value) ? 0 : mi.Value), (int)(double.IsNaN(s.Value) ? 0 : s.Value));
            if (d.TotalSeconds >= 1) _timers.Start(d, label.Text);
        });
        start.VerticalAlignment = VerticalAlignment.Bottom;
        page.Children.Add(Kit.Card(Kit.Row(12, h, mi, s, label, start)));

        var running = _timers.List().Where(t => t.State != TimerState.Cancelled).OrderBy(t => t.State == TimerState.Done).ThenBy(t => t.Remaining(DateTimeOffset.Now)).ToList();
        page.Children.Add(Kit.Section(running.Count == 0 ? "No timers running" : "Running"));
        var updates = new List<Action>();
        foreach (var t in running)
        {
            var id = t.Id;
            var remaining = new TextBlock { FontSize = 30, FontFamily = Tokens.Instrument, FontWeight = FontWeights.SemiBold, Foreground = Ui.B("Vos.TextPrimary"), MinWidth = 150 };
            var bar = new ProgressBar { Maximum = 1, Value = 0, Margin = new Thickness(0, 8, 0, 0) };
            var info = Kit.Stack(2, Ui.Text(t.Label, 14), Ui.Text(Fmt.DurationWords(TimeSpan.FromSeconds(t.DurationSeconds)) + " timer", 12, "Vos.TextSecondary"), bar);
            var actions = Kit.Row(4,
                t.State == TimerState.Done ? new Border() : Buttons.Icon(t.State == TimerState.Paused ? Glyphs.Play : Glyphs.Pause, () => { if (t.State == TimerState.Paused) _timers.Resume(id); else _timers.Pause(id); }, 34, 14, t.State == TimerState.Paused ? "Resume" : "Pause"),
                Buttons.Icon(Glyphs.Add, () => _timers.AddTime(id, TimeSpan.FromMinutes(1)), 34, 14, "Add one minute"),
                Buttons.Icon(Glyphs.Close, () => _timers.Cancel(id), 34, 14, "Cancel"));
            var g = new Grid { Padding = new Thickness(18, 14, 16, 14), ColumnSpacing = 18 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(remaining);
            Grid.SetColumn(info, 1); g.Children.Add(info);
            actions.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(actions, 2); g.Children.Add(actions);
            page.Children.Add(Kit.Card(g, new Thickness(0)));
            page.Children.Add(new Border { Height = 8 });
            updates.Add(() =>
            {
                var left = t.Remaining(DateTimeOffset.Now);
                remaining.Text = t.State == TimerState.Done || left <= TimeSpan.Zero ? "Done" : left.TotalHours >= 1 ? left.ToString(@"h\:mm\:ss") : left.ToString(@"m\:ss");
                bar.Value = Math.Clamp(t.Progress(DateTimeOffset.Now), 0, 1);
                remaining.Foreground = Ui.B(t.State == TimerState.Paused ? "Vos.TextTertiary" : t.State == TimerState.Done ? "Vos.AccentText" : "Vos.TextPrimary");
            });
        }
        void Tick() { foreach (var u in updates) u(); }
        Tick();
        _onSecond = Tick;
        return page;
    }

    // ------------------------------------------------------------------ stopwatch

    private readonly Stopwatch _sw = new();
    private readonly List<TimeSpan> _laps = [];
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _stopwatchTimer;

    private StackPanel StopwatchPage()
    {
        var page = Kit.Stack(4, Kit.PageHeader("Stopwatch"));
        var display = new TextBlock { FontSize = 72, FontFamily = Tokens.Instrument, FontWeight = FontWeights.SemiBold, Foreground = Ui.B("Vos.TextPrimary"), HorizontalAlignment = HorizontalAlignment.Center };
        var startStop = Buttons.Accent("Start", null, 22, new Thickness(28, 10, 28, 10));
        var lap = Buttons.Filled("Lap", null, 22, new Thickness(28, 10, 28, 10));
        var reset = Buttons.Filled("Reset", null, 22, new Thickness(28, 10, 28, 10));
        var buttons = Kit.Row(12, startStop, lap, reset);
        buttons.HorizontalAlignment = HorizontalAlignment.Center;
        var lapList = new StackPanel();
        page.Children.Add(Kit.Card(Kit.Stack(20, display, buttons), new Thickness(24, 30, 24, 30)));
        page.Children.Add(Kit.Section("Laps"));
        page.Children.Add(Kit.Card(lapList, new Thickness(0)));

        static string Fmt2(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss\.ff") : t.ToString(@"mm\:ss\.ff");
        void Render()
        {
            display.Text = Fmt2(_sw.Elapsed);
            ((TextBlock)startStop.Content).Text = _sw.IsRunning ? "Stop" : _sw.Elapsed > TimeSpan.Zero ? "Resume" : "Start";
            lap.IsEnabled = _sw.IsRunning;
            reset.IsEnabled = !_sw.IsRunning && _sw.Elapsed > TimeSpan.Zero;
        }
        void RenderLaps()
        {
            lapList.Children.Clear();
            if (_laps.Count == 0) { lapList.Children.Add(Kit.Setting("No laps yet", "Press Lap while the stopwatch runs.")); return; }
            var splits = _laps.Select((t, i) => i == 0 ? t : t - _laps[i - 1]).ToList();
            var fastest = splits.Count > 1 ? splits.Min() : TimeSpan.MinValue;
            var slowest = splits.Count > 1 ? splits.Max() : TimeSpan.MaxValue;
            for (int i = _laps.Count - 1; i >= 0; i--)
            {
                string brush = splits[i] == fastest ? "Vos.Success" : splits[i] == slowest ? "Vos.Critical" : "Vos.TextPrimary";
                var row = Kit.Setting($"Lap {i + 1}", $"Total {Fmt2(_laps[i])}", Ui.Text(Fmt2(splits[i]), 16, brush, Tokens.Instrument, FontWeights.SemiBold));
                if (lapList.Children.Count > 0) lapList.Children.Add(new Border { Height = 1, Background = Ui.B("Vos.StrokeSubtle") });
                lapList.Children.Add(row);
            }
        }
        _stopwatchTimer?.Stop();
        _stopwatchTimer = UiThread.Timer(TimeSpan.FromMilliseconds(33), () => { if (_nav.Current == "stopwatch") display.Text = Fmt2(_sw.Elapsed); }, repeating: true);
        startStop.Click += (_, _) =>
        {
            if (_sw.IsRunning) { _sw.Stop(); _stopwatchTimer.Stop(); }
            else { _sw.Start(); _stopwatchTimer.Start(); }
            Render();
        };
        lap.Click += (_, _) => { _laps.Add(_sw.Elapsed); RenderLaps(); };
        reset.Click += (_, _) => { _sw.Reset(); _laps.Clear(); Render(); RenderLaps(); };
        startStop.Content = new TextBlock { Text = "Start" };
        if (_sw.IsRunning) _stopwatchTimer.Start();
        Render();
        RenderLaps();
        return page;
    }

    // ------------------------------------------------------------------ calendar

    private DateTime _month = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private DateTime _day = DateTime.Today;

    private Grid Calendar()
    {
        var root = new Grid { Padding = new Thickness(28, 22, 28, 20), ColumnSpacing = 20, RowSpacing = 12 };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.6, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 260 });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var title = Ui.Text(Cap(_month.ToString("Y", CultureInfo.CurrentCulture)), 26, "Vos.TextPrimary", Tokens.Display, FontWeights.SemiBold);
        var nav = Kit.Row(4,
            Buttons.Icon(Glyphs.ChevronLeft, () => { _month = _month.AddMonths(-1); _nav.Select("calendar"); }, 34, 13, "Previous month"),
            Buttons.Filled("Today", () => { _month = new(DateTime.Today.Year, DateTime.Today.Month, 1); _day = DateTime.Today; _nav.Select("calendar"); }),
            Buttons.Icon(Glyphs.ChevronRight, () => { _month = _month.AddMonths(1); _nav.Select("calendar"); }, 34, 13, "Next month"),
            Buttons.Filled(Kit.Row(6, Ui.Icon(Glyphs.Download, 13), Ui.Text("Import .ics", 13.5)), ImportIcs),
            Buttons.Accent(Kit.Row(6, Ui.Icon(Glyphs.Add, 13, "Vos.OnAccent"), Ui.Text("New event", 13.5, "Vos.OnAccent")), () => _ = NewEventAsync()));
        nav.HorizontalAlignment = HorizontalAlignment.Right;
        var head = new Grid();
        head.Children.Add(title);
        head.Children.Add(nav);
        Grid.SetColumnSpan(head, 2);
        root.Children.Add(head);

        // Month grid: 6 weeks starting on the culture's first day of the week.
        var fmt = CultureInfo.CurrentCulture.DateTimeFormat;
        int firstDow = (int)fmt.FirstDayOfWeek;
        var gridStart = _month.AddDays(-(((int)_month.DayOfWeek - firstDow + 7) % 7));
        var events = _calendar.Range(gridStart, gridStart.AddDays(42));
        var grid = new Grid { RowSpacing = 6, ColumnSpacing = 6 };
        for (int c = 0; c < 7; c++) grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int r = 0; r < 6; r++) grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        for (int c = 0; c < 7; c++)
        {
            var h = Ui.Text(Cap(fmt.AbbreviatedDayNames[(firstDow + c) % 7]), 12, "Vos.TextTertiary", Tokens.Text, FontWeights.SemiBold);
            h.Margin = new Thickness(8, 0, 0, 2);
            Grid.SetColumn(h, c); grid.Children.Add(h);
        }
        for (int i = 0; i < 42; i++)
        {
            var date = gridStart.AddDays(i);
            var dayEvents = events.Where(e => e.Start.Date <= date && (e.End > date || e.Start.Date == date) && (e.AllDay ? e.End.Date > date || e.Start.Date == date : true) && e.Start < date.AddDays(1)).ToList();
            bool inMonth = date.Month == _month.Month, today = date == DateTime.Today, selected = date == _day;
            var cell = new StackPanel { Spacing = 3, Padding = new Thickness(8, 6, 6, 6) };
            var number = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(13), HorizontalAlignment = HorizontalAlignment.Left, Background = today ? Ui.B("Vos.Accent") : new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
            number.Child = new TextBlock { Text = date.Day.ToString(CultureInfo.CurrentCulture), FontSize = 13, FontWeight = today ? FontWeights.SemiBold : FontWeights.Normal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.B(today ? "Vos.OnAccent" : inMonth ? "Vos.TextPrimary" : "Vos.TextTertiary") };
            cell.Children.Add(number);
            foreach (var e in dayEvents.Take(2))
                cell.Children.Add(new Border { CornerRadius = new CornerRadius(4), Background = Ui.B("Vos.AccentSubtle"), Padding = new Thickness(5, 1, 5, 2), Child = Ui.Text(e.Title, 11, "Vos.TextPrimary") });
            if (dayEvents.Count > 2) cell.Children.Add(Ui.Text($"+{dayEvents.Count - 2} more", 11, "Vos.TextTertiary"));
            var b = Buttons.Subtle(cell, () => { _day = date; if (date.Month != _month.Month) _month = new DateTime(date.Year, date.Month, 1); _nav.Select("calendar"); }, Tokens.RadiusControl, new Thickness(0));
            b.HorizontalAlignment = HorizontalAlignment.Stretch; b.VerticalAlignment = VerticalAlignment.Stretch;
            b.HorizontalContentAlignment = HorizontalAlignment.Stretch; b.VerticalContentAlignment = VerticalAlignment.Top;
            if (selected) { b.BorderBrush = Ui.B("Vos.Accent"); b.BorderThickness = new Thickness(1.5); b.Background = Ui.B("Vos.Control"); }
            else b.Background = inMonth ? Ui.B("Vos.Card") : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            Buttons.AutomationName(b, date.ToString("D", CultureInfo.CurrentCulture));
            Grid.SetColumn(b, i % 7); Grid.SetRow(b, i / 7 + 1);
            grid.Children.Add(b);
        }
        Grid.SetRow(grid, 1);
        root.Children.Add(grid);

        // Agenda for the selected day.
        var agenda = Kit.Stack(8, Ui.Text(Cap(_day.ToString("D", CultureInfo.CurrentCulture)), 16, "Vos.TextPrimary", Tokens.Text, FontWeights.SemiBold));
        var dayList = _calendar.Range(_day, _day.AddDays(1));
        if (dayList.Count == 0) agenda.Children.Add(Ui.Text("Nothing planned.", 13, "Vos.TextSecondary"));
        foreach (var e in dayList)
        {
            var when = e.AllDay ? "All day" : $"{Clock(e.Start)} – {Clock(e.End)}";
            var lines = Kit.Stack(2, Ui.Text(e.Title, 14, "Vos.TextPrimary", Tokens.Text, FontWeights.SemiBold), Ui.Text(when + (e.Location.Length > 0 ? " · " + e.Location : ""), 12, "Vos.TextSecondary"));
            if (e.Notes.Length > 0) lines.Children.Add(Ui.Text(e.Notes, 12, "Vos.TextTertiary", wrap: TextWrapping.Wrap));
            var g = new Grid { ColumnSpacing = 10 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(new Border { Background = Ui.B("Vos.Accent"), CornerRadius = new CornerRadius(1.5) });
            Grid.SetColumn(lines, 1); g.Children.Add(lines);
            if (e.Source == "local" && !e.Id.Contains('@'))
            {
                var id = e.Id;
                var del = Buttons.Icon(Glyphs.Delete, async () =>
                {
                    if (!await Dialogs.ConfirmAsync(Frame.Root.XamlRoot, "Delete this event?", e.Title, "Delete", destructive: true)) return;
                    _calendar.Delete(id); _nav.Select("calendar");
                }, 30, 12, "Delete event");
                Grid.SetColumn(del, 2); g.Children.Add(del);
            }
            agenda.Children.Add(Kit.Card(g, new Thickness(12, 10, 10, 10)));
        }
        var sources = _calendar.Sources().Where(s => s.StartsWith("ics:", StringComparison.Ordinal)).ToList();
        if (sources.Count > 0)
        {
            agenda.Children.Add(Kit.Section("Imported calendars"));
            foreach (var src in sources)
                agenda.Children.Add(Kit.Setting(src[4..], null, Buttons.Icon(Glyphs.Delete, () => { _calendar.DeleteSource(src); _nav.Select("calendar"); }, 30, 12, "Remove calendar")));
        }
        var agendaScroll = new ScrollViewer { Content = agenda, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(agendaScroll, 1); Grid.SetColumn(agendaScroll, 1);
        root.Children.Add(agendaScroll);
        return root;
    }

    private async void ImportIcs()
    {
        var path = await Pickers.FileAsync(Frame.Hwnd, Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary, ".ics");
        if (path == null) return;
        try
        {
            int n = _calendar.ImportIcs(path);
            await Dialogs.InfoAsync(Frame.Root.XamlRoot, "Calendar imported", $"{n} events from {Path.GetFileName(path)}. Importing the same file again replaces them.");
        }
        catch (Exception ex) { await Dialogs.InfoAsync(Frame.Root.XamlRoot, "Import failed", ex.Message); }
        _nav.Select("calendar");
    }

    private async Task NewEventAsync()
    {
        var title = new TextBox { Header = "Title", PlaceholderText = "Dinner with friends" };
        var date = new CalendarDatePicker { Header = "Date", Date = _day, HorizontalAlignment = HorizontalAlignment.Stretch };
        var allDay = new CheckBox { Content = "All day" };
        var start = new TimePicker { Header = "Starts", ClockIdentifier = H24 ? "24HourClock" : "12HourClock", Time = new TimeSpan(Math.Min(22, DateTime.Now.Hour + 1), 0, 0) };
        var end = new TimePicker { Header = "Ends", ClockIdentifier = H24 ? "24HourClock" : "12HourClock", Time = new TimeSpan(Math.Min(23, DateTime.Now.Hour + 2), 0, 0) };
        var location = new TextBox { Header = "Location", PlaceholderText = "Optional" };
        var notes = new TextBox { Header = "Notes", PlaceholderText = "Optional", AcceptsReturn = true, Height = 72, TextWrapping = TextWrapping.Wrap };
        allDay.Checked += (_, _) => start.IsEnabled = end.IsEnabled = false;
        allDay.Unchecked += (_, _) => start.IsEnabled = end.IsEnabled = true;
        var dlg = new ContentDialog
        {
            XamlRoot = Frame.Root.XamlRoot, Title = "New event", PrimaryButtonText = "Save", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary,
            RequestedTheme = ThemeService.Current.ElementTheme,
            Content = Kit.Stack(10, title, date, allDay, Kit.Row(12, start, end), location, notes),
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(title.Text)) return;
        var day = (date.Date ?? _day).Date;
        var e = new CalendarEvent
        {
            Title = title.Text.Trim(), AllDay = allDay.IsChecked == true, Location = location.Text.Trim(), Notes = notes.Text.Trim(),
            Start = allDay.IsChecked == true ? day : day + start.Time,
            End = allDay.IsChecked == true ? day.AddDays(1) : day + (end.Time > start.Time ? end.Time : start.Time + TimeSpan.FromHours(1)),
        };
        _calendar.Save(e);
        _day = day;
        _month = new DateTime(day.Year, day.Month, 1);
        _nav.Select("calendar");
    }
}
