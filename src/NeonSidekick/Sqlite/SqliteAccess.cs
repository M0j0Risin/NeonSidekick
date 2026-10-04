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
/// calls <c>sqlite3_interrupt</c> on the connection (thread-safe), which ends it with <c>SQLITE_INTERRUPT</c>.
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
    public SqliteTarget? Resolve(string? database, string? defaultName, out SqlRun? refused)
    {
        refused = null;
        var catalog = Catalog();
        var sandbox = _sandbox();
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
    public static SqlRun Run(SqliteTarget target, IReadOnlyList<string> statements, IReadOnlyList<SqlParameterValue> parameters, int maxRows, int timeoutSeconds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!File.Exists(target.FullPath))
        {
            return SqlRun.Refused(SqlOutcome.ConnectFailed, SqliteText.NoFile(target.FullPath), target.Name);
        }

        var builder = new SqliteConnectionStringBuilder { DataSource = target.FullPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
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

        using var timer = new Timer(_ => Interrupt(timer: true), null, TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)), Timeout.InfiniteTimeSpan);
        using var registration = cancellationToken.Register(() => Interrupt(timer: false));
        try
        {
            using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA query_only = ON";
                pragma.ExecuteNonQuery();
            }

            using var transaction = connection.BeginTransaction(deferred: true);
            try
            {
                foreach (string sql in statements)
                {
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = sql;
                    // A placeholder params does not name binds as NULL, PostgresAccess's way (the 2026-10-04 review): left unbound,
                    // Microsoft.Data.Sqlite threw an InvalidOperationException past the SqliteException catches below.
                    foreach (string bind in SqliteReadOnlyGate.Binds(sql))
                    {
                        var value = parameters.FirstOrDefault(p => string.Equals(p.Name, bind[1..], StringComparison.OrdinalIgnoreCase));
                        command.Parameters.Add(Bind(bind, value?.Value));
                    }

                    using var reader = command.ExecuteReader();
                    grids.Add(SqlGrid.ReadAsync(reader, maxRows, CancellationToken.None, ReadCell).GetAwaiter().GetResult());
                }
            }
            finally
            {
                try
                {
                    transaction.Rollback();
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

        return new SqlRun(SqlOutcome.Ok, "", target.Name, "", grids, watch.Elapsed);
    }

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
