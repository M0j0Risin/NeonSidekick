using Microsoft.Data.Sqlite;
using NeonSidekick.Files;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Sqlite;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary><c>sqlite_execute</c> on the screen (2026-10-05): offered only under read-write, each change asked on the allow pane.</summary>
public partial class ChatScreenTests
{
    private string SqliteWorkFile(string name) => Path.Combine(WorkingDirectory.Resolve("", _settings.ProfileDirectory), name);

    /// <summary>
    /// A model reply that is <paramref name="calls"/> <c>sqlite_execute</c> calls, then <paramref name="reply"/>; <paramref name="answer"/>
    /// pushed at the allow pane's first wait only, so a second ask would leave the script dry.
    /// </summary>
    private void SqliteExecuteFixture(ConsoleKeyInfo[] answer, string reply, params Dictionary<string, object?>[] calls)
    {
        _settings.Update(d => { d.TtsOutput = false; d.SqliteTools = true; d.SqliteMode = "read-write"; d.SqliteStatementsAllowed = ["data", "create"]; });
        Directory.CreateDirectory(WorkingDirectory.Resolve("", _settings.ProfileDirectory));
        _console.Profile.Height = 40;
        _geometry = new ScreenGeometry(() => null);   // the pane: nothing can ask without it
        for (int i = 0; i < calls.Length; i++)
        {
            _chat.Enqueue(FakeChatClient.Call("c" + i, SqliteExecuteTool.ToolName, calls[i]));
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
    public async Task SqliteExecute_AllowForTheSession_CreatesTheFile_AndTheNextChangeIsNotAsked()
    {
        SqliteExecuteFixture([Keys.Char('s'), Keys.Enter], "Done.",
            new() { ["sql"] = "CREATE TABLE t (a)", ["database"] = "app.db", ["create"] = true },
            new() { ["sql"] = "INSERT INTO t VALUES (1), (2)", ["database"] = "app.db" });

        string output = await RunAsync();

        Assert.Contains(SqliteText.AllowTitle, output);
        var results = Results(_chat.Requests[^1]).Select(r => (string)r.Result!).ToList();
        Assert.StartsWith("Created app.db. Changed 0 rows in app.db", results[0]);
        Assert.StartsWith("Changed 2 rows in app.db", results[1]);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = SqliteWorkFile("app.db"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM t";
        Assert.Equal(2L, command.ExecuteScalar());
    }

    [Fact]
    public async Task SqliteExecute_Deny_IsTheModelsAnswer_AndNothingIsMade()
    {
        SqliteExecuteFixture([Keys.Enter], "Understood.", new Dictionary<string, object?> { ["sql"] = "CREATE TABLE t (a)", ["database"] = "app.db", ["create"] = true });

        await RunAsync();

        Assert.Equal(SqliteText.Declined, Assert.Single(Results(_chat.Requests[^1])).Result);
        Assert.False(File.Exists(SqliteWorkFile("app.db")));
    }

    [Theory]
    [InlineData("read-write", true)]
    [InlineData("read-only", false)]
    public async Task SqliteExecute_IsOffered_OnlyUnderReadWrite(string mode, bool offered)
    {
        _chat.EnqueueText("one");
        _settings.Update(d => { d.TtsOutput = false; d.SqliteTools = true; d.SqliteMode = mode; });
        _geometry = new ScreenGeometry(() => null);
        PushLine("hi");
        PushLine("/exit");

        await RunAsync();

        var names = (_chat.Options[0]?.Tools ?? []).Select(t => t.Name).ToList();
        Assert.Contains(SqliteQueryTool.ToolName, names);
        Assert.Equal(offered, names.Contains(SqliteExecuteTool.ToolName));
    }
}
