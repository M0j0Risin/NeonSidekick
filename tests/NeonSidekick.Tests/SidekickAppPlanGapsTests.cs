using Microsoft.Extensions.AI;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Plans;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// Plan mode's gap-closing loop headless (2026-10-13): <c>/plan gaps</c> typed, each next pass sent as the next line with no stdin
/// read between, the end said with <c>/plan approve</c>; no ESC, so a loop with no limit stops at the most the setting allows.
/// </summary>
public partial class SidekickAppTests
{
    private const string HeadlessPlanPath = ".neon/plans/add-guide.md";

    /// <summary>The steps the test's <see cref="HeadlessPresentGaps"/> calls added so far, in the order they are scripted (2026-11-17).</summary>
    private readonly List<string> _headlessGapSteps = [];

    /// <summary>
    /// A pass's <c>present_plan</c>; with no gaps, a <c>checked</c> line too (2026-10-17). The body carries forward (2026-11-17, PlanLoss): each
    /// call's gaps are steps added to those the earlier calls added, so a clean call keeps them rather than dropping them.
    /// </summary>
    private ChatResponseUpdate HeadlessPresentGaps(string id, params string[] gaps)
    {
        _headlessGapSteps.AddRange(gaps);
        return HeadlessPresentGapsOver(id, HeadlessGapBody(_headlessGapSteps), gaps);
    }

    private static string HeadlessGapBody(IEnumerable<string> steps) =>
        "# Guide\n\n- [ ] Write it\n  Done when: guide.md exists\n" + string.Concat(steps.Select(g => "- [ ] " + g + "\n  Done when: " + g + " is done\n"));

    private static ChatResponseUpdate HeadlessPresentGapsOver(string id, string markdown, params string[] gaps)
    {
        var args = new Dictionary<string, object?>
        {
            [PresentPlanTool.TitleArgument] = "Guide",
            [PresentPlanTool.MarkdownArgument] = markdown,
            [PresentPlanTool.GapsArgument] = gaps,
        };
        if (gaps.Length == 0)
        {
            args[PresentPlanTool.CheckedArgument] = new[] { "the steps" };
        }

        return FakeChatClient.Call(id, PresentPlanTool.ToolName, args);
    }

    private static string? HeadlessLastUser(IReadOnlyList<ChatMessage> request) => request.Last(m => m.Role == ChatRole.User).Text;

    /// <summary>The headless plan with its test step reworded: a revision that loses nothing.</summary>
    private const string Reworded = "# Guide\n\n- [ ] Write it\n  Done when: guide.md exists\n- [ ] Test the guide\n  Done when: Test it is done\n";

    [Fact]
    public async Task Headless_PlanGaps_ATransientFailure_IsRetriedOnce_AndTheReviewGoesOn()
    {
        // 2026-11-17, the gap-audit-reliability plan's step 2: after the row, a wait, the failed turn dropped, the same message again.
        ServerOn1234("llama");
        _chat.Enqueue(HeadlessPresentPlan());
        _chat.EnqueueText("Saved.");
        _chat.EnqueueText("(never seen)");                      // 2: refused
        _chat.Enqueue(HeadlessPresentGaps("p2", "Test it"));    // 3: the retry
        _chat.EnqueueText("Pass 1 done.");
        _chat.BeforeUpdateOf = (request, update, _) => request == 2
            ? throw new System.Net.Http.HttpRequestException("refused", new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused))
            : Task.CompletedTask;

        string output = await Headless("/plan Add a guide\n/plan gaps 1\n");

        Assert.Equal(5, _chat.Requests.Count);
        string message = PlanText.GapPassMessage(HeadlessPlanPath, 1, 1, PlanReviewer.Model);
        Assert.Equal(message, HeadlessLastUser(_chat.Requests[3]));
        Assert.Single(_chat.Requests[3], m => m.Role == ChatRole.User && m.Text == message);
        Assert.Contains("[notice] " + PlanText.GapRetryNotice(1, TimeSpan.Zero), output);
        Assert.Contains("[notice] " + PlanText.GapStopNotice(PlanGapStop.Limit, 1, 1), output);
    }

    [Fact]
    public async Task Headless_PlanGaps_AFailedPass_StopsTheReview()
    {
        // 2026-11-17: headless judged a failed pass as one that ended on its own (NoRevision, or more passes sent to a server that was down).
        ServerOn1234("llama");
        _chat.Enqueue(HeadlessPresentPlan());
        _chat.EnqueueText("Saved.");
        _chat.EnqueueText("(never seen)");
        _chat.BeforeUpdateOf = (request, update, _) => request == 2 ? throw new System.Net.Http.HttpRequestException("the server went away") : Task.CompletedTask;

        string output = await Headless("/plan Add a guide\n/plan gaps\n");

        Assert.Equal(3, _chat.Requests.Count);
        Assert.Contains("[notice] " + PlanText.GapStopNotice(PlanGapStop.Stopped, 1, 0), output);
        Assert.DoesNotContain(PlanText.GapFollowUpMessage(HeadlessPlanPath, 1), output);
    }

    [Fact]
    public async Task Headless_PlanGapsResume_SaysNothingIsPaused()
    {
        // 2026-11-17: headless never pauses a review (no line is read during its passes).
        ServerOn1234("llama");
        _chat.Enqueue(HeadlessPresentPlan());
        _chat.EnqueueText("Saved.");

        string output = await Headless("/plan gaps resume\n/plan Add a guide\n/plan gaps resume\n");

        Assert.Contains("[error] " + PlanText.NothingPausedError, output);
        Assert.Equal(2, _chat.Requests.Count);
    }

    [Fact]
    public async Task Headless_PlanGaps_ACleanReportThatDropsAStep_IsRefused_AndTheSameTurnListsIt()
    {
        // 2026-11-17, the gap-audit-reliability plan (PlanLoss): refused, nothing counted; the model's own second call in the turn lists it.
        ServerOn1234("llama");
        string dropped = HeadlessGapBody([]);
        _chat.Enqueue(HeadlessPresentPlan());
        _chat.EnqueueText("Saved.");
        _chat.Enqueue(HeadlessPresentGaps("p2", "Test it"));
        _chat.EnqueueText("Pass 1 done.");
        _chat.Enqueue(HeadlessPresentGapsOver("p3", dropped));
        _chat.Enqueue(HeadlessPresentGapsOver("p4", dropped, "Test it repeated Write it"));
        _chat.EnqueueText("Pass 2 done.");

        string output = await Headless("/plan Add a guide\n/plan gaps 2\n");

        Assert.Equal(7, _chat.Requests.Count);
        var loss = PlanLoss.Compare(HeadlessGapBody(["Test it"]), dropped);
        Assert.Equal(["Test it"], loss.Steps);
        Assert.Equal(PlanText.LossCleanResult(loss), _chat.Requests[5].SelectMany(m => m.Contents.OfType<FunctionResultContent>()).Single(r => r.CallId == "p3").Result);
        Assert.EndsWith(PlanText.LossSentence(loss), (string)_chat.Requests[6].SelectMany(m => m.Contents.OfType<FunctionResultContent>()).Single(r => r.CallId == "p4").Result!, StringComparison.Ordinal);
        Assert.Contains("[notice] " + PlanText.PassNoticeLines(2, 2, 3, ["Test it repeated Write it"])[0], output);
    }

    [Fact]
    public async Task Headless_PlanGaps_RunsPassesWithNoInput_UntilNoneAreLeft()
    {
        _settings.Update(d => d.PlanGapCleanPasses = 2);   // the confirming pass is this test's subject (1 is the default since 2026-11-19)
        ServerOn1234("llama");
        _chat.Enqueue(HeadlessPresentPlan());
        _chat.EnqueueText("Saved.");
        _chat.Enqueue(HeadlessPresentGaps("p2", "Test it"));
        _chat.EnqueueText("Pass 1 done.");
        _chat.Enqueue(HeadlessPresentGapsOver("p3", Reworded));
        _chat.EnqueueText("None found.");
        _chat.Enqueue(HeadlessPresentGapsOver("p4", Reworded));
        _chat.EnqueueText("Nothing left.");

        string output = await Headless("/plan Add a guide\n/plan gaps\n/plan open\n");

        Assert.Equal(8, _chat.Requests.Count);
        Assert.Equal(PlanText.GapPassMessage(HeadlessPlanPath, 1, 5, PlanReviewer.Model), HeadlessLastUser(_chat.Requests[2]));
        Assert.Equal(PlanText.GapPassMessage(HeadlessPlanPath, 2, 5, PlanReviewer.Model), HeadlessLastUser(_chat.Requests[4]));
        Assert.Contains("A gap review is running (pass 2 of 5)", _chat.Requests[4][0].Text);
        Assert.Equal(PlanText.GapPassResult(HeadlessPlanPath, 2, 1, bodyChars: PlanGapLoopTests.BodyChars("# Guide\n\n- [ ] Write it\n  Done when: guide.md exists\n- [ ] Test it\n  Done when: Test it is done\n")), _chat.Requests[3].SelectMany(m => m.Contents.OfType<FunctionResultContent>()).Single(r => r.CallId == "p2").Result);
        // Pass 2's markdown rewords the test step, so it is a revision with no gaps: clean 1 of 2, and pass 3 confirms (2026-10-17). It loses
        // nothing (2026-11-17: a clean pass that dropped the step, as this one did before, is refused, PlanLoss).
        Assert.Equal(PlanText.CleanPassResult(HeadlessPlanPath, 3, 1, 2, bodyChars: PlanGapLoopTests.BodyChars(Reworded)), _chat.Requests[5].SelectMany(m => m.Contents.OfType<FunctionResultContent>()).Single(r => r.CallId == "p3").Result);
        Assert.Equal(PlanText.GapPassMessage(HeadlessPlanPath, 3, 5, PlanReviewer.Model, cleanSoFar: 1), HeadlessLastUser(_chat.Requests[6]));
        Assert.Equal(PlanText.SavedResult(HeadlessPlanPath), _chat.Requests[7].SelectMany(m => m.Contents.OfType<FunctionResultContent>()).Single(r => r.CallId == "p4").Result);
        Assert.Contains("You: " + PlanText.GapPassMessage(HeadlessPlanPath, 2, 5, PlanReviewer.Model), output);
        Assert.Contains("[notice] " + PlanText.PassNoticeLines(1, 5, 2, ["Test it"])[0], output);
        Assert.Contains("[notice] " + PlanText.CleanPassNotice(2, 5, 1, 2), output);
        Assert.True(output.IndexOf("[notice] " + PlanText.CleanPassNotice(2, 5, 1, 2), StringComparison.Ordinal) < output.IndexOf("You: " + PlanText.GapPassMessage(HeadlessPlanPath, 3, 5, PlanReviewer.Model, cleanSoFar: 1), StringComparison.Ordinal));
        Assert.Contains("[notice] " + PlanText.Glyph + " Gap review, 3 passes", output);
        Assert.True(output.IndexOf("[notice] " + PlanText.Glyph + " Gap review, 3 passes", StringComparison.Ordinal) < output.IndexOf(PlanText.ConvergedNotice(HeadlessPlanPath, 3, 1, 2), StringComparison.Ordinal));
        Assert.Contains("[notice] " + PlanText.ConvergedNotice(HeadlessPlanPath, 3, 1, 2) + " — /plan approve [--fresh | --ralph] starts it", output);
        // The review line, and /plan open lists the plan confirmed.
        Assert.Contains(PlanText.ReviewMarkWord(PlanReviewMark.Confirmed), output[output.LastIndexOf("add-guide — Guide", StringComparison.Ordinal)..]);
        // A pass's revision is said by its own notice, not the saved line every pass.
        Assert.DoesNotContain("[notice] " + PlanText.SavedNotice(HeadlessPlanPath, 2), output);
    }

    [Fact]
    public async Task Headless_PlanGaps_TwoPresentsInOneResponse_TheSecondIsRefused()
    {
        // 2026-11-17, one revision a pass: a model that batches its calls sends both in one response; the first saves, the second is refused,
        // and the shadow notes the first call's gaps alone.
        ServerOn1234("llama");
        _chat.Enqueue(HeadlessPresentPlan());
        _chat.EnqueueText("Saved.");
        _chat.Enqueue(HeadlessPresentGaps("p2", "[high] Test it"), HeadlessPresentGapsOver("p2b", HeadlessGapBody(["Test it", "Ship it"]), "[low] Ship it"));
        _chat.EnqueueText("Pass 1 done.");
        _chat.Enqueue(HeadlessPresentGaps("p3"));
        _chat.EnqueueText("None found.");
        _chat.Enqueue(HeadlessPresentGaps("p4"));
        _chat.EnqueueText("Nothing left.");
        var shadow = new List<string>();
        Action<NeonSidekick.Diagnostics.DiagnosticEvent> capture = e => { if (e.Message.StartsWith(PlanText.ShadowLogPrefix, StringComparison.Ordinal)) shadow.Add(e.Message); };
        NeonSidekick.Diagnostics.DiagnosticLog.Emitted += capture;
        string output;
        try
        {
            output = await Headless("/plan Add a guide\n/plan gaps\n");
        }
        finally
        {
            NeonSidekick.Diagnostics.DiagnosticLog.Emitted -= capture;
        }

        Assert.Contains("[tool] present_plan -> " + PlanText.PassAlreadySavedResult(HeadlessPlanPath, 2), output);
        Assert.DoesNotContain("· [low] Ship it", output);
        Assert.DoesNotContain("Ship it", File.ReadAllText(HeadlessPlanFile));
        Assert.StartsWith("plan gaps shadow: pass 1: 1 high (serious);", shadow[0]);
    }

    [Fact]
    public async Task Headless_PlanGaps_TheShadowLine_OnceAPass_TheConvergedPassToo()
    {
        _settings.Update(d => d.PlanGapCleanPasses = 2);   // the confirming pass is this test's subject (1 is the default since 2026-11-19)
        // 2026-11-17, phase 3: tagged gaps counted by their tags, logged once a pass, the pass that converged inside its turn included.
        ServerOn1234("llama");
        _chat.Enqueue(HeadlessPresentPlan());
        _chat.EnqueueText("Saved.");
        _chat.Enqueue(HeadlessPresentGaps("p2", "[high] Test it", "Low: Lint it"));
        _chat.EnqueueText("Pass 1 done.");
        _chat.Enqueue(HeadlessPresentGaps("p3"));
        _chat.EnqueueText("None found.");
        _chat.Enqueue(HeadlessPresentGaps("p4"));
        _chat.EnqueueText("Nothing left.");
        var shadow = new List<string>();
        Action<NeonSidekick.Diagnostics.DiagnosticEvent> capture = e => { if (e.Message.StartsWith(PlanText.ShadowLogPrefix, StringComparison.Ordinal)) shadow.Add(e.Message); };
        NeonSidekick.Diagnostics.DiagnosticLog.Emitted += capture;
        try
        {
            await Headless("/plan Add a guide\n/plan gaps\n");
        }
        finally
        {
            NeonSidekick.Diagnostics.DiagnosticLog.Emitted -= capture;
        }

        Assert.Equal(
            ["plan gaps shadow: pass 1: 1 high, 1 low (serious); shadow streak 0 of 2; real: found something",
             "plan gaps shadow: pass 2: no gaps; shadow streak 1 of 2; real: clean 1 of 2",
             "plan gaps shadow: pass 3: no gaps; shadow streak 2 of 2, would converge; real: converged"],
            shadow);
    }

    [Fact]
    public async Task Headless_PlanGaps_AnAdvisorReviewer_WithNoAdvisorOffered_IsTheModelOnEveryPass()
    {
        // 2026-10-14, the review: the first pass named the model, passes 2..N the advisor asked for.
        ServerOn1234("llama");
        _settings.Update(d => d.PlanGapCleanPasses = 1);
        _chat.Enqueue(HeadlessPresentPlan());
        _chat.EnqueueText("Saved.");
        _chat.Enqueue(HeadlessPresentGaps("p2", "Test it"));
        _chat.EnqueueText("Pass 1 done.");
        _chat.Enqueue(HeadlessPresentGaps("p3"));
        _chat.EnqueueText("Nothing left.");

        await Headless("/plan Add a guide\n/plan gaps advisor\n");

        Assert.Equal(PlanText.GapPassMessage(HeadlessPlanPath, 1, 5, PlanReviewer.Model), HeadlessLastUser(_chat.Requests[2]));
        Assert.Equal(PlanText.GapPassMessage(HeadlessPlanPath, 2, 5, PlanReviewer.Model), HeadlessLastUser(_chat.Requests[4]));
    }

    [Fact]
    public async Task Headless_PlanGaps_StopsAtTheLimit()
    {
        ServerOn1234("llama");
        _settings.Update(d => d.PlanGapPasses = 1);
        _chat.Enqueue(HeadlessPresentPlan());
        _chat.EnqueueText("Saved.");
        _chat.Enqueue(HeadlessPresentGaps("p2", "Test it"));
        _chat.EnqueueText("Pass 1 done.");

        string output = await Headless("/plan Add a guide\n/plan gaps\n");

        Assert.Equal(4, _chat.Requests.Count);
        Assert.Contains("[notice] " + PlanText.GapStopNotice(PlanGapStop.Limit, 1, 1) + " — /plan approve [--fresh | --ralph] starts it", output);
    }

    [Fact]
    public async Task Headless_PlanGaps_NoLimitMeansTheMost_AndAdvisorConfirmFallsBackToTheModel()
    {
        ServerOn1234("llama");
        _settings.Update(d =>
        {
            d.PlanGapPasses = 0;
            d.Advisors = true;
            d.AdvisorConfirm = true;
            d.AdvisorList = [new AdvisorEntry("dba", "database architect")];
        });
        _chat.Enqueue(HeadlessPresentPlan());
        _chat.EnqueueText("Saved.");
        _chat.EnqueueText("Which framework?");
        _chat.EnqueueText("I need the framework first.");   // the pass's one follow-up (2026-11-17): still no revision

        string output = await Headless("/plan Add a guide\n/plan gaps advisor\n/plan gaps nonsense\n");

        Assert.Contains("[notice] " + PlanText.GapsConfirmFallbackNotice, output);
        Assert.Contains("[notice] " + PlanText.GapsHeadlessCapNotice(AppSettingsData.MaxPlanGapPasses), output);
        Assert.Equal(PlanText.GapPassMessage(HeadlessPlanPath, 1, AppSettingsData.MaxPlanGapPasses, PlanReviewer.Model), HeadlessLastUser(_chat.Requests[2]));
        Assert.Equal(PlanText.GapFollowUpMessage(HeadlessPlanPath, 1), HeadlessLastUser(_chat.Requests[3]));
        Assert.Contains("[notice] " + PlanText.GapStopNotice(PlanGapStop.NoRevision, 1, 0), output);
        Assert.Contains("[error] " + PlanText.UnknownReviewerError("nonsense"), output);
        Assert.Equal(4, _chat.Requests.Count);
    }

    [Fact]
    public async Task Headless_PlanGaps_ACount_CapsThisRun_AndZeroSaysItWasTyped()
    {
        // 2026-10-17, the user's ask: the count replaces Plan gap max passes for the run; 0 is still the most headless (50 since 2026-10-18, 100 since 2026-10-21).
        ServerOn1234("llama");
        _chat.Enqueue(HeadlessPresentPlan());
        _chat.EnqueueText("Saved.");
        _chat.Enqueue(HeadlessPresentGaps("p2", "Test it"));
        _chat.EnqueueText("Pass 1 done.");
        _chat.EnqueueText("Which framework?");
        _chat.EnqueueText("I need the framework first.");   // the pass's one follow-up (2026-11-17)

        string output = await Headless("/plan Add a guide\n/plan gaps model 1\n/plan gaps 0\n/plan gaps 101\n");

        Assert.Equal(6, _chat.Requests.Count);
        Assert.Equal(PlanText.GapPassMessage(HeadlessPlanPath, 1, 1, PlanReviewer.Model), HeadlessLastUser(_chat.Requests[2]));
        Assert.Contains("[notice] " + PlanText.GapStopNotice(PlanGapStop.Limit, 1, 1) + " — /plan approve [--fresh | --ralph] starts it", output);
        Assert.Contains("[notice] " + PlanText.GapsHeadlessCapNotice(AppSettingsData.MaxPlanGapPasses, fromCommand: true), output);
        Assert.DoesNotContain(PlanText.GapsHeadlessCapNotice(AppSettingsData.MaxPlanGapPasses), output);
        Assert.Equal(PlanText.GapPassMessage(HeadlessPlanPath, 1, AppSettingsData.MaxPlanGapPasses, PlanReviewer.Model), HeadlessLastUser(_chat.Requests[4]));
        Assert.Contains("[error] " + PlanText.GapPassesRangeError("101"), output);
    }

    /// <summary>The result <c>present_plan</c> gave the call <paramref name="callId"/>, as the next request carries it.</summary>
    private static string? HeadlessResultOf(IReadOnlyList<ChatMessage> request, string callId) =>
        request.SelectMany(m => m.Contents.OfType<FunctionResultContent>()).Single(r => r.CallId == callId).Result?.ToString();

    [Fact]
    public async Task Headless_PlanGaps_PassOneHearsTheMeasuredProblem_AndAGapCallWithItIsSentBackOnce()
    {
        // 2026-11-18 (phase 3 item 2, known problems up front): revision 1 has a step without its Done when line.
        ServerOn1234("llama");
        _chat.Enqueue(HeadlessPresentPlan("# Guide\n\n- [ ] Write it\n"));
        _chat.EnqueueText("Saved.");
        _chat.Enqueue(HeadlessPresentGapsOver("p2", "# Guide\n\n- [ ] Write it\n- [ ] Test it\n  Done when: the test passes\n", "Test it"));   // 2: refused
        _chat.Enqueue(HeadlessPresentGapsOver("p3", HeadlessGapBody(["Test it"]), "Test it", "Give Write it its check"));                   // 3: fixed, saved
        _chat.EnqueueText("Pass 1 done.");

        string output = await Headless("/plan Add a guide\n/plan gaps 1\n");

        Assert.Equal(5, _chat.Requests.Count);
        Assert.Equal(PlanText.GapPassMessage(HeadlessPlanPath, 1, 1, PlanReviewer.Model, measured: [RalphText.FitNoDoneWhen(1)]), HeadlessLastUser(_chat.Requests[2]));
        Assert.Equal(PlanText.ShapeGapResult([RalphText.FitNoDoneWhen(1)]), HeadlessResultOf(_chat.Requests[3], "p2"));
        Assert.StartsWith("Revision 2 of " + HeadlessPlanPath + " is saved with 2 gaps closed.", HeadlessResultOf(_chat.Requests[4], "p3"));
        Assert.DoesNotContain("The app measured", HeadlessResultOf(_chat.Requests[4], "p3"));
        Assert.Contains("[notice] " + PlanText.GapStopNotice(PlanGapStop.Limit, 1, 2), output);
    }

    [Fact]
    public async Task Headless_PlanGaps_AProblemLeftByASecondTry_IsNamedInTheNextPassesMessage()
    {
        // 2026-11-18: the refusal is once a pass, so a second call that still has the problem saves; pass 2's message names it.
        ServerOn1234("llama");
        string noCheck = HeadlessGapBody(["Test it"]) + "- [ ] Ship it\n";
        _chat.Enqueue(HeadlessPresentPlan());
        _chat.EnqueueText("Saved.");
        _chat.Enqueue(HeadlessPresentGapsOver("p2", noCheck, "Test it", "Ship it"));   // 2: refused
        _chat.Enqueue(HeadlessPresentGapsOver("p3", noCheck, "Test it", "Ship it"));   // 3: taken as it is
        _chat.EnqueueText("Pass 1 done.");
        _chat.Enqueue(HeadlessPresentGapsOver("p4", HeadlessGapBody(["Test it", "Ship it"]), "Give Ship it its check"));   // 5: pass 2 fixes it
        _chat.EnqueueText("Pass 2 done.");

        string output = await Headless("/plan Add a guide\n/plan gaps 2\n");

        Assert.Equal(7, _chat.Requests.Count);
        Assert.StartsWith("Error: nothing was saved", HeadlessResultOf(_chat.Requests[3], "p2"));
        Assert.EndsWith(PlanText.MeasuredSentence([RalphText.FitNoDoneWhen(1)]), HeadlessResultOf(_chat.Requests[4], "p3"));
        Assert.Equal(PlanText.GapPassMessage(HeadlessPlanPath, 2, 2, PlanReviewer.Model, measured: [RalphText.FitNoDoneWhen(1)]), HeadlessLastUser(_chat.Requests[5]));
        Assert.Contains("[notice] " + PlanText.GapStopNotice(PlanGapStop.Limit, 2, 3), output);
    }
}
