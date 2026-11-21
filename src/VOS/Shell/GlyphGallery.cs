using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VOS.UI;
using VOS.UI.Theme;

namespace VOS.Shell;

/// <summary>Developer diagnostic (VOS.exe --glyph-test): renders every glyph constant with its name for visual QA.</summary>
public static class GlyphGallery
{
    public static void Show()
    {
        var frame = new AppFrame(VosApp.All[^1], "Glyph gallery", 900, 700, 1200, 820);
        var grid = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, ItemWidth = 118, ItemHeight = 64, Margin = new Thickness(16) };
        var entries = new List<(string Name, string Value)>();
        bool candidatesOnly = Environment.GetCommandLineArgs().Contains("--candidates");
        if (!candidatesOnly)
            entries.AddRange(typeof(Glyphs).GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.FieldType == typeof(string))
                .Select(f => (f.Name, (string)f.GetValue(null)!)));
        int[] candidates = [0xE7E7, 0xE7ED, 0xE91C, 0xEA8F, 0xE789, 0xED0C, 0xE8BD, 0xE711, 0xEC42, 0xE9A8, 0xE8F2, 0xF2A3, 0xE7F8, 0xE8E1, 0xE753,
            0xE9BE, 0xE9BF, 0xE9C0, 0xE9C1, 0xE9C2, 0xE9C3, 0xE9C4, 0xE9C5, 0xE9C6, 0xE9C7, 0xE9C8, 0xE9C9, 0xE9CA, 0xE9CB, 0xE9CC, 0xE9CD, 0xE9CE, 0xE9CF,
            0xF0C9, 0xF08D, 0xE706, 0xE708, 0xEA3A, 0xE7F4, 0xF4A5, 0xF5B0, 0xEE57, 0xE9D9, 0xEC4A, 0xE945, 0xE916, 0xF0E3, 0xED5A, 0xED5B, 0xED5C, 0xED5D,
            0xE8EF, 0xE8AB, 0xE9F5, 0xE8B1, 0xE8EE, 0xE8ED, 0xE950, 0xE964, 0xEDA2, 0xE968, 0xE821, 0xE82D, 0xF012, 0xEA86, 0xE8EA, 0xEC48, 0xEBA0, 0xEBAA, 0xEBAB];
        foreach (var code in candidates) entries.Add(($"U+{code:X4}", ((char)code).ToString()));
        foreach (var (name, glyph) in entries)
        {
            var cell = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center };
            cell.Children.Add(new FontIcon { Glyph = glyph, FontFamily = Tokens.Icons, FontSize = 22, Foreground = Ui.B("Vos.TextPrimary") });
            cell.Children.Add(new TextBlock { Text = name, FontSize = 10.5, Foreground = Ui.B("Vos.TextSecondary"), HorizontalAlignment = HorizontalAlignment.Center });
            grid.Children.Add(cell);
        }
        frame.Body.Content = new ScrollViewer { Content = grid };
        frame.Activate();
    }
}
