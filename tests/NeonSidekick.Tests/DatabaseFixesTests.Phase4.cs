using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Diagnostics;
using NeonSidekick.Files;
using NeonSidekick.Llm.Tools;
using NeonSidekick.MySql;
using NeonSidekick.Plans;
using NeonSidekick.Settings;
using NeonSidekick.Sql;
using NeonSidekick.Sqlite;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// Phase 4 of the database review's fixes (2026-10-24): the allow pane, the session allow, the audit and the offering — mechanism 4,
/// decisions DB4, DB6 and DB7.
/// </summary>
public sealed class DatabaseAllowAndAuditTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("neon-dbfix-").FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>DB6 and A17: the session allow is keyed on the server too; I10: a SQLite file's key keeps its case where its folder does.</summary>
    [Fact]
    public void TheSessionAllowsKey_NamesTheServer_AndASqliteFilesKeepsItsCase()
    {
        var family = MySqlStatementKinds.Family;
        Assert.NotEqual(ChatScreen.DatabaseAllowKey(family, new ExecuteTarget("shop", true, "sales", "db1:3306")), ChatScreen.DatabaseAllowKey(family, new ExecuteTarget("shop", true, "sales", "db2:3306")));
        Assert.Equal(ChatScreen.DatabaseAllowKey(family, new ExecuteTarget("shop", true, "sales", "db1:3306")), ChatScreen.DatabaseAllowKey(family, new ExecuteTarget("shop ", true, "sales", " db1:3306")));

        using (CaseRule.Override(_ => true))
        {
            Assert.NotEqual(ChatScreen.SqliteAllowKey("/data/test.db"), ChatScreen.SqliteAllowKey("/data/Test.db"));
        }

        using (CaseRule.Override(_ => false))
        {
            Assert.Equal(ChatScreen.SqliteAllowKey("/data/test.db"), ChatScreen.SqliteAllowKey("/data/Test.db"));
        }
    }

    /// <summary>B5 and F16: the caption shows the server, the bound values, and the statement with its hiding characters escaped and a lone CR a line break.</summary>
    [Fact]
    public void TheAllowCaption_ShowsTheValues_AndAStatementThatCannotHideItself()
    {
        string caption = ServerWriteText.AllowCaption(MySqlStatementKinds.Family, "shop", "sales", "DELETE FROM t\rWHERE id = @id -- \u202Eok", "db1:3306", [new("id", 43659L), new("name", "x")]);
        Assert.Contains("on db1:3306", caption);
        Assert.Contains("DELETE FROM t\nWHERE id = @id", caption);
        Assert.Contains("\\u202E", caption);
        Assert.DoesNotContain("\u202E", caption, StringComparison.Ordinal);
        Assert.EndsWith("with   id = 43659\n  name = 'x'", caption);
        Assert.Contains("(v shows the whole statement)", ServerWriteText.CaptionStatement(new string('x', ServerWriteText.CaptionChars + 1)));
        Assert.Contains("@n = ", SqliteText.AllowCaption(new SqliteTarget("app", "/w/app.db"), "INSERT INTO t VALUES (@n)", false, [new("@n", 1L)]));
    }

    /// <summary>DB7, H3, J4: one line, the whole statement, its secrets redacted, cut; and an outcome that is not success said as such (A8).</summary>
    [Fact]
    public void TheAuditLine_IsTheWholeStatement_Redacted_AndSaysHowItEnded()
    {
        string line = DatabaseAudit.Line("shop/sales", DatabaseAuditOutcome.Done, 2, "-- the note\nUPDATE t\n  SET a = 1\n  WHERE b = 'mysql://u:hunter2@db/x'");
        Assert.Equal("shop/sales: 2 rows changed by -- the note UPDATE t SET a = 1 WHERE b = 'mysql://u:<redacted>@db/x'", line);
        Assert.Contains(": failed (part of it may have committed) by ", DatabaseAudit.Line("w", DatabaseAuditOutcome.Failed, null, "x"));
        Assert.Contains(": timed out (part of it may have committed) by ", DatabaseAudit.Line("w", DatabaseAuditOutcome.TimedOut, null, "x"));
        Assert.Contains(": ran by ", DatabaseAudit.Line("w", DatabaseAuditOutcome.Done, null, "CREATE TABLE t (a int)"));
        Assert.EndsWith("…", DatabaseAudit.Statement(new string('x', DatabaseAudit.MaxStatementChars + 5)));
    }

    /// <summary>A8: a change the server refused after it was allowed is in the log, and a canceled one too.</summary>
    [Fact]
    public async Task AFailedOrCanceledChange_IsAudited()
    {
        var lines = new List<string>();
        void Collect(DiagnosticEvent e) => lines.Add(e.Message);
        DiagnosticLog.Emitted += Collect;
        try
        {
            ServerExecuteHooks Hooks(Func<CancellationToken, Task<SqlRun>> execute) => new(
                (_, _) => null, _ => [ServerStatementKind.Data], _ => null, body => body,
                () => (new ExecuteTarget("shop", true, "sales"), null),
                (_, _) => Task.FromResult("read"), (_, token) => execute(token), _ => "Error: refused");
            var family = MySqlStatementKinds.Family;
            string[] all = [.. ServerStatementKinds.Names];
            DatabaseWriteAllow yes = (_, _, _, _, _) => Task.FromResult<bool?>(true);

            Assert.Equal("Error: refused", await ServerExecute.RunAsync(family, "read-write", all, "UPDATE t SET a = 1", [], 10, 1000,
                Hooks(_ => Task.FromResult(new SqlRun(SqlOutcome.Failed, "x", "shop", "sales", [], TimeSpan.FromMilliseconds(5)))), yes, CancellationToken.None));   // it ran, then failed
            Assert.Contains(lines, l => l == "shop/sales: failed (part of it may have committed) by UPDATE t SET a = 1");

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ServerExecute.RunAsync(family, "read-write", all, "UPDATE t SET a = 2", [], 10, 1000,
                Hooks(_ => throw new OperationCanceledException()), yes, CancellationToken.None));
            Assert.Contains(lines, l => l == "shop/sales: canceled (part of it may have committed) by UPDATE t SET a = 2");
        }
        finally
        {
            DiagnosticLog.Emitted -= Collect;
        }
    }

    /// <summary>E11: every result set a change gave back is in the answer, the text cap shared.</summary>
    [Fact]
    public void AnExecutedAnswer_ShowsEveryResultSet()
    {
        var one = new SqlGrid(["a"], [["1"]], false);
        var two = new SqlGrid(["b"], [["2"], ["3"]], false);
        string answer = ServerWriteText.Executed(new SqlRun(SqlOutcome.Ok, "", "shop", "sales", [one, two], TimeSpan.Zero), 10, 10_000);
        Assert.Contains("it returned 2 result sets", answer);
        Assert.Contains("Set 1: 1 row", answer);
        Assert.Contains("Set 2: 2 rows", answer);
        Assert.Contains("| 3 |", answer);
    }

    /// <summary>H11: an execute call resolves its connection once; every later step works on that entry.</summary>
    [Fact]
    public async Task AnExecuteCall_ResolvesItsConnectionOnce()
    {
        int reads = 0;
        var settings = new AppSettingsData { MySqlTools = true, MySqlMode = "read-write", MySqlStatementsAllowed = ["data"] };
        var access = new MySqlAccess(() =>
        {
            reads++;
            return new MySqlCatalog([new MySqlNamedConnection("shop", new MySqlConnectionConfig { Host = "127.0.0.1", Port = 1, Database = "sales", User = "u", Password = "p", Access = "readwrite", ConnectTimeoutSeconds = 1 }, "x.json")], []);
        });
        ExecuteTarget? asked = null;
        var tool = new MySqlExecuteTool(access, () => settings, (_, target, _, _, _) => { asked = target; return Task.FromResult<bool?>(true); });
        Assert.StartsWith("Error: could not connect to shop", await tool.RunAsync("UPDATE t SET a = 1", "shop", [], null, CancellationToken.None));
        Assert.Equal(1, reads);
        Assert.Equal("127.0.0.1:1", asked!.Where);
    }

    /// <summary>H2: an unattended Ralph pass leaves out every database change tool, as it does the others that wait on a pane.</summary>
    [Fact]
    public void AnUnattendedPass_LeavesOutTheDatabaseChanges()
    {
        var left = PlanTools.WithoutAskingOrConfirming(new HashSet<string>());
        Assert.Contains(SqlExecuteTool.ToolName, left);
        Assert.Contains(OracleExecuteTool.ToolName, left);
        Assert.Contains(MySqlExecuteTool.ToolName, left);
        Assert.Contains(PostgresExecuteTool.ToolName, left);
        Assert.Contains(SqliteExecuteTool.ToolName, left);
    }

    /// <summary>J2: an advisor's recent lines carry a stand-in for a database call, never its rows.</summary>
    [Fact]
    public void AnAdvisorsRecentLines_WithholdADatabaseResult()
    {
        var call = new FunctionCallContent("c1", SqlQueryTool.ToolName, new Dictionary<string, object?> { ["sql"] = "SELECT secret FROM t", ["connection"] = "prod" });
        Assert.Equal(SqlText.WithheldFromAdvisor(SqlQueryTool.ToolName, "prod"), Advisors.AdvisorRecent.StandIn(call));
        Assert.Equal("(sql_query on prod: read it with your own database tools)", SqlText.WithheldFromAdvisor(SqlQueryTool.ToolName, "prod"));
    }

    private (SqliteAccess Access, AppSettingsData Settings, string Work) Sqlite(string[] kinds, Func<IReadOnlyList<string>>? unticked = null)
    {
        string work = Path.Combine(_dir, "work");
        Directory.CreateDirectory(work);
        var settings = new AppSettingsData { SqliteTools = true, SqliteMode = "read-write", SqliteStatementsAllowed = [.. kinds], SqliteSandboxFiles = true };
        var files = new WorkingDirectory(() => work, new ManualTimeProvider());
        return (new SqliteAccess(() => SqliteCatalog.Empty, () => files, unticked), settings, work);
    }

    private static void Make(string path, string script)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = script;
        command.ExecuteNonQuery();
    }

    /// <summary>P3: create on a file already there is just an open, so it needs no Create tick; G16: DDL answers "Ran it".</summary>
    [Fact]
    public async Task SqliteCreate_OnAFileAlreadyThere_NeedsNoCreateTick()
    {
        var (access, settings, work) = Sqlite(["data", "alter"]);
        Make(Path.Combine(work, "app.db"), "CREATE TABLE t (a)");
        var tool = new SqliteExecuteTool(access, () => settings, (_, _, _, _, _) => Task.FromResult<bool?>(true));
        Assert.StartsWith("Changed 1 row in app.db", await tool.RunAsync("INSERT INTO t VALUES (1)", "app.db", [], null, create: true, CancellationToken.None));
        Assert.StartsWith("Ran it in app.db", await tool.RunAsync("ALTER TABLE t ADD COLUMN b", "app.db", [], null, create: false, CancellationToken.None));
        Assert.Equal(SqliteText.CreateNotAllowed, await tool.RunAsync("INSERT INTO t VALUES (1)", "new.db", [], null, create: true, CancellationToken.None));   // a new file still needs it
    }

    /// <summary>A10: a change to a database under <c>.neon</c> or <c>.git</c> is refused by the file guard's rules.</summary>
    [Fact]
    public async Task SqliteExecute_RefusesAFileTheGuardKeeps()
    {
        var (access, settings, work) = Sqlite(["data", "create"]);
        Make(Path.Combine(work, ".neon", "x.db"), "CREATE TABLE t (a)");
        var tool = new SqliteExecuteTool(access, () => settings, (_, _, _, _, _) => Task.FromResult<bool?>(true));
        Assert.StartsWith("Error: sqlite_execute may not change .neon/x.db", await tool.RunAsync("INSERT INTO t VALUES (1)", ".neon/x.db", [], null, false, CancellationToken.None));
    }

    /// <summary>I5: a working-directory path that is a database sqlite.json names but the user has not ticked is refused.</summary>
    [Fact]
    public async Task SqliteByPath_RefusesAnUntickedNamedDatabase()
    {
        string work = Path.Combine(_dir, "work");
        var (access, settings, _) = Sqlite(["data"], () => [Path.Combine(work, "named.db")]);
        Make(Path.Combine(work, "named.db"), "CREATE TABLE t (a)");
        var query = new SqliteQueryTool(access, () => settings);
        Assert.Equal(SqliteText.UntickedDatabase("named.db"), (string)(await query.InvokeAsync(new AIFunctionArguments { ["sql"] = "SELECT * FROM t", ["database"] = "named.db" }))!);
    }

    /// <summary>F11: the read shortcut runs only what sqlite_query's gate passes (a plan of one too); EXPLAIN PRAGMA goes to the pane.</summary>
    [Fact]
    public void SqliteReadShortcut_IsTheReadGates()
    {
        Assert.True(SqliteExecuteTool.ReadGatePasses("EXPLAIN QUERY PLAN SELECT * FROM t"));
        Assert.True(SqliteExecuteTool.ReadGatePasses("SELECT 1"));
        Assert.False(SqliteExecuteTool.ReadGatePasses("EXPLAIN PRAGMA writable_schema = 1"));
    }
}
