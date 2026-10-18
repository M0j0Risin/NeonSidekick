using System.Globalization;
using System.Text.RegularExpressions;

namespace NeonSidekick.Plans;

/// <summary>What <c>/plan open</c> says of a plan's last gap review (2026-10-17, <see cref="PlanListing.Review"/>).</summary>
public enum PlanReviewMark
{
    /// <summary>No review line, or one whose review did not converge (the line in the file tells).</summary>
    None,

    /// <summary>A review that converged on a single clean pass, and the body is the one it reviewed.</summary>
    Reviewed,

    /// <summary>A review that converged on two clean passes or more, and the body is the one it reviewed.</summary>
    Confirmed,

    /// <summary>A review that converged, but the body has changed since (ticks aside).</summary>
    EditedSince,
}

/// <summary>
/// A plan file's <c>review:</c> line read back (2026-10-17, the user's approval of later idea #3): the passes, the gaps closed, whether
/// the review converged and on how many clean passes, and the body's fingerprint (<see cref="PlanDocument.Fingerprint"/>). Written by
/// <see cref="PlanText.ReviewLine"/>; a line it can't parse counts as no review. Pure.
/// </summary>
public sealed partial record PlanReview(int Passes, int Closed, bool Converged, int CleanPasses, string Fingerprint)
{
    /// <summary>The review <paramref name="line"/> records, or null for a line this build can't read.</summary>
    public static PlanReview? TryParse(string? line)
    {
        if (string.IsNullOrWhiteSpace(line) || LinePattern().Match(line.Trim()) is not { Success: true } m)
        {
            return null;
        }

        if (!int.TryParse(m.Groups["passes"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int passes)
            || !int.TryParse(m.Groups["closed"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int closed))
        {
            return null;
        }

        string outcome = m.Groups["outcome"].Value;
        if (ConfirmedPattern().Match(outcome) is { Success: true } confirmed
            && int.TryParse(confirmed.Groups["clean"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int clean))
        {
            return new PlanReview(passes, closed, true, clean, m.Groups["body"].Value);
        }

        return new PlanReview(passes, closed, string.Equals(outcome, PlanText.ReviewOneCleanPass, StringComparison.Ordinal), 1, m.Groups["body"].Value);
    }

    /// <summary>
    /// What <c>/plan open</c> marks for a file whose header holds <paramref name="line"/> over <paramref name="body"/>: confirmed or reviewed
    /// when the review converged and the fingerprint matches, edited since when it converged and the body changed, else none.
    /// </summary>
    public static PlanReviewMark Mark(string? line, string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (TryParse(line) is not { Converged: true } review)
        {
            return PlanReviewMark.None;
        }

        if (!string.Equals(review.Fingerprint, PlanDocument.Fingerprint(body), StringComparison.Ordinal))
        {
            return PlanReviewMark.EditedSince;
        }

        return review.CleanPasses >= 2 ? PlanReviewMark.Confirmed : PlanReviewMark.Reviewed;
    }

    [GeneratedRegex(@"^(?<passes>\d{1,4}) pass(?:es)?, (?<closed>\d{1,6}) gaps? closed, (?<outcome>.+) \(body (?<body>[0-9a-f]{8})\)$", RegexOptions.CultureInvariant)]
    private static partial Regex LinePattern();

    [GeneratedRegex(@"^confirmed by (?<clean>\d) clean passes$", RegexOptions.CultureInvariant)]
    private static partial Regex ConfirmedPattern();
}
