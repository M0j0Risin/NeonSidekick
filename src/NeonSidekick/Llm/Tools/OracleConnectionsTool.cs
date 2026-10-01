using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Oracle;
using NeonSidekick.Settings;

namespace NeonSidekick.Llm.Tools;

/// <summary><c>oracle_connections()</c>: the named connections of <c>oracle.json</c> — data source, user, schema, description, the default marked; never a password. Touches no server.</summary>
public sealed class OracleConnectionsTool : OracleTool
{
    public const string ToolName = "oracle_connections";

    private static readonly JsonElement Schema = ToolSchema.Parse("""{ "type": "object", "properties": {} }""");

    public OracleConnectionsTool(OracleAccess oracle, Func<AppSettingsData> effective) : base(oracle, effective)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Lists the Oracle database connections the user has set up: each one's name, data source, user, schema and what it holds, the default marked. " +
        "Pass a name as \"connection\" to the other oracle_ tools.";

    public override JsonElement JsonSchema => Schema;

    public string Describe() => OracleText.Connections(Oracle.Catalog(), Effective.OracleDefaultConnection);

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) =>
        new(Describe());
}
