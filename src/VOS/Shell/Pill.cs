using System.Globalization;
using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using VOS.Core;
using VOS.Core.Notifications;
using VOS.Core.Settings;
using VOS.Core.Timers;
using VOS.Core.Weather;
using VOS.Platform.Media;
using VOS.Platform.Windowing;
using VOS.UI;
using VOS.UI.Quality;
using VOS.UI.Surfaces;
using VOS.UI.Theme;
using Windows.Graphics;

namespace VOS.Shell;

/// <summary>
/// V-Pill: the interactive capsule. Ink shape animated by compositor springs (ADR-015); content follows the most relevant
/// live state: banner &gt; activity &gt; timer &gt; media &gt; weather. Click to expand, click again (or wait) to collapse.
/// </summary>
public sealed class PillSurface : SurfaceWindow
{
    private const double W = 560, H = 240, Top = 4, CompactH = 26;
    private enum Mode { Idle, Media, Timer, Activity, Banner }

    private readonly ShellHost _host;
    private readonly Compositor _c;
    private readonly CompositionRoundedRectangleGeometry _geo;
    private readonly Grid _compact = new() { Height = CompactH, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, Top, 0, 0) };
    private readonly Grid _expanded = new() { VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _tick, _collapse, _bannerTimer;
    private readonly Queue<(VosNotification n, double seconds)> _banners = new();
    private (VosNotification n, double seconds)? _banner;
    private Mode _mode = Mode.Idle;
    private bool _isExpanded;
    private Vector2 _size = new(44, (float)CompactH);
    private string _artKey = "";
    private ImageSource? _art;
    private readonly List<UIElement> _bars = [];

    public PillSurface(ShellHost host) : base("V-OS Capsule", topmost: true, noActivate: true)
    {
        _host = host;
        var compositionHost = new Grid();
        Root.Children.Add(compositionHost);
        Root.Children.Add(_compact);
        Root.Children.Add(_expanded);
        _c = ElementCompositionPreview.GetElementVisual(compositionHost).Compositor;
        _geo = _c.CreateRoundedRectangleGeometry();
        _geo.Size = _size;
        _geo.CornerRadius = new Vector2((float)(CompactH / 2));
        var shape = _c.CreateSpriteShape(_geo);
        shape.FillBrush = _c.CreateColorBrush(Colors2.Hex("#0B0C0F"));
        shape.StrokeBrush = _c.CreateColorBrush(global::Windows.UI.Color.FromArgb(0x1C, 255, 255, 255));
        shape.StrokeThickness = 1;
        var visual = _c.CreateShapeVisual();
        visual.Size = new Vector2((float)W, (float)H);
        visual.Shapes.Add(shape);
        ElementCompositionPreview.SetElementChildVisual(compositionHost, visual);
        var offset = _c.CreateExpressionAnimation("Vector2((W - g.Size.X) * 0.5, T)");
        offset.SetReferenceParameter("g", _geo);
        offset.SetScalarParameter("W", (float)W);
        offset.SetScalarParameter("T", (float)Top);
        _geo.StartAnimation("Offset", offset);
        var radius = _c.CreateExpressionAnimation("Vector2(Min(g.Size.Y * 0.5, 28.0), Min(g.Size.Y * 0.5, 28.0))");
        radius.SetReferenceParameter("g", _geo);
        _geo.StartAnimation("CornerRadius", radius);

        Root.Tapped += (_, e) => { if (e.OriginalSource is not ButtonBase && FindButton(e.OriginalSource as DependencyObject) == null) ToggleExpanded(); };

        _tick = UiThread.Timer(TimeSpan.FromSeconds(1), OnTick);
        _collapse = UiThread.Timer(TimeSpan.FromSeconds(6), () => SetExpanded(false), repeating: false);
        _bannerTimer = UiThread.Timer(TimeSpan.FromSeconds(3), NextBanner, repeating: false);
        Root.PointerEntered += (_, _) => _collapse.Stop();
        Root.PointerExited += (_, _) => { if (_isExpanded && _banner == null) { _collapse.Stop(); _collapse.Start(); } };

        host.Media.Changed += m => UiThread.Post(() => { var pending = OnMediaAsync(); });
        host.Timers.Changed += () => UiThread.Post(Recompute);
        host.Activities.Changed += () => UiThread.Post(Recompute);
        host.Weather.Changed += () => UiThread.Post(Recompute);
        host.Notifications.Banner += n => UiThread.Post(() => EnqueueBanner(n, 4.5));
        if (host.Audio != null)
        {
            host.Audio.DefaultDeviceChanged += d => UiThread.Post(() => EnqueueBanner(new VosNotification { AppName = "Sound", Title = "Output changed", Body = d?.Name ?? "Default device", Glyph = Glyphs.Volume }, 2.6));
        }
        VisualQuality.Changed += () => { if (!VisualQuality.Current.Springs) Morph(_size, instant: true); };
        Reposition();
        Recompute();
        _ = OnMediaAsync();
    }

    private static ButtonBase? FindButton(DependencyObject? d)
    {
        while (d != null) { if (d is ButtonBase b) return b; d = VisualTreeHelper.GetParent(d); }
        return null;
    }

    public void Reposition()
    {
        var mon = Displays.Primary;
        double s = mon.Scale;
        int w = (int)(W * s), h = (int)(H * s);
        MoveAndResize(new RectInt32(mon.Bounds.Left + (mon.Bounds.Width - w) / 2, mon.Bounds.Top, w, h));
        UpdateRegion(_size);
    }

    // ---------------- state ----------------

    private void EnqueueBanner(VosNotification n, double seconds)
    {
        _banners.Enqueue((n, seconds));
        if (_banner == null) NextBanner();
    }

    private void NextBanner()
    {
        _banner = _banners.Count > 0 ? _banners.Dequeue() : null;
        if (_banner is { } b) { _bannerTimer.Interval = TimeSpan.FromSeconds(b.seconds); _bannerTimer.Start(); }
        Recompute();
    }

    private TimerItem? ActiveTimer => _host.Timers.List().Where(t => t.State is TimerState.Running or TimerState.Paused).OrderBy(t => t.Remaining(DateTimeOffset.Now)).FirstOrDefault();

    private void Recompute()
    {
        var media = _host.Media.Current;
        var timer = ActiveTimer;
        var activity = _host.Activities.Items.FirstOrDefault();
        _mode = _banner != null ? Mode.Banner
            : activity != null ? Mode.Activity
            : timer != null ? Mode.Timer
            : media.HasSession && (media.IsPlaying || media.Title.Length > 0) ? Mode.Media
            : Mode.Idle;
        BuildCompact(media, timer, activity);
        if (_mode == Mode.Banner) BuildExpanded(media, timer, activity, forceBanner: true);
        else if (_isExpanded) BuildExpanded(media, timer, activity);
        else _expanded.Children.Clear();
        bool ticking = _mode == Mode.Timer || (_isExpanded && _mode == Mode.Media && media.IsPlaying);
        if (ticking && !_tick.IsRunning) _tick.Start(); else if (!ticking) _tick.Stop();
        ApplySize();
    }

    private void OnTick()
    {
        if (_mode == Mode.Timer)
        {
            foreach (var done in _host.Timers.CollectFinished())
            {
                _host.Notifications.Post(new VosNotification { AppId = "time", AppName = "Time", Title = done.Label, Body = "Time is up.", Glyph = Glyphs.Timer, Priority = NotificationPriority.High, Command = "app:time" });
                Sound.Alert();
            }
        }
        Recompute();
    }

    private async Task OnMediaAsync()
    {
        var m = _host.Media.Current;
        if (m.ArtworkKey != _artKey)
        {
            _artKey = m.ArtworkKey;
            _art = await Images.FromBytesAsync(m.Artwork, 160);
            if (_host.Settings.Current.Appearance.AccentFollowsMedia)
                ThemeService.Current.SetMediaAccent(m.Artwork != null && m.IsPlaying ? await ArtworkColor.ExtractAsync(m.Artwork) : null);
        }
        Recompute();
    }

    private void ToggleExpanded()
    {
        if (_mode == Mode.Banner) { _bannerTimer.Stop(); NextBanner(); return; }
        SetExpanded(!_isExpanded);
    }

    private void SetExpanded(bool on)
    {
        _isExpanded = on;
        if (on) { _collapse.Stop(); _collapse.Start(); }
        Recompute();
    }

    // ---------------- content ----------------

    private TextBlock InkText(string s, double size, bool secondary = false, FontFamily? font = null, bool semibold = false) => new()
    {
        Text = s, FontSize = size, Foreground = Ui.B(secondary ? "Vos.InkTextSecondary" : "Vos.InkText"), FontFamily = font ?? Tokens.Text,
        FontWeight = semibold ? FontWeights.SemiBold : FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
    };

    private FontIcon InkIcon(string glyph, double size = 13, string brush = "Vos.InkText") => new() { Glyph = glyph, FontFamily = Tokens.Icons, FontSize = size, Foreground = Ui.B(brush), VerticalAlignment = VerticalAlignment.Center };

    private void BuildCompact(MediaState media, TimerItem? timer, LiveActivity? activity)
    {
        _compact.Children.Clear();
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(12, 0, 12, 0) };
        switch (_mode)
        {
            case Mode.Banner:
                break;
            case Mode.Activity:
                row.Children.Add(InkIcon(activity!.Glyph.Length > 0 ? activity.Glyph : Glyphs.Download, 12, "Vos.MediaAccent"));
                row.Children.Add(Limit(InkText(activity.Title, 12), 180));
                if (activity.Progress is double p) row.Children.Add(InkText(Fmt.Percent(p), 12, true, Tokens.Instrument));
                break;
            case Mode.Timer:
                var rem = timer!.Remaining(DateTimeOffset.Now);
                row.Children.Add(InkIcon(timer.State == TimerState.Paused ? Glyphs.Pause : Glyphs.Timer, 12, "Vos.Accent"));
                row.Children.Add(InkText(Fmt.Duration(rem), 13, false, Tokens.Instrument, true));
                row.Children.Add(Limit(InkText(timer.Label, 12, true), 110));
                break;
            case Mode.Media:
                if (_art != null) row.Children.Add(new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Background = new ImageBrush { ImageSource = _art, Stretch = Stretch.UniformToFill } });
                else row.Children.Add(InkIcon(Glyphs.Music, 12, "Vos.MediaAccent"));
                row.Children.Add(Limit(InkText(media.Artist.Length > 0 ? $"{media.Title}  ·  {media.Artist}" : media.Title, 12), 220));
                row.Children.Add(Bars(media.IsPlaying));
                break;
            default:
                var w = _host.Weather.Current;
                if (w != null)
                {
                    row.Children.Add(new WeatherIcon(w.Now.Condition, w.Now.IsDay, 15, Ui.B("Vos.InkText")) { VerticalAlignment = VerticalAlignment.Center });
                    row.Children.Add(InkText(WeatherFormat.Temperature(w.Now.TemperatureC, _host.Settings.Current.Weather.TemperatureUnit), 13, false, Tokens.Instrument, true));
                }
                else row.Children.Add(new Border { Width = 6, Height = 6, CornerRadius = new CornerRadius(3), Background = Ui.B("Vos.Accent") });
                break;
        }
        _compact.Children.Add(row);
        Buttons.AutomationName(_compact, _mode switch { Mode.Media => "Now playing", Mode.Timer => "Timer", Mode.Activity => "Activity", _ => "V-Pill" });
    }

    private static T Limit<T>(T e, double max) where T : FrameworkElement { e.MaxWidth = max; return e; }

    private UIElement Bars(bool playing)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Height = 12 };
        _bars.Clear();
        double[] durations = [0.62, 0.48, 0.71];
        for (int i = 0; i < 3; i++)
        {
            var bar = new Border { Width = 2.5, Height = 12, CornerRadius = new CornerRadius(1.25), Background = Ui.B("Vos.MediaAccent"), VerticalAlignment = VerticalAlignment.Bottom };
            panel.Children.Add(bar);
            _bars.Add(bar);
            var v = ElementCompositionPreview.GetElementVisual(bar);
            v.CenterPoint = new Vector3(1.25f, 12f, 0);
            if (playing && Motion.Enabled && VisualQuality.Current.AudioVisualizer)
            {
                var a = _c.CreateVector3KeyFrameAnimation();
                a.InsertKeyFrame(0f, new Vector3(1, 0.35f, 1));
                a.InsertKeyFrame(0.5f, new Vector3(1, 1f, 1));
                a.InsertKeyFrame(1f, new Vector3(1, 0.35f, 1));
                a.Duration = TimeSpan.FromSeconds(durations[i]);
                a.IterationBehavior = AnimationIterationBehavior.Forever;
                v.StartAnimation("Scale", a);
            }
            else v.Scale = new Vector3(1, playing ? 0.7f : 0.3f, 1);
        }
        return panel;
    }

    private void BuildExpanded(MediaState media, TimerItem? timer, LiveActivity? activity, bool forceBanner = false)
    {
        _expanded.Children.Clear();
        _expanded.Margin = new Thickness(0, Top, 0, 0);
        var g = new Grid { Padding = new Thickness(18, 14, 18, 14) };
        switch (forceBanner ? Mode.Banner : _mode)
        {
            case Mode.Banner when _banner is { } b:
                var row = new Grid { ColumnSpacing = 12 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var badge = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(10), Background = Ui.B("Vos.InkHover"), Child = InkIcon(b.n.Glyph.Length > 0 ? b.n.Glyph : Glyphs.Bell, 15, "Vos.Accent") };
                badge.Child.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
                row.Children.Add(badge);
                var text = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
                text.Children.Add(InkText(b.n.AppName, 11, true));
                text.Children.Add(InkText(b.n.Title, 14, false, Tokens.Text, true));
                if (b.n.Body.Length > 0) { var body = InkText(b.n.Body, 12, true); body.TextWrapping = TextWrapping.Wrap; body.MaxLines = 2; text.Children.Add(body); }
                Grid.SetColumn(text, 1);
                row.Children.Add(text);
                g.Children.Add(row);
                g.Width = 400;
                break;
            case Mode.Media:
                g.Width = 430;
                g.ColumnSpacing = 14;
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var art = new Border { Width = 72, Height = 72, CornerRadius = new CornerRadius(14), Background = _art != null ? new ImageBrush { ImageSource = _art, Stretch = Stretch.UniformToFill } : Ui.B("Vos.InkHover") };
                if (_art == null) art.Child = new FontIcon { Glyph = Glyphs.Music, FontFamily = Tokens.Icons, FontSize = 24, Foreground = Ui.B("Vos.InkTextSecondary") };
                g.Children.Add(art);
                var info = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
                info.Children.Add(InkText(media.Title.Length > 0 ? media.Title : "Nothing playing", 15, false, Tokens.Text, true));
                info.Children.Add(InkText(media.Artist, 12, true));
                var dur = media.Duration;
                if (dur > TimeSpan.Zero)
                {
                    var pos = media.EstimatedPosition(DateTimeOffset.Now);
                    var track = new Grid { Height = 3, Margin = new Thickness(0, 8, 0, 2), CornerRadius = new CornerRadius(1.5), Background = Ui.B("Vos.InkTrack") };
                    track.Children.Add(new Border { Height = 3, HorizontalAlignment = HorizontalAlignment.Left, Width = 1, CornerRadius = new CornerRadius(1.5), Background = Ui.B("Vos.MediaAccent"), Tag = pos.TotalSeconds / dur.TotalSeconds });
                    track.SizeChanged += (_, e) => { if (track.Children[0] is Border fill && fill.Tag is double f) fill.Width = Math.Max(3, e.NewSize.Width * Math.Clamp(f, 0, 1)); };
                    info.Children.Add(track);
                    var times = new Grid();
                    times.Children.Add(InkText(Fmt.Duration(pos), 10.5, true, Tokens.Instrument));
                    var end = InkText(Fmt.Duration(dur), 10.5, true, Tokens.Instrument);
                    end.HorizontalAlignment = HorizontalAlignment.Right;
                    times.Children.Add(end);
                    info.Children.Add(times);
                }
                var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(-8, 4, 0, 0) };
                controls.Children.Add(Buttons.Ink(Glyphs.Previous, () => _ = _host.Media.PreviousAsync(), tooltip: "Previous"));
                controls.Children.Add(Buttons.Ink(media.IsPlaying ? Glyphs.Pause : Glyphs.Play, () => _ = _host.Media.PlayPauseAsync(), 38, 17, media.IsPlaying ? "Pause" : "Play"));
                controls.Children.Add(Buttons.Ink(Glyphs.Next, () => _ = _host.Media.NextAsync(), tooltip: "Next"));
                info.Children.Add(controls);
                Grid.SetColumn(info, 1);
                g.Children.Add(info);
                break;
            case Mode.Timer when timer != null:
                g.Width = 340;
                var st = new StackPanel { Spacing = 2 };
                st.Children.Add(InkText(timer.Label, 12, true));
                st.Children.Add(InkText(Fmt.Duration(timer.Remaining(DateTimeOffset.Now)), 32, false, Tokens.Instrument));
                var tc = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(-8, 4, 0, 0) };
                tc.Children.Add(Buttons.Ink(timer.State == TimerState.Paused ? Glyphs.Play : Glyphs.Pause,
                    () => { if (timer.State == TimerState.Paused) _host.Timers.Resume(timer.Id); else _host.Timers.Pause(timer.Id); }, tooltip: timer.State == TimerState.Paused ? "Resume" : "Pause"));
                tc.Children.Add(Buttons.Ink(Glyphs.Add, () => _host.Timers.AddTime(timer.Id, TimeSpan.FromMinutes(1)), tooltip: "Add one minute"));
                tc.Children.Add(Buttons.Ink(Glyphs.Close, () => _host.Timers.Cancel(timer.Id), tooltip: "Cancel timer"));
                st.Children.Add(tc);
                g.Children.Add(st);
                break;
            case Mode.Activity when activity != null:
                g.Width = 380;
                var sa = new StackPanel { Spacing = 4 };
                sa.Children.Add(InkText(activity.Title, 14, false, Tokens.Text, true));
                if (activity.Subtitle.Length > 0) sa.Children.Add(InkText(activity.Subtitle, 12, true));
                if (activity.Progress is double ap)
                    sa.Children.Add(new ProgressBar { Value = ap * 100, Maximum = 100, Margin = new Thickness(0, 6, 0, 0), Foreground = Ui.B("Vos.MediaAccent"), Background = Ui.B("Vos.InkTrack") });
                else sa.Children.Add(new ProgressBar { IsIndeterminate = true, Margin = new Thickness(0, 6, 0, 0), Foreground = Ui.B("Vos.MediaAccent") });
                g.Children.Add(sa);
                break;
            default:
                var w = _host.Weather.Current;
                g.Width = 360;
                if (w != null)
                {
                    var unit = _host.Settings.Current.Weather.TemperatureUnit;
                    var wg = new Grid { ColumnSpacing = 14 };
                    wg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    wg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    var big = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                    big.Children.Add(new WeatherIcon(w.Now.Condition, w.Now.IsDay, 30, Ui.B("Vos.InkText")) { VerticalAlignment = VerticalAlignment.Center });
                    big.Children.Add(InkText(WeatherFormat.Temperature(w.Now.TemperatureC, unit), 34, false, Tokens.Instrument));
                    wg.Children.Add(big);
                    var details = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
                    details.Children.Add(InkText(WeatherCodes.Describe(w.Now.Condition, w.Now.IsDay), 13, false, Tokens.Text, true));
                    if (w.Today is { } t) details.Children.Add(InkText($"H {WeatherFormat.Temperature(t.MaxC, unit)}  L {WeatherFormat.Temperature(t.MinC, unit)}  ·  {w.Location.Name}", 12, true));
                    Grid.SetColumn(details, 1);
                    wg.Children.Add(details);
                    g.Children.Add(wg);
                    g.Tapped += (_, e) => { e.Handled = true; _host.Commands.OpenApp("weather"); SetExpanded(false); };
                }
                else
                {
                    var hint = new StackPanel { Spacing = 2 };
                    hint.Children.Add(InkText("V-Pill", 14, false, Tokens.Text, true));
                    hint.Children.Add(InkText("Music, timers, downloads and alerts appear here.", 12, true));
                    g.Children.Add(hint);
                }
                break;
        }
        _expanded.Children.Add(g);
        Motion.Fade(_expanded, 0, 0);
        Motion.Fade(_expanded, 1, 200, 90);
    }

    // ---------------- geometry ----------------

    private void ApplySize()
    {
        Vector2 target;
        bool showExpanded = _expanded.Children.Count > 0;
        if (showExpanded)
        {
            _expanded.Measure(new global::Windows.Foundation.Size(W, H));
            var d = _expanded.DesiredSize;
            target = new Vector2((float)Math.Min(W - 8, Math.Max(200, d.Width)), (float)Math.Min(H - Top - 4, Math.Max(CompactH, d.Height)));
            _compact.Visibility = Visibility.Collapsed;
        }
        else
        {
            _compact.Visibility = Visibility.Visible;
            _compact.Measure(new global::Windows.Foundation.Size(W, CompactH));
            target = new Vector2((float)Math.Max(44, _compact.DesiredSize.Width), (float)CompactH);
        }
        Morph(target);
    }

    private void Morph(Vector2 target, bool instant = false)
    {
        bool growing = target.X > _size.X || target.Y > _size.Y;
        if (growing) UpdateRegion(Vector2.Max(target, _size));
        _size = target;
        if (instant || !Motion.Enabled)
        {
            _geo.StopAnimation("Size");
            _geo.Size = target;
            UpdateRegion(target);
            return;
        }
        var batch = _c.CreateScopedBatch(CompositionBatchTypes.Animation);
        if (VisualQuality.Current.Springs)
        {
            var spring = _c.CreateSpringVector2Animation();
            spring.FinalValue = target;
            spring.DampingRatio = 0.76f;
            spring.Period = TimeSpan.FromMilliseconds(46);
            _geo.StartAnimation("Size", spring);
        }
        else
        {
            var a = _c.CreateVector2KeyFrameAnimation();
            a.InsertKeyFrame(1f, target, Motion.Decelerate(_c));
            a.Duration = Motion.Scaled(220);
            _geo.StartAnimation("Size", a);
        }
        batch.End();
        batch.Completed += (_, _) => UiThread.Post(() => { if (_size == target) UpdateRegion(target); });
    }

    private void UpdateRegion(Vector2 size)
    {
        double x = (W - size.X) / 2;
        SetHitRegion((x, Top, size.X, size.Y, Math.Min(size.Y / 2, 28)));
    }
}

/// <summary>Short alert sound for finished timers (system sound alias, respects the user's sound scheme).</summary>
public static class Sound
{
    [System.Runtime.InteropServices.DllImport("winmm.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool PlaySoundW(string sound, IntPtr module, uint flags);

    public static void Alert()
    {
        if (!SettingsStore.Instance.Current.Notifications.Sounds) return;
        PlaySoundW("SystemNotification", IntPtr.Zero, 0x00010000 /*SND_ALIAS*/ | 0x0001 /*SND_ASYNC*/ | 0x0002 /*SND_NODEFAULT*/);
    }
}

