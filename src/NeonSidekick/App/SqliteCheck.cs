using System.Globalization;
using NeonSidekick.Sql;
using NeonSidekick.Sqlite;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>
/// <c>--sqlite-check &lt;name|path&gt;</c> (2026-10-04), <see cref="MySqlCheck"/>'s twin: the SQLite tools' proof on the published binary
/// against a real file — a name of the loaded profile's <c>sqlite.json</c>, or a file in the working directory by its path — nothing
/// written: it opens and counts the tables; every type from literals; the gate refusing the bypasses; the read-only layers refusing a
/// write the app sends past the gate; and the interrupt stopping a runaway query at a one-second cap. Exit 0 only when every line passes.
/// </summary>
internal static class SqliteCheck
{
    /// <summary>A query that runs for ever and changes nothing: what the interrupt stops.</summary>
    public const string SlowQuery = "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n) SELECT count(*) FROM n";

    /// <summary>Every storage class a row can bring back, from literals.</summary>
    public const string TypeMatrix =
        "SELECT 42 AS integer, 9223372036854775807 AS bigint, 0.5 AS real, 'ünïcode' AS text, X'DEADBEEF' AS blob, NULL AS none, " +
        "date('2026-10-04') AS date, json_object('a', 1) AS json";

    public static string IntroLine(string name) => $"SQLite check: database '{name}' — nothing is written.";

    public static async Task<int> RunAsync(IAnsiConsole console, SqliteAccess access, string name, int timeoutSeconds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(name);
        console.MarkupLine(Markup.Escape(IntroLine(name)));
        var checks = new List<SmokeCheck>();
        Task<SqlRun> Run(IReadOnlyList<string> statements, int timeout) => access.RunAsync(name, null, statements, [], 10, timeout, cancellationToken);

        var count = await Run([SqliteCatalogQueries.Count], timeoutSeconds).ConfigureAwait(false);
        if (count.Outcome != SqlOutcome.Ok)
        {
            checks.Add(new SmokeCheck("sqlite:open", false, SqliteText.Error(count)));
            return Report(console, checks);
        }

        checks.Add(new SmokeCheck("sqlite:open", true, $"{count.Connection}: {count.Grids[0].Rows[0][0]} tables and views"));

        var types = await Run([TypeMatrix], timeoutSeconds).ConfigureAwait(false);
        if (types.Outcome == SqlOutcome.Ok)
        {
            var cells = types.Grids[0].Columns.Zip(types.Grids[0].Rows[0]).ToList();
            bool ok = cells.Any(c => c.First == "bigint" && c.Second == "9223372036854775807") && cells.Any(c => c.First == "blob" && c.Second == "0xDEADBEEF");
            checks.Add(new SmokeCheck("sqlite:types", ok, string.Join(", ", cells.Select(c => c.First + "=" + c.Second))));
        }
        else
        {
            checks.Add(new SmokeCheck("sqlite:types", false, SqliteText.Error(types)));
        }

        string[] bypasses = ["SELECT 1; DELETE FROM x", "ATTACH 'other.db' AS o", "PRAGMA writable_schema = 1", "WITH c AS (SELECT 1) INSERT INTO x SELECT * FROM c", "SELECT load_extension('x')", "SELECT ?"];
        var passed = bypasses.Where(b => SqliteReadOnlyGate.Check(b) is null).ToList();
        checks.Add(new SmokeCheck("sqlite:gate", passed.Count == 0, passed.Count == 0 ? $"refused all {bypasses.Length.ToString(CultureInfo.InvariantCulture)} bypasses" : "let through: " + string.Join(" | ", passed)));

        var write = await Run(["CREATE TABLE neonsidekick_check (a)"], timeoutSeconds).ConfigureAwait(false);
        checks.Add(new SmokeCheck("sqlite:readonly", write.Outcome == SqlOutcome.Failed, write.Outcome == SqlOutcome.Failed ? "a write past the gate refused: " + write.Detail : "a write was not refused: " + write.Outcome));

        var slow = await Run([SlowQuery], 1).ConfigureAwait(false);
        checks.Add(new SmokeCheck("sqlite:interrupt", slow.Outcome == SqlOutcome.Timeout, slow.Outcome == SqlOutcome.Timeout ? $"stopped at 1 s ({(long)slow.Elapsed.TotalMilliseconds} ms)" : "not stopped: " + slow.Outcome));
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
            ? Theme.ColorMarkup(Theme.Good, $"SQLITE CHECK PASS  {checks.Count.ToString(CultureInfo.InvariantCulture)} checks")
            : Theme.ColorMarkup(Theme.Bad, $"SQLITE CHECK FAIL  {failed.ToString(CultureInfo.InvariantCulture)} of {checks.Count.ToString(CultureInfo.InvariantCulture)} checks failed"));
        return failed == 0 ? 0 : 1;
    }
}
