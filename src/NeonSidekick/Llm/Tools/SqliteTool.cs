using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Settings;
using NeonSidekick.Sql;
using NeonSidekick.Sqlite;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// What the SQLite tools share (2026-10-04; four, and <c>sqlite_execute</c> since 2026-10-05), <see cref="MySqlTool"/>'s shape: the <see cref="SqliteAccess"/> door, the settings
/// in force at each call, the optional <c>database</c> (a name from <c>sqlite.json</c>, or a file in the working directory by its
/// path while <c>SQLite sandbox files</c> is on; left out, the only named one, several refused since 2026-10-09), the statement timeout, and the table lookup
/// <c>sqlite_describe</c> needs. A refused run is <see cref="SqliteText.Error"/>'s sentence.
/// </summary>
public abstract class SqliteTool : ServerTool<SqliteAccess>
{
    public const string DatabaseArgument = "database";

    /// <summary>The <c>database</c> property every schema but <c>sqlite_databases</c> carries.</summary>
    public const string DatabaseProperty = "\"database\": { \"type\": \"string\", \"description\": \"The database's name (sqlite_databases lists them), or a .db file in the working directory by its path; it may be left out only when there is one.\" }";

    protected SqliteTool(SqliteAccess sqlite, Func<AppSettingsData> effective) : base(sqlite, effective)
    {
    }

    protected SqliteAccess Files => Access;

    /// <summary>SQLite's tools never took another argument for where they work: nothing is retired.</summary>
    protected sealed override string? RetiredArgument => null;

    protected sealed override string RetiredRefusal => "";

    /// <summary>The statement timeout: the setting, clamped to the range a hand-edited value may have left.</summary>
    public static int TimeoutSeconds(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return Math.Clamp(effective.SqliteQueryTimeoutSeconds, AppSettingsData.MinSqlQueryTimeoutSeconds, AppSettingsData.MaxSqlQueryTimeoutSeconds);
    }

    /// <summary>Runs the app's own statements on the call's database, capped at <see cref="SqliteAccess.MaxCatalogRows"/>.</summary>
    protected async Task<SqlRun> CatalogAsync(string? database, IReadOnlyList<string> statements, IReadOnlyList<SqlParameterValue> parameters, CancellationToken cancellationToken) =>
        SqlText.CatalogRun(await Files.RunAsync(database, statements, parameters, SqliteAccess.MaxCatalogRows, TimeoutSeconds(Effective), cancellationToken).ConfigureAwait(false));
}

/// <summary>
/// The user's allow for one <c>sqlite_execute</c> change: the file, the statement, whether the file is made, and the values its placeholders
/// are bound to (2026-10-24, the database review's B5). True to run, false declined, null when no pane could ask.
/// </summary>
public delegate Task<bool?> SqliteWriteAllow(SqliteTarget target, string sql, bool creating, IReadOnlyList<SqlParameterValue> parameters, CancellationToken cancellationToken);

/// <summary><c>sqlite_databases()</c>: the named databases of <c>sqlite.json</c> — the file, the description — and whether a working-directory file may be named too. Opens nothing.</summary>
public sealed class SqliteDatabasesTool : SqliteTool
{
    public const string ToolName = "sqlite_databases";

    private static readonly JsonElement Schema = ToolSchema.Parse("""{ "type": "object", "properties": {} }""");

    public SqliteDatabasesTool(SqliteAccess sqlite, Func<AppSettingsData> effective) : base(sqlite, effective)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Lists the SQLite databases the user has named, each one's file and what it holds, and whether a database file " +
        "in the working directory can be named by its path. Pass a name (or such a path) as \"database\" to the other sqlite_ tools.";

    public override JsonElement JsonSchema => Schema;

    public string Describe() => SqliteText.Databases(Files.Catalog(), Files.SandboxOn);

    protected override ValueTask<object?> InvokeToolAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) => new(Describe());
}

/// <summary><c>sqlite_tables(database?, pattern?)</c>: the tables and views of the file, with their kind.</summary>
public sealed class SqliteTablesTool : SqliteTool
{
    public const string ToolName = "sqlite_tables";
    public const string PatternArgument = "pattern";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        $$"""
        {
          "type": "object",
          "properties": {
            {{DatabaseProperty}},
            "pattern": { "type": "string", "description": "Only names containing this text (case-insensitive), or matching it as a LIKE pattern when it holds %; leave it out for all." }
          }
        }
        """);

    public SqliteTablesTool(SqliteAccess sqlite, Func<AppSettingsData> effective) : base(sqlite, effective)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Lists the tables and views of a SQLite database. Narrow it with pattern; then sqlite_describe a table before querying it.";

    public override JsonElement JsonSchema => Schema;

    public async Task<string> DescribeAsync(string? database, string? pattern, CancellationToken cancellationToken)
    {
        var run = await CatalogAsync(database, [SqliteCatalogQueries.Tables], [new SqlParameterValue("pattern", OracleTablesTool.LikePattern(pattern))], cancellationToken).ConfigureAwait(false);
        return run.Outcome == SqlOutcome.Ok ? SqliteText.Tables(run, NeonSidekick.Files.WorkingDirectory.MaxReadChars) : SqliteText.Error(run);
    }

    protected override async ValueTask<object?> InvokeToolAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return await DescribeAsync(Optional(arguments, DatabaseArgument), Optional(arguments, PatternArgument), cancellationToken).ConfigureAwait(false);
    }
}

/// <summary><c>sqlite_describe(table, database?)</c>: a table's or view's columns, its foreign keys out and in, its indexes and its CREATE text, in one call.</summary>
public sealed class SqliteDescribeTool : SqliteTool
{
    public const string ToolName = "sqlite_describe";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        $$"""
        {
          "type": "object",
          "properties": {
            "table": { "type": "string", "description": "The table or view (case-insensitive)." },
            {{DatabaseProperty}}
          },
          "required": ["table"]
        }
        """);

    public SqliteDescribeTool(SqliteAccess sqlite, Func<AppSettingsData> effective) : base(sqlite, effective)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Shows a SQLite table's or view's columns (type, nullability, primary key, default), its foreign keys both ways, its indexes " +
        "and the CREATE statement — what to read before writing a query on it.";

    public override JsonElement JsonSchema => Schema;

    public async Task<string> DescribeAsync(string table, string? database, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(table))
        {
            return SqliteText.NoTable;
        }

        var found = await CatalogAsync(database, [SqliteCatalogQueries.Resolve], [new SqlParameterValue("name", table.Trim())], cancellationToken).ConfigureAwait(false);
        if (found.Outcome != SqlOutcome.Ok)
        {
            return SqliteText.Error(found);
        }

        if (found.Grids is not [{ Rows: [var row, ..] }, ..])
        {
            return SqliteText.TableNotFound(table.Trim(), found.Connection);
        }

        var run = await CatalogAsync(database, SqliteCatalogQueries.Describe, [new SqlParameterValue("name", row[0])], cancellationToken).ConfigureAwait(false);
        return run.Outcome == SqlOutcome.Ok ? SqliteText.Describe(row[0], row[1], run, NeonSidekick.Files.WorkingDirectory.MaxReadChars) : SqliteText.Error(run);
    }

    protected override async ValueTask<object?> InvokeToolAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return await DescribeAsync(ToolArguments.ReadString(arguments, TableArgument), Optional(arguments, DatabaseArgument), cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// <c>sqlite_query(sql, database?, params?, max_rows?)</c>: one read-only SELECT (a WITH or VALUES may lead it), passed by
/// <see cref="SqliteReadOnlyGate"/> first, then run on the file opened read-only with <c>query_only</c> on, in a transaction that is
/// rolled back, interrupted at the setting's timeout; the rows as a Markdown table under a header that says when the cap cut it.
/// <c>params</c> is one object, each name bound to its <c>@name</c>, <c>:name</c> or <c>$name</c> — never spliced into the text.
/// </summary>
public sealed class SqliteQueryTool : SqliteTool
{
    public const string ToolName = "sqlite_query";
    public const string SqlArgument = "sql";
    public const string ParamsArgument = "params";
    public const string MaxRowsArgument = "max_rows";

    public const int MinRows = AppSettingsData.MinSqlQueryMaxRows;
    public const int MaxRows = AppSettingsData.MaxSqlQueryMaxRows;

    private static readonly (JsonElement Export, JsonElement Plain) Schemas = QuerySchemas(members =>
        $$"""
        {
          "type": "object",
          "properties": {
            "sql": { "type": "string", "description": "One SQLite SELECT (LIMIT n); a WITH clause may lead it. No INSERT, UPDATE, DELETE, DDL, PRAGMA, ATTACH or second statement." },
            {{DatabaseProperty}},
            "params": { "type": "object", "description": "Values for the named placeholders in the SQL (@name, :name, $name), each by its name, e.g. {\"id\": 101} for @id or :id; strings, numbers, true, false or null." },
            {{members}}
          },
          "required": ["sql"]
        }
        """);

    public SqliteQueryTool(SqliteAccess sqlite, Func<AppSettingsData> effective) : base(sqlite, effective)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Runs one read-only SELECT on a SQLite database and returns the rows as a table. " +
        "Only a single SELECT (or WITH … SELECT) is accepted; bind values as @name through params rather than writing them into the text. " +
        "The header says when more rows exist than were returned." + SaveSentence;

    public override JsonElement JsonSchema => QuerySchema(Schemas);

    /// <summary>The row cap a call without <c>max_rows</c> gets: the setting, clamped.</summary>
    public static int DefaultRows(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return Math.Clamp(effective.SqliteQueryMaxRows, MinRows, MaxRows);
    }

    /// <remarks><paramref name="export"/> (2026-10-29, the Office writing plan): every row to a workbook instead (<see cref="ServerTool{TAccess}.ExportAsync"/>).</remarks>
    public async Task<string> RunAsync(string sql, string? database, IReadOnlyList<SqlParameterValue> parameters, int? maxRows, CancellationToken cancellationToken, ExportTarget? export = null)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(parameters);
        if (RowCap(export, maxRows, DefaultRows(Effective), MinRows, MaxRows, out int rows) is { } badRows)
        {
            return badRows;
        }

        if (SqliteReadOnlyGate.Check(sql) is { } refused)
        {
            return refused;
        }

        string body = SqliteReadOnlyGate.Body(sql);
        if (export is not null)
        {
            if (Files.Resolve(database, out var unresolved) is not { } target)
            {
                return SqliteText.Error(unresolved!);
            }

            return await ExportAsync(export, Path.GetFileNameWithoutExtension(target.Name), rows, maxRows, TimeoutSeconds(Effective), SqliteAccess.ExportCell,
                (job, timeout, token) => SqliteAccess.ExportAsync(target, body, parameters, job, rows, timeout, token), SqliteText.Error, cancellationToken).ConfigureAwait(false);
        }

        var run = await Files.RunAsync(database, [body], parameters, rows, TimeoutSeconds(Effective), cancellationToken).ConfigureAwait(false);
        return run.Outcome == SqlOutcome.Ok ? SqliteText.Query(run, rows, SqlTool.ResultChars(Effective), CanSave) : SqliteText.Error(run);
    }

    protected override async ValueTask<object?> InvokeToolAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!ToolArguments.TryReadInt32(arguments, MaxRowsArgument, out var max, out var raw))
        {
            return ClockText.BadInteger(MaxRowsArgument, raw);
        }

        if (ReadParams(arguments, out var error) is not { } parameters)
        {
            return error;
        }

        if (ReadExport(arguments, out var export) is { } badExport)
        {
            return badExport;
        }

        return await RunAsync(ToolArguments.ReadString(arguments, SqlArgument), Optional(arguments, DatabaseArgument), parameters, max, cancellationToken, export).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>params</c> as <c>sqlite_query</c> and <c>sqlite_execute</c> take it: one object, each name read as SQLite reads a
    /// placeholder's; null with the <c>Error:</c> sentence in <paramref name="error"/>.
    /// </summary>
    internal static IReadOnlyList<SqlParameterValue>? ReadParams(AIFunctionArguments arguments, out string error)
    {
        error = "";
        if (!ToolArguments.TryReadObjectList(arguments, ParamsArgument, out var objects, out var sent) || objects.Count > 1)
        {
            error = SqliteText.BadParams(sent);
            return null;
        }

        if (objects.Count == 0)
        {
            return [];
        }

        if (SqlQueryTool.ReadParameters(objects[0], out var bad, SqliteReadOnlyGate.ParamName, SqliteText.BadParamName) is not { } read)
        {
            error = bad ?? "";
            return null;
        }

        return read;
    }
}

/// <summary>
/// <c>sqlite_execute(sql, database?, params?, max_rows?, create?)</c> (2026-10-05, the user's ask): one statement that may change
/// the database — DML, DDL, PRAGMA — offered only while <c>SQLite mode</c> is <c>read-write</c>, a pane can ask and
/// <c>SQLite statements allowed</c> ticks a kind (all checked again at every call; the statement's kind must be ticked, and
/// <c>create</c> needs creating). A read (reading ticked) asks nothing and runs on the read-only path, <see cref="SqliteAccess.Run"/>
/// (later on 2026-10-05, the user's ask). <see cref="SqliteWriteGate"/> first, then the database (with <c>create</c>, a new file in the
/// working directory), then the user's allow — Deny / Allow once / Allow for this session, per file — then
/// <see cref="SqliteAccess.Execute"/>, committed as it runs. Every change is written to the log (the audit line). The answer is
/// the rows changed and any rows a RETURNING or PRAGMA gave back. Plan mode drops it.
/// </summary>
public sealed partial class SqliteExecuteTool : SqliteTool
{
    public const string ToolName = "sqlite_execute";
    public const string CreateArgument = "create";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        $$"""
        {
          "type": "object",
          "properties": {
            "sql": { "type": "string", "description": "One SQLite statement of a kind this tool's description lists (RETURNING allowed). No ATTACH, BEGIN/COMMIT or second statement." },
            {{DatabaseProperty}},
            "params": { "type": "object", "description": "Values for the named placeholders in the SQL (@name, :name, $name), each by its name, e.g. {\"id\": 101} for @id or :id; strings, numbers, true, false or null." },
            "max_rows": { "type": "integer", "description": "How many returned rows (RETURNING, PRAGMA) to show at most, 1 to {{MaxRowsWords}}. Leave it out for the user's default." },
            "create": { "type": "boolean", "description": "true to create a new database file at \"database\", a path in the working directory ending .db, .sqlite, .sqlite3 or .db3 (its folder must exist); only when the description says creating is allowed. An existing file is just opened." }
          },
          "required": ["sql"]
        }
        """);

    private readonly SqliteWriteAllow? _allow;

    /// <param name="allow">The user's allow for one change (the target, the statement, whether the file is made, the bound values): true to run, false declined, null when no pane could ask. Null where nothing can ask: every call is refused.</param>
    public SqliteExecuteTool(SqliteAccess sqlite, Func<AppSettingsData> effective, SqliteWriteAllow? allow) : base(sqlite, effective)
    {
        _allow = allow;
    }

    public override string Name => ToolName;

    /// <summary>The description, read at each turn: it names the kinds <c>SQLite statements allowed</c> ticks (later on 2026-10-05), and create only with creating.</summary>
    public override string Description => DescribeFor(SqliteStatementKinds.Resolve(Effective));

    /// <summary>The description for <paramref name="kinds"/>. Pinned.</summary>
    public static string DescribeFor(IReadOnlyList<SqliteStatementKind> kinds)
    {
        ArgumentNullException.ThrowIfNull(kinds);
        return "Runs one statement that changes a SQLite database. The user allows only these kinds: " + SqliteStatementKinds.Describe(kinds) + ". " +
            (kinds.Contains(SqliteStatementKind.Create) ? "With create it makes a new database file in the working directory. " : "") +
            "The user allows each change first; a change is permanent once it runs. " +
            (kinds.Contains(SqliteStatementKind.Read) ? "A read runs without asking, on the file opened read-only. " : "") +
            "Bind values as @name through params. For reading, use sqlite_query.";
    }

    public override JsonElement JsonSchema => Schema;

    public async Task<string> RunAsync(string sql, string? database, IReadOnlyList<SqlParameterValue> parameters, int? maxRows, bool create, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(parameters);
        var effective = Effective;
        if (SqliteModes.Resolve(effective) != SqliteMode.ReadWrite)
        {
            return SqliteText.ReadOnlyMode;
        }

        int rows = maxRows ?? SqliteQueryTool.DefaultRows(effective);
        if (rows < SqliteQueryTool.MinRows || rows > SqliteQueryTool.MaxRows)
        {
            return SqlText.BadMaxRows(SqliteQueryTool.MinRows, SqliteQueryTool.MaxRows);
        }

        var kinds = SqliteStatementKinds.Resolve(effective);
        if (kinds.Count == 0)
        {
            return SqliteText.NoKindsAllowed;
        }

        if (SqliteWriteGate.Check(sql, kinds) is { } refused)
        {
            return refused;
        }

        if (Files.Resolve(database, create, out var unresolved, out bool creating) is not { } target)
        {
            return SqliteText.Error(unresolved!);
        }

        // Create is asked of the kinds only when the file is to be made (2026-10-24, the database review's P3: create: true on a file already
        // there was refused with Create unticked, though the schema says it is then just opened).
        if (creating && !kinds.Contains(SqliteStatementKind.Create))
        {
            return SqliteText.CreateNotAllowed;
        }

        // The file guard's other rules hold for a change (2026-10-24, review A10: the target resolved as a read, so .git, .neon and the app's
        // own home were never judged for a create or a change).
        if (Files.WriteRefusal(target) is { } guarded)
        {
            return guarded;
        }

        string body = SqliteReadOnlyGate.Body(sql);

        // A read asks nothing (later on 2026-10-05, the user's ask): it runs as sqlite_query's do — the file opened read-only,
        // query_only on, the transaction rolled back — so it cannot change the file whatever it is, and is no change to audit — and only
        // what sqlite_query's own gate passes counts as one (2026-10-24, review F11: an EXPLAIN-led statement ran on this path unchecked).
        // A read that would make a new file is not one: it asks and runs as a create.
        if (!creating && SqliteWriteGate.Classify(body) == SqliteStatementKind.Read && ReadGatePasses(body))
        {
            var read = await Task.Run(() => SqliteAccess.Run(target, [body], parameters, rows, TimeoutSeconds(effective), cancellationToken), cancellationToken).ConfigureAwait(false);
            return read.Outcome == SqlOutcome.Ok ? SqliteText.Query(read, rows, SqlTool.ResultChars(effective)) : SqliteText.Error(read);
        }

        if (_allow is null)
        {
            return SqliteText.NoPane;
        }

        switch (await _allow(target, body, creating, parameters, cancellationToken).ConfigureAwait(false))
        {
            case null:
                return SqliteText.NoPane;
            case false:
                return SqliteText.Declined;
        }

        // Every attempted change audited whatever its end (2026-10-24, the database review's B17, decision DB7: a failed create whose file
        // would not go away was never in the log), the whole statement redacted and scrubbed (H3, J4).
        SqlRun run;
        try
        {
            run = await SqliteAccess.ExecuteAsync(target, creating, body, parameters, rows, TimeoutSeconds(effective), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Diagnostics.DiagnosticLog.Info(SqliteConfigFile.Category, SqliteText.AuditLogLine(target.Name, target.FullPath, creating, DatabaseAudit.Word(DatabaseAuditOutcome.Canceled, null), DatabaseAudit.Statement(body)));
            throw;
        }

        // Rows are counted for a change to data alone (2026-10-24, review G16: DDL, a PRAGMA or VACUUM said "Changed 0 rows"; the server
        // families say "Ran it").
        if (SqliteWriteGate.Classify(body) is not (SqliteStatementKind.Data or SqliteStatementKind.Delete))
        {
            run = run with { Changes = null };
        }

        Diagnostics.DiagnosticLog.Info(SqliteConfigFile.Category, SqliteText.AuditLogLine(target.Name, target.FullPath, creating && run.Outcome == SqlOutcome.Ok, DatabaseAudit.Word(DatabaseAudit.OutcomeOf(run), run.Changes), DatabaseAudit.Statement(body)));
        return run.Outcome != SqlOutcome.Ok ? SqliteText.Error(run) : SqliteText.Executed(run, creating, rows, SqlTool.ResultChars(effective));
    }

    /// <summary>
    /// Whether <paramref name="body"/> is a read <c>sqlite_query</c>'s gate passes (F11), an <c>EXPLAIN</c> or <c>EXPLAIN QUERY PLAN</c> in
    /// front of one too: the plan of a read is a read; <c>EXPLAIN PRAGMA …</c> and the like go to the pane.
    /// </summary>
    public static bool ReadGatePasses(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var explain = ExplainLead().Match(body);
        return SqliteReadOnlyGate.Check(explain.Success ? body[explain.Length..] : body) is null;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^\s*EXPLAIN\s+(?:QUERY\s+PLAN\s+)?", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex ExplainLead();

    protected override async ValueTask<object?> InvokeToolAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!ToolArguments.TryReadInt32(arguments, SqliteQueryTool.MaxRowsArgument, out var max, out var raw))
        {
            return ClockText.BadInteger(SqliteQueryTool.MaxRowsArgument, raw);
        }

        if (!ToolArguments.TryReadBoolean(arguments, CreateArgument, out var create, out raw))
        {
            return NeonSidekick.Files.FileText.BadBoolean(CreateArgument, raw);
        }

        if (SqliteQueryTool.ReadParams(arguments, out var error) is not { } parameters)
        {
            return error;
        }

        return await RunAsync(ToolArguments.ReadString(arguments, SqliteQueryTool.SqlArgument), Optional(arguments, DatabaseArgument), parameters, max, create ?? false, cancellationToken).ConfigureAwait(false);
    }
}
