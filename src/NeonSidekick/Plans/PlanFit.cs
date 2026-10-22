namespace NeonSidekick.Plans;

/// <summary>
/// Whether a plan suits a Ralph loop, measured (2026-10-18, the plan at <c>~/.claude/plans/snuggly-finding-comet.md</c>; the user's sample plan
/// came out of 46 gap passes as one checkbox of ~170,000 characters with no <c>verify:</c>, which a loop's three passes a step never finished):
/// the problems in plain words (<see cref="RalphText"/>'s, in a fixed order), read by the gap pass's result (<see cref="Shape"/>), the approval
/// pane's caption and the loop's start (the question on the screen, a notice headless). The <c>body</c> everywhere is
/// <see cref="PlanDocument.Body"/>'s, the header stripped: what a pass is sent and what <see cref="PlanSteps.Parse"/> reads. Zero steps and boxes
/// the steps' reader passes over lead the list since 2026-10-21 (the plan at
/// <c>~/.claude/plans/pasted-content-id-319b-i-thought-delegated-stardust.md</c>: a plan of <c>### [ ] Step 1</c> headings read as zero steps, and
/// with zero steps left out here no gap pass and no pane said so; the loop refused it only at the end, <see cref="StepsRefusal"/>). Pure.
/// </summary>
public static class PlanFit
{
    /// <summary>
    /// The body past which a one-step plan is a problem: a short one-step plan is a small job ("rename a variable") and asks nothing. A judgment
    /// call: a plan just over it with one honest step is asked once, and the user says yes.
    /// </summary>
    public const int OneStepChars = 6000;

    /// <summary>
    /// The problems of <paramref name="body"/> for a loop, in order: box lines no step is read from (<see cref="PlanSteps.UnreadBoxes"/>, a mixed
    /// plan's too: its <c>- [ ]</c> steps run and the rest are skipped silently), else no step at all; one step over <see cref="OneStepChars"/> (every leaf counted, ticked or not:
    /// it is the plan's shape), unticked steps with no <c>Done when:</c> (a step already done never runs again, so its missing line costs nothing),
    /// a step with sub-steps whose check is by hand (<see cref="ByHandParents"/>, 2026-10-21),
    /// a body over <paramref name="inlineLimit"/> (<see cref="RalphText.InlineLimit"/>), and no check at all with <paramref name="reviewed"/> false
    /// (with a reviewer each step is still judged, and many projects have no command to name, so a plan without one is not a problem there).
    /// </summary>
    public static IReadOnlyList<string> Problems(string body, PlanChecks? checks, bool reviewed, int inlineLimit)
    {
        ArgumentNullException.ThrowIfNull(body);
        return ProblemsOf(body, PlanSteps.Parse(body), checks, reviewed, inlineLimit);
    }

    /// <summary>
    /// <see cref="Problems"/> over <paramref name="steps"/>, <paramref name="body"/>'s already read (2026-10-21, a review: each helper parsed the
    /// body again, four to six readings of up to ~170,000 characters a check).
    /// </summary>
    private static List<string> ProblemsOf(string body, IReadOnlyList<PlanStep> steps, PlanChecks? checks, bool reviewed, int inlineLimit)
    {
        var leaves = PlanSteps.Leaves(steps).ToList();
        var problems = new List<string>(5);
        if (StepsProblemOf(body, leaves.Count > 0) is { } stepsProblem)
        {
            problems.Add(stepsProblem);
        }

        if (leaves.Count == 1 && body.Length > OneStepChars)
        {
            problems.Add(RalphText.FitOneStep);
        }

        int missing = leaves.Count(s => !s.Ticked && s.DoneWhen is null);
        if (missing > 0)
        {
            problems.Add(RalphText.FitNoDoneWhen(missing));
        }

        if (ByHandParentsOf(steps) is int parents and > 0)
        {
            problems.Add(RalphText.FitByHandParent(parents));
        }

        if (body.Length > inlineLimit)
        {
            problems.Add(RalphText.FitTooLong(body.Length, inlineLimit));
        }

        if (!reviewed && checks is null or { IsEmpty: true })
        {
            problems.Add(RalphText.FitNoCheck);
        }

        return problems;
    }

    /// <summary>
    /// The unticked leaves of <paramref name="body"/> only a person can check (<see cref="PlanStep.ByHand"/>, 2026-10-21, the plan at
    /// <c>~/.claude/plans/i-put-you-in-snoopy-perlis.md</c>): said on the approval pane and at a loop's start, never one of the <see cref="Problems"/> —
    /// a by-hand step is legitimate, so a gap pass must not try to fix it, nor may it raise the fit question alone.
    /// </summary>
    public static int ByHandSteps(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return ByHandStepsOf(PlanSteps.Parse(body));
    }

    private static int ByHandStepsOf(IReadOnlyList<PlanStep> steps) => PlanSteps.Leaves(steps).Count(s => !s.Ticked && s.ByHand);

    /// <summary>
    /// The unticked steps with sub-steps whose own <c>Done when:</c> is by hand (2026-10-21): a parent is never worked, the app ticks it once its
    /// children stand, so its check would be ticked with nobody checking it. A problem (<see cref="RalphText.FitByHandParent"/>).
    /// </summary>
    public static int ByHandParents(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return ByHandParentsOf(PlanSteps.Parse(body));
    }

    private static int ByHandParentsOf(IReadOnlyList<PlanStep> steps) => steps.Count(s => !s.IsLeaf && !s.Ticked && s.ByHand);

    /// <summary>
    /// <see cref="Problems"/> without the no-check item: the plan's shape alone, for the gap pass's result (the directive already says to leave a
    /// check out rather than guess) and the approval pane's caption (its check line already says <c>unchecked</c> and the reviewer).
    /// </summary>
    public static IReadOnlyList<string> Shape(string body, int inlineLimit) => Problems(body, null, reviewed: true, inlineLimit);

    /// <summary><see cref="Shape"/> and <see cref="ByHandSteps"/> from one reading of <paramref name="body"/>: the approval pane's check line and a loop's confirm.</summary>
    public static IReadOnlyList<string> Shape(string body, int inlineLimit, out int byHand)
    {
        ArgumentNullException.ThrowIfNull(body);
        var steps = PlanSteps.Parse(body);
        byHand = ByHandStepsOf(steps);
        return ProblemsOf(body, steps, null, reviewed: true, inlineLimit);
    }

    /// <summary>
    /// Why a loop cannot start on <paramref name="body"/>, or null when it has a step (2026-10-21): <see cref="RalphText.NoStepsUnreadError"/> when
    /// its boxes are ones the steps' reader passes over, else <see cref="RalphText.NoStepsError"/>. Read before any confirm on the screen and by
    /// headless, so nobody says yes to a question and is then refused.
    /// </summary>
    public static string? StepsRefusal(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (PlanSteps.Leaves(PlanSteps.Parse(body)).Any())
        {
            return null;
        }

        return PlanSteps.UnreadBoxes(body) is int unread and > 0 ? RalphText.NoStepsUnreadError(unread) : RalphText.NoStepsError;
    }

    /// <summary>
    /// Whether a clean gap pass on <paramref name="body"/> is refused (2026-10-21, the user's call): no step at all, or any box the steps' reader
    /// passes over (the mixed case too). The problem to name, <see cref="RalphText.FitUnreadSteps"/>'s or <see cref="RalphText.FitNoSteps"/>, or null.
    /// </summary>
    public static string? StepsProblem(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return StepsProblemOf(body, PlanSteps.Leaves(PlanSteps.Parse(body)).Any());
    }

    private static string? StepsProblemOf(string body, bool anyStep)
    {
        if (PlanSteps.UnreadBoxes(body) is int unread and > 0)
        {
            return RalphText.FitUnreadSteps(unread);
        }

        return anyStep ? null : RalphText.FitNoSteps;
    }
}
