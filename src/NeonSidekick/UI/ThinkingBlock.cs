using NeonSidekick.UI.Markdown;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace NeonSidekick.UI;

/// <summary>
/// The model's thinking in the pane's live slot (2026-09-26, the user's ask): the header
/// <see cref="ThinkingFoldText.LiveHeader"/>, then the text so far — dim italic on the code block's
/// panel fill, under a tool line's indent — the whole of it every time, as a <see cref="ReplyBlock"/>
/// is; the pane draws only its <see cref="Tail"/> while it streams. Plain text, not markdown: thinking
/// is a draft, and half a table or fence mid-stream would jump about. When the pane commits it, the
/// header is the summary of a thinking group (<see cref="Scrollback.BeginThinkingGroup"/>) and every
/// row its members, so it folds to <see cref="ThinkingFoldText.Summary"/> with <see cref="Elapsed"/>
/// the moment it ends.
/// </summary>
public sealed class ThinkingBlock : IRenderable
{
    /// <summary>The body's indent ahead of the panel fill: a tool line's, so the fill starts under the triangle.</summary>
    public const string Indent = "  ";

    /// <summary>
    /// The body rows the slot shows while the thinking streams (2026-09-26, the user's ask: a long
    /// thought filled the screen until the answer started): the header and the last five laid-out rows,
    /// a wrapped line counting each of its rows, scrolling up as the text comes. The whole block goes
    /// into the transcript when it folds, so unfolding it shows every line.
    /// </summary>
    public const int LiveTailRows = 5;

    private IRenderable? _content;

    // The text is a body already trimmed (LiveView's): its first line keeps its indent.
    private readonly bool _trimmed;

    public ThinkingBlock(string text, TimeSpan? elapsed = null)
    {
        Text = text ?? throw new ArgumentNullException(nameof(text));
        Elapsed = elapsed;
    }

    private ThinkingBlock(string body, bool trimmed)
    {
        Text = body;
        _trimmed = trimmed;
    }

    /// <summary>
    /// The block over the body's last <see cref="LiveTailRows"/> lines only, for the slot's draw
    /// (2026-09-26, the user's report: the pane flickered through long thinking — every tick laid out
    /// the whole thought, between the erase and the repaint, to show five rows of it). A line lays out
    /// as one row or more, so the <see cref="Tail"/> of this is the tail of the whole, at a cost that
    /// no longer grows with the thought.
    /// </summary>
    public ThinkingBlock LiveView()
    {
        string body = Text.Trim();
        if (body.Length == 0)
        {
            return new ThinkingBlock(body, trimmed: true);
        }

        int start = body.Length;
        for (int i = 0; i < LiveTailRows && start > 0; i++)
        {
            start = body.LastIndexOf('\n', start - 1);
            if (start < 0)
            {
                return new ThinkingBlock(body, trimmed: true);
            }
        }

        return new ThinkingBlock(body[(start + 1)..], trimmed: true);
    }

    public string Text { get; }

    /// <summary>How long the thinking streamed; null while it still does.</summary>
    public TimeSpan? Elapsed { get; }

    /// <summary>The thinking still streams (no <see cref="Elapsed"/> yet): the pane draws its <see cref="Tail"/>.</summary>
    public bool Streaming => Elapsed is null;

    /// <summary>
    /// The rows of a streaming block the slot draws, from its full layout <paramref name="lines"/>: the
    /// header row and the last <see cref="LiveTailRows"/> body rows — fewer on a region under
    /// <paramref name="region"/> rows, the header kept while there is room for it and one row more.
    /// </summary>
    public static List<SegmentLine> Tail(List<SegmentLine> lines, int region)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (region < 2)
        {
            int keep = Math.Clamp(region, 0, lines.Count);
            return lines.GetRange(lines.Count - keep, keep);
        }

        int body = Math.Min(Math.Min(LiveTailRows, region - 1), Math.Max(0, lines.Count - 1));
        if (lines.Count == 0)
        {
            return [];
        }

        var tail = new List<SegmentLine>(body + 1) { lines[0] };
        tail.AddRange(lines.GetRange(lines.Count - body, body));
        return tail;
    }

    public Measurement Measure(RenderOptions options, int maxWidth) => Content.Measure(options, maxWidth);

    public IEnumerable<Segment> Render(RenderOptions options, int maxWidth) => Content.Render(options, maxWidth);

    private IRenderable Content => _content ??= Build();

    private IRenderable Build()
    {
        var header = new Text(ThinkingFoldText.LiveHeader, Theme.DimText);
        string body = _trimmed ? Text : Text.Trim();
        if (body.Length == 0)
        {
            return header;
        }

        var lines = new List<IRenderable>();
        foreach (string line in body.Split('\n'))
        {
            string clean = line.TrimEnd('\r').Replace("\t", "    ", StringComparison.Ordinal);

            // A blank line must keep its row (Rows skips a child that renders nothing).
            lines.Add(new Text(clean.Length == 0 ? " " : clean, Theme.ThinkingText));
        }

        var panel = new HangingIndent(MarkdownView.CodeIndent, MarkdownView.CodeIndent, Theme.ThinkingText, new Rows(lines));
        return new Rows(header, new HangingIndent(Indent, Indent, Style.Plain, panel));
    }
}
