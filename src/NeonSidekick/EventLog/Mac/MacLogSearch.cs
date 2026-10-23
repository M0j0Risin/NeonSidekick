namespace NeonSidekick.EventLog;

/// <summary>
/// One entry as an answer shows it (2026-10-18): the entry, its handle, and its message under the read's detail. <see cref="Sender"/>
/// (2026-10-20, the code review) is the sender image's path under the same detail: a redacted read writes the home as <c>~</c> there too.
/// </summary>
public sealed record MacLogView(MacLogEntry Entry, MacLogHandle Handle, string Message, string? Sender = null)
{
    /// <summary>The message's first line, or null when it is blank.</summary>
    public string? FirstLine => Message.Split('\n', 2)[0].Trim() is { Length: > 0 } first ? first : null;

    /// <summary>The sender as shown: <see cref="Sender"/> when the view was made with one, else the entry's own.</summary>
    public string ShownSender => Sender ?? Entry.Sender;
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
/// covers much more time than the count says). Ties: <see cref="MacLogHandle.Tie"/> counts the entries with the same microsecond, pid,
/// thread and mark in the order the read meets them, the same in any later read whatever its filter (<see cref="MacLogHandle"/>), which
/// is what lets <c>before</c> and <c>eventlog_event</c> find one again. The budget is checked before each entry and before each batch,
/// which the reader hands back short when OSLog is slow (2026-10-20, the code review).
/// </summary>
public static class MacLogSearch
{
    /// <summary>How many groups a summary shows.</summary>
    public const int SummaryGroups = 30;

    /// <summary>The loudest process is named when it made more than this share of a read.</summary>
    public const double LoudShare = 0.5;

    /// <summary>
    /// The entry as the detail shows it: redacted, the message's secret-like values hidden and the home written as <c>~</c> in it and in the
    /// sender's path (2026-10-20, the code review: <see cref="MacFileLogs.Redactor"/>'s rule, the unified log's too, so a twin never reads
    /// <c>/Users/&lt;name&gt;/</c>); <paramref name="places"/> null leaves the home alone.
    /// </summary>
    public static MacLogView View(MacLogEntry entry, MacLogHandle handle, bool redacted, MacLogPlaces? places = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!redacted)
        {
            return new MacLogView(entry, handle, entry.Message.TrimEnd(), entry.Sender);
        }

        Func<string, string> redact = places is null ? text => EventLogRedaction.Text(MacLogPlaces.OtherHomes(text)) : MacFileLogs.Redactor(places, redacted: true);
        return new MacLogView(entry, handle, redact(entry.Message).TrimEnd(), redact(entry.Sender));
    }

    /// <summary>
    /// <paramref name="query"/> with an archive's default window anchored at the archive's newest entry (2026-10-20, the code review: a
    /// saved archive got the live store's last hour and an older one read as empty). A reverse read is put at now (<see cref="MacLogFilter"/>)
    /// and reads back from the newest entry, so the first entry of a read with no terms and no lower bound is it, at once (the live check,
    /// <c>MacUnifiedLogTests</c>); the window is then <see cref="MacLogQuery.DefaultWindow"/> before it. The query
    /// as it was for this Mac's own log, a window the call named, or an archive with no entry.
    /// </summary>
    public static MacLogQuery Anchor(IUnifiedLog log, MacLogSource source, MacLogQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(query);
        if (!source.IsArchive || query.SinceGiven)
        {
            return query;
        }

        using var reader = log.Open(source.Target, new MacLogFilter(DateTimeOffset.UnixEpoch, []), cancellationToken);
        while (reader.Next() is { } batch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (batch.Count > 0)
            {
                return query with { Since = batch[0].Time - MacLogQuery.DefaultWindow, SinceGiven = true };
            }
        }

        return query;
    }

    /// <summary>One page of <paramref name="query"/>'s entries from <paramref name="reader"/>, under <paramref name="scan"/>.</summary>
    public static MacSearchPage Search(IUnifiedLogReader reader, MacLogQuery query, EventScan scan, CancellationToken cancellationToken, MacLogPlaces? places = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(scan);
        long started = scan.Time.GetTimestamp();
        var kept = new List<MacLogView>(Math.Clamp(query.Limit, 0, EventLogQuery.MaxLimit));
        var walk = new Walk(query.Before);
        bool Stopped() => walk.Read >= scan.ScanLimit || scan.Time.GetElapsedTime(started) >= scan.Budget;
        MacSearchPage Stop() => new(kept, walk.Read, true, walk.Cursor, true, walk.Reached, walk.Loudest());
        while (reader.Next() is { } batch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Stopped())
            {
                return Stop();
            }

            foreach (var entry in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Stopped())
                {
                    return Stop();
                }

                if (walk.Step(entry) is not { } handle)
                {
                    continue;
                }

                var view = View(entry, handle, scan.Redacted, places);
                if (scan.Redacted && !query.KeepsText(view.Message))
                {
                    continue;   // full: the text went to OSLog and was checked on the whole message; redacted: on what the answer shows
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
    public static MacSummary Summarize(IUnifiedLogReader reader, MacLogQuery query, EventScan scan, CancellationToken cancellationToken, MacLogPlaces? places = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(scan);
        long started = scan.Time.GetTimestamp();
        var groups = new Dictionary<(string, string, string), MacSummaryGroup>();
        var walk = new Walk(null);
        int total = 0;
        bool stopped = false;
        bool Stopped() => walk.Read >= scan.ScanLimit || scan.Time.GetElapsedTime(started) >= scan.Budget;
        while (!stopped && reader.Next() is { } batch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Stopped())
            {
                stopped = true;
                break;
            }

            foreach (var entry in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Stopped())
                {
                    stopped = true;
                    break;
                }

                var handle = walk.Step(entry)!.Value;

                // The entry redacted only when its text decides it or it is a group's first, the sample (2026-10-23, the admin review's I6: every
                // entry was redacted two or three times, the scan's budget spent on regexes).
                MacLogView? view = null;
                if (scan.Redacted && query.Text is { Length: > 0 })
                {
                    view = View(entry, handle, scan.Redacted, places);
                    if (!query.KeepsText(view.Message))
                    {
                        continue;
                    }
                }

                total++;
                var key = (entry.Process, entry.Subsystem, entry.Category);
                groups[key] = groups.TryGetValue(key, out var group)
                    ? group with { Count = group.Count + 1, Level = Math.Max(group.Level, entry.Level) }
                    : new MacSummaryGroup(entry.Process, entry.Subsystem, entry.Category, entry.Level, 1, entry.Time, handle, (view ?? View(entry, handle, scan.Redacted, places)).FirstLine ?? "");
            }
        }

        var top = groups.Values.OrderByDescending(g => g.Count).ThenByDescending(g => g.Newest).Take(SummaryGroups).ToList();
        return new MacSummary(top, total, groups.Count, walk.Read, stopped, walk.Reached, walk.Loudest());
    }

    /// <summary>
    /// The entry <paramref name="handle"/> names in <paramref name="reader"/>'s read, or null when it is no longer there. The find's read is
    /// forward (oldest first, later on 2026-10-18), so the entries it names are turned round before the tie is counted. A handle with a mark
    /// (2026-10-20) names only the entries with its mark, whose count is the search's whatever its filter; a four-part one from before the
    /// mark was counted under the search's own filter, which the find (<see cref="FindFilter"/>, every level) may not repeat, so when its
    /// exact tie is not met the newest entry with its microsecond, pid and thread stands in, as it did.
    /// </summary>
    public static MacLogView? Find(IUnifiedLogReader reader, MacLogHandle handle, bool redacted, CancellationToken cancellationToken, MacLogPlaces? places = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var named = new List<MacLogEntry>();
        while (reader.Next() is { } batch)
        {
            foreach (var entry in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (handle.Names(entry))
                {
                    named.Add(entry);
                }
            }
        }

        if (named.Count == 0)
        {
            return null;   // purged since
        }

        named.Reverse();   // the find reads oldest first (FindFilter); a search counted the ties newest first
        if (handle.Mark is not null)
        {
            return handle.Tie < named.Count ? View(named[handle.Tie], handle, redacted, places) : null;
        }

        return View(named[handle.Tie < named.Count ? handle.Tie : 0], handle, redacted, places);
    }

    /// <summary>
    /// The filter that finds one entry again (2026-10-18): its pid, and a window from a second before it to its microsecond, every level,
    /// so the read is short and the ties come out as they did. Redacted or not, nothing else narrows it (the text never decides a find).
    /// </summary>
    public static MacLogFilter FindFilter(MacLogHandle handle) =>
        new(handle.Time - FindSlack, [], handle.Time + Microsecond, Pid: handle.Pid, Forward: true);

    /// <summary>One microsecond: a handle's time is cut to it, an entry's is not, so an until at the handle's own time would leave the entry out.</summary>
    public static readonly TimeSpan Microsecond = TimeSpan.FromTicks(10);

    /// <summary>How far before a handle's microsecond a find starts reading (the forward read's position need not be exact).</summary>
    public static readonly TimeSpan FindSlack = TimeSpan.FromMilliseconds(1);

    /// <summary>
    /// A read walked entry by entry (the scan's count, how far back, each process's count, the tie numbers) and, for a page after the
    /// first, the entries skipped until the cursor's own one has passed. <see cref="Cursor"/> is where a stopped page's next one starts:
    /// the last entry past the cursor, or the cursor itself while the walk is still skipping toward it (2026-10-20, the code review: the
    /// last entry met then could be an earlier tie in the cursor's microsecond, or one above it, and the next page repeated rows or made
    /// no progress).
    /// </summary>
    private sealed class Walk(MacLogHandle? before)
    {
        private readonly Dictionary<(long, int, ulong, uint), int> _ties = [];
        private readonly Dictionary<(long, int, ulong), int> _plainTies = [];
        private readonly Dictionary<string, int> _processes = new(StringComparer.Ordinal);
        private bool _passed = before is null;
        private MacLogHandle? _lastPassed;

        public int Read { get; private set; }

        public DateTimeOffset? Reached { get; private set; }

        public MacLogHandle? Cursor => _lastPassed ?? before;

        /// <summary>The entry's handle, or null while it is still at or above the cursor.</summary>
        public MacLogHandle? Step(MacLogEntry entry)
        {
            Read++;
            Reached = entry.Time;
            _processes[entry.Process] = _processes.GetValueOrDefault(entry.Process) + 1;
            long micro = MacLogHandle.MicrosecondsOf(entry.Time);
            uint mark = MacLogHandle.MarkOf(entry);
            var key = (micro, entry.Pid, entry.Thread, mark);
            int tie = _ties.GetValueOrDefault(key);
            _ties[key] = tie + 1;
            var plainKey = (micro, entry.Pid, entry.Thread);
            int plainTie = _plainTies.GetValueOrDefault(plainKey);
            _plainTies[plainKey] = plainTie + 1;
            var handle = new MacLogHandle(micro, entry.Pid, entry.Thread, tie, mark);
            if (!_passed && before is { } cursor)
            {
                if (micro > cursor.Microseconds)
                {
                    return null;
                }

                if (micro == cursor.Microseconds)
                {
                    _passed = cursor.Mark is null   // a handle from before the mark: its tie was counted among the plain ties
                        ? entry.Pid == cursor.Pid && entry.Thread == cursor.Thread && plainTie == cursor.Tie
                        : handle == cursor;
                    return null;
                }

                _passed = true;
            }

            _lastPassed = handle;
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
