using NeonSidekick.DuckDb;

namespace NeonSidekick.App;

// ── DuckDB changes (2026-10-25) ───────────────────────────────────────────

internal sealed partial class ChatScreen
{
    /// <summary>
    /// The DuckDB files "Allow for this session" was picked for, by <see cref="DuckDbAllowKey"/>; forgotten with the session
    /// (<see cref="ForgetSession"/>, <see cref="ForgetAllows"/>), as SQLite's are.
    /// </summary>
    private readonly HashSet<string> _duckDbAllowed = new(StringComparer.Ordinal);

    /// <summary>A DuckDB file's session-allow key: <see cref="SqliteAllowKey"/>'s rule (the full path, case-folded only where its folder ignores case).</summary>
    public static string DuckDbAllowKey(string fullPath) => SqliteAllowKey(fullPath);

    /// <summary>
    /// <c>duckdb_execute</c>'s allow, <see cref="AllowSqliteWriteAsync"/>'s twin: a file allowed for the session runs at once; else the
    /// camera's allow pane titled <see cref="DuckDbText.AllowTitle"/>, its caption the file and the statement as the model wrote it.
    /// Null when no pane could ask.
    /// </summary>
    private async Task<bool?> AllowDuckDbWriteAsync(DuckDbTarget target, string sql, bool creating, IReadOnlyList<Sql.SqlParameterValue> parameters, CancellationToken turnToken)
    {
        if (!_pane.Enabled)
        {
            return null;
        }

        string key = DuckDbAllowKey(target.FullPath);
        lock (_duckDbAllowed)
        {
            if (_duckDbAllowed.Contains(key))
            {
                return true;
            }
        }

        var whole = Shell.ShellText.WholeCommandLines(new Shell.CommandRequest("sql", Files.NameScrub.Display(Sql.ServerWriteText.Lines(sql), keepLineBreaks: true) + Sql.ServerWriteText.Bound(parameters), [], IsScript: true));
        string caption = DuckDbText.AllowCaption(target, sql, creating, parameters);
        var allow = await AllowBothAsync(DuckDbText.AllowTitle, caption, whole, token => _cameraMenu.AllowAsync(caption, token, DuckDbText.AllowTitle, DuckDbText.AllowTitle, whole), turnToken).ConfigureAwait(false);
        if (allow is not { } choice)
        {
            return null;
        }

        if (choice == CameraAllow.Session)
        {
            lock (_duckDbAllowed)
            {
                _duckDbAllowed.Add(key);
            }
        }

        return choice != CameraAllow.Deny;
    }
}
