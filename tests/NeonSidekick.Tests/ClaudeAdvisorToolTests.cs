using Microsoft.Extensions.AI;
using NeonSidekick.Claude;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Plans;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// <c>claude_advisor_cli</c> (2026-09-27) over <see cref="FakeClaudeCli"/>: always read-only, its own thread resumed, the
/// per-turn cap, the opt-in confirm, the two context modes, the model and effort falling back to <c>/claude</c>'s, the
/// cost handed on, the failures as <c>Error:</c> sentences and a cancel that goes on up.
/// </summary>
public class ClaudeAdvisorToolTests
{
    private readonly FakeClaudeCli _cli = new();
    private readonly ClaudeAdvisorThread _thread = new();
    private readonly List<(TokenUsage Usage, decimal Usd)> _spent = [];
    private readonly List<ChatMessage> _history = [];
    private readonly RecordingView _view = new();
    private AppSettingsData _settings = new() { ClaudeCliAdvisor = true };

    private ClaudeAdvisorTool Tool(Func<string, CancellationToken, Task<bool?>>? confirm = null) =>
        new(_cli, () => _settings, () => @"C:\work", _thread, (usage, usd) => _spent.Add((usage, usd)), () => _history, confirm, _view);

    private static Task<object?> Ask(ClaudeAdvisorTool tool, string question, string? context = null, CancellationToken cancellationToken = default)
    {
        var arguments = new AIFunctionArguments { [ClaudeAdvisorTool.QuestionArgument] = question };
        if (context is not null)
        {
            arguments[ClaudeAdvisorTool.ContextArgument] = context;
        }

        return tool.InvokeAsync(arguments, cancellationToken).AsTask();
    }

    private sealed class RecordingView : IClaudeAdvisorView
    {
        public List<string> Lines { get; } = [];

        public void Began(string question) => Lines.Add("began " + question);

        public void Tool(string name, string detail) => Lines.Add("tool " + name + " " + detail);

        public void Answered(string answer, ClaudeEvent.Result result) => Lines.Add("answered " + answer);
    }

    [Fact]
    public void Shape_TheNameTheSchemaAndPlanMode()
    {
        var tool = Tool();

        Assert.Equal("claude_advisor_cli", tool.Name);
        var properties = tool.JsonSchema.GetProperty("properties");
        Assert.True(properties.TryGetProperty("question", out _));
        Assert.True(properties.TryGetProperty("context", out _));
        Assert.Equal("question", Assert.Single(tool.JsonSchema.GetProperty("required").EnumerateArray()).GetString());
        Assert.True(PlanTools.Allowed(ClaudeAdvisorTool.ToolName));   // it only reads: plan mode keeps it
    }

    [Fact]
    public async Task ACall_IsReadOnly_WhateverTheCommandsLevel_FramedOnce_ThenResumed_TheCostHandedOn()
    {
        _settings.ClaudeCliPermissions = "full";
        _settings.ClaudeCliModel = "sonnet";
        _settings.ClaudeCliEffort = "low";
        _cli.Enqueue(new ClaudeEvent.ToolActivity("Grep", "Parse"), new ClaudeEvent.TextDelta("Use the "), new ClaudeEvent.TextDelta("streaming one."), FakeClaudeCli.Ok("s-1"));
        _cli.EnqueueReply("s-1", "Still the streaming one.");
        var tool = Tool();

        var first = await Ask(tool, "Which parser should I use?", "Two exist: Dom and Stream.");
        var second = await Ask(tool, "Even for small files?");

        Assert.Equal("Use the streaming one.", first);
        Assert.Equal("Still the streaming one.", second);
        var one = _cli.Requests[0];
        Assert.Equal(ClaudePermissionLevel.ReadOnly, one.Permission);   // never the command's full
        Assert.Equal(("sonnet", "low"), (one.Model, one.Effort));        // blank advisor rows follow /claude's
        Assert.Equal(@"C:\work", one.WorkingDirectory);
        Assert.False(one.Resume);
        Assert.Equal(ClaudeText.AdvisorPrompt("Which parser should I use?", "Two exist: Dom and Stream.", null, first: true), one.Prompt);
        Assert.StartsWith(ClaudeText.AdvisorFraming, one.Prompt, StringComparison.Ordinal);
        Assert.Contains("\n\nContext from the assistant:\nTwo exist: Dom and Stream.", one.Prompt, StringComparison.Ordinal);
        var two = _cli.Requests[1];
        Assert.True(two.Resume);
        Assert.Equal("s-1", two.SessionId);
        Assert.Equal("Question: Even for small files?", two.Prompt);   // a resumed thread is not framed again
        Assert.Equal("s-1", _thread.SessionId);
        Assert.Equal(2, _spent.Count);
        Assert.Equal(0.02m, _spent[0].Usd);
        Assert.Equal(["began Which parser should I use?", "tool Grep Parse", "answered Use the streaming one.", "began Even for small files?", "answered Still the streaming one."], _view.Lines);
    }

    [Fact]
    public async Task TheAdvisorsOwnModelAndEffort_OutrankTheCommands()
    {
        _settings.ClaudeCliModel = "sonnet";
        _settings.ClaudeCliEffort = "low";
        _settings.ClaudeCliAdvisorModel = " opus ";
        _settings.ClaudeCliAdvisorEffort = "max";
        _cli.EnqueueReply("s-1", "ok");

        await Ask(Tool(), "q");

        Assert.Equal(("opus", "max"), (_cli.Requests[0].Model, _cli.Requests[0].Effort));
    }

    [Fact]
    public async Task ALostResume_StartsANewThread_FramedAgain()
    {
        _thread.SessionId = "gone";
        _cli.Enqueue(FakeClaudeCli.Failed("gone", "No conversation found with session ID: gone"));
        _cli.EnqueueReply("s-2", "fresh");

        var answer = await Ask(Tool(), "q");

        Assert.Equal("fresh", answer);
        Assert.True(_cli.Requests[0].Resume);
        Assert.False(_cli.Requests[1].Resume);
        Assert.NotEqual("gone", _cli.Requests[1].SessionId);
        Assert.StartsWith(ClaudeText.AdvisorFraming, _cli.Requests[1].Prompt, StringComparison.Ordinal);
        Assert.Equal("s-2", _thread.SessionId);
    }

    [Fact]
    public async Task TheCap_RefusesTheCallPastIt_AndBeginTurnStartsItOver()
    {
        _settings.ClaudeCliAdvisorCallsPerTurn = 1;
        _cli.EnqueueReply("s-1", "one").EnqueueReply("s-1", "two");
        var tool = Tool();

        Assert.Equal("one", await Ask(tool, "a"));
        Assert.Equal(ClaudeText.AdvisorCapError(1), await Ask(tool, "b"));
        tool.BeginTurn();
        Assert.Equal("two", await Ask(tool, "c"));
        Assert.Equal(2, _cli.Requests.Count);
        Assert.Equal("Error: claude_advisor_cli was already called 2 times this turn, the most allowed. Carry on without it.", ClaudeText.AdvisorCapError(2));
    }

    [Fact]
    public async Task Confirm_OffNeverAsks_OnAsksEachCall_NoAndNobodyToAskRunNothing()
    {
        var asked = new List<string>();
        var answers = new Queue<bool?>([false, null, true]);
        Task<bool?> Confirm(string question, CancellationToken _)
        {
            asked.Add(question);
            return Task.FromResult(answers.Dequeue());
        }

        _cli.EnqueueReply("s-1", "unasked").EnqueueReply("s-1", "yes");
        var tool = Tool(Confirm);

        Assert.Equal("unasked", await Ask(tool, "first"));   // Claude CLI advisor tool confirm off: the seam is never called
        Assert.Empty(asked);
        _settings.ClaudeCliAdvisorConfirm = true;
        _settings.ClaudeCliAdvisorCallsPerTurn = 10;
        Assert.Equal(ClaudeText.AdvisorDeclinedError, await Ask(tool, "second"));
        Assert.Equal(ClaudeText.AdvisorNotAskedError, await Ask(tool, "third"));
        Assert.Equal("yes", await Ask(tool, "fourth"));
        Assert.Equal(["second", "third", "fourth"], asked);
        Assert.Equal(2, _cli.Requests.Count);
        // No seam at all (headless): a refusal, never a run.
        Assert.Equal(ClaudeText.AdvisorNotAskedError, await Ask(new ClaudeAdvisorTool(_cli, () => _settings, () => "", new(), (_, _) => { }, () => []), "fifth"));
        Assert.Equal(2, _cli.Requests.Count);
    }

    [Fact]
    public async Task ContextRecent_SendsTheLastMessages_BriefDoesNot()
    {
        _history.Add(new ChatMessage(ChatRole.User, "Refactor the parser."));
        _history.Add(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c1", "read_file")]));
        _history.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent("c1", new string('x', 600))]));
        _cli.EnqueueReply("s-1", "a").EnqueueReply("s-1", "b");
        var tool = Tool();

        await Ask(tool, "q1");
        _settings.ClaudeCliAdvisorContext = "recent";
        await Ask(tool, "q2");

        Assert.DoesNotContain("The conversation so far", _cli.Requests[0].Prompt, StringComparison.Ordinal);
        Assert.Contains("\n\nThe conversation so far (its last 3 messages):\nuser: Refactor the parser.\nassistant: (called read_file)\ntool: " + new string('x', 500) + "…", _cli.Requests[1].Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void RecentLines_TheLastTenMessages_NoSystem_NoEmptyParts()
    {
        var messages = new List<ChatMessage> { new(ChatRole.System, "sys") };
        for (int i = 0; i < 12; i++)
        {
            messages.Add(new ChatMessage(i % 2 == 0 ? ChatRole.User : ChatRole.Assistant, "m" + i));
        }

        messages.Add(new ChatMessage(ChatRole.Assistant, " "));

        var lines = ClaudeAdvisorTool.RecentLines(messages);

        Assert.Equal(9, lines.Count);   // the last ten messages, one of them blank
        Assert.Equal("assistant: m3", lines[0]);
        Assert.Equal("assistant: m11", lines[^1]);
    }

    [Fact]
    public async Task Failures_AreErrorSentences_NoQuestionRunsNothing()
    {
        _cli.EnqueueStartFailure(ClaudeText.NotFound);
        _cli.Enqueue(FakeClaudeCli.Failed(null, "Not logged in"));
        _cli.Enqueue(FakeClaudeCli.Ok("s-1"));
        _settings.ClaudeCliAdvisorCallsPerTurn = 10;
        var tool = Tool();

        Assert.Equal(ClaudeText.AdvisorNoQuestionError, await Ask(tool, "  "));
        Assert.Empty(_cli.Requests);
        Assert.Equal(ClaudeText.AdvisorFailedError(ClaudeText.NotFound), await Ask(tool, "a"));
        Assert.Equal("Error: Claude advisor failed: Not logged in", await Ask(tool, "b"));
        Assert.Null(_thread.SessionId);   // a failed first call never became a thread
        Assert.Equal(ClaudeText.AdvisorNoAnswer, await Ask(tool, "c"));   // ok, but not a word
        Assert.Equal("s-1", _thread.SessionId);
    }

    [Fact]
    public async Task ACancelledTurn_GoesOnUp()
    {
        using var cts = new CancellationTokenSource();
        _cli.Enqueue(new ClaudeEvent.TextDelta("partial"), FakeClaudeCli.Ok("s-1"));
        _cli.BeforeEvent = (i, _) =>
        {
            if (i == 1)
            {
                cts.Cancel();
            }

            return Task.CompletedTask;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Ask(Tool(), "q", cancellationToken: cts.Token));
        Assert.Null(_thread.SessionId);
        Assert.Empty(_spent);
    }

    [Fact]
    public void Rules_TheAdvisorSentence_RidesAfterSql_OnlyWhenOffered()
    {
        string with = Assistant.DefaultRules(markdown: false, tools: true, sql: true, advisor: true);
        string without = Assistant.DefaultRules(markdown: false, tools: true, sql: true);

        Assert.Contains(Assistant.SqlRule + " " + Assistant.ClaudeAdvisorRule, with, StringComparison.Ordinal);
        Assert.DoesNotContain(ClaudeAdvisorTool.ToolName, without, StringComparison.Ordinal);
        Assert.DoesNotContain(ClaudeAdvisorTool.ToolName, Assistant.DefaultRules(markdown: false, tools: false, advisor: true), StringComparison.Ordinal);
    }

    [Fact]
    public void Context_TheTwoWords()
    {
        Assert.Equal(["brief", "recent"], ClaudeAdvisorContext.Names);
        Assert.False(ClaudeAdvisorContext.IsRecent("brief"));
        Assert.True(ClaudeAdvisorContext.IsRecent(" Recent "));
        Assert.False(ClaudeAdvisorContext.IsRecent("everything"));   // warned, read as brief
        Assert.Equal("brief", new AppSettingsData().ClaudeCliAdvisorContext);
        Assert.False(new AppSettingsData().ClaudeCliAdvisor);            // off by default: every call costs money
        Assert.False(new AppSettingsData().ClaudeCliAdvisorConfirm);     // opt-in
        Assert.Equal(2, new AppSettingsData().ClaudeCliAdvisorCallsPerTurn);
    }
}
