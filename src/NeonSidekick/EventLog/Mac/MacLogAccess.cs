using System.Diagnostics;
using System.Globalization;
using NeonSidekick.Diagnostics;
using NeonSidekick.Files;

namespace NeonSidekick.EventLog;

/// <summary>Which of a Mac's logs a read goes to (2026-10-18): the unified log (this Mac's or an archive), the crash reports, install.log.</summary>
public enum MacLogKind
{
    Unified,
    Crashes,
    Install,
}

/// <summary>
/// Where one Mac read goes (2026-10-18, the Mac event-log plan): the log as the answers name it (<c>system</c>, <c>crashes</c>,
/// <c>install</c>, or a <c>.logarchive</c> as the working directory names it), the reader's target (the unified log's alone), whether the
/// model reads it redacted, and which kind of log it is.
/// </summary>
public sealed record MacLogSource(string Log, UnifiedLogTarget Target, bool Redacted, MacLogKind Kind = MacLogKind.Unified)
{
    public bool IsArchive => Kind == MacLogKind.Unified && !Target.IsLocal;
}

/// <summary>
/// The Mac event logs' one way in (2026-10-18, the Mac event-log plan; <see cref="EventLogAccess"/>'s shape for one computer, this Mac):
/// which log a name means — the unified log by <c>system</c> (or <c>unified</c>, <c>sys</c>, <c>log</c>), the crash reports by <c>crashes</c>,
/// install.log by <c>install</c> (phase 3), a <c>.logarchive</c> folder under the working directory, an <c>.evtx</c> refused as Windows' — the detail (<c>local</c>'s from <c>eventlog.json</c>, redacted unless it
/// says <c>full</c>, always redacted for a twin), and the read itself on a pool thread under a 30 s token with
/// <see cref="Task.WaitAsync(CancellationToken)"/>, so a native call that will not end is abandoned rather than waited out. A reader
/// failure is its sentence (<see cref="MacLogErrors"/>), never a throw; a cancel by the caller is the caller's.
/// </summary>
public sealed class MacLogAccess
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>The unified log's names, the first the one the answers use.</summary>
    public static readonly IReadOnlyList<string> SystemNames = ["system", "unified", "sys", "log"];

    /// <summary>The crash reports' names, the first the one the answers use.</summary>
    public static readonly IReadOnlyList<string> CrashNames = ["crashes", "crash", "crashreports", "diagnosticreports", "reports"];

    /// <summary>install.log's names, the first the one the answers use.</summary>
    public static readonly IReadOnlyList<string> InstallNames = ["install", "install.log", "installs", "installer"];

    public const string ArchiveExtension = ".logarchive";

    private readonly Func<bool> _localFull;

    /// <param name="log">The reader; null where there is none (off a Mac): every call says so.</param>
    /// <param name="localFull">Whether <c>eventlog.json</c>'s <c>local</c> says <c>full</c>, read at each call.</param>
    /// <param name="files">The working directory a <c>.logarchive</c> is found under.</param>
    /// <param name="time">The clock: the times a call names, the zone an answer shows, the scan's budget.</param>
    /// <param name="places">Where the crash reports and install.log are; this account's when null.</param>
    public MacLogAccess(IUnifiedLog? log, Func<bool> localFull, WorkingDirectory files, TimeProvider time, MacLogPlaces? places = null)
    {
        Places = places ?? MacLogPlaces.Default;
        Log = log;
        _localFull = localFull ?? throw new ArgumentNullException(nameof(localFull));
        Files = files ?? throw new ArgumentNullException(nameof(files));
        Time = time ?? throw new ArgumentNullException(nameof(time));
    }

    public IUnifiedLog? Log { get; }

    public WorkingDirectory Files { get; }

    public TimeProvider Time { get; }

    /// <summary>Where the logs that are files are.</summary>
    public MacLogPlaces Places { get; }

    /// <summary>Whether <paramref name="log"/> names the unified log.</summary>
    public static bool IsSystem(string log) => SystemNames.Contains(log.Trim().ToLowerInvariant(), StringComparer.Ordinal);

    /// <summary>Whether <paramref name="log"/> names a <c>.logarchive</c>.</summary>
    public static bool IsArchive(string log) => log.Trim().TrimEnd('/', '\\').EndsWith(ArchiveExtension, StringComparison.OrdinalIgnoreCase);

    /// <summary>Where a call reads: the unified log, or a <c>.logarchive</c> folder under the working directory; anything else is a sentence.</summary>
    public (MacLogSource? Source, string? Error) Resolve(string log, bool forceRedacted)
    {
        ArgumentNullException.ThrowIfNull(log);
        string name = log.Trim();
        bool redacted = forceRedacted || !_localFull();
        if (IsSystem(name))
        {
            return (new MacLogSource(SystemNames[0], UnifiedLogTarget.Local, redacted), null);
        }

        string word = name.ToLowerInvariant();
        if (CrashNames.Contains(word, StringComparer.Ordinal))
        {
            return (new MacLogSource(CrashNames[0], UnifiedLogTarget.Local, redacted, MacLogKind.Crashes), null);
        }

        if (InstallNames.Contains(word, StringComparer.Ordinal))
        {
            return (new MacLogSource(InstallNames[0], UnifiedLogTarget.Local, redacted, MacLogKind.Install), null);
        }

        if (EventLogQuery.IsFile(name))
        {
            return (null, MacLogText.EvtxIsWindows);
        }

        if (IsArchive(name))
        {
            string relative = name.TrimEnd('/', '\\');
            var outcome = Files.Resolve(relative, forWrite: false, out string full);
            if (outcome != FileOutcome.Ok)
            {
                return (null, FileText.Error(outcome, relative, "read"));
            }

            return Directory.Exists(full)
                ? (new MacLogSource(relative, UnifiedLogTarget.Archive(full), redacted), null)
                : (null, "Error: " + MacLogText.NoArchive(relative));
        }

        return (null, MacLogText.UnknownLog(name));
    }

    /// <summary>install.log opened for a backward read (shared, so the system's writer is never in the way).</summary>
    public FileStream OpenInstallLog() =>
        new(Places.InstallLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);

    /// <summary>The scan's bounds: <paramref name="scanLimit"/> entries the predicate let through, and 10 s.</summary>
    public EventScan Scan(MacLogSource source, int scanLimit)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new EventScan(source.Redacted, scanLimit, EventScan.DefaultBudget, Time);
    }

    /// <summary>
    /// Runs <paramref name="work"/> over the reader on a pool thread, under a token that also fires at <see cref="Timeout"/>; the reader's
    /// failure as its sentence, the timeout as its own. <paramref name="cancellationToken"/>'s cancel is thrown.
    /// </summary>
    public async Task<EventLogResult<T>> RunAsync<T>(MacLogSource source, Func<IUnifiedLog, CancellationToken, T> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(work);
        if (Log is not { } log)
        {
            return EventLogResult<T>.Failed(EventLogText.NoneHere);
        }

        return await RunCoreAsync(source, token => work(log, token), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs a read of a log that is a file (the crash reports, install.log) as <see cref="RunAsync{T}"/> runs the unified log's: a pool
    /// thread, the 30 s token, a refusal as its sentence (a folder or file this account may not read, a file that is gone).
    /// </summary>
    public Task<EventLogResult<T>> RunFilesAsync<T>(MacLogSource source, Func<CancellationToken, T> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(work);
        return RunCoreAsync(source, work, cancellationToken);
    }

    private async Task<EventLogResult<T>> RunCoreAsync<T>(MacLogSource source, Func<CancellationToken, T> work, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(Timeout, Time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var token = linked.Token;
        long started = Stopwatch.GetTimestamp();
        try
        {
            var value = await Task.Run(() => work(token), CancellationToken.None).WaitAsync(token).ConfigureAwait(false);
            DiagnosticLog.Debug(EventLogText.Category, $"{source.Log} read in {Stopwatch.GetElapsedTime(started).TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture)} ms");
            return EventLogResult<T>.Ok(value);
        }
        catch (UnifiedLogException ex)
        {
            DiagnosticLog.Debug(EventLogText.Category, $"{source.Log} failed: {ex.Message}");
            return EventLogResult<T>.Failed("Error: " + MacLogErrors.Sentence(ex, source.Target.ArchivePath));
        }
        catch (UnauthorizedAccessException)
        {
            DiagnosticLog.Debug(EventLogText.Category, $"{source.Log} refused");
            return EventLogResult<T>.Failed("Error: " + MacLogText.FileRefused(source.Log));
        }
        catch (IOException ex)
        {
            DiagnosticLog.Debug(EventLogText.Category, $"{source.Log} failed: {ex.Message}");
            return EventLogResult<T>.Failed("Error: " + MacLogText.FileFailed(source.Log, ex.Message));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            DiagnosticLog.Debug(EventLogText.Category, $"{source.Log} timed out");
            return EventLogResult<T>.Failed(MacLogText.TimedOut);
        }
    }

    /// <summary>The working directory's <c>.logarchive</c> folders (none deeper than two levels), by their paths there, A to Z.</summary>
    public IReadOnlyList<string> Archives()
    {
        try
        {
            string root = Files.Root;
            return Directory.EnumerateDirectories(root, "*" + ArchiveExtension, new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true })
                .Select(full => Path.GetRelativePath(root, full))
                .Order(StringComparer.OrdinalIgnoreCase)
                .Take(50)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}

/// <summary>
/// What a Mac search or summary asks (2026-10-18): the log, the levels (OSLog's words; empty is every level), the filters, the window
/// (<see cref="Since"/> always set: the read's time limit), the page's cursor and size. <see cref="Filter"/> is what the reader gets: under
/// the redacted detail the text is not sent to OSLog but matched on the redacted message (<see cref="KeepsText"/>), so a twin cannot
/// learn by a text search what it may not see.
/// </summary>
public sealed record MacLogQuery(
    string Log,
    IReadOnlyList<string> Levels,
    DateTimeOffset Since,
    DateTimeOffset? Until = null,
    string? Process = null,
    int? Pid = null,
    string? Subsystem = null,
    string? Category = null,
    string? Text = null,
    IReadOnlyList<string>? Exclude = null,
    MacLogHandle? Before = null,
    int Limit = 20)
{
    /// <summary>A search's window when none is given: the last hour (the log is vast: Phase 0 read 20,000 errors and faults in 2.3 minutes).</summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromHours(1);

    /// <summary>The levels when none is given: faults and errors.</summary>
    public static readonly IReadOnlyList<string> DefaultLevels = ["fault", "error"];

    /// <summary>The level words a call may give, <c>all</c> for every level.</summary>
    public const string LevelChoices = "fault, error, default, info, debug or all";

    /// <summary>A comma list of levels: blank is the default, <c>all</c> every level, else each must be one of OSLog's words.</summary>
    public static (IReadOnlyList<string>? Levels, string? Error) ParseLevels(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return (DefaultLevels, null);
        }

        var words = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(w => w.ToLowerInvariant()).ToList();
        if (words.Contains("all", StringComparer.Ordinal))
        {
            return ([], null);
        }

        foreach (string word in words)
        {
            if (MacLogPredicate.LevelOf(word) == 0)
            {
                return (null, MacLogText.BadLevel(word));
            }
        }

        return (words.Distinct(StringComparer.Ordinal).ToList(), null);
    }

    /// <summary>A comma list of process names to leave out.</summary>
    public static IReadOnlyList<string> ParseList(string? text) =>
        (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>The reader's filter: the page's cursor as the read's newest moment, the text left to <see cref="KeepsText"/> when redacted.</summary>
    public MacLogFilter Filter(bool redacted)
    {
        DateTimeOffset? until = Until;
        if (Before is { } before && (until is null || before.Time < until))
        {
            until = before.Time;
        }

        return new MacLogFilter(Since, Levels, until, Process, Pid, Subsystem, Category, redacted ? null : Text, Exclude);
    }

    /// <summary>Whether a message, as the detail shows it, holds the query's text (case ignored); true with no text.</summary>
    public bool KeepsText(string shownMessage) =>
        MacLogPredicate.Clean(Text) is not { Length: > 0 } text || shownMessage.Contains(text, StringComparison.OrdinalIgnoreCase);
}
