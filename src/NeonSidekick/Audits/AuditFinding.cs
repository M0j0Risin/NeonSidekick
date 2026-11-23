namespace NeonSidekick.Audits;

/// <summary>How bad a code audit's finding is (2026-10-18): a nit is never a finding, it goes into <c>present_audit</c>'s <c>minor</c>.</summary>
public enum AuditSeverity
{
    Critical,
    High,
    Medium,
    Low,
}

/// <summary>
/// Where a finding stands, the one definition every reader uses (2026-10-18): open while its box is unticked, settled once ticked (fixed
/// by the fix plan, or dismissed by the user), retracted once a pass withdrew it.
/// </summary>
public enum AuditFindingState
{
    Open,
    Settled,
    Retracted,
}

/// <summary>
/// One finding of a code audit's report (2026-10-18, <see cref="AuditReport"/>): its id (<c>A3</c>, never reused), severity and dimension,
/// the file (an <see cref="AuditPath"/>, <c>/</c>-separated and sandbox-relative) and first line of the issue (null for a whole file or a
/// deleted one), the one-line title, the detail, the suggested fix and the quoted evidence (redacted, as saved), the pass that reported it,
/// whether its box is ticked (<see cref="Settled"/>) or a pass retracted it (with the reason), and the notes the report keeps under it
/// ("reopened in pass 4: …", "revised in pass 3: …").
/// </summary>
public sealed partial record AuditFinding(
    string Id,
    AuditSeverity Severity,
    string Dimension,
    string File,
    int? Line,
    string Title,
    string Detail,
    string? Fix,
    string? Evidence,
    int Pass,
    bool Settled,
    bool Retracted,
    string? RetractReason,
    IReadOnlyList<string> Notes)
{
    /// <summary>Open, settled or retracted: the one definition (retracted first, then the box).</summary>
    public AuditFindingState State => Retracted ? AuditFindingState.Retracted : Settled ? AuditFindingState.Settled : AuditFindingState.Open;

    /// <summary><c>src/x.cs:42</c>, or the file alone with no line.</summary>
    public string Location => Line is { } line ? File + ":" + line.ToString(System.Globalization.CultureInfo.InvariantCulture) : File;

    /// <summary>
    /// What <see cref="Plans.PlanGapLoop.Record"/> keys a new finding by (2026-10-18): <c>file — title</c>, so <c>SameGaps</c> catches a pass
    /// that repeats the last one (ids always differ).
    /// </summary>
    public string GapKey => GapKeyOf(File, Title);

    /// <summary><see cref="GapKey"/> of a file and a title.</summary>
    public static string GapKeyOf(string file, string title) => file + " — " + title;

    /// <summary>The number in an id (<c>A12</c> → 12), or null for anything else.</summary>
    public static int? IdNumber(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        string trimmed = id.Trim();
        return trimmed.Length > 1 && trimmed[0] is 'A' or 'a'
            && int.TryParse(trimmed.AsSpan(1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int n) && n > 0
            ? n
            : null;
    }

    /// <summary>The id for <paramref name="number"/>: <c>A7</c>.</summary>
    public static string IdFor(int number) => "A" + number.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The widest range <see cref="ClosedBy"/> expands; a wider one is read as its two ends (a slip never builds a long list).</summary>
    public const int MaxClosedRange = 100;

    /// <summary>
    /// The ids a fix plan's step line says it closes (2026-11-22, the session review: the app ticks them as the step passes): <c>(closes A7)</c>,
    /// <c>(closes A1, A3)</c>, <c>closes A2 and A5</c>, a range <c>closes A3–A5</c> or <c>A3-A5</c> (expanded, a reversed one in order), case-insensitive,
    /// in order without repeats. Only the ids right after the word <c>closes</c> count, so an "(see A9)" elsewhere on the line is never one;
    /// emphasis around them is no matter.
    /// </summary>
    public static IReadOnlyList<string> ClosedBy(string stepText)
    {
        ArgumentNullException.ThrowIfNull(stepText);
        var ids = new List<string>();
        foreach (System.Text.RegularExpressions.Match m in ClosesList().Matches(stepText))
        {
            foreach (System.Text.RegularExpressions.Match item in ClosesItem().Matches(m.Groups[1].Value))
            {
                int from = int.Parse(item.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                int to = item.Groups[2].Success ? int.Parse(item.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) : from;
                (from, to) = from <= to ? (from, to) : (to, from);
                IEnumerable<int> numbers = to - from > MaxClosedRange ? [from, to] : Enumerable.Range(from, to - from + 1);
                foreach (int n in numbers.Where(n => n > 0))
                {
                    string id = IdFor(n);
                    if (!ids.Contains(id))
                    {
                        ids.Add(id);
                    }
                }
            }
        }

        return ids;
    }

    // "closes" then a list: ids or ranges, joined by commas, "and" or "&".
    [System.Text.RegularExpressions.GeneratedRegex(@"\bcloses\s*:?\s*((?:\*|_)*A\d{1,6}(?:\s*(?:[-–—]|to)\s*A\d{1,6})?(?:\s*(?:,\s*and|,|and|&)\s*A\d{1,6}(?:\s*(?:[-–—]|to)\s*A\d{1,6})?)*)", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex ClosesList();

    [System.Text.RegularExpressions.GeneratedRegex(@"A(\d{1,6})(?:\s*(?:[-–—]|to)\s*A(\d{1,6}))?", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex ClosesItem();

    /// <summary>The severity's word: <c>critical</c>, <c>high</c>, <c>medium</c>, <c>low</c>.</summary>
    public static string Word(AuditSeverity severity) => severity switch
    {
        AuditSeverity.Critical => "critical",
        AuditSeverity.High => "high",
        AuditSeverity.Medium => "medium",
        _ => "low",
    };

    /// <summary>The severity's heading in the report: <c>Critical</c>, <c>High</c>, <c>Medium</c>, <c>Low</c>.</summary>
    public static string Heading(AuditSeverity severity) => severity.ToString();

    /// <summary>
    /// The severity <paramref name="word"/> names, case aside: the four words, <c>severe</c> and <c>major</c> as high, <c>moderate</c> as
    /// medium, <c>minor</c> as low (a nit is not: <see cref="IsNitWord"/>); null for anything else.
    /// </summary>
    public static AuditSeverity? ParseSeverity(string word)
    {
        ArgumentNullException.ThrowIfNull(word);
        return word.Trim().ToLowerInvariant() switch
        {
            "critical" or "blocker" => AuditSeverity.Critical,
            "high" or "severe" or "major" => AuditSeverity.High,
            "medium" or "moderate" => AuditSeverity.Medium,
            "low" or "minor" => AuditSeverity.Low,
            _ => null,
        };
    }

    /// <summary>A severity word that means a nit (<c>nit</c>, <c>info</c>, <c>trivial</c>…): refused with "put it in minor".</summary>
    public static bool IsNitWord(string word)
    {
        ArgumentNullException.ThrowIfNull(word);
        return word.Trim().ToLowerInvariant() is "nit" or "nits" or "info" or "informational" or "trivial" or "style" or "note";
    }
}
