using Microsoft.Extensions.AI;
using NeonSidekick.Settings;
using NeonSidekick.Sql;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// What the SQL tools share (2026-09-23): the <see cref="SqlAccess"/> door, the settings in force at each
/// call, the optional <c>connection</c> (a name from <c>sql.json</c>; left out, the only one offered;
/// several and none named is refused since 2026-10-09, the setting <c>SQL default connection</c> gone) — each connection reaching its own database alone since 2026-10-09 (the user's call;
/// <c>database</c>, the per-call way into another, and <c>sql_databases</c> went, and a call that still sends one is refused,
/// <see cref="SqlText.NoDatabaseArgument"/>) — the batch timeout, and the table lookup <c>sql_describe</c>, <c>sql_relationships</c> and <c>sql_indexes</c> need. A refused
/// run is <see cref="SqlText.Error"/>'s sentence, every one starting <c>Error:</c>.
/// </summary>
public abstract class SqlTool : ServerTool<SqlAccess>
{
    /// <summary>The argument the tools took until 2026-10-09; a call that still sends it is refused.</summary>
    public const string DatabaseArgument = "database";

    /// <summary>The <c>connection</c> property the server-reaching schemas carry.</summary>
    public const string ConnectionProperty = "\"connection\": { \"type\": \"string\", \"description\": \"The SQL connection's name (sql_connections lists them); it may be left out only when there is one.\" }";

    protected SqlTool(SqlAccess sql, Func<AppSettingsData> effective) : base(sql, effective)
    {
    }

    protected SqlAccess Sql => Access;

    /// <summary>
    /// The characters of table a query tool's answer may carry (2026-10-01, the user's ask): <c>SQL query result max chars</c> (named so on 2026-10-03, the user's ask; <c>Query result max chars</c> before)
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

    /// <summary>Every SQL tool refuses a call that still names a <c>database</c> (<see cref="ServerTool{TAccess}.InvokeCoreAsync"/>).</summary>
    protected sealed override string RetiredArgument => DatabaseArgument;

    protected sealed override string RetiredRefusal => SqlText.NoDatabaseArgument;

    /// <summary>Runs one of the app's catalog batches on the call's connection (in its database), capped at <see cref="SqlAccess.MaxCatalogRows"/>.</summary>
    protected async Task<SqlRun> CatalogAsync(string? connection, string sql, IReadOnlyList<SqlParameterValue> parameters, CancellationToken cancellationToken) =>
        SqlText.CatalogRun(await Sql.RunAsync(connection, sql, parameters, SqlAccess.MaxCatalogRows, TimeoutSeconds(Effective), cancellationToken, keepLaterSets: true).ConfigureAwait(false));

    /// <summary>
    /// The model's own SQL held to the connection's database (<see cref="SqlBoundary"/>): null when it keeps to it, or when the
    /// connection does not resolve (the run that follows says why); else the <c>Error:</c> sentence. A call that may be another
    /// database's or an alias's column method sends for the server's database names (<see cref="SqlCatalogQueries.DatabaseNames"/>,
    /// kept a minute, <see cref="SqlAccess.Names"/>; the review, 2026-10-09); a failed read of them is its error, never a pass.
    /// </summary>
    protected async Task<string?> OutsideAsync(string sql, string? connection, CancellationToken cancellationToken, SqlAccess? via = null)
    {
        var access = via ?? Sql;
        if (access.Resolve(connection, out _) is not { } target)
        {
            return null;
        }

        string database = target.Config.Database ?? "";
        var (refused, ambiguous) = SqlBoundary.Inspect(sql, database);
        if (refused is not null || ambiguous.Count == 0)
        {
            return refused;
        }

        var (rows, failed) = await ReadNamesAsync(access.Names, BoundaryNames.Key(target.Name, target.Config.Server, database),
            token => access.RunAsync(connection, SqlCatalogQueries.DatabaseNames, [], 100_000, TimeoutSeconds(Effective), token), SqlText.Error, cancellationToken).ConfigureAwait(false);
        if (failed is not null)
        {
            return failed;
        }

        // A partial list (no VIEW ANY DATABASE) is made whole for this statement's heads: a database the login can open joins it, one it cannot
        // is out of reach anyway (2026-10-24, the review of these fixes).
        var names = rows!.Select(r => r[0]).ToList();
        if (SqlBoundary.UnsettledHeads(sql, database, names) is { Count: > 0 } unsettled)
        {
            foreach (string head in unsettled)
            {
                var probe = await access.RunAsync(connection, SqlCatalogQueries.CanOpen, [new SqlParameterValue("name", head)], 1, TimeoutSeconds(Effective), cancellationToken).ConfigureAwait(false);
                if (probe.Outcome != SqlOutcome.Ok)
                {
                    return SqlText.Error(probe);
                }

                if (probe.Grids.FirstOrDefault()?.Rows.FirstOrDefault()?[0] == "1")
                {
                    names.Add(head);
                }
            }

            names.Remove("");
        }

        return SqlBoundary.Check(sql, database, names);
    }

    /// <summary>What <see cref="FindTableAsync"/> found: the object's id, schema, name and kind — or the <c>Error:</c> sentence.</summary>
    protected sealed record TableMatch(int Id, string Schema, string Name, string Kind, string? Error)
    {
        public static TableMatch Refused(string error) => new(0, "", "", "", error);
    }

    /// <summary>
    /// The table or view <paramref name="table"/> means (<see cref="SqlCatalogQueries.Resolve"/>): the one
    /// <c>OBJECT_ID</c> resolves, else the only one of that name in any schema; none is not found, several is
    /// ambiguous with the candidates named. A three-part name in another database is refused before the lookup (2026-10-09:
    /// <c>OBJECT_ID</c> would resolve it there, and its id could match an unrelated object here).
    /// </summary>
    protected async Task<TableMatch> FindTableAsync(string? connection, string table, CancellationToken cancellationToken)
    {
        if (SqlBoundary.NamedDatabase(table) is { } named && Sql.Resolve(connection, out _) is { } target
            && (SqlBoundary.IsOther(named, target.Config.Database ?? "") || SqlBoundary.IsCaseTwin(named, target.Config.Database ?? "")))   // D4: OBJECT_ID would resolve a twin
        {
            return TableMatch.Refused(SqlText.OtherDatabase(named, target.Config.Database!.Trim()));
        }

        var run = await CatalogAsync(connection, SqlCatalogQueries.Resolve, [new SqlParameterValue("name", table)], cancellationToken).ConfigureAwait(false);
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
