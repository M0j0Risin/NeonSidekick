namespace NeonSidekick.Plans;

/// <summary>
/// How a revision changed a plan's steps (2026-11-18, phase 3 item 2: the mechanical minor check, measured before it is trusted): the steps added,
/// removed and moved, and the paired steps whose <c>Done when:</c> changed, each by its text. A reworded step that stays paired is no change: that is
/// the minor fix <c>minor</c> exists for. <see cref="Any"/> when anything changed. <paramref name="Reworded"/> (2026-11-18, Phase 3 item 3, focus selection):
/// the paired steps whose own words changed, which <see cref="Any"/> leaves out; <see cref="PlanFocusAreas"/> weighs them as <i>assumptions</i>.
/// </summary>
public sealed record PlanStepChangeReport(IReadOnlyList<string> Added, IReadOnlyList<string> Removed, IReadOnlyList<string> Moved, IReadOnlyList<string> DoneWhenChanged,
    IReadOnlyList<string>? Reworded = null)
{
    /// <summary>The paired steps whose words changed: <see cref="Reworded"/>, empty for none.</summary>
    public IReadOnlyList<string> RewordedSteps => Reworded ?? [];

    /// <summary>No step changed.</summary>
    public static PlanStepChangeReport None { get; } = new([], [], [], []);

    /// <summary>Whether any step changed.</summary>
    public bool Any => Added.Count > 0 || Removed.Count > 0 || Moved.Count > 0 || DoneWhenChanged.Count > 0;
}

/// <summary>
/// The step-change check over two plan bodies (2026-11-18, phase 3 item 2): a clean report whose revision adds, removes or moves a step, or changes a
/// <c>Done when:</c>, is logged (<see cref="PlanText.StepChangesLogLine"/>) and counted by GapBench, so the bench shows how often a "minor" fix changed
/// what the plan does before the check is allowed to make it a gap. Steps are paired as <see cref="PlanLoss"/> pairs them: <c>## References</c> set
/// aside, <see cref="PlanSteps.Flat"/>, <see cref="PlanSteps.Align"/>, then <see cref="PlanSteps.PairMoved"/>; a pair the moved-line pass made is a
/// moved step. It inherits <see cref="PlanSteps.Align"/>'s documented limit: a step deleted and another added in its place pair as a rewording. Pure.
/// </summary>
public static class PlanStepChanges
{
    /// <summary>How <paramref name="newBody"/> changed the steps of <paramref name="oldBody"/>.</summary>
    public static PlanStepChangeReport Compare(string oldBody, string newBody)
    {
        ArgumentNullException.ThrowIfNull(oldBody);
        ArgumentNullException.ThrowIfNull(newBody);
        string before = PlanReferences.WithoutSection(Lf(oldBody)).Trim();
        string after = PlanReferences.WithoutSection(Lf(newBody)).Trim();
        if (string.Equals(before, after, StringComparison.Ordinal))
        {
            return PlanStepChangeReport.None;
        }

        bool headed = PlanSteps.HasStepsHeading(before);
        var was = PlanSteps.Flat(PlanSteps.Parse(before, headed));
        var now = PlanSteps.Flat(PlanSteps.Parse(after, headed));
        var pairs = PlanSteps.Align(was, now);
        var aligned = pairs.Select(p => p >= 0).ToArray();
        PlanSteps.PairMoved(was, now, pairs);
        var added = new List<string>();
        var moved = new List<string>();
        var doneWhen = new List<string>();
        var reworded = new List<string>();
        for (int i = 0; i < now.Count; i++)
        {
            if (pairs[i] < 0)
            {
                added.Add(now[i].Text);
                continue;
            }

            if (!aligned[i])
            {
                moved.Add(now[i].Text);
            }

            if (!string.Equals(Collapsed(was[pairs[i]].Text), Collapsed(now[i].Text), StringComparison.Ordinal))
            {
                reworded.Add(now[i].Text);
            }

            if (!string.Equals(Collapsed(was[pairs[i]].DoneWhen), Collapsed(now[i].DoneWhen), StringComparison.Ordinal))
            {
                doneWhen.Add(now[i].Text);
            }
        }

        var paired = new HashSet<int>(pairs.Where(p => p >= 0));
        var removed = was.Where((_, k) => !paired.Contains(k)).Select(s => s.Text).ToList();
        return new PlanStepChangeReport(added, removed, moved, doneWhen, reworded);
    }

    /// <summary>A <c>Done when:</c> with its whitespace runs as one space, trimmed; null for none.</summary>
    private static string? Collapsed(string? doneWhen) =>
        doneWhen is null ? null : string.Join(' ', doneWhen.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string Lf(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
}
