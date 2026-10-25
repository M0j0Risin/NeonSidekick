using DuckDB.NET.Data;
using NeonSidekick.DuckDb;
using NeonSidekick.Sql;

namespace NeonSidekick.App;

public static partial class SmokeChecks
{
    /// <summary>
    /// <c>duckdb:readonly</c> (2026-10-25): the DuckDB tools' layers on the published binary over a scratch file — the gate passes a WITH
    /// clause and refuses a COPY and a second statement; a read through <see cref="DuckDbAccess.Run"/> sees a LIST and a STRUCT column (the
    /// values DuckDB.NET's own readers threw on, read as DuckDB writes them); a write the app sends past the gate is refused by the read-only
    /// open; an outside file is refused by the engine's folder rule alone; and <c>duckdb_interrupt</c> stops a long query at a one-second cap
    /// (the timer, the interop call and the error together). The scratch folder is deleted.
    /// </summary>
    public static SmokeCheck ProbeDuckDb()
    {
        const string name = "duckdb:readonly";
        if (DuckDbReadOnlyGate.Check("WITH c AS (SELECT 1 AS x) SELECT x FROM c") is { } refused)
        {
            return new SmokeCheck(name, false, "the gate refused a plain WITH clause: " + refused);
        }

        if (DuckDbReadOnlyGate.Check("SELECT 1; DELETE FROM t") is null || DuckDbReadOnlyGate.Check("COPY t TO 'out.csv'") is null)
        {
            return new SmokeCheck(name, false, "the gate let a second statement or a COPY through");
        }

        string folder = Path.Combine(Path.GetTempPath(), "neonsidekick-smoke-duckdb-" + Guid.NewGuid().ToString("N"));
        string box = Path.Combine(folder, "box");
        string path = Path.Combine(box, "smoke.duckdb");
        string outside = Path.Combine(folder, "outside.csv");
        try
        {
            Directory.CreateDirectory(box);
            File.WriteAllText(outside, "a\n1\n");
            using (var setup = new DuckDBConnection(new DuckDBConnectionStringBuilder { DataSource = path }.ConnectionString))
            {
                setup.Open();
                using var command = setup.CreateCommand();
                command.CommandText = "CREATE TABLE t (id INTEGER, tags VARCHAR[], meta STRUCT(a INTEGER, b VARCHAR)); INSERT INTO t VALUES (1, ['x', 'y'], {'a': 1, 'b': 'probe'});";
                command.ExecuteNonQuery();
            }

            var target = new DuckDbTarget("smoke", path);
            var read = DuckDbAccess.Run(target, [DuckDbPrepared.AsWritten("SELECT tags, meta FROM t WHERE id = $id")], [new SqlParameterValue("id", 1L)], 10, 5, box, CancellationToken.None);
            if (read.Outcome != SqlOutcome.Ok || read.Grids is not [{ Rows: [[{ } tags, { } meta]] }] || !tags.Contains('y', StringComparison.Ordinal) || !meta.Contains("probe", StringComparison.Ordinal))
            {
                return new SmokeCheck(name, false, "the read did not see the row: " + DuckDbText.Error(read));
            }

            var write = DuckDbAccess.Run(target, [DuckDbPrepared.AsWritten(DuckDbCheck.WritePastGate)], [], 10, 5, box, CancellationToken.None);
            if (write.Outcome == SqlOutcome.Ok || !write.Detail.Contains("read-only", StringComparison.OrdinalIgnoreCase))
            {
                return new SmokeCheck(name, false, write.Outcome == SqlOutcome.Ok ? "a write went through the read-only open" : "the write failed, but not as read-only: " + write.Detail);
            }

            var reach = DuckDbAccess.Run(target, [DuckDbPrepared.AsWritten("SELECT * FROM read_csv('" + outside.Replace("'", "''", StringComparison.Ordinal) + "')")], [], 10, 5, box, CancellationToken.None);
            if (reach.Outcome == SqlOutcome.Ok)
            {
                return new SmokeCheck(name, false, "the engine read a file outside the working directory");
            }

            var slow = DuckDbAccess.Run(target, [DuckDbPrepared.AsWritten(DuckDbCheck.SlowQuery)], [], 10, 1, box, CancellationToken.None);
            return slow.Outcome == SqlOutcome.Timeout
                ? new SmokeCheck(name, true, "gate ok; LIST and STRUCT read; write refused (read-only); outside file refused; interrupted at 1 s")
                : new SmokeCheck(name, false, "the long query was not interrupted: " + slow.Outcome);
        }
        catch (Exception ex) when (ex is DuckDBException or TypeInitializationException or DllNotFoundException or EntryPointNotFoundException or NotSupportedException or IOException or InvalidOperationException)
        {
            return new SmokeCheck(name, false, ex.GetType().Name + ": " + Diagnostics.LogText.Excerpt(ex.Message));
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
