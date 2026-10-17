using System.Text;

namespace NeonSidekick.Web;

/// <summary>
/// A page reduced to what the model reads: its title and its readable text as light Markdown. <paramref name="Anchors"/> (2026-10-17, the
/// toki-pona session: <c>#n</c> and <c>#epiku</c> on one page both read from its top) maps each element id (and an <c>a</c>'s name) to the
/// start of the Markdown line it fell on, so <c>web_fetch</c> can start at a URL's fragment; null when the page has none, and always for
/// a PDF or a text answer.
/// </summary>
public sealed record PageText(string Title, string Markdown, IReadOnlyDictionary<string, int>? Anchors = null);

/// <summary>
/// HTML to light Markdown over <see cref="HtmlTokenizer"/>: headings as <c>#</c>, links as
/// <c>[text](absolute url)</c> so the model can follow them, lists, fenced code, tables as rows,
/// bold and italic; scripts, styles, navigation, footers, asides, form controls and hidden
/// elements dropped; whitespace collapsed to what Markdown needs. No Readability-style scoring:
/// every visible block stays, in document order. Pure; pinned by <c>HtmlToMarkdownTests</c>.
/// </summary>
public static class HtmlToMarkdown
{
    /// <summary>The elements whose whole subtree is dropped.</summary>
    public static readonly IReadOnlySet<string> Dropped = new HashSet<string>(StringComparer.Ordinal)
    {
        "head", "script", "style", "noscript", "svg", "canvas", "template", "iframe", "nav", "footer", "aside",
        "button", "select", "datalist", "option", "textarea", "object", "embed", "audio", "video", "map", "dialog",
    };

    /// <summary>The ARIA roles that mark a subtree as chrome rather than content.</summary>
    public static readonly IReadOnlySet<string> DroppedRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "navigation", "banner", "contentinfo", "search", "menu", "menubar", "toolbar", "tooltip", "dialog", "alertdialog", "complementary",
    };

    private static readonly IReadOnlySet<string> Blocks = new HashSet<string>(StringComparer.Ordinal)
    {
        "p", "div", "section", "article", "main", "header", "blockquote", "figure", "figcaption", "details", "summary",
        "address", "center", "fieldset", "legend", "form", "dl", "body", "html",
    };

    public static PageText Convert(string html, Uri? baseUri = null)
    {
        ArgumentNullException.ThrowIfNull(html);
        var writer = new Writer(baseUri);
        foreach (var token in HtmlTokenizer.Tokenize(html))
        {
            writer.Accept(token);
        }

        return writer.Finish();
    }

    /// <summary>A run of text as one line: whitespace runs to one space, trimmed. For titles, link text and table cells.</summary>
    public static string Collapse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var sb = new StringBuilder(text.Length);
        bool pendingSpace = false;
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    private enum FrameKind
    {
        Plain,
        Skip,
        Heading,
        List,
        Item,
        Link,
        Emphasis,
        Code,
        Pre,
        Table,
        Row,
        Cell,
    }

    private sealed class Frame
    {
        public Frame(string name, FrameKind kind, int start)
        {
            Name = name;
            Kind = kind;
            Start = start;
        }

        public string Name { get; }
        public FrameKind Kind { get; }
        public int Start { get; set; }
        public string? Href { get; init; }
        public string Marker { get; init; } = "";
        public bool Ordered { get; init; }
        public int Counter { get; set; }
        public List<string>? Cells { get; set; }
        public int RowsEmitted { get; set; }
        public int RowStart { get; set; }
    }

    private sealed class Writer
    {
        private readonly StringBuilder _out = new();
        private readonly StringBuilder _title = new();
        private readonly List<Frame> _stack = new();
        private Uri? _base;
        private int _skip;
        private int _pre;
        private bool _inTitle;
        private bool _titleDone;

        public Writer(Uri? baseUri)
        {
            _base = baseUri;
        }

        public void Accept(HtmlToken token)
        {
            switch (token.Kind)
            {
                case HtmlTokenKind.Text:
                    if (_inTitle)
                    {
                        _title.Append(token.Text);
                    }
                    else
                    {
                        Text(token.Text);
                    }

                    break;
                case HtmlTokenKind.Open:
                    Open(token);
                    break;
                case HtmlTokenKind.Close:
                    Close(token.Name);
                    break;
            }
        }

        public PageText Finish()
        {
            while (_stack.Count > 0)
            {
                Pop();
            }

            string markdown = Tidy(_out.ToString(), _anchors, out var anchors);
            return new PageText(Collapse(_title.ToString()), markdown, anchors);
        }

        /// <summary>The most anchors a page keeps (2026-10-17); past it a page's later ids are not recorded.</summary>
        public const int MaxAnchors = 10_000;

        // Each id's offset in the output as written, first one wins; Truncate pulls back any past the cut.
        private readonly List<(string Id, int Offset)> _anchors = new();
        private readonly HashSet<string> _anchorIds = new(StringComparer.Ordinal);

        private void Anchor(HtmlToken token)
        {
            if (_anchors.Count >= MaxAnchors)
            {
                return;
            }

            Add(token.Attribute("id"));
            if (token.Name == "a")
            {
                Add(token.Attribute("name"));
            }

            void Add(string? id)
            {
                if (!string.IsNullOrWhiteSpace(id) && _anchorIds.Add(id.Trim()))
                {
                    _anchors.Add((id.Trim(), _out.Length));
                }
            }
        }

        // ── Elements ────────────────────────────────────────────────────────

        private void Open(HtmlToken token)
        {
            string name = token.Name;
            if (name == "title")
            {
                // The first document title; an <svg><title> is an icon's tooltip, never the page's.
                if (!_titleDone && !InElement("svg"))
                {
                    _inTitle = true;
                }

                return;
            }

            if (name == "base")
            {
                var href = token.Attribute("href");
                if (!string.IsNullOrWhiteSpace(href) && Uri.TryCreate(_base, href, out var resolved) && resolved.IsAbsoluteUri)
                {
                    _base = resolved;
                }

                return;
            }

            if (_skip > 0)
            {
                if (!token.SelfClosing)
                {
                    Push(new Frame(name, FrameKind.Skip, _out.Length));
                }

                return;
            }

            if (IsDropped(token))
            {
                if (!token.SelfClosing)
                {
                    Push(new Frame(name, FrameKind.Skip, _out.Length));
                }

                return;
            }

            // A fragment's target (2026-10-17): where the element's text will start, chrome and dropped subtrees left out.
            Anchor(token);
            switch (name)
            {
                case "br":
                    if (!EndsWith("\n\n"))
                    {
                        TrimTrailingSpaces();
                        _out.Append('\n');
                    }

                    return;
                case "hr":
                    BlockBreak();
                    _out.Append("---");
                    BlockBreak();
                    return;
                case "img":
                    var alt = Collapse(token.Attribute("alt") ?? "");
                    if (alt.Length > 0)
                    {
                        Text(" [image: " + alt + "] ");
                    }

                    return;
            }

            if (token.SelfClosing)
            {
                return;
            }

            switch (name)
            {
                case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                    BlockBreak();
                    string marker = new string('#', name[1] - '0') + " ";
                    _out.Append(marker);
                    Push(new Frame(name, FrameKind.Heading, _out.Length) { Marker = marker });
                    return;
                case "ul" or "ol":
                    if (ListDepth() == 0)
                    {
                        BlockBreak();
                    }
                    else
                    {
                        LineBreak();
                    }

                    Push(new Frame(name, FrameKind.List, _out.Length) { Ordered = name == "ol" });
                    return;
                case "li":
                    LineBreak();
                    var list = Innermost(FrameKind.List);
                    int depth = Math.Max(ListDepth(), 1);
                    int bullet = _out.Length;
                    _out.Append(' ', 2 * (depth - 1));
                    if (list is { Ordered: true })
                    {
                        list.Counter++;
                        _out.Append(list.Counter.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(". ");
                    }
                    else
                    {
                        _out.Append("- ");
                    }

                    Push(new Frame(name, FrameKind.Item, _out.Length) { RowStart = bullet });
                    return;
                case "dt":
                    LineBreak();
                    Push(new Frame(name, FrameKind.Plain, _out.Length));
                    return;
                case "dd":
                    LineBreak();
                    _out.Append("  ");
                    Push(new Frame(name, FrameKind.Plain, _out.Length));
                    return;
                case "a":
                    if (Innermost(FrameKind.Link) is not null)
                    {
                        Push(new Frame(name, FrameKind.Plain, _out.Length));
                        return;
                    }

                    Push(new Frame(name, FrameKind.Link, _out.Length) { Href = Resolve(token.Attribute("href")) });
                    return;
                case "strong" or "b":
                    PushEmphasis(name, "**");
                    return;
                case "em" or "i":
                    PushEmphasis(name, "*");
                    return;
                case "code":
                    if (_pre > 0)
                    {
                        // A ```lang fence when <pre><code class="language-x"> says which.
                        string? language = Language(token.Attribute("class"));
                        if (language is not null && EndsWith("```\n"))
                        {
                            _out.Length -= 1;
                            _out.Append(language).Append('\n');
                        }

                        Push(new Frame(name, FrameKind.Plain, _out.Length));
                        return;
                    }

                    Push(new Frame(name, FrameKind.Code, _out.Length));
                    return;
                case "pre":
                    BlockBreak();
                    _out.Append("```\n");
                    _pre++;
                    Push(new Frame(name, FrameKind.Pre, _out.Length));
                    return;
                case "table":
                    BlockBreak();
                    Push(new Frame(name, FrameKind.Table, _out.Length));
                    return;
                case "tr":
                    Push(new Frame(name, FrameKind.Row, _out.Length) { Cells = new List<string>(), RowStart = _out.Length });
                    return;
                case "td" or "th":
                    Push(new Frame(name, FrameKind.Cell, _out.Length));
                    return;
            }

            if (Blocks.Contains(name))
            {
                BlockBreak();
            }

            Push(new Frame(name, FrameKind.Plain, _out.Length));
        }

        private void Close(string name)
        {
            if (name == "title")
            {
                if (_inTitle)
                {
                    _inTitle = false;
                    _titleDone = true;
                }

                return;
            }

            for (int i = _stack.Count - 1; i >= 0; i--)
            {
                if (_stack[i].Name == name)
                {
                    while (_stack.Count > i)
                    {
                        Pop();
                    }

                    return;
                }
            }
            // A close with no open: ignored, as a browser does.
        }

        private void Push(Frame frame)
        {
            _stack.Add(frame);
            if (frame.Kind == FrameKind.Skip)
            {
                _skip++;
            }
        }

        private void Pop()
        {
            var frame = _stack[^1];
            _stack.RemoveAt(_stack.Count - 1);
            switch (frame.Kind)
            {
                case FrameKind.Skip:
                    _skip--;
                    return;
                case FrameKind.Heading:
                    if (Collapse(Since(frame.Start)).Length == 0)
                    {
                        Truncate(frame.Start - frame.Marker.Length);
                    }

                    BlockBreak();
                    return;
                case FrameKind.List:
                    if (ListDepth() == 0)
                    {
                        BlockBreak();
                    }
                    else
                    {
                        LineBreak();
                    }

                    return;
                case FrameKind.Item:
                    // An item with nothing readable (an icon, an empty link): no bullet either.
                    if (Collapse(Since(frame.Start)).Length == 0)
                    {
                        Truncate(frame.RowStart);
                        if (Innermost(FrameKind.List) is { Ordered: true } ordered)
                        {
                            ordered.Counter--;
                        }

                        return;
                    }

                    LineBreak();
                    return;
                case FrameKind.Link:
                    CloseLink(frame);
                    return;
                case FrameKind.Emphasis:
                    Wrap(frame, frame.Marker);
                    return;
                case FrameKind.Code:
                    if (Since(frame.Start).Contains('`'))
                    {
                        return;
                    }

                    Wrap(frame, "`");
                    return;
                case FrameKind.Pre:
                    _pre--;
                    if (!EndsWith("\n"))
                    {
                        _out.Append('\n');
                    }

                    _out.Append("```");
                    BlockBreak();
                    return;
                case FrameKind.Table:
                    BlockBreak();
                    return;
                case FrameKind.Cell:
                    if (Innermost(FrameKind.Row) is { } row && row.Cells is not null)
                    {
                        row.Cells.Add(Collapse(Since(frame.Start)).Replace("|", "\\|", StringComparison.Ordinal));
                        Truncate(frame.Start);
                    }

                    return;
                case FrameKind.Row:
                    EmitRow(frame);
                    return;
                default:
                    if (Blocks.Contains(frame.Name) || frame.Name is "dt" or "dd")
                    {
                        if (frame.Name is "dt" or "dd")
                        {
                            LineBreak();
                        }
                        else
                        {
                            BlockBreak();
                        }
                    }

                    return;
            }
        }

        private void PushEmphasis(string name, string marker)
        {
            if (_pre > 0 || _stack.Any(f => f.Kind == FrameKind.Emphasis && f.Marker == marker))
            {
                Push(new Frame(name, FrameKind.Plain, _out.Length));
                return;
            }

            Push(new Frame(name, FrameKind.Emphasis, _out.Length) { Marker = marker });
        }

        private void CloseLink(Frame frame)
        {
            string inner = Since(frame.Start);
            // Blocks inside the link (a card): one line, the heading markers off.
            string text = inner.Contains('\n')
                ? Collapse(string.Join(" ", inner.Split('\n').Select(line => line.TrimStart().TrimStart('#'))))
                : Collapse(inner);
            if (frame.Href is null || text.Length == 0)
            {
                return;
            }

            Truncate(frame.Start);
            if (string.Equals(text, frame.Href, StringComparison.Ordinal))
            {
                Text(text);
                return;
            }

            bool leadingSpace = inner.Length > 0 && char.IsWhiteSpace(inner[0]);
            bool trailingSpace = inner.Length > 0 && char.IsWhiteSpace(inner[^1]);
            if (leadingSpace)
            {
                Text(" ");
            }

            _out.Append('[').Append(text.Replace("]", "\\]", StringComparison.Ordinal)).Append("](").Append(frame.Href).Append(')');
            if (trailingSpace)
            {
                Text(" ");
            }
        }

        private void Wrap(Frame frame, string marker)
        {
            string inner = Since(frame.Start);
            string trimmed = inner.Trim();
            if (trimmed.Length == 0 || trimmed.Contains('\n'))
            {
                return;
            }

            int lead = inner.Length - inner.TrimStart().Length;
            int trail = inner.Length - inner.TrimEnd().Length;
            Truncate(frame.Start);
            _out.Append(inner, 0, lead).Append(marker).Append(trimmed).Append(marker).Append(inner, inner.Length - trail, trail);
        }

        private void EmitRow(Frame row)
        {
            Truncate(row.RowStart);
            var cells = row.Cells ?? new List<string>();
            if (cells.Count == 0 || cells.All(c => c.Length == 0))
            {
                return;
            }

            LineBreak();
            _out.Append("| ").Append(string.Join(" | ", cells)).Append(" |\n");
            var table = Innermost(FrameKind.Table);
            if (table is not null)
            {
                table.RowsEmitted++;
                if (table.RowsEmitted == 1)
                {
                    _out.Append("| ").Append(string.Join(" | ", cells.Select(_ => "---"))).Append(" |\n");
                }
            }
        }

        // ── Text and breaks ─────────────────────────────────────────────────

        private void Text(string text)
        {
            if (_skip > 0 || text.Length == 0)
            {
                return;
            }

            if (_pre > 0)
            {
                _out.Append(text);
                return;
            }

            foreach (char c in text)
            {
                if (char.IsWhiteSpace(c))
                {
                    if (_out.Length > 0 && _out[^1] != ' ' && _out[^1] != '\n')
                    {
                        _out.Append(' ');
                    }

                    continue;
                }

                _out.Append(c);
            }
        }

        private void BlockBreak()
        {
            if (_skip > 0 || _out.Length == 0)
            {
                return;
            }

            TrimTrailingSpaces();
            if (_out.Length == 0)
            {
                return;
            }

            if (!EndsWith("\n"))
            {
                _out.Append("\n\n");
            }
            else if (!EndsWith("\n\n"))
            {
                _out.Append('\n');
            }
        }

        private void LineBreak()
        {
            if (_skip > 0 || _out.Length == 0)
            {
                return;
            }

            TrimTrailingSpaces();
            if (_out.Length > 0 && !EndsWith("\n"))
            {
                _out.Append('\n');
            }
        }

        private void TrimTrailingSpaces()
        {
            while (_out.Length > 0 && _out[^1] == ' ')
            {
                _out.Length--;
            }
        }

        private bool EndsWith(string tail)
        {
            if (_out.Length < tail.Length)
            {
                return false;
            }

            for (int i = 0; i < tail.Length; i++)
            {
                if (_out[_out.Length - tail.Length + i] != tail[i])
                {
                    return false;
                }
            }

            return true;
        }

        private string Since(int start) => start >= _out.Length ? "" : _out.ToString(start, _out.Length - start);

        /// <summary>Cuts the output back to <paramref name="start"/>; never past what is there (the setter would pad).</summary>
        private void Truncate(int start)
        {
            if (start < _out.Length)
            {
                _out.Length = Math.Max(start, 0);
                // An anchor in what was cut lands where the rewrite starts (a link's text, a table's row).
                for (int i = _anchors.Count - 1; i >= 0 && _anchors[i].Offset > _out.Length; i--)
                {
                    _anchors[i] = (_anchors[i].Id, _out.Length);
                }
            }
        }

        // ── Stack queries ───────────────────────────────────────────────────

        private Frame? Innermost(FrameKind kind)
        {
            for (int i = _stack.Count - 1; i >= 0; i--)
            {
                if (_stack[i].Kind == kind)
                {
                    return _stack[i];
                }
            }

            return null;
        }

        private int ListDepth() => _stack.Count(f => f.Kind == FrameKind.List);

        private bool InElement(string name) => _stack.Any(f => f.Name == name);

        private static bool IsDropped(HtmlToken token)
        {
            if (Dropped.Contains(token.Name))
            {
                return true;
            }

            if (token.Attributes is null)
            {
                return false;
            }

            if (token.Attributes.ContainsKey("hidden"))
            {
                return true;
            }

            if (string.Equals(token.Attribute("aria-hidden"), "true", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var role = token.Attribute("role");
            return role is not null && DroppedRoles.Contains(role.Trim());
        }

        private string? Resolve(string? href)
        {
            if (string.IsNullOrWhiteSpace(href))
            {
                return null;
            }

            href = href.Trim();
            if (href.StartsWith('#') || href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) || href.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            Uri? resolved;
            if (_base is not null)
            {
                if (!Uri.TryCreate(_base, href, out resolved))
                {
                    return null;
                }
            }
            else if (!Uri.TryCreate(href, UriKind.Absolute, out resolved))
            {
                return null;
            }

            return resolved.IsAbsoluteUri ? resolved.AbsoluteUri : null;
        }

        private static string? Language(string? classes)
        {
            if (classes is null)
            {
                return null;
            }

            foreach (var word in classes.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                foreach (var prefix in new[] { "language-", "lang-" })
                {
                    if (word.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && word.Length > prefix.Length)
                    {
                        return word[prefix.Length..];
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Trailing spaces off every line, blank runs to one blank line (fences included), nothing blank at either end. Each of
        /// <paramref name="anchors"/> comes out as the start of its line in the result (2026-10-17): a line dropped as blank sends it to
        /// the next line kept, and with none after, to the last; null when there are none.
        /// </summary>
        private static string Tidy(string text, List<(string Id, int Offset)> anchors, out IReadOnlyDictionary<string, int>? mapped)
        {
            var lines = text.Split('\n');
            var sb = new StringBuilder(text.Length);
            var starts = new int[lines.Length];   // each line's start in the result, -1 when dropped
            bool blankPending = false;
            bool any = false;
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].TrimEnd();
                if (line.Length == 0)
                {
                    blankPending = any;
                    starts[i] = -1;
                    continue;
                }

                if (blankPending)
                {
                    sb.Append('\n');
                    blankPending = false;
                }

                if (any)
                {
                    sb.Append('\n');
                }

                starts[i] = sb.Length;
                sb.Append(line);
                any = true;
            }

            mapped = anchors.Count == 0 ? null : MapAnchors(lines, starts, anchors);
            return sb.ToString();
        }

        private static Dictionary<string, int> MapAnchors(string[] lines, int[] starts, List<(string Id, int Offset)> anchors)
        {
            // The kept line at or after each line, else the last kept one (0 for an empty result).
            var target = new int[lines.Length];
            int next = -1;
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                if (starts[i] >= 0)
                {
                    next = starts[i];
                }

                target[i] = next;
            }

            int last = starts.Where(s => s >= 0).DefaultIfEmpty(0).Max();
            var lineStarts = new int[lines.Length];
            for (int i = 1; i < lines.Length; i++)
            {
                lineStarts[i] = lineStarts[i - 1] + lines[i - 1].Length + 1;
            }

            var map = new Dictionary<string, int>(anchors.Count, StringComparer.Ordinal);
            foreach (var (id, offset) in anchors)
            {
                int line = Array.BinarySearch(lineStarts, offset);
                line = line >= 0 ? line : ~line - 1;
                line = Math.Clamp(line, 0, lines.Length - 1);
                // A block's id is recorded before the break the block writes ahead of itself: nothing of its own on that line yet,
                // so it belongs to the next one.
                int column = Math.Min(Math.Max(offset - lineStarts[line], 0), lines[line].Length);
                if (lines[line].AsSpan(column).IsWhiteSpace() && line + 1 < lines.Length)
                {
                    line++;
                }

                map[id] = target[line] >= 0 ? target[line] : last;
            }

            return map;
        }
    }
}
