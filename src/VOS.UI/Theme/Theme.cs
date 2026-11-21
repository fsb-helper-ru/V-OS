using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using VOS.Core;
using VOS.Core.Settings;
using Windows.UI;

namespace VOS.UI.Theme;

/// <summary>Colour helpers (sRGB, HSL, WCAG contrast).</summary>
public static class Colors2
{
    public static Color Hex(string hex)
    {
        var h = hex.TrimStart('#');
        if (h.Length == 6) h = "FF" + h;
        uint v = uint.Parse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return Color.FromArgb((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    public static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);
    public static Color Mix(Color a, Color b, double t) => Color.FromArgb(
        (byte)(a.A + (b.A - a.A) * t), (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
    public static Color Lighten(Color c, double t) => Mix(c, Color.FromArgb(c.A, 255, 255, 255), t);
    public static Color Darken(Color c, double t) => Mix(c, Color.FromArgb(c.A, 0, 0, 0), t);

    public static double Luminance(Color c)
    {
        static double L(byte v) { double s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        return 0.2126 * L(c.R) + 0.7152 * L(c.G) + 0.0722 * L(c.B);
    }

    public static double Contrast(Color a, Color b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>Adjusts lightness until the colour reaches the target contrast against a background.</summary>
    public static Color EnsureContrast(Color c, Color background, double target)
    {
        var x = c;
        bool darkBg = Luminance(background) < 0.4;
        for (int i = 0; i < 20 && Contrast(x, background) < target; i++) x = darkBg ? Lighten(x, 0.08) : Darken(x, 0.08);
        return x;
    }
}

/// <summary>"Instrument" design tokens. Graphite surfaces, accent as light, ink capsule in both themes.</summary>
public sealed record Palette(
    bool IsDark, Color Base, Color Surface1, Color Surface2, Color Surface3, Color StrokeSubtle, Color Stroke, Color StrokeStrong,
    Color TextPrimary, Color TextSecondary, Color TextTertiary, Color TextDisabled, Color GlassTint, double GlassTintOpacity,
    double GlassLuminosity, Color Ink, Color Scrim, Color Success, Color Warning, Color Critical)
{
    public static Palette Dark { get; } = new(true,
        Base: Colors2.Hex("#0F1013"), Surface1: Colors2.Hex("#17181C"), Surface2: Colors2.Hex("#1F2126"), Surface3: Colors2.Hex("#2A2C33"),
        StrokeSubtle: Color.FromArgb(0x12, 255, 255, 255), Stroke: Color.FromArgb(0x1E, 255, 255, 255), StrokeStrong: Color.FromArgb(0x30, 255, 255, 255),
        TextPrimary: Colors2.Hex("#F1F2F4"), TextSecondary: Colors2.Hex("#A9ADB7"), TextTertiary: Colors2.Hex("#757A86"), TextDisabled: Colors2.Hex("#4F535C"),
        GlassTint: Colors2.Hex("#14151A"), GlassTintOpacity: 0.50, GlassLuminosity: 0.78, Ink: Colors2.Hex("#0B0C0F"),
        Scrim: Color.FromArgb(0x99, 0, 0, 0), Success: Colors2.Hex("#5BD69A"), Warning: Colors2.Hex("#F2C35B"), Critical: Colors2.Hex("#FF7468"));

    public static Palette Light { get; } = new(false,
        Base: Colors2.Hex("#F2F3F5"), Surface1: Colors2.Hex("#FFFFFF"), Surface2: Colors2.Hex("#ECEEF2"), Surface3: Colors2.Hex("#E1E4EA"),
        StrokeSubtle: Color.FromArgb(0x12, 15, 17, 21), Stroke: Color.FromArgb(0x1F, 15, 17, 21), StrokeStrong: Color.FromArgb(0x33, 15, 17, 21),
        TextPrimary: Colors2.Hex("#15171C"), TextSecondary: Colors2.Hex("#525762"), TextTertiary: Colors2.Hex("#848993"), TextDisabled: Colors2.Hex("#B3B7BF"),
        GlassTint: Colors2.Hex("#F5F6F8"), GlassTintOpacity: 0.55, GlassLuminosity: 0.85, Ink: Colors2.Hex("#0B0C0F"),
        Scrim: Color.FromArgb(0x66, 0, 0, 0), Success: Colors2.Hex("#1F9D63"), Warning: Colors2.Hex("#B07A0E"), Critical: Colors2.Hex("#D23B2F"));
}

public static class AccentPresets
{
    public sealed record Preset(string Id, string Name, string Dark, string Light);

    public static readonly IReadOnlyList<Preset> All =
    [
        new("aurora", "Aurora", "#8EA2FF", "#4C5FD6"),
        new("glacier", "Glacier", "#6FB4FF", "#1F6FD1"),
        new("lume", "Lume", "#6FE0C0", "#0E8A6A"),
        new("iris", "Iris", "#B79CFF", "#6A47D9"),
        new("rose", "Rose", "#FF8FB3", "#C2386A"),
        new("ember", "Ember", "#FF9466", "#C9541F"),
        new("sand", "Sand", "#E6C78F", "#8F6A2B"),
        new("graphite", "Graphite", "#C3C8D2", "#4B515C"),
    ];

    public static Color Resolve(string id, bool dark)
    {
        var p = All.FirstOrDefault(x => x.Id == id) ?? All[0];
        return Colors2.Hex(dark ? p.Dark : p.Light);
    }
}

/// <summary>
/// Owns one mutable brush per token. Every view references these instances, so a theme or accent change is a
/// colour change on existing brushes: instant, no reload, every open surface updates together.
/// Also re-points WinUI's own resource keys at the same brushes so standard controls match V-OS.
/// </summary>
public sealed class ThemeService
{
    public static ThemeService Current { get; } = new();

    private readonly Dictionary<string, SolidColorBrush> _brushes = [];
    private readonly List<WeakReference<FrameworkElement>> _roots = [];
    public Palette Palette { get; private set; } = Palette.Dark;
    public Color Accent { get; private set; } = AccentPresets.Resolve("aurora", true);
    public Color? MediaAccent { get; private set; }
    public bool IsDark => Palette.IsDark;
    public ElementTheme ElementTheme => IsDark ? ElementTheme.Dark : ElementTheme.Light;

    public event Action? Changed;

    public SolidColorBrush Brush(string key)
    {
        if (!_brushes.TryGetValue(key, out var b)) { b = new SolidColorBrush(Microsoft.UI.Colors.Transparent); _brushes[key] = b; }
        return b;
    }

    /// <summary>Installs all brushes into the application's resources. Call once from App before any window is created.</summary>
    public void Install(ResourceDictionary resources)
    {
        Apply(Palette, Accent);
        foreach (var (key, brush) in _brushes) resources[key] = brush;
        // WinUI built-in keys → our brushes, in every theme dictionary so controls follow V-OS in both themes.
        var map = WinUiKeyMap();
        foreach (var themeKey in new[] { "Default", "Light", "Dark" })
        {
            var td = resources.ThemeDictionaries.TryGetValue(themeKey, out var d) && d is ResourceDictionary rd ? rd : new ResourceDictionary();
            foreach (var (winui, ours) in map) td[winui] = Brush(ours);
            td["SystemAccentColor"] = Accent;
            resources.ThemeDictionaries[themeKey] = td;
        }
    }

    private static (string winui, string ours)[] WinUiKeyMap() =>
    [
        ("AccentFillColorDefaultBrush", "Vos.Accent"), ("AccentFillColorSecondaryBrush", "Vos.AccentHover"), ("AccentFillColorTertiaryBrush", "Vos.AccentPressed"),
        ("AccentTextFillColorPrimaryBrush", "Vos.AccentText"), ("AccentTextFillColorSecondaryBrush", "Vos.AccentText"), ("TextOnAccentFillColorPrimaryBrush", "Vos.OnAccent"),
        ("TextFillColorPrimaryBrush", "Vos.TextPrimary"), ("TextFillColorSecondaryBrush", "Vos.TextSecondary"), ("TextFillColorTertiaryBrush", "Vos.TextTertiary"),
        ("TextFillColorDisabledBrush", "Vos.TextDisabled"), ("ControlFillColorDefaultBrush", "Vos.Control"), ("ControlFillColorSecondaryBrush", "Vos.ControlHover"),
        ("ControlFillColorTertiaryBrush", "Vos.ControlPressed"), ("CardBackgroundFillColorDefaultBrush", "Vos.Surface1"), ("CardStrokeColorDefaultBrush", "Vos.StrokeSubtle"),
        ("ControlStrokeColorDefaultBrush", "Vos.Stroke"), ("SolidBackgroundFillColorBaseBrush", "Vos.Base"), ("LayerFillColorDefaultBrush", "Vos.Surface1"),
        ("SubtleFillColorSecondaryBrush", "Vos.ControlHover"), ("SubtleFillColorTertiaryBrush", "Vos.ControlPressed"), ("DividerStrokeColorDefaultBrush", "Vos.StrokeSubtle"),
        ("FocusStrokeColorOuterBrush", "Vos.TextPrimary"), ("SystemFillColorCriticalBrush", "Vos.Critical"), ("SystemFillColorSuccessBrush", "Vos.Success"),
        ("SystemFillColorCautionBrush", "Vos.Warning"),
        // Control-specific keys are StaticResource aliases inside XamlControlsResources, resolved within that dictionary,
        // so overriding the base keys above does not reach them. Point them straight at the mutable V-OS brushes
        // (this also makes runtime accent changes apply to standard controls without a theme refresh).
        ("ToggleSwitchFillOn", "Vos.Accent"), ("ToggleSwitchFillOnPointerOver", "Vos.AccentHover"), ("ToggleSwitchFillOnPressed", "Vos.AccentPressed"),
        ("ToggleSwitchStrokeOn", "Vos.Accent"), ("ToggleSwitchStrokeOnPointerOver", "Vos.AccentHover"), ("ToggleSwitchStrokeOnPressed", "Vos.AccentPressed"),
        ("ToggleSwitchKnobFillOn", "Vos.OnAccent"), ("ToggleSwitchKnobFillOnPointerOver", "Vos.OnAccent"), ("ToggleSwitchKnobFillOnPressed", "Vos.OnAccent"),
        ("SliderTrackValueFill", "Vos.Accent"), ("SliderTrackValueFillPointerOver", "Vos.AccentHover"), ("SliderTrackValueFillPressed", "Vos.AccentPressed"),
        ("SliderThumbBackground", "Vos.Accent"), ("SliderThumbBackgroundPointerOver", "Vos.AccentHover"), ("SliderThumbBackgroundPressed", "Vos.AccentPressed"),
        ("CheckBoxCheckBackgroundFillChecked", "Vos.Accent"), ("CheckBoxCheckBackgroundFillCheckedPointerOver", "Vos.AccentHover"), ("CheckBoxCheckBackgroundFillCheckedPressed", "Vos.AccentPressed"),
        ("CheckBoxCheckBackgroundStrokeChecked", "Vos.Accent"), ("CheckBoxCheckGlyphForegroundChecked", "Vos.OnAccent"),
        ("CheckBoxCheckGlyphForegroundCheckedPointerOver", "Vos.OnAccent"), ("CheckBoxCheckGlyphForegroundCheckedPressed", "Vos.OnAccent"),
        ("RadioButtonOuterEllipseCheckedFill", "Vos.Accent"), ("RadioButtonOuterEllipseCheckedFillPointerOver", "Vos.AccentHover"), ("RadioButtonOuterEllipseCheckedFillPressed", "Vos.AccentPressed"),
        ("RadioButtonOuterEllipseCheckedStroke", "Vos.Accent"), ("RadioButtonOuterEllipseCheckedStrokePointerOver", "Vos.AccentHover"), ("RadioButtonOuterEllipseCheckedStrokePressed", "Vos.AccentPressed"),
        ("RadioButtonCheckGlyphFill", "Vos.OnAccent"), ("RadioButtonCheckGlyphFillPointerOver", "Vos.OnAccent"), ("RadioButtonCheckGlyphFillPressed", "Vos.OnAccent"),
        ("AccentButtonBackground", "Vos.Accent"), ("AccentButtonBackgroundPointerOver", "Vos.AccentHover"), ("AccentButtonBackgroundPressed", "Vos.AccentPressed"),
        ("AccentButtonForeground", "Vos.OnAccent"), ("AccentButtonForegroundPointerOver", "Vos.OnAccent"), ("AccentButtonForegroundPressed", "Vos.OnAccent"),
        ("HyperlinkButtonForeground", "Vos.AccentText"), ("HyperlinkButtonForegroundPointerOver", "Vos.AccentHover"), ("HyperlinkButtonForegroundPressed", "Vos.AccentPressed"),
        ("ProgressBarForeground", "Vos.Accent"), ("ProgressRingForegroundThemeBrush", "Vos.Accent"),
        ("ComboBoxItemPillFillBrush", "Vos.Accent"), ("ListViewItemSelectionIndicatorBrush", "Vos.Accent"), ("NavigationViewSelectionIndicatorForeground", "Vos.Accent"),
        ("TextControlSelectionHighlightColor", "Vos.Accent"),
    ];

    /// <summary>Resolves theme and accent from settings (and Windows, for Auto / System sources) and applies them.</summary>
    public void ApplySettings(AppearanceSettings a, bool windowsLight, string windowsAccentHex, string? wallpaperAccentHex = null)
    {
        bool dark = a.Theme switch
        {
            ThemeMode.Light => false,
            ThemeMode.Auto => !windowsLight,
            ThemeMode.Sun => SunIsDown(),
            _ => true,
        };
        var palette = dark ? Palette.Dark : Palette.Light;
        Color accent = a.AccentSource switch
        {
            AccentSource.Windows => Colors2.Hex(windowsAccentHex),
            AccentSource.Custom => SafeHex(a.CustomAccent),
            AccentSource.Wallpaper when wallpaperAccentHex != null => Colors2.Hex(wallpaperAccentHex),
            _ => AccentPresets.Resolve(a.AccentPreset, dark),
        };
        Apply(palette, accent);
    }

    private static Color SafeHex(string s) { try { return Colors2.Hex(s); } catch { return AccentPresets.Resolve("aurora", true); } }

    /// <summary>Sun-based theme: dark between sunset and sunrise of the selected weather location (cached data).</summary>
    public static Func<bool>? SunIsDownProvider { get; set; }
    private static bool SunIsDown() => SunIsDownProvider?.Invoke() ?? (DateTime.Now.Hour < 7 || DateTime.Now.Hour >= 19);

    /// <summary>Temporarily tints accent-driven elements with album-art colour (null restores the configured accent).</summary>
    public void SetMediaAccent(Color? c)
    {
        MediaAccent = c;
        Apply(Palette, Accent);
    }

    public void Apply(Palette p, Color accent)
    {
        Palette = p;
        Accent = accent;
        var effectiveAccent = accent;
        var onAccent = Colors2.Luminance(effectiveAccent) > 0.45 ? p.Ink : Color.FromArgb(255, 255, 255, 255);
        void Set(string k, Color c) => Brush(k).Color = c;
        Set("Vos.Base", p.Base); Set("Vos.Surface1", p.Surface1); Set("Vos.Surface2", p.Surface2); Set("Vos.Surface3", p.Surface3);
        Set("Vos.StrokeSubtle", p.StrokeSubtle); Set("Vos.Stroke", p.Stroke); Set("Vos.StrokeStrong", p.StrokeStrong);
        Set("Vos.TextPrimary", p.TextPrimary); Set("Vos.TextSecondary", p.TextSecondary); Set("Vos.TextTertiary", p.TextTertiary); Set("Vos.TextDisabled", p.TextDisabled);
        Set("Vos.Control", p.IsDark ? Color.FromArgb(0x10, 255, 255, 255) : Color.FromArgb(0xB3, 255, 255, 255));
        Set("Vos.ControlHover", p.IsDark ? Color.FromArgb(0x1A, 255, 255, 255) : Color.FromArgb(0x0D, 0, 0, 0));
        Set("Vos.ControlPressed", p.IsDark ? Color.FromArgb(0x0C, 255, 255, 255) : Color.FromArgb(0x16, 0, 0, 0));
        Set("Vos.Accent", effectiveAccent);
        Set("Vos.AccentHover", p.IsDark ? Colors2.Lighten(effectiveAccent, 0.10) : Colors2.Darken(effectiveAccent, 0.08));
        Set("Vos.AccentPressed", p.IsDark ? Colors2.Darken(effectiveAccent, 0.10) : Colors2.Darken(effectiveAccent, 0.16));
        Set("Vos.AccentSubtle", Colors2.WithAlpha(effectiveAccent, 0x26));
        Set("Vos.AccentGlow", Colors2.WithAlpha(effectiveAccent, 0x55));
        Set("Vos.AccentText", Colors2.EnsureContrast(effectiveAccent, p.Surface1, 4.5));
        Set("Vos.OnAccent", onAccent);
        Set("Vos.MediaAccent", MediaAccent ?? effectiveAccent);
        Set("Vos.Ink", p.Ink);
        Set("Vos.InkText", Colors2.Hex("#ECEEF2"));
        Set("Vos.InkTextSecondary", Colors2.Hex("#9298A3"));
        Set("Vos.InkHover", Color.FromArgb(0x24, 255, 255, 255));
        Set("Vos.InkPressed", Color.FromArgb(0x14, 255, 255, 255));
        Set("Vos.InkTrack", Color.FromArgb(0x33, 255, 255, 255));
        Set("Vos.Scrim", p.Scrim);
        Set("Vos.GlassTint", Colors2.WithAlpha(p.GlassTint, (byte)(p.IsDark ? 0x8C : 0x99)));
        Set("Vos.GlassSolid", p.IsDark ? Colors2.Hex("#1A1B20") : Colors2.Hex("#F7F8FA"));
        Set("Vos.GlassRim", p.IsDark ? Color.FromArgb(0x24, 255, 255, 255) : Color.FromArgb(0x40, 255, 255, 255));
        Set("Vos.Success", p.Success); Set("Vos.Warning", p.Warning); Set("Vos.Critical", p.Critical);
        // App content layer over Mica, and cards on that layer (Windows 11 layering, V-OS graphite tones).
        Set("Vos.Layer", p.IsDark ? Color.FromArgb(0x52, 0x22, 0x24, 0x2A) : Color.FromArgb(0x80, 255, 255, 255));
        Set("Vos.Card", p.IsDark ? Color.FromArgb(0x0E, 255, 255, 255) : Color.FromArgb(0xB3, 255, 255, 255));
        Set("Vos.CardHover", p.IsDark ? Color.FromArgb(0x16, 255, 255, 255) : Color.FromArgb(0xE6, 255, 255, 255));
        Set("Vos.SuccessSubtle", Colors2.WithAlpha(p.Success, 0x24)); Set("Vos.WarningSubtle", Colors2.WithAlpha(p.Warning, 0x24)); Set("Vos.CriticalSubtle", Colors2.WithAlpha(p.Critical, 0x24));
        RetintControlBrushes(effectiveAccent, Brush("Vos.AccentHover").Color, Brush("Vos.AccentPressed").Color, Brush("Vos.AccentText").Color, onAccent);
        foreach (var r in _roots.ToList())
        {
            if (r.TryGetTarget(out var fe)) fe.RequestedTheme = ElementTheme;
            else _roots.Remove(r);
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Standard control templates hold references to the brush instances in XamlControlsResources (resolved when that
    /// dictionary loads), so keys added later never reach them. Re-colour those shared instances instead: every
    /// control that uses the accent (toggles, sliders, check boxes, radio buttons, accent buttons, links, dialogs,
    /// popups) follows the V-OS accent, at start and after every change.
    /// </summary>
    private static void RetintControlBrushes(Color accent, Color hover, Color pressed, Color text, Color onAccent)
    {
        if (Application.Current?.Resources is not { } app) return;
        foreach (var dict in app.MergedDictionaries.Append(app))
            foreach (var themeKey in new[] { "Default", "Dark", "Light" })
            {
                if (!dict.ThemeDictionaries.TryGetValue(themeKey, out var o) || o is not ResourceDictionary td) continue;
                Tint(td, "AccentFillColorDefaultBrush", accent);
                Tint(td, "AccentFillColorSecondaryBrush", hover);
                Tint(td, "AccentFillColorTertiaryBrush", pressed);
                Tint(td, "AccentFillColorSelectedTextBackgroundBrush", accent);
                Tint(td, "AccentTextFillColorPrimaryBrush", text);
                Tint(td, "AccentTextFillColorSecondaryBrush", text);
                Tint(td, "AccentTextFillColorTertiaryBrush", hover);
                Tint(td, "TextOnAccentFillColorPrimaryBrush", onAccent);
                Tint(td, "TextOnAccentFillColorSecondaryBrush", Colors2.WithAlpha(onAccent, 0xB3));
                Tint(td, "SystemControlHighlightAccentBrush", accent);
                Tint(td, "SystemControlForegroundAccentBrush", text);
                Tint(td, "SystemControlBackgroundAccentBrush", accent);
                // The focused text-box underline is a gradient whose first stop is the accent colour.
                if (Find(td, "TextControlElevationBorderFocusedBrush", 0) is LinearGradientBrush focus && focus.GradientStops.Count > 0)
                    focus.GradientStops[0].Color = accent;
            }
    }

    private static void Tint(ResourceDictionary d, string key, Color c)
    {
        if (Find(d, key, 0) is SolidColorBrush b && b.Color != c) b.Color = c;
    }

    private static object? Find(ResourceDictionary d, string key, int depth)
    {
        if (d.TryGetValue(key, out var v)) return v;
        if (depth > 4) return null;
        foreach (var m in d.MergedDictionaries) if (Find(m, key, depth + 1) is { } x) return x;
        return null;
    }

    /// <summary>Stops syncing a root's RequestedTheme (for surfaces that keep their own, e.g. full-bleed dark content).</summary>
    public void Untrack(FrameworkElement root) =>
        _roots.RemoveAll(r => !r.TryGetTarget(out var fe) || ReferenceEquals(fe, root));

    /// <summary>Keeps a window root's RequestedTheme in sync (for built-in control templates).</summary>
    public void Track(FrameworkElement root)
    {
        root.RequestedTheme = ElementTheme;
        _roots.Add(new WeakReference<FrameworkElement>(root));
    }
}

/// <summary>Spacing, radii and type scale (logical pixels).</summary>
public static class Tokens
{
    public const double Space1 = 4, Space2 = 8, Space3 = 12, Space4 = 16, Space5 = 24, Space6 = 32, Space7 = 48;
    public const double RadiusSmall = 6, RadiusControl = 8, RadiusCard = 14, RadiusPanel = 18, RadiusWindow = 12;
    public const string FontText = "Segoe UI Variable Text, Segoe UI";
    public const string FontDisplay = "Segoe UI Variable Display, Segoe UI";
    public const string FontSmall = "Segoe UI Variable Small, Segoe UI";
    public const string FontInstrument = "Bahnschrift, Segoe UI Variable Display, Segoe UI";
    public const string FontIcons = "Segoe Fluent Icons, Segoe MDL2 Assets";
    public static readonly FontFamily Text = new(FontText), Display = new(FontDisplay), Small = new(FontSmall), Instrument = new(FontInstrument), Icons = new(FontIcons);
}
