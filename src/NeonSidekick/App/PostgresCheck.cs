using System.Globalization;
using NeonSidekick.Postgres;
using NeonSidekick.Sql;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>
/// <c>--postgres-check &lt;connection&gt;</c> (2026-10-04), <see cref="MySqlCheck"/>'s twin: the PostgreSQL tools' proof on the published
/// binary against a real server, over a connection of the loaded profile's <c>postgres.json</c>, nothing seeded, nothing written: who it
/// signs in as (and what it could change); every type from literals (a numeric past <see cref="decimal"/>, arrays, JSON, interval, uuid,
/// inet, a range); the gate refusing the bypasses; the server refusing a write the app sends past the gate (a temporary table inside the
/// read-only transaction: SQLSTATE 25006); <c>statement_timeout</c> stopping a sleep; the lock to the connection's database and schema
/// (2026-10-09, <see cref="PostgresBoundary"/>) and what else the role could reach. Exit 0 only when every line passes.
/// </summary>
internal static class PostgresCheck
{
    /// <summary>A statement that runs for a while and changes nothing: what the server's timeout stops. pg_sleep is the app's own here, past the gate.</summary>
    public const string SlowQuery = "SELECT pg_sleep(30)";

    /// <summary>Every type a row can bring back, from literals.</summary>
    public const string TypeMatrix =
        "SELECT 42::int AS int, 12345678901234567890123456789012345.123456789::numeric AS numeric_big, 12.5::numeric(10,2) AS numeric, 0.5::float8 AS double, " +
        "DATE '2026-10-04' AS date, TIMESTAMP '2026-10-04 10:15:00.123' AS timestamp, TIMESTAMPTZ '2026-10-04 10:15:00+02' AS timestamptz, " +
        "INTERVAL '1 day 02:03:04' AS interval, '{\"a\": 1}'::jsonb AS jsonb, ARRAY[1, 2, 3] AS int_array, 'f81d4fae-7dec-11d0-a765-00a0c91e6bf6'::uuid AS uuid, " +
        "'192.168.0.1/24'::inet AS inet, int4range(1, 10) AS range, '\\xDEADBEEF'::bytea AS bytea, 'ünïcode'::text AS text, NULL::text AS none";

    public static string IntroLine(string name) => $"PostgreSQL check: connection '{name}' — nothing is written.";

    /// <summary>The first column of one of a run's result sets.</summary>
    private static List<string> Column(SqlRun run, int set) => set < run.Grids.Count ? run.Grids[set].Rows.Select(r => r[0]).ToList() : [];

    /// <summary>
    /// The lock's line: the cluster's own catalogs refused; under a schema lock, another schema the database holds (when there is one),
    /// <c>information_schema</c> and <c>pg_tables</c> refused, the schema's own and <c>pg_catalog</c> passed.
    /// </summary>
    private static SmokeCheck BoundaryCheck(string? schema, SqlRun names)
    {
        if (names.Outcome != SqlOutcome.Ok)
        {
            return new SmokeCheck("postgres:boundary", false, PostgresText.Error(names));
        }

        var visible = Column(names, 0);
        var outside = new List<string> { "SELECT datname FROM pg_database", "SELECT * FROM pg_stat_activity", "SELECT rolname FROM pg_catalog.pg_roles" };
        var inside = new List<string> { "SELECT now()", "SELECT t.a FROM t" };
        if (schema is not null)
        {
            outside.Add("SELECT * FROM information_schema.tables");
            outside.Add("SELECT schemaname, tablename FROM pg_tables");   // a catalog listing every schema (2026-10-11)
            if (visible.FirstOrDefault(v => !v.Equals(schema, StringComparison.OrdinalIgnoreCase) && !PostgresBoundary.AlwaysSchemas.Contains(v) && v != "information_schema") is { } other)
            {
                outside.Add($"SELECT 1 FROM \"{other}\".t");
            }

            inside.Add($"SELECT * FROM \"{schema}\".t");
            inside.Add("SELECT pg_catalog.now()");
        }

        var passed = outside.Where(s => PostgresBoundary.Check(s, schema, visible) is null).ToList();
        var refused = inside.Where(s => PostgresBoundary.Check(s, schema, visible) is not null).ToList();
        bool ok = passed.Count == 0 && refused.Count == 0;
        return new SmokeCheck("postgres:boundary", ok, ok
            ? $"held to its database{(schema is null ? "" : " and schema " + schema)}: refused all {outside.Count.ToString(CultureInfo.InvariantCulture)} reaches outside it"
            : (passed.Count > 0 ? "let through: " + string.Join(" | ", passed) : "") + (refused.Count > 0 ? " refused: " + string.Join(" | ", refused) : ""));
    }

    public static async Task<int> RunAsync(IAnsiConsole console, PostgresCatalog catalog, string name, int timeoutSeconds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(name);
        console.MarkupLine(Markup.Escape(IntroLine(name)));
        var access = new PostgresAccess(() => catalog);
        var checks = new List<SmokeCheck>();
        if (string.IsNullOrWhiteSpace(name) || catalog.Find(name) is not { } target)
        {
            checks.Add(new SmokeCheck("postgres:connection", false, catalog.Connections.Count == 0 ? PostgresText.NoConnections : PostgresText.UnknownConnection(name, string.Join(", ", catalog.Connections.Select(c => c.Name)))));
            return Report(console, checks);
        }

        Task<SqlRun> Run(IReadOnlyList<string> statements, int timeout) => access.RunAsync(name, statements, [], 10, timeout, cancellationToken);

        var who = await Run(PostgresCatalogQueries.WhoAmI, timeoutSeconds).ConfigureAwait(false);
        if (who.Outcome != SqlOutcome.Ok)
        {
            checks.Add(new SmokeCheck("postgres:connect", false, PostgresText.Error(who)));
            return Report(console, checks);
        }

        var row = who.Grids[0].Rows.FirstOrDefault() ?? ["", "", ""];
        var powers = who.Grids.Count > 1 ? who.Grids[1].Rows.Select(r => r[0]).ToList() : [];
        checks.Add(new SmokeCheck("postgres:connect", true, $"as {row[0]} in {row[1]}; {row[2]}" + (powers.Count > 0 ? $" — can change data: {string.Join(", ", powers)}" : "")));

        var types = await Run([TypeMatrix], timeoutSeconds).ConfigureAwait(false);
        if (types.Outcome == SqlOutcome.Ok)
        {
            var cells = types.Grids[0].Columns.Zip(types.Grids[0].Rows[0]).ToList();
            var unreadable = cells.Where(c => c.First != "numeric_big" && c.Second.StartsWith('(') && c.Second.Contains(':', StringComparison.Ordinal)).Select(c => c.First).ToList();
            string detail = string.Join(", ", cells.Select(c => c.First + "=" + (c.Second.Length > 24 ? c.Second[..24] + "…" : c.Second)));
            checks.Add(new SmokeCheck("postgres:types", unreadable.Count == 0, unreadable.Count == 0 ? detail : "unreadable: " + string.Join(", ", unreadable) + " — " + detail));
        }
        else
        {
            checks.Add(new SmokeCheck("postgres:types", false, PostgresText.Error(types)));
        }

        string[] bypasses =
        [
            "SELECT 1; DROP TABLE x", "WITH d AS (DELETE FROM x RETURNING *) SELECT * FROM d", "SELECT * INTO y FROM x", "SELECT * FROM x FOR UPDATE",
            "DO $$ BEGIN PERFORM 1; END $$", "SELECT pg_read_file('/etc/passwd')", "SELECT nextval('s')", "SELECT $1", "COPY x TO '/tmp/x'",
            "SELECT U&'\\0041'", "SELECT 1 /* /* nested */ ; DROP TABLE x",
        ];
        var passed = bypasses.Where(b => PostgresReadOnlyGate.Check(b) is null).ToList();
        checks.Add(new SmokeCheck("postgres:gate", passed.Count == 0, passed.Count == 0 ? $"refused all {bypasses.Length.ToString(CultureInfo.InvariantCulture)} bypasses" : "let through: " + string.Join(" | ", passed)));

        string? schema = string.IsNullOrWhiteSpace(target.Config.Schema) ? null : target.Config.Schema.Trim();
        var names = await access.RunAsync(name, [PostgresBoundary.Names], [], PostgresBoundary.MaxNames, timeoutSeconds, cancellationToken).ConfigureAwait(false);
        checks.Add(BoundaryCheck(schema, names));
        var reach = await access.RunAsync(name, PostgresCatalogQueries.Reach, [], 100, timeoutSeconds, cancellationToken).ConfigureAwait(false);
        checks.Add(new SmokeCheck("postgres:reach", reach.Outcome == SqlOutcome.Ok, reach.Outcome == SqlOutcome.Ok
            ? PostgresText.Reach(target.Config.Database?.Trim() ?? "", schema, Column(reach, 0), Column(reach, 1), Column(reach, 2))
            : PostgresText.Error(reach)));

        var write = await Run(["CREATE TEMP TABLE neonsidekick_check (a int)"], timeoutSeconds).ConfigureAwait(false);
        bool refusedWrite = write.Outcome == SqlOutcome.Failed && write.Detail.Contains(PostgresAccess.ReadOnlyTransaction, StringComparison.Ordinal);
        checks.Add(new SmokeCheck("postgres:readonly", refusedWrite, refusedWrite ? "a write past the gate refused: " + write.Detail : "a write was not refused: " + write.Outcome + " " + write.Detail));

        var slow = await Run([SlowQuery], 1).ConfigureAwait(false);
        checks.Add(new SmokeCheck("postgres:timeout", slow.Outcome == SqlOutcome.Timeout, slow.Outcome == SqlOutcome.Timeout ? $"statement_timeout stopped it ({(long)slow.Elapsed.TotalMilliseconds} ms)" : "not stopped: " + slow.Outcome + " " + slow.Detail));
        return Report(console, checks);
    }

    private static int Report(IAnsiConsole console, IReadOnlyList<SmokeCheck> checks)
    {
        int failed = 0;
        foreach (var check in checks)
        {
            failed += check.Passed ? 0 : 1;
            string verdict = check.Passed ? Theme.ColorMarkup(Theme.Good, "PASS") : Theme.ColorMarkup(Theme.Bad, "FAIL");
            console.MarkupLine($"  {verdict}  {Markup.Escape(check.Name)}  {Theme.DimMarkup(check.Detail)}");
        }

        console.WriteLine();
        console.MarkupLine(failed == 0
            ? Theme.ColorMarkup(Theme.Good, $"POSTGRES CHECK PASS  {checks.Count.ToString(CultureInfo.InvariantCulture)} checks")
            : Theme.ColorMarkup(Theme.Bad, $"POSTGRES CHECK FAIL  {failed.ToString(CultureInfo.InvariantCulture)} of {checks.Count.ToString(CultureInfo.InvariantCulture)} checks failed"));
        return failed == 0 ? 0 : 1;
    }
}
