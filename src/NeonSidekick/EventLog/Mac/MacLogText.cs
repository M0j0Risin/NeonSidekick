namespace NeonSidekick.EventLog;

/// <summary>
/// The Mac event logs' wording (2026-10-18, the Mac event-log plan): what the unified log, the crash reports and install.log say
/// to the model and the user. Pinned by tests; the Windows sentences stay in <see cref="EventLogText"/>.
/// </summary>
public static class MacLogText
{
    /// <summary>The store refused for want of rights: Apple's local store is for an administrator account.</summary>
    public const string NeedsAdmin = "the unified log can be read only from an administrator account on this Mac.";

    /// <summary>A <c>.logarchive</c> that is not there or not an archive.</summary>
    public static string NoArchive(string name) => $"'{name}' is not a log archive this Mac can read.";

    /// <summary>OSLog refused the filter itself (a value it could not take).</summary>
    public const string FilterRefused = "the unified log refused that filter; try fewer or plainer terms.";

    /// <summary>Any other refusal, OSLog's own words kept.</summary>
    public static string Refused(string description) => $"the unified log could not be read: {description}";
}

/// <summary>
/// An OSLog refusal turned into a sentence (2026-10-18). OSLog's error codes are not documented, so the words of its description
/// decide, with the code a fallback: rights (an account that is not an administrator), a missing archive, a refused filter, else
/// OSLog's own description. Pure.
/// </summary>
public static class MacLogErrors
{
    public static string Sentence(UnifiedLogException failure, string? archive = null)
    {
        ArgumentNullException.ThrowIfNull(failure);
        string words = failure.Description.ToLowerInvariant();
        if (words.Contains("permission", StringComparison.Ordinal) || words.Contains("not permitted", StringComparison.Ordinal)
            || words.Contains("not authorized", StringComparison.Ordinal) || words.Contains("denied", StringComparison.Ordinal))
        {
            return MacLogText.NeedsAdmin;
        }

        if (archive is not null && (words.Contains("no such file", StringComparison.Ordinal) || words.Contains("doesn't exist", StringComparison.Ordinal)
            || words.Contains("couldn't be opened", StringComparison.Ordinal) || words.Contains("not a valid", StringComparison.Ordinal)))
        {
            return MacLogText.NoArchive(Path.GetFileName(archive.TrimEnd('/')));
        }

        if (words.Contains("predicate", StringComparison.Ordinal) || words.Contains("no such field", StringComparison.Ordinal))
        {
            return MacLogText.FilterRefused;
        }

        return MacLogText.Refused(failure.Description);
    }
}
