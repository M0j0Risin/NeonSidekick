using NeonSidekick.Llm.Tools;
using NeonSidekick.Postgres;
using NeonSidekick.Sql;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// The server families' <c>_execute</c> tools on the screen (2026-10-05), shown through PostgreSQL's: offered only under read-write,
/// each change asked on the allow pane, "Allow for this session" holding for one connection and database. The connection points at a
/// port nothing listens on, so an allowed change fails to connect — what is pinned here is what was asked, never a server's answer.
/// </summary>
public partial class ChatScreenTests
{
    private void PostgresConnectionsFile()
    {
        string path = PostgresConfigFile.ProfilePath(_settings.ProfileDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ \"connections\": { \"shop\": { \"host\": \"127.0.0.1\", \"port\": 9, \"database\": \"sales\", \"user\": \"u\", \"password\": \"p\", \"connectTimeoutSeconds\": 1, \"access\": \"readwrite\" } } }");
    }

    /// <summary>
    /// A model reply that is <paramref name="calls"/> <c>postgres_execute</c> calls, then <paramref name="reply"/>; each of
    /// <paramref name="answers"/> pushed at one wait of the allow pane, in turn, so one ask more than answers leaves the script dry.
    /// </summary>
    private void PostgresExecuteFixture(ConsoleKeyInfo[][] answers, string reply, params Dictionary<string, object?>[] calls)
    {
        _settings.Update(d => { d.TtsOutput = false; d.PostgresTools = true; d.PostgresMode = "read-write"; d.PostgresConnectionsOffered = ["shop"]; });
        PostgresConnectionsFile();
        ExecuteFixture(PostgresStatementKinds.Family, answers, reply, calls);
    }

    /// <summary>The allow pane's script for any family's <c>_execute</c> tool, its settings and connections file already set.</summary>
    private void ExecuteFixture(ServerWriteFamily family, ConsoleKeyInfo[][] answers, string reply, params Dictionary<string, object?>[] calls)
    {
        _console.Profile.Height = 40;
        _geometry = new ScreenGeometry(() => null);   // the pane: nothing can ask without it
        for (int i = 0; i < calls.Length; i++)
        {
            _chat.Enqueue(FakeChatClient.Call("c" + i, family.ToolName, calls[i]));
        }

        _chat.EnqueueText(reply);
        var input = Scripted();
        StepsWhenIdle(Line("change the shop"), Line("/exit"));
        var idle = input.OnWait!;
        var pending = new Queue<ConsoleKeyInfo[]>(answers);
        string title = ServerWriteText.AllowTitle(family);
        int asked = 0;
        input.OnWait = () =>
        {
            if (_keys is { PendingLine.IsCompleted: false })
            {
                // One answer per pane shown: a wait after a new title on the screen gets the next one.
                int shown = CountOf(_console.Output, title);
                if (shown > asked && pending.Count > 0)
                {
                    asked = shown;
                    input.Push(pending.Dequeue());
                }

                return;
            }

            idle();
        };
    }

    [Fact]
    public async Task PostgresExecute_AllowForTheSession_HoldsForThatDatabase_AnotherAsksAgain()
    {
        PostgresExecuteFixture([[Keys.Char('s'), Keys.Enter], [Keys.Enter]], "Done.",
            new() { ["sql"] = "INSERT INTO t VALUES (1)" },
            new() { ["sql"] = "UPDATE t SET a = 2" },
            new() { ["sql"] = "DELETE FROM t", ["database"] = "archive" });
        _settings.Update(d => d.PostgresStatementsAllowed = ["data", "delete"]);

        string output = await RunAsync();

        Assert.Contains(ServerWriteText.AllowTitle(PostgresStatementKinds.Family), output);
        var results = Results(_chat.Requests[^1]).Select(r => (string)r.Result!).ToList();
        Assert.StartsWith("Error: could not connect to shop", results[0]);   // allowed, then no server: it was run
        Assert.StartsWith("Error: could not connect to shop", results[1]);   // not asked: allowed for shop/sales
        Assert.Equal(ServerWriteText.Declined, results[2]);                  // shop/archive asked again, and denied
    }

    /// <summary>
    /// A statement that qualifies a name with another database is asked about although the call's database is allowed for the
    /// session (the review, 2026-10-05: <c>UPDATE payroll.t</c> under an allow for <c>sales</c>), its caption saying why.
    /// </summary>
    [Fact]
    public async Task MySqlExecute_ANameQualifiedElsewhere_IsAsked_DespiteTheSessionsAllow()
    {
        _settings.Update(d => { d.TtsOutput = false; d.MySqlTools = true; d.MySqlMode = "read-write"; d.MySqlConnectionsOffered = ["shop"]; d.MySqlStatementsAllowed = ["data"]; });
        string path = MySql.MySqlConfigFile.ProfilePath(_settings.ProfileDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ \"connections\": { \"shop\": { \"host\": \"127.0.0.1\", \"port\": 9, \"database\": \"sales\", \"user\": \"u\", \"password\": \"p\", \"connectTimeoutSeconds\": 1, \"access\": \"readwrite\" } } }");
        ExecuteFixture(MySql.MySqlStatementKinds.Family, [[Keys.Char('s'), Keys.Enter], [Keys.Enter]], "Done.",
            new() { ["sql"] = "INSERT INTO t VALUES (1)" },
            new() { ["sql"] = "UPDATE sales.t SET a = 2" },
            new() { ["sql"] = "UPDATE payroll.t SET a = 3" });

        string output = await RunAsync();

        var results = Results(_chat.Requests[^1]).Select(r => (string)r.Result!).ToList();
        Assert.StartsWith("Error: could not connect to shop", results[0]);   // allowed for the session, then no server
        Assert.StartsWith("Error: could not connect to shop", results[1]);   // its own database by name: not asked
        Assert.Equal(ServerWriteText.Declined, results[2]);                  // payroll: asked again, and denied
        Assert.Contains(ServerWriteText.NamesElsewhere(MySql.MySqlStatementKinds.Family)[..30], output);
    }

    [Fact]
    public async Task PostgresExecute_Deny_IsTheModelsAnswer()
    {
        PostgresExecuteFixture([[Keys.Enter]], "Understood.", new Dictionary<string, object?> { ["sql"] = "INSERT INTO t VALUES (1)" });

        await RunAsync();

        Assert.Equal(ServerWriteText.Declined, Assert.Single(Results(_chat.Requests[^1])).Result);
    }

    [Theory]
    [InlineData("read-write", true)]
    [InlineData("read-only", false)]
    public async Task PostgresExecute_IsOffered_OnlyUnderReadWrite(string mode, bool offered)
    {
        _chat.EnqueueText("one");
        _settings.Update(d => { d.TtsOutput = false; d.PostgresTools = true; d.PostgresMode = mode; d.PostgresConnectionsOffered = ["shop"]; });
        PostgresConnectionsFile();
        _geometry = new ScreenGeometry(() => null);
        PushLine("hi");
        PushLine("/exit");

        await RunAsync();

        var names = (_chat.Options[0]?.Tools ?? []).Select(t => t.Name).ToList();
        Assert.Contains(PostgresQueryTool.ToolName, names);
        Assert.Equal(offered, names.Contains(PostgresExecuteTool.ToolName));
        Assert.Equal(offered, _chat.Requests[0][0].Text.Contains(Llm.Assistant.PostgresWriteRule, StringComparison.Ordinal));
    }
}
