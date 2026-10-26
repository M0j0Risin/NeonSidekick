using DuckDB.NET.Data;
using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.DuckDb;
using NeonSidekick.Files;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Plans;
using NeonSidekick.Settings;
using NeonSidekick.Sql;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>The DuckDB read gate (2026-10-25): one read-only statement by DuckDB's lexing, and every place it would read a file.</summary>
public sealed class DuckDbReadOnlyGateTests
{
    [Theory]
    [InlineData("SELECT 1")]
    [InlineData("select * from t where name = 'insert into x'  ;  ")]
    [InlineData("WITH c AS (SELECT 1 AS x) SELECT x FROM c")]
    [InlineData("VALUES (1, 2)")]
    [InlineData("FROM t")]
    [InlineData("TABLE t")]
    [InlineData("DESCRIBE t")]
    [InlineData("SHOW TABLES")]
    [InlineData("SUMMARIZE t")]
    [InlineData("PIVOT t ON k USING sum(v)")]
    [InlineData("EXPLAIN SELECT 1")]
    [InlineData("EXPLAIN ANALYZE SELECT 1")]
    [InlineData("SELECT \"update\", \"delete\" FROM t -- a DROP in a comment\n")]
    [InlineData("SELECT 1 /* a /* nested */ DROP */")]
    [InlineData("SELECT 6 // 4, a # 2 FROM t")]
    [InlineData("SELECT $$it's DROP$$, $tag$x$tag$, E'a\\'b'")]
    [InlineData("SELECT * FROM t WHERE id = $id")]
    [InlineData("SELECT * FROM range(10)")]
    [InlineData("SELECT * FROM duckdb_tables()")]
    [InlineData("SELECT * FROM main.\"sales.csv\"")]
    [InlineData("SELECT * FROM my_macro(1)")]
    public void Allows_OneRead(string sql) => Assert.Null(DuckDbReadOnlyGate.Check(sql));

    [Theory]
    [InlineData("INSERT INTO t VALUES (1)", "the SQL starts with INSERT")]
    [InlineData("SELECT 1; SELECT 2", "the SQL is 2 statements")]
    [InlineData("WITH c AS (SELECT 1) INSERT INTO t SELECT * FROM c", "INSERT")]
    [InlineData("SELECT 1 FROM t RETURNING id", "RETURNING")]
    [InlineData("ATTACH 'x.duckdb' AS x", "ATTACH")]
    [InlineData("COPY t TO 'x.csv'", "COPY")]
    [InlineData("EXPORT DATABASE 'x'", "EXPORT")]
    [InlineData("INSTALL httpfs", "INSTALL")]
    [InlineData("LOAD httpfs", "LOAD")]
    [InlineData("SET enable_external_access = true", "SET")]
    [InlineData("PRAGMA version", "PRAGMA")]
    [InlineData("CALL checkpoint()", "CALL")]
    [InlineData("EXPLAIN CREATE TABLE x (a INT)", "CREATE")]
    [InlineData("SELECT * FROM query('SELECT 1')", "query")]
    [InlineData("SELECT * FROM glob('*')", "glob")]
    [InlineData("SELECT * FROM read_duckdb('x.duckdb')", "read_duckdb")]
    [InlineData("SELECT * FROM duckdb_secrets()", "duckdb_secrets")]
    [InlineData("SELECT * FROM force_checkpoint()", "force_checkpoint")]
    [InlineData("SELECT nextval('s')", "nextval")]
    [InlineData("SELECT getenv('PATH')", "getenv")]
    [InlineData("SELECT * FROM read_csv(name)", "read_csv() takes its file as text")]
    [InlineData("SELECT * FROM t WHERE id = ?", "positional placeholder")]
    [InlineData("SELECT * FROM t WHERE id = $1", "positional placeholder")]
    [InlineData("SELECT 'never ends", "never ends")]
    [InlineData("SELECT 1 /* never ends", "never ends")]
    [InlineData("SELECT 1 /* /* nested */ never ends", "never ends")]
    [InlineData("   ", "give the SELECT")]
    public void Refuses_TheRest_WithTheSentence(string sql, string part)
    {
        string? refused = DuckDbReadOnlyGate.Check(sql);
        Assert.NotNull(refused);
        Assert.StartsWith("Error: ", refused);
        Assert.Contains(part, refused, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Inspect_HandsOutEveryFileReference_WithHowItMustBeMet()
    {
        Assert.Null(DuckDbReadOnlyGate.Inspect("SELECT * FROM 'a.csv' JOIN read_parquet(['b.parquet', 'c.parquet']) USING (id), \"d.json\", s.\"e.csv\"", out var files));
        Assert.Equal(["a.csv", "b.parquet", "c.parquet", "d.json", "s.e.csv"], files.Select(f => f.Path));   // a qualified name is read whole, as DuckDB reads main."sales.csv"
        Assert.Equal([DuckDbFileNeed.Required, DuckDbFileNeed.Required, DuckDbFileNeed.Required, DuckDbFileNeed.FileLike, DuckDbFileNeed.IfFile], files.Select(f => f.Need));
        Assert.Equal([false, true, true, false, false], files.Select(f => f.InReader));
        Assert.Equal([false, true, true, false, false], files.Select(f => f.InList));

        // A quoted name that reads like no file is a table, and a string anywhere but a table or a reader is a value.
        Assert.Null(DuckDbReadOnlyGate.Inspect("SELECT 'x.csv' AS v FROM \"orders\" WHERE n = 'y.csv'", out var none));
        Assert.Empty(none);
    }

    [Fact]
    public void Binds_Body_AndTheExplainAndReturningReads()
    {
        Assert.Equal(["id", "name"], DuckDbReadOnlyGate.Binds("SELECT $id, $name, '$not', $id"));
        Assert.Equal("SELECT 1", DuckDbReadOnlyGate.Body("SELECT 1 ;  "));
        Assert.True(DuckDbReadOnlyGate.IsExplain("explain analyze select 1"));
        Assert.False(DuckDbReadOnlyGate.IsExplain("SELECT 'EXPLAIN'"));
        Assert.True(DuckDbReadOnlyGate.Returns("DELETE FROM t RETURNING id"));
        Assert.False(DuckDbReadOnlyGate.Returns("SELECT 'RETURNING'"));
    }

    /// <summary>
    /// Every table function and table macro of the bundled engine is in exactly one of the gate's lists (the plan's spike item 7): an engine
    /// upgrade that adds a reader fails here instead of slipping through as a name the gate passes.
    /// </summary>
    [Fact]
    public void EveryTableFunction_OfTheBundledEngine_IsClassified()
    {
        using var connection = new DuckDBConnection("DataSource=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT upper(function_name) FROM duckdb_functions() WHERE function_type IN ('table', 'table_macro') AND NOT starts_with(function_name, '__') ORDER BY 1";
        var unclassified = new List<string>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                string name = reader.GetString(0);
                if (DuckDbFunctions.Of(name) is null)
                {
                    unclassified.Add(name);
                }
            }
        }

        Assert.True(unclassified.Count == 0, "unclassified: " + string.Join(", ", unclassified));
        Assert.Empty(DuckDbFunctions.Readers.Intersect(DuckDbFunctions.Denied));
        Assert.Empty(DuckDbFunctions.Readers.Intersect(DuckDbFunctions.Harmless));
        Assert.Empty(DuckDbFunctions.Denied.Intersect(DuckDbFunctions.Harmless));
    }
}

/// <summary><c>duckdb.json</c>, the access and the four read tools over real files in a temp folder (2026-10-25).</summary>
public sealed class DuckDbToolsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("neon-duckdb-").FullName;
    private readonly ManualTimeProvider _time = new();
    private readonly AppSettingsData _settings = new() { DuckDbTools = true, DuckDbSandboxFiles = true, DuckDbDatabasesOffered = ["shop", "notes"] };
    private readonly string _profile;
    private readonly string _home;
    private readonly string _work;
    private readonly IDisposable _cache = NeonSidekick.Computers.ConfigCache.Scope();

    public DuckDbToolsTests()
    {
        _home = Path.Combine(_dir, "home");
        _profile = Path.Combine(_home, "profiles", "default");
        _work = Path.Combine(_dir, "work");
        Directory.CreateDirectory(Profiles.ConfigsDirectory(_profile));
        Directory.CreateDirectory(Profiles.ConfigsDirectory(_home));
        Directory.CreateDirectory(Path.Combine(_work, "data"));
        Make(Path.Combine(_dir, "shop.duckdb"), """
            CREATE TABLE customers (id INTEGER PRIMARY KEY, name VARCHAR NOT NULL, city VARCHAR DEFAULT 'Oslo');
            CREATE TABLE orders (id INTEGER PRIMARY KEY, customer_id INTEGER REFERENCES customers(id), total DOUBLE, tags VARCHAR[]);
            CREATE INDEX orders_by_customer ON orders (customer_id);
            CREATE VIEW big_orders AS SELECT * FROM orders WHERE total > 100;
            INSERT INTO customers (id, name) VALUES (1, 'Ada'), (2, 'Grace'), (3, 'Linus');
            INSERT INTO orders VALUES (1, 1, 250.5, ['a', 'b']), (2, 2, 12, NULL), (3, 1, 99.99, []);
            """);
        Make(Path.Combine(_work, "data", "local.duckdb"), "CREATE TABLE things (a INTEGER); INSERT INTO things VALUES (7);");
        File.WriteAllText(Path.Combine(_work, "data", "sales.csv"), "region,amount\nnorth,10\nsouth,32\n");
        File.WriteAllText(Path.Combine(_work, "data", "more.csv"), "region,amount\neast,5\n");
        File.WriteAllText(Path.Combine(_work, "data", "people.json"), "[{\"name\": \"Ada\"}, {\"name\": \"Grace\"}]");
        File.WriteAllText(Path.Combine(_dir, "outside.csv"), "secret\n42\n");
        File.WriteAllText(DuckDbConfigFile.ProfilePath(_profile), """
            {
              // a comment, as the empty text has
              "databases": {
                "shop": { "path": "../../../../shop.duckdb", "description": "the sample shop" },
                "nopath": { "description": "no file" },
                "notes": { "path": "D:\\nowhere\\notes.duckdb" },
              }
            }
            """);
    }

    public void Dispose()
    {
        _cache.Dispose();
        Junction.DeleteTree(_dir);
    }

    internal static void Make(string path, string script)
    {
        using var connection = new DuckDBConnection(new DuckDBConnectionStringBuilder { DataSource = path }.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = script;
        command.ExecuteNonQuery();
    }

    private DuckDbCatalog Catalog() => DuckDbConfigFile.LoadCatalog(_profile, _home).Offered(_settings.DuckDbDatabasesOffered);

    private DuckDbAccess Access() => new(Catalog, () => _settings.DuckDbSandboxFiles ? new WorkingDirectory(() => _work, _time) : null);

    private DuckDbQueryTool Query() => new(Access(), () => _settings);

    [Fact]
    public void TheFile_NamesTheDatabases_ARelativePathFromItsFolder_AndSaysWhatItSkipped()
    {
        var loaded = DuckDbConfigFile.LoadCatalog(_profile, _home);
        Assert.Equal(["shop", "notes"], loaded.Databases.Select(d => d.Name));
        Assert.Equal(Path.Combine(_dir, "shop.duckdb"), loaded.Databases[0].FullPath);
        var problem = Assert.Single(loaded.Problems);
        Assert.Equal(DuckDbText.NoPath, problem.Reason);
        Assert.Equal(1, loaded.Offered(["SHOP"]).Hidden);
        Assert.Same(DuckDbCatalog.Empty, DuckDbConfigFile.Load(Path.Combine(_dir, "missing.json")));

        File.WriteAllText(DuckDbConfigFile.GlobalPath(_home), "{ not json");
        Assert.Contains(DuckDbConfigFile.LoadCatalog(_profile, _home).Problems, p => p.Source == DuckDbConfigFile.GlobalPath(_home));

        File.WriteAllText(DuckDbConfigFile.GlobalPath(_home), "{ \"databases\": null }");
        Assert.Same(DuckDbCatalog.Empty, DuckDbConfigFile.Load(DuckDbConfigFile.GlobalPath(_home)));
    }

    [Fact]
    public void TheGlobalFile_ARelativePath_CountsFromTheHomesConfigs()
    {
        File.WriteAllText(DuckDbConfigFile.GlobalPath(_home), """{ "databases": { "x": { "path": "../x.duckdb" } } }""");
        var global = Assert.Single(DuckDbConfigFile.Load(DuckDbConfigFile.GlobalPath(_home)).Databases);
        Assert.Equal(Path.Combine(_home, "x.duckdb"), global.FullPath);
    }

    [Fact]
    public void AddDatabase_KeepsTheFile_AndTheEntryLoads_AndReplaceRenames()
    {
        string path = DuckDbConfigFile.GlobalPath(_home);
        Assert.Null(DuckDbConfigFile.AddDatabase(path, "lake", new DuckDbDatabaseConfig { Path = "C:\\data\\lake.duckdb", Description = "the lake" }));
        var added = Assert.Single(DuckDbConfigFile.Load(path).Databases);
        Assert.Equal(("lake", "the lake"), (added.Name, added.Config.Description));
        Assert.NotNull(DuckDbConfigFile.AddDatabase(path, "lake", new DuckDbDatabaseConfig { Path = "x.duckdb" }));

        Assert.Null(DuckDbConfigFile.ReplaceDatabase(path, "lake", "pond", new DuckDbDatabaseConfig { Path = "C:\\data\\pond.duckdb" }));
        Assert.Equal("pond", Assert.Single(DuckDbConfigFile.Load(path).Databases).Name);
        Assert.Contains(DuckDbConfigFile.EveryFile(_profile, _home), f => f.EndsWith("pond.duckdb", StringComparison.Ordinal));   // the guard sees the renamed entry's file
    }

    [Fact]
    public void Resolve_ANameWins_ThenMemory_ThenASandboxPath_ElseTheSentence()
    {
        var access = Access();
        Assert.Null(access.Resolve(null, out var blank));
        Assert.Equal(DuckDbText.DatabaseNeeded("shop, notes", sandbox: true), DuckDbText.Error(blank!));
        Assert.Equal("shop", access.Resolve("Shop", out _)!.Name);
        Assert.True(access.Resolve(":memory:", out _)!.InMemory);
        Assert.Equal(Path.Combine(_work, "data", "local.duckdb"), access.Resolve("data/local.duckdb", out _)!.FullPath);

        Assert.Null(access.Resolve("../shop.duckdb", out var outside));
        Assert.Equal(SqlOutcome.ConnectFailed, outside!.Outcome);
        Assert.Null(access.Resolve("missing.duckdb", out var unknown));
        Assert.StartsWith("Error: ", DuckDbText.Error(unknown!));

        // The sniffs: a SQLite file, an empty one and any other are refused with their sentence.
        using (var sqlite = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(_work, "lite.db")};Pooling=false"))
        {
            sqlite.Open();
            using var make = sqlite.CreateCommand();
            make.CommandText = "CREATE TABLE t (a)";
            make.ExecuteNonQuery();
        }

        File.WriteAllText(Path.Combine(_work, "empty.duckdb"), "");
        File.WriteAllText(Path.Combine(_work, "text.duckdb"), "not a database at all");
        Assert.Null(access.Resolve("lite.db", out var lite));
        Assert.Contains(DuckDbText.IsSqliteFile("lite.db"), DuckDbText.Error(lite!));
        Assert.Null(access.Resolve("empty.duckdb", out var empty));
        Assert.Contains(DuckDbText.EmptyFile("empty.duckdb"), DuckDbText.Error(empty!));
        Assert.Null(access.Resolve("text.duckdb", out var text));
        Assert.Contains(DuckDbText.NotADuckDbFile("text.duckdb"), DuckDbText.Error(text!));
        Assert.Equal(DuckDbFileKind.DuckDb, DuckDbAccess.Sniff(Path.Combine(_dir, "shop.duckdb")));
        Assert.True(DuckDbAccess.HasDuckDbHeader(Path.Combine(_dir, "shop.duckdb")));
        Assert.False(DuckDbAccess.HasDuckDbHeader(Path.Combine(_work, "lite.db")));

        // create: a new file's path in the working directory, its extension and folder checked.
        Assert.NotNull(access.Resolve("new.duckdb", create: true, out _, out bool creating));
        Assert.True(creating);
        Assert.Null(access.Resolve("new.db", create: true, out var badExtension, out _));
        Assert.Equal(DuckDbText.BadExtension("new.db"), DuckDbText.Error(badExtension!));
        Assert.Null(access.Resolve("nofolder/new.duckdb", create: true, out var noFolder, out _));
        Assert.Equal(DuckDbText.NoFolder("nofolder/new.duckdb"), DuckDbText.Error(noFolder!));
        Assert.Null(access.Resolve(":memory:", create: true, out var memory, out _));
        Assert.Equal(DuckDbText.MemoryIsGone, DuckDbText.Error(memory!));
        Assert.Null(access.Resolve(null, create: true, out var noPath, out _));
        Assert.Equal(DuckDbText.CreateNeedsPath, DuckDbText.Error(noPath!));

        _settings.DuckDbSandboxFiles = false;
        Assert.Null(Access().Resolve("data/local.duckdb", out _));
        Assert.Null(Access().Resolve("x.duckdb", create: true, out var noSandbox, out _));
        Assert.Equal(DuckDbText.CreateNeedsSandbox, DuckDbText.Error(noSandbox!));
        _settings.DuckDbDatabasesOffered = ["shop"];
        Assert.Equal("shop", Access().Resolve(null, out _)!.Name);
        _settings.DuckDbDatabasesOffered = null;
        Assert.Null(Access().Resolve(null, out var none));
        Assert.Equal(DuckDbText.NoDatabases, DuckDbText.Error(none!));
        _settings.DuckDbSandboxFiles = true;
        Assert.True(Access().Resolve(null, out _)!.InMemory);   // nothing named, the sandbox on: the data files alone
    }

    [Fact]
    public void Resolve_AnUntickedFile_ByItsPath_IsRefused()
    {
        string local = Path.Combine(_work, "data", "local.duckdb");
        var access = new DuckDbAccess(Catalog, () => new WorkingDirectory(() => _work, _time), () => [local]);
        Assert.Null(access.Resolve("data/local.duckdb", out var refused));
        Assert.Equal(DuckDbText.UntickedDatabase("data/local.duckdb"), DuckDbText.Error(refused!));
    }

    [Fact]
    public async Task Run_ReadsWithBinds_CapsTheRows_RefusesAWrite_AndInterruptsAtTheTimeout()
    {
        var access = Access();
        var run = await access.RunAsync("shop", ["SELECT name FROM customers WHERE id IN ($a, $b) ORDER BY id"], [new("a", 1L), new("b", 2)], 10, 5, CancellationToken.None);
        Assert.Equal(SqlOutcome.Ok, run.Outcome);
        Assert.Equal(["Ada", "Grace"], run.Grids[0].Rows.Select(r => r[0]));

        var nested = await access.RunAsync("shop", ["SELECT tags FROM orders WHERE id = 1"], [], 10, 5, CancellationToken.None);
        Assert.Equal("[a, b]", nested.Grids[0].Rows[0][0]);

        var capped = await access.RunAsync("shop", ["SELECT * FROM customers"], [], 2, 5, CancellationToken.None);
        Assert.True(capped.Grids[0].More);
        Assert.Equal(2, capped.Grids[0].Rows.Count);

        var write = await access.RunAsync("shop", [DuckDbCheck.WritePastGate], [], 2, 5, CancellationToken.None);
        Assert.Equal(SqlOutcome.Failed, write.Outcome);
        Assert.Contains("read-only", write.Detail);

        var slow = await access.RunAsync("shop", [DuckDbCheck.SlowQuery], [], 2, 1, CancellationToken.None);
        Assert.Equal(SqlOutcome.Timeout, slow.Outcome);
        Assert.Equal("Error: the query on shop ran past 1 s and was stopped; narrow it (WHERE, LIMIT, fewer joins)", DuckDbText.Error(slow));

        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => access.RunAsync("shop", [DuckDbCheck.SlowQuery], [], 2, 30, cancel.Token));

        var gone = await access.RunAsync("notes", ["SELECT 1"], [], 2, 5, CancellationToken.None);
        Assert.Equal(SqlOutcome.ConnectFailed, gone.Outcome);
        Assert.StartsWith("Error: could not open notes: no database file at ", DuckDbText.Error(gone));

        // The file is let go after every run: it opens exclusively and deletes at once.
        string shop = Path.Combine(_dir, "shop.duckdb");
        using (new FileStream(shop, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
        }
    }

    /// <summary>
    /// Each read layer alone keeps a write from landing (SQLite's S14): the row count after says so. The rolled-back layer lets the change run,
    /// then undoes it; with no layer the same change lands, so the seam leaves the layer out for real.
    /// </summary>
    [Theory]
    [InlineData(DuckDbReadLayers.ReadOnlyOpen)]
    [InlineData(DuckDbReadLayers.RolledBack)]
    public void EachReadLayer_Alone_KeepsAChangeFromLanding(DuckDbReadLayers layer)
    {
        var target = new DuckDbTarget("shop", Path.Combine(_dir, "shop.duckdb"));
        var run = DuckDbAccess.RunWithLayers(target, ["EXPLAIN ANALYZE DELETE FROM orders"], null, layer);
        Assert.Equal(layer == DuckDbReadLayers.RolledBack ? SqlOutcome.Ok : SqlOutcome.Failed, run.Outcome);
        Assert.Equal("3", DuckDbAccess.RunWithLayers(target, ["SELECT count(*) FROM orders"], null, DuckDbReadLayers.All).Grids[0].Rows[0][0]);

        Assert.Equal(SqlOutcome.Ok, DuckDbAccess.RunWithLayers(target, ["EXPLAIN ANALYZE DELETE FROM orders WHERE id = 2"], null, DuckDbReadLayers.None).Outcome);
        Assert.Equal("2", DuckDbAccess.RunWithLayers(target, ["SELECT count(*) FROM orders"], null, DuckDbReadLayers.All).Grids[0].Rows[0][0]);
    }

    /// <summary>The locked configuration alone: no data file outside the working directory, whatever the path layer let past.</summary>
    [Fact]
    public void TheLockedLayer_Alone_HoldsEveryFileToTheWorkingDirectory()
    {
        string outside = Path.Combine(_dir, "outside.csv").Replace("'", "''", StringComparison.Ordinal);
        string inside = Path.Combine(_work, "data", "sales.csv").Replace("'", "''", StringComparison.Ordinal);
        var refused = DuckDbAccess.RunWithLayers(DuckDbTarget.Memory, [$"SELECT * FROM read_csv('{outside}')"], _work, DuckDbReadLayers.Locked);
        Assert.Equal(SqlOutcome.Failed, refused.Outcome);
        Assert.Equal(SqlOutcome.Ok, DuckDbAccess.RunWithLayers(DuckDbTarget.Memory, [$"SELECT * FROM read_csv('{inside}')"], _work, DuckDbReadLayers.Locked).Outcome);

        // Sandbox files off: no folder at all.
        Assert.Equal(SqlOutcome.Failed, DuckDbAccess.RunWithLayers(DuckDbTarget.Memory, [$"SELECT * FROM read_csv('{inside}')"], null, DuckDbReadLayers.Locked).Outcome);

        // Nothing is installed or loaded, nor a setting changed past the lock.
        Assert.Equal(SqlOutcome.Failed, DuckDbAccess.RunWithLayers(DuckDbTarget.Memory, ["SELECT * FROM read_csv('https://example.com/x.csv')"], _work, DuckDbReadLayers.Locked).Outcome);
    }

    [Fact]
    public async Task TheTools_ListDescribeAndQuery()
    {
        var access = Access();
        string listed = new DuckDbDatabasesTool(access, () => _settings).Describe();
        Assert.StartsWith("2 DuckDB databases (every DuckDB tool takes one by name in \"database\"; it may be left out only when there is one):\n- shop: ", listed);
        Assert.Contains(" — the sample shop", listed);
        Assert.Contains(DuckDbText.SandboxLine, listed);
        Assert.Equal(DuckDbText.DatabaseNeeded("shop, notes", sandbox: true), await new DuckDbTablesTool(access, () => _settings).DescribeAsync(null, null, CancellationToken.None));

        _settings.DuckDbDatabasesOffered = ["shop"];
        string tables = await new DuckDbTablesTool(access, () => _settings).DescribeAsync(null, null, CancellationToken.None);
        Assert.StartsWith("3 tables and views in shop", tables);
        Assert.Contains("| big_orders | view |", tables);
        Assert.StartsWith("1 table or view in shop", await new DuckDbTablesTool(access, () => _settings).DescribeAsync(null, "%cust%", CancellationToken.None));

        var describe = new DuckDbDescribeTool(access, () => _settings);
        string orders = await describe.DescribeAsync("ORDERS", null, CancellationToken.None);
        Assert.StartsWith("orders (table, 4 columns) in shop\n", orders);
        Assert.Contains("customers", orders);
        Assert.Contains("orders_by_customer", orders);
        Assert.Contains("CREATE TABLE orders", orders);
        Assert.Equal(DuckDbText.TableNotFound("nope", "shop"), await describe.DescribeAsync("nope", null, CancellationToken.None));
        Assert.Equal(DuckDbText.NoTable, await describe.DescribeAsync(" ", null, CancellationToken.None));

        var query = new DuckDbQueryTool(access, () => _settings);
        string rows = await query.RunAsync("SELECT id, total, tags FROM orders ORDER BY id;", null, [], null, CancellationToken.None);
        Assert.StartsWith("3 rows × 3 columns from shop (", rows);
        Assert.Contains("| 1 | 250.5 | [a, b] |", rows);
        Assert.Contains("| 2 | 12.0 | NULL |", rows);
        Assert.StartsWith("Error: the SQL starts with DROP", await query.RunAsync("DROP TABLE orders", null, [], null, CancellationToken.None));
        Assert.Contains("the first 1 shown", await query.RunAsync("SELECT * FROM orders", null, [], 1, CancellationToken.None));
        Assert.Equal(SqlText.BadMaxRows(DuckDbQueryTool.MinRows, DuckDbQueryTool.MaxRows), await query.RunAsync("SELECT 1", null, [], 0, CancellationToken.None));
        Assert.Contains("| 7 |", await query.RunAsync("SELECT a FROM things", "data/local.duckdb", [], null, CancellationToken.None));

        var invoked = (string)(await query.InvokeAsync(new AIFunctionArguments { ["sql"] = "SELECT name FROM customers WHERE id = $id", ["params"] = System.Text.Json.JsonDocument.Parse("""{"id": 3}""").RootElement }))!;
        Assert.Contains("| Linus |", invoked);
        Assert.StartsWith("Error: \"params\" must be one object", (string)(await query.InvokeAsync(new AIFunctionArguments { ["sql"] = "SELECT 1", ["params"] = "x" }))!);
    }

    [Fact]
    public async Task DataFiles_InsideRead_ByPathQuotedNameGlobAndList()
    {
        _settings.DuckDbDatabasesOffered = null;
        var query = Query();
        string direct = await query.RunAsync("SELECT sum(amount) AS total FROM 'data/sales.csv'", null, [], null, CancellationToken.None);
        Assert.StartsWith("1 row × 1 column from " + DuckDbText.MemoryName, direct);
        Assert.Contains("| 42 |", direct);
        Assert.Contains("| 42 |", await query.RunAsync("SELECT sum(amount) FROM \"data/sales.csv\"", null, [], null, CancellationToken.None));
        Assert.Contains("| 47 |", await query.RunAsync("SELECT sum(amount) FROM read_csv('data/*.csv')", null, [], null, CancellationToken.None));
        Assert.Contains("| 47 |", await query.RunAsync("SELECT sum(amount) FROM read_csv(['data/sales.csv', 'data/more.csv'])", null, [], null, CancellationToken.None));
        Assert.Contains("| Grace |", await query.RunAsync("SELECT name FROM read_json('data/people.json') ORDER BY name DESC LIMIT 1", null, [], null, CancellationToken.None));

        // A data file joined to a named database's table.
        _settings.DuckDbDatabasesOffered = ["shop"];
        Assert.Contains("| 3 |", await query.RunAsync("SELECT count(*) FROM customers, 'data/sales.csv' WHERE amount = 10", null, [], null, CancellationToken.None));
    }

    [Fact]
    public async Task DataFiles_OutsideMissingAndPatterns_AreRefused()
    {
        _settings.DuckDbDatabasesOffered = null;
        var query = Query();
        string outside = Path.Combine(_dir, "outside.csv");
        Assert.StartsWith("Error: ", await query.RunAsync("SELECT * FROM '../outside.csv'", null, [], null, CancellationToken.None));
        Assert.StartsWith("Error: ", await query.RunAsync("SELECT * FROM read_csv('" + outside + "')", null, [], null, CancellationToken.None));
        Assert.StartsWith("Error: ", await query.RunAsync("SELECT * FROM \"../outside.csv\"", null, [], null, CancellationToken.None));
        Assert.Equal(DuckDbText.NoSuchFile("missing.csv"), await query.RunAsync("SELECT * FROM 'missing.csv'", null, [], null, CancellationToken.None));
        Assert.Equal(DuckDbText.TableLooksLikeFile("missing.csv", sandbox: true), await query.RunAsync("SELECT * FROM \"missing.csv\"", null, [], null, CancellationToken.None));
        Assert.StartsWith("Error: ", await query.RunAsync("SELECT * FROM read_csv('data/**/*.csv')", null, [], null, CancellationToken.None));
        Assert.StartsWith("Error: ", await query.RunAsync("SELECT * FROM 'data/*.csv'", null, [], null, CancellationToken.None));
        Assert.Equal(DuckDbText.NoMatch("data/*.tsv"), await query.RunAsync("SELECT * FROM read_csv('data/*.tsv')", null, [], null, CancellationToken.None));

        // Sandbox files off: no data file at all.
        _settings.DuckDbSandboxFiles = false;
        _settings.DuckDbDatabasesOffered = ["shop"];
        Assert.Equal(DuckDbText.FilesNeedSandbox("data/sales.csv"), await Query().RunAsync("SELECT * FROM 'data/sales.csv'", null, [], null, CancellationToken.None));
    }

    [Fact]
    public async Task DataFiles_ThroughAJunctionOut_AreRefused()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string outside = Path.Combine(_dir, "away");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.csv"), "a\n1\n");
        Junction.Make(Path.Combine(_work, "link"), outside);
        _settings.DuckDbDatabasesOffered = null;
        Assert.StartsWith("Error: ", await Query().RunAsync("SELECT * FROM 'link/secret.csv'", null, [], null, CancellationToken.None));
        Assert.StartsWith("Error: ", await Query().RunAsync("SELECT * FROM read_csv('link/*.csv')", null, [], null, CancellationToken.None));
    }

    [SymlinkFact]
    public async Task DataFiles_ThroughASymlinkOut_AreRefused()
    {
        File.CreateSymbolicLink(Path.Combine(_work, "secret.csv"), Path.Combine(_dir, "outside.csv"));
        _settings.DuckDbDatabasesOffered = null;
        Assert.StartsWith("Error: ", await Query().RunAsync("SELECT * FROM 'secret.csv'", null, [], null, CancellationToken.None));
    }

    [Fact]
    public void TheGroup_IsOfferedWithTheSwitch_AndSomethingToOpen_AndReadsInPlanMode()
    {
        Assert.True(ChatScreen.DuckDbOffered(_settings, Access()));
        _settings.DuckDbDatabasesOffered = null;
        Assert.True(ChatScreen.DuckDbOffered(_settings, Access()));   // the sandbox's files
        _settings.DuckDbSandboxFiles = false;
        Assert.False(ChatScreen.DuckDbOffered(_settings, Access()));
        _settings.DuckDbDatabasesOffered = ["shop"];
        _settings.DuckDbTools = false;
        Assert.False(ChatScreen.DuckDbOffered(_settings, Access()));

        var all = ChatScreen.DuckDbTools(Access(), () => _settings, allow: null);
        Assert.All(all.Where(t => t.Name != DuckDbExecuteTool.ToolName), t => Assert.True(PlanTools.Allowed(t.Name), t.Name));
        Assert.Contains(DuckDbExecuteTool.ToolName, PlanTools.Mutating);
        Assert.Equal(ChatScreen.DuckDbToolNames, all.Select(t => t.Name).ToHashSet());
        Assert.Equal(["shop", "notes"], ChatScreen.DuckDbChoices(DuckDbConfigFile.LoadCatalog(_profile, _home)).Select(c => c.Text));
    }

    [Fact]
    public void TheRules_NameTheTools_OnlyWhileOffered()
    {
        Assert.Contains(Assistant.DuckDbRule, Assistant.DefaultRules(false, tools: true, duckDb: true));
        Assert.DoesNotContain(Assistant.DuckDbRule, Assistant.DefaultRules(false, tools: true));
        Assert.DoesNotContain(Assistant.DuckDbWriteRule, Assistant.DefaultRules(false, tools: true, duckDb: true));
        Assert.Contains(Assistant.DuckDbWriteRule, Assistant.DefaultRules(false, tools: true, duckDb: true, duckDbWrite: true));
        Assert.Contains("duckdb_query reads the DuckDB files and data files (not the duckdb CLI)", Assistant.ShellNativeRule(false, false, false, false, duckDb: true));
        Assert.Equal("duckdb_query", Shell.NativeRedirect.Table["duckdb"]);
        Assert.Contains(Assistant.DuckDbRule, new TurnRules(DuckDb: true).DefaultRules(false));
    }

    [Fact]
    public void PrepareTurn_TheMainChatsPrompt_CarriesTheRule_WhileOffered()
    {
        var assistant = new Assistant(new FakeChatClient(), new ConversationHistory(""), new LlmTimeouts(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)));
        var memory = new NeonSidekick.Memory.MemoryStore(_dir);
        string home = Path.Combine(_dir, "profile");
        var tools = ChatScreen.DuckDbTools(Access(), () => _settings, allow: null);

        ChatScreen.PrepareTurn(assistant, memory, [], [], new PersonaFile(home), new OperataFile(home), new VocaliaFile(home), memoryEnabled: false, speechOutput: false, duckDbTools: tools, duckDbEnabled: true);
        Assert.Contains(Assistant.DuckDbRule, assistant.History.SystemPrompt);

        ChatScreen.PrepareTurn(assistant, memory, [], [], new PersonaFile(home), new OperataFile(home), new VocaliaFile(home), memoryEnabled: false, speechOutput: false, duckDbTools: tools, duckDbEnabled: false);
        Assert.DoesNotContain(Assistant.DuckDbRule, assistant.History.SystemPrompt);
    }

    [Fact]
    public void Sweep_TakesAwayOnlyTheFoldersOfProcessesThatAreGone()
    {
        string root = Path.Combine(_dir, "spill");
        Directory.CreateDirectory(Path.Combine(root, "11"));
        Directory.CreateDirectory(Path.Combine(root, "22"));
        Directory.CreateDirectory(Path.Combine(root, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Directory.CreateDirectory(Path.Combine(root, "not-a-pid"));
        Assert.Equal(1, DuckDbAccess.Sweep(root, pid => pid == 22));
        Assert.False(Directory.Exists(Path.Combine(root, "11")));
        Assert.True(Directory.Exists(Path.Combine(root, "22")));
        Assert.True(Directory.Exists(Path.Combine(root, "not-a-pid")));
        Assert.Equal(0, DuckDbAccess.Sweep(Path.Combine(_dir, "no-such-folder")));
    }

    /// <summary>
    /// The exit waits for the start's sweep (2026-10-25, the Mac pass: a run that ended at once cut it mid-delete): an ended one at once, a
    /// slow one until it ends, one past the wait given up on, never thrown.
    /// </summary>
    [Fact]
    public async Task FinishSweep_WaitsForTheStartsSweep_UpToItsCap()
    {
        string root = Path.Combine(_dir, "spill");
        Directory.CreateDirectory(Path.Combine(root, "11", "nested"));
        File.WriteAllText(Path.Combine(root, "11", "nested", "duckdb_temp_storage_DEFAULT-1.tmp"), new string('x', 1000));
        var release = new TaskCompletionSource();
        var slow = Task.Run(async () =>
        {
            await release.Task;
            return DuckDbAccess.Sweep(root, _ => false);
        });

        Assert.False(DuckDbAccess.FinishSweep(slow, TimeSpan.FromMilliseconds(50)));   // past the cap: given up on
        _ = Task.Delay(100).ContinueWith(_ => release.SetResult(), TaskScheduler.Default);
        Assert.True(DuckDbAccess.FinishSweep(slow, TimeSpan.FromSeconds(10)));
        Assert.False(Directory.Exists(Path.Combine(root, "11")));
        Assert.True(DuckDbAccess.FinishSweep(Task.FromResult(0), TimeSpan.Zero));
        Assert.True(DuckDbAccess.FinishSweep(Task.FromException<int>(new IOException("x")), TimeSpan.Zero));
        await slow;
    }

    /// <summary>
    /// The Mac pass (2026-10-25): DuckDB makes only the last folder of <c>temp_directory</c>, so the spill folder's parents are made before a
    /// locked connection opens; a file in the way is left to DuckDB's own error, never thrown.
    /// </summary>
    [Fact]
    public async Task TheSpillFoldersParents_AreMadeBeforeALockedOpen_AndAFileInTheWayIsNoThrow()
    {
        string root = Path.Combine(_dir, "temp", "NeonSidekick", "duckdb");
        DuckDbAccess.EnsureSpillRoot(root);
        Assert.True(Directory.Exists(root));
        DuckDbAccess.EnsureSpillRoot(root);

        string blocked = Path.Combine(_dir, "file");
        File.WriteAllText(blocked, "");
        DuckDbAccess.EnsureSpillRoot(Path.Combine(blocked, "duckdb"));

        var access = new DuckDbAccess(() => DuckDbCatalog.Empty, () => new WorkingDirectory(() => _dir, TimeProvider.System));
        var run = await access.RunAsync(DuckDbTarget.MemoryPath, ["SELECT 1"], [], 10, 5, CancellationToken.None);
        Assert.Equal(SqlOutcome.Ok, run.Outcome);
        Assert.True(Directory.Exists(DuckDbAccess.SpillRoot));
        Assert.Equal(DuckDbAccess.SpillRoot, Path.GetDirectoryName(DuckDbAccess.SpillFolder));
    }
}

/// <summary><c>--duckdb-check</c> over a temp file (2026-10-25): every line passes on a real database, and a missing one fails at the open.</summary>
public sealed class DuckDbCheckTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("neon-duckdb-check-").FullName;

    public void Dispose() => Junction.DeleteTree(_dir);

    [Fact]
    public async Task TheCheck_PassesEveryLine_OnARealFile()
    {
        DuckDbToolsTests.Make(Path.Combine(_dir, "sample.duckdb"), "CREATE TABLE t (a INTEGER); CREATE VIEW v AS SELECT * FROM t;");
        var console = new Spectre.Console.Testing.TestConsole();
        var access = new DuckDbAccess(() => DuckDbCatalog.Empty, () => new WorkingDirectory(() => _dir, TimeProvider.System));
        int exit = await DuckDbCheck.RunAsync(console, access, "sample.duckdb", 10, CancellationToken.None);
        Assert.True(exit == 0, console.Output);
        Assert.Contains("DUCKDB CHECK PASS  6 checks", console.Output);
        Assert.Contains("2 tables and views", console.Output);

        var missing = new Spectre.Console.Testing.TestConsole();
        Assert.Equal(1, await DuckDbCheck.RunAsync(missing, access, "missing.duckdb", 10, CancellationToken.None));
        Assert.Contains("DUCKDB CHECK FAIL  1 of 1 checks failed", missing.Output);
    }
}
