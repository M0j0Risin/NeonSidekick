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
    public void NoSteps_IsNotAProblemHere()
    {
        // The loop refuses it already (RalphText.NoStepsError).
        Assert.Empty(PlanFit.Shape("# Hello\n\n## Goal\n\nSay hello.", Limit));
    }
}
