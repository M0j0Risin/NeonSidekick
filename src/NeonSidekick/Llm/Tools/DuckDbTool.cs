using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.DuckDb;
using NeonSidekick.Settings;
using NeonSidekick.Sql;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// What the DuckDB tools share (2026-10-25), <see cref="SqliteTool"/>'s twin: the <see cref="DuckDbAccess"/> door, the settings in force at
/// each call, the optional <c>database</c> (a name from <c>duckdb.json</c>, a DuckDB file in the working directory by its path while
/// <c>DuckDB sandbox files</c> is on, or left out: the only named one, or the data files alone when none is named), the statement timeout,
/// and the table lookup <c>duckdb_describe</c> needs. A refused run is <see cref="DuckDbText.Error"/>'s sentence.
/// </summary>
public abstract class DuckDbTool : ServerTool<DuckDbAccess>
{
    public const string DatabaseArgument = "database";

    /// <summary>The <c>database</c> property every schema but <c>duckdb_databases</c> carries.</summary>
    public const string DatabaseProperty = "\"database\": { \"type\": \"string\", \"description\": \"The database's name (duckdb_databases lists them), or a .duckdb file in the working directory by its path; leave it out when there is one, or to query data files alone when none is named (\\\":memory:\\\" queries data files alone always).\" }";

    protected DuckDbTool(DuckDbAccess duckDb, Func<AppSettingsData> effective) : base(duckDb, effective)
    {
    }

    protected DuckDbAccess Files => Access;

    /// <summary>DuckDB's tools never took another argument for where they work: nothing is retired.</summary>
    protected sealed override string? RetiredArgument => null;

    protected sealed override string RetiredRefusal => "";

    /// <summary>The statement timeout: the setting, clamped to the range a hand-edited value may have left.</summary>
    public static int TimeoutSeconds(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return Math.Clamp(effective.DuckDbQueryTimeoutSeconds, AppSettingsData.MinSqlQueryTimeoutSeconds, AppSettingsData.MaxSqlQueryTimeoutSeconds);
    }

    /// <summary>Runs the app's own statements on the call's database, capped at <see cref="DuckDbAccess.MaxCatalogRows"/>.</summary>
    protected async Task<SqlRun> CatalogAsync(string? database, IReadOnlyList<string> statements, IReadOnlyList<SqlParameterValue> parameters, CancellationToken cancellationToken) =>
        SqlText.CatalogRun(await Files.RunAsync(database, statements, parameters, DuckDbAccess.MaxCatalogRows, TimeoutSeconds(Effective), cancellationToken).ConfigureAwait(false));

    /// <summary>The built-in readers' sentence for a description, while data files may be read; else nothing.</summary>
    protected string FilesSentence => Files.SandboxOn
        ? " CSV, Parquet and JSON files in the working directory are read by their paths: FROM 'data/sales.csv', or the readers " + DuckDbFunctions.ReaderNames + " ('data/*.csv' within one folder)."
        : "";
}

/// <summary>
/// The user's allow for one <c>duckdb_execute</c> change: the file, the statement (as the model wrote it), whether the file is made, and the
/// values its placeholders are bound to. True to run, false declined, null when no pane could ask.
/// </summary>
public delegate Task<bool?> DuckDbWriteAllow(DuckDbTarget target, string sql, bool creating, IReadOnlyList<SqlParameterValue> parameters, CancellationToken cancellationToken);

/// <summary><c>duckdb_databases()</c>: the named databases of <c>duckdb.json</c> — the file, the description — and whether working-directory files may be named too. Opens nothing.</summary>
public sealed class DuckDbDatabasesTool : DuckDbTool
{
    public const string ToolName = "duckdb_databases";

    private static readonly JsonElement Schema = ToolSchema.Parse("""{ "type": "object", "properties": {} }""");

    public DuckDbDatabasesTool(DuckDbAccess duckDb, Func<AppSettingsData> effective) : base(duckDb, effective)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Lists the DuckDB databases the user has named, each one's file and what it holds, and whether DuckDB files and data files " +
        "in the working directory can be named by their paths. Pass a name (or such a path) as \"database\" to the other duckdb_ tools.";

    public override JsonElement JsonSchema => Schema;

    public string Describe() => DuckDbText.Databases(Files.Catalog(), Files.SandboxOn);

    protected override ValueTask<object?> InvokeToolAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) => new(Describe());
}

/// <summary><c>duckdb_tables(database?, pattern?)</c>: the tables and views of the database, with their kind.</summary>
public sealed class DuckDbTablesTool : DuckDbTool
{
    public const string ToolName = "duckdb_tables";
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

    public DuckDbTablesTool(DuckDbAccess duckDb, Func<AppSettingsData> effective) : base(duckDb, effective)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Lists the tables and views of a DuckDB database. Narrow it with pattern; then duckdb_describe a table before querying it.";

    public override JsonElement JsonSchema => Schema;

    public async Task<string> DescribeAsync(string? database, string? pattern, CancellationToken cancellationToken)
    {
        var run = await CatalogAsync(database, [DuckDbCatalogQueries.Tables], [new SqlParameterValue("pattern", OracleTablesTool.LikePattern(pattern))], cancellationToken).ConfigureAwait(false);
        return run.Outcome == SqlOutcome.Ok ? DuckDbText.Tables(run, NeonSidekick.Files.WorkingDirectory.MaxReadChars) : DuckDbText.Error(run);
    }

    protected override async ValueTask<object?> InvokeToolAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return await DescribeAsync(Optional(arguments, DatabaseArgument), Optional(arguments, PatternArgument), cancellationToken).ConfigureAwait(false);
    }
}

/// <summary><c>duckdb_describe(table, database?)</c>: a table's or view's columns, its foreign keys out and in, its indexes and its CREATE text, in one call.</summary>
public sealed class DuckDbDescribeTool : DuckDbTool
{
    public const string ToolName = "duckdb_describe";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        $$"""
        {
          "type": "object",
          "properties": {
            "table": { "type": "string", "description": "The table or view, as name or schema.name (case-insensitive). For a data file, use duckdb_query with DESCRIBE 'file.csv'." },
            {{DatabaseProperty}}
          },
          "required": ["table"]
        }
        """);

    public DuckDbDescribeTool(DuckDbAccess duckDb, Func<AppSettingsData> effective) : base(duckDb, effective)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Shows a DuckDB table's or view's columns (type, nullability, primary key, default), its foreign keys both ways, its indexes " +
        "and the CREATE statement — what to read before writing a query on it.";

    public override JsonElement JsonSchema => Schema;

    public async Task<string> DescribeAsync(string table, string? database, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(table))
        {
            return DuckDbText.NoTable;
        }

        var found = await CatalogAsync(database, [DuckDbCatalogQueries.Resolve], [new SqlParameterValue("name", table.Trim())], cancellationToken).ConfigureAwait(false);
        if (found.Outcome != SqlOutcome.Ok)
        {
            return DuckDbText.Error(found);
        }

        if (found.Grids is not [{ Rows: [var row, ..] }, ..])
        {
            return DuckDbText.TableNotFound(table.Trim(), found.Connection);
        }

        // schema, name, kind
        var run = await CatalogAsync(database, DuckDbCatalogQueries.Describe, [new SqlParameterValue("schema", row[0]), new SqlParameterValue("name", row[1])], cancellationToken).ConfigureAwait(false);
        string shown = row[0] == "main" ? row[1] : row[0] + "." + row[1];
        return run.Outcome == SqlOutcome.Ok ? DuckDbText.Describe(shown, row[2], run, NeonSidekick.Files.WorkingDirectory.MaxReadChars) : DuckDbText.Error(run);
    }

    protected override async ValueTask<object?> InvokeToolAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return await DescribeAsync(ToolArguments.ReadString(arguments, TableArgument), Optional(arguments, DatabaseArgument), cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// <c>duckdb_query(sql, database?, params?, max_rows?)</c>: one read-only statement (SELECT, WITH, VALUES, FROM, DESCRIBE, SUMMARIZE, PIVOT,
/// EXPLAIN …), passed by <see cref="DuckDbReadOnlyGate"/>, its data files checked and named by full path (<see cref="DuckDbFilePaths"/>), then
/// run under every layer of <see cref="DuckDbAccess"/>; the rows as a Markdown table under a header that says when the cap cut it.
/// <c>params</c> is one object, each name bound to its <c>$name</c> — never spliced into the text.
/// </summary>
public sealed class DuckDbQueryTool : DuckDbTool
{
    public const string ToolName = "duckdb_query";
    public const string SqlArgument = "sql";
    public const string ParamsArgument = "params";
    public const string MaxRowsArgument = "max_rows";

    public const int MinRows = AppSettingsData.MinSqlQueryMaxRows;
    public const int MaxRows = AppSettingsData.MaxSqlQueryMaxRows;

    /// <summary>The <c>params</c> property both query tools carry.</summary>
    public const string ParamsProperty = "\"params\": { \"type\": \"object\", \"description\": \"Values for the named placeholders in the SQL ($name), each by its name, e.g. {\\\"id\\\": 101} for $id; strings, numbers, true, false or null.\" }";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        $$"""
        {
          "type": "object",
          "properties": {
            "sql": { "type": "string", "description": "One DuckDB read: SELECT (LIMIT n), WITH, VALUES, FROM-first, DESCRIBE, SUMMARIZE, PIVOT or EXPLAIN. No INSERT, UPDATE, DELETE, DDL, COPY, ATTACH, SET or second statement." },
            {{DatabaseProperty}},
            {{ParamsProperty}},
            "max_rows": { "type": "integer", "description": "How many rows at most, 1 to 100000. Leave it out for the user's default." }
          },
          "required": ["sql"]
        }
        """);

    public DuckDbQueryTool(DuckDbAccess duckDb, Func<AppSettingsData> effective) : base(duckDb, effective)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Runs one read-only statement on a DuckDB database and returns the rows as a table. " +
        "Bind values as $name through params rather than writing them into the text. " +
        "The header says when more rows exist than were returned." + FilesSentence;

    public override JsonElement JsonSchema => Schema;

    /// <summary>The row cap a call without <c>max_rows</c> gets: the setting, clamped.</summary>
    public static int DefaultRows(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return Math.Clamp(effective.DuckDbQueryMaxRows, MinRows, MaxRows);
    }

    public async Task<string> RunAsync(string sql, string? database, IReadOnlyList<SqlParameterValue> parameters, int? maxRows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(parameters);
        var effective = Effective;
        int rows = maxRows ?? DefaultRows(effective);
        if (rows < MinRows || rows > MaxRows)
        {
            return SqlText.BadMaxRows(MinRows, MaxRows);
        }

        string body = DuckDbReadOnlyGate.Body(sql);
        if (DuckDbReadOnlyGate.Inspect(body, out var files) is { } refused)
        {
            return refused;
        }

        if (Files.Resolve(database, out var unresolved) is not { } target)
        {
            return DuckDbText.Error(unresolved!);
        }

        var sandbox = Files.Sandbox();
        if (DuckDbFilePaths.Rewrite(body, files, sandbox, cancellationToken, out var prepared) is { } pathRefused)
        {
            return pathRefused;
        }

        string? root = sandbox?.Root;
        var run = await Task.Run(() => DuckDbAccess.Run(target, [prepared], parameters, rows, TimeoutSeconds(effective), root, cancellationToken), cancellationToken).ConfigureAwait(false);
        return run.Outcome == SqlOutcome.Ok ? DuckDbText.Query(run, rows, SqlTool.ResultChars(effective)) : DuckDbText.Error(run);
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

        return await RunAsync(ToolArguments.ReadString(arguments, SqlArgument), Optional(arguments, DatabaseArgument), parameters, max, cancellationToken).ConfigureAwait(false);
    }

    /// <summary><c>params</c> as <c>duckdb_query</c> and <c>duckdb_execute</c> take it: one object, each name read as DuckDB reads a <c>$name</c>; null with the <c>Error:</c> sentence.</summary>
    internal static IReadOnlyList<SqlParameterValue>? ReadParams(AIFunctionArguments arguments, out string error)
    {
        error = "";
        if (!ToolArguments.TryReadObjectList(arguments, ParamsArgument, out var objects, out var sent) || objects.Count > 1)
        {
            error = DuckDbText.BadParams(sent);
            return null;
        }

        if (objects.Count == 0)
        {
            return [];
        }

        if (SqlQueryTool.ReadParameters(objects[0], out var bad, DuckDbReadOnlyGate.ParamName, DuckDbText.BadParamName) is not { } read)
        {
            error = bad ?? "";
            return null;
        }

        return read;
    }
}

/// <summary>
/// <c>duckdb_execute(sql, database?, params?, max_rows?, create?)</c>: one statement that may change the database, offered only while
/// <c>DuckDB mode</c> is <c>read-write</c>, a pane can ask and <c>DuckDB statements allowed</c> ticks a kind (all checked again at every call).
/// A read (reading ticked) asks nothing and runs on the read path. <see cref="DuckDbWriteGate"/> first, then the database (with <c>create</c>,
/// a new file in the working directory), the file guard, the data files, then the user's allow — Deny / Allow once / Allow for this session,
/// per file — then <see cref="DuckDbAccess.Execute"/>, committed as it runs. Every change is written to the log (the audit line). Plan mode
/// drops it.
/// </summary>
public sealed class DuckDbExecuteTool : DuckDbTool
{
    public const string ToolName = "duckdb_execute";
    public const string CreateArgument = "create";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        $$"""
        {
          "type": "object",
          "properties": {
            "sql": { "type": "string", "description": "One DuckDB statement of a kind this tool's description lists (RETURNING allowed). No COPY, ATTACH, SET, BEGIN/COMMIT or second statement." },
            {{DatabaseProperty}},
            {{DuckDbQueryTool.ParamsProperty}},
            "max_rows": { "type": "integer", "description": "How many returned rows (RETURNING) to show at most, 1 to 100000. Leave it out for the user's default." },
            "create": { "type": "boolean", "description": "true to create a new database file at \"database\", a path in the working directory ending .duckdb or .ddb (its folder must exist); only when the description says creating is allowed. An existing file is just opened." }
          },
          "required": ["sql"]
        }
        """);

    private readonly DuckDbWriteAllow? _allow;

    /// <param name="allow">The user's allow for one change: true to run, false declined, null when no pane could ask. Null where nothing can ask: every call is refused.</param>
    public DuckDbExecuteTool(DuckDbAccess duckDb, Func<AppSettingsData> effective, DuckDbWriteAllow? allow) : base(duckDb, effective)
    {
        _allow = allow;
    }

    public override string Name => ToolName;

    /// <summary>The description, read at each turn: it names the kinds <c>DuckDB statements allowed</c> ticks, and create only with creating.</summary>
    public override string Description => DescribeFor(DuckDbStatementKinds.Resolve(Effective));

    /// <summary>The description for <paramref name="kinds"/>. Pinned.</summary>
    public static string DescribeFor(IReadOnlyList<DuckDbStatementKind> kinds)
    {
        ArgumentNullException.ThrowIfNull(kinds);
        return "Runs one statement that changes a DuckDB database. The user allows only these kinds: " + DuckDbStatementKinds.Describe(kinds) + ". " +
            (kinds.Contains(DuckDbStatementKind.Create) ? "With create it makes a new database file in the working directory. " : "") +
            "The user allows each change first; a change is permanent once it runs. " +
            (kinds.Contains(DuckDbStatementKind.Read) ? "A read runs without asking, on the file opened read-only. " : "") +
            "Load a data file with CREATE TABLE t AS SELECT * FROM 'data.csv' (COPY is refused). Bind values as $name through params. For reading, use duckdb_query.";
    }

    public override JsonElement JsonSchema => Schema;

    public async Task<string> RunAsync(string sql, string? database, IReadOnlyList<SqlParameterValue> parameters, int? maxRows, bool create, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(parameters);
        var effective = Effective;
        if (DuckDbModes.Resolve(effective) != DuckDbMode.ReadWrite)
        {
            return DuckDbText.ReadOnlyMode;
        }

        int rows = maxRows ?? DuckDbQueryTool.DefaultRows(effective);
        if (rows < DuckDbQueryTool.MinRows || rows > DuckDbQueryTool.MaxRows)
        {
            return SqlText.BadMaxRows(DuckDbQueryTool.MinRows, DuckDbQueryTool.MaxRows);
        }

        var kinds = DuckDbStatementKinds.Resolve(effective);
        if (kinds.Count == 0)
        {
            return DuckDbText.NoKindsAllowed;
        }

        string body = DuckDbReadOnlyGate.Body(sql);
        if (DuckDbWriteGate.Check(body, kinds, out var files) is { } refused)
        {
            return refused;
        }

        if (Files.Resolve(database, create, out var unresolved, out bool creating) is not { } target)
        {
            return DuckDbText.Error(unresolved!);
        }

        if (creating && !kinds.Contains(DuckDbStatementKind.Create))
        {
            return DuckDbText.CreateNotAllowed;
        }

        var sandbox = Files.Sandbox();
        string? root = sandbox?.Root;

        // A read asks nothing: it runs as duckdb_query's do, under every read layer, and only what duckdb_query's own gate passes counts as
        // one. A read that would make a new file is not one: it asks and runs as a create.
        if (!creating && DuckDbWriteGate.Classify(body) == DuckDbStatementKind.Read && DuckDbReadOnlyGate.Inspect(body, out var readFiles) is null)
        {
            if (DuckDbFilePaths.Rewrite(body, readFiles, sandbox, cancellationToken, out var readPrepared) is { } readRefused)
            {
                return readRefused;
            }

            var read = await Task.Run(() => DuckDbAccess.Run(target, [readPrepared], parameters, rows, TimeoutSeconds(effective), root, cancellationToken), cancellationToken).ConfigureAwait(false);
            return read.Outcome == SqlOutcome.Ok ? DuckDbText.Query(read, rows, SqlTool.ResultChars(effective)) : DuckDbText.Error(read);
        }

        if (target.InMemory)
        {
            return DuckDbText.MemoryIsGone;
        }

        if (Files.WriteRefusal(target) is { } guarded)
        {
            return guarded;
        }

        if (DuckDbFilePaths.Rewrite(body, files, sandbox, cancellationToken, out var prepared) is { } pathRefused)
        {
            return pathRefused;
        }

        if (_allow is null)
        {
            return DuckDbText.NoPane;
        }

        switch (await _allow(target, body, creating, parameters, cancellationToken).ConfigureAwait(false))
        {
            case null:
                return DuckDbText.NoPane;
            case false:
                return DuckDbText.Declined;
        }

        // Every attempted change audited whatever its end (decision DB7), the statement as the model wrote it, redacted and scrubbed.
        SqlRun run;
        try
        {
            run = await DuckDbAccess.ExecuteAsync(target, creating, prepared, parameters, rows, TimeoutSeconds(effective), root, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Diagnostics.DiagnosticLog.Info(DuckDbConfigFile.Category, DuckDbText.AuditLogLine(target.Name, target.FullPath, creating, DatabaseAudit.Word(DatabaseAuditOutcome.Canceled, null), DatabaseAudit.Statement(body)));
            throw;
        }

        // Rows are counted for a change to data alone (SQLite's G16).
        if (DuckDbWriteGate.Classify(body) is not (DuckDbStatementKind.Data or DuckDbStatementKind.Delete))
        {
            run = run with { Changes = null };
        }

        Diagnostics.DiagnosticLog.Info(DuckDbConfigFile.Category, DuckDbText.AuditLogLine(target.Name, target.FullPath, creating && run.Outcome == SqlOutcome.Ok, DatabaseAudit.Word(DatabaseAudit.OutcomeOf(run), run.Changes), DatabaseAudit.Statement(body)));
        return run.Outcome != SqlOutcome.Ok ? DuckDbText.Error(run) : DuckDbText.Executed(run, creating, rows, SqlTool.ResultChars(effective));
    }

    protected override async ValueTask<object?> InvokeToolAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!ToolArguments.TryReadInt32(arguments, DuckDbQueryTool.MaxRowsArgument, out var max, out var raw))
        {
            return ClockText.BadInteger(DuckDbQueryTool.MaxRowsArgument, raw);
        }

        if (!ToolArguments.TryReadBoolean(arguments, CreateArgument, out var create, out raw))
        {
            return NeonSidekick.Files.FileText.BadBoolean(CreateArgument, raw);
        }

        if (DuckDbQueryTool.ReadParams(arguments, out var error) is not { } parameters)
        {
            return error;
        }

        return await RunAsync(ToolArguments.ReadString(arguments, DuckDbQueryTool.SqlArgument), Optional(arguments, DatabaseArgument), parameters, max, create ?? false, cancellationToken).ConfigureAwait(false);
    }
}
