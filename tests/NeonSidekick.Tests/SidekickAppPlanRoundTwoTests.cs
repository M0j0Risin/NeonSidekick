using Microsoft.Extensions.AI;
using NeonSidekick.Files;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Plans;

namespace NeonSidekick.Tests;

/// <summary>Plan mode's second round headless (2026-09-26): open, the hint and save, and a plan marked done.</summary>
public partial class SidekickAppTests
{
    private string HeadlessPlansFolder => Path.Combine(_settings.ProfileDirectory, WorkingDirectory.DefaultFolderName, ".neon", "plans");

    [Fact]
    public async Task Headless_Plan_Open_ReopensAndAsks_AndAloneLists()
    {
        ServerOn1234("llama");
        Directory.CreateDirectory(HeadlessPlansFolder);
        var at = new DateTimeOffset(2026, 9, 20, 9, 0, 0, TimeSpan.Zero);
        File.WriteAllText(Path.Combine(HeadlessPlansFolder, "add-guide.md"), PlanDocument.Render(new PlanHeader(PlanStatus.Incomplete, 2, at, at, "Add a guide"), "Guide", "# Guide\n- [x] One\n- [ ] Two"));
        _chat.EnqueueText("What should change?");

        string output = await Headless("/plan open\n/plan open nope\n/plan open add-guide\n");

        Assert.Contains("[notice] add-guide — Guide (incomplete · 1/2", output);
        Assert.Contains("Neon: [error] " + PlanText.NoSuchPlanError("nope"), output);
        Assert.Contains("[notice] " + PlanText.OpenedNotice(".neon/plans/add-guide.md", PlanStatus.Incomplete, 1, 2), output);
        Assert.Equal(PlanText.OpenMessage(".neon/plans/add-guide.md", 1, 2), Assert.Single(_chat.Requests).Last(m => m.Role == ChatRole.User).Text);
        Assert.Contains(PresentPlanTool.ToolName, Offered(_chat.Options[0]));
    }

    [Fact]
    public async Task Headless_Plan_AnUnpresentedPlan_IsHinted_Saved_ApprovedAndMarkedDone()
    {
        ServerOn1234("llama");
        _chat.EnqueueText("# Guide\n\n- [ ] Write it\n- [ ] Link it");
        _chat.EnqueueText("Done.");
        string file = Path.Combine(HeadlessPlansFolder, "guide.md");
        _chat.BeforeUpdateOf = (request, _, _) =>
        {
            if (request == 1 && File.Exists(file))
            {
                File.WriteAllText(file, File.ReadAllText(file).Replace("- [ ]", "- [x]", StringComparison.Ordinal));
            }

            return Task.CompletedTask;
        };

        string output = await Headless("/plan Add a guide\n/plan save\n/plan approve\n");

        Assert.Contains("[notice] " + PlanText.UnpresentedHint, output);
        Assert.Contains("[notice] " + PlanText.SavedNotice(".neon/plans/guide.md", 1), output);
        Assert.Equal(PlanText.ExecuteMessage(".neon/plans/guide.md"), _chat.Requests[1].Last(m => m.Role == ChatRole.User).Text);
        Assert.Contains("[notice] " + PlanText.DoneNotice(".neon/plans/guide.md"), output);
        Assert.Equal((PlanStatus.Done, 2, 2), (PlanDocument.TryParse(File.ReadAllText(file))!.Status, PlanDocument.TryParse(File.ReadAllText(file))!.StepsDone, PlanDocument.TryParse(File.ReadAllText(file))!.StepsTotal));
    }

    [Fact]
    public async Task Headless_Plan_SaveWithNothingToSave_Errors()
    {
        ServerOn1234("llama");
        _chat.EnqueueText("What should it cover?");

        string output = await Headless("/plan Add a guide\n/plan save\n");

        Assert.Contains("Neon: [error] " + PlanText.NothingToSaveError, output);
        Assert.DoesNotContain(PlanText.UnpresentedHint, output);
    }
}
