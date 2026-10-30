using System.Globalization;

namespace NeonSidekick.Sql;

/// <summary>The words of a query's export to a workbook (2026-10-29, the Office writing plan), shared by the six families.</summary>
public static class SqlExportText
{
    /// <summary>The sheet an export writes when none is named.</summary>
    public const string DefaultSheet = "Query";

    /// <summary>The most characters of preview table an export's answer carries.</summary>
    public const int PreviewChars = 6_000;

    /// <summary>
    /// What a failed or timed-out export adds to the family's error: nothing was written, how many rows had been read, and (for a timeout)
    /// to narrow the query. Pinned.
    /// </summary>
    public static string NotWritten(long rows, bool timedOut) =>
        $"; nothing was written ({Count(rows, "row")} read)" + (timedOut ? "; narrow the query to fewer rows or columns" : "");

    /// <summary>
    /// A statement that ran but gave no rows to write (an <c>EXEC</c> with no result set; 2026-10-29, review P1-4: it made an empty file
    /// named as a saved workbook). Pinned.
    /// </summary>
    public const string NoResultSet = "Error: the query returned no result set, so no workbook was written; to writes the rows a SELECT returns";

    /// <summary>A cell whose value the provider won't hand over in any form: its type said. Pinned.</summary>
    public static string Unreadable(string? typeName) => $"({(string.IsNullOrWhiteSpace(typeName) ? "a value" : typeName)} that can't be read)";

    /// <summary>The clause a cut chat query's "more exist" adds while a save root is open, so the model learns <c>to</c> when it helps.</summary>
    public const string SaveHint = ", or write them all to a spreadsheet with to";

    /// <summary>The hint when <paramref name="canSave"/>, else nothing: the six families' cut sentences end with it.</summary>
    public static string Hint(bool canSave) => canSave ? SaveHint : "";

    /// <summary>
    /// An export's answer: the save sentence first (the transcript's note), the cut note when the call's own <c>max_rows</c> stopped it
    /// (<paramref name="given"/>; with none given the sheet stops at Excel's last row, which the save sentence says itself, 2026-10-29, review
    /// P2-4), then the columns and the first rows as the chat would show them. A refusal or a late close is the answer alone.
    /// </summary>
    public static string Answer(string saved, SqlGrid grid, int? given)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(grid);
        if (saved.StartsWith("Error:", StringComparison.Ordinal) || saved.StartsWith("Not saved", StringComparison.Ordinal))
        {
            return saved;
        }

        string cut = grid.More && given is { } maxRows ? $"; stopped at max_rows ({maxRows.ToString("N0", CultureInfo.InvariantCulture)}), so later rows are left out" : "";
        if (grid.Rows.Count == 0)
        {
            return saved + cut + "\n\nNo rows: the sheet holds the header alone.";
        }

        string table = SqlText.Table(grid, PreviewChars, out _);
        return saved + cut + $"\n\nThe first {Count(grid.Rows.Count, "row")}:\n\n" + table;
    }

    private static string Count(long count, string what) => count.ToString("N0", CultureInfo.InvariantCulture) + " " + what + (count == 1 ? "" : "s");
}
