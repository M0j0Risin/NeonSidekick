using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Settings;
using NeonSidekick.Sql;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>sql_columns(pattern, connection?, schema?)</c> (later on 2026-09-23, the user's pick): every table and
/// view column whose name matches — where an email address or a customer id lives, before the model guesses a
/// table — with its type, nullability and description.
/// </summary>
public sealed class SqlColumnsTool : SqlTool
{
    public const string ToolName = "sql_columns";
    public const string PatternArgument = "pattern";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        $$"""
        {
          "type": "object",
          "properties": {
            "pattern": { "type": "string", "description": "The column name, or part of it (EmailAddress, CustomerID), or a LIKE pattern with % and _." },
            {{ConnectionProperty}},
            "schema": { "type": "string", "description": "Only this schema's tables and views; leave it out for all." }
          },
          "required": ["pattern"]
        }
        """);

    public SqlColumnsTool(SqlAccess sql, Func<AppSettingsData> effective) : base(sql, effective)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Finds the columns of a SQL database whose name matches, across every table and view: where each lives, its type, whether it allows NULL, and its description. " +
        "Use it to learn which table holds a value before describing or querying it.";

    public override JsonElement JsonSchema => Schema;

    public async Task<string> DescribeAsync(string pattern, string? connection, string? schema, CancellationToken cancellationToken)
    {
        if (SqlTablesTool.LikePattern(pattern) is not { } like)
        {
            return SqlText.NoPattern;
        }

        var run = await CatalogAsync(connection, SqlCatalogQueries.Columns, [new SqlParameterValue("pattern", like), new SqlParameterValue("schema", schema)], cancellationToken).ConfigureAwait(false);
        return run.Outcome == SqlOutcome.Ok ? SqlText.Columns(pattern.Trim(), run, Files.WorkingDirectory.MaxReadChars) : SqlText.Error(run);
    }

    protected override async ValueTask<object?> InvokeToolAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return await DescribeAsync(ToolArguments.ReadString(arguments, PatternArgument), Optional(arguments, ConnectionArgument), Optional(arguments, SqlTablesTool.SchemaArgument), cancellationToken).ConfigureAwait(false);
    }
}
