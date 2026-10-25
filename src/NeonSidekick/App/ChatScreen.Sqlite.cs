using NeonSidekick.Sqlite;

namespace NeonSidekick.App;

// ── SQLite changes (2026-10-05) ───────────────────────────────────────────

internal sealed partial class ChatScreen
{
    /// <summary>
    /// The database files "Allow for this session" was picked for, by <see cref="SqliteAllowKey"/>; forgotten with the session
    /// (<see cref="ForgetSession"/>, <see cref="ForgetAllows"/>).
    /// </summary>
    private readonly HashSet<string> _sqliteAllowed = new(StringComparer.Ordinal);

    /// <summary>
    /// A SQLite file's session-allow key (2026-10-24, the database review's I10, decision DB6): its full path, case-folded only when its folder
    /// ignores case (<see cref="Files.CaseRule"/>), so on a case-sensitive volume <c>test.db</c>'s allow does not cover <c>Test.db</c>, and one set
    /// holds keys from folders with either rule.
    /// </summary>
    public static string SqliteAllowKey(string fullPath)
    {
        ArgumentNullException.ThrowIfNull(fullPath);
        string folder = Path.GetDirectoryName(fullPath) ?? fullPath;
        return Files.CaseRule.IsSensitive(folder) ? fullPath : fullPath.ToUpperInvariant();
    }

    /// <summary>
    /// <c>sqlite_execute</c>'s allow (2026-10-05, the user's pick: the screen capture's way), on the turn task: a file allowed for
    /// the session runs at once; else the camera's allow pane — Deny, Allow once, Allow for this session — titled
    /// <see cref="SqliteText.AllowTitle"/>, its caption the file and the statement. Null when no pane could ask.
    /// </summary>
    private async Task<bool?> AllowSqliteWriteAsync(SqliteTarget target, string sql, bool creating, IReadOnlyList<Sql.SqlParameterValue> parameters, CancellationToken turnToken)
    {
        if (!_pane.Enabled)
        {
            return null;
        }

        string key = SqliteAllowKey(target.FullPath);
        lock (_sqliteAllowed)
        {
            if (_sqliteAllowed.Contains(key))
            {
                return true;
            }
        }

        var whole = Shell.ShellText.WholeCommandLines(new Shell.CommandRequest("sql", Files.NameScrub.Display(Sql.ServerWriteText.Lines(sql), keepLineBreaks: true) + Sql.ServerWriteText.Bound(parameters), [], IsScript: true));
        string caption = SqliteText.AllowCaption(target, sql, creating, parameters);
        var allow = await AllowBothAsync(SqliteText.AllowTitle, caption, whole, token => _cameraMenu.AllowAsync(caption, token, SqliteText.AllowTitle, SqliteText.AllowTitle, whole), turnToken).ConfigureAwait(false);
        if (allow is not { } choice)
        {
            return null;
        }

        if (choice == CameraAllow.Session)
        {
            lock (_sqliteAllowed)
            {
                _sqliteAllowed.Add(key);
            }
        }

        return choice != CameraAllow.Deny;
    }
}
