namespace NeonSidekick.Plans;

/// <summary>
/// What a plan revision touched, by confirming focus (2026-11-18, Phase 3 item 3, focus selection, the notes' "focus picked by what changed"): for
/// each of <see cref="PlanText.ConfirmFocuses"/>' five, in their order (order, verification, risks, assumptions, size), the count of lines the
/// revision changed in that focus's area. <see cref="ConfirmFocusPick"/> reads it when <see cref="Settings.AppSettingsData.ConfirmFocusByChange"/>
/// is on.
/// <list type="bullet">
/// <item>Steps (<see cref="PlanStepChanges.Compare"/>): each added, removed or moved step counts for <i>order</i> and <i>size</i>; each changed
/// <c>Done when:</c> for <i>verification</i>; each reworded step for <i>assumptions</i>, since step lines name the files and APIs.</item>
/// <item>Every other line, by its <c>##</c> section (<see cref="PlanDocument.SectionLines"/>), the lines gone and the lines new, order aside: a
/// heading with "risk" counts for <i>risks</i>, one with "verif" for <i>verification</i>, any other (Goal, Context, Files and areas…) for
/// <i>assumptions</i>. Step lines (<see cref="PlanSteps.IsStepLine"/>) are left to the step count, blank lines aside.</item>
/// </list>
/// Left out, as <see cref="PlanStepChanges"/> leaves them out: <c>## References</c>, the lines before the first <c>##</c> heading and fenced code.
/// A known limit: the header's <c>verify</c> and <c>final</c> aren't in the body, so a change to them weighs nothing (they're rarely changed, and
/// <c>Done when:</c> covers <i>verification</i>). Pure.
/// </summary>
public static class PlanFocusAreas
{
    /// <summary><see cref="PlanText.ConfirmFocusLabels"/>' indexes, the order <see cref="Weigh"/> returns its counts in.</summary>
    public const int Order = 0;

    /// <summary>The <i>verification</i> focus's index.</summary>
    public const int Verification = 1;

    /// <summary>The <i>risks</i> focus's index.</summary>
    public const int Risks = 2;

    /// <summary>The <i>assumptions</i> focus's index.</summary>
    public const int Assumptions = 3;

    /// <summary>The <i>size</i> focus's index.</summary>
    public const int Size = 4;

    /// <summary>
    /// The lines <paramref name="after"/> changed of <paramref name="before"/>, per focus. Either null (headless couldn't read the plan file) gives all
    /// zeros, so the pick falls back to the least-used focus.
    /// </summary>
    public static IReadOnlyList<int> Weigh(string? before, string? after)
    {
        var weights = new int[PlanText.ConfirmFocuses.Count];
        if (before is null || after is null || string.Equals(before, after, StringComparison.Ordinal))
        {
            return weights;
        }

        var steps = PlanStepChanges.Compare(before, after);
        int shape = steps.Added.Count + steps.Removed.Count + steps.Moved.Count;
        weights[Order] += shape;
        weights[Size] += shape;
        weights[Verification] += steps.DoneWhenChanged.Count;
        weights[Assumptions] += steps.RewordedSteps.Count;

        var was = Sections(before);
        var now = Sections(after);
        foreach (string name in was.Keys.Union(now.Keys, StringComparer.OrdinalIgnoreCase))
        {
            int changed = Changed(was.GetValueOrDefault(name) ?? [], now.GetValueOrDefault(name) ?? []);
            weights[AreaOf(name)] += changed;
        }

        return weights;
    }

    /// <summary>The focus a <c>##</c> section's lines count for: <i>risks</i> for a heading with "risk", <i>verification</i> for "verif", else <i>assumptions</i>.</summary>
    public static int AreaOf(string section)
    {
        ArgumentNullException.ThrowIfNull(section);
        return section.Contains("risk", StringComparison.OrdinalIgnoreCase) ? Risks
            : section.Contains("verif", StringComparison.OrdinalIgnoreCase) ? Verification
            : Assumptions;
    }

    /// <summary>Each section's lines that aren't blank or a step's own, trimmed, by section name; a name twice joins its lines.</summary>
    private static Dictionary<string, List<string>> Sections(string body)
    {
        var sections = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, lines) in PlanDocument.SectionLines(body.Replace("\r\n", "\n", StringComparison.Ordinal)))
        {
            if (!sections.TryGetValue(name, out var kept))
            {
                kept = [];
                sections[name] = kept;
            }

            kept.AddRange(lines.Select(l => l.Trim()).Where(l => l.Length > 0 && !PlanSteps.IsStepLine(l)));
        }

        return sections;
    }

    /// <summary>The lines gone from <paramref name="was"/> plus the lines new in <paramref name="now"/>, as multisets: a line that only moved counts nothing.</summary>
    private static int Changed(List<string> was, List<string> now)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string line in was)
        {
            counts[line] = counts.GetValueOrDefault(line) + 1;
        }

        foreach (string line in now)
        {
            counts[line] = counts.GetValueOrDefault(line) - 1;
        }

        return counts.Values.Sum(Math.Abs);
    }
}
