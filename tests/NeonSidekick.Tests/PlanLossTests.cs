using NeonSidekick.Plans;

namespace NeonSidekick.Tests;

/// <summary>The lost-content check over two plan bodies (2026-11-17, the gap-audit-reliability plan).</summary>
public class PlanLossTests
{
    private const string Plan =
        "# Guide\n\n## Goal\n\nA guide for new users.\n\n## Steps\n- [x] Write it\n  Done when: guide.md exists\n- [ ] Test it\n  Done when: the check passes\n" +
        "- [ ] Publish it\n  Done when: it is on the site\n\n## Risks\n\nNone known.\n";

    [Fact]
    public void TheSamePlan_LosesNothing()
    {
        Assert.False(PlanLoss.Compare(Plan, Plan).Any);
        Assert.False(PlanLoss.Compare(Plan, Plan.Replace("\n", "\r\n", StringComparison.Ordinal)).Any);
    }

    [Fact]
    public void AnUnfinishedStepRemoved_IsALoss_AFinishedOneIsNot()
    {
        var loss = PlanLoss.Compare(Plan, Plan.Replace("- [ ] Publish it\n  Done when: it is on the site\n", "", StringComparison.Ordinal));
        Assert.Equal(["Publish it"], loss.Steps);
        Assert.True(loss.Any);

        Assert.Empty(PlanLoss.Compare(Plan, Plan.Replace("- [x] Write it\n  Done when: guide.md exists\n", "", StringComparison.Ordinal)).Steps);
    }

    [Fact]
    public void ARewording_IsNoLoss_NorAMove()
    {
        Assert.False(PlanLoss.Compare(Plan, Plan.Replace("- [ ] Test it", "- [ ] Test the guide", StringComparison.Ordinal)).Any);
        string moved = Plan.Replace("- [ ] Test it\n  Done when: the check passes\n- [ ] Publish it\n  Done when: it is on the site\n",
            "- [ ] Publish it\n  Done when: it is on the site\n- [ ] Test it\n  Done when: the check passes\n", StringComparison.Ordinal);
        Assert.False(PlanLoss.Compare(Plan, moved).Any);
    }

    [Fact]
    public void ATickTakenOff_IsALoss()
    {
        Assert.Equal(["Write it"], PlanLoss.Compare(Plan, Plan.Replace("- [x] Write it", "- [ ] Write it", StringComparison.Ordinal)).Unticked);
    }

    [Fact]
    public void ASectionDropped_IsALoss_ButNotReferences()
    {
        Assert.Equal(["Risks"], PlanLoss.Compare(Plan, Plan.Replace("\n## Risks\n\nNone known.\n", "\n", StringComparison.Ordinal)).Sections);

        string withReferences = Plan + "\n## References\n\n- ref-1: .neon/plans/guide/ref-1.png\n";
        Assert.False(PlanLoss.Compare(withReferences, Plan).Any);
    }

    [Fact]
    public void ALongReferencesSection_LeftOut_IsNoShrink()
    {
        string withReferences = Plan + "\n## References\n\n" + string.Concat(Enumerable.Range(1, 40).Select(i => $"- ref-{i}: .neon/plans/guide/ref-{i}.png\n"));
        Assert.Null(PlanLoss.Compare(withReferences, Plan).ShrinkPercent);
    }

    [Fact]
    public void ACutPastTheLimit_IsALoss()
    {
        string padded = Plan.Replace("A guide for new users.", "A guide for new users. " + new string('x', 600), StringComparison.Ordinal);
        var loss = PlanLoss.Compare(padded, Plan);
        Assert.NotNull(loss.ShrinkPercent);
        Assert.True(loss.ShrinkPercent > 40);
        Assert.Empty(loss.Steps);
        Assert.Null(PlanLoss.Compare(Plan, Plan.Replace("A guide for new users.", "A guide.", StringComparison.Ordinal)).ShrinkPercent);
    }

    [Fact]
    public void FencedCode_IsNeitherAStepNorASection()
    {
        string fenced = Plan.Replace("None known.", "```\n## Not a section\n- [ ] not a step\n```", StringComparison.Ordinal);
        Assert.False(PlanLoss.Compare(fenced, fenced.Replace("## Not a section\n- [ ] not a step\n", "", StringComparison.Ordinal)).Any);
    }

    [Fact]
    public void TheWords_NameEachKind()
    {
        var loss = new PlanLossReport(["Publish it"], ["Write it"], ["Risks"], 45);
        Assert.Equal("Error: this pass found no gaps but removed the unfinished step \"Publish it\"; the tick of \"Write it\"; the section \"## Risks\"; 45% of its length. "
            + "List each in gaps if it was meant, or put it back, and call present_plan again.", PlanText.LossCleanResult(loss));
        Assert.Equal("This revision removed the unfinished step \"Publish it\"; the tick of \"Write it\"; the section \"## Risks\"; 45% of its length; if any was not meant to go, "
            + "put it back in the next pass.", PlanText.LossSentence(loss));
        Assert.EndsWith("and 2 more", PlanText.LossLogLine(2, new PlanLossReport(["a", "b", "c", "d", "e", "f", "g"], [], [], null), refused: true), StringComparison.Ordinal);
    }

    [Fact]
    public void PairMoved_PairsAStepMovedElsewhere()
    {
        var before = PlanSteps.Flat(PlanSteps.Parse("- [ ] a\n- [ ] b\n- [ ] c\n- [ ] d\n"));
        var after = PlanSteps.Flat(PlanSteps.Parse("- [ ] d\n- [ ] a\n- [ ] b\n- [ ] c\n"));
        var pairs = PlanSteps.Align(before, after);
        Assert.Equal(-1, pairs[0]);   // Align alone: a lone line at the top, the other side empty there
        PlanSteps.PairMoved(before, after, pairs);
        Assert.Equal(3, pairs[0]);
    }
}
