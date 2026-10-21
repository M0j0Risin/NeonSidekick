using NeonSidekick.EventLog;

namespace NeonSidekick.Tests.Fakes;

/// <summary>
/// A unified log for the tests (2026-10-18, the Mac event-log plan): its entries handed back newest first (a forward filter's oldest
/// first, ended at its Until) in batches of
/// <see cref="BatchSize"/>, each filtered as OSLog's predicate would (<see cref="MacLogMatch"/>) and the read ended at the first entry
/// older than <see cref="MacLogFilter.Since"/>, as the predicate's time limit ends the real one. Records every open with its predicate
/// (<see cref="MacLogPredicate.Build"/>); <see cref="Fail"/> makes the next open or probe throw, <see cref="Gate"/> holds every open until
/// released (or the token fires). <see cref="Archives"/> holds what each archive path reads; a path not there throws as OSLog does.
/// </summary>
public sealed class FakeUnifiedLog : IUnifiedLog
{
    /// <summary>This Mac's store, any order (read newest first, ties in the order added).</summary>
    public List<MacLogEntry> Entries { get; } = [];

    /// <summary>The archives by full path.</summary>
    public Dictionary<string, List<MacLogEntry>> Archives { get; } = new(StringComparer.Ordinal);

    /// <summary>A refusal the next open or probe throws, then cleared.</summary>
    public UnifiedLogException? Fail { get; set; }

    /// <summary>Holds every open until released; the token cancels the wait.</summary>
    public SemaphoreSlim? Gate { get; set; }

    public int BatchSize { get; set; } = MacLogLimits.Batch;

    /// <summary>
    /// How many empty batches each read hands back before its entries (2026-10-20): the real reader's slow read, which returns a short or
    /// empty batch after <see cref="MacLogLimits.BatchTime"/>.
    /// </summary>
    public int EmptyBatches { get; set; }

    /// <summary>Heard before every <see cref="IUnifiedLogReader.Next"/> (2026-10-20): a test moves its clock there.</summary>
    public Action? OnNext { get; set; }

    /// <summary>Every open: the target, the filter, and the predicate the real reader would have sent.</summary>
    public List<(UnifiedLogTarget Target, MacLogFilter Filter, string Format, IReadOnlyList<MacPredicateArgument> Arguments)> Opens { get; } = [];

    public int Probes { get; private set; }

    /// <summary>How many readers are still open.</summary>
    public int Open { get; private set; }

    public static MacLogEntry Entry(DateTimeOffset time, int level = 4, string process = "loginwindow", int pid = 395, ulong thread = 7,
        string subsystem = "com.apple.loginwindow.logging", string category = "TAL", string message = "applicationQuit: for app:Single Sign-On", string? sender = null) =>
        new(time, level, process, pid, thread, subsystem, category, sender ?? process, message);

    IUnifiedLogReader IUnifiedLog.Open(UnifiedLogTarget target, MacLogFilter filter, CancellationToken cancellationToken)
    {
        var (format, arguments) = MacLogPredicate.Build(filter);
        lock (Opens)
        {
            Opens.Add((target, filter, format, arguments));
        }

        Gate?.Wait(cancellationToken);
        var source = Source(target);
        var newest = source
            .Select((entry, index) => (entry, index))
            .OrderByDescending(e => e.entry.Time)
            .ThenBy(e => e.index)
            .Select(e => e.entry);
        var matched = (filter.Forward
                ? newest.Reverse().SkipWhile(e => e.Time < filter.Since).TakeWhile(e => e.Time <= filter.Until)   // oldest first, ended past Until
                : newest.TakeWhile(e => e.Time >= filter.Since))
            .Where(e => MacLogMatch.Matches(e, filter))
            .ToList();
        Open++;
        return new Reader(this, matched, cancellationToken);
    }

    public void Probe(UnifiedLogTarget target)
    {
        Probes++;
        _ = Source(target);
    }

    private List<MacLogEntry> Source(UnifiedLogTarget target)
    {
        if (Fail is { } fail)
        {
            Fail = null;
            throw fail;
        }

        if (target.IsLocal)
        {
            return Entries;
        }

        return Archives.TryGetValue(target.ArchivePath!, out var entries)
            ? entries
            : throw new UnifiedLogException("NSCocoaErrorDomain", 260, "The file couldn't be opened because there is no such file.");
    }

    private sealed class Reader(FakeUnifiedLog owner, List<MacLogEntry> entries, CancellationToken token) : IUnifiedLogReader
    {
        private int _at;
        private int _empty;
        private bool _disposed;

        public IReadOnlyList<MacLogEntry>? Next()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            owner.OnNext?.Invoke();
            token.ThrowIfCancellationRequested();
            if (_empty < owner.EmptyBatches)
            {
                _empty++;
                return [];
            }

            if (_at >= entries.Count)
            {
                return null;
            }

            var batch = entries.Skip(_at).Take(owner.BatchSize).ToList();
            _at += batch.Count;
            return batch;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                owner.Open--;
            }
        }
    }
}
