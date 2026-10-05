using System.Globalization;
using System.Text;
using NeonSidekick.Sql;

namespace NeonSidekick.Sqlite;

/// <summary>
/// The SQLite tools' wording and formatting (2026-10-04), <c>MySqlText</c>'s part for SQLite, pure and pinned: the <c>sqlite.json</c>
/// problems, the gate's refusals, the outcomes and the results. The engine-neutral pieces — a cell's text, a Markdown table, a
/// count — are <see cref="SqlText"/>'s. Every error starts <c>Error:</c>. Invariant culture throughout.
/// </summary>
public static class SqliteText
{
    // ─── sqlite.json ────────────────────────────────────────────────────────────

    public const string NoPath = "no \"path\" is given";

    /// <summary>One database as <c>sqlite_databases</c> lists it: the name, the file, the description. Pinned.</summary>
    public static string DatabaseLine(SqliteNamedDatabase database, bool isDefault)
    {
        ArgumentNullException.ThrowIfNull(database);
        string line = $"- {database.Name}{(isDefault ? " (default)" : "")}: {database.FullPath}";
        return string.IsNullOrWhiteSpace(database.Config.Description) ? line : line + " — " + database.Config.Description.Trim();
    }

    /// <summary>A database's note on the <c>%</c>-mention list: <c>SQLite ·</c>, the file, its description. Pinned.</summary>
    public static string MentionNote(SqliteNamedDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        string where = "SQLite · " + database.FullPath;
        return string.IsNullOrWhiteSpace(database.Config.Description) ? where : where + " — " + database.Config.Description.Trim();
    }

    /// <summary>The closing line of <c>sqlite_databases</c> while the profile hides some: how many, never which. Pinned.</summary>
    public static string HiddenDatabases(int count) =>
        count == 1
            ? "1 more database in sqlite.json is switched off for this profile (the SQLite tab of /tools)."
            : $"{Invariant(count)} more databases in sqlite.json are switched off for this profile (the SQLite tab of /tools).";

    public const string SandboxLine = "Any SQLite file in the working directory can be named by its path too (e.g. \"data/app.db\").";

    /// <summary><c>sqlite_databases</c>' whole answer: a count, one line each, the sandbox line when on, the problems.</summary>
    public static string Databases(SqliteCatalog catalog, string? defaultName, bool sandbox)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var sb = new StringBuilder();
        if (catalog.Databases.Count == 0)
        {
            sb.Append(sandbox ? "No named SQLite database." : NoDatabases);
        }
        else
        {
            var chosen = catalog.Named(defaultName) ?? catalog.Databases[0];
            sb.Append(SqlText.Count(catalog.Databases.Count, "SQLite database")).Append(" (every SQLite tool takes one by name in \"database\"; the default is used when it is left out):");
            foreach (var database in catalog.Databases)
            {
                sb.Append('\n').Append(DatabaseLine(database, ReferenceEquals(database, chosen)));
            }
        }

        if (sandbox)
        {
            sb.Append('\n').Append(SandboxLine);
        }

        foreach (var problem in catalog.Problems)
        {
            sb.Append("\nSkipped ").Append(problem.Source).Append(": ").Append(problem.Reason);
        }

        if (catalog.Hidden > 0)
        {
            sb.Append('\n').Append(HiddenDatabases(catalog.Hidden));
        }

        return sb.ToString();
    }

    // ─── the gate ───────────────────────────────────────────────────────────────

    public const string NoSql = "Error: give the SELECT to run in \"sql\"";
    public const string Positional = "Error: the SQL uses a ? placeholder; name it (@id, :id or $id) and pass its value in \"params\"";
    public static string Unterminated(string what, int line, int column) => $"Error: the SQL does not parse (line {Invariant(line)}, column {Invariant(column)}): a {what} never ends";
    public static string NotOneStatement(int count) => $"Error: the SQL is {Invariant(count)} statements; send exactly one SELECT per call (a WITH clause may lead it)";
    public static string NotASelect(string word) => $"Error: the SQL starts with {word}; the SQLite tools only read — send one SELECT (a WITH clause may lead it, or VALUES)";
    public static string Forbidden(string what) => $"Error: the SQL uses {what}, which the SQLite tools refuse (they only read this one file)";

    // ─── sqlite_execute's gate (2026-10-05) ─────────────────────────────────────

    public const string NoStatement = "Error: give the statement to run in \"sql\"";
    public static string WriteNotOneStatement(int count) => $"Error: the SQL is {Invariant(count)} statements; sqlite_execute runs exactly one per call — send the next one in the next call";
    public static string WriteForbidden(string what, string why) => $"Error: the SQL uses {what}, which sqlite_execute refuses: {why}";
    public static string UnknownStatement(string word) => $"Error: the SQL starts with {word}, which is no statement sqlite_execute runs";

    /// <summary>A statement of a kind the user has not ticked: its kind, then what is allowed. Pinned.</summary>
    public static string KindNotAllowed(SqliteStatementKind kind, IReadOnlyList<SqliteStatementKind> allowed)
    {
        ArgumentNullException.ThrowIfNull(allowed);
        string may = allowed.Count == 0 ? "nothing" : string.Join(", ", allowed.Select(SqliteStatementKinds.Title));
        return $"Error: the SQL is {SqliteStatementKinds.Title(kind)} ({SqliteStatementKinds.Statements(kind)}), which the user has not allowed; sqlite_execute may run {may} — the user ticks more in SQLite statements allowed on the SQLite tab of /tools";
    }

    public const string CreateNotAllowed = "Error: \"create\" makes a new database, and the user has not allowed creating (SQLite statements allowed on the SQLite tab of /tools)";
    public const string NoKindsAllowed = "Error: the user has allowed no kind of statement for sqlite_execute (SQLite statements allowed on the SQLite tab of /tools)";

    public const string OwnTransaction = "each call is a transaction of its own, committed when its statement succeeds";
    public const string AnotherFile = "it reaches a file other than this database";
    public const string Corrupts = "it can corrupt the database file";
    public const string OutsideCode = "it loads code or reads and writes other files";

    // ─── sqlite_execute (2026-10-05) ────────────────────────────────────────────

    public const string ReadOnlyMode = "Error: SQLite mode is read-only, so nothing may change a SQLite database; the user switches it to read-write on the SQLite tab of /tools";
    public const string NoPane = "Error: sqlite_execute needs the user to allow each change on a pane, and there is none here";
    public const string Declined = "The user declined the change; nothing was run. Do not run it again unless the user asks for it.";
    public const string CreateNeedsSandbox = "Error: a new database file can only be made in the working directory, and SQLite sandbox files is off";
    public const string CreateNeedsPath = "Error: \"create\" needs the new file's path in the working directory as \"database\", e.g. \"data/app.db\"";
    public static string BadExtension(string path) => $"Error: '{path}' is no database file name; a new database's file ends .db, .sqlite, .sqlite3 or .db3";
    public static string NoFolder(string path) => $"Error: the folder for '{path}' does not exist; make it first (create_directory)";

    /// <summary>The allow pane's title. Pinned.</summary>
    public const string AllowTitle = "Change a SQLite database?";

    /// <summary>The allow pane's caption: what would change (or be made), and the statement. Pinned.</summary>
    public static string AllowCaption(SqliteTarget target, string sql, bool creating)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(sql);
        string where = creating ? $"The model wants to create the new database {target.FullPath} and run:" : $"The model wants to change {target.Name} ({target.FullPath}):";
        return where + "\n" + Clip(sql.Trim(), 1200);
    }

    /// <summary>The audit line of a change (every one run): the database, the file, whether it was made, the rows changed, the statement. Pinned.</summary>
    public static string AuditLogLine(string database, string path, bool created, int changes, string sql) =>
        $"{database} ({path}){(created ? " created" : "")}: {SqlText.Count(changes, "row")} changed by {sql}";

    /// <summary>
    /// <c>sqlite_execute</c>'s answer: <c>Changed N rows in database (T ms)</c>, the file's making when it was made, then the rows
    /// a RETURNING or a PRAGMA gave back, as <see cref="Query"/> shows them.
    /// </summary>
    public static string Executed(SqlRun run, bool created, int maxRows, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(run);
        string head = (created ? $"Created {run.Connection}. " : "") + $"Changed {SqlText.Count(run.Changes ?? 0, "row")} in {run.Connection} ({Millis(run.Elapsed)})";
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

    // ─── arguments ──────────────────────────────────────────────────────────────

    public const string NoTable = "Error: give the table or view in \"table\"";
    public static string BadParamName(string name) => $"Error: '{name}' is no parameter name; give the name as the SQL writes it after its @, :, $ or #, e.g. \"id\" for :id";
    public static string BadParams(string raw) => $"Error: \"params\" must be one object of names and values, e.g. {{\"id\": 5}} for @id (got {Clip(raw, 200)})";
    public static string TableNotFound(string table, string database) => $"Error: no table or view '{table}' in {database}; sqlite_tables lists them";

    // ─── outcomes ───────────────────────────────────────────────────────────────

    public const string NoDatabases = "Error: no SQLite database is defined; the user adds one to sqlite.json (the SQLite tab of /tools)";
    public static string UnknownDatabase(string name, string names, bool sandbox) =>
        $"Error: no SQLite database is named '{name}'" + (names.Length > 0 ? $"; the databases are {names}" : "") + (sandbox ? ", and no file by that path is in the working directory" : "");
    public static string NoFile(string path) => $"no database file at {path}";
    public static string OutsideSandbox(string path) => $"'{path}' is outside the working directory";
    public static string ConnectFailed(string database, string detail) => $"Error: could not open {database}: {detail}";
    public static string Timeout(string database, string seconds) => $"Error: the query on {database} ran past {seconds} s and was stopped; narrow it (WHERE, LIMIT, fewer joins)";
    public static string Failed(string database, string detail) => $"Error: SQLite refused it ({database}): {detail}";
    public static string OpenFailedLogLine(string database, string detail) => $"{database} did not open: {detail}";
    public static string UnmakeFailedLogLine(string path, string detail) => $"{path}, made by a create whose statement failed, could not be removed: {detail}";

    /// <summary>The sentence for a run that did not return rows.</summary>
    public static string Error(SqlRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return run.Outcome switch
        {
            SqlOutcome.NoConnections => NoDatabases,
            SqlOutcome.UnknownConnection => run.Detail,
            SqlOutcome.ConnectFailed => ConnectFailed(run.Connection, run.Detail),
            SqlOutcome.Timeout => Timeout(run.Connection, run.Detail),
            _ => Failed(run.Connection, run.Detail),
        };
    }

    // ─── results ────────────────────────────────────────────────────────────────

    /// <summary><c>sqlite_query</c>'s answer: <c>N rows × M columns from database (T ms)</c>, a sentence when a cap cut it, and the table.</summary>
    public static string Query(SqlRun run, int maxRows, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (run.Grids.Count == 0)
        {
            return $"The query returned no result set ({run.Connection}, {Millis(run.Elapsed)})";
        }

        var grid = run.Grids[0];
        string table = SqlText.Table(grid, maxChars, out int shown);
        var header = new StringBuilder();
        header.Append(SqlText.Count(grid.Rows.Count, "row")).Append(grid.More ? "+" : "")
            .Append(" × ").Append(SqlText.Count(grid.Columns.Count, "column"))
            .Append(" from ").Append(run.Connection)
            .Append(" (").Append(Millis(run.Elapsed)).Append(')');
        if (grid.More)
        {
            header.Append(" — the first ").Append(Invariant(maxRows)).Append(" shown, more exist (narrow it with WHERE or LIMIT, or raise max_rows)");
        }

        if (shown < grid.Rows.Count)
        {
            header.Append(" — ").Append(Invariant(shown)).Append(" fit the text cap; select fewer columns or rows for the rest");
        }

        return header + "\n\n" + table;
    }

    /// <summary><c>sqlite_tables</c>' answer: a count naming the database, then the table (cut by the text cap, said so).</summary>
    public static string Tables(SqlRun run, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(run);
        var grid = run.Grids.Count > 0 ? run.Grids[0] : new SqlGrid([], [], false);
        if (grid.Rows.Count == 0)
        {
            return $"No tables or views in {run.Connection}";
        }

        string table = SqlText.Table(grid, maxChars, out int shown);
        string header = $"{SqlText.Count(grid.Rows.Count, "table or view", "tables and views")}{(grid.More ? "+" : "")} in {run.Connection}";
        if (grid.More || shown < grid.Rows.Count)
        {
            header += $" — the first {Invariant(shown)} shown; narrow it with pattern";
        }

        return header + "\n\n" + table;
    }

    /// <summary>
    /// <c>sqlite_describe</c>'s answer, read by position (<see cref="SqliteCatalogQueries.Describe"/>): the name and kind, the columns
    /// (type as declared, nullability, primary-key place, default; generated and hidden columns marked), the foreign keys out, the
    /// tables whose keys point in, the indexes, and the <c>CREATE</c> text — each section only when it has a line.
    /// </summary>
    public static string Describe(string name, string kind, SqlRun run, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(run);
        var sb = new StringBuilder();
        var columns = Set(run, 0);
        sb.Append(name).Append(" (").Append(kind).Append(", ").Append(SqlText.Count(columns.Rows.Count, "column")).Append(") in ").Append(run.Connection).Append('\n');
        // name, type, notnull, dflt_value, pk, hidden
        sb.Append("\n| column | type | null | key | default |\n|---|---|---|---|---|\n");
        foreach (var c in columns.Rows)
        {
            string key = At(c, 4) is "0" or "" ? "" : "PK" + (At(c, 4) == "1" ? "" : " " + At(c, 4));
            string hidden = At(c, 5) switch { "2" or "3" => " (generated)", "1" => " (hidden)", _ => "" };
            sb.Append("| ").Append(At(c, 0)).Append(hidden).Append(" | ").Append(At(c, 1).Length == 0 ? "(any)" : At(c, 1))
                .Append(" | ").Append(At(c, 2) == "1" ? "no" : "yes").Append(" | ").Append(key)
                .Append(" | ").Append(At(c, 3) == SqlText.Null ? "" : At(c, 3)).Append(" |\n");
        }

        // table, from, to, on_update, on_delete
        if (Set(run, 1).Rows is { Count: > 0 } keys)
        {
            sb.Append("\nForeign keys:\n");
            foreach (var k in keys)
            {
                sb.Append("- (").Append(At(k, 1)).Append(") → ").Append(At(k, 0)).Append(" (").Append(At(k, 2)).Append(')')
                    .Append(Rule("ON UPDATE", At(k, 3))).Append(Rule("ON DELETE", At(k, 4))).Append('\n');
            }
        }

        // table, from, to
        if (Set(run, 2).Rows is { Count: > 0 } incoming)
        {
            sb.Append("\nReferenced by:\n");
            foreach (var k in incoming)
            {
                sb.Append("- ").Append(At(k, 0)).Append(" (").Append(At(k, 1)).Append(") → (").Append(At(k, 2)).Append(")\n");
            }
        }

        // name, unique, origin, partial, columns
        if (Set(run, 3).Rows is { Count: > 0 } indexes)
        {
            sb.Append("\nIndexes:\n");
            foreach (var x in indexes)
            {
                string origin = At(x, 2) switch { "pk" => ", the primary key", "u" => ", a UNIQUE constraint", _ => "" };
                sb.Append("- ").Append(At(x, 0)).Append(" (").Append(At(x, 4)).Append(')').Append(At(x, 1) == "1" ? " unique" : "")
                    .Append(At(x, 3) == "1" ? " partial" : "").Append(origin).Append('\n');
            }
        }

        if (Set(run, 4).Rows is [var created, ..] && At(created, 0) is { Length: > 0 } sql && sql != SqlText.Null)
        {
            sb.Append("\n```sql\n").Append(sql.Trim()).Append("\n```\n");
        }

        string text = sb.ToString().TrimEnd();
        return text.Length <= maxChars ? text : text[..maxChars] + "\n… (cut at the text cap)";
    }

    private static string Rule(string what, string action) => action is "" or "NO ACTION" ? "" : " " + what + " " + action;

    private static SqlGrid Set(SqlRun run, int index) => index < run.Grids.Count ? run.Grids[index] : new SqlGrid([], [], false);

    private static string At(string[] row, int index) => index < row.Length ? row[index] : "";

    private static string Millis(TimeSpan elapsed) => Invariant((long)elapsed.TotalMilliseconds) + " ms";

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    private static string Invariant(long value) => value.ToString(CultureInfo.InvariantCulture);
}
