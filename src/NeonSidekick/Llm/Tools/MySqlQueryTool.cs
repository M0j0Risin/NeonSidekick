using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.MySql;
using NeonSidekick.Settings;
using NeonSidekick.Sql;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>mysql_query(sql, connection?, params?, max_rows?)</c>: one read-only SELECT (a WITH clause may lead it), passed by
/// <see cref="MySqlReadOnlyGate"/> first and held to the connection's database (<see cref="MySqlTool.OutsideAsync"/>, 2026-10-09), then run in a hardened session and a read-only transaction that is rolled back, with the
/// setting's timeout; the rows as a Markdown table under a header that says when the cap cut it. <c>params</c> is one object, each
/// name bound as <c>@name</c> — the values never spliced into the text.
/// </summary>
public sealed class MySqlQueryTool : MySqlTool
{
    public const string ToolName = "mysql_query";
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
            "sql": { "type": "string", "description": "One MySQL SELECT (LIMIT n, not TOP); a WITH clause may lead it. No INSERT, UPDATE, DELETE, DDL, CALL, INTO, FOR UPDATE, /*! */ comments or second statement." },
            {{ConnectionProperty}},
            "params": { "type": "object", "description": "Values for @name placeholders in the SQL, e.g. {\"id\": 101} for @id; strings, numbers, true, false or null." },
            "max_rows": { "type": "integer", "description": "How many rows at most, 1 to 100000 (1 to 1048575 with to). Leave it out for the user's default (with to, every row)." },
            {{ExportProperties}}
          },
          "required": ["sql"]
        }
        """);

    public MySqlQueryTool(MySqlAccess mysql, Func<AppSettingsData> effective) : base(mysql, effective)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Runs one read-only SELECT on a MySQL or MariaDB connection and returns the rows as a table. " +
        "Only a single SELECT (or WITH … SELECT) is accepted; bind values as @name through params rather than writing them into the text. " +
        "The header says when more rows exist than were returned." + SaveSentence;

    public override JsonElement JsonSchema => QuerySchema(Schemas);

    /// <summary>The row cap a call without <c>max_rows</c> gets: the setting, clamped.</summary>
    public static int DefaultRows(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return Math.Clamp(effective.MySqlQueryMaxRows, MinRows, MaxRows);
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

        if (MySqlReadOnlyGate.Check(sql) is { } refused)
        {
            return refused;
        }

        string body = MySqlReadOnlyGate.Body(sql);
        if (await OutsideAsync(body, connection, cancellationToken).ConfigureAwait(false) is { } outside)
        {
            return outside;
        }

        if (export is not null)
        {
            if (Server.Resolve(connection, out var unresolved) is not { } target)
            {
                return MySqlText.Error(unresolved!);
            }

            return await ExportAsync(export, target.Name, rows, maxRows, TimeoutSeconds(Effective), MySqlAccess.ExportCell,
                (job, timeout, token) => Server.ExportAsync(connection, body, parameters, job, rows, timeout, token), MySqlText.Error, cancellationToken).ConfigureAwait(false);
        }

        var run = await Server.RunAsync(connection, [body], parameters, rows, TimeoutSeconds(Effective), cancellationToken).ConfigureAwait(false);
        return run.Outcome == SqlOutcome.Ok ? MySqlText.Query(run, rows, SqlTool.ResultChars(Effective), CanSave) : MySqlText.Error(run);
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
            return MySqlText.BadParams(sent);
        }

        IReadOnlyList<SqlParameterValue> parameters = [];
        if (objects.Count == 1)
        {
            if (SqlQueryTool.ReadParameters(objects[0], out var error) is not { } read)
            {
                return error;
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
