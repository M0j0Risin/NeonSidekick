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
/// <para>
/// Later fixes (2026-10-20, the code review). <c>Event log computers offered</c> holds here as on Windows: with <c>local</c> unticked the
/// Mac's own logs (system, crashes, install) are refused and only the working directory's archives read, as Windows keeps its
/// <c>.evtx</c> files (<see cref="LocalTicked"/>). An abandoned read is observed, so its late fault is a debug line and never an unobserved
/// task exception; a reader's <see cref="InvalidOperationException"/>, <see cref="ArgumentException"/> and the like are an <c>Error:</c>
/// sentence too. The archive walk (<see cref="Archives"/>) never enters dot-folders, links, packages or the home's protected folders
/// (<c>~/Library</c>, Desktop, Documents…, each a privacy prompt), runs under the caller's token, and is kept a few seconds.
/// </para>
/// </summary>
public sealed class MacLogAccess
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>How long an archive listing is reused (2026-10-20): completion asks on every keystroke.</summary>
    public static readonly TimeSpan ArchiveCacheTime = TimeSpan.FromSeconds(5);

    /// <summary>The unified log's names, the first the one the answers use.</summary>
    public static readonly IReadOnlyList<string> SystemNames = ["system", "unified", "sys", "log"];

    /// <summary>The crash reports' names, the first the one the answers use.</summary>
    public static readonly IReadOnlyList<string> CrashNames = ["crashes", "crash", "crashreports", "diagnosticreports", "reports"];

    /// <summary>install.log's names, the first the one the answers use.</summary>
    public static readonly IReadOnlyList<string> InstallNames = ["install", "install.log", "installs", "installer"];

    public const string ArchiveExtension = ".logarchive";

    /// <summary>
    /// Folders the archive walk never enters (2026-10-20, the code review): macOS packages, which Finder shows as one file and which hold
    /// no archive of the user's, and the photo and music libraries, whose reading asks for the privacy grant.
    /// </summary>
    public static readonly IReadOnlySet<string> PackageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".app", ".bundle", ".framework", ".plugin", ".kext", ".appex", ".xpc", ".dSYM", ".pkg", ".mpkg", ".photoslibrary", ".photolibrary",
        ".musiclibrary", ".tvlibrary", ".imovielibrary", ".fcpbundle", ".aplibrary", ".lrlibrary", ".xcodeproj", ".xcworkspace", ".xcarchive",
        ".rtfd", ".pages", ".numbers", ".key", ".sparsebundle", ".band", ".vmwarevm", ".pvm", ".utm", ".photoboothlibrary",
    };

    /// <summary>The home's folders the archive walk never enters from the home itself: each is behind a privacy prompt (2026-10-20).</summary>
    public static readonly IReadOnlySet<string> ProtectedHomeFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Library", "Desktop", "Documents", "Downloads", "Pictures", "Movies", "Music",
    };

    private readonly Func<bool> _localFull;
    private readonly Func<bool> _localTicked;
    private readonly object _archivesGate = new();
    private (string Root, DateTimeOffset At, IReadOnlyList<string> List)? _archives;
    private int _archivesRefreshing;

    /// <param name="log">The reader; null where there is none (off a Mac): every call says so.</param>
    /// <param name="localFull">Whether <c>eventlog.json</c>'s <c>local</c> says <c>full</c>, read at each call.</param>
    /// <param name="files">The working directory a <c>.logarchive</c> is found under.</param>
    /// <param name="time">The clock: the times a call names, the zone an answer shows, the scan's budget.</param>
    /// <param name="places">Where the crash reports and install.log are; this account's when null.</param>
    /// <param name="localTicked">Whether <c>local</c> is ticked in <c>Event log computers offered</c>, read at each call; always when null.</param>
    public MacLogAccess(IUnifiedLog? log, Func<bool> localFull, WorkingDirectory files, TimeProvider time, MacLogPlaces? places = null, Func<bool>? localTicked = null)
    {
        Places = places ?? MacLogPlaces.Default;
        Log = log;
        _localFull = localFull ?? throw new ArgumentNullException(nameof(localFull));
        _localTicked = localTicked ?? (() => true);
        Files = files ?? throw new ArgumentNullException(nameof(files));
        Time = time ?? throw new ArgumentNullException(nameof(time));
    }

    public IUnifiedLog? Log { get; }

    public WorkingDirectory Files { get; }

    public TimeProvider Time { get; }

    /// <summary>Where the logs that are files are.</summary>
    public MacLogPlaces Places { get; }

    /// <summary>Whether this Mac's own logs may be read: <c>local</c> ticked in <c>Event log computers offered</c> (2026-10-20, the code review).</summary>
    public bool LocalTicked => _localTicked();

    /// <summary>
    /// Whether <c>Event log computers offered</c>, as the settings hold it, ticks <c>local</c> (2026-10-20): null is <c>local</c> alone,
    /// the Windows door's rule (<see cref="EventLogCatalog.OfferedNames"/>).
    /// </summary>
    public static bool Ticks(IReadOnlyList<string>? offered) =>
        EventLogCatalog.OfferedNames(offered).Any(n => string.Equals(n.Trim(), EventLogText.LocalName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether <paramref name="log"/> names the unified log.</summary>
    public static bool IsSystem(string log) => SystemNames.Contains(log.Trim().ToLowerInvariant(), StringComparer.Ordinal);

    /// <summary>Whether <paramref name="log"/> names a <c>.logarchive</c>.</summary>
    public static bool IsArchive(string log) => log.Trim().TrimEnd('/', '\\').EndsWith(ArchiveExtension, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Where a call reads: the unified log, the crash reports or install.log while <c>local</c> is ticked, or a <c>.logarchive</c> folder
    /// under the working directory; anything else is a sentence.
    /// </summary>
    public (MacLogSource? Source, string? Error) Resolve(string log, bool forceRedacted)
    {
        ArgumentNullException.ThrowIfNull(log);
        string name = log.Trim();
        bool redacted = forceRedacted || !_localFull();
        string word = name.ToLowerInvariant();
        MacLogSource? local = IsSystem(name) ? new MacLogSource(SystemNames[0], UnifiedLogTarget.Local, redacted)
            : CrashNames.Contains(word, StringComparer.Ordinal) ? new MacLogSource(CrashNames[0], UnifiedLogTarget.Local, redacted, MacLogKind.Crashes)
            : InstallNames.Contains(word, StringComparer.Ordinal) ? new MacLogSource(InstallNames[0], UnifiedLogTarget.Local, redacted, MacLogKind.Install)
            : null;
        if (local is not null)
        {
            return LocalTicked ? (local, null) : (null, MacLogText.LocalNotTicked);
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
        var read = Task.Run(() => work(token), CancellationToken.None);
        _ = read.ContinueWith(   // observed whatever happens: a read given up on faults long after, with the linked token's cancel (2026-10-20, the code review)
            static t => _ = t.Exception,
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try
        {
            var value = await read.WaitAsync(token).ConfigureAwait(false);
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
            DiagnosticLog.Debug(EventLogText.Category, $"{source.Log} timed out" + (read.IsCompleted ? "" : "; the read is left to end at its next entry"));
            return EventLogResult<T>.Failed(MacLogText.TimedOut);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException or OverflowException or NotSupportedException
            or System.Text.Json.JsonException or System.Text.DecoderFallbackException)
        {
            DiagnosticLog.Debug(EventLogText.Category, $"{source.Log} failed: {ex.GetType().Name}: {ex.Message}");
            return EventLogResult<T>.Failed("Error: " + MacLogText.FileFailed(source.Log, ex.Message));
        }
    }

    /// <summary>
    /// The working directory's <c>.logarchive</c> folders (none deeper than two levels), by their paths there, A to Z, at most 50. Walked on
    /// the caller's thread under <paramref name="cancellationToken"/> (the tools run it under the read's timeout); a listing of the same root
    /// from the last <see cref="ArchiveCacheTime"/> is reused.
    /// </summary>
    public IReadOnlyList<string> Archives(CancellationToken cancellationToken = default)
    {
        string root = Files.Root;
        lock (_archivesGate)
        {
            if (_archives is { } kept && kept.Root == root && Time.GetUtcNow() - kept.At < ArchiveCacheTime)
            {
                return kept.List;
            }
        }

        var list = WalkArchives(root, Places.Home, cancellationToken);
        lock (_archivesGate)
        {
            _archives = (root, Time.GetUtcNow(), list);
        }

        return list;
    }

    /// <summary>
    /// The archives for <c>/eventlog</c>'s completion (2026-10-20, the code review: it walked the working directory on the input thread at
    /// every keystroke): the last listing of this root, never a walk on the caller's thread; a stale or missing one is walked again on a
    /// pool thread, for the next keystroke.
    /// </summary>
    public IReadOnlyList<string> CachedArchives()
    {
        string root;
        try
        {
            root = Files.Root;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }

        IReadOnlyList<string> known = [];
        bool fresh = false;
        lock (_archivesGate)
        {
            if (_archives is { } kept && kept.Root == root)
            {
                known = kept.List;
                fresh = Time.GetUtcNow() - kept.At < ArchiveCacheTime;
            }
        }

        if (!fresh && Interlocked.Exchange(ref _archivesRefreshing, 1) == 0)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    using var timeout = new CancellationTokenSource(Timeout);
                    _ = Archives(timeout.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or UnauthorizedAccessException or ArgumentException)
                {
                    DiagnosticLog.Debug(EventLogText.Category, "the archive listing failed: " + ex.Message);
                }
                finally
                {
                    Volatile.Write(ref _archivesRefreshing, 0);
                }
            });
        }

        return known;
    }

    /// <summary>The walk itself: two levels under <paramref name="root"/>, never into a dot-folder, a link, a package or the home's protected folders.</summary>
    public static IReadOnlyList<string> WalkArchives(string root, string home, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(root);
        var found = new List<string>();
        string homeFolder = Path.TrimEndingDirectorySeparator(home ?? "");
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
        };

        void Visit(string folder, int depth)
        {
            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateDirectories(folder, "*", options);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }

            bool atHome = homeFolder.Length > 1 && string.Equals(Path.TrimEndingDirectorySeparator(folder), homeFolder, StringComparison.Ordinal);
            try
            {
                foreach (string child in children)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string name = Path.GetFileName(child);
                    if (name.EndsWith(ArchiveExtension, StringComparison.OrdinalIgnoreCase))
                    {
                        found.Add(Path.GetRelativePath(root, child));
                        continue;   // never into an archive
                    }

                    if (depth >= 2 || name.StartsWith('.') || PackageExtensions.Contains(Path.GetExtension(name)) || (atHome && ProtectedHomeFolders.Contains(name)))
                    {
                        continue;
                    }

                    Visit(child, depth + 1);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // a folder that went away mid-walk: what was found stays
            }
        }

        Visit(root, 0);
        return found.Order(StringComparer.OrdinalIgnoreCase).Take(50).ToList();
    }
}

/// <summary>
/// What a Mac search or summary asks (2026-10-18): the log, the levels (OSLog's words; empty is every level), the filters, the window
/// (<see cref="Since"/> always set: the read's time limit), the page's cursor and size. <see cref="Filter"/> is what the reader gets: under
/// the redacted detail the text is not sent to OSLog but matched on the redacted message (<see cref="KeepsText"/>), so a twin cannot
/// learn by a text search what it may not see. <see cref="SinceGiven"/> (2026-10-20): false when the window is the default one, so an
/// archive's can be anchored at its own newest entry (<see cref="MacLogSearch.Anchor"/>).
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
    int Limit = 20,
    bool SinceGiven = true)
{
    /// <summary>A search's window when none is given: the last hour (the log is vast: Phase 0 read 20,000 errors and faults in 2.3 minutes).</summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromHours(1);

    /// <summary>The levels when none is given: faults and errors.</summary>
    public static readonly IReadOnlyList<string> DefaultLevels = ["fault", "error"];

    /// <summary>The level words a call may give, <c>all</c> for every level.</summary>
    public const string LevelChoices = "fault, error, default, info, debug or all";

    /// <summary>The window a call gets when it gives no <c>since</c>: an hour of the unified log, a week of reports, a day of install.log.</summary>
    public static TimeSpan WindowFor(MacLogKind kind) => kind switch
    {
        MacLogKind.Crashes => MacFileLogs.CrashWindow,
        MacLogKind.Install => MacFileLogs.InstallWindow,
        _ => DefaultWindow,
    };

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
            until = before.Time + MacLogSearch.Microsecond;   // the cursor's whole microsecond: its ties after it are on the next page
        }

        return new MacLogFilter(Since, Levels, until, Process, Pid, Subsystem, Category, redacted ? null : Text, Exclude);
    }

    /// <summary>Whether a message, as the detail shows it, holds the query's text (case ignored); true with no text.</summary>
    public bool KeepsText(string shownMessage) =>
        MacLogPredicate.Clean(Text) is not { Length: > 0 } text || shownMessage.Contains(text, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The terms a call gives, as the tools' arguments and <c>/eventlog</c>'s words both read (2026-10-20, the code review: the two built their
/// query apart, and their windows drifted). <see cref="QueryFor"/> is the one builder: the unified log's levels (faults and errors when none
/// was given) and terms; a log that is a file's window (a week of reports, a day of install.log) without the terms it lacks, which it
/// names for the answer's note. The window's start when no <c>since</c> is given: <see cref="MacLogQuery.WindowFor"/> before
/// <c>until</c>, else before now — for an archive, before its own newest entry, found at read time (<see cref="MacLogQuery.SinceGiven"/>).
/// </summary>
public sealed record MacLogTerms(
    string? Levels = null,
    DateTimeOffset? Since = null,
    DateTimeOffset? Until = null,
    string? Process = null,
    int? Pid = null,
    string? Subsystem = null,
    string? Category = null,
    string? Text = null,
    IReadOnlyList<string>? Exclude = null)
{
    public const string LevelTerm = "level";
    public const string PidTerm = "pid";
    public const string SubsystemTerm = "subsystem";
    public const string CategoryTerm = "category";

    /// <summary>The query for <paramref name="source"/>'s kind and the terms its log lacks, or the refusal of a level that is none of OSLog's.</summary>
    public (MacLogQuery? Query, IReadOnlyList<string> Ignored, string? Error) QueryFor(MacLogSource source, DateTimeOffset now, int limit, MacLogHandle? before = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        bool unified = source.Kind == MacLogKind.Unified;
        var ignored = new List<string>();
        if (!unified)
        {
            if (Levels is not null)
            {
                ignored.Add(LevelTerm);
            }

            if (Pid is not null && source.Kind == MacLogKind.Crashes)
            {
                ignored.Add(PidTerm);
            }

            if (Subsystem is not null)
            {
                ignored.Add(SubsystemTerm);
            }

            if (Category is not null)
            {
                ignored.Add(CategoryTerm);
            }
        }

        IReadOnlyList<string> levels = [];
        if (unified)
        {
            var (parsed, error) = MacLogQuery.ParseLevels(Levels);
            if (parsed is null)
            {
                return (null, ignored, error);
            }

            levels = parsed;
        }

        var query = new MacLogQuery(
            source.Log,
            levels,
            Since ?? (Until ?? now) - MacLogQuery.WindowFor(source.Kind),
            Until,
            Process,
            source.Kind == MacLogKind.Crashes ? null : Pid,
            unified ? Subsystem : null,
            unified ? Category : null,
            Text,
            Exclude ?? [],
            before,
            limit,
            SinceGiven: Since is not null || Until is not null);
        return (query, ignored, null);
    }
}
