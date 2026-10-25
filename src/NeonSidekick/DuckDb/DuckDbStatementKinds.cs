using NeonSidekick.Settings;

namespace NeonSidekick.DuckDb;

/// <summary>What a statement <c>duckdb_execute</c> runs does (<see cref="DuckDbStatementKinds"/>).</summary>
public enum DuckDbStatementKind
{
    /// <summary>INSERT, UPDATE, MERGE (a WITH may lead them; RETURNING included).</summary>
    Data,

    /// <summary>DELETE, TRUNCATE, and every insert that replaces a row (<c>INSERT OR REPLACE</c>, <c>ON CONFLICT … DO UPDATE</c>): off by default.</summary>
    Delete,

    /// <summary>CREATE TABLE, VIEW, INDEX, TYPE, SEQUENCE, MACRO, SCHEMA.</summary>
    Create,

    /// <summary>ALTER TABLE, ALTER VIEW, ALTER SEQUENCE.</summary>
    Alter,

    /// <summary>DROP of any object, and <c>CREATE OR REPLACE</c>, which drops what it replaces.</summary>
    Drop,

    /// <summary>CHECKPOINT, VACUUM, ANALYZE.</summary>
    Upkeep,

    /// <summary>PRAGMA (a setting one meets the locked configuration and is refused by DuckDB).</summary>
    Pragma,

    /// <summary>SELECT, WITH, VALUES, FROM, TABLE, DESCRIBE, SHOW, SUMMARIZE, PIVOT, UNPIVOT, EXPLAIN: reads, which <c>duckdb_query</c> is the tool for.</summary>
    Read,
}

/// <summary>
/// The setting <c>DuckDB statements allowed</c> (2026-10-25), <c>SQLite statements allowed</c>'s twin: which kinds of statement
/// <c>duckdb_execute</c> may run under <c>DuckDB mode</c> <c>read-write</c>, a checklist of eight, changing data, creating and reading by
/// default. Saved by the words of <see cref="Names"/>; <see cref="Resolve(IReadOnlyList{string}?)"/> is the one place they become kinds (a
/// null list is the default, a word it does not know is passed over). <see cref="DuckDbWriteGate"/> tells a statement's kinds.
/// </summary>
public static class DuckDbStatementKinds
{
    /// <summary>The kinds' saved words, in menu order (the order of <see cref="DuckDbStatementKind"/>).</summary>
    public static readonly string[] Names = ["data", "delete", "create", "alter", "drop", "upkeep", "pragma", "read"];

    /// <summary>A fresh profile's list: changing data, creating, reading.</summary>
    public static List<string> Default() => ["data", "create", "read"];

    public static string NameOf(DuckDbStatementKind kind) => Names[(int)kind];

    /// <summary>A kind in words, as the menu, the refusals and the tool's description name it. Pinned.</summary>
    public static string Title(DuckDbStatementKind kind) => kind switch
    {
        DuckDbStatementKind.Data => "changing data",
        DuckDbStatementKind.Delete => "deleting",
        DuckDbStatementKind.Create => "creating",
        DuckDbStatementKind.Alter => "changing structure",
        DuckDbStatementKind.Drop => "dropping",
        DuckDbStatementKind.Upkeep => "upkeep",
        DuckDbStatementKind.Pragma => "settings",
        _ => "reading",
    };

    /// <summary>The statements a kind covers. Pinned.</summary>
    public static string Statements(DuckDbStatementKind kind) => kind switch
    {
        DuckDbStatementKind.Data => "INSERT, UPDATE, MERGE",
        DuckDbStatementKind.Delete => "DELETE, TRUNCATE, INSERT OR REPLACE, ON CONFLICT DO UPDATE",
        DuckDbStatementKind.Create => "CREATE TABLE, VIEW, INDEX, TYPE, SEQUENCE, MACRO, SCHEMA",
        DuckDbStatementKind.Alter => "ALTER TABLE, VIEW, SEQUENCE",
        DuckDbStatementKind.Drop => "DROP, CREATE OR REPLACE",
        DuckDbStatementKind.Upkeep => "CHECKPOINT, VACUUM, ANALYZE",
        DuckDbStatementKind.Pragma => "PRAGMA",
        _ => "SELECT, DESCRIBE, SUMMARIZE, PIVOT, EXPLAIN",
    };

    /// <summary>The kinds <paramref name="saved"/> allows, in menu order: null is <see cref="Default"/>; words matched case-blind, unknown ones passed over.</summary>
    public static IReadOnlyList<DuckDbStatementKind> Resolve(IReadOnlyList<string>? saved)
    {
        var words = (saved ?? Default()).Select(w => w?.Trim() ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Enum.GetValues<DuckDbStatementKind>().Where(k => words.Contains(NameOf(k))).ToList();
    }

    /// <summary>The kinds the effective settings allow.</summary>
    public static IReadOnlyList<DuckDbStatementKind> Resolve(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return Resolve(effective.DuckDbStatementsAllowed);
    }

    /// <summary>Each kind in words with its statements: <c>changing data (INSERT, UPDATE, MERGE); creating (…)</c>. Pinned.</summary>
    public static string Describe(IReadOnlyList<DuckDbStatementKind> kinds)
    {
        ArgumentNullException.ThrowIfNull(kinds);
        return string.Join("; ", kinds.Select(k => Title(k) + " (" + Statements(k) + ")"));
    }
}
