using System.Collections.Concurrent;
using NeonSidekick.Diagnostics;
using NeonSidekick.Llm.Tools;

namespace NeonSidekick.Timers;

public enum TimerStartOutcome
{
    Started,

    /// <summary>A timer of that name exists; <see cref="TimerStartResult.Timer"/> is the existing one.</summary>
    Duplicate,
    Full,
    BadName,
    BadDuration,
}

/// <summary><see cref="Timer"/> is the new timer for <see cref="TimerStartOutcome.Started"/>, the existing one for <see cref="TimerStartOutcome.Duplicate"/>, default otherwise.</summary>
public readonly record struct TimerStartResult(TimerStartOutcome Outcome, TimerSnapshot Timer);

/// <summary>
/// One timer as it stands: <see cref="Remaining"/> is rounded up to whole seconds and zero once <see cref="Ringing"/>. A
/// <see cref="Stopwatch"/> (2026-10-18, <c>/timer stopwatch</c>) counts up: <see cref="Elapsed"/> is whole seconds rounded down,
/// <see cref="Duration"/> and <see cref="Remaining"/> are zero, and <see cref="DueLocal"/> is when it started.
/// </summary>
public readonly record struct TimerSnapshot(string Name, TimeSpan Duration, TimeSpan Remaining, DateTimeOffset DueLocal, bool Ringing, bool Stopwatch = false, TimeSpan Elapsed = default);

/// <summary>One alert to print and speak. <see cref="Repeat"/> is 0 for the first, then 1, 2, … every <see cref="TimerBoard.RepeatInterval"/>; <see cref="Overdue"/> is the time since it expired, whole seconds.</summary>
public readonly record struct TimerAlert(string Name, TimeSpan Duration, TimeSpan Overdue, int Repeat);

/// <summary>
/// The named countdown timers, one board per screen. They live for the process (not the
/// conversation, not the profile) and are never persisted. Time is the monotonic clock of the
/// <see cref="TimeProvider"/> (<c>GetTimestamp</c>); the wall clock is read only for the "done at
/// 14:15" wording.
///
/// <para>One <see cref="ITimer"/> is kept pointed at the earliest thing to happen: a running
/// timer's due, or a ringing timer's next repeat. Its callback (a pool thread under the real
/// clock) moves due timers to ringing, queues one <see cref="TimerAlert"/> per timer whose alert
/// is due, and then calls <c>signal</c> outside the lock — that is the whole of what runs off the
/// screen's thread. It never touches the console: the screen drains <see cref="TryTakeAlert"/> at
/// its safe points, and <c>signal</c> only ends an idle input read early.</para>
///
/// <para>A ringing timer repeats its alert every <see cref="RepeatInterval"/> until
/// <see cref="Acknowledge"/> (any input at the line) or <see cref="Stop"/> removes it.</para>
///
/// <para>A stopwatch (2026-10-18, the user's ask: <c>/timer stopwatch</c>, no laps) shares the board and its names, its
/// <see cref="MaxTimers"/> and <see cref="Stop"/>/<see cref="StopAll"/>, but counts up from its start and never rings: it has
/// nothing due, so <see cref="Reschedule"/> and <see cref="OnTick"/> pass it by and it queues no alert; <see cref="Acknowledge"/>
/// removes only ringing timers, so it survives any input.</para>
/// </summary>
public sealed class TimerBoard : IDisposable
{
    public const int MaxTimers = 20;
    public static readonly TimeSpan MinDuration = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaxDuration = TimeSpan.FromHours(24);
    public static readonly TimeSpan RepeatInterval = TimeSpan.FromSeconds(60);

    private readonly TimeProvider _time;
    private readonly Action _signal;
    private readonly ITimer _timer;
    private readonly object _lock = new();
    private readonly List<Entry> _entries = new();
    private readonly ConcurrentQueue<TimerAlert> _alerts = new();
    private bool _disposed;

    /// <param name="signal">Runs after alerts were queued, on the clock's thread (a pool thread under the real clock, the caller's under a manual one). Must not block or write.</param>
    public TimerBoard(TimeProvider time, Action signal)
    {
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _signal = signal ?? throw new ArgumentNullException(nameof(signal));
        _timer = time.CreateTimer(OnTick, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public bool HasAlerts => !_alerts.IsEmpty;

    public bool HasRinging
    {
        get
        {
            lock (_lock)
            {
                return _entries.Any(e => e.Ringing);
            }
        }
    }

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>The name is normalised (<see cref="TimerText.NormalizeName"/>) and must pass <see cref="TimerText.IsValidName"/>; the duration is whole seconds within <see cref="MinDuration"/>..<see cref="MaxDuration"/>.</summary>
    public TimerStartResult Start(string name, TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(name);
        string normalized = TimerText.NormalizeName(name);
        if (!TimerText.IsValidName(normalized))
        {
            return new(TimerStartOutcome.BadName, default);
        }

        if (duration < MinDuration || duration > MaxDuration)
        {
            return new(TimerStartOutcome.BadDuration, default);
        }

        duration = TimeSpan.FromSeconds(Math.Round(duration.TotalSeconds));
        lock (_lock)
        {
            long now = _time.GetTimestamp();
            var existing = Find(normalized);
            if (existing is not null)
            {
                return new(TimerStartOutcome.Duplicate, Snapshot(existing, now));
            }

            if (_entries.Count >= MaxTimers)
            {
                return new(TimerStartOutcome.Full, default);
            }

            var entry = new Entry(normalized, duration, now);
            _entries.Add(entry);
            Reschedule(now);
            DiagnosticLog.Info(Category, StartedLogLine(normalized, duration));
            return new(TimerStartOutcome.Started, Snapshot(entry, now));
        }
    }

    /// <summary>
    /// A stopwatch called <paramref name="name"/> (2026-10-18): the same name rules, the same duplicate check (against timers and
    /// stopwatches alike) and the same <see cref="MaxTimers"/> as <see cref="Start"/>; <see cref="TimerStartOutcome.BadDuration"/> never.
    /// </summary>
    public TimerStartResult StartStopwatch(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        string normalized = TimerText.NormalizeName(name);
        if (!TimerText.IsValidName(normalized))
        {
            return new(TimerStartOutcome.BadName, default);
        }

        lock (_lock)
        {
            long now = _time.GetTimestamp();
            var existing = Find(normalized);
            if (existing is not null)
            {
                return new(TimerStartOutcome.Duplicate, Snapshot(existing, now));
            }

            if (_entries.Count >= MaxTimers)
            {
                return new(TimerStartOutcome.Full, default);
            }

            var entry = new Entry(normalized, TimeSpan.Zero, now) { Stopwatch = true };
            _entries.Add(entry);
            DiagnosticLog.Info(Category, StopwatchStartedLogLine(normalized));
            return new(TimerStartOutcome.Started, Snapshot(entry, now));
        }
    }

    /// <summary>The log category of the timers' lines.</summary>
    public const string Category = "Timers";

    /// <summary><c>Timer "tea" started for 5 minutes</c>. Pinned.</summary>
    public static string StartedLogLine(string name, TimeSpan duration) => $"Timer \"{name}\" started for {TimerText.Describe(duration)}";

    /// <summary><c>Stopwatch "tea" started</c> (2026-10-18). Pinned.</summary>
    public static string StopwatchStartedLogLine(string name) => $"Stopwatch \"{name}\" started";

    /// <summary><c>Timer "tea" rang</c> (its first alert; the repeats are not logged). Pinned.</summary>
    public static string RangLogLine(string name) => $"Timer \"{name}\" rang";

    /// <summary><c>Timer "tea" stopped</c> / <c>… stopped (ringing)</c>. Pinned.</summary>
    public static string StoppedLogLine(string name, bool ringing) => $"Timer \"{name}\" stopped{(ringing ? " (ringing)" : "")}";

    /// <summary><c>All timers stopped (2)</c> — nothing logged for none. Pinned.</summary>
    public static string StoppedAllLogLine(int count) => $"All timers stopped ({count.ToString(System.Globalization.CultureInfo.InvariantCulture)})";

    /// <summary><c>2 ringing timers acknowledged</c> — nothing logged for none. Pinned.</summary>
    public static string AcknowledgedLogLine(int count) => $"{ClockText.Count(count, "ringing timer")} acknowledged";

    /// <summary>Removes the timer (running or ringing); <paramref name="removed"/> is how it stood.</summary>
    public bool Stop(string name, out TimerSnapshot removed)
    {
        ArgumentNullException.ThrowIfNull(name);
        string normalized = TimerText.NormalizeName(name);
        lock (_lock)
        {
            var entry = Find(normalized);
            if (entry is null)
            {
                removed = default;
                return false;
            }

            long now = _time.GetTimestamp();
            removed = Snapshot(entry, now);
            _entries.Remove(entry);
            Reschedule(now);
            DiagnosticLog.Info(Category, StoppedLogLine(normalized, removed.Ringing));
            return true;
        }
    }

    /// <summary>Removes every timer; returns how many there were.</summary>
    public int StopAll()
    {
        lock (_lock)
        {
            int count = _entries.Count;
            _entries.Clear();
            Reschedule(_time.GetTimestamp());
            if (count > 0)
            {
                DiagnosticLog.Info(Category, StoppedAllLogLine(count));
            }

            return count;
        }
    }

    public bool TryFind(string name, out TimerSnapshot timer)
    {
        ArgumentNullException.ThrowIfNull(name);
        string normalized = TimerText.NormalizeName(name);
        lock (_lock)
        {
            var entry = Find(normalized);
            if (entry is null)
            {
                timer = default;
                return false;
            }

            timer = Snapshot(entry, _time.GetTimestamp());
            return true;
        }
    }

    /// <summary>Running timers by remaining time, then the stopwatches oldest first (2026-10-18), then the ringing ones in the order they expired.</summary>
    public IReadOnlyList<TimerSnapshot> Snapshot()
    {
        lock (_lock)
        {
            long now = _time.GetTimestamp();
            return _entries
                .Select(e => (Entry: e, Snapshot: Snapshot(e, now)))
                .OrderBy(x => x.Entry.Ringing ? 2 : x.Entry.Stopwatch ? 1 : 0)
                .ThenBy(x => x.Snapshot.Remaining)
                .ThenBy(x => x.Entry.Stopwatch ? x.Entry.Started : 0)
                .Select(x => x.Snapshot)
                .ToArray();
        }
    }

    /// <summary>Any input at the line: every ringing timer is removed and its repeats stop. Returns how many.</summary>
    public int Acknowledge()
    {
        lock (_lock)
        {
            int count = _entries.RemoveAll(e => e.Ringing);
            if (count > 0)
            {
                Reschedule(_time.GetTimestamp());
                DiagnosticLog.Debug(Category, AcknowledgedLogLine(count));
            }

            return count;
        }
    }

    /// <summary>The next queued alert, oldest first.</summary>
    public bool TryTakeAlert(out TimerAlert alert) => _alerts.TryDequeue(out alert);

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _timer.Dispose();
        }
    }

    // ---- under the lock ----

    private Entry? Find(string normalized) => _entries.Find(e => TimerText.NameEquals(e.Name, normalized));

    private TimerSnapshot Snapshot(Entry entry, long now)
    {
        if (entry.Stopwatch)
        {
            var elapsed = _time.GetElapsedTime(entry.Started, now);
            return new(entry.Name, TimeSpan.Zero, TimeSpan.Zero, ClockText.LocalNow(_time) - elapsed, false, Stopwatch: true, Elapsed: RoundDown(elapsed));
        }

        var remaining = entry.Ringing ? TimeSpan.Zero : RoundUp(entry.Duration - _time.GetElapsedTime(entry.Started, now));
        if (remaining < TimeSpan.Zero)
        {
            remaining = TimeSpan.Zero;
        }

        return new(entry.Name, entry.Duration, remaining, ClockText.LocalNow(_time) + remaining, entry.Ringing);
    }

    /// <summary>Points the one timer at the earliest due or repeat, or parks it when there is nothing to wait for.</summary>
    private void Reschedule(long now)
    {
        if (_disposed)
        {
            return;
        }

        TimeSpan? next = null;
        foreach (var entry in _entries)
        {
            if (entry.Stopwatch)
            {
                continue;   // nothing due (2026-10-18)
            }

            var delay = entry.Ringing
                ? RepeatInterval - _time.GetElapsedTime(entry.LastAlert, now)
                : entry.Duration - _time.GetElapsedTime(entry.Started, now);
            if (delay < TimeSpan.Zero)
            {
                delay = TimeSpan.Zero;
            }

            if (next is null || delay < next)
            {
                next = delay;
            }
        }

        _timer.Change(next ?? Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    private void OnTick(object? state)
    {
        bool queued = false;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            long now = _time.GetTimestamp();
            foreach (var entry in _entries)
            {
                if (entry.Stopwatch)
                {
                    continue;   // never rings (2026-10-18)
                }

                if (!entry.Ringing)
                {
                    var elapsed = _time.GetElapsedTime(entry.Started, now);
                    if (elapsed < entry.Duration)
                    {
                        continue;
                    }

                    entry.Ringing = true;
                    entry.Due = entry.Started + (long)(entry.Duration.TotalSeconds * _time.TimestampFrequency);
                    entry.LastAlert = now;
                    entry.Repeats = 0;
                    _alerts.Enqueue(new(entry.Name, entry.Duration, RoundDown(_time.GetElapsedTime(entry.Due, now)), 0));
                    DiagnosticLog.Info(Category, RangLogLine(entry.Name));
                    queued = true;
                }
                else if (_time.GetElapsedTime(entry.LastAlert, now) >= RepeatInterval)
                {
                    entry.LastAlert = now;
                    entry.Repeats++;
                    _alerts.Enqueue(new(entry.Name, entry.Duration, RoundDown(_time.GetElapsedTime(entry.Due, now)), entry.Repeats));
                    queued = true;
                }
            }

            Reschedule(now);
        }

        if (queued)
        {
            _signal();
        }
    }

    private static TimeSpan RoundUp(TimeSpan value) => TimeSpan.FromSeconds(Math.Ceiling(value.TotalSeconds));

    private static TimeSpan RoundDown(TimeSpan value) => TimeSpan.FromSeconds(Math.Max(0, Math.Floor(value.TotalSeconds)));

    private sealed class Entry
    {
        public Entry(string name, TimeSpan duration, long started)
        {
            Name = name;
            Duration = duration;
            Started = started;
        }

        public string Name { get; }

        public TimeSpan Duration { get; }

        /// <summary>Timestamp.</summary>
        public long Started { get; }

        public bool Ringing { get; set; }

        /// <summary>A stopwatch (2026-10-18): counts up from <see cref="Started"/>, never rings.</summary>
        public bool Stopwatch { get; init; }

        /// <summary>Timestamp of the expiry, set when it rang.</summary>
        public long Due { get; set; }

        /// <summary>Timestamp of the last alert queued.</summary>
        public long LastAlert { get; set; }

        public int Repeats { get; set; }
    }
}
