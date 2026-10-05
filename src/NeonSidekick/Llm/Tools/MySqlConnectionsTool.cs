using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.MySql;
using NeonSidekick.Settings;
using NeonSidekick.Sql;

namespace NeonSidekick.Llm.Tools;

/// <summary><c>mysql_connections()</c>: the named connections of <c>mysql.json</c> — host, database, user, description, the default marked; never a password. Touches no server.</summary>
public sealed class MySqlConnectionsTool : MySqlTool
{
    public const string ToolName = "mysql_connections";

    private static readonly JsonElement Schema = ToolSchema.Parse("""{ "type": "object", "properties": {} }""");

    public MySqlConnectionsTool(MySqlAccess mysql, Func<AppSettingsData> effective) : base(mysql, effective)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Lists the MySQL and MariaDB connections the user has set up: each one's name, host, database, user and what it holds, the default marked. " +
        "Pass a name as \"connection\" to the other mysql_ tools.";

    public override JsonElement JsonSchema => Schema;

    public string Describe() => MySqlText.Connections(Server.Catalog(), Effective.MySqlDefaultConnection, DatabaseWriteModes.IsReadWrite(Effective.MySqlMode));

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) =>
        new(Describe());
}
