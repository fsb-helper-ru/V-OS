using System.Diagnostics;
using System.Globalization;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using VOS.Core;
using VOS.Platform.Desktop;
using VOS.Platform.Native;
using VOS.Platform.Shell;
using VOS.Platform.Sys;
using VOS.Platform.Windowing;
using VOS.UI;
using VOS.UI.Surfaces;
using VOS.UI.Theme;
using Windows.Graphics;
using AppBar = VOS.Platform.Desktop.AppBar;

namespace VOS.Shell;

/// <summary>The top bar: identity, active app, workspace, status and time. Registered as an AppBar so windows do not cover it.</summary>
public sealed class TopBar : SurfaceWindow, IDisposable
{
    public const double Height = 34;
    private readonly ShellHost _host;
    private readonly AppBar _appBar;
    private readonly TextBlock _appName, _workspaceName, _date, _time;
    private readonly FontIcon _workspaceGlyph, _net, _vol, _battery, _focus, _bell;
    private readonly TextBlock _batteryText;
    private readonly Border _bellDot;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _clock;
    private static readonly Dictionary<string, string> s_descriptions = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Raised when a fullscreen app (game, video, presentation) starts or ends on this monitor.</summary>
    public event Action<bool>? FullscreenApp;

    public TopBar(ShellHost host) : base("V-OS Top Bar", topmost: true, noActivate: true)
    {
        _host = host;
        var glass = new GlassSurface(0, thin: true);
        Root.Children.Add(glass);
        Root.Children.Add(new Border { BorderThickness = new Thickness(0, 0, 0, 1), BorderBrush = Ui.B("Vos.StrokeSubtle") });

        var grid = new Grid { Padding = new Thickness(8, 0, 10, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Left: V menu, active app, workspace.
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        var mark = new TextBlock { Text = "V", FontFamily = Tokens.Instrument, FontWeight = FontWeights.SemiBold, FontSize = 15, Foreground = Ui.B("Vos.Accent") };
        var vButton = Buttons.Subtle(mark, () => host.Panels.Toggle(PanelKind.VMenu), 7, new Thickness(9, 2, 9, 3));
        Buttons.AutomationName(vButton, "V-OS menu");
        ToolTipService.SetToolTip(vButton, "V-OS");
        left.Children.Add(vButton);
        _appName = Ui.Text("Desktop", 13, "Vos.TextPrimary", Tokens.Text, FontWeights.SemiBold);
        _appName.MaxWidth = 280;
        _appName.VerticalAlignment = VerticalAlignment.Center;
        _appName.Margin = new Thickness(6, 0, 10, 1);
        left.Children.Add(_appName);
        _workspaceGlyph = Ui.Icon(Glyphs.Layers, 12, "Vos.TextSecondary");
        _workspaceName = Ui.Text("Personal", 12, "Vos.TextSecondary");
        var wsContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        wsContent.Children.Add(_workspaceGlyph);
        wsContent.Children.Add(_workspaceName);
        var wsButton = Buttons.Subtle(wsContent, () => host.Panels.Toggle(PanelKind.Workspaces), 11, new Thickness(10, 3, 10, 4));
        Buttons.AutomationName(wsButton, "Workspaces");
        left.Children.Add(wsButton);
        grid.Children.Add(left);

        // Right: status cluster (opens the control center), bell, clock (opens activity).
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        var status = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        _focus = Ui.Icon(Glyphs.Moon, 13, "Vos.Accent");
        _net = Ui.Icon(Glyphs.Wifi, 14);
        _vol = Ui.Icon(Glyphs.Volume3, 14);
        _battery = Ui.Icon("", 15);
        _batteryText = Ui.Text("", 12, "Vos.TextSecondary");
        _batteryText.VerticalAlignment = VerticalAlignment.Center;
        status.Children.Add(_focus);
        status.Children.Add(_net);
        status.Children.Add(_vol);
        var bat = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        bat.Children.Add(_battery);
        bat.Children.Add(_batteryText);
        status.Children.Add(bat);
        var statusButton = Buttons.Subtle(status, () => host.Panels.Toggle(PanelKind.Control), 11, new Thickness(10, 4, 10, 4));
        Buttons.AutomationName(statusButton, "Quick settings");
        right.Children.Add(statusButton);

        var bellGrid = new Grid();
        _bell = Ui.Icon("", 14);
        _bellDot = new Border { Width = 6, Height = 6, CornerRadius = new CornerRadius(3), Background = Ui.B("Vos.Accent"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -1, -3, 0) };
        bellGrid.Children.Add(_bell);
        bellGrid.Children.Add(_bellDot);
        var bellButton = Buttons.Subtle(bellGrid, () => host.Panels.Toggle(PanelKind.Activity), 11, new Thickness(9, 4, 9, 4));
        Buttons.AutomationName(bellButton, "Notifications");
        right.Children.Add(bellButton);

        var clock = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        _date = Ui.Text("", 12.5, "Vos.TextSecondary");
        _time = Ui.Text("", 13.5, "Vos.TextPrimary", Tokens.Instrument, FontWeights.SemiBold);
        _date.VerticalAlignment = _time.VerticalAlignment = VerticalAlignment.Center;
        clock.Children.Add(_date);
        clock.Children.Add(_time);
        var clockButton = Buttons.Subtle(clock, () => host.Panels.Toggle(PanelKind.Activity), 11, new Thickness(10, 3, 8, 4));
        Buttons.AutomationName(clockButton, "Date and time");
        right.Children.Add(clockButton);
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);
        Root.Children.Add(grid);

        _appBar = new AppBar(Hwnd);
        _appBar.Positioned += r => MoveAndResize(new RectInt32(r.Left, r.Top, r.Width, r.Height));
        _appBar.FullscreenAppChanged += fs => UiThread.Post(() => FullscreenApp?.Invoke(fs));
        _clock = UiThread.Timer(TimeSpan.FromSeconds(1), Tick);

        host.Status.NetworkChanged += _ => UiThread.Post(UpdateStatus);
        host.Status.BatteryChanged += _ => UiThread.Post(UpdateStatus);
        if (host.Audio != null) host.Audio.VolumeChanged += (_, _) => UiThread.Post(UpdateStatus);
        host.FocusChanged += UpdateStatus;
        host.Notifications.Changed += () => UiThread.Post(UpdateStatus);
        Reposition();
        Refresh();
        Tick();
    }

    public void Reposition()
    {
        var mon = Displays.Primary;
        int thickness = (int)Math.Round(Height * mon.Scale);
        if (_host.Settings.Current.Shell.TopBarReservesSpace) _appBar.Register(Win32.ABE_TOP, thickness, mon.Bounds);
        else
        {
            _appBar.Unregister();
            MoveAndResize(new RectInt32(mon.Bounds.Left, mon.Bounds.Top, mon.Bounds.Width, thickness));
        }
    }

    public void Refresh()
    {
        var ws = _host.Settings.Current.ActiveWorkspace;
        _workspaceName.Text = ws?.Name ?? "Personal";
        _workspaceGlyph.Glyph = WorkspaceGlyph(ws?.Icon);
        UpdateStatus();
        OnForegroundChanged();
        Tick();
    }

    public static string WorkspaceGlyph(string? icon) => icon switch
    {
        "home" => Glyphs.Home, "briefcase" => Glyphs.Briefcase, "book" => Glyphs.Book, "gamepad" => Glyphs.Game,
        "music" => Glyphs.Music, "leaf" => Glyphs.Leaf, _ => Glyphs.Layers,
    };

    private void Tick()
    {
        var now = DateTime.Now;
        var s = _host.Settings.Current.Shell;
        _time.Text = now.ToString(s.Use24Hour ? (s.ShowSeconds ? "HH:mm:ss" : "HH:mm") : (s.ShowSeconds ? "h:mm:ss tt" : "h:mm tt"), CultureInfo.CurrentCulture);
        _date.Text = now.ToString("ddd d MMM", CultureInfo.CurrentCulture);
        // Wake exactly at the next minute boundary (or every second when seconds are shown): no needless timer churn.
        _clock.Stop();
        _clock.Interval = s.ShowSeconds ? TimeSpan.FromMilliseconds(1000 - now.Millisecond + 5) : TimeSpan.FromSeconds(60 - now.Second + 0.05 - now.Millisecond / 1000.0);
        _clock.Start();
    }

    private void UpdateStatus()
    {
        var n = _host.Status.Network;
        _net.Glyph = n.IsEthernet ? Glyphs.Ethernet : Glyphs.Wifi;
        _net.Opacity = n.Connected ? 1 : 0.35;
        ToolTipService.SetToolTip(_net, n.Connected ? n.Name : "Not connected");
        if (_host.Audio != null)
        {
            float v = _host.Audio.Volume;
            _vol.Glyph = _host.Audio.Muted || v < 0.01 ? Glyphs.VolumeMute : v < 0.34 ? Glyphs.Volume1 : v < 0.67 ? Glyphs.Volume2 : Glyphs.Volume3;
        }
        var b = _host.Status.Battery;
        _battery.Visibility = _batteryText.Visibility = b.Present ? Visibility.Visible : Visibility.Collapsed;
        if (b.Present)
        {
            int level = Math.Clamp((int)Math.Round(b.Percent / 10.0), 0, 10);
            _battery.Glyph = ((char)((b.Charging ? 0xEBAB : 0xEBA0) + level)).ToString();
            _batteryText.Text = $"{b.Percent}%";
        }
        _focus.Visibility = _host.FocusOn ? Visibility.Visible : Visibility.Collapsed;
        _bellDot.Visibility = _host.Notifications.Unread > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Shows the name of the app in front (V-OS apps by their V-OS name, others by product name).</summary>
    public void OnForegroundChanged()
    {
        var fg = _host.Tracker.Foreground;
        string name = "Desktop";
        if (fg != IntPtr.Zero && WindowTracker.IsAppWindow(fg))
        {
            var appId = ShellNative.WindowAppId(fg);
            if (appId?.StartsWith("VOS.App.", StringComparison.Ordinal) == true) name = VosApp.Find(appId[8..])?.Name ?? name;
            else
            {
                Win32.GetWindowThreadProcessId(fg, out uint pid);
                var exe = Win32.GetProcessPath(pid);
                name = exe != null ? Describe(exe) : Win32.GetText(fg);
            }
        }
        _appName.Text = name;
    }

    private static string Describe(string exe)
    {
        if (s_descriptions.TryGetValue(exe, out var d)) return d;
        try
        {
            var info = FileVersionInfo.GetVersionInfo(exe);
            d = !string.IsNullOrWhiteSpace(info.FileDescription) && info.FileDescription.Length < 40 ? info.FileDescription : Path.GetFileNameWithoutExtension(exe);
        }
        catch { d = Path.GetFileNameWithoutExtension(exe); }
        return s_descriptions[exe] = d;
    }

    public void Dispose() => _appBar.Dispose();
}

