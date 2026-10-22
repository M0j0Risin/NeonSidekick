using System.Globalization;

namespace NeonSidekick.Obsidian;

/// <summary>How a link is written: <c>[[wiki]]</c>, <c>![[embed]]</c>, or a Markdown <c>[text](url)</c> (<c>![alt](url)</c> an embed too).</summary>
public enum NoteLinkKind
{
    Wiki,
    Embed,
    Markdown,
    MarkdownEmbed,
}

/// <summary>
/// One link in a note: <paramref name="Target"/> the path or name as written (no <c>#</c> part, a
/// Markdown URL percent-decoded), <paramref name="Subpath"/> the <c>#Heading</c> / <c>#^block</c>
/// part with its <c>#</c> (empty when none), <paramref name="Alias"/> the <c>|alias</c> of a wikilink
/// or a Markdown link's text. <paramref name="Line"/> is 1-based, <paramref name="Column"/> the
/// 0-based index of the link's first character (the <c>!</c> of an embed) in that line, and
/// <paramref name="Length"/> runs through the closing <c>]]</c> or <c>)</c> — the span a move rewrites;
/// <paramref name="Raw"/> is that span as written.
/// </summary>
public sealed record NoteLink(NoteLinkKind Kind, string Target, string Subpath, string? Alias, int Line, int Column, int Length, string Raw = "")
{
    public bool IsWiki => Kind is NoteLinkKind.Wiki or NoteLinkKind.Embed;

    public bool IsEmbed => Kind is NoteLinkKind.Embed or NoteLinkKind.MarkdownEmbed;

    /// <summary>
    /// A Markdown link's title as written after its URL, quotes and all (<c>"The plan"</c>), empty for none (2026-10-22, review H19: a move
    /// rewrote <c>[x](Plan.md "title")</c> without it).
    /// </summary>
    public string Title { get; init; } = "";

    /// <summary>Whether the link sits in the note's properties (2026-10-22, review O4: Obsidian reads and renames those too).</summary>
    public bool InProperties { get; init; }
}

/// <summary>An ATX heading: its level (1–6), its text without the <c>#</c>s, its 1-based line.</summary>
public sealed record NoteHeading(int Level, string Text, int Line);

/// <summary>An inline <c>#tag</c> (without the <c>#</c>) and its 1-based line.</summary>
public sealed record NoteTag(string Name, int Line);

/// <summary>What <see cref="NoteScanner.Scan"/> found: the links, inline tags, headings, block ids, and the first body line (1-based, past the frontmatter).</summary>
public sealed record NoteScan(IReadOnlyList<NoteLink> Links, IReadOnlyList<NoteTag> Tags, IReadOnlyList<NoteHeading> Headings, IReadOnlyList<string> BlockIds, int BodyLine)
{
    public static NoteScan Empty { get; } = new([], [], [], [], 1);
}

/// <summary>
/// The Obsidian-flavoured Markdown a vault tool needs to know, read line by line by hand
/// (2026-09-22, the Obsidian tools): not Markdig, whose tree the transcript folds loses the source
/// positions a move must rewrite, and which has no wikilink, embed, tag or frontmatter syntax.
///
/// <para>Skipped, as Obsidian skips them: the frontmatter (its tags and aliases are
/// <see cref="NoteProperties"/>' to read), fenced code (<c>```</c> or <c>~~~</c>, closed by a fence of
/// the same character at least as long), inline code spans, and <c>%%comments%%</c> (which may span
/// lines). Found: <c>[[target#sub|alias]]</c>, <c>![[embed]]</c>, <c>[text](url)</c> to anything but
/// a URL with a scheme or a bare <c>#heading</c>, <c>#tags</c> (letters, digits, <c>_</c> <c>-</c>
/// <c>/</c>; not all digits; at a line's start or after whitespace), ATX headings, and block ids
/// (<c>^id</c> ending a line). Pure; never throws.</para>
/// </summary>
public static class NoteScanner
{
    public static NoteScan Scan(IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var links = new List<NoteLink>();
        var tags = new List<NoteTag>();
        var headings = new List<NoteHeading>();
        var blocks = new List<string>();
        int body = NoteProperties.BodyStart(lines);
        // The properties' wikilinks (2026-10-22, review O4: related: "[[Plan]]" is a link to Obsidian, renamed with the note).
        for (int index = 1; index < body - 1; index++)
        {
            PropertyLinks(lines[index], index + 1, links);
        }

        char fence = '\0';
        int fenceLength = 0;
        bool comment = false;
        for (int index = body; index < lines.Count; index++)
        {
            string line = lines[index];
            int number = index + 1;
            if (!comment && IsFence(line, out char c, out int length))
            {
                if (fence == '\0')
                {
                    fence = c;
                    fenceLength = length;
                    continue;
                }

                if (c == fence && length >= fenceLength && line.AsSpan().TrimStart().TrimStart(c).IsWhiteSpace())
                {
                    fence = '\0';
                    continue;
                }
            }

            if (fence != '\0')
            {
                continue;
            }

            if (!comment && Heading(line) is { } heading)
            {
                headings.Add(heading with { Line = number });
            }

            comment = ScanLine(line, number, comment, links, tags);
            // A block id inside a %% comment is no block id (2026-10-22, review K16): the line ends outside one for it to count.
            if (!comment && BlockId(line) is { } id)
            {
                blocks.Add(id);
            }
        }

        return new NoteScan(links, tags, headings, blocks, body + 1);
    }

    /// <summary>A fence line: up to three spaces, then three or more backticks or tildes.</summary>
    private static bool IsFence(string line, out char c, out int length)
    {
        c = '\0';
        length = 0;
        int i = 0;
        while (i < line.Length && i < 3 && line[i] == ' ')
        {
            i++;
        }

        if (i >= line.Length || (line[i] != '`' && line[i] != '~'))
        {
            return false;
        }

        c = line[i];
        int start = i;
        while (i < line.Length && line[i] == c)
        {
            i++;
        }

        length = i - start;
        return length >= 3;
    }

    /// <summary>The heading on an ATX line (<c>## Text ##</c>), its line left 0 for the caller; null for any other line.</summary>
    public static NoteHeading? Heading(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        int level = 0;
        while (level < line.Length && line[level] == '#')
        {
            level++;
        }

        if (level is 0 or > 6 || (level < line.Length && line[level] != ' ' && line[level] != '\t'))
        {
            return null;
        }

        string text = line[level..].Trim();
        int closing = text.Length;
        while (closing > 0 && text[closing - 1] == '#')
        {
            closing--;
        }

        if (closing < text.Length && (closing == 0 || text[closing - 1] == ' '))
        {
            text = text[..closing].TrimEnd();
        }

        return new NoteHeading(level, text, 0);
    }

    /// <summary>The block id ending a line (<c>text ^id</c> or a lone <c>^id</c>), or null.</summary>
    public static string? BlockId(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        string trimmed = line.TrimEnd();
        int caret = trimmed.LastIndexOf('^');
        if (caret < 0 || caret == trimmed.Length - 1 || (caret > 0 && !char.IsWhiteSpace(trimmed[caret - 1])))
        {
            return null;
        }

        for (int i = caret + 1; i < trimmed.Length; i++)
        {
            if (!(char.IsAsciiLetterOrDigit(trimmed[i]) || trimmed[i] == '-'))
            {
                return null;
            }
        }

        return trimmed[(caret + 1)..];
    }

    /// <summary>A property line's <c>[[wikilinks]]</c> (review O4); the line is YAML, so nothing else in it is a link.</summary>
    private static void PropertyLinks(string line, int number, List<NoteLink> links)
    {
        int i = 0;
        while ((i = line.IndexOf("[[", i, StringComparison.Ordinal)) >= 0)
        {
            int end = line.IndexOf("]]", i + 2, StringComparison.Ordinal);
            if (end < 0)
            {
                return;
            }

            if (end > i + 2)
            {
                links.Add(Wiki(line.Substring(i + 2, end - i - 2), false, number, i, end + 2 - i) with { Raw = line[i..(end + 2)], InProperties = true });
            }

            i = end + 2;
        }
    }

    /// <summary>
    /// One body line's links and tags; returns whether a <c>%%</c> comment is still open at its end. Linear in the line (2026-10-22, review M1:
    /// every unclosed <c>[</c>, <c>[[</c> and backtick run searched to the line's end again, a 2 MB line held the vault's lock for minutes):
    /// the brackets are paired once, and each search remembers where it found its next match.
    /// </summary>
    private static bool ScanLine(string line, int number, bool comment, List<NoteLink> links, List<NoteTag> tags)
    {
        var scan = new LineScan(line);
        int i = 0;
        while (i < line.Length)
        {
            if (line[i] == '%' && i + 1 < line.Length && line[i + 1] == '%')
            {
                comment = !comment;
                i += 2;
                continue;
            }

            if (comment)
            {
                i++;
                continue;
            }

            char c = line[i];
            if (c == '`')
            {
                int run = Run(line, i, '`');
                int close = scan.FindRun(i + run, run);
                if (close >= 0)
                {
                    i = close + run;
                    continue;
                }

                i += run;
                continue;
            }

            bool bang = c == '!' && i + 1 < line.Length && line[i + 1] == '[';
            int open = bang ? i + 1 : i;
            if (line[open] == '[' && open + 1 < line.Length && line[open + 1] == '[')
            {
                int end = scan.NextWikiClose(open + 2);
                if (end > open + 2)
                {
                    links.Add(Wiki(line.Substring(open + 2, end - open - 2), bang, number, i, end + 2 - i) with { Raw = line[i..(end + 2)] });
                    i = end + 2;
                    continue;
                }
            }
            else if (line[open] == '[' && MarkdownLink(line, open, scan, out string text, out string url, out string title, out int after) && IsNoteUrl(url))
            {
                SplitSubpath(Decode(url), out string target, out string subpath);
                links.Add(new NoteLink(bang ? NoteLinkKind.MarkdownEmbed : NoteLinkKind.Markdown, target, subpath, text, number, i, after - i, line[i..after]) { Title = title });
                i = after;
                continue;
            }

            if (c == '#' && (i == 0 || char.IsWhiteSpace(line[i - 1])) && Tag(line, i + 1) is { } tag)
            {
                tags.Add(new NoteTag(tag, number));
                i += tag.Length + 1;
                continue;
            }

            i++;
        }

        return comment;
    }

    private static int Run(string line, int at, char c)
    {
        int i = at;
        while (i < line.Length && line[i] == c)
        {
            i++;
        }

        return i - at;
    }

    /// <summary>
    /// One line's searches, each remembering its last answer (review M1): the brackets paired once by a stack, the next <c>]]</c> and the
    /// next run of each length of backticks found from the last place looked, so a line full of unclosed openers is read a bounded number of times.
    /// </summary>
    private sealed class LineScan(string line)
    {
        private int[]? _closeOf;
        private int _wikiFrom = int.MaxValue;
        private int _wikiAt = -1;
        private Dictionary<int, (int From, int At)>? _runs;
        private int _parenFrom = int.MaxValue;
        private int _parenAt = -1;

        /// <summary>The <c>]</c> pairing the <c>[</c> at <paramref name="open"/>, or -1 (nested brackets counted, as the scan always did).</summary>
        public int CloseOf(int open)
        {
            if (_closeOf is null)
            {
                _closeOf = new int[line.Length];
                Array.Fill(_closeOf, -1);
                var opens = new Stack<int>();
                for (int i = 0; i < line.Length; i++)
                {
                    if (line[i] == '[')
                    {
                        opens.Push(i);
                    }
                    else if (line[i] == ']' && opens.Count > 0)
                    {
                        _closeOf[opens.Pop()] = i;
                    }
                }
            }

            return _closeOf[open];
        }

        /// <summary>The next <c>]]</c> at or after <paramref name="from"/>, or -1.</summary>
        public int NextWikiClose(int from)
        {
            if (from >= _wikiFrom && (_wikiAt < 0 || _wikiAt >= from))
            {
                return _wikiAt;
            }

            _wikiFrom = from;
            _wikiAt = line.IndexOf("]]", from, StringComparison.Ordinal);
            return _wikiAt;
        }

        /// <summary>The next <c>)</c> at or after <paramref name="from"/>, or -1.</summary>
        public int NextParen(int from)
        {
            if (from >= _parenFrom && (_parenAt < 0 || _parenAt >= from))
            {
                return _parenAt;
            }

            _parenFrom = from;
            _parenAt = line.IndexOf(')', from);
            return _parenAt;
        }

        /// <summary>The index of the next run of exactly <paramref name="length"/> backticks from <paramref name="from"/>, or -1.</summary>
        public int FindRun(int from, int length)
        {
            _runs ??= [];
            if (_runs.TryGetValue(length, out var last) && from >= last.From && (last.At < 0 || last.At >= from))
            {
                return last.At;
            }

            int found = -1;
            int i = from;
            while (i < line.Length)
            {
                if (line[i] == '`')
                {
                    int run = Run(line, i, '`');
                    if (run == length)
                    {
                        found = i;
                        break;
                    }

                    i += run;
                    continue;
                }

                i++;
            }

            _runs[length] = (from, found);
            return found;
        }
    }

    private static NoteLink Wiki(string inner, bool embed, int line, int column, int length)
    {
        string? alias = null;
        int pipe = inner.IndexOf('|', StringComparison.Ordinal);
        if (pipe >= 0)
        {
            alias = inner[(pipe + 1)..];
            // In a table the pipe is escaped (Obsidian's [[Note\|alias]]): the backslash is not the target's.
            inner = pipe > 0 && inner[pipe - 1] == '\\' ? inner[..(pipe - 1)] : inner[..pipe];
        }

        SplitSubpath(inner, out string target, out string subpath);
        return new NoteLink(embed ? NoteLinkKind.Embed : NoteLinkKind.Wiki, target, subpath, alias, line, column, length);
    }

    /// <summary><c>Note#Heading</c> as <c>Note</c> and <c>#Heading</c>; the target trimmed.</summary>
    public static void SplitSubpath(string text, out string target, out string subpath)
    {
        ArgumentNullException.ThrowIfNull(text);
        int hash = text.IndexOf('#', StringComparison.Ordinal);
        target = (hash >= 0 ? text[..hash] : text).Trim();
        subpath = hash >= 0 ? text[hash..].Trim() : "";
    }

    /// <summary>
    /// <c>[text](url "title")</c> starting at the <c>[</c> at <paramref name="open"/>: its text, its URL (a <c>&lt;…&gt;</c> one unwrapped)
    /// and its title as written, and the index after the <c>)</c>. The URL runs to its first space or to the <c>)</c> that balances its
    /// parentheses (2026-10-22, review H19: it ended at the first <c>)</c>, so <c>[x](Notes (old).md)</c> was <c>Notes (old</c> and a move
    /// wrote <c>[x](New.md).md)</c>), a backslash escaping the next character, as CommonMark reads it.
    /// </summary>
    private static bool MarkdownLink(string line, int open, LineScan scan, out string text, out string url, out string title, out int after)
    {
        text = url = title = "";
        after = 0;
        int close = scan.CloseOf(open);
        if (close < 0 || close + 1 >= line.Length || line[close + 1] != '(')
        {
            return false;
        }

        // No ')' anywhere after: no link, found in one remembered search.
        if (scan.NextParen(close + 2) < 0)
        {
            return false;
        }

        int i = close + 2;
        while (i < line.Length && line[i] is ' ' or '\t')
        {
            i++;
        }

        if (i < line.Length && line[i] == '<')
        {
            int end = line.IndexOf('>', i + 1);
            if (end < 0)
            {
                return false;
            }

            url = line[(i + 1)..end];
            i = end + 1;
        }
        else
        {
            int start = i;
            int depth = 0;
            while (i < line.Length)
            {
                char c = line[i];
                if (c == '\\' && i + 1 < line.Length)
                {
                    i += 2;
                    continue;
                }

                if (c is ' ' or '\t')
                {
                    break;
                }

                if (c == '(')
                {
                    depth++;
                }
                else if (c == ')')
                {
                    if (depth == 0)
                    {
                        break;
                    }

                    depth--;
                }

                i++;
            }

            url = line[start..i];
        }

        int beforeTitle = i;
        while (i < line.Length && line[i] is ' ' or '\t')
        {
            i++;
        }

        if (i < line.Length && line[i] is '"' or '\'' or '(' && i > beforeTitle)
        {
            char closer = line[i] == '(' ? ')' : line[i];
            int end = line.IndexOf(closer, i + 1);
            if (end < 0)
            {
                return false;
            }

            title = line[i..(end + 1)];
            i = end + 1;
            while (i < line.Length && line[i] is ' ' or '\t')
            {
                i++;
            }
        }

        if (i >= line.Length || line[i] != ')')
        {
            return false;
        }

        text = line[(open + 1)..close];
        after = i + 1;
        return url.Length > 0;
    }

    /// <summary>A URL that names a vault file: no scheme (<c>https:</c>, <c>mailto:</c>, <c>obsidian:</c>) and not a bare <c>#heading</c> of the note itself.</summary>
    private static bool IsNoteUrl(string url)
    {
        if (url.StartsWith('#'))
        {
            return false;
        }

        int colon = url.IndexOf(':', StringComparison.Ordinal);
        return colon < 0 || (colon == 1 && char.IsAsciiLetter(url[0]));
    }

    /// <summary>A Markdown URL's percent-escapes decoded (<c>My%20Note.md</c>); a malformed one as written.</summary>
    public static string Decode(string url)
    {
        ArgumentNullException.ThrowIfNull(url);
        try
        {
            return Uri.UnescapeDataString(url);
        }
        catch (UriFormatException)
        {
            return url;
        }
    }

    /// <summary>A path as a Markdown link spells it: spaces and the few characters that break the link percent-encoded, <c>/</c> kept.</summary>
    public static string Encode(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var sb = new System.Text.StringBuilder(path.Length + 8);
        foreach (char c in path)
        {
            sb.Append(c switch
            {
                ' ' => "%20",
                '(' => "%28",
                ')' => "%29",
                '<' => "%3C",
                '>' => "%3E",
                '#' => "%23",
                '%' => "%25",
                _ => c.ToString(),
            });
        }

        return sb.ToString();
    }

    /// <summary>The tag starting at <paramref name="at"/> (after its <c>#</c>), or null: at least one character, not all digits.</summary>
    private static string? Tag(string line, int at)
    {
        int i = at;
        while (i < line.Length && IsTagChar(line[i]))
        {
            i++;
        }

        string tag = line[at..i].TrimEnd('/');
        if (tag.Length == 0 || tag.All(char.IsAsciiDigit) || tag.StartsWith('/'))
        {
            return null;
        }

        return tag;
    }

    private static bool IsTagChar(char c) =>
        char.IsLetterOrDigit(c) || c is '_' or '-' or '/' || char.GetUnicodeCategory(c) is UnicodeCategory.OtherSymbol or UnicodeCategory.NonSpacingMark;
}
