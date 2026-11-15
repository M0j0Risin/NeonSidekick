using System.Globalization;
using NeonSidekick.Sql;

namespace NeonSidekick.Sqlite;

/// <summary>
/// <c>sqlite_profile</c>'s SQL (2026-11-14, the database-tools plan's #14), beside <see cref="SqliteCatalogQueries"/>: double quotes,
/// <c>LIMIT</c>, no sample and no row estimate (a file is read as its first rows, one past the cap). Types are dynamic, so a column takes every
/// measure, except one declared a BLOB (#125): the nulls alone, not COUNT(DISTINCT) and a range over images.
/// </summary>
public sealed class SqliteProfile : ProfileDialect
{
    /// <summary>The columns of <c>@name</c> (name, declared type), generated ones included, a virtual table's hidden ones left out.</summary>
    public const string Catalog = "SELECT name, type FROM pragma_table_xinfo(@name) WHERE hidden <> 1 ORDER BY cid";

    public static readonly SqliteProfile Instance = new();

    /// <summary>What a column declared <paramref name="declared"/> takes: the nulls alone for a BLOB, else every measure.</summary>
    public static ProfileMeasure Measure(string declared)
    {
        ArgumentNullException.ThrowIfNull(declared);
        return declared.Contains("BLOB", StringComparison.OrdinalIgnoreCase) ? ProfileMeasure.NullsOnly : ProfileMeasure.Full;
    }

    public override string Quote(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    public override string FirstRows(string table, string list, long rows) => $"(SELECT {list} FROM {table} LIMIT {Invariant(rows)}) p";

    protected override string Limit(string query, int rows) => query + " LIMIT " + Invariant(rows);
}
