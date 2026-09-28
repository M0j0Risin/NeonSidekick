using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace NeonSidekick.UI.Markdown;

/// <summary>
/// The Markdown reader: Markdig's CommonMark parser (pipe tables on, raw HTML off) folded into the
/// app's own <see cref="MarkdownDocument"/>. Only the parse is Markdig's; every sentence the
/// transcript shows is composed from this tree by <see cref="MarkdownView"/>. Setext headings stay
/// on: with them off Markdig's thematic-break parser still defers to the paragraph, so
/// <c>Title</c> over <c>---</c> became one paragraph rather than a rule (measured 2026-09-16).
/// </summary>
public sealed class MarkdigParser : IMarkdownParser
{
    private static readonly MarkdownPipeline Pipeline = Build();

    public MarkdownDocument Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            return MarkdownDocument.Empty;
        }

        var document = Markdig.Markdown.Parse(text, Pipeline);
        return new MarkdownDocument(Blocks(document));
    }

    private static MarkdownPipeline Build() =>
        new MarkdownPipelineBuilder().UsePipeTables().DisableHtml().Build();

    private static List<MarkdownBlock> Blocks(ContainerBlock container)
    {
        var blocks = new List<MarkdownBlock>();
        foreach (var child in container)
        {
            switch (child)
            {
                case Markdig.Syntax.HeadingBlock heading:
                    blocks.Add(new HeadingBlock(heading.Level, Inlines(heading.Inline)));
                    break;
                case Markdig.Syntax.ParagraphBlock paragraph:
                    blocks.Add(new ParagraphBlock(Inlines(paragraph.Inline)));
                    break;
                case FencedCodeBlock fenced:
                    blocks.Add(new CodeBlock(Language(fenced.Info), Lines(fenced), fenced.ClosingFencedCharCount == 0));
                    break;
                case Markdig.Syntax.CodeBlock indented:
                    blocks.Add(new CodeBlock(null, Lines(indented)));
                    break;
                case Markdig.Syntax.ListBlock list:
                    blocks.Add(List(list));
                    break;
                case Markdig.Syntax.QuoteBlock quote:
                    blocks.Add(new QuoteBlock(Blocks(quote)));
                    break;
                case ThematicBreakBlock:
                    blocks.Add(new RuleBlock());
                    break;
                case Table table:
                    if (Table(table) is { } converted)
                    {
                        blocks.Add(converted);
                    }

                    break;
                case ContainerBlock other:
                    // A container this tree has no shape for (a definition group): its blocks stand on their own.
                    blocks.AddRange(Blocks(other));
                    break;
                case LeafBlock leaf when leaf.Inline is not null:
                    blocks.Add(new ParagraphBlock(Inlines(leaf.Inline)));
                    break;
            }
        }

        return blocks;
    }

    private static string? Language(string? info)
    {
        if (string.IsNullOrWhiteSpace(info))
        {
            return null;
        }

        // The info string's first word names the language; the rest is the author's.
        string word = info.Trim();
        int space = word.IndexOf(' ');
        return space > 0 ? word[..space] : word;
    }

    private static List<string> Lines(LeafBlock block)
    {
        var lines = new List<string>(block.Lines.Count);
        for (int i = 0; i < block.Lines.Count; i++)
        {
            lines.Add(block.Lines.Lines[i].Slice.ToString());
        }

        return lines;
    }

    private static ListBlock List(Markdig.Syntax.ListBlock list)
    {
        int start = 1;
        if (list.IsOrdered && int.TryParse(list.OrderedStart, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int parsed))
        {
            start = parsed;
        }

        var items = new List<ListItem>();
        foreach (var child in list)
        {
            if (child is ListItemBlock item)
            {
                items.Add(new ListItem(Blocks(item)));
            }
        }

        return new ListBlock(list.IsOrdered, start, items);
    }

    private static TableBlock? Table(Table table)
    {
        TableRow? header = null;
        var rows = new List<TableRow>();
        foreach (var child in table)
        {
            if (child is not Markdig.Extensions.Tables.TableRow row)
            {
                continue;
            }

            var cells = new List<TableCell>();
            foreach (var cellBlock in row)
            {
                if (cellBlock is Markdig.Extensions.Tables.TableCell cell)
                {
                    cells.Add(new TableCell(CellInlines(cell)));
                }
            }

            var converted = new TableRow(cells);
            if (row.IsHeader && header is null)
            {
                header = converted;
            }
            else
            {
                rows.Add(converted);
            }
        }

        if (header is null)
        {
            return null;
        }

        var alignments = new List<TableAlignment>();
        foreach (var column in table.ColumnDefinitions)
        {
            alignments.Add(column.Alignment switch
            {
                TableColumnAlign.Left => TableAlignment.Left,
                TableColumnAlign.Center => TableAlignment.Center,
                TableColumnAlign.Right => TableAlignment.Right,
                _ => TableAlignment.Default,
            });
        }

        return new TableBlock(header, rows, alignments);
    }

    private static List<MarkdownInline> CellInlines(ContainerBlock cell)
    {
        var inlines = new List<MarkdownInline>();
        foreach (var child in cell)
        {
            if (child is LeafBlock leaf && leaf.Inline is not null)
            {
                if (inlines.Count > 0)
                {
                    Add(inlines, new TextRun(" "));
                }

                foreach (var inline in Inlines(leaf.Inline))
                {
                    Add(inlines, inline);
                }
            }
        }

        return inlines;
    }

    private static List<MarkdownInline> Inlines(ContainerInline? container)
    {
        var inlines = new List<MarkdownInline>();
        if (container is not null)
        {
            Walk(container, inlines, bold: false, italic: false);
        }

        return inlines;
    }

    private static void Walk(ContainerInline container, List<MarkdownInline> into, bool bold, bool italic)
    {
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    Add(into, new TextRun(literal.Content.ToString(), bold, italic));
                    break;
                case CodeInline code:
                    Add(into, new TextRun(code.Content, bold, italic, Code: true));
                    break;
                case EmphasisInline emphasis:
                    Walk(emphasis, into, bold || emphasis.DelimiterCount >= 2, italic || emphasis.DelimiterCount == 1);
                    break;
                case LinkInline link:
                    Add(into, new LinkRun(PlainText(link), link.Url ?? ""));
                    break;
                case AutolinkInline autolink:
                    Add(into, new LinkRun(autolink.Url, autolink.Url));
                    break;
                case LineBreakInline lineBreak:
                    if (lineBreak.IsHard)
                    {
                        into.Add(new HardBreak());
                    }
                    else
                    {
                        Add(into, new TextRun(" ", bold, italic));
                    }

                    break;
                case HtmlEntityInline entity:
                    Add(into, new TextRun(entity.Transcoded.ToString(), bold, italic));
                    break;
                case DelimiterInline delimiter:
                    // An opener that never closed: shown as typed.
                    Add(into, new TextRun(delimiter.ToLiteral(), bold, italic));
                    Walk(delimiter, into, bold, italic);
                    break;
                case ContainerInline nested:
                    Walk(nested, into, bold, italic);
                    break;
                case LeafInline leaf:
                    Add(into, new TextRun(leaf.ToString() ?? "", bold, italic));
                    break;
            }
        }
    }

    /// <summary>The text of a link as typed, its own emphasis dropped: a link is one unit on the screen.</summary>
    private static string PlainText(ContainerInline container)
    {
        var text = new System.Text.StringBuilder();
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    text.Append(literal.Content.AsSpan());
                    break;
                case CodeInline code:
                    text.Append(code.Content);
                    break;
                case HtmlEntityInline entity:
                    text.Append(entity.Transcoded.AsSpan());
                    break;
                case LineBreakInline:
                    text.Append(' ');
                    break;
                case ContainerInline nested:
                    text.Append(PlainText(nested));
                    break;
            }
        }

        return text.ToString();
    }

    /// <summary>Adds a run, folding it into the previous one when they carry the same emphasis.</summary>
    private static void Add(List<MarkdownInline> into, MarkdownInline inline)
    {
        if (inline is TextRun { Text.Length: 0 })
        {
            return;
        }

        if (inline is TextRun run && into.Count > 0 && into[^1] is TextRun last
            && last.Bold == run.Bold && last.Italic == run.Italic && last.Code == run.Code)
        {
            into[^1] = last with { Text = last.Text + run.Text };
            return;
        }

        into.Add(inline);
    }
}
