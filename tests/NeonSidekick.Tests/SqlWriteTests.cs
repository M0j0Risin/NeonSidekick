using System.Globalization;
using NeonSidekick.App;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Plans;
using NeonSidekick.Settings;
using NeonSidekick.Sql;

namespace NeonSidekick.Tests;

/// <summary><c>sql_execute</c>'s gate (2026-10-05): ScriptDom's statement types, an allow-list; every data change inside counts; the rest refused.</summary>
public sealed class SqlWriteGateTests
{
    private static readonly IReadOnlyList<ServerStatementKind> Every = ServerStatementKinds.Resolve(ServerStatementKinds.Names);

    [Theory]
    [InlineData("INSERT INTO dbo.t (a) OUTPUT inserted.id VALUES (@a);")]
    [InlineData("INSERT INTO dbo.t (id) VALUES (NEXT VALUE FOR dbo.s)")]
    [InlineData("UPDATE t SET a = 1 WHERE id = @id")]
    [InlineData("WITH c AS (SELECT 1 AS a) INSERT INTO t SELECT a FROM c")]
    [InlineData("MERGE t USING s ON t.id = s.id WHEN MATCHED THEN UPDATE SET a = s.a WHEN NOT MATCHED THEN INSERT (id, a) VALUES (s.id, s.a);")]
    [InlineData("CREATE TABLE dbo.t (id int PRIMARY KEY, a nvarchar(10), b int REFERENCES u (id) ON DELETE CASCADE)")]
    [InlineData("CREATE NONCLUSTERED INDEX ix ON t (a)")]
    [InlineData("CREATE SCHEMA app")]
    [InlineData("SELECT a INTO #scratch FROM t")]
    [InlineData("SELECT 1")]
    [InlineData("INSERT INTO t VALUES ('GRANT; DROP')  -- GRANT in a comment")]
    public void Allows_UnderTheDefault(string sql) => Assert.Null(SqlWriteGate.Check(sql, ServerStatementKinds.Resolve(null)));

    [Theory]
    [InlineData("CREATE PROCEDURE dbo.p @x int AS BEGIN SET NOCOUNT ON; UPDATE t SET a = @x; SELECT 1; END")]
    [InlineData("CREATE OR ALTER TRIGGER tr ON t AFTER INSERT AS BEGIN UPDATE t SET a = 1 FROM inserted WHERE t.id = inserted.id; END")]
    [InlineData("EXEC dbo.p @x = 1")]
    [InlineData("EXEC sp_rename 'dbo.t.a', 'b', 'COLUMN'")]
    [InlineData("INSERT INTO t EXEC dbo.p 1")]
    [InlineData("DELETE FROM t WHERE id = 1")]
    [InlineData("TRUNCATE TABLE t")]
    [InlineData("DROP TABLE IF EXISTS t")]
    [InlineData("ALTER TABLE t ADD b int NULL")]
    [InlineData("UPDATE STATISTICS t")]
    [InlineData("ALTER INDEX ALL ON t REBUILD")]
    public void Allows_EveryKind_WhenAllAreTicked(string sql) => Assert.Null(SqlWriteGate.Check(sql, Every));

    [Theory]
    [InlineData("BEGIN TRAN", "uses BEGIN TRANSACTION, which sql_execute refuses: each call is a transaction")]
    [InlineData("COMMIT", "uses COMMIT TRANSACTION")]
    [InlineData("SET IDENTITY_INSERT t ON", "the session's settings")]
    [InlineData("SET @x = 1", "the session's settings")]
    [InlineData("USE master", "uses USE")]
    [InlineData("GRANT SELECT ON t TO bob", "uses GRANT, which sql_execute refuses: accounts")]
    [InlineData("CREATE LOGIN bob WITH PASSWORD = 'x'", "accounts")]
    [InlineData("CREATE USER bob WITHOUT LOGIN", "accounts")]
    [InlineData("ALTER ROLE db_owner ADD MEMBER bob", "accounts")]
    [InlineData("EXECUTE AS USER = 'dbo'", "accounts")]
    [InlineData("CREATE DATABASE x", "the server or a whole database")]
    [InlineData("DROP DATABASE x", "the server or a whole database")]
    [InlineData("DBCC CHECKDB", "the server or a whole database")]
    [InlineData("BACKUP DATABASE x TO DISK = 'c:\\x.bak'", "the server or a whole database")]
    [InlineData("BULK INSERT t FROM 'c:\\x.csv'", "it reaches files")]
    [InlineData("INSERT INTO t SELECT * FROM OPENROWSET(BULK 'c:\\x', SINGLE_CLOB) AS b", "uses OPENROWSET")]
    [InlineData("INSERT INTO t SELECT * FROM srv.db.dbo.t", "a linked server")]
    [InlineData("EXEC ('DROP TABLE t')", "EXEC of a string")]
    [InlineData("EXEC sp_executesql N'DROP TABLE t'", "uses sp_executesql")]
    [InlineData("EXEC xp_cmdshell 'dir'", "uses xp_cmdshell")]
    [InlineData("EXEC sp_configure 'show advanced options', 1", "uses sp_configure")]
    [InlineData("INSERT INTO t EXEC ('SELECT 1')", "EXEC of a string")]
    [InlineData("EXEC ('SELECT 1') AT linked", "a linked server")]
    [InlineData("WAITFOR DELAY '00:00:10'", "uses WAIT FOR")]
    [InlineData("INSERT INTO t VALUES (1); DELETE FROM t", "the SQL is 2 statements")]
    [InlineData("INSERT INTO t VALUES (1) DELETE FROM t", "the SQL is 2 statements")]
    [InlineData("CREATE TABLE t (a int)\nGO\nDROP TABLE t", "the SQL is 2 statements")]
    [InlineData("DECLARE @x int", "starts with DECLARE VARIABLE")]
    [InlineData("SELECT FROM WHERE", "does not parse")]
    [InlineData("  ", "give the statement")]
    public void Refuses_TheRest_WithTheSentence(string sql, string part)
    {
        string? refused = SqlWriteGate.Check(sql, Every);
        Assert.NotNull(refused);
        Assert.StartsWith("Error:", refused);
        Assert.Contains(part, refused, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("INSERT INTO t VALUES (1)", new[] { ServerStatementKind.Data })]
    [InlineData("MERGE t USING s ON t.id = s.id WHEN MATCHED THEN DELETE;", new[] { ServerStatementKind.Delete })]
    [InlineData("MERGE t USING s ON t.id = s.id WHEN MATCHED THEN UPDATE SET a = 1 WHEN NOT MATCHED BY SOURCE THEN DELETE;", new[] { ServerStatementKind.Data, ServerStatementKind.Delete })]
    [InlineData("INSERT INTO log SELECT x.id FROM (MERGE t USING s ON t.id = s.id WHEN MATCHED THEN DELETE OUTPUT deleted.id) AS x (id)", new[] { ServerStatementKind.Data, ServerStatementKind.Delete })]
    [InlineData("INSERT INTO t EXEC dbo.p", new[] { ServerStatementKind.Data, ServerStatementKind.Procedures })]
    [InlineData("SELECT a INTO t2 FROM t", new[] { ServerStatementKind.Create })]
    [InlineData("WITH c AS (SELECT 1 AS a) SELECT * FROM c", new[] { ServerStatementKind.Read })]
    [InlineData("CREATE FUNCTION dbo.f() RETURNS int AS BEGIN RETURN 1; END", new[] { ServerStatementKind.Procedures })]
    [InlineData("DROP PROCEDURE dbo.p", new[] { ServerStatementKind.Drop })]
    [InlineData("ALTER TABLE t DROP COLUMN a", new[] { ServerStatementKind.Alter })]
    [InlineData("SELEC 1", new[] { ServerStatementKind.Procedures })]   // a batch's first word alone is EXEC of that procedure (T-SQL's rule): a typo is a procedure call
    public void Kinds_EveryChangeInTheStatement(string sql, ServerStatementKind[] kinds) => Assert.Equal(kinds, SqlWriteGate.Kinds(sql));

    /// <summary>The read rules' last sentence (the shell's) stands apart from the one before it (the review, 2026-10-05: "params.Reach").</summary>
    [Fact]
    public void TheReadRules_KeepASpaceBeforeTheShellsSentence()
    {
        foreach (string rule in new[] { Assistant.SqlRule, Assistant.OracleRule, Assistant.MySqlRule, Assistant.PostgresRule, Assistant.SqliteRule })
        {
            Assert.Contains("params. Reach ", rule, StringComparison.Ordinal);
            Assert.DoesNotContain(".Reach", rule, StringComparison.Ordinal);
        }
    }

    /// <summary>CREATE OR ALTER VIEW changes the view that is there (the review, 2026-10-05).</summary>
    [Fact]
    public void Kinds_CreateOrAlterView_AlsoChangesStructure()
    {
        Assert.Equal([ServerStatementKind.Create, ServerStatementKind.Alter], SqlWriteGate.Kinds("CREATE OR ALTER VIEW v AS SELECT * FROM t"));
        Assert.Equal([ServerStatementKind.Create], SqlWriteGate.Kinds("CREATE VIEW v AS SELECT * FROM t"));
        Assert.StartsWith("Error: the SQL is changing structure", SqlWriteGate.Check("CREATE OR ALTER VIEW v AS SELECT * FROM t", ServerStatementKinds.Resolve(null)));
    }

    /// <summary>What may reach past the database a call names (the review, 2026-10-05): ScriptDom tells a database's part from a schema's.</summary>
    [Theory]
    [InlineData("DELETE FROM payroll.dbo.salaries WHERE id = 1", "scratch", true)]
    [InlineData("DELETE FROM dbo.salaries WHERE id = 1", "scratch", false)]
    [InlineData("DELETE FROM Scratch.dbo.t", "scratch", false)]
    [InlineData("DELETE FROM scratch.dbo.t", "", true)]
    [InlineData("INSERT INTO t SELECT x FROM other.dbo.u", "scratch", true)]
    [InlineData("UPDATE t SET a = other.dbo.f(1)", "scratch", true)]
    [InlineData("UPDATE t SET a = dbo.f(1), b = t.c", "scratch", false)]
    [InlineData("EXEC payroll.dbo.p", "scratch", true)]
    [InlineData("not sql at all (", "scratch", true)]
    public void NamesElsewhere_ADatabaseButThePlace(string sql, string place, bool elsewhere)
    {
        Assert.Equal(elsewhere, SqlWriteGate.NamesElsewhere(sql, place));
        Assert.Equal(elsewhere, SqlStatementKinds.Family.NamesElsewhere!(sql, place));
    }

    [Fact]
    public void Check_RefusesAKindNotTicked_AndARoutineIsCode()
    {
        Assert.Equal(
            "Error: the SQL is deleting (DELETE, TRUNCATE TABLE, a MERGE that deletes), which the user has not allowed; sql_execute may run changing data, creating, reading — the user ticks more in SQL statements allowed on the SQL tab of /tools",
            SqlWriteGate.Check("DELETE FROM t", ServerStatementKinds.Resolve(null)));
        Assert.StartsWith("Error: the SQL is procedures and triggers", SqlWriteGate.Check("CREATE PROCEDURE p AS SELECT 1", ServerStatementKinds.Resolve(null)));
        Assert.Null(SqlWriteGate.Kinds("GRANT SELECT ON t TO bob"));
        Assert.Null(SqlWriteGate.Kinds("EXEC xp_cmdshell 'dir'"));
    }
}

/// <summary><c>sql_execute</c> (2026-10-05): the order of its checks and its offering, no server needed until a change is allowed.</summary>
public sealed class SqlExecuteTests
{
    private readonly AppSettingsData _settings = new() { SqlTools = true, SqlConnectionsOffered = ["shop", "ro"], SqlMode = "read-write", SqlStatementsAllowed = [.. ServerStatementKinds.Names] };

    private static SqlConnectionConfig Config(string? access) => new() { Server = "127.0.0.1,1", Database = "Sales", Auth = "windows", Access = access, ConnectTimeoutSeconds = 1 };

    private SqlAccess Access() =>
        new(() => new SqlCatalog([new SqlNamedConnection("shop", Config("readwrite"), "x.json"), new SqlNamedConnection("ro", Config(null), "x.json")], []).Offered(_settings.SqlConnectionsOffered));

    [Fact]
    public async Task TheTool_ChecksInOrder_ThenAsks_PerConnectionAndDatabase()
    {
        var asked = new List<(string Connection, string Place, string Sql)>();
        var tool = new SqlExecuteTool(Access(), () => _settings, (_, connection, place, sql, _) =>
        {
            asked.Add((connection, place, sql));
            return Task.FromResult<bool?>(false);
        });

        Assert.Equal(ServerWriteText.Declined, await tool.RunAsync(" DELETE FROM t WHERE id = @id; ", null, null, [new("id", 1L)], null, CancellationToken.None));
        Assert.Equal(ServerWriteText.Declined, await tool.RunAsync("DELETE FROM t", "shop", "Archive", [], null, CancellationToken.None));
        Assert.Equal([("shop", "Sales", "DELETE FROM t WHERE id = @id;"), ("shop", "Archive", "DELETE FROM t")], asked);

        asked.Clear();
        Assert.Equal(ServerWriteText.ReadOnlyConnection(SqlStatementKinds.Family, "ro"), await tool.RunAsync("DELETE FROM t", "ro", null, [], null, CancellationToken.None));
        Assert.StartsWith("Error: the SQL uses xp_cmdshell", await tool.RunAsync("EXEC xp_cmdshell 'dir'", null, null, [], null, CancellationToken.None));
        _settings.SqlMode = "read-only";
        Assert.Equal(ServerWriteText.ReadOnlyMode(SqlStatementKinds.Family), await tool.RunAsync("INSERT INTO t VALUES (1)", null, null, [], null, CancellationToken.None));
        Assert.Empty(asked);
        _settings.SqlMode = "read-write";
        Assert.Equal(ServerWriteText.NoPane(SqlStatementKinds.Family), await new SqlExecuteTool(Access(), () => _settings, null).RunAsync("INSERT INTO t VALUES (1)", null, null, [], null, CancellationToken.None));
    }

    [Fact]
    public void TheTurn_OffersExecute_OnlyUnderReadWrite_AndTheRulesFollow()
    {
        var access = Access();
        var all = ChatScreen.SqlTools(access, () => _settings);
        Assert.Contains(SqlExecuteTool.ToolName, ChatScreen.SqlToolsFor(all, _settings, true, access.Catalog()).Select(t => t.Name));
        Assert.Equal(ServerWrites.Sql, ChatScreen.ServerWritesOf(ChatScreen.SqlToolsFor(all, _settings, true, access.Catalog())));
        Assert.DoesNotContain(SqlExecuteTool.ToolName, ChatScreen.SqlToolsFor(all, _settings, false, access.Catalog()).Select(t => t.Name));
        _settings.SqlConnectionsOffered = ["ro"];
        Assert.DoesNotContain(SqlExecuteTool.ToolName, ChatScreen.SqlToolsFor(all, _settings, true, access.Catalog()).Select(t => t.Name));
        Assert.False(PlanTools.Allowed(SqlExecuteTool.ToolName));
        Assert.Contains(SqlExecuteTool.ToolName, PlanTools.Mutating);

        Assert.Contains(Assistant.SqlRule + " " + Assistant.SqlWriteRule, Assistant.DefaultRules(false, tools: true, sql: true, serverWrites: ServerWrites.Sql));
        Assert.DoesNotContain(Assistant.SqlWriteRule, Assistant.DefaultRules(false, tools: true, sql: true, serverWrites: ServerWrites.MySql));
        Assert.Contains("only these kinds: changing data (INSERT, UPDATE, MERGE).", SqlExecuteTool.DescribeFor([ServerStatementKind.Data]));
    }

    [Fact]
    public void TheListing_SaysWhichConnectionsMayChange()
    {
        _settings.SqlConnectionsOffered = ["shop", "ro"];
        string listing = new SqlConnectionsTool(Access(), () => _settings).Describe();
        Assert.Contains("- shop (default): 127.0.0.1,1 / Sales, windows sign-in, read-write", listing);
        Assert.Contains("- ro: 127.0.0.1,1 / Sales, windows sign-in", listing);
        Assert.Equal(Microsoft.Data.SqlClient.ApplicationIntent.ReadOnly, Config(null).Builder().ApplicationIntent);
        Assert.Equal(Microsoft.Data.SqlClient.ApplicationIntent.ReadWrite, Config(null).Builder(readOnlyIntent: false).ApplicationIntent);
    }
}

/// <summary><c>sql_execute</c> against a real server (2026-10-05): a scratch table and a procedure made, used and dropped through the tool.</summary>
public sealed class LiveSqlWriteTests
{
    private readonly AppSettingsData _settings = new() { SqlTools = true, SqlMode = "read-write", SqlStatementsAllowed = [.. ServerStatementKinds.Names] };

    private (SqlExecuteTool Tool, List<string> Asked) Tool(string? access)
    {
        var asked = new List<string>();
        var c = LiveSql.Config!;
        var copy = new SqlConnectionConfig { Server = c.Server, Database = c.Database, Auth = c.Auth, User = c.User, Password = c.Password, TrustServerCertificate = c.TrustServerCertificate, Encrypt = c.Encrypt, ConnectTimeoutSeconds = c.ConnectTimeoutSeconds, Access = access };
        var server = new SqlAccess(() => new SqlCatalog([new SqlNamedConnection("live", copy, "test")], []));
        return (new SqlExecuteTool(server, () => _settings, (_, _, _, sql, _) =>
        {
            asked.Add(sql);
            return Task.FromResult<bool?>(true);
        }), asked);
    }

    [LiveSqlFact]
    public async Task AScratchTableAndProcedure_AreMadeUsedAndDropped()
    {
        var (tool, asked) = Tool("readwrite");
        string id = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        string table = "dbo.ns_write_" + id, proc = "dbo.ns_bump_" + id;
        try
        {
            Assert.StartsWith("Ran it in live/", await tool.RunAsync($"CREATE TABLE {table} (id int PRIMARY KEY, a nvarchar(10))", null, null, [], null, CancellationToken.None));
            string inserted = await tool.RunAsync($"INSERT INTO {table} OUTPUT inserted.id VALUES (1, @a), (2, N'b')", null, null, [new("a", "x")], null, CancellationToken.None);
            Assert.StartsWith("Changed 2 rows in live/", inserted);
            Assert.Contains("it returned 2 rows", inserted);
            Assert.StartsWith("Ran it", await tool.RunAsync($"CREATE PROCEDURE {proc} @n int AS BEGIN SET NOCOUNT ON; UPDATE {table} SET a = a + N'+' WHERE id <= @n; END", null, null, [], null, CancellationToken.None));
            Assert.StartsWith("Ran it", await tool.RunAsync($"EXEC {proc} @n = 1", null, null, [], null, CancellationToken.None));
            Assert.Contains("| x+ |", await tool.RunAsync($"SELECT a FROM {table} ORDER BY id", null, null, [], null, CancellationToken.None));
            Assert.Equal(4, asked.Count);   // the read asked nothing
            Assert.StartsWith("Error: the server refused it", await tool.RunAsync($"INSERT INTO {table} VALUES (1, N'dup')", null, null, [], null, CancellationToken.None));
            Assert.StartsWith("Changed 2 rows", await tool.RunAsync($"DELETE FROM {table}", null, null, [], null, CancellationToken.None));
        }
        finally
        {
            await tool.RunAsync($"DROP PROCEDURE IF EXISTS {proc}", null, null, [], null, CancellationToken.None);
            Assert.StartsWith("Ran it", await tool.RunAsync($"DROP TABLE IF EXISTS {table}", null, null, [], null, CancellationToken.None));
        }

        var (readOnly, roAsked) = Tool(null);
        Assert.StartsWith("Error: connection 'live' is read-only", await readOnly.RunAsync("CREATE TABLE dbo.ns_never (a int)", null, null, [], null, CancellationToken.None));
        Assert.Empty(roAsked);
    }
}
