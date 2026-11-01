using Spectre.Console;
using Spectre.Console.Rendering;

namespace NeonSidekick.UI.Markdown;

/// <summary>
/// A <see cref="MarkdownDocument"/> as Spectre renderables: paragraphs and headings as styled
/// <see cref="Paragraph"/>s, lists and quotes as <see cref="HangingIndent"/>s, code blocks as an
/// indented block under a dim language label, a rule, a table. The markers are consumed —
/// <c>**bold**</c> is bold text, <c>- item</c> is <c>• item</c> — and every colour is the theme's.
/// Model text never goes through <see cref="Markup"/>: a <c>[</c> in a reply is a <c>[</c>.
///
/// <para>Pure and lazy: the tree is composed on the first render and the class holds no console,
/// because the pane renders the reply block on its tick thread under its own lock.</para>
/// </summary>
public sealed class MarkdownView : IRenderable
{
    /// <summary>The parser the transcript reads replies with: <see cref="MarkdigParser.Replies"/>, a single line break a new line (2026-11-01).</summary>
    public static readonly IMarkdownParser DefaultParser = MarkdigParser.Replies;

    public const string BulletGlyph = "• ";
    public const string QuoteGlyph = "▎ ";
    public const string CodeIndent = "  ";
    /// <summary>The label above a fenced block with no language.</summary>
    public const string CodeLabel = "code";

    /// <summary>
    /// Ahead of every code block's label (2026-09-22, the user's pick): the scroll, <c>📜 csharp</c>.
    /// U+1F4DC is emoji-presentation by default — two cells without a selector, a surrogate pair to
    /// <see cref="TextCells"/> — so one space after it, as after the tools' glyph.
    /// </summary>
    public const string CodeGlyph = "📜 ";

    /// <summary>A code block's label line as drawn: <see cref="CodeGlyph"/> and the language, or <see cref="CodeLabel"/>.</summary>
    public static string CodeHeading(string? language) => CodeGlyph + (language ?? CodeLabel);

    private static readonly IRenderable Spacer = new Text(" ");

    private readonly MarkdownDocument _document;
    private readonly Func<string, string?>? _resolvePath;
    private IRenderable? _content;

    /// <param name="document">The reply's tree.</param>
    /// <param name="resolvePath">
    /// Turns an inline code span's text into the full path of a file or folder inside the working folder, or null (2026-11-01, clickable
    /// paths): the span then carries a <see cref="LinkText.PathScheme"/> link. Null (the default, smoke and most tests) makes no path links.
    /// </param>
    public MarkdownView(MarkdownDocument document, Func<string, string?>? resolvePath = null)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
        _resolvePath = resolvePath;
    }

    /// <summary>The view of <paramref name="text"/> read by <see cref="DefaultParser"/>.</summary>
    public static MarkdownView Of(string text, IMarkdownParser? parser = null) =>
        new((parser ?? DefaultParser).Parse(text));

    public Measurement Measure(RenderOptions options, int maxWidth) => Content.Measure(options, maxWidth);

    public IEnumerable<Segment> Render(RenderOptions options, int maxWidth) => Content.Render(options, maxWidth);

    private IRenderable Content => _content ??= Compose(_document.Blocks, quote: null, tight: false, _resolvePath);

    private IReadOnlyList<CodeSpan>? _spans;
    private int _spansWidth = -1;

    /// <summary>
    /// The top-level code blocks' rows in this view rendered at <paramref name="maxWidth"/> (2026-09-22,
    /// the code fold): each block laid out alone, a spacer row between two, as <see cref="Compose"/>
    /// stacks them — <see cref="Rows"/> gives every child the same width. A block inside a list item
    /// or a quote is not one. Cached for the last width.
    /// </summary>
    public IReadOnlyList<CodeSpan> CodeSpans(RenderOptions options, int maxWidth)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (_spans is { } cached && _spansWidth == maxWidth)
        {
            return cached;
        }

        var spans = new List<CodeSpan>();
        int row = 0;
        for (int i = 0; i < _document.Blocks.Count; i++)
        {
            var block = _document.Blocks[i];
            if (i > 0)
            {
                row++;   // the spacer
            }

            int rows = Segment.SplitLines(Block(block, quote: null, resolve: null).Render(options, maxWidth)).Count;   // links never change a row
            if (block is CodeBlock code)
            {
                string label = code.Language ?? CodeLabel;
                int labelRows = Segment.SplitLines(((IRenderable)new Text(CodeHeading(code.Language), Theme.MarkdownCodeLabel)).Render(options, maxWidth)).Count;
                spans.Add(new CodeSpan(row, labelRows, rows - labelRows, label, code.Lines.Count, code.Open, string.Join("\n", code.Lines)));
            }

            row += rows;
        }

        _spansWidth = maxWidth;
        return _spans = spans;
    }

    /// <summary>
    /// The last top-level block's rows at <paramref name="maxWidth"/> when it is a code block (2026-09-27,
    /// the streaming code window): its label's and its body's, the block alone laid out — what
    /// <see cref="CodeSpans"/> finds for it, without laying out every block above. Null otherwise.
    /// </summary>
    public (int LabelRows, int BodyRows)? LastCodeRows(RenderOptions options, int maxWidth)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (_document.Blocks is not [.., CodeBlock code])
        {
            return null;
        }

        int rows = Segment.SplitLines(Code(code).Render(options, maxWidth)).Count;
        int labelRows = Segment.SplitLines(((IRenderable)new Text(CodeHeading(code.Language), Theme.MarkdownCodeLabel)).Render(options, maxWidth)).Count;
        return (labelRows, rows - labelRows);
    }

    /// <summary>The blocks stacked, a blank row between them unless <paramref name="tight"/> (a list item's).</summary>
    private static IRenderable Compose(IReadOnlyList<MarkdownBlock> blocks, Style? quote, bool tight, Func<string, string?>? resolve)
    {
        var children = new List<IRenderable>(blocks.Count * 2);
        foreach (var block in blocks)
        {
            if (children.Count > 0 && !tight)
            {
                children.Add(Spacer);
            }

            children.Add(Block(block, quote, resolve));
        }

        return new Rows(children);
    }

    private static IRenderable Block(MarkdownBlock block, Style? quote, Func<string, string?>? resolve) => block switch
    {
        ParagraphBlock paragraph => Paragraph(paragraph.Inlines, quote, resolve),
        HeadingBlock heading => Paragraph(heading.Inlines, heading.Level switch { 1 => Theme.MarkdownHeading1, 2 => Theme.MarkdownHeading, _ => Theme.MarkdownHeading3 }, resolve, flat: true),
        CodeBlock code => Code(code),
        ListBlock list => List(list, quote, resolve),
        QuoteBlock inner => new HangingIndent(QuoteGlyph, QuoteGlyph, Theme.MarkdownQuoteBar, Compose(inner.Blocks, Theme.MarkdownQuote, tight: false, resolve)),
        RuleBlock => new Rule().RuleStyle(Theme.MarkdownRule),
        TableBlock table => Table(table, quote, resolve),
        _ => Spacer,
    };

    private static IRenderable List(ListBlock list, Style? quote, Func<string, string?>? resolve)
    {
        var items = new List<IRenderable>(list.Items.Count);
        for (int i = 0; i < list.Items.Count; i++)
        {
            string marker = list.Ordered
                ? (list.Start + i).ToString(System.Globalization.CultureInfo.InvariantCulture) + ". "
                : BulletGlyph;
            var content = Compose(list.Items[i].Blocks, quote, tight: true, resolve);
            items.Add(new HangingIndent(marker, new string(' ', TextCells.Width(marker)), Theme.MarkdownBullet, content));
        }

        return new Rows(items);
    }

    private static IRenderable Code(CodeBlock code)
    {
        var language = CodeLanguages.Find(code.Language);
        var lines = language is null || code.Lines.Count == 0 ? PlainCode(code.Lines) : HighlightedCode(code.Lines, language);
        // One even slab, padded to the block's widest row (2026-10-04, the UI review).
        var block = new HangingIndent(CodeIndent, CodeIndent, Theme.MarkdownCodeBlock, new PaddedSlab(new Rows(lines), Theme.MarkdownCodeBlock));
        return new Rows(new Text(CodeHeading(code.Language), Theme.MarkdownCodeLabel), block);
    }

    /// <summary>A tab is four cells.</summary>
    private static string Untab(string line) => line.Replace("\t", "    ", StringComparison.Ordinal);

    private static List<IRenderable> PlainCode(IReadOnlyList<string> source)
    {
        var lines = new List<IRenderable>(source.Count);
        foreach (string line in source)
        {
            // A blank line must keep its row (Rows skips a child that renders nothing).
            lines.Add(new Text(line.Length == 0 ? " " : Untab(line), Theme.MarkdownCodeBlock));
        }

        return lines;
    }

    /// <summary>
    /// The block lexed as one text (a block comment colours every line it spans) and cut back into
    /// one <see cref="Paragraph"/> per line, a token crossing a line break split at it.
    /// </summary>
    private static List<IRenderable> HighlightedCode(IReadOnlyList<string> source, CodeLanguage language)
    {
        string text = string.Join('\n', source.Select(Untab));
        var lines = new List<IRenderable>(source.Count);
        var line = new Paragraph();
        bool blank = true;
        foreach (var token in CodeLexer.Lex(text, language))
        {
            var style = Theme.CodeStyle(token.Kind);
            int start = token.Start;
            while (start < token.End)
            {
                int newline = text.IndexOf('\n', start, token.End - start);
                int stop = newline < 0 ? token.End : newline;
                if (stop > start)
                {
                    line.Append(text[start..stop], style);
                    blank = false;
                }

                if (newline < 0)
                {
                    break;
                }

                lines.Add(blank ? line.Append(" ", Theme.MarkdownCodeBlock) : line);
                line = new Paragraph();
                blank = true;
                start = newline + 1;
            }
        }

        lines.Add(blank ? line.Append(" ", Theme.MarkdownCodeBlock) : line);
        return lines;
    }

    private static IRenderable Table(TableBlock table, Style? quote, Func<string, string?>? resolve)
    {
        var spectre = Theme.MarkdownTable();
        int columns = table.Header.Cells.Count;
        for (int c = 0; c < columns; c++)
        {
            var column = new TableColumn(Paragraph(table.Header.Cells[c].Inlines, Theme.TableHeader, resolve, flat: true));
            if (c < table.Alignments.Count)
            {
                column.Alignment = table.Alignments[c] switch
                {
                    TableAlignment.Left => Justify.Left,
                    TableAlignment.Center => Justify.Center,
                    TableAlignment.Right => Justify.Right,
                    _ => null,
                };
            }

            spectre.AddColumn(column);
        }

        foreach (var row in table.Rows)
        {
            var cells = new IRenderable[columns];
            for (int c = 0; c < columns; c++)
            {
                cells[c] = c < row.Cells.Count ? Paragraph(row.Cells[c].Inlines, quote, resolve) : new Text("");
            }

            spectre.AddRow(cells);
        }

        return spectre;
    }

    /// <summary>
    /// The inlines as one wrapped paragraph. <paramref name="baseStyle"/> is the quote's or the
    /// heading's colour; <paramref name="flat"/> keeps it for every run (a heading is one voice).
    /// Links (2026-11-01, clickable links and paths): a link's text and its <c>(url)</c>, a bare web address in the text and an inline
    /// code span that is one carry the address as their segments' <see cref="Segment.Link"/>; an inline code span <paramref name="resolve"/>
    /// finds as a path carries <see cref="LinkText.PathScheme"/> and a quiet underline. Code blocks never link.
    /// </summary>
    private static Paragraph Paragraph(IReadOnlyList<MarkdownInline> inlines, Style? baseStyle, Func<string, string?>? resolve, bool flat = false)
    {
        var paragraph = new Paragraph();
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case TextRun run:
                    AppendRun(run, flat ? baseStyle ?? Theme.Assistant : RunStyle(run, baseStyle));
                    break;
                case LinkRun link:
                    AppendLink(link.Text, link.Url);
                    break;
                case ImageRun image:
                    // A picture shows as the link it was (2026-10-29): its alt text and its address, or its link's.
                    AppendLink(image.Alt, image.Shown);
                    break;
                case HardBreak:
                    paragraph.Append("\n", baseStyle);
                    break;
            }
        }

        return paragraph;

        // One shape for a link and a picture (2026-10-29, review P6-9: one case re-tested and cast the picture twice). Both parts carry the
        // link (2026-11-01) when it is a web one; any other scheme gets none.
        void AppendLink(string text, string url)
        {
            string? target = LinkText.WebTarget(url);
            paragraph.Append(text.Length > 0 ? text : url, baseStyle ?? Theme.Assistant, LinkOf(target));
            if (url.Length > 0 && !string.Equals(url, text, StringComparison.Ordinal))
            {
                paragraph.Append(" (" + url + ")", Theme.MarkdownLinkUrl, LinkOf(target));
            }
        }

        void AppendRun(TextRun run, Style style)
        {
            if (run.Code)
            {
                // Inline code that is exactly a web address links like text; one the resolver finds in the working folder links as a path.
                string trimmed = run.Text.Trim();
                if (LinkText.IsWeb(trimmed) && LinkText.BareUrls(trimmed) is [var only] && only.Start == 0 && only.Length == trimmed.Length)
                {
                    paragraph.Append(run.Text, style, LinkOf(trimmed));
                }
                else if (resolve is not null && LinkText.PathCandidate(run.Text) is { Length: > 0 } candidate && resolve(candidate) is { } full
                    && LinkText.IsLinkable(full, web: false))
                {
                    paragraph.Append(run.Text, style.Combine(new Style(decoration: Decoration.Underline)), LinkOf(LinkText.PathLink(full)));
                }
                else
                {
                    paragraph.Append(run.Text, style);
                }

                return;
            }

            int at = 0;
            foreach (var (start, length) in LinkText.BareUrls(run.Text))
            {
                if (start > at)
                {
                    paragraph.Append(run.Text[at..start], style);
                }

                paragraph.Append(run.Text.Substring(start, length), style, LinkOf(run.Text.Substring(start, length)));
                at = start + length;
            }

            if (at < run.Text.Length || at == 0)
            {
                paragraph.Append(run.Text[at..], style);
            }
        }
    }

    /// <summary>
    /// <paramref name="target"/> as a segment's link (2026-11-01), null for none. Spectre carries a link on the <see cref="Segment"/>, beside its
    /// style (<see cref="Segment.Link"/>), so a fold, a resize reflow and a scroll keep it as they keep the style.
    /// </summary>
    internal static Link? LinkOf(string? target) => target is null ? null : new Link(target);

    /// <summary>A run's style: code wins, then the emphasis over the base colour (the quote's) or the body's.</summary>
    public static Style RunStyle(TextRun run, Style? baseStyle = null)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (run.Code)
        {
            return Theme.MarkdownCode;
        }

        var decoration = (run.Bold ? Decoration.Bold : Decoration.None) | (run.Italic ? Decoration.Italic : Decoration.None);
        if (baseStyle is not null)
        {
            return decoration == Decoration.None ? baseStyle.Value : baseStyle.Value.Combine(new Style(decoration: decoration));
        }

        return (run.Bold, run.Italic) switch
        {
            (true, true) => Theme.MarkdownBold.Combine(Theme.MarkdownItalic),
            (true, false) => Theme.MarkdownBold,
            (false, true) => Theme.MarkdownItalic,
            _ => Theme.Assistant,
        };
    }
}
