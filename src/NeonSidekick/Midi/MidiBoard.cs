using System.Globalization;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.Midi;

/// <summary>An input as the board last saw it: listed, open or not, and why not when it couldn't be opened (<see cref="Problem"/>; <see cref="InUse"/> when another app holds it).</summary>
public sealed record MidiInputState(string Name, int Occurrence, bool Open, bool InUse, string? Problem);

/// <summary>The last message the board heard (2026-11-12): the Devices tab's live line and <c>--midi-check</c>'s.</summary>
public sealed record MidiSeen(MidiTrigger Trigger, int Value, bool Fires);

/// <summary>
/// The MIDI listening (2026-11-12, the MIDI plan). It opens <b>only the inputs the profile's bindings name</b> while <i>MIDI input</i> is on,
/// so a music app keeps the rest (WinMM's classic stack opens a port for one app only; Windows MIDI Services and CoreMIDI share). A
/// learn (<see cref="LearnAsync"/>) adds every other input for its length and puts them back after; the first press it hears is the
/// button. Each message is decoded (<see cref="MidiParser"/>), judged a press or not (<see cref="MidiEdge"/>) and routed: to a waiting
/// learn; else, unless the MIDI pane holds the board (<see cref="Hold"/>: a foot on the pedal must not close the pane mid-edit), to the
/// binding's command line through <c>onLine</c>, which only queues it for the screen (<c>KeySource.PostCommand</c>). <see cref="Changed"/>
/// fires on every message and every change of the inputs, for the pane.
/// <para>Threads: <see cref="Apply"/>, <see cref="Reload"/>, <see cref="Hold"/> and the rescan only signal; the opening and closing run
/// on a pool thread under <c>_gate</c> (a slow driver never holds the screen, the reply, or a Kestrel thread). Bytes arrive on each
/// backend's pump thread and are routed under <c>_route</c>, never <c>_gate</c>: closing an input waits for its pump, which must never wait
/// on the closer. The bindings are a snapshot swapped whole. A rescan every <see cref="RescanEvery"/> picks up a pedal plugged in later
/// and closes one pulled out; with nothing to listen to it reads nothing.</para>
/// </summary>
public sealed class MidiBoard : IDisposable
{
    /// <summary>How often the inputs are listed again while anything listens.</summary>
    public static readonly TimeSpan RescanEvery = TimeSpan.FromSeconds(3);

    private const string Category = "MIDI";

    private readonly IMidiSystem _system;
    private readonly Action<string> _onLine;
    private readonly Action<string>? _notice;
    private readonly Action<Action> _run;
    private readonly Lock _gate = new();
    private readonly Lock _route = new();
    private readonly ITimer _timer;
    private readonly Dictionary<string, OpenInput> _open = new(StringComparer.Ordinal);
    private readonly HashSet<string> _noticed = new(StringComparer.Ordinal);
    private readonly MidiEdge _edge = new();

    private MidiBindingSet _set = MidiBindingSet.Empty;
    private IReadOnlyList<MidiInputState> _inputs = [];
    private MidiSeen? _seen;
    private TaskCompletionSource<MidiTrigger>? _learn;
    private string? _profile;
    private bool _enabled;
    private bool _reload;
    private int _holds;
    private int _syncQueued;
    private long _version;
    private bool _disposed;

    // Whether the last Sync listened (2026-11-14, the review's #18), so off then on says a failed open again. Sync's own, under _gate.
    private bool _wasListening;

    /// <param name="system">The backend.</param>
    /// <param name="onLine">A press's command line, called on a pump thread: it must only queue.</param>
    /// <param name="notice">A line for the user (a bound input that can't be opened), once per input until it opens, or until MIDI input is turned
    /// off and on or the bindings are read again; may be called on any thread.</param>
    /// <param name="time">The rescan's clock.</param>
    /// <param name="run">How the opening and closing are run: a pool thread by default; the tests run it inline.</param>
    public MidiBoard(IMidiSystem system, Action<string> onLine, Action<string>? notice = null, TimeProvider? time = null, Action<Action>? run = null)
    {
        _system = system ?? throw new ArgumentNullException(nameof(system));
        _onLine = onLine ?? throw new ArgumentNullException(nameof(onLine));
        _notice = notice;
        _run = run ?? (work => _ = Task.Run(work));
        _timer = (time ?? TimeProvider.System).CreateTimer(_ => Schedule(), null, RescanEvery, RescanEvery);
    }

    /// <summary>Raised on every message heard and every change of the inputs or the bindings, on whichever thread made it. Must not block.</summary>
    public event Action? Changed;

    /// <summary>Moves with each <see cref="Changed"/>.</summary>
    public long Version => Interlocked.Read(ref _version);

    /// <summary>The bindings the board routes by (the last load).</summary>
    public MidiBindingSet Bindings => Volatile.Read(ref _set);

    /// <summary>Every input listed at the last scan, open or not.</summary>
    public IReadOnlyList<MidiInputState> Inputs => Volatile.Read(ref _inputs);

    /// <summary>The last message heard, or null.</summary>
    public MidiSeen? LastSeen => Volatile.Read(ref _seen);

    /// <summary>Whether <i>MIDI input</i> is on (as last applied).</summary>
    public bool Enabled => Volatile.Read(ref _enabled);

    /// <summary>Whether a learn is waiting for a press.</summary>
    public bool Learning => Volatile.Read(ref _learn) is not null;

    /// <summary>Whether the MIDI pane holds the board (presses shown, never run).</summary>
    public bool Held => Volatile.Read(ref _holds) > 0;

    /// <summary>
    /// <i>MIDI input</i> and the profile as they are now (from <c>AppSettings.Changed</c>, any thread): a new profile's bindings are read
    /// and its inputs opened, the old ones closed; off closes every input a learn isn't using. Only signals.
    /// </summary>
    public void Apply(bool enabled, string profileDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileDirectory);
        lock (_route)
        {
            Volatile.Write(ref _enabled, enabled);
            if (!string.Equals(_profile, profileDirectory, StringComparison.OrdinalIgnoreCase))
            {
                _profile = profileDirectory;
                _reload = true;
            }
        }

        Schedule();
    }

    /// <summary>The bindings read again (the pane saved them, or a profile section came in). Only signals.</summary>
    public void Reload()
    {
        lock (_route)
        {
            _reload = true;
        }

        Schedule();
    }

    /// <summary>
    /// A lease the MIDI pane holds while it is open (2026-11-12): presses are shown (<see cref="LastSeen"/>, <see cref="Changed"/>) and
    /// a learn still hears them, but no binding runs. The first lease lists the inputs for the Devices tab; the last one's dispose lets
    /// the buttons run again.
    /// </summary>
    public IDisposable Hold()
    {
        Interlocked.Increment(ref _holds);
        Schedule();
        return new Lease(this);
    }

    /// <summary>
    /// The next press on any input (its button), or null when <paramref name="cancellationToken"/> fires first. Every input the board can
    /// open listens meanwhile (those bound stay on their handles); after, only the bound ones stay open. One learn at a time: a second
    /// replaces the first, which comes back null.
    /// </summary>
    public async Task<MidiTrigger?> LearnAsync(CancellationToken cancellationToken)
    {
        var learn = new TaskCompletionSource<MidiTrigger>(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Exchange(ref _learn, learn)?.TrySetCanceled(CancellationToken.None);
        Schedule();
        Bump();
        try
        {
            using var stop = cancellationToken.Register(() => learn.TrySetCanceled(cancellationToken));
            return await learn.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            Interlocked.CompareExchange(ref _learn, null, learn);
            Schedule();
            Bump();
        }
    }

    /// <summary>
    /// The scan and the opens and closes run now, on this thread: the body <see cref="Schedule"/> runs (tests drive it through the board's
    /// run delegate). <c>--midi-check</c> has its own loop and never calls it (2026-11-14, the review's #20).
    /// </summary>
    public void SyncNow()
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                Sync();
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _timer.Dispose();
            Volatile.Read(ref _learn)?.TrySetCanceled(CancellationToken.None);
            foreach (var open in _open.Values)
            {
                Close(open);
            }

            _open.Clear();
        }
    }

    /// <summary>The binding the press <paramref name="trigger"/> runs, on an input whose name is <paramref name="unique"/> or not. Pure.</summary>
    public static MidiBinding? BindingFor(MidiBindingSet set, MidiTrigger trigger, bool unique)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(trigger);
        return set.Bindings.FirstOrDefault(b =>
            b.Trigger.Kind == trigger.Kind && b.Trigger.Channel == trigger.Channel && b.Trigger.Number == trigger.Number
            && MidiNames.Same(b.Trigger.Device, trigger.Device) && (unique || b.Trigger.Occurrence == trigger.Occurrence));
    }

    /// <summary>
    /// Whether a binding names the input <paramref name="name"/> at <paramref name="occurrence"/> of <paramref name="sameNamed"/>
    /// inputs of that name: by its occurrence, or any when the name is the only one of its kind. Pure.
    /// </summary>
    public static bool Names(MidiBindingSet set, string name, int occurrence, int sameNamed)
    {
        ArgumentNullException.ThrowIfNull(set);
        return set.Bindings.Any(b => MidiNames.Same(b.Trigger.Device, name) && (sameNamed == 1 || b.Trigger.Occurrence == occurrence));
    }

    private void Schedule()
    {
        if (Interlocked.Exchange(ref _syncQueued, 1) == 0)
        {
            _run(() =>
            {
                Volatile.Write(ref _syncQueued, 0);
                SyncNow();
            });
        }
    }

    private void Bump()
    {
        Interlocked.Increment(ref _version);
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error(Category, "A MIDI change handler failed: " + ex.Message, ex);
        }
    }

    /// <summary>The inputs listed and each opened or closed as the board wants it. Caller holds <c>_gate</c>.</summary>
    private void Sync()
    {
        string? profile;
        bool reload;
        lock (_route)
        {
            profile = _profile;
            reload = _reload;
            _reload = false;
        }

        bool changed = false;
        if (reload && profile is not null)
        {
            Volatile.Write(ref _set, new MidiBindings(profile).Load());
            changed = true;
        }

        var set = Volatile.Read(ref _set);
        bool learning = Learning;
        bool listening = Enabled && set.Bindings.Count > 0;

        // An input that couldn't open is said again after MIDI input goes off and on, or the bindings are read again (2026-11-14, the review's
        // #18): the user just asked for it once more. Tracked here, not in the idle branch below, which a held pane skips.
        if (reload || (listening && !_wasListening))
        {
            _noticed.Clear();
        }

        _wasListening = listening;
        if (!listening && !learning && !Held)
        {
            // Nothing listens and nothing is shown: every input closed, nothing listed (no enumeration while idle).
            changed |= CloseAll() | (Inputs.Count > 0);
            Volatile.Write(ref _inputs, []);
            if (changed)
            {
                Bump();
            }

            return;
        }

        IReadOnlyList<MidiInputInfo> listed;
        try
        {
            listed = _system.Inputs();
        }
        catch (MidiException ex)
        {
            DiagnosticLog.Warn(Category, "Could not list the MIDI inputs: " + ex.Message, ex);
            listed = [];
        }

        var occurrences = MidiNames.Occurrences(listed);
        var counts = listed.GroupBy(i => MidiNames.Normalize(i.Name), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var states = new List<MidiInputState>(listed.Count);
        var present = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < listed.Count; i++)
        {
            var info = listed[i];
            string name = MidiNames.Normalize(info.Name);
            int occurrence = occurrences[i];
            int sameNamed = counts[name];
            string key = Key(name, occurrence);
            present.Add(key);
            bool wanted = learning || (listening && Names(set, name, occurrence, sameNamed));
            // Its place in the list or its listed name changed (2026-11-14, the review's #7 and #8): reopened. WinMM says nothing when a
            // device is unplugged, so a replug inside one rescan was kept on a dead handle; Windows numbers it anew as it comes back
            // ("2 - PAD", then "3 - PAD"), the same input to the bindings (Normalize) but another name to the listing, and that is the sign.
            if (_open.TryGetValue(key, out var open) && open.Info != info)
            {
                Close(open);
                _open.Remove(key);
                changed = true;
                open = null;
            }

            string? problem = null;
            bool inUse = false;
            if (wanted && open is null)
            {
                try
                {
                    var parser = new MidiParser();
                    var entry = new OpenInput(key, info) { Unique = sameNamed == 1 };
                    entry.Handle = _system.Open(info, bytes => Heard(key, name, occurrence, entry, parser, bytes));
                    _open[key] = entry;
                    _noticed.Remove(key);
                    changed = true;
                    DiagnosticLog.Info(Category, $"Opened MIDI input \"{name}\"{(occurrence > 0 ? " #" + (occurrence + 1).ToString(CultureInfo.InvariantCulture) : "")}.");
                }
                catch (MidiException ex)
                {
                    problem = ex.Message;
                    inUse = ex.InUse;
                    if (listening && Names(set, name, occurrence, sameNamed) && _noticed.Add(key))
                    {
                        DiagnosticLog.Warn(Category, $"Could not open MIDI input \"{name}\": {ex.Message}", ex);
                        _notice?.Invoke(MidiText.OpenFailedNotice(name, ex.Message));
                    }
                }
            }
            else if (!wanted && open is not null)
            {
                Close(open);
                _open.Remove(key);
                changed = true;
            }

            // Whether it is the only one of its name, each scan (2026-11-14, the review's #1): a twin plugged in or pulled out later changes
            // which bindings its presses match, with no reopen (that would reset its button state and might lose the port to another app).
            if (_open.TryGetValue(key, out var held))
            {
                held.Unique = sameNamed == 1;
            }

            states.Add(new MidiInputState(name, occurrence, held is not null, inUse, problem));
        }

        // Pulled out: closed, and its button state forgotten.
        foreach (var gone in _open.Keys.Where(k => !present.Contains(k)).ToList())
        {
            Close(_open[gone]);
            _open.Remove(gone);
            changed = true;
            DiagnosticLog.Info(Category, $"MIDI input {gone} is gone; closed.");
        }

        if (!changed && states.SequenceEqual(Inputs))
        {
            return;
        }

        Volatile.Write(ref _inputs, states);
        Bump();
    }

    private bool CloseAll()
    {
        if (_open.Count == 0)
        {
            return false;
        }

        foreach (var open in _open.Values)
        {
            Close(open);
        }

        _open.Clear();
        return true;
    }

    private void Close(OpenInput open)
    {
        try
        {
            open.Handle.Dispose();
        }
        catch (MidiException ex)
        {
            DiagnosticLog.Warn(Category, $"Could not close MIDI input {open.Key}: {ex.Message}", ex);
        }

        lock (_route)
        {
            _edge.Forget(open.Key);
        }
    }

    /// <summary>An input's bytes, on its pump thread: decoded, judged, and routed. Never takes <c>_gate</c>.</summary>
    private void Heard(string key, string name, int occurrence, OpenInput input, MidiParser parser, ReadOnlySpan<byte> bytes)
    {
        bool unique = input.Unique;
        List<string>? lines = null;
        bool any;
        lock (_route)
        {
            var messages = new List<MidiMessage>(2);
            parser.Feed(bytes, messages);
            any = messages.Count > 0;
            foreach (var message in messages)
            {
                bool fires = _edge.Fires(key, message);
                var trigger = new MidiTrigger(name, occurrence, message.Kind, message.Channel, message.Number);
                Volatile.Write(ref _seen, new MidiSeen(trigger, message.Value, fires));
                if (!fires)
                {
                    continue;
                }

                if (Volatile.Read(ref _learn) is { } learn)
                {
                    learn.TrySetResult(trigger);
                    continue;
                }

                if (Held || !Enabled)
                {
                    continue;
                }

                if (BindingFor(Volatile.Read(ref _set), trigger, unique) is { } binding)
                {
                    (lines ??= []).Add(binding.Command);
                }
            }
        }

        if (any)
        {
            Bump();
        }

        foreach (string line in lines ?? [])
        {
            try
            {
                _onLine(line);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Error(Category, "A MIDI command could not be queued: " + ex.Message, ex);
            }
        }
    }

    private static string Key(string name, int occurrence) => name.ToUpperInvariant() + "#" + occurrence.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// An open input: its key, where it was listed, its handle, and whether it is the only input of its name (2026-11-14, the review's #1:
    /// refreshed by every <see cref="Sync"/> and read by its callback on the pump thread, so volatile).
    /// </summary>
    private sealed class OpenInput(string key, MidiInputInfo info)
    {
        private volatile bool _unique;

        public string Key { get; } = key;

        public MidiInputInfo Info { get; } = info;

        public IMidiInput Handle { get; set; } = null!;

        public bool Unique
        {
            get => _unique;
            set => _unique = value;
        }
    }

    private sealed class Lease(MidiBoard board) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
            {
                Interlocked.Decrement(ref board._holds);
                board.Schedule();
            }
        }
    }
}
