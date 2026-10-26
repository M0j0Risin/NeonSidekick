using DuckDB.NET.Data;
using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Diagnostics;
using NeonSidekick.DuckDb;
using NeonSidekick.Files;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Settings;
using NeonSidekick.Sql;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary><c>duckdb_execute</c>'s gate (2026-10-25): one statement of a kind the user ticked, never one that reaches past the database.</summary>
public sealed class DuckDbWriteGateTests
{
    [Theory]
    [InlineData("INSERT INTO t VALUES (1)")]
    [InlineData("UPDATE t SET a = 2 WHERE a = 1")]
    [InlineData("DELETE FROM t WHERE a = 1")]
    [InlineData("CREATE TABLE t2 AS SELECT * FROM 'data/sales.csv'")]
    [InlineData("CREATE OR REPLACE VIEW v AS SELECT 1")]
    [InlineData("ALTER TABLE t ADD COLUMN b INTEGER")]
    [InlineData("DROP TABLE t")]
    [InlineData("CHECKPOINT")]
    [InlineData("PRAGMA enable_progress_bar")]
    [InlineData("WITH c AS (SELECT 1 AS a) INSERT INTO t SELECT a FROM c")]
    [InlineData("SELECT nextval('s')")]
    [InlineData("INSERT INTO t SELECT * FROM read_csv('data/x.csv')")]
    public void Allows_OneStatement(string sql) => Assert.Null(DuckDbWriteGate.Check(sql));

    [Theory]
    [InlineData("INSERT INTO t VALUES (1); DROP TABLE t", "2 statements")]
    [InlineData("BEGIN TRANSACTION", "BEGIN")]
    [InlineData("COMMIT", "COMMIT")]
    [InlineData("SET memory_limit = '100GB'", "SET")]
    [InlineData("RESET enable_external_access", "RESET")]
    [InlineData("ATTACH 'other.duckdb' AS o", "ATTACH")]
    [InlineData("DETACH o", "DETACH")]
    [InlineData("USE o", "USE")]
    [InlineData("COPY t TO 'out.csv'", "COPY")]
    [InlineData("EXPORT DATABASE 'dump'", "EXPORT")]
    [InlineData("IMPORT DATABASE 'dump'", "IMPORT")]
    [InlineData("INSTALL httpfs", "INSTALL")]
    [InlineData("LOAD httpfs", "LOAD")]
    [InlineData("PREPARE p AS SELECT 1", "PREPARE")]
    [InlineData("CALL checkpoint()", "CALL")]
    [InlineData("CREATE SECRET s (TYPE s3)", "CREATE SECRET")]
    [InlineData("CREATE PERSISTENT SECRET s (TYPE s3)", "CREATE SECRET")]
    [InlineData("UPDATE EXTENSIONS", "UPDATE EXTENSIONS")]
    [InlineData("EXPLAIN ANALYZE DELETE FROM t", "EXPLAIN")]
    [InlineData("INSERT INTO t SELECT * FROM query('SELECT 1')", "query")]
    [InlineData("INSERT INTO t SELECT getenv('PATH')", "getenv")]
    [InlineData("INSERT INTO t SELECT * FROM glob('*')", "glob")]
    [InlineData("GRANT SELECT ON t TO x", "GRANT")]
    [InlineData("   ", "give the statement")]
    public void Refuses_TheRest_WithTheSentence(string sql, string part)
    {
        string? refused = DuckDbWriteGate.Check(sql);
        Assert.NotNull(refused);
        Assert.StartsWith("Error: ", refused);
        Assert.Contains(part, refused, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("INSERT INTO t VALUES (1)", DuckDbStatementKind.Data)]
    [InlineData("MERGE INTO t USING s ON t.a = s.a WHEN MATCHED THEN UPDATE SET b = s.b", DuckDbStatementKind.Data)]
    [InlineData("TRUNCATE t", DuckDbStatementKind.Delete)]
    [InlineData("CREATE INDEX i ON t (a)", DuckDbStatementKind.Create)]
    [InlineData("COMMENT ON TABLE t IS 'x'", DuckDbStatementKind.Alter)]
    [InlineData("DROP VIEW v", DuckDbStatementKind.Drop)]
    [InlineData("VACUUM", DuckDbStatementKind.Upkeep)]
    [InlineData("FORCE CHECKPOINT", DuckDbStatementKind.Upkeep)]
    [InlineData("PRAGMA version", DuckDbStatementKind.Pragma)]
    [InlineData("WITH c AS (SELECT 1) DELETE FROM t", DuckDbStatementKind.Delete)]
    [InlineData("WITH RECURSIVE c(n) AS MATERIALIZED (SELECT 1) SELECT * FROM c", DuckDbStatementKind.Read)]
    [InlineData("EXPLAIN SELECT 1", DuckDbStatementKind.Read)]
    [InlineData("SUMMARIZE t", DuckDbStatementKind.Read)]
    public void Classify_ByTheStatement(string sql, DuckDbStatementKind kind) => Assert.Equal(kind, DuckDbWriteGate.Classify(sql));

    [Fact]
    public void Check_RefusesAKindNotTicked_AndTheExtraKindsAReplaceNeeds()
    {
        var defaults = DuckDbStatementKinds.Resolve((IReadOnlyList<string>?)null);
        Assert.Equal([DuckDbStatementKind.Data, DuckDbStatementKind.Create, DuckDbStatementKind.Read], defaults);
        Assert.Equal(DuckDbText.KindNotAllowed(DuckDbStatementKind.Delete, defaults), DuckDbWriteGate.Check("DELETE FROM t", defaults));
        Assert.Equal(DuckDbText.KindNotAllowed(DuckDbStatementKind.Delete, defaults), DuckDbWriteGate.Check("INSERT OR REPLACE INTO t VALUES (1)", defaults));
        Assert.Equal(DuckDbText.KindNotAllowed(DuckDbStatementKind.Delete, defaults), DuckDbWriteGate.Check("INSERT INTO t VALUES (1) ON CONFLICT DO UPDATE SET a = 2", defaults));
        Assert.Equal(DuckDbText.KindNotAllowed(DuckDbStatementKind.Drop, defaults), DuckDbWriteGate.Check("CREATE OR REPLACE TABLE t (a INTEGER)", defaults));
        Assert.Null(DuckDbWriteGate.Check("INSERT INTO t SELECT replace(a, 'x', 'y') FROM s", defaults));   // a function's replace()
        Assert.Equal(
            "Error: the SQL is deleting (DELETE, TRUNCATE, INSERT OR REPLACE, ON CONFLICT DO UPDATE), which the user has not allowed; duckdb_execute may run changing data — the user ticks more in DuckDB statements allowed on /tools › Databases › DuckDB",
            DuckDbText.KindNotAllowed(DuckDbStatementKind.Delete, [DuckDbStatementKind.Data]));
    }

    [Fact]
    public void TheKinds_AndTheMode_ParseCaseBlind_AndFallBack()
    {
        Assert.Equal(["data", "create", "read"], new AppSettingsData().DuckDbStatementsAllowed);
        Assert.Equal([DuckDbStatementKind.Data, DuckDbStatementKind.Drop], DuckDbStatementKinds.Resolve(["DROP", " data ", "nonsense"]));
        Assert.Empty(DuckDbStatementKinds.Resolve(new List<string>()));
        Assert.Equal(8, DuckDbStatementKinds.Names.Length);

        Assert.True(DuckDbModes.TryParse("Read-Write", out var mode));
        Assert.Equal(DuckDbMode.ReadWrite, mode);
        Assert.False(DuckDbModes.TryParse("sometimes", out _));
        Assert.Equal(DuckDbMode.ReadOnly, DuckDbModes.Resolve(new AppSettingsData()));
        Assert.Equal(DuckDbMode.ReadOnly, DuckDbModes.Resolve(new AppSettingsData { DuckDbMode = "sometimes" }));
    }

    [Fact]
    public void TheRules_CarryTheWriteSentence_OnlyWithTheTool()
    {
        Assert.Contains(Assistant.DuckDbWriteRule, Assistant.DefaultRules(false, tools: true, duckDb: true, duckDbWrite: true));
        Assert.DoesNotContain(Assistant.DuckDbWriteRule, Assistant.DefaultRules(false, tools: true, duckDb: false, duckDbWrite: true));
        Assert.Contains("COPY", Assistant.DuckDbWriteRule);
    }
}

/// <summary><c>duckdb_execute</c> over real files (2026-10-25): it commits, asks first, creates, and takes a failed create's file away.</summary>
public sealed class DuckDbExecuteTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("neon-duckdb-write-").FullName;
    private readonly ManualTimeProvider _time = new();
    private readonly AppSettingsData _settings = new() { DuckDbTools = true, DuckDbSandboxFiles = true, DuckDbMode = "read-write", DuckDbDatabasesOffered = ["shop"], DuckDbStatementsAllowed = [.. DuckDbStatementKinds.Names] };
    private readonly string _profile;
    private readonly string _work;
    private readonly string _shop;
    private readonly IDisposable _cache = NeonSidekick.Computers.ConfigCache.Scope();

    public DuckDbExecuteTests()
    {
        string home = Path.Combine(_dir, "home");
        _profile = Path.Combine(home, "profiles", "default");
        _work = Path.Combine(_dir, "work");
        _shop = Path.Combine(_work, "shop.duckdb");
        Directory.CreateDirectory(Profiles.ConfigsDirectory(_profile));
        Directory.CreateDirectory(Path.Combine(_work, "data"));
        DuckDbToolsTests.Make(_shop, "CREATE SEQUENCE ids START 3; CREATE TABLE customers (id INTEGER PRIMARY KEY DEFAULT nextval('ids'), name VARCHAR NOT NULL); INSERT INTO customers VALUES (1, 'Ada'), (2, 'Grace');");
        File.WriteAllText(Path.Combine(_work, "data", "sales.csv"), "region,amount\nnorth,10\nsouth,32\n");
        File.WriteAllText(DuckDbConfigFile.ProfilePath(_profile), """{ "databases": { "shop": { "path": "../../../../work/shop.duckdb" } } }""");
    }

    public void Dispose()
    {
        _cache.Dispose();
        Junction.DeleteTree(_dir);
    }

    private static string Read(string path, string sql)
    {
        var builder = new DuckDBConnectionStringBuilder { DataSource = path };
        builder["access_mode"] = "READ_ONLY";
        using var connection = new DuckDBConnection(builder.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? "";
    }

    private DuckDbAccess Access() => new(
        () => DuckDbConfigFile.LoadCatalog(_profile, Path.Combine(_dir, "home")).Offered(_settings.DuckDbDatabasesOffered),
        () => _settings.DuckDbSandboxFiles ? new WorkingDirectory(() => _work, _time) : null);

    private DuckDbTarget Shop() => new("shop", _shop);

    private static DuckDbPrepared P(string sql) => DuckDbPrepared.AsWritten(sql);

    [Fact]
    public async Task Execute_Commits_CountsTheChanges_AndReturnsTheRowsOfARETURNING()
    {
        var insert = await DuckDbAccess.ExecuteAsync(Shop(), false, P("INSERT INTO customers (name) VALUES ($a), ($b) RETURNING id, name"), [new("a", "Linus"), new("b", "Barbara")], 10, 5, null, CancellationToken.None);
        Assert.Equal(SqlOutcome.Ok, insert.Outcome);
        Assert.Equal(2, insert.Changes);
        Assert.Equal([["3", "Linus"], ["4", "Barbara"]], insert.Grids[0].Rows);
        Assert.Equal("4", Read(_shop, "SELECT count(*) FROM customers"));

        var update = await DuckDbAccess.ExecuteAsync(Shop(), false, P("UPDATE customers SET name = upper(name)"), [], 10, 5, null, CancellationToken.None);
        Assert.Equal(4, update.Changes);

        var ddl = await DuckDbAccess.ExecuteAsync(Shop(), false, P("CREATE TABLE notes (id INTEGER, body VARCHAR)"), [], 10, 5, null, CancellationToken.None);
        Assert.Equal(SqlOutcome.Ok, ddl.Outcome);
        Assert.Equal("1", Read(_shop, "SELECT count(*) FROM duckdb_tables() WHERE table_name = 'notes'"));
        Assert.Equal(SqlOutcome.Ok, (await DuckDbAccess.ExecuteAsync(Shop(), false, P("CHECKPOINT"), [], 10, 5, null, CancellationToken.None)).Outcome);

        // A data file loaded into a table, from the working directory alone.
        var load = await DuckDbAccess.ExecuteAsync(Shop(), false, P("CREATE TABLE sales AS SELECT * FROM read_csv('" + Path.Combine(_work, "data", "sales.csv") + "')"), [], 10, 5, _work, CancellationToken.None);
        Assert.Equal(SqlOutcome.Ok, load.Outcome);
        Assert.Equal("42", Read(_shop, "SELECT sum(amount) FROM sales"));
    }

    [Fact]
    public async Task Execute_AFailedOrStoppedStatement_ChangesNothing_AndTheFileIsLetGo()
    {
        var failed = await DuckDbAccess.ExecuteAsync(Shop(), false, P("INSERT INTO customers VALUES (5, 'Linus'), (1, 'Dup')"), [], 10, 5, null, CancellationToken.None);
        Assert.Equal(SqlOutcome.Failed, failed.Outcome);
        Assert.Equal("2", Read(_shop, "SELECT count(*) FROM customers"));

        const string slowWrite = "INSERT INTO customers SELECT 100 + (a.range * 100000 + b.range), 'x' FROM range(100000) a, range(100000) b WHERE hash(a.range + b.range) = 0";
        Assert.Equal(SqlOutcome.Timeout, (await DuckDbAccess.ExecuteAsync(Shop(), false, P(slowWrite), [], 2, 1, null, CancellationToken.None)).Outcome);
        using (var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(300)))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DuckDbAccess.ExecuteAsync(Shop(), false, P(slowWrite), [], 2, 60, null, stop.Token));
        }

        Assert.Equal("2", Read(_shop, "SELECT count(*) FROM customers"));
        using (new FileStream(_shop, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
        }

        Assert.Equal(SqlOutcome.UnknownConnection, DuckDbAccess.Execute(DuckDbTarget.Memory, false, P("CREATE TABLE t (a INTEGER)"), [], 2, 5, null, CancellationToken.None).Outcome);
        var gone = DuckDbAccess.Execute(new DuckDbTarget("gone", Path.Combine(_work, "gone.duckdb")), false, P("CREATE TABLE t (a INTEGER)"), [], 2, 5, null, CancellationToken.None);
        Assert.Equal(SqlOutcome.ConnectFailed, gone.Outcome);
        Assert.False(File.Exists(Path.Combine(_work, "gone.duckdb")));
    }

    [Fact]
    public void Execute_Create_MakesTheFile_AndAFailedStatementTakesItAndItsLogAway()
    {
        string path = Path.Combine(_work, "data", "made.duckdb");
        var run = DuckDbAccess.Execute(new DuckDbTarget("data/made.duckdb", path), true, P("CREATE TABLE t (a INTEGER)"), [], 10, 5, null, CancellationToken.None);
        Assert.Equal(SqlOutcome.Ok, run.Outcome);
        Assert.Equal("1", Read(path, "SELECT count(*) FROM duckdb_tables() WHERE table_name = 't'"));

        string failed = Path.Combine(_work, "data", "failed.duckdb");
        Assert.Equal(SqlOutcome.Failed, DuckDbAccess.Execute(new DuckDbTarget("data/failed.duckdb", failed), true, P("INSERT INTO nope VALUES (1)"), [], 10, 5, null, CancellationToken.None).Outcome);
        Assert.False(File.Exists(failed));
        Assert.False(File.Exists(failed + ".wal"));

        Assert.Equal(SqlOutcome.Failed, DuckDbAccess.Execute(new DuckDbTarget("data/made.duckdb", path), true, P("INSERT INTO nope VALUES (1)"), [], 10, 5, null, CancellationToken.None).Outcome);
        Assert.True(File.Exists(path));
    }

    /// <summary>A view stored with an absolute path outside the working directory is held by DuckDB's own folder rule when it is read.</summary>
    [Fact]
    public async Task AStoredViewReachingOut_IsRefusedByTheEngine_WhenRead()
    {
        string outside = Path.Combine(_dir, "outside.csv");
        // A value no temp path holds (2026-10-25, the Mac pass: "42" was in this Mac's TMPDIR, /var/folders/jm/cr7tmbwd2gn42…, which the refusal quotes).
        File.WriteAllText(outside, "secret\nleaked-cell\n");
        DuckDbToolsTests.Make(Path.Combine(_work, "views.duckdb"), "CREATE VIEW leak AS SELECT * FROM read_csv('" + outside.Replace("'", "''", StringComparison.Ordinal) + "');");
        string answer = await new DuckDbQueryTool(Access(), () => _settings).RunAsync("SELECT * FROM leak", "views.duckdb", [], null, CancellationToken.None);
        Assert.StartsWith("Error: ", answer);
        Assert.DoesNotContain("leaked-cell", answer);
    }

    [Fact]
    public async Task TheTool_ChecksTheMode_TheGate_ThenAsks_ThenRuns_AndAudits()
    {
        var asked = new List<(string Path, string Sql, bool Creating)>();
        bool? answer = true;
        var tool = new DuckDbExecuteTool(Access(), () => _settings, (target, sql, creating, _, _) =>
        {
            asked.Add((target.FullPath, sql, creating));
            return Task.FromResult(answer);
        });
        var audit = new List<string>();
        void Collect(DiagnosticEvent e) => audit.Add(e.Message);
        DiagnosticLog.Emitted += Collect;
        try
        {
            string done = await tool.RunAsync("INSERT INTO customers (name) VALUES ($n);", null, [new("n", "Linus")], null, false, CancellationToken.None);
            Assert.StartsWith("Changed 1 row in shop (", done);
            Assert.Equal([(_shop, "INSERT INTO customers (name) VALUES ($n)", false)], asked);
            Assert.Contains(audit, line => line.StartsWith("shop (" + _shop + "): ", StringComparison.Ordinal) && line.EndsWith("by INSERT INTO customers (name) VALUES ($n)", StringComparison.Ordinal));

            string returned = await tool.RunAsync("DELETE FROM customers WHERE name = 'Linus' RETURNING name", "shop", [], null, false, CancellationToken.None);
            Assert.Contains("it returned 1 row", returned);
            Assert.Contains("| Linus |", returned);

            string created = (string)(await tool.InvokeAsync(new AIFunctionArguments { ["sql"] = "CREATE TABLE t AS SELECT * FROM 'data/sales.csv'", ["database"] = "data/fresh.duckdb", ["create"] = true }))!;
            Assert.StartsWith("Created data/fresh.duckdb. Ran it in data/fresh.duckdb", created);
            Assert.True(asked[^1].Creating);
            Assert.Equal("42", Read(Path.Combine(_work, "data", "fresh.duckdb"), "SELECT sum(amount) FROM t"));

            asked.Clear();
            Assert.StartsWith("Error: the SQL uses ATTACH", await tool.RunAsync("ATTACH 'x.duckdb' AS x", null, [], null, false, CancellationToken.None));
            Assert.StartsWith("Error: ", await tool.RunAsync("INSERT INTO customers SELECT 9, secret FROM '../outside.csv'", null, [], null, false, CancellationToken.None));
            Assert.Equal(DuckDbText.MemoryIsGone, await tool.RunAsync("CREATE TABLE t (a INTEGER)", ":memory:", [], null, false, CancellationToken.None));
            Assert.Equal(FileText.BadBoolean("create", "maybe"), (string)(await tool.InvokeAsync(new AIFunctionArguments { ["sql"] = "SELECT 1", ["create"] = "maybe" }))!);
            Assert.Empty(asked);

            answer = false;
            Assert.Equal(DuckDbText.Declined, await tool.RunAsync("DELETE FROM customers", null, [], null, false, CancellationToken.None));
            answer = null;
            Assert.Equal(DuckDbText.NoPane, await tool.RunAsync("DELETE FROM customers", null, [], null, false, CancellationToken.None));
            Assert.Equal("2", Read(_shop, "SELECT count(*) FROM customers"));

            asked.Clear();
            _settings.DuckDbMode = "read-only";
            Assert.Equal(DuckDbText.ReadOnlyMode, await tool.RunAsync("DELETE FROM customers", null, [], null, false, CancellationToken.None));
            Assert.Empty(asked);
        }
        finally
        {
            DiagnosticLog.Emitted -= Collect;
        }

        _settings.DuckDbMode = "read-write";
        Assert.Equal(DuckDbText.NoPane, await new DuckDbExecuteTool(Access(), () => _settings, allow: null).RunAsync("DELETE FROM customers", null, [], null, false, CancellationToken.None));
    }

    [Fact]
    public async Task TheTool_RunsOnlyTheTickedKinds_AndCreateNeedsCreating()
    {
        int asked = 0;
        var tool = new DuckDbExecuteTool(Access(), () => _settings, (_, _, _, _, _) =>
        {
            asked++;
            return Task.FromResult<bool?>(true);
        });
        _settings.DuckDbStatementsAllowed = ["data"];
        Assert.StartsWith("Error: the SQL is dropping", await tool.RunAsync("DROP TABLE customers", null, [], null, false, CancellationToken.None));
        Assert.Equal(DuckDbText.CreateNotAllowed, await tool.RunAsync("INSERT INTO t VALUES (1)", "data/new.duckdb", [], null, true, CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(_work, "data", "new.duckdb")));
        Assert.StartsWith("Error: the SQL is deleting", await tool.RunAsync("DELETE FROM customers WHERE id = 1", null, [], null, false, CancellationToken.None));
        Assert.Equal(0, asked);
        Assert.StartsWith("Changed 1 row", await tool.RunAsync("UPDATE customers SET name = 'Ada L.' WHERE id = 1", null, [], null, false, CancellationToken.None));
        Assert.Equal(1, asked);

        _settings.DuckDbStatementsAllowed = [];
        Assert.Equal(DuckDbText.NoKindsAllowed, await tool.RunAsync("DELETE FROM customers", null, [], null, false, CancellationToken.None));
        Assert.Equal("Ada L.", Read(_shop, "SELECT name FROM customers WHERE id = 1"));
        Assert.Equal(1, asked);
    }

    [Fact]
    public async Task TheTool_ARead_AsksNothing_AndRunsReadOnly()
    {
        int asked = 0;
        var tool = new DuckDbExecuteTool(Access(), () => _settings, (_, _, _, _, _) =>
        {
            asked++;
            return Task.FromResult<bool?>(false);
        });

        string rows = await tool.RunAsync("WITH c AS (SELECT name FROM customers) SELECT * FROM c ORDER BY name", null, [], null, false, CancellationToken.None);
        Assert.StartsWith("2 rows × 1 column from shop (", rows);
        Assert.Contains("| Ada |", rows);
        Assert.Equal(0, asked);

        Assert.Equal(DuckDbText.Declined, await tool.RunAsync("DELETE FROM customers", null, [], null, false, CancellationToken.None));
        Assert.Equal(1, asked);
        _settings.DuckDbStatementsAllowed = ["data"];
        Assert.StartsWith("Error: the SQL is reading", await tool.RunAsync("SELECT 1", null, [], null, false, CancellationToken.None));
        Assert.Equal("2", Read(_shop, "SELECT count(*) FROM customers"));
    }

    [Fact]
    public void TheTurn_OffersExecute_OnlyUnderReadWrite_WithAPane()
    {
        var all = ChatScreen.DuckDbTools(Access(), () => _settings, allow: null);
        Assert.Contains(DuckDbExecuteTool.ToolName, ChatScreen.DuckDbToolsFor(all, _settings, pane: true).Select(t => t.Name));
        Assert.DoesNotContain(DuckDbExecuteTool.ToolName, ChatScreen.DuckDbToolsFor(all, _settings, pane: false).Select(t => t.Name));
        _settings.DuckDbStatementsAllowed = [];
        Assert.DoesNotContain(DuckDbExecuteTool.ToolName, ChatScreen.DuckDbToolsFor(all, _settings, pane: true).Select(t => t.Name));
        _settings.DuckDbStatementsAllowed = ["data"];
        _settings.DuckDbMode = "read-only";
        Assert.Equal([DuckDbDatabasesTool.ToolName, DuckDbTablesTool.ToolName, DuckDbDescribeTool.ToolName, DuckDbQueryTool.ToolName], ChatScreen.DuckDbToolsFor(all, _settings, pane: true).Select(t => t.Name));
    }

    [Fact]
    public void TheWording()
    {
        string caption = DuckDbText.AllowCaption(new DuckDbTarget("shop", "C:\\w\\shop.duckdb"), " DELETE FROM t ", creating: false);
        Assert.Equal("The model wants to change shop (C:\\w\\shop.duckdb):\nDELETE FROM t", caption);
        Assert.StartsWith("The model wants to create the new database C:\\w\\a.duckdb and run:\n", DuckDbText.AllowCaption(new DuckDbTarget("a.duckdb", "C:\\w\\a.duckdb"), "CREATE TABLE t (a INTEGER)", creating: true));
        var run = new SqlRun(SqlOutcome.Ok, "", "shop", "", [new SqlGrid([], [], false)], TimeSpan.FromMilliseconds(4)) { Changes = 3 };
        Assert.Equal("Changed 3 rows in shop (4 ms)", DuckDbText.Executed(run, false, 10, 1000));
        Assert.Equal("Change a DuckDB database?", DuckDbText.AllowTitle);
    }
}
