using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Oracle;
using NeonSidekick.Settings;
using NeonSidekick.Sql;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>oracle_columns(pattern, connection?, schema?)</c>: every table and view column whose name matches — where an email
/// address or a customer id lives, before the model guesses a table — with its type, nullability and comment.
/// </summary>
public sealed class OracleColumnsTool : OracleTool
{
    public const string ToolName = "oracle_columns";
    public const string PatternArgument = "pattern";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        $$"""
        {
          "type": "object",
          "properties": {
            "pattern": { "type": "string", "description": "The column name, or part of it (EMAIL, CUSTOMER_ID), or a LIKE pattern with %; case-insensitive." },
            {{ConnectionProperty}}
          },
          "required": ["pattern"]
        }
        """);

    public OracleColumnsTool(OracleAccess oracle, Func<AppSettingsData> effective) : base(oracle, effective)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Finds the columns of an Oracle database whose name matches, across every table and view: where each lives, its type, whether it allows NULL, and its comment. " +
        "Use it to learn which table holds a value before describing or querying it.";

    public override JsonElement JsonSchema => Schema;

    public async Task<string> DescribeAsync(string pattern, string? connection, CancellationToken cancellationToken)
    {
        if (OracleTablesTool.LikePattern(pattern) is not { } like)
        {
            return OracleText.NoPattern;
        }

        var run = await CatalogAsync(connection, [OracleCatalogQueries.Columns], [new SqlParameterValue("pattern", like)], cancellationToken).ConfigureAwait(false);
        return run.Outcome == SqlOutcome.Ok ? OracleText.Columns(pattern.Trim(), run, Files.WorkingDirectory.MaxReadChars) : OracleText.Error(run);
    }

    protected override async ValueTask<object?> InvokeToolAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return await DescribeAsync(ToolArguments.ReadString(arguments, PatternArgument), Optional(arguments, ConnectionArgument), cancellationToken).ConfigureAwait(false);
    }
}
