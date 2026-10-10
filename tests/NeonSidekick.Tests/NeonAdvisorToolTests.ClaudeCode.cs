using Microsoft.Extensions.AI;
using NeonSidekick.Advisors;
using NeonSidekick.App;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Sessions;
using NeonSidekick.Settings;

namespace NeonSidekick.Tests;

/// <summary>
/// A Claude Code advisor (2026-10-10, the user's rules): the first listed on the Claude Code server, with the chat elsewhere, runs its turns
/// on the server with the tools under its thread's conversation; any other answers text-only, told so, its footer saying why.
/// </summary>
public partial class NeonAdvisorToolTests
{
    [Fact]
    public void TheGate_TheFirstListed_AndNoneWhileTheChatIsOnClaudeCode()
    {
        IReadOnlyList<AdvisorEntry> entries = [new("ux"), new("dba"), new("qa")];
        bool OnClaudeCode(string profile) => profile is "dba" or "qa";

        Assert.Equal("dba", AdvisorClaudeGate.Holder(entries, OnClaudeCode, masterOnClaudeCode: false));
        Assert.Null(AdvisorClaudeGate.Holder(entries, OnClaudeCode, masterOnClaudeCode: true));
        Assert.Null(AdvisorClaudeGate.Holder(entries, _ => false, masterOnClaudeCode: false));

        Assert.Null(AdvisorClaudeGate.TextOnlyReason("DBA", "dba", masterOnClaudeCode: false));
        Assert.Equal("no tools: dba is the Claude Code advisor with tools", AdvisorClaudeGate.TextOnlyReason("qa", "dba", masterOnClaudeCode: false));
        Assert.Equal("no tools: this chat is on the Claude Code server", AdvisorClaudeGate.TextOnlyReason("dba", null, masterOnClaudeCode: true));
        Assert.Equal("tools", AdvisorText.ClaudeCodeMark(tools: true));
        Assert.Equal("text only", AdvisorText.ClaudeCodeMark(tools: false));
    }

    [Fact]
    public async Task ATextOnlyLink_IsOfferedNoTools_ToldSo_AndItsFooterSaysWhy()
    {
        _host.LinkTools = false;
        _host.Warning = AdvisorText.OtherClaudeCodeNote("ux");
        var client = _host.Add("dba");
        client.EnqueueText("from what I know");

        Assert.Equal("from what I know", await Ask(Tool(), "dba", "q"));

        Assert.Null(client.Options.Single()?.Tools);
        Assert.Contains(AdvisorText.Framing("neon", "database architect", tools: false), Assert.Single(_host.SystemPrompts));
        Assert.DoesNotContain("RULES", _host.SystemPrompts[0]);
        Assert.Contains("answered dba from what I know on dba-model (no tools: ux is the Claude Code advisor with tools)", _view.Lines);
        Assert.Null(client.Options[0]?.ConversationId);
    }

    [Fact]
    public async Task ASessionLink_NamesTheThreadsConversation_KeepsTheToolsInTheLastRound_AndTheIdGoesWithTheThread()
    {
        _host.LinkSession = true;
        _settings.AdvisorMaxToolIterations = 1;
        var client = _host.Add("dba");
        client.EnqueueText("first").EnqueueText("second");
        var tool = Tool();

        await Ask(tool, "dba", "q1");
        tool.BeginTurn();
        await Ask(tool, "dba", "q2");

        string id = _threads.SessionOf("dba");
        Assert.All(client.Options, o => Assert.Equal(id, o?.ConversationId));   // one conversation for the thread
        Assert.Equal(["read_file"], client.Options[0]!.Tools!.Select(t => t.Name));
        Assert.Equal(id, _threads.Sessions()!["DBA"]);

        string json = SessionHistory.ToJson([new ChatMessage(ChatRole.User, "hi")], advisorThreads: _threads.Snapshot(), advisorSessions: _threads.Sessions());
        var restored = new AdvisorThreads();
        restored.RestoreSessions(SessionHistory.AdvisorSessionsOf(json));
        Assert.Equal(id, restored.SessionOf("dba"));
        Assert.Null(SessionHistory.AdvisorSessionsOf(SessionHistory.ToJson([])));

        _threads.Clear();
        Assert.Null(_threads.Sessions());
        Assert.NotEqual(id, _threads.SessionOf("dba"));   // a new thread, a new conversation
    }

    [Fact]
    public void TheListEditorsRow_MarksAClaudeCodeAdvisor()
    {
        Assert.Equal(Spectre.Console.Markup.Escape("dba  ") + NeonSidekick.UI.Theme.DimMarkup("architect    · claudecode · sonnet · tools"),
            SettingsMenu.AdvisorRow(new AdvisorEntry("dba", "architect"), exists: true, 5, new AdvisorServerFacts(AdvisorServer.ClaudeCode, "sonnet"), 12, AdvisorText.ClaudeCodeMark(tools: true)));
    }
}
