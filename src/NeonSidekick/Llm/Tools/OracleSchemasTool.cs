using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Oracle;
using NeonSidekick.Settings;
using NeonSidekick.Sql;

namespace NeonSidekick.Llm.Tools;

/// <summary><c>oracle_schemas(connection?)</c>: the schemas the connection's account can see, Oracle's own left out, with their table and view counts — <c>sql_databases</c>' part, since an Oracle connection is one database of many schemas.</summary>
public sealed class OracleSchemasTool : OracleTool
{
    public const string ToolName = "oracle_schemas";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        $$"""
        {
          "type": "object",
          "properties": {
            {{ConnectionProperty}}
          }
        }
        """);

    public OracleSchemasTool(OracleAccess oracle, Func<AppSettingsData> effective) : base(oracle, effective)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Lists the schemas of an Oracle connection that its account can see (Oracle's own left out), with each one's table and view counts; the account's own is marked. " +
        "Pass one as \"schema\" to the other oracle_ tools to work in it.";

    public override JsonElement JsonSchema => Schema;

    public async Task<string> DescribeAsync(string? connection, CancellationToken cancellationToken)
    {
        var run = await CatalogAsync(connection, null, [OracleCatalogQueries.Schemas], [], cancellationToken).ConfigureAwait(false);
        if (run.Outcome != SqlOutcome.Ok)
        {
            return OracleText.Error(run);
        }

        var listed = run.Grids.Count > 0 ? run with { Grids = [SqlText.WithoutEmptyColumn(run.Grids[0], "note")], Database = "" } : run;
        return OracleText.Listing("schema", "schemas", listed, Files.WorkingDirectory.MaxReadChars);
    }

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return await DescribeAsync(Optional(arguments, ConnectionArgument), cancellationToken).ConfigureAwait(false);
    }
}
