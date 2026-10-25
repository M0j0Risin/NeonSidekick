using System.Globalization;
using NeonSidekick.DuckDb;
using NeonSidekick.Files;
using NeonSidekick.Sql;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>
/// <c>--duckdb-check &lt;name|path&gt;</c> (2026-10-25), <see cref="SqliteCheck"/>'s twin: the DuckDB tools' proof on the published binary
/// against a real file — a name of the loaded profile's <c>duckdb.json</c>, or a DuckDB file in the working directory by its path — nothing
/// written to it: it opens and counts the tables and views; nested and wide types from literals; the gate refusing the bypasses; the
/// read-only open refusing a write the app sends past the gate; the interrupt stopping a long query at a one-second cap; and the data-file
/// layers in a scratch folder of their own (a file inside reads; one outside, one through a link out and a quoted name reaching out are
/// refused, and the engine's own folder rule refuses an outside file the path layer never saw). Exit 0 only when every line passes.
/// </summary>
internal static class DuckDbCheck
{
    /// <summary>
    /// A query that runs for minutes and changes nothing: what the interrupt stops (a cross join DuckDB cannot fold away, the spike's —
    /// a bare <c>count(*)</c> over a huge <c>range</c> answers at once).
    /// </summary>
    public const string SlowQuery = "SELECT sum(hash(a.range + b.range)) FROM range(100000) a, range(100000) b";

    /// <summary>Nested and wide values from literals, each read back as DuckDB writes it.</summary>
    public const string TypeMatrix =
        "SELECT [1, 2, 3] AS list, {'a': 1, 'b': 'x'} AS struct, 9223372036854775807 AS bigint, 12345678901234567890 AS hugeint, " +
        "'\\xDE\\xAD'::BLOB AS blob, DATE '2026-10-25' AS date, 0.5 AS decimal, 'ünïcode' AS text, NULL AS none, INTERVAL 3 DAY AS interval, true AS bool";

    /// <summary>A write the gate refuses, sent past it: <c>EXPLAIN</c> is the one form the reader runs unwrapped, and DuckDB plans it on the file.</summary>
    public const string WritePastGate = "EXPLAIN ANALYZE CREATE TABLE neonsidekick_check AS SELECT 1 AS a";

    public static string IntroLine(string name) => $"DuckDB check: database '{name}' — nothing is written to it.";

    public static async Task<int> RunAsync(IAnsiConsole console, DuckDbAccess access, string name, int timeoutSeconds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(name);
        console.MarkupLine(Markup.Escape(IntroLine(name)));
        var checks = new List<SmokeCheck>();
        Task<SqlRun> Run(IReadOnlyList<string> statements, int timeout) => access.RunAsync(name, statements, [], 10, timeout, cancellationToken);

        var count = await Run([DuckDbCatalogQueries.Count], timeoutSeconds).ConfigureAwait(false);
        if (count.Outcome != SqlOutcome.Ok)
        {
            checks.Add(new SmokeCheck("duckdb:open", false, DuckDbText.Error(count)));
            return Report(console, checks);
        }

        checks.Add(new SmokeCheck("duckdb:open", true, $"{count.Connection}: {count.Grids[0].Rows[0][0]} tables and views"));

        var types = await Run([TypeMatrix], timeoutSeconds).ConfigureAwait(false);
        if (types.Outcome == SqlOutcome.Ok)
        {
            var cells = types.Grids[0].Columns.Zip(types.Grids[0].Rows[0]).ToList();
            bool ok = cells.Any(c => c.First == "list" && c.Second == "[1, 2, 3]") && cells.Any(c => c.First == "bigint" && c.Second == "9223372036854775807")
                && cells.Any(c => c.First == "hugeint" && c.Second == "12345678901234567890");
            checks.Add(new SmokeCheck("duckdb:types", ok, string.Join(", ", cells.Select(c => c.First + "=" + c.Second))));
        }
        else
        {
            checks.Add(new SmokeCheck("duckdb:types", false, DuckDbText.Error(types)));
        }

        string[] bypasses = ["SELECT 1; DELETE FROM x", "ATTACH 'other.duckdb' AS o", "COPY x TO 'out.csv'", "WITH c AS (SELECT 1) INSERT INTO x SELECT * FROM c", "INSTALL httpfs", "SELECT * FROM read_csv(lower('x.csv'))", "SET enable_external_access = true"];
        var passed = bypasses.Where(b => DuckDbReadOnlyGate.Check(b) is null).ToList();
        checks.Add(new SmokeCheck("duckdb:gate", passed.Count == 0, passed.Count == 0 ? $"refused all {bypasses.Length.ToString(CultureInfo.InvariantCulture)} bypasses" : "let through: " + string.Join(" | ", passed)));

        // The read-only refusal itself, as SQLite's (the database review's D9): only DuckDB's "read-only mode" proves the open held.
        var write = await Run([WritePastGate], timeoutSeconds).ConfigureAwait(false);
        bool refused = write.Outcome == SqlOutcome.Failed && write.Detail.Contains("read-only", StringComparison.OrdinalIgnoreCase);
        checks.Add(new SmokeCheck("duckdb:readonly", refused, refused ? "a write past the gate refused: " + write.Detail : "a write was not refused as read-only: " + write.Outcome + " " + write.Detail));

        var slow = await Run([SlowQuery], 1).ConfigureAwait(false);
        checks.Add(new SmokeCheck("duckdb:interrupt", slow.Outcome == SqlOutcome.Timeout, slow.Outcome == SqlOutcome.Timeout ? $"stopped at 1 s ({(long)slow.Elapsed.TotalMilliseconds} ms)" : "not stopped: " + slow.Outcome));

        checks.Add(await Task.Run(() => Sandbox(cancellationToken), cancellationToken).ConfigureAwait(false));
        return Report(console, checks);
    }

    /// <summary>
    /// <c>duckdb:sandbox</c>: the data-file layers in a scratch folder under the temp folder (deleted after), never the user's — the path layer
    /// and the engine's folder rule each refusing on its own. A link out is skipped (and said) where this account may not make links.
    /// </summary>
    internal static SmokeCheck Sandbox(CancellationToken cancellationToken)
    {
        const string name = "duckdb:sandbox";
        string scratch = Path.Combine(Path.GetTempPath(), "neonsidekick-duckdb-check-" + Guid.NewGuid().ToString("N"));
        string inside = Path.Combine(scratch, "box");
        string outside = Path.Combine(scratch, "outside");
        try
        {
            Directory.CreateDirectory(inside);
            Directory.CreateDirectory(outside);
            File.WriteAllText(Path.Combine(inside, "inside.csv"), "a,b\n1,in\n");
            string outsideFile = Path.Combine(outside, "outside.csv");
            File.WriteAllText(outsideFile, "a,b\n2,out\n");
            var box = new WorkingDirectory(() => inside, TimeProvider.System);
            string root = box.Root;

            string? Read(string sql, out SqlRun? run)
            {
                run = null;
                string body = DuckDbReadOnlyGate.Body(sql);
                if (DuckDbReadOnlyGate.Inspect(body, out var files) is { } gateRefused)
                {
                    return gateRefused;
                }

                if (DuckDbFilePaths.Rewrite(body, files, box, cancellationToken, out var prepared) is { } pathRefused)
                {
                    return pathRefused;
                }

                run = DuckDbAccess.Run(DuckDbTarget.Memory, [prepared], [], 10, 10, root, cancellationToken);
                return null;
            }

            string? insideRefused = Read("SELECT b FROM read_csv('inside.csv')", out var read);
            if (insideRefused is not null || read is not { Outcome: SqlOutcome.Ok, Grids: [{ Rows: [["in"]] }] })
            {
                return new SmokeCheck(name, false, "a file inside did not read: " + (insideRefused ?? (read!.Outcome == SqlOutcome.Ok ? "the wrong rows" : DuckDbText.Error(read))));
            }

            var reachingOut = new List<string>();
            foreach (string sql in new[] { "SELECT * FROM read_csv('../outside/outside.csv')", "SELECT * FROM read_csv('https://example.com/x.csv')", "SELECT * FROM \"../outside/outside.csv\"", "SELECT * FROM read_csv('" + outsideFile.Replace("'", "''", StringComparison.Ordinal) + "')" })
            {
                if (Read(sql, out var run) is null && run is { Outcome: SqlOutcome.Ok })
                {
                    reachingOut.Add(sql);
                }
            }

            string link = "link skipped (no right to make links)";
            try
            {
                Directory.CreateSymbolicLink(Path.Combine(inside, "link"), outside);
                link = Read("SELECT * FROM read_csv('link/outside.csv')", out var linked) is null && linked is { Outcome: SqlOutcome.Ok } ? "" : "link out refused";
                if (link.Length == 0)
                {
                    reachingOut.Add("link/outside.csv");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }

            // The engine's own folder rule, under no path layer: the outside file's full path handed straight to DuckDB.
            var engine = DuckDbAccess.Run(DuckDbTarget.Memory, [DuckDbPrepared.AsWritten("SELECT * FROM read_csv('" + outsideFile.Replace("'", "''", StringComparison.Ordinal) + "')")], [], 10, 10, root, cancellationToken);
            if (engine.Outcome == SqlOutcome.Ok)
            {
                reachingOut.Add("the engine read an outside file");
            }

            return reachingOut.Count == 0
                ? new SmokeCheck(name, true, $"a file inside read; outside, quoted, full paths and a URL refused; {link}; the engine refused on its own")
                : new SmokeCheck(name, false, "reached out: " + string.Join(" | ", reachingOut));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SmokeCheck(name, false, ex.GetType().Name + ": " + Diagnostics.LogText.Excerpt(ex.Message));
        }
        finally
        {
            try
            {
                Directory.Delete(scratch, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
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
            ? Theme.ColorMarkup(Theme.Good, $"DUCKDB CHECK PASS  {checks.Count.ToString(CultureInfo.InvariantCulture)} checks")
            : Theme.ColorMarkup(Theme.Bad, $"DUCKDB CHECK FAIL  {failed.ToString(CultureInfo.InvariantCulture)} of {checks.Count.ToString(CultureInfo.InvariantCulture)} checks failed"));
        return failed == 0 ? 0 : 1;
    }
}
