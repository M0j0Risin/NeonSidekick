using System.Text.RegularExpressions;

namespace NeonSidekick.Plans;

/// <summary>
/// How much a plan gap matters (2026-11-17, phase 3 of the gap and audit improvements: <c>present_plan</c>'s gap lines start with a tag), in
/// the shadow's bucket order (<see cref="GapShadow"/>). Logged only, never acted on. <see cref="Untagged"/> is a line with no tag the parser knows,
/// counted as serious, the safe side.
/// </summary>
public enum PlanGapSeverity
{
    High,
    Medium,
    Low,
    Untagged,
}

/// <summary>
/// Reads a plan gap's leading severity tag (2026-11-17): <c>[high]</c> as the prompt asks, and the forms models drift to, <c>(high)</c> and
/// <c>high:</c>, case and spaces aside. Only at the line's start: "High-level steps …" and a tag in mid-line stay untagged. The same reader
/// serves <see cref="PlanGapLoop.SameGaps"/> (a relabeled gap is the same gap) and GapBench.
/// </summary>
public static partial class PlanGapTags
{
    /// <summary>The line's tag and the text after it, trimmed; <see cref="PlanGapSeverity.Untagged"/> and the whole line, trimmed, with none.</summary>
    public static (PlanGapSeverity Severity, string Text) Read(string gap)
    {
        ArgumentNullException.ThrowIfNull(gap);
        var match = Tag().Match(gap);
        if (!match.Success)
        {
            return (PlanGapSeverity.Untagged, gap.Trim());
        }

        string word = match.Groups["a"].Success ? match.Groups["a"].Value : match.Groups["b"].Success ? match.Groups["b"].Value : match.Groups["c"].Value;
        var severity = word.ToLowerInvariant() switch
        {
            "high" => PlanGapSeverity.High,
            "medium" => PlanGapSeverity.Medium,
            _ => PlanGapSeverity.Low,
        };
        return (severity, match.Groups["rest"].Value.Trim());
    }

    /// <summary>Whether a gap of <paramref name="severity"/> resets the plan's shadow streak: anything above low, untagged included.</summary>
    public static bool Serious(PlanGapSeverity severity) => severity != PlanGapSeverity.Low;

    [GeneratedRegex(@"^\s*(?:\[\s*(?<a>high|medium|low)\s*\]|\(\s*(?<b>high|medium|low)\s*\)|(?<c>high|medium|low)\s*:)(?<rest>.*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex Tag();
}
