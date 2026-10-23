using System.Globalization;
using System.Text;
using NeonSidekick.Files;

namespace NeonSidekick.EventLog;

/// <summary>One log as <c>eventlog_logs</c> lists it: its facts, or that it refused (no access), or another failure's sentence.</summary>
public sealed record EventLogListing(string Name, EventLogInfo? Info, bool NoAccess = false, string? Error = null);

/// <summary>An <c>.evtx</c> under the working directory: its path as the sandbox names it, and its size.</summary>
public sealed record EventLogFile(string Path, long Size);

/// <summary>
/// The event-log tools' answers (2026-10-15, the event-log plan): a line per event in a list (about 400 characters: the record, the local
/// time with its offset, the level, the provider, the id, the message's first line cut to fit), a header that says what was read and how,
/// and the next page as the whole call to make. Every message and field value is fenced as quoted data (<see cref="EventLogText.Quote"/>),
/// and so is every other value the event itself carries — the provider, the source, the channel, the computer, the user and the fields'
/// names (2026-10-20, a review: a crafted <c>.evtx</c> or source could name itself an instruction outside the marks) — every text scrubbed of control characters once (<see cref="EventLogText.Scrub"/>), and an answer held to <see cref="MaxChars"/>
/// (<c>eventlog_event</c>'s to <see cref="EventMaxChars"/>). Pure.
/// </summary>
public static class EventLogRender
{
    /// <summary>The most characters a list or summary answer carries (<c>DockerLogsTool.MaxChars</c>).</summary>
    public const int MaxChars = Llm.Tools.DockerLogsTool.MaxChars;

    /// <summary>The most characters one event's page carries (<see cref="WorkingDirectory.MaxReadChars"/>).</summary>
    public const int EventMaxChars = WorkingDirectory.MaxReadChars;

    /// <summary>How much of a message's first line a list line carries.</summary>
    public const int LineMessageChars = 300;

    /// <summary>How much of a provider's name a list line carries.</summary>
    public const int ProviderChars = 60;

    /// <summary>One event's line in a list; <paramref name="tagged"/> (a forwarded log's page) gives its record with its source's tag (<see cref="EventCursor"/>).</summary>
    public static string Line(EventView view, TimeZoneInfo zone, string? computer = null, bool tagged = false)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(zone);
        var s = view.System;
        string message = Cut(view.FirstLine ?? NoMessage(view), LineMessageChars);
        var line = new StringBuilder();
        line.Append('#').Append(EventCursor.Of(s, tagged).ToString()).Append("  ").Append(EventLogText.Time(s.Time, zone))
            .Append("  ").Append(EventLogText.Level(s.Level, s.Keywords)).Append("  ").Append(Fence(Cut(s.Provider, ProviderChars)))
            .Append("  ").Append(s.Id.ToString(CultureInfo.InvariantCulture));
        if (computer is not null)
        {
            line.Append("  on ").Append(Fence(computer));
        }

        line.Append("  ").Append(EventLogText.Quote(Clean(message)));
        if (view.Unregistered)
        {
            line.Append("  (").Append(EventLogText.UnregisteredSource).Append(')');
        }

        return line.ToString();
    }

    /// <summary>The computer a list line names: a remote read's, or a local event written elsewhere (a forwarded one); else null.</summary>
    public static string? ComputerOf(EventLogSource source, EventView view)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(view);
        string computer = view.System.Computer;
        if (computer.Length == 0)
        {
            return null;
        }

        // This PC by its NetBIOS name, its DNS host name or its full name (2026-10-23, the admin review's S12: MachineName is 15 characters at
        // most, so a long host name made every local event "on ⟪…⟫"); read once a run.
        return source.Remote || !LocalNames.Value.Contains(computer.TrimEnd('.')) && !LocalNames.Value.Contains(computer.Split('.')[0]) ? computer : null;
    }

    /// <summary>This PC's names: NetBIOS, the DNS host name, and the host name with its domain.</summary>
    private static readonly Lazy<HashSet<string>> LocalNames = new(() =>
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Environment.MachineName };
        try
        {
            string host = System.Net.Dns.GetHostName();
            names.Add(host);
            names.Add(host.Split('.')[0]);
            string domain = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().DomainName;
            if (domain.Length > 0)
            {
                names.Add(host.Split('.')[0] + "." + domain);
            }
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or System.Net.NetworkInformation.NetworkInformationException or PlatformNotSupportedException)
        {
            // The NetBIOS name alone.
        }

        return names;
    });

    /// <summary><c>eventlog_search</c>'s answer: the header, the lines that fit, the budget's note, the next call.</summary>
    public static string Search(EventLogSource source, EventLogQuery query, EventSearchPage page, TimeZoneInfo zone, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(page);
        var lines = new List<string>(page.Events.Count + 4);
        string header = Clean(source.Log) + " on " + source.Where + ": " + EventLogText.Count(page.Events.Count, "event") + ", newest first (looked at "
            + EventLogText.Count(page.Scanned, "event") + ")" + (source.Redacted ? "; " + EventLogText.RedactedNote : "");
        if (page.Events.Count == 0 && !page.BudgetStop)
        {
            return header + "\n" + (page.CursorMissing && query.BeforeRecord is { } gone
                ? EventLogText.CursorMissing(gone.ToString())
                : EventLogText.NoEvents(Clean(source.Log), query.Levels, query.Audit, query.Since, now, zone) + EventLogText.Narrowed(query) + "."
                    + (page.Skipped > 0 ? "\n" + EventLogText.SkippedNote(page.Skipped) : ""));
        }

        // The tail measured, not guessed (2026-10-23, the admin review's S10: a fixed 200 for every trailing line, while a next call alone can
        // carry a 256-character text and the providers, ids and fields, so answers ended hundreds past MaxChars).
        int tail = 64 + (page.Cursor is { } preview ? NextCall(source, query, preview, zone).Length : 0) + (page.BudgetStop ? BudgetNote(page.Scanned, page.ReachedTime, zone).Length : 0)
            + (page.Skipped > 0 ? EventLogText.SkippedNote(page.Skipped).Length : 0) + (page.CursorMissing && query.BeforeRecord is { } missing ? EventLogText.CursorMissing(missing.ToString()).Length : 0);
        int budget = MaxChars - header.Length - EventLogText.QuoteNote.Length - tail;
        var cursor = page.Cursor;
        int shown = 0;
        foreach (var view in page.Events)
        {
            string line = Line(view, zone, page.Tagged ? Nonblank(view.System.Computer) : ComputerOf(source, view), page.Tagged);
            if (budget - line.Length - 1 < 0 && shown > 0)
            {
                cursor = EventCursor.Of(page.Events[shown - 1].System, page.Tagged);
                break;
            }

            budget -= line.Length + 1;
            lines.Add(line);
            shown++;
        }

        var answer = new StringBuilder(header);
        if (shown > 0)
        {
            answer.Append('\n').Append(EventLogText.QuoteNote);
        }

        foreach (string line in lines)
        {
            answer.Append('\n').Append(line);
        }

        if (shown < page.Events.Count)
        {
            answer.Append('\n').Append(Cut(page.Events.Count - shown));
        }

        if (page.BudgetStop)
        {
            answer.Append('\n').Append(BudgetNote(page.Scanned, page.ReachedTime, zone));
        }

        if (page.Skipped > 0)
        {
            answer.Append('\n').Append(EventLogText.SkippedNote(page.Skipped));
        }

        if (page.CursorMissing && query.BeforeRecord is { } lost)
        {
            answer.Append('\n').Append(EventLogText.CursorMissing(lost.ToString()));
        }

        bool more = page.More || shown < page.Events.Count;
        if (more && cursor is { } next)
        {
            answer.Append('\n').Append(NextCall(source, query, next, zone));
        }

        return answer.ToString();
    }

    /// <summary>The note on a page the scan's budget stopped. Pinned.</summary>
    public static string BudgetNote(int scanned, DateTimeOffset? reached, TimeZoneInfo zone) =>
        "(searched " + EventLogText.Count(scanned, "event") + (reached is { } r ? " back to " + EventLogText.Time(r, zone) : "") + " without reaching the end; the next call goes on from there)";

    /// <summary>The note when a page's lines did not all fit. Pinned.</summary>
    public static string Cut(int more) => "(" + EventLogText.Count(more, "more event") + " on this page did not fit; the next call starts at them)";

    /// <summary>
    /// The next page as the whole call (2026-10-15, the plan's rule: the model copies it), every argument restated with the times absolute:
    /// <c>next: log=System level=error since=2026-10-13T21:00:00-05:00 before_record=81100</c>. Pinned.
    /// </summary>
    public static string NextCall(EventLogSource source, EventLogQuery query, EventCursor cursor, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(query);
        var parts = new List<string> { "log=" + Value(query.Log) };
        if (!source.IsFile && !source.Computer.IsLocal)
        {
            parts.Add("computer=" + Value(source.Computer.Name));
        }

        parts.Add("level=" + EventLogQuery.LevelWords(query.Levels));
        if (query.Audit != EventAudit.Any)
        {
            parts.Add("audit=" + (query.Audit == EventAudit.Success ? "success" : "failure"));
        }

        if (query.Ids.Count > 0)
        {
            parts.Add("id=" + string.Join(",", query.Ids));
        }

        if (query.Providers.Count > 0)
        {
            // One argument, joined as the tool reads it (2026-10-20, a review: a part per provider could not be copied into one call).
            parts.Add("provider=" + Value(string.Join(",", query.Providers)));
        }

        foreach (var field in query.Fields)
        {
            parts.Add("field=" + Value(field.ToString()));
        }

        if (query.Since is { } since)
        {
            parts.Add("since=" + EventLogText.Argument(since, zone));
        }

        if (query.Until is { } until)
        {
            parts.Add("until=" + EventLogText.Argument(until, zone));
        }

        if (query.Text is { Length: > 0 } text)
        {
            parts.Add("text=" + Value(text));
        }

        // The limit always (2026-10-23, the admin review's S5: it was left out at 20, while a call without one gets Event log max events).
        parts.Add("before_record=" + cursor.ToString());
        parts.Add("limit=" + query.Limit.ToString(CultureInfo.InvariantCulture));

        return "next: " + string.Join(" ", parts);
    }

    /// <summary><c>eventlog_summary</c>'s answer: the header, a line per provider and id by count, the budget's note.</summary>
    public static string Summary(EventLogSource source, EventLogQuery query, EventSummary summary, TimeZoneInfo zone, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(summary);
        string what = query.Audit switch
        {
            EventAudit.Failure => "audit failures",
            EventAudit.Success => "audit successes",
            _ => EventLogQuery.LevelWords(query.Levels).Replace(",", ", ", StringComparison.Ordinal) + " events",
        };
        string header = Clean(source.Log) + " on " + source.Where + (query.Since is { } since ? " since " + EventLogText.Time(since, zone) : "") + ": "
            + EventLogText.Count(summary.Total, "event") + " (" + what + ") of " + EventLogText.Count(summary.GroupCount, "kind") + " (looked at "
            + EventLogText.Count(summary.Scanned, "event") + ")" + (source.Redacted ? "; " + EventLogText.RedactedNote : "");
        if (summary.Total == 0 && !summary.BudgetStop)
        {
            return header + "\n" + EventLogText.NoEvents(Clean(source.Log), query.Levels, query.Audit, query.Since, now, zone) + EventLogText.Narrowed(query) + ".";
        }

        var answer = new StringBuilder(header);
        if (summary.Groups.Count > 0)
        {
            answer.Append('\n').Append(EventLogText.QuoteNote);
        }

        int budget = MaxChars - answer.Length - 200;
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
            answer.Append('\n').Append("(" + EventLogText.Count(summary.GroupCount - shown, "more kind") + " not shown)");
        }

        if (summary.BudgetStop)
        {
            answer.Append('\n').Append("(the counts are of the newest " + EventLogText.Count(summary.Scanned, "event") + " looked at"
                + (summary.ReachedTime is { } r ? ", back to " + EventLogText.Time(r, zone) : "") + ")");
        }

        if (summary.Skipped > 0)
        {
            answer.Append('\n').Append(EventLogText.SkippedNote(summary.Skipped));
        }

        return answer.ToString();
    }

    /// <summary>One summary line: <c>31×  Service Control Manager 7000  error  newest 2026-10-14 21:03:11 -05:00 #81234  ⟪sample⟫</c>.</summary>
    public static string SummaryLine(EventSummaryGroup group, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(group);
        string line = group.Count.ToString(CultureInfo.InvariantCulture) + "×  " + Fence(Cut(group.Provider, ProviderChars)) + " " + group.Id.ToString(CultureInfo.InvariantCulture)
            + "  " + EventLogText.Level(group.Level, group.Keywords) + "  newest " + EventLogText.Time(group.Newest, zone) + " #" + (group.NewestCursor?.ToString() ?? group.NewestRecord.ToString(CultureInfo.InvariantCulture))
            + "  " + EventLogText.Quote(Clean(Cut(group.Sample ?? "(no message)", LineMessageChars)));
        return group.Unregistered ? line + "  (" + EventLogText.UnregisteredSource + ")" : line;
    }

    /// <summary>
    /// <c>eventlog_event</c>'s answer: every system value, the message, the named fields, and the XML under the full detail; clipped at
    /// <see cref="EventMaxChars"/>, a fence the cut splits closed before the note (2026-10-20, a review: the note landed inside an open one).
    /// </summary>
    public static string Event(EventLogSource source, EventView view, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(view);
        var s = view.System;
        var page = new StringBuilder();
        page.Append(Clean(source.Log)).Append(" record ").Append(s.Record.ToString(CultureInfo.InvariantCulture)).Append(" on ").Append(source.Where);
        if (source.Redacted)
        {
            page.Append("; ").Append(EventLogText.RedactedNote);
        }

        page.Append('\n').Append(EventLogText.QuoteNote);
        page.Append("\ntime: ").Append(EventLogText.Time(s.Time, zone));
        page.Append("\nlevel: ").Append(EventLogText.Level(s.Level, s.Keywords));
        page.Append("\nprovider: ").Append(Fence(s.Provider));
        if (view.SourceName is { Length: > 0 } sourceName && !string.Equals(sourceName, s.Provider, StringComparison.OrdinalIgnoreCase))
        {
            page.Append(" (source ").Append(Fence(sourceName)).Append(')');
        }

        if (view.Unregistered)
        {
            page.Append("  (").Append(EventLogText.UnregisteredSource).Append(')');
        }

        page.Append("\nid: ").Append(s.Id.ToString(CultureInfo.InvariantCulture)).Append("   task: ").Append(s.Task.ToString(CultureInfo.InvariantCulture))
            .Append("   opcode: ").Append(s.Opcode.ToString(CultureInfo.InvariantCulture)).Append("   keywords: 0x").Append(s.Keywords.ToString("X", CultureInfo.InvariantCulture));
        page.Append("\nchannel: ").Append(Fence(s.Channel)).Append("   computer: ").Append(Fence(s.Computer));
        if (s.UserSid is { Length: > 0 } sid)
        {
            page.Append("\nuser: ").Append(Fence(sid));
        }

        if (s.ProcessId is { } process)
        {
            page.Append("\nprocess: ").Append(process.ToString(CultureInfo.InvariantCulture)).Append("   thread: ").Append((s.ThreadId ?? 0).ToString(CultureInfo.InvariantCulture));
        }

        page.Append("\nmessage:\n").Append(EventLogText.Quote(Clean(view.Message ?? NoMessage(view))));
        if (view.Fields.Count > 0)
        {
            page.Append("\nfields:");
            foreach (var field in view.Fields)
            {
                page.Append("\n  ").Append(Fence(field.Name)).Append(": ").Append(EventLogText.Quote(Clean(field.Value)));
            }
        }

        if (!source.Redacted && view.Xml is { Length: > 0 } xml)
        {
            page.Append("\nxml:\n").Append(EventLogText.Quote(Clean(xml)));
        }

        string text = page.ToString();
        return text.Length <= EventMaxChars ? text : Llm.Tools.UntrustedText.CutFenced(text, EventMaxChars - 80) + "\n(cut at " + EventMaxChars.ToString("N0", CultureInfo.InvariantCulture) + " characters)";
    }

    /// <summary>
    /// <c>eventlog_logs</c>' answer: the computers offered, the logs listed (or why not), how many were not checked, and the working
    /// directory's <c>.evtx</c> files.
    /// </summary>
    public static string Logs(EventLogCatalog offered, EventLogSource? source, string? filter, IReadOnlyList<EventLogListing> logs, int notChecked, string? logsError, IReadOnlyList<EventLogFile> files, bool filesTruncated, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(offered);
        ArgumentNullException.ThrowIfNull(logs);
        ArgumentNullException.ThrowIfNull(files);
        var answer = new StringBuilder();
        answer.Append("Computers offered: ").Append(offered.Computers.Count == 0 ? "(none ticked)" : string.Join(" · ", offered.Computers.Select(ComputerLine)));
        if (logsError is not null)
        {
            answer.Append('\n').Append(logsError);
        }
        else if (source is not null)
        {
            answer.Append("\nLogs on ").Append(source.Computer.Name).Append(filter is { Length: > 0 } ? " matching \"" + Clean(filter) + "\"" : "").Append(": ")
                .Append(logs.Count == 0 ? "none" : EventLogText.Count(logs.Count, "log"));
            int budget = MaxChars - 2000;
            foreach (var log in logs)
            {
                string line = "  " + LogLine(log, zone, source.Remote);
                if (answer.Length + line.Length > budget)
                {
                    answer.Append("\n  (the rest did not fit; narrow with filter)");
                    break;
                }

                answer.Append('\n').Append(line);
            }

            if (notChecked > 0)
            {
                answer.Append("\n  (").Append(EventLogText.Count(notChecked, "more log")).Append(" not checked; narrow with filter)");
            }
        }

        answer.Append("\nFiles (.evtx in the working directory):");
        if (files.Count == 0)
        {
            answer.Append(" none");
        }

        foreach (var file in files)
        {
            answer.Append("\n  ").Append(Clean(file.Path)).Append("  ").Append(EventLogText.Size(file.Size));
        }

        if (filesTruncated)
        {
            answer.Append("\n  (more files not listed)");
        }

        return answer.ToString();
    }

    /// <summary>A computer as <c>eventlog_logs</c> names it: <c>local (this PC; redacted)</c>, <c>fs01 (fs01.corp.example, file server; full)</c>.</summary>
    public static string ComputerLine(EventLogComputer computer)
    {
        ArgumentNullException.ThrowIfNull(computer);
        string description = computer.Config.Description is { Length: > 0 } d ? ", " + Clean(d.Trim()) : "";
        return computer.Name + " (" + Clean(computer.Where) + description + "; " + computer.Config.DetailWord + ")";
    }

    /// <summary>One log's line: <c>Application  12,345 records · 20.0 MB · last written …</c>, or why not.</summary>
    /// <remarks>
    /// The channel's name and a failure's text fenced (2026-10-23, the admin review's C10: a remote host's manifests name its channels, and its
    /// errors carry its words); the no-access advice for a remote computer is the remote one (S6: "run the app as administrator" helps
    /// nothing there).
    /// </remarks>
    public static string LogLine(EventLogListing log, TimeZoneInfo zone, bool remote = false)
    {
        ArgumentNullException.ThrowIfNull(log);
        string name = Fence(Cut(log.Name, 256));
        if (log.NoAccess)
        {
            return name + "  " + (remote ? EventLogText.NoAccessRemote : EventLogText.NoAccessLocal);
        }

        if (log.Error is { } error)
        {
            return name + "  " + Fence(Cut(error, 300));
        }

        if (log.Info is not { } info)
        {
            return name;
        }

        var parts = new List<string>(4);
        if (info.Type is EventChannelType.Analytic or EventChannelType.Debug)
        {
            parts.Add("read oldest-first only (" + (info.Type == EventChannelType.Analytic ? "Analytic" : "Debug") + ")");
        }

        if (!info.Enabled)
        {
            parts.Add("disabled");
        }

        if (info.Records is { } records)
        {
            parts.Add(EventLogText.Count(records, "record"));
        }

        if (info.SizeBytes is { } size)
        {
            parts.Add(EventLogText.Size(size));
        }

        if (info.LastWrite is { } written)
        {
            parts.Add("last written " + EventLogText.Time(written, zone));
        }

        return parts.Count == 0 ? name : name + "  " + string.Join(" · ", parts);
    }

    /// <summary>What an event with no message says in its place.</summary>
    public static string NoMessage(EventView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        string what = view.Problem == EventMessageProblem.Withheld ? EventLogText.MessageWithheld : "no message";
        return view.Fields.Count == 0 ? "(" + what + ")" : "(" + what + "; fields: " + string.Join(", ", view.Fields.Take(6).Select(f => f.Name + "=" + Cut(f.Value, 60))) + ")";
    }

    private static string Value(string text) =>
        text.Length > 0 && !text.Any(c => char.IsWhiteSpace(c) || c == '"') ? text : "\"" + text.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static string Clean(string text) => EventLogText.Scrub(text);

    /// <summary>A value the event itself carries, scrubbed and fenced: a newline or a tab in it is a space, so the fence stays on its line.</summary>
    private static string Fence(string text) => EventLogText.Quote(Clean(text).Replace('\n', ' ').Replace('\t', ' '));

    private static string? Nonblank(string text) => text.Length > 0 ? text : null;

    private static string Cut(string text, int max)
    {
        // A surrogate pair kept whole (2026-10-23, the admin review's B12, folded site).
        string flat = text.Replace('\n', ' ').Replace('\t', ' ');
        return flat.Length <= max ? flat : Files.TextCut.Safe(flat, max - 1) + "…";
    }
}
