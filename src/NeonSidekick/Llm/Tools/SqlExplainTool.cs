using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Settings;
using NeonSidekick.Sql;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>sql_explain(sql, connection?, params?)</c> (2026-11-14, the database-tools plan's #12): how SQL Server would run one read, from its
/// estimated showplan. The statement passes <see cref="SqlReadOnlyGate"/> and the database boundary as <c>sql_query</c>'s does, then runs
/// on the same path (the guarded open, the wrong-database check, the rolled-back read transaction, the timeout) between
/// <c>SET SHOWPLAN_XML ON</c> and <c>OFF</c> (<see cref="SqlServerPlan"/>): compiled, never run. The answer is
/// <see cref="QueryPlanText.Answer"/>'s summary, ending with a pointer to <c>sql_indexes</c>' suggestions.
/// </summary>
public sealed class SqlExplainTool : SqlTool
{
    public const string ToolName = "sql_explain";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        $$"""
        {
          "type": "object",
          "properties": {
            "sql": { "type": "string", "description": "The T-SQL SELECT to explain, as sql_query takes it (a WITH … CTE may lead it). Give the query alone: the tool asks for the plan itself." },
            {{ConnectionProperty}},
            "params": { "type": "object", "description": "Values for @name placeholders in the SQL, as sql_query takes them. SQL Server plans them as unknown values: its guess from the column's statistics, not the given ones." }
          },
          "required": ["sql"]
        }
        """);

    public SqlExplainTool(SqlAccess sql, Func<AppSettingsData> effective) : base(sql, effective)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Shows how SQL Server would run one SELECT: its estimated plan's steps (scans, seeks, lookups, joins, with estimated rows and cost), missing-index hints and warnings. " +
        "The query is planned, never run; use it when a query is slow, before rewriting it.";

    public override JsonElement JsonSchema => Schema;

    public async Task<string> ExplainAsync(string sql, string? connection, IReadOnlyList<SqlParameterValue> parameters, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(parameters);
        if ((SqlReadOnlyGate.Check(sql) ?? await OutsideAsync(sql, connection, cancellationToken).ConfigureAwait(false)) is { } refused)
        {
            return refused;
        }

        // The values as DECLAREs in the batch: a call with parameters goes as an RPC, which SHOWPLAN_XML doesn't plan.
        var effective = Effective;
        var run = await Sql.RunAsync(connection, SqlServerPlan.WithDeclarations(sql, parameters), [], SqlAccess.MaxCatalogRows, TimeoutSeconds(effective), cancellationToken,
            readCell: QueryPlan.WholeCell, showPlan: true).ConfigureAwait(false);
        if (run.Outcome != SqlOutcome.Ok)
        {
            return run.Outcome == SqlOutcome.Failed && run.Detail.Contains("Msg " + SqlServerPlan.NoShowplanPermission.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",", StringComparison.Ordinal)
                ? QueryPlanText.NeedsShowplan
                : SqlText.Error(run);
        }

        string text = QueryPlan.TextOf(run, 0);
        return ExplainAnswer.Of(SqlServerPlan.Parse(text), run, text, effective, QueryPlanText.SqlServerFooter);
    }

    protected override async ValueTask<object?> InvokeToolAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (SqlQueryTool.ReadParams(arguments, SqlText.BadParams, out var error) is not { } parameters)
        {
            return error;
        }

        return await ExplainAsync(ToolArguments.ReadString(arguments, SqlQueryTool.SqlArgument), Optional(arguments, ConnectionArgument), parameters, cancellationToken).ConfigureAwait(false);
    }
}
