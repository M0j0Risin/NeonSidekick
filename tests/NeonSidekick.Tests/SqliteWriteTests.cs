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

    [Fact]
    public void TheMode_ParsesBothWords_AndFallsBackToReadOnly()
    {
        Assert.Equal("read-only", SqliteProtectionMode.Default);
        Assert.Equal("read-only", new AppSettingsData().SqliteProtectionMode);
        Assert.Equal(SqliteProtection.ReadWrite, SqliteProtectionMode.Resolve(new AppSettingsData { SqliteProtectionMode = " Read-Write " }));
        Assert.Equal(SqliteProtection.ReadOnly, SqliteProtectionMode.Resolve(new AppSettingsData { SqliteProtectionMode = "read-only" }));
        Assert.Equal(SqliteProtection.ReadOnly, SqliteProtectionMode.Resolve(new AppSettingsData { SqliteProtectionMode = "yolo" }));
        Assert.All(SqliteProtectionMode.Names, n => Assert.NotEmpty(SqliteProtectionMode.Describe(n)));
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
    private readonly AppSettingsData _settings = new() { SqliteTools = true, SqliteProtectionMode = "read-write", SqliteDatabasesOffered = ["shop"] };
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
            _settings.SqliteProtectionMode = "read-only";
            Assert.Equal(SqliteText.ReadOnlyMode, await tool.RunAsync("DELETE FROM customers", null, [], null, false, CancellationToken.None));
            Assert.Empty(asked);
        }
        finally
        {
            DiagnosticLog.Emitted -= Collect;
        }

        _settings.SqliteProtectionMode = "read-write";
        Assert.Equal(SqliteText.NoPane, await new SqliteExecuteTool(Access(), () => _settings, allow: null).RunAsync("DELETE FROM customers", null, [], null, false, CancellationToken.None));
    }

    [Fact]
    public void TheTurn_OffersExecute_OnlyUnderReadWrite_WithAPane()
    {
        var all = ChatScreen.SqliteTools(Access(), () => _settings, allow: null);
        Assert.Contains(SqliteExecuteTool.ToolName, ChatScreen.SqliteToolsFor(all, _settings, pane: true).Select(t => t.Name));
        Assert.DoesNotContain(SqliteExecuteTool.ToolName, ChatScreen.SqliteToolsFor(all, _settings, pane: false).Select(t => t.Name));
        _settings.SqliteProtectionMode = "read-only";
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
