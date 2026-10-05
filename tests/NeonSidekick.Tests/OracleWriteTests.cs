using System.Globalization;
using NeonSidekick.App;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Oracle;
using NeonSidekick.Plans;
using NeonSidekick.Settings;
using NeonSidekick.Sql;

namespace NeonSidekick.Tests;

/// <summary><c>oracle_execute</c>'s gate (2026-10-05): one statement or one PL/SQL unit of an allowed kind; the rest refused.</summary>
public sealed class OracleWriteGateTests
{
    private static readonly IReadOnlyList<ServerStatementKind> Every = ServerStatementKinds.Resolve(ServerStatementKinds.Names);

    [Theory]
    [InlineData("INSERT INTO t (a) VALUES (:a);")]
    [InlineData("INSERT INTO t (id) VALUES (s.NEXTVAL)")]
    [InlineData("UPDATE t SET a = 1 WHERE id = :id")]
    [InlineData("MERGE INTO t USING s ON (t.id = s.id) WHEN MATCHED THEN UPDATE SET t.a = s.a WHEN NOT MATCHED THEN INSERT (id, a) VALUES (s.id, s.a)")]
    [InlineData("INSERT ALL INTO t VALUES (1) INTO u VALUES (2) SELECT * FROM dual")]
    [InlineData("CREATE TABLE t (id NUMBER PRIMARY KEY, a VARCHAR2(10), b NUMBER REFERENCES u (id) ON DELETE CASCADE)")]
    [InlineData("CREATE OR REPLACE FORCE VIEW v AS SELECT * FROM t")]
    [InlineData("CREATE GLOBAL TEMPORARY TABLE g (a NUMBER) ON COMMIT PRESERVE ROWS")]
    [InlineData("CREATE UNIQUE INDEX ix ON t (a)")]
    [InlineData("SELECT 1 FROM dual")]
    [InlineData("INSERT INTO t VALUES (q'[GRANT; DROP]')  -- GRANT in a comment\n/")]
    public void Allows_UnderTheDefault(string sql) => Assert.Null(OracleWriteGate.Check(sql, ServerStatementKinds.Resolve(null)));

    [Theory]
    [InlineData("BEGIN UPDATE t SET a = 1; DELETE FROM u WHERE id = :id; END;")]
    [InlineData("DECLARE n NUMBER := 0; BEGIN SELECT COUNT(*) INTO n FROM t; UPDATE t SET a = n RETURNING id INTO n; END;\n/")]
    [InlineData("CREATE OR REPLACE PROCEDURE p (n IN NUMBER) AS BEGIN IF n > 0 THEN UPDATE t SET a = n; END IF; END;")]
    [InlineData("CREATE OR REPLACE TRIGGER tr BEFORE INSERT ON t FOR EACH ROW BEGIN :NEW.a := UPPER(:NEW.a); END;")]
    [InlineData("CREATE OR REPLACE PACKAGE BODY pk AS PROCEDURE p IS BEGIN NULL; END; END pk;")]
    [InlineData("BEGIN DBMS_STATS.GATHER_TABLE_STATS(USER, 'T'); END;")]
    [InlineData("CALL p(1)")]
    [InlineData("DELETE FROM t WHERE id = 1")]
    [InlineData("TRUNCATE TABLE t")]
    [InlineData("DROP TABLE t PURGE")]
    [InlineData("ALTER TABLE t ADD (b NUMBER)")]
    [InlineData("RENAME t TO u")]
    [InlineData("COMMENT ON TABLE t IS 'x'")]
    [InlineData("ANALYZE TABLE t COMPUTE STATISTICS")]
    public void Allows_EveryKind_WhenAllAreTicked(string sql) => Assert.Null(OracleWriteGate.Check(sql, Every));

    [Theory]
    [InlineData("COMMIT", "uses COMMIT, which oracle_execute refuses: each call is a transaction")]
    [InlineData("SET TRANSACTION READ WRITE", "uses SET")]
    [InlineData("ALTER SESSION SET CURRENT_SCHEMA = SYS", "uses ALTER SESSION")]
    [InlineData("ALTER SYSTEM KILL SESSION '1,2'", "uses ALTER SYSTEM")]
    [InlineData("GRANT SELECT ON t TO bob", "uses GRANT, which oracle_execute refuses: accounts")]
    [InlineData("CREATE USER bob IDENTIFIED BY x", "uses CREATE USER")]
    [InlineData("CREATE PUBLIC SYNONYM s FOR t", "uses CREATE PUBLIC")]
    [InlineData("CREATE DIRECTORY d AS '/tmp'", "uses CREATE DIRECTORY")]
    [InlineData("CREATE DATABASE LINK l CONNECT TO x IDENTIFIED BY y USING 'z'", "uses CREATE DATABASE")]
    [InlineData("LOCK TABLE t IN EXCLUSIVE MODE", "uses LOCK")]
    [InlineData("INSERT INTO t SELECT * FROM t@remote", "a database link")]
    [InlineData("BEGIN EXECUTE IMMEDIATE 'DROP TABLE t'; END;", "uses EXECUTE IMMEDIATE")]
    [InlineData("BEGIN UTL_FILE.FREMOVE('D', 'f'); END;", "uses UTL_FILE")]
    [InlineData("BEGIN \"DBMS_SQL\".PARSE(1, 'x', 1); END;", "uses DBMS_SQL")]
    [InlineData("INSERT INTO t VALUES (1) RETURNING id INTO :x", "uses RETURNING … INTO")]
    [InlineData("INSERT INTO t VALUES (1); DELETE FROM t", "the SQL is 2 statements")]
    [InlineData("BEGIN NULL; END;\n/\nDROP TABLE t", "the SQL is 2 statements")]
    [InlineData("FLASHBACK TABLE t TO BEFORE DROP", "uses FLASHBACK")]
    [InlineData("EXPLAIN PLAN FOR SELECT 1 FROM dual", "starts with EXPLAIN")]
    [InlineData("INSERT INTO t VALUES ('never ends", "never ends")]
    [InlineData("  ", "give the statement")]
    public void Refuses_TheRest_WithTheSentence(string sql, string part)
    {
        string? refused = OracleWriteGate.Check(sql, Every);
        Assert.NotNull(refused);
        Assert.StartsWith("Error:", refused);
        Assert.Contains(part, refused, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("INSERT INTO t VALUES (1)", new[] { ServerStatementKind.Data })]
    [InlineData("MERGE INTO t USING s ON (t.id = s.id) WHEN MATCHED THEN UPDATE SET t.a = 1 DELETE WHERE t.a = 0", new[] { ServerStatementKind.Data, ServerStatementKind.Delete })]
    [InlineData("WITH c AS (SELECT 1 a FROM dual) SELECT * FROM c", new[] { ServerStatementKind.Read })]
    [InlineData("BEGIN NULL; END;", new[] { ServerStatementKind.Procedures })]
    [InlineData("CREATE OR REPLACE EDITIONABLE FUNCTION f RETURN NUMBER IS BEGIN RETURN 1; END;", new[] { ServerStatementKind.Procedures })]
    [InlineData("CREATE MATERIALIZED VIEW mv AS SELECT 1 a FROM dual", new[] { ServerStatementKind.Create })]
    [InlineData("DROP PACKAGE pk", new[] { ServerStatementKind.Drop })]
    [InlineData("ALTER PROCEDURE p COMPILE", new[] { ServerStatementKind.Procedures })]
    public void Kinds_EveryChangeInTheStatement(string sql, ServerStatementKind[] kinds) => Assert.Equal(kinds, OracleWriteGate.Kinds(sql));

    [Fact]
    public void TheBody_KeepsAPlSqlUnitsSemicolon_AndDropsPlainSqls()
    {
        Assert.Equal("INSERT INTO t VALUES (1)", OracleWriteGate.Body("INSERT INTO t VALUES (1);\n/"));
        Assert.Equal("BEGIN NULL; END;", OracleWriteGate.Body("BEGIN NULL; END;\n/\n-- done"));
        Assert.Equal("CREATE OR REPLACE PROCEDURE p AS BEGIN NULL; END;", OracleWriteGate.Body("CREATE OR REPLACE PROCEDURE p AS BEGIN NULL; END;"));
        Assert.StartsWith("Error: the SQL is procedures and triggers", OracleWriteGate.Check("BEGIN NULL; END;", ServerStatementKinds.Resolve(null)));
    }
}

/// <summary><c>oracle_execute</c> (2026-10-05): the order of its checks and its offering, no server needed until a change is allowed.</summary>
public sealed class OracleExecuteTests
{
    private readonly AppSettingsData _settings = new() { OracleTools = true, OracleConnectionsOffered = ["hr", "ro"], OracleMode = "read-write", OracleStatementsAllowed = [.. ServerStatementKinds.Names] };

    private static OracleConnectionConfig Config(string? access) => new() { DataSource = "127.0.0.1:1/X", User = "hr", Schema = "HR", Password = "p", Access = access, ConnectTimeoutSeconds = 1 };

    private OracleAccess Access() =>
        new(() => new OracleCatalog([new OracleNamedConnection("hr", Config("readwrite"), "x.json"), new OracleNamedConnection("ro", Config(null), "x.json")], []).Offered(_settings.OracleConnectionsOffered));

    [Fact]
    public async Task TheTool_ChecksInOrder_ThenAsks_PerConnectionAndSchema()
    {
        var asked = new List<(string Connection, string Place, string Sql)>();
        var tool = new OracleExecuteTool(Access(), () => _settings, (_, connection, place, sql, _) =>
        {
            asked.Add((connection, place, sql));
            return Task.FromResult<bool?>(false);
        });

        Assert.Equal(ServerWriteText.Declined, await tool.RunAsync("DELETE FROM t WHERE id = :id;", null, null, [new("id", 1L)], null, CancellationToken.None));
        Assert.Equal(ServerWriteText.Declined, await tool.RunAsync("BEGIN NULL; END;\n/", "hr", "app", [], null, CancellationToken.None));
        Assert.Equal([("hr", "HR", "DELETE FROM t WHERE id = :id"), ("hr", "APP", "BEGIN NULL; END;")], asked);

        asked.Clear();
        Assert.Equal(ServerWriteText.ReadOnlyConnection(OracleStatementKinds.Family, "ro"), await tool.RunAsync("DELETE FROM t", "ro", null, [], null, CancellationToken.None));
        Assert.Equal(OracleText.BadSchema("1bad"), await tool.RunAsync("DELETE FROM t", null, "1bad", [], null, CancellationToken.None));
        Assert.StartsWith("Error: the SQL uses UTL_HTTP", await tool.RunAsync("SELECT UTL_HTTP.REQUEST('x') FROM dual", null, null, [], null, CancellationToken.None));
        _settings.OracleMode = "read-only";
        Assert.Equal(ServerWriteText.ReadOnlyMode(OracleStatementKinds.Family), await tool.RunAsync("INSERT INTO t VALUES (1)", null, null, [], null, CancellationToken.None));
        Assert.Empty(asked);
        _settings.OracleMode = "read-write";
        Assert.Equal(ServerWriteText.NoPane(OracleStatementKinds.Family), await new OracleExecuteTool(Access(), () => _settings, null).RunAsync("INSERT INTO t VALUES (1)", null, null, [], null, CancellationToken.None));
    }

    [Fact]
    public void TheTurn_OffersExecute_OnlyUnderReadWrite_AndTheRulesFollow()
    {
        var access = Access();
        var all = ChatScreen.OracleTools(access, () => _settings);
        Assert.Contains(OracleExecuteTool.ToolName, ChatScreen.OracleToolsFor(all, _settings, true, access.Catalog()).Select(t => t.Name));
        Assert.Equal(ServerWrites.Oracle, ChatScreen.ServerWritesOf(ChatScreen.OracleToolsFor(all, _settings, true, access.Catalog())));
        Assert.DoesNotContain(OracleExecuteTool.ToolName, ChatScreen.OracleToolsFor(all, _settings, false, access.Catalog()).Select(t => t.Name));
        _settings.OracleConnectionsOffered = ["ro"];
        Assert.DoesNotContain(OracleExecuteTool.ToolName, ChatScreen.OracleToolsFor(all, _settings, true, access.Catalog()).Select(t => t.Name));
        Assert.False(PlanTools.Allowed(OracleExecuteTool.ToolName));
        Assert.Contains(OracleExecuteTool.ToolName, PlanTools.Mutating);

        Assert.Contains(Assistant.OracleRule + " " + Assistant.OracleWriteRule, Assistant.DefaultRules(false, tools: true, oracle: true, serverWrites: ServerWrites.Oracle));
        Assert.DoesNotContain(Assistant.OracleWriteRule, Assistant.DefaultRules(false, tools: true, oracle: true, serverWrites: ServerWrites.Sql));
        Assert.Contains("only these kinds: changing data (INSERT, UPDATE, MERGE).", OracleExecuteTool.DescribeFor([ServerStatementKind.Data]));
    }

    [Fact]
    public void TheListingTheWarningAndTheSession()
    {
        _settings.OracleConnectionsOffered = ["hr", "ro"];
        string listing = new OracleConnectionsTool(Access(), () => _settings).Describe();
        Assert.Contains("- hr (default): 127.0.0.1:1/X, user hr, schema HR, read-write", listing);
        Assert.Contains("- ro: 127.0.0.1:1/X, user hr, schema HR", listing);
        Assert.EndsWith("a read-only account is the real guard.", OracleText.CanWrite(["INSERT ANY TABLE"]));
        Assert.Contains("as readwrite, oracle_execute may use those powers", OracleText.CanWrite(["INSERT ANY TABLE"], readWrite: true));
        Assert.True(OracleExecuteTool.IsTooNew(new SqlRun(SqlOutcome.Failed, "ORA-01466: unable to read data - table definition has changed", "x", "", [], TimeSpan.Zero)));
        Assert.False(OracleExecuteTool.IsTooNew(new SqlRun(SqlOutcome.Failed, "ORA-00942: table or view does not exist", "x", "", [], TimeSpan.Zero)));
        Assert.True(Config(null).Builder().Pooling);
        Assert.False(Config(null).Builder(pooling: false).Pooling);
    }
}

/// <summary><c>oracle_execute</c> against a real database (2026-10-05): a scratch table and a procedure made, used and dropped through the tool.</summary>
public sealed class LiveOracleWriteTests
{
    private readonly AppSettingsData _settings = new() { OracleTools = true, OracleMode = "read-write", OracleStatementsAllowed = [.. ServerStatementKinds.Names] };

    private (OracleExecuteTool Tool, List<string> Asked) Tool(string? access)
    {
        var asked = new List<string>();
        var c = LiveOracle.Config!;
        var copy = new OracleConnectionConfig { DataSource = c.DataSource, User = c.User, Password = c.Password, ConnectTimeoutSeconds = c.ConnectTimeoutSeconds, Access = access };
        var server = new OracleAccess(() => new OracleCatalog([new OracleNamedConnection("live", copy, "test")], []));
        return (new OracleExecuteTool(server, () => _settings, (_, _, _, sql, _) =>
        {
            asked.Add(sql);
            return Task.FromResult<bool?>(true);
        }), asked);
    }

    [LiveOracleFact]
    public async Task AScratchTableAndProcedure_AreMadeUsedAndDropped()
    {
        var (tool, asked) = Tool("readwrite");
        string id = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        string table = "NS_WRITE_" + id, proc = "NS_BUMP_" + id;
        try
        {
            Assert.StartsWith("Ran it in live/", await tool.RunAsync($"CREATE TABLE {table} (id NUMBER PRIMARY KEY, a VARCHAR2(10));", null, null, [], null, CancellationToken.None));
            Assert.StartsWith("Changed 1 row in live/", await tool.RunAsync($"INSERT INTO {table} VALUES (1, :a)", null, null, [new("a", "x")], null, CancellationToken.None));
            Assert.StartsWith("Changed 1 row", await tool.RunAsync($"INSERT INTO {table} VALUES (2, 'b')", null, null, [], null, CancellationToken.None));
            Assert.StartsWith("Ran it", await tool.RunAsync(
                $"CREATE OR REPLACE PROCEDURE {proc} (n IN NUMBER) AS BEGIN FOR i IN 1 .. n LOOP UPDATE {table} SET a = a || '+' WHERE id = 1; END LOOP; END;\n/",
                null, null, [], null, CancellationToken.None));
            Assert.StartsWith("Ran it", await tool.RunAsync($"BEGIN {proc}(2); END;", null, null, [], null, CancellationToken.None));
            Assert.Contains("| x++ |", await tool.RunAsync($"SELECT a FROM {table} ORDER BY id", null, null, [], null, CancellationToken.None));   // read again past ORA-01466
            Assert.Equal(5, asked.Count);   // the read asked nothing
            Assert.StartsWith("Error: the server refused it", await tool.RunAsync($"INSERT INTO {table} VALUES (1, 'dup')", null, null, [], null, CancellationToken.None));
            Assert.StartsWith("Changed 2 rows", await tool.RunAsync($"DELETE FROM {table}", null, null, [], null, CancellationToken.None));
        }
        finally
        {
            await tool.RunAsync($"DROP PROCEDURE {proc}", null, null, [], null, CancellationToken.None);
            Assert.StartsWith("Ran it", await tool.RunAsync($"DROP TABLE {table} PURGE", null, null, [], null, CancellationToken.None));
        }

        var (readOnly, roAsked) = Tool(null);
        Assert.StartsWith("Error: connection 'live' is read-only", await readOnly.RunAsync("CREATE TABLE NS_NEVER (a NUMBER)", null, null, [], null, CancellationToken.None));
        Assert.Empty(roAsked);
    }
}
