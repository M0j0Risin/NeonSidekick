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
/// </summary>
public sealed record EventSearchPage(IReadOnlyList<EventView> Events, int Scanned, bool More, long? Cursor, bool BudgetStop, DateTimeOffset? ReachedTime);

/// <summary>One provider and id's share of a summary: how many, the newest one's time and record, and a sample message.</summary>
public sealed record EventSummaryGroup(string Provider, int Id, int Level, ulong Keywords, int Count, DateTimeOffset? Newest, long NewestRecord, string? Sample, bool Unregistered);

/// <summary>A summary: the groups by count (the top <see cref="EventLogSearch.SummaryGroups"/>), the events counted, the scan, and whether the budget stopped it.</summary>
public sealed record EventSummary(IReadOnlyList<EventSummaryGroup> Groups, int Total, int GroupCount, int Scanned, bool BudgetStop, DateTimeOffset? ReachedTime);

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
        redacted ? data.Fields.Select(f => new EventField(f.Name, EventLogRedaction.Field(f.Name, f.Value))).ToList() : data.Fields;

    private static EventView View(RawEvent raw, bool redacted, EventXmlData data, IReadOnlyList<EventField> fields, bool withXml)
    {
        var message = redacted ? raw.Format((i, value) => EventLogRedaction.Insert(data.Fields, i, value)) : raw.Format();
        string? text = message.Text ?? data.Message;
        if (redacted && text is not null)
        {
            text = EventLogRedaction.Text(text);
        }

        return new EventView(raw.System, string.IsNullOrWhiteSpace(text) ? null : text.TrimEnd(), message.Text is null ? message.Problem : EventMessageProblem.None, fields, data.SourceName,
            withXml && !redacted ? raw.Xml : null);
    }

    /// <summary>One page of <paramref name="query"/>'s events from <paramref name="reader"/>, under <paramref name="scan"/>.</summary>
    public static EventSearchPage Search(IEventLogReader reader, EventLogQuery query, EventScan scan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(scan);
        long started = scan.Time.GetTimestamp();
        var kept = new List<EventView>(Math.Min(query.Limit, EventLogQuery.MaxLimit));
        int scanned = 0;
        long? lastScanned = null;
        DateTimeOffset? reached = null;
        while (reader.Next() is { } batch)
        {
            foreach (var raw in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (scanned >= scan.ScanLimit || scan.Time.GetElapsedTime(started) >= scan.Budget)
                {
                    return new EventSearchPage(kept, scanned, true, lastScanned, true, reached);
                }

                scanned++;
                lastScanned = raw.System.Record;
                reached = raw.System.Time ?? reached;
                if (Keep(raw, query, scan.Redacted) is not { } view)
                {
                    continue;
                }

                kept.Add(view);
                if (kept.Count >= query.Limit)
                {
                    return new EventSearchPage(kept, scanned, true, view.System.Record, false, reached);
                }
            }
        }

        return new EventSearchPage(kept, scanned, false, null, false, reached);
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
            foreach (var raw in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (scanned >= scan.ScanLimit || scan.Time.GetElapsedTime(started) >= scan.Budget)
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
                    groups[key] = new EventSummaryGroup(system.Provider, system.Id, system.Level, system.Keywords, 1, system.Time, system.Record, view.FirstLine, view.Unregistered);
                    order.Add(key);
                }

                total++;
            }
        }

        var top = order.Select(k => groups[k]).OrderByDescending(g => g.Count).ThenByDescending(g => g.Newest).Take(SummaryGroups).ToList();
        return new EventSummary(top, total, groups.Count, scanned, stopped, reached);
    }

    /// <summary>The event with record <paramref name="record"/>, or null; its XML kept under the full detail.</summary>
    public static EventView? Find(IEventLogReader reader, long record, bool redacted, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        while (reader.Next() is { } batch)
        {
            foreach (var raw in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (raw.System.Record == record)
                {
                    return View(raw, redacted, withXml: true);
                }
            }
        }

        return null;
    }
}
