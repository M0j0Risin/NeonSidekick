using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.MySql;
using NeonSidekick.Settings;
using NeonSidekick.Sql;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>mysql_explain(sql, connection?, params?)</c> (2026-11-14, the database-tools plan's #12): how MySQL or MariaDB would run one read, from
/// <c>EXPLAIN FORMAT=JSON</c>. The statement passes <see cref="MySqlReadOnlyGate"/> and the database boundary as <c>mysql_query</c>'s does, then
/// runs with the prefix on the same path (the hardened session, its mode read back, the read-only session and transaction, rolled back,
/// the timeout); the plan is made, never run. MariaDB's reference names only SELECT after EXPLAIN, so a WITH it refuses is answered with
/// the server's words and the way round. The answer is <see cref="QueryPlanText.Answer"/>'s summary (<see cref="MySqlPlan"/> reads both servers' JSON).
/// </summary>
public sealed class MySqlExplainTool : MySqlTool
{
    public const string ToolName = "mysql_explain";

    /// <summary>MySQL's and MariaDB's syntax error.</summary>
    private const int SyntaxError = 1064;

    private static readonly JsonElement Schema = ToolSchema.Parse(
        "{ \"type\": \"object\", \"properties\": { " +
        "\"sql\": { \"type\": \"string\", \"description\": \"The MySQL SELECT to explain, as mysql_query takes it (a WITH clause may lead it). Give the query alone: the tool adds the EXPLAIN.\" }, " +
        ConnectionProperty + ", " +
        "\"params\": { \"type\": \"object\", \"description\": \"Values for @name placeholders in the SQL, as mysql_query takes them.\" } " +
        "}, \"required\": [\"sql\"] }");

    public MySqlExplainTool(MySqlAccess mysql, Func<AppSettingsData> effective) : base(mysql, effective)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Shows how MySQL or MariaDB would run one SELECT: its query plan's steps (table scans, index lookups, joins, with estimated rows and cost) and what is worth a look. " +
        "The query is planned, never run; use it when a query is slow, before rewriting it.";

    public override JsonElement JsonSchema => Schema;

    public async Task<string> ExplainAsync(string sql, string? connection, IReadOnlyList<SqlParameterValue> parameters, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(parameters);
        if (MySqlReadOnlyGate.Check(sql) is { } refused)
        {
            return refused;
        }

        string body = MySqlReadOnlyGate.Body(sql);
        if (await OutsideAsync(body, connection, cancellationToken).ConfigureAwait(false) is { } outside)
        {
            return outside;
        }

        var run = await Server.RunAsync(connection, [MySqlPlan.Prefix + body], parameters, MySqlAccess.MaxCatalogRows, TimeoutSeconds(Effective), cancellationToken, readCell: QueryPlan.WholeCell).ConfigureAwait(false);
        if (run.Outcome != SqlOutcome.Ok)
        {
            return run.Outcome == SqlOutcome.Failed && LeadsWithWith(body) && run.Detail.StartsWith(MySqlText.ServerError(SyntaxError, ""), StringComparison.Ordinal)
                ? QueryPlanText.WithRefused(run.Detail)
                : MySqlText.Error(run);
        }

        string text = QueryPlan.TextOf(run, 0);
        int cap = Math.Min(QueryPlan.MaxAnswerChars, SqlTool.ResultChars(Effective));
        return MySqlPlan.Parse(text) is { } plan
            ? QueryPlanText.Answer(plan, run.Connection, run.Database, run.Elapsed, cap)
            : QueryPlanText.Unreadable(run.Connection, run.Database, run.Elapsed, text);
    }

    /// <summary>Whether the statement leads with WITH (past any opening brackets).</summary>
    internal static bool LeadsWithWith(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        string lead = body.TrimStart().TrimStart('(').TrimStart();
        return lead.Length > 4 && lead.StartsWith("WITH", StringComparison.OrdinalIgnoreCase) && !char.IsAsciiLetterOrDigit(lead[4]) && lead[4] != '_';
    }

    protected override async ValueTask<object?> InvokeToolAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (SqlQueryTool.ReadParams(arguments, MySqlText.BadParams, out var error) is not { } parameters)
        {
            return error;
        }

        return await ExplainAsync(ToolArguments.ReadString(arguments, MySqlQueryTool.SqlArgument), Optional(arguments, ConnectionArgument), parameters, cancellationToken).ConfigureAwait(false);
    }
}
