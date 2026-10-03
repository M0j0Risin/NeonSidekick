using System.Globalization;
using NeonSidekick.Files;
using NeonSidekick.Git;
using NeonSidekick.UI.Markdown;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace NeonSidekick.UI;

/// <summary>
/// A file edit's diff under its 🛠️ line (2026-10-03, the user's ask, Claude Code's look): the <paramref name="head"/> line (the
/// edit's note), then <c>└ Added 3 lines, removed 1 line</c> and the hunks' rows — the line number right-aligned in a gutter
/// (the old one for a removed line, the new one otherwise), the sign, the text. An added or removed row sits on its slab
/// (<see cref="Theme.DiffAdded"/>/<see cref="Theme.DiffRemoved"/>) to the right edge, wrapped rows included; the text keeps the
/// colours of its language (<see cref="CodeLexer"/>, each side of a hunk lexed as one text so a block comment colours every
/// line it spans) and only takes the slab's background. A dim <c>⋮</c> parts two hunks; past <paramref name="maxLines"/> rows
/// a <c>… 12 more lines</c> ends it (0: the header alone). Long lines wrap at the cell (a tab is four), under the text column.
/// Everything is one write, so a tool run counts it once (<see cref="Scrollback"/>'s units).
/// </summary>
public sealed class DiffView : IRenderable
{
    /// <summary>What stands before every row: the width of the tools' glyph, blank (<see cref="TranscriptRenderer.ToolAnswerIndent"/>).</summary>
    public const string Indent = TranscriptRenderer.ToolAnswerIndent;

    /// <summary>The header's elbow, Claude Code's.</summary>
    public const string Elbow = "└ ";

    /// <summary>The row between two hunks, under the gutter.</summary>
    public const string HunkGap = "⋮";

    private readonly IRenderable? _head;
    private readonly FileDiff _diff;
    private readonly int _maxLines;
    private readonly CodeLanguage? _language;

    public DiffView(IRenderable? head, FileDiff diff, int maxLines)
    {
        _head = head;
        _diff = diff ?? throw new ArgumentNullException(nameof(diff));
        _maxLines = Math.Max(0, maxLines);
        _language = LanguageOf(diff.Path);
    }

    /// <summary>The language the file's extension names (<see cref="CodeLanguages.Find"/>); none for a patch file, whose own signs would colour it.</summary>
    public static CodeLanguage? LanguageOf(string path)
    {
        string extension = Path.GetExtension(path ?? "").TrimStart('.');
        return extension.Length > 0 && CodeLanguages.Find(extension) is { } language && language.Mode != CodeLexMode.Diff ? language : null;
    }

    /// <summary>One row of a hunk: its sign (<c>' '</c>, <c>'+'</c>, <c>'-'</c>), its number, and its text with each run's style.</summary>
    public readonly record struct Row(char Sign, int Number, IReadOnlyList<(string Text, Style Style)> Pieces);

    /// <summary>The hunks' rows in order, a null between two hunks; the no-newline markers left out.</summary>
    public IReadOnlyList<Row?> Rows() => RowsOf(_diff, _language);

    public Measurement Measure(RenderOptions options, int maxWidth) => new(Math.Min(maxWidth, Indent.Length + 8), maxWidth);

    public IEnumerable<Segment> Render(RenderOptions options, int maxWidth)
    {
        var segments = new List<Segment>();
        if (_head is not null)
        {
            var head = _head.Render(options, maxWidth).Where(s => !s.IsLineBreak).ToList();
            segments.AddRange(head);
            segments.Add(Segment.LineBreak);
        }

        var dim = new Style(foreground: Theme.Dim);
        segments.Add(new Segment(Indent + Elbow + FileText.DiffSummary(_diff), dim));
        segments.Add(Segment.LineBreak);
        if (_maxLines == 0)
        {
            return segments;
        }

        var rows = Rows();
        int total = rows.Count(r => r is not null);
        var shown = new List<Row?>();
        int taken = 0;
        foreach (var row in rows)
        {
            if (row is not null && taken == _maxLines)
            {
                break;
            }

            shown.Add(row);
            taken += row is null ? 0 : 1;
        }

        int gutter = shown.Where(r => r is not null).Select(r => r!.Value.Number.ToString(CultureInfo.InvariantCulture).Length).DefaultIfEmpty(1).Max();
        foreach (var row in shown)
        {
            if (row is not { } line)
            {
                segments.Add(new Segment(Indent + HunkGap.PadLeft(gutter), dim));
                segments.Add(Segment.LineBreak);
                continue;
            }

            RenderRow(segments, line, gutter, maxWidth);
        }

        if (taken < total)
        {
            segments.Add(new Segment(Indent + FileText.DiffMore(total - taken), dim));
            segments.Add(Segment.LineBreak);
        }

        return segments;
    }

    /// <summary>One hunk row, wrapped at the cell under its text column, a slab row filled to <paramref name="maxWidth"/>.</summary>
    private static void RenderRow(List<Segment> segments, Row row, int gutter, int maxWidth)
    {
        Style? slab = row.Sign switch
        {
            '+' => Theme.DiffAdded,
            '-' => Theme.DiffRemoved,
            _ => (Style?)null,
        };
        var fill = slab is { } s ? new Style(background: s.Background) : new Style();
        var mark = slab ?? new Style(foreground: Theme.Dim);
        string lead = row.Number.ToString(CultureInfo.InvariantCulture).PadLeft(gutter) + " " + row.Sign + " ";
        int textColumn = Indent.Length + lead.Length;
        int room = Math.Max(1, maxWidth - textColumn);

        segments.Add(new Segment(Indent));
        segments.Add(new Segment(lead, mark));
        int used = 0;
        foreach (var (text, style) in row.Pieces)
        {
            var paint = slab is { } on ? new Style(style.Foreground, on.Background, style.Decoration) : style;
            int i = 0, from = 0;
            while (i < text.Length)
            {
                int width = TextCells.ElementWidth(text, i, out int length);
                if (used + width > room && used > 0)
                {
                    // The run so far as one segment, then the row ends and the rest goes on under the text column.
                    if (i > from)
                    {
                        segments.Add(new Segment(text[from..i], paint));
                    }

                    from = i;
                    EndRow(segments, fill, slab is null ? 0 : room - used);
                    segments.Add(new Segment(Indent));
                    segments.Add(new Segment(new string(' ', lead.Length), fill));
                    used = 0;
                }

                used += width;
                i += length;
            }

            if (i > from)
            {
                segments.Add(new Segment(text[from..i], paint));
            }
        }

        EndRow(segments, fill, slab is null ? 0 : room - used);
    }

    /// <summary>A row's end: the slab carried to the edge (<paramref name="rest"/> cells), then the break.</summary>
    private static void EndRow(List<Segment> segments, Style fill, int rest)
    {
        if (rest > 0)
        {
            segments.Add(new Segment(new string(' ', rest), fill));
        }

        segments.Add(Segment.LineBreak);
    }

    /// <summary>
    /// The rows of <paramref name="diff"/>'s hunks, numbered by walking each hunk from its starts; each side of a hunk lexed as
    /// one text in <paramref name="language"/> (the new side for a context or added line, the old for a removed one).
    /// </summary>
    public static IReadOnlyList<Row?> RowsOf(FileDiff diff, CodeLanguage? language)
    {
        ArgumentNullException.ThrowIfNull(diff);
        var rows = new List<Row?>();
        foreach (var hunk in diff.Hunks)
        {
            if (rows.Count > 0)
            {
                rows.Add(null);
            }

            var lines = hunk.Lines.Where(l => !string.Equals(l, UnifiedDiff.NoNewlineMarker, StringComparison.Ordinal) && l.Length > 0).ToList();
            var newSide = lines.Where(l => l[0] != '-').Select(l => Clean(l[1..])).ToList();
            var oldSide = lines.Where(l => l[0] != '+').Select(l => Clean(l[1..])).ToList();
            var newPieces = Paint(newSide, language);
            var oldPieces = Paint(oldSide, language);
            int oldNumber = hunk.OldStart, newNumber = hunk.NewStart, oldIndex = 0, newIndex = 0;
            foreach (string line in lines)
            {
                switch (line[0])
                {
                    case '-':
                        rows.Add(new Row('-', oldNumber++, oldPieces[oldIndex++]));
                        break;
                    case '+':
                        rows.Add(new Row('+', newNumber++, newPieces[newIndex++]));
                        break;
                    default:
                        rows.Add(new Row(' ', newNumber++, newPieces[newIndex++]));
                        oldNumber++;
                        oldIndex++;
                        break;
                }
            }
        }

        return rows;
    }

    /// <summary>A tab as four cells, any other control character as <c>·</c>: a file's bytes never reach the terminal as codes.</summary>
    private static string Clean(string line)
    {
        if (!line.Any(char.IsControl))
        {
            return line;
        }

        var sb = new System.Text.StringBuilder(line.Length);
        foreach (char c in line)
        {
            sb.Append(c == '\t' ? "    " : char.IsControl(c) ? "·" : c.ToString());
        }

        return sb.ToString();
    }

    /// <summary>
    /// The lines' styled runs: lexed as one text when the language is known (a token across a break split at it, as a reply's
    /// code block does), else each line whole in the code block's ink.
    /// </summary>
    private static List<IReadOnlyList<(string, Style)>> Paint(IReadOnlyList<string> lines, CodeLanguage? language)
    {
        var plain = new Style(foreground: Theme.MarkdownCodeBlock.Foreground);
        var result = new List<IReadOnlyList<(string, Style)>>(lines.Count);
        if (language is null)
        {
            foreach (string line in lines)
            {
                result.Add([(line, plain)]);
            }

            return result;
        }

        string text = string.Join('\n', lines);
        var current = new List<(string, Style)>();
        foreach (var token in CodeLexer.Lex(text, language))
        {
            var code = Theme.CodeStyle(token.Kind);
            var style = new Style(foreground: code.Foreground, decoration: code.Decoration);
            int start = token.Start;
            while (start < token.End)
            {
                int newline = text.IndexOf('\n', start, token.End - start);
                int stop = newline < 0 ? token.End : newline;
                if (stop > start)
                {
                    current.Add((text[start..stop], style));
                }

                if (newline < 0)
                {
                    break;
                }

                result.Add(current);
                current = [];
                start = newline + 1;
            }
        }

        result.Add(current);

        // A lexer that dropped trailing blank lines' breaks leaves the list short: blank rows for them.
        while (result.Count < lines.Count)
        {
            result.Add([]);
        }

        return result;
    }
}
