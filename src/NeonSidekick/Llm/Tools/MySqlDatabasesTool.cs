using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.MySql;
using NeonSidekick.Settings;
using NeonSidekick.Sql;

namespace NeonSidekick.Llm.Tools;

/// <summary><c>mysql_databases(connection?)</c>: the databases the account can see, the server's own left out, with their table and view counts and character set; the one in force marked.</summary>
public sealed class MySqlDatabasesTool : MySqlTool
{
    public const string ToolName = "mysql_databases";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        $$"""
        {
          "type": "object",
          "properties": {
            {{ConnectionProperty}}
          }
        }
        """);

    public MySqlDatabasesTool(MySqlAccess mysql, Func<AppSettingsData> effective) : base(mysql, effective)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Lists the databases on a MySQL connection's server that its account can see (the server's own left out), with table and view counts and character set; the one in force is marked. " +
        "Pass one as \"database\" to the other mysql_ tools to work in it.";

    public override JsonElement JsonSchema => Schema;

    public async Task<string> DescribeAsync(string? connection, CancellationToken cancellationToken)
    {
        var run = await CatalogAsync(connection, [MySqlCatalogQueries.Databases], [], cancellationToken).ConfigureAwait(false);
        if (run.Outcome != SqlOutcome.Ok)
        {
            return MySqlText.Error(run);
        }

        var listed = run.Grids.Count > 0 ? run with { Grids = [SqlText.WithoutEmptyColumn(run.Grids[0], "note")], Database = "" } : run;
        return MySqlText.Listing("database", "databases", listed, Files.WorkingDirectory.MaxReadChars);
    }

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return await DescribeAsync(Optional(arguments, ConnectionArgument), cancellationToken).ConfigureAwait(false);
    }
}
