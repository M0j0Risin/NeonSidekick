using System.Globalization;
using NeonSidekick.Sql;

namespace NeonSidekick.Oracle;

/// <summary>
/// <c>oracle_profile</c>'s SQL (2026-11-14, the database-tools plan's #14), beside <see cref="OracleCatalogQueries"/>: double quotes, the table
/// always written with its owner, <c>ROWNUM</c> for the first rows, <c>SAMPLE (p) SEED</c> for a table (a view takes the first rows) and
/// <c>APPROX_COUNT_DISTINCT</c> (12c, the app's floor).
/// </summary>
public sealed class OracleProfile : ProfileDialect
{
    /// <summary>The columns of <c>:owner</c>.<c>:name</c> (name, data type, <c>char_length</c>), then the optimizer's row count (null for a view or before statistics).</summary>
    public static readonly IReadOnlyList<string> Catalog =
    [
        """
        SELECT c.column_name, c.data_type, c.char_length FROM sys.all_tab_cols c
        WHERE c.owner = :owner AND c.table_name = :name AND c.user_generated = 'YES'
        ORDER BY c.column_id
        """,
        "SELECT t.num_rows FROM sys.all_tables t WHERE t.owner = :owner AND t.table_name = :name",
    ];

    public static readonly OracleProfile Instance = new();

    private static readonly HashSet<string> Ranged = new(StringComparer.Ordinal)
    {
        "NUMBER", "FLOAT", "BINARY_FLOAT", "BINARY_DOUBLE", "DATE", "VARCHAR2", "NVARCHAR2", "CHAR", "NCHAR",
    };

    /// <summary>What a column of <paramref name="type"/> takes (<c>TIMESTAMP(6) WITH TIME ZONE</c> and <c>INTERVAL …</c> by their lead).</summary>
    public static ProfileMeasure Measure(string type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (Ranged.Contains(type) || type.StartsWith("TIMESTAMP", StringComparison.Ordinal) || type.StartsWith("INTERVAL", StringComparison.Ordinal))
        {
            return ProfileMeasure.Full;
        }

        return type switch
        {
            "RAW" => ProfileMeasure.Distinct,
            "LONG" or "LONG RAW" => ProfileMeasure.None,
            _ => ProfileMeasure.NullsOnly,
        };
    }

    public override string Quote(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    public override bool ApproximateDistinct => true;

    public override string Distinct(string column) => "APPROX_COUNT_DISTINCT(" + column + ")";

    public override string FirstRows(string table, long rows) => $"(SELECT * FROM {table} WHERE ROWNUM <= {Invariant(rows)}) p";

    /// <summary>A table's sample: the share of its estimate that gives about <paramref name="rows"/>, seeded, capped at that many.</summary>
    public override string? Sample(string table, string kind, long rows, long? estimate)
    {
        if (kind != "table" || estimate is not > 0)
        {
            return null;
        }

        double percent = Math.Clamp(100.0 * rows / estimate.Value, 0.000001, 99.999999);
        return $"(SELECT * FROM {table} SAMPLE ({percent.ToString("0.######", CultureInfo.InvariantCulture)}) SEED ({Seed}) WHERE ROWNUM <= {Invariant(rows)}) p";
    }

    protected override string Limit(string query, int rows) => query + " FETCH FIRST " + Invariant(rows) + " ROWS ONLY";

    private static string Invariant(long n) => n.ToString(CultureInfo.InvariantCulture);
}
