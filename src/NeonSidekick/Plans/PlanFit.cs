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
    /// a body over <paramref name="inlineLimit"/> (<see cref="RalphText.InlineLimit"/>), and no check at all with <paramref name="reviewed"/> false
    /// (with a reviewer each step is still judged, and many projects have no command to name, so a plan without one is not a problem there).
    /// </summary>
    public static IReadOnlyList<string> Problems(string body, PlanChecks? checks, bool reviewed, int inlineLimit)
    {
        ArgumentNullException.ThrowIfNull(body);
        var leaves = PlanSteps.Leaves(PlanSteps.Parse(body)).ToList();
        var problems = new List<string>(5);
        if (StepsProblem(body) is { } steps)
        {
            problems.Add(steps);
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
    /// <see cref="Problems"/> without the no-check item: the plan's shape alone, for the gap pass's result (the directive already says to leave a
    /// check out rather than guess) and the approval pane's caption (its check line already says <c>unchecked</c> and the reviewer).
    /// </summary>
    public static IReadOnlyList<string> Shape(string body, int inlineLimit) => Problems(body, null, reviewed: true, inlineLimit);

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
        if (PlanSteps.UnreadBoxes(body) is int unread and > 0)
        {
            return RalphText.FitUnreadSteps(unread);
        }

        return PlanSteps.Leaves(PlanSteps.Parse(body)).Any() ? null : RalphText.FitNoSteps;
    }
}
