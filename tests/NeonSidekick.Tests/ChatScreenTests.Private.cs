using System.Text;
using NeonSidekick.App;
using NeonSidekick.Diagnostics;
using NeonSidekick.Sessions;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary><c>/private</c> on the screen (2026-11-25, the private-conversations plan).</summary>
public partial class ChatScreenTests
{
    private string SessionsFile => Path.Combine(_settings.ProfileDirectory, SessionStore.FolderName, SessionStore.FileName);

    /// <summary>A private turn, its line and the <c>/private</c> line itself reach neither the sessions nor the input-line history.</summary>
    [Fact]
    public async Task Private_OnAFreshConversation_KeepsItsTurnsAndLinesOutOfTheStore()
    {
        _settings.Update(d => d.TtsOutput = false);
        _chat.EnqueueText("Noted.");
        PushLine("/private");
        PushLine("the ZebraQuartz phrase");
        PushLine("/exit");

        try
        {
            string output = await RunAsync();

            Assert.Contains(PrivateText.EnteredNotice, output);
            using var store = OpenSessions();
            Assert.Equal(0, store.Count);
            Assert.DoesNotContain(store.CommandHistory(), l => l.Contains("ZebraQuartz", StringComparison.Ordinal) || l.StartsWith("/private", StringComparison.Ordinal));
            Assert.False(PrivateMode.Active);   // the exit ends it
        }
        finally
        {
            PrivateMode.Active = false;
            PrivateMode.HideLog = false;
            PrivateMode.HidAtExit = false;
        }
    }

    /// <summary>A saved conversation asks first; a yes deletes its row and its input lines, and no copy of its words stays in the file.</summary>
    [Fact]
    public async Task Private_OnASavedConversation_AsksThenDeletesItsRowAndLines_FromTheFileToo()
    {
        _settings.Update(d => d.TtsOutput = false);
        _geometry = new ScreenGeometry(() => null);
        _chat.EnqueueText("One ZebraQuartz.").EnqueueText("Two.");
        StepsWhenIdle(Line("first ZebraQuartz line"), Line("/private"), Key(Keys.Char('y')), Key(Keys.Enter), Line("after private"), Line("/exit"));

        try
        {
            string output = await RunAsync();

            Assert.Contains(PrivateText.DeletedNotice(1)[..20], output);
            using (var store = OpenSessions())
            {
                Assert.Equal(0, store.Count);
                Assert.DoesNotContain(store.CommandHistory(), l => l.Contains("ZebraQuartz", StringComparison.Ordinal) || l.Contains("after private", StringComparison.Ordinal));
            }

            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (string file in new[] { SessionsFile, SessionsFile + "-wal" }.Where(File.Exists))
            {
                byte[] bytes = File.ReadAllBytes(file);
                Assert.True(bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes("ZebraQuartz")) < 0, file);
                Assert.True(bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes("zebraquartz")) < 0, file);
            }
        }
        finally
        {
            PrivateMode.Active = false;
            PrivateMode.HideLog = false;
            PrivateMode.HidAtExit = false;
        }
    }

    /// <summary><c>/private off</c> ends it as <c>/clear</c> does; the next conversation is saved.</summary>
    [Fact]
    public async Task PrivateOff_EndsIt_AndTheNextConversationIsSaved()
    {
        _settings.Update(d => d.TtsOutput = false);
        _chat.EnqueueText("Hidden.").EnqueueText("Saved.");
        PushLine("/private");
        PushLine("a private line");
        PushLine("/private off");
        PushLine("a saved line");
        PushLine("/exit");

        try
        {
            string output = await RunAsync();

            Assert.Contains(PrivateText.OffNotice, output);
            using var store = OpenSessions();
            Assert.Equal("a saved line", Assert.Single(store.List(0)).Title);
            Assert.DoesNotContain("a private line", store.CommandHistory());
            Assert.Contains("a saved line", store.CommandHistory());
        }
        finally
        {
            PrivateMode.Active = false;
            PrivateMode.HideLog = false;
            PrivateMode.HidAtExit = false;
        }
    }

    /// <summary>Under <c>--private</c> every conversation is private: <c>/private off</c> is refused, saying why, and <c>/new</c> starts a fresh private one.</summary>
    [Fact]
    public async Task UnderTheFlag_EveryConversationIsPrivate_AndOffIsRefused()
    {
        _settings.Update(d => { d.TtsOutput = false; d.PrivateAtStart = true; });
        _overriddenBy = field => field == SettingsField.PrivateAtStart ? SidekickOptions.PrivateFlag : null;
        _chat.EnqueueText("One.").EnqueueText("Two.");
        PushLine("hello");
        PushLine("/private off");
        PushLine("/new");
        PushLine("again");
        PushLine("/exit");

        try
        {
            string output = await RunAsync();

            Assert.Contains(PrivateText.OffRefused(SidekickOptions.PrivateFlag), output);
            Assert.Contains(PrivateText.NewNotice, output);
            using var store = OpenSessions();
            Assert.Equal(0, store.Count);
            Assert.Empty(store.CommandHistory());
        }
        finally
        {
            PrivateMode.Active = false;
            PrivateMode.HideLog = false;
            PrivateMode.HidAtExit = false;
        }
    }

    /// <summary>
    /// A file checkpoint made while private is kept under the private excerpt and removed at the end (2026-11-26, the review's #15/#31, the user's
    /// call): /undo of a private turn works only until then.
    /// </summary>
    [Fact]
    public async Task Private_ACheckpointMadeWhilePrivate_GoesAtTheEnd()
    {
        CheckpointsOn();
        _chat.Enqueue(FakeChatClient.Call("c1", NeonSidekick.Llm.Tools.WriteFileTool.ToolName, WriteArgs("note.txt", "hello")))
            .EnqueueText("Written.");
        string? id = null;
        StepsWhenIdle(
            Line("/private"),
            Line("write a ZebraQuartz note"),
            input => { id = Assert.Single(_checkpoints!.Records).Id; PushLine(input, "/private off"); },
            Line("/exit"));

        try
        {
            await RunAsync();

            Assert.NotNull(id);
            Assert.Equal([id!], _checkpoints!.RemovedNow);
            Assert.Empty(_checkpoints.Records);
        }
        finally
        {
            PrivateMode.Active = false;
            PrivateMode.HideLog = false;
            PrivateMode.HidAtExit = false;
        }
    }

    /// <summary>While private and without Private learning, <c>save_memory</c> and <c>skill_editor</c> aren't offered; outside it they are.</summary>
    [Fact]
    public async Task Private_WithholdsTheLearningTools_UnlessPrivateLearningIsOn()
    {
        _settings.Update(d => { d.TtsOutput = false; d.AgentSkills = true; });
        _chat.EnqueueText("One.").EnqueueText("Two.");
        PushLine("/private");
        PushLine("during");
        PushLine("/private off");
        PushLine("after");
        PushLine("/exit");

        string output;
        try
        {
            output = await RunAsync();
        }
        finally
        {
            PrivateMode.Active = false;
            PrivateMode.HideLog = false;
            PrivateMode.HidAtExit = false;
        }

        Assert.True(_chat.Options.Count >= 2, output);
        var during = _chat.Options[0]!.Tools!.Select(t => t.Name).ToList();
        var before = _chat.Options[1]!.Tools!.Select(t => t.Name).ToList();   // after the private conversation ended
        Assert.Contains(NeonSidekick.Llm.Tools.SaveMemoryTool.ToolName, before);
        Assert.DoesNotContain(NeonSidekick.Llm.Tools.SaveMemoryTool.ToolName, during);
        Assert.DoesNotContain(NeonSidekick.Llm.Tools.SkillEditorTool.ToolName, during);
    }
}
