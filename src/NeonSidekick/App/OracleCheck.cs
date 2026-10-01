using System.Globalization;
using NeonSidekick.Oracle;
using NeonSidekick.Sql;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>
/// <c>--oracle-check &lt;connection&gt;</c> (2026-09-30): the Oracle tools' proof on the <em>published</em> binary against a real
/// server, the one build where ODP.NET's NativeAOT failures show (the spike found the trimmed driver reading <c>LONG</c> columns
/// as empty and throwing from a setter; <c>dotnet test</c> runs under the JIT and sees neither). Over a connection of the
/// loaded profile's <c>oracle.json</c>, nothing seeded, nothing written: who it signs in as; every scalar type, the LOBs and
/// (on 23ai) BOOLEAN materialised from <c>DUAL</c>; a <c>LONG</c> read from the dictionary (a column default: 23ai hides <c>ALL_VIEWS.TEXT</c> of its own views); the gate refusing the bypasses; the
/// server refusing a row lock inside the read-only transaction (ORA-28193 on 23ai, ORA-01456 before); a cancel and a timeout
/// stopping a query on the server. Exit 0 only when every line passes. Plain lines, like <c>--smoke</c>; not part of the
/// <c>build.ps1</c> gate — it needs a server.
/// </summary>
internal static class OracleCheck
{
    /// <summary>A statement that runs for a long while and changes nothing: what the cancel and the timeout stop.</summary>
    public const string SlowQuery = "SELECT COUNT(*) FROM sys.all_objects a, sys.all_objects b, sys.all_objects c";

    /// <summary>Every scalar and LOB type a row can bring back, from <c>DUAL</c>.</summary>
    public const string TypeMatrix =
        """
        SELECT CAST(42 AS NUMBER(10)) AS "number", 12345678901234567890123456789012345678 AS "number38", CAST(12.5 AS NUMBER(12,2)) AS "decimal",
               CAST(0.5 AS BINARY_DOUBLE) AS "binary_double", DATE '2026-09-30' AS "date", TIMESTAMP '2026-09-30 10:15:00.123 +02:00' AS "timestamp_tz",
               CAST(TIMESTAMP '2026-09-30 10:15:00' AS TIMESTAMP) AS "timestamp", INTERVAL '1 02:03:04' DAY TO SECOND AS "interval_ds",
               INTERVAL '2-3' YEAR TO MONTH AS "interval_ym", TO_CLOB(RPAD('x', 500, 'x')) AS "clob", HEXTORAW('DEADBEEF') AS "raw",
               TO_BLOB(HEXTORAW('0102')) AS "blob", N'ünïcode' AS "nvarchar", CAST(NULL AS VARCHAR2(10)) AS "null"
        FROM sys.dual
        """;

    public static string IntroLine(string name) => $"Oracle check: connection '{name}' — nothing is written.";

    public static async Task<int> RunAsync(IAnsiConsole console, OracleCatalog catalog, string name, int timeoutSeconds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(name);
        console.MarkupLine(Markup.Escape(IntroLine(name)));
        var access = new OracleAccess(() => catalog);
        var checks = new List<SmokeCheck>();
        if (catalog.Find(name, null) is null || string.IsNullOrWhiteSpace(name))
        {
            checks.Add(new SmokeCheck("oracle:connection", false, catalog.Connections.Count == 0 ? OracleText.NoConnections : OracleText.UnknownConnection(name, string.Join(", ", catalog.Connections.Select(c => c.Name)))));
            return Report(console, checks);
        }

        Task<SqlRun> Run(IReadOnlyList<string> statements, int timeout, CancellationToken token) =>
            access.RunAsync(name, null, null, statements, [], 10, timeout, token);

        var who = await Run([OracleCatalogQueries.WhoAmI], timeoutSeconds, cancellationToken).ConfigureAwait(false);
        if (who.Outcome != SqlOutcome.Ok)
        {
            checks.Add(new SmokeCheck("oracle:connect", false, OracleText.Error(who)));
            return Report(console, checks);
        }

        var row = who.Grids[0].Rows.FirstOrDefault() ?? ["", "", ""];
        int release = OracleAccess.Release(row.ElementAtOrDefault(2));
        checks.Add(new SmokeCheck("oracle:connect", true, $"as {row[0]} in {row[1]}, Oracle {row.ElementAtOrDefault(2)}; session READ_ONLY {(release >= OracleAccess.SessionReadOnlyRelease ? "on" : "not before 23ai")}"));

        var types = await Run(release >= OracleAccess.SessionReadOnlyRelease ? [TypeMatrix, "SELECT TRUE AS \"boolean\" FROM sys.dual"] : [TypeMatrix], timeoutSeconds, cancellationToken).ConfigureAwait(false);
        checks.Add(TypesCheck(types));

        var longs = await Run(["SELECT c.data_default AS \"default\" FROM sys.all_tab_columns c WHERE c.data_default IS NOT NULL AND ROWNUM = 1"], timeoutSeconds, cancellationToken).ConfigureAwait(false);
        string longText = longs.Outcome == SqlOutcome.Ok && longs.Grids[0].Rows.Count > 0 ? longs.Grids[0].Rows[0][0] : "";
        checks.Add(new SmokeCheck("oracle:long", longs.Outcome == SqlOutcome.Ok && longText.Length > 0 && longText != SqlText.Null,
            longs.Outcome == SqlOutcome.Ok ? $"ALL_TAB_COLUMNS.DATA_DEFAULT read {longText.Length.ToString(CultureInfo.InvariantCulture)} chars" : OracleText.Error(longs)));

        string[] bypasses = ["SELECT 1 FROM dual FOR UPDATE", "WITH FUNCTION f RETURN NUMBER IS BEGIN RETURN 1; END; SELECT f FROM dual", "SELECT s.NEXTVAL FROM dual", "SELECT 1 FROM t@remote", "SELECT 1 FROM dual; DELETE FROM t"];
        var passed = bypasses.Where(b => OracleReadOnlyGate.Check(b) is null).ToList();
        checks.Add(new SmokeCheck("oracle:gate", passed.Count == 0, passed.Count == 0 ? $"refused all {bypasses.Length.ToString(CultureInfo.InvariantCulture)} bypasses" : "let through: " + string.Join(" | ", passed)));

        var locked = await Run(["SELECT dummy FROM sys.dual FOR UPDATE"], timeoutSeconds, cancellationToken).ConfigureAwait(false);
        bool refused = locked.Outcome == SqlOutcome.Failed && (locked.Detail.Contains("ORA-28193", StringComparison.Ordinal) || locked.Detail.Contains("ORA-01456", StringComparison.Ordinal));
        checks.Add(new SmokeCheck("oracle:read-only", refused, locked.Outcome == SqlOutcome.Ok ? "a row lock ran inside the read-only transaction" : locked.Detail));

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

            checks.Add(new SmokeCheck("oracle:cancel", ok, detail));
        }

        var timedOut = await Run([SlowQuery], 1, cancellationToken).ConfigureAwait(false);
        checks.Add(new SmokeCheck("oracle:timeout", timedOut.Outcome == SqlOutcome.Timeout, timedOut.Outcome == SqlOutcome.Timeout ? "ORA-01013 after 1 s" : timedOut.Outcome + " " + timedOut.Detail));
        return Report(console, checks);
    }

    /// <summary>The type matrix's line: every cell read — none left unreadable, the 38-digit number whole, the CLOB clipped, not lost.</summary>
    private static SmokeCheck TypesCheck(SqlRun run)
    {
        if (run.Outcome != SqlOutcome.Ok)
        {
            return new SmokeCheck("oracle:types", false, OracleText.Error(run));
        }

        var grid = run.Grids[0];
        var cells = grid.Columns.Zip(grid.Rows[0]).ToList();
        if (run.Grids.Count > 1)
        {
            cells.AddRange(run.Grids[1].Columns.Zip(run.Grids[1].Rows[0]));
        }

        var unreadable = cells.Where(c => c.Second.StartsWith('(') && c.Second.Contains(':', StringComparison.Ordinal)).Select(c => c.First).ToList();
        bool whole = cells.Any(c => c.First == "number38" && c.Second == "12345678901234567890123456789012345678");
        bool ok = unreadable.Count == 0 && whole;
        string detail = string.Join(", ", cells.Select(c => c.First + "=" + (c.Second.Length > 24 ? c.Second[..24] + "…" : c.Second)));
        return new SmokeCheck("oracle:types", ok, ok ? detail : "unreadable: " + string.Join(", ", unreadable) + (whole ? "" : "; NUMBER(38) not whole") + " — " + detail);
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
            ? Theme.ColorMarkup(Theme.Good, $"ORACLE CHECK PASS  {checks.Count.ToString(CultureInfo.InvariantCulture)} checks")
            : Theme.ColorMarkup(Theme.Bad, $"ORACLE CHECK FAIL  {failed.ToString(CultureInfo.InvariantCulture)} of {checks.Count.ToString(CultureInfo.InvariantCulture)} checks failed"));
        return failed == 0 ? 0 : 1;
    }
}
