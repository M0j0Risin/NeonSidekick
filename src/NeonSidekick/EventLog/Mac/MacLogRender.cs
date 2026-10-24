using System.Globalization;
using System.Text;

namespace NeonSidekick.EventLog;

/// <summary>
/// The Mac event logs' answers (2026-10-18, the Mac event-log plan; <see cref="EventLogRender"/>'s shape): a header line first (the
/// transcript's note), every message fenced (<see cref="EventLogText.Quote"/>, <see cref="EventLogText.QuoteNote"/> once), held to
/// <see cref="EventLogRender.MaxChars"/> (one entry to <see cref="EventLogRender.EventMaxChars"/>), how far back a stopped read got, the
/// loudest process when one made most of a read, and the next page as the whole call to make. Pinned by <c>MacLogToolsTests</c>.
/// The process, the subsystem and the category are fenced too (2026-10-20, the code review): any program names its own with
/// <c>os_log_create</c>, as a Windows event's provider is fenced (<see cref="EventLogRender.Line"/>).
/// </summary>
public static class MacLogRender
{
    /// <summary>The level as OSLog names it.</summary>
    public static string LevelWord(int level) => level is >= 1 and <= 5 ? MacLogPredicate.LevelWords[level - 1] : "level " + level.ToString(CultureInfo.InvariantCulture);

    /// <summary>One entry's line: <c>u…  2026-10-18 01:09:34 -05:00  error  ⟪loginwindow⟫[395]  ⟪com.apple.x/Cat⟫  ⟪message⟫</c>.</summary>
    public static string Line(MacLogView view, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(view);
        var e = view.Entry;
        return view.Handle + "  " + EventLogText.Time(e.Time, zone) + "  " + LevelWord(e.Level) + "  " + Who(e) + Where(e)
            + "  " + EventLogText.Quote(Clean(Cut(view.FirstLine ?? MacLogText.NoMessage, EventLogRender.LineMessageChars)));
    }

    public static string Search(MacLogSource source, MacLogQuery query, MacSearchPage page, TimeZoneInfo zone, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(page);
        string header = Clean(source.Log) + " " + MacLogText.OnThisMac + ": " + EventLogText.Count(page.Entries.Count, "entry", "entries")
            + MacLogText.NewestFirstRead(EventLogText.Count(page.Read, "entry", "entries"), EventLogText.Time(query.Since, zone)) + (source.Redacted ? "; " + EventLogText.RedactedNote : "");
        if (page.Entries.Count == 0 && !page.BudgetStop)
        {
            return header + "\n" + MacLogText.NoEntries(Clean(source.Log), query.Levels, query.Since, now, zone) + MacLogText.Narrowed(query) + ".";
        }

        // The tail measured (2026-10-23, the admin review's S10's Mac half: a fixed 300).
        int budget = EventLogRender.MaxChars - header.Length - EventLogText.QuoteNote.Length - 400 - (page.Cursor is { } preview ? NextCall(query, preview, zone).Length : 0);
        var cursor = page.Cursor;
        var lines = new List<string>();
        foreach (var view in page.Entries)
        {
            string line = Line(view, zone);
            if (budget - line.Length - 1 < 0 && lines.Count > 0)
            {
                cursor = page.Entries[lines.Count - 1].Handle;
                break;
            }

            budget -= line.Length + 1;
            lines.Add(line);
        }

        var answer = new StringBuilder(header);
        if (lines.Count > 0)
        {
            answer.Append('\n').Append(EventLogText.QuoteNote);
        }

        foreach (string line in lines)
        {
            answer.Append('\n').Append(line);
        }

        if (lines.Count < page.Entries.Count)
        {
            answer.Append('\n').Append(MacLogText.Cut(page.Entries.Count - lines.Count));
        }

        AppendNotes(answer, page.BudgetStop, page.Read, page.Reached, page.Loudest, zone, query.Exclude);
        if ((page.More || lines.Count < page.Entries.Count) && cursor is { } next)
        {
            answer.Append('\n').Append(NextCall(query, next, zone));
        }

        return answer.ToString();
    }

    public static string Summary(MacLogSource source, MacLogQuery query, MacSummary summary, TimeZoneInfo zone, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(summary);
        string header = Clean(source.Log) + " " + MacLogText.OnThisMac + " since " + EventLogText.Time(query.Since, zone) + ": " + EventLogText.Count(summary.Total, "entry", "entries")
            + " in " + EventLogText.Count(summary.GroupCount, "group") + " (read " + EventLogText.Count(summary.Read, "entry", "entries") + ")"
            + (source.Redacted ? "; " + EventLogText.RedactedNote : "");
        if (summary.Total == 0 && !summary.BudgetStop)
        {
            return header + "\n" + MacLogText.NoEntries(Clean(source.Log), query.Levels, query.Since, now, zone) + ".";
        }

        var answer = new StringBuilder(header);
        if (summary.Groups.Count > 0)
        {
            answer.Append('\n').Append(EventLogText.QuoteNote);
        }

        int budget = EventLogRender.MaxChars - header.Length - 600;
        int shown = 0;
        foreach (var group in summary.Groups)
        {
            string line = SummaryLine(group, zone);
            if (budget - line.Length - 1 < 0 && shown > 0)
            {
                break;
            }

            budget -= line.Length + 1;
            answer.Append('\n').Append(line);
            shown++;
        }

        if (summary.GroupCount > shown)
        {
            answer.Append('\n').Append(MacLogText.MoreGroups(summary.GroupCount - shown));
        }

        AppendNotes(answer, summary.BudgetStop, summary.Read, summary.Reached, summary.Loudest, zone, query.Exclude);
        return answer.ToString();
    }

    /// <summary>A summary's group: <c>412×  error  ⟪mediaanalysisd⟫  ⟪com.apple.mediaanalysis/common⟫  newest … u…  ⟪sample⟫</c>.</summary>
    public static string SummaryLine(MacSummaryGroup group, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(group);
        return group.Count.ToString("N0", CultureInfo.InvariantCulture) + "×  " + LevelWord(group.Level) + "  " + Fence(group.Process.Length > 0 ? group.Process : "?", 40)
            + (group.Subsystem.Length > 0 || group.Category.Length > 0 ? "  " + Fence(group.Subsystem + (group.Category.Length > 0 ? "/" + group.Category : ""), 80) : "")
            + "  newest " + EventLogText.Time(group.Newest, zone) + " " + group.NewestHandle
            + "  " + EventLogText.Quote(Clean(Cut(group.Sample.Length > 0 ? group.Sample : MacLogText.NoMessage, 200)));
    }

    /// <summary>One entry in full: every field, then the whole message, fenced.</summary>
    public static string Event(MacLogSource source, MacLogView view, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(view);
        var e = view.Entry;
        var answer = new StringBuilder();
        answer.Append(Clean(source.Log)).Append(' ').Append(MacLogText.OnThisMac).Append(": entry ").Append(view.Handle)
            .Append(source.Redacted ? "; " + EventLogText.RedactedNote : "");
        answer.Append('\n').Append(EventLogText.QuoteNote);
        answer.Append("\ntime: ").Append(EventLogText.Time(e.Time, zone));
        answer.Append("\nlevel: ").Append(LevelWord(e.Level));
        answer.Append("\nprocess: ").Append(EventLogText.Quote(Clean(e.Process))).Append(" (pid ").Append(e.Pid.ToString(CultureInfo.InvariantCulture))
            .Append(", thread ").Append(e.Thread.ToString(CultureInfo.InvariantCulture)).Append(')');
        if (e.Subsystem.Length > 0)
        {
            answer.Append("\nsubsystem: ").Append(EventLogText.Quote(Clean(e.Subsystem)));
        }

        if (e.Category.Length > 0)
        {
            answer.Append("\ncategory: ").Append(EventLogText.Quote(Clean(e.Category)));
        }

        if (e.Sender.Length > 0 && e.Sender != e.Process)
        {
            answer.Append("\nsender: ").Append(EventLogText.Quote(Clean(view.ShownSender)));   // redacted with the message (2026-10-20)
        }

        string message = view.Message.Length > 0 ? view.Message : MacLogText.NoMessage;
        int room = Room(answer);
        answer.Append("\nmessage:\n").Append(EventLogText.Quote(Clean(message.Length > room ? Files.TextCut.Safe(message, room) + "…" : message)));
        return answer.ToString();
    }

    /// <summary>
    /// <c>eventlog_logs</c> on a Mac: the unified log (readable, or why not), the crash reports and install.log (each its own line, phase
    /// 3), and each <c>.logarchive</c> in the working directory; <paramref name="filter"/> narrows the archives by name. Not
    /// <paramref name="offered"/> (<c>local</c> unticked, 2026-10-20), <paramref name="systemError"/> is the system line's whole text.
    /// </summary>
    public static string Logs(string? systemError, string crashesLine, string installLine, IReadOnlyList<string> archives, string? filter, bool offered = true,
        bool systemAllowed = true)
    {
        ArgumentNullException.ThrowIfNull(archives);
        var shown = archives.Where(a => filter is not { Length: > 0 } f || a.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
        var answer = new StringBuilder(MacLogText.LogsHeader);
        // The unified log left out by local's logs list (2026-10-23, the Mac pass) says so, as the crashes' and install.log's lines do.
        string systemLine = !offered ? systemError ?? "" : !systemAllowed ? MacLogText.NotInLocalLogsLine
            : systemError is null ? MacLogText.SystemReadable : MacLogText.SystemRefused(systemError);
        answer.Append('\n').Append(MacLogAccess.SystemNames[0]).Append("  ").Append(systemLine);
        answer.Append('\n').Append(MacLogAccess.CrashNames[0]).Append("  ").Append(crashesLine);
        answer.Append('\n').Append(MacLogAccess.InstallNames[0]).Append("  ").Append(installLine);
        if (shown.Count == 0)
        {
            answer.Append('\n').Append(MacLogText.NoArchives);
        }

        foreach (string archive in shown)
        {
            answer.Append('\n').Append(Clean(archive)).Append("  ").Append(MacLogText.ArchiveLine);
        }

        return answer.ToString();
    }

    /// <summary>
    /// The next page as the whole call, every argument restated with the times absolute: <c>next: log=system level=error,fault
    /// since=2026-10-18T01:00:00-05:00 before=u1792…</c>. Pinned.
    /// </summary>
    public static string NextCall(MacLogQuery query, MacLogHandle cursor, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(query);
        var parts = new List<string> { "log=" + Value(query.Log), "level=" + (query.Levels.Count == 0 ? "all" : string.Join(",", query.Levels)) };
        Add(parts, "process", query.Process);
        if (query.Pid is { } pid)
        {
            parts.Add("pid=" + pid.ToString(CultureInfo.InvariantCulture));
        }

        Add(parts, "subsystem", query.Subsystem);
        Add(parts, "category", query.Category);
        if (query.Exclude is { Count: > 0 } exclude)
        {
            parts.Add("exclude_process=" + Value(string.Join(",", exclude)));
        }

        parts.Add("since=" + EventLogText.Argument(query.Since, zone));
        if (query.Until is { } until)
        {
            parts.Add("until=" + EventLogText.Argument(until, zone));
        }

        Add(parts, "text", query.Text);
        parts.Add("before=" + cursor);
        parts.Add("limit=" + query.Limit.ToString(CultureInfo.InvariantCulture));   // always (S5's Mac half)

        return "next: " + string.Join(" ", parts);
    }

    // ── The logs that are files (phase 3) ───────────────────────────────────

    /// <summary>
    /// One file-log entry's line: a report's <c>dotnet-2026-10-18-011440.ips  2026-10-18 01:14:40 -05:00  crash  ⟪dotnet⟫[83930]  ⟪summary⟫</c>,
    /// an install entry's <c>i23790500.1792…  2026-10-18 02:28:55 -05:00  ⟪softwareupdated⟫[534]  ⟪message⟫</c>.
    /// </summary>
    public static string FileLine(MacFileView view, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(view);
        return Clean(view.Handle) + "  " + EventLogText.Time(view.Time, zone) + (view.Kind is { } kind ? "  " + kind : "") + "  " + FileWho(view.Process, view.Pid)
            + "  " + EventLogText.Quote(Clean(Cut(view.FirstLine ?? MacLogText.NoMessage, EventLogRender.LineMessageChars)));
    }

    public static string FileSearch(MacLogSource source, MacLogQuery query, MacFilePage page, IReadOnlyList<string> ignored, TimeZoneInfo zone, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(page);
        bool crashes = source.Kind == MacLogKind.Crashes;
        string header = source.Log + " " + MacLogText.OnThisMac + ": " + (crashes ? EventLogText.Count(page.Entries.Count, "report") : EventLogText.Count(page.Entries.Count, "entry", "entries"))
            + MacLogText.NewestFirstRead(crashes ? EventLogText.Count(page.Read, "report") : EventLogText.Count(page.Read, "entry", "entries"), EventLogText.Time(query.Since, zone))
            + (source.Redacted ? "; " + EventLogText.RedactedNote : "");
        if (ignored.Count > 0)
        {
            header += "\n" + MacLogText.NotApplied(source.Log, ignored);
        }

        if (page.Entries.Count == 0 && !page.BudgetStop)
        {
            return header + "\n" + MacLogText.NoFileEntries(source.Log, query.Since, now, zone) + MacLogText.Narrowed(query) + "."
                + (page.Refused is { } emptyRefused ? "\n" + MacLogText.FoldersRefused(emptyRefused) : "");
        }

        int budget = EventLogRender.MaxChars - header.Length - EventLogText.QuoteNote.Length - 400 - (page.Cursor is { } preview ? FileNextCall(query, preview, zone).Length : 0);   // S10
        string? cursor = page.Cursor;
        var lines = new List<string>();
        foreach (var view in page.Entries)
        {
            string line = FileLine(view, zone);
            if (budget - line.Length - 1 < 0 && lines.Count > 0)
            {
                cursor = page.Entries[lines.Count - 1].Handle;
                break;
            }

            budget -= line.Length + 1;
            lines.Add(line);
        }

        var answer = new StringBuilder(header);
        if (lines.Count > 0)
        {
            answer.Append('\n').Append(EventLogText.QuoteNote);
        }

        foreach (string line in lines)
        {
            answer.Append('\n').Append(line);
        }

        if (lines.Count < page.Entries.Count)
        {
            answer.Append('\n').Append(MacLogText.Cut(page.Entries.Count - lines.Count));
        }

        AppendNotes(answer, page.BudgetStop, page.Read, page.Reached, page.Loudest, zone, query.Exclude);
        if (page.Refused is { } refusedFolders)
        {
            answer.Append('\n').Append(MacLogText.FoldersRefused(refusedFolders));   // X11
        }

        if ((page.More || lines.Count < page.Entries.Count) && cursor is { } next)
        {
            answer.Append('\n').Append(FileNextCall(query, next, zone));
        }

        return answer.ToString();
    }

    public static string FileSummary(MacLogSource source, MacLogQuery query, MacFileSummary summary, IReadOnlyList<string> ignored, TimeZoneInfo zone, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(summary);
        bool crashes = source.Kind == MacLogKind.Crashes;
        string header = source.Log + " " + MacLogText.OnThisMac + " since " + EventLogText.Time(query.Since, zone) + ": "
            + (crashes ? EventLogText.Count(summary.Total, "report") : EventLogText.Count(summary.Total, "entry", "entries"))
            + " in " + EventLogText.Count(summary.GroupCount, "group") + " (read " + (crashes ? EventLogText.Count(summary.Read, "report") : EventLogText.Count(summary.Read, "entry", "entries")) + ")"
            + (source.Redacted ? "; " + EventLogText.RedactedNote : "");
        if (ignored.Count > 0)
        {
            header += "\n" + MacLogText.NotApplied(source.Log, ignored);
        }

        if (summary.Total == 0 && !summary.BudgetStop)
        {
            return header + "\n" + MacLogText.NoFileEntries(source.Log, query.Since, now, zone) + ".";
        }

        var answer = new StringBuilder(header);
        if (summary.Groups.Count > 0)
        {
            answer.Append('\n').Append(EventLogText.QuoteNote);
        }

        int budget = EventLogRender.MaxChars - header.Length - 600;
        int shown = 0;
        foreach (var group in summary.Groups)
        {
            string line = group.Count.ToString("N0", CultureInfo.InvariantCulture) + "×  " + (group.Kind is { } kind ? kind + "  " : "") + Fence(group.Process.Length > 0 ? group.Process : "?", 40)
                + "  newest " + EventLogText.Time(group.Newest, zone) + " " + Clean(group.NewestHandle)
                + "  " + EventLogText.Quote(Clean(Cut(group.Sample.Length > 0 ? group.Sample : MacLogText.NoMessage, 200)));
            if (budget - line.Length - 1 < 0 && shown > 0)
            {
                break;
            }

            budget -= line.Length + 1;
            answer.Append('\n').Append(line);
            shown++;
        }

        if (summary.GroupCount > shown)
        {
            answer.Append('\n').Append(MacLogText.MoreGroups(summary.GroupCount - shown));
        }

        AppendNotes(answer, summary.BudgetStop, summary.Read, summary.Reached, summary.Loudest, zone, query.Exclude);
        return answer.ToString();
    }

    /// <summary>One report in full: its time, kind, process, folder and summary, then the report's fields and frames, fenced.</summary>
    public static string CrashEvent(MacLogSource source, CrashReportInfo report, CrashDetail detail, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(detail);
        var answer = new StringBuilder();
        answer.Append(source.Log).Append(' ').Append(MacLogText.OnThisMac).Append(": report ").Append(Clean(report.Name))
            .Append(source.Redacted ? "; " + EventLogText.RedactedNote : "");
        answer.Append('\n').Append(EventLogText.QuoteNote);
        answer.Append("\ntime: ").Append(EventLogText.Time(report.Time, zone));
        answer.Append("\nkind: ").Append(report.Kind);
        answer.Append("\nprocess: ").Append(EventLogText.Quote(Clean(report.Process))).Append(detail.Pid is { } pid ? " (pid " + pid.ToString(CultureInfo.InvariantCulture) + ")" : "");
        answer.Append("\nfolder: ").Append(report.Owner);
        answer.Append("\nsummary: ").Append(EventLogText.Quote(Clean(Cut(detail.Summary, 400))));
        int room = Room(answer);
        string text = detail.Text.Length > 0 ? detail.Text : MacLogText.NoMessage;
        answer.Append("\nreport:\n").Append(EventLogText.Quote(Clean(text.Length > room ? Files.TextCut.Safe(text, room) + "…" : text)));
        return answer.ToString();
    }

    /// <summary>One install.log entry in full.</summary>
    public static string InstallEvent(MacLogSource source, MacFileView view, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(view);
        var answer = new StringBuilder();
        answer.Append(source.Log).Append(' ').Append(MacLogText.OnThisMac).Append(": entry ").Append(view.Handle)
            .Append(source.Redacted ? "; " + EventLogText.RedactedNote : "");
        answer.Append('\n').Append(EventLogText.QuoteNote);
        answer.Append("\ntime: ").Append(EventLogText.Time(view.Time, zone));
        answer.Append("\nprocess: ").Append(EventLogText.Quote(Clean(view.Process.Length > 0 ? view.Process : "?")))
            .Append(view.Pid is { } pid ? " (pid " + pid.ToString(CultureInfo.InvariantCulture) + ")" : "");
        string message = view.Message.Length > 0 ? view.Message : MacLogText.NoMessage;
        int room = Room(answer);
        answer.Append("\nmessage:\n").Append(EventLogText.Quote(Clean(message.Length > room ? Files.TextCut.Safe(message, room) + "…" : message)));
        return answer.ToString();
    }

    /// <summary>A file log's next page as the whole call: <c>next: log=install since=… before=i…</c>. Pinned.</summary>
    public static string FileNextCall(MacLogQuery query, string cursor, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(query);
        var parts = new List<string> { "log=" + Value(query.Log) };
        Add(parts, "process", query.Process);
        if (query.Pid is { } pid)
        {
            parts.Add("pid=" + pid.ToString(CultureInfo.InvariantCulture));
        }

        if (query.Exclude is { Count: > 0 } exclude)
        {
            parts.Add("exclude_process=" + Value(string.Join(",", exclude)));
        }

        parts.Add("since=" + EventLogText.Argument(query.Since, zone));
        if (query.Until is { } until)
        {
            parts.Add("until=" + EventLogText.Argument(until, zone));
        }

        Add(parts, "text", query.Text);
        parts.Add("before=" + Value(cursor));
        parts.Add("limit=" + query.Limit.ToString(CultureInfo.InvariantCulture));   // always (S5)

        return "next: " + string.Join(" ", parts);
    }

    private static string FileWho(string process, int? pid) =>
        Fence(process.Length > 0 ? process : "?", 40) + (pid is { } p ? "[" + p.ToString(CultureInfo.InvariantCulture) + "]" : "");

    private static void AppendNotes(StringBuilder answer, bool stopped, int read, DateTimeOffset? reached, MacLoudest? loudest, TimeZoneInfo zone, IReadOnlyList<string>? exclude = null)
    {
        if (stopped)
        {
            answer.Append('\n').Append(MacLogText.ScanStopped(read, reached is { } r ? EventLogText.Time(r, zone) : null));
        }

        if (loudest is { } loud)
        {
            // The copyable argument is the whole name, quoted, with the query's own exclusions (S4).
            string copy = Value(string.Join(",", (exclude ?? []).Append(loud.Process).Distinct(StringComparer.Ordinal)));
            answer.Append('\n').Append(MacLogText.Loudest(Clean(Cut(loud.Process, 40)), loud.Count, loud.Of, fenced: true, copy: copy));
        }
    }

    private static string Who(MacLogEntry e) => Fence(e.Process.Length > 0 ? e.Process : "?", 40) + "[" + e.Pid.ToString(CultureInfo.InvariantCulture) + "]";

    private static string Where(MacLogEntry e)
    {
        string where = e.Subsystem + (e.Category.Length > 0 ? "/" + e.Category : "");
        return where.Length == 0 ? "" : "  " + Fence(where, 80);
    }

    /// <summary>A name a program chose, cut, scrubbed and fenced as a message is (2026-10-20, the code review).</summary>
    private static string Fence(string text, int max) => EventLogText.Quote(Clean(Cut(text, max)));

    private static void Add(List<string> parts, string name, string? value)
    {
        if (MacLogPredicate.Clean(value) is { Length: > 0 } clean)
        {
            parts.Add(name + "=" + Value(clean));
        }
    }

    private static string Value(string text) =>
        text.Length > 0 && !text.Any(c => char.IsWhiteSpace(c) || c == '"') ? text : "\"" + text.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static string Clean(string text) => EventLogText.Scrub(text);

    private static string Cut(string text, int max)
    {
        string flat = text.Replace('\n', ' ').Replace('\t', ' ');
        return flat.Length <= max ? flat : Files.TextCut.Safe(flat, max - 1) + "…";   // a surrogate pair kept whole (2026-10-23, B12's sibling)
    }

    /// <summary>
    /// What is left of an event's budget for its body, never below 0 (2026-10-23, the admin review's O5: a long process name or subsystem
    /// made it negative and the cut threw). The cut keeps a surrogate pair whole.
    /// </summary>
    private static int Room(System.Text.StringBuilder answer) => Math.Max(0, EventLogRender.EventMaxChars - answer.Length - 40);
}
