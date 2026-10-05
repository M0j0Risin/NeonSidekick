using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Settings;
using NeonSidekick.Sql;

namespace NeonSidekick.Llm.Tools;

/// <summary><c>sql_connections()</c>: the named connections of <c>sql.json</c> — server, database, sign-in, description, the default marked; never a password. Touches no server.</summary>
public sealed class SqlConnectionsTool : SqlTool
{
    public const string ToolName = "sql_connections";

    private static readonly JsonElement Schema = ToolSchema.Parse("""{ "type": "object", "properties": {} }""");

    public SqlConnectionsTool(SqlAccess sql, Func<AppSettingsData> effective) : base(sql, effective)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Lists the SQL Server connections the user has set up: each one's name, server, database, how it signs in and what it holds, the default marked. " +
        "Pass a name as \"connection\" to the other sql_ tools.";

    public override JsonElement JsonSchema => Schema;

    public string Describe() => SqlText.Connections(Sql.Catalog(), Effective.SqlDefaultConnection, DatabaseWriteModes.IsReadWrite(Effective.SqlMode));

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) =>
        new(Describe());
}
