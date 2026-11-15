using System.Globalization;
using NeonSidekick.Sql;

namespace NeonSidekick.MySql;

/// <summary>
/// <c>mysql_profile</c>'s SQL (2026-11-14, the database-tools plan's #14), beside <see cref="MySqlCatalogQueries"/>: backticks, <c>LIMIT</c>,
/// and no sample (MySQL's <c>TABLESAMPLE</c> is MariaDB's alone, and neither reads it as a page sample): a big table's first rows.
/// </summary>
public sealed class MySqlProfile : ProfileDialect
{
    /// <summary>
    /// The columns of <c>@db</c>.<c>@name</c> (name, the column type as declared, the data type, the character length), then the table's
    /// row estimate (InnoDB's, approximate; null for a view). Invisible columns are left out (#17; <c>EXTRA</c> may read
    /// <c>VIRTUAL GENERATED INVISIBLE</c>, so a match, not an equality); the schema lists only columns the account holds a privilege on.
    /// </summary>
    public static readonly IReadOnlyList<string> Catalog =
    [
        "SELECT COLUMN_NAME, COLUMN_TYPE, DATA_TYPE, CHARACTER_MAXIMUM_LENGTH FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = @db AND TABLE_NAME = @name " +
        "AND COALESCE(EXTRA, '') NOT LIKE '%INVISIBLE%' ORDER BY ORDINAL_POSITION",
        "SELECT TABLE_ROWS FROM information_schema.TABLES WHERE TABLE_SCHEMA = @db AND TABLE_NAME = @name AND TABLE_TYPE = 'BASE TABLE'",
    ];

    public static readonly MySqlProfile Instance = new();

    private static readonly HashSet<string> Ranged = new(StringComparer.OrdinalIgnoreCase)
    {
        "tinyint", "smallint", "mediumint", "int", "integer", "bigint", "decimal", "numeric", "float", "double", "real", "year",
        "date", "datetime", "timestamp", "time",
    };

    private static readonly HashSet<string> Grouped = new(StringComparer.OrdinalIgnoreCase) { "bit", "enum", "set", "binary", "varbinary", "uuid", "inet4", "inet6" };

    /// <summary>What a column of <paramref name="type"/> takes; <paramref name="length"/> is a text's longest, in characters.</summary>
    public static ProfileMeasure Measure(string type, long? length)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (Ranged.Contains(type))
        {
            return ProfileMeasure.Full;
        }

        if (type.Equals("char", StringComparison.OrdinalIgnoreCase) || type.Equals("varchar", StringComparison.OrdinalIgnoreCase))
        {
            return length is > 0 and <= 4_000 ? ProfileMeasure.Full : ProfileMeasure.NullsOnly;
        }

        return Grouped.Contains(type) && (length is null or <= 4_000) ? ProfileMeasure.Distinct : ProfileMeasure.NullsOnly;
    }

    public override string Quote(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return "`" + name.Replace("`", "``", StringComparison.Ordinal) + "`";
    }

    /// <summary>Backticks as well as double quotes (2026-11-15, #51): a backticked name is matched as written.</summary>
    public override (string Name, bool Quoted) Unquote(string written)
    {
        ArgumentNullException.ThrowIfNull(written);
        string name = written.Trim();
        return name.Length > 1 && name[0] == '`' && name[^1] == '`'
            ? (name[1..^1].Replace("``", "`", StringComparison.Ordinal), true)
            : base.Unquote(name);
    }

    public override string FirstRows(string table, string list, long rows) => $"(SELECT {list} FROM {table} LIMIT {Invariant(rows)}) p";

    protected override string Limit(string query, int rows) => query + " LIMIT " + Invariant(rows);
}
