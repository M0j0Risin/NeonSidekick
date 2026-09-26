using System.Text;

namespace NeonSidekick.Speech;

/// <summary>
/// Drops markdown pipe tables — the header row, the delimiter row and every body row — from text on its way to the voice. The
/// <see cref="CodeBlockFilter"/> shape: one instance per reply, fed deltas as they stream, pure, no console, no log.
///
/// <para>Why it exists (2026-09-26, the user's ask, the same call as for code): a table used to reach the voice row by row,
/// the delimiter row dropped and each row read as a comma list (<see cref="SpeakableText.StripMarkdown"/>) — "Name, Role,
/// Notes", cell after cell, which nobody can follow by ear. Now a table is skipped silently, always. Only speech changes;
/// the transcript still shows it.</para>
///
/// <para>Rules, GFM's pipe table narrowed to the shape models write: a header row is a line with up to three spaces of
/// indent and then a <c>|</c>; the line right after it must be a delimiter row (<c>|</c> first too, then cells of
/// <c>:?-+:?</c> between pipes); the body is every following line whose first non-space character is <c>|</c>, so the table
/// ends at a blank line or at any other line, which passes through. A table with no leading pipes is not caught — that would
/// mean holding back every line with a pipe in it until its end, and models lead with the pipe. The whole table becomes one
/// <c>\n</c>, as a code block does, so the sentence before it ends there and the text after it starts afresh. A lone
/// pipe-led line with no delimiter row after it passes through (the cleaner still reads it as a list). A line is held back
/// only while it could still be part of a table: a pipe-led line to its end, and the line after it while it could still be
/// a delimiter row.</para>
/// </summary>
public sealed class TableFilter
{
    private const int MaxIndent = 3;

    private enum Lead
    {
        Undecided,
        Pipe,
        Other,
    }

    // The current line so far while held; _passing once it is released (the rest goes straight out), _dropping for a body row.
    private readonly StringBuilder _line = new();
    private bool _passing;
    private bool _dropping;

    // A complete pipe-led line, waiting on the line after it to be (or not be) a delimiter row.
    private string? _header;
    private bool _inTable;

    /// <summary>Feeds one delta and returns the text that is safe to speak now, possibly empty.</summary>
    public string Push(string delta)
    {
        ArgumentNullException.ThrowIfNull(delta);
        var output = new StringBuilder();
        foreach (char c in delta)
        {
            if (c == '\n')
            {
                EndLine(output);
                continue;
            }

            if (_passing)
            {
                output.Append(c);
                continue;
            }

            if (_dropping)
            {
                continue;
            }

            _line.Append(c);
            Decide(output);
        }

        return output.ToString();
    }

    /// <summary>
    /// The end of the reply: a header whose delimiter row is complete but unterminated is a table (one <c>\n</c>); anything
    /// else still held — a lone header, a line that might have been a delimiter row — is released as it was.
    /// </summary>
    public string Flush()
    {
        string line = _line.ToString();
        string result;
        if (_passing || _dropping)
        {
            result = string.Empty;
        }
        else if (_header is not null)
        {
            result = IsDelimiterRow(line) ? "\n" : _header + "\n" + line;
        }
        else
        {
            result = line;
        }

        _line.Clear();
        _passing = false;
        _dropping = false;
        _header = null;
        _inTable = false;
        return result;
    }

    /// <summary><paramref name="text"/> without its pipe tables, each one a single <c>\n</c>. Pure.</summary>
    public static string Strip(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var filter = new TableFilter();
        return filter.Push(text) + filter.Flush();
    }

    /// <summary>
    /// Where <paramref name="text"/>'s pipe tables are: each from its header row's start to just past its last row's newline
    /// (or the end of the text), in order — what <see cref="Strip"/> replaces with a <c>\n</c>. Pure.
    /// </summary>
    public static IReadOnlyList<(int Start, int End)> Spans(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var spans = new List<(int Start, int End)>();
        int pos = 0;
        while (pos < text.Length)
        {
            int newline = text.IndexOf('\n', pos);
            int after = newline < 0 ? text.Length : newline + 1;
            if (newline < 0 || LeadOf(text[pos..newline]) != Lead.Pipe)
            {
                pos = after;
                continue;
            }

            int next = text.IndexOf('\n', after);
            int nextEnd = next < 0 ? text.Length : next;
            if (!IsDelimiterRow(text[after..nextEnd]))
            {
                pos = after;
                continue;
            }

            int start = pos;
            pos = next < 0 ? text.Length : next + 1;
            while (pos < text.Length)
            {
                int bodyNewline = text.IndexOf('\n', pos);
                int bodyEnd = bodyNewline < 0 ? text.Length : bodyNewline;
                if (LeadOf(text[pos..bodyEnd]) != Lead.Pipe)
                {
                    break;
                }

                pos = bodyNewline < 0 ? text.Length : bodyNewline + 1;
            }

            spans.Add((start, pos));
        }

        return spans;
    }

    /// <summary>A character of a held line was just added: release it, drop it or keep holding.</summary>
    private void Decide(StringBuilder output)
    {
        string prefix = _line.ToString();
        Lead lead = LeadOf(prefix);
        if (_inTable)
        {
            if (lead == Lead.Undecided)
            {
                return;
            }

            if (lead == Lead.Pipe)
            {
                _line.Clear();
                _dropping = true;
                return;
            }

            _inTable = false;
        }

        if (_header is not null)
        {
            if (CouldBeDelimiter(prefix))
            {
                return;
            }

            // Not the delimiter row: the header was an ordinary line. This one may still be a header of its own.
            output.Append(_header).Append('\n');
            _header = null;
        }

        if (lead == Lead.Other)
        {
            output.Append(_line);
            _line.Clear();
            _passing = true;
        }
    }

    /// <summary>A newline arrived: settle the current line.</summary>
    private void EndLine(StringBuilder output)
    {
        if (_passing)
        {
            output.Append('\n');
            _passing = false;
            return;
        }

        if (_dropping)
        {
            _dropping = false;
            return;
        }

        string line = _line.ToString();
        _line.Clear();
        if (_inTable)
        {
            // Only an undecided (blank) line gets here: it ends the table and passes through.
            _inTable = false;
            output.Append(line).Append('\n');
            return;
        }

        if (_header is not null)
        {
            if (IsDelimiterRow(line))
            {
                _header = null;
                _inTable = true;
                output.Append('\n');
                return;
            }

            output.Append(_header).Append('\n');
            _header = null;
        }

        if (LeadOf(line) == Lead.Pipe)
        {
            _header = line;
        }
        else
        {
            output.Append(line).Append('\n');
        }
    }

    /// <summary>What a line (or its start) begins with past up to three spaces: a pipe, something else, or nothing yet.</summary>
    private static Lead LeadOf(string line)
    {
        int i = 0;
        while (i < line.Length && line[i] == ' ')
        {
            i++;
        }

        if (i > MaxIndent)
        {
            return Lead.Other;
        }

        if (i == line.Length)
        {
            return Lead.Undecided;
        }

        return line[i] == '|' ? Lead.Pipe : Lead.Other;
    }

    /// <summary>Whether a line that starts with <paramref name="prefix"/> (no newline yet) may still turn out to be a delimiter row.</summary>
    private static bool CouldBeDelimiter(string prefix) => LeadOf(prefix) switch
    {
        Lead.Undecided => true,
        Lead.Pipe => prefix.TrimStart(' ').AsSpan(1).IndexOfAnyExcept(" \t\r-:|") < 0,
        _ => false,
    };

    /// <summary>Whether <paramref name="line"/> is a delimiter row: <c>|</c> first, then one or more <c>:?-+:?</c> cells between pipes.</summary>
    private static bool IsDelimiterRow(string line)
    {
        if (LeadOf(line) != Lead.Pipe)
        {
            return false;
        }

        string body = line.TrimStart(' ')[1..].TrimEnd(' ', '\t', '\r');
        if (body.EndsWith('|'))
        {
            body = body[..^1];
        }

        if (body.Length == 0)
        {
            return false;
        }

        foreach (string raw in body.Split('|'))
        {
            string cell = raw.Trim(' ', '\t');
            string dashes = cell.TrimStart(':').TrimEnd(':');
            if (dashes.Length == 0 || dashes.AsSpan().IndexOfAnyExcept('-') >= 0 || cell.Length - dashes.Length > 2
                || (cell.Length - dashes.Length == 2 && !(cell[0] == ':' && cell[^1] == ':')))
            {
                return false;
            }
        }

        return true;
    }
}
