using Microsoft.Extensions.AI;
using NeonSidekick.Advisors;
using NeonSidekick.App;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Sessions;
using NeonSidekick.Settings;

namespace NeonSidekick.Tests;

/// <summary>
/// A Claude Code advisor (2026-10-10): its turns run under its thread's conversation, with the tools. Every one since 2026-10-13 (the
/// user's ask), on a process of its own beside the chat's; until then only the first listed, with the chat elsewhere, and the rest
/// text-only (<c>AdvisorClaudeGate</c>, retired).
/// </summary>
public partial class NeonAdvisorToolTests
{
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
    public void TheListEditorsRow_NamesAClaudeCodeAdvisor_WithNoMark()
    {
        // No `tools` or `text only` since 2026-10-13: every Claude Code advisor runs with the tools on a process of its own.
        Assert.Equal(Spectre.Console.Markup.Escape("dba  ") + NeonSidekick.UI.Theme.DimMarkup("architect    · claudecode · sonnet"),
            SettingsMenu.AdvisorRow(new AdvisorEntry("dba", "architect"), exists: true, 5, new AdvisorServerFacts(AdvisorServer.ClaudeCode, "sonnet"), 12));
    }
}
