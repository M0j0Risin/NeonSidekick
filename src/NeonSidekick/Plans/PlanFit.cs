namespace NeonSidekick.Plans;

/// <summary>
/// What <see cref="PlanFit.Read"/> found in one reading of a plan's body (2026-10-22, a review: each helper parsed the body again, about four readings a
/// clean gap pass): its steps, the problems of its shape — the ones a gap review must close before it converges (<see cref="Structural"/>) — and the
/// two it never must: <see cref="TooLong"/>, which depends on the model's window, and <see cref="NoCheck"/>, which the reviewer covers; with
/// <see cref="ByHand"/>, its unticked leaves only a person can check.
/// </summary>
public sealed record PlanFitReading(IReadOnlyList<PlanStep> Steps, IReadOnlyList<string> Structural, string? TooLong, string? NoCheck, int ByHand)
{
    /// <summary>Every problem, in <see cref="PlanFit"/>'s order: the structural ones, the length, the check.</summary>
    public IReadOnlyList<string> Problems => [.. Structural, .. Optional(TooLong), .. Optional(NoCheck)];

    /// <summary><see cref="Problems"/> without the no-check item: the plan's shape alone.</summary>
    public IReadOnlyList<string> Shape => [.. Structural, .. Optional(TooLong)];

    private static IEnumerable<string> Optional(string? problem) => problem is null ? [] : [problem];
}

/// <summary>
/// Whether a plan suits a Ralph loop, measured (2026-10-18, the plan at <c>~/.claude/plans/snuggly-finding-comet.md</c>; the user's sample plan
/// came out of 46 gap passes as one checkbox of ~170,000 characters with no <c>verify:</c>, which a loop's three passes a step never finished):
/// the problems in plain words (<see cref="RalphText"/>'s, in a fixed order), read by the gap pass's result, the approval pane's caption and the
/// loop's start (the question on the screen, a notice headless). The <c>body</c> everywhere is <see cref="PlanDocument.Body"/>'s, the header
/// stripped: what a pass is sent and what <see cref="PlanSteps.Parse"/> reads. Steps the loop cannot reach lead the list since 2026-10-21 (zero steps,
/// or, since 2026-10-22, <c>- [ ]</c> steps outside <c>## Steps</c>, <see cref="PlanSteps.StraySteps"/>; the box forms a model drifts into are written
/// as task lines when the plan is saved, <see cref="PlanSteps.Normalized"/>). The structural problems (2026-10-22, the user's call) refuse a clean gap
/// pass until they are closed; the length is said, never a gap. Pure.
/// </summary>
public static class PlanFit
{
    /// <summary>
    /// The body past which a one-step plan is a problem: a short one-step plan is a small job ("rename a variable") and asks nothing. A judgment
    /// call: a plan just over it with one honest step is asked once, and the user says yes.
    /// </summary>
    public const int OneStepChars = 6000;

    /// <summary>
    /// <paramref name="body"/> read once: the steps the loop cannot reach (<see cref="PlanSteps.StraySteps"/>), else no step at all; one step over
    /// <see cref="OneStepChars"/> (every leaf counted, ticked or not: it is the plan's shape), unticked steps with no <c>Done when:</c> (a step already
    /// done never runs again, so its missing line costs nothing), a step with sub-steps whose check is by hand (<see cref="ByHandParents"/>,
    /// 2026-10-21) — the structural problems —, then a body over <paramref name="inlineLimit"/> (<see cref="RalphText.InlineLimit"/>), and no check at
    /// all with <paramref name="reviewed"/> false (with a reviewer each step is still judged, and many projects have no command to name, so a plan
    /// without one is not a problem there).
    /// </summary>
    public static PlanFitReading Read(string body, PlanChecks? checks, bool reviewed, int inlineLimit)
    {
        ArgumentNullException.ThrowIfNull(body);
        var steps = PlanSteps.Parse(body);
        var leaves = PlanSteps.Leaves(steps).ToList();
        var structural = new List<string>(4);
        if (StepsProblemOf(body, leaves.Count > 0) is { } stepsProblem)
        {
            structural.Add(stepsProblem);
        }

        if (leaves.Count == 1 && body.Length > OneStepChars)
        {
            structural.Add(RalphText.FitOneStep);
        }

        int missing = leaves.Count(s => !s.Ticked && s.DoneWhen is null);
        if (missing > 0)
        {
            structural.Add(RalphText.FitNoDoneWhen(missing));
        }

        if (ByHandParentsOf(steps) is int parents and > 0)
        {
            structural.Add(RalphText.FitByHandParent(parents));
        }

        string? noCheck = !reviewed && checks is null or { IsEmpty: true } ? RalphText.FitNoCheck : null;
        return new PlanFitReading(steps, structural, TooLong(body.Length, inlineLimit), noCheck, leaves.Count(s => !s.Ticked && s.ByHand));
    }

    /// <summary>Every problem of <paramref name="body"/> for a loop, in order (<see cref="Read"/>).</summary>
    public static IReadOnlyList<string> Problems(string body, PlanChecks? checks, bool reviewed, int inlineLimit) => Read(body, checks, reviewed, inlineLimit).Problems;

    /// <summary>
    /// A body of <paramref name="chars"/> characters over <paramref name="inlineLimit"/>, said (<see cref="RalphText.FitTooLong"/>), else null: no parse
    /// needed, so a clean pass measures its saved revision without reading it again.
    /// </summary>
    public static string? TooLong(int chars, int inlineLimit) => chars > inlineLimit ? RalphText.FitTooLong(chars, inlineLimit) : null;

    /// <summary>
    /// The unticked leaves of <paramref name="body"/> only a person can check (<see cref="PlanStep.ByHand"/>, 2026-10-21, the plan at
    /// <c>~/.claude/plans/i-put-you-in-snoopy-perlis.md</c>): said on the approval pane and at a loop's start, never one of the <see cref="Problems"/> —
    /// a by-hand step is legitimate, so a gap pass must not try to fix it, nor may it raise the fit question alone.
    /// </summary>
    public static int ByHandSteps(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return PlanSteps.Leaves(PlanSteps.Parse(body)).Count(s => !s.Ticked && s.ByHand);
    }

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
    /// <see cref="Problems"/> without the no-check item: the plan's shape alone, for the approval pane's caption (its check line already says
    /// <c>unchecked</c> and the reviewer).
    /// </summary>
    public static IReadOnlyList<string> Shape(string body, int inlineLimit) => Read(body, null, reviewed: true, inlineLimit).Shape;

    /// <summary><see cref="Shape"/> and <see cref="ByHandSteps"/> from one reading of <paramref name="body"/>: the approval pane's check line and a loop's confirm.</summary>
    public static IReadOnlyList<string> Shape(string body, int inlineLimit, out int byHand)
    {
        var reading = Read(body, null, reviewed: true, inlineLimit);
        byHand = reading.ByHand;
        return reading.Shape;
    }

    /// <summary>
    /// Why a loop cannot start on <paramref name="body"/>, or null when it has a step (2026-10-21): <see cref="RalphText.NoStepsStrayError"/> when its
    /// steps sit outside <c>## Steps</c>, else <see cref="RalphText.NoStepsError"/>. Read before any confirm on the screen and by headless, so nobody
    /// says yes to a question and is then refused.
    /// </summary>
    public static string? StepsRefusal(string body) => StepsRefusal(body, PlanSteps.Parse(body ?? throw new ArgumentNullException(nameof(body))));

    /// <summary><see cref="StepsRefusal(string)"/> over <paramref name="steps"/>, <paramref name="body"/>'s already parsed (2026-10-22, a review).</summary>
    public static string? StepsRefusal(string body, IReadOnlyList<PlanStep> steps)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(steps);
        if (PlanSteps.Leaves(steps).Any())
        {
            return null;
        }

        return PlanSteps.StraySteps(body) is int stray and > 0 ? RalphText.NoStepsStrayError(stray) : RalphText.NoStepsError;
    }

    private static string? StepsProblemOf(string body, bool anyStep)
    {
        if (PlanSteps.StraySteps(body) is int stray and > 0)
        {
            return RalphText.FitStraySteps(stray);
        }

        return anyStep ? null : RalphText.FitNoSteps;
    }
}
