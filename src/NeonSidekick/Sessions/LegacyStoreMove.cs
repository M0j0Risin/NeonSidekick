using NeonSidekick.Diagnostics;

namespace NeonSidekick.Sessions;

/// <summary>
/// The one-time move of a SQLite store left where an older build kept it (2026-10-11, the review: <c>sessions.db</c> and <c>skills.db</c>
/// went under <c>db\</c> on 2026-10-10 with no migration, so any install not moved by hand — a release zip's, the Mac's, another
/// machine's — opened an empty store and left its history behind). The file and its siblings (<c>-wal</c>, <c>-shm</c>, <c>-journal</c>)
/// move only when the old file is there and the new one is not: never an overwrite, never a merge. The siblings go first and the file
/// last, and a failure puts back what moved, so the new folder never holds a WAL without its database.
/// </summary>
public static class LegacyStoreMove
{
    private static readonly string[] Siblings = ["-wal", "-shm", "-journal"];

    /// <summary>
    /// The path the store should open: <paramref name="newPath"/>, after moving <paramref name="oldPath"/> there when only the old one
    /// exists; <paramref name="oldPath"/> when that move failed (another instance holding it open, a read-only folder), so the history is
    /// still read where it is and the move is tried again at the next start. Logged under <paramref name="category"/>.
    /// </summary>
    public static string Resolve(string oldPath, string newPath, string category)
    {
        ArgumentNullException.ThrowIfNull(oldPath);
        ArgumentNullException.ThrowIfNull(newPath);
        if (File.Exists(newPath) || !File.Exists(oldPath))
        {
            return newPath;
        }

        var moved = new List<(string From, string To)>();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);
            foreach (string sibling in Siblings)
            {
                string from = oldPath + sibling;
                if (File.Exists(from))
                {
                    File.Move(from, newPath + sibling);
                    moved.Add((from, newPath + sibling));
                }
            }

            File.Move(oldPath, newPath);
            DiagnosticLog.Info(category, MovedLogLine(oldPath, newPath));
            return newPath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            for (int i = moved.Count - 1; i >= 0; i--)
            {
                try
                {
                    File.Move(moved[i].To, moved[i].From);
                }
                catch (Exception back) when (back is IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    DiagnosticLog.Warn(category, $"could not put {moved[i].To} back as {moved[i].From}: {back.Message}");
                }
            }

            DiagnosticLog.Warn(category, NotMovedLogLine(oldPath, newPath, ex.Message));
            return oldPath;
        }
    }

    /// <summary>Logged once a store moved. Pinned.</summary>
    public static string MovedLogLine(string oldPath, string newPath) => $"moved {oldPath} to {newPath}, its folder since 2026-10-10";

    /// <summary>Logged when the move failed and the old file is read where it is. Pinned.</summary>
    public static string NotMovedLogLine(string oldPath, string newPath, string reason) =>
        $"could not move {oldPath} to {newPath} ({reason}); reading it where it is, and trying again at the next start";
}
