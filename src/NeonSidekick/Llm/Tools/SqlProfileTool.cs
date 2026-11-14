using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Settings;
using NeonSidekick.Sql;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>sql_profile(table, connection?, columns?, top?)</c> (2026-11-14, the database-tools plan's #14): one table or view's data at a glance —
/// its rows, and per column the nulls, distinct values, min, max and most common values — in one answer, so the model need not page sample
/// rows into its context. The table found as <c>sql_describe</c> finds it (the connection's one database), the SQL the app's own
/// (<see cref="SqlServerProfile"/>), run in the read transaction that is rolled back; a table past <see cref="TableProfile.SampleAbove"/> rows
/// is sampled, and the answer says so.
/// </summary>
public sealed class SqlProfileTool : SqlTool
{
    public const string ToolName = "sql_profile";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        $$"""
        {
          "type": "object",
          "properties": {
            "table": { "type": "string", "description": "The table or view as schema.name (Sales.SalesOrderHeader); a bare name works when only one schema has it." },
            {{ConnectionProperty}},
            {{ProfileArguments.ColumnsProperty}},
            {{ProfileArguments.TopProperty}}
          },
          "required": ["table"]
        }
        """);

    public SqlProfileTool(SqlAccess sql, Func<AppSettingsData> effective) : base(sql, effective)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Profiles one SQL table or view: its rows and, per column, the nulls, distinct values, min, max and most common values (a big table is " +
        "sampled, and the answer says so). Read-only; use it before reading pages of sample rows.";

    public override JsonElement JsonSchema => Schema;

    public async Task<string> ProfileAsync(string table, string? connection, IReadOnlyList<string>? columns, int top, CancellationToken cancellationToken)
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

        var effective = Effective;
        int timeout = TimeoutSeconds(effective);
        var catalog = await Sql.RunAsync(connection, SqlServerProfile.Catalog, [new SqlParameterValue("id", match.Id)], SqlAccess.MaxCatalogRows, timeout, cancellationToken, keepLaterSets: true).ConfigureAwait(false);
        if (catalog.Outcome != SqlOutcome.Ok)
        {
            return SqlText.Error(catalog);
        }

        // name, declared type, system type, max_length; then rows, compatibility level
        var all = catalog.Grids.Count > 0 ? catalog.Grids[0].Rows.Select(r => new ProfileColumn(r[0], r[1], SqlServerProfile.Measure(r[2], TableProfile.Whole(r[3])))).ToList() : [];
        var facts = catalog.Grids.Count > 1 ? catalog.Grids[1].Rows.FirstOrDefault() : null;
        var dialect = SqlServerProfile.For(TableProfile.Whole(facts?[1]));
        var request = new TableProfile.Request(match.Schema + "." + match.Name, dialect.Quote(match.Schema) + "." + dialect.Quote(match.Name), match.Kind, all, columns, top,
            match.Kind == "table" ? TableProfile.Whole(facts?[0]) : null, timeout, Math.Min(QueryPlan.MaxAnswerChars, ResultChars(effective)), SqlDescribeTool.ToolName, StringComparison.OrdinalIgnoreCase);
        return await TableProfile.RunAsync(request, dialect,
            (statements, seconds, token) => Sql.RunAsync(connection, string.Join(";\n", statements), [], SqlAccess.MaxCatalogRows, seconds, token, keepLaterSets: true),
            SqlText.Error, cancellationToken).ConfigureAwait(false);
    }

    protected override async ValueTask<object?> InvokeToolAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (ProfileArguments.Read(arguments, out var columns, out int top) is { } refused)
        {
            return refused;
        }

        return await ProfileAsync(ToolArguments.ReadString(arguments, TableArgument), Optional(arguments, ConnectionArgument), columns, top, cancellationToken).ConfigureAwait(false);
    }
}
