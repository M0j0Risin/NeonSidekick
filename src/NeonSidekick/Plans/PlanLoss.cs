namespace NeonSidekick.Plans;

/// <summary>
/// What a revision took out of a plan (2026-11-17, the gap-audit-reliability plan: a review pass would report no gaps while its revision dropped
/// unfinished steps or whole sections, and the review converged on the shorter plan): the unfinished steps removed, the ticked boxes unticked,
/// the <c>##</c> sections dropped, and a cut past <see cref="PlanLoss.ShrinkLimit"/> of the length. <see cref="Any"/> when anything went.
/// </summary>
public sealed record PlanLossReport(IReadOnlyList<string> Steps, IReadOnlyList<string> Unticked, IReadOnlyList<string> Sections, int? ShrinkPercent)
{
    /// <summary>Nothing lost.</summary>
    public static PlanLossReport None { get; } = new([], [], [], null);

    /// <summary>Whether anything went.</summary>
    public bool Any => Steps.Count > 0 || Unticked.Count > 0 || Sections.Count > 0 || ShrinkPercent is not null;
}

/// <summary>
/// The lost-content check (2026-11-17, the gap-audit-reliability plan) over two plan bodies, the one on disk and the one a pass sends. Steps are
/// paired as the Ralph loop pairs them (<see cref="PlanSteps.Align"/> and <see cref="PlanSteps.PairMoved"/>), sections by their <c>##</c> headings
/// outside fenced code, the length in characters with line endings as <c>\n</c>. <c>## References</c> is the app's (it reconciles the section
/// on every save), so both bodies are measured without it. Known limits, both from <see cref="PlanSteps.Align"/>: a step deleted and another added
/// in its place pair as a rewording (a loss missed); a step reworded past alike where the count changes beside it reads as deleted. Pure.
/// </summary>
public static class PlanLoss
{
    /// <summary>The share of the plan's length a revision may cut before it counts as a loss.</summary>
    public const double ShrinkLimit = 0.40;

    /// <summary>What <paramref name="newBody"/> lost of <paramref name="oldBody"/>.</summary>
    public static PlanLossReport Compare(string oldBody, string newBody)
    {
        ArgumentNullException.ThrowIfNull(oldBody);
        ArgumentNullException.ThrowIfNull(newBody);
        string before = PlanReferences.WithoutSection(Lf(oldBody)).Trim();
        string after = PlanReferences.WithoutSection(Lf(newBody)).Trim();
        if (string.Equals(before, after, StringComparison.Ordinal))
        {
            return PlanLossReport.None;
        }

        bool headed = PlanSteps.HasStepsHeading(before);
        var was = PlanSteps.Flat(PlanSteps.Parse(before, headed));
        var now = PlanSteps.Flat(PlanSteps.Parse(after, headed));
        var pairs = PlanSteps.Align(was, now);
        PlanSteps.PairMoved(was, now, pairs);
        var paired = new HashSet<int>(pairs.Where(p => p >= 0));
        var steps = was.Where((s, k) => !s.Ticked && !paired.Contains(k)).Select(s => s.Text).ToList();
        var unticked = new List<string>();
        for (int i = 0; i < now.Count; i++)
        {
            if (pairs[i] >= 0 && was[pairs[i]].Ticked && !now[i].Ticked)
            {
                unticked.Add(now[i].Text);
            }
        }

        var kept = new HashSet<string>(PlanDocument.Sections(after), StringComparer.OrdinalIgnoreCase);
        var sections = PlanDocument.Sections(before).Where(s => !kept.Contains(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        int? shrink = before.Length > 0 && after.Length < before.Length * (1 - ShrinkLimit)
            ? (int)((long)(before.Length - after.Length) * 100 / before.Length)
            : null;
        return new PlanLossReport(steps, unticked, sections, shrink);
    }

    private static string Lf(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
}
