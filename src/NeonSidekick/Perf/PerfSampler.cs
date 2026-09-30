using NeonSidekick.Diagnostics;

namespace NeonSidekick.Perf;

/// <summary>
/// The performance bar's clock (2026-09-29): while the bar is on it reads its <see cref="IPerfSource"/> once every
/// <see cref="Interval"/> on a timer of the injected <see cref="TimeProvider"/> and keeps the reading and the last
/// <see cref="HistoryLength"/> of each meter (the sparklines'); off, the timer stops and the source is disposed, so an
/// unused bar costs nothing — NVML or PDH is opened only while it shows. The pane asks <see cref="Read"/> from its own
/// tick, under its own lock; the sampling never runs there.
/// </summary>
public sealed class PerfSampler : IDisposable
{
    /// <summary>How often the machine is read. Pinned.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    /// <summary>How many readings a sparkline shows. Pinned.</summary>
    public const int HistoryLength = 10;

    private const string Category = "Perf";

    private readonly Func<IPerfSource> _factory;
    private readonly TimeProvider _time;
    private readonly object _gate = new();

    // Held while the source samples and while a stop disposes it: a tick in flight never samples a disposed source.
    private readonly object _sampleGate = new();
    private IPerfSource? _source;
    private ITimer? _timer;
    private PerfSnapshot _latest;
    private readonly List<PerfSnapshot> _history = new(HistoryLength);
    private long _version;
    private bool _warned;

    public PerfSampler(Func<IPerfSource> factory, TimeProvider time)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <summary>Whether the timer runs.</summary>
    public bool Running
    {
        get { lock (_gate) { return _timer is not null; } }
    }

    /// <summary>Starts the sampling (the first reading at once) or stops it; the same state again does nothing.</summary>
    public void Ensure(bool on)
    {
        IPerfSource? stopped = null;
        lock (_gate)
        {
            if (on && _timer is null)
            {
                try
                {
                    _source = _factory();
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    WarnOnce($"The performance bar's source did not open ({ex.GetType().Name}: {ex.Message}); it shows nothing.");
                    _source = new NullPerfSource();
                }

                _timer = _time.CreateTimer(_ => Tick(), null, TimeSpan.Zero, Interval);
            }
            else if (!on && _timer is not null)
            {
                _timer.Dispose();
                _timer = null;
                stopped = _source;
                _source = null;
                _latest = PerfSnapshot.None;
                _history.Clear();
                _version++;
            }
        }

        if (stopped is not null)
        {
            lock (_sampleGate)
            {
                stopped.Dispose();
            }
        }
    }

    /// <summary>The latest reading, the history oldest first (at most <see cref="HistoryLength"/>), and a version that moves with each reading.</summary>
    public (PerfSnapshot Latest, IReadOnlyList<PerfSnapshot> History, long Version) Read()
    {
        lock (_gate)
        {
            return (_latest, _history.ToArray(), _version);
        }
    }

    private void Tick()
    {
        lock (_sampleGate)
        {
            IPerfSource? source;
            lock (_gate)
            {
                source = _source;
            }

            if (source is null)
            {
                return;
            }

            PerfSnapshot reading;
            try
            {
                reading = source.Sample();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                WarnOnce($"The performance bar could not read the machine ({ex.GetType().Name}: {ex.Message}).");
                reading = PerfSnapshot.None;
            }

            lock (_gate)
            {
                if (!ReferenceEquals(source, _source))
                {
                    return;   // stopped (or stopped and started again) while it sampled
                }

                _latest = reading;
                if (_history.Count == HistoryLength)
                {
                    _history.RemoveAt(0);
                }

                _history.Add(reading);
                _version++;
            }
        }
    }

    private void WarnOnce(string message)
    {
        if (!_warned)
        {
            _warned = true;
            DiagnosticLog.Warn(Category, message);
        }
    }

    public void Dispose() => Ensure(false);
}
