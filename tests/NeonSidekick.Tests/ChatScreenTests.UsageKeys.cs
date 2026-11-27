using NeonSidekick.App;
using NeonSidekick.Llm;
using NeonSidekick.Mcp;
using NeonSidekick.Sessions;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// <c>/usage</c> › Context's row cursor (2026-11-27, the user's ask): the pane opens with the cursor on the first tool group (Files in this
/// rig, the largest); Space switches a group's on/off from the row, or says why it can't; Enter opens where the row is changed and comes back to
/// the same row. Without the pane, the trend prints after two replies, never under <c>/usage context</c>, and <c>/clear</c> ends it.
/// </summary>
public partial class ChatScreenTests
{
    /// <summary>The cursor's line on the Context tab: the stop pointer, then the group's label under the Tools row.</summary>
    private static string CursorOn(string label) => InfoPane.StopPointer + "  " + label + " ";

    /// <summary>From Files to the resources group in its rig: past the ten other groups (the server's own row, smaller, after it).</summary>
    private const int ResourcesDowns = 11;

    /// <summary>From Files to History under a reply: past the eleven groups (the reply's tools the same).</summary>
    private const int HistoryDownsMidTurn = 11;

    private void UsagePane()
    {
        _settings.Update(d => { d.TtsOutput = false; d.MenuMaxHeight = "full"; });
        _console.Profile.Height = 80;
        _geometry = new ScreenGeometry(() => null);
    }

    [Fact]
    public async Task UsagePane_OpensWithTheCursorOnTheFirstGroup_AndSpaceSwitchesItOff_ThenOn()
    {
        UsagePane();
        // Both in one visit: a group already off at the open isn't listed, so a second visit would open on the next group.
        PushLine("/usage");
        _console.Input.PushKey(Keys.Char(' '));
        _console.Input.PushKey(Keys.Char(' '));
        _console.Input.PushKey(Keys.Escape);
        PushLine("/exit");

        bool sawOff = false;
        _settings.Changed += d => sawOff |= !d.FileTools;
        string output = await RunAsync();

        Assert.Contains(CursorOn("Files"), output, StringComparison.Ordinal);
        Assert.True(sawOff);                       // the first Space saved it off
        Assert.True(_settings.Current.FileTools);  // the second back on
        // Off during the visit: the row stays at its place, its note the one word.
        Assert.Matches(System.Text.RegularExpressions.Regex.Escape(CursorOn("Files")) + @"\s+~[\d.,k]+\s+" + UsageText.GroupOff, output);
    }

    [Fact]
    public async Task UsagePane_SpaceOnShell_SaysItHasMoreThanOnAndOff_AndChangesNothing()
    {
        UsagePane();
        var before = _settings.Current.ShellCommandPolicy;
        PushLine("/usage");
        _console.Input.PushKey(Keys.Down);
        _console.Input.PushKey(Keys.Down);
        _console.Input.PushKey(Keys.Down);
        _console.Input.PushKey(Keys.Down);   // Files, GitLib, Web, Skills, Shell
        _console.Input.PushKey(Keys.Char(' '));
        _console.Input.PushKey(Keys.Escape);
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(CursorOn("Shell"), output, StringComparison.Ordinal);
        Assert.Contains(UsageText.NotASwitchNotice("Shell"), output, StringComparison.Ordinal);
        Assert.Equal(before, _settings.Current.ShellCommandPolicy);
    }

    [Fact]
    public async Task UsagePane_EnterOnFiles_OpensItsToolsPage_AndEscComesBackOnFiles()
    {
        UsagePane();
        PushLine("/usage");
        _console.Input.PushKey(Keys.Down);   // GitLib, so the return proves the row kept
        _console.Input.PushKey(Keys.Up);
        _console.Input.PushKey(Keys.Enter);
        _console.Input.PushKey(Keys.Escape); // /tools › Files
        _console.Input.PushKey(Keys.Escape); // /usage again
        PushLine("/exit");

        string output = await RunAsync();

        int tools = output.IndexOf(SettingsMenu.FieldName(SettingsField.FileTools), StringComparison.Ordinal);
        Assert.True(tools > 0);
        Assert.Contains(CursorOn("Files"), output[tools..], StringComparison.Ordinal);   // reopened on the same row
    }

    [Fact]
    public async Task UsagePane_EnterOnTheSystemPromptRow_ShowsThePromptTab()
    {
        UsagePane();
        PushLine("/usage");
        _console.Input.PushKey(Keys.Up);
        _console.Input.PushKey(Keys.Up);     // Tools, then System prompt
        _console.Input.PushKey(Keys.Enter);
        _console.Input.PushKey(Keys.Escape);
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(InfoPane.StopPointer + UsageText.PromptLabel, output, StringComparison.Ordinal);
        Assert.Contains(HeadingRow("── Persona ── default"), output, StringComparison.Ordinal);   // the Prompt tab's own section
    }

    [Fact]
    public async Task UsagePane_EnterOnThinking_OpensTheSettingsSearch_AndChangesNothing()
    {
        UsagePane();
        bool before = _settings.Current.LlmPreserveThinking;
        PushLine("/usage");
        for (int i = 0; i < 12; i++)
        {
            _console.Input.PushKey(Keys.Down);   // from Files past the eleven groups and History, onto Thinking
        }

        _console.Input.PushKey(Keys.Enter);
        _console.Input.PushKey(Keys.Escape); // the search
        _console.Input.PushKey(Keys.Escape); // /usage again
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(InfoPane.StopPointer + UsageText.ThinkingLabel, output, StringComparison.Ordinal);
        Assert.Contains(SettingsMenu.FieldName(SettingsField.LlmPreserveThinking), output, StringComparison.Ordinal);
        Assert.Equal(before, _settings.Current.LlmPreserveThinking);
    }

    [Fact]
    public async Task UsagePane_EnterOnHistory_AsksToCompact_AndAYesCompacts()
    {
        UsagePane();
        _settings.Update(d => d.LlmCompactKeepRecent = 1);
        ReplyWithUsage();
        _chat.Enqueue(FakeChatClient.Text("tw"), FakeChatClient.Text("o"), FakeChatClient.Usage(12, 3));
        _chat.Enqueue(FakeChatClient.Text("A summary."), FakeChatClient.Usage(30, 4));   // the compact
        var steps = new List<Action<ScriptedInput>> { Line("a"), Line("b"), Line("/usage") };
        steps.AddRange(Enumerable.Repeat(Key(Keys.Down), 11));   // from Files past the groups onto History
        steps.AddRange([Key(Keys.Enter), Key(Keys.Char('y')), Key(Keys.Enter), Key(Keys.Escape), Line("/exit")]);   // y moves to Yes, Enter picks it
        StepsWhenIdle([.. steps]);

        string output = await RunAsync();

        Assert.Contains(InfoPane.StopPointer + UsageText.HistoryLabel, output, StringComparison.Ordinal);
        Assert.Contains(UsageText.CompactQuestion, output, StringComparison.Ordinal);
        Assert.Contains("🗜️ compacted", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Usage_WithoutThePane_TheTrendAfterTwoReplies_NoneUnderContext_AndClearEndsIt()
    {
        _settings.Update(d => d.TtsOutput = false);
        ReplyWithUsage();
        ReplyWithUsage();
        PushLine("a");
        PushLine("b");
        PushLine("/usage context");
        PushLine("/usage");
        PushLine("/clear");
        PushLine("/usage");
        PushLine("/exit");

        string output = await RunAsync();

        string trend = "  · " + UsageText.TrendSpeedLabel.ToLowerInvariant() + ": ";
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(output, System.Text.RegularExpressions.Regex.Escape(trend)));   // the first /usage's alone
        int context = output.IndexOf("  · Context: ~", StringComparison.Ordinal);
        Assert.True(output.IndexOf(trend, StringComparison.Ordinal) > output.IndexOf("  · Context: ~", context + 1, StringComparison.Ordinal));
    }

    /// <summary>An MCP server's row (the last group here, the smallest): Space switches that server alone, never the shared MCP switch.</summary>
    [Fact]
    public async Task UsagePane_SpaceOnAnMcpServersRow_DisablesThatServerAlone()
    {
        UsagePane();
        _mcpServers.ReadOnly("pipe", "echo", "fail");
        McpServer();
        PushLine("/usage");
        for (int i = 0; i < 11; i++)
        {
            _console.Input.PushKey(Keys.Down);   // from Files past the ten other groups onto the server's
        }

        _console.Input.PushKey(Keys.Char(' '));
        _console.Input.PushKey(Keys.Escape);
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(CursorOn(SystemPromptSummary.McpGroupPrefix + "pipe"), output, StringComparison.Ordinal);
        Assert.Equal(["pipe"], _settings.Current.McpServersDisabled);
        Assert.True(_settings.Current.McpServers);
    }

    /// <summary>
    /// Space on a server's row that fails to connect (2026-11-27, the last-3 review's #41): the server is saved off again, so the row stays
    /// dimmed under the cursor true to the setting, and the failure says why.
    /// </summary>
    [Fact]
    public async Task UsagePane_SpaceOnAServerThatFailsToConnect_LeavesItOff()
    {
        UsagePane();
        _mcpServers.ReadOnly("pipe", "echo", "fail");
        McpServer();
        _settings.Changed += d =>
        {
            if (d.McpServersDisabled.Contains("pipe"))
            {
                _mcpServers.Failing.Add("pipe");   // switched off: the next connect fails
            }
        };
        PushLine("/usage");
        for (int i = 0; i < 11; i++)
        {
            _console.Input.PushKey(Keys.Down);
        }

        _console.Input.PushKey(Keys.Char(' '));   // off
        _console.Input.PushKey(Keys.Char(' '));   // on again: the connect fails
        _console.Input.PushKey(Keys.Escape);
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Equal(["pipe"], _settings.Current.McpServersDisabled);
        Assert.Contains(McpText.ConnectingNotice("pipe"), output, StringComparison.Ordinal);
        Assert.Contains(McpText.FailedNotice("pipe", ""), output, StringComparison.Ordinal);
        Assert.Contains(CursorOn(SystemPromptSummary.McpGroupPrefix + "pipe"), output, StringComparison.Ordinal);
    }

    /// <summary>A restored conversation's trend is seeded from its saved turns (2026-11-27): <c>/usage</c> without the pane prints it at once.</summary>
    [Fact]
    public async Task Usage_ARestoredConversation_SeedsTheTrend_FromItsSavedTurns()
    {
        _settings.Update(d => d.TtsOutput = false);
        long id;
        using (var store = OpenSessions())
        {
            id = store.Begin("saved", "llama")!.Value;
            var history = new ConversationHistory("");
            foreach (var (written, seconds) in new[] { (40L, 2.0), (90L, 3.0) })
            {
                var usage = new TokenUsage(100, written, 100 + written, 1, TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(seconds));
                store.AppendTurn(id, "q", "r", 0, [], [], 0, 100, written, false, model: "llama", usage: usage, contextTokens: 100 + written);
                history.AddUser("q");
                history.AddAssistant("r");
            }

            store.SaveHistory(id, SessionHistory.ToJson(history.Messages));
        }

        PushLine("/usage");
        PushLine("/sessions " + id);
        PushLine("/usage");
        PushLine("/exit");

        string output = await RunAsync();

        string trend = "  · " + UsageText.TrendSpeedLabel.ToLowerInvariant() + ": ";
        int restored = output.IndexOf("/sessions " + id, StringComparison.Ordinal);
        Assert.DoesNotContain(trend, output[..restored], StringComparison.Ordinal);   // nothing before the restore
        Assert.Contains(trend, output[restored..], StringComparison.Ordinal);
        Assert.Contains("(20 → 30 tok/s)", output[restored..], StringComparison.Ordinal);   // the two saved speeds, first → latest
    }

    /// <summary><c>mcp_read</c>'s group (the servers' resources) is one tool, not a server's switch: Space says so and switches nothing.</summary>
    [Fact]
    public async Task UsagePane_SpaceOnTheResourcesGroup_SaysItIsNoSwitch_AndChangesNothing()
    {
        UsagePane();
        _mcpServers.ReadOnly("pipe", "echo", "fail");
        _mcpServers.Resources("pipe", ("file:///notes.txt", "notes", "hello"));
        McpServer();
        PushLine("/usage");
        for (int i = 0; i < ResourcesDowns; i++)
        {
            _console.Input.PushKey(Keys.Down);
        }

        _console.Input.PushKey(Keys.Char(' '));
        _console.Input.PushKey(Keys.Escape);
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(CursorOn(SystemPromptSummary.McpResourcesGroup), output, StringComparison.Ordinal);
        Assert.Contains(UsageText.NoSwitchNotice(SystemPromptSummary.McpResourcesGroup, UsageText.McpCommand), output, StringComparison.Ordinal);
        Assert.Empty(_settings.Current.McpServersDisabled);
        Assert.True(_settings.Current.McpServers);
    }

    /// <summary>Enter on an MCP server's row opens <c>/mcp</c> on its Servers tab, then <c>/usage</c> again on the same row.</summary>
    [Fact]
    public async Task UsagePane_EnterOnAnMcpServersRow_OpensMcpsServersTab_AndComesBack()
    {
        UsagePane();
        _mcpServers.ReadOnly("pipe", "echo", "fail");
        McpServer();
        PushLine("/usage");
        for (int i = 0; i < 11; i++)
        {
            _console.Input.PushKey(Keys.Down);
        }

        _console.Input.PushKey(Keys.Enter);
        _console.Input.PushKey(Keys.Escape);   // /mcp
        _console.Input.PushKey(Keys.Escape);   // /usage again
        PushLine("/exit");

        string output = await RunAsync();

        int mcp = output.IndexOf(McpText.Label + " │ " + McpText.ServersTabTitle, StringComparison.Ordinal);
        Assert.True(mcp > 0);
        Assert.Contains(CursorOn(SystemPromptSummary.McpGroupPrefix + "pipe"), output[mcp..], StringComparison.Ordinal);
    }

    /// <summary>Under a reply (2026-11-27): Space on an MCP server's row would reconnect it, so it answers the pane's not-while-a-reply note.</summary>
    [Fact]
    public async Task MidTurn_UsagePane_SpaceOnAnMcpServersRow_IsNotWhileAReplyRuns()
    {
        _mcpServers.ReadOnly("pipe", "echo", "fail");
        McpServer();
        MidTurnHeldFixture(async ct =>
        {
            Scripted().Push(Keys.CtrlU);
            Scripted().Push([.. Enumerable.Repeat(Keys.Down, 11), Keys.Char(' '), Keys.Escape]);
            await UntilAsync(() => Scripted().Queued == 0, ct);
        });

        string output = await RunAsync();

        Assert.Contains(SettingsMenu.NotWhileReplyRunsNotice, output, StringComparison.Ordinal);
        Assert.Empty(_settings.Current.McpServersDisabled);
        Assert.Contains("three.", output, StringComparison.Ordinal);   // the reply went on
    }

    /// <summary>Under a reply, Enter on History and a yes queue <c>/compact</c>, which runs once the reply ends.</summary>
    [Fact]
    public async Task MidTurn_UsagePane_EnterOnHistory_AndAYes_CompactsOnceTheReplyEnds()
    {
        _settings.Update(d => d.LlmCompactKeepRecent = 0);
        MidTurnHeldFixture(async ct =>
        {
            Scripted().Push(Keys.CtrlU);
            Scripted().Push([.. Enumerable.Repeat(Keys.Down, HistoryDownsMidTurn), Keys.Enter, Keys.Char('y'), Keys.Enter, Keys.Escape]);
            await UntilAsync(() => Scripted().Queued == 0, ct);
        });
        _chat.Enqueue(FakeChatClient.Text("A summary."), FakeChatClient.Usage(30, 4));   // the compact, after the reply

        string output = await RunAsync();

        Assert.Contains(UsageText.CompactQuestion, output, StringComparison.Ordinal);
        int reply = output.IndexOf("three.", StringComparison.Ordinal);
        int compacted = output.IndexOf("🗜️ compacted", StringComparison.Ordinal);
        Assert.True(reply > 0 && compacted > reply, "the compact after the reply");
    }
}
