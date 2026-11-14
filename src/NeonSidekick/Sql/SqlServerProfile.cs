using System.Globalization;

namespace NeonSidekick.Sql;

/// <summary>
/// <c>sql_profile</c>'s SQL (2026-11-14, the database-tools plan's #14), beside <see cref="SqlCatalogQueries"/>: brackets, <c>COUNT_BIG</c>,
/// <c>TOP</c>, <c>TABLESAMPLE (n ROWS) REPEATABLE</c> for a table (a view takes the first rows), and <c>APPROX_COUNT_DISTINCT</c> when the
/// database's compatibility level is 150 or higher.
/// </summary>
public sealed class SqlServerProfile : ProfileDialect
{
    /// <summary>
    /// Two result sets on the object <c>@id</c>: its columns (name, the type as declared, the system type, <c>max_length</c>), then the
    /// partition counts (approximate, free; null for a view) and the database's compatibility level.
    /// </summary>
    public const string Catalog =
        """
        SELECT c.name, TYPE_NAME(c.user_type_id), TYPE_NAME(c.system_type_id), c.max_length
        FROM sys.columns AS c
        WHERE c.object_id = @id
        ORDER BY c.column_id;

        SELECT (SELECT SUM(p.rows) FROM sys.partitions AS p JOIN sys.objects AS o ON o.object_id = p.object_id
                WHERE p.object_id = @id AND p.index_id IN (0, 1) AND o.type = 'U'),
               (SELECT d.compatibility_level FROM sys.databases AS d WHERE d.database_id = DB_ID())
        """;

    /// <summary>The level <c>APPROX_COUNT_DISTINCT</c> came with (SQL Server 2019).</summary>
    public const int ApproximateLevel = 150;

    private static readonly HashSet<string> Ranged = new(StringComparer.OrdinalIgnoreCase)
    {
        "tinyint", "smallint", "int", "bigint", "decimal", "numeric", "money", "smallmoney", "float", "real",
        "date", "datetime", "datetime2", "smalldatetime", "datetimeoffset", "time",
    };

    private static readonly HashSet<string> Text = new(StringComparer.OrdinalIgnoreCase) { "char", "varchar", "nchar", "nvarchar" };

    private readonly bool _approximate;

    public SqlServerProfile(bool approximate)
    {
        _approximate = approximate;
    }

    /// <summary>The dialect for a database at <paramref name="level"/> (null: unknown, the exact count).</summary>
    public static SqlServerProfile For(long? level) => new(level >= ApproximateLevel);

    /// <summary>What a column of the system type <paramref name="type"/> takes; <paramref name="maxLength"/> is -1 for a <c>(max)</c> type.</summary>
    public static ProfileMeasure Measure(string type, long? maxLength)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (Ranged.Contains(type))
        {
            return ProfileMeasure.Full;
        }

        if (Text.Contains(type))
        {
            return maxLength is > 0 and <= 8_000 ? ProfileMeasure.Full : ProfileMeasure.NullsOnly;
        }

        return type.ToLowerInvariant() is "bit" or "uniqueidentifier" ? ProfileMeasure.Distinct : ProfileMeasure.NullsOnly;
    }

    public override string Quote(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return "[" + name.Replace("]", "]]", StringComparison.Ordinal) + "]";
    }

    public override string CountAll => "COUNT_BIG(*)";

    public override string CountOf(string column) => "COUNT_BIG(" + column + ")";

    public override bool ApproximateDistinct => _approximate;

    public override string Distinct(string column) => _approximate ? "APPROX_COUNT_DISTINCT(" + column + ")" : "COUNT_BIG(DISTINCT " + column + ")";

    public override string FirstRows(string table, long rows) => $"(SELECT TOP ({Invariant(rows)}) * FROM {table}) p";

    public override string? Sample(string table, string kind, long rows, long? estimate) =>
        kind == "table" ? $"(SELECT TOP ({Invariant(rows)}) * FROM {table} TABLESAMPLE ({Invariant(rows)} ROWS) REPEATABLE ({Seed})) p" : null;

    public override string Top(string column, string from, int top) =>
        $"SELECT TOP ({Invariant(top)}) {column}, COUNT_BIG(*) FROM {from} WHERE {column} IS NOT NULL GROUP BY {column} ORDER BY 2 DESC, 1";

    protected override string Limit(string query, int rows) => throw new NotSupportedException();

    private static string Invariant(long n) => n.ToString(CultureInfo.InvariantCulture);
}
