using System.Globalization;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using VOS.Core;
using VOS.Core.Notifications;
using VOS.Core.Settings;
using VOS.Core.Timers;
using VOS.Core.Weather;
using VOS.Platform.Native;
using VOS.Platform.Shell;
using VOS.Platform.Sys;
using VOS.Platform.Windowing;
using VOS.UI;
using VOS.UI.Quality;
using VOS.UI.Surfaces;
using VOS.UI.Theme;
using Windows.Devices.Radios;
using Windows.Graphics;

namespace VOS.Shell;

public enum PanelKind { Command, Control, Activity, VMenu, Workspaces, Clipboard, Switcher, Drop, Layout }
public enum PanelPlacement { TopLeft, TopRight, TopCenter, Right, Center }

/// <summary>Creates panels on demand (lazy loading) and keeps at most one open at a time.</summary>
public sealed class Panels(ShellHost host)
{
    private readonly Dictionary<PanelKind, PanelWindow> _panels = [];
    private PanelWindow? _open;

    public XamlRoot? AnyXamlRoot => _open?.Root.XamlRoot ?? host.Bar.Root.XamlRoot;

    public void Toggle(PanelKind kind)
    {
        if (_open is { IsOpen: true } o && _panels.TryGetValue(kind, out var p) && o == p) { o.Dismiss(); return; }
        Show(kind);
    }

    public void Show(PanelKind kind)
    {
        _open?.Dismiss();
        var panel = Get(kind);
        _open = panel;
        panel.Open();
    }

    /// <summary>Builds and renders panels invisibly during idle time, so their first opening is instant.</summary>
    public void Prewarm(params PanelKind[] kinds)
    {
        foreach (var kind in kinds) Get(kind).Prewarm();
    }

    private PanelWindow Get(PanelKind kind)
    {
        if (!_panels.TryGetValue(kind, out var panel))
        {
            panel = kind switch
            {
                PanelKind.Command => new CommandPanel(host),
                PanelKind.Control => new ControlCenterPanel(host),
                PanelKind.Activity => new ActivityPanel(host),
                PanelKind.VMenu => new VMenuPanel(host),
                PanelKind.Workspaces => new WorkspacesPanel(host),
                PanelKind.Clipboard => new ClipboardPanel(host),
                PanelKind.Switcher => new SwitcherPanel(host),
                PanelKind.Drop => new DropShelfPanel(host),
                PanelKind.Layout => new LayoutPanel(host),
                _ => throw new ArgumentOutOfRangeException(nameof(kind)),
            };
            _panels[kind] = panel;
        }
        return panel;
    }

    public void DismissAll() => _open?.Dismiss();
}

/// <summary>
/// Base for shell panels: activating glass window, dismissed on focus loss or Esc, soft shadow, entrance motion.
/// A panel never appears half-built: it stays DWM-cloaked until its content is ready and XAML has rendered it.
/// </summary>
public abstract class PanelWindow : SurfaceWindow
{
    protected const double ShadowMargin = 26;
    protected ShellHost Host { get; }
    protected Grid Body { get; } = new();
    protected PanelPlacement Placement { get; init; } = PanelPlacement.TopRight;
    protected double PanelWidth { get; set; }
    protected double PanelHeight { get; set; }
    private readonly Grid _card = new();   // shadow + frame: what the entrance motion moves and fades
    private readonly Grid _frame = new();
    private readonly SoftShadow _shadow;
    private DateTime _openedAt;
    private int _generation;   // bumped by every open/dismiss: stale asynchronous steps check it and stop
    private bool _presented, _warmed;
    private FrameworkElement? _fit;
    protected IntPtr PreviousForeground { get; private set; }

    /// <summary>When set, the panel height follows this element's desired height (clamped to the work area).</summary>
    protected FrameworkElement? FitContent
    {
        get => _fit;
        set
        {
            _fit = value;
            if (value != null) value.SizeChanged += (_, _) => { if (_presented && IsOpen) FitHeight(); };
        }
    }

    protected PanelWindow(ShellHost host, string title, double width, double height) : base(title, topmost: true, noActivate: false)
    {
        Host = host;
        PanelWidth = width;
        PanelHeight = height;
        _shadow = new SoftShadow(_frame, Tokens.RadiusPanel);
        _frame.Margin = new Thickness(ShadowMargin);
        _frame.Children.Add(new GlassSurface(Tokens.RadiusPanel));
        _frame.Children.Add(Body);
        _card.Children.Add(_frame);
        Root.Children.Add(_card);
        Window.Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated && IsOpen && DateTime.Now - _openedAt > TimeSpan.FromMilliseconds(250)) Dismiss();
        };
        Root.KeyDown += (_, e) => { if (e.Key == global::Windows.System.VirtualKey.Escape) { e.Handled = true; Dismiss(); } };
    }

    /// <summary>
    /// Whether the panel is open for the user. A closed panel keeps its window shown but DWM-cloaked (invisible and
    /// not hit-testable): re-showing a hidden WinUI window can flash one black frame before XAML re-presents, while
    /// uncloaking an already presented window cannot.
    /// </summary>
    public bool IsOpen { get; private set; }

    public async void Open()
    {
        int gen = ++_generation;
        IsOpen = true;
        try
        {
            PreviousForeground = Host.Tracker.Foreground;
            OnOpening();
            var preparing = Prepare();
            _openedAt = DateTime.Now;
            _presented = false;
            bool firstShow = !IsVisible;
            SetCloaked(true);
            Place();
            if (firstShow) Show(activate: true); else Win32.Activate(Hwnd);
            // Give asynchronous content a moment so the panel appears complete, then let the new content reach the screen.
            await Task.WhenAny(preparing, Task.Delay(400));
            if (gen != _generation) return;
            FitHeight();
            if (firstShow) await PresentedAsync();
            else { await UiThread.NextFrameAsync(1); await Task.Run(() => Win32.DwmFlush()); }
            if (gen != _generation) return;
            SetCloaked(false);
            _presented = true;
            Motion.Enter(_card, new Vector3(0, Placement is PanelPlacement.TopCenter or PanelPlacement.Center ? 10 : -10, 0), 240);
            OnOpened();
        }
        catch (Exception ex)
        {
            Log.Warn($"Panel '{Window.Title}' failed to open", ex);
            SetCloaked(false);
        }
    }

    /// <summary>Builds and renders the panel once, invisibly (cloaked), so its first real opening is instant.</summary>
    public async void Prewarm()
    {
        if (_warmed || IsVisible) return;
        _warmed = true;
        int gen = _generation;
        try
        {
            OnOpening();
            await Task.WhenAny(Prepare(), Task.Delay(2000));
            if (gen != _generation) return; // the user opened it meanwhile; Open() owns the window now
            SetCloaked(true);
            Place();
            Show(activate: false);
            await PresentedAsync();
        }
        catch (Exception ex) { Log.Warn($"Panel '{Window.Title}' warm-up failed", ex); }
    }
    private Task Prepare()
    {
        var task = OnOpeningAsync();
        _ = task.ContinueWith(t => Log.Warn($"Panel '{Window.Title}' content failed", t.Exception?.GetBaseException()), TaskContinuationOptions.OnlyOnFaulted);
        return task;
    }

    private void FitHeight()
    {
        if (_fit == null) return;
        var mon = Displays.Primary;
        _fit.Measure(new global::Windows.Foundation.Size(PanelWidth, double.PositiveInfinity));
        double max = mon.WorkArea.Height / mon.Scale - 12;
        double h = Math.Min(max, Math.Ceiling(_fit.DesiredSize.Height));
        if (h <= 0 || Math.Abs(h - PanelHeight) < 1) return;
        PanelHeight = h;
        Place();
    }

    private void Place()
    {
        var mon = Displays.Primary;
        double s = mon.Scale;
        double w = PanelWidth + ShadowMargin * 2, h = PanelHeight + ShadowMargin * 2;
        var work = mon.WorkArea;
        double top = work.Top / s + 6 - ShadowMargin;
        double left = Placement switch
        {
            PanelPlacement.TopLeft => work.Left / s + 8 - ShadowMargin,
            PanelPlacement.TopCenter or PanelPlacement.Center => work.Left / s + (work.Width / s - w) / 2,
            _ => work.Right / s - w - 8 + ShadowMargin,
        };
        if (Placement == PanelPlacement.TopCenter) top = work.Top / s + work.Height / s * 0.14 - ShadowMargin;
        if (Placement == PanelPlacement.Center) top = work.Top / s + (work.Height / s - h) / 2;
        MoveAndResize(new RectInt32((int)(left * s), (int)(top * s), (int)(w * s), (int)(h * s)));
    }

    public virtual void Dismiss()
    {
        _generation++;
        if (!IsOpen) return;
        IsOpen = false;
        _presented = false;
        OnClosing();
        SetCloaked(true);
        // A cloaked window can still hold keyboard focus: hand it back to where the user was.
        if (Win32.GetForegroundWindow() == Hwnd)
        {
            var back = PreviousForeground;
            Win32.Activate(back != IntPtr.Zero && Win32.IsWindow(back) && Win32.IsWindowVisible(back) ? back : Win32.GetShellWindow());
        }
    }

    protected virtual void OnOpening() { }
    /// <summary>Asynchronous content (device queries and the like). Opening waits for it briefly before showing the panel.</summary>
    protected virtual Task OnOpeningAsync() => Task.CompletedTask;
    protected virtual void OnOpened() { }
    protected virtual void OnClosing() { }

    protected static TextBlock Header(string text) => Ui.Text(text, 12, "Vos.TextSecondary", Tokens.Small, FontWeights.SemiBold);
}

/// <summary>
/// Soft rounded drop shadow behind an element, drawn by the compositor (only when quality allows shadows).
/// A sprite visual casts a DropShadow whose mask is an opaque rounded rectangle rendered into a visual surface; the
/// sprite itself draws nothing. (A LayerVisual over a near-transparent shape occasionally rendered the shape solid
/// black for a frame, which showed as a black flash when a panel opened.)
/// </summary>
public sealed class SoftShadow
{
    public SoftShadow(FrameworkElement target, double radius)
    {
        target.Loaded += (_, _) => Attach(target, radius);
    }

    private static void Attach(FrameworkElement target, double radius)
    {
        if (target.Parent is not Panel parent || !VisualQuality.Current.Shadows) return;
        var host = new Grid { IsHitTestVisible = false, Margin = target.Margin };
        parent.Children.Insert(parent.Children.IndexOf(target), host);
        var c = ElementCompositionPreview.GetElementVisual(host).Compositor;
        var geo = c.CreateRoundedRectangleGeometry();
        geo.CornerRadius = new Vector2((float)radius);
        var shape = c.CreateSpriteShape(geo);
        shape.FillBrush = c.CreateColorBrush(global::Windows.UI.Color.FromArgb(255, 0, 0, 0));
        var shapeVisual = c.CreateShapeVisual();
        shapeVisual.Shapes.Add(shape);
        var surface = c.CreateVisualSurface();
        surface.SourceVisual = shapeVisual;
        var mask = c.CreateSurfaceBrush(surface);
        mask.Stretch = CompositionStretch.Fill;
        var shadow = c.CreateDropShadow();
        shadow.BlurRadius = 30;
        shadow.Opacity = ThemeService.Current.IsDark ? 0.55f : 0.22f;
        shadow.Offset = new Vector3(0, 10, 0);
        shadow.Color = global::Windows.UI.Color.FromArgb(255, 0, 0, 0);
        shadow.Mask = mask;
        var sprite = c.CreateSpriteVisual();
        sprite.Shadow = shadow;
        ElementCompositionPreview.SetElementChildVisual(host, sprite);
        void Resize()
        {
            var sz = new Vector2((float)target.ActualWidth, (float)target.ActualHeight);
            geo.Size = sz; shapeVisual.Size = sz; surface.SourceSize = sz; sprite.Size = sz;
        }
        target.SizeChanged += (_, _) => Resize();
        Resize();
    }
}
/// <summary>The V menu: about, settings, power and session actions.</summary>
public sealed class VMenuPanel : PanelWindow
{
    public VMenuPanel(ShellHost host) : base(host, "V-OS Menu", 268, 404)
    {
        Placement = PanelPlacement.TopLeft;
        var stack = new StackPanel { Padding = new Thickness(8), Spacing = 2 };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(10, 8, 10, 10) };
        header.Children.Add(new TextBlock { Text = "V", FontFamily = Tokens.Instrument, FontSize = 26, FontWeight = FontWeights.SemiBold, Foreground = Ui.B("Vos.Accent") });
        var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        name.Children.Add(Ui.Text("V-OS", 14, "Vos.TextPrimary", Tokens.Display, FontWeights.SemiBold));
        name.Children.Add(Ui.Text($"Version {AppInfo.Version}", 12, "Vos.TextSecondary"));
        header.Children.Add(name);
        stack.Children.Add(header);
        void Item(string glyph, string text, Action a)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            row.Children.Add(Ui.Icon(glyph, 14, "Vos.TextSecondary"));
            row.Children.Add(Ui.Text(text, 13.5));
            var b = Buttons.Subtle(row, () => { Dismiss(); a(); }, 10, new Thickness(12, 9, 12, 9));
            b.HorizontalAlignment = HorizontalAlignment.Stretch;
            b.HorizontalContentAlignment = HorizontalAlignment.Left;
            stack.Children.Add(b);
        }
        void Separator() => stack.Children.Add(new Border { Height = 1, Margin = new Thickness(10, 6, 10, 6), Background = Ui.B("Vos.StrokeSubtle") });
        Item(Glyphs.Apps, "All apps", () => host.Panels.Show(PanelKind.Command));
        Item(Glyphs.Settings, "Settings", () => host.Commands.OpenApp("settings"));
        Item(Glyphs.Info, "About V-OS", () => host.Commands.OpenApp("settings", "about"));
        Separator();
        Item(Glyphs.Lock, "Lock", () => LockPresentation.Lock(host));
        Item(Glyphs.Sleep, "Sleep", () => _ = host.Commands.RunAsync("system:sleep", host.Bar.Root.XamlRoot));
        Item(Glyphs.Restart, "Restart…", () => _ = host.Commands.RunAsync("system:restart", host.Bar.Root.XamlRoot));
        Item(Glyphs.Power, "Shut down…", () => _ = host.Commands.RunAsync("system:shutdown", host.Bar.Root.XamlRoot));
        Item(Glyphs.SignOut, "Sign out…", () => _ = host.Commands.RunAsync("system:signout", host.Bar.Root.XamlRoot));
        Separator();
        Item(Glyphs.Desktop, "Exit to Windows…", async () =>
        {
            if (await Dialogs.ConfirmAsync(host.Bar.Root.XamlRoot, "Exit V-OS?", "The V-OS desktop closes and your Windows desktop is restored. Start V-OS again from the Start menu.", "Exit V-OS"))
                host.ExitShell(restoreDesktop: true);
        });
        Body.Children.Add(stack);
    }
}

/// <summary>Workspace switcher.</summary>
public sealed class WorkspacesPanel : PanelWindow
{
    private readonly StackPanel _list = new() { Spacing = 2 };

    public WorkspacesPanel(ShellHost host) : base(host, "V-OS Workspaces", 300, 420)
    {
        Placement = PanelPlacement.TopLeft;
        var root = new StackPanel { Padding = new Thickness(14), Spacing = 10 };
        root.Children.Add(Ui.Text("Workspaces", 16, "Vos.TextPrimary", Tokens.Display, FontWeights.SemiBold));
        root.Children.Add(_list);
        var save = Buttons.Filled(Ui.Text("Save open windows to this workspace", 12.5), () =>
        {
            if (host.Workspaces.Active is { } ws) host.Workspaces.Capture(ws.Id);
            host.Notifications.Post(new VosNotification { AppId = "workspaces", AppName = "Workspaces", Title = "Layout saved", Body = "Open windows will be restored when you return to this workspace.", Glyph = Glyphs.Layers, Priority = NotificationPriority.Low });
            Dismiss();
        });
        save.HorizontalAlignment = HorizontalAlignment.Stretch;
        root.Children.Add(save);
        Body.Children.Add(root);
    }

    protected override void OnOpening()
    {
        _list.Children.Clear();
        var s = Host.Settings.Current;
        foreach (var ws in s.Workspaces)
        {
            bool active = ws.Id == s.ActiveWorkspaceId;
            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var glyph = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(9), Background = Ui.B(active ? "Vos.AccentSubtle" : "Vos.Control"), Child = Ui.Icon(TopBar.WorkspaceGlyph(ws.Icon), 14, active ? "Vos.Accent" : "Vos.TextSecondary") };
            row.Children.Add(glyph);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(Ui.Text(ws.Name, 13.5, "Vos.TextPrimary", Tokens.Text, active ? FontWeights.SemiBold : FontWeights.Normal));
            string detail = string.Join(" · ", new[] { ws.Profile?.ToString(), ws.Focus ? "Focus" : null, ws.Windows.Count > 0 ? $"{ws.Windows.Count} windows" : null }.Where(x => x != null));
            if (detail.Length > 0) text.Children.Add(Ui.Text(detail, 11.5, "Vos.TextTertiary"));
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            if (active) { var check = Ui.Icon(Glyphs.Check, 13, "Vos.Accent"); Grid.SetColumn(check, 2); check.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(check); }
            var b = Buttons.Subtle(row, () => { Dismiss(); Host.Workspaces.Switch(ws.Id); }, 12, new Thickness(8, 7, 10, 7));
            b.HorizontalAlignment = HorizontalAlignment.Stretch;
            b.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            _list.Children.Add(b);
        }
    }
}

/// <summary>V-Control: quick settings. Shows only controls the hardware actually supports.</summary>
public sealed class ControlCenterPanel : PanelWindow
{
    private readonly StackPanel _root = new() { Padding = new Thickness(16), Spacing = 12 };
    private static bool? s_brightnessSupported; // probed once: external monitors usually have no software brightness

    public ControlCenterPanel(ShellHost host) : base(host, "V-OS Control Center", 360, 420)
    {
        Placement = PanelPlacement.TopRight;
        var scroll = new ScrollViewer { Content = _root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Body.Children.Add(scroll);
        FitContent = _root;
    }

    protected override Task OnOpeningAsync() => BuildAsync();

    private async Task BuildAsync()
    {
        // Gather everything asynchronous first, then rebuild in one step: the panel is never seen half-filled.
        var radiosTask = Radios.ListAsync();
        var brightnessTask = s_brightnessSupported == false ? Task.FromResult<int?>(null) : Task.Run(Brightness.Get);
        var radios = await radiosTask;
        int? brightness = await brightnessTask;
        s_brightnessSupported = brightness != null;

        _root.Children.Clear();
        var s = Host.Settings.Current;
        var tiles = new Grid { ColumnSpacing = 10, RowSpacing = 10 };
        tiles.ColumnDefinitions.Add(new ColumnDefinition());
        tiles.ColumnDefinitions.Add(new ColumnDefinition());
        int index = 0;
        void Add(ToggleTile t) { t.Height = 92; Grid.SetColumn(t, index % 2); Grid.SetRow(t, index / 2); if (index / 2 >= tiles.RowDefinitions.Count) tiles.RowDefinitions.Add(new RowDefinition()); tiles.Children.Add(t); index++; }

        var net = Host.Status.Network;
        if (radios.FirstOrDefault(r => r.Kind == RadioKind.WiFi) is { } wifi)
        {
            var t = new ToggleTile(Glyphs.Wifi, "Wi-Fi", wifi.On ? (net.IsWifi ? net.Name : "On") : "Off", wifi.On);
            t.Toggled += async on => { if (!await Radios.SetAsync(RadioKind.WiFi, on)) { t.On = !on; t.StateText = "Not allowed"; } else t.StateText = on ? "On" : "Off"; };
            Add(t);
        }
        else if (net.IsEthernet) Add(new ToggleTile(Glyphs.Ethernet, "Network", net.Connected ? net.Name : "Disconnected", net.Connected) { IsEnabled = false });
        if (radios.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth) is { } bt)
        {
            var t = new ToggleTile(Glyphs.Bluetooth, "Bluetooth", bt.On ? "On" : "Off", bt.On);
            t.Toggled += async on => { if (!await Radios.SetAsync(RadioKind.Bluetooth, on)) { t.On = !on; t.StateText = "Not allowed"; } else t.StateText = on ? "On" : "Off"; };
            Add(t);
        }
        var focus = new ToggleTile(Glyphs.Focus, "Focus", Host.FocusOn ? "Only priority alerts" : "Off", Host.FocusOn);
        focus.Toggled += on => { Host.SetFocus(on); focus.StateText = on ? "Only priority alerts" : "Off"; };
        Add(focus);
        var dark = new ToggleTile(Glyphs.Moon, "Dark theme", ThemeService.Current.IsDark ? "On" : "Off", ThemeService.Current.IsDark);
        dark.Toggled += on => { _ = Host.Commands.RunAsync(on ? "theme:dark" : "theme:light"); dark.StateText = on ? "On" : "Off"; };
        Add(dark);
        var q = VisualQuality.Current;
        var perf = new ToggleTile(Glyphs.Speed, "Performance", s.Performance.Profile == PerformanceProfile.Adaptive ? $"Adaptive · {q.Profile}" : s.Performance.Profile.ToString(), s.Performance.Profile != PerformanceProfile.Adaptive);
        perf.Toggled += _ =>
        {
            var next = (PerformanceProfile)(((int)Host.Settings.Current.Performance.Profile + 1) % 5);
            Host.Settings.Update(x => x.Performance.Profile = next);
            perf.On = next != PerformanceProfile.Adaptive;
            perf.StateText = next == PerformanceProfile.Adaptive ? $"Adaptive · {VisualQuality.Current.Profile}" : next.ToString();
        };
        Add(perf);
        var immersive = new ToggleTile(Glyphs.Desktop, "Immersive", s.Shell.Mode == ShellMode.Immersive ? "Full V-OS desktop" : "Off", s.Shell.Mode == ShellMode.Immersive);
        immersive.Toggled += async on =>
        {
            if (on && !await Dialogs.ConfirmAsync(Root.XamlRoot, "Turn on immersive mode?", "V-OS replaces the Windows taskbar and desktop icons with its own desktop. Everything is reversible: turn it off here, press Ctrl+Alt+Shift+F12, or use V-OS Launcher › Recovery.", "Turn on"))
            { immersive.On = false; return; }
            Host.Settings.Update(x => x.Shell.Mode = on ? ShellMode.Immersive : ShellMode.Companion);
            immersive.StateText = on ? "Full V-OS desktop" : "Off";
        };
        Add(immersive);
        _root.Children.Add(tiles);

        if (Host.Audio is { Available: true } audio)
        {
            _root.Children.Add(SliderRow(audio.Muted ? Glyphs.VolumeMute : Glyphs.Volume3, "Volume", audio.Volume * 100, v => { audio.Muted = false; audio.Volume = (float)(v / 100); }, () => audio.Muted = !audio.Muted));
            var devices = audio.Devices();
            if (devices.Count > 1)
            {
                var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = devices.Select(d => d.Name).ToList(), SelectedIndex = devices.FindIndex(d => d.IsDefault) };
                combo.SelectionChanged += (_, _) => { if (combo.SelectedIndex >= 0 && !audio.SetDefaultDevice(devices[combo.SelectedIndex].Id)) Host.Commands.RunAsync("ms-settings:sound").GetAwaiter(); };
                Buttons.AutomationName(combo, "Output device");
                _root.Children.Add(combo);
            }
        }
        if (brightness is int b) _root.Children.Add(SliderRow(Glyphs.Brightness, "Brightness", b, v => _ = Task.Run(() => Brightness.Set((int)v)), null));

        var media = Host.Media.Current;
        if (media.HasSession && media.Title.Length > 0)
        {
            var card = new Grid { Padding = new Thickness(12), ColumnSpacing = 12, CornerRadius = new CornerRadius(Tokens.RadiusCard), Background = Ui.B("Vos.Control") };
            card.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            card.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(Ui.Text(media.Title, 13.5, "Vos.TextPrimary", Tokens.Text, FontWeights.SemiBold));
            info.Children.Add(Ui.Text(media.Artist, 12, "Vos.TextSecondary"));
            card.Children.Add(info);
            var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
            controls.Children.Add(Buttons.Icon(Glyphs.Previous, () => _ = Host.Media.PreviousAsync(), 32, 13, "Previous"));
            controls.Children.Add(Buttons.Icon(media.IsPlaying ? Glyphs.Pause : Glyphs.Play, () => { _ = Host.Media.PlayPauseAsync(); }, 32, 14, "Play or pause"));
            controls.Children.Add(Buttons.Icon(Glyphs.Next, () => _ = Host.Media.NextAsync(), 32, 13, "Next"));
            Grid.SetColumn(controls, 1);
            card.Children.Add(controls);
            _root.Children.Add(card);
        }

        var battery = Host.Status.Battery;
        if (battery.Present)
        {
            string line = $"{battery.Percent}% · {(battery.Charging ? "Charging" : battery.OnAc ? "Plugged in" : battery.Remaining is { } r ? Fmt.DurationWords(r) + " left" : "On battery")}" + (battery.Saver ? " · Battery saver" : "");
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            row.Children.Add(Ui.Icon(battery.Charging ? Glyphs.BatteryCharging : Glyphs.Battery, 15, "Vos.TextSecondary"));
            row.Children.Add(Ui.Text(line, 12.5, "Vos.TextSecondary"));
            _root.Children.Add(row);
        }

        var footer = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        var settingsButton = Buttons.Subtle(Ui.Text("All settings", 12.5, "Vos.AccentText"), () => { Dismiss(); Host.Commands.OpenApp("settings"); }, 8, new Thickness(8, 6, 8, 6));
        footer.Children.Add(settingsButton);
        var power = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, HorizontalAlignment = HorizontalAlignment.Right };
        power.Children.Add(Buttons.Icon(Glyphs.Lock, () => { Dismiss(); LockPresentation.Lock(Host); }, 32, 14, "Lock"));
        power.Children.Add(Buttons.Icon(Glyphs.Power, () => { Dismiss(); Host.Panels.Show(PanelKind.VMenu); }, 32, 14, "Power"));
        footer.Children.Add(power);
        _root.Children.Add(footer);
    }

    private static Grid SliderRow(string glyph, string name, double value, Action<double> changed, Action? glyphClick)
    {
        var g = new Grid { ColumnSpacing = 10 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var icon = Buttons.Icon(glyph, glyphClick, 32, 15, name);
        g.Children.Add(icon);
        var slider = new Slider { Minimum = 0, Maximum = 100, Value = Math.Round(value), VerticalAlignment = VerticalAlignment.Center, StepFrequency = 1 };
        Buttons.AutomationName(slider, name);
        slider.ValueChanged += (_, e) => changed(e.NewValue);
        Grid.SetColumn(slider, 1);
        g.Children.Add(slider);
        return g;
    }
}

/// <summary>V-Activity: today at a glance, running timers and the notification timeline.</summary>
public sealed class ActivityPanel : PanelWindow
{
    private readonly StackPanel _root = new() { Padding = new Thickness(18, 18, 18, 12), Spacing = 14 };

    public ActivityPanel(ShellHost host) : base(host, "V-OS Activity", 380, 640)
    {
        Placement = PanelPlacement.TopRight;
        Body.Children.Add(new ScrollViewer { Content = _root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        host.Notifications.Changed += () => UiThread.Post(() => { if (IsOpen) Build(); });
    }

    protected override void OnOpening()
    {
        var work = Displays.Primary.WorkArea;
        PanelHeight = Math.Min(720, work.Height / Displays.Primary.Scale - 20 - ShadowMargin * 2);
        Build();
    }

    protected override void OnClosing() => Host.Notifications.MarkAllRead();

    private void Build()
    {
        _root.Children.Clear();
        var now = DateTime.Now;
        var head = new StackPanel { Spacing = 0 };
        head.Children.Add(Ui.Text(now.ToString("dddd", CultureInfo.CurrentCulture), 30, "Vos.TextPrimary", Tokens.Instrument, FontWeights.Light));
        head.Children.Add(Ui.Text(now.ToString("d MMMM yyyy", CultureInfo.CurrentCulture), 13, "Vos.TextSecondary"));
        _root.Children.Add(head);

        if (Host.Weather.Current is { } w)
        {
            var unit = Host.Settings.Current.Weather.TemperatureUnit;
            var row = new Grid { Padding = new Thickness(14, 12, 14, 12), ColumnSpacing = 12, CornerRadius = new CornerRadius(Tokens.RadiusCard), Background = Ui.B("Vos.Control") };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new WeatherIcon(w.Now.Condition, w.Now.IsDay, 30, Ui.B("Vos.Accent")));
            var t = new StackPanel();
            t.Children.Add(Ui.Text($"{WeatherFormat.Temperature(w.Now.TemperatureC, unit)}  {WeatherCodes.Describe(w.Now.Condition, w.Now.IsDay)}", 14, "Vos.TextPrimary", Tokens.Text, FontWeights.SemiBold));
            if (w.Today is { } d) t.Children.Add(Ui.Text($"{w.Location.Name} · H {WeatherFormat.Temperature(d.MaxC, unit)} L {WeatherFormat.Temperature(d.MinC, unit)}" + (w.Stale ? " · offline" : ""), 12, "Vos.TextSecondary"));
            Grid.SetColumn(t, 1);
            row.Children.Add(t);
            row.Tapped += (_, _) => { Dismiss(); Host.Commands.OpenApp("weather"); };
            _root.Children.Add(row);
        }

        var timers = Host.Timers.List().Where(x => x.State is TimerState.Running or TimerState.Paused).ToList();
        if (timers.Count > 0)
        {
            _root.Children.Add(Header("Timers"));
            foreach (var t in timers)
            {
                var row = new Grid { ColumnSpacing = 10 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(Ui.Text($"{t.Label}", 13));
                var rem = Ui.Text(Fmt.Duration(t.Remaining(DateTimeOffset.Now)), 14, "Vos.TextPrimary", Tokens.Instrument, FontWeights.SemiBold);
                Grid.SetColumn(rem, 1);
                row.Children.Add(rem);
                _root.Children.Add(row);
            }
        }

        var items = Host.Notifications.Items;
        var header = new Grid();
        header.Children.Add(Header("Notifications"));
        if (items.Count > 0)
        {
            var clear = Buttons.Subtle(Ui.Text("Clear all", 12, "Vos.AccentText"), () => Host.Notifications.ClearAll(), 6, new Thickness(6, 2, 6, 2));
            clear.HorizontalAlignment = HorizontalAlignment.Right;
            header.Children.Add(clear);
        }
        _root.Children.Add(header);
        if (items.Count == 0)
        {
            var empty = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 30, 0, 30) };
            empty.Children.Add(Ui.Icon(Glyphs.Check, 22, "Vos.TextTertiary"));
            empty.Children.Add(Ui.Text("You’re all caught up", 13, "Vos.TextSecondary"));
            _root.Children.Add(empty);
            return;
        }
        foreach (var group in items.GroupBy(n => n.Time.Date).Take(7))
        {
            _root.Children.Add(Ui.Text(group.Key == DateTime.Today ? "Today" : group.Key == DateTime.Today.AddDays(-1) ? "Yesterday" : group.Key.ToString("dddd d MMMM", CultureInfo.CurrentCulture), 11.5, "Vos.TextTertiary"));
            foreach (var n in group.Take(40)) _root.Children.Add(Card(n));
        }
    }

    private Grid Card(VosNotification n)
    {
        var g = new Grid { Padding = new Thickness(12, 10, 8, 10), ColumnSpacing = 12, CornerRadius = new CornerRadius(Tokens.RadiusCard), Background = Ui.B(n.Read ? "Vos.Control" : "Vos.AccentSubtle") };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var app = VosApp.Find(n.AppId);
        g.Children.Add(app != null ? new AppIconView(app, 26) : new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(8), Background = Ui.B("Vos.Control"), Child = Ui.Icon(n.Glyph.Length > 0 ? n.Glyph : Glyphs.Bell, 12, "Vos.TextSecondary") });
        var text = new StackPanel { Spacing = 1 };
        var top = new Grid();
        top.Children.Add(Ui.Text(n.AppName, 11.5, "Vos.TextTertiary"));
        var time = Ui.Text(Fmt.Relative(n.Time), 11.5, "Vos.TextTertiary");
        time.HorizontalAlignment = HorizontalAlignment.Right;
        top.Children.Add(time);
        text.Children.Add(top);
        text.Children.Add(Ui.Text(n.Title, 13.5, "Vos.TextPrimary", Tokens.Text, FontWeights.SemiBold));
        if (n.Body.Length > 0) text.Children.Add(Ui.Text(n.Body, 12.5, "Vos.TextSecondary", null, null, TextWrapping.Wrap));
        Grid.SetColumn(text, 1);
        g.Children.Add(text);
        var close = Buttons.Icon(Glyphs.Close, () => Host.Notifications.Dismiss(n.Id), 26, 10, "Dismiss");
        close.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(close, 2);
        g.Children.Add(close);
        if (n.Command != null) g.Tapped += (_, e) => { if (e.OriginalSource is not FontIcon) { Dismiss(); _ = Host.Commands.RunAsync(n.Command, Root.XamlRoot); } };
        return g;
    }
}
