using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.MySql;
using NeonSidekick.Settings;
using NeonSidekick.Sql;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>mysql_execute(sql, connection?, database?, params?, max_rows?)</c> (2026-10-05, the user's ask: <c>postgres_execute</c>'s twin):
/// one statement that may change a MySQL or MariaDB database, offered only while <c>MySQL mode</c> is <c>read-write</c>, a pane can ask,
/// <c>MySQL statements allowed</c> ticks a kind and an offered connection says <c>"access": "readwrite"</c>. <see cref="MySqlWriteGate"/>,
/// the connection, a read unasked, else the user's allow per connection and database, then <see cref="MySqlAccess.ExecuteAsync(string?, string?, string?, string, IReadOnlyList{SqlParameterValue}, int, int, CancellationToken)"/>
/// committed as it runs and written to the log (<see cref="ServerExecute"/>). Plan mode drops it.
/// </summary>
public sealed class MySqlExecuteTool : MySqlTool
{
    public const string ToolName = "mysql_execute";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        "{ \"type\": \"object\", \"properties\": { " +
        "\"sql\": { \"type\": \"string\", \"description\": \"One MySQL statement of a kind this tool's description lists. No START TRANSACTION/COMMIT, SET, GRANT, LOAD DATA or second statement.\" }, " +
        ConnectionProperty + ", " +
        "\"database\": { \"type\": \"string\", \"description\": \"The database unqualified names resolve in; leave it out for the connection's own.\" }, " +
        "\"params\": { \"type\": \"object\", \"description\": \"Values for @name placeholders in the SQL, e.g. {\\\"id\\\": 101} for @id; strings, numbers, true, false or null.\" }, " +
        "\"max_rows\": { \"type\": \"integer\", \"description\": \"How many returned rows to show at most, 1 to 100000. Leave it out for the user's default.\" } }, \"required\": [\"sql\"] }");

    private readonly DatabaseWriteAllow? _allow;

    /// <param name="allow">The user's allow for one change. Null where nothing can ask: every change is refused.</param>
    public MySqlExecuteTool(MySqlAccess mysql, Func<AppSettingsData> effective, DatabaseWriteAllow? allow) : base(mysql, effective)
    {
        _allow = allow;
    }

    public override string Name => ToolName;

    /// <summary>The description, read at each turn: it names the kinds <c>MySQL statements allowed</c> ticks.</summary>
    public override string Description => DescribeFor(ServerStatementKinds.Resolve(Effective.MySqlStatementsAllowed));

    /// <summary>The description for <paramref name="kinds"/>. Pinned.</summary>
    public static string DescribeFor(IReadOnlyList<ServerStatementKind> kinds) =>
        ServerWriteText.Describe(MySqlStatementKinds.Family, kinds, MySqlQueryTool.ToolName, "MySQL SQL (`backticks` for names); bind values as @name through params.");

    public override JsonElement JsonSchema => Schema;

    public Task<string> RunAsync(string sql, string? connection, string? database, IReadOnlyList<SqlParameterValue> parameters, int? maxRows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(parameters);
        var effective = Effective;
        int rows = maxRows ?? MySqlQueryTool.DefaultRows(effective);
        if (rows < MySqlQueryTool.MinRows || rows > MySqlQueryTool.MaxRows)
        {
            return Task.FromResult(SqlText.BadMaxRows(MySqlQueryTool.MinRows, MySqlQueryTool.MaxRows));
        }

        int timeout = TimeoutSeconds(effective);
        var hooks = new ServerExecuteHooks(
            MySqlWriteGate.Check,
            MySqlWriteGate.Kinds,
            MySqlReadOnlyGate.Check,
            MySqlReadOnlyGate.Body,
            () => Server.Resolve(connection, effective.MySqlDefaultConnection, out var refused) is { } target
                ? (new ExecuteTarget(target.Name, target.Config.IsReadWrite, Place(target.Config, database)), null)
                : (null, MySqlText.Error(refused!)),
            async (body, token) =>
            {
                var run = await Server.RunAsync(connection, effective.MySqlDefaultConnection, database, [body], parameters, rows, timeout, token).ConfigureAwait(false);
                return run.Outcome == SqlOutcome.Ok ? MySqlText.Query(run, rows, SqlTool.ResultChars(effective)) : MySqlText.Error(run);
            },
            (body, token) => Server.ExecuteAsync(connection, effective.MySqlDefaultConnection, database, body, parameters, rows, timeout, token),
            MySqlText.Error);
        return ServerExecute.RunAsync(MySqlStatementKinds.Family, effective.MySqlMode, effective.MySqlStatementsAllowed, sql, rows, SqlTool.ResultChars(effective), hooks, _allow, cancellationToken);
    }

    /// <summary>The database a call works in: the one it names, else the connection's own, else none (the server's default).</summary>
    public static string Place(MySqlConnectionConfig config, string? database)
    {
        ArgumentNullException.ThrowIfNull(config);
        return !string.IsNullOrWhiteSpace(database) ? database.Trim() : config.Database?.Trim() ?? "";
    }

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!ToolArguments.TryReadInt32(arguments, MySqlQueryTool.MaxRowsArgument, out var max, out var raw))
        {
            return ClockText.BadInteger(MySqlQueryTool.MaxRowsArgument, raw);
        }

        if (!ToolArguments.TryReadObjectList(arguments, MySqlQueryTool.ParamsArgument, out var objects, out var sent) || objects.Count > 1)
        {
            return MySqlText.BadParams(sent);
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

        return await RunAsync(ToolArguments.ReadString(arguments, MySqlQueryTool.SqlArgument), Optional(arguments, ConnectionArgument), Optional(arguments, DatabaseArgument), parameters, max, cancellationToken).ConfigureAwait(false);
    }
}
