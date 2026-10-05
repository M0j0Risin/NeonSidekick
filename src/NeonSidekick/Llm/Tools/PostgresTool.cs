using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Postgres;
using NeonSidekick.Settings;
using NeonSidekick.Sql;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// What the nine PostgreSQL tools share (2026-10-04), <see cref="MySqlTool"/>'s shape: the <see cref="PostgresAccess"/> door, the
/// settings in force at each call, the optional <c>connection</c> (a name from <c>postgres.json</c>; the default when left out),
/// <c>database</c> (another database on the same server: a connection to it, Postgres cannot switch in-session) and <c>schema</c> (the
/// listings' scope), the statement timeout, and the table lookup the per-table tools need.
/// </summary>
public abstract class PostgresTool : AIFunction
{
    public const string ConnectionArgument = "connection";
    public const string DatabaseArgument = "database";
    public const string SchemaArgument = "schema";
    public const string TableArgument = "table";

    public const string ConnectionProperty = "\"connection\": { \"type\": \"string\", \"description\": \"The PostgreSQL connection's name (postgres_connections lists them); leave it out for the default one.\" }";
    public const string DatabaseProperty = "\"database\": { \"type\": \"string\", \"description\": \"Another database on the same server (postgres_databases lists them); leave it out for the connection's own.\" }";
    public const string SchemaProperty = "\"schema\": { \"type\": \"string\", \"description\": \"Only this schema (postgres_schemas lists them); leave it out for every schema.\" }";

    private readonly PostgresAccess _postgres;
    private readonly Func<AppSettingsData> _effective;

    protected PostgresTool(PostgresAccess postgres, Func<AppSettingsData> effective)
    {
        _postgres = postgres ?? throw new ArgumentNullException(nameof(postgres));
        _effective = effective ?? throw new ArgumentNullException(nameof(effective));
    }

    protected PostgresAccess Server => _postgres;

    protected AppSettingsData Effective => _effective();

    public static int TimeoutSeconds(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return Math.Clamp(effective.PostgresQueryTimeoutSeconds, AppSettingsData.MinSqlQueryTimeoutSeconds, AppSettingsData.MaxSqlQueryTimeoutSeconds);
    }

    protected static string? Optional(AIFunctionArguments arguments, string name) =>
        ToolArguments.ReadString(arguments, name).Trim() is { Length: > 0 } text ? text : null;

    protected Task<SqlRun> CatalogAsync(string? connection, string? database, IReadOnlyList<string> statements, IReadOnlyList<SqlParameterValue> parameters, CancellationToken cancellationToken) =>
        _postgres.RunAsync(connection, Effective.PostgresDefaultConnection, database, statements, parameters, PostgresAccess.MaxCatalogRows, TimeoutSeconds(Effective), cancellationToken);

    protected sealed record TableMatch(string Schema, string Name, string Kind, string? Error)
    {
        public static TableMatch Refused(string error) => new("", "", "", error);
    }

    /// <summary>
    /// The table or view <paramref name="table"/> means: <c>schema.name</c> as written, else in <paramref name="schema"/>, else the one
    /// of that name in any schema — the exact spelling over a case-folded one, then the search path's; several is ambiguous with the
    /// candidates named.
    /// </summary>
    protected async Task<TableMatch> FindTableAsync(string? connection, string? database, string? schema, string table, CancellationToken cancellationToken)
    {
        if (PostgresAccess.Split(table) is not { } parts)
        {
            return TableMatch.Refused(PostgresText.BadTable(table));
        }

        var run = await CatalogAsync(connection, database, [PostgresCatalogQueries.Resolve], [new("schema", parts.Schema ?? schema), new("name", parts.Name)], cancellationToken).ConfigureAwait(false);
        if (run.Outcome != SqlOutcome.Ok)
        {
            return TableMatch.Refused(PostgresText.Error(run));
        }

        // schema, name, kind, exact, on the search path
        var rows = run.Grids.Count > 0 ? run.Grids[0].Rows.ToList() : [];
        var pick = rows.Where(r => r[3] == "true").ToList() is { Count: > 0 } exact ? exact : rows;
        if (pick.Count > 1 && pick.Where(r => r[4] == "true").ToList() is [var onPath])
        {
            pick = [onPath];
        }

        return pick.Count switch
        {
            0 => TableMatch.Refused(PostgresText.TableNotFound(table, PostgresText.Where(run))),
            1 => new TableMatch(pick[0][0], pick[0][1], pick[0][2], null),
            _ => TableMatch.Refused(PostgresText.TableAmbiguous(table, pick.Select(r => r[0] + "." + r[1]))),
        };
    }
}

public sealed class PostgresConnectionsTool : PostgresTool
{
    public const string ToolName = "postgres_connections";

    private static readonly JsonElement Schema = ToolSchema.Parse("""{ "type": "object", "properties": {} }""");

    public PostgresConnectionsTool(PostgresAccess postgres, Func<AppSettingsData> effective) : base(postgres, effective)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Lists the PostgreSQL connections the user has set up: each one's name, host, database, user and what it holds, the default marked. " +
        "Pass a name as \"connection\" to the other postgres_ tools.";

    public override JsonElement JsonSchema => Schema;

    public string Describe() => PostgresText.Connections(Server.Catalog(), Effective.PostgresDefaultConnection, DatabaseWriteModes.IsReadWrite(Effective.PostgresMode));

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) => new(Describe());
}

/// <summary>A catalog listing: the statement, its parameters from the arguments, and the nouns for the answer.</summary>
public abstract class PostgresListingTool : PostgresTool
{
    protected PostgresListingTool(PostgresAccess postgres, Func<AppSettingsData> effective) : base(postgres, effective)
    {
    }

    protected async Task<string> ListAsync(string? connection, string? database, string statement, IReadOnlyList<SqlParameterValue> parameters, string singular, string plural, CancellationToken cancellationToken)
    {
        var run = await CatalogAsync(connection, database, [statement], parameters, cancellationToken).ConfigureAwait(false);
        return run.Outcome != SqlOutcome.Ok ? PostgresText.Error(run)
            : PostgresText.Listing(singular, plural, run.Grids.Count > 0 ? run with { Grids = [SqlText.WithoutEmptyColumn(run.Grids[0], "description")] } : run, Files.WorkingDirectory.MaxReadChars);
    }
}

public sealed class PostgresDatabasesTool : PostgresListingTool
{
    public const string ToolName = "postgres_databases";

    private static readonly JsonElement Schema = ToolSchema.Parse("{ \"type\": \"object\", \"properties\": { " + ConnectionProperty + " } }");

    public PostgresDatabasesTool(PostgresAccess postgres, Func<AppSettingsData> effective) : base(postgres, effective)
    {
    }

    public override string Name => ToolName;

    public override string Description => "Lists the databases on a PostgreSQL server the account may connect to, with their size and encoding.";

    public override JsonElement JsonSchema => Schema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) =>
        await ListAsync(Optional(arguments, ConnectionArgument), null, PostgresCatalogQueries.Databases, [], "database", "databases", cancellationToken).ConfigureAwait(false);
}

public sealed class PostgresSchemasTool : PostgresListingTool
{
    public const string ToolName = "postgres_schemas";

    private static readonly JsonElement Schema = ToolSchema.Parse("{ \"type\": \"object\", \"properties\": { " + ConnectionProperty + ", " + DatabaseProperty + " } }");

    public PostgresSchemasTool(PostgresAccess postgres, Func<AppSettingsData> effective) : base(postgres, effective)
    {
    }

    public override string Name => ToolName;

    public override string Description => "Lists the schemas of a PostgreSQL database the account may use, with how many tables and views each holds and its owner.";

    public override JsonElement JsonSchema => Schema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) =>
        await ListAsync(Optional(arguments, ConnectionArgument), Optional(arguments, DatabaseArgument), PostgresCatalogQueries.Schemas, [], "schema", "schemas", cancellationToken).ConfigureAwait(false);
}

public sealed class PostgresTablesTool : PostgresListingTool
{
    public const string ToolName = "postgres_tables";
    public const string PatternArgument = "pattern";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        "{ \"type\": \"object\", \"properties\": { " + ConnectionProperty + ", " + DatabaseProperty + ", " + SchemaProperty + ", " +
        "\"pattern\": { \"type\": \"string\", \"description\": \"Only names containing this text (case-insensitive), or matching it as a LIKE pattern when it holds % over name or schema.name; leave it out for all.\" } } }");

    public PostgresTablesTool(PostgresAccess postgres, Func<AppSettingsData> effective) : base(postgres, effective)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Lists the tables and views of a PostgreSQL database as schema.name, with each one's kind, approximate row count and comment. " +
        "Narrow it with schema and pattern; then postgres_describe a table before querying it.";

    public override JsonElement JsonSchema => Schema;

    public Task<string> DescribeAsync(string? connection, string? database, string? schema, string? pattern, CancellationToken cancellationToken) =>
        ListAsync(connection, database, PostgresCatalogQueries.Tables, [new("schema", schema), new("pattern", OracleTablesTool.LikePattern(pattern))], "table or view", "tables and views", cancellationToken);

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) =>
        await DescribeAsync(Optional(arguments, ConnectionArgument), Optional(arguments, DatabaseArgument), Optional(arguments, SchemaArgument), Optional(arguments, PatternArgument), cancellationToken).ConfigureAwait(false);
}

public sealed class PostgresColumnsTool : PostgresListingTool
{
    public const string ToolName = "postgres_columns";
    public const string PatternArgument = "pattern";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        "{ \"type\": \"object\", \"properties\": { \"pattern\": { \"type\": \"string\", \"description\": \"The column name or part of it (case-insensitive; % as a LIKE wildcard).\" }, " +
        ConnectionProperty + ", " + DatabaseProperty + ", " + SchemaProperty + " }, \"required\": [\"pattern\"] }");

    public PostgresColumnsTool(PostgresAccess postgres, Func<AppSettingsData> effective) : base(postgres, effective)
    {
    }

    public override string Name => ToolName;

    public override string Description => "Finds every column whose name matches across a PostgreSQL database's tables and views: the table, the type, nullability and the comment.";

    public override JsonElement JsonSchema => Schema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (Optional(arguments, PatternArgument) is not { } pattern)
        {
            return PostgresText.NoPattern;
        }

        return await ListAsync(Optional(arguments, ConnectionArgument), Optional(arguments, DatabaseArgument), PostgresCatalogQueries.ColumnSearch, [new("schema", Optional(arguments, SchemaArgument)), new("pattern", OracleTablesTool.LikePattern(pattern))], "column", "columns", cancellationToken).ConfigureAwait(false);
    }
}

public sealed class PostgresDescribeTool : PostgresTool
{
    public const string ToolName = "postgres_describe";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        "{ \"type\": \"object\", \"properties\": { \"table\": { \"type\": \"string\", \"description\": \"The table or view: name or schema.name (case-insensitive unless quoted).\" }, " +
        ConnectionProperty + ", " + DatabaseProperty + ", " + SchemaProperty + " }, \"required\": [\"table\"] }");

    public PostgresDescribeTool(PostgresAccess postgres, Func<AppSettingsData> effective) : base(postgres, effective)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Shows a PostgreSQL table's or view's columns (type, nullability, primary key, default, comment), its foreign keys both ways, its indexes " +
        "and CHECK constraints — what to read before writing a query on it.";

    public override JsonElement JsonSchema => Schema;

    public async Task<string> DescribeAsync(string table, string? connection, string? database, string? schema, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(table))
        {
            return PostgresText.NoTable;
        }

        var found = await FindTableAsync(connection, database, schema, table, cancellationToken).ConfigureAwait(false);
        if (found.Error is { } error)
        {
            return error;
        }

        var run = await CatalogAsync(connection, database, PostgresCatalogQueries.Describe, [new("schema", found.Schema), new("name", found.Name)], cancellationToken).ConfigureAwait(false);
        return run.Outcome == SqlOutcome.Ok ? PostgresText.Describe(found.Schema, found.Name, found.Kind, run, Files.WorkingDirectory.MaxReadChars) : PostgresText.Error(run);
    }

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return await DescribeAsync(ToolArguments.ReadString(arguments, TableArgument), Optional(arguments, ConnectionArgument), Optional(arguments, DatabaseArgument), Optional(arguments, SchemaArgument), cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>The two per-schema-or-table listings, <c>postgres_relationships</c> and <c>postgres_indexes</c>: a table resolved first when given.</summary>
public abstract class PostgresScopedTool : PostgresListingTool
{
    protected PostgresScopedTool(PostgresAccess postgres, Func<AppSettingsData> effective) : base(postgres, effective)
    {
    }

    protected static readonly JsonElement ScopedSchema = ToolSchema.Parse(
        "{ \"type\": \"object\", \"properties\": { " + ConnectionProperty + ", " + DatabaseProperty + ", " + SchemaProperty + ", " +
        "\"table\": { \"type\": \"string\", \"description\": \"Only this table (name or schema.name); leave it out for the schema's, or every schema's.\" } } }");

    protected async Task<string> ScopedAsync(AIFunctionArguments arguments, string statement, string singular, string plural, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        string? connection = Optional(arguments, ConnectionArgument), database = Optional(arguments, DatabaseArgument), schema = Optional(arguments, SchemaArgument);
        string? name = null;
        if (Optional(arguments, TableArgument) is { } table)
        {
            var found = await FindTableAsync(connection, database, schema, table, cancellationToken).ConfigureAwait(false);
            if (found.Error is { } error)
            {
                return error;
            }

            (schema, name) = (found.Schema, found.Name);
        }

        return await ListAsync(connection, database, statement, [new("schema", schema), new("name", name)], singular, plural, cancellationToken).ConfigureAwait(false);
    }
}

public sealed class PostgresRelationshipsTool : PostgresScopedTool
{
    public const string ToolName = "postgres_relationships";

    public PostgresRelationshipsTool(PostgresAccess postgres, Func<AppSettingsData> effective) : base(postgres, effective)
    {
    }

    public override string Name => ToolName;

    public override string Description => "Lists the foreign keys of a PostgreSQL schema, or of one table either way: the join paths between tables.";

    public override JsonElement JsonSchema => ScopedSchema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) =>
        await ScopedAsync(arguments, PostgresCatalogQueries.Relationships, "foreign key", "foreign keys", cancellationToken).ConfigureAwait(false);
}

public sealed class PostgresIndexesTool : PostgresScopedTool
{
    public const string ToolName = "postgres_indexes";

    public PostgresIndexesTool(PostgresAccess postgres, Func<AppSettingsData> effective) : base(postgres, effective)
    {
    }

    public override string Name => ToolName;

    public override string Description => "Lists the indexes of a PostgreSQL schema or table: unique, primary, the definition, and how often each was scanned.";

    public override JsonElement JsonSchema => ScopedSchema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) =>
        await ScopedAsync(arguments, PostgresCatalogQueries.Indexes, "index", "indexes", cancellationToken).ConfigureAwait(false);
}

/// <summary>
/// <c>postgres_query(sql, connection?, database?, params?, max_rows?)</c>: one read-only SELECT, passed by <see cref="PostgresReadOnlyGate"/>,
/// then run in a read-only transaction that is rolled back under the session's own read-only default and <c>statement_timeout</c>; the
/// rows as a Markdown table. <c>params</c> is one object, each name bound as <c>@name</c>.
/// </summary>
public sealed class PostgresQueryTool : PostgresTool
{
    public const string ToolName = "postgres_query";
    public const string SqlArgument = "sql";
    public const string ParamsArgument = "params";
    public const string MaxRowsArgument = "max_rows";

    public const int MinRows = AppSettingsData.MinSqlQueryMaxRows;
    public const int MaxRows = AppSettingsData.MaxSqlQueryMaxRows;

    private static readonly JsonElement Schema = ToolSchema.Parse(
        "{ \"type\": \"object\", \"properties\": { " +
        "\"sql\": { \"type\": \"string\", \"description\": \"One PostgreSQL SELECT (LIMIT n); a WITH clause may lead it. No INSERT, UPDATE, DELETE, DDL, COPY, DO, SELECT INTO, FOR UPDATE or second statement.\" }, " +
        ConnectionProperty + ", " + DatabaseProperty + ", " +
        "\"params\": { \"type\": \"object\", \"description\": \"Values for @name placeholders in the SQL, e.g. {\\\"id\\\": 101} for @id; strings, numbers, true, false or null.\" }, " +
        "\"max_rows\": { \"type\": \"integer\", \"description\": \"How many rows at most, 1 to 100000. Leave it out for the user's default.\" } }, \"required\": [\"sql\"] }");

    public PostgresQueryTool(PostgresAccess postgres, Func<AppSettingsData> effective) : base(postgres, effective)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Runs one read-only SELECT on a PostgreSQL connection and returns the rows as a table. " +
        "Only a single SELECT (or WITH … SELECT) is accepted; bind values as @name through params rather than writing them into the text. " +
        "The header says when more rows exist than were returned.";

    public override JsonElement JsonSchema => Schema;

    public static int DefaultRows(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return Math.Clamp(effective.PostgresQueryMaxRows, MinRows, MaxRows);
    }

    public async Task<string> RunAsync(string sql, string? connection, string? database, IReadOnlyList<SqlParameterValue> parameters, int? maxRows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(parameters);
        int rows = maxRows ?? DefaultRows(Effective);
        if (rows < MinRows || rows > MaxRows)
        {
            return SqlText.BadMaxRows(MinRows, MaxRows);
        }

        if (PostgresReadOnlyGate.Check(sql) is { } refused)
        {
            return refused;
        }

        var run = await Server.RunAsync(connection, Effective.PostgresDefaultConnection, database, [PostgresReadOnlyGate.Body(sql)], parameters, rows, TimeoutSeconds(Effective), cancellationToken).ConfigureAwait(false);
        return run.Outcome == SqlOutcome.Ok ? PostgresText.Query(run, rows, SqlTool.ResultChars(Effective)) : PostgresText.Error(run);
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
            return PostgresText.BadParams(sent);
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

        return await RunAsync(ToolArguments.ReadString(arguments, SqlArgument), Optional(arguments, ConnectionArgument), Optional(arguments, DatabaseArgument), parameters, max, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// <c>postgres_execute(sql, connection?, database?, params?, max_rows?)</c> (2026-10-05, the user's ask: <c>sqlite_execute</c>
/// mirrored): one statement that may change a database, offered only while <c>PostgreSQL mode</c> is <c>read-write</c>, a pane can ask,
/// <c>PostgreSQL statements allowed</c> ticks a kind and an offered connection says <c>"access": "readwrite"</c> (all checked again at
/// every call). <see cref="PostgresWriteGate"/> first, then the connection, then a read runs as <c>postgres_query</c>'s do without
/// asking, else the user's allow — per connection and database — then <see cref="PostgresAccess.ExecuteAsync"/>, committed as it runs,
/// written to the log (<see cref="ServerExecute"/>). Plan mode drops it.
/// </summary>
public sealed class PostgresExecuteTool : PostgresTool
{
    public const string ToolName = "postgres_execute";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        "{ \"type\": \"object\", \"properties\": { " +
        "\"sql\": { \"type\": \"string\", \"description\": \"One PostgreSQL statement of a kind this tool's description lists (RETURNING allowed). No BEGIN/COMMIT, SET, GRANT or second statement.\" }, " +
        ConnectionProperty + ", " + DatabaseProperty + ", " +
        "\"params\": { \"type\": \"object\", \"description\": \"Values for @name placeholders in the SQL, e.g. {\\\"id\\\": 101} for @id; strings, numbers, true, false or null.\" }, " +
        "\"max_rows\": { \"type\": \"integer\", \"description\": \"How many returned rows (RETURNING) to show at most, 1 to 100000. Leave it out for the user's default.\" } }, \"required\": [\"sql\"] }");

    private readonly DatabaseWriteAllow? _allow;

    /// <param name="allow">The user's allow for one change. Null where nothing can ask: every change is refused.</param>
    public PostgresExecuteTool(PostgresAccess postgres, Func<AppSettingsData> effective, DatabaseWriteAllow? allow) : base(postgres, effective)
    {
        _allow = allow;
    }

    public override string Name => ToolName;

    /// <summary>The description, read at each turn: it names the kinds <c>PostgreSQL statements allowed</c> ticks.</summary>
    public override string Description => DescribeFor(ServerStatementKinds.Resolve(Effective.PostgresStatementsAllowed));

    /// <summary>The description for <paramref name="kinds"/>. Pinned.</summary>
    public static string DescribeFor(IReadOnlyList<ServerStatementKind> kinds) =>
        ServerWriteText.Describe(PostgresStatementKinds.Family, kinds, PostgresQueryTool.ToolName, "PostgreSQL SQL; bind values as @name through params.");

    public override JsonElement JsonSchema => Schema;

    public Task<string> RunAsync(string sql, string? connection, string? database, IReadOnlyList<SqlParameterValue> parameters, int? maxRows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(parameters);
        var effective = Effective;
        int rows = maxRows ?? PostgresQueryTool.DefaultRows(effective);
        if (rows < PostgresQueryTool.MinRows || rows > PostgresQueryTool.MaxRows)
        {
            return Task.FromResult(SqlText.BadMaxRows(PostgresQueryTool.MinRows, PostgresQueryTool.MaxRows));
        }

        int timeout = TimeoutSeconds(effective);
        var hooks = new ServerExecuteHooks(
            PostgresWriteGate.Check,
            PostgresWriteGate.Kinds,
            PostgresReadOnlyGate.Check,
            PostgresReadOnlyGate.Body,
            () => Server.Resolve(connection, effective.PostgresDefaultConnection, out var refused) is { } target
                ? (new ExecuteTarget(target.Name, target.Config.IsReadWrite, Place(target.Config, database)), null)
                : (null, PostgresText.Error(refused!)),
            async (body, token) =>
            {
                var run = await Server.RunAsync(connection, effective.PostgresDefaultConnection, database, [body], parameters, rows, timeout, token).ConfigureAwait(false);
                return run.Outcome == SqlOutcome.Ok ? PostgresText.Query(run, rows, SqlTool.ResultChars(effective)) : PostgresText.Error(run);
            },
            (body, token) => Server.ExecuteAsync(connection, effective.PostgresDefaultConnection, database, body, parameters, rows, timeout, token),
            PostgresText.Error);
        return ServerExecute.RunAsync(PostgresStatementKinds.Family, effective.PostgresMode, effective.PostgresStatementsAllowed, sql, rows, SqlTool.ResultChars(effective), hooks, _allow, cancellationToken);
    }

    /// <summary>The database a call works in: the one it names, else the connection's own, else <c>postgres</c>.</summary>
    public static string Place(PostgresConnectionConfig config, string? database)
    {
        ArgumentNullException.ThrowIfNull(config);
        return !string.IsNullOrWhiteSpace(database) ? database.Trim() : !string.IsNullOrWhiteSpace(config.Database) ? config.Database.Trim() : PostgresConnectionConfig.DefaultDatabase;
    }

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!ToolArguments.TryReadInt32(arguments, PostgresQueryTool.MaxRowsArgument, out var max, out var raw))
        {
            return ClockText.BadInteger(PostgresQueryTool.MaxRowsArgument, raw);
        }

        if (!ToolArguments.TryReadObjectList(arguments, PostgresQueryTool.ParamsArgument, out var objects, out var sent) || objects.Count > 1)
        {
            return PostgresText.BadParams(sent);
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

        return await RunAsync(ToolArguments.ReadString(arguments, PostgresQueryTool.SqlArgument), Optional(arguments, ConnectionArgument), Optional(arguments, DatabaseArgument), parameters, max, cancellationToken).ConfigureAwait(false);
    }
}
