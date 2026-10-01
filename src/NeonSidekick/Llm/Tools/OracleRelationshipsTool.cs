using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Oracle;
using NeonSidekick.Settings;
using NeonSidekick.Sql;

namespace NeonSidekick.Llm.Tools;

/// <summary><c>oracle_relationships(connection?, schema?, table?)</c>: the foreign-key join paths <c>from.col -&gt; to.col</c>, every one outside Oracle's schemas, or those touching a schema or a table — so a JOIN is written from the keys, not guessed.</summary>
public sealed class OracleRelationshipsTool : OracleTool
{
    public const string ToolName = "oracle_relationships";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        $$"""
        {
          "type": "object",
          "properties": {
            {{ConnectionProperty}},
            "schema": { "type": "string", "description": "Only the keys into or out of this schema's tables; leave it out for every schema." },
            "table": { "type": "string", "description": "Only the keys into or out of this table (SCHEMA.NAME); leave it out for more." }
          }
        }
        """);

    public OracleRelationshipsTool(OracleAccess oracle, Func<AppSettingsData> effective) : base(oracle, effective)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Lists the foreign-key relationships of an Oracle database as join paths, from_table.from_column -> to_table.to_column, " +
        "every one, those touching one schema, or those touching one table. Use them to write JOINs without guessing column names.";

    public override JsonElement JsonSchema => Schema;

    public async Task<string> DescribeAsync(string? connection, string? schema, string? table, CancellationToken cancellationToken)
    {
        if (!TryReadSchema(schema, out var owner, out var error))
        {
            return error!;
        }

        string? tableOwner = null;
        string? tableName = null;
        string? label = owner is null ? null : "schema " + owner;
        if (table is not null)
        {
            var match = await FindTableAsync(connection, owner, table, cancellationToken).ConfigureAwait(false);
            if (match.Error is { } refused)
            {
                return refused;
            }

            (tableOwner, tableName) = (match.Owner, match.Name);
            label = match.Owner + "." + match.Name;
        }

        var run = await CatalogAsync(connection, null, [OracleCatalogQueries.Relationships],
            [new SqlParameterValue("owner", tableOwner), new SqlParameterValue("name", tableName), new SqlParameterValue("schema", table is null ? owner : null)], cancellationToken).ConfigureAwait(false);
        return run.Outcome == SqlOutcome.Ok ? OracleText.Relationships(label, run, Files.WorkingDirectory.MaxReadChars) : OracleText.Error(run);
    }

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return await DescribeAsync(Optional(arguments, ConnectionArgument), Optional(arguments, SchemaArgument), Optional(arguments, TableArgument), cancellationToken).ConfigureAwait(false);
    }
}
