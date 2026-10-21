using NeonSidekick.Plans;

namespace NeonSidekick.Tests;

/// <summary>
/// Whether a plan suits a Ralph loop (2026-10-18, <see cref="PlanFit"/>): one long step, unticked steps with no <c>Done when:</c>, a body past the
/// inline limit, and no check with the reviewer off, in that order; a well-shaped plan and a short one-step plan have none.
/// </summary>
public sealed class PlanFitTests
{
    private const int Limit = RalphText.InlinePlanChars;

    private static string Plan(string steps, int pad = 0) =>
        "# Hello\n\n## Goal\n\nSay hello." + (pad > 0 ? "\n\n## Context\n\n" + new string('c', pad) : "") + "\n\n## Steps\n\n" + steps + "\n\n## Verification\n\n- it runs";

    private const string Good =
        "- [ ] Write hello.py\n  Done when: python hello.py prints hello\n" +
        "- [ ] Add colors\n  Done when: python hello.py --color-depth prints a depth";

    [Fact]
    public void AWellShapedPlan_HasNoProblems()
    {
        Assert.Empty(PlanFit.Problems(Plan(Good), new PlanChecks("python hello.py", null), reviewed: false, Limit));
        Assert.Empty(PlanFit.Problems(Plan(Good), null, reviewed: true, Limit));
        Assert.Empty(PlanFit.Shape(Plan(Good), Limit));
    }

    [Fact]
    public void OneStep_IsAProblem_OnlyPastTheOneStepLength()
    {
        string one = "- [ ] Build the whole thing\n  Done when: it works";
        Assert.Empty(PlanFit.Shape(Plan(one), Limit));   // a small job ("rename a variable") asks nothing

        string big = Plan(one, pad: PlanFit.OneStepChars);
        Assert.True(big.Length > PlanFit.OneStepChars);
        Assert.Equal([RalphText.FitOneStep], PlanFit.Shape(big, Limit));
    }

    [Fact]
    public void OneStep_CountsEveryLeaf_TickedOrNot()
    {
        // Two leaves, one ticked: not a one-step plan, however long.
        string steps = "- [x] Write hello.py\n  Done when: it runs\n- [ ] Add colors\n  Done when: --color-depth prints";
        Assert.Empty(PlanFit.Shape(Plan(steps, pad: PlanFit.OneStepChars), Limit));
    }

    [Fact]
    public void StepsWithNoDoneWhen_AreCounted_UntickedOnly()
    {
        string one = "- [ ] Write hello.py\n  Done when: it runs\n- [ ] Add colors";
        Assert.Equal([RalphText.FitNoDoneWhen(1)], PlanFit.Shape(Plan(one), Limit));

        string two = "- [ ] Write hello.py\n- [ ] Add colors\n- [ ] Ship it\n  Done when: it is shipped";
        Assert.Equal([RalphText.FitNoDoneWhen(2)], PlanFit.Shape(Plan(two), Limit));

        // A ticked step never runs again, so its missing line costs nothing.
        string ticked = "- [x] Write hello.py\n- [ ] Add colors\n  Done when: it is colored";
        Assert.Empty(PlanFit.Shape(Plan(ticked), Limit));
    }

    [Fact]
    public void ASplitParent_CountsItsChildren_NotItself()
    {
        // The parent has no Done when line of its own, but it is never a step the loop works.
        string split = "- [ ] Build it\n  - [ ] Write hello.py\n    Done when: it runs\n  - [ ] Add colors\n    Done when: it is colored";
        Assert.Empty(PlanFit.Shape(Plan(split), Limit));

        string missing = "- [ ] Build it\n  Done when: all of it\n  - [ ] Write hello.py\n  - [ ] Add colors\n    Done when: it is colored";
        Assert.Equal([RalphText.FitNoDoneWhen(1)], PlanFit.Shape(Plan(missing), Limit));
    }

    [Fact]
    public void ALongBody_IsMeasuredAgainstTheLimitGiven()
    {
        string plan = Plan(Good, pad: 3000);
        Assert.Empty(PlanFit.Shape(plan, Limit));
        Assert.Equal([RalphText.FitTooLong(plan.Length, 2000)], PlanFit.Shape(plan, 2000));
        Assert.Empty(PlanFit.Shape(plan, plan.Length));   // exactly at the limit is sent whole
    }

    [Fact]
    public void NoCheck_IsAProblem_OnlyWithTheReviewerOff()
    {
        Assert.Equal([RalphText.FitNoCheck], PlanFit.Problems(Plan(Good), null, reviewed: false, Limit));
        Assert.Equal([RalphText.FitNoCheck], PlanFit.Problems(Plan(Good), new PlanChecks(" ", ""), reviewed: false, Limit));
        Assert.Empty(PlanFit.Problems(Plan(Good), new PlanChecks(null, "pytest -q"), reviewed: false, Limit));
        Assert.Empty(PlanFit.Problems(Plan(Good), null, reviewed: true, Limit));
    }

    [Fact]
    public void TheOrder_IsPinned()
    {
        // The user's sample's shape: one step, no Done when, far past the limit, no check, the reviewer off.
        string plan = Plan("- [ ] Build the fancy terminal hello world\n  1. Write the script\n  2. Add the colors", pad: 30_000);
        Assert.Equal(
            [RalphText.FitOneStep, RalphText.FitNoDoneWhen(1), RalphText.FitTooLong(plan.Length, Limit), RalphText.FitNoCheck],
            PlanFit.Problems(plan, null, reviewed: false, Limit));
        Assert.Equal(
            [RalphText.FitOneStep, RalphText.FitNoDoneWhen(1), RalphText.FitTooLong(plan.Length, Limit)],
            PlanFit.Shape(plan, Limit));
    }

    [Fact]
    public void TheOrder_IsPinned_WithNoSteps()
    {
        // Zero steps leads, then the length, then the check (2026-10-21).
        string plan = "# Hello\n\n## Goal\n\n" + new string('c', 3000);
        Assert.Equal([RalphText.FitNoSteps, RalphText.FitTooLong(plan.Length, 2000), RalphText.FitNoCheck], PlanFit.Problems(plan, null, reviewed: false, 2000));
    }

    [Fact]
    public void NoSteps_IsAProblem()
    {
        Assert.Equal([RalphText.FitNoSteps], PlanFit.Shape("# Hello\n\n## Goal\n\nSay hello.", Limit));
        Assert.Equal(RalphText.FitNoSteps, PlanFit.StepsProblem("# Hello\n\n## Goal\n\nSay hello."));
        Assert.Equal(RalphText.NoStepsError, PlanFit.StepsRefusal("# Hello\n\n## Goal\n\nSay hello."));
    }

    /// <summary>The matrix-rain plan's shape (2026-10-21): steps as boxed headings, their Done when lines at column 0, a fence between.</summary>
    internal const string HeadingSteps =
        "### [ ] Step 1 — Rewrite `hello_world.py`\n\nReplace the script.\n\n```python\n- [ ] not a step\n```\n\n" +
        "Done when: running `python hello_world.py` shows the rain\n\n" +
        "### [ ] Step 2 — Check it\n\nDone when: `python hello_world.py --test` exits 0";

    [Fact]
    public void UnreadSteps_AreNamed()
    {
        Assert.Equal([RalphText.FitUnreadSteps(2)], PlanFit.Shape(Plan(HeadingSteps), Limit));
        Assert.Equal(RalphText.NoStepsUnreadError(2), PlanFit.StepsRefusal(Plan(HeadingSteps)));
    }

    [Fact]
    public void MixedSteps_NameTheUnread()
    {
        string mixed = "### [ ] Step 1 — Build it\n\n- [ ] Write hello.py\n  Done when: it runs";
        Assert.Equal([RalphText.FitUnreadSteps(1)], PlanFit.Shape(Plan(mixed), Limit));
        Assert.Null(PlanFit.StepsRefusal(Plan(mixed)));   // a step runs: only zero steps is refused
    }

    [Fact]
    public void AnUnindentedDoneWhen_IsMissing()
    {
        Assert.Equal([RalphText.FitNoDoneWhen(1)], PlanFit.Shape(Plan("- [ ] Write hello.py\nDone when: it runs"), Limit));
    }

    // ── Steps only a person can check (2026-10-21, the plan at ~/.claude/plans/i-put-you-in-snoopy-perlis.md) ──

    [Fact]
    public void ByHandSteps_CountsUntickedLeaves_AndIsNoProblem()
    {
        string steps = Good + "\n- [ ] Animate it\n  Done when: By hand: the terminal shows it\n- [x] Show it\n  Done when: By hand: it shows\n" +
            "- [ ] Parent\n  Done when: every part stands\n  - [ ] Part\n    Done when: **By hand:** the part shows\n";

        Assert.Equal(2, PlanFit.ByHandSteps(Plan(steps)));
        Assert.Empty(PlanFit.Shape(Plan(steps), Limit));   // a by-hand leaf is legitimate: no gap pass "fixes" it, no fit question
    }

    [Fact]
    public void AByHandParent_IsAProblem_AfterTheNoDoneWhenItem()
    {
        string steps = "- [ ] Parent\n  Done when: By hand: the whole thing works\n  - [ ] Part one\n    Done when: `a` exists\n  - [ ] Part two\n" +
            "- [ ] Other\n  Done when: `b` exists";

        Assert.Equal([RalphText.FitNoDoneWhen(1), RalphText.FitByHandParent(1)], PlanFit.Shape(Plan(steps), Limit));
        Assert.Equal(0, PlanFit.ByHandSteps(Plan(steps)));
        Assert.Equal(1, PlanFit.ByHandParents(Plan(steps)));
        Assert.Equal("1 step with sub-steps has a By hand: check", RalphText.FitByHandParent(1));
        Assert.Equal("2 steps with sub-steps have a By hand: check", RalphText.FitByHandParent(2));
    }
}
