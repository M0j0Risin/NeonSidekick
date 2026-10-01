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
            "table": { "type": "string", "description": "Only this table's indexes (SCHEMA.NAME); leave it out for more." },
            "schema": { "type": "string", "description": "Only this schema's indexes; leave table and schema out for every schema." }
          }
        }
        """);

    public OracleIndexesTool(OracleAccess oracle, Func<AppSettingsData> effective) : base(oracle, effective)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Lists the indexes of an Oracle table, schema or whole database: kind, key columns, status and visibility, the optimizer's row and distinct-key counts, " +
        "with each one's recorded use when the account may read it (an index with none recorded is marked). Use it for questions about performance or indexing.";

    public override JsonElement JsonSchema => Schema;

    public async Task<string> DescribeAsync(string? connection, string? table, string? schema, CancellationToken cancellationToken)
    {
        if (!TryReadSchema(schema, out var owner, out var error))
        {
            return error!;
        }

        string? tableOwner = null;
        string? tableName = null;
        string scope = owner is null ? "" : $"in schema {owner}";
        if (table is not null)
        {
            var match = await FindTableAsync(connection, owner, table, cancellationToken).ConfigureAwait(false);
            if (match.Error is { } refused)
            {
                return refused;
            }

            (tableOwner, tableName) = (match.Owner, match.Name);
            scope = $"on {match.Owner}.{match.Name}";
        }

        var run = await CatalogAsync(connection, null, [OracleCatalogQueries.Indexes],
            [new SqlParameterValue("owner", tableOwner), new SqlParameterValue("name", tableName), new SqlParameterValue("schema", table is null ? owner : null)], cancellationToken).ConfigureAwait(false);
        if (run.Outcome != SqlOutcome.Ok)
        {
            return OracleText.Error(run);
        }

        var usage = await CatalogAsync(connection, null, [OracleCatalogQueries.IndexUsage], [new SqlParameterValue("owner", tableOwner ?? owner)], cancellationToken).ConfigureAwait(false);
        var (grid, usageError) = usage.Outcome == SqlOutcome.Ok
            ? (usage.Grids.Count > 0 ? usage.Grids[0] : new SqlGrid([], [], false), (string?)null)
            : ((SqlGrid?)null, usage.Outcome == SqlOutcome.Failed ? usage.Detail : OracleText.Error(usage));
        return OracleText.Indexes(scope, run, grid, usageError, Files.WorkingDirectory.MaxReadChars);
    }

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return await DescribeAsync(Optional(arguments, ConnectionArgument), Optional(arguments, TableArgument), Optional(arguments, SchemaArgument), cancellationToken).ConfigureAwait(false);
    }
}
