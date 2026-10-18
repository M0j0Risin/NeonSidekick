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

    /// <summary>OSLog refused the filter itself (a value it could not take).</summary>
    public const string FilterRefused = "the unified log refused that filter; try fewer or plainer terms.";

    /// <summary>Any other refusal, OSLog's own words kept.</summary>
    public static string Refused(string description) => $"the unified log could not be read: {description}";

    // ── The tools' refusals ─────────────────────────────────────────────────

    /// <summary>An <c>.evtx</c> named on a Mac.</summary>
    public const string EvtxIsWindows = "Error: an .evtx file is a Windows event log; on a Mac the logs are system and the working directory's .logarchive folders.";

    /// <summary>A log name that is none of the Mac's.</summary>
    public static string UnknownLog(string name) => $"Error: there is no log named '{name}' on this Mac; eventlog_logs lists them (system, or a .logarchive in the working directory).";

    /// <summary>A read that ran out of time.</summary>
    public const string TimedOut = "Error: the read took too long and was stopped; narrow it (a process, a subsystem, a level, or a shorter since).";

    /// <summary>A level that is none of OSLog's.</summary>
    public static string BadLevel(string word) => $"Error: level '{word}' is not one of fault, error, default, info, debug or all.";

    /// <summary>A handle not written by an answer.</summary>
    public static string BadHandle(string text) => $"Error: '{text}' is not an entry's handle (the u… an answer shows).";

    /// <summary>A handle whose entry is gone.</summary>
    public static string NoEntry(string handle, string log) => $"Error: entry {handle} is no longer in {log}: the log has moved past it.";

    // ── The answers ─────────────────────────────────────────────────────────

    /// <summary>Where every answer says it read.</summary>
    public const string OnThisMac = "on this Mac";

    /// <summary>The pane's place in its title.</summary>
    public const string OnThisMacWords = "this Mac";

    /// <summary>An entry with no message.</summary>
    public const string NoMessage = "(no message)";

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

    /// <summary>The note on a read the scan's limit or budget stopped.</summary>
    public static string ScanStopped(int read, string? reached) =>
        "(stopped after reading " + EventLogText.Count(read, "entry", "entries") + (reached is null ? "" : ", back to " + reached) + "; the unified log is vast: narrow it with process, subsystem, level or a shorter since)";

    /// <summary>The note when one process made most of a read (Phase 0: mediaanalysisd made 19,997 of 20,000 errors).</summary>
    public static string Loudest(string process, int count, int of) =>
        "(" + process + " made " + count.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " of the " + of.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)
        + " entries read; exclude_process=" + process + " leaves it out)";

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
/// An OSLog refusal turned into a sentence (2026-10-18). OSLog's error codes are not documented, so the words of its description
/// decide, with the code a fallback: rights (an account that is not an administrator), a missing archive, a refused filter, else
/// OSLog's own description. Pure.
/// </summary>
public static class MacLogErrors
{
    public static string Sentence(UnifiedLogException failure, string? archive = null)
    {
        ArgumentNullException.ThrowIfNull(failure);
        string words = failure.Description.ToLowerInvariant();
        if (words.Contains("permission", StringComparison.Ordinal) || words.Contains("not permitted", StringComparison.Ordinal)
            || words.Contains("not authorized", StringComparison.Ordinal) || words.Contains("denied", StringComparison.Ordinal))
        {
            return MacLogText.NeedsAdmin;
        }

        if (archive is not null && (words.Contains("no such file", StringComparison.Ordinal) || words.Contains("doesn't exist", StringComparison.Ordinal)
            || words.Contains("couldn't be opened", StringComparison.Ordinal) || words.Contains("not a valid", StringComparison.Ordinal)))
        {
            return MacLogText.NoArchive(Path.GetFileName(archive.TrimEnd('/')));
        }

        if (words.Contains("predicate", StringComparison.Ordinal) || words.Contains("no such field", StringComparison.Ordinal))
        {
            return MacLogText.FilterRefused;
        }

        return MacLogText.Refused(failure.Description);
    }
}
