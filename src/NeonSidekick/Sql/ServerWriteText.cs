using System.Globalization;
using System.Text;

namespace NeonSidekick.Sql;

/// <summary>
/// One server family as its write path names it (2026-10-05): <paramref name="Title"/> as its settings and tab say it (<c>SQL</c>,
/// <c>Oracle</c>, <c>MySQL</c>, <c>PostgreSQL</c>), <paramref name="Engine"/> as a person says it (<c>SQL Server</c>), the
/// <c>_execute</c> tool's name, the connections file, <paramref name="Place"/> (what a call works in: <c>database</c>, or Oracle's
/// <c>schema</c>), the log category, the mode's <c>profile.json</c> key, and what each statement kind covers in its dialect.
/// </summary>
public sealed record ServerWriteFamily(
    string Title,
    string Engine,
    string ToolName,
    string FileName,
    string Place,
    string Category,
    string ModeKey,
    Func<ServerStatementKind, string> Statements)
{
    /// <summary>The mode setting's label: <c>PostgreSQL mode</c>.</summary>
    public string ModeSetting => Title + " mode";

    /// <summary>The checklist setting's label: <c>PostgreSQL statements allowed</c>.</summary>
    public string KindsSetting => Title + " statements allowed";

    /// <summary>Where the user changes either: <c>the PostgreSQL tab of /tools</c>.</summary>
    public string Tab => "the " + Title + " tab of /tools";
}

/// <summary>
/// The server families' write wording (2026-10-05), <c>SqliteText</c>'s <c>sqlite_execute</c> part shared by the four through
/// <see cref="ServerWriteFamily"/>: the write gates' refusals, the tool's answers, the allow pane and the audit line. Pure and pinned.
/// </summary>
public static class ServerWriteText
{
    // ─── the write gates ────────────────────────────────────────────────────────

    public const string NoStatement = "Error: give the statement to run in \"sql\"";

    public static string NotOneStatement(ServerWriteFamily family, int count) =>
        $"Error: the SQL is {Invariant(count)} statements; {family.ToolName} runs exactly one per call — send the next one in the next call";

    public static string Forbidden(ServerWriteFamily family, string what, string why) => $"Error: the SQL uses {what}, which {family.ToolName} refuses: {why}";

    public static string UnknownStatement(ServerWriteFamily family, string word) => $"Error: the SQL starts with {word}, which is no statement {family.ToolName} runs";

    /// <summary>A statement of a kind the user has not ticked: its kind, then what is allowed. Pinned.</summary>
    public static string KindNotAllowed(ServerWriteFamily family, ServerStatementKind kind, IReadOnlyList<ServerStatementKind> allowed)
    {
        ArgumentNullException.ThrowIfNull(family);
        ArgumentNullException.ThrowIfNull(allowed);
        string may = allowed.Count == 0 ? "nothing" : string.Join(", ", allowed.Select(ServerStatementKinds.Title));
        return $"Error: the SQL is {ServerStatementKinds.Title(kind)} ({family.Statements(kind)}), which the user has not allowed; {family.ToolName} may run {may} — the user ticks more in {family.KindsSetting} on {family.Tab}";
    }

    public static string NoKindsAllowed(ServerWriteFamily family) => $"Error: the user has allowed no kind of statement for {family.ToolName} ({family.KindsSetting} on {family.Tab})";

    /// <summary>Why a transaction's own words are refused.</summary>
    public const string OwnTransaction = "each call is a transaction of its own, committed when its statement succeeds";

    /// <summary>Why the account, role and permission statements are refused.</summary>
    public const string Accounts = "accounts, roles and permissions are the user's to change";

    /// <summary>Why a statement over a whole database or the server is refused.</summary>
    public const string ServerWide = "it changes the server or a whole database, not one database's objects";

    /// <summary>Why a statement that reaches files, programs or other servers is refused.</summary>
    public const string Outside = "it reaches files, programs or other servers";

    /// <summary>Why dynamic SQL is refused.</summary>
    public const string Dynamic = "it runs SQL built as text, which the gate cannot read";

    /// <summary>Why a session's settings are refused.</summary>
    public const string Session = "it changes the session's settings, which the app keeps as they are";

    /// <summary>Why an explicit lock is refused.</summary>
    public const string Locks = "it locks what others use, and each call ends its own transaction";

    /// <summary>Why a statement the family's read gate denies (a sleep, a lock, a file) is refused here too.</summary>
    public const string Denied = "it reaches outside the database, waits, or changes something no statement kind covers";

    // ─── the tool ───────────────────────────────────────────────────────────────

    public static string ReadOnlyMode(ServerWriteFamily family) =>
        $"Error: {family.ModeSetting} is read-only, so nothing may change a {family.Engine} database; the user switches it to read-write on {family.Tab}";

    /// <summary>A connection whose entry does not say <c>"access": "readwrite"</c>. Pinned.</summary>
    public static string ReadOnlyConnection(ServerWriteFamily family, string name) =>
        $"Error: connection '{name}' is read-only (\"access\": \"read\" in {family.FileName}); the user makes it readwrite to allow changes";

    /// <summary>No connection the tool may change: the mode is read-write but every connection reads only.</summary>
    public static string NoReadWriteConnection(ServerWriteFamily family) =>
        $"Error: no {family.Engine} connection allows changes; the user sets \"access\": \"readwrite\" on one in {family.FileName}";

    public static string NoPane(ServerWriteFamily family) => $"Error: {family.ToolName} needs the user to allow each change on a pane, and there is none here";

    public const string Declined = "The user declined the change; nothing was run. Do not run it again unless the user asks for it.";

    /// <summary>The allow pane's title. Pinned.</summary>
    public static string AllowTitle(ServerWriteFamily family) => $"Change a {family.Engine} database?";

    /// <summary>The allow pane's caption: where the change would land, and the statement. Pinned.</summary>
    public static string AllowCaption(ServerWriteFamily family, string connection, string place, string sql)
    {
        ArgumentNullException.ThrowIfNull(family);
        ArgumentNullException.ThrowIfNull(sql);
        string where = place.Length == 0 ? connection : connection + "/" + place;
        return $"The model wants to change {where} ({family.Engine}, {family.Place} {(place.Length == 0 ? "the connection's own" : place)}):\n" + Clip(sql.Trim(), 1200);
    }

    /// <summary>The audit line of a change (every one run): where, the rows changed (when the server counts them), the statement. Pinned.</summary>
    public static string AuditLogLine(string where, int? changes, string sql) =>
        $"{where}: {(changes is { } n ? SqlText.Count(n, "row") + " changed" : "ran")} by {sql}";

    /// <summary>
    /// The tool's answer: <c>Changed N rows in connection/database (T ms)</c> (<c>Ran it in …</c> for a statement the server counts no
    /// rows for, DDL), then the rows an OUTPUT or RETURNING gave back as a table.
    /// </summary>
    public static string Executed(SqlRun run, int maxRows, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(run);
        string where = run.Database.Length == 0 ? run.Connection : run.Connection + "/" + run.Database;
        string head = (run.Changes is { } n ? $"Changed {SqlText.Count(n, "row")} in {where}" : $"Ran it in {where}") + $" ({Invariant((long)run.Elapsed.TotalMilliseconds)} ms)";
        if (run.Grids is not [{ Columns.Count: > 0 } grid, ..])
        {
            return head;
        }

        string table = SqlText.Table(grid, maxChars, out int shown);
        var header = new StringBuilder(head);
        header.Append("; it returned ").Append(SqlText.Count(grid.Rows.Count, "row")).Append(grid.More ? "+" : "");
        if (grid.More)
        {
            header.Append(" — the first ").Append(Invariant(maxRows)).Append(" shown");
        }

        if (shown < grid.Rows.Count)
        {
            header.Append(" — ").Append(Invariant(shown)).Append(" fit the text cap");
        }

        return header + "\n\n" + table;
    }

    /// <summary>
    /// The tool's description for the kinds the user ticks (read at every turn). Pinned. <paramref name="dialect"/> is the family's
    /// one-line reminder (<c>PostgreSQL SQL; bind values as @name through params</c>).
    /// </summary>
    public static string Describe(ServerWriteFamily family, IReadOnlyList<ServerStatementKind> kinds, string readTool, string dialect)
    {
        ArgumentNullException.ThrowIfNull(family);
        ArgumentNullException.ThrowIfNull(kinds);
        return $"Runs one statement that changes a {family.Engine} database, on a connection the user has opened to changes. The user allows only these kinds: " +
            ServerStatementKinds.Describe(kinds, family) + ". " +
            "The user allows each change first; a change is permanent once it runs. " +
            (kinds.Contains(ServerStatementKind.Read) ? "A read runs without asking, read-only. " : "") +
            dialect + " For reading, use " + readTool + ".";
    }

    /// <summary>A connection's access as the connections listings say it: empty for read, <c>, read-write</c> or <c>, readwrite (… mode is read-only)</c>. Pinned.</summary>
    public static string AccessNote(ServerWriteFamily family, bool readWrite, bool modeReadWrite) =>
        !readWrite ? "" : modeReadWrite ? ", read-write" : $", readwrite (but {family.ModeSetting} is read-only)";

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    private static string Invariant(long value) => value.ToString(CultureInfo.InvariantCulture);
}
