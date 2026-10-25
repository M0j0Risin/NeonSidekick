using Microsoft.Extensions.AI;
using NeonSidekick.Oracle;
using NeonSidekick.Settings;
using NeonSidekick.Sql;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// What the Oracle tools share (2026-09-30), <see cref="SqlTool"/>'s shape: the <see cref="OracleAccess"/> door, the
/// settings in force at each call, the optional <c>connection</c> (a name from <c>oracle.json</c>; left out,
/// the only one offered, several refused since 2026-10-09) — each connection held to its one schema since 2026-10-09
/// (the user's pick: the entry's <c>schema</c>, else the account's own, <see cref="OracleAccess.Place"/>; the per-call
/// <c>schema</c> and <c>oracle_schemas</c> went, a call that still sends one is refused, and <see cref="OutsideAsync"/> holds the
/// model's SQL to it) — the statement timeout, and the table lookup <c>oracle_describe</c>, <c>oracle_relationships</c> and <c>oracle_indexes</c> need. A refused run is
/// <see cref="OracleText.Error"/>'s sentence, every one starting <c>Error:</c>.
/// </summary>
public abstract class OracleTool : ServerTool<OracleAccess>
{
    /// <summary>The argument the tools took until 2026-10-09; a call that still sends it is refused.</summary>
    public const string SchemaArgument = "schema";

    /// <summary>The <c>connection</c> property the server-reaching schemas carry.</summary>
    public const string ConnectionProperty = "\"connection\": { \"type\": \"string\", \"description\": \"The Oracle connection's name (oracle_connections lists them); it may be left out only when there is one.\" }";

    protected OracleTool(OracleAccess oracle, Func<AppSettingsData> effective) : base(oracle, effective)
    {
    }

    protected OracleAccess Oracle => Access;

    /// <summary>The statement timeout: the setting, clamped to the range a hand-edited value may have left.</summary>
    public static int TimeoutSeconds(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return Math.Clamp(effective.OracleQueryTimeoutSeconds, AppSettingsData.MinSqlQueryTimeoutSeconds, AppSettingsData.MaxSqlQueryTimeoutSeconds);
    }

    /// <summary>Every Oracle tool refuses a call that still names a <c>schema</c> (<see cref="ServerTool{TAccess}.InvokeCoreAsync"/>).</summary>
    protected sealed override string RetiredArgument => SchemaArgument;

    protected sealed override string RetiredRefusal => OracleText.NoSchemaArgument;

    /// <summary>
    /// The model's own SQL held to the connection's schema (<see cref="OracleBoundary"/>, 2026-10-09): a qualifier but the schema's own,
    /// or a word with a dictionary prefix, sends for the database's names (<see cref="OracleBoundary.Names"/>, kept a minute), and a user
    /// or a dictionary view among them is refused. A statement with neither asks the database nothing. Null when it keeps to the schema, or
    /// when the connection does not resolve (the run that follows says why); a failed read of the names is its error, never a pass.
    /// </summary>
    protected async Task<string?> OutsideAsync(string sql, string? connection, CancellationToken cancellationToken, OracleAccess? via = null)
    {
        var access = via ?? Oracle;
        if (access.Resolve(connection, out _) is not { } target)
        {
            return null;
        }

        string place = OracleAccess.Place(target.Config);
        if (OracleBoundary.Refusal(sql, place) is { } refused)
        {
            return refused;   // before any names are read (2026-10-24, the database review's Phase 9)
        }

        var (heads, dictionary) = OracleBoundary.Qualifiers(sql, place);
        if (heads.Count == 0 && dictionary.Count == 0)
        {
            return null;
        }

        // The names kept a minute per connection (the review, 2026-10-09: an alias's e.id sent every join for them).
        var (rows, failed) = await ReadNamesAsync(access.Names, BoundaryNames.Key(target.Name, target.Config.DataSource, target.Config.User, place),
            token => access.RunAsync(connection, [OracleBoundary.Names], [], OracleBoundary.MaxNames, TimeoutSeconds(Effective), token), OracleText.Error, cancellationToken).ConfigureAwait(false);
        if (failed is not null)
        {
            return failed;
        }

        var sets = access.Names.Built(rows!, OracleBoundary.Sets);
        return OracleBoundary.CheckQualifiers(heads, dictionary, place, sets.Users, sets.Views);
    }

    /// <summary>Runs the app's own statements on the call's connection (in its schema), capped at <see cref="OracleAccess.MaxCatalogRows"/>.</summary>
    protected async Task<SqlRun> CatalogAsync(string? connection, IReadOnlyList<string> statements, IReadOnlyList<SqlParameterValue> parameters, CancellationToken cancellationToken) =>
        SqlText.CatalogRun(await Oracle.RunAsync(connection, statements, parameters, OracleAccess.MaxCatalogRows, TimeoutSeconds(Effective), cancellationToken).ConfigureAwait(false));

    /// <summary>What <see cref="FindTableAsync"/> found: the object's owner, name and kind — or the <c>Error:</c> sentence.</summary>
    protected sealed record TableMatch(string Owner, string Name, string Kind, string? Error)
    {
        public static TableMatch Refused(string error) => new("", "", "", error);
    }

    /// <summary>
    /// The table or view <paramref name="table"/> means in the connection's schema (<see cref="OracleCatalogQueries.Resolve"/>):
    /// <c>NAME</c>, or <c>OWNER.NAME</c> with the schema's own owner — another is refused (2026-10-09); none is not found.
    /// </summary>
    protected async Task<TableMatch> FindTableAsync(string? connection, string table, CancellationToken cancellationToken)
    {
        if (OracleIdentifier.Split(table) is not { } parts)
        {
            return TableMatch.Refused(OracleText.BadTable(table));
        }

        if (parts.Owner is { } owner && Oracle.Resolve(connection, out _) is { } target
            && !owner.Equals(OracleAccess.Place(target.Config), StringComparison.Ordinal))
        {
            return TableMatch.Refused(OracleText.TableElsewhere(owner, OracleAccess.Place(target.Config)));
        }

        var run = await CatalogAsync(connection, [OracleCatalogQueries.Resolve], [new SqlParameterValue("name", parts.Name)], cancellationToken).ConfigureAwait(false);
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
