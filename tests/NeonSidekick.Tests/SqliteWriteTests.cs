using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Diagnostics;
using NeonSidekick.Files;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Settings;
using NeonSidekick.Sql;
using NeonSidekick.Sqlite;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary><c>sqlite_execute</c>'s gate (2026-10-05): one statement of any kind, DML + DDL + PRAGMA, what reaches beyond the file refused.</summary>
public sealed class SqliteWriteGateTests
{
    [Theory]
    [InlineData("INSERT INTO t (a) VALUES (@a) RETURNING id;")]
    [InlineData("UPDATE t SET a = 1 WHERE id = :id")]
    [InlineData("DELETE FROM t WHERE id = $id")]
    [InlineData("REPLACE INTO t VALUES (1)")]
    [InlineData("INSERT INTO t VALUES (1) ON CONFLICT (id) DO UPDATE SET a = excluded.a")]
    [InlineData("WITH c AS (SELECT 1) INSERT INTO t SELECT * FROM c")]
    [InlineData("CREATE TABLE t (id INTEGER PRIMARY KEY, a TEXT)")]
    [InlineData("CREATE INDEX t_a ON t (a)")]
    [InlineData("CREATE VIEW v AS SELECT * FROM t")]
    [InlineData("DROP TABLE t")]
    [InlineData("ALTER TABLE t ADD COLUMN b TEXT")]
    [InlineData("CREATE TRIGGER tr AFTER INSERT ON t BEGIN UPDATE t SET a = CASE WHEN a IS NULL THEN 'x' ELSE a END; DELETE FROM u; END;")]
    [InlineData("CREATE TEMP TRIGGER tr AFTER DELETE ON t BEGIN SELECT RAISE(ROLLBACK, 'no'); END")]
    [InlineData("PRAGMA user_version = 3")]
    [InlineData("PRAGMA foreign_keys")]
    [InlineData("VACUUM")]
    [InlineData("SELECT 1")]
    [InlineData("INSERT INTO t VALUES ('attach; detach')  -- ATTACH in a comment")]
    public void Allows_OneStatement(string sql) => Assert.Null(SqliteWriteGate.Check(sql));

    [Theory]
    [InlineData("ATTACH 'x.db' AS x", "uses ATTACH")]
    [InlineData("DETACH x", "uses DETACH")]
    [InlineData("BEGIN", "uses BEGIN")]
    [InlineData("COMMIT", "uses COMMIT")]
    [InlineData("SAVEPOINT a", "uses SAVEPOINT")]
    [InlineData("VACUUM INTO 'copy.db'", "uses VACUUM INTO")]
    [InlineData("VACUUM main INTO 'copy.db'", "uses VACUUM INTO")]
    [InlineData("PRAGMA writable_schema = ON", "uses writable_schema")]
    [InlineData("PRAGMA main.writable_schema = 1", "uses writable_schema")]
    [InlineData("INSERT INTO sqlite_dbpage VALUES (1, x'00')", "uses sqlite_dbpage")]
    [InlineData("UPDATE \"sqlite_dbpage\" SET data = x'00'", "uses sqlite_dbpage")]
    [InlineData("SELECT load_extension('evil')", "uses load_extension()")]
    [InlineData("INSERT INTO t VALUES (\"readfile\"('x'))", "uses readfile()")]
    [InlineData("DELETE FROM t WHERE id = ?", "? placeholder")]
    [InlineData("INSERT INTO t VALUES (1); INSERT INTO t VALUES (2)", "the SQL is 2 statements")]
    [InlineData("CREATE TRIGGER tr AFTER INSERT ON t BEGIN DELETE FROM u; END; DROP TABLE t", "the SQL is 2 statements")]
    [InlineData("INSERT INTO t VALUES ('never ends", "a string never ends")]
    [InlineData("  ", "give the statement")]
    public void Refuses_TheRest_WithTheSentence(string sql, string part)
    {
        string? refused = SqliteWriteGate.Check(sql);
        Assert.NotNull(refused);
        Assert.StartsWith("Error: ", refused);
        Assert.Contains(part, refused);
    }

    [Theory]
    [InlineData("INSERT INTO t VALUES (1)", SqliteStatementKind.Data)]
    [InlineData("replace into t values (1)", SqliteStatementKind.Data)]
    [InlineData("UPDATE t SET a = 1", SqliteStatementKind.Data)]
    [InlineData("DELETE FROM t RETURNING *", SqliteStatementKind.Delete)]
    [InlineData("WITH c(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM c LIMIT 5) INSERT INTO t SELECT x FROM c", SqliteStatementKind.Data)]
    [InlineData("WITH RECURSIVE c AS (SELECT 1), d AS MATERIALIZED (VALUES (2)) DELETE FROM t WHERE a IN (SELECT * FROM c)", SqliteStatementKind.Delete)]
    [InlineData("WITH RECURSIVE c AS (SELECT 1) UPDATE t SET a = 1 WHERE a IN (SELECT * FROM c)", SqliteStatementKind.Data)]
    [InlineData("WITH c AS (SELECT 1) SELECT * FROM c", SqliteStatementKind.Read)]
    [InlineData("CREATE TABLE t (a)", SqliteStatementKind.Create)]
    [InlineData("CREATE TABLE t AS SELECT * FROM u", SqliteStatementKind.Create)]
    [InlineData("CREATE UNIQUE INDEX i ON t (a)", SqliteStatementKind.Create)]
    [InlineData("CREATE VIRTUAL TABLE f USING fts5(body)", SqliteStatementKind.Create)]
    [InlineData("CREATE TRIGGER tr AFTER INSERT ON t BEGIN DELETE FROM u; END", SqliteStatementKind.Create)]
    [InlineData("ALTER TABLE t RENAME COLUMN a TO b", SqliteStatementKind.Alter)]
    [InlineData("DROP VIEW IF EXISTS v", SqliteStatementKind.Drop)]
    [InlineData("VACUUM", SqliteStatementKind.Upkeep)]
    [InlineData("REINDEX", SqliteStatementKind.Upkeep)]
    [InlineData("ANALYZE t", SqliteStatementKind.Upkeep)]
    [InlineData("PRAGMA user_version = 3", SqliteStatementKind.Pragma)]
    [InlineData("SELECT 1", SqliteStatementKind.Read)]
    [InlineData("(SELECT 1)", SqliteStatementKind.Read)]
    [InlineData("VALUES (1)", SqliteStatementKind.Read)]
    [InlineData("EXPLAIN QUERY PLAN DELETE FROM t", SqliteStatementKind.Read)]
    public void Classify_ByTheStatement_AWithByWhatFollowsIt(string sql, SqliteStatementKind kind) => Assert.Equal(kind, SqliteWriteGate.Classify(sql));

    [Fact]
    public void Check_RefusesAKindNotTicked_NamingWhatIs()
    {
        IReadOnlyList<SqliteStatementKind> data = [SqliteStatementKind.Data];
        Assert.Null(SqliteWriteGate.Check("WITH c AS (SELECT 1) INSERT INTO t SELECT * FROM c", data));
        Assert.Equal(
            "Error: the SQL is dropping (DROP TABLE, INDEX, VIEW, TRIGGER), which the user has not allowed; sqlite_execute may run changing data — the user ticks more in SQLite statements allowed on the SQLite tab of /tools",
            SqliteWriteGate.Check("DROP TABLE t", data));
        Assert.Contains("is reading (SELECT, VALUES, EXPLAIN)", SqliteWriteGate.Check("SELECT 1", data));
        Assert.Contains("may run changing data, settings —", SqliteWriteGate.Check("CREATE TABLE t (a)", [SqliteStatementKind.Data, SqliteStatementKind.Pragma]));
        // The structural refusals come first, whatever is ticked.
        Assert.StartsWith("Error: the SQL uses ATTACH", SqliteWriteGate.Check("ATTACH 'x.db' AS x", data));
        Assert.Equal(SqliteText.UnknownStatement("FOO"), SqliteWriteGate.Check("FOO BAR"));
        Assert.Null(SqliteWriteGate.Classify("WITH c AS (SELECT 1)"));
    }

    /// <summary>
    /// A trigger's body ends at the first END after a <c>;</c>, as SQLite's grammar has it (the review, 2026-10-05): BEGIN, END and
    /// CASE spelled as names, or a CASE … END inside the body, never move it.
    /// </summary>
    [Theory]
    [InlineData("CREATE TRIGGER t AFTER UPDATE OF begin ON x BEGIN SELECT 1; END; DELETE FROM users", 1)]
    [InlineData("CREATE TRIGGER t AFTER INSERT ON x WHEN new.begin = 1 BEGIN SELECT 1; END; DELETE FROM users", 1)]
    [InlineData("CREATE TRIGGER t AFTER INSERT ON x BEGIN UPDATE y SET end = CASE WHEN 1 THEN 2 END; END; DELETE FROM users; DROP TABLE y", 2)]
    [InlineData("CREATE TRIGGER t AFTER INSERT ON x; DELETE FROM users; END", 2)]
    public void Refuses_ASecondStatement_AfterATriggersBody(string sql, int separators) =>
        Assert.Equal(SqliteText.WriteNotOneStatement(separators + 1), SqliteWriteGate.Check(sql));

    [Theory]
    [InlineData("CREATE TRIGGER t AFTER UPDATE OF begin, end ON x BEGIN UPDATE y SET end = CASE WHEN new.begin THEN 2 END; END;")]
    [InlineData("CREATE TRIGGER t AFTER INSERT ON x BEGIN SELECT CASE WHEN 1 THEN 2 END; SELECT 3; END")]
    public void Allows_ATriggerWhoseBodyHoldsBlockWords(string sql) => Assert.Null(SqliteWriteGate.Check(sql));

    /// <summary>A trigger needs its body's kinds as well as creating (the review, 2026-10-05): one that deletes, deleting.</summary>
    [Fact]
    public void ATrigger_NeedsTheKindsOfItsBodysChanges()
    {
        var defaults = SqliteStatementKinds.Resolve((IReadOnlyList<string>?)null);
        string wipe = "CREATE TRIGGER wipe AFTER INSERT ON log BEGIN DELETE FROM orders; END";
        Assert.Equal(SqliteText.KindNotAllowed(SqliteStatementKind.Delete, defaults), SqliteWriteGate.Check(wipe, defaults));
        Assert.Null(SqliteWriteGate.Check(wipe, [.. defaults, SqliteStatementKind.Delete]));

        // The event (AFTER DELETE, UPDATE OF) is no change of the body's, and a function named like a verb is a function.
        Assert.Null(SqliteWriteGate.Check("CREATE TRIGGER tr AFTER DELETE ON t BEGIN INSERT INTO log VALUES (replace(old.a, 'x', 'y')); END", defaults));
        Assert.Equal(
            SqliteText.KindNotAllowed(SqliteStatementKind.Data, [SqliteStatementKind.Create]),
            SqliteWriteGate.Check("CREATE TRIGGER tr AFTER UPDATE OF a ON t BEGIN UPDATE u SET b = 1; END", [SqliteStatementKind.Create]));
        Assert.Null(SqliteWriteGate.Check("CREATE TRIGGER tr AFTER UPDATE OF a ON t BEGIN SELECT RAISE(ABORT, 'no'); END", [SqliteStatementKind.Create]));
    }

    [Fact]
    public void TheKinds_ChangingDataByDefault_ReadCaseBlind()
    {
        Assert.Equal(["data", "create", "read"], new AppSettingsData().SqliteStatementsAllowed);
        Assert.Equal([SqliteStatementKind.Data, SqliteStatementKind.Create, SqliteStatementKind.Read], SqliteStatementKinds.Resolve((IReadOnlyList<string>?)null));
        Assert.Equal([SqliteStatementKind.Create, SqliteStatementKind.Read], SqliteStatementKinds.Resolve([" READ ", "create", "nonsense"]));
        Assert.Empty(SqliteStatementKinds.Resolve([]));
        Assert.Equal(Enum.GetValues<SqliteStatementKind>().Length, SqliteStatementKinds.Names.Length);
        Assert.Equal("changing data (INSERT, UPDATE, REPLACE); settings (PRAGMA)", SqliteStatementKinds.Describe([SqliteStatementKind.Data, SqliteStatementKind.Pragma]));

        string data = SqliteExecuteTool.DescribeFor([SqliteStatementKind.Data]);
        Assert.Contains("only these kinds: changing data (INSERT, UPDATE, REPLACE).", data);
        Assert.DoesNotContain("With create", data);
        Assert.Contains("With create it makes a new database file", SqliteExecuteTool.DescribeFor([SqliteStatementKind.Data, SqliteStatementKind.Create]));
        Assert.DoesNotContain("A read runs without asking", data);
        Assert.Contains("A read runs without asking, on the file opened read-only.", SqliteExecuteTool.DescribeFor([SqliteStatementKind.Data, SqliteStatementKind.Read]));
    }

    [Fact]
    public void TheMode_ParsesBothWords_AndFallsBackToReadOnly()
    {
        Assert.Equal("read-only", SqliteModes.Default);
        Assert.Equal("read-only", new AppSettingsData().SqliteMode);
        Assert.False(new AppSettingsData().SqliteSandboxFiles);   // off by default since 2026-10-05 (the user's call)
        Assert.Equal(SqliteMode.ReadWrite, SqliteModes.Resolve(new AppSettingsData { SqliteMode = " Read-Write " }));
        Assert.Equal(SqliteMode.ReadOnly, SqliteModes.Resolve(new AppSettingsData { SqliteMode = "read-only" }));
        Assert.Equal(SqliteMode.ReadOnly, SqliteModes.Resolve(new AppSettingsData { SqliteMode = "yolo" }));
        Assert.All(SqliteModes.Names, n => Assert.NotEmpty(SqliteModes.Describe(n)));
    }

    [Fact]
    public void TheRules_CarryTheWriteSentence_OnlyWithTheTool()
    {
        Assert.Contains(Assistant.SqliteRule + " " + Assistant.SqliteWriteRule, Assistant.DefaultRules(false, tools: true, sqlite: true, sqliteWrite: true));
        Assert.DoesNotContain(Assistant.SqliteWriteRule, Assistant.DefaultRules(false, tools: true, sqlite: true));
        Assert.DoesNotContain(Assistant.SqliteWriteRule, Assistant.DefaultRules(false, tools: true, sqliteWrite: true));
        Assert.Contains(Assistant.SqliteWriteRule, new TurnRules(Sqlite: true, SqliteWrite: true).DefaultRules(false));
        Assert.DoesNotContain(Assistant.SqliteWriteRule, new TurnRules(SqliteWrite: true).DefaultRules(false));
        Assert.Contains(SqliteExecuteTool.ToolName, Assistant.SqliteWriteRule);
    }
}

/// <summary><c>sqlite_execute</c> and its access over real files in a temp folder (2026-10-05).</summary>
public sealed class SqliteExecuteTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("neon-sqlite-write-").FullName;
    private readonly ManualTimeProvider _time = new();
    private readonly AppSettingsData _settings = new() { SqliteTools = true, SqliteSandboxFiles = true, SqliteMode = "read-write", SqliteDatabasesOffered = ["shop"], SqliteStatementsAllowed = [.. SqliteStatementKinds.Names] };
    private readonly string _profile;
    private readonly string _work;
    private readonly string _shop;

    public SqliteExecuteTests()
    {
        string home = Path.Combine(_dir, "home");
        _profile = Path.Combine(home, "profiles", "default");
        _work = Path.Combine(_dir, "work");
        _shop = Path.Combine(_work, "shop.db");
        Directory.CreateDirectory(_profile);
        Directory.CreateDirectory(Path.Combine(_work, "data"));
        using (var connection = Open(_shop, SqliteOpenMode.ReadWriteCreate))
        {
            Exec(connection, "CREATE TABLE customers (id INTEGER PRIMARY KEY, name TEXT NOT NULL); INSERT INTO customers (name) VALUES ('Ada'), ('Grace');");
        }

        File.WriteAllText(SqliteConfigFile.ProfilePath(_profile), """{ "databases": { "shop": { "path": "../../../work/shop.db" } } }""");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    private static SqliteConnection Open(string path, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false }.ConnectionString);
        connection.Open();
        return connection;
    }

    private static string Exec(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? "";
    }

    private static string Read(string path, string sql)
    {
        using var connection = Open(path, SqliteOpenMode.ReadOnly);
        return Exec(connection, sql);
    }

    private SqliteAccess Access() => new(
        () => SqliteConfigFile.LoadCatalog(_profile, Path.Combine(_dir, "home")).Offered(_settings.SqliteDatabasesOffered),
        () => _settings.SqliteSandboxFiles ? new WorkingDirectory(() => _work, _time) : null);

    private SqliteTarget Shop() => new("shop", _shop);

    [Fact]
    public async Task Execute_Commits_CountsTheChanges_AndReturnsTheRowsOfARETURNING()
    {
        var insert = await SqliteAccess.ExecuteAsync(Shop(), false, "INSERT INTO customers (name) VALUES (@a), (:b) RETURNING id, name", [new("a", "Linus"), new("b", "Barbara")], 10, 5, CancellationToken.None);
        Assert.Equal(SqlOutcome.Ok, insert.Outcome);
        Assert.Equal(2, insert.Changes);
        Assert.Equal([["3", "Linus"], ["4", "Barbara"]], insert.Grids[0].Rows);
        Assert.Equal("4", Read(_shop, "SELECT count(*) FROM customers"));

        // A RETURNING cut by the row cap still made every change: they all happen at the first step.
        var capped = await SqliteAccess.ExecuteAsync(Shop(), false, "UPDATE customers SET name = upper(name) RETURNING id", [], 1, 5, CancellationToken.None);
        Assert.Equal(4, capped.Changes);
        Assert.True(capped.Grids[0].More);
        Assert.Equal("0", Read(_shop, "SELECT count(*) FROM customers WHERE name <> upper(name)"));

        var ddl = await SqliteAccess.ExecuteAsync(Shop(), false, "CREATE TABLE notes (id INTEGER PRIMARY KEY, body TEXT)", [], 10, 5, CancellationToken.None);
        Assert.Equal((SqlOutcome.Ok, 0), (ddl.Outcome, ddl.Changes));
        Assert.Equal("1", Read(_shop, "SELECT count(*) FROM sqlite_schema WHERE name = 'notes'"));

        // No transaction of the app's: VACUUM and a lasting PRAGMA run, which SQLite refuses inside one.
        Assert.Equal(SqlOutcome.Ok, (await SqliteAccess.ExecuteAsync(Shop(), false, "PRAGMA user_version = 7", [], 10, 5, CancellationToken.None)).Outcome);
        Assert.Equal("7", Read(_shop, "PRAGMA user_version"));
        Assert.Equal(SqlOutcome.Ok, (await SqliteAccess.ExecuteAsync(Shop(), false, "VACUUM", [], 10, 5, CancellationToken.None)).Outcome);
        var pragma = await SqliteAccess.ExecuteAsync(Shop(), false, "PRAGMA user_version", [], 10, 5, CancellationToken.None);
        Assert.Equal("7", pragma.Grids[0].Rows[0][0]);
    }

    [Fact]
    public async Task Execute_AFailedStatement_ChangesNothing_AndTheTimeoutStillInterrupts()
    {
        var failed = await SqliteAccess.ExecuteAsync(Shop(), false, "INSERT INTO customers (id, name) VALUES (3, 'Linus'), (1, 'Dup')", [], 10, 5, CancellationToken.None);
        Assert.Equal(SqlOutcome.Failed, failed.Outcome);
        Assert.Contains("UNIQUE", failed.Detail);
        Assert.Equal("2", Read(_shop, "SELECT count(*) FROM customers"));

        var slow = await SqliteAccess.ExecuteAsync(Shop(), false, SqliteCheck.SlowQuery, [], 2, 1, CancellationToken.None);
        Assert.Equal(SqlOutcome.Timeout, slow.Outcome);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SqliteAccess.ExecuteAsync(Shop(), false, SqliteCheck.SlowQuery, [], 2, 30, cancel.Token));

        var gone = SqliteAccess.Execute(new SqliteTarget("gone", Path.Combine(_work, "gone.db")), false, "CREATE TABLE t (a)", [], 2, 5, CancellationToken.None);
        Assert.Equal(SqlOutcome.ConnectFailed, gone.Outcome);
        Assert.False(File.Exists(Path.Combine(_work, "gone.db")));
    }

    [Fact]
    public void Resolve_Create_ANewFileInTheWorkingDirectory_Only()
    {
        var access = Access();
        var made = access.Resolve("data/new.sqlite", null, create: true, out _, out bool creating)!;
        Assert.True(creating);
        Assert.Equal((Path.Combine(_work, "data", "new.sqlite"), "data/new.sqlite"), (made.FullPath, made.Name));

        // A file already there is opened, a sqlite.json name wins and is never made.
        Assert.False(access.Resolve("shop.db", null, create: true, out _, out creating) is null || creating);
        Assert.Equal("shop", access.Resolve("shop", null, create: true, out _, out creating)!.Name);
        Assert.False(creating);

        Assert.Null(access.Resolve(null, null, create: true, out var blank, out _));
        Assert.Equal(SqliteText.CreateNeedsPath, SqliteText.Error(blank!));
        Assert.Null(access.Resolve("notes.txt", null, create: true, out var extension, out _));
        Assert.Equal(SqliteText.BadExtension("notes.txt"), SqliteText.Error(extension!));
        Assert.Null(access.Resolve("data", null, create: true, out _, out _));
        Assert.Null(access.Resolve("nowhere/new.db", null, create: true, out var folder, out _));
        Assert.Equal(SqliteText.NoFolder("nowhere/new.db"), SqliteText.Error(folder!));
        Assert.Null(access.Resolve("../outside.db", null, create: true, out var outside, out _));
        Assert.Equal(SqlOutcome.ConnectFailed, outside!.Outcome);
        Assert.Null(access.Resolve("data/new.db", null, create: false, out _, out _));   // without create, a missing file is unknown

        _settings.SqliteSandboxFiles = false;
        Assert.Null(Access().Resolve("data/new.db", null, create: true, out var sandbox, out _));
        Assert.Equal(SqliteText.CreateNeedsSandbox, SqliteText.Error(sandbox!));
    }

    [Fact]
    public void Execute_Create_MakesTheFile_AndAFailedStatementTakesItAway()
    {
        string path = Path.Combine(_work, "data", "made.db");
        var run = SqliteAccess.Execute(new SqliteTarget("data/made.db", path), true, "CREATE TABLE t (a)", [], 10, 5, CancellationToken.None);
        Assert.Equal(SqlOutcome.Ok, run.Outcome);
        Assert.Equal("1", Read(path, "SELECT count(*) FROM sqlite_schema WHERE name = 't'"));

        string failed = Path.Combine(_work, "data", "failed.db");
        Assert.Equal(SqlOutcome.Failed, SqliteAccess.Execute(new SqliteTarget("data/failed.db", failed), true, "INSERT INTO nope VALUES (1)", [], 10, 5, CancellationToken.None).Outcome);
        Assert.False(File.Exists(failed));

        // An existing file a create named is never taken away, whatever its statement did.
        Assert.Equal(SqlOutcome.Failed, SqliteAccess.Execute(new SqliteTarget("data/made.db", path), true, "INSERT INTO nope VALUES (1)", [], 10, 5, CancellationToken.None).Outcome);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task TheTool_ChecksTheMode_TheGate_ThenAsks_ThenRuns_AndAudits()
    {
        var asked = new List<(string Path, string Sql, bool Creating)>();
        bool? answer = true;
        var tool = new SqliteExecuteTool(Access(), () => _settings, (target, sql, creating, _) =>
        {
            asked.Add((target.FullPath, sql, creating));
            return Task.FromResult(answer);
        });
        var audit = new List<string>();
        void Collect(DiagnosticEvent e) => audit.Add(e.Message);
        DiagnosticLog.Emitted += Collect;
        try
        {
            string done = await tool.RunAsync("INSERT INTO customers (name) VALUES (@n);", null, [new("n", "Linus")], null, false, CancellationToken.None);
            Assert.StartsWith("Changed 1 row in shop (", done);
            Assert.Equal([(_shop, "INSERT INTO customers (name) VALUES (@n)", false)], asked);
            Assert.Contains(SqliteText.AuditLogLine("shop", _shop, false, 1, "INSERT INTO customers (name) VALUES (@n)"), audit);

            string returned = await tool.RunAsync("DELETE FROM customers WHERE name = 'Linus' RETURNING name", "shop", [], null, false, CancellationToken.None);
            Assert.Contains("it returned 1 row", returned);
            Assert.Contains("| Linus |", returned);

            string created = (string)(await tool.InvokeAsync(new AIFunctionArguments { ["sql"] = "CREATE TABLE t (a)", ["database"] = "data/fresh.db", ["create"] = true }))!;
            Assert.StartsWith("Created data/fresh.db. Changed 0 rows in data/fresh.db", created);
            Assert.True(asked[^1].Creating);
            Assert.True(File.Exists(Path.Combine(_work, "data", "fresh.db")));

            asked.Clear();
            Assert.StartsWith("Error: the SQL uses ATTACH", await tool.RunAsync("ATTACH 'x.db' AS x", null, [], null, false, CancellationToken.None));
            Assert.Equal(NeonSidekick.Files.FileText.BadBoolean("create", "maybe"), (string)(await tool.InvokeAsync(new AIFunctionArguments { ["sql"] = "SELECT 1", ["create"] = "maybe" }))!);
            Assert.Empty(asked);   // a refused text never asks

            answer = false;
            Assert.Equal(SqliteText.Declined, await tool.RunAsync("DELETE FROM customers", null, [], null, false, CancellationToken.None));
            answer = null;
            Assert.Equal(SqliteText.NoPane, await tool.RunAsync("DELETE FROM customers", null, [], null, false, CancellationToken.None));
            Assert.Equal("2", Read(_shop, "SELECT count(*) FROM customers"));

            // Read at every call: back to read-only, nothing is asked or changed.
            asked.Clear();
            _settings.SqliteMode = "read-only";
            Assert.Equal(SqliteText.ReadOnlyMode, await tool.RunAsync("DELETE FROM customers", null, [], null, false, CancellationToken.None));
            Assert.Empty(asked);
        }
        finally
        {
            DiagnosticLog.Emitted -= Collect;
        }

        _settings.SqliteMode = "read-write";
        Assert.Equal(SqliteText.NoPane, await new SqliteExecuteTool(Access(), () => _settings, allow: null).RunAsync("DELETE FROM customers", null, [], null, false, CancellationToken.None));
    }

    [Fact]
    public async Task TheTool_RunsOnlyTheTickedKinds_AndCreateNeedsCreating()
    {
        int asked = 0;
        var tool = new SqliteExecuteTool(Access(), () => _settings, (_, _, _, _) =>
        {
            asked++;
            return Task.FromResult<bool?>(true);
        });
        _settings.SqliteStatementsAllowed = ["data"];
        Assert.StartsWith("Error: the SQL is dropping", await tool.RunAsync("DROP TABLE customers", null, [], null, false, CancellationToken.None));
        Assert.Equal(SqliteText.CreateNotAllowed, await tool.RunAsync("INSERT INTO t VALUES (1)", "data/new.db", [], null, true, CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(_work, "data", "new.db")));
        Assert.Equal(0, asked);   // refused before asking
        // DELETE is deleting, its own kind since later on 2026-10-05: not ticked here, so refused before asking.
        Assert.Equal(
            "Error: the SQL is deleting (DELETE), which the user has not allowed; sqlite_execute may run changing data — the user ticks more in SQLite statements allowed on the SQLite tab of /tools",
            await tool.RunAsync("DELETE FROM customers WHERE id = 1", null, [], null, false, CancellationToken.None));
        Assert.Equal(0, asked);
        Assert.StartsWith("Changed 1 row", await tool.RunAsync("UPDATE customers SET name = 'Ada L.' WHERE id = 1", null, [], null, false, CancellationToken.None));
        Assert.Equal(1, asked);

        _settings.SqliteStatementsAllowed = [];
        Assert.Equal(SqliteText.NoKindsAllowed, await tool.RunAsync("DELETE FROM customers", null, [], null, false, CancellationToken.None));
        Assert.Equal("2", Read(_shop, "SELECT count(*) FROM customers"));
        Assert.Equal("Ada L.", Read(_shop, "SELECT name FROM customers WHERE id = 1"));
        Assert.Equal(1, asked);
    }

    [Fact]
    public async Task TheTool_ARead_AsksNothing_AndRunsReadOnly()
    {
        int asked = 0;
        var tool = new SqliteExecuteTool(Access(), () => _settings, (_, _, _, _) =>
        {
            asked++;
            return Task.FromResult<bool?>(false);
        });

        string rows = await tool.RunAsync("WITH c AS (SELECT name FROM customers) SELECT * FROM c ORDER BY name", null, [], null, false, CancellationToken.None);
        Assert.StartsWith("2 rows × 1 column from shop (", rows);
        Assert.Contains("| Ada |", rows);
        Assert.StartsWith("1 row", await tool.RunAsync("EXPLAIN QUERY PLAN SELECT * FROM customers WHERE id = @id", null, [new("id", 1L)], null, false, CancellationToken.None));
        Assert.Equal(0, asked);

        // Not ticked, a read is refused like any kind; and a change still asks (and is declined here).
        Assert.Equal(SqliteText.Declined, await tool.RunAsync("DELETE FROM customers", null, [], null, false, CancellationToken.None));
        Assert.Equal(1, asked);
        _settings.SqliteStatementsAllowed = ["data"];
        Assert.StartsWith("Error: the SQL is reading", await tool.RunAsync("SELECT 1", null, [], null, false, CancellationToken.None));
        Assert.Equal("2", Read(_shop, "SELECT count(*) FROM customers"));
    }

    [Fact]
    public void TheTurn_OffersExecute_OnlyUnderReadWrite_WithAPane()
    {
        var all = ChatScreen.SqliteTools(Access(), () => _settings, allow: null);
        Assert.Contains(SqliteExecuteTool.ToolName, ChatScreen.SqliteToolsFor(all, _settings, pane: true).Select(t => t.Name));
        Assert.DoesNotContain(SqliteExecuteTool.ToolName, ChatScreen.SqliteToolsFor(all, _settings, pane: false).Select(t => t.Name));
        _settings.SqliteStatementsAllowed = [];
        Assert.DoesNotContain(SqliteExecuteTool.ToolName, ChatScreen.SqliteToolsFor(all, _settings, pane: true).Select(t => t.Name));   // no kind ticked
        _settings.SqliteStatementsAllowed = ["data"];
        _settings.SqliteMode = "read-only";
        Assert.Equal([SqliteDatabasesTool.ToolName, SqliteTablesTool.ToolName, SqliteDescribeTool.ToolName, SqliteQueryTool.ToolName], ChatScreen.SqliteToolsFor(all, _settings, pane: true).Select(t => t.Name));
    }

    [Fact]
    public void TheWording()
    {
        string caption = SqliteText.AllowCaption(new SqliteTarget("shop", "C:\\w\\shop.db"), " DELETE FROM t ", creating: false);
        Assert.Equal("The model wants to change shop (C:\\w\\shop.db):\nDELETE FROM t", caption);
        Assert.StartsWith("The model wants to create the new database C:\\w\\a.db and run:\n", SqliteText.AllowCaption(new SqliteTarget("a.db", "C:\\w\\a.db"), "CREATE TABLE t (a)", creating: true));
        Assert.Equal("shop (C:\\w\\shop.db) created: 2 rows changed by INSERT …", SqliteText.AuditLogLine("shop", "C:\\w\\shop.db", true, 2, "INSERT …"));
        var run = new SqlRun(SqlOutcome.Ok, "", "shop", "", [new SqlGrid([], [], false)], TimeSpan.FromMilliseconds(4)) { Changes = 3 };
        Assert.Equal("Changed 3 rows in shop (4 ms)", SqliteText.Executed(run, false, 10, 1000));
    }
}
