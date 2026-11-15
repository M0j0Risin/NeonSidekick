using System.Data.Common;
using System.Globalization;
using System.Text;

namespace NeonSidekick.Sql;

/// <summary>
/// One step of a query plan (2026-11-14, the database-tools plan's #12): its depth in the tree (0 the root), the operation as the server
/// names it, the object it reads (a table, an index, a file), and the server's estimates of its rows and its cost (the step and what
/// feeds it), either one null where the format has none. Every string is the server's, scrubbed when the answer is written.
/// </summary>
public sealed record QueryPlanStep(int Depth, string Operation, string? Target, double? Rows, double? Cost);

/// <summary>
/// A plan as the <c>*_explain</c> tools summarize it: the steps in tree order, the flags worth a look (each a sentence of
/// <see cref="QueryPlanText"/>'s), and the whole plan's estimated cost and rows where the server gives them.
/// </summary>
public sealed record QueryPlan(IReadOnlyList<QueryPlanStep> Steps, IReadOnlyList<string> Flags, double? TotalCost, double? TotalRows)
{
    /// <summary>The estimated rows from which a full scan, a nested loop's input or a sort is flagged.</summary>
    public const double BigRows = 10_000;

    /// <summary>
    /// The longest plan text read (4 MB): a plan is read whole, never through <see cref="SqlGrid.ReadAsync"/>'s 400-character cell, but a
    /// plan past this is one no summary would help with, and its parse fails into <see cref="QueryPlanText.Unreadable"/>'s raw start.
    /// </summary>
    public const int MaxPlanChars = 4 * 1024 * 1024;

    /// <summary>The most characters an explain answer carries (2026-11-14): a short summary, lowered by <c>SQL query result max chars</c> (<see cref="Llm.Tools.SqlTool.AnswerChars"/>).</summary>
    public const int MaxAnswerChars = 6_000;

    /// <summary>
    /// A cell read whole (to <see cref="MaxPlanChars"/>), in place of the grid's clipped and scrubbed one: a plan, or a name a catalog read
    /// brings. Text goes through the provider's reader in chunks (2026-11-15, #100: SQL Server, under SequentialAccess, and PostgreSQL stream
    /// it, so a huge value isn't loaded whole); any other value by its text. NULL is empty. A read that fails in any way but the server's
    /// own error, a cancel or memory is empty too (#154, as the grid's cell is): the plan is then unreadable and a name is left out. Never
    /// SQLite's text reader: it opens a blob on the column's table through <c>sqlite3_table_column_metadata</c>, which crashed the process
    /// (an access violation, 2026-11-15) on an <c>EXPLAIN QUERY PLAN</c> row; a SQLite plan's rows are short, so it is read by its text.
    /// </summary>
    public static string WholeCell(DbDataReader reader, int ordinal)
    {
        ArgumentNullException.ThrowIfNull(reader);
        try
        {
            if (reader.IsDBNull(ordinal))
            {
                return "";
            }

            if (reader is not Microsoft.Data.Sqlite.SqliteDataReader && reader.GetFieldType(ordinal) == typeof(string))
            {
                using var source = reader.GetTextReader(ordinal);
                var text = new StringBuilder();
                var buffer = new char[8192];
                int read;
                while (text.Length < MaxPlanChars && (read = source.Read(buffer, 0, Math.Min(buffer.Length, MaxPlanChars - text.Length))) > 0)
                {
                    text.Append(buffer, 0, read);
                }

                return text.ToString();
            }

            string value = Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture) ?? "";
            return value.Length <= MaxPlanChars ? value : Files.TextCut.Safe(value, MaxPlanChars);
        }
        catch (Exception ex) when (ex is not (DbException or OperationCanceledException or OutOfMemoryException))
        {
            return "";
        }
    }

    /// <summary>The plan text a run read: column <paramref name="column"/> of every row of its first result set, joined in order (a server may split one plan across rows).</summary>
    public static string TextOf(SqlRun run, int column)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (run.Grids.Count == 0 || run.Grids[0].Columns.Count == 0)
        {
            return "";
        }

        var grid = run.Grids[0];
        int at = column < 0 ? grid.Columns.Count + column : column;
        return at < 0 || at >= grid.Columns.Count ? "" : string.Concat(grid.Rows.Select(r => r[at]));
    }

    /// <summary>
    /// A plan's row estimate as the answer writes it: whole rows, at least 1 where the plan says above 0, clamped to <see cref="long"/>;
    /// null for none, a negative or a non-number.
    /// </summary>
    public static long? Estimate(double? rows)
    {
        if (rows is not { } value || double.IsNaN(value) || value < 0)
        {
            return null;
        }

        if (value >= long.MaxValue)
        {
            return long.MaxValue;
        }

        long whole = (long)Math.Round(value, MidpointRounding.AwayFromZero);
        return whole == 0 && value > 0 ? 1 : whole;
    }

    /// <summary>A number a plan wrote as text (MySQL's <c>"1.20"</c>, DuckDB's <c>"40000"</c>), invariant; null when it isn't one.</summary>
    public static double? Number(string? text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value) ? value : null;
}
