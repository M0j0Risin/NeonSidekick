using NeonSidekick.Files;
using NeonSidekick.UI;

namespace NeonSidekick.WebUi;

/// <summary>
/// An edit's diff as the Web UI's page draws it (2026-10-15, the plan's Part B): the terminal's <see cref="DiffView"/> in plain rows — the head
/// (<c>Added 3 lines, removed 1 line</c>), each row's sign, number and text (its colored pieces joined; the page draws no syntax colors), a null
/// between two hunks, cut at <c>Diff max lines</c> with the line saying how many more there were, and the fold <c>Diff collapse count</c> asks for with
/// <see cref="DiffFoldText"/>'s summary. Pure.
/// </summary>
public static class WebDiffs
{
    /// <summary>The page's diff of <paramref name="diff"/>, its rows cut at <paramref name="maxLines"/> (0: the head alone), folded past <paramref name="collapseCount"/> (0: never).</summary>
    public static WebDiff From(FileDiff diff, int maxLines, int collapseCount)
    {
        ArgumentNullException.ThrowIfNull(diff);
        var all = DiffView.RowsOf(diff, DiffView.LanguageOf(diff.Path));
        int total = all.Count(r => r is not null);
        var rows = new List<WebDiffRow?>();
        int taken = 0;
        if (maxLines > 0)
        {
            foreach (var row in all)
            {
                if (row is { } line)
                {
                    if (taken == maxLines)
                    {
                        break;
                    }

                    rows.Add(new WebDiffRow(line.Sign.ToString(), line.Number, string.Concat(line.Pieces.Select(p => p.Text))));
                    taken++;
                }
                else
                {
                    rows.Add(null);
                }
            }
        }

        while (rows.Count > 0 && rows[^1] is null)
        {
            rows.RemoveAt(rows.Count - 1);   // a gap with nothing after it, past the cut
        }

        int fold = collapseCount > 0 && maxLines > 0 && total > collapseCount ? collapseCount : 0;
        // The head alone (Diff max lines 0) says nothing more, as the PC's.
        return new WebDiff(FileText.DiffSummary(diff), rows, maxLines > 0 && taken < total ? FileText.DiffMore(total - taken) : null, fold, fold > 0 ? DiffFoldText.Bare(diff, total) : "");
    }
}
