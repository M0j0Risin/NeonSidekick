using Microsoft.Data.Sqlite;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.Sessions;

/// <summary>One stored input line, as <see cref="SessionStore.AppendCommand"/> wrote it (2026-11-25, the private-conversations plan): its row id, its stamp and its text.</summary>
/// <param name="Id">The row id. Never enough alone: <c>command_history</c>'s id has no AUTOINCREMENT, so ids come back after <c>/msgclear</c> or a trim.</param>
/// <param name="At">The row's stamp, as stored.</param>
/// <param name="Text">The line.</param>
public readonly record struct CommandRow(long Id, string At, string Text);

/// <summary>What <see cref="SessionStore.DeleteForPrivate"/> did.</summary>
public enum PrivateDeleteOutcome
{
    /// <summary>The row and the lines are gone.</summary>
    Deleted,

    /// <summary>The row was gone already (another window's delete); the lines named are gone.</summary>
    Gone,

    /// <summary>Nothing changed: the delete failed (or the store isn't there), and the row and the lines are where they were.</summary>
    Failed,
}

/// <summary>
/// The store's side of a private conversation (2026-11-25, the private-conversations plan): the input lines it stored before the switch, known by
/// id, stamp and text, and the switch's delete — the row, its turns, its search index and those lines in one transaction, with
/// <c>PRAGMA secure_delete</c> on and the full-text index rebuilt (FTS5 keeps deleted terms in its segments until a merge, and its own
/// <c>secure-delete</c> option would leave the file unreadable to SQLite older than 3.42), then a <c>VACUUM</c> (each turn rewrites the history JSON,
/// so earlier copies sit in free pages a delete-time pragma never reaches: 4,615 marker hits without it, 0 with it, on SQLite 3.50.4, 2026-11-25)
/// and a <c>wal_checkpoint(TRUNCATE)</c>. Another window holding the file can make those last two busy: each waits <see cref="TidyBusyMs"/> at most
/// (the driver's 30 s would hold the store's lock), and one that fails is owed, tried again by <see cref="RetryPrivateTidy"/> at the end and at exit.
/// </summary>
public sealed partial class SessionStore
{
    /// <summary>The busy wait of the <c>VACUUM</c> and the checkpoint, in milliseconds.</summary>
    public const int TidyBusyMs = 2000;

    /// <summary>Whether a <c>VACUUM</c> or checkpoint after a private delete failed and is owed.</summary>
    private bool _tidyOwed;

    /// <summary>Whether a tidy after a private delete is still owed. For the tests.</summary>
    public bool PrivateTidyOwed
    {
        get
        {
            lock (_gate)
            {
                return _tidyOwed;
            }
        }
    }

    /// <summary>
    /// One sent line appended, the oldest past <see cref="CommandHistoryCap"/> dropped; the row written, or null when nothing was (a repeat of the last
    /// line, an empty line, the store unavailable). The caller de-duplicates (<c>InputLine.Remember</c>). Never a stale <c>last_insert_rowid()</c>.
    /// </summary>
    public CommandRow? AppendCommand(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            return null;
        }

        lock (_gate)
        {
            if (Open() is not { } connection)
            {
                return null;
            }

            try
            {
                using var transaction = connection.BeginTransaction();
                using (var tail = connection.CreateCommand())
                {
                    tail.Transaction = transaction;
                    tail.CommandText = "SELECT text FROM command_history ORDER BY id DESC LIMIT 1";
                    if (string.Equals(tail.ExecuteScalar() as string, text, StringComparison.Ordinal))
                    {
                        return null;
                    }
                }

                string at = Stamp(_time.GetUtcNow());
                long id;
                using (var insert = connection.CreateCommand())
                {
                    insert.Transaction = transaction;
                    insert.CommandText = "INSERT INTO command_history(at, text) VALUES ($at, $text); SELECT last_insert_rowid();";
                    insert.Parameters.AddWithValue("$at", at);
                    insert.Parameters.AddWithValue("$text", text);
                    id = (long)insert.ExecuteScalar()!;
                }

                TrimCommandHistory(connection, transaction);
                transaction.Commit();
                return new CommandRow(id, at, text);
            }
            catch (SqliteException ex)
            {
                Fail("write the message history", ex);
                return null;
            }
        }
    }

    /// <summary>
    /// The switch to private's delete: session <paramref name="sessionId"/> (none when null) with its reflections, sources and turns, and every row of
    /// <paramref name="lines"/> whose id, stamp and text all still match, in one transaction under <c>secure_delete</c>, the search index rebuilt
    /// inside it; then the <c>VACUUM</c> and the checkpoint. <see cref="PrivateDeleteOutcome.Gone"/> when the row was gone already;
    /// <see cref="PrivateDeleteOutcome.Failed"/> leaves everything. A delete that had nothing to delete leaves the file alone. Raises
    /// <see cref="Purged"/> for the row, so its file checkpoints go.
    /// </summary>
    public PrivateDeleteOutcome DeleteForPrivate(long? sessionId, IReadOnlyList<CommandRow> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        bool gone = false;
        bool changed = false;
        lock (_gate)
        {
            Interlocked.Increment(ref _version);
            if (Open() is not { } connection)
            {
                return PrivateDeleteOutcome.Failed;
            }

            try
            {
                Exec(connection, null, "PRAGMA secure_delete = ON");
                using var transaction = connection.BeginTransaction();
                if (sessionId is { } id)
                {
                    using var exists = connection.CreateCommand();
                    exists.Transaction = transaction;
                    exists.CommandText = "SELECT count(*) FROM sessions WHERE id = $id";
                    exists.Parameters.AddWithValue("$id", id);
                    gone = (long)exists.ExecuteScalar()! == 0;
                    foreach (string sql in new[]
                    {
                        "DELETE FROM reflections WHERE session_id = $id",
                        "DELETE FROM turn_sources WHERE turn_id IN (SELECT id FROM turns WHERE session_id = $id)",
                        "DELETE FROM turns WHERE session_id = $id",
                        "DELETE FROM sessions WHERE id = $id",
                    })
                    {
                        using var delete = connection.CreateCommand();
                        delete.Transaction = transaction;
                        delete.CommandText = sql;
                        delete.Parameters.AddWithValue("$id", id);
                        changed |= delete.ExecuteNonQuery() > 0;
                    }
                }

                using (var line = connection.CreateCommand())
                {
                    line.Transaction = transaction;
                    line.CommandText = "DELETE FROM command_history WHERE id = $id AND at = $at AND text = $text";
                    var rowId = line.Parameters.Add("$id", SqliteType.Integer);
                    var at = line.Parameters.Add("$at", SqliteType.Text);
                    var text = line.Parameters.Add("$text", SqliteType.Text);
                    foreach (var row in lines)
                    {
                        rowId.Value = row.Id;
                        at.Value = row.At;
                        text.Value = row.Text;
                        changed |= line.ExecuteNonQuery() > 0;
                    }
                }

                if (changed)
                {
                    Exec(connection, transaction, "INSERT INTO turns_fts(turns_fts) VALUES('rebuild')");
                }

                transaction.Commit();
            }
            catch (SqliteException ex)
            {
                Fail("delete the conversation going private", ex);
                return PrivateDeleteOutcome.Failed;
            }
            finally
            {
                TryExec(connection, "PRAGMA secure_delete = OFF");
            }

            if (changed)
            {
                _tidyOwed = true;
                Tidy(connection);
            }
        }

        if (sessionId is { } purged && !gone)
        {
            DiagnosticLog.Info(Category, PurgedLogLine(purged));
            RaisePurged([purged]);
        }

        return gone ? PrivateDeleteOutcome.Gone : PrivateDeleteOutcome.Deleted;
    }

    /// <summary>A <c>VACUUM</c> and checkpoint still owed after a private delete, tried again (the end's, and the exit's). Nothing when none is.</summary>
    public void RetryPrivateTidy()
    {
        lock (_gate)
        {
            if (_tidyOwed && Open() is { } connection)
            {
                Tidy(connection);
            }
        }
    }

    /// <summary>The owed <c>VACUUM</c> then <c>wal_checkpoint(TRUNCATE)</c>, each under a <see cref="TidyBusyMs"/> busy wait. Caller holds the lock.</summary>
    private void Tidy(SqliteConnection connection)
    {
        try
        {
            Exec(connection, null, "PRAGMA busy_timeout = " + TidyBusyMs.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Exec(connection, null, "VACUUM");
            using var checkpoint = connection.CreateCommand();
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
            using var reader = checkpoint.ExecuteReader();
            // The first column is 1 when the checkpoint couldn't finish (another reader or writer held it).
            bool busy = reader.Read() && reader.GetInt64(0) != 0;
            _tidyOwed = busy;
            if (busy)
            {
                DiagnosticLog.Warn(Category, TidyFailedWarning("another window holds the file"));
            }
        }
        catch (SqliteException ex)
        {
            DiagnosticLog.Warn(Category, TidyFailedWarning(ex.Message));
        }
        finally
        {
            TryExec(connection, "PRAGMA busy_timeout = 0");
        }
    }

    /// <summary>The warning when the tidy after a private delete couldn't finish; the row is gone all the same. Pinned.</summary>
    public static string TidyFailedWarning(string reason) =>
        $"The private delete's cleanup of {FileName} didn't finish ({reason}); the deleted text may sit in its free space until it is tried again.";

    private static void Exec(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void TryExec(SqliteConnection connection, string sql)
    {
        try
        {
            Exec(connection, null, sql);
        }
        catch (SqliteException)
        {
            // A pragma put back; the next open sets its own.
        }
    }
}
