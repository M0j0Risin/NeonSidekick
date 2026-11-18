using NeonSidekick.Plans;

namespace NeonSidekick.Tests;

/// <summary>
/// The step-change check over two plan bodies (2026-11-18, phase 3 item 2: the mechanical minor check, measured before it is trusted): added,
/// removed and moved steps, a changed <c>Done when:</c>, and what is no change (a reworded step, an unchanged body, <c>## References</c>).
/// </summary>
public class PlanStepChangesTests
{
    private const string Plan = "# Guide\n\n## Steps\n- [ ] Write the guide\n  Done when: guide.md exists\n- [ ] Test the guide\n  Done when: the link check passes\n"
        + "- [ ] Ship the guide\n  Done when: the release has it\n";

    [Fact]
    public void AnUnchangedBody_ChangesNothing()
    {
        Assert.Same(PlanStepChangeReport.None, PlanStepChanges.Compare(Plan, Plan));
        Assert.False(PlanStepChanges.Compare(Plan, Plan.Replace("\n", "\r\n", StringComparison.Ordinal)).Any);
    }

    [Fact]
    public void AnAddedStep_IsAdded()
    {
        var report = PlanStepChanges.Compare(Plan, Plan + "- [ ] Announce the guide\n  Done when: the post is up\n");

        Assert.Equal(["Announce the guide"], report.Added);
        Assert.Empty(report.Removed);
        Assert.Empty(report.Moved);
        Assert.Empty(report.DoneWhenChanged);
        Assert.True(report.Any);
    }

    [Fact]
    public void ARemovedStep_IsRemoved()
    {
        var report = PlanStepChanges.Compare(Plan, Plan.Replace("- [ ] Ship the guide\n  Done when: the release has it\n", "", StringComparison.Ordinal));

        Assert.Equal(["Ship the guide"], report.Removed);
        Assert.Empty(report.Added);
    }

    [Fact]
    public void AMovedStep_IsMoved_NotAddedAndRemoved()
    {
        const string Moved = "# Guide\n\n## Steps\n- [ ] Ship the guide\n  Done when: the release has it\n- [ ] Write the guide\n  Done when: guide.md exists\n"
            + "- [ ] Test the guide\n  Done when: the link check passes\n";

        var report = PlanStepChanges.Compare(Plan, Moved);

        Assert.Equal(["Ship the guide"], report.Moved);
        Assert.Empty(report.Added);
        Assert.Empty(report.Removed);
    }

    [Fact]
    public void AChangedDoneWhen_IsCounted_ButNotItsWhitespace()
    {
        var changed = PlanStepChanges.Compare(Plan, Plan.Replace("the link check passes", "every link answers 200", StringComparison.Ordinal));
        var spaced = PlanStepChanges.Compare(Plan, Plan.Replace("the link check passes", "the  link   check passes ", StringComparison.Ordinal));

        Assert.Equal(["Test the guide"], changed.DoneWhenChanged);
        Assert.Empty(spaced.DoneWhenChanged);
        Assert.False(spaced.Any);
    }

    [Fact]
    public void ARewordedStep_ThatStaysPaired_IsNoChange()
    {
        var report = PlanStepChanges.Compare(Plan, Plan.Replace("Test the guide", "Test the whole guide", StringComparison.Ordinal));

        Assert.False(report.Any);
    }

    [Fact]
    public void TheReferencesSection_IsSetAside()
    {
        string withReferences = Plan + "\n## References\n- [ ] Read the style guide\n";

        Assert.False(PlanStepChanges.Compare(Plan, withReferences).Any);
    }

    [Fact]
    public void TheLogLine_NamesEachKind_AndTheChecks()
    {
        var report = new PlanStepChangeReport(["a"], [], ["b"], ["c", "d"]);

        Assert.Equal("plan gaps steps: pass 3's clean report changed 1 added, 0 removed, 1 moved, 2 Done when lines, verify",
            PlanText.StepChangesLogLine(3, report, verifyChanged: true, finalChanged: false));
        Assert.EndsWith(" Done when lines, verify, final", PlanText.StepChangesLogLine(3, report, true, true));
        Assert.StartsWith(PlanText.StepChangesLogPrefix + " ", PlanText.StepChangesLogLine(1, PlanStepChangeReport.None, false, false));
    }
}
