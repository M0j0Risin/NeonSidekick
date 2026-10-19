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
public sealed record AuditFinding(
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
