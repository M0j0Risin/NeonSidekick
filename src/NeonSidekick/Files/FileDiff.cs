using NeonSidekick.Git;

namespace NeonSidekick.Files;

/// <summary>
/// What a write or an edit changed in a text file, for the transcript only (2026-10-03, the user's ask: a diff under each
/// edit, the way Claude Code shows one): the hunks <see cref="UnifiedDiff"/> makes of the text before and after, with
/// <see cref="UnifiedDiff.DefaultContext"/> lines around each change, and the counts. The model never sees it; its result
/// sentence is unchanged. <paramref name="Created"/> marks a file that was not there, every line an addition.
/// <paramref name="Path"/> is the file's relative path, which names its language for the colouring. Pure.
/// </summary>
public sealed record FileDiff(string Path, bool Created, IReadOnlyList<UnifiedDiff.Hunk> Hunks, int Added, int Removed)
{
    /// <summary>
    /// The diff of <paramref name="before"/> (null for a file that was not there) to <paramref name="after"/>; null when nothing
    /// changed or the texts hold more distinct lines than <see cref="UnifiedDiff.MaxDistinctLines"/>.
    /// </summary>
    public static FileDiff? Of(string path, string? before, string after)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(after);
        var a = before is null ? UnifiedDiff.Text.Empty : UnifiedDiff.Split(before);
        var b = UnifiedDiff.Split(after);
        if (UnifiedDiff.Hunks(a, b) is not { Count: > 0 } hunks)
        {
            return null;
        }

        var (added, removed) = UnifiedDiff.Count(hunks);
        return new FileDiff(path, before is null, hunks, added, removed);
    }

    /// <summary>
    /// An append's diff (<c>write_file</c> mode append): <paramref name="appended"/> as added lines after the
    /// <paramref name="oldLines"/> the file held, with no context — the file's head is not read for it. Null for nothing added.
    /// </summary>
    public static FileDiff? Appended(string path, int oldLines, string appended)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(appended);
        var added = UnifiedDiff.Split(appended).Lines;
        if (added.Count == 0)
        {
            return null;
        }

        var lines = added.Select(l => "+" + l).ToList();
        return new FileDiff(path, oldLines == 0, [new UnifiedDiff.Hunk(oldLines + 1, 0, oldLines + 1, added.Count, lines)], added.Count, 0);
    }
}
