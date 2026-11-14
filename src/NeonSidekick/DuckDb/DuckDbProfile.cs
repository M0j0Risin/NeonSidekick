using System.Globalization;
using NeonSidekick.Sql;

namespace NeonSidekick.DuckDb;

/// <summary>
/// <c>duckdb_profile</c>'s SQL (2026-11-14, the database-tools plan's #14): pass 1 is DuckDB's own <c>SUMMARIZE</c> (every type's range and
/// an approximate distinct count) beside an exact count of each column's values; a big table or file is a seeded reservoir sample of
/// <see cref="TableProfile.SampleAbove"/> rows. Every value comes back as DuckDB writes it (the access's <c>::VARCHAR</c> wrapper).
/// </summary>
public sealed class DuckDbProfile : ProfileDialect
{
    public static readonly DuckDbProfile Instance = new();

    /// <summary>The columns of a source, read as the header of no rows: <c>SELECT * FROM source LIMIT 0</c>.</summary>
    public static string Columns(string table) => "SELECT * FROM " + table + " LIMIT 0";

    public override string Quote(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    public override bool ApproximateDistinct => true;

    public override string FirstRows(string table, long rows) => $"(SELECT * FROM {table} LIMIT {Invariant(rows)}) p";

    /// <summary>Any source past the cap is sampled, a table, a view or a file alike: DuckDB's reservoir reads every row and keeps the count asked.</summary>
    public override string? Sample(string table, string kind, long rows, long? estimate) =>
        $"(SELECT * FROM {table} USING SAMPLE reservoir({Invariant(rows)} ROWS) REPEATABLE ({Seed})) p";

    protected override string Limit(string query, int rows) => query + " LIMIT " + Invariant(rows);

    /// <summary>Pass 1: <c>SUMMARIZE</c> of the columns, then <c>count(*)</c> and each column's <c>count</c>, over the same source.</summary>
    public override IReadOnlyList<string> Aggregate(IReadOnlyList<ProfileColumn> columns, string from)
    {
        ArgumentNullException.ThrowIfNull(columns);
        string list = string.Join(", ", columns.Select(c => Quote(c.Name)));
        return
        [
            $"SUMMARIZE SELECT {list} FROM {from}",
            "SELECT count(*), " + string.Join(", ", columns.Select(c => "count(" + Quote(c.Name) + ")")) + " FROM " + from,
        ];
    }

    /// <summary>
    /// <c>SUMMARIZE</c>'s rows matched to the columns by name (<c>column_name</c>, <c>column_type</c>, <c>min</c>, <c>max</c>,
    /// <c>approx_unique</c>), the counts by position. A nested type (a list, a struct, a map) has no top values: it can't be ordered.
    /// </summary>
    public override IReadOnlyList<ColumnStats>? ReadAggregate(IReadOnlyList<SqlGrid> grids, IReadOnlyList<ProfileColumn> columns, out long rows)
    {
        ArgumentNullException.ThrowIfNull(grids);
        ArgumentNullException.ThrowIfNull(columns);
        rows = 0;
        if (grids is not [var summary, { Rows: [var counts, ..] }, ..] || counts.Length < 1 + columns.Count || TableProfile.Whole(counts[0]) is not { } counted)
        {
            return null;
        }

        int name = Index(summary, "column_name"), type = Index(summary, "column_type"), min = Index(summary, "min"), max = Index(summary, "max"), unique = Index(summary, "approx_unique");
        if (name < 0 || type < 0 || min < 0 || max < 0 || unique < 0)
        {
            return null;
        }

        rows = counted;
        var stats = new List<ColumnStats>(columns.Count);
        for (int i = 0; i < columns.Count; i++)
        {
            var row = summary.Rows.FirstOrDefault(r => r.Length > name && r[name] == columns[i].Name);
            string shown = row?[type] ?? columns[i].Type;
            var column = columns[i] with { Type = shown };
            stats.Add(new ColumnStats(column, TableProfile.Whole(counts[1 + i]), TableProfile.Whole(row?[unique]), true, row?[min], row?[max]) { Groupable = !Nested(shown) });
        }

        return stats;
    }

    /// <summary>Whether a DuckDB type is a list, an array, a struct, a map or a union.</summary>
    public static bool Nested(string type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return type.EndsWith(']') || type.StartsWith("STRUCT", StringComparison.OrdinalIgnoreCase) || type.StartsWith("MAP", StringComparison.OrdinalIgnoreCase)
            || type.StartsWith("UNION", StringComparison.OrdinalIgnoreCase);
    }

    private static int Index(SqlGrid grid, string column) => grid.Columns.ToList().FindIndex(c => c.Equals(column, StringComparison.OrdinalIgnoreCase));

    private static string Invariant(long n) => n.ToString(CultureInfo.InvariantCulture);
}
