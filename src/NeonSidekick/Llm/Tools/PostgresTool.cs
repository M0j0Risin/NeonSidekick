using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Postgres;
using NeonSidekick.Settings;
using NeonSidekick.Sql;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// What the PostgreSQL tools share (2026-10-04), <see cref="MySqlTool"/>'s shape: the <see cref="PostgresAccess"/> door, the
/// settings in force at each call, the optional <c>connection</c> (a name from <c>postgres.json</c>; left out, the only one offered, several refused since 2026-10-09) — each
/// connection reaching its own database alone since 2026-10-09 (the user's call: <c>database</c> and <c>postgres_databases</c> went, a
/// call that still sends one is refused) and, when its entry names one, its schema alone (<see cref="ScopeSchema"/>,
/// <see cref="OutsideAsync"/>) — <c>schema</c> (the listings' scope), the statement timeout, and the table lookup the per-table tools need.
/// </summary>
public abstract class PostgresTool : ServerTool<PostgresAccess>
{
    /// <summary>The argument the tools took until 2026-10-09; a call that still sends it is refused.</summary>
    public const string DatabaseArgument = "database";
    public const string SchemaArgument = "schema";

    public const string ConnectionProperty = "\"connection\": { \"type\": \"string\", \"description\": \"The PostgreSQL connection's name (postgres_connections lists them); it may be left out only when there is one.\" }";
    public const string SchemaProperty = "\"schema\": { \"type\": \"string\", \"description\": \"Only this schema (postgres_schemas lists them); leave it out for every schema (a connection held to one keeps to it).\" }";

    protected PostgresTool(PostgresAccess postgres, Func<AppSettingsData> effective) : base(postgres, effective)
    {
    }

    protected PostgresAccess Server => Access;

    public static int TimeoutSeconds(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return Math.Clamp(effective.PostgresQueryTimeoutSeconds, AppSettingsData.MinSqlQueryTimeoutSeconds, AppSettingsData.MaxSqlQueryTimeoutSeconds);
    }

    /// <summary>Every PostgreSQL tool refuses a call that still names a <c>database</c> (<see cref="ServerTool{TAccess}.InvokeCoreAsync"/>).</summary>
    protected sealed override string RetiredArgument => DatabaseArgument;

    protected sealed override string RetiredRefusal => PostgresText.NoDatabaseArgument;

    /// <summary>
    /// One call's connection, resolved once (2026-11-15, the database-tools review's #12/#138: each step resolved the file again, so an edit
    /// mid-call could mix two versions of the entry, and the lock's array was rebuilt four to six times): the access every step runs on
    /// (pinned to the entry as read now, or the tool's own when it doesn't resolve, so the run says why), the name to run it by, and the
    /// schemas it is held to as the <c>@lock</c> bind (<c>text[]</c>), or null for none.
    /// </summary>
    protected sealed record PostgresCall(PostgresAccess Access, string? Connection, string[]? Locks);

    /// <summary>The call's connection resolved and pinned (<see cref="PostgresCall"/>).</summary>
    protected PostgresCall Pin(string? connection) =>
        Server.Resolve(connection, out _) is { } target
            ? new PostgresCall(Server.Pinned(target), target.Name, target.Config.Locks is { Count: > 0 } locks ? [.. locks] : null)
            : new PostgresCall(Server, connection, null);

    /// <summary>
    /// The schema a listing keeps to (2026-10-09): the one <paramref name="asked"/>, or null for every one. Under a lock, an asked one off
    /// the list is <paramref name="error"/>, and none asked is null too: the statement's <c>@lock</c> keeps it to the list (#18; until then
    /// the one locked schema came back here).
    /// </summary>
    protected static string? ScopeSchema(PostgresCall call, string? asked, out string? error)
    {
        ArgumentNullException.ThrowIfNull(call);
        error = null;

        // Spelled exactly (2026-10-24, the database review's P4: under a lock to Sales an asked sales was silently swapped for it; they are
        // two schemas to the server).
        if (asked is not null && call.Locks is { } locks && !locks.Contains(asked, StringComparer.Ordinal))
        {
            error = PostgresText.SchemaElsewhere(asked, locks);
        }

        return asked;
    }

    /// <summary>
    /// The model's own SQL held to the connection's database and schema (<see cref="PostgresBoundary"/>, 2026-10-09): the server's own
    /// catalogs refused at once; under a schema lock a qualifier but its own (and <c>pg_catalog</c>) sends for the schemas' names
    /// (<see cref="PostgresBoundary.Names"/>), and one among them is refused. A statement with no such qualifier asks the server nothing.
    /// Null when it keeps to its place, or when the connection does not resolve (the run that follows says why); a failed read of the
    /// names is its error, never a pass.
    /// </summary>
    protected async Task<string?> OutsideAsync(string sql, PostgresCall call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        var access = call.Access;
        string? connection = call.Connection;
        if (access.Resolve(connection, out _) is not { } target)
        {
            return null;
        }

        var locks = target.Config.Locks;
        var (heads, refused) = PostgresBoundary.QualifiersUnder(sql, locks);
        if (refused is not null || heads.Count == 0)
        {
            return refused;
        }

        // The names kept a minute per connection (the review, 2026-10-09: an alias's t.col sent every join for them).
        var (rows, failed) = await ReadNamesAsync(access.Names, BoundaryNames.Key(target.Name, target.Config.Endpoint, target.Config.Database, target.Config.User),
            token => access.RunAsync(connection, [PostgresBoundary.Names], [], PostgresBoundary.MaxNames, TimeoutSeconds(Effective), token), PostgresText.Error, cancellationToken).ConfigureAwait(false);
        return failed ?? PostgresBoundary.CheckUnder(sql, locks, access.Names.Built(rows!, PostgresBoundary.NameSet));
    }

    protected async Task<SqlRun> CatalogAsync(PostgresCall call, IReadOnlyList<string> statements, IReadOnlyList<SqlParameterValue> parameters, CancellationToken cancellationToken) =>
        SqlText.CatalogRun(await call.Access.RunAsync(call.Connection, statements, parameters, PostgresAccess.MaxCatalogRows, TimeoutSeconds(Effective), cancellationToken).ConfigureAwait(false));

    protected sealed record TableMatch(string Schema, string Name, string Kind, string? Error)
    {
        public static TableMatch Refused(string error) => new("", "", "", error);
    }

    /// <summary>
    /// The table or view <paramref name="table"/> means: <c>schema.name</c> as written, else in <paramref name="schema"/>, else the one
    /// of that name in any schema — the exact spelling over a case-folded one, then the one earliest on the search path (2026-11-15, the
    /// database-tools review's #19: under a lock to several schemas a name in two was refused as ambiguous, where PostgreSQL takes the
    /// first); several off the path are ambiguous with the candidates named. Under a schema lock (2026-10-09) the locked schemas alone:
    /// another, as <paramref name="schema"/> or written, is refused.
    /// </summary>
    protected async Task<TableMatch> FindTableAsync(PostgresCall call, string? schema, string table, CancellationToken cancellationToken)
    {
        if (PostgresAccess.Split(table) is not { } parts)
        {
            return TableMatch.Refused(PostgresText.BadTable(table));
        }

        // A table's unquoted schema part folds as the server folds it (2026-10-24, with P4's exact compare: SALES.orders is sales.orders).
        string? written = parts.Schema is { } part && !table.TrimStart().StartsWith('"') ? PostgresReadOnlyGate.AsciiLower(part) : parts.Schema;
        string? scope = ScopeSchema(call, written ?? schema, out var elsewhere);
        if (elsewhere is not null)
        {
            return TableMatch.Refused(elsewhere);
        }

        var run = await CatalogAsync(call, [PostgresCatalogQueries.Resolve], [new("schema", scope), new("name", parts.Name), new("lock", call.Locks)], cancellationToken).ConfigureAwait(false);
        if (run.Outcome != SqlOutcome.Ok)
        {
            return TableMatch.Refused(PostgresText.Error(run));
        }

        // schema, name, kind, exact, the position on the search path (empty off it): the lowest position wins.
        var rows = run.Grids.Count > 0 ? run.Grids[0].Rows.ToList() : [];
        var pick = rows.Where(r => r[3] == "true").ToList() is { Count: > 0 } exact ? exact : rows;
        if (pick.Count > 1 && pick.Select(r => PathPosition(r[4])).Where(p => p is not null).Min() is { } first)
        {
            pick = [.. pick.Where(r => PathPosition(r[4]) == first)];
        }

        return pick.Count switch
        {
            0 => TableMatch.Refused(PostgresText.TableNotFound(table, PostgresText.Where(run))),
            1 => new TableMatch(pick[0][0], pick[0][1], pick[0][2], null),
            _ => TableMatch.Refused(PostgresText.TableAmbiguous(table, pick.Select(r => r[0] + "." + r[1]))),
        };
    }

    /// <summary>A candidate's place on the search path (<c>array_position</c>, 1-based), or null off it.</summary>
    private static int? PathPosition(string cell) =>
        int.TryParse(cell, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int at) ? at : null;
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
        "Lists the PostgreSQL connections the user has set up: each one's name, host, database, user and what it holds. " +
        "Pass a name as \"connection\" to the other postgres_ tools.";

    public override JsonElement JsonSchema => Schema;

    public string Describe() => PostgresText.Connections(Server.Catalog(), DatabaseWriteModes.IsReadWrite(Effective.PostgresMode));

    protected override ValueTask<object?> InvokeToolAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) => new(Describe());
}

/// <summary>A catalog listing: the statement, its parameters from the arguments, and the nouns for the answer.</summary>
public abstract class PostgresListingTool : PostgresTool
{
    protected PostgresListingTool(PostgresAccess postgres, Func<AppSettingsData> effective) : base(postgres, effective)
    {
    }

    protected async Task<string> ListAsync(PostgresCall call, string statement, IReadOnlyList<SqlParameterValue> parameters, string singular, string plural, CancellationToken cancellationToken)
    {
        var run = await CatalogAsync(call, [statement], parameters, cancellationToken).ConfigureAwait(false);
        return run.Outcome != SqlOutcome.Ok ? PostgresText.Error(run)
            : PostgresText.Listing(singular, plural, run.Grids.Count > 0 ? run with { Grids = [SqlText.WithoutEmptyColumn(run.Grids[0], "description")] } : run, Files.WorkingDirectory.MaxReadChars);
    }
}

public sealed class PostgresSchemasTool : PostgresListingTool
{
    public const string ToolName = "postgres_schemas";

    private static readonly JsonElement Schema = ToolSchema.Parse("{ \"type\": \"object\", \"properties\": { " + ConnectionProperty + " } }");

    public PostgresSchemasTool(PostgresAccess postgres, Func<AppSettingsData> effective) : base(postgres, effective)
    {
    }

    public override string Name => ToolName;

    public override string Description => "Lists the schemas of a PostgreSQL connection's database the account may use (only its own, on a connection held to some), with how many tables and views each holds and its owner.";

    public override JsonElement JsonSchema => Schema;

    protected override async ValueTask<object?> InvokeToolAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var call = Pin(Optional(arguments, ConnectionArgument));
        return await ListAsync(call, PostgresCatalogQueries.Schemas, [new("lock", call.Locks)], "schema", "schemas", cancellationToken).ConfigureAwait(false);
    }
}

public sealed class PostgresTablesTool : PostgresListingTool
{
    public const string ToolName = "postgres_tables";
    public const string PatternArgument = "pattern";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        "{ \"type\": \"object\", \"properties\": { " + ConnectionProperty + ", " + SchemaProperty + ", " +
        "\"pattern\": { \"type\": \"string\", \"description\": \"Only names containing this text (case-insensitive), or matching it as a LIKE pattern when it holds % over name or schema.name; leave it out for all.\" } } }");

    public PostgresTablesTool(PostgresAccess postgres, Func<AppSettingsData> effective) : base(postgres, effective)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Lists the tables and views of a PostgreSQL database as schema.name, with each one's kind, approximate row count and comment. " +
        "Narrow it with schema and pattern; then postgres_describe a table before querying it.";

    public override JsonElement JsonSchema => Schema;

    public async Task<string> DescribeAsync(string? connection, string? schema, string? pattern, CancellationToken cancellationToken)
    {
        var call = Pin(connection);
        string? scope = ScopeSchema(call, schema, out var elsewhere);
        return elsewhere ?? await ListAsync(call, PostgresCatalogQueries.Tables, [new("schema", scope), new("pattern", OracleTablesTool.LikePattern(pattern)), new("lock", call.Locks)], "table or view", "tables and views", cancellationToken).ConfigureAwait(false);
    }

    protected override async ValueTask<object?> InvokeToolAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) =>
        await DescribeAsync(Optional(arguments, ConnectionArgument), Optional(arguments, SchemaArgument), Optional(arguments, PatternArgument), cancellationToken).ConfigureAwait(false);
}

public sealed class PostgresColumnsTool : PostgresListingTool
{
    public const string ToolName = "postgres_columns";
    public const string PatternArgument = "pattern";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        "{ \"type\": \"object\", \"properties\": { \"pattern\": { \"type\": \"string\", \"description\": \"The column name or part of it (case-insensitive; % as a LIKE wildcard).\" }, " +
        ConnectionProperty + ", " + SchemaProperty + " }, \"required\": [\"pattern\"] }");

    public PostgresColumnsTool(PostgresAccess postgres, Func<AppSettingsData> effective) : base(postgres, effective)
    {
    }

    public override string Name => ToolName;

    public override string Description => "Finds every column whose name matches across a PostgreSQL database's tables and views: the table, the type, nullability and the comment.";

    public override JsonElement JsonSchema => Schema;

    protected override async ValueTask<object?> InvokeToolAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (Optional(arguments, PatternArgument) is not { } pattern)
        {
            return PostgresText.NoPattern;
        }

        var call = Pin(Optional(arguments, ConnectionArgument));
        string? scope = ScopeSchema(call, Optional(arguments, SchemaArgument), out var elsewhere);
        return elsewhere ?? await ListAsync(call, PostgresCatalogQueries.ColumnSearch, [new("schema", scope), new("pattern", OracleTablesTool.LikePattern(pattern)), new("lock", call.Locks)], "column", "columns", cancellationToken).ConfigureAwait(false);
    }
}

public sealed class PostgresDescribeTool : PostgresTool
{
    public const string ToolName = "postgres_describe";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        "{ \"type\": \"object\", \"properties\": { \"table\": { \"type\": \"string\", \"description\": \"The table or view: name or schema.name (case-insensitive unless quoted).\" }, " +
        ConnectionProperty + ", " + SchemaProperty + " }, \"required\": [\"table\"] }");

    public PostgresDescribeTool(PostgresAccess postgres, Func<AppSettingsData> effective) : base(postgres, effective)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Shows a PostgreSQL table's or view's columns (type, nullability, primary key, default, comment), its foreign keys both ways, its indexes " +
        "and CHECK constraints — what to read before writing a query on it.";

    public override JsonElement JsonSchema => Schema;

    public async Task<string> DescribeAsync(string table, string? connection, string? schema, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(table))
        {
            return PostgresText.NoTable;
        }

        var call = Pin(connection);
        var found = await FindTableAsync(call, schema, table, cancellationToken).ConfigureAwait(false);
        if (found.Error is { } error)
        {
            return error;
        }

        var run = await CatalogAsync(call, PostgresCatalogQueries.Describe, [new("schema", found.Schema), new("name", found.Name), new("lock", call.Locks)], cancellationToken).ConfigureAwait(false);
        return run.Outcome == SqlOutcome.Ok ? PostgresText.Describe(found.Schema, found.Name, found.Kind, run, Files.WorkingDirectory.MaxReadChars) : PostgresText.Error(run);
    }

    protected override async ValueTask<object?> InvokeToolAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return await DescribeAsync(ToolArguments.ReadString(arguments, TableArgument), Optional(arguments, ConnectionArgument), Optional(arguments, SchemaArgument), cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>The two per-schema-or-table listings, <c>postgres_relationships</c> and <c>postgres_indexes</c>: a table resolved first when given.</summary>
public abstract class PostgresScopedTool : PostgresListingTool
{
    protected PostgresScopedTool(PostgresAccess postgres, Func<AppSettingsData> effective) : base(postgres, effective)
    {
    }

    protected static readonly JsonElement ScopedSchema = ToolSchema.Parse(
        "{ \"type\": \"object\", \"properties\": { " + ConnectionProperty + ", " + SchemaProperty + ", " +
        "\"table\": { \"type\": \"string\", \"description\": \"Only this table (name or schema.name); leave it out for the schema's, or every schema's.\" } } }");

    protected async Task<string> ScopedAsync(AIFunctionArguments arguments, string statement, string singular, string plural, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var call = Pin(Optional(arguments, ConnectionArgument));
        string? schema = ScopeSchema(call, Optional(arguments, SchemaArgument), out var elsewhere);
        if (elsewhere is not null)
        {
            return elsewhere;
        }

        string? name = null;
        if (Optional(arguments, TableArgument) is { } table)
        {
            var found = await FindTableAsync(call, schema, table, cancellationToken).ConfigureAwait(false);
            if (found.Error is { } error)
            {
                return error;
            }

            (schema, name) = (found.Schema, found.Name);
        }

        return await ListAsync(call, statement, [new("schema", schema), new("name", name), new("lock", call.Locks)], singular, plural, cancellationToken).ConfigureAwait(false);
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

    protected override async ValueTask<object?> InvokeToolAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) =>
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

    protected override async ValueTask<object?> InvokeToolAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) =>
        await ScopedAsync(arguments, PostgresCatalogQueries.Indexes, "index", "indexes", cancellationToken).ConfigureAwait(false);
}

/// <summary>
/// <c>postgres_query(sql, connection?, params?, max_rows?)</c>: one read-only SELECT, passed by <see cref="PostgresReadOnlyGate"/> and held to
/// the connection's database and schema (<see cref="PostgresTool.OutsideAsync"/>, 2026-10-09), then run in a read-only transaction that is rolled back under the session's own read-only default and <c>statement_timeout</c>; the
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

    private static readonly (JsonElement Export, JsonElement Plain) Schemas = QuerySchemas(members =>
        "{ \"type\": \"object\", \"properties\": { " +
        "\"sql\": { \"type\": \"string\", \"description\": \"One PostgreSQL SELECT (LIMIT n); a WITH clause may lead it. No INSERT, UPDATE, DELETE, DDL, COPY, DO, SELECT INTO, FOR UPDATE or second statement.\" }, " +
        ConnectionProperty + ", " +
        "\"params\": { \"type\": \"object\", \"description\": \"Values for @name placeholders in the SQL, e.g. {\\\"id\\\": 101} for @id; strings, numbers, true, false or null.\" }, " +
        members + " }, \"required\": [\"sql\"] }");

    public PostgresQueryTool(PostgresAccess postgres, Func<AppSettingsData> effective) : base(postgres, effective)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Runs one read-only SELECT on a PostgreSQL connection and returns the rows as a table. " +
        "Only a single SELECT (or WITH … SELECT) is accepted; bind values as @name through params rather than writing them into the text. " +
        "The header says when more rows exist than were returned." + SaveSentence;

    public override JsonElement JsonSchema => QuerySchema(Schemas);

    public static int DefaultRows(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return Math.Clamp(effective.PostgresQueryMaxRows, MinRows, MaxRows);
    }

    /// <remarks><paramref name="export"/> (2026-10-29, the Office writing plan): every row to a workbook instead (<see cref="ServerTool{TAccess}.ExportAsync"/>).</remarks>
    public async Task<string> RunAsync(string sql, string? connection, IReadOnlyList<SqlParameterValue> parameters, int? maxRows, CancellationToken cancellationToken, ExportTarget? export = null)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(parameters);
        if (RowCap(export, maxRows, DefaultRows(Effective), MinRows, MaxRows, out int rows) is { } badRows)
        {
            return badRows;
        }

        if (PostgresReadOnlyGate.Check(sql) is { } refused)
        {
            return refused;
        }

        string body = PostgresReadOnlyGate.Body(sql);
        var call = Pin(connection);
        if (await OutsideAsync(body, call, cancellationToken).ConfigureAwait(false) is { } outside)
        {
            return outside;
        }

        if (export is not null)
        {
            if (call.Access.Resolve(call.Connection, out var unresolved) is not { } target)
            {
                return PostgresText.Error(unresolved!);
            }

            // The run on the entry resolved once (2026-10-30, review F54: an edit between two reads could name the workbook after one
            // connection and read another).
            var pinned = call.Access;
            return await ExportAsync(export, target.Name, rows, maxRows, TimeoutSeconds(Effective), PostgresAccess.ExportCell,
                (job, timeout, token) => pinned.ExportAsync(target.Name, body, parameters, job, rows, timeout, token), PostgresText.Error, cancellationToken,
                PostgresAccess.IsUtcColumn).ConfigureAwait(false);
        }

        var run = await call.Access.RunAsync(call.Connection, [body], parameters, rows, TimeoutSeconds(Effective), cancellationToken).ConfigureAwait(false);
        return run.Outcome == SqlOutcome.Ok ? PostgresText.Query(run, rows, SqlTool.ResultChars(Effective), CanSave) : PostgresText.Error(run);
    }

    protected override async ValueTask<object?> InvokeToolAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!ToolArguments.TryReadInt32(arguments, MaxRowsArgument, out var max, out var raw))
        {
            return ClockText.BadInteger(MaxRowsArgument, raw);
        }

        if (SqlQueryTool.ReadParams(arguments, PostgresText.BadParams, out var error) is not { } parameters)
        {
            return error;
        }

        if (ReadExport(arguments, out var export) is { } badExport)
        {
            return badExport;
        }

        return await RunAsync(ToolArguments.ReadString(arguments, SqlArgument), Optional(arguments, ConnectionArgument), parameters, max, cancellationToken, export).ConfigureAwait(false);
    }
}

/// <summary>
/// <c>postgres_explain(sql, connection?, params?)</c> (2026-11-14, the database-tools plan's #12): how PostgreSQL would run one read, from
/// <c>EXPLAIN (FORMAT JSON)</c>. The statement passes <see cref="PostgresReadOnlyGate"/> and the database and schema boundary as
/// <c>postgres_query</c>'s does, then runs with the prefix on the same path (the session read back, the read-only transaction, rolled back,
/// the timeout); the plan is made, never run (no ANALYZE). Parameters bind over the extended protocol, so the plan is the one those values
/// get. The answer is <see cref="QueryPlanText.Answer"/>'s summary.
/// </summary>
public sealed class PostgresExplainTool : PostgresTool
{
    public const string ToolName = "postgres_explain";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        "{ \"type\": \"object\", \"properties\": { " +
        "\"sql\": { \"type\": \"string\", \"description\": \"The PostgreSQL SELECT to explain, as postgres_query takes it (a WITH clause may lead it). Give the query alone: the tool adds the EXPLAIN.\" }, " +
        ConnectionProperty + ", " +
        "\"params\": { \"type\": \"object\", \"description\": \"Values for @name placeholders in the SQL, as postgres_query takes them, so the plan is the one those values get.\" } " +
        "}, \"required\": [\"sql\"] }");

    public PostgresExplainTool(PostgresAccess postgres, Func<AppSettingsData> effective) : base(postgres, effective)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Shows how PostgreSQL would run one SELECT: its query plan's steps (scans, joins, sorts, with estimated rows and cost) and what is worth a look. " +
        "The query is planned, never run; use it when a query is slow, before rewriting it.";

    public override JsonElement JsonSchema => Schema;

    public async Task<string> ExplainAsync(string sql, string? connection, IReadOnlyList<SqlParameterValue> parameters, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(parameters);
        if (PostgresReadOnlyGate.Check(sql) is { } refused)
        {
            return refused;
        }

        string body = PostgresReadOnlyGate.Body(sql);
        var call = Pin(connection);
        if (await OutsideAsync(body, call, cancellationToken).ConfigureAwait(false) is { } outside)
        {
            return outside;
        }

        var effective = Effective;
        var run = await call.Access.RunAsync(call.Connection, [PostgresPlan.Prefix + body], parameters, PostgresAccess.MaxCatalogRows, TimeoutSeconds(effective), cancellationToken, readCell: QueryPlan.WholeCell).ConfigureAwait(false);
        if (run.Outcome != SqlOutcome.Ok)
        {
            return PostgresText.Error(run);
        }

        string text = QueryPlan.TextOf(run, 0);
        return ExplainAnswer.Of(PostgresPlan.Parse(text), run, text, effective);
    }

    protected override async ValueTask<object?> InvokeToolAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (SqlQueryTool.ReadParams(arguments, PostgresText.BadParams, out var error) is not { } parameters)
        {
            return error;
        }

        return await ExplainAsync(ToolArguments.ReadString(arguments, PostgresQueryTool.SqlArgument), Optional(arguments, ConnectionArgument), parameters, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// <c>postgres_profile(table, connection?, schema?, columns?, top?)</c> (2026-11-14, the database-tools plan's #14), <see cref="SqlProfileTool"/>'s
/// PostgreSQL twin: the table found as <c>postgres_describe</c> finds it (a held schema kept to), the SQL the app's own
/// (<see cref="PostgresProfile"/>), in the read-only transaction that is rolled back; a big table or materialized view is sampled.
/// </summary>
public sealed class PostgresProfileTool : PostgresTool
{
    public const string ToolName = "postgres_profile";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        "{ \"type\": \"object\", \"properties\": { \"table\": { \"type\": \"string\", \"description\": \"The table or view: name or schema.name (case-insensitive unless quoted).\" }, " +
        ConnectionProperty + ", " + SchemaProperty + ", " + ProfileArguments.ColumnsProperty + ", " + ProfileArguments.TopProperty + " }, \"required\": [\"table\"] }");

    public PostgresProfileTool(PostgresAccess postgres, Func<AppSettingsData> effective) : base(postgres, effective)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Profiles one PostgreSQL table or view: its rows and, per column, the nulls, distinct values, min, max and most common values (a big table is " +
        "sampled, and the answer says so). Read-only; use it before reading pages of sample rows.";

    public override JsonElement JsonSchema => Schema;

    /// <summary>The rows a profile reads at most: a table estimated bigger is sampled. Settable so the live facts make the server sample.</summary>
    internal long SampleAbove { get; set; } = TableProfile.DefaultSampleAbove;

    public async Task<string> ProfileAsync(string table, string? connection, string? schema, IReadOnlyList<string>? columns, int top, CancellationToken cancellationToken)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        if (string.IsNullOrWhiteSpace(table))
        {
            return PostgresText.NoTable;
        }

        var effective = Effective;
        int timeout = TimeoutSeconds(effective);
        var call = Pin(connection);
        var found = await FindTableAsync(call, schema, table, cancellationToken).ConfigureAwait(false);
        if (found.Error is { } error)
        {
            return error;
        }

        string shown = found.Schema + "." + found.Name;
        if (SqlDeadline.Left(watch, timeout) is not (> 0 and var left))
        {
            return TableProfileText.Stopped(shown, "", timeout);
        }

        var catalog = await call.Access.RunAsync(call.Connection, PostgresProfile.Catalog, [new("schema", found.Schema), new("name", found.Name)], PostgresAccess.MaxCatalogRows, left, cancellationToken,
            readCell: TableProfile.CatalogCell).ConfigureAwait(false);
        if (catalog.Outcome != SqlOutcome.Ok)
        {
            return catalog.Outcome == SqlOutcome.Timeout ? TableProfileText.Stopped(shown, catalog.Connection, timeout) : PostgresText.Error(catalog);
        }

        // name, type, category, type name (a domain's base); then the row estimate
        var listed = catalog.Grids.Count > 0 ? catalog.Grids[0] : null;
        var all = listed?.Rows.Select(r => new ProfileColumn(r[0], r[1], PostgresProfile.Measure(r[2], r[3]))).ToList() ?? [];
        long? estimate = catalog.Grids.Count > 1 ? TableProfile.Whole(catalog.Grids[1].Rows.FirstOrDefault()?[0]) : null;
        var dialect = PostgresProfile.Instance;
        var request = new TableProfile.Request(shown, dialect.Quote(found.Schema) + "." + dialect.Quote(found.Name), found.Kind, all, columns, top,
            estimate, timeout, SqlTool.AnswerChars(effective), PostgresDescribeTool.ToolName, watch)
        {
            SampleAbove = SampleAbove,
            ColumnsCut = listed?.More == true,
        };
        return await TableProfile.RunAsync(request, dialect,
            (statements, seconds, token) => call.Access.RunAsync(call.Connection, statements, [], PostgresAccess.MaxCatalogRows, seconds, token),
            PostgresText.Error, cancellationToken).ConfigureAwait(false);
    }

    protected override async ValueTask<object?> InvokeToolAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (ProfileArguments.Read(arguments, out var columns, out int top) is { } refused)
        {
            return refused;
        }

        return await ProfileAsync(ToolArguments.ReadString(arguments, TableArgument), Optional(arguments, ConnectionArgument), Optional(arguments, SchemaArgument), columns, top, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// <c>postgres_execute(sql, connection?, params?, max_rows?)</c> (2026-10-05, the user's ask: <c>sqlite_execute</c>
/// mirrored): one statement that may change a database, offered only while <c>PostgreSQL mode</c> is <c>read-write</c>, a pane can ask,
/// <c>PostgreSQL statements allowed</c> ticks a kind and an offered connection says <c>"access": "readwrite"</c> (all checked again at
/// every call). <see cref="PostgresWriteGate"/> first, then the connection, then the lock to its database and schema
/// (<see cref="PostgresTool.OutsideAsync"/>, 2026-10-09: refused, never asked about), then a read runs as <c>postgres_query</c>'s do without
/// asking, else the user's allow — per connection — then <see cref="PostgresAccess.ExecuteAsync"/>, committed as it runs,
/// written to the log (<see cref="ServerExecute"/>). Plan mode drops it.
/// </summary>
public sealed class PostgresExecuteTool : PostgresTool
{
    public const string ToolName = "postgres_execute";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        "{ \"type\": \"object\", \"properties\": { " +
        "\"sql\": { \"type\": \"string\", \"description\": \"One PostgreSQL statement of a kind this tool's description lists (RETURNING allowed). No BEGIN/COMMIT, SET, GRANT or second statement.\" }, " +
        ConnectionProperty + ", " +
        "\"params\": { \"type\": \"object\", \"description\": \"Values for @name placeholders in the SQL, e.g. {\\\"id\\\": 101} for @id; strings, numbers, true, false or null.\" }, " +
        "\"max_rows\": { \"type\": \"integer\", \"description\": \"How many returned rows (RETURNING) to show at most, 1 to " + MaxRowsWords + ". Leave it out for the user's default.\" } }, \"required\": [\"sql\"] }");

    private readonly DatabaseWriteAllow? _allow;

    /// <param name="allow">The user's allow for one change. Null where nothing can ask: every change is refused.</param>
    public PostgresExecuteTool(PostgresAccess postgres, Func<AppSettingsData> effective, DatabaseWriteAllow? allow) : base(postgres, effective)
    {
        _allow = allow;
    }

    public override string Name => ToolName;

    /// <summary>The description, read at each turn: it names the kinds <c>PostgreSQL statements allowed</c> ticks.</summary>
    public override string Description => DescribeFor(ServerStatementKinds.Resolve(Effective.PostgresStatementsAllowed), !Effective.ToolsDisabled.Contains(PostgresQueryTool.ToolName, StringComparer.Ordinal));

    /// <summary>The description for <paramref name="kinds"/>. Pinned.</summary>
    public static string DescribeFor(IReadOnlyList<ServerStatementKind> kinds, bool readOffered = true) =>
        ServerWriteText.Describe(PostgresStatementKinds.Family, kinds, PostgresQueryTool.ToolName, "PostgreSQL SQL; bind values as @name through params.", readOffered);

    public override JsonElement JsonSchema => Schema;

    public Task<string> RunAsync(string sql, string? connection, IReadOnlyList<SqlParameterValue> parameters, int? maxRows, CancellationToken cancellationToken)
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
        // The connection resolved once (2026-10-24, the database review's H11): the boundary, the pane and the run below all work on the entry
        // as read now, through an access pinned to it.
        var resolved = Server.Resolve(connection, out var unresolved);
        var pinned = resolved is null ? Server : Server.Pinned(resolved);
        var hooks = new ServerExecuteHooks(
            PostgresWriteGate.Check,
            PostgresWriteGate.Kinds,
            PostgresReadOnlyGate.Check,
            PostgresReadOnlyGate.Body,
            () => resolved is { } target
                ? (new ExecuteTarget(target.Name, target.Config.IsReadWrite, [target.Config.Database?.Trim() ?? ""], target.Config.SignInHost), null)
                : (null, PostgresText.Error(unresolved!)),
            async (body, token) =>
            {
                var run = await pinned.RunAsync(connection, [body], parameters, rows, timeout, token).ConfigureAwait(false);
                return run.Outcome == SqlOutcome.Ok ? PostgresText.Query(run, rows, SqlTool.ResultChars(effective)) : PostgresText.Error(run);
            },
            (body, token) => pinned.ExecuteAsync(connection, body, parameters, rows, timeout, token),
            PostgresText.Error,
            (body, token) => OutsideAsync(body, new PostgresCall(pinned, connection, resolved?.Config.Locks is { Count: > 0 } locks ? [.. locks] : null), token), Changed: pinned.Names.Forget);
        return ServerExecute.RunAsync(PostgresStatementKinds.Family, effective.PostgresMode, effective.PostgresStatementsAllowed, sql, parameters, rows, SqlTool.ResultChars(effective), hooks, _allow, cancellationToken);
    }

    protected override async ValueTask<object?> InvokeToolAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!ToolArguments.TryReadInt32(arguments, PostgresQueryTool.MaxRowsArgument, out var max, out var raw))
        {
            return ClockText.BadInteger(PostgresQueryTool.MaxRowsArgument, raw);
        }

        if (SqlQueryTool.ReadParams(arguments, PostgresText.BadParams, out var error) is not { } parameters)
        {
            return error;
        }

        return await RunAsync(ToolArguments.ReadString(arguments, PostgresQueryTool.SqlArgument), Optional(arguments, ConnectionArgument), parameters, max, cancellationToken).ConfigureAwait(false);
    }
}
