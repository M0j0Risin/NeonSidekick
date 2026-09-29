using System.Globalization;
using System.Text;
using NeonSidekick.UI.Markdown;
using NeonSidekick.Viewer;

namespace NeonSidekick.Printing;

/// <summary>
/// Paper layout (2026-09-28, the user's calls: a listing for code and text, light styling for markdown, a picture fitted to
/// one page, a header on every page): what goes where, in points, against a <see cref="PageMetrics"/> and an
/// <see cref="ITextMeasure"/> — the printer's own fonts when printing, a fixed advance in tests. Pure: nothing here touches
/// the spooler, so every decision is tested without a printer.
/// <list type="bullet">
/// <item><see cref="Listing"/> — monospace, tabs to four columns, long lines wrapped at the column, a form feed a new page;</item>
/// <item><see cref="Markdown"/> — the transcript's own parse (<see cref="MarkdigParser"/>): headings bold and stepped, prose
/// word-wrapped with bold, italic and code runs, lists with hanging indents, quotes and code blocks indented with a bar, a rule
/// a line, links as their text and address, tables as columns cut to fit;</item>
/// <item><see cref="Picture"/> — fitted to the page, never enlarged past its own size at 96 dpi, centred;</item>
/// <item><see cref="WithHeaders"/> — the title and the time on the left, <c>page N of M</c> on the right, a rule under them.</item>
/// </list>
/// A page range is applied after all of this (<see cref="PageRange"/>), so the numbers always count the whole document.
/// </summary>
public static class PrintLayout
{
    /// <summary>The margin every side keeps, or the printer's own if that is wider: 0.6 inch.</summary>
    public const double Margin = 43.2;

    /// <summary>The header's size in points.</summary>
    public const double HeaderSize = 8;

    /// <summary>The body size the setting falls back to, and its range.</summary>
    public const int DefaultFontSize = 10;
    public const int MinFontSize = 6;
    public const int MaxFontSize = 24;

    /// <summary>A tab's width in columns.</summary>
    public const int TabWidth = 4;

    /// <summary>The resolution a picture's own size is read at: a pixel is 0.75 pt.</summary>
    public const double PictureDpi = 96;

    private const double LineSpacing = 1.35;
    private const double Ascent = 1.02;
    private const double HeaderBlock = HeaderSize * 1.6;
    private const double HeaderGap = 10;

    /// <summary>The part of the sheet a page's content fills, and where the header sits above it.</summary>
    public sealed record Frame(double Left, double Top, double Right, double Bottom, double HeaderTop)
    {
        public double Width => Right - Left;

        public double Height => Bottom - Top;
    }

    /// <summary>The content frame on <paramref name="page"/>: the margin or the printer's reach, the header's room taken off the top.</summary>
    public static Frame FrameOf(PageMetrics page)
    {
        ArgumentNullException.ThrowIfNull(page);
        double left = Math.Max(Margin, page.PrintableLeft);
        double right = Math.Min(page.Width - Margin, page.PrintableRight);
        double headerTop = Math.Max(Margin, page.PrintableTop);
        double bottom = Math.Min(page.Height - Margin, page.PrintableBottom);
        if (right - left < 72)
        {
            // A sheet narrower than two margins and an inch: the printer's reach alone.
            left = page.PrintableLeft;
            right = page.PrintableRight;
        }

        double top = headerTop + HeaderBlock + HeaderGap;
        if (bottom - top < 72)
        {
            headerTop = page.PrintableTop;
            top = headerTop + HeaderBlock + HeaderGap;
            bottom = page.PrintableBottom;
        }

        return new Frame(left, top, right, Math.Max(top + 1, bottom), headerTop);
    }

    // ─── lines and pages ───────────────────────────────────────────────────────

    /// <summary>
    /// One laid-out line before pagination: its height, the ops on it (Y relative to the line's top), the space it wants above
    /// it (dropped at the top of a page), whether it stays on the page of the line after it (a heading), and whether it starts a
    /// page (a form feed).
    /// </summary>
    private sealed class Line
    {
        public double Height;
        public double SpaceBefore;
        public bool KeepWithNext;
        public bool BreakBefore;
        public readonly List<PrintOp> Ops = [];
    }

    /// <summary>The lines placed on pages from <paramref name="frame"/>'s top, a new page whenever the next would cross its bottom.</summary>
    private static List<PrintPage> Paginate(IReadOnlyList<Line> lines, Frame frame)
    {
        var pages = new List<PrintPage>();
        var ops = new List<PrintOp>();
        double y = frame.Top;
        bool empty = true;

        void NewPage()
        {
            pages.Add(new PrintPage(pages.Count + 1, ops));
            ops = [];
            y = frame.Top;
            empty = true;
        }

        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.BreakBefore && !empty)
            {
                NewPage();
            }

            double space = empty ? 0 : line.SpaceBefore;
            if (!empty && y + space + line.Height > frame.Bottom)
            {
                NewPage();
                space = 0;
            }
            else if (!empty && line.KeepWithNext && i + 1 < lines.Count)
            {
                var next = lines[i + 1];
                bool nextFitsHere = y + space + line.Height + next.SpaceBefore + next.Height <= frame.Bottom;
                bool pairFitsFresh = line.Height + next.SpaceBefore + next.Height <= frame.Height;
                if (!nextFitsHere && pairFitsFresh && !next.BreakBefore)
                {
                    NewPage();
                    space = 0;
                }
            }

            double top = y + space;
            foreach (var op in line.Ops)
            {
                ops.Add(Shift(op, top));
            }

            y = top + line.Height;
            empty = false;
        }

        if (!empty || pages.Count == 0)
        {
            pages.Add(new PrintPage(pages.Count + 1, ops));
        }

        return pages;
    }

    private static PrintOp Shift(PrintOp op, double dy) => op switch
    {
        PrintTextOp t => t with { Y = t.Y + dy },
        PrintBoxOp b => b with { Y = b.Y + dy },
        PrintImageOp i => i with { Y = i.Y + dy },
        _ => op,
    };

    private static Line TextLine(double size, double spaceBefore = 0) => new() { Height = size * LineSpacing, SpaceBefore = spaceBefore };

    private static double Baseline(double size) => size * Ascent;

    // ─── the listing ──────────────────────────────────────────────────────────

    /// <summary>
    /// <paramref name="text"/> as a monospace listing at <paramref name="size"/> points: every line its own, tabs to
    /// <see cref="TabWidth"/> columns, a line longer than the frame wrapped at the last column that fits, a form feed a new page,
    /// other control characters dropped.
    /// </summary>
    public static IReadOnlyList<PrintPage> Listing(string text, PageMetrics page, ITextMeasure measure, double size)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(measure);
        var frame = FrameOf(page);
        var font = new PrintFont(PrintFace.Mono, size);
        var lines = new List<Line>();
        foreach (var (row, breakBefore) in Rows(text))
        {
            bool first = true;
            foreach (string piece in WrapColumns(row, Columns(frame.Width, measure, font)))
            {
                var line = TextLine(size);
                line.BreakBefore = breakBefore && first;
                if (piece.Length > 0)
                {
                    line.Ops.Add(new PrintTextOp(frame.Left, Baseline(size), piece, font));
                }

                lines.Add(line);
                first = false;
            }
        }

        return Paginate(lines, frame);
    }

    /// <summary>How many monospace columns fit in <paramref name="width"/>: at least ten.</summary>
    public static int Columns(double width, ITextMeasure measure, PrintFont font)
    {
        ArgumentNullException.ThrowIfNull(measure);
        double advance = measure.Width("MMMMMMMMMM", font) / 10;
        if (advance <= 0)
        {
            advance = font.Size * 0.6;
        }

        return Math.Max(10, (int)Math.Floor((width + 0.01) / advance));
    }

    /// <summary>The text's lines with tabs expanded and control characters gone, each marked when a form feed put it on a new page.</summary>
    public static IEnumerable<(string Row, bool BreakBefore)> Rows(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string normal = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        bool pending = false;
        foreach (string raw in normal.Split('\n'))
        {
            var row = new StringBuilder(raw.Length);
            foreach (char c in raw)
            {
                if (c == '\t')
                {
                    row.Append(' ', TabWidth - (row.Length % TabWidth));
                }
                else if (c == '\f')
                {
                    if (row.Length > 0)
                    {
                        yield return (row.ToString(), pending);
                        row.Clear();
                    }

                    pending = true;
                }
                else if (c >= ' ' && c != '\u007F')
                {
                    row.Append(c);
                }
            }

            yield return (row.ToString(), pending);
            pending = false;
        }
    }

    /// <summary><paramref name="row"/> cut every <paramref name="columns"/> characters, never between the halves of a surrogate pair; an empty row is one empty piece.</summary>
    public static IEnumerable<string> WrapColumns(string row, int columns)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
        if (row.Length <= columns)
        {
            yield return row;
            yield break;
        }

        int at = 0;
        while (at < row.Length)
        {
            int take = Math.Min(columns, row.Length - at);
            if (at + take < row.Length && char.IsLowSurrogate(row[at + take]) && take > 1)
            {
                take--;
            }

            yield return row.Substring(at, take);
            at += take;
        }
    }

    // ─── markdown ─────────────────────────────────────────────────────────────

    /// <summary>A markdown document at <paramref name="size"/> points: the blocks in order, see the class note for the styling.</summary>
    public static IReadOnlyList<PrintPage> Markdown(MarkdownDocument document, PageMetrics page, ITextMeasure measure, double size)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(measure);
        var frame = FrameOf(page);
        var flow = new MarkdownFlow(measure, size, frame.Right);
        var lines = new List<Line>();
        flow.Blocks(document.Blocks, frame.Left, lines, 0);
        return Paginate(lines, frame);
    }

    /// <summary>The size a heading of <paramref name="level"/> prints at, over a body of <paramref name="size"/>.</summary>
    public static double HeadingSize(int level, double size) => level switch
    {
        1 => size * 1.7,
        2 => size * 1.4,
        3 => size * 1.2,
        _ => size * 1.05,
    };

    /// <summary>The bullet of a list nested <paramref name="depth"/> deep.</summary>
    public static string Bullet(int depth) => (depth % 3) switch
    {
        0 => "•",
        1 => "◦",
        _ => "▪",
    };

    /// <summary>How a link prints: its text, then its address in brackets unless the text already is the address.</summary>
    public static string LinkText(string text, string url)
    {
        string label = text ?? "";
        string address = url ?? "";
        if (address.Length == 0 || address.StartsWith('#') || string.Equals(label.Trim(), address.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return label.Length > 0 ? label : address;
        }

        return label.Length == 0 ? address : label + " (" + address + ")";
    }

    /// <summary>The inlines as plain text (a table cell): links as <see cref="LinkText"/>, a break a space.</summary>
    public static string Plain(IReadOnlyList<MarkdownInline> inlines)
    {
        ArgumentNullException.ThrowIfNull(inlines);
        var text = new StringBuilder();
        foreach (var inline in inlines)
        {
            text.Append(inline switch
            {
                TextRun run => run.Text,
                LinkRun link => LinkText(link.Text, link.Url),
                HardBreak => " ",
                _ => "",
            });
        }

        return text.ToString();
    }

    private sealed class MarkdownFlow(ITextMeasure measure, double size, double right)
    {
        private readonly double _blockGap = size * 0.6;

        public void Blocks(IReadOnlyList<MarkdownBlock> blocks, double left, List<Line> lines, int depth, double firstGap = -1)
        {
            for (int i = 0; i < blocks.Count; i++)
            {
                double gap = i == 0 && firstGap >= 0 ? firstGap : _blockGap;
                Block(blocks[i], left, lines, depth, gap);
            }
        }

        private void Block(MarkdownBlock block, double left, List<Line> lines, int depth, double gap)
        {
            switch (block)
            {
                case HeadingBlock heading:
                {
                    double headingSize = HeadingSize(heading.Level, size);
                    var font = new PrintFont(PrintFace.Sans, headingSize, Bold: true);
                    int start = lines.Count;
                    Wrap(Runs(heading.Inlines, font), left, lines, heading.Level <= 2 ? headingSize * 0.9 : headingSize * 0.7);
                    for (int i = start; i < lines.Count; i++)
                    {
                        lines[i].KeepWithNext = true;
                    }

                    break;
                }

                case ParagraphBlock paragraph:
                    Wrap(Runs(paragraph.Inlines, new PrintFont(PrintFace.Sans, size)), left, lines, gap);
                    break;
                case CodeBlock code:
                    Code(code, left, lines, gap);
                    break;
                case ListBlock list:
                    List(list, left, lines, depth, gap);
                    break;
                case QuoteBlock quote:
                {
                    int start = lines.Count;
                    Blocks(quote.Blocks, left + 14, lines, depth, gap);
                    for (int i = start; i < lines.Count; i++)
                    {
                        lines[i].Ops.Add(new PrintBoxOp(left + 2, 0, 2, lines[i].Height));
                    }

                    break;
                }

                case RuleBlock:
                {
                    var line = new Line { Height = size, SpaceBefore = gap };
                    line.Ops.Add(new PrintBoxOp(left, size * 0.5, right - left, 0.75));
                    lines.Add(line);
                    break;
                }

                case TableBlock table:
                    Table(table, left, lines, gap);
                    break;
            }
        }

        private void Code(CodeBlock code, double left, List<Line> lines, double gap)
        {
            var font = new PrintFont(PrintFace.Mono, size * 0.9);
            double indent = left + 12;
            int columns = Columns(right - indent, measure, font);
            bool first = true;
            foreach (string source in code.Lines)
            {
                foreach (var (row, _) in Rows(source))
                {
                    foreach (string piece in WrapColumns(row, columns))
                    {
                        var line = TextLine(font.Size, first ? gap : 0);
                        if (piece.Length > 0)
                        {
                            line.Ops.Add(new PrintTextOp(indent, Baseline(font.Size), piece, font));
                        }

                        line.Ops.Add(new PrintBoxOp(left + 2, 0, 0.75, line.Height));
                        lines.Add(line);
                        first = false;
                    }
                }
            }
        }

        private void List(ListBlock list, double left, List<Line> lines, int depth, double gap)
        {
            var font = new PrintFont(PrintFace.Sans, size);
            string widest = list.Ordered ? (list.Start + list.Items.Count - 1).ToString(CultureInfo.InvariantCulture) + "." : Bullet(depth);
            double hang = measure.Width(widest, font) + size * 0.6;
            for (int i = 0; i < list.Items.Count; i++)
            {
                string marker = list.Ordered ? (list.Start + i).ToString(CultureInfo.InvariantCulture) + "." : Bullet(depth);
                int start = lines.Count;
                Blocks(list.Items[i].Blocks, left + hang, lines, depth + 1, i == 0 ? gap : size * 0.25);
                if (lines.Count == start)
                {
                    lines.Add(TextLine(size, i == 0 ? gap : size * 0.25));
                }

                var first = lines[start];
                double baseline = first.Ops.OfType<PrintTextOp>().Select(t => t.Y).DefaultIfEmpty(Baseline(size)).First();
                first.Ops.Add(new PrintTextOp(left, baseline, marker, font));
            }
        }

        private void Table(TableBlock table, double left, List<Line> lines, double gap)
        {
            var body = new PrintFont(PrintFace.Sans, size * 0.95);
            var head = body with { Bold = true };
            const double pad = 8;
            int columns = Math.Max(table.Header.Cells.Count, table.Rows.Count == 0 ? 0 : table.Rows.Max(r => r.Cells.Count));
            if (columns == 0)
            {
                return;
            }

            var natural = new double[columns];
            void Measure(TableRow row, PrintFont font)
            {
                for (int c = 0; c < row.Cells.Count; c++)
                {
                    natural[c] = Math.Max(natural[c], measure.Width(Plain(row.Cells[c].Inlines), font) + pad);
                }
            }

            Measure(table.Header, head);
            foreach (var row in table.Rows)
            {
                Measure(row, body);
            }

            double available = right - left;
            double total = natural.Sum();
            var widths = natural.Select(w => total <= available || total <= 0 ? Math.Max(w, pad * 2) : Math.Max(pad * 2, available * w / total)).ToArray();

            void Row(TableRow row, PrintFont font, double space, bool rule)
            {
                var line = TextLine(font.Size, space);
                double x = left;
                for (int c = 0; c < columns; c++)
                {
                    string text = c < row.Cells.Count ? Plain(row.Cells[c].Inlines) : "";
                    string fitted = Fit(text, widths[c] - pad, font);
                    if (fitted.Length > 0)
                    {
                        var alignment = c < table.Alignments.Count ? table.Alignments[c] : TableAlignment.Default;
                        double w = measure.Width(fitted, font);
                        double at = alignment switch
                        {
                            TableAlignment.Right => x + widths[c] - pad - w,
                            TableAlignment.Center => x + (widths[c] - pad - w) / 2,
                            _ => x,
                        };
                        line.Ops.Add(new PrintTextOp(at, Baseline(font.Size), fitted, font));
                    }

                    x += widths[c];
                }

                if (rule)
                {
                    line.Ops.Add(new PrintBoxOp(left, line.Height - 1, Math.Min(available, widths.Sum()), 0.5));
                }

                lines.Add(line);
            }

            Row(table.Header, head, gap, rule: true);
            lines[^1].KeepWithNext = true;
            foreach (var row in table.Rows)
            {
                Row(row, body, 0, rule: false);
            }
        }

        /// <summary><paramref name="text"/> cut to <paramref name="width"/> with an ellipsis, whole when it fits.</summary>
        private string Fit(string text, double width, PrintFont font)
        {
            if (text.Length == 0 || measure.Width(text, font) <= width)
            {
                return text;
            }

            int low = 0;
            int high = text.Length;
            while (low < high)
            {
                int mid = (low + high + 1) / 2;
                if (measure.Width(text[..mid] + "…", font) <= width)
                {
                    low = mid;
                }
                else
                {
                    high = mid - 1;
                }
            }

            if (low > 0 && char.IsHighSurrogate(text[low - 1]))
            {
                low--;
            }

            return low == 0 ? "" : text[..low] + "…";
        }

        /// <summary>The inlines as runs of one font each: bold and italic on the base, code in the fixed face, a link with its address, a hard break as a newline.</summary>
        private static List<(string Text, PrintFont Font)> Runs(IReadOnlyList<MarkdownInline> inlines, PrintFont baseFont)
        {
            var runs = new List<(string, PrintFont)>();
            foreach (var inline in inlines)
            {
                switch (inline)
                {
                    case TextRun run when run.Code:
                        runs.Add((run.Text, new PrintFont(PrintFace.Mono, baseFont.Size * 0.92, baseFont.Bold)));
                        break;
                    case TextRun run:
                        runs.Add((run.Text, baseFont with { Bold = baseFont.Bold || run.Bold, Italic = baseFont.Italic || run.Italic }));
                        break;
                    case LinkRun link:
                        runs.Add((LinkText(link.Text, link.Url), baseFont with { Italic = true }));
                        break;
                    case HardBreak:
                        runs.Add(("\n", baseFont));
                        break;
                }
            }

            return runs;
        }

        /// <summary>Greedy word wrap of <paramref name="runs"/> from <paramref name="left"/> to the right edge; a word wider than the line is cut where it fills it.</summary>
        private void Wrap(List<(string Text, PrintFont Font)> runs, double left, List<Line> lines, double gap)
        {
            double width = Math.Max(24, right - left);
            var pieces = new List<(double X, string Text, PrintFont Font)>();
            double x = 0;
            bool firstLine = true;

            void Emit()
            {
                double tallest = pieces.Count == 0 ? size : pieces.Max(p => p.Font.Size);
                var line = TextLine(tallest, firstLine ? gap : 0);
                foreach (var (at, text, font) in Merge(pieces))
                {
                    line.Ops.Add(new PrintTextOp(left + at, Baseline(tallest), text, font));
                }

                lines.Add(line);
                pieces.Clear();
                x = 0;
                firstLine = false;
            }

            foreach (var (text, font) in runs)
            {
                foreach (string token in Tokens(text))
                {
                    if (token == "\n")
                    {
                        Emit();
                        continue;
                    }

                    bool space = token[0] == ' ';
                    if (space)
                    {
                        if (pieces.Count > 0)
                        {
                            double w = measure.Width(" ", font);
                            if (x + w <= width)
                            {
                                pieces.Add((x, " ", font));
                                x += w;
                            }
                        }

                        continue;
                    }

                    string word = token;
                    double wordWidth = measure.Width(word, font);
                    if (x > 0 && x + wordWidth > width)
                    {
                        TrimTrailingSpace(pieces);
                        Emit();
                    }

                    while (wordWidth > width && word.Length > 1)
                    {
                        int fit = FitChars(word, width - x, font);
                        pieces.Add((x, word[..fit], font));
                        Emit();
                        word = word[fit..];
                        wordWidth = measure.Width(word, font);
                    }

                    pieces.Add((x, word, font));
                    x += wordWidth;
                }
            }

            TrimTrailingSpace(pieces);
            if (pieces.Count > 0 || firstLine)
            {
                Emit();
            }
        }

        private static void TrimTrailingSpace(List<(double X, string Text, PrintFont Font)> pieces)
        {
            while (pieces.Count > 0 && pieces[^1].Text == " ")
            {
                pieces.RemoveAt(pieces.Count - 1);
            }
        }

        /// <summary>How many characters of <paramref name="word"/> fit in <paramref name="width"/>: at least one, never half a surrogate pair.</summary>
        private int FitChars(string word, double width, PrintFont font)
        {
            int low = 1;
            int high = word.Length - 1;
            while (low < high)
            {
                int mid = (low + high + 1) / 2;
                if (measure.Width(word[..mid], font) <= width)
                {
                    low = mid;
                }
                else
                {
                    high = mid - 1;
                }
            }

            if (low < word.Length && char.IsLowSurrogate(word[low]) && low > 1)
            {
                low--;
            }
            else if (low < word.Length && char.IsLowSurrogate(word[low]))
            {
                low++;
            }

            return low;
        }

        /// <summary>Neighbouring pieces in one font joined into one text op, placed where the first began.</summary>
        private static IEnumerable<(double X, string Text, PrintFont Font)> Merge(List<(double X, string Text, PrintFont Font)> pieces)
        {
            int i = 0;
            while (i < pieces.Count)
            {
                var (x, text, font) = pieces[i];
                var joined = new StringBuilder(text);
                int j = i + 1;
                while (j < pieces.Count && pieces[j].Font == font)
                {
                    joined.Append(pieces[j].Text);
                    j++;
                }

                string merged = joined.ToString();
                if (merged.Trim().Length > 0)
                {
                    yield return (x, merged, font);
                }

                i = j;
            }
        }
    }

    /// <summary>Words, single spaces (a run of whitespace is one) and newlines, in order.</summary>
    public static IEnumerable<string> Tokens(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '\n')
            {
                yield return "\n";
                i++;
            }
            else if (char.IsWhiteSpace(c))
            {
                while (i < text.Length && char.IsWhiteSpace(text[i]) && text[i] != '\n')
                {
                    i++;
                }

                yield return " ";
            }
            else
            {
                int start = i;
                while (i < text.Length && !char.IsWhiteSpace(text[i]))
                {
                    i++;
                }

                yield return text[start..i];
            }
        }
    }

    // ─── a picture ────────────────────────────────────────────────────────────

    /// <summary>
    /// <paramref name="bitmap"/> on one page: its own size at <see cref="PictureDpi"/>, shrunk to the frame when bigger (never
    /// enlarged), centred across and down.
    /// </summary>
    public static IReadOnlyList<PrintPage> Picture(ViewerBitmap bitmap, PageMetrics page)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        var frame = FrameOf(page);
        double width = bitmap.Width * 72 / PictureDpi;
        double height = bitmap.Height * 72 / PictureDpi;
        double scale = Math.Min(1, Math.Min(frame.Width / width, frame.Height / height));
        width *= scale;
        height *= scale;
        double x = frame.Left + (frame.Width - width) / 2;
        double y = frame.Top + (frame.Height - height) / 2;
        return [new PrintPage(1, [new PrintImageOp(x, y, width, height, bitmap)])];
    }

    // ─── the header ───────────────────────────────────────────────────────────

    /// <summary>
    /// Every page with its header: <paramref name="title"/> and <paramref name="stamp"/> on the left (the title cut to leave
    /// room), <see cref="PrintText.PageOf"/> on the right, a hairline under them.
    /// </summary>
    public static IReadOnlyList<PrintPage> WithHeaders(IReadOnlyList<PrintPage> pages, string title, string stamp, PageMetrics page, ITextMeasure measure)
    {
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(measure);
        var frame = FrameOf(page);
        var font = new PrintFont(PrintFace.Sans, HeaderSize);
        var bold = font with { Bold = true };
        double baseline = frame.HeaderTop + Baseline(HeaderSize);
        var result = new List<PrintPage>(pages.Count);
        foreach (var sheet in pages)
        {
            string number = PrintText.PageOf(sheet.Number, pages.Count);
            double numberWidth = measure.Width(number, font);
            string tail = stamp.Length > 0 ? "  ·  " + stamp : "";
            double tailWidth = measure.Width(tail, font);
            double room = frame.Width - numberWidth - tailWidth - 18;
            string shown = Clip(title, room, bold, measure);
            double shownWidth = measure.Width(shown, bold);
            var ops = new List<PrintOp>(sheet.Ops.Count + 4)
            {
                new PrintTextOp(frame.Left, baseline, shown, bold),
            };
            if (tail.Length > 0)
            {
                ops.Add(new PrintTextOp(frame.Left + shownWidth, baseline, tail, font));
            }

            ops.Add(new PrintTextOp(frame.Right - numberWidth, baseline, number, font));
            ops.Add(new PrintBoxOp(frame.Left, frame.HeaderTop + HeaderBlock, frame.Width, 0.5));
            ops.AddRange(sheet.Ops);
            result.Add(sheet with { Ops = ops });
        }

        return result;
    }

    private static string Clip(string text, double width, PrintFont font, ITextMeasure measure)
    {
        if (measure.Width(text, font) <= width)
        {
            return text;
        }

        int keep = text.Length;
        while (keep > 1 && measure.Width(text[..keep] + "…", font) > width)
        {
            keep = Math.Max(1, keep * 9 / 10);
        }

        if (char.IsHighSurrogate(text[keep - 1]) && keep > 1)
        {
            keep--;
        }

        return text[..keep] + "…";
    }

    // ─── page ranges ──────────────────────────────────────────────────────────

    /// <summary>
    /// The pages <paramref name="text"/> names out of <paramref name="total"/>: <c>3</c>, <c>1-3</c>, <c>4-</c> (to the end),
    /// joined with commas. Blank is every page. False with <paramref name="error"/> for anything else, or a page past the end.
    /// </summary>
    public static bool PageRange(string? text, int total, out IReadOnlySet<int> pages, out string error)
    {
        var chosen = new SortedSet<int>();
        pages = chosen;
        error = "";
        string spec = (text ?? "").Trim();
        if (spec.Length == 0)
        {
            for (int n = 1; n <= total; n++)
            {
                chosen.Add(n);
            }

            return true;
        }

        foreach (string raw in spec.Split(',', StringSplitOptions.TrimEntries))
        {
            if (raw.Length == 0)
            {
                continue;
            }

            int dash = raw.IndexOf('-', StringComparison.Ordinal);
            string from = dash < 0 ? raw : raw[..dash].Trim();
            string to = dash < 0 ? raw : raw[(dash + 1)..].Trim();
            if (!int.TryParse(from, NumberStyles.None, CultureInfo.InvariantCulture, out int first))
            {
                error = PrintText.BadPages(spec, total);
                return false;
            }

            int last = total;
            if (to.Length > 0 && !int.TryParse(to, NumberStyles.None, CultureInfo.InvariantCulture, out last))
            {
                error = PrintText.BadPages(spec, total);
                return false;
            }

            if (first < 1 || last < first || first > total)
            {
                error = PrintText.BadPages(spec, total);
                return false;
            }

            for (int n = first; n <= Math.Min(last, total); n++)
            {
                chosen.Add(n);
            }
        }

        if (chosen.Count == 0)
        {
            error = PrintText.BadPages(spec, total);
            return false;
        }

        return true;
    }
}
