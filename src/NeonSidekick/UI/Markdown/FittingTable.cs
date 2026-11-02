using Spectre.Console;
using Spectre.Console.Rendering;

namespace NeonSidekick.UI.Markdown;

/// <summary>
/// A reply's table that fits its window (2026-11-01, feature idea #5, plan playful-popping-tower): the grid as before while it fits or its
/// columns can wrap readably; a table too wide for that shows each row as a card of <c>Header  value</c> lines instead. The headers in
/// <see cref="Theme.TableHeader"/> padded to the widest (at most a third of the width, a longer one wrapped in its column) plus two cells,
/// so the values line up, a long value wrapping under its own column. Every other card sits on the code block's slab
/// (<see cref="Theme.MarkdownCodeBlock"/>'s background) to the full width, the only divider. An empty cell's line is left out. Decided
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
    /// <see cref="MinColumnCells"/>.
    /// </summary>
    public static bool Stacks(Measurement measured, int columns, int bodyRows, int maxWidth)
    {
        if (columns < 2 || bodyRows == 0 || measured.Max <= maxWidth)
        {
            return false;
        }

        return measured.Min > maxWidth || (maxWidth - (3 * columns + 1)) / columns < MinColumnCells;
    }

    private bool StacksAt(RenderOptions options, int maxWidth) =>
        Stacks(((IRenderable)_grid).Measure(options, maxWidth), _headers.Count, _rows.Count, maxWidth);

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
        var segments = new List<Segment>();
        for (int r = 0; r < _rows.Count; r++)
        {
            bool striped = r % 2 == 1;
            for (int c = 0; c < _headers.Count; c++)
            {
                if (c >= _rows[r].Count || _rows[r][c] is not { } value)
                {
                    continue;
                }

                var header = Segment.SplitLines(_headers[c].Render(options, headerCells), headerCells);
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
                            line[s] = new Segment(line[s].Text, line[s].Style.Combine(new Style(background: slab)), line[s].Link);   // a link kept
                        }
                    }

                    segments.AddRange(line);
                    segments.Add(Segment.LineBreak);
                }
            }
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
