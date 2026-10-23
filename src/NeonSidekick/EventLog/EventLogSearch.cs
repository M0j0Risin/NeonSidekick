namespace NeonSidekick.EventLog;

/// <summary>
/// How far one read may go (2026-10-15): the detail it is read under, the most events it looks at (<c>Event log scan limit</c>, a quarter
/// of it for a remote computer), the wall-clock budget, and the clock both are measured by.
/// </summary>
public sealed record EventScan(bool Redacted, int ScanLimit, TimeSpan Budget, TimeProvider Time)
{
    /// <summary>The wall-clock budget of one search.</summary>
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(10);
}

/// <summary>
/// A search's page (2026-10-15): the events kept, newest first; how many were looked at; whether more may follow; the cursor the next
/// page starts below (the last event shown, or on a budget stop the last one looked at); and on a budget stop how far back it got.
/// <see cref="Tagged"/> (2026-10-20, a review): the page's cursors name each event's source (<see cref="EventCursor"/>), a forwarded log's;
/// <see cref="CursorMissing"/>: a tagged cursor's event was not found again (the log rolled over it); <see cref="Skipped"/>: events too
/// large to read even alone, stepped over.
/// </summary>
public sealed record EventSearchPage(IReadOnlyList<EventView> Events, int Scanned, bool More, EventCursor? Cursor, bool BudgetStop, DateTimeOffset? ReachedTime,
    bool Tagged = false, bool CursorMissing = false, int Skipped = 0);

/// <summary>One provider and id's share of a summary: how many, the newest one's time and record, and a sample message.</summary>
/// <remarks><c>NewestCursor</c> (2026-10-23, the admin review's S11): a forwarded log's newest by its tagged cursor, its record alone not unique there.</remarks>
public sealed record EventSummaryGroup(string Provider, int Id, int Level, ulong Keywords, int Count, DateTimeOffset? Newest, long NewestRecord, string? Sample, bool Unregistered, EventCursor? NewestCursor = null);

/// <summary>A summary: the groups by count (the top <see cref="EventLogSearch.SummaryGroups"/>), the events counted, the scan, whether the budget stopped it, and the events too large to read.</summary>
public sealed record EventSummary(IReadOnlyList<EventSummaryGroup> Groups, int Total, int GroupCount, int Scanned, bool BudgetStop, DateTimeOffset? ReachedTime, int Skipped = 0);

/// <summary>
/// What <see cref="EventLogSearch.Find"/> found: the event, or none; and when the cursor was a record alone and more than one event holds it
/// (a forwarded log's), each one's tagged cursor and computer, so the answer can ask which (2026-10-20, a review).
/// </summary>
public sealed record EventFound(EventView? View, IReadOnlyList<(EventCursor Cursor, string Computer)> Several);

/// <summary>
/// The policy over any reader (2026-10-15, the event-log plan): the match (<see cref="EventLogMatch"/>), the redaction
/// (<see cref="EventLogRedaction"/>), the scan budget, the cursor and the paging — so the very code the tools run is what the
/// <c>[WindowsFact]</c>s run over the real reader and the tests over the fake. Every event is read in the batch that brought it; nothing
/// is kept across <see cref="IEventLogReader.Next"/>.
/// </summary>
public static class EventLogSearch
{
    /// <summary>How many groups a summary shows.</summary>
    public const int SummaryGroups = 30;

    /// <summary>
    /// The event as an answer shows it: its fields and message, redacted under that detail — the message formatted from the redacted
    /// inserts, then its free text redacted too — and its XML only when <paramref name="withXml"/> under the full detail.
    /// </summary>
    public static EventView View(RawEvent raw, bool redacted, bool withXml = false)
    {
        ArgumentNullException.ThrowIfNull(raw);
        var data = EventXml.Parse(raw.Xml);
        return View(raw, redacted, data, Shown(data, redacted), withXml);
    }

    /// <summary>The fields as the detail shows them: each redacted under <c>redacted</c>.</summary>
    private static IReadOnlyList<EventField> Shown(EventXmlData data, bool redacted) =>
        redacted ? data.Fields.Select(f => new EventField(f.Name, EventLogRedaction.Field(f), f.Binary)).ToList() : data.Fields;

    private static EventView View(RawEvent raw, bool redacted, EventXmlData data, IReadOnlyList<EventField> fields, bool withXml)
    {
        int values = 0;
        var message = redacted
            ? raw.Format((i, value) =>
            {
                values = Math.Max(values, i + 1);
                return EventLogRedaction.Insert(data.Fields, i, value);
            })
            : raw.Format();

        // The inserts are matched to the fields by position; when the event's values and its XML's leaves differ in number (an array or a
        // structure is one value over several leaves, 1 in 4,669 events on the PC checked), a secret's name may not cover its value, so the
        // redacted detail withholds the message (2026-10-23, the admin review's I1). A classic event's trailing <Binary> is one leaf the values
        // may not carry, so the count without it matches too (the branch review of DP7: every such event's message was withheld).
        var problem = message.Text is null ? message.Problem : EventMessageProblem.None;
        string? formatted = message.Text;
        if (redacted && formatted is not null && values > 0 && !ValuesMatch(values, data.Fields))
        {
            formatted = null;
            problem = EventMessageProblem.Withheld;
        }

        // The event's own rendered text (a forwarded event's, its provider not here) was formatted from the raw values, so under redacted
        // each secret field's value is taken out of it first (2026-10-20, a review: the fields hid a password the message still held).
        string? text = formatted;
        if (text is null && problem != EventMessageProblem.Withheld && data.Message is { } own)
        {
            text = redacted ? EventLogRedaction.Rendered(own, data.Fields) : own;
            if (redacted && text is null)
            {
                // Withheld by the redaction, said so (W13: the reader's own reason was given).
                problem = EventMessageProblem.Withheld;
            }
        }

        if (redacted && text is not null)
        {
            text = EventLogRedaction.Text(text);
        }

        return new EventView(raw.System, string.IsNullOrWhiteSpace(text) ? null : text.TrimEnd(), text is null ? problem : EventMessageProblem.None, fields, data.SourceName,
            withXml && !redacted ? raw.Xml : null);
    }

    /// <summary>Whether <paramref name="values"/> inserts line up with <paramref name="fields"/> by position: all of them, or all but a last Binary (DP7).</summary>
    internal static bool ValuesMatch(int values, IReadOnlyList<EventField> fields) =>
        values == fields.Count || (fields.Count > 0 && fields[^1].Binary && values == fields.Count - 1);

    /// <summary>One page of <paramref name="query"/>'s events from <paramref name="reader"/>, under <paramref name="scan"/>.</summary>
    public static EventSearchPage Search(IEventLogReader reader, EventLogQuery query, EventScan scan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(scan);
        long started = scan.Time.GetTimestamp();
        var kept = new List<EventView>(Math.Min(query.Limit, EventLogQuery.MaxLimit));
        int scanned = 0;
        EventSystemValues? lastScanned = null;
        DateTimeOffset? reached = null;

        // A forwarded log's records are the source computers' own (2026-10-20, a review): such a page's cursors are tagged, and a tagged
        // cursor is reached by reading from the top and skipping to it. A log is taken for one when it is ForwardedEvents, when an event's
        // channel is not the log's, or when its events come from more than one computer or channel.
        var skipTo = query.BeforeRecord is { Tagged: true } given ? given : (EventCursor?)null;
        string? channel = EventLogQuery.IsFile(query.Log) ? null : query.Log;
        bool tagged = skipTo is not null || EventLogQuery.IsForwarded(query.Log);
        (string Computer, string Channel)? first = null;
        while (reader.Next() is { } batch)
        {
            // A round of oversized events only (D6): skipped ones count toward the scan limit, and the clock is read.
            if (batch.Count == 0 && (scanned + reader.Skipped >= scan.ScanLimit || scan.Time.GetElapsedTime(started) >= scan.Budget))
            {
                return new EventSearchPage(kept, scanned, true, skipTo ?? (lastScanned is { } last ? EventCursor.Of(last, tagged) : null), true, reached, tagged, Skipped: reader.Skipped);
            }

            foreach (var raw in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (skipTo is { } target)
                {
                    // Down to the cursor's event every one was on an earlier page (or came in since); only the clock holds the skip.
                    if (scan.Time.GetElapsedTime(started) >= scan.Budget)
                    {
                        return new EventSearchPage(kept, scanned, true, target, true, reached, tagged, Skipped: reader.Skipped);
                    }

                    if (target.Is(raw.System))
                    {
                        skipTo = null;
                    }

                    continue;
                }

                // The clock only once an event was looked at (2026-10-23, the admin review's W1: a first batch whose read alone took past the
                // budget was thrown away, the page empty with no cursor to go on from, and the next call did the same).
                if (scanned + reader.Skipped >= scan.ScanLimit || (scanned > 0 && scan.Time.GetElapsedTime(started) >= scan.Budget))
                {
                    return new EventSearchPage(kept, scanned, true, lastScanned is { } last ? EventCursor.Of(last, tagged) : null, true, reached, tagged, Skipped: reader.Skipped);
                }

                scanned++;
                lastScanned = raw.System;
                reached = raw.System.Time ?? reached;
                tagged = tagged || Mixed(raw.System, channel, ref first);
                if (Keep(raw, query, scan.Redacted) is not { } view)
                {
                    continue;
                }

                kept.Add(view);
                if (kept.Count >= query.Limit)
                {
                    return new EventSearchPage(kept, scanned, true, EventCursor.Of(view.System, tagged), false, reached, tagged, Skipped: reader.Skipped);
                }
            }
        }

        return new EventSearchPage(kept, scanned, false, null, false, reached, tagged, CursorMissing: skipTo is not null, Skipped: reader.Skipped);
    }

    /// <summary>Whether <paramref name="system"/> shows the log holds other computers' events: a channel not the log's, or a computer or channel unlike the first event's.</summary>
    private static bool Mixed(EventSystemValues system, string? channel, ref (string Computer, string Channel)? first)
    {
        if (channel is not null && !string.Equals(system.Channel, channel, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (first is not { } seen)
        {
            first = (system.Computer, system.Channel);
            return false;
        }

        return !string.Equals(seen.Computer, system.Computer, StringComparison.OrdinalIgnoreCase) || !string.Equals(seen.Channel, system.Channel, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The event's view when the query keeps it, else null: the system terms first, then (only when there are any) the fields and the
    /// text over the event's view, the message formatted only if the text is still undecided.
    /// </summary>
    private static EventView? Keep(RawEvent raw, EventLogQuery query, bool redacted)
    {
        if (!EventLogMatch.System(raw.System, query))
        {
            return null;
        }

        var data = EventXml.Parse(raw.Xml);
        var fields = Shown(data, redacted);
        if (query.Fields.Count > 0 && !EventLogMatch.Fields(fields, query))
        {
            return null;
        }

        // The message is formatted only when the provider and the fields did not already decide the text.
        EventView? view = null;
        if (!EventLogMatch.Text(query.Text, raw.System, fields, () => (view ??= View(raw, redacted, data, fields, withXml: false)).Message))
        {
            return null;
        }

        return view ?? View(raw, redacted, data, fields, withXml: false);
    }

    /// <summary>Counts <paramref name="query"/>'s events by provider and id, the newest of each with a sample message, under <paramref name="scan"/>.</summary>
    public static EventSummary Summarize(IEventLogReader reader, EventLogQuery query, EventScan scan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(scan);
        long started = scan.Time.GetTimestamp();
        var groups = new Dictionary<(string, int), EventSummaryGroup>();
        var order = new List<(string, int)>();
        int scanned = 0;
        int total = 0;
        bool stopped = false;
        DateTimeOffset? reached = null;
        while (!stopped && reader.Next() is { } batch)
        {
            if (batch.Count == 0 && (scanned + reader.Skipped >= scan.ScanLimit || scan.Time.GetElapsedTime(started) >= scan.Budget))
            {
                stopped = true;
                break;
            }

            foreach (var raw in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (scanned + reader.Skipped >= scan.ScanLimit || (scanned > 0 && scan.Time.GetElapsedTime(started) >= scan.Budget))   // W1
                {
                    stopped = true;
                    break;
                }

                scanned++;
                reached = raw.System.Time ?? reached;
                var system = raw.System;
                if (!EventLogMatch.System(system, query))
                {
                    continue;
                }

                var key = (system.Provider.ToUpperInvariant(), system.Id);
                if (groups.TryGetValue(key, out var group))
                {
                    groups[key] = group with { Count = group.Count + 1 };
                }
                else
                {
                    // The newest of the group is the first seen (newest first): its message is the sample, formatted once.
                    var view = View(raw, scan.Redacted);
                    groups[key] = new EventSummaryGroup(system.Provider, system.Id, system.Level, system.Keywords, 1, system.Time, system.Record, view.FirstLine, view.Unregistered,
                        EventLogQuery.IsForwarded(query.Log) ? EventCursor.Of(system, tagged: true) : null);
                    order.Add(key);
                }

                total++;
            }
        }

        var top = order.Select(k => groups[k]).OrderByDescending(g => g.Count).ThenByDescending(g => g.Newest).Take(SummaryGroups).ToList();
        return new EventSummary(top, total, groups.Count, scanned, stopped, reached, reader.Skipped);
    }

    /// <summary>
    /// The event <paramref name="cursor"/> names, its XML kept under the full detail; a tagged cursor's first match. A record alone reads on:
    /// when other events hold it too (a forwarded log's, from other computers or channels), <see cref="EventFound.Several"/> lists each.
    /// </summary>
    /// <remarks>
    /// <paramref name="mayRepeat"/> (2026-10-23, the admin review's B1): only a forwarded log or a file can hold a record twice, so elsewhere the
    /// first match ends the read; it read on to the oldest event, and on a big Security log timed out and lost the match it had.
    /// </remarks>
    public static EventFound Find(IEventLogReader reader, EventCursor cursor, bool redacted, CancellationToken cancellationToken, bool mayRepeat = true)
    {
        ArgumentNullException.ThrowIfNull(reader);
        EventView? found = null;
        var several = new List<(EventCursor Cursor, string Computer)>();
        while (reader.Next() is { } batch)
        {
            foreach (var raw in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!cursor.Is(raw.System))
                {
                    continue;
                }

                if (cursor.Tagged || !mayRepeat)
                {
                    return new EventFound(View(raw, redacted, withXml: true), []);
                }

                var tagged = EventCursor.Of(raw.System, tagged: true);
                if (several.All(s => s.Cursor != tagged))
                {
                    several.Add((tagged, raw.System.Computer));
                }

                found ??= View(raw, redacted, withXml: true);
            }
        }

        return new EventFound(found, several.Count > 1 ? several : []);
    }
}
