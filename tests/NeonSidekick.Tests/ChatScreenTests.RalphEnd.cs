using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Audits;
using NeonSidekick.Plans;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// The end of a Ralph loop and the start of a code audit on the screen (2026-11-22, the session review): a loop that ran a pass and wasn't paused
/// leaves the next message a new conversation and session; a pass that ends without <c>finish_step</c> is asked once to call it; a fix plan's
/// standing steps tick their findings in the report, and its done end points at <c>/code-audit resume</c>; <c>/code-audit</c> starts afresh.
/// </summary>
public partial class ChatScreenTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ralph_ALoopThatRanAPass_LeavesTheNextMessage_ANewConversationAndSession(bool blocked)
    {
        RalphPlan();
        RalphFixture(Line("/plan ralph add-guide"), Yes, Line("what next?"), Line("/exit"));
        if (blocked)
        {
            RalphPass("1", "blocked", "Need a name.");
        }
        else
        {
            RalphPass("1");
            RalphPass("2");
        }

        _chat.EnqueueText("Nothing.");

        string output = await RunAsync();

        var request = _chat.Requests[^1];
        Assert.Equal("what next?", LastUserText(request));
        Assert.Single(request, Llm.ConversationHistory.IsTurnStart);
        Assert.DoesNotContain(request, m => m.Text.StartsWith("Pass ", StringComparison.Ordinal) && m.Text.EndsWith(" over.", StringComparison.Ordinal));
        // The stop's notice above the new-conversation rule.
        string stop = blocked ? RalphText.StopNotice(RalphStop.Blocked, PlanPath, 0, 2) : RalphText.StopNotice(RalphStop.Done, PlanPath, 2, 2);
        Assert.True(output.IndexOf(stop, StringComparison.Ordinal) < output.LastIndexOf(ChatScreen.NewConversationNotice, StringComparison.Ordinal));
        // An ordinary plan's end says no re-audit hint.
        Assert.DoesNotContain("/code-audit resume", output);
        using var store = new Sessions.SessionStore(_settings.ProfileDirectory, _time);
        var rows = store.List(10).Select(s => store.Load(s.Id)!).ToList();
        var row = Assert.Single(rows, r => r.Turns.Any(t => t.UserText == "what next?"));
        Assert.Single(row.Turns);
    }

    [Fact]
    public async Task Ralph_ALoopThatEndsBeforeAnyPass_KeepsTheChat()
    {
        RalphPlan(RalphSteps.Replace("- [ ]", "- [x]", StringComparison.Ordinal));
        RalphFixture(Line("hello there"), Line("/plan ralph add-guide"), Yes, Line("and again"), Line("/exit"));
        _chat.EnqueueText("Hi.").EnqueueText("Again.");

        string output = await RunAsync();

        Assert.Equal(2, _chat.Requests.Count);
        Assert.Contains(_chat.Requests[1], m => m.Text == "Hi.");
        Assert.DoesNotContain(ChatScreen.NewConversationNotice, output);
    }

    [Fact]
    public async Task Ralph_APassThatEndsWithoutFinishStep_IsAskedOnce_AndThenStands()
    {
        RalphPlan();
        RalphFixture(Line("/plan ralph add-guide"), Yes, Line("/exit"));
        _chat.EnqueueText("\n\n");   // the session's pass 12: a blank reply, no finish_step
        RalphPass("1");              // the nudge's answer
        RalphPass("2");

        string output = await RunAsync();

        Assert.Equal(5, _chat.Requests.Count);
        Assert.Equal(RalphText.NudgeMessage, LastUserText(_chat.Requests[1]));
        Assert.Contains(_chat.Requests[1], m => m.Role == ChatRole.User && m.Text == LastUserText(_chat.Requests[0]));   // the pass's own conversation
        Assert.Contains(RalphText.NudgeLine(1), output);
        Assert.Contains(RalphText.StepDoneNotice(1, 2, "One"), output);
        Assert.Contains("Two", LastUserText(_chat.Requests[3]));
        Assert.Equal(PlanStatus.Done, PlanFileStatus);
    }

    [Fact]
    public async Task Ralph_ASecondEndingWithoutFinishStep_IsNotAskedAgain()
    {
        RalphPlan();
        RalphFixture(Line("/plan ralph add-guide"), Yes, Line("/exit"));
        _chat.EnqueueText("\n\n");
        _chat.EnqueueText("Done.");   // the nudge's answer, still no call: judged with no report
        RalphPass("2");               // step one again, in a fresh pass
        RalphPass("3");

        string output = await RunAsync();

        Assert.Equal(6, _chat.Requests.Count);
        Assert.Single(_chat.Requests, r => LastUserText(r) == RalphText.NudgeMessage);
        Assert.Contains("One", LastUserText(_chat.Requests[2]));
        Assert.Single(_chat.Requests[2], Llm.ConversationHistory.IsTurnStart);
        Assert.Equal(PlanStatus.Done, PlanFileStatus);
    }

    [Fact]
    public async Task Ralph_AFixPlansStandingSteps_TickTheirFindings_AndItsEndPointsAtAResume()
    {
        const string report = ".neon/audits/tree-2026-11-22.md";
        var now = _time.GetLocalNow();
        var audit = new AuditReport { Label = "tree", Header = new AuditHeader(AuditStatus.Fixing, "3 passes", "", AuditFocus.Dimensions, "", "model", 1, now, now) };
        foreach (string id in new[] { "A1", "A2" })
        {
            audit.Findings.Add(new AuditFinding(id, AuditSeverity.High, "correctness", "src/a.cs", 3, "Problem " + id, "Detail.", null, null, 1, false, false, null, []));
        }

        Assert.Null(NeonSidekick.Audits.AuditFiles.Save(AuditFiles(), report, audit, now));
        RalphPlan("# Fix\n\n## Steps\n- [ ] One (closes A1)\n  Done when: one.md exists\n- [ ] Two (closes A2)\n  Done when: two.md exists\n",
            requirement: AuditText.FixRequirement(report, ["A1", "A2"]));
        RalphFixture(Line("/plan ralph add-guide"), Yes, Line("/exit"));
        RalphPass("1");
        RalphPass("2");

        string output = await RunAsync();

        var read = NeonSidekick.Audits.AuditFiles.Read(AuditFiles(), report)!;
        Assert.All(read.Findings, f => Assert.True(f.Settled));
        Assert.Equal(AuditText.SettledNote("add-guide", 1), read.Findings[0].Notes[^1]);
        Assert.Contains(AuditText.TickedNotice(["A1"], "tree-2026-11-22", 1, 2), output);
        Assert.Contains(AuditText.TickedNotice(["A2"], "tree-2026-11-22", 2, 2), output);
        Assert.Contains(AuditText.ReauditHint("tree-2026-11-22"), output);
    }

    [Fact]
    public async Task CodeAudit_AfterAChat_StartsInANewConversation()
    {
        _settings.Update(d => d.CodeAuditCleanPasses = 1);
        AuditFixture(Line("hello there"), Line("/code-audit src security"), Key(Keys.Enter), Line("/exit"));
        _chat.EnqueueText("Hi.");
        _chat.Enqueue(FindingCall("a1"));
        _chat.EnqueueText("Pass 1 done.");
        _chat.Enqueue(CleanCall("a2"));
        _chat.EnqueueText("Pass 2 done.");

        string output = await RunAsync();

        Assert.DoesNotContain(_chat.Requests[1], m => m.Text == "hello there" || m.Text == "Hi.");
        Assert.Single(_chat.Requests[1], Llm.ConversationHistory.IsTurnStart);
        Assert.Contains(ChatScreen.NewConversationNotice, output);
    }
}
