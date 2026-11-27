using System.Text;
using System.Text.RegularExpressions;

namespace Niskala.Updater;

public enum NoteKind
{
    Title,
    Heading,
    Subheading,
    Paragraph,
    Bullet,
}

public sealed record NoteLine(NoteKind Kind, string Text, int Level = 0, string Marker = "");

public static partial class PatchNotes
{
    public const string FileName = "PATCHNOTES.md";

    internal const string ResourceName = "Niskala.Updater.PATCHNOTES.md";

    private const int MaxLevel = 3;
    private const int IndentWidth = 2;
    private const string BulletMarker = "•";
    private const string CellSeparator = ", ";
    private const string Fence = "```";
    private const string AlternateFence = "~~~";
    private const char EscapeBase = '\uE000';
    private const int AsciiLimit = 128;

    public static string? Bundled() => Bundled(AppContext.BaseDirectory);

    internal static string? Bundled(string directory) => Embedded() ?? ReadFile(Path.Combine(directory, FileName));

    internal static string? Embedded()
    {
        using Stream? stream = typeof(PatchNotes).Assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    internal static string? ReadFile(string file)
    {
        try
        {
            return File.Exists(file) ? File.ReadAllText(file) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static IReadOnlyList<NoteLine> Parse(string? markdown)
    {
        var lines = new List<NoteLine>();
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return lines;
        }

        string[] source = markdown.ReplaceLineEndings("\n").Split('\n');
        var paragraph = new StringBuilder();
        string[]? header = null;
        bool inFence = false;
        bool inComment = false;

        void FlushParagraph()
        {
            if (paragraph.Length > 0)
            {
                lines.Add(new NoteLine(NoteKind.Paragraph, Plain(paragraph.ToString())));
                paragraph.Clear();
            }
        }

        for (int i = 0; i < source.Length; i++)
        {
            string raw = source[i];
            string line = raw.Trim();

            if (inComment)
            {
                inComment = !line.Contains("-->", StringComparison.Ordinal);
                continue;
            }

            if (line.StartsWith(Fence, StringComparison.Ordinal) || line.StartsWith(AlternateFence, StringComparison.Ordinal))
            {
                FlushParagraph();
                inFence = !inFence;
                continue;
            }

            if (inFence)
            {
                if (line.Length > 0)
                {
                    lines.Add(new NoteLine(NoteKind.Paragraph, raw.TrimEnd()));
                }

                continue;
            }

            if (line.StartsWith("<!--", StringComparison.Ordinal))
            {
                FlushParagraph();
                inComment = !line.Contains("-->", StringComparison.Ordinal);
                continue;
            }

            if (line.Length == 0 || RuleLine().IsMatch(line))
            {
                FlushParagraph();
                header = null;
                continue;
            }

            if (line.StartsWith('|'))
            {
                FlushParagraph();
                string[] cells = Cells(line);
                if (TableSeparator().IsMatch(line))
                {
                    continue;
                }

                if (header is null && i + 1 < source.Length && TableSeparator().IsMatch(source[i + 1].Trim()))
                {
                    header = cells;
                    continue;
                }

                if (TableRow(cells, header) is { Length: > 0 } row)
                {
                    lines.Add(new NoteLine(NoteKind.Bullet, row, 0, BulletMarker));
                }

                continue;
            }

            header = null;

            if (HeadingLine().Match(line) is { Success: true } heading)
            {
                FlushParagraph();
                NoteKind kind = heading.Groups[1].Length switch
                {
                    1 => NoteKind.Title,
                    2 => NoteKind.Heading,
                    _ => NoteKind.Subheading,
                };
                lines.Add(new NoteLine(kind, Plain(heading.Groups[2].Value)));
                continue;
            }

            if (ListLine().Match(raw) is { Success: true } item)
            {
                FlushParagraph();
                int indent = item.Groups[1].Value.Replace("\t", new string(' ', IndentWidth * 2), StringComparison.Ordinal).Length;
                string marker = item.Groups[2].Value is "-" or "*" or "+" ? BulletMarker : item.Groups[2].Value.Replace(')', '.');
                lines.Add(new NoteLine(NoteKind.Bullet, Plain(item.Groups[3].Value), Math.Min(indent / IndentWidth, MaxLevel), marker));
                continue;
            }

            string text = line.StartsWith('>') ? line.TrimStart('>').Trim() : line;
            if (paragraph.Length == 0 && raw.StartsWith(' ') && lines.Count > 0 && lines[^1].Kind == NoteKind.Bullet)
            {
                NoteLine last = lines[^1];
                lines[^1] = last with { Text = $"{last.Text} {Plain(text)}" };
                continue;
            }

            if (paragraph.Length > 0)
            {
                paragraph.Append(' ');
            }

            paragraph.Append(text);
        }

        FlushParagraph();
        return lines;
    }

    internal static string Plain(string text)
    {
        text = AutoLink().Replace(Link().Replace(Image().Replace(text, "$1"), "$1"), "$1");
        var output = new StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] == '`')
            {
                int ticks = Run(text, i, '`');
                int close = text.IndexOf(new string('`', ticks), i + ticks, StringComparison.Ordinal);
                if (close > 0)
                {
                    output.Append(text.AsSpan(i + ticks, close - i - ticks).Trim());
                    i = close + ticks;
                    continue;
                }

                output.Append(text, i, ticks);
                i += ticks;
                continue;
            }

            int next = text.IndexOf('`', i);
            int end = next < 0 ? text.Length : next;
            output.Append(Inline(text[i..end]));
            i = end;
        }

        return Whitespace().Replace(output.ToString(), " ").Trim();
    }

    private static string Inline(string text)
    {
        text = Escape().Replace(text, m => ((char)(EscapeBase + m.Groups[1].Value[0])).ToString());
        text = Tag().Replace(text, string.Empty);
        text = Strong().Replace(text, "$2");
        text = Strike().Replace(text, "$1");
        text = Emphasis().Replace(text, "$2");
        return string.Create(text.Length, text, static (span, source) =>
        {
            for (int i = 0; i < source.Length; i++)
            {
                char c = source[i];
                span[i] = c >= EscapeBase && c < EscapeBase + AsciiLimit ? (char)(c - EscapeBase) : c;
            }
        });
    }

    private static string[] Cells(string line)
    {
        var cells = new List<string>();
        var cell = new StringBuilder();
        bool inCode = false;
        string body = line.Trim();
        body = body.StartsWith('|') ? body[1..] : body;
        body = body.EndsWith('|') && !body.EndsWith("\\|", StringComparison.Ordinal) ? body[..^1] : body;
        for (int i = 0; i < body.Length; i++)
        {
            char c = body[i];
            if (c == '\\' && i + 1 < body.Length && body[i + 1] == '|')
            {
                cell.Append('|');
                i++;
            }
            else if (c == '`')
            {
                inCode = !inCode;
                cell.Append(c);
            }
            else if (c == '|' && !inCode)
            {
                cells.Add(Plain(cell.ToString()));
                cell.Clear();
            }
            else
            {
                cell.Append(c);
            }
        }

        cells.Add(Plain(cell.ToString()));
        return [.. cells];
    }

    private static string TableRow(string[] cells, string[]? header)
    {
        string[] filled = [.. cells.Where(c => c.Length > 0)];
        if (filled.Length == 0)
        {
            return string.Empty;
        }

        if (header is null || header.Length != cells.Length || cells.Length < 2)
        {
            return string.Join(CellSeparator, filled);
        }

        IEnumerable<string> values = cells.Skip(1)
            .Select((value, index) => (Label: header[index + 1], Value: value))
            .Where(pair => pair.Value.Length > 0)
            .Select(pair => pair.Label.Length > 0 ? $"{pair.Label} {pair.Value}" : pair.Value);
        string rest = string.Join(CellSeparator, values);
        return cells[0].Length == 0 ? rest : rest.Length == 0 ? cells[0] : $"{cells[0]}: {rest}";
    }

    private static int Run(string text, int start, char c)
    {
        int end = start;
        while (end < text.Length && text[end] == c)
        {
            end++;
        }

        return end - start;
    }

    [GeneratedRegex(@"^(#{1,6})\s+(.*?)\s*#*\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex HeadingLine();

    [GeneratedRegex(@"^(\s*)([-*+]|\d{1,9}[.)])\s+(.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex ListLine();

    [GeneratedRegex(@"^\|?\s*:?-+:?\s*(\|\s*:?-+:?\s*)*\|?$", RegexOptions.CultureInvariant)]
    private static partial Regex TableSeparator();

    [GeneratedRegex(@"^([-*_])(\s*\1){2,}$", RegexOptions.CultureInvariant)]
    private static partial Regex RuleLine();

    [GeneratedRegex(@"!\[([^\]]*)\]\([^)]*\)", RegexOptions.CultureInvariant)]
    private static partial Regex Image();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]*\)", RegexOptions.CultureInvariant)]
    private static partial Regex Link();

    [GeneratedRegex(@"<((?:https?|mailto):[^>\s]+)>", RegexOptions.CultureInvariant)]
    private static partial Regex AutoLink();

    [GeneratedRegex(@"</?[A-Za-z][A-Za-z0-9-]*(\s[^<>]*)?/?>", RegexOptions.CultureInvariant)]
    private static partial Regex Tag();

    [GeneratedRegex(@"(\*\*|__)(?=\S)(.+?)(?<=\S)\1", RegexOptions.CultureInvariant)]
    private static partial Regex Strong();

    [GeneratedRegex(@"~~(?=\S)(.+?)(?<=\S)~~", RegexOptions.CultureInvariant)]
    private static partial Regex Strike();

    [GeneratedRegex(@"(?<![\w*])([*_])(?=\S)(.+?)(?<=\S)\1(?![\w*])", RegexOptions.CultureInvariant)]
    private static partial Regex Emphasis();

    [GeneratedRegex(@"\\([!-/:-@\[-`{-~])", RegexOptions.CultureInvariant)]
    private static partial Regex Escape();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();
}
