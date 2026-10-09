using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.MySql;
using NeonSidekick.Settings;
using NeonSidekick.Sql;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>mysql_indexes(connection?, table?)</c>: the indexes of a table or a database — kind, key columns, the cardinality
/// estimate — then each one's reads and writes since the server started (<c>performance_schema</c>), a separate statement so an
/// account (or a MariaDB with it off) still gets the indexes, with a line saying why the rest is missing.
/// </summary>
public sealed class MySqlIndexesTool : MySqlTool
{
    public const string ToolName = "mysql_indexes";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        $$"""
        {
          "type": "object",
          "properties": {
            {{ConnectionProperty}},
            "table": { "type": "string", "description": "Only this table's indexes; leave it out for the whole database." }
          }
        }
        """);

    public MySqlIndexesTool(MySqlAccess mysql, Func<AppSettingsData> effective) : base(mysql, effective)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Lists the indexes of a MySQL table or database: kind, key columns and the cardinality estimate, " +
        "with each one's reads and writes since the server started when performance_schema allows (an index nothing reads is marked). Use it for questions about performance or indexing.";

    public override JsonElement JsonSchema => Schema;

    public async Task<string> DescribeAsync(string? connection, string? table, CancellationToken cancellationToken)
    {
        string? name = null;
        string scope = "";
        if (table is not null)
        {
            var match = await FindTableAsync(connection, table, cancellationToken).ConfigureAwait(false);
            if (match.Error is { } refused)
            {
                return refused;
            }

            name = match.Name;
            scope = $"on {match.Database}.{match.Name}";
        }

        SqlParameterValue[] parameters = [new("name", name)];
        var run = await CatalogAsync(connection, [MySqlCatalogQueries.Indexes], parameters, cancellationToken).ConfigureAwait(false);
        if (run.Outcome != SqlOutcome.Ok)
        {
            return MySqlText.Error(run);
        }

        var usage = await CatalogAsync(connection, [MySqlCatalogQueries.IndexUsage], parameters, cancellationToken).ConfigureAwait(false);
        var (grid, usageError) = usage.Outcome == SqlOutcome.Ok
            ? (usage.Grids.Count > 0 ? usage.Grids[0] : new SqlGrid([], [], false), (string?)null)
            : ((SqlGrid?)null, usage.Outcome == SqlOutcome.Failed ? usage.Detail : MySqlText.Error(usage));
        return MySqlText.Indexes(scope, run, grid, usageError, Files.WorkingDirectory.MaxReadChars);
    }

    protected override async ValueTask<object?> InvokeToolAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return await DescribeAsync(Optional(arguments, ConnectionArgument), Optional(arguments, TableArgument), cancellationToken).ConfigureAwait(false);
    }
}
