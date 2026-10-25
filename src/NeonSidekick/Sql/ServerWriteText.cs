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

    /// <summary>
    /// Where the user changes either: <c>/tools › Databases › PostgreSQL</c> since 2026-10-09, when the five database tabs became menus
    /// on one Database tab (the user's call; <c>the PostgreSQL tab of /tools</c> until then). The menu is the strip's title, which is
    /// the family's own since 2026-10-10 (PostgreSQL's menu was <c>Postgres</c> until then).
    /// </summary>
    public string Tab => "/tools › Databases › " + Title;
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

    public static string UnknownStatement(ServerWriteFamily family, string word) => $"Error: the SQL starts with {word}, which is not a statement {family.ToolName} runs";

    /// <summary>A routine's body the gate cannot read for sure (the review, 2026-10-05: a name spelled BEGIN or END could hide a second statement). Pinned.</summary>
    public static string UnclearBody(ServerWriteFamily family) =>
        $"Error: {family.ToolName} cannot tell where the routine's body ends; write the body as BEGIN … END and quote any name spelled like a block word (BEGIN, END, a label)";

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

    /// <summary>Why Oracle's <c>RETURNING … INTO</c> outside PL/SQL is refused.</summary>
    public const string ReturnsInto = "its INTO needs out binds the tool does not read back; select the rows after the change instead";

    /// <summary>Why a statement the family's read gate denies (a sleep, a lock, a file) is refused here too.</summary>
    public const string Denied = "it reaches outside the database, waits, or changes something no statement kind covers";

    /// <summary>
    /// Why a routine whose body the gate cannot read is refused whatever is ticked (2026-10-24, the database review's DB3: another language,
    /// a native library, a CLR assembly, a call spec, JavaScript), as a <c>CREATE LIBRARY</c> already was.
    /// </summary>
    public const string ForeignCode = "its body is code the gate cannot read (another language, a native library or an assembly); write it in SQL";

    /// <summary>Why a body the gate cannot lex is refused (2026-10-24, DB3: an <c>E'…'</c> string, or one that does not lex as SQL).</summary>
    public const string UnreadableBody = "the gate cannot read the body; write it as '…' or $$…$$, without E'…' escapes";

    /// <summary>Why a change of who a routine runs as is refused (DB3: <c>WITH EXECUTE AS</c>, <c>SQL SECURITY DEFINER</c>, <c>SECURITY DEFINER</c>).</summary>
    public const string RunsAs = "who a routine runs as is the user's to change; leave the clause out";


    // ─── the settings ───────────────────────────────────────────────────────────
    // (2026-10-24, the database review's C13: these were inline in DatabaseWriteModes, out of the pins' reach.)

    /// <summary>The menu hint next to <c>read-only</c>. Pinned.</summary>
    public static string ReadOnlyModeHint(ServerWriteFamily family) => $"the {family.Title} tools only read";

    /// <summary>The menu hint next to <c>read-write</c>. Pinned.</summary>
    public static string ReadWriteModeHint(ServerWriteFamily family) => $"{family.ToolName} may change a readwrite connection's database, each change asking you first";

    /// <summary>The warning for a saved mode that is neither word.</summary>
    public static string BadModeLogLine(ServerWriteFamily family, string? saved, IReadOnlyList<string> names, string fallback) =>
        $"{family.ModeKey}='{saved}' is not one of {string.Join(", ", names)}. Using {fallback}.";

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

    /// <summary>
    /// The allow pane's caption: where the change would land (the server too, since 2026-10-24, decision DB6), the statement, and the values its
    /// placeholders are bound to (B5: the pane showed <c>DELETE FROM t WHERE id = @id</c> and not which id). Pinned. The statement's hiding
    /// characters are escaped and a lone carriage return is a line break (2026-10-24, review F16: it went raw, a bidi override or a CR able to
    /// make the pane show another statement than the one that runs); past <see cref="CaptionChars"/> it is cut, and the pane's v shows it
    /// whole. (A statement that reached past the place was asked about every time from the review of 2026-10-05; since 2026-10-09 each
    /// family's boundary refuses it before the pane.)
    /// </summary>
    public static string AllowCaption(ServerWriteFamily family, string connection, string place, string sql, string where = "", IReadOnlyList<SqlParameterValue>? parameters = null)
    {
        ArgumentNullException.ThrowIfNull(family);
        ArgumentNullException.ThrowIfNull(sql);
        string at = place.Length == 0 ? connection : connection + "/" + place;
        string server = where.Length == 0 ? "" : " on " + Files.NameScrub.Display(where);
        return $"The model wants to change {at} ({family.Engine}{server}, {family.Place} {(place.Length == 0 ? "the connection's own" : place)}):\n"
            + CaptionStatement(sql) + Bound(parameters);
    }

    /// <summary>How much of a statement the caption shows; the pane's v shows the whole.</summary>
    public const int CaptionChars = 1200;

    /// <summary>A statement as a caption shows it: its line ends as line breaks, its hiding characters escaped, cut at <see cref="CaptionChars"/>.</summary>
    public static string CaptionStatement(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        string text = Files.NameScrub.Display(Lines(sql).Trim(), keepLineBreaks: true);
        return text.Length <= CaptionChars ? text : Files.TextCut.Safe(text, CaptionChars) + "… (v shows the whole statement)";
    }

    /// <summary><paramref name="sql"/> with every line end — CRLF, a lone CR, a lone LF — one line break, so a pane splits it as the server reads it.</summary>
    public static string Lines(string sql) => (sql ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    /// <summary>The bound values under the statement, one per line (<c>@id = 43659</c>), each cut and scrubbed; empty for none.</summary>
    public static string Bound(IReadOnlyList<SqlParameterValue>? parameters)
    {
        if (parameters is not { Count: > 0 })
        {
            return "";
        }

        var lines = parameters.Select(p => "  " + Files.NameScrub.Display(p.Name) + " = " + Files.NameScrub.Display(Files.TextCut.Safe(Shown(p.Value), 200)));
        return "\nwith " + string.Join("\n", lines);
    }

    private static string Shown(object? value) => value switch
    {
        null => "NULL",
        string s => "'" + s + "'",
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    /// <summary>The audit line of a change (every one run): where, the rows changed (when the server counts them), the statement. Pinned.</summary>
    public static string AuditLogLine(string where, int? changes, string sql) =>
        AuditLogLine(where, changes is { } n ? SqlText.Count(n, "row") + " changed" : "ran", sql);

    /// <summary>The audit line with its verb already worded (<see cref="DatabaseAudit.Word"/>): <c>where: failed by …</c>. Pinned.</summary>
    public static string AuditLogLine(string where, string word, string sql) => $"{where}: {word} by {sql}";

    /// <summary>
    /// The tool's answer: <c>Changed N rows in connection/database (T ms)</c> (<c>Ran it in …</c> for a statement the server counts no
    /// rows for, DDL), then the rows an OUTPUT or RETURNING gave back as a table.
    /// </summary>
    public static string Executed(SqlRun run, int maxRows, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(run);
        string where = run.Database.Length == 0 ? run.Connection : run.Connection + "/" + run.Database;
        string head = (run.Changes is { } n ? $"Changed {SqlText.Count(n, "row")} in {where}" : $"Ran it in {where}") + $" ({Invariant((long)run.Elapsed.TotalMilliseconds)} ms)";
        var grids = run.Grids.Where(g => g.Columns.Count > 0).ToList();
        if (grids.Count == 0)
        {
            return head;
        }

        if (grids.Count == 1)
        {
            return head + Returned(grids[0], maxRows, maxChars, "; it returned ");
        }

        // Every result set a procedure gave back, the text cap shared (2026-10-24, the database review's E11: only the first was shown, the
        // rest dropped without a word).
        var sb = new StringBuilder(head).Append("; it returned ").Append(Invariant(grids.Count)).Append(" result sets");
        int left = maxChars;
        for (int i = 0; i < grids.Count; i++)
        {
            if (left <= 0)
            {
                sb.Append("\n\n").Append(SetsLeftOut(grids.Count - i));
                break;
            }

            string part = Returned(grids[i], maxRows, left, $"\n\nSet {Invariant(i + 1)}: ");
            sb.Append(part);
            left -= part.Length;
        }

        return sb.ToString();
    }

    /// <summary>The sentence for result sets past the text cap. Pinned.</summary>
    public static string SetsLeftOut(int count) => $"(and {Invariant(count)} more result set{(count == 1 ? "" : "s")} past the text cap)";

    private static string Returned(SqlGrid grid, int maxRows, int maxChars, string lead)
    {
        string table = SqlText.Table(grid, maxChars, out int shown);
        var header = new StringBuilder(lead);
        header.Append(SqlText.Count(grid.Rows.Count, "row")).Append(grid.More ? "+" : "");
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
    /// one-line reminder (<c>PostgreSQL SQL; bind values as @name through params</c>). The pointer to <paramref name="readTool"/> only
    /// while it is offered (2026-10-24, the database review's P6: the description sent the model to a tool the user had switched off).
    /// </summary>
    public static string Describe(ServerWriteFamily family, IReadOnlyList<ServerStatementKind> kinds, string readTool, string dialect, bool readOffered = true)
    {
        ArgumentNullException.ThrowIfNull(family);
        ArgumentNullException.ThrowIfNull(kinds);
        return $"Runs one statement that changes a {family.Engine} database, on a connection the user has opened to changes. The user allows only these kinds: " +
            ServerStatementKinds.Describe(kinds, family) + ". " +
            "The user allows each change first; a change is permanent once it runs. " +
            (kinds.Contains(ServerStatementKind.Read) ? "A read runs without asking, read-only. " : "") +
            dialect + (readOffered ? " For reading, use " + readTool + "." : "");
    }

    /// <summary>A connection's access as the connections listings say it: empty for read, <c>, read-write</c> or <c>, readwrite (… mode is read-only)</c>. Pinned.</summary>
    public static string AccessNote(ServerWriteFamily family, bool readWrite, bool modeReadWrite) =>
        !readWrite ? "" : modeReadWrite ? ", read-write" : $", readwrite (but {family.ModeSetting} is read-only)";

    private static string Invariant(long value) => value.ToString(CultureInfo.InvariantCulture);
}
