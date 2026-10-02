using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Settings;
using NeonSidekick.Sql;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>sql_query(sql, connection?, database?, params?, max_rows?)</c>: one read-only SELECT (a CTE may lead it),
/// passed by <see cref="SqlReadOnlyGate"/> first, then run in a transaction that is rolled back with the
/// setting's timeout; the rows as a Markdown table under a header that says when the cap cut it.
/// <c>params</c> is one object, each name bound as <c>@name</c> — the values never spliced into the text.
/// </summary>
public sealed class SqlQueryTool : SqlTool
{
    public const string ToolName = "sql_query";
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
            "sql": { "type": "string", "description": "One T-SQL SELECT (TOP, not LIMIT); a WITH … CTE may lead it. No INSERT, UPDATE, DELETE, EXEC, DDL or second statement." },
            {{ConnectionProperty}},
            {{DatabaseProperty}},
            "params": { "type": "object", "description": "Values for @name placeholders in the SQL, e.g. {\"id\": 43659} for @id; strings, numbers, true, false or null." },
            "max_rows": { "type": "integer", "description": "How many rows at most, 1 to 100000. Leave it out for the user's default." }
          },
          "required": ["sql"]
        }
        """);

    public SqlQueryTool(SqlAccess sql, Func<AppSettingsData> effective) : base(sql, effective)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Runs one read-only T-SQL SELECT on a SQL Server connection and returns the rows as a table. " +
        "Only a single SELECT (or WITH … SELECT) is accepted; bind values as @name through params rather than writing them into the text. " +
        "The header says when more rows exist than were returned.";

    public override JsonElement JsonSchema => Schema;

    /// <summary>The row cap a call without <c>max_rows</c> gets: the setting, clamped.</summary>
    public static int DefaultRows(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return Math.Clamp(effective.SqlQueryMaxRows, MinRows, MaxRows);
    }

    /// <summary>
    /// The <c>params</c> object as bound values, or the <c>Error:</c> sentence: a name (a leading <c>@</c> dropped)
    /// must be letters, digits and <c>_</c>; a value a string, a number (whole = <see cref="long"/>, else
    /// <see cref="decimal"/>, else <see cref="double"/>), a boolean or null.
    /// </summary>
    public static IReadOnlyList<SqlParameterValue>? ReadParameters(JsonElement values, out string? error)
    {
        error = null;
        var list = new List<SqlParameterValue>();
        foreach (var property in values.EnumerateObject())
        {
            string name = property.Name.TrimStart('@');
            if (name.Length == 0 || !(char.IsAsciiLetter(name[0]) || name[0] == '_') || !name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            {
                error = SqlText.BadParamName(property.Name);
                return null;
            }

            object? value;
            switch (property.Value.ValueKind)
            {
                case JsonValueKind.String:
                    value = property.Value.GetString();
                    break;
                case JsonValueKind.Number when property.Value.TryGetInt64(out long whole):
                    value = whole;
                    break;
                case JsonValueKind.Number when property.Value.TryGetDecimal(out decimal exact):
                    value = exact;
                    break;
                case JsonValueKind.Number:
                    value = property.Value.GetDouble();
                    break;
                case JsonValueKind.True:
                    value = true;
                    break;
                case JsonValueKind.False:
                    value = false;
                    break;
                case JsonValueKind.Null:
                    value = null;
                    break;
                default:
                    error = SqlText.BadParamValue(name);
                    return null;
            }

            list.Add(new SqlParameterValue(name, value));
        }

        return list;
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

        if (SqlReadOnlyGate.Check(sql) is { } refused)
        {
            return refused;
        }

        var run = await Sql.RunAsync(connection, Effective.SqlDefaultConnection, database, sql, parameters, rows, TimeoutSeconds(Effective), cancellationToken).ConfigureAwait(false);
        return run.Outcome == SqlOutcome.Ok ? SqlText.Query(run, rows, ResultChars(Effective)) : SqlText.Error(run);
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
            return SqlText.BadParams(sent);
        }

        IReadOnlyList<SqlParameterValue> parameters = [];
        if (objects.Count == 1)
        {
            if (ReadParameters(objects[0], out var error) is not { } read)
            {
                return error;
            }

            parameters = read;
        }

        string sql = ToolArguments.ReadString(arguments, SqlArgument);
        return await RunAsync(sql, Optional(arguments, ConnectionArgument), Optional(arguments, DatabaseArgument), parameters, max, cancellationToken).ConfigureAwait(false);
    }
}
