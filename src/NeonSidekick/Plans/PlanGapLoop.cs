namespace NeonSidekick.Plans;

/// <summary>Who looks for a plan's gaps on each pass of the loop (<see cref="PlanGapLoop"/>).</summary>
public enum PlanReviewer
{
    /// <summary>The planning model alone; the advisors are left out of the pass.</summary>
    Model,

    /// <summary>At least one advisor (<c>neon_advisor</c>) finds the gaps each pass; the model closes what they name.</summary>
    Advisor,

    /// <summary>The model, consulting an advisor only where a role fits (plan mode's own habit).</summary>
    Any,
}

/// <summary>Why a gap-closing loop ended (<see cref="PlanGapLoop.Next"/>).</summary>
public enum PlanGapStop
{
    /// <summary>A pass reported no gaps: the plan went back to the user on the approval pane.</summary>
    Converged,

    /// <summary>A message was queued under the pass's reply: it goes first.</summary>
    UserSpoke,

    /// <summary>The pass limit was reached with gaps still reported.</summary>
    Limit,

    /// <summary>Gaps were reported but the plan did not change, or the same gaps came back.</summary>
    Stalled,

    /// <summary>The pass ended without a revision saved (a question in prose, a failed save, a file gone).</summary>
    NoRevision,

    /// <summary>The pass's reply did not end on its own: ESC, a failure, an interrupt.</summary>
    Stopped,
}

/// <summary>
/// The gap-closing loop (2026-10-13, the user's ask: a <c>Close gaps…</c> choice on the plan's approval pane that keeps
/// reviewing the plan until a pass finds nothing left): the reviewer, the pass number and its limit, the gaps each pass
/// closed and the plan's body as the pass began. One pass is one turn: the screen (or headless) sends the pass's message,
/// <c>present_plan</c> records what the pass closed (<see cref="Record"/>, <see cref="MarkConverged"/>), and after the
/// turn <see cref="Next"/> says whether another pass follows. Pure: the callers read the file and the queue. A stop
/// always ends the loop (<see cref="PlanSession.StopClosing"/>); a new <c>/plan gaps</c> starts a fresh one, so there is
/// no paused state to go stale.
/// </summary>
public sealed class PlanGapLoop
{
    private List<string> _passGaps = [];
    private string? _baseline;

    public PlanGapLoop(PlanReviewer reviewer, int limit)
    {
        Reviewer = reviewer;
        Limit = Math.Max(0, limit);
    }

    public PlanReviewer Reviewer { get; }

    /// <summary>The most passes the loop may run; 0 for no limit. Read once, when the loop starts.</summary>
    public int Limit { get; }

    /// <summary>The pass under way, from 1; 0 while the loop waits for its first pass (<see cref="Pending"/>).</summary>
    public int Pass { get; private set; }

    /// <summary>Chosen, but the first pass not sent yet: the turn that chose it is still ending.</summary>
    public bool Pending => Pass == 0;

    /// <summary>A pass reported no gaps; the loop is over once the turn ends.</summary>
    public bool Converged { get; private set; }

    /// <summary>The gaps closed over every pass so far.</summary>
    public int Closed { get; private set; }

    /// <summary>The revisions <c>present_plan</c> saved (or found unchanged) in this pass.</summary>
    public int Presented { get; private set; }

    /// <summary>The gaps this pass closed, in the order they were reported.</summary>
    public IReadOnlyList<string> PassGaps => _passGaps;

    /// <summary>The gaps the pass before this one closed; empty on the first pass.</summary>
    public IReadOnlyList<string> LastGaps { get; private set; } = [];

    /// <summary>The next pass begins over <paramref name="body"/>, the plan as it stands (the file's body, header aside).</summary>
    public void BeginPass(string? body)
    {
        if (Pass > 0)
        {
            LastGaps = _passGaps;
        }

        _passGaps = [];
        Presented = 0;
        _baseline = body;
        Pass++;
    }

    /// <summary>A revision saved with <paramref name="gaps"/> closed (blank ones already dropped).</summary>
    public void Record(IReadOnlyList<string> gaps)
    {
        ArgumentNullException.ThrowIfNull(gaps);
        Presented++;
        _passGaps.AddRange(gaps);
        Closed += gaps.Count;
    }

    /// <summary>A revision presented with no gaps: the loop has converged.</summary>
    public void MarkConverged()
    {
        Presented++;
        Converged = true;
    }

    /// <summary>
    /// What follows the pass that just ended: null for another pass, else why the loop stops. <paramref name="endedOnItsOwn"/>
    /// is the turn's own ending (no ESC, failure or interrupt); <paramref name="userSpoke"/> a message waiting that would be sent
    /// next; <paramref name="body"/> the plan's body as the pass left it (null when the file cannot be read).
    /// </summary>
    public PlanGapStop? Next(bool endedOnItsOwn, bool userSpoke, string? body)
    {
        if (Converged)
        {
            return PlanGapStop.Converged;
        }

        if (!endedOnItsOwn)
        {
            return PlanGapStop.Stopped;
        }

        if (Presented == 0 || body is null)
        {
            return PlanGapStop.NoRevision;
        }

        if (string.Equals(body, _baseline, StringComparison.Ordinal) || (LastGaps.Count > 0 && SameGaps(_passGaps, LastGaps)))
        {
            return PlanGapStop.Stalled;
        }

        if (Limit > 0 && Pass >= Limit)
        {
            return PlanGapStop.Limit;
        }

        return userSpoke ? PlanGapStop.UserSpoke : null;
    }

    /// <summary>The same gaps, order aside: trimmed, case aside, as sets.</summary>
    public static bool SameGaps(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        var left = new HashSet<string>(a.Select(g => g.Trim()), StringComparer.OrdinalIgnoreCase);
        var right = new HashSet<string>(b.Select(g => g.Trim()), StringComparer.OrdinalIgnoreCase);
        return left.SetEquals(right);
    }
}

/// <summary>What the plan directive says of a running loop (<see cref="PlanText.Directive(string, string?, int, IReadOnlyList{Settings.AdvisorEntry}?, PlanGapState?)"/>): the pass, its limit (0 for none) and the reviewer chosen.</summary>
public sealed record PlanGapState(int Pass, int Limit, PlanReviewer Reviewer);
