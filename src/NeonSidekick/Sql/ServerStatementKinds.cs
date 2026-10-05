namespace NeonSidekick.Sql;

/// <summary>What a statement a server family's <c>_execute</c> tool runs does (<see cref="ServerStatementKinds"/>).</summary>
public enum ServerStatementKind
{
    /// <summary>INSERT, UPDATE, MERGE without a delete, REPLACE (MySQL): rows added or changed.</summary>
    Data,

    /// <summary>DELETE, TRUNCATE, a MERGE that deletes: rows removed.</summary>
    Delete,

    /// <summary>CREATE of a table, index, view, sequence or type (a routine or trigger is code: <see cref="Procedures"/>).</summary>
    Create,

    /// <summary>ALTER of those, RENAME, COMMENT ON.</summary>
    Alter,

    /// <summary>DROP of those, and of routines and triggers.</summary>
    Drop,

    /// <summary>Statistics and housekeeping: UPDATE STATISTICS, ANALYZE, VACUUM, OPTIMIZE TABLE and their kin.</summary>
    Upkeep,

    /// <summary>
    /// Code: EXEC/CALL of a stored procedure, an anonymous block (Oracle's BEGIN … END, PostgreSQL's DO), and making or changing a
    /// function, procedure, package, trigger or rule (its body runs later, inside statements of any kind). Its effects cannot be read
    /// from the call, so it is off by default (the user's call).
    /// </summary>
    Procedures,

    /// <summary>SELECT and its kin: reads, which the family's <c>_query</c> tool is the tool for; run without asking, on the read-only path.</summary>
    Read,
}

/// <summary>
/// The settings <c>SQL statements allowed</c>, <c>Oracle statements allowed</c>, <c>MySQL statements allowed</c> and <c>PostgreSQL
/// statements allowed</c> (2026-10-05, the user's ask: SQLite's checklist mirrored): which kinds of statement the family's
/// <c>_execute</c> tool may run under its mode <c>read-write</c>, a checklist of eight. Changing data, creating and reading by default,
/// SQLite's safe three (none can lose data); deleting, changing structure, dropping, upkeep and procedures and triggers (the user's call:
/// code's effects cannot be classified, so it is off in a fresh profile) are the user's to tick. Saved by the words of
/// <see cref="Names"/>; <see cref="Resolve(IReadOnlyList{string})"/> is the one place they become kinds (a null list is the default,
/// a word it does not know is passed over). Each family's write gate tells a statement's kind, and its <c>Statements</c> names what
/// each kind covers in its dialect (<see cref="ServerWriteFamily.Statements"/>).
/// </summary>
public static class ServerStatementKinds
{
    /// <summary>The kinds' saved words, in menu order (the order of <see cref="ServerStatementKind"/>).</summary>
    public static readonly string[] Names = ["data", "delete", "create", "alter", "drop", "upkeep", "procedures", "read"];

    /// <summary>A fresh profile's list: changing data, creating, reading.</summary>
    public static List<string> Default() => ["data", "create", "read"];

    public static string NameOf(ServerStatementKind kind) => Names[(int)kind];

    /// <summary>A kind in words, as the menu, the refusals and the tools' descriptions name it. Pinned.</summary>
    public static string Title(ServerStatementKind kind) => kind switch
    {
        ServerStatementKind.Data => "changing data",
        ServerStatementKind.Delete => "deleting",
        ServerStatementKind.Create => "creating",
        ServerStatementKind.Alter => "changing structure",
        ServerStatementKind.Drop => "dropping",
        ServerStatementKind.Upkeep => "upkeep",
        ServerStatementKind.Procedures => "procedures and triggers",
        _ => "reading",
    };

    /// <summary>The kinds <paramref name="saved"/> allows, in menu order: null is <see cref="Default"/>; words matched case-blind, unknown ones passed over.</summary>
    public static IReadOnlyList<ServerStatementKind> Resolve(IReadOnlyList<string>? saved)
    {
        var words = (saved ?? Default()).Select(w => w?.Trim() ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Enum.GetValues<ServerStatementKind>().Where(k => words.Contains(NameOf(k))).ToList();
    }

    /// <summary>Each kind in words with its statements in <paramref name="family"/>'s dialect: <c>changing data (INSERT, UPDATE, MERGE); creating (…)</c>. Pinned.</summary>
    public static string Describe(IReadOnlyList<ServerStatementKind> kinds, ServerWriteFamily family)
    {
        ArgumentNullException.ThrowIfNull(kinds);
        ArgumentNullException.ThrowIfNull(family);
        return string.Join("; ", kinds.Select(k => Title(k) + " (" + family.Statements(k) + ")"));
    }
}
