using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Settings;
using NeonSidekick.Sql;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>sql_tables(connection?, schema?, pattern?)</c>: the user tables and views as <c>schema.name</c> —
/// the schema the <c>mcp-mssql-read</c> server's <c>list_tables</c> dropped — with their kind and approximate
/// row counts, narrowed by a schema and a name pattern.
/// </summary>
public sealed class SqlTablesTool : SqlTool
{
    public const string ToolName = "sql_tables";
    public const string SchemaArgument = "schema";
    public const string PatternArgument = "pattern";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        $$"""
        {
          "type": "object",
          "properties": {
            {{ConnectionProperty}},
            "schema": { "type": "string", "description": "Only this schema (Sales, dbo, …); leave it out for all." },
            "pattern": { "type": "string", "description": "Only names containing this text, or matching it as a LIKE pattern (% and _) over name or schema.name; leave it out for all." }
          }
        }
        """);

    public SqlTablesTool(SqlAccess sql, Func<AppSettingsData> effective) : base(sql, effective)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Lists the tables and views of a SQL database as schema.name, with each one's kind and approximate row count. " +
        "Narrow it with schema and pattern; then sql_describe a table before querying it.";

    public override JsonElement JsonSchema => Schema;

    /// <summary>
    /// The LIKE pattern a <c>pattern</c> argument means: a text with <c>%</c> as it is, <c>*</c> read as <c>%</c>, else the text anywhere in
    /// the name — <see cref="OracleTablesTool.LikePattern"/>, every family's (2026-10-24, the database review's C12: an <c>_</c> alone made it a
    /// pattern here, so <c>order_item</c> was no longer a contains search, unlike the other four families).
    /// </summary>
    public static string? LikePattern(string? pattern) => OracleTablesTool.LikePattern(pattern);

    public async Task<string> DescribeAsync(string? connection, string? schema, string? pattern, CancellationToken cancellationToken)
    {
        var run = await CatalogAsync(connection, SqlCatalogQueries.Tables, [new SqlParameterValue("schema", schema), new SqlParameterValue("pattern", LikePattern(pattern))], cancellationToken).ConfigureAwait(false);
        if (run.Outcome != SqlOutcome.Ok)
        {
            return SqlText.Error(run);
        }

        // The description column only while a table has one (later on 2026-09-23): an undocumented database lists as before.
        var listed = run.Grids.Count > 0 ? run with { Grids = [SqlText.WithoutEmptyColumn(run.Grids[0], "description")] } : run;
        return SqlText.Listing("table or view", "tables and views", listed, Files.WorkingDirectory.MaxReadChars);
    }

    protected override async ValueTask<object?> InvokeToolAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return await DescribeAsync(Optional(arguments, ConnectionArgument), Optional(arguments, SchemaArgument), Optional(arguments, PatternArgument), cancellationToken).ConfigureAwait(false);
    }
}
