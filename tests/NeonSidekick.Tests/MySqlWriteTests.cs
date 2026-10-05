using System.Globalization;
using NeonSidekick.App;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;
using NeonSidekick.MySql;
using NeonSidekick.Plans;
using NeonSidekick.Settings;
using NeonSidekick.Sql;

namespace NeonSidekick.Tests;

/// <summary><c>mysql_execute</c>'s gate (2026-10-05): one statement of an allowed kind, a routine's BEGIN … END body whole; the rest refused.</summary>
public sealed class MySqlWriteGateTests
{
    private static readonly IReadOnlyList<ServerStatementKind> Every = ServerStatementKinds.Resolve(ServerStatementKinds.Names);

    [Theory]
    [InlineData("INSERT INTO t (a) VALUES (@a);")]
    [InlineData("INSERT INTO t VALUES (1) ON DUPLICATE KEY UPDATE a = VALUES(a)")]
    [InlineData("UPDATE t SET a = REPLACE(a, 'x', 'y') WHERE id = @id")]
    [InlineData("REPLACE INTO t VALUES (1, 'a')")]
    [InlineData("WITH c AS (SELECT 1 AS a) SELECT * FROM c")]
    [InlineData("CREATE TABLE t (id INT PRIMARY KEY, a VARCHAR(10), b INT REFERENCES u (id) ON DELETE CASCADE)")]
    [InlineData("CREATE OR REPLACE ALGORITHM = MERGE SQL SECURITY INVOKER VIEW v AS SELECT * FROM t")]
    [InlineData("CREATE TEMPORARY TABLE x (a INT)")]
    [InlineData("CREATE UNIQUE INDEX t_a ON t (a)")]
    [InlineData("INSERT INTO t VALUES ('grant; revoke') # GRANT in a comment")]
    public void Allows_UnderTheDefault(string sql) => Assert.Null(MySqlWriteGate.Check(sql, ServerStatementKinds.Resolve(null)));

    [Theory]
    [InlineData("CREATE PROCEDURE p(IN x INT) BEGIN DECLARE y INT DEFAULT 0; IF x > 0 THEN SET y = 1; ELSEIF x < 0 THEN SET y = -1; END IF; WHILE y < 3 DO SET y = y + 1; END WHILE; SELECT CASE WHEN y > 1 THEN 'a' ELSE 'b' END; END")]
    [InlineData("CREATE TRIGGER tr BEFORE INSERT ON t FOR EACH ROW BEGIN IF NEW.a IS NULL THEN SET NEW.a = 'x'; END IF; END;")]
    [InlineData("CREATE FUNCTION f() RETURNS INT DETERMINISTIC RETURN 1")]
    [InlineData("CREATE PROCEDURE IF NOT EXISTS p() BEGIN lbl: LOOP LEAVE lbl; END LOOP lbl; REPEAT SET @x = 1; UNTIL TRUE END REPEAT; DROP TABLE IF EXISTS x; END")]
    [InlineData("CALL p(1)")]
    [InlineData("DELETE FROM t WHERE id = 1")]
    [InlineData("TRUNCATE TABLE t")]
    [InlineData("DROP TABLE t")]
    [InlineData("ALTER TABLE t ADD COLUMN b INT")]
    [InlineData("RENAME TABLE t TO u")]
    [InlineData("OPTIMIZE TABLE t")]
    public void Allows_EveryKind_WhenAllAreTicked(string sql) => Assert.Null(MySqlWriteGate.Check(sql, Every));

    [Theory]
    [InlineData("START TRANSACTION", "uses START, which mysql_execute refuses: each call is a transaction")]
    [InlineData("SET autocommit = 0", "uses SET")]
    [InlineData("USE other", "uses USE")]
    [InlineData("GRANT ALL ON *.* TO bob", "uses GRANT")]
    [InlineData("LOAD DATA INFILE '/etc/passwd' INTO TABLE t", "uses LOAD")]
    [InlineData("SELECT * FROM t INTO OUTFILE '/tmp/x'", "uses INTO OUTFILE")]
    [InlineData("LOCK TABLES t WRITE", "uses LOCK")]
    [InlineData("KILL 12", "uses KILL")]
    [InlineData("DO SLEEP(10)", "uses DO")]
    [InlineData("PREPARE s FROM 'DROP TABLE t'", "uses PREPARE")]
    [InlineData("CREATE DATABASE x", "uses CREATE DATABASE")]
    [InlineData("DROP SCHEMA x", "uses DROP SCHEMA")]
    [InlineData("CREATE USER bob", "uses CREATE USER, which mysql_execute refuses: accounts")]
    [InlineData("RENAME USER a TO b", "uses RENAME USER")]
    [InlineData("CREATE EVENT e ON SCHEDULE EVERY 1 DAY DO DELETE FROM t", "uses CREATE EVENT")]
    [InlineData("CREATE DEFINER = root PROCEDURE p() SELECT 1", "uses DEFINER =")]
    [InlineData("INSERT INTO t VALUES (LOAD_FILE('/etc/passwd'))", "uses LOAD_FILE()")]
    [InlineData("INSERT INTO t VALUES (1); DROP TABLE t", "the SQL is 2 statements")]
    [InlineData("CREATE PROCEDURE p() BEGIN SELECT 1; END; DROP TABLE t", "the SQL is 2 statements")]
    [InlineData("CREATE PROCEDURE p() BEGIN IF 1 THEN SELECT 1; END IF; END; DROP TABLE t", "the SQL is 2 statements")]
    [InlineData("SELECT 1 /*! ; DROP TABLE t */", "executable comment")]
    [InlineData("SHOW TABLES", "starts with SHOW")]
    public void Refuses_TheRest_WithTheSentence(string sql, string part)
    {
        string? refused = MySqlWriteGate.Check(sql, Every);
        Assert.NotNull(refused);
        Assert.StartsWith("Error:", refused);
        Assert.Contains(part, refused, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("INSERT INTO t VALUES (1)", new[] { ServerStatementKind.Data })]
    [InlineData("UPDATE t SET a = INSERT(a, 1, 2, 'x')", new[] { ServerStatementKind.Data })]
    [InlineData("WITH d AS (SELECT id FROM u) DELETE FROM t WHERE id IN (SELECT id FROM d)", new[] { ServerStatementKind.Delete })]
    [InlineData("SELECT REPLACE(a, 'x', 'y') FROM t", new[] { ServerStatementKind.Read })]
    [InlineData("CREATE TABLE t (a INT REFERENCES u ON DELETE CASCADE ON UPDATE CASCADE)", new[] { ServerStatementKind.Create })]
    [InlineData("CREATE TRIGGER tr AFTER INSERT ON t FOR EACH ROW INSERT INTO log VALUES (NEW.id)", new[] { ServerStatementKind.Procedures })]
    [InlineData("ALTER PROCEDURE p COMMENT 'x'", new[] { ServerStatementKind.Procedures })]
    [InlineData("DROP PROCEDURE p", new[] { ServerStatementKind.Drop })]
    [InlineData("ANALYZE TABLE t", new[] { ServerStatementKind.Upkeep })]
    public void Kinds_EveryChangeInTheStatement(string sql, ServerStatementKind[] kinds) => Assert.Equal(kinds, MySqlWriteGate.Kinds(sql));

    [Fact]
    public void Check_RefusesAKindNotTicked_AndARoutineIsCode()
    {
        Assert.Equal(
            "Error: the SQL is deleting (DELETE, TRUNCATE), which the user has not allowed; mysql_execute may run changing data, creating, reading — the user ticks more in MySQL statements allowed on the MySQL tab of /tools",
            MySqlWriteGate.Check("TRUNCATE t", ServerStatementKinds.Resolve(null)));
        Assert.StartsWith("Error: the SQL is procedures and triggers", MySqlWriteGate.Check("CREATE FUNCTION f() RETURNS INT DETERMINISTIC RETURN 1", ServerStatementKinds.Resolve(null)));
        Assert.Null(MySqlWriteGate.Kinds("GRANT ALL ON t TO bob"));
    }
}

/// <summary><c>mysql_execute</c> (2026-10-05): the order of its checks and its offering, no server needed until a change is allowed.</summary>
public sealed class MySqlExecuteTests
{
    private readonly AppSettingsData _settings = new() { MySqlTools = true, MySqlConnectionsOffered = ["shop", "ro"], MySqlMode = "read-write", MySqlStatementsAllowed = [.. ServerStatementKinds.Names] };

    private static MySqlConnectionConfig Config(string? access) => new() { Host = "127.0.0.1", Port = 1, Database = "sales", User = "u", Password = "p", Access = access, ConnectTimeoutSeconds = 1 };

    private MySqlAccess Access() =>
        new(() => new MySqlCatalog([new MySqlNamedConnection("shop", Config("readwrite"), "x.json"), new MySqlNamedConnection("ro", Config(null), "x.json")], []).Offered(_settings.MySqlConnectionsOffered));

    [Fact]
    public async Task TheTool_ChecksInOrder_ThenAsks_PerConnectionAndDatabase()
    {
        var asked = new List<(string Connection, string Place, string Sql)>();
        var tool = new MySqlExecuteTool(Access(), () => _settings, (_, connection, place, sql, _) =>
        {
            asked.Add((connection, place, sql));
            return Task.FromResult<bool?>(false);
        });

        Assert.Equal(ServerWriteText.Declined, await tool.RunAsync("DELETE FROM t WHERE id = @id;", null, null, [new("id", 1L)], null, CancellationToken.None));
        Assert.Equal(ServerWriteText.Declined, await tool.RunAsync("DELETE FROM t", "shop", "archive", [], null, CancellationToken.None));
        Assert.Equal([("shop", "sales", "DELETE FROM t WHERE id = @id"), ("shop", "archive", "DELETE FROM t")], asked);

        asked.Clear();
        Assert.Equal(ServerWriteText.ReadOnlyConnection(MySqlStatementKinds.Family, "ro"), await tool.RunAsync("DELETE FROM t", "ro", null, [], null, CancellationToken.None));
        Assert.StartsWith("Error: the SQL uses GRANT", await tool.RunAsync("GRANT ALL ON t TO bob", null, null, [], null, CancellationToken.None));
        Assert.StartsWith("Error: the SQL uses LOAD_FILE()", await tool.RunAsync("SELECT LOAD_FILE('/etc/passwd')", null, null, [], null, CancellationToken.None));
        _settings.MySqlMode = "read-only";
        Assert.Equal(ServerWriteText.ReadOnlyMode(MySqlStatementKinds.Family), await tool.RunAsync("INSERT INTO t VALUES (1)", null, null, [], null, CancellationToken.None));
        Assert.Empty(asked);
        _settings.MySqlMode = "read-write";
        Assert.Equal(ServerWriteText.NoPane(MySqlStatementKinds.Family), await new MySqlExecuteTool(Access(), () => _settings, null).RunAsync("INSERT INTO t VALUES (1)", null, null, [], null, CancellationToken.None));
    }

    [Fact]
    public void TheTurn_OffersExecute_OnlyUnderReadWrite_AndTheRulesFollow()
    {
        var access = Access();
        var all = ChatScreen.MySqlTools(access, () => _settings);
        Assert.Contains(MySqlExecuteTool.ToolName, ChatScreen.MySqlToolsFor(all, _settings, true, access.Catalog()).Select(t => t.Name));
        Assert.Equal(ServerWrites.MySql, ChatScreen.ServerWritesOf(ChatScreen.MySqlToolsFor(all, _settings, true, access.Catalog())));
        Assert.DoesNotContain(MySqlExecuteTool.ToolName, ChatScreen.MySqlToolsFor(all, _settings, false, access.Catalog()).Select(t => t.Name));
        _settings.MySqlConnectionsOffered = ["ro"];
        Assert.DoesNotContain(MySqlExecuteTool.ToolName, ChatScreen.MySqlToolsFor(all, _settings, true, access.Catalog()).Select(t => t.Name));
        Assert.False(PlanTools.Allowed(MySqlExecuteTool.ToolName));
        Assert.Contains(MySqlExecuteTool.ToolName, PlanTools.Mutating);

        Assert.Contains(Assistant.MySqlRule + " " + Assistant.MySqlWriteRule, Assistant.DefaultRules(false, tools: true, mysql: true, serverWrites: ServerWrites.MySql));
        Assert.DoesNotContain(Assistant.MySqlWriteRule, Assistant.DefaultRules(false, tools: true, mysql: true, serverWrites: ServerWrites.Postgres));
        Assert.Contains("mysql_execute", Assistant.MySqlWriteRule);
        Assert.Contains("only these kinds: changing data (INSERT, UPDATE, REPLACE).", MySqlExecuteTool.DescribeFor([ServerStatementKind.Data]));
    }

    [Fact]
    public void TheListingAndTheWizardsWarning_SayWhatAReadwriteConnectionMayDo()
    {
        _settings.MySqlConnectionsOffered = ["shop", "ro"];
        string listing = new MySqlConnectionsTool(Access(), () => _settings).Describe();
        Assert.Contains("- shop (default): 127.0.0.1:1 / sales, user u, read-write", listing);
        Assert.Contains("- ro: 127.0.0.1:1 / sales, user u", listing);
        Assert.EndsWith("a SELECT-only account is the real guard.", MySqlText.CanWrite(["ALL"]));
        Assert.Contains("as readwrite, mysql_execute may use those powers", MySqlText.CanWrite(["ALL"], readWrite: true));
    }
}

/// <summary><c>mysql_execute</c> against a real server (2026-10-05): a scratch table and a procedure made, used and dropped through the tool.</summary>
public sealed class LiveMySqlWriteTests
{
    private readonly AppSettingsData _settings = new() { MySqlTools = true, MySqlMode = "read-write", MySqlStatementsAllowed = [.. ServerStatementKinds.Names] };

    private (MySqlExecuteTool Tool, List<string> Asked) Tool(string? access)
    {
        var asked = new List<string>();
        var c = LiveMySql.Config!;
        var copy = new MySqlConnectionConfig { Host = c.Host, Port = c.Port, Database = c.Database, User = c.User, Password = c.Password, ConnectTimeoutSeconds = c.ConnectTimeoutSeconds, Access = access };
        var server = new MySqlAccess(() => new MySqlCatalog([new MySqlNamedConnection("live", copy, "test")], []));
        return (new MySqlExecuteTool(server, () => _settings, (_, _, _, sql, _) =>
        {
            asked.Add(sql);
            return Task.FromResult<bool?>(true);
        }), asked);
    }

    [LiveMySqlFact]
    public async Task AScratchTableAndProcedure_AreMadeUsedAndDropped()
    {
        var (tool, asked) = Tool("readwrite");
        string id = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        string table = "ns_write_" + id, proc = "ns_bump_" + id;
        try
        {
            Assert.StartsWith("Ran it in live/", await tool.RunAsync($"CREATE TABLE {table} (id INT PRIMARY KEY, a VARCHAR(10))", null, null, [], null, CancellationToken.None));
            Assert.StartsWith("Changed 2 rows in live/", await tool.RunAsync($"INSERT INTO {table} VALUES (1, @a), (2, 'b')", null, null, [new("a", "x")], null, CancellationToken.None));
            Assert.StartsWith("Ran it", await tool.RunAsync(
                $"CREATE PROCEDURE {proc}(IN n INT) BEGIN DECLARE i INT DEFAULT 0; WHILE i < n DO UPDATE {table} SET a = CONCAT(a, '+'); SET i = i + 1; END WHILE; END",
                null, null, [], null, CancellationToken.None));
            Assert.StartsWith("Ran it", await tool.RunAsync($"CALL {proc}(2)", null, null, [], null, CancellationToken.None));
            string rows = await tool.RunAsync($"SELECT a FROM {table} ORDER BY id", null, null, [], null, CancellationToken.None);
            Assert.Contains("| x++ |", rows);
            Assert.Equal(4, asked.Count);   // the read asked nothing
            Assert.StartsWith("Error: the server refused it", await tool.RunAsync($"INSERT INTO {table} VALUES (1, 'dup')", null, null, [], null, CancellationToken.None));
            Assert.StartsWith("Changed 2 rows", await tool.RunAsync($"DELETE FROM {table}", null, null, [], null, CancellationToken.None));
        }
        finally
        {
            await tool.RunAsync($"DROP PROCEDURE IF EXISTS {proc}", null, null, [], null, CancellationToken.None);
            Assert.StartsWith("Ran it", await tool.RunAsync($"DROP TABLE IF EXISTS {table}", null, null, [], null, CancellationToken.None));
        }

        var (readOnly, roAsked) = Tool(null);
        Assert.StartsWith("Error: connection 'live' is read-only", await readOnly.RunAsync("CREATE TABLE ns_never (a INT)", null, null, [], null, CancellationToken.None));
        Assert.Empty(roAsked);
    }
}
