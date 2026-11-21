using System.Text;
using System.Text.RegularExpressions;

namespace VOS.Core.Assistant;

public enum MdBlockKind { Paragraph, Heading, Bullet, Numbered, Quote, Code, Rule }
public enum MdInlineKind { Text, Bold, Italic, Code, Link }

public sealed record MdInline(MdInlineKind Kind, string Text, string? Url = null);

/// <summary>One block. <see cref="Level"/> is the heading level or the list nesting depth; <see cref="Marker"/> the
/// list number ("3."); <see cref="Code"/> the verbatim text of a code block.</summary>
public sealed record MdBlock(MdBlockKind Kind, IReadOnlyList<MdInline> Inlines, int Level = 0, string? Marker = null, string? Code = null);

/// <summary>
/// The small Markdown subset chat answers use: paragraphs, headings, bullet and numbered lists, quotes, rules, fenced
/// code, and inline bold, italic, code and links. Tables stay verbatim (shown as code). Links are kept only for http
/// and https, so a reply cannot produce a link that runs something.
/// </summary>
public static partial class Markdown
{
    public static List<MdBlock> Parse(string text)
    {
        var blocks = new List<MdBlock>();
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var paragraph = new StringBuilder();

        void FlushParagraph()
        {
            if (paragraph.Length == 0) return;
            blocks.Add(new MdBlock(MdBlockKind.Paragraph, ParseInlines(paragraph.ToString())));
            paragraph.Clear();
        }

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();

            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                FlushParagraph();
                var fence = trimmed[..3];
                var code = new StringBuilder();
                for (i++; i < lines.Length && !lines[i].Trim().StartsWith(fence, StringComparison.Ordinal); i++)
                    code.Append(code.Length > 0 ? "\n" : "").Append(lines[i]);
                blocks.Add(new MdBlock(MdBlockKind.Code, [], Code: code.ToString()));
                continue;
            }
            if (trimmed.Length == 0) { FlushParagraph(); continue; }
            if (trimmed.StartsWith('|'))
            {
                FlushParagraph();
                var table = new StringBuilder(trimmed);
                while (i + 1 < lines.Length && lines[i + 1].Trim().StartsWith('|')) table.Append('\n').Append(lines[++i].Trim());
                blocks.Add(new MdBlock(MdBlockKind.Code, [], Code: table.ToString()));
                continue;
            }
            if (RuleRegex().IsMatch(trimmed)) { FlushParagraph(); blocks.Add(new MdBlock(MdBlockKind.Rule, [])); continue; }

            var heading = HeadingRegex().Match(trimmed);
            if (heading.Success)
            {
                FlushParagraph();
                blocks.Add(new MdBlock(MdBlockKind.Heading, ParseInlines(heading.Groups[2].Value.TrimEnd('#', ' ')), heading.Groups[1].Length));
                continue;
            }
            int indent = line.Length - line.TrimStart().Length;
            var bullet = BulletRegex().Match(trimmed);
            if (bullet.Success)
            {
                FlushParagraph();
                blocks.Add(new MdBlock(MdBlockKind.Bullet, ParseInlines(bullet.Groups[1].Value), indent / 2));
                continue;
            }
            var numbered = NumberedRegex().Match(trimmed);
            if (numbered.Success)
            {
                FlushParagraph();
                blocks.Add(new MdBlock(MdBlockKind.Numbered, ParseInlines(numbered.Groups[2].Value), indent / 2, numbered.Groups[1].Value + "."));
                continue;
            }
            if (trimmed.StartsWith('>'))
            {
                FlushParagraph();
                blocks.Add(new MdBlock(MdBlockKind.Quote, ParseInlines(trimmed.TrimStart('>').Trim())));
                continue;
            }
            // Chat answers use single line breaks on purpose; keep them inside the paragraph.
            if (paragraph.Length > 0) paragraph.Append('\n');
            paragraph.Append(trimmed);
        }
        FlushParagraph();
        return blocks;
    }

    public static List<MdInline> ParseInlines(string text)
    {
        var result = new List<MdInline>();
        var plain = new StringBuilder();
        void Flush() { if (plain.Length > 0) { result.Add(new MdInline(MdInlineKind.Text, plain.ToString())); plain.Clear(); } }

        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '\\' && i + 1 < text.Length && IsMarker(text[i + 1])) { plain.Append(text[i + 1]); i += 2; continue; }
            if (c == '`')
            {
                int end = text.IndexOf('`', i + 1);
                if (end > i + 1) { Flush(); result.Add(new MdInline(MdInlineKind.Code, text[(i + 1)..end])); i = end + 1; continue; }
            }
            if ((c == '*' || c == '_') && i + 1 < text.Length && text[i + 1] == c)
            {
                var marker = new string(c, 2);
                int end = text.IndexOf(marker, i + 2, StringComparison.Ordinal);
                if (end > i + 2 && !char.IsWhiteSpace(text[i + 2]) && !char.IsWhiteSpace(text[end - 1]))
                {
                    Flush();
                    result.Add(new MdInline(MdInlineKind.Bold, StripMarkers(text[(i + 2)..end])));
                    i = end + 2;
                    continue;
                }
            }
            if ((c == '*' || c == '_') && i + 1 < text.Length && !char.IsWhiteSpace(text[i + 1]) && (c == '*' || i == 0 || !char.IsLetterOrDigit(text[i - 1])))
            {
                int end = i + 1;
                while ((end = text.IndexOf(c, end)) > 0 && (char.IsWhiteSpace(text[end - 1]) || (c == '_' && end + 1 < text.Length && char.IsLetterOrDigit(text[end + 1])))) end++;
                if (end > i + 1)
                {
                    Flush();
                    result.Add(new MdInline(MdInlineKind.Italic, StripMarkers(text[(i + 1)..end])));
                    i = end + 1;
                    continue;
                }
            }
            if (c == '[')
            {
                var link = LinkRegex().Match(text, i);
                if (link.Success && link.Index == i)
                {
                    Flush();
                    var url = link.Groups[2].Value.Trim();
                    bool safe = Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
                    result.Add(safe ? new MdInline(MdInlineKind.Link, StripMarkers(link.Groups[1].Value), uri!.AbsoluteUri) : new MdInline(MdInlineKind.Text, link.Groups[1].Value));
                    i += link.Length;
                    continue;
                }
            }
            plain.Append(c);
            i++;
        }
        Flush();
        return result;
    }

    /// <summary>Plain text of a block list (copying an answer gives readable text without Markdown syntax).</summary>
    public static string ToPlainText(IEnumerable<MdBlock> blocks)
    {
        var sb = new StringBuilder();
        foreach (var b in blocks)
        {
            if (sb.Length > 0) sb.Append('\n');
            var inline = string.Concat(b.Inlines.Select(x => x.Text));
            sb.Append(b.Kind switch
            {
                MdBlockKind.Code => b.Code,
                MdBlockKind.Bullet => new string(' ', b.Level * 2) + "• " + inline,
                MdBlockKind.Numbered => new string(' ', b.Level * 2) + b.Marker + " " + inline,
                MdBlockKind.Rule => "—",
                _ => inline,
            });
        }
        return sb.ToString();
    }

    private static bool IsMarker(char c) => c is '*' or '_' or '`' or '[' or ']' or '\\' or '#';
    private static string StripMarkers(string s) => s.Replace("**", "").Replace("__", "");

    [GeneratedRegex(@"^(#{1,6})\s+(.+)$")] private static partial Regex HeadingRegex();
    [GeneratedRegex(@"^[-*+]\s+(.*)$")] private static partial Regex BulletRegex();
    [GeneratedRegex(@"^(\d{1,3})[.)]\s+(.*)$")] private static partial Regex NumberedRegex();
    [GeneratedRegex(@"^(?:-{3,}|\*{3,}|_{3,})$")] private static partial Regex RuleRegex();
    [GeneratedRegex(@"\[([^\]\n]+)\]\(([^)\s]+)\)")] private static partial Regex LinkRegex();
}
