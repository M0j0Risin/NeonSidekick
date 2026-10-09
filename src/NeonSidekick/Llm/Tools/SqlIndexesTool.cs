using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Settings;
using NeonSidekick.Sql;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>sql_indexes(connection?, table?, schema?, missing?)</c> (later on 2026-09-23, the user's ask: "I don't
/// see any tools to retrieve indexes"): the indexes of a table, a schema or the database — kind, key and included
/// columns, filter, size — from the catalog views any reader may see; then how each has been used since the server
/// started (<c>sys.dm_db_index_usage_stats</c>) and, with <c>missing</c>, the optimizer's missing-index suggestions.
/// The two DMV reads are separate batches: a login without <c>VIEW SERVER STATE</c> still gets the indexes, with a
/// line saying why the rest is missing, never a failed call.
/// </summary>
public sealed class SqlIndexesTool : SqlTool
{
    public const string ToolName = "sql_indexes";
    public const string MissingArgument = "missing";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        $$"""
        {
          "type": "object",
          "properties": {
            {{ConnectionProperty}},
            "table": { "type": "string", "description": "Only this table or view's indexes (schema.name); leave it out for more." },
            "schema": { "type": "string", "description": "Only this schema's indexes; leave table and schema out for the whole database." },
            "missing": { "type": "boolean", "description": "Also list the server's missing-index suggestions (true); leave it out for the indexes alone." }
          }
        }
        """);

    public SqlIndexesTool(SqlAccess sql, Func<AppSettingsData> effective) : base(sql, effective)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Lists the indexes of a SQL table, schema or whole database: kind, key and included columns, filter and size, " +
        "with each one's seeks, scans, lookups and updates since the server started (an index nothing reads is marked). " +
        "With missing, also the optimizer's missing-index suggestions. Use it for questions about performance or indexing.";

    public override JsonElement JsonSchema => Schema;

    public async Task<string> DescribeAsync(string? connection, string? table, string? schema, bool missing, CancellationToken cancellationToken)
    {
        object? id = null;
        string scope = schema is null ? "" : $"in schema {schema}";
        if (table is not null)
        {
            var match = await FindTableAsync(connection, table, cancellationToken).ConfigureAwait(false);
            if (match.Error is { } error)
            {
                return error;
            }

            id = match.Id;
            scope = $"on {match.Schema}.{match.Name}";
        }

        var run = await CatalogAsync(connection, SqlCatalogQueries.Indexes, [new SqlParameterValue("id", id), new SqlParameterValue("schema", table is null ? schema : null)], cancellationToken).ConfigureAwait(false);
        if (run.Outcome != SqlOutcome.Ok)
        {
            return SqlText.Error(run);
        }

        var (usage, usageError) = await ReadDmvAsync(connection, SqlCatalogQueries.IndexUsage, id, cancellationToken).ConfigureAwait(false);
        SqlGrid? suggestions = null;
        string? missingError = null;
        if (missing)
        {
            (suggestions, missingError) = await ReadDmvAsync(connection, SqlCatalogQueries.MissingIndexes, id, cancellationToken).ConfigureAwait(false);
        }

        return SqlText.Indexes(scope, run, usage, usageError, suggestions, missingError, Files.WorkingDirectory.MaxReadChars);
    }

    /// <summary>One DMV batch: its grid, or the server's refusal as the detail of a note (a timeout or a lost connection too — the indexes stand).</summary>
    private async Task<(SqlGrid? Grid, string? Error)> ReadDmvAsync(string? connection, string sql, object? id, CancellationToken cancellationToken)
    {
        var run = await CatalogAsync(connection, sql, [new SqlParameterValue("id", id)], cancellationToken).ConfigureAwait(false);
        return run.Outcome == SqlOutcome.Ok
            ? (run.Grids.Count > 0 ? run.Grids[0] : new SqlGrid([], [], false), null)
            : (null, run.Outcome == SqlOutcome.Failed ? run.Detail : SqlText.Error(run));
    }

    protected override async ValueTask<object?> InvokeToolAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!ToolArguments.TryReadBoolean(arguments, MissingArgument, out var missing, out var raw))
        {
            return SqlText.BadMissing(raw);
        }

        return await DescribeAsync(Optional(arguments, ConnectionArgument), Optional(arguments, TableArgument), Optional(arguments, SqlTablesTool.SchemaArgument), missing ?? false, cancellationToken).ConfigureAwait(false);
    }
}
