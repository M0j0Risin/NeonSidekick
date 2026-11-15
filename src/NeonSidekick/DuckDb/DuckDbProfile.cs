using NeonSidekick.Sql;

namespace NeonSidekick.DuckDb;

/// <summary>
/// <c>duckdb_profile</c>'s SQL (2026-11-14, the database-tools plan's #14): the columns from <c>DESCRIBE</c>, pass 1 the shared aggregate with
/// <c>approx_count_distinct</c> (2026-11-15, #35/#55: not <c>SUMMARIZE</c>, whose rows were matched by scrubbed name and whose quantiles were
/// never read), pass 2 one statement over one materialized source. A source past the cap, or one with no estimate (DuckDB has none), is a
/// seeded reservoir sample: a source smaller than the reservoir comes back whole. Every value comes back as DuckDB writes it (the access's
/// <c>::VARCHAR</c> wrapper).
/// </summary>
public sealed class DuckDbProfile : ProfileDialect
{
    public static readonly DuckDbProfile Instance = new();

    /// <summary>The columns of a source: <c>DESCRIBE</c>'s rows, <c>column_name</c> then <c>column_type</c> first.</summary>
    public static string Columns(string table) => "DESCRIBE SELECT * FROM " + table;

    /// <summary>
    /// What a column of <paramref name="type"/> takes: a nested type (a list, a struct, a map, a union) the nulls and distinct (no range, no
    /// top values: it can't be ordered), BLOB and BIT the nulls alone, the rest every measure.
    /// </summary>
    public static ProfileMeasure Measure(string type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (Nested(type))
        {
            return ProfileMeasure.Distinct;
        }

        return type.Equals("BLOB", StringComparison.OrdinalIgnoreCase) || type.Equals("BIT", StringComparison.OrdinalIgnoreCase)
            ? ProfileMeasure.NullsOnly
            : ProfileMeasure.Full;
    }

    /// <summary>Whether a DuckDB type is a list, an array, a struct, a map or a union.</summary>
    public static bool Nested(string type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return type.EndsWith(']') || type.StartsWith("STRUCT", StringComparison.OrdinalIgnoreCase) || type.StartsWith("MAP", StringComparison.OrdinalIgnoreCase)
            || type.StartsWith("UNION", StringComparison.OrdinalIgnoreCase);
    }

    public override string Quote(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    public override bool ApproximateDistinct => true;

    public override string Distinct(string column) => "approx_count_distinct(" + column + ")";

    public override bool Groupable(ProfileColumn column)
    {
        ArgumentNullException.ThrowIfNull(column);
        return !Nested(column.Type);
    }

    /// <summary>DuckDB's reservoir returns every row of a source smaller than it (held by <c>TableProfileTests</c>).</summary>
    public override bool SampleTakesSmallWhole => true;

    public override string FirstRows(string table, string list, long rows) => $"(SELECT {list} FROM {table} LIMIT {Invariant(rows)}) p";

    /// <summary>Any source is sampled, a table, a view or a file alike: DuckDB's reservoir reads every row and keeps the count asked.</summary>
    public override string? Sample(string table, string list, string kind, long rows, long? estimate) =>
        $"(SELECT {list} FROM {table} USING SAMPLE reservoir({Invariant(rows)} ROWS) REPEATABLE ({Seed})) p";

    protected override string Limit(string query, int rows) => query + " LIMIT " + Invariant(rows);

    /// <summary>
    /// Pass 2 in one statement over one materialized source (2026-11-15, #5/#21: each statement drew its own reservoir, and a multi-threaded
    /// run's REPEATABLE isn't the same rows twice).
    /// </summary>
    public override IReadOnlyList<string> Tops(IReadOnlyList<ProfileColumn> columns, string from, int top) =>
        [OneSourceTops(columns, from, top, "MATERIALIZED ", "VARCHAR")];

    public override IReadOnlyList<IReadOnlyList<(string Value, long Count)>?> ReadTops(IReadOnlyList<SqlGrid> grids, int count)
    {
        ArgumentNullException.ThrowIfNull(grids);
        return ReadOneSourceTops(grids.Count > 0 ? grids[0] : null, count);
    }
}
