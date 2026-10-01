using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.MySql;
using NeonSidekick.Settings;
using NeonSidekick.Sql;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>mysql_query(sql, connection?, database?, params?, max_rows?)</c>: one read-only SELECT (a WITH clause may lead it), passed by
/// <see cref="MySqlReadOnlyGate"/> first, then run in a hardened session and a read-only transaction that is rolled back, with the
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

    private static readonly JsonElement Schema = ToolSchema.Parse(
        $$"""
        {
          "type": "object",
          "properties": {
            "sql": { "type": "string", "description": "One MySQL SELECT (LIMIT n, not TOP); a WITH clause may lead it. No INSERT, UPDATE, DELETE, DDL, CALL, INTO, FOR UPDATE, /*! */ comments or second statement." },
            {{ConnectionProperty}},
            "database": { "type": "string", "description": "The database unqualified names resolve in; leave it out for the connection's own." },
            "params": { "type": "object", "description": "Values for @name placeholders in the SQL, e.g. {\"id\": 101} for @id; strings, numbers, true, false or null." },
            "max_rows": { "type": "integer", "description": "How many rows at most, 1 to 1000. Leave it out for the user's default." }
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
        "The header says when more rows exist than were returned.";

    public override JsonElement JsonSchema => Schema;

    /// <summary>The row cap a call without <c>max_rows</c> gets: the setting, clamped.</summary>
    public static int DefaultRows(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return Math.Clamp(effective.MySqlQueryMaxRows, MinRows, MaxRows);
    }

    public async Task<string> RunAsync(string sql, string? connection, string? database, IReadOnlyList<SqlParameterValue> parameters, int? maxRows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(parameters);
        int rows = maxRows ?? DefaultRows(Effective);
        if (rows < MinRows || rows > MaxRows)
        {
            return SqlText.BadMaxRows(MinRows, MaxRows);
        }

        if (MySqlReadOnlyGate.Check(sql) is { } refused)
        {
            return refused;
        }

        var run = await Server.RunAsync(connection, Effective.MySqlDefaultConnection, database, [MySqlReadOnlyGate.Body(sql)], parameters, rows, TimeoutSeconds(Effective), cancellationToken).ConfigureAwait(false);
        return run.Outcome == SqlOutcome.Ok ? MySqlText.Query(run, rows, Files.WorkingDirectory.MaxReadChars) : MySqlText.Error(run);
    }

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
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

        string sql = ToolArguments.ReadString(arguments, SqlArgument);
        return await RunAsync(sql, Optional(arguments, ConnectionArgument), Optional(arguments, DatabaseArgument), parameters, max, cancellationToken).ConfigureAwait(false);
    }
}
