using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Claude;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary><c>/claude</c> headless (2026-09-27), over <see cref="FakeClaudeCli"/>.</summary>
public partial class SidekickAppTests
{
    private readonly FakeClaudeCli _claudeCli = new();

    [Fact]
    public async Task Headless_Claude_StreamsUnderItsPrefix_NeedsNoServer_ResumesAndFeedsTheNextTurn()
    {
        _claudeCli.Enqueue(new ClaudeEvent.ToolActivity("Read", "a.txt"), new ClaudeEvent.TextDelta("It says hi."), FakeClaudeCli.Ok("s-1", denied: ["Bash"]));
        _claudeCli.EnqueueReply("s-1", "Again.");
        _claudeCli.EnqueueStartFailure(ClaudeText.NotFound);

        string output = await Headless("/claude read a.txt\n/claude and again\n/claude new\n/claude\n/claude once more\n");

        Assert.Contains("[tool] " + ClaudeText.ToolNote("Read", "a.txt"), output);
        Assert.Contains(SidekickApp.HeadlessClaudePrefix + "It says hi.", output);
        Assert.Contains("[notice] Claude was denied Bash (Claude slash command permissions: read-only).", output);
        Assert.Contains("[notice] " + ClaudeText.Footer(0.02m, FakeClaudeCli.Ok("s").Usage), output);
        Assert.Contains(SidekickApp.HeadlessClaudePrefix + "Again.", output);
        Assert.Contains("[notice] " + ClaudeText.NewThreadNotice, output);
        Assert.Contains("[error] " + ClaudeText.UsageError, output);
        Assert.Contains("[error] " + ClaudeText.NotFound, output);
        Assert.DoesNotContain(SidekickApp.HeadlessNoAssistantReply, output);   // no LLM server, and none needed
        Assert.Equal(3, _claudeCli.Requests.Count);
        Assert.False(_claudeCli.Requests[0].Resume);
        Assert.True(_claudeCli.Requests[1].Resume);
        Assert.Equal("s-1", _claudeCli.Requests[1].SessionId);
        Assert.False(_claudeCli.Requests[2].Resume);   // after /claude new
    }

    [Fact]
    public async Task Headless_Claude_TheLocalModelSeesTheTaggedExchange()
    {
        ServerOn1234("llama");
        _claudeCli.EnqueueReply("s-1", "Forty-two.");
        _chat.EnqueueText("Noted.");

        string output = await Headless("/claude the answer?\nwhat did Claude say?\n");

        Assert.Contains("Neon: Noted.", output);
        var request = Assert.Single(_chat.Requests);
        Assert.Contains(request, m => m.Role == ChatRole.User && m.Text == "[to Claude] the answer?");
        Assert.Contains(request, m => m.Role == ChatRole.Assistant && m.Text == "[Claude] Forty-two.");
    }

    [Fact]
    public async Task Headless_Advisor_ClaudesToolsAndTheFooter_AreLines_TheAnswerIsTheResult()
    {
        ServerOn1234("llama");
        _settings.Update(d => d.ClaudeAdvisor = true);
        _chat.Enqueue(FakeChatClient.Call("c1", Llm.Tools.ClaudeAdvisorTool.ToolName, new Dictionary<string, object?> { ["question"] = "Which parser?" }));
        _chat.EnqueueText("Streaming it is.");
        _claudeCli.Enqueue(new ClaudeEvent.ToolActivity("Grep", "Parse"), new ClaudeEvent.TextDelta("The streaming one."), FakeClaudeCli.Ok("adv-1"));

        string output = await Headless("pick a parser\n");

        Assert.Contains("[tool] " + ClaudeText.ToolNote("Grep", "Parse"), output);
        Assert.Contains("[notice] " + ClaudeText.Footer(0.02m, FakeClaudeCli.Ok("s").Usage, ClaudeText.AdvisorName), output);
        Assert.Contains("[tool] claude_advisor -> The streaming one.", output);
        Assert.Contains("Streaming it is.", output);
        Assert.Equal(ClaudePermissionLevel.ReadOnly, Assert.Single(_claudeCli.Requests).Permission);
    }

    [Fact]
    public async Task Headless_Advisor_UnderConfirm_IsRefused_NoOneToAsk()
    {
        ServerOn1234("llama");
        _settings.Update(d => { d.ClaudeAdvisor = true; d.ClaudeAdvisorConfirm = true; });
        _chat.Enqueue(FakeChatClient.Call("c1", Llm.Tools.ClaudeAdvisorTool.ToolName, new Dictionary<string, object?> { ["question"] = "Which parser?" }));
        _chat.EnqueueText("Alone, then.");

        string output = await Headless("pick a parser\n");

        Assert.Contains("[tool] claude_advisor -> " + ClaudeText.AdvisorNotAskedError, output);
        Assert.Empty(_claudeCli.Requests);
    }
}
