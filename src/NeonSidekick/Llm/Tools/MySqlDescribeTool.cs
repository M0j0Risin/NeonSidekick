using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.MySql;
using NeonSidekick.Settings;
using NeonSidekick.Sql;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>mysql_describe(table, connection?, database?)</c>: one table or view's columns (types as declared, nullability,
/// auto_increment, default, primary key, comment), its foreign keys out and in, its indexes, CHECK constraints and triggers.
/// </summary>
public sealed class MySqlDescribeTool : MySqlTool
{
    public const string ToolName = "mysql_describe";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        $$"""
        {
          "type": "object",
          "properties": {
            "table": { "type": "string", "description": "The table or view as database.name (shop.orders); a bare name works when the database in force or only one database has it." },
            {{ConnectionProperty}},
            "database": { "type": "string", "description": "The database a bare table name is looked up in first; leave it out for the connection's own." }
          },
          "required": ["table"]
        }
        """);

    public MySqlDescribeTool(MySqlAccess mysql, Func<AppSettingsData> effective) : base(mysql, effective)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Describes one MySQL table or view: its columns with their types, nullability, auto_increment, defaults, primary key and comments, " +
        "the foreign keys out of and into it (the joins), its indexes, check constraints and triggers. Use it before writing a query on the table.";

    public override JsonElement JsonSchema => Schema;

    public async Task<string> DescribeAsync(string table, string? connection, string? database, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(table))
        {
            return MySqlText.NoTable;
        }

        var match = await FindTableAsync(connection, database, table.Trim(), cancellationToken).ConfigureAwait(false);
        if (match.Error is { } refused)
        {
            return refused;
        }

        var run = await CatalogAsync(connection, MySqlCatalogQueries.Describe, [new SqlParameterValue("db", match.Database), new SqlParameterValue("name", match.Name)], cancellationToken).ConfigureAwait(false);
        return run.Outcome == SqlOutcome.Ok ? MySqlText.Describe(match.Database, match.Name, match.Kind, run, Files.WorkingDirectory.MaxReadChars) : MySqlText.Error(run);
    }

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return await DescribeAsync(ToolArguments.ReadString(arguments, TableArgument), Optional(arguments, ConnectionArgument), Optional(arguments, DatabaseArgument), cancellationToken).ConfigureAwait(false);
    }
}
