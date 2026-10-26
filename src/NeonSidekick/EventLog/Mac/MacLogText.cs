namespace NeonSidekick.EventLog;

/// <summary>
/// The Mac event logs' wording (2026-10-18, the Mac event-log plan): what the unified log, the crash reports and install.log say
/// to the model and the user. Pinned by tests; the Windows sentences stay in <see cref="EventLogText"/>.
/// </summary>
public static class MacLogText
{
    /// <summary>The store refused for want of rights: Apple's local store is for an administrator account.</summary>
    public const string NeedsAdmin = "the unified log can be read only from an administrator account on this Mac.";

    /// <summary>A <c>.logarchive</c> that is not there or not an archive.</summary>
    public static string NoArchive(string name) => $"'{name}' is not a log archive this Mac can read.";

    /// <summary>A <c>.logarchive</c> this account may not open (2026-10-20: it said "needs an administrator" before, which an archive never does).</summary>
    public static string ArchiveRefused(string name) => $"'{name}' cannot be read from this account.";

    /// <summary>OSLog refused the filter itself (a value it could not take).</summary>
    public const string FilterRefused = "the unified log refused that filter; try fewer or plainer terms.";

    /// <summary>Any other refusal, OSLog's own words kept.</summary>
    public static string Refused(string description) => $"the unified log could not be read: {description}";

    // ── The tools' refusals ─────────────────────────────────────────────────

    /// <summary>An <c>.evtx</c> named on a Mac.</summary>
    public const string EvtxIsWindows = "Error: an .evtx file is a Windows event log; on a Mac the logs are system, crashes, install and the working directory's .logarchive folders.";

    /// <summary>A log name that is none of the Mac's.</summary>
    public static string UnknownLog(string name) => $"Error: there is no log named '{name}' on this Mac; eventlog_logs lists them (system, crashes, install, or a .logarchive in the working directory).";

    /// <summary>A read that ran out of time.</summary>
    public const string TimedOut = "Error: the read took too long and was stopped; narrow it (a process, a subsystem, a level, or a shorter since).";

    /// <summary>A level that is none of OSLog's.</summary>
    public static string BadLevel(string word) => $"Error: level '{Llm.Tools.ArgumentText.Echo(word)}' is not one of fault, error, default, info, debug or all.";

    /// <summary>A handle not written by an answer.</summary>
    public static string BadHandle(string text) => $"Error: '{text}' is not an entry's handle (the u… an answer shows).";

    /// <summary>A handle whose entry is gone.</summary>
    public static string NoEntry(string handle, string log) => $"Error: entry {handle} is no longer in {log}: the log has moved past it.";

    /// <summary>
    /// The Mac's own logs asked for with <c>local</c> unticked in <c>Event log computers offered</c> (2026-10-20, the code review: Windows'
    /// <see cref="EventLogText.NoComputerTicked"/>, which keeps its <c>.evtx</c> files as this keeps the archives).
    /// </summary>
    public const string LocalNotTicked = "Error: local is not ticked in Event log computers offered (/tools › Admin › EventLog); only the working directory's .logarchive folders can be read.";

    /// <summary><c>eventlog_logs</c>' line for the Mac's own logs while <c>local</c> is unticked.</summary>
    public const string LocalNotTickedLine = "not offered: local is not ticked in Event log computers offered";

    /// <summary><c>eventlog_logs</c>' line for one of the Mac's own logs local's <c>logs</c> list leaves out (2026-10-23, the Mac pass). Pinned.</summary>
    public const string NotInLocalLogsLine = "not offered: local's logs in eventlog.json leave it out";

    /// <summary>A report that is a link, a FIFO or a device, never opened (2026-10-20).</summary>
    public const string NotARegularFile = "not a regular file";

    /// <summary>A pid that is not a number (<c>/eventlog pid:x</c>; moved here from <c>MacLogCommand</c>, 2026-10-20).</summary>
    public static string BadPid(string text) => $"Error: pid '{text}' is not a process id.";

    // ── The answers ─────────────────────────────────────────────────────────

    /// <summary>Where every answer says it read.</summary>
    public const string OnThisMac = "on this Mac";

    /// <summary><c>--eventlog-check</c> naming anything but <c>local</c> on a Mac.</summary>
    public static string OnlyLocal(string name) => $"'{name}': a Mac reads only its own logs; use --eventlog-check local.";

    /// <summary>The pane's place in its title.</summary>
    public const string OnThisMacWords = "this Mac";

    /// <summary>An entry with no message.</summary>
    public const string NoMessage = "(no message)";

    /// <summary>What an empty read with a text term adds to its sentence (moved here from <c>MacLogRender</c>, 2026-10-20).</summary>
    public const string HoldingThatText = " holding that text";

    /// <summary>A page's header after its count: <c>, newest first (read 20 entries since …)</c> (moved here from <c>MacLogRender</c>, 2026-10-20).</summary>
    public static string NewestFirstRead(string read, string since) => ", newest first (read " + read + " since " + since + ")";

    /// <summary>A .logarchive holding a link inside it (2026-10-23, X4). Pinned.</summary>
    public static string ArchiveHoldsLinks(string archive) =>
        $"Error: {archive} holds links (or is too large to check), which could reach outside it; copy the archive without links into the working directory.";

    /// <summary>An .ips whose body could not be read (cut or malformed, 2026-10-23, X8). Pinned.</summary>
    public const string ReportBodyUnread = "(the report's body could not be read: it is cut short or not the JSON a report holds)";

    /// <summary>The crash folders a listing could not read (2026-10-23, X11). Pinned.</summary>
    public static string FoldersRefused(IReadOnlyList<string> owners) =>
        "(the " + string.Join(" and ", owners) + " report folder" + (owners.Count == 1 ? " was" : "s were") + " not readable from this account, so its reports are not in this answer)";

    /// <summary>What an empty answer adds when the search was narrower than its levels and time (2026-10-23, S2's Mac half); "" when it was not.</summary>
    public static string Narrowed(MacLogQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return query.Process is { Length: > 0 } || query.Subsystem is { Length: > 0 } || query.Category is { Length: > 0 } || query.Pid is not null
            || query.Exclude is { Count: > 0 } || query.Until is not null || query.Text is { Length: > 0 }
            ? " matching the search's other terms"
            : "";
    }

    /// <summary>What an empty read says: <c>no faults or errors in system since an hour ago</c>. Pinned.</summary>
    public static string NoEntries(string log, IReadOnlyList<string> levels, DateTimeOffset since, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(levels);
        string what = levels.Count == 0 ? "entries" : string.Join(" or ", levels.Select(Plural));
        return "no " + what + " in " + log + " since " + EventLogText.Since(since, now, zone);
    }

    private static string Plural(string level) => level switch
    {
        "fault" => "faults",
        "error" => "errors",
        "default" => "default entries",
        "info" => "info entries",
        "debug" => "debug entries",
        _ => level,
    };

    /// <summary>The note when a page's lines did not all fit.</summary>
    public static string Cut(int more) => "(" + EventLogText.Count(more, "more entry", "more entries") + " on this page did not fit; the next call starts at them)";

    /// <summary>The note when a summary has more groups than it shows.</summary>
    public static string MoreGroups(int more) => "(" + EventLogText.Count(more, "more group") + " not shown; filter by process or subsystem to see them)";

    /// <summary>
    /// The note on a read the scan's limit or budget stopped, in <paramref name="kind"/>'s own words (2026-10-25, the Mac pass: an install.log read
    /// said "the unified log is vast" and named subsystem and level, which a file log doesn't take): the crash reports count reports and are narrowed by
    /// process and text, install.log by process, pid and text.
    /// </summary>
    public static string ScanStopped(int read, string? reached, MacLogKind kind = MacLogKind.Unified)
    {
        string back = reached is null ? "" : ", back to " + reached;
        return kind switch
        {
            MacLogKind.Crashes => "(stopped after reading " + EventLogText.Count(read, "report") + back + "; narrow it with process, text or a shorter since)",
            MacLogKind.Install => "(stopped after reading " + EventLogText.Count(read, "entry", "entries") + back + "; install.log is long: narrow it with process, pid, text or a shorter since)",
            _ => "(stopped after reading " + EventLogText.Count(read, "entry", "entries") + back + "; the unified log is vast: narrow it with process, subsystem, level or a shorter since)",
        };
    }

    /// <summary>The note when one process made most of a read (Phase 0: mediaanalysisd made 19,997 of 20,000 errors).</summary>
    /// <remarks>
    /// <paramref name="fenced"/> (2026-10-20, the code review): the model's answers fence the first mention, a name any program chose; the
    /// <c>exclude_process=</c> value stays plain, to be copied into a call. The user's printed lines leave both plain.
    /// </remarks>
    /// <remarks>
    /// <paramref name="copy"/> (2026-10-23, the admin review's S4): the argument to copy, the whole name quoted with the query's own exclusions
    /// kept; the shown name was cut to 40 with "…", unquoted, and the copied call excluded nothing.
    /// </remarks>
    public static string Loudest(string process, int count, int of, bool fenced = false, string? copy = null) =>
        "(" + (fenced ? EventLogText.Quote(process) : process) + " made " + count.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " of the " + of.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)
        + " entries read; exclude_process=" + (copy ?? process) + " leaves it out)";

    /// <summary><c>eventlog_logs</c>' header on a Mac.</summary>
    public const string LogsHeader = "The logs on this Mac (eventlog_search's log names):";

    /// <summary>The unified log's line when it opens.</summary>
    public const string SystemReadable = "the unified log: every process's entries, faults, errors and below";

    /// <summary>The unified log's line when it does not.</summary>
    public static string SystemRefused(string why) => "the unified log, not readable here: " + why;

    /// <summary>No archive in the working directory.</summary>
    public const string NoArchives = "(no .logarchive folder in the working directory; `log collect` makes one)";

    /// <summary>An archive's line.</summary>
    public const string ArchiveLine = "a saved log archive";

    // ── The logs that are files (phase 3) ───────────────────────────────────

    /// <summary>A report folder this account may not list (the system's needs the <c>_analyticsusers</c> group).</summary>
    public const string FolderRefused = "not readable from this account";

    /// <summary>A file log this account may not read.</summary>
    public static string FileRefused(string log) => $"{log} cannot be read from this account.";

    /// <summary>A file log that failed to read, the system's words kept.</summary>
    public static string FileFailed(string log, string why) => $"{log} could not be read: {why}";

    /// <summary>A report read only up to the cap.</summary>
    public static string ReportCut(int max) => "(the report is longer than " + (max / (1024 * 1024)).ToString(System.Globalization.CultureInfo.InvariantCulture) + " MB; only its start was read)";

    /// <summary>A report with nothing to sum it up.</summary>
    public static string ReportOf(string kind) => "a " + kind + " report";

    /// <summary>A report that could not be read.</summary>
    public const string ReportUnreadable = "(this report could not be read)";

    /// <summary>A jetsam report's line: the processes the system ended for memory, and the largest.</summary>
    public static string JetsamSummary(int ended, string? first, string? largest) =>
        "low memory: " + (ended == 0 ? "no process ended" : EventLogText.Count(ended, "process", "processes") + " ended" + (first is null ? "" : ", first " + first))
        + (largest is null ? "" : "; largest " + largest);

    /// <summary>The note under a text report's shown lines.</summary>
    public static string MoreLines(int more) => "(" + EventLogText.Count(more, "more line") + " not shown)";

    /// <summary>The note under a thread's shown frames.</summary>
    public static string MoreFrames(int more) => "(" + EventLogText.Count(more, "more frame") + ")";

    /// <summary>The note when a call gave terms a file log does not have.</summary>
    public static string NotApplied(string log, IReadOnlyList<string> terms) =>
        "(" + string.Join(", ", terms) + (terms.Count == 1 ? " does" : " do") + " not apply to " + log + "; ignored)";

    /// <summary>A file log's handle not written by an answer.</summary>
    public static string BadFileHandle(string text, string log) => log == MacLogAccess.CrashNames[0]
        ? $"Error: '{text}' is not a report's file name (a name an answer shows, like dotnet-2026-10-18-011440.ips)."
        : $"Error: '{text}' is not an install.log entry's handle (the i… an answer shows).";

    /// <summary>A page's cursor naming a report that is gone.</summary>
    public static string CursorGone(string name) => $"Error: report {name} is no longer there; search again without before.";

    /// <summary>A page's cursor naming an install.log entry that is gone (2026-10-20: the file rotated or was cut since the page).</summary>
    public static string InstallCursorGone(string handle) => $"Error: entry {handle} is no longer in install.log; search again without before.";

    /// <summary>A page's cursor gone, in the log's own words.</summary>
    public static string CursorGoneIn(string log, string cursor) => log == MacLogAccess.CrashNames[0] ? CursorGone(cursor) : InstallCursorGone(cursor);

    // ── /eventlog's completion (moved here from MacLogCommand, 2026-10-20, the code review) ──

    /// <summary>The level choices' note.</summary>
    public const string LevelNote = "the level (system and archives)";

    /// <summary>The crash reports' note.</summary>
    public const string CrashesNote = "crash and diagnostic reports";

    /// <summary>install.log's note.</summary>
    public const string InstallNote = "install.log: software installs and updates";

    /// <summary>Each term key's note.</summary>
    public static string KeyNote(string key) => key switch
    {
        "level:" => "fault, error, default, info, debug, all",
        "process:" => "the process by name",
        "subsystem:" => "subsystems starting with this",
        "category:" => "the category",
        "pid:" => "the process id",
        "exclude:" => "processes to leave out",
        "since:" => "an age (2h, 1d) or a moment",
        _ => "an age or a moment",
    };

    /// <summary>What an empty file-log read says: <c>no crash reports since 7 days ago</c>.</summary>
    public static string NoFileEntries(string log, DateTimeOffset since, DateTimeOffset now, TimeZoneInfo zone) =>
        "no " + (log == MacLogAccess.CrashNames[0] ? "crash or diagnostic reports" : "install.log entries") + " since " + EventLogText.Since(since, now, zone);

    /// <summary>The crash reports' line in <c>eventlog_logs</c>.</summary>
    public static string CrashesLine(int week, IReadOnlyList<(string Owner, string? Refused)> folders) =>
        "crash and diagnostic reports: " + EventLogText.Count(week, "report") + " in the last 7 days ("
        + string.Join(", ", folders.Select(f => f.Owner + (f.Refused is null ? "" : " " + f.Refused))) + ")";

    /// <summary>install.log's line in <c>eventlog_logs</c>.</summary>
    public static string InstallLine(string? newest) =>
        "install.log: software installs and updates" + (newest is null ? "" : ", newest entry " + newest);

    /// <summary>install.log's line when it cannot be read.</summary>
    public static string InstallRefused(string why) => "install.log, not readable here: " + why;
}

/// <summary>
/// An OSLog refusal turned into a sentence (2026-10-18). By the <c>NSError</c>'s domain and code (2026-10-20, the code review: the words of
/// its localized description decided before, and they follow the Mac's language), an archive's first: a read of a <c>.logarchive</c> is
/// never "needs an administrator" — a refused file is <see cref="MacLogText.ArchiveRefused"/>, a missing or unreadable one, or any OSLog
/// refusal of it, <see cref="MacLogText.NoArchive"/>. For this Mac's store, Cocoa's and POSIX's rights codes and OSLog's code 9 (the
/// store's "Client lacks entitlement to perform operation", reported for an account that is not an administrator) are
/// <see cref="MacLogText.NeedsAdmin"/>. First of all, the app's own codes 2 and 3 (a filter value or predicate it could not make) and a
/// description naming the predicate (OSLog's own, not localized) are <see cref="MacLogText.FilterRefused"/>; anything else keeps OSLog's
/// description.
/// Pure.
/// </summary>
public static class MacLogErrors
{
    public const string OSLogDomain = "OSLogErrorDomain";
    public const string CocoaDomain = "NSCocoaErrorDomain";
    public const string PosixDomain = "NSPOSIXErrorDomain";
    public const string OwnDomain = "NeonSidekick";

    /// <summary>
    /// OSLog's code for a store the caller may not read: "Client lacks entitlement to perform operation", as reported for a read that is not
    /// an administrator's (not yet seen here on a standard account: MAC.md's open check).
    /// </summary>
    public const long OSLogNotEntitled = 9;

    public static string Sentence(UnifiedLogException failure, string? archive = null)
    {
        ArgumentNullException.ThrowIfNull(failure);
        if ((failure.Domain == OwnDomain && failure.Code is 2 or 3) || failure.Description.Contains("predicate", StringComparison.OrdinalIgnoreCase))
        {
            return MacLogText.FilterRefused;
        }

        if (archive is not null)
        {
            string name = Path.GetFileName(archive.TrimEnd('/'));
            if (IsRightsRefusal(failure))
            {
                return MacLogText.ArchiveRefused(name);
            }

            if (IsMissingOrUnreadable(failure) || failure.Domain == OSLogDomain)
            {
                return MacLogText.NoArchive(name);
            }
        }
        else if (IsRightsRefusal(failure) || (failure.Domain == OSLogDomain && failure.Code == OSLogNotEntitled))
        {
            return MacLogText.NeedsAdmin;
        }

        return MacLogText.Refused(failure.Description);
    }

    /// <summary>Cocoa's no-permission codes (257 reading, 513 writing) and POSIX's EPERM and EACCES.</summary>
    private static bool IsRightsRefusal(UnifiedLogException failure) =>
        (failure.Domain == CocoaDomain && failure.Code is 257 or 513) || (failure.Domain == PosixDomain && failure.Code is 1 or 13);

    /// <summary>Cocoa's no-such-file and unreadable codes (4, 256, 259, 260) and POSIX's ENOENT and ENOTDIR.</summary>
    private static bool IsMissingOrUnreadable(UnifiedLogException failure) =>
        (failure.Domain == CocoaDomain && failure.Code is 4 or 256 or 259 or 260) || (failure.Domain == PosixDomain && failure.Code is 2 or 20);
}
