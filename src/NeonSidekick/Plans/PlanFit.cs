namespace NeonSidekick.Plans;

/// <summary>
/// Whether a plan suits a Ralph loop, measured (2026-10-18, the plan at <c>~/.claude/plans/snuggly-finding-comet.md</c>; the user's sample plan
/// came out of 46 gap passes as one checkbox of ~170,000 characters with no <c>verify:</c>, which a loop's three passes a step never finished):
/// the problems in plain words (<see cref="RalphText"/>'s, in a fixed order), read by the gap pass's result (<see cref="Shape"/>), the approval
/// pane's caption and the loop's start (the question on the screen, a notice headless). The <c>body</c> everywhere is
/// <see cref="PlanDocument.Body"/>'s, the header stripped: what a pass is sent and what <see cref="PlanSteps.Parse"/> reads. Zero steps is not a
/// problem here: the loop refuses it already (<see cref="RalphText.NoStepsError"/>). Pure.
/// </summary>
public static class PlanFit
{
    /// <summary>
    /// The body past which a one-step plan is a problem: a short one-step plan is a small job ("rename a variable") and asks nothing. A judgment
    /// call: a plan just over it with one honest step is asked once, and the user says yes.
    /// </summary>
    public const int OneStepChars = 6000;

    /// <summary>
    /// The problems of <paramref name="body"/> for a loop, in order: one step over <see cref="OneStepChars"/> (every leaf counted, ticked or not:
    /// it is the plan's shape), unticked steps with no <c>Done when:</c> (a step already done never runs again, so its missing line costs nothing),
    /// a body over <paramref name="inlineLimit"/> (<see cref="RalphText.InlineLimit"/>), and no check at all with <paramref name="reviewed"/> false
    /// (with a reviewer each step is still judged, and many projects have no command to name, so a plan without one is not a problem there).
    /// </summary>
    public static IReadOnlyList<string> Problems(string body, PlanChecks? checks, bool reviewed, int inlineLimit)
    {
        ArgumentNullException.ThrowIfNull(body);
        var leaves = PlanSteps.Leaves(PlanSteps.Parse(body)).ToList();
        var problems = new List<string>(4);
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
}
