using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Oracle;
using NeonSidekick.Settings;
using NeonSidekick.Sql;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>oracle_describe(table, connection?, schema?)</c>: one table or view's columns (types as declared, nullability, identity,
/// virtual, default, primary key, comment), its foreign keys out and in, its indexes, CHECK constraints and triggers. A bare
/// name finds the one in the call's schema, else the one schema that has it; an unknown name is an explicit not-found.
/// </summary>
public sealed class OracleDescribeTool : OracleTool
{
    public const string ToolName = "oracle_describe";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        $$"""
        {
          "type": "object",
          "properties": {
            "table": { "type": "string", "description": "The table or view's name (EMPLOYEES), or SCHEMA.NAME with the connection's schema. Unquoted names are upper-cased, as Oracle does." },
            {{ConnectionProperty}}
          },
          "required": ["table"]
        }
        """);

    public OracleDescribeTool(OracleAccess oracle, Func<AppSettingsData> effective) : base(oracle, effective)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Describes one Oracle table or view: its columns with their types, nullability, identity, defaults, primary key and comments, " +
        "the foreign keys out of and into it (the joins), its indexes, check constraints and triggers. Use it before writing a query on the table.";

    public override JsonElement JsonSchema => Schema;

    public async Task<string> DescribeAsync(string table, string? connection, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(table))
        {
            return OracleText.NoTable;
        }

        var match = await FindTableAsync(connection, table.Trim(), cancellationToken).ConfigureAwait(false);
        if (match.Error is { } refused)
        {
            return refused;
        }

        var run = await CatalogAsync(connection, OracleCatalogQueries.Describe, [new SqlParameterValue("owner", match.Owner), new SqlParameterValue("name", match.Name)], cancellationToken).ConfigureAwait(false);
        return run.Outcome == SqlOutcome.Ok ? OracleText.Describe(match.Owner, match.Name, match.Kind, run, Files.WorkingDirectory.MaxReadChars) : OracleText.Error(run);
    }

    protected override async ValueTask<object?> InvokeToolAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return await DescribeAsync(ToolArguments.ReadString(arguments, TableArgument), Optional(arguments, ConnectionArgument), cancellationToken).ConfigureAwait(false);
    }
}
