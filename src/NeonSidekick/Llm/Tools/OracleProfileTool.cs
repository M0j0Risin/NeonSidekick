using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Oracle;
using NeonSidekick.Settings;
using NeonSidekick.Sql;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>oracle_profile(table, connection?, columns?, top?)</c> (2026-11-14, the database-tools plan's #14), <see cref="SqlProfileTool"/>'s
/// Oracle twin: the table found as <c>oracle_describe</c> finds it (the connection's schema), always written with its owner, the SQL the app's
/// own (<see cref="OracleProfile"/>), in the read-only session and its rolled-back transaction.
/// </summary>
public sealed class OracleProfileTool : OracleTool
{
    public const string ToolName = "oracle_profile";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        $$"""
        {
          "type": "object",
          "properties": {
            "table": { "type": "string", "description": "The table or view's name (EMPLOYEES), or SCHEMA.NAME with one of the connection's schemas. Unquoted names are upper-cased, as Oracle does." },
            {{ConnectionProperty}},
            {{ProfileArguments.ColumnsProperty}},
            {{ProfileArguments.TopProperty}}
          },
          "required": ["table"]
        }
        """);

    public OracleProfileTool(OracleAccess oracle, Func<AppSettingsData> effective) : base(oracle, effective)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Profiles one Oracle table or view: its rows and, per column, the nulls, distinct values, min, max and most common values (a big table is " +
        "sampled, and the answer says so). Read-only; use it before reading pages of sample rows.";

    public override JsonElement JsonSchema => Schema;

    public async Task<string> ProfileAsync(string table, string? connection, IReadOnlyList<string>? columns, int top, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(table))
        {
            return OracleText.NoTable;
        }

        var match = await FindTableAsync(connection, table.Trim(), cancellationToken).ConfigureAwait(false);
        if (match.Error is { } refused)
        {
            return refused;
        }

        var effective = Effective;
        int timeout = TimeoutSeconds(effective);
        var catalog = await Oracle.RunAsync(connection, OracleProfile.Catalog, [new SqlParameterValue("owner", match.Owner), new SqlParameterValue("name", match.Name)],
            OracleAccess.MaxCatalogRows, timeout, cancellationToken).ConfigureAwait(false);
        if (catalog.Outcome != SqlOutcome.Ok)
        {
            return OracleText.Error(catalog);
        }

        // name, data type, char length; then num_rows
        var all = catalog.Grids.Count > 0 ? catalog.Grids[0].Rows.Select(r => new ProfileColumn(r[0], r[1], OracleProfile.Measure(r[1]))).ToList() : [];
        long? estimate = match.Kind == "table" && catalog.Grids.Count > 1 ? TableProfile.Whole(catalog.Grids[1].Rows.FirstOrDefault()?[0]) : null;
        var dialect = OracleProfile.Instance;
        var request = new TableProfile.Request(match.Owner + "." + match.Name, dialect.Quote(match.Owner) + "." + dialect.Quote(match.Name), match.Kind, all, columns, top,
            estimate, timeout, Math.Min(QueryPlan.MaxAnswerChars, SqlTool.ResultChars(effective)), OracleDescribeTool.ToolName, StringComparison.OrdinalIgnoreCase);
        return await TableProfile.RunAsync(request, dialect,
            (statements, seconds, token) => Oracle.RunAsync(connection, statements, [], OracleAccess.MaxCatalogRows, seconds, token),
            OracleText.Error, cancellationToken).ConfigureAwait(false);
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
