using System.Globalization;
using NeonSidekick.Sql;

namespace NeonSidekick.Sqlite;

/// <summary>
/// <c>sqlite_explain</c>'s plan reader (2026-11-14, the database-tools plan's #12): <c>EXPLAIN QUERY PLAN</c>'s rows (<c>id</c>,
/// <c>parent</c>, <c>notused</c>, <c>detail</c>), the tree rebuilt from the parent ids. SQLite gives no row or cost estimates, so the flags are
/// by kind: a <c>SCAN</c> of a table with no index (every row read), a temporary B-tree for an ORDER BY, GROUP BY or DISTINCT, and an
/// automatic index or a Bloom filter SQLite builds for every run (#162). A scan of what is no table of the database's is not flagged
/// (2026-11-15, #39, #168): a CTE or subquery the plan materializes or runs as a co-routine, and the forms SQLite's source prints for rows it
/// makes itself (<c>select.c</c>'s <c>SCAN %d CONSTANT ROW%s</c>; <c>printf.c</c>'s <c>N-ROW VALUES CLAUSE</c> and <c>(join-N)</c>). Pure.
/// </summary>
public static partial class SqlitePlan
{
    /// <summary>What the tool puts in front of the model's statement: the plan, never the bytecode (<c>EXPLAIN</c> alone).</summary>
    public const string Prefix = "EXPLAIN QUERY PLAN ";

    /// <summary>The plan from a run's first grid, or null when its rows aren't <c>EXPLAIN QUERY PLAN</c>'s four columns.</summary>
    public static QueryPlan? Parse(SqlGrid grid)
    {
        ArgumentNullException.ThrowIfNull(grid);
        if (grid.Columns.Count < 4)
        {
            return null;
        }

        var depthOf = new Dictionary<long, int>();
        var steps = new List<QueryPlanStep>();
        var flags = new List<string>();
        var made = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in grid.Rows)
        {
            if (!long.TryParse(row[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long id)
                || !long.TryParse(row[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long parent))
            {
                return null;
            }

            int depth = depthOf.TryGetValue(parent, out int above) ? above + 1 : 0;
            depthOf[id] = depth;
            string detail = row[3];
            steps.Add(new QueryPlanStep(depth, detail, null, null, null));
            if (Made(detail) is { } name)
            {
                made.Add(name);
            }

            if (Flag(detail, made) is { } flag)
            {
                flags.Add(flag);
            }
        }

        return new QueryPlan(steps, flags, null, null);
    }

    private const string TempBTreeLead = "USE TEMP B-TREE";
    private const string BloomLead = "BLOOM FILTER ON ";

    /// <summary>The name a <c>MATERIALIZE</c> or <c>CO-ROUTINE</c> step makes (a CTE's or a subquery's), or null.</summary>
    private static string? Made(string detail)
    {
        foreach (string lead in new[] { "MATERIALIZE ", "CO-ROUTINE " })
        {
            if (detail.StartsWith(lead, StringComparison.Ordinal) && detail[lead.Length..].Trim() is { Length: > 0 } name)
            {
                return name;
            }
        }

        return null;
    }

    /// <summary>The flag a step's detail raises, or null; <paramref name="made"/> holds the names the plan's own steps made.</summary>
    internal static string? Flag(string detail, IReadOnlySet<string>? made = null)
    {
        ArgumentNullException.ThrowIfNull(detail);
        if (detail.StartsWith(TempBTreeLead, StringComparison.Ordinal))
        {
            // #40: the detail may be the lead alone.
            string what = detail[TempBTreeLead.Length..].Trim();
            return QueryPlanText.TempBTree(what.Length > 0 ? what.ToLowerInvariant() : detail);
        }

        if (detail.StartsWith(BloomLead, StringComparison.Ordinal))
        {
            string on = detail[BloomLead.Length..];
            int open = on.IndexOf(" (", StringComparison.Ordinal);
            return QueryPlanText.BloomFilter(open < 0 ? on : on[..open]);
        }

        if (!detail.StartsWith("SCAN ", StringComparison.Ordinal) && !detail.StartsWith("SEARCH ", StringComparison.Ordinal))
        {
            return null;
        }

        string target = detail[(detail.IndexOf(' ', StringComparison.Ordinal) + 1)..];
        int usingAt = target.IndexOf(" USING ", StringComparison.Ordinal);
        string table = usingAt < 0 ? target : target[..usingAt];
        int alias = table.IndexOf(" AS ", StringComparison.Ordinal);   // #84: SQLite before 3.36 named the table and its alias
        if (alias > 0)
        {
            table = table[..alias];
        }

        if (target.Contains("AUTOMATIC", StringComparison.Ordinal))
        {
            return QueryPlanText.AutomaticIndex(table);
        }

        // A bare SCAN reads the table; a scan of rows the plan makes itself reads no table of the database's.
        return detail.StartsWith("SCAN ", StringComparison.Ordinal) && usingAt < 0 && !MadeRows(table) && made?.Contains(table) != true
            ? QueryPlanText.FullScan(table, null)
            : null;
    }

    /// <summary>Whether a scanned name is rows SQLite makes itself: constant rows, a VALUES clause, a subquery, a nested join, a virtual table.</summary>
    private static bool MadeRows(string table) =>
        ConstantRows().IsMatch(table) || table.EndsWith("-ROW VALUES CLAUSE", StringComparison.Ordinal)
        || table.StartsWith("(subquery", StringComparison.Ordinal) || table.StartsWith("(join-", StringComparison.Ordinal)
        || table.Contains(" VIRTUAL TABLE", StringComparison.Ordinal);

    /// <summary><c>CONSTANT ROW</c> (before 3.47) and <c>N CONSTANT ROW</c>/<c>N CONSTANT ROWS</c> after.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"^(\d+ )?CONSTANT ROWS?\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex ConstantRows();
}
