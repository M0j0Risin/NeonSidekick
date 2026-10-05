using NeonSidekick.Settings;

namespace NeonSidekick.Sqlite;

/// <summary>What a statement <c>sqlite_execute</c> runs does (<see cref="SqliteStatementKinds"/>).</summary>
public enum SqliteStatementKind
{
    /// <summary>INSERT, UPDATE, REPLACE (a WITH may lead them; RETURNING, upserts and OR … included).</summary>
    Data,

    /// <summary>DELETE (a WITH may lead it; RETURNING included): its own kind since later on 2026-10-05, the user's call, off by default.</summary>
    Delete,

    /// <summary>CREATE TABLE, INDEX, VIEW, TRIGGER, VIRTUAL TABLE.</summary>
    Create,

    /// <summary>ALTER TABLE: rename a table or a column, add or drop a column.</summary>
    Alter,

    /// <summary>DROP TABLE, INDEX, VIEW, TRIGGER.</summary>
    Drop,

    /// <summary>VACUUM, REINDEX, ANALYZE.</summary>
    Upkeep,

    /// <summary>PRAGMA (all but <c>writable_schema</c>, which the gate refuses whatever is ticked).</summary>
    Pragma,

    /// <summary>SELECT, VALUES, EXPLAIN: reads, which <c>sqlite_query</c> is the tool for.</summary>
    Read,
}

/// <summary>
/// The setting <c>SQLite statements allowed</c> (2026-10-05, the user's ask): which kinds of statement <c>sqlite_execute</c> may
/// run under <c>SQLite mode</c> <c>read-write</c>, a checklist of eight, changing data, creating and reading by default (reading and creating
/// added to the default the same day, the user's call: neither can lose data, and <c>create</c> needs creating). DELETE was
/// changing data's until later that day, when it became deleting, a kind of its own and off by default (the user's call; a saved
/// <c>data</c> no longer covers it, no migration). Saved by the
/// words of <see cref="Names"/>; <see cref="Resolve"/> is the one place they become kinds (a null list is the default, a word it
/// does not know is passed over). <see cref="SqliteWriteGate"/> tells a statement's kind (<see cref="SqliteWriteGate.Classify"/>).
/// </summary>
public static class SqliteStatementKinds
{
    /// <summary>The kinds' saved words, in menu order (the order of <see cref="SqliteStatementKind"/>).</summary>
    public static readonly string[] Names = ["data", "delete", "create", "alter", "drop", "upkeep", "pragma", "read"];

    /// <summary>A fresh profile's list: changing data, creating, reading.</summary>
    public static List<string> Default() => ["data", "create", "read"];

    public static string NameOf(SqliteStatementKind kind) => Names[(int)kind];

    /// <summary>A kind in words, as the menu, the refusals and the tool's description name it. Pinned.</summary>
    public static string Title(SqliteStatementKind kind) => kind switch
    {
        SqliteStatementKind.Data => "changing data",
        SqliteStatementKind.Delete => "deleting",
        SqliteStatementKind.Create => "creating",
        SqliteStatementKind.Alter => "changing structure",
        SqliteStatementKind.Drop => "dropping",
        SqliteStatementKind.Upkeep => "upkeep",
        SqliteStatementKind.Pragma => "settings",
        _ => "reading",
    };

    /// <summary>The statements a kind covers. Pinned.</summary>
    public static string Statements(SqliteStatementKind kind) => kind switch
    {
        SqliteStatementKind.Data => "INSERT, UPDATE, REPLACE",
        SqliteStatementKind.Delete => "DELETE",
        SqliteStatementKind.Create => "CREATE TABLE, INDEX, VIEW, TRIGGER, VIRTUAL TABLE",
        SqliteStatementKind.Alter => "ALTER TABLE",
        SqliteStatementKind.Drop => "DROP TABLE, INDEX, VIEW, TRIGGER",
        SqliteStatementKind.Upkeep => "VACUUM, REINDEX, ANALYZE",
        SqliteStatementKind.Pragma => "PRAGMA",
        _ => "SELECT, VALUES, EXPLAIN",
    };

    /// <summary>The kinds <paramref name="saved"/> allows, in menu order: null is <see cref="Default"/>; words matched case-blind, unknown ones passed over.</summary>
    public static IReadOnlyList<SqliteStatementKind> Resolve(IReadOnlyList<string>? saved)
    {
        var words = (saved ?? Default()).Select(w => w?.Trim() ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Enum.GetValues<SqliteStatementKind>().Where(k => words.Contains(NameOf(k))).ToList();
    }

    /// <summary>The kinds the effective settings allow.</summary>
    public static IReadOnlyList<SqliteStatementKind> Resolve(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return Resolve(effective.SqliteStatementsAllowed);
    }

    /// <summary>Each kind in words with its statements: <c>changing data (INSERT, UPDATE, REPLACE); creating (…)</c>. Pinned.</summary>
    public static string Describe(IReadOnlyList<SqliteStatementKind> kinds)
    {
        ArgumentNullException.ThrowIfNull(kinds);
        return string.Join("; ", kinds.Select(k => Title(k) + " (" + Statements(k) + ")"));
    }
}
