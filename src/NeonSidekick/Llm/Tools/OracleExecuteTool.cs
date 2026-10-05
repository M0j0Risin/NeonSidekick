using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Oracle;
using NeonSidekick.Settings;
using NeonSidekick.Sql;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>oracle_execute(sql, connection?, schema?, params?)</c> (2026-10-05, the user's ask: <c>postgres_execute</c>'s twin): one statement
/// that may change an Oracle schema — SQL, or a PL/SQL unit — offered only while <c>Oracle mode</c> is <c>read-write</c>, a pane can ask,
/// <c>Oracle statements allowed</c> ticks a kind and an offered connection says <c>"access": "readwrite"</c>. <see cref="OracleWriteGate"/>,
/// the connection, a read unasked, else the user's allow per connection and schema, then <see cref="OracleAccess.ExecuteAsync"/>
/// committed as it runs and written to the log (<see cref="ServerExecute"/>). Plan mode drops it.
/// </summary>
public sealed class OracleExecuteTool : OracleTool
{
    public const string ToolName = "oracle_execute";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        $$"""
        {
          "type": "object",
          "properties": {
            "sql": { "type": "string", "description": "One Oracle statement of a kind this tool's description lists, or one PL/SQL block or unit ending END; (no trailing / needed). No COMMIT, ALTER SESSION, GRANT, EXECUTE IMMEDIATE, @dblink, RETURNING INTO or second statement." },
            {{ConnectionProperty}},
            "schema": { "type": "string", "description": "The schema unqualified names resolve in (CURRENT_SCHEMA); leave it out for the connection's own." },
            "params": { "type": "object", "description": "Values for :name placeholders in the SQL, e.g. {\"id\": 101} for :id; strings, numbers, true, false or null." },
            "max_rows": { "type": "integer", "description": "How many rows a read shows at most, 1 to 100000. Leave it out for the user's default." }
          },
          "required": ["sql"]
        }
        """);

    private readonly DatabaseWriteAllow? _allow;

    /// <param name="allow">The user's allow for one change. Null where nothing can ask: every change is refused.</param>
    public OracleExecuteTool(OracleAccess oracle, Func<AppSettingsData> effective, DatabaseWriteAllow? allow) : base(oracle, effective)
    {
        _allow = allow;
    }

    public override string Name => ToolName;

    /// <summary>The description, read at each turn: it names the kinds <c>Oracle statements allowed</c> ticks.</summary>
    public override string Description => DescribeFor(ServerStatementKinds.Resolve(Effective.OracleStatementsAllowed));

    /// <summary>The description for <paramref name="kinds"/>. Pinned.</summary>
    public static string DescribeFor(IReadOnlyList<ServerStatementKind> kinds) =>
        ServerWriteText.Describe(OracleStatementKinds.Family, kinds, OracleQueryTool.ToolName, "Oracle SQL; bind values as :name through params.");

    public override JsonElement JsonSchema => Schema;

    public Task<string> RunAsync(string sql, string? connection, string? schema, IReadOnlyList<SqlParameterValue> parameters, int? maxRows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(parameters);
        var effective = Effective;
        int rows = maxRows ?? OracleQueryTool.DefaultRows(effective);
        if (rows < OracleQueryTool.MinRows || rows > OracleQueryTool.MaxRows)
        {
            return Task.FromResult(SqlText.BadMaxRows(OracleQueryTool.MinRows, OracleQueryTool.MaxRows));
        }

        if (!TryReadSchema(schema, out var owner, out var badSchema))
        {
            return Task.FromResult(badSchema!);
        }

        int timeout = TimeoutSeconds(effective);
        var hooks = new ServerExecuteHooks(
            OracleWriteGate.Check,
            OracleWriteGate.Kinds,
            OracleReadOnlyGate.Check,
            OracleWriteGate.Body,
            () => Oracle.Resolve(connection, effective.OracleDefaultConnection, out var refused) is { } target
                ? (new ExecuteTarget(target.Name, target.Config.IsReadWrite, Place(target.Config, owner)), null)
                : (null, OracleText.Error(refused!)),
            async (body, token) =>
            {
                var run = await Oracle.RunAsync(connection, effective.OracleDefaultConnection, owner, [body], parameters, rows, timeout, token).ConfigureAwait(false);
                if (IsTooNew(run))
                {
                    // ORA-01466 (found live, 2026-10-05): a read-only transaction cannot read a table whose DDL is younger than Oracle's
                    // SCN-to-time grain (about three seconds) — what a read straight after this tool's CREATE meets. Read again once, after it.
                    await Task.Delay(TooNewWait, token).ConfigureAwait(false);
                    run = await Oracle.RunAsync(connection, effective.OracleDefaultConnection, owner, [body], parameters, rows, timeout, token).ConfigureAwait(false);
                }

                return run.Outcome == SqlOutcome.Ok ? OracleText.Query(run, rows, SqlTool.ResultChars(effective)) : OracleText.Error(run);
            },
            (body, token) => Oracle.ExecuteAsync(connection, effective.OracleDefaultConnection, owner, body, parameters, timeout, token),
            OracleText.Error);
        return ServerExecute.RunAsync(OracleStatementKinds.Family, effective.OracleMode, effective.OracleStatementsAllowed, sql, rows, SqlTool.ResultChars(effective), hooks, _allow, cancellationToken);
    }

    /// <summary>How long a read waits before its one retry after ORA-01466.</summary>
    public static readonly TimeSpan TooNewWait = TimeSpan.FromSeconds(3);

    /// <summary>Whether a read failed with ORA-01466: the table's definition is newer than the read-only transaction's snapshot.</summary>
    public static bool IsTooNew(SqlRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return run.Outcome == SqlOutcome.Failed && run.Detail.Contains("ORA-01466", StringComparison.Ordinal);
    }

    /// <summary>The schema a call works in: the one it names, else the connection's own, else the user's — as the dictionary spells it.</summary>
    public static string Place(OracleConnectionConfig config, string? schema)
    {
        ArgumentNullException.ThrowIfNull(config);
        return OracleIdentifier.Normalize(schema) ?? OracleIdentifier.Normalize(config.Schema) ?? OracleIdentifier.Normalize(config.User) ?? config.User?.Trim().ToUpperInvariant() ?? "";
    }

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!ToolArguments.TryReadInt32(arguments, OracleQueryTool.MaxRowsArgument, out var max, out var raw))
        {
            return ClockText.BadInteger(OracleQueryTool.MaxRowsArgument, raw);
        }

        if (!ToolArguments.TryReadObjectList(arguments, OracleQueryTool.ParamsArgument, out var objects, out var sent) || objects.Count > 1)
        {
            return OracleText.BadParams(sent);
        }

        IReadOnlyList<SqlParameterValue> parameters = [];
        if (objects.Count == 1)
        {
            if (SqlQueryTool.ReadParameters(objects[0], out var error) is not { } read)
            {
                return error?.Replace("@name", ":name", StringComparison.Ordinal);
            }

            parameters = read;
        }

        return await RunAsync(ToolArguments.ReadString(arguments, OracleQueryTool.SqlArgument), Optional(arguments, ConnectionArgument), Optional(arguments, SchemaArgument), parameters, max, cancellationToken).ConfigureAwait(false);
    }
}
