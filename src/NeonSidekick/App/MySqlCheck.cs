using System.Globalization;
using NeonSidekick.MySql;
using NeonSidekick.Sql;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>
/// <c>--mysql-check &lt;connection&gt;</c> (2026-09-30), <see cref="OracleCheck"/>'s twin: the MySQL tools' proof on the published binary
/// against a real MySQL or MariaDB server, over a connection of the loaded profile's <c>mysql.json</c>, nothing seeded, nothing
/// written: who it signs in as; every type from literals (a 65-digit DECIMAL, unsigned BIGINT, JSON, BIT, TIME, geometry); the
/// gate refusing the bypasses; the server refusing a write inside the read-only transaction (ERROR 1792 on a temporary table); a
/// cancel and the server-side time cap stopping a query. Exit 0 only when every line passes. Not part of the build gate.
/// </summary>
internal static class MySqlCheck
{
    /// <summary>A statement that runs for a long while and changes nothing: what the cancel and the time cap stop (a killed <c>SLEEP</c> just returns 1).</summary>
    public const string SlowQuery = "SELECT COUNT(*) FROM information_schema.COLUMNS a, information_schema.COLUMNS b, information_schema.COLUMNS c";

    /// <summary>Every type a row can bring back, from literals.</summary>
    public const string TypeMatrix =
        """
        SELECT CAST(42 AS SIGNED) AS `int`, CAST('12345678901234567890123456789012345.123456789012345678901234567890' AS DECIMAL(65,30)) AS `decimal65`,
               CAST(12.5 AS DECIMAL(10,2)) AS `decimal`, CAST(0.5 AS DOUBLE) AS `double`, DATE '2026-09-30' AS `date`,
               TIMESTAMP '2026-09-30 10:15:00.123' AS `datetime`, TIME '-12:30:00' AS `time`, JSON_OBJECT('a', 1) AS `json`,
               X'DEADBEEF' AS `binary`, b'10100101' AS `bit`, REPEAT('x', 500) AS `text`, 18446744073709551615 AS `unsigned`,
               ST_GeomFromText('POINT(1 2)') AS `geometry`, N'ünïcode' AS `nchar`, NULL AS `null`
        """;

    public static string IntroLine(string name) => $"MySQL check: connection '{name}' — nothing is written.";

    public static async Task<int> RunAsync(IAnsiConsole console, MySqlCatalog catalog, string name, int timeoutSeconds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(name);
        console.MarkupLine(Markup.Escape(IntroLine(name)));
        var access = new MySqlAccess(() => catalog);
        var checks = new List<SmokeCheck>();
        if (string.IsNullOrWhiteSpace(name) || catalog.Find(name, null) is null)
        {
            checks.Add(new SmokeCheck("mysql:connection", false, catalog.Connections.Count == 0 ? MySqlText.NoConnections : MySqlText.UnknownConnection(name, string.Join(", ", catalog.Connections.Select(c => c.Name)))));
            return Report(console, checks);
        }

        Task<SqlRun> Run(IReadOnlyList<string> statements, int timeout, CancellationToken token) =>
            access.RunAsync(name, null, null, statements, [], 10, timeout, token);

        var who = await Run([MySqlCatalogQueries.WhoAmI], timeoutSeconds, cancellationToken).ConfigureAwait(false);
        if (who.Outcome != SqlOutcome.Ok)
        {
            checks.Add(new SmokeCheck("mysql:connect", false, MySqlText.Error(who)));
            return Report(console, checks);
        }

        var row = who.Grids[0].Rows.FirstOrDefault() ?? ["", "", ""];
        string version = row.ElementAtOrDefault(2) ?? "";
        checks.Add(new SmokeCheck("mysql:connect", true, $"as {row[0]}, {(MySqlAccess.IsMariaDb(version) ? "MariaDB" : "MySQL")} {version}"));

        var types = await Run([TypeMatrix], timeoutSeconds, cancellationToken).ConfigureAwait(false);
        checks.Add(TypesCheck(types));

        string[] bypasses =
        [
            "SELECT 1; DELETE FROM t", "SELECT * FROM t FOR UPDATE", "SELECT 1 INTO OUTFILE '/tmp/x'", "SELECT LOAD_FILE('/etc/passwd')",
            "SELECT /*! 1; DROP TABLE t */ 1", "SELECT 'a\\'; DROP TABLE t; --' AS s",
        ];
        // The last is no bypass: one string, 'a\'; DROP …', a backslash escape inside it — the gate passes it and the server must read it whole.
        var passed = bypasses[..^1].Where(b => MySqlReadOnlyGate.Check(b) is null).ToList();
        bool escapeRead = MySqlReadOnlyGate.Check(bypasses[^1]) is null;
        checks.Add(new SmokeCheck("mysql:gate", passed.Count == 0 && escapeRead, passed.Count > 0 ? "let through: " + string.Join(" | ", passed) : escapeRead ? $"refused all {(bypasses.Length - 1).ToString(CultureInfo.InvariantCulture)} bypasses" : "refused a string holding an escaped quote"));

        var escaped = await Run([bypasses[^1]], timeoutSeconds, cancellationToken).ConfigureAwait(false);
        bool sameString = escaped.Outcome == SqlOutcome.Ok && escaped.Grids[0].Rows.FirstOrDefault()?[0] == "a'; DROP TABLE t; --";
        checks.Add(new SmokeCheck("mysql:escapes", sameString, sameString ? "the server read the backslash escape as the gate did (sql_mode stripped)" : escaped.Outcome + " " + escaped.Detail));

        var written = await Run(["CREATE TEMPORARY TABLE neonsidekick_check (x INT)"], timeoutSeconds, cancellationToken).ConfigureAwait(false);
        bool refused = written.Outcome == SqlOutcome.Failed && written.Detail.Contains("1792", StringComparison.Ordinal);
        checks.Add(new SmokeCheck("mysql:read-only", refused, written.Outcome == SqlOutcome.Ok ? "a write ran inside the read-only transaction" : written.Detail));

        using (var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            cancel.CancelAfter(TimeSpan.FromSeconds(1.5));
            var started = DateTime.UtcNow;
            string detail;
            bool ok;
            try
            {
                var run = await Run([SlowQuery], 120, cancel.Token).ConfigureAwait(false);
                (ok, detail) = (false, "not cancelled: " + run.Outcome);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                (ok, detail) = (true, $"stopped after {(DateTime.UtcNow - started).TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)} s");
            }

            checks.Add(new SmokeCheck("mysql:cancel", ok, detail));
        }

        var timedOut = await Run([SlowQuery], 1, cancellationToken).ConfigureAwait(false);
        checks.Add(new SmokeCheck("mysql:timeout", timedOut.Outcome == SqlOutcome.Timeout, timedOut.Outcome == SqlOutcome.Timeout ? "stopped after 1 s" : timedOut.Outcome + " " + timedOut.Detail));
        return Report(console, checks);
    }

    private static SmokeCheck TypesCheck(SqlRun run)
    {
        if (run.Outcome != SqlOutcome.Ok)
        {
            return new SmokeCheck("mysql:types", false, MySqlText.Error(run));
        }

        var grid = run.Grids[0];
        var cells = grid.Columns.Zip(grid.Rows[0]).ToList();
        var unreadable = cells.Where(c => c.Second.StartsWith('(') && c.Second.Contains(':', StringComparison.Ordinal)).Select(c => c.First).ToList();
        bool whole = cells.Any(c => c.First == "decimal65" && c.Second.StartsWith("12345678901234567890123456789012345.1234567890", StringComparison.Ordinal));
        bool ok = unreadable.Count == 0 && whole;
        string detail = string.Join(", ", cells.Select(c => c.First + "=" + (c.Second.Length > 24 ? c.Second[..24] + "…" : c.Second)));
        return new SmokeCheck("mysql:types", ok, ok ? detail : "unreadable: " + string.Join(", ", unreadable) + (whole ? "" : "; DECIMAL(65,30) not whole") + " — " + detail);
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
            ? Theme.ColorMarkup(Theme.Good, $"MYSQL CHECK PASS  {checks.Count.ToString(CultureInfo.InvariantCulture)} checks")
            : Theme.ColorMarkup(Theme.Bad, $"MYSQL CHECK FAIL  {failed.ToString(CultureInfo.InvariantCulture)} of {checks.Count.ToString(CultureInfo.InvariantCulture)} checks failed"));
        return failed == 0 ? 0 : 1;
    }
}
