using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Files;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Plans;
using NeonSidekick.Settings;
using NeonSidekick.Sql;
using NeonSidekick.Sqlite;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>The SQLite gate (2026-10-04): what one read-only statement is, lexed by SQLite's rules.</summary>
public sealed class SqliteReadOnlyGateTests
{
    [Theory]
    [InlineData("SELECT 1")]
    [InlineData("select * from t where name = 'insert into x'  ;  ")]
    [InlineData("WITH c AS (SELECT 1 AS x) SELECT x FROM c")]
    [InlineData("VALUES (1, 2)")]
    [InlineData("(SELECT 1)")]
    [InlineData("SELECT \"update\", [delete], `drop` FROM t -- a DROP in a comment\n")]
    [InlineData("SELECT replace(name, 'a', 'b') FROM t /* REPLACE INTO */")]
    [InlineData("SELECT * FROM t WHERE id = @id OR id = :id OR id = $id")]
    [InlineData("SELECT \"load_extension\", [readfile] FROM t")]
    public void Allows_OneRead(string sql) => Assert.Null(SqliteReadOnlyGate.Check(sql));

    [Theory]
    [InlineData("INSERT INTO t VALUES (1)", "the SQL starts with INSERT")]
    [InlineData("SELECT 1; SELECT 2", "the SQL is 2 statements")]
    [InlineData("WITH c AS (SELECT 1) INSERT INTO t SELECT * FROM c", "uses INSERT")]
    [InlineData("WITH c AS (SELECT 1) REPLACE INTO t SELECT * FROM c", "uses REPLACE INTO")]
    [InlineData("SELECT 1 FROM t WHERE x IN (SELECT 1) RETURNING id", "uses RETURNING")]
    [InlineData("ATTACH 'x.db' AS x", "starts with ATTACH")]
    [InlineData("PRAGMA query_only = OFF", "starts with PRAGMA")]
    [InlineData("SELECT load_extension('evil')", "uses load_extension()")]
    [InlineData("SELECT \"load_extension\"('evil.dll')", "uses load_extension()")]
    [InlineData("SELECT [fts3_tokenizer]('simple', x'00')", "uses fts3_tokenizer()")]
    [InlineData("SELECT `writefile`('x', 'y')", "uses writefile()")]
    [InlineData("SELECT * FROM t WHERE id = ?", "? placeholder")]
    [InlineData("SELECT 'never ends", "a string never ends")]
    [InlineData("SELECT 1 /* never ends", "a comment never ends")]
    [InlineData("   ", "give the SELECT")]
    public void Refuses_TheRest_WithTheSentence(string sql, string part)
    {
        string? refused = SqliteReadOnlyGate.Check(sql);
        Assert.NotNull(refused);
        Assert.StartsWith("Error: ", refused);
        Assert.Contains(part, refused);
    }

    [Fact]
    public void Binds_AndBody()
    {
        Assert.Equal(["@id", ":name", "$x"], SqliteReadOnlyGate.Binds("SELECT @id, :name, $x, @ID, '@not'"));
        Assert.Equal("SELECT 1", SqliteReadOnlyGate.Body("SELECT 1 ;  "));
        Assert.Equal("SELECT 1", SqliteReadOnlyGate.Body("SELECT 1"));
    }
}

/// <summary><c>sqlite.json</c>, the access and the four tools over real files in a temp folder (2026-10-04).</summary>
public sealed class SqliteToolsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("neon-sqlite-").FullName;
    private readonly ManualTimeProvider _time = new();
    private readonly AppSettingsData _settings = new() { SqliteTools = true, SqliteDatabasesOffered = ["shop", "notes"] };
    private readonly string _profile;
    private readonly string _home;
    private readonly string _work;

    public SqliteToolsTests()
    {
        _home = Path.Combine(_dir, "home");
        _profile = Path.Combine(_home, "profiles", "default");
        _work = Path.Combine(_dir, "work");
        Directory.CreateDirectory(_profile);
        Directory.CreateDirectory(Path.Combine(_work, "data"));
        Make(Path.Combine(_dir, "shop.db"), """
            CREATE TABLE customers (id INTEGER PRIMARY KEY, name TEXT NOT NULL, city TEXT DEFAULT 'Oslo');
            CREATE TABLE orders (id INTEGER PRIMARY KEY, customer_id INTEGER REFERENCES customers(id) ON DELETE CASCADE, total REAL, note BLOB);
            CREATE INDEX orders_by_customer ON orders (customer_id);
            CREATE VIEW big_orders AS SELECT * FROM orders WHERE total > 100;
            INSERT INTO customers (name) VALUES ('Ada'), ('Grace'), ('Linus');
            INSERT INTO orders (customer_id, total, note) VALUES (1, 250.5, X'CAFE'), (2, 12, NULL), (1, 99.99, NULL);
            """);
        Make(Path.Combine(_work, "data", "local.db"), "CREATE TABLE things (a); INSERT INTO things VALUES (7);");
        File.WriteAllText(SqliteConfigFile.ProfilePath(_profile), """
            {
              // a comment, as the empty text has
              "databases": {
                "shop": { "path": "../../../shop.db", "description": "the sample shop" },
                "nopath": { "description": "no file" },
                "notes": { "path": "D:\\nowhere\\notes.db" },
              }
            }
            """);
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

    private static void Make(string path, string script)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = script;
        command.ExecuteNonQuery();
    }

    private SqliteCatalog Catalog() => SqliteConfigFile.LoadCatalog(_profile, _home).Offered(_settings.SqliteDatabasesOffered);

    private SqliteAccess Access() => new(Catalog, () => _settings.SqliteSandboxFiles ? new WorkingDirectory(() => _work, _time) : null);

    [Fact]
    public void TheFile_NamesTheDatabases_ARelativePathFromItsFolder_AndSaysWhatItSkipped()
    {
        var loaded = SqliteConfigFile.LoadCatalog(_profile, _home);
        Assert.Equal(["shop", "notes"], loaded.Databases.Select(d => d.Name));
        Assert.Equal(Path.Combine(_dir, "shop.db"), loaded.Databases[0].FullPath);
        var problem = Assert.Single(loaded.Problems);
        Assert.Equal(SqliteText.NoPath, problem.Reason);
        Assert.Equal(1, loaded.Offered(["SHOP"]).Hidden);
        Assert.Same(SqliteCatalog.Empty, SqliteConfigFile.Load(Path.Combine(_dir, "missing.json")));

        File.WriteAllText(SqliteConfigFile.GlobalPath(_home), "{ not json");
        Assert.Contains(SqliteConfigFile.LoadCatalog(_profile, _home).Problems, p => p.Source == SqliteConfigFile.GlobalPath(_home));
    }

    [Fact]
    public void AddDatabase_KeepsTheFile_AndTheEntryLoads()
    {
        string path = SqliteConfigFile.GlobalPath(_home);
        Assert.Null(SqliteConfigFile.AddDatabase(path, "chinook", new SqliteDatabaseConfig { Path = "C:\\data\\chinook.db", Description = "music" }));
        string text = File.ReadAllText(path);
        Assert.Contains("// One entry per SQLite database.", text);
        var added = Assert.Single(SqliteConfigFile.Load(path).Databases);
        Assert.Equal(("chinook", "music"), (added.Name, added.Config.Description));
        Assert.NotNull(SqliteConfigFile.AddDatabase(path, "chinook", new SqliteDatabaseConfig { Path = "x.db" }));
    }

    [Fact]
    public void Resolve_ANameWins_ThenASandboxPath_ElseTheSentence()
    {
        var access = Access();
        Assert.Equal("shop", access.Resolve(null, null, out _)!.Name);
        Assert.Equal("shop", access.Resolve("Shop", null, out _)!.Name);
        var local = access.Resolve("data/local.db", null, out _)!;
        Assert.Equal(Path.Combine(_work, "data", "local.db"), local.FullPath);

        Assert.Null(access.Resolve("../shop.db", null, out var outside));
        Assert.Equal(SqlOutcome.ConnectFailed, outside!.Outcome);
        Assert.Null(access.Resolve("missing.db", null, out var unknown));
        Assert.Equal("Error: no SQLite database is named 'missing.db'; the databases are shop, notes, and no file by that path is in the working directory", SqliteText.Error(unknown!));

        _settings.SqliteSandboxFiles = false;
        Assert.Null(Access().Resolve("data/local.db", null, out _));
        _settings.SqliteDatabasesOffered = null;
        Assert.Null(Access().Resolve(null, null, out var none));
        Assert.Equal(SqliteText.NoDatabases, SqliteText.Error(none!));
    }

    [Fact]
    public async Task Run_ReadsWithBinds_CapsTheRows_RefusesAWrite_AndInterruptsAtTheTimeout()
    {
        var access = Access();
        var run = await access.RunAsync("shop", null, ["SELECT name FROM customers WHERE id IN (@a, :b, $c) ORDER BY id"], [new("a", 1L), new("b", 2), new("c", true)], 10, 5, CancellationToken.None);
        Assert.Equal(SqlOutcome.Ok, run.Outcome);
        Assert.Equal(["Ada", "Grace"], run.Grids[0].Rows.Select(r => r[0]));

        var unbound = await access.RunAsync("shop", null, ["SELECT @missing IS NULL"], [], 10, 5, CancellationToken.None);
        Assert.Equal(SqlOutcome.Ok, unbound.Outcome);
        Assert.Equal("1", unbound.Grids[0].Rows[0][0]);

        var capped = await access.RunAsync("shop", null, ["SELECT * FROM customers"], [], 2, 5, CancellationToken.None);
        Assert.True(capped.Grids[0].More);
        Assert.Equal(2, capped.Grids[0].Rows.Count);

        var write = await access.RunAsync("shop", null, ["DELETE FROM customers"], [], 2, 5, CancellationToken.None);
        Assert.Equal(SqlOutcome.Failed, write.Outcome);
        Assert.Contains("readonly", write.Detail);

        var slow = await access.RunAsync("shop", null, [SqliteCheck.SlowQuery], [], 2, 1, CancellationToken.None);
        Assert.Equal(SqlOutcome.Timeout, slow.Outcome);
        Assert.Equal("Error: the query on shop ran past 1 s and was stopped; narrow it (WHERE, LIMIT, fewer joins)", SqliteText.Error(slow));

        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => access.RunAsync("shop", null, [SqliteCheck.SlowQuery], [], 2, 30, cancel.Token));

        var gone = await access.RunAsync("notes", null, ["SELECT 1"], [], 2, 5, CancellationToken.None);
        Assert.Equal(SqlOutcome.ConnectFailed, gone.Outcome);
        Assert.StartsWith("Error: could not open notes: no database file at ", SqliteText.Error(gone));
    }

    [Fact]
    public async Task TheTools_ListDescribeAndQuery()
    {
        var access = Access();
        string listed = new SqliteDatabasesTool(access, () => _settings).Describe();
        Assert.StartsWith("2 SQLite databases (every SQLite tool takes one by name in \"database\"; the default is used when it is left out):\n- shop (default): ", listed);
        Assert.Contains(" — the sample shop", listed);
        Assert.Contains(SqliteText.SandboxLine, listed);

        string tables = await new SqliteTablesTool(access, () => _settings).DescribeAsync(null, null, CancellationToken.None);
        Assert.StartsWith("3 tables and views in shop", tables);
        Assert.Contains("| big_orders | view |", tables);
        Assert.StartsWith("1 table or view in shop", await new SqliteTablesTool(access, () => _settings).DescribeAsync(null, "cust", CancellationToken.None));

        var describe = new SqliteDescribeTool(access, () => _settings);
        string orders = await describe.DescribeAsync("ORDERS", null, CancellationToken.None);
        Assert.StartsWith("orders (table, 4 columns) in shop\n", orders);
        Assert.Contains("| id | INTEGER | yes | PK |  |", orders);
        Assert.Contains("Foreign keys:\n- (customer_id) → customers (id) ON DELETE CASCADE", orders);
        Assert.Contains("Indexes:\n- orders_by_customer (customer_id)", orders);
        Assert.Contains("```sql\nCREATE TABLE orders", orders);
        string customers = await describe.DescribeAsync("customers", "shop", CancellationToken.None);
        Assert.Contains("Referenced by:\n- orders (customer_id) → (id)", customers);
        Assert.Contains("| city | TEXT | yes |  | 'Oslo' |", customers);
        Assert.Equal(SqliteText.TableNotFound("nope", "shop"), await describe.DescribeAsync("nope", null, CancellationToken.None));
        Assert.Equal(SqliteText.NoTable, await describe.DescribeAsync(" ", null, CancellationToken.None));

        var query = new SqliteQueryTool(access, () => _settings);
        string rows = await query.RunAsync("SELECT id, total, note FROM orders ORDER BY id;", null, [], null, CancellationToken.None);
        Assert.StartsWith("3 rows × 3 columns from shop (", rows);
        Assert.Contains("| 1 | 250.5 | 0xCAFE |", rows);
        Assert.Contains("| 2 | 12 | NULL |", rows);
        Assert.StartsWith("Error: the SQL starts with DROP", await query.RunAsync("DROP TABLE orders", null, [], null, CancellationToken.None));
        Assert.Contains("the first 1 shown", await query.RunAsync("SELECT * FROM orders", null, [], 1, CancellationToken.None));
        Assert.Equal(SqlText.BadMaxRows(SqliteQueryTool.MinRows, SqliteQueryTool.MaxRows), await query.RunAsync("SELECT 1", null, [], 0, CancellationToken.None));
        Assert.Contains("| 7 |", await query.RunAsync("SELECT a FROM things", "data/local.db", [], null, CancellationToken.None));

        var invoked = (string)(await query.InvokeAsync(new AIFunctionArguments { ["sql"] = "SELECT name FROM customers WHERE id = @id", ["params"] = System.Text.Json.JsonDocument.Parse("""{"id": 3}""").RootElement }))!;
        Assert.Contains("| Linus |", invoked);
        Assert.StartsWith("Error: \"params\" must be one object", (string)(await query.InvokeAsync(new AIFunctionArguments { ["sql"] = "SELECT 1", ["params"] = "x" }))!);
    }

    [Fact]
    public void TheGroup_IsOfferedWithTheSwitch_AndSomethingToOpen_AndReadsInPlanMode()
    {
        Assert.True(ChatScreen.SqliteOffered(_settings, Access()));
        _settings.SqliteDatabasesOffered = null;
        Assert.True(ChatScreen.SqliteOffered(_settings, Access()));   // the sandbox's files
        _settings.SqliteSandboxFiles = false;
        Assert.False(ChatScreen.SqliteOffered(_settings, Access()));
        _settings.SqliteDatabasesOffered = ["shop"];
        _settings.SqliteTools = false;
        Assert.False(ChatScreen.SqliteOffered(_settings, Access()));

        Assert.All(ChatScreen.SqliteTools(Access(), () => _settings), t => Assert.True(PlanTools.Allowed(t.Name), t.Name));
        Assert.Equal(ChatScreen.SqliteToolNames, ChatScreen.SqliteTools(Access(), () => _settings).Select(t => t.Name).ToHashSet());
        Assert.Equal(["shop", "notes"], ChatScreen.SqliteChoices(SqliteConfigFile.LoadCatalog(_profile, _home)).Select(c => c.Text));
    }

    [Fact]
    public void TheRules_NameTheTools_OnlyWhileOffered()
    {
        Assert.Contains(Assistant.SqliteRule, Assistant.DefaultRules(false, tools: true, sqlite: true));
        Assert.DoesNotContain(Assistant.SqliteRule, Assistant.DefaultRules(false, tools: true));
        Assert.Contains("sqlite_query reads the SQLite files (not sqlite3)", Assistant.ShellNativeRule(false, false, false, false, sqlite: true));
        Assert.Equal("sqlite_query", Shell.NativeRedirect.Table["sqlite3"]);
        Assert.Contains(Assistant.SqliteRule, new TurnRules(Sqlite: true).DefaultRules(false));
    }
}
