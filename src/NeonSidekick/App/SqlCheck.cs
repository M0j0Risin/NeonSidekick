using System.Globalization;
using NeonSidekick.Sql;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>
/// <c>--sql-check &lt;connection&gt;</c> (2026-10-03, the user's ask): the SQL tools' proof on the <em>published</em> binary against a
/// real SQL Server, <see cref="OracleCheck"/>'s and <see cref="MySqlCheck"/>'s elder sibling come late. SqlClient declares no AOT
/// support and ScriptDom none either; until now <c>sql:parse-and-sni</c> and <c>sql:credentials</c> loaded them with no server,
/// and the spike's one hand-run was the only live proof. Over a connection of the loaded profile's <c>sql.json</c> (whatever its
/// sign-in: a <c>runas</c> connection proves the impersonated open too): who it signs in as; every type from literals (a
/// <c>decimal(38)</c> past <see cref="decimal"/>, <c>datetimeoffset</c>, <c>xml</c>, <c>sql_variant</c>, an <c>nvarchar(max)</c>) and a
/// <c>geography</c> read as the unreadable marker, not a failed call; the gate refusing the bypasses under the trimmed ScriptDom;
/// the batch inside a transaction and the transaction rolled back (a global temporary table made inside it is gone after — the
/// one thing the check creates, and it never outlives the batch); a cancel and a timeout stopping a query on the server. SQL
/// Server has no read-only session as Oracle's 23ai and MySQL's transaction have, so the rollback is the layer to prove. Exit 0
/// only when every line passes. Not part of the build gate: it needs a server.
/// </summary>
internal static class SqlCheck
{
    /// <summary>A cross join big enough to run for minutes, changing nothing: what the cancel and the timeout stop. Not <c>COUNT_BIG(*)</c>: the optimizer answers that without the rows.</summary>
    public const string SlowQuery = "SELECT MAX(CHECKSUM(a.name, b.name, c.name)) AS n FROM sys.all_objects AS a CROSS JOIN sys.all_objects AS b CROSS JOIN sys.all_objects AS c";

    /// <summary>Who the connection signs in as, where, and on what.</summary>
    public const string WhoAmI =
        "SELECT SUSER_SNAME() AS login, DB_NAME() AS db, CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128)) AS version, " +
        "CAST(SERVERPROPERTY('Edition') AS nvarchar(128)) AS edition, CAST(CONNECTIONPROPERTY('net_transport') AS nvarchar(40)) AS transport";

    /// <summary>Every type a row can bring back, from literals; <c>geography</c> is the CLR type the app carries no assembly for.</summary>
    public const string TypeMatrix =
        """
        SELECT CAST(42 AS int) AS [int], CAST(9223372036854775807 AS bigint) AS [bigint],
               CAST(99999999999999999999999999999999999999 AS decimal(38,0)) AS [decimal38], CAST(12345678901234567890.123456789 AS decimal(38,9)) AS [decimal],
               CAST(0.5 AS float) AS [float], CAST(0.25 AS real) AS [real], CAST(12.3456 AS money) AS [money], CAST('2026-10-03' AS date) AS [date],
               CAST('2026-10-03T10:15:00' AS datetime) AS [datetime], CAST('2026-10-03T10:15:00.1234567' AS datetime2(7)) AS [datetime2],
               CAST('2026-10-03T10:15:00+02:00' AS datetimeoffset) AS [datetimeoffset], CAST('10:15:00.5' AS time) AS [time],
               CAST('6F9619FF-8B86-D011-B42D-00C04FC964FF' AS uniqueidentifier) AS [uniqueidentifier], 0xDEADBEEF AS [varbinary], CAST(1 AS bit) AS [bit],
               CAST('café' AS varchar(10)) AS [varchar], N'ünïcode' AS [nvarchar], REPLICATE(CAST(N'x' AS nvarchar(max)), 5000) AS [nvarcharmax],
               CAST('<a b="1"/>' AS xml) AS [xml], CAST(42 AS sql_variant) AS [sql_variant], geography::Point(47.65, -122.35, 4326) AS [geography],
               CAST(NULL AS int) AS [null]
        """;

    /// <summary>The global temporary table the rollback line makes inside the transaction and expects gone after it.</summary>
    public const string ScratchTable = "##neonsidekick_check";

    public static string IntroLine(string name) => $"SQL check: connection '{name}' — nothing is kept.";

    public static async Task<int> RunAsync(IAnsiConsole console, SqlCatalog catalog, string name, int timeoutSeconds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(name);
        console.MarkupLine(Markup.Escape(IntroLine(name)));
        var access = new SqlAccess(() => catalog);
        var checks = new List<SmokeCheck>();
        if (string.IsNullOrWhiteSpace(name) || catalog.Find(name, null) is not { } target)
        {
            checks.Add(new SmokeCheck("sql:connection", false, catalog.Connections.Count == 0 ? SqlText.NoConnections : SqlText.UnknownConnection(name, string.Join(", ", catalog.Connections.Select(c => c.Name)))));
            return Report(console, checks);
        }

        Task<SqlRun> Run(string sql, int timeout, CancellationToken token) =>
            access.RunAsync(name, null, null, sql, [], 10, timeout, token);

        var who = await Run(WhoAmI, timeoutSeconds, cancellationToken).ConfigureAwait(false);
        if (who.Outcome != SqlOutcome.Ok)
        {
            checks.Add(new SmokeCheck("sql:connect", false, SqlText.Error(who)));
            return Report(console, checks);
        }

        var row = who.Grids[0].Rows.FirstOrDefault() ?? ["", "", "", "", ""];
        checks.Add(new SmokeCheck("sql:connect", true, $"as {row[0]} in {row[1]} ({target.Config.Auth ?? SqlConnectionConfig.SqlAuth} sign-in), SQL Server {row[2]} {row[3]}, over {row[4]}"));

        var types = await Run(TypeMatrix, timeoutSeconds, cancellationToken).ConfigureAwait(false);
        checks.Add(TypesCheck(types));

        string[] bypasses =
        [
            "SELECT 1 DELETE FROM t", "SELECT 1 EXEC xp_cmdshell 'dir'", "SELECT * INTO copy FROM t", "WITH c AS (SELECT 1 AS x) DELETE FROM t",
            "SELECT * FROM OPENROWSET(BULK 'C:\\secret.txt', SINGLE_CLOB) AS f", "SELECT * FROM linked.db.dbo.t", "SELECT NEXT VALUE FOR dbo.seq",
            "SELECT 'DELETE FROM t' AS looks_bad",
        ];
        // The last is no bypass: a string that only reads like one. A parser passes it; a pattern match would not.
        var passed = bypasses[..^1].Where(b => SqlReadOnlyGate.Check(b) is null).ToList();
        bool stringRead = SqlReadOnlyGate.Check(bypasses[^1]) is null;
        checks.Add(new SmokeCheck("sql:gate", passed.Count == 0 && stringRead, passed.Count > 0 ? "let through: " + string.Join(" | ", passed) : stringRead ? $"refused all {(bypasses.Length - 1).ToString(CultureInfo.InvariantCulture)} bypasses" : "refused a SELECT of a string that reads like a DELETE"));

        var inside = await Run($"CREATE TABLE {ScratchTable} (x int); INSERT INTO {ScratchTable} VALUES (1); SELECT @@TRANCOUNT AS trancount, COUNT(*) AS n FROM {ScratchTable}", timeoutSeconds, cancellationToken).ConfigureAwait(false);
        var after = await Run($"SELECT CASE WHEN OBJECT_ID('tempdb..{ScratchTable}') IS NULL THEN 'gone' ELSE 'kept' END AS state", timeoutSeconds, cancellationToken).ConfigureAwait(false);
        checks.Add(RollbackCheck(inside, after));

        using (var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            cancel.CancelAfter(TimeSpan.FromSeconds(1.5));
            var started = DateTime.UtcNow;
            string detail;
            bool ok;
            try
            {
                var run = await Run(SlowQuery, 120, cancel.Token).ConfigureAwait(false);
                (ok, detail) = (false, "not cancelled: " + run.Outcome);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                (ok, detail) = (true, $"stopped after {(DateTime.UtcNow - started).TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)} s");
            }

            checks.Add(new SmokeCheck("sql:cancel", ok, detail));
        }

        var timedOut = await Run(SlowQuery, 1, cancellationToken).ConfigureAwait(false);
        checks.Add(new SmokeCheck("sql:timeout", timedOut.Outcome == SqlOutcome.Timeout, timedOut.Outcome == SqlOutcome.Timeout ? "stopped after 1 s" : timedOut.Outcome + " " + timedOut.Detail));
        return Report(console, checks);
    }

    /// <summary>The type matrix's line: every cell read but the CLR one, which must be the unreadable marker; the 38-digit decimal whole.</summary>
    private static SmokeCheck TypesCheck(SqlRun run)
    {
        if (run.Outcome != SqlOutcome.Ok)
        {
            return new SmokeCheck("sql:types", false, SqlText.Error(run));
        }

        var grid = run.Grids[0];
        var cells = grid.Columns.Zip(grid.Rows[0]).ToList();
        string clrMarker = SqlText.Unreadable("geography");
        var unreadable = cells.Where(c => c.First != "geography" && c.Second.StartsWith('(') && c.Second.Contains(':', StringComparison.Ordinal)).Select(c => c.First).ToList();
        bool clr = cells.Any(c => c.First == "geography" && c.Second == clrMarker);
        bool whole = cells.Any(c => c.First == "decimal38" && c.Second == "99999999999999999999999999999999999999");
        bool ok = unreadable.Count == 0 && clr && whole;
        string detail = string.Join(", ", cells.Select(c => c.First + "=" + (c.Second.Length > 24 ? c.Second[..24] + "…" : c.Second)));
        return new SmokeCheck("sql:types", ok, ok ? detail
            : "unreadable: " + string.Join(", ", unreadable) + (whole ? "" : "; decimal(38) not whole") + (clr ? "" : "; geography not the unreadable marker") + " — " + detail);
    }

    /// <summary>The rollback's line: the batch ran inside one transaction (and its write was seen there), and nothing of it is left.</summary>
    private static SmokeCheck RollbackCheck(SqlRun inside, SqlRun after)
    {
        if (inside.Outcome != SqlOutcome.Ok)
        {
            return new SmokeCheck("sql:rollback", false, SqlText.Error(inside));
        }

        if (after.Outcome != SqlOutcome.Ok)
        {
            return new SmokeCheck("sql:rollback", false, SqlText.Error(after));
        }

        var seen = inside.Grids.LastOrDefault()?.Rows.FirstOrDefault() ?? ["", ""];
        string state = after.Grids.FirstOrDefault()?.Rows.FirstOrDefault()?[0] ?? "";
        bool ok = seen[0] == "1" && seen[1] == "1" && state == "gone";
        return new SmokeCheck("sql:rollback", ok, ok
            ? $"the batch ran in one transaction and its write to {ScratchTable} was rolled back"
            : $"@@TRANCOUNT {seen[0]}, rows seen {seen[1]}, afterwards {state}");
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
            ? Theme.ColorMarkup(Theme.Good, $"SQL CHECK PASS  {checks.Count.ToString(CultureInfo.InvariantCulture)} checks")
            : Theme.ColorMarkup(Theme.Bad, $"SQL CHECK FAIL  {failed.ToString(CultureInfo.InvariantCulture)} of {checks.Count.ToString(CultureInfo.InvariantCulture)} checks failed"));
        return failed == 0 ? 0 : 1;
    }
}
