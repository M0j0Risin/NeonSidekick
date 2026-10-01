using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.MySql;
using NeonSidekick.Settings;
using NeonSidekick.Sql;

namespace NeonSidekick.Llm.Tools;

/// <summary><c>mysql_relationships(connection?, database?, table?)</c>: the foreign-key join paths <c>from.col -&gt; to.col</c>, every one in a database or those touching a table.</summary>
public sealed class MySqlRelationshipsTool : MySqlTool
{
    public const string ToolName = "mysql_relationships";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        $$"""
        {
          "type": "object",
          "properties": {
            {{ConnectionProperty}},
            "database": { "type": "string", "description": "Only the keys into or out of this database's tables; leave it out for the connection's own (or every one)." },
            "table": { "type": "string", "description": "Only the keys into or out of this table (database.name); leave it out for more." }
          }
        }
        """);

    public MySqlRelationshipsTool(MySqlAccess mysql, Func<AppSettingsData> effective) : base(mysql, effective)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Lists the foreign-key relationships of a MySQL database as join paths, from_table.from_column -> to_table.to_column, " +
        "every one or only those touching one table. Use them to write JOINs without guessing column names.";

    public override JsonElement JsonSchema => Schema;

    public async Task<string> DescribeAsync(string? connection, string? database, string? table, CancellationToken cancellationToken)
    {
        string? db = null;
        string? name = null;
        string? label = database is null ? null : "database " + database;
        if (table is not null)
        {
            var match = await FindTableAsync(connection, database, table, cancellationToken).ConfigureAwait(false);
            if (match.Error is { } refused)
            {
                return refused;
            }

            (db, name) = (match.Database, match.Name);
            label = match.Database + "." + match.Name;
        }

        var run = await CatalogAsync(connection, [MySqlCatalogQueries.Relationships],
            [new SqlParameterValue("db", db), new SqlParameterValue("name", name), new SqlParameterValue("database", database)], cancellationToken).ConfigureAwait(false);
        return run.Outcome == SqlOutcome.Ok ? MySqlText.Relationships(label, run, Files.WorkingDirectory.MaxReadChars) : MySqlText.Error(run);
    }

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return await DescribeAsync(Optional(arguments, ConnectionArgument), Optional(arguments, DatabaseArgument), Optional(arguments, TableArgument), cancellationToken).ConfigureAwait(false);
    }
}
