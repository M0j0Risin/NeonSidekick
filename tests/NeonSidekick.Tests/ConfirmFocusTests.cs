using NeonSidekick.Plans;

namespace NeonSidekick.Tests;

/// <summary>
/// Confirm focus by change (2026-11-18, Phase 3 item 3, focus selection): the pick (<see cref="ConfirmFocusPick"/>), what a plan revision touched
/// (<see cref="PlanFocusAreas"/>, <see cref="PlanDocument.SectionLines"/>, reworded steps), the loop's <see cref="PlanGapLoop.Focus"/> with the
/// setting off (the rotation, unchanged) and on, the directive's focus and the focus log line.
/// </summary>
public class ConfirmFocusTests
{
    private const string Plan = "# Guide\n\n## Context\n- The guide lives in docs/guide.md.\n- The link checker is lychee.\n\n"
        + "## Steps\n- [ ] Write the guide\n  Done when: guide.md exists\n- [ ] Test the guide\n  Done when: the link check passes\n"
        + "- [ ] Ship the guide\n  Done when: the release has it\n\n"
        + "## Risks\n- A dead link slips through.\n\n## Verification\n- Run the link check.\n\n## References\n- Pasted: notes.md\n";

    private static string Swap(string from, string to) => Plan.Replace(from, to, StringComparison.Ordinal);

    private static int[] Weights(string after) => [.. PlanFocusAreas.Weigh(Plan, after)];

    // ── The pick ────────────────────────────────────────────────────────────

    [Fact]
    public void Pick_TakesTheHeaviest_OfTheLeastUsed_TiesByOrder()
    {
        Assert.Equal(3, ConfirmFocusPick.Pick([1, 0, 2, 5, 0], [0, 0, 0, 0, 0]));   // the heaviest
        Assert.Equal(2, ConfirmFocusPick.Pick([1, 0, 2, 5, 0], [0, 0, 0, 1, 0]));   // assumptions used: the heaviest left
        Assert.Equal(0, ConfirmFocusPick.Pick([2, 0, 2, 0, 0], [0, 0, 0, 0, 0]));   // a tie goes by order
        Assert.Equal(3, ConfirmFocusPick.Pick([1, 0, 2, 5, 0], [1, 0, 1, 1, 0]));   // all weighed used once: the heaviest again
    }

    [Fact]
    public void Pick_WithNothingWeighed_TakesTheLeastUsed()
    {
        Assert.Equal(0, ConfirmFocusPick.Pick([0, 0, 0], [0, 0, 0]));
        Assert.Equal(1, ConfirmFocusPick.Pick([], [1, 0, 0]));                      // a shorter list reads as nothing weighed
        Assert.Equal(2, ConfirmFocusPick.Pick([0, 0], [1, 1, 0]));
        Assert.False(ConfirmFocusPick.AnyWeighed([0, 0]));
        Assert.True(ConfirmFocusPick.AnyWeighed([0, 1]));
        Assert.Throws<ArgumentException>(() => ConfirmFocusPick.Pick([1], []));
    }

    // ── What a revision touched ─────────────────────────────────────────────

    [Fact]
    public void Weigh_AMovedStep_IsOrderAndSize()
    {
        string moved = Swap("- [ ] Test the guide\n  Done when: the link check passes\n- [ ] Ship the guide\n  Done when: the release has it\n",
            "- [ ] Ship the guide\n  Done when: the release has it\n- [ ] Test the guide\n  Done when: the link check passes\n");

        int[] weights = Weights(moved);

        Assert.True(weights[PlanFocusAreas.Order] > 0);
        Assert.Equal(weights[PlanFocusAreas.Order], weights[PlanFocusAreas.Size]);
        Assert.Equal(0, weights[PlanFocusAreas.Assumptions]);                        // the step lines aren't counted again by section
    }

    [Fact]
    public void Weigh_AnAddedStep_IsOrderAndSize()
    {
        int[] weights = Weights(Swap("- [ ] Ship the guide\n", "- [ ] Announce the guide\n  Done when: the post is up\n- [ ] Ship the guide\n"));

        Assert.Equal([1, 0, 0, 0, 1], weights);
    }

    [Fact]
    public void Weigh_AChangedDoneWhen_IsVerification()
    {
        Assert.Equal([0, 1, 0, 0, 0], Weights(Swap("Done when: the link check passes", "Done when: `lychee docs` exits 0")));
    }

    [Fact]
    public void Weigh_ARisksEdit_IsRisks_AndAVerificationEdit_IsVerification()
    {
        Assert.Equal([0, 0, 2, 0, 0], Weights(Swap("- A dead link slips through.\n", "- A dead link slips through.\n- lychee may be missing.\n- The CI runner may be offline.\n")));
        Assert.Equal([0, 1, 0, 0, 0], Weights(Swap("- Run the link check.\n", "- Run the link check.\n- Open the guide.\n")));
    }

    [Fact]
    public void Weigh_AContextEdit_AndARewording_AreAssumptions()
    {
        Assert.Equal([0, 0, 0, 2, 0], Weights(Swap("- The link checker is lychee.", "- The link checker is markdown-link-check.")));
        Assert.Equal([0, 0, 0, 1, 0], Weights(Swap("- [ ] Test the guide", "- [ ] Test the guide with lychee")));
    }

    [Fact]
    public void Weigh_LeavesOutReferences_MovedLines_AndAnUnreadSide()
    {
        Assert.Equal([0, 0, 0, 0, 0], Weights(Swap("- Pasted: notes.md\n", "- Pasted: notes.md\n- Pasted: more.md\n")));
        Assert.Equal([0, 0, 0, 0, 0], Weights(Swap("- The guide lives in docs/guide.md.\n- The link checker is lychee.\n",
            "- The link checker is lychee.\n- The guide lives in docs/guide.md.\n")));                          // lines that only moved
        Assert.Equal([0, 0, 0, 0, 0], PlanFocusAreas.Weigh(null, Plan));
        Assert.Equal([0, 0, 0, 0, 0], PlanFocusAreas.Weigh(Plan, null));
        Assert.Equal([0, 0, 0, 0, 0], PlanFocusAreas.Weigh(Plan, Plan.Replace("\n", "\r\n", StringComparison.Ordinal)));
    }

    [Fact]
    public void AreaOf_ReadsTheHeading()
    {
        Assert.Equal(PlanFocusAreas.Risks, PlanFocusAreas.AreaOf("Risks and open questions"));
        Assert.Equal(PlanFocusAreas.Verification, PlanFocusAreas.AreaOf("How to verify"));
        Assert.Equal(PlanFocusAreas.Assumptions, PlanFocusAreas.AreaOf("Files and areas"));
    }

    [Fact]
    public void ARewordedStep_IsReported_ButIsNoChange()
    {
        var report = PlanStepChanges.Compare(Plan, Swap("Test the guide", "Test the whole guide"));

        Assert.False(report.Any);
        Assert.Equal(["Test the whole guide"], report.RewordedSteps);
        Assert.Empty(new PlanStepChangeReport([], [], [], []).RewordedSteps);
    }

    [Fact]
    public void SectionLines_KeepsEachSectionsLines_OutsideFences()
    {
        const string body = "# T\nbefore\n## Goal:\nline 1\r\n```\n## not a heading\n```\n### Sub\n## References\n- x\n## Risks\n- y\n";

        var sections = PlanDocument.SectionLines(body);

        Assert.Equal(["Goal", "Risks"], sections.Select(s => s.Name));
        Assert.Equal(["line 1", "### Sub"], sections[0].Lines);
        Assert.Equal(["- y", ""], sections[1].Lines);
        Assert.Empty(PlanDocument.SectionLines("no headings\nat all\n"));
    }

    // ── The loop ────────────────────────────────────────────────────────────

    /// <summary>A pass that finds a gap on <paramref name="before"/> and revises it to <paramref name="after"/>, then a clean pass: the next begins confirming.</summary>
    private static PlanGapLoop GapThenClean(string? before, string? after, bool byChange, int cleanPasses = 3)
    {
        var loop = byChange
            ? new PlanSession().StartClosing(PlanReviewer.Model, 0, cleanPasses, focusByChange: true)
            : new PlanSession().StartClosing(PlanReviewer.Model, 0, cleanPasses);
        loop.BeginPass(before);
        loop.Record(["[high] a gap"]);
        Assert.Null(loop.Next(true, false, after));
        loop.BeginPass(after);
        Assert.Equal(-1, loop.Focus);
        loop.MarkClean();
        Assert.Null(loop.Next(true, false, after));
        loop.BeginPass(after);
        return loop;
    }

    [Fact]
    public void Loop_Off_TheFocusIsTheRotation()
    {
        var loop = GapThenClean(Plan, Swap("Done when: the link check passes", "Done when: lychee exits 0"), byChange: false);

        Assert.False(loop.PicksByChange);
        Assert.Equal(1, loop.Confirmation);
        Assert.Equal(0, loop.Focus);                                                 // order, as before
        Assert.Empty(loop.FocusWeights);
        Assert.Equal("in turn", PlanText.FocusHow(loop, PlanText.ConfirmFocusLabels));
    }

    [Fact]
    public void Loop_On_TwoConfirmingPassesOnOneChange_TakeTheTwoHeaviestAreas_ThenTheLeastUsed()
    {
        string after = Swap("Done when: the link check passes", "Done when: lychee exits 0")
            .Replace("- A dead link slips through.\n", "- A dead link slips through.\n- lychee may be missing.\n- The CI runner may be offline.\n", StringComparison.Ordinal);
        var loop = GapThenClean(Plan, after, byChange: true, cleanPasses: 4);

        Assert.True(loop.PicksByChange);
        Assert.Equal(Plan, loop.ChangeBefore);
        Assert.Equal(after, loop.ChangeAfter);
        Assert.Equal(PlanFocusAreas.Risks, loop.Focus);                              // risks 2 before verification 1
        Assert.Equal("by change (risks 2, verification 1)", PlanText.FocusHow(loop, PlanText.ConfirmFocusLabels));

        loop.MarkClean();
        Assert.Null(loop.Next(true, false, after));
        loop.BeginPass(after);
        Assert.Equal(PlanFocusAreas.Verification, loop.Focus);                       // the next area the change touched

        loop.MarkClean();
        Assert.Null(loop.Next(true, false, after));
        loop.BeginPass(after);
        Assert.Equal(PlanFocusAreas.Risks, loop.Focus);                              // both used once: the heaviest again

        loop.MarkConverged();
        Assert.Equal(PlanGapStop.Converged, loop.Next(true, false, after));
        Assert.Equal([-1, -1, PlanFocusAreas.Risks, PlanFocusAreas.Verification, PlanFocusAreas.Risks], loop.History.Select(p => p.Focus));
    }

    [Fact]
    public void Loop_On_AnUnreadPlan_TakesTheLeastUsed()
    {
        // Headless's first pass begins on null when the plan file can't be read; a later unread body stops the review (Next's NoRevision).
        var loop = GapThenClean(null, Plan, byChange: true);

        Assert.Equal(0, loop.Focus);
        Assert.Equal("least used", PlanText.FocusHow(loop, PlanText.ConfirmFocusLabels));
    }

    // ── The directive and the log line ──────────────────────────────────────

    [Fact]
    public void TheDirective_NamesThePickedFocus_ElseTheRotation()
    {
        var picked = new PlanGapState(3, 0, PlanReviewer.Model, CleanSoFar: 1, CleanPasses: 2, Confirmation: 1, Focus: PlanFocusAreas.Verification);
        var rotated = picked with { Focus = -1 };

        Assert.Contains(PlanText.ConfirmFocuses[PlanFocusAreas.Verification], PlanText.GapReviewLines(picked, advisorsOffered: false, web: false), StringComparison.Ordinal);
        Assert.Contains(PlanText.ConfirmFocuses[0], PlanText.GapReviewLines(rotated, advisorsOffered: false, web: false), StringComparison.Ordinal);
    }

    [Fact]
    public void TheFocusLogLines_NameThePass_TheFocus_AndHow()
    {
        Assert.Equal("plan gaps focus: pass 3: risks, by change (risks 2)", PlanText.FocusLogLine(3, "risks", "by change (risks 2)"));
        Assert.StartsWith(PlanText.FocusLogPrefix, PlanText.FocusLogLine(1, "order", "in turn"), StringComparison.Ordinal);
        Assert.Equal("code audit focus: pass 4: maintainability, least used", Audits.AuditText.FocusLogLine(4, "maintainability", "least used"));
    }
}
