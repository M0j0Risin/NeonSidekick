using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using DuckDB.NET.Data;
using NeonSidekick.Diagnostics;
using NeonSidekick.Files;
using NeonSidekick.Sql;

namespace NeonSidekick.DuckDb;

/// <summary>
/// A database a call works on: the name it goes by (a <c>duckdb.json</c> name, the path as given, or <see cref="DuckDbText.MemoryName"/>) and
/// its file, or <see cref="MemoryPath"/> for an in-memory database that only reads data files.
/// </summary>
public sealed record DuckDbTarget(string Name, string FullPath)
{
    /// <summary>DuckDB's name for a database in memory.</summary>
    public const string MemoryPath = ":memory:";

    /// <summary>The in-memory database a call with no <c>database</c> gets while data files may be read and nothing is named.</summary>
    public static DuckDbTarget Memory { get; } = new(DuckDbText.MemoryName, MemoryPath);

    public bool InMemory => FullPath == MemoryPath;
}

/// <summary>What a file's first bytes say it is (<see cref="DuckDbAccess.Sniff"/>).</summary>
public enum DuckDbFileKind
{
    /// <summary>A DuckDB database (<c>DUCK</c> at offset 8).</summary>
    DuckDb,

    /// <summary>A SQLite database: the SQLite tools read it.</summary>
    Sqlite,

    /// <summary>A file of 0 bytes: DuckDB opens none.</summary>
    Empty,

    /// <summary>Anything else.</summary>
    Other,

    /// <summary>Not read (held by another program, or gone): the open says why.</summary>
    Unread,
}

/// <summary>
/// The layers under a DuckDB read past the gate and the path layer (SQLite's S14 shape): every read takes them all; a test leaves one in at a
/// time to prove each alone.
/// </summary>
[Flags]
public enum DuckDbReadLayers
{
    None = 0,

    /// <summary>The file opened <c>access_mode=READ_ONLY</c> (never an in-memory database: DuckDB refuses one).</summary>
    ReadOnlyOpen = 1,

    /// <summary>
    /// The locked configuration, read back before the statement: outside access off with the working directory the only folder (none while
    /// sandbox files is off), no extension installed or loaded, no community extension, no persistent secret, the memory and spill caps, the
    /// configuration locked.
    /// </summary>
    Locked = 2,

    /// <summary>A transaction, always rolled back.</summary>
    RolledBack = 4,

    All = ReadOnlyOpen | Locked | RolledBack,
}

/// <summary>
/// The DuckDB tools' one door (2026-10-25), <c>SqliteAccess</c>'s twin over DuckDB.NET (<c>DuckDB.NET.Data.Full</c>, in-process, its native
/// <c>duckdb</c> smoke-probed): the databases read from <c>duckdb.json</c> at every call, and — with <c>DuckDB sandbox files</c> on — any DuckDB
/// file in the working directory by its path (resolved through <see cref="WorkingDirectory"/>), or an in-memory database for data files alone.
/// Read-only by layers: the tools' <see cref="DuckDbReadOnlyGate"/> and <see cref="DuckDbFilePaths"/>; the file opened read-only; the locked
/// configuration (<see cref="DuckDbReadLayers.Locked"/>), whose folder rule holds every file DuckDB opens — a view's stored path too — to the
/// working directory; and a transaction always rolled back. Every value is read as DuckDB writes it (<c>SELECT COLUMNS(*)::VARCHAR FROM (…)</c>,
/// the spike's: native nested and out-of-range values threw, under AOT too). Opened and closed per call on a pool thread; one open per file
/// at a time in the process (DuckDB.NET shares one instance per file, the spike's: a second open got the first one's locked instance).
/// A timer or the turn's Esc interrupts a running statement. <see cref="Execute"/> is the one way to change a file, opened read-write under
/// the same locked configuration, behind <see cref="DuckDbWriteGate"/> and the user's allow. Fixed caps (the user's call, not settings): a
/// quarter of the machine's memory and 10 GB of spill, in a folder of this process's own under the temp folder (<see cref="SpillRoot"/>).
/// </summary>
public sealed class DuckDbAccess
{
    /// <summary>The rows a catalog listing shows at most.</summary>
    public const int MaxCatalogRows = 1000;

    /// <summary>DuckDB's native library on Windows, at the binary's root (the smoke's list).</summary>
    public const string NativeLibraryFileName = "duckdb.dll";

    /// <summary>DuckDB's native library on macOS, at the binary's root (the smoke's list).</summary>
    public const string MacNativeLibraryFileName = "libduckdb.dylib";

    /// <summary>The spill cap (a DuckDB size).</summary>
    public const string MaxSpill = "10GB";

    /// <summary>The extensions a new database's file may have (<c>duckdb_execute</c>'s <c>create</c>).</summary>
    public static readonly IReadOnlySet<string> CreateExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".duckdb", ".ddb" };

    private readonly Func<DuckDbCatalog> _catalog;
    private readonly Func<WorkingDirectory?> _sandbox;
    private readonly Func<IReadOnlyList<string>>? _unticked;

    /// <param name="catalog">The databases in force; the app's reads the profile's and the home's <c>duckdb.json</c> at every call.</param>
    /// <param name="sandbox">The working directory while <c>DuckDB sandbox files</c> is on; null while it is off.</param>
    /// <param name="unticked">The files <c>duckdb.json</c> names that the user has not ticked: a path in the working directory that is one of them is refused. Null names none.</param>
    public DuckDbAccess(Func<DuckDbCatalog> catalog, Func<WorkingDirectory?> sandbox, Func<IReadOnlyList<string>>? unticked = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _sandbox = sandbox ?? throw new ArgumentNullException(nameof(sandbox));
        _unticked = unticked;
    }

    public DuckDbCatalog Catalog() => _catalog();

    /// <summary>The working directory while data files and DuckDB files there may be named, else null; read per call so <c>/cwd</c> is followed.</summary>
    public WorkingDirectory? Sandbox() => _sandbox();

    /// <summary>Whether a file in the working directory may be named by its path now.</summary>
    public bool SandboxOn => _sandbox() is not null;

    /// <summary>
    /// The file guard's refusal of a change to <paramref name="target"/>, or null: its rules but the databases' own — <c>.git</c>, <c>.neon</c>,
    /// the app's own files, the skill folders — judged from the working directory for a file in it, else from the file's own folder.
    /// </summary>
    public string? WriteRefusal(DuckDbTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.InMemory)
        {
            return null;
        }

        string root = _sandbox() is { } sandbox && WorkingDirectory.IsInside(sandbox.Root, target.FullPath) ? sandbox.Root : Path.GetDirectoryName(target.FullPath) ?? target.FullPath;
        return WriteGuard.Judge(root, target.FullPath, WriteRole.Write, () => false, () => null) is { } outcome
            ? DuckDbText.WriteGuarded(target.Name, FileText.Error(outcome, target.Name, "change"))
            : null;
    }

    /// <summary>The database a call means (<see cref="Resolve(string?, bool, out SqlRun?, out bool)"/> without create).</summary>
    public DuckDbTarget? Resolve(string? database, out SqlRun? refused) => Resolve(database, create: false, out refused, out _);

    /// <summary>
    /// The database a call means: <paramref name="database"/> as a <c>duckdb.json</c> name (case-insensitive, it wins), <c>:memory:</c>, else a
    /// path in the working directory (sandbox on); blank, the only named database, or the in-memory one while sandbox files is on and nothing is
    /// named (several named refused). With <paramref name="create"/> a path in the working directory that names no file yet is a new database's
    /// (its name ending <c>.duckdb</c> or <c>.ddb</c>, its folder already there); <paramref name="creating"/> says it is to be made. A file
    /// already there is sniffed: a SQLite one, an empty one and any other are refused with their sentence. Null with the refusal.
    /// </summary>
    public DuckDbTarget? Resolve(string? database, bool create, out SqlRun? refused, out bool creating)
    {
        refused = null;
        creating = false;
        var catalog = Catalog();
        var sandbox = _sandbox();
        if (create && string.IsNullOrWhiteSpace(database))
        {
            refused = SqlRun.Refused(SqlOutcome.UnknownConnection, DuckDbText.CreateNeedsPath);
            return null;
        }

        if (string.IsNullOrWhiteSpace(database))
        {
            if (catalog.Databases.Count == 0)
            {
                if (sandbox is not null)
                {
                    return DuckDbTarget.Memory;
                }

                refused = SqlRun.Refused(SqlOutcome.NoConnections, "");
                return null;
            }

            if (catalog.Databases.Count > 1)
            {
                refused = SqlRun.Refused(SqlOutcome.UnknownConnection, DuckDbText.DatabaseNeeded(string.Join(", ", catalog.Databases.Select(d => d.Name)), sandbox is not null));
                return null;
            }

            return Checked(new DuckDbTarget(catalog.Databases[0].Name, catalog.Databases[0].FullPath), out refused);
        }

        string wanted = database.Trim().Trim('"');
        if (catalog.Named(wanted) is { } named)
        {
            return Checked(new DuckDbTarget(named.Name, named.FullPath), out refused);
        }

        if (string.Equals(wanted, DuckDbTarget.MemoryPath, StringComparison.OrdinalIgnoreCase))
        {
            if (create)
            {
                refused = SqlRun.Refused(SqlOutcome.UnknownConnection, DuckDbText.MemoryIsGone, wanted);
                return null;
            }

            return DuckDbTarget.Memory;
        }

        if (sandbox is not null)
        {
            var outcome = sandbox.Resolve(wanted, forWrite: false, out string full);
            if (outcome == FileOutcome.Ok && _unticked?.Invoke() is { Count: > 0 } unticked && unticked.Any(n => string.Equals(n, full, StringComparison.OrdinalIgnoreCase)))
            {
                refused = SqlRun.Refused(SqlOutcome.UnknownConnection, DuckDbText.UntickedDatabase(wanted), wanted);
                return null;
            }

            if (outcome == FileOutcome.Ok && File.Exists(full))
            {
                return Checked(new DuckDbTarget(wanted.Replace('\\', '/'), full), out refused);
            }

            if (outcome is FileOutcome.OutsideRoot or FileOutcome.InImagesFolder or FileOutcome.InSkillFolder)
            {
                refused = SqlRun.Refused(SqlOutcome.ConnectFailed, DuckDbText.OutsideSandbox(wanted), wanted);
                return null;
            }

            if (create && outcome == FileOutcome.Ok)
            {
                if (!CreateExtensions.Contains(Path.GetExtension(full)) || Directory.Exists(full))
                {
                    refused = SqlRun.Refused(SqlOutcome.UnknownConnection, DuckDbText.BadExtension(wanted), wanted);
                    return null;
                }

                if (!Directory.Exists(Path.GetDirectoryName(full)))
                {
                    refused = SqlRun.Refused(SqlOutcome.UnknownConnection, DuckDbText.NoFolder(wanted), wanted);
                    return null;
                }

                if (DuckDbFilePaths.TooLong(full))
                {
                    refused = SqlRun.Refused(SqlOutcome.ConnectFailed, DuckDbText.PathTooLong(wanted), wanted);
                    return null;
                }

                creating = true;
                return new DuckDbTarget(wanted.Replace('\\', '/'), full);
            }
        }
        else if (create)
        {
            refused = SqlRun.Refused(SqlOutcome.UnknownConnection, DuckDbText.CreateNeedsSandbox, wanted);
            return null;
        }

        refused = SqlRun.Refused(SqlOutcome.UnknownConnection, DuckDbText.UnknownDatabase(wanted, string.Join(", ", catalog.Databases.Select(d => d.Name)), sandbox is not null), wanted);
        return null;
    }

    /// <summary><paramref name="target"/> when its file (if there) is a DuckDB database DuckDB can open; else null with the sentence.</summary>
    private static DuckDbTarget? Checked(DuckDbTarget target, out SqlRun? refused)
    {
        refused = null;
        if (DuckDbFilePaths.TooLong(target.FullPath))
        {
            refused = SqlRun.Refused(SqlOutcome.ConnectFailed, DuckDbText.PathTooLong(target.Name), target.Name);
            return null;
        }

        string? problem = File.Exists(target.FullPath) ? Sniff(target.FullPath) switch
        {
            DuckDbFileKind.Sqlite => DuckDbText.IsSqliteFile(target.Name),
            DuckDbFileKind.Empty => DuckDbText.EmptyFile(target.Name),
            DuckDbFileKind.Other => DuckDbText.NotADuckDbFile(target.Name),
            _ => null,
        } : null;
        if (problem is null)
        {
            return target;
        }

        refused = SqlRun.Refused(SqlOutcome.ConnectFailed, problem, target.Name);
        return null;
    }

    /// <summary>The bytes a SQLite database starts with.</summary>
    private static ReadOnlySpan<byte> SqliteHeader => "SQLite format 3\0"u8;

    /// <summary>What <paramref name="path"/>'s first 16 bytes say it is; <see cref="DuckDbFileKind.Unread"/> when they can't be read. Never throws.</summary>
    public static DuckDbFileKind Sniff(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> head = stackalloc byte[16];
            int read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            return read == 0 ? DuckDbFileKind.Empty
                : head[..read].StartsWith(SqliteHeader) ? DuckDbFileKind.Sqlite
                : IsDuckDbHeader(head[..read]) ? DuckDbFileKind.DuckDb
                : DuckDbFileKind.Other;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DuckDbFileKind.Unread;
        }
    }

    /// <summary>Whether <paramref name="head"/> (a file's first bytes) is a DuckDB database's: <c>DUCK</c> at offset 8 (the spike's).</summary>
    public static bool IsDuckDbHeader(ReadOnlySpan<byte> head) => head.Length >= 12 && head[8..12].SequenceEqual("DUCK"u8);

    /// <summary>Whether <paramref name="path"/> starts as a DuckDB database (the file guard's sniff). Never throws.</summary>
    public static bool HasDuckDbHeader(string path) => Sniff(path) == DuckDbFileKind.DuckDb;

    /// <summary>
    /// Runs the app's own <paramref name="statements"/> (the catalog's, no file in them) on the database <paramref name="database"/> means, each
    /// one's rows read to <paramref name="maxRows"/>, all inside one transaction rolled back. A timeout, a failed open and a DuckDB error are
    /// outcomes; a canceled token throws.
    /// </summary>
    public async Task<SqlRun> RunAsync(string? database, IReadOnlyList<string> statements, IReadOnlyList<SqlParameterValue> parameters, int maxRows, int timeoutSeconds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(statements);
        ArgumentNullException.ThrowIfNull(parameters);
        if (Resolve(database, out var refused) is not { } target)
        {
            return refused!;
        }

        string? root = _sandbox()?.Root;
        return await Task.Run(() => Run(target, [.. statements.Select(DuckDbPrepared.AsWritten)], parameters, maxRows, timeoutSeconds, root, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A read on the calling thread with every layer: <paramref name="root"/> is the folder data files may be read from (null: none).</summary>
    public static SqlRun Run(DuckDbTarget target, IReadOnlyList<DuckDbPrepared> statements, IReadOnlyList<SqlParameterValue> parameters, int maxRows, int timeoutSeconds, string? root, CancellationToken cancellationToken) =>
        Run(target, statements, parameters, maxRows, timeoutSeconds, write: false, create: false, root, DuckDbReadLayers.All, cancellationToken);

    /// <summary>A read with only <paramref name="layers"/> under it: a test's way to prove each layer alone. Never the app's.</summary>
    internal static SqlRun RunWithLayers(DuckDbTarget target, IReadOnlyList<string> statements, string? root, DuckDbReadLayers layers) =>
        Run(target, [.. statements.Select(DuckDbPrepared.AsWritten)], [], 100, 30, write: false, create: false, root, layers, CancellationToken.None);

    /// <summary><see cref="Execute"/> on the pool.</summary>
    public static Task<SqlRun> ExecuteAsync(DuckDbTarget target, bool create, DuckDbPrepared statement, IReadOnlyList<SqlParameterValue> parameters, int maxRows, int timeoutSeconds, string? root, CancellationToken cancellationToken) =>
        Task.Run(() => Execute(target, create, statement, parameters, maxRows, timeoutSeconds, root, cancellationToken), cancellationToken);

    /// <summary>
    /// <c>duckdb_execute</c>'s run: <paramref name="statement"/>, already passed by <see cref="DuckDbWriteGate"/>, its files checked and allowed
    /// by the user, on the file opened read-write (<paramref name="create"/>: made when missing) under the locked configuration and with no
    /// transaction of the app's — one statement commits on its own. <see cref="SqlRun.Changes"/> is DuckDB's count, or the rows a RETURNING gave
    /// back. A file this call made is taken away again (its <c>.wal</c> too) once the database is closed, when its statement fails.
    /// </summary>
    public static SqlRun Execute(DuckDbTarget target, bool create, DuckDbPrepared statement, IReadOnlyList<SqlParameterValue> parameters, int maxRows, int timeoutSeconds, string? root, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(statement);
        if (target.InMemory)
        {
            return SqlRun.Refused(SqlOutcome.UnknownConnection, DuckDbText.MemoryIsGone, target.Name);
        }

        bool made = create && !File.Exists(target.FullPath);
        bool done = false;
        try
        {
            var run = Run(target, [statement], parameters, maxRows, timeoutSeconds, write: true, create, root, DuckDbReadLayers.Locked, cancellationToken);
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

    /// <summary>A database file a failed create made, and its write-ahead log, taken away; a file that will not go stays, logged.</summary>
    private static void Unmake(string path)
    {
        foreach (string file in new[] { path, path + ".wal" })
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                DiagnosticLog.Warn(DuckDbConfigFile.Category, DuckDbText.UnmakeFailedLogLine(file, LogText.Excerpt(ex.Message)));
            }
        }
    }

    /// <summary>One gate per database file in the process (keyed case-blind), so no file is ever open twice at once.</summary>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> FileGates = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How often the run's timer looks at the deadline and the token, and interrupts again once either is past (SQLite's).</summary>
    private static readonly TimeSpan InterruptRepeat = TimeSpan.FromMilliseconds(100);

    private static SqlRun Run(DuckDbTarget target, IReadOnlyList<DuckDbPrepared> statements, IReadOnlyList<SqlParameterValue> parameters, int maxRows, int timeoutSeconds, bool write, bool create, string? root, DuckDbReadLayers layers, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(statements);
        if (!target.InMemory && !(write && create) && !File.Exists(target.FullPath))
        {
            return SqlRun.Refused(SqlOutcome.ConnectFailed, DuckDbText.NoFile(target.FullPath), target.Name);
        }

        if (write || target.InMemory)
        {
            layers &= ~DuckDbReadLayers.ReadOnlyOpen;
        }

        if (write)
        {
            layers &= ~DuckDbReadLayers.RolledBack;
        }

        var watch = Stopwatch.StartNew();
        var deadline = TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds));
        var fileGate = target.InMemory ? null : FileGates.GetOrAdd(Path.GetFullPath(target.FullPath), _ => new SemaphoreSlim(1, 1));
        if (fileGate is not null && !fileGate.Wait(deadline, cancellationToken))
        {
            return new SqlRun(SqlOutcome.Timeout, timeoutSeconds.ToString(CultureInfo.InvariantCulture), target.Name, "", [], watch.Elapsed);
        }

        try
        {
            return RunOpen(target, statements, parameters, maxRows, timeoutSeconds, write, root, layers, watch, deadline, cancellationToken);
        }
        finally
        {
            fileGate?.Release();
        }
    }

    private static SqlRun RunOpen(DuckDbTarget target, IReadOnlyList<DuckDbPrepared> statements, IReadOnlyList<SqlParameterValue> parameters, int maxRows, int timeoutSeconds, bool write, string? root, DuckDbReadLayers layers, Stopwatch watch, TimeSpan deadline, CancellationToken cancellationToken)
    {
        using var connection = new DuckDBConnection(ConnectionString(target, layers));
        try
        {
            connection.Open();
        }
        catch (Exception ex) when (ex is DuckDBException or IOException or InvalidOperationException)
        {
            DiagnosticLog.Warn(DuckDbConfigFile.Category, DuckDbText.OpenFailedLogLine(target.Name, LogText.Excerpt(ex.Message)));
            return SqlRun.Refused(SqlOutcome.ConnectFailed, Message(ex, DuckDbPrepared.AsWritten("")), target.Name);
        }

        if (layers.HasFlag(DuckDbReadLayers.Locked) && Lockdown(connection, root, readOnly: layers.HasFlag(DuckDbReadLayers.ReadOnlyOpen)) is { } unlocked)
        {
            DiagnosticLog.Warn(DuckDbConfigFile.Category, DuckDbText.LockdownFailedLogLine(target.Name, LogText.Excerpt(unlocked)));
            return SqlRun.Refused(SqlOutcome.ConnectFailed, unlocked, target.Name);
        }

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
                    DuckDB.NET.Native.NativeMethods.Startup.DuckDBInterrupt(connection.NativeConnection);
                }
            }
        }

        // SQLite's shape: past the deadline, or once canceled, the timer goes on interrupting until the run ends, since DuckDB drops an
        // interrupt that lands while no statement runs (the spike's), and the open flag keeps every interrupt off a closed connection.
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
        var current = DuckDbPrepared.AsWritten("");
        try
        {
            bool rolledBack = layers.HasFlag(DuckDbReadLayers.RolledBack);
            if (rolledBack)
            {
                NonQuery(connection, "BEGIN TRANSACTION");
            }

            try
            {
                foreach (var statement in statements)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    lock (gate)
                    {
                        if (timedOut)
                        {
                            return new SqlRun(SqlOutcome.Timeout, timeoutSeconds.ToString(CultureInfo.InvariantCulture), target.Name, "", grids, watch.Elapsed);
                        }
                    }

                    current = statement;
                    using var command = connection.CreateCommand();
                    bool returns = write && DuckDbReadOnlyGate.Returns(statement.Sql);
                    command.CommandText = write || DuckDbReadOnlyGate.IsExplain(statement.Sql) ? statement.Sql : Wrapped(statement.Sql);
                    command.UseStreamingMode = !write;
                    foreach (string bind in DuckDbReadOnlyGate.Binds(statement.Sql))
                    {
                        var value = parameters.FirstOrDefault(p => string.Equals(p.Name, bind, StringComparison.Ordinal))
                            ?? parameters.FirstOrDefault(p => string.Equals(p.Name, bind, StringComparison.OrdinalIgnoreCase));
                        command.Parameters.Add(Bind(bind, value?.Value));
                    }

                    if (write && !returns)
                    {
                        changes = command.ExecuteNonQuery();
                        continue;
                    }

                    using var reader = command.ExecuteReader();
                    var grid = SqlGrid.ReadAsync(reader, maxRows, CancellationToken.None, write ? ReadAnyCell : ReadCell).GetAwaiter().GetResult();
                    grids.Add(grid);
                    if (returns)
                    {
                        // RETURNING's rows are the change's count (ExecuteNonQuery said 0, the spike's): the rest read past the cap and counted.
                        int count = grid.Rows.Count + (grid.More ? 1 : 0);
                        while (reader.Read())
                        {
                            count++;
                        }

                        changes = count;
                    }
                }
            }
            finally
            {
                lock (gate)
                {
                    open = false;
                }

                if (rolledBack)
                {
                    try
                    {
                        NonQuery(connection, "ROLLBACK");
                    }
                    catch (Exception ex) when (ex is DuckDBException or InvalidOperationException)
                    {
                        // Interrupted mid-statement, the transaction may be gone already: there is nothing left to undo.
                    }
                }
            }
        }
        catch (Exception ex) when (IsInterrupt(ex) && cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(ex.Message, ex, cancellationToken);
        }
        catch (Exception ex) when (IsInterrupt(ex) && timedOut)
        {
            return new SqlRun(SqlOutcome.Timeout, timeoutSeconds.ToString(CultureInfo.InvariantCulture), target.Name, "", grids, watch.Elapsed);
        }
        catch (Exception ex) when (ex is DuckDBException or InvalidOperationException)
        {
            return new SqlRun(SqlOutcome.Failed, Message(ex, current), target.Name, "", grids, watch.Elapsed);
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

    /// <summary>A read as the reader takes it: every value as DuckDB writes it (newlines so a trailing <c>--</c> comment can't eat the parenthesis).</summary>
    public static string Wrapped(string sql) => "SELECT COLUMNS(*)::VARCHAR FROM (\n" + sql + "\n)";

    /// <summary>
    /// The open's settings: the file (read-only under <see cref="DuckDbReadLayers.ReadOnlyOpen"/>) and, under <see cref="DuckDbReadLayers.Locked"/>,
    /// the ones the connection string takes (the spike's: <c>allowed_directories</c> cannot go there; it and the lock are <c>SET</c>s).
    /// </summary>
    private static string ConnectionString(DuckDbTarget target, DuckDbReadLayers layers)
    {
        var builder = new DuckDBConnectionStringBuilder { DataSource = target.FullPath };
        if (layers.HasFlag(DuckDbReadLayers.ReadOnlyOpen))
        {
            builder["access_mode"] = "READ_ONLY";
        }

        if (layers.HasFlag(DuckDbReadLayers.Locked))
        {
            builder["autoinstall_known_extensions"] = "false";
            builder["autoload_known_extensions"] = "false";
            builder["allow_community_extensions"] = "false";
            builder["allow_persistent_secrets"] = "false";
            builder["memory_limit"] = MemoryCap;
            builder["max_temp_directory_size"] = MaxSpill;
            builder["temp_directory"] = SpillFolder;
        }

        return builder.ConnectionString;
    }

    /// <summary>
    /// The configuration locked before any statement of the call's (the spike's order: the folder, outside access off, the lock), then read
    /// back from <c>duckdb_settings()</c>; null when it holds, else why not (the connection is then closed unused: a failed lockdown must not
    /// leave DuckDB.NET's shared instance behind, the spike's).
    /// </summary>
    private static string? Lockdown(DuckDBConnection connection, string? root, bool readOnly)
    {
        try
        {
            if (root is not null)
            {
                NonQuery(connection, "SET allowed_directories = [" + DuckDbFilePaths.Literal(root) + "]");
            }

            NonQuery(connection, "SET enable_external_access = false");
            NonQuery(connection, "SET lock_configuration = true");
            var wanted = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["enable_external_access"] = "false",
                ["lock_configuration"] = "true",
                ["autoinstall_known_extensions"] = "false",
                ["autoload_known_extensions"] = "false",
                ["allow_community_extensions"] = "false",
                ["allow_persistent_secrets"] = "false",
            };
            if (readOnly)
            {
                wanted["access_mode"] = "read_only";
            }

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT name, value FROM duckdb_settings() WHERE name IN (" + string.Join(", ", wanted.Keys.Select(DuckDbFilePaths.Literal)) + ")";
            var seen = new Dictionary<string, string>(StringComparer.Ordinal);
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    seen[reader.GetString(0)] = reader.IsDBNull(1) ? "" : reader.GetString(1);
                }
            }

            foreach (var (name, value) in wanted)
            {
                if (!seen.TryGetValue(name, out string? actual) || !string.Equals(actual, value, StringComparison.OrdinalIgnoreCase))
                {
                    return DuckDbText.NotLocked(name);
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is DuckDBException or InvalidOperationException)
        {
            return Message(ex, DuckDbPrepared.AsWritten(""));
        }
    }

    private static void NonQuery(DuckDBConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static bool IsInterrupt(Exception ex) =>
        ex is OperationCanceledException || (ex is DuckDBException && ex.Message.Contains("Interrupt", StringComparison.OrdinalIgnoreCase));

    /// <summary>A <c>$name</c> placeholder bound: text, whole numbers, floats (a decimal as a float), booleans, null.</summary>
    public static DuckDBParameter Bind(string name, object? value) => new(name, value switch
    {
        null => DBNull.Value,
        decimal d => (double)d,
        int i => (long)i,
        var other => other,
    });

    /// <summary>One cell of a wrapped read: text already (<see cref="Wrapped"/>), or NULL.</summary>
    private static string ReadCell(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? SqlText.Null : SqlText.Cell(reader.GetValue(ordinal));

    /// <summary>One cell of a RETURNING (not wrapped: DuckDB takes no change in a subquery): a value the driver can't read shown as such.</summary>
    private static string ReadAnyCell(DbDataReader reader, int ordinal)
    {
        try
        {
            return ReadCell(reader, ordinal);
        }
        catch (Exception ex) when (ex is InvalidCastException or OverflowException or ArgumentException or NotSupportedException or MissingMethodException or InvalidOperationException)
        {
            return SqlText.Unreadable(reader.GetDataTypeName(ordinal));
        }
    }

    /// <summary>
    /// DuckDB's message as a sentence: a file another program holds, an encrypted or newer database, an extension that is not built in, a file
    /// the folder rule refused; else the message's lines before its <c>LINE n:</c> pointer, every rewritten path put back as the model wrote it.
    /// </summary>
    public static string Message(Exception ex, DuckDbPrepared statement)
    {
        ArgumentNullException.ThrowIfNull(ex);
        ArgumentNullException.ThrowIfNull(statement);
        string text = ex.Message;
        if (text.Contains("File is already open in", StringComparison.Ordinal) || text.Contains("being used by another process", StringComparison.OrdinalIgnoreCase))
        {
            int at = text.IndexOf("File is already open in", StringComparison.Ordinal);
            return DuckDbText.InUse(LogText.Excerpt(at >= 0 ? text[at..].Split('\n')[0].Trim().TrimEnd('.') : "it holds the file", 200));
        }

        if (text.Contains("encrypted", StringComparison.OrdinalIgnoreCase) && text.Contains("key", StringComparison.OrdinalIgnoreCase))
        {
            return DuckDbText.Encrypted;
        }

        if (text.Contains("newer version of DuckDB", StringComparison.OrdinalIgnoreCase))
        {
            return DuckDbText.NewerFile;
        }

        if (text.Contains("Autoloading", StringComparison.OrdinalIgnoreCase) && ExtensionName(text) is { } extension)
        {
            return DuckDbText.ReaderNotBuiltIn(extension);
        }

        var lines = new List<string>();
        foreach (string line in text.ReplaceLineEndings("\n").Split('\n'))
        {
            if (line.StartsWith("LINE ", StringComparison.Ordinal))
            {
                break;
            }

            if (line.Trim().Length > 0)
            {
                lines.Add(line.Trim());
            }
        }

        string message = statement.Unspell(string.Join(" ", lines));
        return LogText.Excerpt(NameScrub.Display(message), 600);
    }

    /// <summary>The extension named in an autoloading error (<c>required extension 'excel'</c>); null when none is.</summary>
    private static string? ExtensionName(string text)
    {
        const string lead = "extension '";
        int at = text.IndexOf(lead, StringComparison.OrdinalIgnoreCase);
        int end = at < 0 ? -1 : text.IndexOf('\'', at + lead.Length);
        return end < 0 ? null : text[(at + lead.Length)..end];
    }

    // ─── the caps and the spill folder ──────────────────────────────────────────

    /// <summary>A quarter of the memory this process may use (the machine's, a container's limit), as DuckDB takes a size; worked out once.</summary>
    public static string MemoryCap { get; } = Math.Max(256, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 4 / (1024 * 1024)).ToString(CultureInfo.InvariantCulture) + "MiB";

    /// <summary>The folder every run's spill folder sits in: <c>&lt;temp&gt;/NeonSidekick/duckdb</c>.</summary>
    public static string SpillRoot { get; } = Path.Combine(Path.GetTempPath(), "NeonSidekick", "duckdb");

    /// <summary>This process's spill folder (DuckDB makes it at the first spill).</summary>
    public static string SpillFolder { get; } = Path.Combine(SpillRoot, Environment.ProcessId.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// The spill folders of processes that are gone, deleted (at the app's start): never this process's nor one whose process still runs.
    /// <paramref name="alive"/> is the look-up (the process table in the app). How many went. Never throws.
    /// </summary>
    public static int Sweep(string? root = null, Func<int, bool>? alive = null)
    {
        root ??= SpillRoot;
        alive ??= Viewer.PdfView.IsRunning;
        int gone = 0;
        try
        {
            if (!Directory.Exists(root))
            {
                return 0;
            }

            foreach (string folder in Directory.EnumerateDirectories(root))
            {
                if (!int.TryParse(Path.GetFileName(folder), NumberStyles.None, CultureInfo.InvariantCulture, out int pid) || pid == Environment.ProcessId || alive(pid))
                {
                    continue;
                }

                try
                {
                    Directory.Delete(folder, recursive: true);
                    gone++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    DiagnosticLog.Debug(DuckDbConfigFile.Category, DuckDbText.SweepFailedLogLine(folder, LogText.Excerpt(ex.Message)));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Debug(DuckDbConfigFile.Category, DuckDbText.SweepFailedLogLine(root, LogText.Excerpt(ex.Message)));
        }

        return gone;
    }
}
