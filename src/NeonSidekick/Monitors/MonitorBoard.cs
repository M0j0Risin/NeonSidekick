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

    /// <summary>Whether <paramref name="monitor"/> shows this PC (later on 2026-10-23): its input as read, its away mark and the pick in <paramref name="words"/>.</summary>
    public MonitorShowing ShowingOf(MonitorInfo monitor, MonitorWords words)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(words);
        return MonitorShowing.Judge(monitor, InputsOf(monitor).Current, AwayOf(monitor), words.PcInput(monitor));
    }

    /// <summary>The monitors on another input, in number order.</summary>
    public IReadOnlyList<MonitorInfo> Elsewhere(MonitorWords words) => Topology.Monitors.Where(m => ShowingOf(m, words).IsElsewhere).ToList();

    /// <summary>
    /// Which input of <paramref name="monitor"/> is this PC's (2026-10-27, the NVIDIA plan): the user's pick, else the cable's guess (the current
    /// input when it is of the cable's kind, else the only offered input of that kind), the pair the PC picker shows; null when neither is known.
    /// Not the away mark: it is pruned when a monitor leaves the desktop.
    /// </summary>
    public byte? PcInputOf(MonitorInfo monitor, MonitorWords words)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(words);
        var inputs = InputsOf(monitor);
        return words.PcInput(monitor) ?? MonitorShowing.CableGuess(monitor, inputs.Current, inputs.Offered);
    }

    /// <summary>
    /// The switch of <paramref name="monitor"/> to <paramref name="code"/>, the one builder every input change takes (the pane's rows and its
    /// Enter a code…, <c>/monitor</c> on the screen and headless, <c>monitor_input</c>): <see cref="MonitorChange.Unlisted"/> for a code it
    /// neither lists nor is common, and <see cref="MonitorChange.Return"/> when it is off the desktop and the code is this PC's
    /// (<see cref="PcInputOf"/>), so the switch also adds it back; <see cref="MonitorChange.ExtendsAll"/> when that Extend all also brings
    /// back another monitor off the desktop or ends a Duplicate (2026-10-27, the review).
    /// </summary>
    public MonitorChange SwitchTo(MonitorInfo monitor, byte code, MonitorWords words)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        var inputs = InputsOf(monitor);
        bool unlisted = !inputs.Offered.Contains(code) && !Mccs.CommonInputs.Contains(code);
        bool back = !monitor.Active && !monitor.Shared && PcInputOf(monitor, words) == code;
        bool all = back && (Topology.Layout == MonitorArrangement.Duplicated
            || Topology.Monitors.Any(m => !m.Active && !string.Equals(m.Target, monitor.Target, StringComparison.Ordinal)));
        return MonitorChange.SwitchInput(monitor, code, unlisted) with { Return = back, ExtendsAll = all };
    }
}

/// <summary>
/// This PC's monitors as the screen knows them (2026-10-23, the monitors plan): one per screen, every call through one
/// <see cref="MonitorCalls"/> (one at a time, a pool thread, never the screen's), and nothing read until a front end asks — the 🖥️ chip shows
/// nothing that changes. <see cref="State"/> is an immutable record swapped by reference, read with no lock, its <see cref="MonitorState.Version"/>
/// the pane's watcher follows (<see cref="Changed"/>). The topology (cheap) is read when the pane opens, by <c>/monitor</c> and the tools, after a
/// change, and every <see cref="TopologyEvery"/> only while a <see cref="Watch"/> lease is held — refcounted, so a pane closed and opened again
/// at once (the chip's double-click after a right-click) never has its clock stopped by the old one's late dispose; a monitor newly on the
/// desktop then has its inputs read too. Inputs (DDC/CI, slow) are read on <see cref="ReadInputsAsync"/>: each active monitor's current input,
/// and its capabilities the first time, cached per key for the process; on that first read a monitor's last reported list (the screen's
/// <c>remembered</c>, 2026-10-27) stands in until its capabilities answer. A failed read keeps the last value, marked stale; a first read that
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
    private readonly Func<MonitorInfo, IReadOnlyList<MonitorInfo>, IReadOnlyList<byte>?>? _remembered;
    private readonly Action<MonitorInfo, IReadOnlyList<MonitorInfo>, IReadOnlyList<byte>>? _seen;
    private volatile MonitorState _state = MonitorState.Empty;
    private ITimer? _clock;
    private int _leases;
    private int _ticking;
    private bool _disposed;

    /// <param name="quick">No settle after a change and no gap before a retry (the tests').</param>
    /// <param name="remembered">
    /// A monitor's last reported inputs among the connected ones (2026-10-27, the user's pick: the screen's <c>Monitor inputs seen</c>), shown
    /// while its capabilities are read on a run's first open; null for none. Asked only for a monitor with a key.
    /// </param>
    /// <param name="seen">
    /// Told, on the reading thread, when a monitor with a key reports a list that differs from <paramref name="remembered"/>'s (the screen writes
    /// it). Never for a read that failed or lists none.
    /// </param>
    public MonitorBoard(IMonitorSystem system, TimeProvider? time = null, bool quick = false,
        Func<MonitorInfo, IReadOnlyList<MonitorInfo>, IReadOnlyList<byte>?>? remembered = null,
        Action<MonitorInfo, IReadOnlyList<MonitorInfo>, IReadOnlyList<byte>>? seen = null)
    {
        ArgumentNullException.ThrowIfNull(system);
        _time = time ?? TimeProvider.System;
        _remembered = remembered;
        _seen = seen;
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
    /// Every monitor's current input and (the first time) capabilities; a duplicated one gets its sentence instead. A monitor off the desktop is
    /// read too (2026-10-27, the NVIDIA plan: the system reaches it through NVIDIA's driver, or answers <see cref="MonitorText.OffDesktop"/>):
    /// for it, read means its current input is known (caps kept from the desktop don't count), and a failure is its problem, never stale. With
    /// <paramref name="onlyUnread"/>, only the monitors never read (the clock's, for one newly on or off the desktop). Two rounds
    /// (2026-10-27, the user's pick): every monitor's current input first, then the capabilities not cached yet, so no tab waits on another
    /// monitor's slow list (the XG27's took ~5 s). A monitor with a remembered list shows it from the first round; the second round's answer
    /// replaces it when it differs (and is <c>seen</c>), and a failed read keeps it. A list cached from earlier is told too (2026-10-27, the
    /// review: a profile switched to never saved it). The second round waits while a change runs (<see cref="MonitorCalls.Foreground"/>), and
    /// any failure, not only a cancel, leaves no tab marked reading.
    /// </summary>
    public async Task ReadInputsAsync(CancellationToken cancellationToken, bool onlyUnread = false)
    {
        var topology = _state.Topology;
        try
        {
            await ReadRoundsAsync(topology, onlyUnread, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Whatever ended the read (a cancel, a seen callback that threw), no tab is left waiting on a round that won't come.
            foreach (var m in topology.Monitors)
            {
                if (_state.InputsOf(m).Reading)
                {
                    Set(m.Target, _state.InputsOf(m) with { Reading = false });
                }
            }

            throw;
        }
    }

    private async Task ReadRoundsAsync(MonitorSnapshot topology, bool onlyUnread, CancellationToken cancellationToken)
    {
        var behind = new List<(MonitorInfo Monitor, IReadOnlyList<byte>? Remembered)>();
        foreach (var m in topology.Monitors)
        {
            var known = _state.InputsOf(m);
            if (m.Shared)
            {
                Set(m.Target, known with { Problem = MonitorText.SharedSource, Reading = false });
                continue;
            }

            bool read = known.Current is not null || (m.Active && known.Caps is not null);
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
                var now = _state.InputsOf(m) with { Current = current, Problem = null, Stale = false };
                if (Cached(m) is { } cached)
                {
                    Set(m.Target, now with { Caps = cached, Reading = false });
                    Tell(m, topology, Remembered(m, topology), cached);
                    continue;
                }

                // Not cached: the capabilities in the second round. A remembered list shows now, and the tab with it.
                var remembered = Remembered(m, topology);
                Set(m.Target, remembered is null ? now : now with { Caps = new MonitorCaps(remembered, true, ""), Reading = false });
                behind.Add((m, remembered));
            }
            catch (MonitorException e)
            {
                var now = _state.InputsOf(m);
                Set(m.Target, read && m.Active ? now with { Stale = true, Reading = false } : now with { Problem = e.Message, Reading = false });
            }
        }

        foreach (var (m, remembered) in behind)
        {
            var caps = await CapsAsync(m, cancellationToken).ConfigureAwait(false);
            var now = _state.InputsOf(m);
            if (caps is null)
            {
                // The input reads, the list doesn't: the remembered list stays; without one, the common four (not reported).
                Set(m.Target, now with { Caps = remembered is null ? new MonitorCaps(Mccs.CommonInputs, false, "") : now.Caps, Reading = false });
                continue;
            }

            Set(m.Target, now with { Caps = caps, Reading = false });
            Tell(m, topology, remembered, caps);
        }
    }

    // The monitor's last reported list among the connected ones, for one with a key; null for none.
    private IReadOnlyList<byte>? Remembered(MonitorInfo m, MonitorSnapshot topology) =>
        m.Key is not null && _remembered?.Invoke(m, topology.Monitors) is { Count: > 0 } list ? list : null;

    // A reported list that differs from the remembered one is seen (the screen writes it).
    private void Tell(MonitorInfo m, MonitorSnapshot topology, IReadOnlyList<byte>? remembered, MonitorCaps caps)
    {
        if (caps.Reported && m.Key is not null && (remembered is null || !remembered.SequenceEqual(caps.Inputs)))
        {
            _seen?.Invoke(m, topology.Monitors, caps.Inputs);
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

    private MonitorCaps? Cached(MonitorInfo m)
    {
        lock (_gate)
        {
            return _caps.GetValueOrDefault(m.Key ?? m.Target);
        }
    }

    // The capabilities read and cached per key for the process; null when the read fails (not cached, so the next open asks again).
    private async Task<MonitorCaps?> CapsAsync(MonitorInfo m, CancellationToken cancellationToken)
    {
        if (Cached(m) is { } cached)
        {
            return cached;
        }

        // After any change the user waits on (2026-10-27, the review): a list takes seconds, and a switch queued behind it waited them out.
        await Actions.Calls.WhenIdleAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var caps = await Actions.Calls.RunAsync("capabilities of " + m.Label, s => s.Capabilities(m.Target), retry: true, cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                _caps[m.Key ?? m.Target] = caps;
            }

            return caps;
        }
        catch (MonitorException)
        {
            return null;
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
