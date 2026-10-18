using System.Globalization;
using System.Text;

namespace NeonSidekick.EventLog;

/// <summary>
/// The Mac event logs' answers (2026-10-18, the Mac event-log plan; <see cref="EventLogRender"/>'s shape): a header line first (the
/// transcript's note), every message fenced (<see cref="EventLogText.Quote"/>, <see cref="EventLogText.QuoteNote"/> once), held to
/// <see cref="EventLogRender.MaxChars"/> (one entry to <see cref="EventLogRender.EventMaxChars"/>), how far back a stopped read got, the
/// loudest process when one made most of a read, and the next page as the whole call to make. Pinned by <c>MacLogToolsTests</c>.
/// </summary>
public static class MacLogRender
{
    /// <summary>The level as OSLog names it.</summary>
    public static string LevelWord(int level) => level is >= 1 and <= 5 ? MacLogPredicate.LevelWords[level - 1] : "level " + level.ToString(CultureInfo.InvariantCulture);

    /// <summary>One entry's line: <c>u…  2026-10-18 01:09:34 -05:00  error  loginwindow[395]  com.apple.x/Cat  ⟪message⟫</c>.</summary>
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
        string header = Clean(source.Log) + " " + MacLogText.OnThisMac + ": " + EventLogText.Count(page.Entries.Count, "entry", "entries") + ", newest first (read "
            + EventLogText.Count(page.Read, "entry", "entries") + " since " + EventLogText.Time(query.Since, zone) + ")" + (source.Redacted ? "; " + EventLogText.RedactedNote : "");
        if (page.Entries.Count == 0 && !page.BudgetStop)
        {
            return header + "\n" + MacLogText.NoEntries(Clean(source.Log), query.Levels, query.Since, now, zone) + (query.Text is { Length: > 0 } ? " holding that text" : "") + ".";
        }

        int budget = EventLogRender.MaxChars - header.Length - EventLogText.QuoteNote.Length - 300;
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

        AppendNotes(answer, page.BudgetStop, page.Read, page.Reached, page.Loudest, zone);
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

        AppendNotes(answer, summary.BudgetStop, summary.Read, summary.Reached, summary.Loudest, zone);
        return answer.ToString();
    }

    /// <summary>A summary's group: <c>412×  error  mediaanalysisd  com.apple.mediaanalysis/common  newest … u…  ⟪sample⟫</c>.</summary>
    public static string SummaryLine(MacSummaryGroup group, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(group);
        return group.Count.ToString("N0", CultureInfo.InvariantCulture) + "×  " + LevelWord(group.Level) + "  " + Clean(Cut(group.Process, 40))
            + (group.Subsystem.Length > 0 || group.Category.Length > 0 ? "  " + Clean(Cut(group.Subsystem + (group.Category.Length > 0 ? "/" + group.Category : ""), 80)) : "")
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
            answer.Append("\nsender: ").Append(EventLogText.Quote(Clean(e.Sender)));
        }

        string message = view.Message.Length > 0 ? view.Message : MacLogText.NoMessage;
        int room = EventLogRender.EventMaxChars - answer.Length - 40;
        answer.Append("\nmessage:\n").Append(EventLogText.Quote(Clean(message.Length > room ? message[..room] + "…" : message)));
        return answer.ToString();
    }

    /// <summary>
    /// <c>eventlog_logs</c> on a Mac: the unified log (readable, or why not), each <c>.logarchive</c> in the working directory, and (from
    /// phase 3) the crash reports and install.log; <paramref name="filter"/> narrows the archives by name.
    /// </summary>
    public static string Logs(string? systemError, IReadOnlyList<string> archives, string? filter)
    {
        ArgumentNullException.ThrowIfNull(archives);
        var shown = archives.Where(a => filter is not { Length: > 0 } f || a.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
        var answer = new StringBuilder(MacLogText.LogsHeader);
        answer.Append('\n').Append(MacLogAccess.SystemNames[0]).Append("  ").Append(systemError is null ? MacLogText.SystemReadable : MacLogText.SystemRefused(systemError));
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
        if (query.Limit != 20)
        {
            parts.Add("limit=" + query.Limit.ToString(CultureInfo.InvariantCulture));
        }

        return "next: " + string.Join(" ", parts);
    }

    private static void AppendNotes(StringBuilder answer, bool stopped, int read, DateTimeOffset? reached, MacLoudest? loudest, TimeZoneInfo zone)
    {
        if (stopped)
        {
            answer.Append('\n').Append(MacLogText.ScanStopped(read, reached is { } r ? EventLogText.Time(r, zone) : null));
        }

        if (loudest is { } loud)
        {
            answer.Append('\n').Append(MacLogText.Loudest(Clean(loud.Process), loud.Count, loud.Of));
        }
    }

    private static string Who(MacLogEntry e) => Clean(Cut(e.Process.Length > 0 ? e.Process : "?", 40)) + "[" + e.Pid.ToString(CultureInfo.InvariantCulture) + "]";

    private static string Where(MacLogEntry e)
    {
        string where = e.Subsystem + (e.Category.Length > 0 ? "/" + e.Category : "");
        return where.Length == 0 ? "" : "  " + Clean(Cut(where, 80));
    }

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
        return flat.Length <= max ? flat : flat[..(max - 1)] + "…";
    }
}
