using System.Globalization;
using NeonSidekick.Sql;

namespace NeonSidekick.Sqlite;

/// <summary>
/// <c>sqlite_profile</c>'s SQL (2026-11-14, the database-tools plan's #14), beside <see cref="SqliteCatalogQueries"/>: double quotes,
/// <c>LIMIT</c>, no sample and no row estimate (a file is counted first, bounded). Types are dynamic, so every column takes every measure.
/// </summary>
public sealed class SqliteProfile : ProfileDialect
{
    /// <summary>The columns of <c>@name</c> (name, declared type), generated ones included, a virtual table's hidden ones left out.</summary>
    public const string Catalog = "SELECT name, type FROM pragma_table_xinfo(@name) WHERE hidden <> 1 ORDER BY cid";

    public static readonly SqliteProfile Instance = new();

    public override string Quote(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    public override string FirstRows(string table, long rows) => $"(SELECT * FROM {table} LIMIT {Invariant(rows)}) p";

    protected override string Limit(string query, int rows) => query + " LIMIT " + Invariant(rows);

    private static string Invariant(long n) => n.ToString(CultureInfo.InvariantCulture);
}
