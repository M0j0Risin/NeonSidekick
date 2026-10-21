namespace NeonSidekick.Audits;

/// <summary>
/// What one pass of a code audit looks at (<see cref="AuditFocus.PassFocus"/>): <paramref name="Dimensions"/>, every chosen dimension on a
/// finding pass and one in rotation on a confirming pass, and with a single dimension chosen an <paramref name="Angle"/> of
/// <see cref="AuditText.ConfirmAngles"/> instead.
/// </summary>
public sealed record AuditPassFocus(IReadOnlyList<string> Dimensions, string? Angle = null)
{
    /// <summary><c>security, correctness</c>, or <c>security: edge cases</c> with an angle.</summary>
    public string Label => string.Join(", ", Dimensions) + (Angle is { Length: > 0 } angle ? ": " + angle : "");

    /// <summary><see cref="Label"/> with the five written <c>all</c>: what the pass message says.</summary>
    public string Short => (Dimensions.Count == AuditFocus.Dimensions.Count ? AuditFocus.AllWord : string.Join(", ", Dimensions)) + (Angle is { Length: > 0 } angle ? ": " + angle : "");
}

/// <summary>
/// A code audit's dimensions (2026-10-18, <c>/code-audit</c>): security, correctness, performance, maintainability and tests, in that order;
/// <c>all</c> names the five. A finding pass covers every chosen dimension; a confirming pass takes one in rotation, or with one dimension
/// chosen one of <see cref="AuditText.ConfirmAngles"/> (the plan gaps' <c>ConfirmFocuses</c> idea). Pure.
/// </summary>
public static class AuditFocus
{
    public const string Security = "security";
    public const string Correctness = "correctness";
    public const string Performance = "performance";
    public const string Maintainability = "maintainability";
    public const string Tests = "tests";

    /// <summary>The word that names every dimension.</summary>
    public const string AllWord = "all";

    /// <summary>The five dimensions in their order. Pinned.</summary>
    public static readonly IReadOnlyList<string> Dimensions = [Security, Correctness, Performance, Maintainability, Tests];

    /// <summary><c>Code audit focus</c>'s default: all five (a fresh list, the setting's own copy).</summary>
    public static List<string> Default() => [.. Dimensions];

    /// <summary>The dimension <paramref name="word"/> names, case aside; null for any other word (<see cref="AllWord"/> included).</summary>
    public static string? Parse(string word)
    {
        ArgumentNullException.ThrowIfNull(word);
        string trimmed = word.Trim();
        return Dimensions.FirstOrDefault(d => string.Equals(d, trimmed, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// <paramref name="dimensions"/> as known dimensions in their order, each once; null, empty or nothing known reads as all five (an old or
    /// hand-edited profile, the setting never saved empty).
    /// </summary>
    public static IReadOnlyList<string> Normalize(IEnumerable<string>? dimensions)
    {
        var known = (dimensions ?? []).Select(d => Parse(d ?? "")).OfType<string>().ToHashSet(StringComparer.Ordinal);
        return known.Count == 0 ? Dimensions : Dimensions.Where(known.Contains).ToList();
    }

    /// <summary>Whether <paramref name="dimensions"/> are all five.</summary>
    public static bool IsAll(IReadOnlyList<string> dimensions)
    {
        ArgumentNullException.ThrowIfNull(dimensions);
        return Normalize(dimensions).Count == Dimensions.Count;
    }

    /// <summary>How many focuses the confirming passes rotate through: the dimensions chosen, or with one the angles. Feeds <see cref="Plans.PlanGapLoop"/>.</summary>
    public static int FocusCount(IReadOnlyList<string> dimensions)
    {
        ArgumentNullException.ThrowIfNull(dimensions);
        var normal = Normalize(dimensions);
        return normal.Count == 1 ? AuditText.ConfirmAngles.Count : normal.Count;
    }

    /// <summary>
    /// What a pass looks at: every dimension on a finding pass (<paramref name="confirmation"/> 0); on a confirming pass, the
    /// <paramref name="confirmation"/>th in rotation from 1 (with one dimension, that dimension and the matching angle).
    /// </summary>
    public static AuditPassFocus PassFocus(IReadOnlyList<string> dimensions, int confirmation)
    {
        ArgumentNullException.ThrowIfNull(dimensions);
        var normal = Normalize(dimensions);
        if (confirmation <= 0)
        {
            return new AuditPassFocus(normal);
        }

        if (normal.Count == 1)
        {
            return new AuditPassFocus(normal, AuditText.ConfirmAngles[(confirmation - 1) % AuditText.ConfirmAngles.Count]);
        }

        return new AuditPassFocus([normal[(confirmation - 1) % normal.Count]]);
    }

    /// <summary>A focus as the header and the notices write it: <c>all</c>, or the names joined.</summary>
    public static string Label(IReadOnlyList<string> dimensions)
    {
        ArgumentNullException.ThrowIfNull(dimensions);
        var normal = Normalize(dimensions);
        return normal.Count == Dimensions.Count ? AllWord : string.Join(", ", normal);
    }
}
