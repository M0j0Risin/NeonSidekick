using NeonSidekick.Diagnostics;

namespace NeonSidekick.Monitors;

/// <summary>
/// What is known of one monitor's inputs: its current input (null until read), its capabilities, the problem that stands for its inputs
/// (no DDC/CI, off the desktop, duplicated — a sentence; null for none), whether the first read is still under way, and whether the last
/// read failed so <see cref="Current"/> may be old.
/// </summary>
public sealed record MonitorInputs(byte? Current, MonitorCaps? Caps, string? Problem, bool Reading, bool Stale)
{
    /// <summary>Nothing read yet.</summary>
    public static readonly MonitorInputs Unread = new(null, null, null, false, false);

    /// <summary>The inputs a tab offers: the reported ones, else the common four.</summary>
    public IReadOnlyList<byte> Offered => Caps?.Inputs ?? Mccs.CommonInputs;

    /// <summary>Whether the monitor lists its inputs (false: the common four, under <see cref="MonitorText.NotReported"/>).</summary>
    public bool Reported => Caps?.Reported ?? false;

    /// <summary>Whether its inputs can be switched: read once, no problem standing.</summary>
    public bool CanSwitch => Problem is null && !Reading && (Current is not null || Caps is not null);
}

/// <summary>The board's snapshot: the topology, each monitor's inputs by <see cref="MonitorInfo.Target"/>, the app's away marks, and a version that moves with every change.</summary>
public sealed record MonitorState(MonitorSnapshot Topology, IReadOnlyDictionary<string, MonitorInputs> Inputs, IReadOnlyDictionary<string, byte> Away, long Version)
{
    public static readonly MonitorState Empty = new(MonitorSnapshot.Empty, new Dictionary<string, MonitorInputs>(StringComparer.Ordinal), new Dictionary<string, byte>(StringComparer.Ordinal), 0);

    /// <summary>What is known of <paramref name="monitor"/>'s inputs.</summary>
    public MonitorInputs InputsOf(MonitorInfo monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        return Inputs.GetValueOrDefault(monitor.Target) ?? MonitorInputs.Unread;
    }

    /// <summary>The input the app switched <paramref name="monitor"/> away from, or null.</summary>
    public byte? AwayOf(MonitorInfo monitor) => monitor.Key is { } key && Away.TryGetValue(key, out byte left) ? left : null;
}

/// <summary>
/// This PC's monitors as the screen knows them (2026-10-23, the monitors plan): one per screen, every call through one
/// <see cref="MonitorCalls"/> (one at a time, a pool thread, never the screen's), and nothing read until a front end asks — the 🖥️ chip shows
/// nothing that changes. <see cref="State"/> is an immutable record swapped by reference, read with no lock, its <see cref="MonitorState.Version"/>
/// the pane's watcher follows (<see cref="Changed"/>). The topology (cheap) is read when the pane opens, by <c>/monitor</c> and the tools, after a
/// change, and every <see cref="TopologyEvery"/> only while a <see cref="Watch"/> lease is held — refcounted, so a pane closed and opened again
/// at once (the chip's double-click after a right-click) never has its clock stopped by the old one's late dispose; a monitor newly on the
/// desktop then has its inputs read too. Inputs (DDC/CI, slow) are read on <see cref="ReadInputsAsync"/>: each active monitor's current input,
/// and its capabilities the first time, cached per key for the process. A failed read keeps the last value, marked stale; a first read that
/// fails is the monitor's <see cref="MonitorInputs.Problem"/>. The snapshot is this PC's, not a profile's: a profile switch leaves it be (the
/// input names are read from the settings in force). Dispose never waits on a running call.
/// </summary>
public sealed class MonitorBoard : IDisposable
{
    /// <summary>The topology clock while a lease is held.</summary>
    public static readonly TimeSpan TopologyEvery = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private readonly Dictionary<string, MonitorCaps> _caps = new(StringComparer.Ordinal);
    private volatile MonitorState _state = MonitorState.Empty;
    private ITimer? _clock;
    private int _leases;
    private int _ticking;
    private bool _disposed;

    /// <param name="quick">No settle after a change and no gap before a retry (the tests').</param>
    public MonitorBoard(IMonitorSystem system, TimeProvider? time = null, bool quick = false)
    {
        ArgumentNullException.ThrowIfNull(system);
        _time = time ?? TimeProvider.System;
        Actions = new MonitorActions(new MonitorCalls(system, _time, quick ? TimeSpan.Zero : null), new MonitorAway(), _time, quick ? TimeSpan.Zero : null);
        Actions.TopologyRead += OnTopology;
        Actions.InputRead += OnInput;
    }

    public MonitorActions Actions { get; }

    public IMonitorSystem System => Actions.Calls.System;

    /// <summary>The snapshot now.</summary>
    public MonitorState State => _state;

    /// <summary>Heard (on the reading thread) after every change of <see cref="State"/>.</summary>
    public event Action? Changed;

    /// <summary>Whether the topology clock runs (a lease is held).</summary>
    public bool Watching
    {
        get
        {
            lock (_gate)
            {
                return _clock is not null;
            }
        }
    }

    /// <summary>A lease on the topology clock: the first starts it, the last one's dispose stops it.</summary>
    public IDisposable Watch()
    {
        lock (_gate)
        {
            if (!_disposed && ++_leases == 1)
            {
                _clock = _time.CreateTimer(_ => Tick(), null, TopologyEvery, TopologyEvery);
            }
        }

        return new Lease(this);
    }

    /// <summary>The topology read now; the last one kept when the read fails (a locked PC, a remote session).</summary>
    public async Task<MonitorSnapshot> RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await Actions.QueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (MonitorException e)
        {
            DiagnosticLog.Debug("Monitors", "topology read failed: " + e.Message);
            return _state.Topology;
        }
    }

    /// <summary>The pane's open: the topology, then every active monitor's inputs.</summary>
    public async Task OpenAsync(CancellationToken cancellationToken)
    {
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
        await ReadInputsAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Every active monitor's current input and (the first time) capabilities; a monitor off the desktop or duplicated gets its sentence
    /// instead. With <paramref name="onlyUnread"/>, only the monitors never read (the clock's, for one newly on the desktop).
    /// </summary>
    public async Task ReadInputsAsync(CancellationToken cancellationToken, bool onlyUnread = false)
    {
        var topology = _state.Topology;
        foreach (var m in topology.Monitors)
        {
            var known = _state.InputsOf(m);
            if (!m.Active || m.Shared)
            {
                Set(m.Target, known with { Problem = m.Active ? MonitorText.SharedSource : MonitorText.OffDesktop, Reading = false });
                continue;
            }

            bool read = known.Current is not null || known.Caps is not null;
            if (onlyUnread && (read || known.Problem is not null))
            {
                continue;
            }

            if (!read)
            {
                Set(m.Target, known with { Reading = true, Problem = null });
            }

            try
            {
                byte current = await Actions.ReadInputAsync(m, cancellationToken).ConfigureAwait(false);
                var caps = await CapsAsync(m, cancellationToken).ConfigureAwait(false);
                Set(m.Target, _state.InputsOf(m) with { Current = current, Caps = caps, Problem = null, Reading = false, Stale = false });
            }
            catch (MonitorException e)
            {
                var now = _state.InputsOf(m);
                Set(m.Target, read ? now with { Stale = true, Reading = false } : now with { Problem = e.Message, Reading = false });
            }
            catch (OperationCanceledException)
            {
                Set(m.Target, _state.InputsOf(m) with { Reading = false });
                throw;
            }
        }
    }

    /// <summary>The away marks moved (a switch): the state follows.</summary>
    public void NoteAway() => Update(s => s with { Away = Actions.Away.Marks });

    public void Dispose()
    {
        ITimer? clock;
        lock (_gate)
        {
            _disposed = true;
            clock = _clock;
            _clock = null;
            _leases = 0;
        }

        clock?.Dispose();
        Actions.TopologyRead -= OnTopology;
        Actions.InputRead -= OnInput;
    }

    private async Task<MonitorCaps> CapsAsync(MonitorInfo m, CancellationToken cancellationToken)
    {
        string cacheKey = m.Key ?? m.Target;
        lock (_gate)
        {
            if (_caps.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }
        }

        try
        {
            var caps = await Actions.Calls.RunAsync("capabilities of " + m.Label, s => s.Capabilities(m.Target), retry: true, cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                _caps[cacheKey] = caps;
            }

            return caps;
        }
        catch (MonitorException)
        {
            // The input reads, the list doesn't: the common four, not cached, so the next open asks again.
            return new MonitorCaps(Mccs.CommonInputs, false, "");
        }
    }

    private void Tick()
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                if (!Watching)
                {
                    return;
                }

                await RefreshAsync(CancellationToken.None).ConfigureAwait(false);
                await ReadInputsAsync(CancellationToken.None, onlyUnread: true).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                DiagnosticLog.Debug("Monitors", "clock read failed: " + e.Message);
            }
            finally
            {
                Volatile.Write(ref _ticking, 0);
            }
        });
    }

    private void OnTopology(MonitorSnapshot snapshot) => Update(s =>
    {
        if (Same(s.Topology, snapshot) && SameAway(s.Away, Actions.Away.Marks))
        {
            return s;
        }

        // A monitor gone, or gone off the desktop, keeps no stale inputs; one that stays keeps what was read.
        var inputs = new Dictionary<string, MonitorInputs>(StringComparer.Ordinal);
        foreach (var m in snapshot.Monitors)
        {
            if (s.Inputs.TryGetValue(m.Target, out var known))
            {
                var old = s.Topology.ByTarget(m.Target);
                bool sameState = old is not null && old.Active == m.Active && old.Shared == m.Shared;
                inputs[m.Target] = sameState ? known : MonitorInputs.Unread with { Caps = known.Caps };
            }
        }

        return s with { Topology = snapshot, Inputs = inputs, Away = Actions.Away.Marks };
    });

    private void OnInput(string target, byte current) => Update(s =>
    {
        var known = s.Inputs.GetValueOrDefault(target) ?? MonitorInputs.Unread;
        if (known.Current == current && !known.Stale && SameAway(s.Away, Actions.Away.Marks))
        {
            return s;
        }

        var inputs = new Dictionary<string, MonitorInputs>(s.Inputs, StringComparer.Ordinal) { [target] = known with { Current = current, Stale = false } };
        return s with { Inputs = inputs, Away = Actions.Away.Marks };
    });

    private void Set(string target, MonitorInputs inputs) => Update(s =>
    {
        if (s.Inputs.TryGetValue(target, out var known) && known == inputs)
        {
            return s;
        }

        return s with { Inputs = new Dictionary<string, MonitorInputs>(s.Inputs, StringComparer.Ordinal) { [target] = inputs } };
    });

    private void Update(Func<MonitorState, MonitorState> change)
    {
        bool changed;
        lock (_gate)
        {
            var before = _state;
            var after = change(before);
            changed = !ReferenceEquals(before, after);
            if (changed)
            {
                _state = after with { Version = before.Version + 1 };
            }
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    private static bool Same(MonitorSnapshot a, MonitorSnapshot b) => a.Layout == b.Layout && a.Monitors.SequenceEqual(b.Monitors);

    private static bool SameAway(IReadOnlyDictionary<string, byte> a, IReadOnlyDictionary<string, byte> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out byte v) && v == kv.Value);

    private void Release()
    {
        ITimer? stop = null;
        lock (_gate)
        {
            if (_leases > 0 && --_leases == 0)
            {
                stop = _clock;
                _clock = null;
            }
        }

        stop?.Dispose();
    }

    private sealed class Lease(MonitorBoard board) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
            {
                board.Release();
            }
        }
    }
}
