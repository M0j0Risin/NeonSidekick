using Microsoft.Extensions.AI;
using NeonSidekick.Settings;
using NeonSidekick.Sql;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// What the eight SQL tools share (2026-09-23): the <see cref="SqlAccess"/> door, the settings in force at each
/// call, the optional <c>connection</c> (a name from <c>sql.json</c>; the setting <c>SQL default connection</c>,
/// else the first, when left out) and <c>database</c> (another database on the same server) most of them take,
/// the batch timeout, and the table lookup <c>sql_describe</c>, <c>sql_relationships</c> and <c>sql_indexes</c> need. A refused
/// run is <see cref="SqlText.Error"/>'s sentence, every one starting <c>Error:</c>.
/// </summary>
public abstract class SqlTool : AIFunction
{
    public const string ConnectionArgument = "connection";
    public const string DatabaseArgument = "database";
    public const string TableArgument = "table";

    /// <summary>The <c>connection</c> property the server-reaching schemas carry.</summary>
    public const string ConnectionProperty = "\"connection\": { \"type\": \"string\", \"description\": \"The SQL connection's name (sql_connections lists them); leave it out for the default one.\" }";

    /// <summary>The <c>database</c> property the schemas below the server carry.</summary>
    public const string DatabaseProperty = "\"database\": { \"type\": \"string\", \"description\": \"Another database on the same server (sql_databases lists them); leave it out for the connection's own.\" }";

    private readonly SqlAccess _sql;
    private readonly Func<AppSettingsData> _effective;

    protected SqlTool(SqlAccess sql, Func<AppSettingsData> effective)
    {
        _sql = sql ?? throw new ArgumentNullException(nameof(sql));
        _effective = effective ?? throw new ArgumentNullException(nameof(effective));
    }

    protected SqlAccess Sql => _sql;

    protected AppSettingsData Effective => _effective();

    /// <summary>
    /// The characters of table a query tool's answer may carry (2026-10-01, the user's ask): <c>Query result max chars</c>
    /// (<see cref="AppSettingsData.QueryResultMaxChars"/>), clamped; one setting for <c>sql_query</c>, <c>oracle_query</c> and
    /// <c>mysql_query</c>. The catalog tools keep <see cref="Files.WorkingDirectory.MaxReadChars"/>.
    /// </summary>
    public static int ResultChars(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return Math.Clamp(effective.QueryResultMaxChars, AppSettingsData.MinQueryResultMaxChars, AppSettingsData.MaxQueryResultMaxChars);
    }

    /// <summary>The batch timeout: the setting, clamped to the range a hand-edited value may have left.</summary>
    public static int TimeoutSeconds(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return Math.Clamp(effective.SqlQueryTimeoutSeconds, AppSettingsData.MinSqlQueryTimeoutSeconds, AppSettingsData.MaxSqlQueryTimeoutSeconds);
    }

    /// <summary>A string argument, or null when blank.</summary>
    protected static string? Optional(AIFunctionArguments arguments, string name) =>
        ToolArguments.ReadString(arguments, name).Trim() is { Length: > 0 } text ? text : null;

    /// <summary>Runs one of the app's catalog batches on the call's connection and database, capped at <see cref="SqlAccess.MaxCatalogRows"/>.</summary>
    protected Task<SqlRun> CatalogAsync(string? connection, string? database, string sql, IReadOnlyList<SqlParameterValue> parameters, CancellationToken cancellationToken) =>
        _sql.RunAsync(connection, Effective.SqlDefaultConnection, database, sql, parameters, SqlAccess.MaxCatalogRows, TimeoutSeconds(Effective), cancellationToken);

    /// <summary>What <see cref="FindTableAsync"/> found: the object's id, schema, name and kind — or the <c>Error:</c> sentence.</summary>
    protected sealed record TableMatch(int Id, string Schema, string Name, string Kind, string? Error)
    {
        public static TableMatch Refused(string error) => new(0, "", "", "", error);
    }

    /// <summary>
    /// The table or view <paramref name="table"/> means (<see cref="SqlCatalogQueries.Resolve"/>): the one
    /// <c>OBJECT_ID</c> resolves, else the only one of that name in any schema; none is not found, several is
    /// ambiguous with the candidates named.
    /// </summary>
    protected async Task<TableMatch> FindTableAsync(string? connection, string? database, string table, CancellationToken cancellationToken)
    {
        var run = await CatalogAsync(connection, database, SqlCatalogQueries.Resolve, [new SqlParameterValue("name", table)], cancellationToken).ConfigureAwait(false);
        if (run.Outcome != SqlOutcome.Ok)
        {
            return TableMatch.Refused(SqlText.Error(run));
        }

        // id, schema, name, type, exact
        var rows = run.Grids.Count > 0 ? run.Grids[0].Rows : [];
        var exact = rows.Where(r => r[4] == "1").ToList();
        var pick = exact.Count == 1 ? exact : rows.ToList();
        if (pick.Count == 0)
        {
            return TableMatch.Refused(SqlText.TableNotFound(table, run.Connection, run.Database));
        }

        if (pick.Count > 1)
        {
            return TableMatch.Refused(SqlText.TableAmbiguous(table, pick.Select(r => r[1] + "." + r[2])));
        }

        var row = pick[0];
        return new TableMatch(int.Parse(row[0], System.Globalization.CultureInfo.InvariantCulture), row[1], row[2], row[3], null);
    }
}
