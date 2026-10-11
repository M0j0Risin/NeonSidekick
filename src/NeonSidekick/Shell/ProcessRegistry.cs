using System.Collections.Concurrent;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.Shell;

/// <summary>
/// One exit to tell the user about: printed as an alert line, and carried to the model as a seeded <c>process poll</c> pair.
/// <paramref name="ByUser"/> (2026-10-05): the user stopped it from <c>/process</c>'s window (<see cref="ProcessRegistry.StopByUser"/>).
/// <paramref name="Mine"/> (2026-10-11): the user's own <c>/cmd --bg</c> run (<see cref="ProcessSession.Mine"/>), reported by the screen in full, never polled for the model.
/// </summary>
public readonly record struct ProcessAlert(string Id, string Label, string Kind, int ExitCode, TimeSpan Elapsed, bool Killed, bool ByUser = false, bool Mine = false);

/// <summary>How <see cref="ProcessRegistry.Find"/> read a session id.</summary>
public enum FindOutcome
{
    Found,
    None,
    Ambiguous,
}

/// <summary>
/// The background processes <c>run_command</c> started (2026-09-21): one board per screen, alive
/// for the process like the <see cref="Timers.TimerBoard"/> — nothing is persisted, and a child
/// still running when the app closes is killed by <see cref="Dispose"/> (a crash leaves it to
/// Windows). Ids are <c>proc_</c> and six hex digits; any unique prefix finds one. At most
/// <see cref="MaxRunning"/> of the model's and as many of the user's own run at once and the newest <see cref="MaxKept"/> finished ones stay for
/// <c>poll</c> / <c>log</c> until <c>close</c> or eviction. A session started with <c>notify</c>
/// queues a <see cref="ProcessAlert"/> when it exits and calls <c>signal</c> (the screen's, which
/// ends an idle read; never writes) — the shape of the timer board — and its id waits in
/// <see cref="TakeNotes"/> for the next turn's seeded poll. Thread-safe: exits land from the pool.
/// </summary>
public sealed class ProcessRegistry : IDisposable
{
    public const int MaxRunning = 16;
    public const int MaxKept = 64;
    public const string IdPrefix = "proc_";

    /// <summary>The most exits one turn is told about through seeded polls; the rest stay listable.</summary>
    public const int MaxNotesPerTurn = 5;

    private readonly ShellRunner _runner;
    private readonly Random _random;
    private readonly Action _signal;
    private readonly object _lock = new();
    private readonly List<ProcessSession> _sessions = new();
    private readonly ConcurrentQueue<ProcessAlert> _alerts = new();
    private readonly List<string> _notes = new();
    private bool _disposed;

    /// <param name="signal">Runs after an alert was queued, on the pool. Must not block or write.</param>
    public ProcessRegistry(ShellRunner runner, Random random, Action signal)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _random = random ?? throw new ArgumentNullException(nameof(random));
        _signal = signal ?? throw new ArgumentNullException(nameof(signal));
    }

    public bool HasAlerts => !_alerts.IsEmpty;

    /// <summary>How many are still running; the performance bar's PROC reads it at the pane's tick (2026-10-05).</summary>
    public int Running
    {
        get
        {
            lock (_lock)
            {
                return _sessions.Count(s => !s.HasExited);
            }
        }
    }

    /// <summary>
    /// Starts <paramref name="launch"/> as a background session; stdin stays open for <c>write</c>. <paramref name="mine"/> (2026-10-11):
    /// the user's own <c>/cmd --bg</c> run, hidden from the model and always alerted (<see cref="ProcessSession.Mine"/>).
    /// </summary>
    /// <exception cref="ShellStartException">Too many are running, or the program could not start.</exception>
    public ProcessSession Start(ProcessLaunch launch, bool notify, bool mine = false)
    {
        ArgumentNullException.ThrowIfNull(launch);
        string id;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // The cap is each owner's (2026-10-11, the review): the user's own runs, which the model's process tool never lists, counted
            // against the model's background run_command, refused with a count its process list could not explain.
            if (_sessions.Count(s => !s.HasExited && s.Mine == mine) >= MaxRunning)
            {
                throw new ShellStartException(ShellText.TooManyProcesses(MaxRunning));
            }

            do
            {
                id = ShellRunner.NewId(_random, IdPrefix);
            }
            while (_sessions.Any(s => string.Equals(s.Id, id, StringComparison.Ordinal)));
        }

        var session = _runner.Start(launch, id);
        session.Notify = notify;
        session.Mine = mine;
        lock (_lock)
        {
            _sessions.Add(session);
            Evict();
        }

        _ = WatchAsync(session);
        return session;
    }

    private async Task WatchAsync(ProcessSession session)
    {
        int code = await session.Exited.ConfigureAwait(false);
        bool tell;
        lock (_lock)
        {
            // Closed (or disposed) before it exited: nothing to tell. The user's own run is always told, to the user alone (no seeded poll).
            tell = (session.Notify || session.Mine) && !_disposed && _sessions.Contains(session);
            if (tell && !session.Mine)
            {
                _notes.Add(session.Id);
            }

            Evict();
        }

        if (tell)
        {
            _alerts.Enqueue(new ProcessAlert(session.Id, session.Label, session.Kind, code, session.Elapsed, session.Killed, session.StoppedByUser, session.Mine));
            _signal();
        }
    }

    /// <summary>The oldest finished sessions beyond <see cref="MaxKept"/> go, their pipes with them. Under the lock.</summary>
    private void Evict()
    {
        var finished = _sessions.Where(s => s.HasExited).ToList();
        for (int i = 0; finished.Count - i > MaxKept; i++)
        {
            _sessions.Remove(finished[i]);
            _notes.Remove(finished[i].Id);
            finished[i].Dispose();
            DiagnosticLog.Debug(ShellKinds.Category, $"{finished[i].Id}: evicted (the registry keeps the newest {MaxKept.ToString(System.Globalization.CultureInfo.InvariantCulture)} finished)");
        }
    }

    /// <summary>The session whose id starts with <paramref name="prefix"/> (trimmed, case-insensitive), unique; <paramref name="matches"/> names the candidates when several do.</summary>
    public FindOutcome Find(string prefix, out ProcessSession? session, out IReadOnlyList<string> matches) => Find(prefix, includeMine: true, out session, out matches);

    /// <summary><see cref="Find"/> for the model's <c>process</c> tool (2026-10-11): the user's own runs (<see cref="ProcessSession.Mine"/>) are not there.</summary>
    public FindOutcome FindForModel(string prefix, out ProcessSession? session, out IReadOnlyList<string> matches) => Find(prefix, includeMine: false, out session, out matches);

    private FindOutcome Find(string prefix, bool includeMine, out ProcessSession? session, out IReadOnlyList<string> matches)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        string wanted = prefix.Trim();
        lock (_lock)
        {
            var found = wanted.Length == 0 ? [] : _sessions.Where(s => (includeMine || !s.Mine) && s.Id.StartsWith(wanted, StringComparison.OrdinalIgnoreCase)).ToList();
            matches = found.Select(s => s.Id).ToList();
            session = found.Count == 1 ? found[0] : null;
            return found.Count switch { 0 => FindOutcome.None, 1 => FindOutcome.Found, _ => FindOutcome.Ambiguous };
        }
    }

    /// <summary>
    /// The user's own stop (2026-10-05, Ctrl+K twice in <c>/process</c>'s window): the session marked <see cref="ProcessSession.StoppedByUser"/>
    /// and notified whatever the model asked, then killed, so its exit goes the way of every notified one — an alert line in the
    /// chat and a seeded poll on the next turn that tells the model. Called on the window's thread: it never writes, the alert's
    /// <c>signal</c> wakes the screen. False when it is not this registry's, or has exited already — the child gone on its own while
    /// its pumps still drain too (2026-10-05, the code review: the kill was a no-op there, yet the session was notified, so the model
    /// got an alert it never asked for whose poll said "exited N" under the menu's "stopping"). The kill is sent under the lock and the
    /// session marked only when it landed: <see cref="WatchAsync"/> reads <see cref="ProcessSession.Notify"/> under the same lock, so
    /// an exit racing the stop sees the flags either before or after, never half set.
    /// </summary>
    public bool StopByUser(ProcessSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_lock)
        {
            if (_disposed || session.HasExited || !_sessions.Contains(session) || !session.Kill())
            {
                return false;
            }

            session.MarkUserStop();
            session.Notify = true;
        }

        DiagnosticLog.Info(ShellKinds.Category, $"{session.Id}: stopped by the user ({session.Kind}) {session.Label}");
        return true;
    }

    /// <summary>Every session, oldest first.</summary>
    public IReadOnlyList<ProcessSession> List()
    {
        lock (_lock)
        {
            return _sessions.ToList();
        }
    }

    /// <summary><see cref="List"/> for the model's <c>process</c> tool (2026-10-11): without the user's own runs (<see cref="ProcessSession.Mine"/>).</summary>
    public IReadOnlyList<ProcessSession> ListForModel()
    {
        lock (_lock)
        {
            return _sessions.Where(s => !s.Mine).ToList();
        }
    }

    /// <summary>Forgets a finished session (its output with it); false while it runs.</summary>
    public bool Close(ProcessSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_lock)
        {
            if (!session.HasExited || !_sessions.Remove(session))
            {
                return false;
            }

            _notes.Remove(session.Id);
        }

        session.Dispose();
        return true;
    }

    public bool TryTakeAlert(out ProcessAlert alert) => _alerts.TryDequeue(out alert);

    /// <summary>The ids of notified exits not yet carried to the model, oldest first, at most <see cref="MaxNotesPerTurn"/>; taken once.</summary>
    public IReadOnlyList<string> TakeNotes()
    {
        lock (_lock)
        {
            int count = Math.Min(MaxNotesPerTurn, _notes.Count);
            var taken = _notes.Take(count).ToList();
            _notes.RemoveRange(0, count);
            return taken;
        }
    }

    /// <summary>Kills what still runs and forgets everything: the app's exit.</summary>
    public void Dispose()
    {
        List<ProcessSession> all;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            all = _sessions.ToList();
            _sessions.Clear();
            _notes.Clear();
        }

        foreach (var session in all)
        {
            if (!session.HasExited)
            {
                DiagnosticLog.Info(ShellKinds.Category, $"{session.Id}: killed at exit ({session.Kind}) {session.Label}");
            }

            session.Dispose();
        }
    }
}
