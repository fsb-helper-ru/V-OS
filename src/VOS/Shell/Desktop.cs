using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using VOS.Core;
using VOS.Core.Settings;
using VOS.Core.Timers;
using VOS.Core.Weather;
using VOS.Platform.Native;
using VOS.Platform.Sys;
using VOS.Platform.Windowing;
using VOS.UI;
using VOS.UI.Quality;
using VOS.UI.Surfaces;
using VOS.UI.Theme;
using Windows.Graphics;

namespace VOS.Shell;

/// <summary>
/// Immersive desktop: wallpaper or living ambient scene, hero clock and widgets. Kept at the bottom of the z-order;
/// all motion runs on the compositor and pauses when covered, locked or in fullscreen apps.
/// </summary>
public sealed class DesktopSurface : SurfaceWindow
{
    private readonly ShellHost _host;
    private readonly Grid _scene = new();
    private readonly Image _wallpaper = new() { Stretch = Stretch.UniformToFill };
    private readonly TextBlock _time, _date;
    private readonly StackPanel _widgets = new() { Spacing = 14, Width = 300, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
    private readonly List<SpriteVisual> _blobs = [];
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _minute, _widgetRefresh;
    private readonly WindowSubclass _bottom;
    private readonly MetricsSampler _metrics = new(includeGpuAndDisk: false);
    private ContainerVisual? _ambientRoot;
    private bool _paused;
    public string? WallpaperAccentHex { get; private set; }

    public DesktopSurface(ShellHost host) : base("V-OS Desktop", topmost: false, noActivate: true, backdrop: null)
    {
        _host = host;
        Root.Background = Ui.B("Vos.Base");
        Root.Children.Add(_scene);
        Root.Children.Add(_wallpaper);
        var content = new Grid { Padding = new Thickness(64, 72, 48, 140) };
        var hero = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        _time = new TextBlock { FontFamily = Tokens.Instrument, FontWeight = FontWeights.Light, FontSize = 112, Foreground = Ui.B("Vos.TextPrimary"), CharacterSpacing = -20, Opacity = 0.94 };
        _date = new TextBlock { FontFamily = Tokens.Display, FontSize = 20, Foreground = Ui.B("Vos.TextSecondary"), Margin = new Thickness(6, -6, 0, 0) };
        hero.Children.Add(_time);
        hero.Children.Add(_date);
        content.Children.Add(hero);
        content.Children.Add(_widgets);
        Root.Children.Add(content);

        // Keep the desktop under every other window.
        _bottom = new WindowSubclass(Hwnd, (msg, w, l) =>
        {
            if (msg == 0x0046 /*WM_WINDOWPOSCHANGING*/)
            {
                uint flags = (uint)Marshal.ReadInt32(l, IntPtr.Size * 2 + 16);
                if ((flags & Win32.SWP_NOZORDER) == 0) Marshal.WriteIntPtr(l, IntPtr.Size, Win32.HWND_BOTTOM);
            }
            return null;
        });

        _minute = UiThread.Timer(TimeSpan.FromSeconds(1), Tick);
        _widgetRefresh = UiThread.Timer(TimeSpan.FromSeconds(5), BuildWidgets);
        host.Weather.Changed += () => UiThread.Post(() => { BuildWidgets(); ApplyAmbientTint(); });
        host.Media.Changed += _ => UiThread.Post(BuildWidgets);
        host.Timers.Changed += () => UiThread.Post(BuildWidgets);
        VisualQuality.Changed += () => UiThread.Post(ApplySettings);
        ThemeService.Current.Changed += ApplyAmbientTint;
        Reposition();
        ApplySettings();
        Tick();
    }

    public void Reposition()
    {
        var m = Displays.Primary;
        MoveAndResize(new RectInt32(m.Bounds.Left, m.Bounds.Top, m.Bounds.Width, m.Bounds.Height));
        Win32.SetWindowPos(Hwnd, Win32.HWND_BOTTOM, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
    }

    public void Show()
    {
        base.Show(activate: false);
        Reposition();
        _widgetRefresh.Start();
    }

    public new void Hide()
    {
        _widgetRefresh.Stop();
        base.Hide();
    }

    public void ApplySettings()
    {
        var s = _host.Settings.Current;
        var wallpaper = s.ActiveWorkspace?.Wallpaper ?? s.Appearance.Wallpaper;
        if (wallpaper.StartsWith("ambient:", StringComparison.Ordinal) && VisualQuality.Current.Ambient)
        {
            _wallpaper.Visibility = Visibility.Collapsed;
            WallpaperAccentHex = null;
            BuildAmbient(wallpaper[8..]);
        }
        else if (File.Exists(wallpaper))
        {
            StopAmbient();
            _wallpaper.Source = new BitmapImage(new Uri(wallpaper)) { DecodePixelWidth = (int)(Displays.Primary.Bounds.Width) };
            _wallpaper.Visibility = Visibility.Visible;
            _ = Task.Run(async () =>
            {
                var c = await ArtworkColor.ExtractAsync(await File.ReadAllBytesAsync(wallpaper));
                WallpaperAccentHex = c is { } col ? Colors2.ToHex(col) : null;
            });
        }
        else
        {
            StopAmbient();
            _wallpaper.Visibility = Visibility.Collapsed;
            if (wallpaper.StartsWith("color:", StringComparison.Ordinal)) { try { Root.Background = new SolidColorBrush(Colors2.Hex(wallpaper[6..])); } catch { } }
            else BuildAmbient("still");
        }
        _time.Visibility = _date.Visibility = s.Desktop.ShowHeroClock ? Visibility.Visible : Visibility.Collapsed;
        _widgets.Visibility = s.Desktop.ShowWidgets ? Visibility.Visible : Visibility.Collapsed;
        BuildWidgets();
    }

    public void SetPaused(bool paused)
    {
        _paused = paused;
        foreach (var b in _blobs)
        {
            var ctrl = b.TryGetAnimationController("Offset");
            if (ctrl == null) continue;
            if (paused) ctrl.Pause(); else ctrl.Resume();
        }
    }

    private void Tick()
    {
        var now = DateTime.Now;
        var s = _host.Settings.Current.Shell;
        _time.Text = now.ToString(s.Use24Hour ? "HH:mm" : "h:mm", CultureInfo.CurrentCulture);
        _date.Text = now.ToString("dddd, d MMMM", CultureInfo.CurrentCulture);
        _minute.Stop();
        _minute.Interval = TimeSpan.FromSeconds(60 - now.Second + 0.05);
        _minute.Start();
    }

    // ---------------- ambient scene ----------------

    private void StopAmbient()
    {
        _blobs.Clear();
        ElementCompositionPreview.SetElementChildVisual(_scene, null);
        _ambientRoot = null;
    }

    /// <summary>Large soft colour fields drifting slowly. Zero CPU: compositor keyframes only.</summary>
    private void BuildAmbient(string scene)
    {
        StopAmbient();
        var c = ElementCompositionPreview.GetElementVisual(_scene).Compositor;
        _ambientRoot = c.CreateContainerVisual();
        var m = Displays.Primary;
        float w = (float)(m.Bounds.Width / m.Scale), h = (float)(m.Bounds.Height / m.Scale);
        _ambientRoot.Size = new Vector2(w, h);
        int count = scene == "still" ? 3 : 5;
        var rnd = new Random(scene.GetHashCode());
        for (int i = 0; i < count; i++)
        {
            var brush = c.CreateRadialGradientBrush();
            brush.ColorStops.Add(c.CreateColorGradientStop(0f, Colors2.WithAlpha(BlobColor(i, scene), 0xB0)));
            brush.ColorStops.Add(c.CreateColorGradientStop(1f, Colors2.WithAlpha(BlobColor(i, scene), 0)));
            var blob = c.CreateSpriteVisual();
            float size = (float)(Math.Max(w, h) * (0.55 + rnd.NextDouble() * 0.35));
            blob.Size = new Vector2(size, size);
            blob.Brush = brush;
            var start = new Vector3((float)(rnd.NextDouble() * w - size / 2), (float)(rnd.NextDouble() * h - size / 2), 0);
            blob.Offset = start;
            _ambientRoot.Children.InsertAtTop(blob);
            _blobs.Add(blob);
            if (VisualQuality.Current.AmbientAnimated && scene != "still")
            {
                var a = c.CreateVector3KeyFrameAnimation();
                a.InsertKeyFrame(0.5f, start + new Vector3((float)(rnd.NextDouble() * w * 0.4 - w * 0.2), (float)(rnd.NextDouble() * h * 0.4 - h * 0.2), 0), Motion.Standard(c));
                a.InsertKeyFrame(1f, start, Motion.Standard(c));
                a.Duration = TimeSpan.FromSeconds(38 + rnd.Next(30));
                a.IterationBehavior = AnimationIterationBehavior.Forever;
                blob.StartAnimation("Offset", a);
            }
        }
        ElementCompositionPreview.SetElementChildVisual(_scene, _ambientRoot);
        if (_paused) SetPaused(true);
    }

    private global::Windows.UI.Color BlobColor(int i, string scene)
    {
        var accent = ThemeService.Current.Accent;
        var p = ThemeService.Current.Palette;
        global::Windows.UI.Color[] set = scene switch
        {
            "nebula" => [Colors2.Hex("#6A3FD9"), Colors2.Hex("#C2386A"), accent, Colors2.Hex("#1F6FD1"), Colors2.Hex("#2A1B4F")],
            "horizon" => [Colors2.Hex("#FF9466"), Colors2.Hex("#C9541F"), Colors2.Hex("#6FB4FF"), accent, Colors2.Hex("#4B2A55")],
            "still" => [Colors2.Mix(accent, p.Base, 0.65), Colors2.Mix(accent, p.Base, 0.8), Colors2.Mix(accent, p.Base, 0.75)],
            _ => [accent, Colors2.Hex("#6FE0C0"), Colors2.Hex("#4C5FD6"), Colors2.Mix(accent, Colors2.Hex("#B79CFF"), 0.5), Colors2.Hex("#1F6FD1")],
        };
        var c = set[i % set.Length];
        return p.IsDark ? Colors2.Darken(c, 0.15) : Colors2.Lighten(c, 0.35);
    }

    private void ApplyAmbientTint()
    {
        if (_ambientRoot == null) return;
        var s = _host.Settings.Current;
        BuildAmbient((s.ActiveWorkspace?.Wallpaper ?? s.Appearance.Wallpaper) is var wp && wp.StartsWith("ambient:", StringComparison.Ordinal) ? wp[8..] : "still");
    }

    // ---------------- widgets ----------------

    private void BuildWidgets()
    {
        if (!IsVisible) return;
        _widgets.Children.Clear();
        var enabled = _host.Settings.Current.Desktop.Widgets?.Where(w => w.Enabled).OrderBy(w => w.Order).Select(w => w.Kind).ToList() ?? [];
        foreach (var kind in enabled)
        {
            var card = kind switch
            {
                "weather" => WeatherWidget(),
                "media" => MediaWidget(),
                "agenda" => TimersWidget(),
                "system" => SystemWidget(),
                _ => null,
            };
            if (card != null) _widgets.Children.Add(card);
        }
    }

    private static Grid Card(UIElement content)
    {
        var g = new Grid();
        g.Children.Add(new GlassSurface(Tokens.RadiusPanel));
        var inner = new Grid { Padding = new Thickness(18, 16, 18, 16) };
        inner.Children.Add(content);
        g.Children.Add(inner);
        return g;
    }

    private Grid? WeatherWidget()
    {
        var w = _host.Weather.Current;
        if (w == null) return null;
        var unit = _host.Settings.Current.Weather.TemperatureUnit;
        var s = new StackPanel { Spacing = 4 };
        var top = new Grid();
        top.Children.Add(Ui.Text(w.Location.Name, 13, "Vos.TextSecondary"));
        var icon = new WeatherIcon(w.Now.Condition, w.Now.IsDay, 26, Ui.B("Vos.Accent"));
        icon.HorizontalAlignment = HorizontalAlignment.Right;
        top.Children.Add(icon);
        s.Children.Add(top);
        s.Children.Add(Ui.Text(WeatherFormat.Temperature(w.Now.TemperatureC, unit), 44, "Vos.TextPrimary", Tokens.Instrument, FontWeights.Light));
        s.Children.Add(Ui.Text(WeatherCodes.Describe(w.Now.Condition, w.Now.IsDay) + (w.Today is { } t ? $" · H {WeatherFormat.Temperature(t.MaxC, unit)}  L {WeatherFormat.Temperature(t.MinC, unit)}" : ""), 12.5, "Vos.TextSecondary"));
        var card = Card(s);
        card.Tapped += (_, _) => _host.Commands.OpenApp("weather");
        return card;
    }

    private Grid? MediaWidget()
    {
        var m = _host.Media.Current;
        if (!m.HasSession || m.Title.Length == 0) return null;
        var s = new StackPanel { Spacing = 2 };
        s.Children.Add(Ui.Text("Now playing", 11.5, "Vos.TextTertiary"));
        s.Children.Add(Ui.Text(m.Title, 15, "Vos.TextPrimary", Tokens.Text, FontWeights.SemiBold));
        s.Children.Add(Ui.Text(m.Artist, 12.5, "Vos.TextSecondary"));
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(-6, 6, 0, 0) };
        controls.Children.Add(Buttons.Icon(Glyphs.Previous, () => _ = _host.Media.PreviousAsync(), 32, 13, "Previous"));
        controls.Children.Add(Buttons.Icon(m.IsPlaying ? Glyphs.Pause : Glyphs.Play, () => _ = _host.Media.PlayPauseAsync(), 34, 15, "Play or pause"));
        controls.Children.Add(Buttons.Icon(Glyphs.Next, () => _ = _host.Media.NextAsync(), 32, 13, "Next"));
        s.Children.Add(controls);
        return Card(s);
    }

    private Grid? TimersWidget()
    {
        var timers = _host.Timers.List().Where(t => t.State is TimerState.Running or TimerState.Paused).ToList();
        if (timers.Count == 0) return null;
        var s = new StackPanel { Spacing = 6 };
        s.Children.Add(Ui.Text("Timers", 11.5, "Vos.TextTertiary"));
        foreach (var t in timers.Take(3))
        {
            var row = new Grid();
            row.Children.Add(Ui.Text(t.Label, 13));
            var r = Ui.Text(Fmt.Duration(t.Remaining(DateTimeOffset.Now)), 15, "Vos.TextPrimary", Tokens.Instrument, FontWeights.SemiBold);
            r.HorizontalAlignment = HorizontalAlignment.Right;
            row.Children.Add(r);
            s.Children.Add(row);
        }
        return Card(s);
    }

    private Grid SystemWidget()
    {
        var sample = _metrics.Sample();
        var s = new StackPanel { Spacing = 8 };
        s.Children.Add(Ui.Text("System", 11.5, "Vos.TextTertiary"));
        s.Children.Add(Meter("CPU", sample.CpuPercent));
        s.Children.Add(Meter("Memory", sample.MemoryPercent));
        var card = Card(s);
        card.Tapped += (_, _) => _host.Commands.OpenApp("monitor");
        return card;
    }

    private static Grid Meter(string name, double percent)
    {
        var g = new Grid { RowSpacing = 4 };
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.Children.Add(Ui.Text(name, 12.5));
        var v = Ui.Text($"{Math.Round(percent)}%", 12.5, "Vos.TextSecondary", Tokens.Instrument);
        v.HorizontalAlignment = HorizontalAlignment.Right;
        g.Children.Add(v);
        var track = new Grid { Height = 4, CornerRadius = new CornerRadius(2), Background = Ui.B("Vos.Control") };
        track.Children.Add(new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = Ui.B("Vos.Accent"), HorizontalAlignment = HorizontalAlignment.Left, Width = Math.Max(4, 264 * Math.Clamp(percent / 100, 0, 1)) });
        Grid.SetRow(track, 1);
        g.Children.Add(track);
        return g;
    }
}

/// <summary>Lock transition: a brief V-OS presentation, then Windows' own secure lock screen (never replaced or weakened).</summary>
public static class LockPresentation
{
    public static void Lock(ShellHost host)
    {
        var m = Displays.Primary;
        var w = new SurfaceWindow("V-OS Lock", topmost: true, noActivate: true, backdrop: null);
        w.Root.Background = Ui.B("Vos.Ink");
        var clock = new TextBlock { Text = DateTime.Now.ToString("HH:mm", CultureInfo.CurrentCulture), FontFamily = Tokens.Instrument, FontWeight = FontWeights.Light, FontSize = 120, Foreground = Ui.B("Vos.InkText"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        w.Root.Children.Add(clock);
        w.MoveAndResize(new RectInt32(m.Bounds.Left, m.Bounds.Top, m.Bounds.Width, m.Bounds.Height));
        w.Show();
        Motion.Fade(w.Root, 0, 0);
        Motion.Fade(w.Root, 1, 260);
        var t = UiThread.Timer(TimeSpan.FromMilliseconds(Motion.Enabled ? 420 : 10), () =>
        {
            VOS.Platform.Shell.ShellOps.Lock();
            UiThread.Timer(TimeSpan.FromSeconds(1.5), w.Close, repeating: false).Start();
        }, repeating: false);
        t.Start();
    }
}
