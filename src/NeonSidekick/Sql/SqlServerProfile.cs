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
    /// Two result sets on the object <c>@id</c>: its columns (name, the type as declared, the system type, <c>max_length</c>, precision,
    /// scale), then the partition counts (approximate, free; null for a view), the database's compatibility level and whether the table is
    /// memory-optimized (TABLESAMPLE refuses one, #71). The columns are those the account may read (#124: one column denied would fail the
    /// whole profile) and not hidden (#17: a temporal table's period columns). The property functions, not the newer catalog columns, keep a
    /// server before 2016 answering: there either gives NULL, read as "no".
    /// </summary>
    public const string Catalog =
        """
        SELECT c.name, TYPE_NAME(c.user_type_id), TYPE_NAME(c.system_type_id), c.max_length, c.precision, c.scale
        FROM sys.columns AS c
        WHERE c.object_id = @id
          AND HAS_PERMS_BY_NAME(QUOTENAME(OBJECT_SCHEMA_NAME(@id)) + '.' + QUOTENAME(OBJECT_NAME(@id)), 'OBJECT', 'SELECT', c.name, 'COLUMN') = 1
          AND ISNULL(COLUMNPROPERTY(c.object_id, c.name, 'IsHidden'), 0) = 0
        ORDER BY c.column_id;

        SELECT (SELECT SUM(p.rows) FROM sys.partitions AS p JOIN sys.objects AS o ON o.object_id = p.object_id
                WHERE p.object_id = @id AND p.index_id IN (0, 1) AND o.type = 'U'),
               (SELECT d.compatibility_level FROM sys.databases AS d WHERE d.database_id = DB_ID()),
               ISNULL(OBJECTPROPERTY(@id, 'TableIsMemoryOptimized'), 0)
        """;

    /// <summary>The kind a memory-optimized table is profiled as: not "table", so it is never sampled.</summary>
    public const string MemoryOptimizedKind = "memory-optimized table";

    /// <summary>The longest text measured, in characters (2026-11-15, #54: the bytes of an <c>nchar</c>/<c>nvarchar</c> halved).</summary>
    public const int MaxTextChars = 4_000;

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
            long? chars = type.StartsWith('n') || type.StartsWith('N') ? maxLength / 2 : maxLength;
            return chars is > 0 and <= MaxTextChars ? ProfileMeasure.Full : ProfileMeasure.NullsOnly;
        }

        return type.ToLowerInvariant() is "bit" or "uniqueidentifier" ? ProfileMeasure.Distinct : ProfileMeasure.NullsOnly;
    }

    public override string Quote(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return "[" + name.Replace("]", "]]", StringComparison.Ordinal) + "]";
    }

    /// <summary>Brackets as well as double quotes (2026-11-15, #51): <c>[Order Date]</c> is matched as written.</summary>
    public override (string Name, bool Quoted) Unquote(string written)
    {
        ArgumentNullException.ThrowIfNull(written);
        string name = written.Trim();
        return name.Length > 1 && name[0] == '[' && name[^1] == ']'
            ? (name[1..^1].Replace("]]", "]", StringComparison.Ordinal), true)
            : base.Unquote(name);
    }

    public override string CountAll => "COUNT_BIG(*)";

    public override string CountOf(string column) => "COUNT_BIG(" + column + ")";

    public override bool ApproximateDistinct => _approximate;

    public override string Distinct(string column) => _approximate ? "APPROX_COUNT_DISTINCT(" + column + ")" : "COUNT_BIG(DISTINCT " + column + ")";

    public override string FirstRows(string table, string list, long rows) => $"(SELECT TOP ({Invariant(rows)}) {list} FROM {table}) p";

    public override string? Sample(string table, string list, string kind, long rows, long? estimate) =>
        kind == "table" ? $"(SELECT TOP ({Invariant(rows)}) {list} FROM {table} TABLESAMPLE ({Invariant(rows)} ROWS) REPEATABLE ({Seed})) p" : null;

    /// <summary><c>TOP (n)</c> after the query's leading <c>SELECT</c> (2026-11-15, #91): not OFFSET/FETCH, which needs 2012.</summary>
    protected override string Limit(string query, int rows)
    {
        ArgumentNullException.ThrowIfNull(query);
        const string Select = "SELECT ";
        if (!query.StartsWith(Select, StringComparison.Ordinal))
        {
            throw new ArgumentException("A limited query starts with SELECT.", nameof(query));
        }

        return Select + "TOP (" + Invariant(rows) + ") " + query[Select.Length..];
    }
}
