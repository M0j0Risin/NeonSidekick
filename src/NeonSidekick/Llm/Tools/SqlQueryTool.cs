using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Settings;
using NeonSidekick.Sql;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>sql_query(sql, connection?, params?, max_rows?)</c>: one read-only SELECT (a CTE may lead it),
/// passed by <see cref="SqlReadOnlyGate"/> first and held to the connection's database (<see cref="SqlBoundary"/>, 2026-10-09), then run in a transaction that is rolled back with the
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

    private static readonly (JsonElement Export, JsonElement Plain) Schemas = QuerySchemas(
        $$"""
        {
          "type": "object",
          "properties": {
            "sql": { "type": "string", "description": "One T-SQL SELECT (TOP, not LIMIT); a WITH … CTE may lead it. No INSERT, UPDATE, DELETE, EXEC, DDL or second statement." },
            {{ConnectionProperty}},
            "params": { "type": "object", "description": "Values for @name placeholders in the SQL, e.g. {\"id\": 43659} for @id; strings, numbers, true, false or null." },
            "max_rows": { "type": "integer", "description": "How many rows at most, 1 to 100000 (1 to 1048575 with to). Leave it out for the user's default (with to, every row)." },
            {{ExportProperties}}
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
        "The header says when more rows exist than were returned." + SaveSentence;

    public override JsonElement JsonSchema => QuerySchema(Schemas);

    /// <summary>The row cap a call without <c>max_rows</c> gets: the setting, clamped.</summary>
    public static int DefaultRows(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return Math.Clamp(effective.SqlQueryMaxRows, MinRows, MaxRows);
    }

    /// <summary>
    /// The <c>params</c> object as bound values, or the <c>Error:</c> sentence: a name (a leading <c>@</c> dropped)
    /// must be letters, digits and <c>_</c>; a value a string, a number (whole = <see cref="long"/>, else
    /// <see cref="decimal"/>, else <see cref="double"/>), a boolean or null. A family whose placeholders are written otherwise
    /// passes its own <paramref name="readName"/> (the bound name, or null for none) and <paramref name="badName"/> sentence:
    /// SQLite's <c>:id</c>, <c>#id</c>, <c>a$b</c> and <c>$a(1)</c> (<see cref="Sqlite.SqliteReadOnlyGate.ParamName"/>, the fourth
    /// 2026-10-04 review: the ASCII rule refused <c>{":id": 5}</c> and left such placeholders bound NULL).
    /// </summary>
    public static IReadOnlyList<SqlParameterValue>? ReadParameters(JsonElement values, out string? error, Func<string, string?>? readName = null, Func<string, string>? badName = null)
    {
        error = null;
        var list = new List<SqlParameterValue>();
        foreach (var property in values.EnumerateObject())
        {
            if ((readName ?? ReadName)(property.Name) is not { } name)
            {
                error = (badName ?? SqlText.BadParamName)(property.Name);
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
                case JsonValueKind.Number when property.Value.TryGetDouble(out double real) && double.IsFinite(real):
                    value = real;
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

    /// <summary>
    /// A params name as Oracle binds it (2026-10-24, the database review's P2: <c>{":id": 5}</c> was refused, and the refusal itself said to
    /// write <c>:name</c>): a leading <c>:</c> dropped as <c>@</c> is, then <see cref="ReadName"/>'s rule.
    /// </summary>
    public static string? ReadColonName(string written) => ReadName(written.StartsWith(':') ? written[1..] : written);

    /// <summary>A params name as bound: letters, digits and <c>_</c>, not leading with a digit, a leading <c>@</c> dropped; null when it is not one.</summary>
    private static string? ReadName(string written)
    {
        string name = written.TrimStart('@');
        return name.Length > 0 && (char.IsAsciiLetter(name[0]) || name[0] == '_') && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') ? name : null;
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

        if ((SqlReadOnlyGate.Check(sql) ?? await OutsideAsync(sql, connection, cancellationToken).ConfigureAwait(false)) is { } refused)
        {
            return refused;
        }

        if (export is not null)
        {
            if (Sql.Resolve(connection, out var unresolved) is not { } target)
            {
                return SqlText.Error(unresolved!);
            }

            return await ExportAsync(export, target.Name, rows, maxRows, TimeoutSeconds(Effective), SqlAccess.ExportCell,
                (job, timeout, token) => Sql.ExportAsync(connection, sql, parameters, job, rows, timeout, token), SqlText.Error, cancellationToken).ConfigureAwait(false);
        }

        var run = await Sql.RunAsync(connection, sql, parameters, rows, TimeoutSeconds(Effective), cancellationToken).ConfigureAwait(false);
        return run.Outcome == SqlOutcome.Ok ? SqlText.Query(run, rows, ResultChars(Effective), CanSave) : SqlText.Error(run);
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

        if (ReadExport(arguments, out var export) is { } badExport)
        {
            return badExport;
        }

        string sql = ToolArguments.ReadString(arguments, SqlArgument);
        return await RunAsync(sql, Optional(arguments, ConnectionArgument), parameters, max, cancellationToken, export).ConfigureAwait(false);
    }
}
