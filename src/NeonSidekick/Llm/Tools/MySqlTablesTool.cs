using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.MySql;
using NeonSidekick.Settings;
using NeonSidekick.Sql;

namespace NeonSidekick.Llm.Tools;

/// <summary><c>mysql_tables(connection?, database?, pattern?)</c>: the tables and views as <c>database.name</c> with their kind, the engine's row estimate and their comment.</summary>
public sealed class MySqlTablesTool : MySqlTool
{
    public const string ToolName = "mysql_tables";
    public const string PatternArgument = "pattern";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        $$"""
        {
          "type": "object",
          "properties": {
            {{ConnectionProperty}},
            "database": { "type": "string", "description": "Only this database; leave it out for the connection's own (or every one, when it has none)." },
            "pattern": { "type": "string", "description": "Only names containing this text (case-insensitive), or matching it as a LIKE pattern when it holds % over name or database.name; leave it out for all." }
          }
        }
        """);

    public MySqlTablesTool(MySqlAccess mysql, Func<AppSettingsData> effective) : base(mysql, effective)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Lists the tables and views of a MySQL database as database.name, with each one's kind, approximate row count and comment. " +
        "Narrow it with database and pattern; then mysql_describe a table before querying it.";

    public override JsonElement JsonSchema => Schema;

    public async Task<string> DescribeAsync(string? connection, string? database, string? pattern, CancellationToken cancellationToken)
    {
        var run = await CatalogAsync(connection, [MySqlCatalogQueries.Tables], [new SqlParameterValue("database", database), new SqlParameterValue("pattern", OracleTablesTool.LikePattern(pattern))], cancellationToken).ConfigureAwait(false);
        if (run.Outcome != SqlOutcome.Ok)
        {
            return MySqlText.Error(run);
        }

        var listed = run.Grids.Count > 0 ? run with { Grids = [SqlText.WithoutEmptyColumn(run.Grids[0], "description")], Database = database ?? run.Database } : run;
        return MySqlText.Listing("table or view", "tables and views", listed, Files.WorkingDirectory.MaxReadChars);
    }

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return await DescribeAsync(Optional(arguments, ConnectionArgument), Optional(arguments, DatabaseArgument), Optional(arguments, PatternArgument), cancellationToken).ConfigureAwait(false);
    }
}
