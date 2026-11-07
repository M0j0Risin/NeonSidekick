using Spectre.Console;
using Spectre.Console.Rendering;

namespace NeonSidekick.UI;

/// <summary>
/// One markup row cut to the width it is rendered at — an ellipsis when cut, never wrapped: the
/// <see cref="MenuPane"/>'s rows since 2026-09-18 (a skill's description, a memory's text, a queued
/// line, a root's path), so a list stays one row per line and the viewport's count holds.
/// <see cref="FittedLine"/> does this for plain text; this is its markup twin, since Spectre's own
/// <c>Overflow.Ellipsis</c> cuts only a single word wider than the line and still wraps a sentence.
/// The markup is laid out at an unbounded width (one line), then cut to one cell short of the room
/// — whole segments while they fit, the first that does not cut inside — and the ellipsis appended
/// in the cut segment's style. An empty
/// row renders one space, so a <see cref="Rows"/> child keeps its row.
/// <para>Since 2026-11-07 (menu clicks) it measures by the wider of <see cref="TextCells"/> and Spectre's count: the terminal draws by the
/// first, which counts ⚙️ 🛠️ 🖥️ as two cells where Spectre counts one, and the overlay's draw splits rows by the second, so either alone can
/// still add a row, and a row more puts clicks on the row above the aim. It cuts between clusters, so a glyph keeps its selector.</para>
/// </summary>
internal sealed class FittedMarkup : IRenderable
{
    private const string Ellipsis = "…";

    private readonly Markup _markup;

    public FittedMarkup(string markup)
    {
        ArgumentNullException.ThrowIfNull(markup);
        _markup = new Markup(markup.Length == 0 ? " " : markup);
    }

    public Measurement Measure(RenderOptions options, int maxWidth) => new(Math.Min(1, maxWidth), Math.Min(maxWidth, _markup.Length));

    public IEnumerable<Segment> Render(RenderOptions options, int maxWidth)
    {
        var segments = ((IRenderable)_markup).Render(options, int.MaxValue).Where(s => !s.IsLineBreak).ToList();
        return Fit(segments, maxWidth);
    }

    /// <summary>The cells a row takes: the wider of what the terminal draws (<see cref="DrawnCells"/>) and what Spectre counts. Control codes take none. Pure.</summary>
    public static int RowCells(IReadOnlyList<Segment> segments) => Math.Max(DrawnCells(segments), Segment.CellCount(segments));

    /// <summary>
    /// The cells the terminal draws a row in (<see cref="TextCells"/>), control codes none (2026-11-07, the code review): where the cursor
    /// stops, so what decides whether a row leaves old cells to erase. Pure.
    /// </summary>
    public static int DrawnCells(IReadOnlyList<Segment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        int cells = 0;
        foreach (var segment in segments)
        {
            cells += segment.IsControlCode ? 0 : TextCells.Width(segment.Text);
        }

        return cells;
    }

    /// <summary><paramref name="segments"/> as they are when they fit <paramref name="maxWidth"/> (<see cref="RowCells"/>), else cut with the ellipsis. Pure.</summary>
    public static List<Segment> Fit(List<Segment> segments, int maxWidth)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (maxWidth <= 0 || RowCells(segments) <= maxWidth)
        {
            return segments;
        }

        // Whole segments while they fit, the first that does not cut inside (Segment.Truncate over a
        // list drops whole segments, which would lose a long path after its short label).
        int room = Math.Max(0, maxWidth - 1);
        int drawn = 0;
        int counted = 0;
        var cut = new List<Segment>(segments.Count + 1);
        var style = segments[^1].Style;
        foreach (var segment in segments)
        {
            if (segment.IsControlCode)
            {
                cut.Add(segment);
                continue;
            }

            int width = TextCells.Width(segment.Text);
            int cells = segment.CellCount();
            if (drawn + width <= room && counted + cells <= room)
            {
                cut.Add(segment);
                drawn += width;
                counted += cells;
                continue;
            }

            style = segment.Style;
            int end = 0;
            while (end < segment.Text.Length)
            {
                int glyph = TextCells.ClusterWidth(segment.Text, end, out int length);
                int spectre = new Segment(segment.Text.Substring(end, length)).CellCount();
                if (drawn + glyph > room || counted + spectre > room)
                {
                    break;
                }

                drawn += glyph;
                counted += spectre;
                end += length;
            }

            if (end > 0)
            {
                cut.Add(new Segment(segment.Text[..end], segment.Style, segment.Link));
            }

            break;
        }

        cut.Add(new Segment(Ellipsis, style));
        return cut;
    }
}
