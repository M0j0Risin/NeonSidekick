using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Settings;
using NeonSidekick.Sql;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>sql_execute(sql, connection?, database?, params?, max_rows?)</c> (2026-10-05, the user's ask: <c>postgres_execute</c>'s twin): one
/// T-SQL statement that may change a SQL Server database, offered only while <c>SQL mode</c> is <c>read-write</c>, a pane can ask,
/// <c>SQL statements allowed</c> ticks a kind and an offered connection says <c>"access": "readwrite"</c>. <see cref="SqlWriteGate"/>
/// (ScriptDom), the connection, a read unasked, else the user's allow per connection and database, then
/// <see cref="SqlAccess.ExecuteAsync"/> committed as it runs and written to the log (<see cref="ServerExecute"/>). Plan mode drops it.
/// </summary>
public sealed class SqlExecuteTool : SqlTool
{
    public const string ToolName = "sql_execute";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        $$"""
        {
          "type": "object",
          "properties": {
            "sql": { "type": "string", "description": "One T-SQL statement of a kind this tool's description lists (OUTPUT allowed). No BEGIN TRAN/COMMIT, SET, USE, GRANT, dynamic SQL or second statement; no GO." },
            {{ConnectionProperty}},
            {{DatabaseProperty}},
            "params": { "type": "object", "description": "Values for @name placeholders in the SQL, e.g. {\"id\": 43659} for @id; strings, numbers, true, false or null." },
            "max_rows": { "type": "integer", "description": "How many returned rows (OUTPUT, a procedure's results) to show at most, 1 to 100000. Leave it out for the user's default." }
          },
          "required": ["sql"]
        }
        """);

    private readonly DatabaseWriteAllow? _allow;

    /// <param name="allow">The user's allow for one change. Null where nothing can ask: every change is refused.</param>
    public SqlExecuteTool(SqlAccess sql, Func<AppSettingsData> effective, DatabaseWriteAllow? allow) : base(sql, effective)
    {
        _allow = allow;
    }

    public override string Name => ToolName;

    /// <summary>The description, read at each turn: it names the kinds <c>SQL statements allowed</c> ticks.</summary>
    public override string Description => DescribeFor(ServerStatementKinds.Resolve(Effective.SqlStatementsAllowed));

    /// <summary>The description for <paramref name="kinds"/>. Pinned.</summary>
    public static string DescribeFor(IReadOnlyList<ServerStatementKind> kinds) =>
        ServerWriteText.Describe(SqlStatementKinds.Family, kinds, SqlQueryTool.ToolName, "T-SQL; bind values as @name through params.");

    public override JsonElement JsonSchema => Schema;

    public Task<string> RunAsync(string sql, string? connection, string? database, IReadOnlyList<SqlParameterValue> parameters, int? maxRows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(parameters);
        var effective = Effective;
        int rows = maxRows ?? SqlQueryTool.DefaultRows(effective);
        if (rows < SqlQueryTool.MinRows || rows > SqlQueryTool.MaxRows)
        {
            return Task.FromResult(SqlText.BadMaxRows(SqlQueryTool.MinRows, SqlQueryTool.MaxRows));
        }

        int timeout = TimeoutSeconds(effective);
        var hooks = new ServerExecuteHooks(
            SqlWriteGate.Check,
            SqlWriteGate.Kinds,
            SqlReadOnlyGate.Check,
            text => text.Trim(),
            () => Sql.Resolve(connection, effective.SqlDefaultConnection, out var refused) is { } target
                ? (new ExecuteTarget(target.Name, target.Config.IsReadWrite, Place(target.Config, database)), null)
                : (null, SqlText.Error(refused!)),
            async (body, token) =>
            {
                var run = await Sql.RunAsync(connection, effective.SqlDefaultConnection, database, body, parameters, rows, timeout, token).ConfigureAwait(false);
                return run.Outcome == SqlOutcome.Ok ? SqlText.Query(run, rows, ResultChars(effective)) : SqlText.Error(run);
            },
            (body, token) => Sql.ExecuteAsync(connection, effective.SqlDefaultConnection, database, body, parameters, rows, timeout, token),
            SqlText.Error);
        return ServerExecute.RunAsync(SqlStatementKinds.Family, effective.SqlMode, effective.SqlStatementsAllowed, sql, rows, ResultChars(effective), hooks, _allow, cancellationToken);
    }

    /// <summary>The database a call works in: the one it names, else the connection's own, else none (the login's default).</summary>
    public static string Place(SqlConnectionConfig config, string? database)
    {
        ArgumentNullException.ThrowIfNull(config);
        return !string.IsNullOrWhiteSpace(database) ? database.Trim() : config.Database?.Trim() ?? "";
    }

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!ToolArguments.TryReadInt32(arguments, SqlQueryTool.MaxRowsArgument, out var max, out var raw))
        {
            return ClockText.BadInteger(SqlQueryTool.MaxRowsArgument, raw);
        }

        if (!ToolArguments.TryReadObjectList(arguments, SqlQueryTool.ParamsArgument, out var objects, out var sent) || objects.Count > 1)
        {
            return SqlText.BadParams(sent);
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

        return await RunAsync(ToolArguments.ReadString(arguments, SqlQueryTool.SqlArgument), Optional(arguments, ConnectionArgument), Optional(arguments, DatabaseArgument), parameters, max, cancellationToken).ConfigureAwait(false);
    }
}
