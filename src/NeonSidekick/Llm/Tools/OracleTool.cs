using Microsoft.Extensions.AI;
using NeonSidekick.Oracle;
using NeonSidekick.Settings;
using NeonSidekick.Sql;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// What the eight Oracle tools share (2026-09-30), <see cref="SqlTool"/>'s shape: the <see cref="OracleAccess"/> door, the
/// settings in force at each call, the optional <c>connection</c> (a name from <c>oracle.json</c>; the setting
/// <c>Oracle default connection</c>, else the first, when left out) and <c>schema</c> (where Oracle has one database per
/// connection and many schemas in it, <c>schema</c> takes the place of the SQL tools' <c>database</c>), the statement timeout,
/// and the table lookup <c>oracle_describe</c>, <c>oracle_relationships</c> and <c>oracle_indexes</c> need. A refused run is
/// <see cref="OracleText.Error"/>'s sentence, every one starting <c>Error:</c>.
/// </summary>
public abstract class OracleTool : AIFunction
{
    public const string ConnectionArgument = "connection";
    public const string SchemaArgument = "schema";
    public const string TableArgument = "table";

    /// <summary>The <c>connection</c> property the server-reaching schemas carry.</summary>
    public const string ConnectionProperty = "\"connection\": { \"type\": \"string\", \"description\": \"The Oracle connection's name (oracle_connections lists them); leave it out for the default one.\" }";

    private readonly OracleAccess _oracle;
    private readonly Func<AppSettingsData> _effective;

    protected OracleTool(OracleAccess oracle, Func<AppSettingsData> effective)
    {
        _oracle = oracle ?? throw new ArgumentNullException(nameof(oracle));
        _effective = effective ?? throw new ArgumentNullException(nameof(effective));
    }

    protected OracleAccess Oracle => _oracle;

    protected AppSettingsData Effective => _effective();

    /// <summary>The statement timeout: the setting, clamped to the range a hand-edited value may have left.</summary>
    public static int TimeoutSeconds(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return Math.Clamp(effective.OracleQueryTimeoutSeconds, AppSettingsData.MinSqlQueryTimeoutSeconds, AppSettingsData.MaxSqlQueryTimeoutSeconds);
    }

    /// <summary>A string argument, or null when blank.</summary>
    protected static string? Optional(AIFunctionArguments arguments, string name) =>
        ToolArguments.ReadString(arguments, name).Trim() is { Length: > 0 } text ? text : null;

    /// <summary>
    /// A <c>schema</c> argument as the dictionary spells it (<see cref="OracleIdentifier.Normalize"/>): null when left out;
    /// false with the <c>Error:</c> sentence when it is no Oracle name.
    /// </summary>
    protected static bool TryReadSchema(string? raw, out string? schema, out string? error)
    {
        error = null;
        schema = null;
        if (raw is null)
        {
            return true;
        }

        schema = OracleIdentifier.Normalize(raw);
        if (schema is null)
        {
            error = OracleText.BadSchema(raw);
            return false;
        }

        return true;
    }

    /// <summary>Runs the app's own statements on the call's connection and schema, capped at <see cref="OracleAccess.MaxCatalogRows"/>.</summary>
    protected Task<SqlRun> CatalogAsync(string? connection, string? schema, IReadOnlyList<string> statements, IReadOnlyList<SqlParameterValue> parameters, CancellationToken cancellationToken) =>
        _oracle.RunAsync(connection, Effective.OracleDefaultConnection, schema, statements, parameters, OracleAccess.MaxCatalogRows, TimeoutSeconds(Effective), cancellationToken);

    /// <summary>What <see cref="FindTableAsync"/> found: the object's owner, name and kind — or the <c>Error:</c> sentence.</summary>
    protected sealed record TableMatch(string Owner, string Name, string Kind, string? Error)
    {
        public static TableMatch Refused(string error) => new("", "", "", error);
    }

    /// <summary>
    /// The table or view <paramref name="table"/> means (<see cref="OracleCatalogQueries.Resolve"/>): <c>OWNER.NAME</c> as
    /// written, else the one in the call's schema, else the only one of that name in any schema; none is not found, several
    /// is ambiguous with the candidates named.
    /// </summary>
    protected async Task<TableMatch> FindTableAsync(string? connection, string? schema, string table, CancellationToken cancellationToken)
    {
        if (OracleIdentifier.Split(table) is not { } parts)
        {
            return TableMatch.Refused(OracleText.BadTable(table));
        }

        var run = await CatalogAsync(connection, schema, [OracleCatalogQueries.Resolve], [new SqlParameterValue("owner", parts.Owner), new SqlParameterValue("name", parts.Name)], cancellationToken).ConfigureAwait(false);
        if (run.Outcome != SqlOutcome.Ok)
        {
            return TableMatch.Refused(OracleText.Error(run));
        }

        // schema, name, type, exact
        var rows = run.Grids.Count > 0 ? run.Grids[0].Rows : [];
        var exact = rows.Where(r => r[3] == "1").ToList();
        var pick = exact.Count == 1 ? exact : rows.ToList();
        if (pick.Count == 0)
        {
            return TableMatch.Refused(OracleText.TableNotFound(table, run.Connection, run.Database));
        }

        if (pick.Count > 1)
        {
            return TableMatch.Refused(OracleText.TableAmbiguous(table, pick.Select(r => r[0] + "." + r[1])));
        }

        var row = pick[0];
        return new TableMatch(row[0], row[1], row[2], null);
    }
}
