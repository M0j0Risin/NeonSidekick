using Microsoft.Extensions.AI;
using NeonSidekick.MySql;
using NeonSidekick.Settings;
using NeonSidekick.Sql;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// What the MySQL tools share (2026-09-30), <see cref="OracleTool"/>'s shape: the <see cref="MySqlAccess"/> door, the
/// settings in force at each call, the optional <c>connection</c> (a name from <c>mysql.json</c>; the setting
/// <c>MySQL default connection</c>, else the first, when left out) — each connection reaching its own database alone since
/// 2026-10-09 (the user's call, SQL Server's lock carried over: <c>database</c> and <c>mysql_databases</c> went, a call that still
/// sends one is refused, and <see cref="OutsideAsync"/> holds the model's SQL to it) — the statement timeout, and the table lookup
/// <c>mysql_describe</c>, <c>mysql_relationships</c> and <c>mysql_indexes</c> need. A refused run is <see cref="MySqlText.Error"/>'s sentence.
/// </summary>
public abstract class MySqlTool : AIFunction
{
    public const string ConnectionArgument = "connection";
    /// <summary>The argument the tools took until 2026-10-09; a call that still sends it is refused.</summary>
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

    /// <summary>Every MySQL tool's call: refused when it still names a <c>database</c>, else the tool's own <see cref="InvokeToolAsync"/>.</summary>
    protected sealed override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return Optional(arguments, DatabaseArgument) is not null ? new(MySqlText.NoDatabaseArgument) : InvokeToolAsync(arguments, cancellationToken);
    }

    /// <summary>The tool's own call, its arguments read.</summary>
    protected abstract ValueTask<object?> InvokeToolAsync(AIFunctionArguments arguments, CancellationToken cancellationToken);

    /// <summary>
    /// The model's own SQL held to the connection's database (<see cref="MySqlBoundary"/>, 2026-10-09): a qualifier naming one of the
    /// server's own databases refused at once; any other but the connection's own sends for the names of the databases on the server
    /// (<see cref="MySqlBoundary.Names"/>), and one among them is refused. A statement with no such qualifier asks the server nothing.
    /// Null when it keeps to the database, or when the connection does not resolve (the run that follows says why); a failed read of
    /// the names is its error, never a pass.
    /// </summary>
    protected async Task<string?> OutsideAsync(string sql, string? connection, CancellationToken cancellationToken)
    {
        if (_mysql.Resolve(connection, Effective.MySqlDefaultConnection, out _) is not { } target)
        {
            return null;
        }

        var (heads, refused) = MySqlBoundary.Qualifiers(sql, target.Config.Database ?? "");
        if (refused is not null || heads.Count == 0)
        {
            return refused;
        }

        // The names kept a minute per connection (the review, 2026-10-09: an alias's t.col sent every join for them).
        var (rows, failed) = await _mysql.Names.GetAsync(BoundaryNames.Key(target.Name, target.Config.Endpoint, target.Config.Database, target.Config.User),
            token => _mysql.RunAsync(connection, Effective.MySqlDefaultConnection, [MySqlBoundary.Names], [], MySqlBoundary.MaxNames, TimeoutSeconds(Effective), token), cancellationToken).ConfigureAwait(false);
        return failed is not null ? MySqlText.Error(failed) : MySqlBoundary.Check(sql, target.Config.Database ?? "", rows!.Select(r => r[0]));
    }

    /// <summary>A string argument, or null when blank.</summary>
    protected static string? Optional(AIFunctionArguments arguments, string name) =>
        ToolArguments.ReadString(arguments, name).Trim() is { Length: > 0 } text ? text : null;

    /// <summary>Runs the app's own statements on the call's connection (in its database), capped at <see cref="MySqlAccess.MaxCatalogRows"/>.</summary>
    protected Task<SqlRun> CatalogAsync(string? connection, IReadOnlyList<string> statements, IReadOnlyList<SqlParameterValue> parameters, CancellationToken cancellationToken) =>
        _mysql.RunAsync(connection, Effective.MySqlDefaultConnection, statements, parameters, MySqlAccess.MaxCatalogRows, TimeoutSeconds(Effective), cancellationToken);

    /// <summary>What <see cref="FindTableAsync"/> found: the object's database, name and kind — or the <c>Error:</c> sentence.</summary>
    protected sealed record TableMatch(string Database, string Name, string Kind, string? Error)
    {
        public static TableMatch Refused(string error) => new("", "", "", error);
    }

    /// <summary>
    /// The table or view <paramref name="table"/> means in the connection's database (<see cref="MySqlCatalogQueries.Resolve"/>,
    /// case-insensitive): <c>name</c>, or <c>database.name</c> with the connection's own database — another is refused (2026-10-09);
    /// none is not found, several is ambiguous with the candidates named.
    /// </summary>
    protected async Task<TableMatch> FindTableAsync(string? connection, string table, CancellationToken cancellationToken)
    {
        if (MySqlAccess.Split(table) is not { } parts)
        {
            return TableMatch.Refused(MySqlText.BadTable(table));
        }

        if (parts.Database is { } named && _mysql.Resolve(connection, Effective.MySqlDefaultConnection, out _) is { } target
            && !named.Equals(target.Config.Database?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return TableMatch.Refused(MySqlText.TableElsewhere(named, target.Config.Database?.Trim() ?? ""));
        }

        var run = await CatalogAsync(connection, [MySqlCatalogQueries.Resolve], [new SqlParameterValue("name", parts.Name)], cancellationToken).ConfigureAwait(false);
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
