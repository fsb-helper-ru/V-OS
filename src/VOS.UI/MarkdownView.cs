using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using VOS.Core.Assistant;
using VOS.Platform.Shell;
using VOS.UI.Theme;

namespace VOS.UI;

/// <summary>Renders the chat Markdown subset (<see cref="Markdown"/>) as selectable native text. Links open in the
/// user's browser and are limited to http and https by the parser.</summary>
public static class MarkdownView
{
    private static readonly FontFamily s_mono = new("Cascadia Mono, Consolas, Courier New");

    public static StackPanel Render(string markdown, double size = 14.5)
    {
        var panel = new StackPanel();
        MdBlockKind? previous = null;
        foreach (var block in Markdown.Parse(markdown))
        {
            var element = (FrameworkElement)Block(block, size);
            bool listContinues = block.Kind is MdBlockKind.Bullet or MdBlockKind.Numbered && previous is MdBlockKind.Bullet or MdBlockKind.Numbered;
            if (previous != null)
            {
                var m = element.Margin;
                element.Margin = new Thickness(m.Left, m.Top + (listContinues ? 3 : 9), m.Right, m.Bottom);
            }
            panel.Children.Add(element);
            previous = block.Kind;
        }
        return panel;
    }

    private static UIElement Block(MdBlock b, double size)
    {
        switch (b.Kind)
        {
            case MdBlockKind.Code:
                var code = new TextBlock
                {
                    Text = b.Code ?? "", FontFamily = s_mono, FontSize = size - 1.5, IsTextSelectionEnabled = true,
                    Foreground = Ui.B("Vos.TextPrimary"), TextWrapping = TextWrapping.NoWrap,
                };
                return new Border
                {
                    Background = Ui.B("Vos.Control"), CornerRadius = new CornerRadius(Tokens.RadiusControl), Padding = new Thickness(12, 10, 12, 10),
                    Child = new ScrollViewer
                    {
                        Content = code, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Auto,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollMode = ScrollMode.Disabled,
                    },
                };
            case MdBlockKind.Rule:
                return new Border { Height = 1, Background = Ui.B("Vos.StrokeSubtle"), Margin = new Thickness(0, 4, 0, 4) };
            case MdBlockKind.Bullet or MdBlockKind.Numbered:
                var row = new Grid { Margin = new Thickness(b.Level * 18 + 2, 0, 0, 0), ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var marker = Ui.Text(b.Kind == MdBlockKind.Bullet ? "•" : b.Marker ?? "", size, "Vos.TextSecondary");
                marker.MinWidth = b.Kind == MdBlockKind.Bullet ? 8 : 16;
                row.Children.Add(marker);
                var item = Rich(b.Inlines, size);
                Grid.SetColumn(item, 1);
                row.Children.Add(item);
                return row;
            case MdBlockKind.Quote:
                return new Border
                {
                    BorderBrush = Ui.B("Vos.StrokeStrong"), BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(10, 2, 0, 2),
                    Child = Rich(b.Inlines, size, "Vos.TextSecondary"),
                };
            case MdBlockKind.Heading:
                var heading = Rich(b.Inlines, b.Level <= 1 ? size + 5 : b.Level == 2 ? size + 3 : size + 1);
                heading.FontWeight = FontWeights.SemiBold;
                heading.FontFamily = Tokens.Display;
                heading.Margin = new Thickness(0, 4, 0, 0);
                return heading;
            default:
                return Rich(b.Inlines, size);
        }
    }

    private static RichTextBlock Rich(IReadOnlyList<MdInline> inlines, double size, string brush = "Vos.TextPrimary")
    {
        var rich = new RichTextBlock
        {
            FontSize = size, FontFamily = Tokens.Text, Foreground = Ui.B(brush), IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.Wrap, LineHeight = Math.Round(size * 1.45),
        };
        var p = new Paragraph();
        foreach (var inline in inlines)
        {
            switch (inline.Kind)
            {
                case MdInlineKind.Bold: AddText(p.Inlines, inline.Text, r => r.FontWeight = FontWeights.SemiBold); break;
                case MdInlineKind.Italic: AddText(p.Inlines, inline.Text, r => r.FontStyle = global::Windows.UI.Text.FontStyle.Italic); break;
                case MdInlineKind.Code: AddText(p.Inlines, inline.Text, r => { r.FontFamily = s_mono; r.Foreground = Ui.B("Vos.AccentText"); }); break;
                case MdInlineKind.Link:
                    var url = inline.Url!;
                    var link = new Hyperlink { Foreground = Ui.B("Vos.AccentText") };
                    link.Click += (_, _) => ShellOps.OpenUrl(url);
                    AddText(link.Inlines, inline.Text, null);
                    p.Inlines.Add(link);
                    break;
                default: AddText(p.Inlines, inline.Text, null); break;
            }
        }
        rich.Blocks.Add(p);
        return rich;
    }

    private static void AddText(InlineCollection target, string text, Action<Run>? style)
    {
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0) target.Add(new LineBreak());
            if (lines[i].Length == 0) continue;
            var run = new Run { Text = lines[i] };
            style?.Invoke(run);
            target.Add(run);
        }
    }
}
