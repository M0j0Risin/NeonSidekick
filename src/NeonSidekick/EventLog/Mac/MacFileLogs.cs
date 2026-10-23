namespace NeonSidekick.EventLog;

/// <summary>
/// One entry of a Mac log that is a file, as an answer shows it (2026-10-18, the Mac event-log plan's phase 3): its handle (a report's file
/// name, or install.log's <c>i…</c>), its time, its kind (a report's; null for install.log), the process and pid, and the line (a report's
/// summary, an install entry's message), redacted when the read is.
/// </summary>
public sealed record MacFileView(string Handle, DateTimeOffset Time, string? Kind, string Process, int? Pid, string Message, string? Owner = null)
{
    /// <summary>The message's first line, or null when it is blank.</summary>
    public string? FirstLine => Message.Split('\n', 2)[0].Trim() is { Length: > 0 } first ? first : null;
}

/// <summary>A page of a file log: the entries kept, newest first; how many were read; whether more may follow and the handle the next page starts after; a stop and how far back; the loudest process.</summary>
/// <remarks><c>Refused</c> (2026-10-23, the admin review's X11): the crash folders this account could not read, said rather than "no crashes".</remarks>
public sealed record MacFilePage(IReadOnlyList<MacFileView> Entries, int Read, bool More, string? Cursor, bool BudgetStop, DateTimeOffset? Reached, MacLoudest? Loudest,
    IReadOnlyList<string>? Refused = null);

/// <summary>One process (and kind)'s share of a file log's summary.</summary>
public sealed record MacFileGroup(string Process, string? Kind, int Count, DateTimeOffset Newest, string NewestHandle, string Sample);

/// <summary>A file log's summary: the groups by count, the entries counted, the read, a stop and how far back, the loudest process.</summary>
public sealed record MacFileSummary(IReadOnlyList<MacFileGroup> Groups, int Total, int GroupCount, int Read, bool BudgetStop, DateTimeOffset? Reached, MacLoudest? Loudest,
    IReadOnlyList<string>? Refused = null);

/// <summary>
/// The policy over the Mac's two logs that are files (2026-10-18, the Mac event-log plan's phase 3; <see cref="MacLogSearch"/>'s shape):
/// <c>crashes</c>, the reports of <see cref="CrashReports"/>, and <c>install</c>, <see cref="InstallLog"/>. A query's process (case
/// ignored, as file names are), pid (install.log's), processes left out, window and text apply; its levels, subsystem and category do
/// not (the tools say so). A report's text search reads the report in full and matches what the answer would show (redacted when the
/// read is), so a twin learns nothing it may not see; install.log's matches the message the same way. The scan's limit and budget stop a
/// read as on the unified log, a summary of the reports too (2026-10-20, the code review: it read every report in the window, each up to
/// 4 MB with a text term, whatever the limit). A page's cursor that names nothing the log still holds is said (a null page: the callers
/// answer <see cref="MacLogText.CursorGone"/>), never read past silently: a report gone from the listing, an install.log entry whose
/// offset no longer starts the entry at that second (the file rotated or was cut). A report's handle names its folder when the name is in
/// two (<see cref="CrashReports.HandleOf"/>).
/// </summary>
public static class MacFileLogs
{
    /// <summary>The window when a call gives none: a week of reports, a day of install.log (it gains about 170 KB a day).</summary>
    public static readonly TimeSpan CrashWindow = TimeSpan.FromDays(7);

    public static readonly TimeSpan InstallWindow = TimeSpan.FromDays(1);

    /// <summary>How many entries older than the window in a row end an install.log read (X9).</summary>
    public const int OlderRun = 50;

    /// <summary>The redaction a file log's text gets: the home as <c>~</c>, then the secret-like values hidden.</summary>
    public static Func<string, string> Redactor(MacLogPlaces places, bool redacted)
    {
        ArgumentNullException.ThrowIfNull(places);
        // Other accounts' homes hidden too (2026-10-23, the admin review's X2).
        return redacted ? text => EventLogRedaction.Text(MacLogPlaces.OtherHomes(places.Tilde(text))) : text => text;
    }

    // ── crashes ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The query's reports, newest first, the page after <c>before</c> (a report's handle) when given; null when <c>before</c> names no report
    /// of the listing (it is gone).
    /// </summary>
    public static MacFilePage? SearchCrashes(MacLogPlaces places, MacLogQuery query, EventScan scan, string? before, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(places);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(scan);
        long started = scan.Time.GetTimestamp();
        var redact = Redactor(places, scan.Redacted);
        var (reports, states) = CrashReports.List(places.CrashFolders, query.Since, query.Until, cancellationToken);
        var refused = Refused(states);
        if (before is not null && !reports.Any(r => CrashReports.Names(before, r)))
        {
            return null;
        }

        var shared = SharedNames(reports);
        var walk = new Tally();
        var kept = new List<MacFileView>();
        bool passed = before is null;
        string? lastRead = null;
        for (int at = 0; at < reports.Count; at++)
        {
            var report = reports[at];
            cancellationToken.ThrowIfCancellationRequested();
            if (!passed)
            {
                passed = CrashReports.Names(before!, report);
                continue;
            }

            if (!Keeps(query, report.Process, null))
            {
                continue;
            }

            if (walk.Read >= scan.ScanLimit || scan.Time.GetElapsedTime(started) >= scan.Budget)
            {
                return new MacFilePage(kept, walk.Read, true, lastRead ?? before, true, walk.Reached, walk.Loudest(), refused);
            }

            walk.Step(report.Process, report.Time);
            string handle = CrashReports.HandleOf(report, shared.Contains(report.Name));
            lastRead = handle;

            // The listing's line from at most a megabyte unless a text term needs the whole (2026-10-23, the admin review's I7: every listed
            // report was read in full, up to 4 MB and a JSON parse each; real reports are 15 to 500 KB, so a megabyte keeps their lines).
            var detail = query.Text is { Length: > 0 } ? Safe(report, redact) : Safe(report, redact, CrashReports.ListingBytes);
            if (query.Text is { Length: > 0 } && !query.KeepsText(report.Name + "\n" + report.Process + "\n" + detail.Summary + "\n" + detail.Text))
            {
                continue;
            }

            kept.Add(new MacFileView(handle, report.Time, report.Kind, report.Process, detail.Pid, detail.Summary, report.Owner));
            if (kept.Count >= query.Limit)
            {
                bool more = at < reports.Count - 1;
                return new MacFilePage(kept, walk.Read, more, more ? handle : null, false, walk.Reached, walk.Loudest(), refused);
            }
        }

        return new MacFilePage(kept, walk.Read, false, null, false, walk.Reached, walk.Loudest(), refused);
    }

    /// <summary>The folders a listing could not read, by owner (X11).</summary>
    private static IReadOnlyList<string>? Refused(List<(MacCrashFolder Folder, string? Refused)> states)
    {
        var refused = states.Where(s => s.Refused is not null).Select(s => s.Folder.Owner).ToList();
        return refused.Count == 0 ? null : refused;
    }

    /// <summary>The names more than one folder holds: their handles name the folder too.</summary>
    private static HashSet<string> SharedNames(List<CrashReportInfo> reports) =>
        reports.GroupBy(r => r.Name, StringComparer.Ordinal).Where(g => g.Select(r => r.Owner).Distinct(StringComparer.Ordinal).Count() > 1)
            .Select(g => g.Key).ToHashSet(StringComparer.Ordinal);

    /// <summary>The window's reports counted by process and kind, the busiest first, under the scan's limit and budget.</summary>
    public static MacFileSummary SummarizeCrashes(MacLogPlaces places, MacLogQuery query, EventScan scan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(places);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(scan);
        long started = scan.Time.GetTimestamp();
        var redact = Redactor(places, scan.Redacted);
        var (reports, states) = CrashReports.List(places.CrashFolders, query.Since, query.Until, cancellationToken);
        var shared = SharedNames(reports);
        var groups = new Dictionary<(string, string?), (MacFileGroup Group, CrashReportInfo Newest)>();
        var walk = new Tally();
        int total = 0;
        bool stopped = false;
        foreach (var report in reports.Where(r => Keeps(query, r.Process, null)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (walk.Read >= scan.ScanLimit || scan.Time.GetElapsedTime(started) >= scan.Budget)
            {
                stopped = true;
                break;
            }

            walk.Step(report.Process, report.Time);
            if (query.Text is { Length: > 0 })
            {
                var detail = Safe(report, redact);
                if (!query.KeepsText(report.Name + "\n" + report.Process + "\n" + detail.Summary + "\n" + detail.Text))
                {
                    continue;
                }
            }

            total++;
            var key = (report.Process, (string?)report.Kind);
            groups[key] = groups.TryGetValue(key, out var group)
                ? (group.Group with { Count = group.Group.Count + 1 }, group.Newest)
                : (new MacFileGroup(report.Process, report.Kind, 1, report.Time, CrashReports.HandleOf(report, shared.Contains(report.Name)), ""), report);
        }

        var top = groups.Values.OrderByDescending(g => g.Group.Count).ThenByDescending(g => g.Group.Newest).Take(MacLogSearch.SummaryGroups)
            .Select(g => g.Group with { Sample = Safe(g.Newest, redact).Summary })
            .ToList();
        return new MacFileSummary(top, total, groups.Count, walk.Read, stopped, walk.Reached, walk.Loudest(), Refused(states));
    }

    /// <summary>One report in full by its handle (its name, and its folder when the handle names one); null when no folder holds it.</summary>
    public static (CrashReportInfo Report, CrashDetail Detail)? CrashEvent(MacLogPlaces places, string name, bool redacted)
    {
        ArgumentNullException.ThrowIfNull(places);
        return CrashReports.Find(places.CrashFolders, name) is { } report ? (report, CrashReports.Describe(report, Redactor(places, redacted))) : null;
    }

    private static CrashDetail Safe(CrashReportInfo report, Func<string, string> redact, int maxBytes = CrashReports.MaxBytes)
    {
        try
        {
            return CrashReports.Describe(report, redact, maxBytes);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException or FormatException or ArgumentException)
        {
            return new CrashDetail(MacLogText.ReportUnreadable, "", null);   // one odd report never ends the listing (2026-10-20)
        }
    }

    // ── install.log ─────────────────────────────────────────────────────────

    /// <summary>
    /// The query's install.log entries, newest first, below <paramref name="before"/> (an entry's <c>i…</c> handle) when given; null when the
    /// handle's entry is no longer at its offset with its second (2026-10-20, the code review: the seconds were dropped and a rotated file
    /// read from the same offset gave another page, or the same one again).
    /// </summary>
    public static MacFilePage? SearchInstall(Stream log, MacLogPlaces places, MacLogQuery query, EventScan scan, string? before, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(scan);
        long? below = null;
        if (before is not null)
        {
            if (!InstallLog.TryParseHandle(before, out long offset, out long seconds) || InstallLog.At(log, offset) is not { } cursor || cursor.Time.ToUnixTimeSeconds() != seconds)
            {
                return null;
            }

            below = offset;
        }

        long started = scan.Time.GetTimestamp();
        var redact = Redactor(places, scan.Redacted);
        var walk = new Tally();
        var kept = new List<MacFileView>();
        string? last = null;
        int older = 0;
        foreach (var entry in InstallLog.Backward(log, below, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Time < query.Since)
            {
                // The log is written in order, but a line stamped with a reset clock (2001-01-01, a setup) is passed over (2026-10-23, the
                // admin review's X9): only a run of older entries ends the window.
                if (++older >= OlderRun)
                {
                    break;
                }

                continue;
            }

            older = 0;
            if (scan.Time.GetElapsedTime(started) >= scan.Budget)
            {
                return new MacFilePage(kept, walk.Read, true, last, true, walk.Reached, walk.Loudest());
            }

            if (query.Until is { } until && entry.Time > until)
            {
                // Newer than the window: stepped over without spending the scan limit, its place kept (2026-10-23, the admin review's I2: they
                // spent it, so the window was never reached and no next page was given).
                last = InstallLog.Handle(entry);
                continue;
            }

            if (walk.Read >= scan.ScanLimit)
            {
                return new MacFilePage(kept, walk.Read, true, last, true, walk.Reached, walk.Loudest());
            }

            walk.Step(entry.Process, entry.Time);
            last = InstallLog.Handle(entry);
            if (!Keeps(query, entry.Process, entry.Pid))
            {
                continue;
            }

            string message = redact(entry.Message);
            if (!query.KeepsText(message))
            {
                continue;
            }

            kept.Add(new MacFileView(last, entry.Time, null, entry.Process, entry.Pid, message));
            if (kept.Count >= query.Limit)
            {
                return new MacFilePage(kept, walk.Read, true, last, false, walk.Reached, walk.Loudest());
            }
        }

        return new MacFilePage(kept, walk.Read, false, null, false, walk.Reached, walk.Loudest());
    }

    /// <summary>The window's install.log entries counted by process, the busiest first.</summary>
    public static MacFileSummary SummarizeInstall(Stream log, MacLogPlaces places, MacLogQuery query, EventScan scan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(scan);
        long started = scan.Time.GetTimestamp();
        var redact = Redactor(places, scan.Redacted);
        var groups = new Dictionary<string, MacFileGroup>(StringComparer.Ordinal);
        var walk = new Tally();
        int total = 0;
        bool stopped = false;
        int older = 0;
        foreach (var entry in InstallLog.Backward(log, null, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Time < query.Since)
            {
                if (++older >= OlderRun)
                {
                    break;   // X9: a run of older entries, not one
                }

                continue;
            }

            older = 0;

            if (walk.Read >= scan.ScanLimit || scan.Time.GetElapsedTime(started) >= scan.Budget)
            {
                stopped = true;
                break;
            }

            walk.Step(entry.Process, entry.Time);
            if ((query.Until is { } until && entry.Time > until) || !Keeps(query, entry.Process, entry.Pid))
            {
                continue;
            }

            string message = redact(entry.Message);
            if (!query.KeepsText(message))
            {
                continue;
            }

            total++;
            string first = message.Split('\n', 2)[0].Trim();
            groups[entry.Process] = groups.TryGetValue(entry.Process, out var group)
                ? group with { Count = group.Count + 1 }
                : new MacFileGroup(entry.Process, null, 1, entry.Time, InstallLog.Handle(entry), first);
        }

        var top = groups.Values.OrderByDescending(g => g.Count).ThenByDescending(g => g.Newest).Take(MacLogSearch.SummaryGroups).ToList();
        return new MacFileSummary(top, total, groups.Count, walk.Read, stopped, walk.Reached, walk.Loudest());
    }

    /// <summary>The install.log entry a handle names, its time checked again; null when it is gone.</summary>
    public static MacFileView? InstallEvent(Stream log, MacLogPlaces places, string handle, bool redacted)
    {
        ArgumentNullException.ThrowIfNull(log);
        if (!InstallLog.TryParseHandle(handle, out long offset, out long seconds)
            || InstallLog.At(log, offset) is not { } entry
            || entry.Time.ToUnixTimeSeconds() != seconds)
        {
            return null;
        }

        return new MacFileView(InstallLog.Handle(entry), entry.Time, null, entry.Process, entry.Pid, Redactor(places, redacted)(entry.Message));
    }

    private static bool Keeps(MacLogQuery query, string process, int? pid)
    {
        if (MacLogPredicate.Clean(query.Process) is { Length: > 0 } wanted && !string.Equals(process, wanted, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (query.Pid is { } p && pid != p)
        {
            return false;
        }

        return query.Exclude is not { Count: > 0 } exclude || !exclude.Contains(process, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>A read's count, how far back it got and each process's share (the loudest-process note).</summary>
    private sealed class Tally
    {
        private readonly Dictionary<string, int> _processes = new(StringComparer.Ordinal);

        public int Read { get; private set; }

        public DateTimeOffset? Reached { get; private set; }

        public void Step(string process, DateTimeOffset time)
        {
            Read++;
            Reached = Reached is { } r && r < time ? r : time;
            _processes[process] = _processes.GetValueOrDefault(process) + 1;
        }

        public MacLoudest? Loudest()
        {
            if (Read < 20 || _processes.Count < 2)
            {
                return null;
            }

            var (process, count) = _processes.MaxBy(p => p.Value);
            return count > Read * MacLogSearch.LoudShare ? new MacLoudest(process, count, Read) : null;
        }
    }
}
