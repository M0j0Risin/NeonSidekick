namespace NeonSidekick.Tests.Fakes;

/// <summary>
/// A clock that only moves when a test says so. Timestamps are in ticks; the wall clock is
/// <see cref="UtcNow"/> in <see cref="LocalTimeZone"/>, fixed at a Friday afternoon in a
/// UTC-07:00 zone so every expected string is the same on every machine. Timers from
/// <see cref="CreateTimer"/> fire synchronously inside <see cref="Advance"/>, in due order, on the
/// caller's thread; a <c>Change</c> from inside a callback is honoured within the same advance.
/// Safe across threads (2026-09-26): <c>ChatScreenTests.RunLoopAsync</c> advances it from a pool task while the screen
/// makes and disposes timers — every <c>Task.Delay</c> on it — on its own, and the unguarded list threw "Collection was
/// modified" inside <see cref="Advance"/>, killing the clock task unobserved and hanging the run. The list and the ticks
/// are under <c>_gate</c>; a callback runs outside it, so a delay's continuation run inline never holds it.
/// </summary>
public sealed class ManualTimeProvider : TimeProvider
{
    /// <summary>2026-09-11 21:05:30 UTC = Friday 11 September 2026, 14:05 in <see cref="DefaultZone"/>.</summary>
    public static readonly DateTimeOffset DefaultUtcNow = new(2026, 9, 11, 21, 5, 30, TimeSpan.Zero);

    /// <summary>A fixed UTC-07:00 zone with no daylight rules, named like the real thing.</summary>
    public static readonly TimeZoneInfo DefaultZone = TimeZoneInfo.CreateCustomTimeZone(
        "Test Pacific", TimeSpan.FromHours(-7), "(UTC-07:00) Test Pacific", "Pacific Daylight Time");

    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = new();
    private long _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp()
    {
        lock (_gate)
        {
            return _ticks;
        }
    }

    /// <summary>Moves both clocks and fires every timer that came due, earliest first.</summary>
    public void Advance(TimeSpan by)
    {
        long target;
        lock (_gate)
        {
            _utcNow += by;
            target = _ticks + by.Ticks;
        }

        while (true)
        {
            ManualTimer? next = null;
            lock (_gate)
            {
                foreach (var timer in _timers)
                {
                    if (timer.Due is { } due && due <= target && (next is null || due < next.Due))
                    {
                        next = timer;
                    }
                }

                if (next is null)
                {
                    _ticks = Math.Max(_ticks, target);
                    return;
                }

                _ticks = Math.Max(_ticks, next.Due!.Value);
                next.Rearm();
            }

            next.Invoke();   // outside the gate: the callback may make, change or dispose timers, or run a continuation inline
        }
    }

    private DateTimeOffset _utcNow = DefaultUtcNow;

    public DateTimeOffset UtcNow
    {
        get { lock (_gate) { return _utcNow; } }
        set { lock (_gate) { _utcNow = value; } }
    }

    public override DateTimeOffset GetUtcNow() => UtcNow;

    public TimeZoneInfo Zone { get; set; } = DefaultZone;

    public override TimeZoneInfo LocalTimeZone => Zone;

    /// <summary>How many timers are alive (created and not disposed).</summary>
    public int TimerCount
    {
        get { lock (_gate) { return _timers.Count; } }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (_gate)
        {
            _timers.Add(timer);
            timer.Change(dueTime, period);
        }

        return timer;
    }

    private sealed class ManualTimer : ITimer
    {
        private readonly ManualTimeProvider _owner;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private TimeSpan _period = Timeout.InfiniteTimeSpan;

        public ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state)
        {
            _owner = owner;
            _callback = callback;
            _state = state;
        }

        /// <summary>The timestamp it fires at, or null when parked. Under the owner's gate.</summary>
        public long? Due { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (_owner._gate)
            {
                if (!_owner._timers.Contains(this))
                {
                    return false;
                }

                _period = period;
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : _owner._ticks + Math.Max(0, dueTime.Ticks);
                return true;
            }
        }

        /// <summary>Moves <see cref="Due"/> on by the period, or parks it — before the callback, which may <see cref="Change"/> it. Under the gate.</summary>
        public void Rearm() => Due = _period == Timeout.InfiniteTimeSpan ? null : Due + _period.Ticks;

        /// <summary>The callback, outside the gate.</summary>
        public void Invoke() => _callback(_state);

        public void Dispose()
        {
            lock (_owner._gate)
            {
                _owner._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
