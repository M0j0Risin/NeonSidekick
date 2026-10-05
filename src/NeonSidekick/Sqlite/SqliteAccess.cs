using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;
using NeonSidekick.Diagnostics;
using NeonSidekick.Files;
using NeonSidekick.Sql;

namespace NeonSidekick.Sqlite;

/// <summary>A database a call works on: the name it goes by (a <c>sqlite.json</c> name, or the path as given) and its file.</summary>
public sealed record SqliteTarget(string Name, string FullPath);

/// <summary>
/// The SQLite tools' one door (2026-10-04), <c>MySqlAccess</c>'s shape over Microsoft.Data.Sqlite (already the session store's, so
/// no new package and no new native library: the bundled e_sqlite3 is smoke-probed): the databases read afresh from <c>sqlite.json</c>
/// at every call, and — with <c>SQLite sandbox files</c> on — any file in the working directory by its path, resolved through
/// <see cref="WorkingDirectory"/> (outside it is refused as every file tool refuses it). Read-only by four layers: the tools'
/// <see cref="SqliteReadOnlyGate"/>; the file opened <c>Mode=ReadOnly</c> without pooling (the <c>SessionStore</c> way); <c>PRAGMA
/// query_only = ON</c>, so even a statement that got past the gate cannot write; and a deferred transaction always rolled back.
/// Microsoft.Data.Sqlite cannot cancel a running statement, so the call runs on a pool thread and a timer — or the turn's ESC —
/// calls <c>sqlite3_interrupt</c> on the connection (thread-safe), which ends it with <c>SQLITE_INTERRUPT</c>. Since 2026-10-05
/// (<c>SQLite protection mode</c> <c>read-write</c>) <see cref="Execute"/> is the one way to change a file: the same machinery
/// with the file opened read-write and no layer of the four, behind <see cref="SqliteWriteGate"/> and the user's allow.
/// </summary>
public sealed class SqliteAccess
{
    /// <summary>The rows a catalog listing shows at most.</summary>
    public const int MaxCatalogRows = 1000;

    /// <summary>SQLite's result code for a statement <c>sqlite3_interrupt</c> stopped.</summary>
    public const int Interrupted = 9;

    private readonly Func<SqliteCatalog> _catalog;
    private readonly Func<WorkingDirectory?> _sandbox;

    /// <param name="catalog">The databases in force; the app's reads the profile's and the home's <c>sqlite.json</c> at every call.</param>
    /// <param name="sandbox">The working directory while <c>SQLite sandbox files</c> is on; null while it is off.</param>
    public SqliteAccess(Func<SqliteCatalog> catalog, Func<WorkingDirectory?> sandbox)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _sandbox = sandbox ?? throw new ArgumentNullException(nameof(sandbox));
    }

    public SqliteCatalog Catalog() => _catalog();

    /// <summary>Whether a file in the working directory may be named by its path now.</summary>
    public bool SandboxOn => _sandbox() is not null;

    /// <summary>
    /// The database a call means: <paramref name="database"/> as a <c>sqlite.json</c> name (case-insensitive, it wins), else as a
    /// path in the working directory (sandbox on); blank, <paramref name="defaultName"/>, else the first named. Null with the refusal.
    /// </summary>
    public SqliteTarget? Resolve(string? database, string? defaultName, out SqlRun? refused) => Resolve(database, defaultName, create: false, out refused, out _);

    /// <summary>The file extensions a new database's file may have (2026-10-05, <c>sqlite_execute</c>'s <c>create</c>).</summary>
    public static readonly IReadOnlySet<string> CreateExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".db", ".sqlite", ".sqlite3", ".db3" };

    /// <summary>
    /// <see cref="Resolve(string?, string?, out SqlRun?)"/>, and with <paramref name="create"/> (2026-10-05, the user's ask) a path in
    /// the working directory that names no file yet is a new database's: the sandbox on, the name ending one of
    /// <see cref="CreateExtensions"/>, its folder already there; <paramref name="creating"/> says it is to be made. A
    /// <c>sqlite.json</c> name still wins and is never made, and a file already there is simply opened.
    /// </summary>
    public SqliteTarget? Resolve(string? database, string? defaultName, bool create, out SqlRun? refused, out bool creating)
    {
        refused = null;
        creating = false;
        var catalog = Catalog();
        var sandbox = _sandbox();
        if (create && string.IsNullOrWhiteSpace(database))
        {
            refused = SqlRun.Refused(SqlOutcome.UnknownConnection, SqliteText.CreateNeedsPath);
            return null;
        }

        if (string.IsNullOrWhiteSpace(database))
        {
            var chosen = catalog.Named(defaultName) ?? (catalog.Databases.Count > 0 ? catalog.Databases[0] : null);
            if (chosen is null)
            {
                refused = SqlRun.Refused(SqlOutcome.NoConnections, "");
                return null;
            }

            return new SqliteTarget(chosen.Name, chosen.FullPath);
        }

        string wanted = database.Trim().Trim('"');
        if (catalog.Named(wanted) is { } named)
        {
            return new SqliteTarget(named.Name, named.FullPath);
        }

        if (sandbox is not null)
        {
            var outcome = sandbox.Resolve(wanted, forWrite: false, out string full);
            if (outcome == FileOutcome.Ok && File.Exists(full))
            {
                return new SqliteTarget(wanted.Replace('\\', '/'), full);
            }

            if (outcome is FileOutcome.OutsideRoot)
            {
                refused = SqlRun.Refused(SqlOutcome.ConnectFailed, SqliteText.OutsideSandbox(wanted), wanted);
                return null;
            }

            if (create && outcome == FileOutcome.Ok)
            {
                if (!CreateExtensions.Contains(Path.GetExtension(full)) || Directory.Exists(full))
                {
                    refused = SqlRun.Refused(SqlOutcome.UnknownConnection, SqliteText.BadExtension(wanted), wanted);
                    return null;
                }

                if (!Directory.Exists(Path.GetDirectoryName(full)))
                {
                    refused = SqlRun.Refused(SqlOutcome.UnknownConnection, SqliteText.NoFolder(wanted), wanted);
                    return null;
                }

                creating = true;
                return new SqliteTarget(wanted.Replace('\\', '/'), full);
            }
        }
        else if (create)
        {
            refused = SqlRun.Refused(SqlOutcome.UnknownConnection, SqliteText.CreateNeedsSandbox, wanted);
            return null;
        }

        refused = SqlRun.Refused(SqlOutcome.UnknownConnection, SqliteText.UnknownDatabase(wanted, string.Join(", ", catalog.Databases.Select(d => d.Name)), sandbox is not null), wanted);
        return null;
    }

    /// <summary>
    /// Runs <paramref name="statements"/> on the database <paramref name="database"/> means: each one's rows read to
    /// <paramref name="maxRows"/> (one past to learn whether more are left), each bound only the placeholders it names (one params
    /// does not give as NULL), all inside
    /// one transaction that is rolled back. A timeout, a failed open and an SQLite error are outcomes; a cancelled token throws.
    /// </summary>
    public async Task<SqlRun> RunAsync(string? database, string? defaultName, IReadOnlyList<string> statements, IReadOnlyList<SqlParameterValue> parameters, int maxRows, int timeoutSeconds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(statements);
        ArgumentNullException.ThrowIfNull(parameters);
        if (Resolve(database, defaultName, out var refused) is not { } target)
        {
            return refused!;
        }

        return await Task.Run(() => Run(target, statements, parameters, maxRows, timeoutSeconds, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The run itself, on the calling thread: <see cref="RunAsync"/> puts it on the pool.</summary>
    public static SqlRun Run(SqliteTarget target, IReadOnlyList<string> statements, IReadOnlyList<SqlParameterValue> parameters, int maxRows, int timeoutSeconds, CancellationToken cancellationToken) =>
        Run(target, statements, parameters, maxRows, timeoutSeconds, write: false, create: false, cancellationToken);

    /// <summary><see cref="Execute"/> on the pool.</summary>
    public static Task<SqlRun> ExecuteAsync(SqliteTarget target, bool create, string sql, IReadOnlyList<SqlParameterValue> parameters, int maxRows, int timeoutSeconds, CancellationToken cancellationToken) =>
        Task.Run(() => Execute(target, create, sql, parameters, maxRows, timeoutSeconds, cancellationToken), cancellationToken);

    /// <summary>
    /// <c>sqlite_execute</c>'s run (2026-10-05, <c>SQLite protection mode</c> <c>read-write</c>): <paramref name="sql"/>, already
    /// passed by <see cref="SqliteWriteGate"/> and allowed by the user, on the file opened read-write (<paramref name="create"/>:
    /// made when missing) with no <c>query_only</c> and no transaction of the app's — one statement commits on its own, atomically,
    /// and SQLite refuses VACUUM and some PRAGMAs inside a transaction. Its rows (a RETURNING, a PRAGMA's answer) are read as a
    /// query's, <see cref="SqlRun.Changes"/> is <c>sqlite3_changes</c>, and the timeout and ESC interrupt it as they do a query
    /// (an interrupted statement is rolled back by SQLite). A file this call made is taken away again when its statement fails, so
    /// a refused create leaves nothing behind.
    /// </summary>
    public static SqlRun Execute(SqliteTarget target, bool create, string sql, IReadOnlyList<SqlParameterValue> parameters, int maxRows, int timeoutSeconds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(sql);
        bool made = create && !File.Exists(target.FullPath);
        bool done = false;
        try
        {
            var run = Run(target, [sql], parameters, maxRows, timeoutSeconds, write: true, create, cancellationToken);
            done = run.Outcome == SqlOutcome.Ok;
            return run;
        }
        finally
        {
            if (made && !done)
            {
                Unmake(target.FullPath);
            }
        }
    }

    /// <summary>A database file a failed create made, and its journal, taken away; a file that will not go stays, logged.</summary>
    private static void Unmake(string path)
    {
        foreach (string file in new[] { path, path + "-journal", path + "-wal", path + "-shm" })
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                DiagnosticLog.Warn(SqliteConfigFile.Category, SqliteText.UnmakeFailedLogLine(file, LogText.Excerpt(ex.Message)));
            }
        }
    }

    /// <summary>
    /// The run: <paramref name="write"/> false is <c>sqlite_query</c>'s four layers (read-only open, <c>query_only</c>, a deferred
    /// transaction rolled back); true is <see cref="Execute"/>'s read-write open with neither.
    /// </summary>
    private static SqlRun Run(SqliteTarget target, IReadOnlyList<string> statements, IReadOnlyList<SqlParameterValue> parameters, int maxRows, int timeoutSeconds, bool write, bool create, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!(write && create) && !File.Exists(target.FullPath))
        {
            return SqlRun.Refused(SqlOutcome.ConnectFailed, SqliteText.NoFile(target.FullPath), target.Name);
        }

        var mode = !write ? SqliteOpenMode.ReadOnly : create ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite;
        var builder = new SqliteConnectionStringBuilder { DataSource = target.FullPath, Mode = mode, Pooling = false };
        using var connection = new SqliteConnection(builder.ConnectionString);
        try
        {
            connection.Open();
        }
        catch (SqliteException ex)
        {
            DiagnosticLog.Warn(SqliteConfigFile.Category, SqliteText.OpenFailedLogLine(target.Name, LogText.Excerpt(ex.Message)));
            return SqlRun.Refused(SqlOutcome.ConnectFailed, Message(ex), target.Name);
        }

        var watch = Stopwatch.StartNew();
        var grids = new List<SqlGrid>();
        var gate = new object();
        bool open = true, timedOut = false;
        void Interrupt(bool timer)
        {
            lock (gate)
            {
                if (open)
                {
                    timedOut |= timer;
                    SQLitePCL.raw.sqlite3_interrupt(connection.Handle);
                }
            }
        }

        // Past the deadline, or once cancelled, the timer goes on interrupting until the run ends (the third 2026-10-04 review):
        // SQLite drops an interrupt that lands while no statement runs, between two statements or before the first, so a one-shot
        // left the rest with no deadline. The loop's checks below end the run between statements at once.
        var deadline = TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds));
        using var timer = new Timer(_ =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                Interrupt(timer: false);
            }
            else if (watch.Elapsed >= deadline)
            {
                Interrupt(timer: true);
            }
        }, null, InterruptRepeat, InterruptRepeat);
        using var registration = cancellationToken.Register(() => Interrupt(timer: false));
        int? changes = null;
        try
        {
            if (!write)
            {
                using var pragma = connection.CreateCommand();
                pragma.CommandText = "PRAGMA query_only = ON";
                pragma.ExecuteNonQuery();
            }

            using var transaction = write ? null : connection.BeginTransaction(deferred: true);
            try
            {
                foreach (string sql in statements)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    lock (gate)
                    {
                        if (timedOut)
                        {
                            return new SqlRun(SqlOutcome.Timeout, timeoutSeconds.ToString(CultureInfo.InvariantCulture), target.Name, "", grids, watch.Elapsed);
                        }
                    }

                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = sql;
                    // A placeholder params does not name binds as NULL, PostgresAccess's way (the 2026-10-04 review): left unbound,
                    // Microsoft.Data.Sqlite threw an InvalidOperationException past the SqliteException catches below. Each
                    // placeholder takes the params name in its own case first, else any case (:id and :ID are two to SQLite). The
                    // name is everything after the mark, a TCL form's :: or (…) included: params names $a(1) as "a(1)"
                    // (SqlQueryTool.ReadParameters with SqliteReadOnlyGate.ParamName, the fourth 2026-10-04 review).
                    foreach (string bind in SqliteReadOnlyGate.Binds(sql))
                    {
                        string name = bind[1..];
                        var value = parameters.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal))
                            ?? parameters.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
                        command.Parameters.Add(Bind(bind, value?.Value));
                    }

                    using (var reader = command.ExecuteReader())
                    {
                        grids.Add(SqlGrid.ReadAsync(reader, maxRows, CancellationToken.None, ReadCell).GetAwaiter().GetResult());
                    }

                    // A write's count once its reader is done (2026-10-05): a RETURNING made every change at its first step.
                    changes = write ? SQLitePCL.raw.sqlite3_changes(connection.Handle) : null;
                }
            }
            finally
            {
                // No interrupt from here on (the fourth 2026-10-04 review): the timer repeats past the deadline, and an interrupt landing
                // on the rollback threw into the catch below and skipped it on exactly the timeout path.
                lock (gate)
                {
                    open = false;
                }

                try
                {
                    transaction?.Rollback();
                }
                catch (Exception ex) when (ex is SqliteException or InvalidOperationException)
                {
                    // Interrupted mid-statement, the transaction may be gone already: there is nothing left to undo.
                }
            }
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == Interrupted && cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(ex.Message, ex, cancellationToken);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == Interrupted && timedOut)
        {
            return new SqlRun(SqlOutcome.Timeout, timeoutSeconds.ToString(CultureInfo.InvariantCulture), target.Name, "", grids, watch.Elapsed);
        }
        catch (SqliteException ex)
        {
            return new SqlRun(SqlOutcome.Failed, Message(ex), target.Name, "", grids, watch.Elapsed);
        }
        finally
        {
            lock (gate)
            {
                open = false;
            }
        }

        return new SqlRun(SqlOutcome.Ok, "", target.Name, "", grids, watch.Elapsed) { Changes = changes };
    }

    /// <summary>How often the run's timer looks at the deadline and the token, and interrupts again once either is past.</summary>
    private static readonly TimeSpan InterruptRepeat = TimeSpan.FromMilliseconds(100);

    /// <summary>A placeholder bound as written (<c>@id</c>, <c>:id</c>, <c>$id</c>): text, whole numbers, floats; a decimal as a float and a boolean as 1 or 0, SQLite's own types.</summary>
    public static SqliteParameter Bind(string name, object? value) => new(name, value switch
    {
        null => DBNull.Value,
        bool b => b ? 1L : 0L,
        decimal d => (double)d,
        int i => (long)i,
        var other => other,
    });

    /// <summary>One cell as text: SQLite's dynamic types (integer, real, text, blob) through <see cref="SqlText.Cell"/>.</summary>
    private static string ReadCell(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? SqlText.Null : SqlText.Cell(reader.GetValue(ordinal));

    /// <summary>SQLite's message without the driver's prefix: <c>no such column: x</c> is what the model needs to fix the text.</summary>
    public static string Message(SqliteException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        string text = ex.Message.ReplaceLineEndings(" ");
        const string prefix = "SQLite Error ";
        if (text.StartsWith(prefix, StringComparison.Ordinal) && text.IndexOf(": ", StringComparison.Ordinal) is int at and > 0)
        {
            text = text[(at + 2)..];
        }

        return LogText.Excerpt(text.Trim('\'', ' ', '.'), 600);
    }
}
