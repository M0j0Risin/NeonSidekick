using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Oracle;
using NeonSidekick.Settings;
using NeonSidekick.Sql;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>oracle_indexes(connection?, table?, schema?)</c>: the indexes of a table, a schema or every schema — kind, key columns,
/// status, the optimizer's counts — from the <c>ALL_</c> views any reader may see; then how each has been used
/// (<c>DBA_INDEX_USAGE</c>, 12.2 and later), a separate statement so an account without the dictionary role still gets the
/// indexes, with a line saying why the rest is missing. Oracle keeps no missing-index hints, so <c>sql_indexes</c>'
/// <c>missing</c> has no twin here.
/// </summary>
public sealed class OracleIndexesTool : OracleTool
{
    public const string ToolName = "oracle_indexes";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        $$"""
        {
          "type": "object",
          "properties": {
            {{ConnectionProperty}},
            "table": { "type": "string", "description": "Only this table's indexes; leave it out for the whole schema." }
          }
        }
        """);

    public OracleIndexesTool(OracleAccess oracle, Func<AppSettingsData> effective) : base(oracle, effective)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Lists the indexes of an Oracle table or of the connection's schema: kind, key columns, status and visibility, the optimizer's row and distinct-key counts, " +
        "with each one's recorded use when the account may read it (an index with none recorded is marked). Use it for questions about performance or indexing.";

    public override JsonElement JsonSchema => Schema;

    public async Task<string> DescribeAsync(string? connection, string? table, CancellationToken cancellationToken)
    {
        string? tableName = null;
        string scope = "";
        if (table is not null)
        {
            var match = await FindTableAsync(connection, table, cancellationToken).ConfigureAwait(false);
            if (match.Error is { } refused)
            {
                return refused;
            }

            tableName = match.Name;
            scope = $"on {match.Owner}.{match.Name}";
        }

        var run = await CatalogAsync(connection, [OracleCatalogQueries.Indexes], [new SqlParameterValue("name", tableName)], cancellationToken).ConfigureAwait(false);
        if (run.Outcome != SqlOutcome.Ok)
        {
            return OracleText.Error(run);
        }

        var usage = await CatalogAsync(connection, [OracleCatalogQueries.IndexUsage], [], cancellationToken).ConfigureAwait(false);
        var (grid, usageError) = usage.Outcome == SqlOutcome.Ok
            ? (usage.Grids.Count > 0 ? usage.Grids[0] : new SqlGrid([], [], false), (string?)null)
            : ((SqlGrid?)null, usage.Outcome == SqlOutcome.Failed ? usage.Detail : OracleText.Error(usage));
        return OracleText.Indexes(scope, run, grid, usageError, Files.WorkingDirectory.MaxReadChars);
    }

    protected override async ValueTask<object?> InvokeToolAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return await DescribeAsync(Optional(arguments, ConnectionArgument), Optional(arguments, TableArgument), cancellationToken).ConfigureAwait(false);
    }
}
