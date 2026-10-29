using System.Globalization;
using System.Text;
using NeonSidekick.Sql;

namespace NeonSidekick.DuckDb;

/// <summary>
/// The DuckDB tools' wording and formatting (2026-10-25), <c>SqliteText</c>'s twin, pure and pinned: the <c>duckdb.json</c> problems, the
/// gates' refusals, the data files' refusals, the outcomes and the results. The engine-neutral pieces — a cell's text, a Markdown table, a
/// count — are <see cref="SqlText"/>'s. Every error starts <c>Error:</c>. Invariant culture throughout.
/// </summary>
public static class DuckDbText
{
    // ─── duckdb.json ────────────────────────────────────────────────────────────

    public const string NoPath = "no \"path\" is given";

    /// <summary>A path no file can have. Pinned.</summary>
    public static string BadPath(string path) => $"\"path\" is '{path}', which no file can have";

    /// <summary>What an in-memory call (no database, data files alone) is called in a result's header and the log.</summary>
    public const string MemoryName = "the data files";

    /// <summary>One database as <c>duckdb_databases</c> lists it: the name, the file, the description. Pinned.</summary>
    public static string DatabaseLine(DuckDbNamedDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        string line = $"- {database.Name}: {database.FullPath}";
        return string.IsNullOrWhiteSpace(database.Config.Description) ? line : line + " — " + database.Config.Description.Trim();
    }

    /// <summary>A database's note on the <c>%</c>-mention list: <c>DuckDB ·</c>, the file, its description. Pinned.</summary>
    public static string MentionNote(DuckDbNamedDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        string where = "DuckDB · " + database.FullPath;
        return string.IsNullOrWhiteSpace(database.Config.Description) ? where : where + " — " + database.Config.Description.Trim();
    }

    /// <summary>The closing line of <c>duckdb_databases</c> while the profile hides some: how many, never which. Pinned.</summary>
    public static string HiddenDatabases(int count) =>
        count == 1
            ? "1 more database in duckdb.json is switched off for this profile (/tools › Databases › DuckDB)."
            : $"{Invariant(count)} more databases in duckdb.json are switched off for this profile (/tools › Databases › DuckDB).";

    /// <summary>What <c>duckdb_databases</c> says while sandbox files is on: DuckDB files by path, and the data files. Pinned.</summary>
    public const string SandboxLine =
        "Any DuckDB file in the working directory can be named by its path too (e.g. \"data/app.duckdb\"). CSV, Parquet and JSON files there " +
        "are queried by their paths (FROM 'sales.csv', " + DuckDbFunctions.ReaderNames + "); leave \"database\" out to query files alone.";

    /// <summary><c>duckdb_databases</c>' whole answer: a count, one line each, the sandbox line when on, the problems.</summary>
    public static string Databases(DuckDbCatalog catalog, bool sandbox)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var sb = new StringBuilder();
        if (catalog.Databases.Count == 0)
        {
            sb.Append(sandbox ? "No named DuckDB database." : NoDatabases);
        }
        else
        {
            sb.Append(SqlText.Count(catalog.Databases.Count, "DuckDB database")).Append(" (every DuckDB tool takes one by name in \"database\"; it may be left out only when there is one):");
            foreach (var database in catalog.Databases)
            {
                sb.Append('\n').Append(DatabaseLine(database));
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
    public const string Positional = "Error: the SQL uses a positional placeholder (? or $1); name it ($id) and pass its value in \"params\"";
    public static string Unterminated(string what, int line, int column) => $"Error: the SQL does not parse (line {Invariant(line)}, column {Invariant(column)}): a {what} never ends";
    public static string NotOneStatement(int count) => $"Error: the SQL is {Invariant(count)} statements; send exactly one per call";
    public static string NotASelect(string word) => $"Error: the SQL starts with {word}; the DuckDB tools only read — send one SELECT (WITH, VALUES, FROM, DESCRIBE, SUMMARIZE, PIVOT or EXPLAIN may lead it)";
    public static string Forbidden(string what) => $"Error: the SQL uses {what}, which the DuckDB tools refuse (they only read this database and the data files in the working directory)";

    /// <summary>A reader whose first argument is not a file named as text. Pinned.</summary>
    public static string ReaderNeedsLiteral(string reader) =>
        $"Error: {reader}() takes its file as text as its first argument: 'data/sales.csv' or a list such as ['a.csv', 'b.csv']";

    // ─── data files ─────────────────────────────────────────────────────────────

    /// <summary>A file read while <c>DuckDB sandbox files</c> is off. Pinned.</summary>
    public static string FilesNeedSandbox(string path) =>
        $"Error: the SQL reads the file '{Shown(path)}', and no data file may be read while DuckDB sandbox files is off; the user turns it on at /tools › Databases › DuckDB";

    /// <summary>A file the file tools would refuse to read too, with their sentence after. Pinned.</summary>
    public static string FileRefused(string path, string why) =>
        $"Error: the SQL reads '{Shown(path)}', which the DuckDB tools may not read: {why.Replace("Error: ", "", StringComparison.Ordinal)}";

    /// <summary>A file the SQL names that is not in the working directory. Pinned.</summary>
    public static string NoSuchFile(string path) => $"Error: the SQL reads '{Shown(path)}', and no such file is in the working directory";

    /// <summary>A pattern where DuckDB would read a file whole, or one reaching below its folder. Pinned.</summary>
    public static string GlobRefused(string path) =>
        $"Error: '{Shown(path)}' is a pattern, which the DuckDB tools take only inside a reader and within one folder: read_csv('data/*.csv') or read_parquet(['a.parquet', 'b.parquet'])";

    /// <summary>The most files one pattern may match.</summary>
    public const int MaxGlobFiles = 1000;

    /// <summary>A pattern that matches too many files. Pinned.</summary>
    public static string TooManyFiles(string path) => $"Error: '{Shown(path)}' matches more than {MaxGlobFiles.ToString("N0", CultureInfo.InvariantCulture)} files; narrow the pattern";

    /// <summary>A pattern that matches nothing. Pinned.</summary>
    public static string NoMatch(string path) => $"Error: no file in the working directory matches '{Shown(path)}'";

    /// <summary>
    /// A quoted table name that reads like a file and names none in the working directory: DuckDB would look for the file, so the table is to be
    /// qualified (a qualified name is a table first). Pinned.
    /// </summary>
    public static string TableLooksLikeFile(string name, bool sandbox) =>
        $"Error: \"{Shown(name)}\" reads like a file name, and " +
        (sandbox ? "no such file is in the working directory" : "no data file is read while DuckDB sandbox files is off") +
        $"; if it is a table, name it with its schema: main.\"{Shown(name)}\"";

    /// <summary>A reader whose extension is not built into the app's DuckDB: nothing is ever installed. Pinned.</summary>
    public static string ReaderNotBuiltIn(string detail) =>
        $"Error: the SQL needs a DuckDB extension that is not built in ({detail}), and the DuckDB tools never install one; the built-in readers are {DuckDbFunctions.ReaderNames}";

    // ─── duckdb_execute's gate ──────────────────────────────────────────────────

    public const string NoStatement = "Error: give the statement to run in \"sql\"";
    public static string WriteNotOneStatement(int count) => $"Error: the SQL is {Invariant(count)} statements; duckdb_execute runs exactly one per call — send the next one in the next call";
    public static string WriteForbidden(string what, string why) => $"Error: the SQL uses {what}, which duckdb_execute refuses: {why}";
    public static string UnknownStatement(string word) => $"Error: the SQL starts with {word}, which is not a statement duckdb_execute runs";

    /// <summary>A statement of a kind the user has not ticked: its kind, then what is allowed. Pinned.</summary>
    public static string KindNotAllowed(DuckDbStatementKind kind, IReadOnlyList<DuckDbStatementKind> allowed)
    {
        ArgumentNullException.ThrowIfNull(allowed);
        string may = allowed.Count == 0 ? "nothing" : string.Join(", ", allowed.Select(DuckDbStatementKinds.Title));
        return $"Error: the SQL is {DuckDbStatementKinds.Title(kind)} ({DuckDbStatementKinds.Statements(kind)}), which the user has not allowed; duckdb_execute may run {may} — the user ticks more in DuckDB statements allowed on /tools › Databases › DuckDB";
    }

    public const string CreateNotAllowed = "Error: \"create\" makes a new database, and the user has not allowed creating (DuckDB statements allowed on /tools › Databases › DuckDB)";
    public const string NoKindsAllowed = "Error: the user has allowed no kind of statement for duckdb_execute (DuckDB statements allowed on /tools › Databases › DuckDB)";

    public const string OwnTransaction = "each call is a transaction of its own, committed when its statement succeeds";
    public const string AnotherFile = "it reaches a database other than this one";
    public const string WritesFiles = "it writes files outside the database; load a data file into a table with CREATE TABLE t AS SELECT * FROM 'data.csv' or INSERT INTO t SELECT … instead";
    public const string OutsideCode = "it installs or loads code, or changes the locked settings";
    public const string RunsText = "it runs SQL handed over as text";
    public const string NeverCalled = "the DuckDB tools never call it (it reads outside the database or runs SQL as text)";
    public const string ExplainsAChange = "EXPLAIN shows a read's plan only; send the change itself";
    public const string Secrets = "the DuckDB tools keep no secrets";

    // ─── duckdb_execute ─────────────────────────────────────────────────────────

    public const string ReadOnlyMode = "Error: DuckDB mode is read-only, so nothing may change a DuckDB database; the user switches it to read-write on /tools › Databases › DuckDB";
    public const string NoPane = "Error: duckdb_execute needs the user to allow each change on a pane, and there is none here";
    public const string Declined = "The user declined the change; nothing was run. Do not run it again unless the user asks for it.";
    public const string CreateNeedsSandbox = "Error: a new database file can only be made in the working directory, and DuckDB sandbox files is off";
    public const string CreateNeedsPath = "Error: \"create\" needs the new file's path in the working directory as \"database\", e.g. \"data/app.duckdb\"";
    public const string MemoryIsGone = "Error: duckdb_execute changes a database file, and with no \"database\" there is none (a change in memory is gone when the call ends); name a database, or a new .duckdb file with create";
    public static string BadExtension(string path) => $"Error: '{Shown(path)}' is not a valid database file name; a new DuckDB database's file ends .duckdb or .ddb";
    public static string NoFolder(string path) => $"Error: the folder for '{Shown(path)}' does not exist; make it first (create_directory)";

    /// <summary>A working-directory path that is a database duckdb.json names and the user has not ticked. Pinned.</summary>
    public static string UntickedDatabase(string path) =>
        $"Error: '{Shown(path)}' is a database the user has named in duckdb.json but not ticked; it is not reached until the user ticks it in DuckDB databases offered on /tools › Databases › DuckDB";

    /// <summary>A change the file guard refuses, its own sentence after. Pinned.</summary>
    public static string WriteGuarded(string database, string why) => $"Error: duckdb_execute may not change {database}: {why.Replace("Error: ", "", StringComparison.Ordinal)}";

    /// <summary>The allow pane's title. Pinned.</summary>
    public const string AllowTitle = "Change a DuckDB database?";

    /// <summary>The allow pane's caption: what would change (or be made), the statement and the values bound to it. Pinned.</summary>
    public static string AllowCaption(DuckDbTarget target, string sql, bool creating, IReadOnlyList<SqlParameterValue>? parameters = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(sql);
        string file = Files.NameScrub.Display(target.FullPath);
        string where = creating ? $"The model wants to create the new database {file} and run:" : $"The model wants to change {Files.NameScrub.Display(target.Name)} ({file}):";
        return where + "\n" + ServerWriteText.CaptionStatement(sql) + ServerWriteText.Bound(parameters);
    }

    /// <summary>The audit line with its verb already worded (<see cref="DatabaseAudit.Word"/>). Pinned.</summary>
    public static string AuditLogLine(string database, string path, bool created, string word, string sql) =>
        $"{database} ({path}){(created ? " created" : "")}: {word} by {sql}";

    /// <summary>
    /// <c>duckdb_execute</c>'s answer: <c>Changed N rows in database (T ms)</c>, the file's making when it was made, then the rows a RETURNING
    /// gave back, as <see cref="Query"/> shows them.
    /// </summary>
    public static string Executed(SqlRun run, bool created, int maxRows, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(run);
        string head = (created ? $"Created {run.Connection}. " : "") + (run.Changes is { } n ? $"Changed {SqlText.Count(n, "row")} in {run.Connection}" : $"Ran it in {run.Connection}") + $" ({Millis(run.Elapsed)})";
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
    public static string BadParamName(string name) => $"Error: '{name}' is not a valid parameter name; give the name as the SQL writes it after its $, e.g. \"id\" for $id";
    public static string BadParams(string raw) => $"Error: \"params\" must be one object of names and values, e.g. {{\"id\": 5}} for $id (got {Clip(raw, 200)})";
    public static string TableNotFound(string table, string database) => $"Error: no table or view '{table}' in {database}; duckdb_tables lists them";

    // ─── outcomes ───────────────────────────────────────────────────────────────

    public const string NoDatabases = "Error: no DuckDB database is defined; the user adds one to duckdb.json (/tools › Databases › DuckDB)";
    public static string UnknownDatabase(string name, string names, bool sandbox) =>
        $"Error: no DuckDB database is named '{Shown(name)}'" + (sandbox ? " and no file by that path is in the working directory" : "") + (names.Length > 0 ? $"; the databases are {names}" : "");

    /// <summary>A call that names no database while several are named in duckdb.json. Pinned.</summary>
    public static string DatabaseNeeded(string names, bool sandbox) =>
        "Error: name the DuckDB database in \"database\"" + (sandbox ? " (or a file's path in the working directory)" : "") + $"; the databases are {names}";
    public static string NoFile(string path) => $"no database file at {Shown(path)}";
    public static string OutsideSandbox(string path) => $"'{Shown(path)}' is outside the working directory";

    /// <summary>A SQLite file named as a DuckDB database: the SQLite tools read it. Pinned.</summary>
    public static string IsSqliteFile(string path) => $"'{Shown(path)}' is a SQLite database, not a DuckDB one; read it with the sqlite_ tools (sqlite_query)";

    /// <summary>A file that is neither. Pinned.</summary>
    public static string NotADuckDbFile(string path) => $"'{Shown(path)}' is not a DuckDB database file";

    /// <summary>A 0-byte file: DuckDB opens none, for a read or a write. Pinned.</summary>
    public static string EmptyFile(string path) => $"'{Shown(path)}' is an empty file, not a DuckDB database; DuckDB opens no empty file";

    /// <summary>A path DuckDB cannot open on Windows. Pinned.</summary>
    public static string PathTooLong(string path) => $"'{Shown(path)}' is a path of {MaxPath.ToString(CultureInfo.InvariantCulture)} characters or more, which DuckDB cannot open on Windows";

    /// <summary>The length from which DuckDB fails to open a path on Windows (the spike's: <c>\\?\</c> does not help).</summary>
    public const int MaxPath = 260;

    /// <summary>A file another program holds. Pinned.</summary>
    public static string InUse(string detail) => $"the file is open in another program ({detail}); close it there and try again";

    public const string Encrypted = "the database is encrypted, and the DuckDB tools open no encrypted database";
    public const string NewerFile = "the database was made by a newer DuckDB than the app's; open it with that DuckDB, or export it";
    public static string ConnectFailed(string database, string detail) => $"Error: could not open {database}: {detail}";
    public static string Timeout(string database, string seconds) => $"Error: the query on {database} ran past {seconds} s and was stopped; narrow it (WHERE, LIMIT, fewer joins)";
    public static string Failed(string database, string detail) => $"Error: DuckDB refused it ({database}): {detail}";
    public static string OpenFailedLogLine(string database, string detail) => $"{database} did not open: {detail}";
    public static string LockdownFailedLogLine(string database, string detail) => $"{database} opened, but its settings could not be locked, so it was closed: {detail}";
    public static string UnmakeFailedLogLine(string path, string detail) => $"{path}, made by a create whose statement failed, could not be removed: {detail}";
    /// <summary>The exit's log line when the start's spill sweep had not ended within <see cref="DuckDbAccess.SweepExitWait"/> (2026-10-25).</summary>
    public const string SweepUnfinishedLogLine = "The spill folders of DuckDB runs that ended were still being removed at the exit; the next start goes on.";

    public static string SweepFailedLogLine(string path, string detail) => $"{path}, a spill folder of a DuckDB run that ended, could not be removed: {detail}";

    /// <summary>The lockdown read back and found not in force: the call is refused. Pinned.</summary>
    public static string NotLocked(string setting) => $"DuckDB did not take the setting {setting}, so nothing was run";

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
            _ => run.Detail.StartsWith("Error: ", StringComparison.Ordinal) ? run.Detail : Failed(run.Connection, run.Detail),
        };
    }

    // ─── results ────────────────────────────────────────────────────────────────

    /// <summary><c>duckdb_query</c>'s answer: <c>N rows × M columns from database (T ms)</c>, a sentence when a cap cut it, and the table.</summary>
    public static string Query(SqlRun run, int maxRows, int maxChars, bool canSave = false)
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
            header.Append(" — the first ").Append(Invariant(maxRows)).Append(" shown, more exist (narrow it with WHERE or LIMIT, or raise max_rows").Append(SqlExport.Hint(canSave)).Append(')');
        }

        if (shown < grid.Rows.Count)
        {
            header.Append(" — ").Append(Invariant(shown)).Append(" fit the text cap; select fewer columns or rows for the rest");
        }

        return header + "\n\n" + table;
    }

    /// <summary><c>duckdb_tables</c>' answer: a count naming the database, then the table (cut by the text cap, said so).</summary>
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
    /// <c>duckdb_describe</c>'s answer, read by position (<see cref="DuckDbCatalogQueries.Describe"/>): the name and kind, the columns (type,
    /// nullability, primary-key place, default), the foreign keys out, the tables whose keys point in, the indexes and UNIQUE constraints, and
    /// the <c>CREATE</c> text — each section only when it has a line.
    /// </summary>
    public static string Describe(string name, string kind, SqlRun run, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(run);
        var sb = new StringBuilder();
        var columns = Set(run, 0);
        sb.Append(name).Append(" (").Append(kind).Append(", ").Append(SqlText.Count(columns.Rows.Count, "column")).Append(") in ").Append(run.Connection).Append('\n');
        // name, type, nullable, default, pk
        sb.Append("\n| column | type | null | key | default |\n|---|---|---|---|---|\n");
        foreach (var c in columns.Rows)
        {
            string key = At(c, 4) is "0" or "" ? "" : "PK" + (At(c, 4) == "1" ? "" : " " + At(c, 4));
            sb.Append("| ").Append(At(c, 0)).Append(" | ").Append(At(c, 1))
                .Append(" | ").Append(At(c, 2) == "false" ? "no" : "yes").Append(" | ").Append(key)
                .Append(" | ").Append(At(c, 3) == SqlText.Null ? "" : At(c, 3)).Append(" |\n");
        }

        // table, from, to
        if (Set(run, 1).Rows is { Count: > 0 } keys)
        {
            sb.Append("\nForeign keys:\n");
            foreach (var k in keys)
            {
                sb.Append("- (").Append(At(k, 1)).Append(") → ").Append(At(k, 0)).Append(" (").Append(At(k, 2)).Append(")\n");
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

        // name, unique, expressions; then UNIQUE constraints' columns
        var indexes = Set(run, 3).Rows;
        var unique = Set(run, 4).Rows;
        if (indexes.Count > 0 || unique.Count > 0)
        {
            sb.Append("\nIndexes:\n");
            foreach (var x in indexes)
            {
                sb.Append("- ").Append(At(x, 0)).Append(" (").Append(Expressions(At(x, 2))).Append(')').Append(At(x, 1) == "true" ? " unique" : "").Append('\n');
            }

            foreach (var u in unique)
            {
                sb.Append("- UNIQUE (").Append(At(u, 0)).Append(")\n");
            }
        }

        if (Set(run, 5).Rows is [var created, ..] && At(created, 0) is { Length: > 0 } sql && sql != SqlText.Null)
        {
            sb.Append("\n```sql\n").Append(sql.Trim()).Append("\n```\n");
        }

        string text = sb.ToString().TrimEnd();
        return text.Length <= maxChars ? text : text[..maxChars] + "\n… (cut at the text cap)";
    }

    /// <summary>An index's expressions as DuckDB lists them (<c>['(lower(x))', 'y']</c>) read as <c>lower(x), y</c>.</summary>
    internal static string Expressions(string listed)
    {
        string inner = listed.Length >= 2 && listed[0] == '[' && listed[^1] == ']' ? listed[1..^1] : listed;
        var parts = inner.Split("', '", StringSplitOptions.None).Select(p => p.Trim().Trim('\'')).Select(p => p.Length > 2 && p[0] == '(' && p[^1] == ')' ? p[1..^1] : p);
        return string.Join(", ", parts);
    }

    /// <summary>A file name as an answer shows it: its hiding characters escaped (<see cref="Files.NameScrub"/>).</summary>
    internal static string Shown(string path) => Files.NameScrub.Display(path);

    private static SqlGrid Set(SqlRun run, int index) => index < run.Grids.Count ? run.Grids[index] : new SqlGrid([], [], false);

    private static string At(string[] row, int index) => index < row.Length ? row[index] : "";

    private static string Millis(TimeSpan elapsed) => Invariant((long)elapsed.TotalMilliseconds) + " ms";

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    private static string Invariant(long value) => value.ToString(CultureInfo.InvariantCulture);
}
