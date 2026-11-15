using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Settings;
using NeonSidekick.Sql;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>sql_profile(table, connection?, columns?, top?)</c> (2026-11-14, the database-tools plan's #14): one table or view's data at a glance —
/// its rows, and per column the nulls, distinct values, min, max and most common values — in one answer, so the model need not page sample
/// rows into its context. The table found as <c>sql_describe</c> finds it (the connection's one database), the SQL the app's own
/// (<see cref="SqlServerProfile"/>), run in the read transaction that is rolled back; a table past <see cref="SampleAbove"/> rows is sampled,
/// and the answer says so. The resolve, the catalog and both passes share the one deadline (2026-11-15, #22).
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

    /// <summary>The rows a profile reads at most: a table estimated bigger is sampled. Settable so the live facts make the server sample.</summary>
    internal long SampleAbove { get; set; } = TableProfile.DefaultSampleAbove;

    public override string Name => ToolName;

    public override string Description =>
        "Profiles one SQL table or view: its rows and, per column, the nulls, distinct values, min, max and most common values (a big table is " +
        "sampled, and the answer says so). Read-only; use it before reading pages of sample rows.";

    public override JsonElement JsonSchema => Schema;

    public async Task<string> ProfileAsync(string table, string? connection, IReadOnlyList<string>? columns, int top, CancellationToken cancellationToken)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        if (string.IsNullOrWhiteSpace(table))
        {
            return SqlText.NoTable;
        }

        var effective = Effective;
        int timeout = TimeoutSeconds(effective);
        var match = await FindTableAsync(connection, table.Trim(), cancellationToken).ConfigureAwait(false);
        if (match.Error is { } error)
        {
            return error;
        }

        string shown = match.Schema + "." + match.Name;
        if (SqlDeadline.Left(watch, timeout) is not (> 0 and var left))
        {
            return TableProfileText.Stopped(shown, "", timeout);
        }

        // Names read whole (#34): a scrubbed one would be quoted back into the SQL as a name the table doesn't have.
        var catalog = await Sql.RunAsync(connection, SqlServerProfile.Catalog, [new SqlParameterValue("id", match.Id)], SqlAccess.MaxCatalogRows, left, cancellationToken,
            keepLaterSets: true, readCell: TableProfile.CatalogCell).ConfigureAwait(false);
        if (catalog.Outcome != SqlOutcome.Ok)
        {
            return catalog.Outcome == SqlOutcome.Timeout ? TableProfileText.Stopped(shown, catalog.Connection, timeout) : SqlText.Error(catalog);
        }

        // name, declared type, system type, max_length, precision, scale; then rows, compatibility level, memory-optimized
        var listed = catalog.Grids.Count > 0 ? catalog.Grids[0] : null;
        var all = listed?.Rows.Select(r => new ProfileColumn(r[0], SqlText.TypeName(r[1], r[3], r[4], r[5]), SqlServerProfile.Measure(r[2], TableProfile.Whole(r[3])))).ToList() ?? [];
        var facts = catalog.Grids.Count > 1 ? catalog.Grids[1].Rows.FirstOrDefault() : null;
        var dialect = SqlServerProfile.For(TableProfile.Whole(facts?[1]));
        string kind = TableProfile.Whole(facts?[2]) is > 0 ? SqlServerProfile.MemoryOptimizedKind : match.Kind;
        var request = new TableProfile.Request(shown, dialect.Quote(match.Schema) + "." + dialect.Quote(match.Name), kind, all, columns, top,
            match.Kind == "table" ? TableProfile.Whole(facts?[0]) : null, timeout, AnswerChars(effective), SqlDescribeTool.ToolName, watch)
        {
            SampleAbove = SampleAbove,
            ColumnsCut = listed?.More == true,
        };
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
