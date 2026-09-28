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
    /// <summary>The parser the transcript reads replies with.</summary>
    public static readonly IMarkdownParser DefaultParser = new MarkdigParser();

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
    private IRenderable? _content;

    public MarkdownView(MarkdownDocument document)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
    }

    /// <summary>The view of <paramref name="text"/> read by <see cref="DefaultParser"/>.</summary>
    public static MarkdownView Of(string text, IMarkdownParser? parser = null) =>
        new((parser ?? DefaultParser).Parse(text));

    public Measurement Measure(RenderOptions options, int maxWidth) => Content.Measure(options, maxWidth);

    public IEnumerable<Segment> Render(RenderOptions options, int maxWidth) => Content.Render(options, maxWidth);

    private IRenderable Content => _content ??= Compose(_document.Blocks, quote: null, tight: false);

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

            int rows = Segment.SplitLines(Block(block, quote: null).Render(options, maxWidth)).Count;
            if (block is CodeBlock code)
            {
                string label = code.Language ?? CodeLabel;
                int labelRows = Segment.SplitLines(((IRenderable)new Text(CodeHeading(code.Language), Theme.MarkdownCodeLabel)).Render(options, maxWidth)).Count;
                spans.Add(new CodeSpan(row, labelRows, rows - labelRows, label, code.Lines.Count, code.Open));
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
    private static IRenderable Compose(IReadOnlyList<MarkdownBlock> blocks, Style? quote, bool tight)
    {
        var children = new List<IRenderable>(blocks.Count * 2);
        foreach (var block in blocks)
        {
            if (children.Count > 0 && !tight)
            {
                children.Add(Spacer);
            }

            children.Add(Block(block, quote));
        }

        return new Rows(children);
    }

    private static IRenderable Block(MarkdownBlock block, Style? quote) => block switch
    {
        ParagraphBlock paragraph => Paragraph(paragraph.Inlines, quote),
        HeadingBlock heading => Paragraph(heading.Inlines, heading.Level == 1 ? Theme.MarkdownHeading1 : Theme.MarkdownHeading, flat: true),
        CodeBlock code => Code(code),
        ListBlock list => List(list, quote),
        QuoteBlock inner => new HangingIndent(QuoteGlyph, QuoteGlyph, Theme.MarkdownQuoteBar, Compose(inner.Blocks, Theme.MarkdownQuote, tight: false)),
        RuleBlock => new Rule().RuleStyle(Theme.MarkdownRule),
        TableBlock table => Table(table, quote),
        _ => Spacer,
    };

    private static IRenderable List(ListBlock list, Style? quote)
    {
        var items = new List<IRenderable>(list.Items.Count);
        for (int i = 0; i < list.Items.Count; i++)
        {
            string marker = list.Ordered
                ? (list.Start + i).ToString(System.Globalization.CultureInfo.InvariantCulture) + ". "
                : BulletGlyph;
            var content = Compose(list.Items[i].Blocks, quote, tight: true);
            items.Add(new HangingIndent(marker, new string(' ', TextCells.Width(marker)), Theme.MarkdownBullet, content));
        }

        return new Rows(items);
    }

    private static IRenderable Code(CodeBlock code)
    {
        var language = CodeLanguages.Find(code.Language);
        var lines = language is null || code.Lines.Count == 0 ? PlainCode(code.Lines) : HighlightedCode(code.Lines, language);
        var block = new HangingIndent(CodeIndent, CodeIndent, Theme.MarkdownCodeBlock, new Rows(lines));
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

    private static IRenderable Table(TableBlock table, Style? quote)
    {
        var spectre = Theme.MarkdownTable();
        int columns = table.Header.Cells.Count;
        for (int c = 0; c < columns; c++)
        {
            var column = new TableColumn(Paragraph(table.Header.Cells[c].Inlines, Theme.TableHeader, flat: true));
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
                cells[c] = c < row.Cells.Count ? Paragraph(row.Cells[c].Inlines, quote) : new Text("");
            }

            spectre.AddRow(cells);
        }

        return spectre;
    }

    /// <summary>
    /// The inlines as one wrapped paragraph. <paramref name="baseStyle"/> is the quote's or the
    /// heading's colour; <paramref name="flat"/> keeps it for every run (a heading is one voice).
    /// </summary>
    private static Paragraph Paragraph(IReadOnlyList<MarkdownInline> inlines, Style? baseStyle, bool flat = false)
    {
        var paragraph = new Paragraph();
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case TextRun run:
                    paragraph.Append(run.Text, flat ? baseStyle : RunStyle(run, baseStyle));
                    break;
                case LinkRun link:
                    paragraph.Append(link.Text.Length > 0 ? link.Text : link.Url, baseStyle ?? Theme.Assistant);
                    if (link.Url.Length > 0 && !string.Equals(link.Url, link.Text, StringComparison.Ordinal))
                    {
                        paragraph.Append(" (" + link.Url + ")", Theme.MarkdownLinkUrl);
                    }

                    break;
                case HardBreak:
                    paragraph.Append("\n", baseStyle);
                    break;
            }
        }

        return paragraph;
    }

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
