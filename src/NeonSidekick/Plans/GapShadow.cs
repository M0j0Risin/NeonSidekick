namespace NeonSidekick.Plans;

/// <summary>
/// One pass as a shadow rule sees it (2026-11-17, phase 3 of the gap and audit improvements): what the pass's calls added or reopened, counted by
/// severity bucket (an audit's <see cref="Audits.AuditSeverity"/> order, a plan's <see cref="PlanGapSeverity"/> order), the shadow streak after
/// it, whether it called the loop's present tool, and whether anything it did was serious.
/// </summary>
public sealed record GapShadowPass(IReadOnlyList<int> Severities, int Streak, bool Called, bool Serious);

/// <summary>
/// A shadow streak (2026-11-17, first the audit's alone in <see cref="Audits.AuditSession"/>, then shared with the plan gap review for phase 3):
/// a pass with a call and nothing serious moves it on, anything serious resets it, and a pass with no call leaves it. Logged only, never acted
/// on: the bench compares when it would have converged with when the real streak did. One lives on each <see cref="PlanGapLoop"/>
/// (<see cref="PlanGapLoop.Shadow"/>), so a new review or audit starts it at 0 and a paused review keeps it.
/// </summary>
public sealed class GapShadow
{
    /// <summary>The severity buckets a pass counts: an audit's four severities, a plan's three tags and untagged.</summary>
    public const int Buckets = 4;

    private readonly int[] _severities = new int[Buckets];
    private int _pass;
    private bool _called;
    private bool _serious;

    /// <summary>Clean passes in a row by the shadow rule: passes whose calls changed nothing serious.</summary>
    public int Streak { get; private set; }

    /// <summary>
    /// One call of <paramref name="pass"/>: its <paramref name="serious"/> ORed into the pass's, and what it added or reopened counted by
    /// <paramref name="buckets"/> (each an index under <see cref="Buckets"/>). A call of another pass than the one noted starts that pass afresh.
    /// </summary>
    public void Note(int pass, bool serious, IEnumerable<int> buckets)
    {
        ArgumentNullException.ThrowIfNull(buckets);
        if (pass != _pass)
        {
            ClearPass();
            _pass = pass;
        }

        _called = true;
        _serious |= serious;
        foreach (int bucket in buckets)
        {
            _severities[bucket]++;
        }
    }

    /// <summary><paramref name="pass"/> over: the streak moved once (or left, with no call), and what the pass did.</summary>
    public GapShadowPass End(int pass)
    {
        bool called = _called && _pass == pass;
        if (called)
        {
            Streak = _serious ? 0 : Streak + 1;
        }

        var ended = new GapShadowPass(called ? [.. _severities] : new int[Buckets], Streak, called, called && _serious);
        ClearPass();
        return ended;
    }

    /// <summary>The pass with nothing noted and the streak at 0: what a pass reads with no review running.</summary>
    public static GapShadowPass Empty => new(new int[Buckets], 0, false, false);

    private void ClearPass()
    {
        _called = false;
        _serious = false;
        Array.Clear(_severities);
    }
}
