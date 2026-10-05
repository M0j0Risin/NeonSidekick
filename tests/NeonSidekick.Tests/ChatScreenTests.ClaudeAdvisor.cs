using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Claude;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Sessions;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;
using Spectre.Console.Testing;

namespace NeonSidekick.Tests;

/// <summary><c>claude_advisor_cli</c> on the screen (2026-09-27), over <see cref="FakeClaudeCli"/>.</summary>
public partial class ChatScreenTests
{
    private static Dictionary<string, object?> AdvisorArgs(string question) => new() { [ClaudeAdvisorTool.QuestionArgument] = question };

    [Fact]
    public async Task Advisor_IsOfferedOnlyWithItsSwitch_WithItsRule()
    {
        _settings.Update(d => d.TtsOutput = false);
        _chat.EnqueueText("one");
        PushLine("hello");
        PushLine("/exit");
        await RunAsync();
        Assert.DoesNotContain(_chat.Options[0]!.Tools!.Cast<AIFunction>(), t => t.Name == ClaudeAdvisorTool.ToolName);
        Assert.DoesNotContain(Assistant.ClaudeAdvisorRule, _chat.Requests[0][0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Advisor_ACall_ShowsTheQuestion_ClaudesTools_TheAnswerAndTheFooter_AndTheModelReadsTheAnswer()
    {
        _settings.Update(d => { d.TtsOutput = false; d.ClaudeCliAdvisor = true; });
        _chat.Enqueue(FakeChatClient.Call("c1", ClaudeAdvisorTool.ToolName, AdvisorArgs("Which parser?")));
        _chat.EnqueueText("I'll use the streaming one.");
        _claudeCli.Enqueue(new ClaudeEvent.ToolActivity("Grep", "Parse"), new ClaudeEvent.TextDelta("Use the streaming parser.\n\nIt is faster."), FakeClaudeCli.Ok("s-1"));
        PushLine("pick a parser");
        PushLine("/exit");

        string output = await RunAsync();

        var offered = _chat.Options[0]!.Tools!.Cast<AIFunction>().Select(t => t.Name).ToList();
        Assert.Contains(ClaudeAdvisorTool.ToolName, offered);
        Assert.Contains(" " + Assistant.ClaudeAdvisorRule, _chat.Requests[0][0].Text, StringComparison.Ordinal);
        Assert.Contains("🛠️ " + ClaudeText.AdvisorQuestionNote("Which parser?") + "\n", output);
        Assert.Contains("🛠️ " + ClaudeText.ToolNote("Grep", "Parse") + "\n", output);
        Assert.Contains(TranscriptRenderer.ToolAnswerIndent + "Use the streaming parser.\n", output);
        Assert.Contains(TranscriptRenderer.ToolAnswerIndent + "It is faster.\n", output);
        Assert.Contains("🛠️ " + ClaudeText.Footer(0.02m, FakeClaudeCli.Ok("s").Usage, ClaudeText.AdvisorName) + "\n", output);
        Assert.DoesNotContain("🛠️ " + ClaudeAdvisorTool.ToolName, output);   // the view's lines stand for the call's and the result's
        Assert.Contains("I'll use the streaming one.", output);
        Assert.Equal("Use the streaming parser.\n\nIt is faster.", ToolResult(_chat.Requests[1], "c1"));
        var asked = Assert.Single(_claudeCli.Requests);
        Assert.Equal(ClaudePermissionLevel.ReadOnly, asked.Permission);
        Assert.Equal(1, _session.Usage.ClaudeRuns);   // /usage's Claude row
    }

    [Fact]
    public async Task Advisor_KeepsItsOwnThread_ApartFromClaudes_Stored_AndNewDropsIt()
    {
        _settings.Update(d => { d.TtsOutput = false; d.ClaudeCliAdvisor = true; });
        _claudeCli.EnqueueReply("cmd-1", "command reply");
        _chat.Enqueue(FakeChatClient.Call("c1", ClaudeAdvisorTool.ToolName, AdvisorArgs("first?")));
        _chat.EnqueueText("ok");
        _claudeCli.EnqueueReply("adv-1", "advice one");
        _chat.Enqueue(FakeChatClient.Call("c2", ClaudeAdvisorTool.ToolName, AdvisorArgs("second?")));
        _chat.EnqueueText("ok again");
        _claudeCli.EnqueueReply("adv-1", "advice two");
        PushLine("/claude hello");
        PushLine("ask it");
        PushLine("ask again");
        PushLine("/exit");
        await RunAsync();

        Assert.False(_claudeCli.Requests[1].Resume);   // the advisor's first call: its own thread, not /claude's
        Assert.NotEqual("cmd-1", _claudeCli.Requests[1].SessionId);
        Assert.True(_claudeCli.Requests[2].Resume);
        Assert.Equal("adv-1", _claudeCli.Requests[2].SessionId);
        using var store = new SessionStore(_settings.ProfileDirectory, _time);
        var record = store.Load(Assert.Single(store.List(10)).Id)!;
        SessionHistory.FromJson(record.HistoryJson, out _, out _, out string? claudeId, out string? advisorId);
        Assert.Equal(("cmd-1", "adv-1"), (claudeId, advisorId));
    }

    [Fact]
    public async Task Advisor_NewForgetsTheThread()
    {
        _settings.Update(d => { d.TtsOutput = false; d.ClaudeCliAdvisor = true; });
        _chat.Enqueue(FakeChatClient.Call("c1", ClaudeAdvisorTool.ToolName, AdvisorArgs("first?")));
        _chat.EnqueueText("ok");
        _claudeCli.EnqueueReply("adv-1", "advice one");
        _chat.Enqueue(FakeChatClient.Call("c2", ClaudeAdvisorTool.ToolName, AdvisorArgs("second?")));
        _chat.EnqueueText("ok again");
        _claudeCli.EnqueueReply("adv-2", "advice two");
        PushLine("ask it");
        PushLine("/new");
        PushLine("ask again");
        PushLine("/exit");
        await RunAsync();

        Assert.False(_claudeCli.Requests[1].Resume);
        Assert.NotEqual("adv-1", _claudeCli.Requests[1].SessionId);
    }

    [Fact]
    public async Task Advisor_WithConfirmOn_NoPane_IsRefused_NothingRuns()
    {
        _settings.Update(d => { d.TtsOutput = false; d.ClaudeCliAdvisor = true; d.ClaudeCliAdvisorConfirm = true; });
        _chat.Enqueue(FakeChatClient.Call("c1", ClaudeAdvisorTool.ToolName, AdvisorArgs("q?")));
        _chat.EnqueueText("fine, alone then");
        PushLine("ask it");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Empty(_claudeCli.Requests);
        Assert.Equal(ClaudeText.AdvisorNotAskedError, ToolResult(_chat.Requests[1], "c1"));
        Assert.Contains("✗  Error: claude_advisor_cli needs the user's yes", output);
    }

    /// <summary>The model calls the advisor under <c>Claude CLI advisor tool confirm</c>, the pane is answered with <paramref name="keys"/>, then the reply.</summary>
    private void AdvisorConfirmFixture(ConsoleKeyInfo[] keys)
    {
        _settings.Update(d => { d.TtsOutput = false; d.ClaudeCliAdvisor = true; d.ClaudeCliAdvisorConfirm = true; });
        _console.Profile.Height = 40;
        _geometry = new ScreenGeometry(() => null);
        _chat.Enqueue(FakeChatClient.Call("c1", ClaudeAdvisorTool.ToolName, AdvisorArgs("Which parser?")));
        _chat.EnqueueText("done");
        _claudeCli.EnqueueReply("s-1", "the streaming one");
        var input = Scripted();
        StepsWhenIdle(Line("ask it"), Line("/exit"));
        var idle = input.OnWait!;
        bool answered = false;
        input.OnWait = () =>
        {
            if (_keys is { PendingLine.IsCompleted: false })
            {
                if (!answered)
                {
                    answered = true;
                    input.Push(keys);
                }

                return;
            }

            idle();
        };
    }

    [Fact]
    public async Task Advisor_WithConfirmOn_ThePaneAsks_YesRuns()
    {
        AdvisorConfirmFixture([Keys.Down, Keys.Enter]);

        string output = await RunAsync();

        Assert.Contains(Titled(ClaudeText.AdvisorConfirmQuestion("Which parser?")), output);
        Assert.Equal("the streaming one", ToolResult(_chat.Requests[1], "c1"));
        Assert.Single(_claudeCli.Requests);
    }

    [Fact]
    public async Task Advisor_WithConfirmOn_ThePaneAsks_NoRunsNothing()
    {
        AdvisorConfirmFixture([Keys.Enter]);   // the cursor starts on No

        string output = await RunAsync();

        Assert.Equal(ClaudeText.AdvisorDeclinedError, ToolResult(_chat.Requests[1], "c1"));
        Assert.Empty(_claudeCli.Requests);
        Assert.Contains("✗  Error: the user declined to let you ask Claude", output);
    }
}
