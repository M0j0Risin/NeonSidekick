using Spectre.Console;
using Spectre.Console.Rendering;

namespace NeonSidekick.UI.Markdown;

/// <summary>
/// The reply in the pane's live slot: the text so far as a <see cref="MarkdownView"/>, the
/// assistant glyph ahead of its first line and <see cref="ContinuationIndent"/> under it when the
/// block opens the reply (<paramref name="glyph"/>); flush-left when it continues one after a tool
/// line, the plain path's shape. Empty text with the glyph is the bare glyph line. The parse
/// happens once, on the first render — the pane lays the block out on its tick, not per token.
/// <paramref name="codeKeep"/> is the <c>Code collapse count</c> the reply opened with (2026-09-22):
/// a top-level code block of more lines folds once the pane commits it (<see cref="CodeSpans"/>),
/// and one still streaming shows only its label and last <paramref name="codeKeep"/> rows
/// (<see cref="OpenCode"/>, <see cref="LiveView"/>, <see cref="Tail"/>; 2026-09-27).
/// </summary>
public sealed class ReplyBlock : IRenderable
{
    /// <summary>Under the glyph: a bullet or a code label at column 0 would read as a second glyph column.</summary>
    public const string ContinuationIndent = "  ";

    private readonly IMarkdownParser _parser;
    private MarkdownDocument? _document;
    private MarkdownView? _view;
    private IRenderable? _content;

    public ReplyBlock(string text, bool glyph, IMarkdownParser? parser = null, int codeKeep = 0)
    {
        Text = text ?? throw new ArgumentNullException(nameof(text));
        Glyph = glyph;
        CodeKeep = Math.Max(0, codeKeep);
        _parser = parser ?? MarkdownView.DefaultParser;
    }

    // LiveView's block: the document already parsed, its open code block cut to its tail.
    private ReplyBlock(ReplyBlock whole, MarkdownDocument document)
    {
        Text = whole.Text;
        Glyph = whole.Glyph;
        CodeKeep = whole.CodeKeep;
        _parser = whole._parser;
        _document = document;
    }

    private MarkdownDocument Document => _document ??= _parser.Parse(Text);

    /// <summary>
    /// The code block the reply is still streaming (2026-09-27, the user's ask: a long code block
    /// scrolled the screen row by row as it came, and flickered — show it as the thinking is shown):
    /// the last top-level block when it is a fence not closed yet, with a <see cref="CodeKeep"/> to
    /// window it by; null otherwise. The pane draws its label and last <see cref="CodeKeep"/> rows
    /// only (<see cref="Tail"/>) and commits none of it until the fence closes.
    /// </summary>
    public CodeBlock? OpenCode => CodeKeep > 0 && Document.Blocks is [.., CodeBlock { Open: true } code] ? code : null;

    /// <summary>
    /// The block for the slot's draw while <see cref="OpenCode"/> streams: the same document with that
    /// block's lines cut to its last <see cref="CodeKeep"/>, so every tick lays out a window, not the
    /// whole block (<see cref="ThinkingBlock.LiveView"/>'s reason). The rows above the block are the
    /// same rows, so the pane's committed count holds for both. A lexer state begun above the cut (a
    /// block comment) is not seen by the window — it is a glimpse; the transcript gets the whole
    /// block. This block itself when there is nothing to cut.
    /// </summary>
    public ReplyBlock LiveView()
    {
        if (OpenCode is not { } code || code.Lines.Count <= CodeKeep)
        {
            return this;
        }

        var blocks = Document.Blocks.ToList();
        blocks[^1] = code with { Lines = code.Lines.Skip(code.Lines.Count - CodeKeep).ToList() };
        return new ReplyBlock(this, new MarkdownDocument(blocks));
    }

    /// <summary>
    /// Where <see cref="OpenCode"/> sits in this block laid out at <paramref name="maxWidth"/> as
    /// <paramref name="rows"/> rows: the last block, so its rows are the last rows — only it is laid
    /// out again, not the reply above it. Null without one.
    /// </summary>
    public CodeSpan? OpenCodeSpan(RenderOptions options, int maxWidth, int rows)
    {
        if (OpenCode is not { } code)
        {
            return null;
        }

        _ = Content;
        if (_view!.LastCodeRows(options, ViewWidth(maxWidth)) is not { } last)
        {
            return null;
        }

        int labelRow = Math.Max(0, rows - last.LabelRows - last.BodyRows);
        return new CodeSpan(labelRow, last.LabelRows, last.BodyRows, code.Language ?? MarkdownView.CodeLabel, code.Lines.Count, Open: true);
    }

    /// <summary>
    /// The top-level code blocks whose fence has closed and that are over <see cref="CodeKeep"/> lines —
    /// the ones the transcript folds (2026-09-27): the pane commits each whole the draw it closes, so it
    /// folds at once, not after streaming at full height. 0 without a <see cref="CodeKeep"/>.
    /// </summary>
    public int FoldingCode => CodeKeep == 0 ? 0 : Document.Blocks.Count(b => b is CodeBlock { Open: false } code && code.Lines.Count > CodeKeep);

    /// <summary>
    /// The rows of a reply streaming <paramref name="open"/> (its last code span) the slot draws, from
    /// its layout <paramref name="lines"/>: every row through the span's label, then the last
    /// <paramref name="keep"/> of its body rows (a wrapped line counts each of its rows).
    /// </summary>
    public static List<SegmentLine> Tail(List<SegmentLine> lines, CodeSpan open, int keep)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(open);
        int bodyRow = Math.Min(open.BodyRow, lines.Count);
        int body = Math.Min(Math.Max(0, keep), lines.Count - bodyRow);
        var tail = lines.GetRange(0, bodyRow);
        tail.AddRange(lines.GetRange(lines.Count - body, body));
        return tail;
    }

    public string Text { get; }

    public bool Glyph { get; }

    /// <summary>How many source lines a top-level code block may have before it folds in the transcript; 0 = never.</summary>
    public int CodeKeep { get; }

    public Measurement Measure(RenderOptions options, int maxWidth) => Content.Measure(options, maxWidth);

    public IEnumerable<Segment> Render(RenderOptions options, int maxWidth) => Content.Render(options, maxWidth);

    /// <summary>
    /// The top-level code blocks' rows in this block rendered at <paramref name="maxWidth"/>
    /// (<see cref="MarkdownView.CodeSpans"/>): the glyph's indent takes columns, never rows.
    /// </summary>
    public IReadOnlyList<CodeSpan> CodeSpans(RenderOptions options, int maxWidth)
    {
        _ = Content;
        return _view!.CodeSpans(options, ViewWidth(maxWidth));
    }

    private int ViewWidth(int maxWidth) => Glyph ? Math.Max(1, maxWidth - TextCells.Width(TranscriptRenderer.AssistantGlyph)) : maxWidth;

    private IRenderable Content => _content ??= Build();

    private IRenderable Build()
    {
        _view = new MarkdownView(Document);
        return Glyph ? new HangingIndent(TranscriptRenderer.AssistantGlyph, ContinuationIndent, Theme.Accent, _view) : _view;
    }
}
