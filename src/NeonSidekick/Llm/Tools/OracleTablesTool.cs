using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Oracle;
using NeonSidekick.Settings;
using NeonSidekick.Sql;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>oracle_tables(connection?, schema?, pattern?)</c>: the tables and views as <c>SCHEMA.NAME</c> with their kind, the
/// optimizer's row count and their comment, narrowed by a schema and a name pattern — <c>sql_tables</c>' part.
/// </summary>
public sealed class OracleTablesTool : OracleTool
{
    public const string ToolName = "oracle_tables";
    public const string PatternArgument = "pattern";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        $$"""
        {
          "type": "object",
          "properties": {
            {{ConnectionProperty}},
            "schema": { "type": "string", "description": "Only this schema (HR, SALES, …); leave it out for every schema the account can see." },
            "pattern": { "type": "string", "description": "Only names containing this text (case-insensitive), or matching it as a LIKE pattern when it holds % over NAME or SCHEMA.NAME; leave it out for all." }
          }
        }
        """);

    public OracleTablesTool(OracleAccess oracle, Func<AppSettingsData> effective) : base(oracle, effective)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Lists the tables and views of an Oracle database as SCHEMA.NAME, with each one's kind, the optimizer's row count and its comment. " +
        "Narrow it with schema and pattern; then oracle_describe a table before querying it.";

    public override JsonElement JsonSchema => Schema;

    /// <summary>
    /// The LIKE pattern a <c>pattern</c> argument means (2026-09-30): a text with <c>%</c> as it is, <c>*</c> read as <c>%</c>,
    /// else the text anywhere in the name. Unlike <see cref="SqlTablesTool.LikePattern"/>, an <c>_</c> alone does not make it a
    /// pattern — Oracle's names are full of them (<c>NS_</c> found nothing as the SQL tools read it, against the live fixtures);
    /// inside <c>%…%</c> it still matches itself among the rest.
    /// </summary>
    public static string? LikePattern(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return null;
        }

        string text = pattern.Trim().Replace('*', '%');
        return text.Contains('%', StringComparison.Ordinal) ? text : "%" + text + "%";
    }

    public async Task<string> DescribeAsync(string? connection, string? schema, string? pattern, CancellationToken cancellationToken)
    {
        if (!TryReadSchema(schema, out var owner, out var error))
        {
            return error!;
        }

        var run = await CatalogAsync(connection, null, [OracleCatalogQueries.Tables], [new SqlParameterValue("schema", owner), new SqlParameterValue("pattern", LikePattern(pattern))], cancellationToken).ConfigureAwait(false);
        if (run.Outcome != SqlOutcome.Ok)
        {
            return OracleText.Error(run);
        }

        var listed = run.Grids.Count > 0 ? run with { Grids = [SqlText.WithoutEmptyColumn(run.Grids[0], "description")], Database = "" } : run;
        return OracleText.Listing("table or view", "tables and views", listed, Files.WorkingDirectory.MaxReadChars);
    }

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return await DescribeAsync(Optional(arguments, ConnectionArgument), Optional(arguments, SchemaArgument), Optional(arguments, PatternArgument), cancellationToken).ConfigureAwait(false);
    }
}
