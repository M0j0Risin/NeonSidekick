using Microsoft.Extensions.AI;
using NeonSidekick.MySql;
using NeonSidekick.Settings;
using NeonSidekick.Sql;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// What the eight MySQL tools share (2026-09-30), <see cref="OracleTool"/>'s shape: the <see cref="MySqlAccess"/> door, the
/// settings in force at each call, the optional <c>connection</c> (a name from <c>mysql.json</c>; the setting
/// <c>MySQL default connection</c>, else the first, when left out) and <c>database</c> (a MySQL database is its schema, so it is
/// the SQL tools' word, not Oracle's), the statement timeout, and the table lookup <c>mysql_describe</c>,
/// <c>mysql_relationships</c> and <c>mysql_indexes</c> need. A refused run is <see cref="MySqlText.Error"/>'s sentence.
/// </summary>
public abstract class MySqlTool : AIFunction
{
    public const string ConnectionArgument = "connection";
    public const string DatabaseArgument = "database";
    public const string TableArgument = "table";

    /// <summary>The <c>connection</c> property the server-reaching schemas carry.</summary>
    public const string ConnectionProperty = "\"connection\": { \"type\": \"string\", \"description\": \"The MySQL connection's name (mysql_connections lists them); leave it out for the default one.\" }";

    private readonly MySqlAccess _mysql;
    private readonly Func<AppSettingsData> _effective;

    protected MySqlTool(MySqlAccess mysql, Func<AppSettingsData> effective)
    {
        _mysql = mysql ?? throw new ArgumentNullException(nameof(mysql));
        _effective = effective ?? throw new ArgumentNullException(nameof(effective));
    }

    protected MySqlAccess Server => _mysql;

    protected AppSettingsData Effective => _effective();

    /// <summary>The statement timeout: the setting, clamped to the range a hand-edited value may have left.</summary>
    public static int TimeoutSeconds(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return Math.Clamp(effective.MySqlQueryTimeoutSeconds, AppSettingsData.MinSqlQueryTimeoutSeconds, AppSettingsData.MaxSqlQueryTimeoutSeconds);
    }

    /// <summary>A string argument, or null when blank.</summary>
    protected static string? Optional(AIFunctionArguments arguments, string name) =>
        ToolArguments.ReadString(arguments, name).Trim() is { Length: > 0 } text ? text : null;

    /// <summary>Runs the app's own statements on the call's connection, capped at <see cref="MySqlAccess.MaxCatalogRows"/>; <paramref name="database"/> is bound as <c>@database</c> (the listing's scope), not opened.</summary>
    protected Task<SqlRun> CatalogAsync(string? connection, IReadOnlyList<string> statements, IReadOnlyList<SqlParameterValue> parameters, CancellationToken cancellationToken) =>
        _mysql.RunAsync(connection, Effective.MySqlDefaultConnection, null, statements, parameters, MySqlAccess.MaxCatalogRows, TimeoutSeconds(Effective), cancellationToken);

    /// <summary>What <see cref="FindTableAsync"/> found: the object's database, name and kind — or the <c>Error:</c> sentence.</summary>
    protected sealed record TableMatch(string Database, string Name, string Kind, string? Error)
    {
        public static TableMatch Refused(string error) => new("", "", "", error);
    }

    /// <summary>
    /// The table or view <paramref name="table"/> means (<see cref="MySqlCatalogQueries.Resolve"/>, case-insensitive):
    /// <c>database.name</c> as written, else the one in <paramref name="database"/> or the connection's database, else the only one
    /// of that name in any database; none is not found, several is ambiguous with the candidates named.
    /// </summary>
    protected async Task<TableMatch> FindTableAsync(string? connection, string? database, string table, CancellationToken cancellationToken)
    {
        if (MySqlAccess.Split(table) is not { } parts)
        {
            return TableMatch.Refused(MySqlText.BadTable(table));
        }

        var run = await CatalogAsync(connection, [MySqlCatalogQueries.Resolve], [new SqlParameterValue("db", parts.Database ?? database), new SqlParameterValue("name", parts.Name)], cancellationToken).ConfigureAwait(false);
        if (run.Outcome != SqlOutcome.Ok)
        {
            return TableMatch.Refused(MySqlText.Error(run));
        }

        // database, name, type, exact
        var rows = run.Grids.Count > 0 ? run.Grids[0].Rows : [];
        var exact = rows.Where(r => r[3] == "1").ToList();
        var pick = exact.Count == 1 ? exact : rows.ToList();
        if (pick.Count == 0)
        {
            return TableMatch.Refused(MySqlText.TableNotFound(table, MySqlText.Where(run)));
        }

        if (pick.Count > 1)
        {
            return TableMatch.Refused(MySqlText.TableAmbiguous(table, pick.Select(r => r[0] + "." + r[1])));
        }

        var row = pick[0];
        return new TableMatch(row[0], row[1], row[2], null);
    }
}
