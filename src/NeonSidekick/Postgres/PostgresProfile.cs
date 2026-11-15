using System.Globalization;
using NeonSidekick.Sql;

namespace NeonSidekick.Postgres;

/// <summary>
/// <c>postgres_profile</c>'s SQL (2026-11-14, the database-tools plan's #14), beside <see cref="PostgresCatalogQueries"/>: double quotes,
/// <c>LIMIT</c>, <c>TABLESAMPLE SYSTEM (p) REPEATABLE</c> for a table or materialized view (a view or a foreign table takes the first rows: a
/// foreign table's wrapper may not sample), and <c>bool_and</c>/<c>bool_or</c> for a boolean's range (there is no <c>min(boolean)</c>).
/// </summary>
public sealed class PostgresProfile : ProfileDialect
{
    /// <summary>
    /// The columns of <c>@schema</c>.<c>@name</c> (name, the type as <c>format_type</c> writes it, its category, its name; a domain's
    /// category and name are its base type's, #114), those the role may read (#123: one column it can't would fail the whole profile).
    /// Then the planner's row count: a table's or materialized view's <c>reltuples</c>, or a partitioned table's leaves summed through
    /// <c>pg_inherits</c> (#62; not <c>pg_partition_tree</c>, which is 12+ and fails the read at parse time on an older server). Null for a
    /// view, a table never analyzed (any leaf below 0), or a count below 1.
    /// </summary>
    public static readonly IReadOnlyList<string> Catalog =
    [
        """
        SELECT a.attname, format_type(a.atttypid, a.atttypmod), COALESCE(b.typcategory, t.typcategory)::text, COALESCE(b.typname, t.typname)::text
        FROM pg_catalog.pg_attribute a
        JOIN pg_catalog.pg_class c ON c.oid = a.attrelid
        JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
        JOIN pg_catalog.pg_type t ON t.oid = a.atttypid
        LEFT JOIN pg_catalog.pg_type b ON t.typtype = 'd' AND b.oid = t.typbasetype
        WHERE n.nspname = @schema AND c.relname = @name AND a.attnum > 0 AND NOT a.attisdropped
          AND has_column_privilege(c.oid, a.attnum, 'SELECT')
        ORDER BY a.attnum
        """,
        """
        WITH RECURSIVE tree AS (
          SELECT c.oid, c.relkind, c.reltuples
          FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
          WHERE n.nspname = @schema AND c.relname = @name AND c.relkind IN ('r', 'm', 'p')
          UNION ALL
          SELECT k.oid, k.relkind, k.reltuples
          FROM tree JOIN pg_catalog.pg_inherits i ON i.inhparent = tree.oid JOIN pg_catalog.pg_class k ON k.oid = i.inhrelid
          WHERE tree.relkind = 'p'
        )
        SELECT CASE WHEN bool_and(reltuples >= 0) AND sum(reltuples) >= 1 THEN sum(reltuples)::bigint END
        FROM tree WHERE relkind <> 'p'
        """,
    ];

    /// <summary>
    /// Pass 2's first statement: the transaction's DateStyle and IntervalStyle pinned (rolled back with it), so the values pass 2 casts to
    /// text read as the typed path reads them, whatever the server's defaults.
    /// </summary>
    public const string TextStyles = "SELECT set_config('DateStyle', 'ISO, MDY', true), set_config('IntervalStyle', 'postgres', true)";

    public static readonly PostgresProfile Instance = new();

    /// <summary>The kinds <c>TABLESAMPLE</c> reads (<see cref="PostgresCatalogQueries"/>' names).</summary>
    private static readonly HashSet<string> Sampled = new(StringComparer.Ordinal) { "table", "partitioned table", "materialized view" };

    /// <summary>
    /// What a column takes by its type's category (<c>pg_type.typcategory</c>): numbers, dates, intervals, strings and enums every measure
    /// (<c>text</c> too: PostgreSQL schemas use it for short values as often as long), a boolean its own range, a uuid or a network address
    /// distinct and top, and the rest (arrays, JSON, bytea, geometry, ranges, XML) the nulls alone.
    /// </summary>
    public static ProfileMeasure Measure(string category, string typeName)
    {
        ArgumentNullException.ThrowIfNull(category);
        ArgumentNullException.ThrowIfNull(typeName);
        return category switch
        {
            "N" when typeName.StartsWith("reg", StringComparison.Ordinal) => ProfileMeasure.Distinct,   // regclass and its kin have no MIN
            "N" or "D" or "T" or "S" or "E" => ProfileMeasure.Full,
            "B" => ProfileMeasure.Bool,
            "I" => ProfileMeasure.Distinct,
            _ when typeName == "uuid" => ProfileMeasure.Distinct,
            _ => ProfileMeasure.NullsOnly,
        };
    }

    public override string Quote(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    public override string Min(string column, ProfileMeasure measure) => measure == ProfileMeasure.Bool ? "bool_and(" + column + ")" : base.Min(column, measure);

    public override string Max(string column, ProfileMeasure measure) => measure == ProfileMeasure.Bool ? "bool_or(" + column + ")" : base.Max(column, measure);

    public override string FirstRows(string table, string list, long rows) => $"(SELECT {list} FROM {table} LIMIT {Invariant(rows)}) p";

    public override string? Sample(string table, string list, string kind, long rows, long? estimate)
    {
        if (!Sampled.Contains(kind) || estimate is not > 0)
        {
            return null;
        }

        double percent = Math.Clamp(100.0 * rows / estimate.Value, 0.000001, 100);
        return $"(SELECT {list} FROM {table} TABLESAMPLE SYSTEM ({percent.ToString("0.######", CultureInfo.InvariantCulture)}) REPEATABLE ({Seed}) LIMIT {Invariant(rows)}) p";
    }

    protected override string Limit(string query, int rows) => query + " LIMIT " + Invariant(rows);

    /// <summary>
    /// Pass 2 in one statement over one read of the source (2026-11-15, #5/#104): a CTE named twice or more is materialized (and before 12,
    /// always), so the keyword is left out and every server takes it. <see cref="TextStyles"/> runs first.
    /// </summary>
    public override IReadOnlyList<string> Tops(IReadOnlyList<ProfileColumn> columns, string from, int top) =>
        [TextStyles, OneSourceTops(columns, from, top, "", "text")];

    public override IReadOnlyList<IReadOnlyList<(string Value, long Count)>?> ReadTops(IReadOnlyList<SqlGrid> grids, int count)
    {
        ArgumentNullException.ThrowIfNull(grids);
        return ReadOneSourceTops(grids.Count > 1 ? grids[1] : null, count);
    }
}
