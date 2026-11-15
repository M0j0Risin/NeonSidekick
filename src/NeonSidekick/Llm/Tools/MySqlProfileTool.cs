using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.MySql;
using NeonSidekick.Settings;
using NeonSidekick.Sql;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>mysql_profile(table, connection?, columns?, top?)</c> (2026-11-14, the database-tools plan's #14), <see cref="SqlProfileTool"/>'s MySQL and
/// MariaDB twin: the table found as <c>mysql_describe</c> finds it (the connection's one database), the SQL the app's own
/// (<see cref="MySqlProfile"/>), in the read-only session; a big table's first rows, since neither server takes a page sample.
/// </summary>
public sealed class MySqlProfileTool : MySqlTool
{
    public const string ToolName = "mysql_profile";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        $$"""
        {
          "type": "object",
          "properties": {
            "table": { "type": "string", "description": "The table or view's name in the connection's database." },
            {{ConnectionProperty}},
            {{ProfileArguments.ColumnsProperty}},
            {{ProfileArguments.TopProperty}}
          },
          "required": ["table"]
        }
        """);

    public MySqlProfileTool(MySqlAccess server, Func<AppSettingsData> effective) : base(server, effective)
    {
    }

    /// <summary>The rows a profile reads at most: a table estimated bigger is sampled. Settable so the live facts make the server sample.</summary>
    internal long SampleAbove { get; set; } = TableProfile.DefaultSampleAbove;

    public override string Name => ToolName;

    public override string Description =>
        "Profiles one MySQL or MariaDB table or view: its rows and, per column, the nulls, distinct values, min, max and most common values (a big " +
        "table's first rows, and the answer says so). Read-only; use it before reading pages of sample rows.";

    public override JsonElement JsonSchema => Schema;

    public async Task<string> ProfileAsync(string table, string? connection, IReadOnlyList<string>? columns, int top, CancellationToken cancellationToken)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        if (string.IsNullOrWhiteSpace(table))
        {
            return MySqlText.NoTable;
        }

        var effective = Effective;
        int timeout = TimeoutSeconds(effective);
        var match = await FindTableAsync(connection, table.Trim(), cancellationToken).ConfigureAwait(false);
        if (match.Error is { } refused)
        {
            return refused;
        }

        if (SqlDeadline.Left(watch, timeout) is not (> 0 and var left))
        {
            return TableProfileText.Stopped(match.Name, "", timeout);
        }

        var catalog = await Server.RunAsync(connection, MySqlProfile.Catalog, [new SqlParameterValue("db", match.Database), new SqlParameterValue("name", match.Name)],
            MySqlAccess.MaxCatalogRows, left, cancellationToken, readCell: TableProfile.CatalogCell).ConfigureAwait(false);
        if (catalog.Outcome != SqlOutcome.Ok)
        {
            return catalog.Outcome == SqlOutcome.Timeout ? TableProfileText.Stopped(match.Name, catalog.Connection, timeout) : MySqlText.Error(catalog);
        }

        // name, column type, data type, character length; then TABLE_ROWS
        var listed = catalog.Grids.Count > 0 ? catalog.Grids[0] : null;
        var all = listed?.Rows.Select(r => new ProfileColumn(r[0], r[1], MySqlProfile.Measure(r[2], TableProfile.Whole(r[3])))).ToList() ?? [];
        long? estimate = catalog.Grids.Count > 1 ? TableProfile.Whole(catalog.Grids[1].Rows.FirstOrDefault()?[0]) : null;
        var dialect = MySqlProfile.Instance;
        var request = new TableProfile.Request(match.Name, dialect.Quote(match.Database) + "." + dialect.Quote(match.Name), match.Kind, all, columns, top,
            estimate, timeout, SqlTool.AnswerChars(effective), MySqlDescribeTool.ToolName, watch)
        {
            SampleAbove = SampleAbove,
            ColumnsCut = listed?.More == true,
        };
        return await TableProfile.RunAsync(request, dialect,
            (statements, seconds, token) => Server.RunAsync(connection, statements, [], MySqlAccess.MaxCatalogRows, seconds, token),
            MySqlText.Error, cancellationToken).ConfigureAwait(false);
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
