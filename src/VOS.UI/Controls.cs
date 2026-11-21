using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using VOS.UI.Theme;

namespace VOS.UI;

/// <summary>Buttons with V-OS lightweight styling (state brushes override WinUI's template resources).</summary>
public static class Buttons
{
    private static readonly SolidColorBrush Clear = new(Colors.Transparent);

    private static Button Make(object content, Action? onClick, double radius, Thickness padding, string bg, string bgHover, string bgPressed, string fg)
    {
        var b = new Button
        {
            Content = content,
            Padding = padding,
            CornerRadius = new CornerRadius(radius),
            BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        Brush Res(string key) => key == "clear" ? Clear : ThemeService.Current.Brush(key);
        b.Resources["ButtonBackground"] = Res(bg);
        b.Resources["ButtonBackgroundPointerOver"] = Res(bgHover);
        b.Resources["ButtonBackgroundPressed"] = Res(bgPressed);
        b.Resources["ButtonBackgroundDisabled"] = Clear;
        b.Resources["ButtonForeground"] = Res(fg);
        b.Resources["ButtonForegroundPointerOver"] = Res(fg);
        b.Resources["ButtonForegroundPressed"] = Res(fg);
        b.Resources["ButtonForegroundDisabled"] = Res("Vos.TextDisabled");
        b.Resources["ButtonBorderBrush"] = Clear;
        b.Resources["ButtonBorderBrushPointerOver"] = Clear;
        b.Resources["ButtonBorderBrushPressed"] = Clear;
        b.Resources["ButtonBorderBrushDisabled"] = Clear;
        b.Background = Res(bg);
        b.Foreground = Res(fg);
        if (onClick != null) b.Click += (_, _) => onClick();
        return b;
    }

    public static Button Subtle(object content, Action? onClick = null, double radius = Tokens.RadiusControl, Thickness? padding = null) =>
        Make(content, onClick, radius, padding ?? new Thickness(10, 6, 10, 6), "clear", "Vos.ControlHover", "Vos.ControlPressed", "Vos.TextPrimary");

    public static Button Filled(object content, Action? onClick = null, double radius = Tokens.RadiusControl, Thickness? padding = null) =>
        Make(content, onClick, radius, padding ?? new Thickness(14, 8, 14, 8), "Vos.Control", "Vos.ControlHover", "Vos.ControlPressed", "Vos.TextPrimary");

    public static Button Accent(object content, Action? onClick = null, double radius = Tokens.RadiusControl, Thickness? padding = null) =>
        Make(content, onClick, radius, padding ?? new Thickness(16, 8, 16, 8), "Vos.Accent", "Vos.AccentHover", "Vos.AccentPressed", "Vos.OnAccent");

    /// <summary>Glyph-only round button (light content on any surface).</summary>
    public static Button Icon(string glyph, Action? onClick = null, double size = 32, double glyphSize = 14, string tooltip = "")
    {
        var b = Subtle(Ui.Icon(glyph, glyphSize), onClick, size / 2, new Thickness(0));
        b.Width = b.Height = size;
        if (tooltip.Length > 0) { ToolTipService.SetToolTip(b, tooltip); AutomationName(b, tooltip); }
        return b;
    }

    /// <summary>Buttons on the ink capsule (always light-on-dark, both themes).</summary>
    public static Button Ink(string glyph, Action? onClick = null, double size = 34, double glyphSize = 15, string tooltip = "")
    {
        var b = Make(new FontIcon { Glyph = glyph, FontFamily = Tokens.Icons, FontSize = glyphSize, Foreground = Ui.B("Vos.InkText") },
            onClick, size / 2, new Thickness(0), "clear", "Vos.InkHover", "Vos.InkPressed", "Vos.InkText");
        b.Width = b.Height = size;
        if (tooltip.Length > 0) { ToolTipService.SetToolTip(b, tooltip); AutomationName(b, tooltip); }
        return b;
    }

    public static void AutomationName(UIElement e, string name) => Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(e, name);
}

/// <summary>Toggle tile used by the control center (icon, label, state line).</summary>
public sealed class ToggleTile : Button
{
    private readonly FontIcon _icon;
    private readonly TextBlock _state;
    private bool _on;
    public event Action<bool>? Toggled;

    public ToggleTile(string glyph, string label, string state, bool on)
    {
        var stack = new StackPanel { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Left };
        _icon = new FontIcon { Glyph = glyph, FontFamily = Tokens.Icons, FontSize = 16, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 8) };
        stack.Children.Add(_icon);
        stack.Children.Add(new TextBlock { Text = label, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        _state = new TextBlock { Text = state, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Opacity = 0.75 };
        stack.Children.Add(_state);
        Content = stack;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        HorizontalContentAlignment = HorizontalAlignment.Left;
        VerticalContentAlignment = VerticalAlignment.Top;
        Padding = new Thickness(14, 12, 12, 12);
        CornerRadius = new CornerRadius(Tokens.RadiusCard);
        BorderThickness = new Thickness(0);
        Buttons.AutomationName(this, label);
        Click += (_, _) => { On = !On; Toggled?.Invoke(On); };
        On = on;
        ThemeService.Current.Changed += Restyle;
    }

    public string StateText { get => _state.Text; set => _state.Text = value; }

    public bool On
    {
        get => _on;
        set { _on = value; Restyle(); }
    }

    private void Restyle()
    {
        string bg = _on ? "Vos.Accent" : "Vos.Control", hover = _on ? "Vos.AccentHover" : "Vos.ControlHover", pressed = _on ? "Vos.AccentPressed" : "Vos.ControlPressed";
        string fg = _on ? "Vos.OnAccent" : "Vos.TextPrimary";
        Resources["ButtonBackground"] = Ui.B(bg);
        Resources["ButtonBackgroundPointerOver"] = Ui.B(hover);
        Resources["ButtonBackgroundPressed"] = Ui.B(pressed);
        Resources["ButtonForeground"] = Resources["ButtonForegroundPointerOver"] = Resources["ButtonForegroundPressed"] = Ui.B(fg);
        Resources["ButtonBorderBrush"] = Resources["ButtonBorderBrushPointerOver"] = Resources["ButtonBorderBrushPressed"] = new SolidColorBrush(Colors.Transparent);
        Background = Ui.B(bg);
        Foreground = Ui.B(fg);
        _icon.Foreground = Ui.B(fg);
        // Force the template to re-read the overridden resources.
        VisualStateManager.GoToState(this, "Normal", false);
    }
}

/// <summary>UI-thread helpers.</summary>
public static class UiThread
{
    public static DispatcherQueue? Queue { get; set; }
    public static void Post(Action a) { if (Queue == null || Queue.HasThreadAccess) a(); else Queue.TryEnqueue(() => a()); }

    public static DispatcherQueueTimer Timer(TimeSpan interval, Action tick, bool repeating = true)
    {
        var t = (Queue ?? DispatcherQueue.GetForCurrentThread()).CreateTimer();
        t.Interval = interval;
        t.IsRepeating = repeating;
        t.Tick += (_, _) => tick();
        return t;
    }

    /// <summary>
    /// Completes (on the UI thread) once XAML has rendered <paramref name="frames"/> more frames, or after the timeout.
    /// Subscribing to Rendering keeps frames coming, so the handler is removed as soon as it is done (idle CPU).
    /// </summary>
    public static Task NextFrameAsync(int frames = 1, int timeoutMs = 300)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int seen = 0;
        DispatcherQueueTimer? guard = null;
        void Finish()
        {
            CompositionTarget.Rendering -= OnRendering;
            guard?.Stop();
            done.TrySetResult();
        }
        void OnRendering(object? sender, object e) { if (++seen > frames) Finish(); }
        CompositionTarget.Rendering += OnRendering;
        guard = Timer(TimeSpan.FromMilliseconds(timeoutMs), Finish, repeating: false);
        guard.Start();
        return done.Task;
    }
}
