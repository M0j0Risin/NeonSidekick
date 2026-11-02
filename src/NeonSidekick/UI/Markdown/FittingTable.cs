using Spectre.Console;
using Spectre.Console.Rendering;

namespace NeonSidekick.UI.Markdown;

/// <summary>
/// A reply's table that fits its window (2026-11-01, feature idea #5, plan playful-popping-tower): the grid as before while it fits or its
/// columns can wrap readably; a table too wide for that shows each row as a card of <c>Header  value</c> lines instead. The headers in
/// <see cref="Theme.TableHeader"/> padded to the widest (at most a third of the width, a longer one wrapped in its column) plus two cells,
/// so the values line up, a long value wrapping under its own column. Every other card sits on the code block's slab
/// (<see cref="Theme.MarkdownCodeBlock"/>'s background) to the full width, the only divider (with no color, a <c>─</c> line between cards;
/// a segment's own background, inline code's, is kept). An empty cell's line is left out, and a card with none is no card. Decided
/// at the width it is drawn at, by <see cref="Stacks"/>; a reply already committed keeps the layout it was drawn with.
/// </summary>
internal sealed class FittingTable : IRenderable
{
    /// <summary>The narrowest the average grid column may be before the table stacks into cards. Pinned.</summary>
    public const int MinColumnCells = 10;

    /// <summary>The cells between a card's header column and its values.</summary>
    public const int HeaderGap = 2;

    private readonly Table _grid;
    private readonly IReadOnlyList<IRenderable> _headers;
    private readonly IReadOnlyList<IReadOnlyList<IRenderable?>> _rows;

    /// <summary>
    /// The last <see cref="StacksAt"/> answer (2026-11-02, the review: the grid was measured again for every measure and render): <c>(width + 1) &lt;&lt; 1</c>
    /// and the answer's bit, 0 for none; one <c>long</c>, so a read on another thread never sees half of it. The table never changes after
    /// the constructor and a measurement holds no color, so the width alone keys it.
    /// </summary>
    private long _stacksAt;

    /// <param name="grid">The table as a grid, drawn whenever it fits.</param>
    /// <param name="headers">Each column's header, as the grid's.</param>
    /// <param name="rows">Each body row's cells, as the grid's, null for an empty or missing one.</param>
    public FittingTable(Table grid, IReadOnlyList<IRenderable> headers, IReadOnlyList<IReadOnlyList<IRenderable?>> rows)
    {
        _grid = grid ?? throw new ArgumentNullException(nameof(grid));
        _headers = headers ?? throw new ArgumentNullException(nameof(headers));
        _rows = rows ?? throw new ArgumentNullException(nameof(rows));
    }

    /// <summary>
    /// Whether a table of <paramref name="columns"/> columns and <paramref name="bodyRows"/> rows, measured <paramref name="measured"/>, shows
    /// as cards at <paramref name="maxWidth"/>: never with one column or no body (nothing to put on a card) or when it fits; always when even a
    /// word a line won't fit; else when the average column, after the borders and padding (<c>3 × columns + 1</c>), falls under
    /// <see cref="MinColumnCells"/>. Never below <see cref="HeaderGap"/> + 2 cells either (2026-11-02, the review), where a card has no room
    /// for a header cell and a value cell.
    /// </summary>
    public static bool Stacks(Measurement measured, int columns, int bodyRows, int maxWidth)
    {
        if (columns < 2 || bodyRows == 0 || measured.Max <= maxWidth || maxWidth < HeaderGap + 2)
        {
            return false;
        }

        return measured.Min > maxWidth || (maxWidth - (3 * columns + 1)) / columns < MinColumnCells;
    }

    private bool StacksAt(RenderOptions options, int maxWidth)
    {
        long cached = Volatile.Read(ref _stacksAt);
        if (cached != 0 && cached >> 1 == maxWidth + 1L)
        {
            return (cached & 1) == 1;
        }

        bool stacks = Stacks(((IRenderable)_grid).Measure(options, maxWidth), _headers.Count, _rows.Count, maxWidth);
        Volatile.Write(ref _stacksAt, ((maxWidth + 1L) << 1) | (stacks ? 1L : 0L));
        return stacks;
    }

    public Measurement Measure(RenderOptions options, int maxWidth) =>
        StacksAt(options, maxWidth) ? new Measurement(Math.Min(maxWidth, 1), maxWidth) : ((IRenderable)_grid).Measure(options, maxWidth);

    public IEnumerable<Segment> Render(RenderOptions options, int maxWidth) =>
        StacksAt(options, maxWidth) ? Cards(options, maxWidth) : ((IRenderable)_grid).Render(options, maxWidth);

    private List<Segment> Cards(RenderOptions options, int maxWidth)
    {
        int widest = _headers.Count == 0 ? 0 : _headers.Max(h => h.Measure(options, maxWidth).Max);
        int headerCells = Math.Max(1, Math.Min(widest, maxWidth / 3));
        int valueCells = Math.Max(1, maxWidth - headerCells - HeaderGap);
        var slab = Theme.MarkdownCodeBlock.Background;
        bool noColor = options.ColorSystem is ColorSystem.NoColors or ColorSystem.Legacy;

        // Each header laid out once, not once a row (2026-11-02, the review).
        var headers = new List<SegmentLine>[_headers.Count];
        for (int c = 0; c < _headers.Count; c++)
        {
            headers[c] = Segment.SplitLines(_headers[c].Render(options, headerCells), headerCells);
        }

        var segments = new List<Segment>();
        int shown = 0;   // the cards drawn: a row of empty cells is none, so the stripes still alternate (2026-11-02, the review)
        for (int r = 0; r < _rows.Count; r++)
        {
            bool striped = !noColor && shown % 2 == 1;
            bool drew = false;
            for (int c = 0; c < _headers.Count; c++)
            {
                if (c >= _rows[r].Count || _rows[r][c] is not { } value)
                {
                    continue;
                }

                if (!drew && shown > 0 && noColor)
                {
                    // With no color the slab can't show: a dim rule between cards instead.
                    segments.Add(new Segment(new string('─', maxWidth), Theme.DimText));
                    segments.Add(Segment.LineBreak);
                }

                drew = true;
                var header = headers[c];
                var lines = Segment.SplitLines(value.Render(options, valueCells), valueCells);
                for (int i = 0; i < Math.Max(header.Count, lines.Count); i++)
                {
                    var line = new List<Segment>();
                    int used = Add(line, i < header.Count ? header[i] : null);
                    line.Add(new Segment(new string(' ', headerCells - used + HeaderGap)));
                    used = headerCells + HeaderGap + Add(line, i < lines.Count ? lines[i] : null);
                    if (striped)
                    {
                        if (used < maxWidth)
                        {
                            line.Add(new Segment(new string(' ', maxWidth - used)));
                        }

                        for (int s = 0; s < line.Count; s++)
                        {
                            // The slab only under a segment with no background of its own: inline code keeps its own (2026-11-02, the review). A link kept.
                            if (line[s].Style.Background == Color.Default)
                            {
                                line[s] = new Segment(line[s].Text, line[s].Style.Combine(new Style(background: slab)), line[s].Link);
                            }
                        }
                    }

                    segments.AddRange(line);
                    segments.Add(Segment.LineBreak);
                }
            }

            shown += drew ? 1 : 0;
        }

        return segments;
    }

    /// <summary>A laid-out line's segments onto <paramref name="line"/>, its cells counted; nothing for none.</summary>
    private static int Add(List<Segment> line, SegmentLine? from)
    {
        if (from is null)
        {
            return 0;
        }

        line.AddRange(from);
        return Segment.CellCount(from);
    }
}
