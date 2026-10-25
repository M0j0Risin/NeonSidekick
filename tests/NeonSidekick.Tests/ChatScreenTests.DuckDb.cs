using DuckDB.NET.Data;
using NeonSidekick.DuckDb;
using NeonSidekick.Files;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary><c>duckdb_execute</c> on the screen (2026-10-25): offered only under read-write, each change asked on the allow pane.</summary>
public partial class ChatScreenTests
{
    private string DuckDbWorkFile(string name) => Path.Combine(WorkingDirectory.Resolve("", _settings.ProfileDirectory), name);

    private static long DuckDbCount(string path, string sql)
    {
        var builder = new DuckDBConnectionStringBuilder { DataSource = path };
        builder["access_mode"] = "READ_ONLY";
        using var connection = new DuckDBConnection(builder.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary><see cref="SqliteExecuteFixture"/>'s twin: <paramref name="answer"/> pushed at the allow pane's first wait only.</summary>
    private void DuckDbExecuteFixture(ConsoleKeyInfo[] answer, string reply, params Dictionary<string, object?>[] calls)
    {
        _settings.Update(d => { d.TtsOutput = false; d.DuckDbTools = true; d.DuckDbMode = "read-write"; d.DuckDbStatementsAllowed = ["data", "create"]; d.DuckDbSandboxFiles = true; });
        Directory.CreateDirectory(WorkingDirectory.Resolve("", _settings.ProfileDirectory));
        _console.Profile.Height = 40;
        _geometry = new ScreenGeometry(() => null);
        for (int i = 0; i < calls.Length; i++)
        {
            _chat.Enqueue(FakeChatClient.Call("c" + i, DuckDbExecuteTool.ToolName, calls[i]));
        }

        _chat.EnqueueText(reply);
        var input = Scripted();
        StepsWhenIdle(Line("make me a database"), Line("/exit"));
        var idle = input.OnWait!;
        bool answered = false;
        input.OnWait = () =>
        {
            if (_keys is { PendingLine.IsCompleted: false })
            {
                if (!answered)
                {
                    answered = true;
                    input.Push(answer);
                }

                return;
            }

            idle();
        };
    }

    [Fact]
    public async Task DuckDbExecute_AllowForTheSession_CreatesTheFile_AndTheNextChangeIsNotAsked()
    {
        DuckDbExecuteFixture([Keys.Char('s'), Keys.Enter], "Done.",
            new() { ["sql"] = "CREATE TABLE t (a INTEGER)", ["database"] = "app.duckdb", ["create"] = true },
            new() { ["sql"] = "INSERT INTO t VALUES (1), (2)", ["database"] = "app.duckdb" });

        string output = await RunAsync();

        Assert.Contains(DuckDbText.AllowTitle, output);
        var results = Results(_chat.Requests[^1]).Select(r => (string)r.Result!).ToList();
        Assert.StartsWith("Created app.duckdb. Ran it in app.duckdb", results[0]);
        Assert.StartsWith("Changed 2 rows in app.duckdb", results[1]);
        Assert.Equal(2L, DuckDbCount(DuckDbWorkFile("app.duckdb"), "SELECT count(*) FROM t"));
    }

    [Fact]
    public async Task DuckDbExecute_Deny_IsTheModelsAnswer_AndNothingIsMade()
    {
        DuckDbExecuteFixture([Keys.Enter], "Understood.", new Dictionary<string, object?> { ["sql"] = "CREATE TABLE t (a INTEGER)", ["database"] = "app.duckdb", ["create"] = true });

        await RunAsync();

        Assert.Equal(DuckDbText.Declined, Assert.Single(Results(_chat.Requests[^1])).Result);
        Assert.False(File.Exists(DuckDbWorkFile("app.duckdb")));
    }

    /// <summary>The session allow is forgotten with the conversation, as SQLite's (the database review's S11).</summary>
    [Fact]
    public async Task DuckDbExecute_TheSessionAllow_IsForgottenWithTheConversation()
    {
        _settings.Update(d => { d.TtsOutput = false; d.DuckDbTools = true; d.DuckDbMode = "read-write"; d.DuckDbStatementsAllowed = ["data", "create"]; d.DuckDbSandboxFiles = true; });
        Directory.CreateDirectory(WorkingDirectory.Resolve("", _settings.ProfileDirectory));
        _console.Profile.Height = 40;
        _geometry = new ScreenGeometry(() => null);
        _chat.Enqueue(FakeChatClient.Call("c0", DuckDbExecuteTool.ToolName, new Dictionary<string, object?> { ["sql"] = "CREATE TABLE t (a INTEGER)", ["database"] = "app.duckdb", ["create"] = true }));
        _chat.EnqueueText("First.");
        _chat.Enqueue(FakeChatClient.Call("c1", DuckDbExecuteTool.ToolName, new Dictionary<string, object?> { ["sql"] = "INSERT INTO t VALUES (1)", ["database"] = "app.duckdb" }));
        _chat.EnqueueText("Second.");
        var input = Scripted();
        StepsWhenIdle(Line("make one"), Line("/new"), Line("change it"), Line("/exit"));
        var idle = input.OnWait!;
        var pending = new Queue<ConsoleKeyInfo[]>([[Keys.Char('s'), Keys.Enter], [Keys.Enter]]);
        int asked = 0;
        input.OnWait = () =>
        {
            if (_keys is { PendingLine.IsCompleted: false })
            {
                int shown = CountOf(_console.Output, DuckDbText.AllowTitle);
                if (shown > asked && pending.Count > 0)
                {
                    asked = shown;
                    input.Push(pending.Dequeue());
                }

                return;
            }

            idle();
        };

        await RunAsync();

        var results = _chat.Requests.SelectMany(r => Results(r)).GroupBy(r => r.CallId).Select(g => (g.Key, (string)g.Last().Result!)).ToDictionary(p => p.Key, p => p.Item2);
        Assert.StartsWith("Created app.duckdb.", results["c0"]);
        Assert.Equal(DuckDbText.Declined, results["c1"]);
        Assert.Empty(pending);
    }

    /// <summary>While the DuckDB tools are on, a model's write_file onto a DuckDB file is refused end to end, the file untouched.</summary>
    [Fact]
    public async Task DuckDbTools_On_TheFileToolsNeverWriteADatabase()
    {
        _settings.Update(d => { d.TtsOutput = false; d.DuckDbTools = true; d.FileTools = true; });
        string db = DuckDbWorkFile("lake.duckdb");
        Directory.CreateDirectory(Path.GetDirectoryName(db)!);
        File.WriteAllText(db, "pretend");
        _chat.Enqueue(FakeChatClient.Call("c1", WriteFileTool.ToolName, new Dictionary<string, object?> { ["path"] = "lake.duckdb", [WriteFileTool.ContentArgument] = "overwritten" }));
        _chat.EnqueueText("Refused.");
        PushLine("overwrite lake.duckdb");
        PushLine("/exit");

        await RunAsync();

        Assert.StartsWith("Error: 'lake.duckdb' is or holds a SQLite or DuckDB database", (string)Assert.Single(Results(_chat.Requests[^1])).Result!);
        Assert.Equal("pretend", File.ReadAllText(db));
    }

    [Theory]
    [InlineData("read-write", true)]
    [InlineData("read-only", false)]
    public async Task DuckDbExecute_IsOffered_OnlyUnderReadWrite(string mode, bool offered)
    {
        _chat.EnqueueText("one");
        _settings.Update(d => { d.TtsOutput = false; d.DuckDbTools = true; d.DuckDbMode = mode; d.DuckDbSandboxFiles = true; });
        _geometry = new ScreenGeometry(() => null);
        PushLine("hi");
        PushLine("/exit");

        await RunAsync();

        var names = (_chat.Options[0]?.Tools ?? []).Select(t => t.Name).ToList();
        Assert.Contains(DuckDbQueryTool.ToolName, names);
        Assert.Equal(offered, names.Contains(DuckDbExecuteTool.ToolName));
    }
}
