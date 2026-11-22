namespace NeonSidekick.Files;

/// <summary>
/// Whether two working folders are the same one (2026-11-21, feature ideas #4 and #6: a session's folder, a memory's): both made full and
/// stripped of a trailing separator, then compared as <see cref="WorkingDirectory.PathComparer"/> does (case-insensitive on Windows, exact
/// elsewhere). A null, blank or malformed side never matches. Pure.
/// </summary>
public static class FolderMatch
{
    public static bool Same(string? a, string? b)
    {
        if (Normal(a) is not { } left || Normal(b) is not { } right)
        {
            return false;
        }

        return WorkingDirectory.PathComparer.Equals(left, right);
    }

    /// <summary>
    /// The folder as compared: full, no trailing separator; null for one that can't be made full. A folder must already be fully qualified in
    /// this system's style (2026-11-22, the review's #78): a Mac's <c>/Users/x/Repo</c> read on Windows would otherwise be made
    /// <c>C:\Users\x\Repo</c>, and a relative one would take the process's folder; both are refused, never guessed.
    /// </summary>
    public static string? Normal(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder))
        {
            return null;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
