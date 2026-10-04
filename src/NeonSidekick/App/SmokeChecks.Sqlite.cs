using Microsoft.Data.Sqlite;
using NeonSidekick.Sql;
using NeonSidekick.Sqlite;

namespace NeonSidekick.App;

public static partial class SmokeChecks
{
    /// <summary>
    /// <c>sqlite:readonly</c> (2026-10-04): the SQLite tools' four layers on the published binary over a scratch file — the gate
    /// passes a WITH clause and refuses an ATTACH; a read through <see cref="SqliteAccess.Run"/> sees the row; a write the app
    /// itself sends (past the gate) is refused by the read-only open and <c>query_only</c>; and <c>sqlite3_interrupt</c> stops a
    /// runaway recursive query at a one-second cap (the timer, the interop call and the error code together). The file is deleted.
    /// </summary>
    public static SmokeCheck ProbeSqlite()
    {
        const string name = "sqlite:readonly";
        if (SqliteReadOnlyGate.Check("WITH c AS (SELECT 1 AS x) SELECT x FROM c") is { } refused)
        {
            return new SmokeCheck(name, false, "the gate refused a plain WITH clause: " + refused);
        }

        if (SqliteReadOnlyGate.Check("SELECT 1; ATTACH 'x.db' AS x") is null || SqliteReadOnlyGate.Check("WITH c AS (SELECT 1) INSERT INTO t SELECT * FROM c") is null)
        {
            return new SmokeCheck(name, false, "the gate let a second statement or a WITH … INSERT through");
        }

        string path = Path.Combine(Path.GetTempPath(), "neonsidekick-smoke-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var setup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ConnectionString))
            {
                setup.Open();
                using var command = setup.CreateCommand();
                command.CommandText = "CREATE TABLE t (id INTEGER PRIMARY KEY, name TEXT); INSERT INTO t (name) VALUES ('probe');";
                command.ExecuteNonQuery();
            }

            var target = new SqliteTarget("smoke", path);
            var read = SqliteAccess.Run(target, ["SELECT name FROM t WHERE id = @id"], [new SqlParameterValue("id", 1L)], 10, 5, CancellationToken.None);
            if (read.Outcome != SqlOutcome.Ok || read.Grids is not [{ Rows: [["probe"]] }])
            {
                return new SmokeCheck(name, false, "the read did not see the row: " + SqliteText.Error(read));
            }

            var write = SqliteAccess.Run(target, ["INSERT INTO t (name) VALUES ('x')"], [], 10, 5, CancellationToken.None);
            if (write.Outcome == SqlOutcome.Ok)
            {
                return new SmokeCheck(name, false, "a write went through the read-only open");
            }

            var slow = SqliteAccess.Run(target, [SqliteCheck.SlowQuery], [], 10, 1, CancellationToken.None);
            return slow.Outcome == SqlOutcome.Timeout
                ? new SmokeCheck(name, true, $"gate ok; read ok; write refused ({write.Detail}); interrupted at 1 s")
                : new SmokeCheck(name, false, "the runaway query was not interrupted: " + slow.Outcome);
        }
        catch (Exception ex) when (ex is SqliteException or TypeInitializationException or DllNotFoundException or EntryPointNotFoundException or NotSupportedException or IOException)
        {
            return new SmokeCheck(name, false, ex.GetType().Name + ": " + Diagnostics.LogText.Excerpt(ex.Message));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
        }
    }
}
