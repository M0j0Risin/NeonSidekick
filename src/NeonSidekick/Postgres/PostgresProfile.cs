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
    /// The columns of <c>@schema</c>.<c>@name</c> (name, the type as <c>format_type</c> writes it, its category, its name), then the
    /// planner's row count: null for a view, a partitioned parent, or a table never analyzed (<c>reltuples</c> below 1).
    /// </summary>
    public static readonly IReadOnlyList<string> Catalog =
    [
        """
        SELECT a.attname, format_type(a.atttypid, a.atttypmod), t.typcategory::text, t.typname::text
        FROM pg_catalog.pg_attribute a
        JOIN pg_catalog.pg_class c ON c.oid = a.attrelid
        JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
        JOIN pg_catalog.pg_type t ON t.oid = a.atttypid
        WHERE n.nspname = @schema AND c.relname = @name AND a.attnum > 0 AND NOT a.attisdropped
        ORDER BY a.attnum
        """,
        """
        SELECT CASE WHEN c.reltuples >= 1 THEN c.reltuples::bigint END
        FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = @schema AND c.relname = @name AND c.relkind IN ('r', 'm')
        """,
    ];

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

    public override string FirstRows(string table, long rows) => $"(SELECT * FROM {table} LIMIT {Invariant(rows)}) p";

    public override string? Sample(string table, string kind, long rows, long? estimate)
    {
        if (!Sampled.Contains(kind) || estimate is not > 0)
        {
            return null;
        }

        double percent = Math.Clamp(100.0 * rows / estimate.Value, 0.000001, 100);
        return $"(SELECT * FROM {table} TABLESAMPLE SYSTEM ({percent.ToString("0.######", CultureInfo.InvariantCulture)}) REPEATABLE ({Seed}) LIMIT {Invariant(rows)}) p";
    }

    protected override string Limit(string query, int rows) => query + " LIMIT " + Invariant(rows);

    private static string Invariant(long n) => n.ToString(CultureInfo.InvariantCulture);
}
