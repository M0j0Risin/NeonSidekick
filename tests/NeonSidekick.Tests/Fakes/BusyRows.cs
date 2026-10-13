using NeonSidekick.UI;

namespace NeonSidekick.Tests.Fakes;

/// <summary>
/// Where a glyph stands on the busy row a test console drew (2026-10-13: the busy row follows the Hint bar's sides, the spinner and its
/// label first, so the status glyphs stand after the label rather than at column 0).
/// </summary>
internal static class BusyRows
{
    /// <summary>The column of <paramref name="glyph"/> on the last row of <paramref name="output"/> that holds it beside a spinner frame.</summary>
    public static int ColumnOf(string output, string glyph)
    {
        foreach (string line in output.Split('\n').Reverse())
        {
            // The spinner leads the row at column 0; a redraw in place may leave earlier text ahead of it on the console's line.
            int at = line.LastIndexOf(glyph, StringComparison.Ordinal);
            int frame = at < 0 ? -1 : Theme.SpinnerFrames.Select(f => line.LastIndexOf(f, at, StringComparison.Ordinal)).Max();
            if (frame >= 0)
            {
                return TextCells.Width(line[frame..at]);
            }
        }

        throw new InvalidOperationException("No busy row holds " + glyph + ".");
    }
}
