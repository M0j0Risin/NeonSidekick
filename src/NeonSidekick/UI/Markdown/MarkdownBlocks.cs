namespace NeonSidekick.UI.Markdown;

/// <summary>
/// The reply's Markdown as the transcript sees it: a small tree of the constructs a chat model
/// actually writes, independent of the parser that produced it (<see cref="IMarkdownParser"/>).
/// <see cref="MarkdownView"/> turns it into Spectre renderables; nothing here knows about the console.
/// </summary>
public sealed record MarkdownDocument(IReadOnlyList<MarkdownBlock> Blocks)
{
    public static readonly MarkdownDocument Empty = new([]);
}

public abstract record MarkdownBlock;

/// <summary>A run of prose.</summary>
public sealed record ParagraphBlock(IReadOnlyList<MarkdownInline> Inlines) : MarkdownBlock;

/// <summary>An ATX heading, <paramref name="Level"/> 1–6.</summary>
public sealed record HeadingBlock(int Level, IReadOnlyList<MarkdownInline> Inlines) : MarkdownBlock;

/// <summary>
/// A fenced or indented code block; <paramref name="Language"/> is the fence's info string, null when none.
/// <paramref name="Open"/>: a fenced block whose closing fence has not come yet — a reply still streaming it
/// (2026-09-27, the streaming code window: <see cref="ReplyBlock.LiveView"/>). An indented block never is.
/// </summary>
public sealed record CodeBlock(string? Language, IReadOnlyList<string> Lines, bool Open = false) : MarkdownBlock;

/// <summary>A bulleted (<paramref name="Ordered"/> false) or numbered list; <paramref name="Start"/> is the first number.</summary>
public sealed record ListBlock(bool Ordered, int Start, IReadOnlyList<ListItem> Items) : MarkdownBlock;

/// <summary>One item of a list: its blocks, a nested list among them.</summary>
public sealed record ListItem(IReadOnlyList<MarkdownBlock> Blocks);

/// <summary>A blockquote.</summary>
public sealed record QuoteBlock(IReadOnlyList<MarkdownBlock> Blocks) : MarkdownBlock;

/// <summary>A thematic break (<c>---</c>).</summary>
public sealed record RuleBlock : MarkdownBlock;

/// <summary>A pipe table: the header row, the body rows, one alignment per column (null = the default).</summary>
public sealed record TableBlock(TableRow Header, IReadOnlyList<TableRow> Rows, IReadOnlyList<TableAlignment> Alignments) : MarkdownBlock;

public sealed record TableRow(IReadOnlyList<TableCell> Cells);

public sealed record TableCell(IReadOnlyList<MarkdownInline> Inlines);

public enum TableAlignment
{
    Default,
    Left,
    Center,
    Right,
}

public abstract record MarkdownInline;

/// <summary>A stretch of text with its emphasis; <paramref name="Code"/> is an inline code span.</summary>
public sealed record TextRun(string Text, bool Bold = false, bool Italic = false, bool Code = false) : MarkdownInline;

/// <summary>A link: its text and its destination.</summary>
public sealed record LinkRun(string Text, string Url) : MarkdownInline;

/// <summary>A hard line break inside a paragraph.</summary>
public sealed record HardBreak : MarkdownInline;
