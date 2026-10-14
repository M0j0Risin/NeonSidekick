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

    private static ChatResponseUpdate HeadlessPresentGaps(string id, params string[] gaps) =>
        FakeChatClient.Call(id, PresentPlanTool.ToolName, new Dictionary<string, object?>
        {
            [PresentPlanTool.TitleArgument] = "Guide",
            [PresentPlanTool.MarkdownArgument] = "# Guide\n\n- [ ] Write it\n" + string.Concat(gaps.Select(g => "- [ ] " + g + "\n")),
            [PresentPlanTool.GapsArgument] = gaps,
        });

    private static string? HeadlessLastUser(IReadOnlyList<ChatMessage> request) => request.Last(m => m.Role == ChatRole.User).Text;

    [Fact]
    public async Task Headless_PlanGaps_RunsPassesWithNoInput_UntilNoneAreLeft()
    {
        ServerOn1234("llama");
        _chat.Enqueue(HeadlessPresentPlan());
        _chat.EnqueueText("Saved.");
        _chat.Enqueue(HeadlessPresentGaps("p2", "Test it"));
        _chat.EnqueueText("Pass 1 done.");
        _chat.Enqueue(HeadlessPresentGaps("p3"));
        _chat.EnqueueText("Nothing left.");

        string output = await Headless("/plan Add a guide\n/plan gaps\n");

        Assert.Equal(6, _chat.Requests.Count);
        Assert.Equal(PlanText.GapPassMessage(HeadlessPlanPath, 1, 5, PlanReviewer.Model), HeadlessLastUser(_chat.Requests[2]));
        Assert.Equal(PlanText.GapPassMessage(HeadlessPlanPath, 2, 5, PlanReviewer.Model), HeadlessLastUser(_chat.Requests[4]));
        Assert.Contains("A gap review is running (pass 2 of 5)", _chat.Requests[4][0].Text);
        Assert.Equal(PlanText.GapPassResult(HeadlessPlanPath, 2, 1), _chat.Requests[3].SelectMany(m => m.Contents.OfType<FunctionResultContent>()).Single(r => r.CallId == "p2").Result);
        Assert.Equal(PlanText.SavedResult(HeadlessPlanPath), _chat.Requests[5].SelectMany(m => m.Contents.OfType<FunctionResultContent>()).Single(r => r.CallId == "p3").Result);
        Assert.Contains("You: " + PlanText.GapPassMessage(HeadlessPlanPath, 2, 5, PlanReviewer.Model), output);
        Assert.Contains("[notice] " + PlanText.PassNoticeLines(1, 5, 2, ["Test it"])[0], output);
        Assert.Contains("[notice] " + PlanText.ConvergedNotice(HeadlessPlanPath, 2, 1) + " — /plan approve [--fresh] starts it", output);
        // A pass's revision is said by its own notice, not the saved line every pass.
        Assert.DoesNotContain("[notice] " + PlanText.SavedNotice(HeadlessPlanPath, 2), output);
    }

    [Fact]
    public async Task Headless_PlanGaps_AnAdvisorReviewer_WithNoAdvisorOffered_IsTheModelOnEveryPass()
    {
        // 2026-10-14, the review: the first pass named the model, passes 2..N the advisor asked for.
        ServerOn1234("llama");
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
        Assert.Contains("[notice] " + PlanText.GapStopNotice(PlanGapStop.Limit, 1, 1) + " — /plan approve [--fresh] starts it", output);
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
}
