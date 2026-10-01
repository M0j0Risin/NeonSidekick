using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.MySql;
using NeonSidekick.Settings;
using NeonSidekick.Sql;

namespace NeonSidekick.Llm.Tools;

/// <summary><c>mysql_columns(pattern, connection?, database?)</c>: every table and view column whose name matches — where a value lives before the model guesses a table — with its type, nullability and comment.</summary>
public sealed class MySqlColumnsTool : MySqlTool
{
    public const string ToolName = "mysql_columns";
    public const string PatternArgument = "pattern";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        $$"""
        {
          "type": "object",
          "properties": {
            "pattern": { "type": "string", "description": "The column name, or part of it (email, customer_id), or a LIKE pattern with %; case-insensitive." },
            {{ConnectionProperty}},
            "database": { "type": "string", "description": "Only this database's tables and views; leave it out for the connection's own (or every one, when it has none)." }
          },
          "required": ["pattern"]
        }
        """);

    public MySqlColumnsTool(MySqlAccess mysql, Func<AppSettingsData> effective) : base(mysql, effective)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Finds the columns of a MySQL database whose name matches, across every table and view: where each lives, its type, whether it allows NULL, and its comment. " +
        "Use it to learn which table holds a value before describing or querying it.";

    public override JsonElement JsonSchema => Schema;

    public async Task<string> DescribeAsync(string pattern, string? connection, string? database, CancellationToken cancellationToken)
    {
        if (OracleTablesTool.LikePattern(pattern) is not { } like)
        {
            return MySqlText.NoPattern;
        }

        var run = await CatalogAsync(connection, [MySqlCatalogQueries.Columns], [new SqlParameterValue("pattern", like), new SqlParameterValue("database", database)], cancellationToken).ConfigureAwait(false);
        return run.Outcome == SqlOutcome.Ok ? MySqlText.Columns(pattern.Trim(), run with { Database = database ?? run.Database }, Files.WorkingDirectory.MaxReadChars) : MySqlText.Error(run);
    }

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return await DescribeAsync(ToolArguments.ReadString(arguments, PatternArgument), Optional(arguments, ConnectionArgument), Optional(arguments, DatabaseArgument), cancellationToken).ConfigureAwait(false);
    }
}
