using System.Globalization;
using NeonSidekick.Sql;

namespace NeonSidekick.Sqlite;

/// <summary>
/// <c>sqlite_explain</c>'s plan reader (2026-11-14, the database-tools plan's #12): <c>EXPLAIN QUERY PLAN</c>'s rows (<c>id</c>,
/// <c>parent</c>, <c>notused</c>, <c>detail</c>), the tree rebuilt from the parent ids. SQLite gives no row or cost estimates, so the flags are
/// by kind: a <c>SCAN</c> of a table with no index (every row read), a temporary B-tree for an ORDER BY, GROUP BY or DISTINCT, and an
/// automatic index SQLite builds for every run. Pure.
/// </summary>
public static class SqlitePlan
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
            if (Flag(detail) is { } flag)
            {
                flags.Add(flag);
            }
        }

        return new QueryPlan(steps, flags, null, null);
    }

    /// <summary>The flag a step's detail raises, or null.</summary>
    internal static string? Flag(string detail)
    {
        ArgumentNullException.ThrowIfNull(detail);
        if (detail.StartsWith("USE TEMP B-TREE", StringComparison.Ordinal))
        {
            return QueryPlanText.TempBTree(detail["USE TEMP B-TREE ".Length..].Trim() is { Length: > 0 } what ? what.ToLowerInvariant() : detail);
        }

        if (!detail.StartsWith("SCAN ", StringComparison.Ordinal) && !detail.StartsWith("SEARCH ", StringComparison.Ordinal))
        {
            return null;
        }

        string target = detail[(detail.IndexOf(' ', StringComparison.Ordinal) + 1)..];
        int usingAt = target.IndexOf(" USING ", StringComparison.Ordinal);
        string table = usingAt < 0 ? target : target[..usingAt];
        if (target.Contains("AUTOMATIC", StringComparison.Ordinal))
        {
            return QueryPlanText.AutomaticIndex(table);
        }

        // A bare SCAN reads the table; SCAN CONSTANT ROW, a co-routine's or a subquery's scan read no table of the database's.
        return detail.StartsWith("SCAN ", StringComparison.Ordinal) && usingAt < 0 && !table.StartsWith("CONSTANT ROW", StringComparison.Ordinal)
            && !table.StartsWith("(subquery", StringComparison.Ordinal) && !table.Contains(" VIRTUAL TABLE", StringComparison.Ordinal)
            ? QueryPlanText.FullScan(table, null)
            : null;
    }
}
