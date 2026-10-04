using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Settings;
using NeonSidekick.Sql;
using NeonSidekick.Sqlite;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// What the four SQLite tools share (2026-10-04), <see cref="MySqlTool"/>'s shape: the <see cref="SqliteAccess"/> door, the settings
/// in force at each call, the optional <c>database</c> (a name from <c>sqlite.json</c>, or a file in the working directory by its
/// path while <c>SQLite sandbox files</c> is on; the default when left out), the statement timeout, and the table lookup
/// <c>sqlite_describe</c> needs. A refused run is <see cref="SqliteText.Error"/>'s sentence.
/// </summary>
public abstract class SqliteTool : AIFunction
{
    public const string DatabaseArgument = "database";
    public const string TableArgument = "table";

    /// <summary>The <c>database</c> property every schema but <c>sqlite_databases</c> carries.</summary>
    public const string DatabaseProperty = "\"database\": { \"type\": \"string\", \"description\": \"The database's name (sqlite_databases lists them), or a .db file in the working directory by its path; leave it out for the default one.\" }";

    private readonly SqliteAccess _sqlite;
    private readonly Func<AppSettingsData> _effective;

    protected SqliteTool(SqliteAccess sqlite, Func<AppSettingsData> effective)
    {
        _sqlite = sqlite ?? throw new ArgumentNullException(nameof(sqlite));
        _effective = effective ?? throw new ArgumentNullException(nameof(effective));
    }

    protected SqliteAccess Files => _sqlite;

    protected AppSettingsData Effective => _effective();

    /// <summary>The statement timeout: the setting, clamped to the range a hand-edited value may have left.</summary>
    public static int TimeoutSeconds(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return Math.Clamp(effective.SqliteQueryTimeoutSeconds, AppSettingsData.MinSqlQueryTimeoutSeconds, AppSettingsData.MaxSqlQueryTimeoutSeconds);
    }

    /// <summary>A string argument, or null when blank.</summary>
    protected static string? Optional(AIFunctionArguments arguments, string name) =>
        ToolArguments.ReadString(arguments, name).Trim() is { Length: > 0 } text ? text : null;

    /// <summary>Runs the app's own statements on the call's database, capped at <see cref="SqliteAccess.MaxCatalogRows"/>.</summary>
    protected Task<SqlRun> CatalogAsync(string? database, IReadOnlyList<string> statements, IReadOnlyList<SqlParameterValue> parameters, CancellationToken cancellationToken) =>
        _sqlite.RunAsync(database, Effective.SqliteDefaultDatabase, statements, parameters, SqliteAccess.MaxCatalogRows, TimeoutSeconds(Effective), cancellationToken);
}

/// <summary><c>sqlite_databases()</c>: the named databases of <c>sqlite.json</c> — the file, the description, the default marked — and whether a working-directory file may be named too. Opens nothing.</summary>
public sealed class SqliteDatabasesTool : SqliteTool
{
    public const string ToolName = "sqlite_databases";

    private static readonly JsonElement Schema = ToolSchema.Parse("""{ "type": "object", "properties": {} }""");

    public SqliteDatabasesTool(SqliteAccess sqlite, Func<AppSettingsData> effective) : base(sqlite, effective)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Lists the SQLite databases the user has named, each one's file and what it holds, the default marked, and whether a database file " +
        "in the working directory can be named by its path. Pass a name (or such a path) as \"database\" to the other sqlite_ tools.";

    public override JsonElement JsonSchema => Schema;

    public string Describe() => SqliteText.Databases(Files.Catalog(), Effective.SqliteDefaultDatabase, Files.SandboxOn);

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) => new(Describe());
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

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
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

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
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

    private static readonly JsonElement Schema = ToolSchema.Parse(
        $$"""
        {
          "type": "object",
          "properties": {
            "sql": { "type": "string", "description": "One SQLite SELECT (LIMIT n); a WITH clause may lead it. No INSERT, UPDATE, DELETE, DDL, PRAGMA, ATTACH or second statement." },
            {{DatabaseProperty}},
            "params": { "type": "object", "description": "Values for @name placeholders in the SQL, e.g. {\"id\": 101} for @id; strings, numbers, true, false or null." },
            "max_rows": { "type": "integer", "description": "How many rows at most, 1 to 100000. Leave it out for the user's default." }
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
        "The header says when more rows exist than were returned.";

    public override JsonElement JsonSchema => Schema;

    /// <summary>The row cap a call without <c>max_rows</c> gets: the setting, clamped.</summary>
    public static int DefaultRows(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return Math.Clamp(effective.SqliteQueryMaxRows, MinRows, MaxRows);
    }

    public async Task<string> RunAsync(string sql, string? database, IReadOnlyList<SqlParameterValue> parameters, int? maxRows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(parameters);
        int rows = maxRows ?? DefaultRows(Effective);
        if (rows < MinRows || rows > MaxRows)
        {
            return SqlText.BadMaxRows(MinRows, MaxRows);
        }

        if (SqliteReadOnlyGate.Check(sql) is { } refused)
        {
            return refused;
        }

        var run = await Files.RunAsync(database, Effective.SqliteDefaultDatabase, [SqliteReadOnlyGate.Body(sql)], parameters, rows, TimeoutSeconds(Effective), cancellationToken).ConfigureAwait(false);
        return run.Outcome == SqlOutcome.Ok ? SqliteText.Query(run, rows, SqlTool.ResultChars(Effective)) : SqliteText.Error(run);
    }

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!ToolArguments.TryReadInt32(arguments, MaxRowsArgument, out var max, out var raw))
        {
            return ClockText.BadInteger(MaxRowsArgument, raw);
        }

        if (!ToolArguments.TryReadObjectList(arguments, ParamsArgument, out var objects, out var sent) || objects.Count > 1)
        {
            return SqliteText.BadParams(sent);
        }

        IReadOnlyList<SqlParameterValue> parameters = [];
        if (objects.Count == 1)
        {
            if (SqlQueryTool.ReadParameters(objects[0], out var error) is not { } read)
            {
                return error;
            }

            parameters = read;
        }

        return await RunAsync(ToolArguments.ReadString(arguments, SqlArgument), Optional(arguments, DatabaseArgument), parameters, max, cancellationToken).ConfigureAwait(false);
    }
}
