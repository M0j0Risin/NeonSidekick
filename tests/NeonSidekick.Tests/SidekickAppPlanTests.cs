using Microsoft.Extensions.AI;
using NeonSidekick.Files;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Plans;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>Plan mode headless (2026-09-26): no pane, so a presented plan is saved and <c>/plan approve</c> typed starts it.</summary>
public partial class SidekickAppTests
{
    private string HeadlessPlanFile => Path.Combine(_settings.ProfileDirectory, WorkingDirectory.DefaultFolderName, ".neon", "plans", "add-guide.md");

    private static ChatResponseUpdate HeadlessPresentPlan() =>
        FakeChatClient.Call("p1", PresentPlanTool.ToolName, new Dictionary<string, object?>
        {
            [PresentPlanTool.TitleArgument] = "Guide",
            [PresentPlanTool.NameArgument] = "add-guide",
            [PresentPlanTool.MarkdownArgument] = "# Guide\n\n- [ ] Write it\n",
        });

    private static List<string> Offered(ChatOptions? options) => options?.Tools?.Cast<AIFunction>().Select(t => t.Name).ToList() ?? [];

    [Fact]
    public async Task Headless_Plan_SavesThePlan_AndApproveTypedRunsIt()
    {
        ServerOn1234("llama");
        _chat.Enqueue(HeadlessPresentPlan());
        _chat.EnqueueText("Saved; approve it when ready.");
        _chat.EnqueueText("Done.");

        string output = await Headless("/plan Add a guide\n/plan show\n/plan approve\n");

        Assert.Equal(3, _chat.Requests.Count);
        Assert.Contains(PresentPlanTool.ToolName, Offered(_chat.Options[0]));
        Assert.DoesNotContain("write_file", Offered(_chat.Options[0]));
        Assert.Equal(PlanText.SavedResult(".neon/plans/add-guide.md"), _chat.Requests[1].SelectMany(m => m.Contents.OfType<FunctionResultContent>()).Single(r => r.CallId == "p1").Result);
        Assert.Contains("[notice] " + PlanText.EnteredNotice, output);
        Assert.Contains("[notice] " + PlanText.SavedNotice(".neon/plans/add-guide.md", 1), output);
        Assert.Contains("[notice] Plan: .neon/plans/add-guide.md (revision 1)", output);
        Assert.Contains("[notice] " + PlanText.ApprovedNotice(".neon/plans/add-guide.md", fresh: false), output);
        Assert.Equal(PlanText.ExecuteMessage(".neon/plans/add-guide.md"), _chat.Requests[2].Last(m => m.Role == ChatRole.User).Text);
        Assert.Contains("write_file", Offered(_chat.Options[2]));
        Assert.Equal(PlanStatus.Incomplete, PlanDocument.TryParse(File.ReadAllText(HeadlessPlanFile))!.Status);
        Assert.Contains("[notice] " + PlanText.IncompleteNotice(".neon/plans/add-guide.md", 0, 1), output);
    }

    [Fact]
    public async Task Headless_Plan_TheWordsThatCannotRun_AndCancel()
    {
        ServerOn1234("llama");
        _chat.EnqueueText("What should it cover?");

        string output = await Headless("/plan\n/plan cancel\n/plan Add a guide\n/plan approve\n/plan cancel\n");

        Assert.Contains("Neon: [error] " + PlanText.UsageError, output);
        Assert.Contains("Neon: [error] " + PlanText.NotPlanningError, output);
        Assert.Contains("Neon: [error] " + PlanText.NothingPresentedError, output);
        Assert.Contains("[notice] " + PlanText.CancelledNotice(null), output);
        Assert.Single(_chat.Requests);
    }

    [Fact]
    public async Task Headless_Plan_NewLeavesIt()
    {
        ServerOn1234("llama");
        _chat.EnqueueText("What should it cover?").EnqueueText("Hi.");

        string output = await Headless("/plan Add a guide\n/new\nhello\n");

        Assert.Contains("[notice] " + PlanText.LeftOnResetNotice(null), output);
        Assert.Contains("write_file", Offered(_chat.Options[1]));
        Assert.DoesNotContain(PresentPlanTool.ToolName, Offered(_chat.Options[1]));
    }
}
