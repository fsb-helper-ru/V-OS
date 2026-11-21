using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using VOS.UI.Theme;

namespace VOS.UI;

/// <summary>
/// Sidebar navigation plus a content layer: the layout most V-OS apps share. Pages are created on first visit by
/// <see cref="PageFactory"/> and (optionally) cached, so opening an app only builds what is shown.
/// </summary>
public sealed class NavShell : Grid
{
    public sealed record Item(string Id, string Label, string Glyph);

    private readonly StackPanel _items = new() { Spacing = 2 };
    private readonly StackPanel _footer = new() { Spacing = 2 };
    private readonly ContentControl _content = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch, IsTabStop = false };
    private readonly Dictionary<string, (Button button, Border indicator)> _buttons = [];
    private readonly Dictionary<string, UIElement> _cache = [];

    public Func<string, UIElement>? PageFactory { get; set; }
    public bool CachePages { get; set; } = true;
    public string? Current { get; private set; }
    public event Action<string>? Navigated;
    /// <summary>Area above the navigation list (search box, account card, "new" button...).</summary>
    public Grid SidebarTop { get; } = new() { Margin = new Thickness(0, 0, 0, 8) };

    public NavShell(double sidebarWidth = 240)
    {
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(sidebarWidth) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var side = new Grid { Padding = new Thickness(10, 2, 10, 12) };
        side.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        side.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        side.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        side.Children.Add(SidebarTop);
        var scroll = new ScrollViewer { Content = _items, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 4, 0) };
        Grid.SetRow(scroll, 1);
        side.Children.Add(scroll);
        Grid.SetRow(_footer, 2);
        side.Children.Add(_footer);
        Children.Add(side);

        var layer = new Border
        {
            Background = Ui.B("Vos.Layer"),
            BorderBrush = Ui.B("Vos.StrokeSubtle"),
            BorderThickness = new Thickness(1, 1, 0, 0),
            CornerRadius = new CornerRadius(Tokens.RadiusWindow, 0, 0, 0),
            Child = _content,
        };
        Grid.SetColumn(layer, 1);
        Children.Add(layer);
    }

    public void AddHeader(string text) =>
        _items.Children.Add(new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Ui.B("Vos.TextTertiary"), Margin = new Thickness(12, 14, 0, 4) });

    public void Add(Item item, bool footer = false)
    {
        var indicator = new Border { Width = 3, Height = 16, CornerRadius = new CornerRadius(1.5), Background = Ui.B("Vos.Accent"), Opacity = 0, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(Ui.Icon(item.Glyph, 16, "Vos.TextPrimary"));
        var label = Ui.Text(item.Label, 13.5);
        Grid.SetColumn(label, 1);
        row.Children.Add(label);
        var button = Buttons.Subtle(row, () => Select(item.Id), Tokens.RadiusControl, new Thickness(14, 9, 10, 9));
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        Buttons.AutomationName(button, item.Label);
        var host = new Grid();
        host.Children.Add(button);
        host.Children.Add(indicator);
        (footer ? _footer : _items).Children.Add(host);
        _buttons[item.Id] = (button, indicator);
    }

    public void AddRange(IEnumerable<Item> items) { foreach (var i in items) Add(i); }

    public void ClearItems() { _items.Children.Clear(); _buttons.Clear(); _cache.Clear(); }

    /// <summary>Drops a cached page so the next visit rebuilds it (after data it shows has changed).</summary>
    public void Invalidate(string id) => _cache.Remove(id);

    public void Select(string id)
    {
        if (!_buttons.ContainsKey(id)) return;
        Current = id;
        foreach (var (key, (button, indicator)) in _buttons)
        {
            bool on = key == id;
            indicator.Opacity = on ? 1 : 0;
            button.Background = on ? Ui.B("Vos.Control") : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }
        if (PageFactory != null)
        {
            if (!CachePages || !_cache.TryGetValue(id, out var page))
            {
                page = PageFactory(id);
                if (CachePages) _cache[id] = page;
            }
            _content.Content = page;
            if (page is FrameworkElement fe) Quality.Motion.Enter(fe, new System.Numerics.Vector3(0, 8, 0), 220);
        }
        Navigated?.Invoke(id);
    }

    public void SetContent(UIElement content) => _content.Content = content;
}

/// <summary>Building blocks for app pages: headers, sections, cards, setting rows, controls bound to actions.</summary>
public static class Kit
{
    public static ScrollViewer Scroll(UIElement content, double maxWidth = 980) => new()
    {
        Content = new Grid { Children = { content }, MaxWidth = maxWidth, HorizontalAlignment = HorizontalAlignment.Stretch },
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        Padding = new Thickness(36, 28, 36, 36),
    };

    public static StackPanel Stack(double spacing = 8, params UIElement[] children)
    {
        var s = new StackPanel { Spacing = spacing };
        foreach (var c in children) s.Children.Add(c);
        return s;
    }

    public static StackPanel Row(double spacing = 8, params UIElement[] children)
    {
        var s = new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing };
        foreach (var c in children) s.Children.Add(c);
        return s;
    }

    public static StackPanel PageHeader(string title, string? subtitle = null)
    {
        var s = new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 0, 12) };
        s.Children.Add(Ui.Text(title, 28, "Vos.TextPrimary", Tokens.Display, FontWeights.SemiBold));
        if (subtitle != null) s.Children.Add(Ui.Text(subtitle, 13.5, "Vos.TextSecondary", wrap: TextWrapping.Wrap));
        return s;
    }

    public static TextBlock Section(string title) =>
        new() { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Ui.B("Vos.TextPrimary"), Margin = new Thickness(2, 18, 0, 6), FontFamily = Tokens.Text };

    public static Border Card(UIElement child, Thickness? padding = null) => new()
    {
        Child = child,
        Padding = padding ?? new Thickness(16),
        Background = Ui.B("Vos.Card"),
        BorderBrush = Ui.B("Vos.StrokeSubtle"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(Tokens.RadiusCard - 4),
    };

    /// <summary>Several setting rows in one card, separated by hairlines.</summary>
    public static Border Group(params UIElement[] rows)
    {
        var stack = new StackPanel();
        for (int i = 0; i < rows.Length; i++)
        {
            if (i > 0) stack.Children.Add(new Border { Height = 1, Background = Ui.B("Vos.StrokeSubtle") });
            stack.Children.Add(rows[i]);
        }
        return Card(stack, new Thickness(0));
    }

    /// <summary>A setting: optional glyph, title, optional description, and a control on the right.</summary>
    public static Grid Setting(string title, string? description = null, FrameworkElement? control = null, string? glyph = null)
    {
        var g = new Grid { Padding = new Thickness(16, 12, 16, 12), MinHeight = 62, ColumnSpacing = 16 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (glyph != null) g.Children.Add(new FontIcon { Glyph = glyph, FontFamily = Tokens.Icons, FontSize = 18, Foreground = Ui.B("Vos.TextPrimary"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 2, 0) });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        text.Children.Add(Ui.Text(title, 14));
        if (!string.IsNullOrEmpty(description)) text.Children.Add(Ui.Text(description, 12, "Vos.TextSecondary", wrap: TextWrapping.Wrap));
        Grid.SetColumn(text, 1);
        g.Children.Add(text);
        if (control != null)
        {
            control.VerticalAlignment = VerticalAlignment.Center;
            Buttons.AutomationName(control, title);
            Grid.SetColumn(control, 2);
            g.Children.Add(control);
        }
        return g;
    }

    public static ToggleSwitch Toggle(bool value, Action<bool> changed)
    {
        var t = new ToggleSwitch { IsOn = value, OnContent = "", OffContent = "", MinWidth = 0, Margin = new Thickness(0, 0, -10, 0) };
        t.Toggled += (_, _) => changed(t.IsOn);
        return t;
    }

    public static ComboBox Choice<T>(IEnumerable<(T value, string label)> options, T selected, Action<T> changed, double width = 220)
    {
        var list = options.ToList();
        var c = new ComboBox { Width = width, ItemsSource = list.Select(o => o.label).ToList() };
        c.SelectedIndex = Math.Max(0, list.FindIndex(o => EqualityComparer<T>.Default.Equals(o.value, selected)));
        c.SelectionChanged += (_, _) => { if (c.SelectedIndex >= 0) changed(list[c.SelectedIndex].value); };
        return c;
    }

    public static ComboBox EnumChoice<T>(T selected, Action<T> changed, Func<T, string>? label = null, double width = 220) where T : struct, Enum =>
        Choice(Enum.GetValues<T>().Select(v => (v, label?.Invoke(v) ?? Words(v.ToString()))), selected, changed, width);

    /// <summary>"AutoHideTaskbar" → "Auto hide taskbar".</summary>
    public static string Words(string pascal)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < pascal.Length; i++)
        {
            char ch = pascal[i];
            if (i > 0 && char.IsUpper(ch) && !char.IsUpper(pascal[i - 1])) { sb.Append(' '); sb.Append(char.ToLowerInvariant(ch)); }
            else sb.Append(ch);
        }
        return sb.ToString();
    }

    public static Slider SliderControl(double min, double max, double value, double step, Action<double> changed, double width = 220, Func<double, string>? format = null)
    {
        var s = new Slider { Minimum = min, Maximum = max, StepFrequency = step, Value = value, Width = width, SnapsTo = Microsoft.UI.Xaml.Controls.Primitives.SliderSnapsTo.StepValues };
        if (format != null) s.ThumbToolTipValueConverter = new FuncConverter(v => format(v));
        s.ValueChanged += (_, e) => changed(e.NewValue);
        return s;
    }

    /// <summary>Text field that commits on Enter or when focus leaves (not on every keystroke).</summary>
    public static TextBox Field(string text, string placeholder, Action<string> committed, double width = 260)
    {
        var t = new TextBox { Text = text, PlaceholderText = placeholder, Width = width };
        string last = text;
        void Commit() { if (t.Text != last) { last = t.Text; committed(t.Text); } }
        t.LostFocus += (_, _) => Commit();
        t.KeyDown += (_, e) => { if (e.Key == global::Windows.System.VirtualKey.Enter) { Commit(); e.Handled = true; } };
        return t;
    }

    public static TextBox SearchBox(string placeholder, Action<string> changed, double width = double.NaN)
    {
        var t = new TextBox { PlaceholderText = placeholder, Width = width };
        t.TextChanged += (_, _) => changed(t.Text);
        Buttons.AutomationName(t, placeholder);
        return t;
    }

    public static StackPanel Empty(string glyph, string title, string text, (string label, Action action)? cta = null)
    {
        var s = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 380, Margin = new Thickness(24) };
        s.Children.Add(new FontIcon { Glyph = glyph, FontFamily = Tokens.Icons, FontSize = 40, Foreground = Ui.B("Vos.TextTertiary"), Margin = new Thickness(0, 0, 0, 6) });
        s.Children.Add(new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Foreground = Ui.B("Vos.TextPrimary"), TextAlignment = TextAlignment.Center });
        s.Children.Add(new TextBlock { Text = text, FontSize = 13, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Foreground = Ui.B("Vos.TextSecondary") });
        if (cta is { } c)
        {
            var b = Buttons.Accent(c.label, c.action);
            b.HorizontalAlignment = HorizontalAlignment.Center;
            b.Margin = new Thickness(0, 10, 0, 0);
            s.Children.Add(b);
        }
        return s;
    }

    public static InfoBar Info(string title, string message, InfoBarSeverity severity = InfoBarSeverity.Informational, bool closable = false) =>
        new() { Title = title, Message = message, Severity = severity, IsOpen = true, IsClosable = closable };

    public static HyperlinkButton Link(string text, Action onClick)
    {
        var b = new HyperlinkButton { Content = text, Padding = new Thickness(0), Foreground = Ui.B("Vos.AccentText") };
        b.Click += (_, _) => onClick();
        return b;
    }

    /// <summary>Pill-shaped filter / segment button.</summary>
    public static Button Chip(string text, bool selected, Action onClick)
    {
        var b = selected ? Buttons.Accent(text, onClick, 16, new Thickness(14, 5, 14, 6)) : Buttons.Filled(text, onClick, 16, new Thickness(14, 5, 14, 6));
        b.FontSize = 13;
        return b;
    }

    /// <summary>A row of chips acting as a segmented control.</summary>
    public static StackPanel Segments(IReadOnlyList<(string id, string label)> items, string selected, Action<string> changed)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        void Build(string current)
        {
            row.Children.Clear();
            foreach (var (id, label) in items)
                row.Children.Add(Chip(label, id == current, () => { Build(id); changed(id); }));
        }
        Build(selected);
        return row;
    }

    /// <summary>Large figure with a caption: used by dashboards (Monitor, Weather details, Gaming).</summary>
    public static StackPanel Stat(string caption, string value, string? detail = null, string valueBrush = "Vos.TextPrimary")
    {
        var s = new StackPanel { Spacing = 2 };
        s.Children.Add(Ui.Text(caption, 12, "Vos.TextSecondary"));
        s.Children.Add(Ui.Text(value, 24, valueBrush, Tokens.Instrument, FontWeights.SemiBold));
        if (detail != null) s.Children.Add(Ui.Text(detail, 12, "Vos.TextTertiary"));
        return s;
    }

    /// <summary>Clickable list row with hover feedback (icon/visual, primary and secondary text, trailing content).</summary>
    public static Button ListRow(FrameworkElement? leading, string primary, string? secondary, FrameworkElement? trailing, Action? onClick, double minHeight = 52)
    {
        var g = new Grid { ColumnSpacing = 12, MinHeight = minHeight - 16 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (leading != null) { leading.VerticalAlignment = VerticalAlignment.Center; g.Children.Add(leading); }
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
        text.Children.Add(Ui.Text(primary, 13.5));
        if (!string.IsNullOrEmpty(secondary)) text.Children.Add(Ui.Text(secondary, 12, "Vos.TextSecondary"));
        Grid.SetColumn(text, 1);
        g.Children.Add(text);
        if (trailing != null) { trailing.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(trailing, 2); g.Children.Add(trailing); }
        var b = Buttons.Subtle(g, onClick, Tokens.RadiusControl, new Thickness(12, 8, 12, 8));
        b.HorizontalAlignment = HorizontalAlignment.Stretch;
        b.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        Buttons.AutomationName(b, primary);
        return b;
    }

    /// <summary>Round badge with a glyph on a tinted circle.</summary>
    public static Grid GlyphBadge(string glyph, double size = 36, string tint = "Vos.AccentSubtle", string fg = "Vos.AccentText")
    {
        var g = new Grid { Width = size, Height = size };
        g.Children.Add(new Border { CornerRadius = new CornerRadius(size / 2), Background = Ui.B(tint) });
        g.Children.Add(new FontIcon { Glyph = glyph, FontFamily = Tokens.Icons, FontSize = size * 0.44, Foreground = Ui.B(fg), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        return g;
    }

    /// <summary>Horizontal meter (0..1) in the accent colour.</summary>
    public static Grid Meter(double value, double width = double.NaN, double height = 6, string brush = "Vos.Accent")
    {
        var g = new Grid { Height = height, Width = width, CornerRadius = new CornerRadius(height / 2), Background = Ui.B("Vos.ControlHover") };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Clamp(value, 0, 1), GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - Math.Clamp(value, 0, 1), GridUnitType.Star) });
        g.Children.Add(new Border { Background = Ui.B(brush), CornerRadius = new CornerRadius(height / 2) });
        return g;
    }

    public static void OnEnter(TextBox box, Action action) =>
        box.KeyDown += (_, e) => { if (e.Key == global::Windows.System.VirtualKey.Enter) { e.Handled = true; action(); } };
}

/// <summary>IValueConverter from a lambda (for slider tooltips and the like).</summary>
public sealed class FuncConverter(Func<double, string> f) : Microsoft.UI.Xaml.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value is double d ? f(d) : value?.ToString() ?? "";
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

/// <summary>Live line chart drawn with a Polyline (cheap: a few dozen points, redrawn on new samples only).</summary>
public sealed class Sparkline : Grid
{
    private readonly Microsoft.UI.Xaml.Shapes.Polyline _line = new() { StrokeThickness = 1.6, StrokeLineJoin = PenLineJoin.Round };
    private readonly Microsoft.UI.Xaml.Shapes.Polygon _fill = new() { Opacity = 0.18 };
    private readonly Queue<double> _values = new();
    private readonly int _capacity;
    public double Max { get; set; } = 100;
    public bool AutoScale { get; set; }

    public Sparkline(int capacity = 60, string brush = "Vos.Accent")
    {
        _capacity = capacity;
        _line.Stroke = Ui.B(brush);
        _fill.Fill = Ui.B(brush);
        Children.Add(_fill);
        Children.Add(_line);
        SizeChanged += (_, _) => Redraw();
    }

    public void Push(double v)
    {
        _values.Enqueue(v);
        while (_values.Count > _capacity) _values.Dequeue();
        Redraw();
    }

    private void Redraw()
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0 || _values.Count == 0) return;
        double max = AutoScale ? Math.Max(1e-6, _values.Max() * 1.15) : Max;
        int i = 0, offset = _capacity - _values.Count;
        var line = new PointCollection();
        double firstX = offset * w / (_capacity - 1);
        var area = new PointCollection { new(firstX, h) }; // closed area under the line
        foreach (var v in _values)
        {
            var p = new global::Windows.Foundation.Point((offset + i++) * w / (_capacity - 1), h - Math.Clamp(v / max, 0, 1) * (h - 2) - 1);
            line.Add(p);
            area.Add(p);
        }
        area.Add(new global::Windows.Foundation.Point(w, h));
        _line.Points = line;
        _fill.Points = area;
    }
}
