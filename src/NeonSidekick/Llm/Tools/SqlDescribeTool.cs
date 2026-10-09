using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Settings;
using NeonSidekick.Sql;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>sql_describe(table, connection?)</c>: one table or view's columns (types as declared, nullability,
/// identity, default, primary key), its foreign keys out and in, and its indexes. A bare name finds the one schema
/// that has it; an unknown name is an explicit not-found (the <c>mcp-mssql-read</c> note: a column-less answer
/// sent a model looping).
/// </summary>
public sealed class SqlDescribeTool : SqlTool
{
    public const string ToolName = "sql_describe";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        $$"""
        {
          "type": "object",
          "properties": {
            "table": { "type": "string", "description": "The table or view as schema.name (Sales.SalesOrderHeader); a bare name works when only one schema has it." },
            {{ConnectionProperty}}
          },
          "required": ["table"]
        }
        """);

    public SqlDescribeTool(SqlAccess sql, Func<AppSettingsData> effective) : base(sql, effective)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Describes one SQL table or view: its columns with their types, nullability, identity, defaults and primary key, " +
        "the foreign keys out of and into it (the joins), and its indexes. Use it before writing a query on the table.";

    public override JsonElement JsonSchema => Schema;

    public async Task<string> DescribeAsync(string table, string? connection, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(table))
        {
            return SqlText.NoTable;
        }

        var match = await FindTableAsync(connection, table.Trim(), cancellationToken).ConfigureAwait(false);
        if (match.Error is { } error)
        {
            return error;
        }

        var run = await CatalogAsync(connection, SqlCatalogQueries.Describe, [new SqlParameterValue("id", match.Id)], cancellationToken).ConfigureAwait(false);
        return run.Outcome == SqlOutcome.Ok ? SqlText.Describe(match.Schema, match.Name, match.Kind, run, Files.WorkingDirectory.MaxReadChars) : SqlText.Error(run);
    }

    protected override async ValueTask<object?> InvokeToolAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return await DescribeAsync(ToolArguments.ReadString(arguments, TableArgument), Optional(arguments, ConnectionArgument), cancellationToken).ConfigureAwait(false);
    }
}
