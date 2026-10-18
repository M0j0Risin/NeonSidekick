namespace NeonSidekick.EventLog;

/// <summary>One entry as an answer shows it (2026-10-18): the entry, its handle, and its message under the read's detail.</summary>
public sealed record MacLogView(MacLogEntry Entry, MacLogHandle Handle, string Message)
{
    /// <summary>The message's first line, or null when it is blank.</summary>
    public string? FirstLine => Message.Split('\n', 2)[0].Trim() is { Length: > 0 } first ? first : null;
}

/// <summary>The process that made most of a read, and how many of the entries read were its (the plan's noise note).</summary>
public readonly record struct MacLoudest(string Process, int Count, int Of);

/// <summary>
/// A search's page (2026-10-18): the entries kept, newest first; how many the read let through; whether more may follow; the handle the
/// next page starts below; whether the scan's limit or budget stopped it and how far back it got; the loudest process.
/// </summary>
public sealed record MacSearchPage(IReadOnlyList<MacLogView> Entries, int Read, bool More, MacLogHandle? Cursor, bool BudgetStop, DateTimeOffset? Reached, MacLoudest? Loudest);

/// <summary>One process, subsystem and category's share of a summary: how many, the highest level, the newest one and a sample message.</summary>
public sealed record MacSummaryGroup(string Process, string Subsystem, string Category, int Level, int Count, DateTimeOffset Newest, MacLogHandle NewestHandle, string Sample);

/// <summary>A summary: the groups by count (the top <see cref="MacLogSearch.SummaryGroups"/>), the entries counted, the read, and whether the scan stopped it.</summary>
public sealed record MacSummary(IReadOnlyList<MacSummaryGroup> Groups, int Total, int GroupCount, int Read, bool BudgetStop, DateTimeOffset? Reached, MacLoudest? Loudest);

/// <summary>
/// The policy over any unified-log reader (2026-10-18, the Mac event-log plan; <see cref="EventLogSearch"/>'s shape): the redaction, the
/// scan's limit and budget, the page's cursor and the summary's groups — the code the tools run over the real reader and the tests over
/// the fake. The scan counts the entries the read let through (OSLog's predicate drops the rest before they reach us, so a narrow filter
/// covers much more time than the count says). Ties: <see cref="MacLogHandle.Tie"/> counts the entries with the same microsecond, pid
/// and thread in the order the read meets them, the same in a later read with the same filter, which is what lets <c>before</c> and
/// <c>eventlog_event</c> find one again.
/// </summary>
public static class MacLogSearch
{
    /// <summary>How many groups a summary shows.</summary>
    public const int SummaryGroups = 30;

    /// <summary>The loudest process is named when it made more than this share of a read.</summary>
    public const double LoudShare = 0.5;

    /// <summary>The entry as the detail shows it: the message's secret-like values hidden when redacted.</summary>
    public static MacLogView View(MacLogEntry entry, MacLogHandle handle, bool redacted)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new MacLogView(entry, handle, (redacted ? EventLogRedaction.Text(entry.Message) : entry.Message).TrimEnd());
    }

    /// <summary>One page of <paramref name="query"/>'s entries from <paramref name="reader"/>, under <paramref name="scan"/>.</summary>
    public static MacSearchPage Search(IUnifiedLogReader reader, MacLogQuery query, EventScan scan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(scan);
        long started = scan.Time.GetTimestamp();
        var kept = new List<MacLogView>(Math.Min(query.Limit, EventLogQuery.MaxLimit));
        var walk = new Walk(query.Before);
        while (reader.Next() is { } batch)
        {
            foreach (var entry in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (walk.Read >= scan.ScanLimit || scan.Time.GetElapsedTime(started) >= scan.Budget)
                {
                    return new MacSearchPage(kept, walk.Read, true, walk.LastHandle, true, walk.Reached, walk.Loudest());
                }

                if (walk.Step(entry) is not { } handle)
                {
                    continue;
                }

                var view = View(entry, handle, scan.Redacted);
                if (!query.KeepsText(view.Message))
                {
                    continue;
                }

                kept.Add(view);
                if (kept.Count >= query.Limit)
                {
                    return new MacSearchPage(kept, walk.Read, true, handle, false, walk.Reached, walk.Loudest());
                }
            }
        }

        return new MacSearchPage(kept, walk.Read, false, null, false, walk.Reached, walk.Loudest());
    }

    /// <summary>The query's entries counted by process, subsystem and category, under <paramref name="scan"/>; the top <see cref="SummaryGroups"/> by count.</summary>
    public static MacSummary Summarize(IUnifiedLogReader reader, MacLogQuery query, EventScan scan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(scan);
        long started = scan.Time.GetTimestamp();
        var groups = new Dictionary<(string, string, string), MacSummaryGroup>();
        var walk = new Walk(null);
        int total = 0;
        bool stopped = false;
        while (!stopped && reader.Next() is { } batch)
        {
            foreach (var entry in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (walk.Read >= scan.ScanLimit || scan.Time.GetElapsedTime(started) >= scan.Budget)
                {
                    stopped = true;
                    break;
                }

                var handle = walk.Step(entry)!.Value;
                var view = View(entry, handle, scan.Redacted);
                if (!query.KeepsText(view.Message))
                {
                    continue;
                }

                total++;
                var key = (entry.Process, entry.Subsystem, entry.Category);
                groups[key] = groups.TryGetValue(key, out var group)
                    ? group with { Count = group.Count + 1, Level = Math.Max(group.Level, entry.Level) }
                    : new MacSummaryGroup(entry.Process, entry.Subsystem, entry.Category, entry.Level, 1, entry.Time, handle, view.FirstLine ?? "");
            }
        }

        var top = groups.Values.OrderByDescending(g => g.Count).ThenByDescending(g => g.Newest).Take(SummaryGroups).ToList();
        return new MacSummary(top, total, groups.Count, walk.Read, stopped, walk.Reached, walk.Loudest());
    }

    /// <summary>
    /// The entry <paramref name="handle"/> names in <paramref name="reader"/>'s read, or null when it is no longer there. The tie number was
    /// counted under the search's own filter, which the find's (<see cref="FindFilter"/>, every level) may not repeat; so when the exact
    /// tie is not met, the first entry with the handle's microsecond, pid and thread stands in.
    /// </summary>
    public static MacLogView? Find(IUnifiedLogReader reader, MacLogHandle handle, bool redacted, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var walk = new Walk(null);
        MacLogView? first = null;
        while (reader.Next() is { } batch)
        {
            foreach (var entry in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var seen = walk.Step(entry)!.Value;
                if (seen == handle)
                {
                    return View(entry, seen, redacted);
                }

                if (first is null && handle.Names(entry))
                {
                    first = View(entry, seen with { Tie = handle.Tie }, redacted);
                }

                if (MacLogHandle.MicrosecondsOf(entry.Time) < handle.Microseconds)
                {
                    return first;   // read past it: the stand-in, or purged since
                }
            }
        }

        return first;
    }

    /// <summary>
    /// The filter that finds one entry again (2026-10-18): its pid, and a window from a second before it to its microsecond, every level,
    /// so the read is short and the ties come out as they did. Redacted or not, nothing else narrows it (the text never decides a find).
    /// </summary>
    public static MacLogFilter FindFilter(MacLogHandle handle) =>
        new(handle.Time.AddSeconds(-1), [], handle.Time, Pid: handle.Pid);

    /// <summary>
    /// A read walked entry by entry (the scan's count, how far back, each process's count, the tie numbers) and, for a page after the
    /// first, the entries skipped until the cursor's own one has passed.
    /// </summary>
    private sealed class Walk(MacLogHandle? before)
    {
        private readonly Dictionary<(long, int, ulong), int> _ties = [];
        private readonly Dictionary<string, int> _processes = new(StringComparer.Ordinal);
        private bool _passed = before is null;

        public int Read { get; private set; }

        public DateTimeOffset? Reached { get; private set; }

        public MacLogHandle? LastHandle { get; private set; }

        /// <summary>The entry's handle, or null while it is still at or above the cursor.</summary>
        public MacLogHandle? Step(MacLogEntry entry)
        {
            Read++;
            Reached = entry.Time;
            _processes[entry.Process] = _processes.GetValueOrDefault(entry.Process) + 1;
            long micro = MacLogHandle.MicrosecondsOf(entry.Time);
            var key = (micro, entry.Pid, entry.Thread);
            int tie = _ties.GetValueOrDefault(key);
            _ties[key] = tie + 1;
            var handle = new MacLogHandle(micro, entry.Pid, entry.Thread, tie);
            LastHandle = handle;
            if (!_passed && before is { } cursor)
            {
                if (micro > cursor.Microseconds)
                {
                    return null;
                }

                if (micro == cursor.Microseconds)
                {
                    _passed = handle == cursor;
                    return null;
                }

                _passed = true;
            }

            return handle;
        }

        public MacLoudest? Loudest()
        {
            if (Read == 0 || _processes.Count < 2)
            {
                return null;
            }

            var (process, count) = _processes.MaxBy(p => p.Value);
            return count > Read * LoudShare ? new MacLoudest(process, count, Read) : null;
        }
    }
}
