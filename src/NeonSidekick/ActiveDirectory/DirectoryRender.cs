using System.Globalization;
using System.Text;
using NeonSidekick.Llm.Tools;
using NeonSidekick.UI;

namespace NeonSidekick.ActiveDirectory;

/// <summary>How a card's line is drawn: the head, a table's title, a header row (dim), a row, a note (dim).</summary>
public enum DirectoryCardStyle
{
    Head,
    Title,
    Header,
    Row,
    Note,
}

/// <summary>One line of a card, at most the width it was laid out at, and the object a double-click on it opens (null for none).</summary>
public sealed record DirectoryCardLine(string Text, DirectoryCardStyle Style, DirectoryRowTarget? Target = null);

/// <summary>
/// A <see cref="DirectoryAnswer"/> drawn (2026-10-15, the AD gaps plan, §6, the user's ask: "line results up in columns a little cleaner"):
/// <see cref="ForModel"/> the tools' text in the shape it always had — the rows' model lines, the fences, <see cref="DirectoryQueries.MaxChars"/>,
/// the quote note, no padding the model would pay for — and <see cref="ForCard"/> <c>/ad</c>'s card: unfenced, each key/value block's labels
/// padded to its widest, each table under a header row with its columns padded to their widest cell (<see cref="TextCells.Width"/>, emoji and
/// wide characters counted right), a row wider than the pane shrinking its widest flexible column. A stranger's text on the card is cleaned
/// (<see cref="Clean"/>): it goes to the terminal as it stands. Pure.
/// </summary>
public static class DirectoryRender
{
    /// <summary>The placeholder in a card's cell with no value (the CSV leaves it empty).</summary>
    public const string NoValue = "–";

    /// <summary>The narrowest a flexible column is cut to.</summary>
    public const int MinFlexible = 6;

    public static string ForModel(DirectoryAnswer answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        if (answer.Error is { } error)
        {
            return error;
        }

        var text = new StringBuilder(answer.Head);
        foreach (var field in answer.Fields)
        {
            text.Append("\n  ").Append(field.Label).Append(": ").Append(field.Value);
        }

        foreach (var table in answer.Tables)
        {
            if (table.TitleInModel && table.Title is { } title)
            {
                text.Append('\n').Append(title).Append(':');
            }

            var shown = table.ShownRows.ToList();
            for (int i = 0; i < shown.Count; i++)
            {
                if (text.Length > DirectoryQueries.MaxChars - table.CutReserve)
                {
                    if (table.CutNote is { } note)
                    {
                        text.Append("\n  ").Append(note(shown.Count - i));
                    }

                    break;
                }

                text.Append("\n  ").Append(shown[i].ModelLine);
            }
        }

        foreach (string note in answer.Notes)
        {
            text.Append('\n').Append(note);
        }

        text.Append('\n').Append(UntrustedText.QuoteNote);
        string finished = text.Length > DirectoryQueries.MaxChars
            ? text.ToString(0, DirectoryQueries.MaxChars - 40) + "\n(cut at " + DirectoryQueries.MaxChars.ToString("N0", CultureInfo.InvariantCulture) + " characters)"
            : text.ToString();
        return answer.Saved is { } saved ? finished + "\n" + saved : finished;
    }

    /// <summary>
    /// The card at <paramref name="width"/> cells (<see cref="int.MaxValue"/>: nothing wrapped or cut, what <c>copy</c>, the Web UI's card and
    /// headless take): every line at most that wide, so the pane's layout wraps none further and a line's index is its row.
    /// </summary>
    public static IReadOnlyList<DirectoryCardLine> ForCard(DirectoryAnswer answer, int width)
    {
        ArgumentNullException.ThrowIfNull(answer);
        width = Math.Max(10, width);
        var lines = new List<DirectoryCardLine>();
        if (answer.Error is { } error)
        {
            string first = error.Split('\n')[0];
            Wrap(lines, "", CleanModel(first.StartsWith("Error: ", StringComparison.Ordinal) ? first[7..] : first), width, 2, DirectoryCardStyle.Head, null);
            if (answer.Candidates is { } candidates)
            {
                Table(lines, candidates, width, answer.Zone, blankBefore: false);
            }

            return lines;
        }

        Wrap(lines, "", CleanModel(answer.Head), width, 2, DirectoryCardStyle.Head, null);
        var plain = answer.Fields.Where(f => f.Links is null).ToList();
        if (plain.Count > 0)
        {
            int labelWidth = plain.Max(f => TextCells.Width(f.Label)) + 1;
            foreach (var field in plain)
            {
                string label = field.Label + ":";
                string lead = "  " + label + new string(' ', labelWidth - TextCells.Width(label)) + " ";
                Wrap(lines, lead, CleanModel(field.Value), width, TextCells.Width(lead), DirectoryCardStyle.Row, field.Target);
            }
        }

        foreach (var field in answer.Fields.Where(f => f.Links is not null))
        {
            Table(lines, field.Links!, width, answer.Zone, blankBefore: true);
        }

        bool firstTable = true;
        foreach (var table in answer.Tables)
        {
            Table(lines, table, width, answer.Zone, blankBefore: !firstTable || table.Title is not null || answer.Fields.Count > 0);
            firstTable = false;
        }

        foreach (string note in answer.Notes)
        {
            Wrap(lines, "", CleanModel(note), width, 2, DirectoryCardStyle.Note, null);
        }

        if (answer.Saved is { } saved)
        {
            Wrap(lines, "", Clean(saved), width, 2, DirectoryCardStyle.Note, null);
        }

        return lines;
    }

    /// <summary>The card as text (<c>copy</c>, the Web UI's card, headless): <see cref="ForCard"/> unbounded.</summary>
    public static IReadOnlyList<string> CardText(DirectoryAnswer answer) => ForCard(answer, int.MaxValue).Select(l => l.Text).ToList();

    /// <summary>A date cell's text on the card and in the CSV: <c>yyyy-MM-dd HH:mm</c> in <paramref name="zone"/>.</summary>
    public static string Date(DateTimeOffset moment, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        return TimeZoneInfo.ConvertTime(moment, zone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    /// <summary>A cell's text on the card: the date, the text cleaned, or <see cref="NoValue"/>.</summary>
    public static string CardCell(DirectoryCell? cell, TimeZoneInfo zone)
    {
        if (cell is null)
        {
            return NoValue;
        }

        string text = cell.When is { } when ? Date(when, zone) : cell.Text is { Length: > 0 } t ? Clean(UntrustedText.Cut(t)) : NoValue;
        return cell.Indent > 0 ? new string(' ', cell.Indent) + text : text;
    }

    /// <summary>
    /// A stranger's text made safe to draw as one row (2026-10-15, the AD gaps plan: the card's text is no longer fenced and goes to the terminal as it
    /// stands): <see cref="UntrustedText.Scrub"/> (control characters, the escape that would start a terminal sequence among them), tabs and line
    /// breaks turned to spaces (Scrub keeps them; they would break a row), and the bidirectional controls dropped (U+202A–202E, U+2066–2069), which
    /// would reorder a row on screen and throw its columns. Pure.
    /// </summary>
    public static string Clean(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string scrubbed = UntrustedText.Scrub(text);
        var clean = new StringBuilder(scrubbed.Length);
        foreach (char c in scrubbed)
        {
            if (c is '\t' or '\n' or '\r')
            {
                clean.Append(' ');
            }
            else if (c is not ((>= '\u202A' and <= '\u202E') or (>= '\u2066' and <= '\u2069')))
            {
                clean.Append(c);
            }
        }

        return clean.ToString();
    }

    /// <summary>A model's text (a head, a value) for the card: its fences dropped, then <see cref="Clean"/>.</summary>
    private static string CleanModel(string text) => Clean(UntrustedText.Unfenced(text));

    private static void Table(List<DirectoryCardLine> lines, DirectoryTable table, int width, TimeZoneInfo zone, bool blankBefore)
    {
        var rows = table.ShownRows.ToList();
        if (blankBefore && lines.Count > 0)
        {
            lines.Add(new DirectoryCardLine("", DirectoryCardStyle.Row));
        }

        if (table.Title is { } title)
        {
            Wrap(lines, "", Clean(title), width, 2, DirectoryCardStyle.Title, null);
        }

        if (rows.Count == 0)
        {
            return;
        }

        int columns = table.Columns.Count;
        var cells = rows.Select(r => Enumerable.Range(0, columns).Select(c => c < r.Cells.Count ? CardCell(r.Cells[c], zone) : NoValue).ToArray()).ToList();
        bool header = columns > 1;
        var widths = new int[columns];
        for (int c = 0; c < columns; c++)
        {
            widths[c] = Math.Max(header ? TextCells.Width(table.Columns[c].Name) : 0, cells.Max(r => TextCells.Width(r[c])));
        }

        // Too wide for the pane: the widest flexible column gives way first, never below MinFlexible.
        int Total() => 2 + widths.Sum() + (2 * (columns - 1));
        while (Total() > width)
        {
            int widest = -1;
            for (int c = 0; c < columns; c++)
            {
                if (table.Columns[c].Flexible && widths[c] > MinFlexible && (widest < 0 || widths[c] > widths[widest]))
                {
                    widest = c;
                }
            }

            if (widest < 0)
            {
                break;
            }

            widths[widest] = Math.Max(MinFlexible, widths[widest] - (Total() - width));
        }

        if (header)
        {
            lines.Add(new DirectoryCardLine(Fitted(Line(table.Columns.Select(c => c.Name).ToArray(), widths), width), DirectoryCardStyle.Header));
        }

        for (int r = 0; r < rows.Count; r++)
        {
            lines.Add(new DirectoryCardLine(Fitted(Line(cells[r], widths), width), DirectoryCardStyle.Row, rows[r].Target));
        }
    }

    /// <summary>A row's cells, each cut to its column and padded to it, two spaces apart; the last column unpadded.</summary>
    private static string Line(string[] cells, int[] widths)
    {
        var line = new StringBuilder("  ");
        for (int c = 0; c < cells.Length; c++)
        {
            string cell = TextCells.Width(cells[c]) > widths[c] ? ScreenPane.Fit(cells[c], widths[c]) : cells[c];
            line.Append(cell);
            if (c < cells.Length - 1)
            {
                line.Append(' ', widths[c] - TextCells.Width(cell) + 2);
            }
        }

        return line.ToString();
    }

    private static string Fitted(string line, int width) => width == int.MaxValue || TextCells.Width(line) <= width ? line : ScreenPane.Fit(line, width);

    /// <summary>
    /// <paramref name="lead"/> as it stands, then <paramref name="body"/> broken at spaces into lines of at most <paramref name="width"/> cells (a
    /// word wider than a line broken where it must), the later lines indented <paramref name="rest"/> cells; each carrying the target.
    /// </summary>
    private static void Wrap(List<DirectoryCardLine> lines, string lead, string body, int width, int rest, DirectoryCardStyle style, DirectoryRowTarget? target)
    {
        string whole = lead + body;
        if (width == int.MaxValue || TextCells.Width(whole) <= width)
        {
            lines.Add(new DirectoryCardLine(whole, style, target));
            return;
        }

        rest = Math.Min(rest, width / 2);
        var line = new StringBuilder(lead);
        int used = TextCells.Width(lead);
        bool empty = true;
        void Flush()
        {
            lines.Add(new DirectoryCardLine(line.ToString(), style, target));
            line.Clear().Append(' ', rest);
            used = rest;
            empty = true;
        }

        foreach (string word in body.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int cells = TextCells.Width(word);
            if (!empty && used + 1 + cells > width)
            {
                Flush();
            }

            if (empty && used + cells > width)
            {
                // A word wider than a whole line: as much as fits on each.
                int at = 0;
                while (at < word.Length)
                {
                    int w = TextCells.ElementWidth(word, at, out int length);
                    if (used + w > width && !empty)
                    {
                        Flush();
                    }

                    line.Append(word, at, Math.Max(1, length));
                    used += w;
                    empty = false;
                    at += Math.Max(1, length);
                }

                continue;
            }

            if (!empty)
            {
                line.Append(' ');
                used++;
            }

            line.Append(word);
            used += cells;
            empty = false;
        }

        if (!empty)
        {
            lines.Add(new DirectoryCardLine(line.ToString(), style, target));
        }
    }
}
