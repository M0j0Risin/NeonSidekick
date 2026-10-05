using System.Globalization;
using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Diagnostics;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Plans;
using NeonSidekick.Postgres;
using NeonSidekick.Settings;
using NeonSidekick.Sql;

namespace NeonSidekick.Tests;

/// <summary>The server families' shared write pieces (2026-10-05): the mode, the statement kinds, the access key, the wording.</summary>
public sealed class ServerWriteTests
{
    private static readonly ServerWriteFamily Family = PostgresStatementKinds.Family;

    [Fact]
    public void TheMode_ParsesBothWords_AndFallsBackToReadOnly()
    {
        Assert.Equal("read-only", DatabaseWriteModes.Default);
        var fresh = new AppSettingsData();
        Assert.Equal(["read-only", "read-only", "read-only", "read-only"], new[] { fresh.SqlMode, fresh.OracleMode, fresh.MySqlMode, fresh.PostgresMode });
        Assert.Equal(DatabaseWriteMode.ReadWrite, DatabaseWriteModes.Resolve(" Read-Write ", Family));
        Assert.Equal(DatabaseWriteMode.ReadOnly, DatabaseWriteModes.Resolve("read-only", Family));
        Assert.Equal(DatabaseWriteMode.ReadOnly, DatabaseWriteModes.Resolve("yolo", Family));
        Assert.True(DatabaseWriteModes.IsReadWrite("read-write"));
        Assert.False(DatabaseWriteModes.IsReadWrite(null));
        Assert.Equal("the PostgreSQL tools only read", DatabaseWriteModes.Describe("read-only", Family));
        Assert.Equal("postgres_execute may change a readwrite connection's database, each change allowed on a pane", DatabaseWriteModes.Describe("read-write", Family));
    }

    [Fact]
    public void TheKinds_ChangingDataCreatingAndReadingByDefault_ReadCaseBlind()
    {
        var fresh = new AppSettingsData();
        Assert.All(new[] { fresh.SqlStatementsAllowed, fresh.OracleStatementsAllowed, fresh.MySqlStatementsAllowed, fresh.PostgresStatementsAllowed }, k => Assert.Equal(["data", "create", "read"], k));
        Assert.Equal([ServerStatementKind.Data, ServerStatementKind.Create, ServerStatementKind.Read], ServerStatementKinds.Resolve(null));
        Assert.Equal([ServerStatementKind.Procedures, ServerStatementKind.Read], ServerStatementKinds.Resolve([" READ ", "Procedures", "nonsense"]));
        Assert.Empty(ServerStatementKinds.Resolve([]));
        Assert.Equal(Enum.GetValues<ServerStatementKind>().Length, ServerStatementKinds.Names.Length);
        Assert.Equal("procedures and triggers", ServerStatementKinds.Title(ServerStatementKind.Procedures));
        Assert.Equal("changing data (INSERT, UPDATE, MERGE); deleting (DELETE, TRUNCATE, a MERGE that deletes)", ServerStatementKinds.Describe([ServerStatementKind.Data, ServerStatementKind.Delete], Family));

        // The clone copies both, the list by value.
        string dir = Directory.CreateTempSubdirectory("neon-writes-").FullName;
        try
        {
            using var settings = new AppSettings(dir);
            settings.Update(d => { d.PostgresMode = "read-write"; d.PostgresStatementsAllowed = ["drop"]; });
            var copy = settings.Current;
            Assert.Equal("read-write", copy.PostgresMode);
            Assert.Equal(["drop"], copy.PostgresStatementsAllowed);
            settings.Update(d => d.PostgresStatementsAllowed!.Add("data"));
            Assert.Equal(["drop"], copy.PostgresStatementsAllowed);   // a snapshot: the list was copied
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void TheAccessKey_ReadByDefault_ReadwriteOptIn_AnythingElseAProblem()
    {
        Assert.Null(ConnectionAccess.Problem(null));
        Assert.Null(ConnectionAccess.Problem(" READ "));
        Assert.Null(ConnectionAccess.Problem("readwrite"));
        Assert.Equal("\"access\" is 'write'; it must be read or readwrite", ConnectionAccess.Problem("write"));
        Assert.True(ConnectionAccess.IsReadWrite(" ReadWrite "));
        Assert.False(ConnectionAccess.IsReadWrite("read"));
        Assert.Null(ConnectionAccess.Stored(false));   // read is the default, left out of the file
        Assert.Equal("readwrite", ConnectionAccess.Stored(true));

        Assert.Equal(ConnectionAccess.Problem("rw"), new PostgresConnectionConfig { Host = "db", User = "u", Access = "rw" }.Problem);
        Assert.Equal(ConnectionAccess.Problem("rw"), new MySql.MySqlConnectionConfig { Host = "db", User = "u", Access = "rw" }.Problem);
        Assert.Equal(ConnectionAccess.Problem("rw"), new Oracle.OracleConnectionConfig { DataSource = "db:1521/x", User = "u", Access = "rw" }.Problem);
        Assert.Equal(ConnectionAccess.Problem("rw"), new SqlConnectionConfig { Server = "db", Auth = "windows", Access = "rw" }.Problem);
        Assert.True(new PostgresConnectionConfig { Access = "readwrite" }.IsReadWrite);
    }

    [Fact]
    public void TheWording()
    {
        Assert.Equal("The model wants to change shop/sales (PostgreSQL, database sales):\nDELETE FROM t", ServerWriteText.AllowCaption(Family, "shop", "sales", " DELETE FROM t "));
        Assert.Equal("Change a PostgreSQL database?", ServerWriteText.AllowTitle(Family));
        Assert.Equal(
            "The model wants to change shop/sales (PostgreSQL, database sales):\nDELETE FROM t\nIt qualifies a name with something other than the database, so it may reach another one; a session's allow never covers it.",
            ServerWriteText.AllowCaption(Family, "shop", "sales", "DELETE FROM t", elsewhere: true));
        Assert.Null(Family.NamesElsewhere);
        Assert.Equal(
            "Error: mysql_execute cannot tell where the routine's body ends; write the body as BEGIN … END and quote any name spelled like a block word (BEGIN, END, a label)",
            ServerWriteText.UnclearBody(NeonSidekick.MySql.MySqlStatementKinds.Family));
        Assert.Equal("shop/sales: 2 rows changed by INSERT …", ServerWriteText.AuditLogLine("shop/sales", 2, "INSERT …"));
        Assert.Equal("shop/sales: ran by CREATE TABLE t (a int)", ServerWriteText.AuditLogLine("shop/sales", null, "CREATE TABLE t (a int)"));
        Assert.Equal(
            "Error: connection 'shop' is read-only (\"access\": \"read\" in postgres.json); the user makes it readwrite to allow changes",
            ServerWriteText.ReadOnlyConnection(Family, "shop"));
        Assert.Equal(
            "Error: PostgreSQL mode is read-only, so nothing may change a PostgreSQL database; the user switches it to read-write on the PostgreSQL tab of /tools",
            ServerWriteText.ReadOnlyMode(Family));

        var run = new SqlRun(SqlOutcome.Ok, "", "shop", "sales", [], TimeSpan.FromMilliseconds(4)) { Changes = 3 };
        Assert.Equal("Changed 3 rows in shop/sales (4 ms)", ServerWriteText.Executed(run, 10, 1000));
        Assert.Equal("Ran it in shop/sales (4 ms)", ServerWriteText.Executed(run with { Changes = null }, 10, 1000));
        var returned = new SqlRun(SqlOutcome.Ok, "", "shop", "sales", [new SqlGrid(["id"], [["7"]], true)], TimeSpan.FromMilliseconds(4)) { Changes = 2 };
        Assert.StartsWith("Changed 2 rows in shop/sales (4 ms); it returned 1 row+ — the first 1 shown\n\n| id |", ServerWriteText.Executed(returned, 1, 1000));

        Assert.Equal("", ServerWriteText.AccessNote(Family, readWrite: false, modeReadWrite: true));
        Assert.Equal(", read-write", ServerWriteText.AccessNote(Family, readWrite: true, modeReadWrite: true));
        Assert.Equal(", readwrite (but PostgreSQL mode is read-only)", ServerWriteText.AccessNote(Family, readWrite: true, modeReadWrite: false));
    }
}

/// <summary><c>postgres_execute</c>'s gate (2026-10-05): one statement of an allowed kind; accounts, the server, the outside, dynamic SQL and code's kin refused.</summary>
public sealed class PostgresWriteGateTests
{
    [Theory]
    [InlineData("INSERT INTO t (a) VALUES (@a) RETURNING id;")]
    [InlineData("INSERT INTO t VALUES (nextval('s'))")]
    [InlineData("UPDATE t SET a = 1 WHERE id = @id")]
    [InlineData("INSERT INTO t VALUES (1) ON CONFLICT (id) DO UPDATE SET a = excluded.a")]
    [InlineData("MERGE INTO t USING s ON t.id = s.id WHEN MATCHED THEN UPDATE SET a = s.a WHEN NOT MATCHED THEN INSERT VALUES (s.id, s.a)")]
    [InlineData("WITH c AS (SELECT 1 AS a) INSERT INTO t SELECT * FROM c")]
    [InlineData("CREATE TABLE t (id serial PRIMARY KEY, a text, b int REFERENCES u (id) ON DELETE CASCADE)")]
    [InlineData("CREATE UNIQUE INDEX t_a ON t (a)")]
    [InlineData("CREATE MATERIALIZED VIEW mv AS SELECT 1")]
    [InlineData("CREATE TEMP TABLE x (a int)")]
    [InlineData("CREATE SCHEMA app")]
    [InlineData("SELECT 1")]
    [InlineData("INSERT INTO t VALUES ('grant; revoke')  -- GRANT in a comment")]
    public void Allows_UnderTheDefault(string sql) => Assert.Null(PostgresWriteGate.Check(sql, ServerStatementKinds.Resolve(null)));

    [Theory]
    [InlineData("CREATE FUNCTION f() RETURNS int LANGUAGE sql AS $$ SELECT 1; $$")]
    [InlineData("DO $$ BEGIN PERFORM 1; DELETE FROM t; END $$")]
    [InlineData("CALL p(1)")]
    [InlineData("CREATE TRIGGER tr AFTER INSERT ON t FOR EACH ROW EXECUTE FUNCTION f()")]
    [InlineData("DROP TABLE t")]
    [InlineData("TRUNCATE t")]
    [InlineData("VACUUM ANALYZE t")]
    [InlineData("ALTER TABLE t ADD COLUMN b int")]
    [InlineData("COMMENT ON TABLE t IS 'x'")]
    [InlineData("REFRESH MATERIALIZED VIEW mv")]
    public void Allows_EveryKind_WhenAllAreTicked(string sql) => Assert.Null(PostgresWriteGate.Check(sql, ServerStatementKinds.Resolve(ServerStatementKinds.Names)));

    [Theory]
    [InlineData("BEGIN", "uses BEGIN, which postgres_execute refuses: each call is a transaction")]
    [InlineData("COMMIT", "uses COMMIT")]
    [InlineData("GRANT SELECT ON t TO bob", "uses GRANT, which postgres_execute refuses: accounts")]
    [InlineData("SET search_path = x", "uses SET")]
    [InlineData("COPY t FROM '/etc/passwd'", "uses COPY, which postgres_execute refuses: it reaches files")]
    [InlineData("PREPARE p AS DELETE FROM t", "uses PREPARE")]
    [InlineData("LOCK TABLE t", "uses LOCK")]
    [InlineData("CREATE ROLE bob", "uses CREATE ROLE, which postgres_execute refuses: accounts")]
    [InlineData("CREATE DATABASE x", "uses CREATE DATABASE")]
    [InlineData("DROP DATABASE x", "uses DROP DATABASE")]
    [InlineData("CREATE EXTENSION dblink", "uses CREATE EXTENSION")]
    [InlineData("ALTER SYSTEM SET work_mem = '1GB'", "uses ALTER SYSTEM")]
    [InlineData("ALTER TABLE t OWNER TO bob", "uses OWNER TO")]
    [InlineData("INSERT INTO t VALUES (pg_read_file('/etc/passwd'))", "uses pg_read_file()")]
    [InlineData("UPDATE t SET a = \"pg_sleep\"(10)", "uses pg_sleep()")]
    [InlineData("INSERT INTO t VALUES (1); DELETE FROM t", "the SQL is 2 statements")]
    [InlineData("CREATE FOO bar", "starts with CREATE FOO, which is not a statement")]
    [InlineData("SHOW work_mem", "starts with SHOW")]
    [InlineData("DELETE FROM t WHERE id = $1", "$1 placeholder")]
    [InlineData("  ", "give the statement")]
    public void Refuses_TheRest_WithTheSentence(string sql, string part)
    {
        string? refused = PostgresWriteGate.Check(sql, ServerStatementKinds.Resolve(ServerStatementKinds.Names));
        Assert.NotNull(refused);
        Assert.StartsWith("Error:", refused);
        Assert.Contains(part, refused);
    }

    [Theory]
    [InlineData("INSERT INTO t VALUES (1)", new[] { ServerStatementKind.Data })]
    [InlineData("DELETE FROM t", new[] { ServerStatementKind.Delete })]
    [InlineData("MERGE INTO t USING s ON t.id = s.id WHEN MATCHED THEN DELETE", new[] { ServerStatementKind.Delete })]
    [InlineData("MERGE INTO t USING s ON t.id = s.id WHEN MATCHED THEN UPDATE SET a = 1 WHEN MATCHED AND s.x THEN DELETE", new[] { ServerStatementKind.Data, ServerStatementKind.Delete })]
    [InlineData("WITH d AS (DELETE FROM t RETURNING *) INSERT INTO log SELECT * FROM d", new[] { ServerStatementKind.Data, ServerStatementKind.Delete })]
    [InlineData("WITH d AS (DELETE FROM t RETURNING *) SELECT * FROM d", new[] { ServerStatementKind.Delete })]
    [InlineData("WITH c AS (SELECT 1) SELECT * FROM c", new[] { ServerStatementKind.Read })]
    [InlineData("TABLE t", new[] { ServerStatementKind.Read })]
    [InlineData("CREATE TABLE t (a int REFERENCES u ON DELETE CASCADE ON UPDATE CASCADE)", new[] { ServerStatementKind.Create })]
    [InlineData("CREATE OR REPLACE FUNCTION f() RETURNS int AS 'select 1' LANGUAGE sql", new[] { ServerStatementKind.Procedures })]
    [InlineData("CREATE CONSTRAINT TRIGGER tr AFTER INSERT ON t FOR EACH ROW EXECUTE FUNCTION f()", new[] { ServerStatementKind.Procedures })]
    [InlineData("ALTER FUNCTION f() RENAME TO g", new[] { ServerStatementKind.Procedures })]
    [InlineData("DROP FUNCTION f()", new[] { ServerStatementKind.Drop })]
    [InlineData("ALTER TABLE t RENAME TO u", new[] { ServerStatementKind.Alter })]
    [InlineData("ANALYZE t", new[] { ServerStatementKind.Upkeep })]
    [InlineData("DO $$ BEGIN END $$", new[] { ServerStatementKind.Procedures })]
    public void Kinds_EveryChangeInTheStatement(string sql, ServerStatementKind[] kinds) => Assert.Equal(kinds, PostgresWriteGate.Kinds(sql));

    /// <summary>CREATE OR REPLACE VIEW changes the view that is there (the review, 2026-10-05); a function's is code either way.</summary>
    [Fact]
    public void Kinds_OrReplace_AViewChangesStructure()
    {
        Assert.Equal([ServerStatementKind.Create, ServerStatementKind.Alter], PostgresWriteGate.Kinds("CREATE OR REPLACE VIEW v AS SELECT * FROM t"));
        Assert.Equal([ServerStatementKind.Create, ServerStatementKind.Alter], PostgresWriteGate.Kinds("CREATE OR REPLACE TEMP VIEW v AS SELECT 1"));
        Assert.Equal([ServerStatementKind.Procedures], PostgresWriteGate.Kinds("CREATE OR REPLACE FUNCTION f() RETURNS int AS 'select 1' LANGUAGE sql"));
        Assert.StartsWith("Error: the SQL is changing structure", PostgresWriteGate.Check("CREATE OR REPLACE VIEW v AS SELECT 1", ServerStatementKinds.Resolve(null)));
    }

    [Fact]
    public void Check_RefusesAKindNotTicked_NamingWhatIs()
    {
        IReadOnlyList<ServerStatementKind> data = [ServerStatementKind.Data];
        Assert.Null(PostgresWriteGate.Check("UPDATE t SET a = 1", data));
        Assert.Equal(
            "Error: the SQL is dropping (DROP TABLE, INDEX, VIEW, SEQUENCE, TYPE, DOMAIN, SCHEMA, FUNCTION, PROCEDURE, TRIGGER), which the user has not allowed; postgres_execute may run changing data — the user ticks more in PostgreSQL statements allowed on the PostgreSQL tab of /tools",
            PostgresWriteGate.Check("DROP TABLE t", data));
        // A WITH that deletes needs deleting, whatever it ends in.
        Assert.StartsWith("Error: the SQL is deleting", PostgresWriteGate.Check("WITH d AS (DELETE FROM t RETURNING *) INSERT INTO log SELECT * FROM d", data));
        // Creating a function is code, not creating: a function made under creating could be called by the next INSERT.
        Assert.StartsWith("Error: the SQL is procedures and triggers", PostgresWriteGate.Check("CREATE FUNCTION f() RETURNS int AS 'select 1' LANGUAGE sql", ServerStatementKinds.Resolve(null)));
        // The structural refusals come first, whatever is ticked.
        Assert.StartsWith("Error: the SQL uses GRANT", PostgresWriteGate.Check("GRANT ALL ON t TO bob", data));
        Assert.Null(PostgresWriteGate.Kinds("GRANT ALL ON t TO bob"));
        Assert.Null(PostgresWriteGate.Kinds("CREATE ROLE bob"));
    }
}

/// <summary><c>postgres_execute</c> (2026-10-05): the order of its checks, its offering, the rules — no server needed until a change is allowed.</summary>
public sealed class PostgresExecuteTests
{
    private readonly AppSettingsData _settings = new() { PostgresTools = true, PostgresConnectionsOffered = ["shop", "ro"], PostgresMode = "read-write", PostgresStatementsAllowed = [.. ServerStatementKinds.Names] };

    // Port 9 (discard): never reached, since every call here is refused, declined or asked first.
    private static PostgresConnectionConfig Config(string? access) => new() { Host = "127.0.0.1", Port = 9, Database = "sales", User = "u", Password = "p", Access = access, ConnectTimeoutSeconds = 1 };

    private PostgresAccess Access() =>
        new(() => new PostgresCatalog([new PostgresNamedConnection("shop", Config("readwrite"), "x.json"), new PostgresNamedConnection("ro", Config(null), "x.json")], []).Offered(_settings.PostgresConnectionsOffered));

    [Fact]
    public async Task TheTool_ChecksTheMode_TheKinds_TheGate_TheConnection_ThenAsks()
    {
        var asked = new List<(string Family, string Connection, string Place, string Sql)>();
        bool? answer = false;
        var tool = new PostgresExecuteTool(Access(), () => _settings, (family, connection, place, sql, _) =>
        {
            asked.Add((family.Title, connection, place, sql));
            return Task.FromResult(answer);
        });

        Assert.Equal(ServerWriteText.Declined, await tool.RunAsync("DELETE FROM t WHERE id = @id;", null, null, [new("id", 1L)], null, CancellationToken.None));
        Assert.Equal([("PostgreSQL", "shop", "sales", "DELETE FROM t WHERE id = @id")], asked);
        Assert.Equal(ServerWriteText.Declined, await tool.RunAsync("DELETE FROM t", "shop", "archive", [], null, CancellationToken.None));
        Assert.Equal(("shop", "archive"), (asked[^1].Connection, asked[^1].Place));
        answer = null;
        Assert.Equal(ServerWriteText.NoPane(PostgresStatementKinds.Family), await tool.RunAsync("DELETE FROM t", null, null, [], null, CancellationToken.None));

        asked.Clear();
        Assert.Equal(ServerWriteText.ReadOnlyConnection(PostgresStatementKinds.Family, "ro"), await tool.RunAsync("DELETE FROM t", "ro", null, [], null, CancellationToken.None));
        Assert.StartsWith("Error: no PostgreSQL connection is named 'nope'", await tool.RunAsync("DELETE FROM t", "nope", null, [], null, CancellationToken.None));
        Assert.StartsWith("Error: the SQL uses GRANT", await tool.RunAsync("GRANT ALL ON t TO bob", null, null, [], null, CancellationToken.None));
        Assert.Equal(SqlText.BadMaxRows(PostgresQueryTool.MinRows, PostgresQueryTool.MaxRows), await tool.RunAsync("DELETE FROM t", null, null, [], 0, CancellationToken.None));
        Assert.StartsWith("Error: \"params\" must be one object of names and values", (string)(await tool.InvokeAsync(new AIFunctionArguments { ["sql"] = "DELETE FROM t", ["params"] = new[] { 1, 2 } }))!);
        // A read the read gate refuses is no read: never asked, never run.
        Assert.StartsWith("Error: the SQL uses pg_sleep()", await tool.RunAsync("SELECT pg_sleep(5)", null, null, [], null, CancellationToken.None));

        _settings.PostgresStatementsAllowed = ["data"];
        Assert.StartsWith("Error: the SQL is deleting (DELETE, TRUNCATE, a MERGE that deletes), which the user has not allowed", await tool.RunAsync("DELETE FROM t", null, null, [], null, CancellationToken.None));
        _settings.PostgresStatementsAllowed = [];
        Assert.Equal(ServerWriteText.NoKindsAllowed(PostgresStatementKinds.Family), await tool.RunAsync("INSERT INTO t VALUES (1)", null, null, [], null, CancellationToken.None));
        _settings.PostgresStatementsAllowed = ["data"];
        _settings.PostgresMode = "read-only";
        Assert.Equal(ServerWriteText.ReadOnlyMode(PostgresStatementKinds.Family), await tool.RunAsync("INSERT INTO t VALUES (1)", null, null, [], null, CancellationToken.None));
        Assert.Empty(asked);   // every refusal comes before the ask

        _settings.PostgresMode = "read-write";
        Assert.Equal(ServerWriteText.NoPane(PostgresStatementKinds.Family), await new PostgresExecuteTool(Access(), () => _settings, allow: null).RunAsync("INSERT INTO t VALUES (1)", null, null, [], null, CancellationToken.None));
    }

    [Fact]
    public void TheDescription_NamesTheTickedKinds()
    {
        string data = PostgresExecuteTool.DescribeFor([ServerStatementKind.Data]);
        Assert.Contains("only these kinds: changing data (INSERT, UPDATE, MERGE).", data);
        Assert.DoesNotContain("A read runs without asking", data);
        Assert.Contains("A read runs without asking, read-only.", PostgresExecuteTool.DescribeFor([ServerStatementKind.Data, ServerStatementKind.Read]));
        Assert.EndsWith("For reading, use postgres_query.", data);
        _settings.PostgresStatementsAllowed = ["drop"];
        Assert.Contains("dropping (DROP TABLE", new PostgresExecuteTool(Access(), () => _settings, null).Description);
    }

    [Fact]
    public void TheTurn_OffersExecute_OnlyUnderReadWrite_WithAPane_AKind_AndAReadwriteConnection()
    {
        var access = Access();
        var all = ChatScreen.PostgresTools(access, () => _settings);
        Assert.Equal(10, all.Count);
        Assert.Contains(PostgresExecuteTool.ToolName, ChatScreen.PostgresToolNames);
        Assert.Contains(PostgresExecuteTool.ToolName, PlanTools.Mutating);
        Assert.False(PlanTools.Allowed(PostgresExecuteTool.ToolName));
        IEnumerable<string> Names(bool pane) => ChatScreen.PostgresToolsFor(all, _settings, pane, access.Catalog()).Select(t => t.Name);

        Assert.Contains(PostgresExecuteTool.ToolName, Names(pane: true));
        Assert.Equal(ServerWrites.Postgres, ChatScreen.ServerWritesOf(ChatScreen.PostgresToolsFor(all, _settings, true, access.Catalog())));
        Assert.DoesNotContain(PostgresExecuteTool.ToolName, Names(pane: false));
        _settings.PostgresStatementsAllowed = [];
        Assert.DoesNotContain(PostgresExecuteTool.ToolName, Names(pane: true));   // no kind ticked
        _settings.PostgresStatementsAllowed = ["data"];
        _settings.PostgresConnectionsOffered = ["ro"];
        Assert.DoesNotContain(PostgresExecuteTool.ToolName, Names(pane: true));   // no readwrite connection offered
        _settings.PostgresConnectionsOffered = ["shop", "ro"];
        _settings.PostgresMode = "read-only";
        Assert.Equal(9, Names(pane: true).Count());
        Assert.Equal(ServerWrites.None, ChatScreen.ServerWritesOf(ChatScreen.PostgresToolsFor(all, _settings, true, access.Catalog())));
    }

    [Fact]
    public void TheRules_CarryTheWriteSentence_OnlyWithTheTool()
    {
        Assert.Contains(Assistant.PostgresRule + " " + Assistant.PostgresWriteRule, Assistant.DefaultRules(false, tools: true, postgres: true, serverWrites: ServerWrites.Postgres));
        Assert.DoesNotContain(Assistant.PostgresWriteRule, Assistant.DefaultRules(false, tools: true, postgres: true));
        Assert.DoesNotContain(Assistant.PostgresWriteRule, Assistant.DefaultRules(false, tools: true, serverWrites: ServerWrites.Postgres));
        Assert.Contains(Assistant.PostgresWriteRule, new TurnRules(Postgres: true, ServerWrites: ServerWrites.Postgres).DefaultRules(false));
        Assert.Contains(PostgresExecuteTool.ToolName, Assistant.PostgresWriteRule);
    }

    [Fact]
    public void TheListing_SaysWhichConnectionsMayChange()
    {
        string listing = new PostgresConnectionsTool(Access(), () => _settings).Describe();
        Assert.Contains("- shop (default): 127.0.0.1:9 / sales, user u, read-write", listing);
        Assert.Contains("- ro: 127.0.0.1:9 / sales, user u\n", listing + "\n");
        _settings.PostgresMode = "read-only";
        Assert.Contains("user u, readwrite (but PostgreSQL mode is read-only)", new PostgresConnectionsTool(Access(), () => _settings).Describe());
    }

    [Fact]
    public void TheWriteSession_DropsOnlyTheReadOnlyDefault()
    {
        string read = Config(null).Builder("p", null, 30).Options!;
        string write = Config(null).Builder("p", null, 30, readOnly: false).Options!;
        Assert.Contains("default_transaction_read_only=on", read);
        Assert.DoesNotContain("default_transaction_read_only", write);
        Assert.Equal(read.Replace("-c default_transaction_read_only=on ", "", StringComparison.Ordinal), write);
    }

    [Fact]
    public void TheWizardsWarning_SaysWhatAReadwriteConnectionMayDo()
    {
        Assert.EndsWith("a SELECT-only role is the real guard.", PostgresText.CanWrite(["superuser"]));
        Assert.Contains("postgres_execute may use those powers", PostgresText.CanWrite(["superuser"], readWrite: true));
    }
}

/// <summary><c>postgres_execute</c> against a real server (2026-10-05): a scratch table made, changed and dropped through the tool, every change allowed.</summary>
public sealed class LivePostgresWriteTests
{
    private readonly AppSettingsData _settings = new() { PostgresTools = true, PostgresMode = "read-write", PostgresStatementsAllowed = [.. ServerStatementKinds.Names] };

    private (PostgresExecuteTool Tool, List<string> Asked) Tool(string? access)
    {
        var asked = new List<string>();
        var config = LivePostgres.Config!;
        var copy = new PostgresConnectionConfig { Host = config.Host, Port = config.Port, Database = config.Database, User = config.User, Password = config.Password, SslMode = config.SslMode, ConnectTimeoutSeconds = config.ConnectTimeoutSeconds, Access = access };
        var access_ = new PostgresAccess(() => new PostgresCatalog([new PostgresNamedConnection("live", copy, "test")], []));
        return (new PostgresExecuteTool(access_, () => _settings, (_, _, _, sql, _) =>
        {
            asked.Add(sql);
            return Task.FromResult<bool?>(true);
        }), asked);
    }

    [LivePostgresFact]
    public async Task AScratchTable_IsMadeChangedAndDropped_EachChangeAsked()
    {
        var (tool, asked) = Tool("readwrite");
        string table = "ns_write_" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        var audit = new List<string>();
        void Collect(DiagnosticEvent e) => audit.Add(e.Message);
        DiagnosticLog.Emitted += Collect;
        try
        {
            Assert.StartsWith("Ran it in live/", await tool.RunAsync($"CREATE TABLE {table} (id int PRIMARY KEY, a text)", null, null, [], null, CancellationToken.None));
            Assert.StartsWith("Changed 2 rows in live/", await tool.RunAsync($"INSERT INTO {table} VALUES (1, @a), (2, 'b')", null, null, [new("a", "x")], null, CancellationToken.None));
            string returned = await tool.RunAsync($"UPDATE {table} SET a = 'y' WHERE id = 1 RETURNING id, a", null, null, [], null, CancellationToken.None);
            Assert.StartsWith("Changed 1 row in live/", returned);
            Assert.Contains("| 1 | y |", returned);
            Assert.Equal(3, asked.Count);

            // A read asks nothing and runs read-only.
            string rows = await tool.RunAsync($"SELECT a FROM {table} ORDER BY id", null, null, [], null, CancellationToken.None);
            Assert.StartsWith("2 rows × 1 column from live/", rows);
            Assert.Equal(3, asked.Count);

            // A failed statement changes nothing and says why.
            Assert.StartsWith("Error: the server refused it", await tool.RunAsync($"INSERT INTO {table} VALUES (1, 'dup')", null, null, [], null, CancellationToken.None));
            Assert.StartsWith("Changed 2 rows", await tool.RunAsync($"DELETE FROM {table}", null, null, [], null, CancellationToken.None));
            Assert.StartsWith("Ran it", await tool.RunAsync($"VACUUM {table}", null, null, [], null, CancellationToken.None));
            Assert.Contains(audit, line => line.Contains("2 rows changed by INSERT INTO " + table, StringComparison.Ordinal));
        }
        finally
        {
            DiagnosticLog.Emitted -= Collect;
            Assert.StartsWith("Ran it", await tool.RunAsync($"DROP TABLE IF EXISTS {table}", null, null, [], null, CancellationToken.None));
        }

        var (readOnly, roAsked) = Tool(null);
        Assert.StartsWith("Error: connection 'live' is read-only", await readOnly.RunAsync("CREATE TABLE ns_never (a int)", null, null, [], null, CancellationToken.None));
        Assert.Empty(roAsked);
    }
}
