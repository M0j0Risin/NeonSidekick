using NeonSidekick.Diagnostics;
using NeonSidekick.EmbeddedLlm;
using NeonSidekick.Llm;
using NeonSidekick.Settings;

namespace NeonSidekick.App;

/// <summary>
/// The embedded model's idle unload (2026-11-16, plan lucid-roaming-lantern #44, the user's ask). The main client, and each link to the main
/// host's own server, count their requests on the connection's one <see cref="UseCount"/>; when the last ends (or a load ends, or a hold
/// ends) a <see cref="TimeProvider"/> timer is armed for <c>Embedded idle unload (min)</c>. Its fire, with nothing in flight, no load, no
/// reflection or title and the screen not busy, sets a due flag and wakes the screen (<see cref="UseIdleUnload"/>'s <c>due</c>), whose
/// drain asks <see cref="UnloadIfIdle"/>: the checks again, the setting and the idle time, then the stop. Never a stop from the timer's
/// thread: <c>Stop</c> waits behind a load. A busy fire re-arms the full minutes from then. A model parked behind another server
/// (<c>LLM one local model</c> off) is unloaded by the same rule, only stopped; extra servers stay. The main server's own crash
/// (<see cref="IEmbeddedLlm.MainExited"/>) sets the flag's other value through the same hand-over, so the next message restarts it.
/// Headless never calls <see cref="UseIdleUnload"/>.
/// </summary>
internal sealed partial class LlmSession
{
    /// <summary>
    /// The model a main embedded connection had when it was unloaded or crashed, and the URL it connected to, for the reload (2026-11-16, #44;
    /// the URL alone since the review's #83, not a whole settings copy).
    /// </summary>
    public sealed record IdleUnload(string LlmUrl, EmbeddedModel Model);

    /// <summary>
    /// What an idle unload stopped, for the screen's line: the model, the minutes, the server it was parked behind (null for the main one, or a
    /// parked one with nothing in front of it), and whether it is kept for the next message's reload (the review's #59).
    /// </summary>
    public sealed record IdleUnloadDone(EmbeddedModel Model, TimeSpan Idle, string? ParkedBehind, bool Reloads);

    /// <summary>The due flag's values: none, an idle unload, a crash.</summary>
    public enum UnloadDue
    {
        None,
        Idle,
        Crashed,
    }

    private readonly Lock _idleLock = new();
    private UseCount _use = new();
    private ITimer? _idleTimer;
    private Func<TimeSpan>? _idleMinutes;
    private Func<bool>? _idleBusy;
    private Action? _idleDue;
    private DateTimeOffset _idleSince;
    private int _unloadDue;
    private IdleUnload? _idleUnloaded;
    private long _idleEpoch;

    /// <summary>
    /// Bumped by <see cref="ForgetIdleUnload"/> alone (2026-11-16, the review's #2): a reload reads it before its connect, and
    /// <see cref="RestoreIdleUnload"/> puts the model back only when no forget came between (a kill switch pressed during the reload stays a
    /// kill). A connect's own clearing doesn't bump it, so a failed reload still keeps the model for the next try.
    /// </summary>
    public long IdleEpoch => Interlocked.Read(ref _idleEpoch);

    /// <summary>The model unloaded by an idle unload or a crash, until a connect (any) or the kill switch; null otherwise. Read from any thread.</summary>
    public IdleUnload? IdleUnloaded => Volatile.Read(ref _idleUnloaded);

    /// <summary>The model the bars and the page show: the endpoint's, else the unloaded one's (each read once, so a read mid-unload gives one or the other).</summary>
    public string? ShownModelId => Endpoint?.ModelId ?? IdleUnloaded?.Model.Id;

    /// <summary>The base URL shown beside <see cref="ShownModelId"/>: the endpoint's, else the embedded sentinel while unloaded.</summary>
    public Uri? ShownBaseUrl => Endpoint?.BaseUrl ?? (IdleUnloaded is null ? null : EmbeddedEndpoint.BaseUrl);

    /// <summary>
    /// Whether the model shown is an unloaded one (no client, an <see cref="IdleUnloaded"/>): the bars' 💤. Not the endpoint: a failed reload
    /// leaves <see cref="Endpoint"/> on the embedded sentinel, as any connect that didn't answer.
    /// </summary>
    public bool ShownUnloaded => Assistant is null && IdleUnloaded is not null;

    /// <summary>Whether a due is waiting for the screen's drain: the idle read checks it right after arming its signal.</summary>
    public bool UnloadPending => Volatile.Read(ref _unloadDue) != (int)UnloadDue.None;

    /// <summary>
    /// The screen turns the idle unload on (once, at its start): <paramref name="minutes"/> read at every arming, <paramref name="busy"/> the
    /// screen's own work between requests, <paramref name="due"/> its wake-up (safe from any thread). The crash of the main server is marked
    /// from then on, and its error line promises the restart.
    /// </summary>
    public void UseIdleUnload(Func<TimeSpan> minutes, Func<bool> busy, Action due)
    {
        ArgumentNullException.ThrowIfNull(minutes);
        ArgumentNullException.ThrowIfNull(busy);
        ArgumentNullException.ThrowIfNull(due);
        lock (_idleLock)
        {
            _idleMinutes = minutes;
            _idleBusy = busy;
            _idleDue = due;
        }

        if (_embedded is { } embedded)
        {
            embedded.RestartsAfterCrash = true;
            embedded.MainExited -= OnMainExited;
            embedded.MainExited += OnMainExited;
        }

        IdleFromNow();
    }

    /// <summary>
    /// A countdown from now (a use's end, a hold's end, a reflection's end, a load's end): with something to unload and the setting on, the
    /// timer armed for the full minutes. Off, or nothing to unload, disposes any timer. Safe from any thread.
    /// </summary>
    public void IdleFromNow()
    {
        if (_idleMinutes is not { } minutes)
        {
            return;
        }

        var span = minutes();
        lock (_idleLock)
        {
            if (!HasIdleTarget)
            {
                DisposeIdleTimerLocked();
                return;
            }

            _idleSince = _time.GetUtcNow();
            if (span <= TimeSpan.Zero)
            {
                DisposeIdleTimerLocked();
                return;
            }

            ArmLocked(span);
        }
    }

    /// <summary>
    /// The setting changed: the countdown re-armed from the last use, never from now (2026-11-16, #44). Off disposes the timer and drops an
    /// idle due (a crash's stays). A request in flight arms at its end instead. Safe from any thread; prints nothing.
    /// </summary>
    public void RearmIdle()
    {
        if (_idleMinutes is not { } minutes)
        {
            return;
        }

        var span = minutes();
        lock (_idleLock)
        {
            if (span <= TimeSpan.Zero || !HasIdleTarget)
            {
                DisposeIdleTimerLocked();
                Interlocked.CompareExchange(ref _unloadDue, (int)UnloadDue.None, (int)UnloadDue.Idle);
                return;
            }

            if (_use.InFlight > 0)
            {
                return;
            }

            if (_idleSince == default)
            {
                _idleSince = _time.GetUtcNow();
            }

            var left = span - (_time.GetUtcNow() - _idleSince);
            ArmLocked(left > TimeSpan.Zero ? left : TimeSpan.FromTicks(1));
        }
    }

    /// <summary>Takes the due flag, leaving none: the screen's drain.</summary>
    public UnloadDue TakeUnloadDue() => (UnloadDue)Interlocked.Exchange(ref _unloadDue, (int)UnloadDue.None);

    /// <summary>
    /// Takes a crash's due alone, leaving an idle one (2026-11-16, the review's #45): the reload before a request finds a crashed server even
    /// when the idle line never drained it (a loop, a botchat, Ralph or an audit's passes run on without one).
    /// </summary>
    public bool TakeCrashDue() =>
        Interlocked.CompareExchange(ref _unloadDue, (int)UnloadDue.None, (int)UnloadDue.Crashed) == (int)UnloadDue.Crashed;

    /// <summary>
    /// The screen's drain after an idle due, on its task: the checks again (a message may have started since), the setting (it may be off
    /// now) and the idle time (a pane held the drain while another model loaded); a failed check re-arms from the last use. Then the
    /// unload: a parked model only stopped; the main one stopped and let go, kept as <see cref="IdleUnloaded"/> for the reload. Null when
    /// nothing was unloaded.
    /// </summary>
    public IdleUnloadDone? UnloadIfIdle()
    {
        if (_idleMinutes is not { } minutes || _embedded is not { } embedded)
        {
            return null;
        }

        var span = minutes();
        if (span <= TimeSpan.Zero || !HasIdleTarget)
        {
            return null;
        }

        DateTimeOffset since;
        lock (_idleLock)
        {
            since = _idleSince;   // a DateTimeOffset is two words: read under the lock that writes it (the review's #7)
        }

        if (Busy() || _time.GetUtcNow() - since < span)
        {
            RearmIdle();
            return null;
        }

        if (EmbeddedParked)
        {
            if (embedded.Running is not { } parked || EmbeddedModelCatalog.Find(parked.ModelId, embedded.Catalog) is not { } parkedModel)
            {
                return null;
            }

            embedded.Stop();
            lock (_idleLock)
            {
                DisposeIdleTimerLocked();
            }

            var front = Endpoint;
            DiagnosticLog.Info(Category, EmbeddedLlmText.IdleUnloadedParkedLogLine(parked.ModelId, front?.Source, span));
            return new IdleUnloadDone(parkedModel, span, front is { } behind ? ServerNameOf(behind) : null, Reloads: false);
        }

        if (Endpoint is not { } endpoint || !EmbeddedEndpoint.IsEmbedded(endpoint.BaseUrl)
            || EmbeddedModelCatalog.Find(endpoint.ModelId, embedded.Catalog) is not { } model)
        {
            return null;
        }

        embedded.Stop();
        LetGoMain(model);
        DiagnosticLog.Info(Category, EmbeddedLlmText.IdleUnloadedLogLine(model.Id, span));
        return new IdleUnloadDone(model, span, null, Reloads: true);
    }

    /// <summary>
    /// The screen's drain after a crash, on its task: the main embedded endpoint's server gone, the session lets it go and keeps the model as
    /// <see cref="IdleUnloaded"/>, so the next message restarts it. Taken whatever the setting says. False when nothing was marked (a connect
    /// since, or the server is running again).
    /// </summary>
    public bool MarkCrashed()
    {
        if (_embedded is not { } embedded || embedded.Running is not null || Endpoint is not { } endpoint || !EmbeddedEndpoint.IsEmbedded(endpoint.BaseUrl)
            || EmbeddedModelCatalog.Find(endpoint.ModelId, embedded.Catalog) is not { } model)
        {
            return false;
        }

        LetGoMain(model);
        return true;
    }

    /// <summary>
    /// A reload that was cancelled or failed puts back what it held, so the next message tries again (<c>EnsureLlmAsync</c>); not when a
    /// forget came since <paramref name="epoch"/> was read (<see cref="IdleEpoch"/>) or a model is connected.
    /// </summary>
    public void RestoreIdleUnload(IdleUnload? kept, long epoch)
    {
        if (kept is not null && Assistant is null && IdleEpoch == epoch)
        {
            Volatile.Write(ref _idleUnloaded, kept);
        }
    }

    /// <summary>
    /// The unloaded model forgotten (the kill switch, the model removed, embedded servers turned off): the next message asks for a server; a
    /// parked model's timer and any due dropped, and the epoch bumped so a reload under way doesn't put it back.
    /// </summary>
    public void ForgetIdleUnload()
    {
        Interlocked.Increment(ref _idleEpoch);
        Volatile.Write(ref _idleUnloaded, null);
        Interlocked.Exchange(ref _unloadDue, (int)UnloadDue.None);
        lock (_idleLock)
        {
            DisposeIdleTimerLocked();
        }
    }

    /// <summary>The model is the main embedded one or one parked behind another server: something to unload.</summary>
    private bool HasIdleTarget => _embedded?.Running is not null;

    private bool Busy() => _use.InFlight > 0 || (_embedded?.Starting ?? false) || IsLearning || IsTitling || (_idleBusy?.Invoke() ?? false);

    /// <summary>A fresh count for a new connection; a request's start drops an idle due (the turn is use), a crash's stays.</summary>
    private UseCount NewUseCount() => new(
        began: () => Interlocked.CompareExchange(ref _unloadDue, (int)UnloadDue.None, (int)UnloadDue.Idle),
        ended: IdleFromNow);

    private void OnIdleTimer(object? state)
    {
        if (_idleMinutes is not { } minutes || !ReferenceEquals(state, _use))
        {
            return;
        }

        var span = minutes();
        if (span <= TimeSpan.Zero || !HasIdleTarget)
        {
            lock (_idleLock)
            {
                if (ReferenceEquals(state, _use))
                {
                    DisposeIdleTimerLocked();   // a stale fire never disposes the new connection's timer (the review's #130)
                }
            }

            return;
        }

        if (Busy())
        {
            lock (_idleLock)
            {
                if (ReferenceEquals(state, _use))
                {
                    ArmLocked(span);   // the full minutes again from now: nothing else arms when a scope ends
                }
            }

            return;
        }

        if (Interlocked.CompareExchange(ref _unloadDue, (int)UnloadDue.Idle, (int)UnloadDue.None) == (int)UnloadDue.None)
        {
            _idleDue?.Invoke();
        }
    }

    private void OnMainExited(EmbeddedServerInfo lost)
    {
        // On the process's exit thread: only the mark and the wake-up; the screen's drain acts. A parked server's crash marks nothing.
        if (Endpoint is { } endpoint && EmbeddedEndpoint.IsEmbedded(endpoint.BaseUrl) && string.Equals(endpoint.ModelId, lost.ModelId, StringComparison.OrdinalIgnoreCase))
        {
            Interlocked.Exchange(ref _unloadDue, (int)UnloadDue.Crashed);
            _idleDue?.Invoke();
        }
    }

    private void ArmLocked(TimeSpan due)
    {
        _idleTimer?.Dispose();
        _idleTimer = _time.CreateTimer(OnIdleTimer, _use, due, Timeout.InfiniteTimeSpan);
    }

    private void DisposeIdleTimerLocked()
    {
        _idleTimer?.Dispose();
        _idleTimer = null;
    }

    /// <summary>At every disconnect: the count retired (a request it cancelled arms nothing when it ends), the timer and any due gone.</summary>
    private void RetireIdle()
    {
        lock (_idleLock)
        {
            _use.Retire();
            _use = NewUseCount();
            DisposeIdleTimerLocked();
        }

        Interlocked.Exchange(ref _unloadDue, (int)UnloadDue.None);
    }

    /// <summary>
    /// The main embedded model let go and kept for the reload (an idle unload after its stop, a crash): the client dropped, the endpoint
    /// cleared, the configured URL and the model remembered (2026-11-16, the review's #30: one block for both).
    /// </summary>
    private void LetGoMain(EmbeddedModel model)
    {
        string url = _configuredUrl;
        Disconnect();
        EmbeddedServer = null;
        Endpoint = null;
        Volatile.Write(ref _idleUnloaded, new IdleUnload(url, model));
    }

    private static string ServerNameOf(LlmEndpoint endpoint) => LlmServer.NameFor(endpoint.BaseUrl, ownedBy: null);
}
