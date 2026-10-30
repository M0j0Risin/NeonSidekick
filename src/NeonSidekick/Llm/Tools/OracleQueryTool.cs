using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Oracle;
using NeonSidekick.Settings;
using NeonSidekick.Sql;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>oracle_query(sql, connection?, params?, max_rows?)</c>: one read-only SELECT (a WITH clause may lead it), held to the connection's
/// schema (<see cref="OracleTool.OutsideAsync"/>, 2026-10-09),
/// passed by <see cref="OracleReadOnlyGate"/> first, then run in a read-only session and transaction that is rolled back,
/// with the setting's timeout; the rows as a Markdown table under a header that says when the cap cut it. <c>params</c> is
/// one object, each name bound as <c>:name</c> — the values never spliced into the text.
/// </summary>
public sealed class OracleQueryTool : OracleTool
{
    public const string ToolName = "oracle_query";
    public const string SqlArgument = "sql";
    public const string ParamsArgument = "params";
    public const string MaxRowsArgument = "max_rows";

    public const int MinRows = AppSettingsData.MinSqlQueryMaxRows;
    public const int MaxRows = AppSettingsData.MaxSqlQueryMaxRows;

    private static readonly (JsonElement Export, JsonElement Plain) Schemas = QuerySchemas(
        $$"""
        {
          "type": "object",
          "properties": {
            "sql": { "type": "string", "description": "One Oracle SELECT (FETCH FIRST n ROWS ONLY, not TOP or LIMIT; no trailing ;); a WITH clause may lead it. No INSERT, UPDATE, DELETE, MERGE, PL/SQL, DDL, FOR UPDATE, NEXTVAL, @dblink or second statement." },
            {{ConnectionProperty}},
            "params": { "type": "object", "description": "Values for :name placeholders in the SQL, e.g. {\"id\": 101} for :id; strings, numbers, true, false or null." },
            "max_rows": { "type": "integer", "description": "How many rows at most, 1 to 100000 (1 to 1048575 with to). Leave it out for the user's default (with to, every row)." },
            {{ExportProperties}}
          },
          "required": ["sql"]
        }
        """);

    public OracleQueryTool(OracleAccess oracle, Func<AppSettingsData> effective) : base(oracle, effective)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Runs one read-only Oracle SELECT on an Oracle connection and returns the rows as a table. " +
        "Only a single SELECT (or WITH … SELECT) is accepted; bind values as :name through params rather than writing them into the text. " +
        "The header says when more rows exist than were returned." + SaveSentence;

    public override JsonElement JsonSchema => QuerySchema(Schemas);

    /// <summary>The row cap a call without <c>max_rows</c> gets: the setting, clamped.</summary>
    public static int DefaultRows(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return Math.Clamp(effective.OracleQueryMaxRows, MinRows, MaxRows);
    }

    /// <remarks><paramref name="export"/> (2026-10-29, the Office writing plan): every row to a workbook instead (<see cref="ServerTool{TAccess}.ExportAsync"/>).</remarks>
    public async Task<string> RunAsync(string sql, string? connection, IReadOnlyList<SqlParameterValue> parameters, int? maxRows, CancellationToken cancellationToken, ExportTarget? export = null)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(parameters);
        if (RowCap(export, maxRows, DefaultRows(Effective), MinRows, MaxRows, out int rows) is { } badRows)
        {
            return badRows;
        }

        if (OracleReadOnlyGate.Check(sql) is { } refused)
        {
            return refused;
        }

        string body = OracleReadOnlyGate.Body(sql);
        if (await OutsideAsync(body, connection, cancellationToken).ConfigureAwait(false) is { } outside)
        {
            return outside;
        }

        if (export is not null)
        {
            if (Oracle.Resolve(connection, out var unresolved) is not { } target)
            {
                return OracleText.Error(unresolved!);
            }

            return await ExportAsync(export, target.Name, rows, maxRows, TimeoutSeconds(Effective), OracleAccess.ExportCell,
                (job, timeout, token) => Oracle.ExportAsync(connection, body, parameters, job, rows, timeout, token), OracleText.Error, cancellationToken).ConfigureAwait(false);
        }

        var run = await Oracle.RunAsync(connection, [body], parameters, rows, TimeoutSeconds(Effective), cancellationToken).ConfigureAwait(false);
        return run.Outcome == SqlOutcome.Ok ? OracleText.Query(run, rows, SqlTool.ResultChars(Effective), CanSave) : OracleText.Error(run);
    }

    protected override async ValueTask<object?> InvokeToolAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!ToolArguments.TryReadInt32(arguments, MaxRowsArgument, out var max, out var raw))
        {
            return ClockText.BadInteger(MaxRowsArgument, raw);
        }

        if (!ToolArguments.TryReadObjectList(arguments, ParamsArgument, out var objects, out var sent) || objects.Count > 1)
        {
            return OracleText.BadParams(sent);
        }

        IReadOnlyList<SqlParameterValue> parameters = [];
        if (objects.Count == 1)
        {
            if (SqlQueryTool.ReadParameters(objects[0], out var error, SqlQueryTool.ReadColonName, OracleText.BadParamName) is not { } read)
            {
                return error?.Replace("@name", ":name", StringComparison.Ordinal);
            }

            parameters = read;
        }

        if (ReadExport(arguments, out var export) is { } badExport)
        {
            return badExport;
        }

        string sql = ToolArguments.ReadString(arguments, SqlArgument);
        return await RunAsync(sql, Optional(arguments, ConnectionArgument), parameters, max, cancellationToken, export).ConfigureAwait(false);
    }
}
