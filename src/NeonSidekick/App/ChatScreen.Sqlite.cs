using NeonSidekick.Sqlite;

namespace NeonSidekick.App;

// ── SQLite changes (2026-10-05) ───────────────────────────────────────────

internal sealed partial class ChatScreen
{
    /// <summary>The database files "Allow for this session" was picked for, by full path; forgotten with the session (<see cref="ForgetSession"/>).</summary>
    private readonly HashSet<string> _sqliteAllowed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// <c>sqlite_execute</c>'s allow (2026-10-05, the user's pick: the screen capture's way), on the turn task: a file allowed for
    /// the session runs at once; else the camera's allow pane — Deny, Allow once, Allow for this session — titled
    /// <see cref="SqliteText.AllowTitle"/>, its caption the file and the statement. Null when no pane could ask.
    /// </summary>
    private async Task<bool?> AllowSqliteWriteAsync(SqliteTarget target, string sql, bool creating, CancellationToken turnToken)
    {
        if (!_pane.Enabled)
        {
            return null;
        }

        lock (_sqliteAllowed)
        {
            if (_sqliteAllowed.Contains(target.FullPath))
            {
                return true;
            }
        }

        var allow = await RunCameraPaneAsync(token => _cameraMenu.AllowAsync(SqliteText.AllowCaption(target, sql, creating), token, SqliteText.AllowTitle), null, turnToken).ConfigureAwait(false);
        if (allow is not { } choice)
        {
            return null;
        }

        if (choice == CameraAllow.Session)
        {
            lock (_sqliteAllowed)
            {
                _sqliteAllowed.Add(target.FullPath);
            }
        }

        return choice != CameraAllow.Deny;
    }
}
