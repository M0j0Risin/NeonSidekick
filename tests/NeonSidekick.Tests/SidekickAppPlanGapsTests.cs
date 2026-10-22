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

    /// <summary>A pass's <c>present_plan</c>; with no gaps, a <c>checked</c> line too (2026-10-17).</summary>
    private static ChatResponseUpdate HeadlessPresentGaps(string id, params string[] gaps) => HeadlessPresentGapsOver(id, "# Guide\n\n- [ ] Write it\n  Done when: guide.md exists\n" + string.Concat(gaps.Select(g => "- [ ] " + g + "\n  Done when: " + g + " is done\n")), gaps);

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

    [Fact]
    public async Task Headless_PlanGaps_RunsPassesWithNoInput_UntilNoneAreLeft()
    {
        ServerOn1234("llama");
        _chat.Enqueue(HeadlessPresentPlan());
        _chat.EnqueueText("Saved.");
        _chat.Enqueue(HeadlessPresentGaps("p2", "Test it"));
        _chat.EnqueueText("Pass 1 done.");
        _chat.Enqueue(HeadlessPresentGaps("p3", []));
        _chat.EnqueueText("None found.");
        _chat.Enqueue(HeadlessPresentGapsOver("p4", "# Guide\n\n- [ ] Write it\n  Done when: guide.md exists\n- [ ] Test it\n  Done when: Test it is done\n"));
        _chat.EnqueueText("Nothing left.");

        string output = await Headless("/plan Add a guide\n/plan gaps\n/plan open\n");

        Assert.Equal(8, _chat.Requests.Count);
        Assert.Equal(PlanText.GapPassMessage(HeadlessPlanPath, 1, 5, PlanReviewer.Model), HeadlessLastUser(_chat.Requests[2]));
        Assert.Equal(PlanText.GapPassMessage(HeadlessPlanPath, 2, 5, PlanReviewer.Model), HeadlessLastUser(_chat.Requests[4]));
        Assert.Contains("A gap review is running (pass 2 of 5)", _chat.Requests[4][0].Text);
        Assert.Equal(PlanText.GapPassResult(HeadlessPlanPath, 2, 1, bodyChars: PlanGapLoopTests.BodyChars("# Guide\n\n- [ ] Write it\n  Done when: guide.md exists\n- [ ] Test it\n  Done when: Test it is done\n")), _chat.Requests[3].SelectMany(m => m.Contents.OfType<FunctionResultContent>()).Single(r => r.CallId == "p2").Result);
        // Pass 2's markdown drops the test step, so it is a revision with no gaps: clean 1 of 2, and pass 3 confirms (2026-10-17).
        Assert.Equal(PlanText.CleanPassResult(HeadlessPlanPath, 3, 1, 2, bodyChars: PlanGapLoopTests.BodyChars("# Guide\n\n- [ ] Write it\n  Done when: guide.md exists\n")), _chat.Requests[5].SelectMany(m => m.Contents.OfType<FunctionResultContent>()).Single(r => r.CallId == "p3").Result);
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

        string output = await Headless("/plan Add a guide\n/plan gaps advisor\n/plan gaps nonsense\n");

        Assert.Contains("[notice] " + PlanText.GapsConfirmFallbackNotice, output);
        Assert.Contains("[notice] " + PlanText.GapsHeadlessCapNotice(AppSettingsData.MaxPlanGapPasses), output);
        Assert.Equal(PlanText.GapPassMessage(HeadlessPlanPath, 1, AppSettingsData.MaxPlanGapPasses, PlanReviewer.Model), HeadlessLastUser(_chat.Requests[2]));
        Assert.Contains("[notice] " + PlanText.GapStopNotice(PlanGapStop.NoRevision, 1, 0), output);
        Assert.Contains("[error] " + PlanText.UnknownReviewerError("nonsense"), output);
        Assert.Equal(3, _chat.Requests.Count);
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

        string output = await Headless("/plan Add a guide\n/plan gaps model 1\n/plan gaps 0\n/plan gaps 101\n");

        Assert.Equal(5, _chat.Requests.Count);
        Assert.Equal(PlanText.GapPassMessage(HeadlessPlanPath, 1, 1, PlanReviewer.Model), HeadlessLastUser(_chat.Requests[2]));
        Assert.Contains("[notice] " + PlanText.GapStopNotice(PlanGapStop.Limit, 1, 1) + " — /plan approve [--fresh | --ralph] starts it", output);
        Assert.Contains("[notice] " + PlanText.GapsHeadlessCapNotice(AppSettingsData.MaxPlanGapPasses, fromCommand: true), output);
        Assert.DoesNotContain(PlanText.GapsHeadlessCapNotice(AppSettingsData.MaxPlanGapPasses), output);
        Assert.Equal(PlanText.GapPassMessage(HeadlessPlanPath, 1, AppSettingsData.MaxPlanGapPasses, PlanReviewer.Model), HeadlessLastUser(_chat.Requests[4]));
        Assert.Contains("[error] " + PlanText.GapPassesRangeError("101"), output);
    }
}
