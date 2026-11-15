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
    /// <summary>
    /// The columns of <c>:owner</c>.<c>:name</c> (name, data type, <c>char_length</c>), invisible ones left out (#17: a profile shows the
    /// columns <c>SELECT *</c> shows), then the optimizer's row count (null for a view or before statistics).
    /// </summary>
    public static readonly IReadOnlyList<string> Catalog =
    [
        """
        SELECT c.column_name, c.data_type, c.char_length FROM sys.all_tab_cols c
        WHERE c.owner = :owner AND c.table_name = :name AND c.user_generated = 'YES' AND c.hidden_column = 'NO'
        ORDER BY c.column_id
        """,
        "SELECT t.num_rows FROM sys.all_tables t WHERE t.owner = :owner AND t.table_name = :name",
    ];

    public static readonly OracleProfile Instance = new();

    private static readonly HashSet<string> Ranged = new(StringComparer.Ordinal) { "NUMBER", "FLOAT", "BINARY_FLOAT", "BINARY_DOUBLE", "DATE" };

    private static readonly HashSet<string> Text = new(StringComparer.Ordinal) { "VARCHAR2", "NVARCHAR2", "CHAR", "NCHAR" };

    /// <summary>The longest text measured, in characters (2026-11-15, #111: a VARCHAR2(32767) under EXTENDED is the nulls alone).</summary>
    public const int MaxTextChars = 4_000;

    /// <summary>
    /// What a column of <paramref name="type"/> takes (<c>TIMESTAMP(6) WITH TIME ZONE</c> and <c>INTERVAL …</c> by their lead); a text type
    /// by its <paramref name="charLength"/> (null: not known, measured).
    /// </summary>
    public static ProfileMeasure Measure(string type, long? charLength = null)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (Ranged.Contains(type) || type.StartsWith("TIMESTAMP", StringComparison.Ordinal) || type.StartsWith("INTERVAL", StringComparison.Ordinal))
        {
            return ProfileMeasure.Full;
        }

        if (Text.Contains(type))
        {
            return charLength is null or (> 0 and <= MaxTextChars) ? ProfileMeasure.Full : ProfileMeasure.NullsOnly;
        }

        // BOOLEAN (23ai), ROWID and UROWID count and group but have no order worth a range (#115).
        return type switch
        {
            "RAW" or "BOOLEAN" or "ROWID" or "UROWID" => ProfileMeasure.Distinct,
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

    public override string FirstRows(string table, string list, long rows) => $"(SELECT {list} FROM {table} WHERE ROWNUM <= {Invariant(rows)}) p";

    /// <summary>A table's sample: the share of its estimate that gives about <paramref name="rows"/>, seeded, capped at that many.</summary>
    public override string? Sample(string table, string list, string kind, long rows, long? estimate)
    {
        if (kind != "table" || estimate is not > 0)
        {
            return null;
        }

        double percent = Math.Clamp(100.0 * rows / estimate.Value, 0.000001, 99.999999);
        return $"(SELECT {list} FROM {table} SAMPLE ({percent.ToString("0.######", CultureInfo.InvariantCulture)}) SEED ({Seed}) WHERE ROWNUM <= {Invariant(rows)}) p";
    }

    protected override string Limit(string query, int rows) => query + " FETCH FIRST " + Invariant(rows) + " ROWS ONLY";
}
