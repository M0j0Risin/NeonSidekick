using System.Globalization;
using NeonSidekick.Files;

namespace NeonSidekick.UI;

/// <summary>
/// The wording of a folded diff's summary row (2026-10-04, the user's ask: the tool runs' and the code blocks' fold for an
/// edit's diff, <c>Diff collapse count</c>): <c>     ▸ Added 3 lines, removed 1 line · 14 rows</c> — the triangle in the place
/// of <see cref="DiffView.Elbow"/> (the user's pick), the triangle down (<see cref="ToolGroupText.ExpandedGlyph"/>) while the diff
/// shows its rows. The count is every hunk row of the diff, past <c>Diff max lines</c> too. Pinned.
/// </summary>
public static class DiffFoldText
{
    /// <summary>The summary row of <paramref name="diff"/>, of <paramref name="rows"/> hunk rows, folded or not.</summary>
    public static string Summary(FileDiff diff, int rows, bool expanded)
    {
        ArgumentNullException.ThrowIfNull(diff);
        return DiffView.Indent + (expanded ? ToolGroupText.ExpandedGlyph : ToolGroupText.CollapsedGlyph) + " " + FileText.DiffSummary(diff) + " · "
            + rows.ToString(CultureInfo.InvariantCulture) + (rows == 1 ? " row" : " rows");
    }
}
