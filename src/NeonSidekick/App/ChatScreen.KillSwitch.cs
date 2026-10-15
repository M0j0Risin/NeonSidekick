using NeonSidekick.Diagnostics;
using NeonSidekick.EmbeddedLlm;

namespace NeonSidekick.App;

// ── The embedded model's kill switch, Ctrl+Alt+X (2026-10-01) ─────────────

/// <summary>
/// What a kill scope holds (2026-10-14, the Web UI's Stop): <see cref="Work"/> the model's work or a loop's wait (a reply, a compact, a
/// botchat, an advisor, <c>/imagine</c>, <c>/test</c>, <c>/loop</c>), which the Web UI's Stop cancels; <see cref="Edit"/> the user's own
/// editor (<c>/draft</c>, <c>/prompts</c>); <see cref="Load"/> a connect or a model's load, which only Ctrl+C and the kill switch end.
/// </summary>
internal enum KillScopeKind
{
    Work,
    Edit,
    Load,

    /// <summary>Work beside the model's (later on 2026-10-14: a <c>/claude</c> run): the Web UI's Stop ends it, the kill switch leaves it.</summary>
    Aside,
}

internal sealed partial class ChatScreen
{
    /// <summary>The log line when Ctrl+Alt+X finds no embedded model to unload: another server, or none loaded. Pinned.</summary>
    public const string KillSwitchIdleLog = "Ctrl+Alt+X: no embedded model is loaded; nothing to unload.";

    /// <summary>
    /// The work the model is doing now, innermost first (<see cref="KillScope"/>): a reply, a compact, a <c>/test</c> run, a
    /// botchat's held reply, a connect or a wait. Written on the owning task, read on whatever task reads the key.
    /// </summary>
    private KillScope? _killScopes;

    /// <summary>The running <c>/botchat</c>'s ESC ladder: the kill switch ends the chat through it. Null with no chat.</summary>
    private BotEscLadder? _botLadder;

    /// <summary>How long after a first Ctrl+Alt+X the second one unloads the model (the alert shows meanwhile), as <see cref="ExitConfirmWindow"/>.</summary>
    public static readonly TimeSpan KillSwitchConfirmWindow = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The kill switch's arm (later on 2026-10-01, the user's ask: two presses in a row, a safeguard against a stray one): the
    /// UTC tick until which a second Ctrl+Alt+X unloads the model, 0 = none. Written and read on whatever task reads the key.
    /// Other keys do not disarm it, as with the double Ctrl+C: the hook sees its own chord alone.
    /// </summary>
    private long _killArmedUntil;

    /// <summary>
    /// Ctrl+Alt+X (2026-10-01, the user's ask: "immediately unload an embedded model if one is loaded; if using an external LLM
    /// server, the command will simply do nothing"), <see cref="UI.KeySource.KillSwitch"/>'s hook: on whatever task read the
    /// key — the idle line, a pane, the watch under a reply or a spinner — so it never waits for a reply, a load or a pane.
    /// With no embedded model running, loading or serving a botchat's bots it does nothing at all (a Debug line). Otherwise
    /// every embedded server is killed at once (<see cref="LlmSession.KillEmbedded"/>), the work using the model is cancelled
    /// as ESC would (every <see cref="KillScope"/>, and a botchat ended), and the session lets the model go
    /// (<see cref="LlmSession.EmbeddedUnloaded"/>) with <see cref="EmbeddedLlmText.KilledNotice"/> — at once on the idle line,
    /// else when the cancelled work has wound down on its own task (<see cref="KillScope.Dispose"/>), so its client is never
    /// disposed under a request still unwinding. The saved URL stays: <c>/server</c> loads a model again.
    /// Two presses since later that day (the user's ask: a safeguard against a stray one): the first, with a model to unload,
    /// only arms it for <see cref="KillSwitchConfirmWindow"/> and puts <see cref="EmbeddedLlmText.KillArmedHint"/> on the hint
    /// row — as an alert (<see cref="UI.ScreenPane.SetAlertHint"/>), since the standing hint is not drawn under a pane or a
    /// spinner, where the key works too; the second inside the window kills. With nothing to unload neither press arms.
    /// </summary>
    private void KillSwitch()
    {
        if (_session.Embedded is not { } embedded || (embedded.Running is null && embedded.Extras.Count == 0 && !embedded.Starting))
        {
            DiagnosticLog.Debug(AppCategory, KillSwitchIdleLog);
            return;
        }

        long now = _time.GetUtcNow().UtcTicks;
        if (now >= Volatile.Read(ref _killArmedUntil))
        {
            Volatile.Write(ref _killArmedUntil, now + KillSwitchConfirmWindow.Ticks);
            _pane.SetAlertHint(EmbeddedLlmText.KillArmedHint, KillSwitchConfirmWindow);
            DiagnosticLog.Debug(AppCategory, EmbeddedLlmText.KillArmedLog);
            return;
        }

        Volatile.Write(ref _killArmedUntil, 0);
        _pane.SetAlertHint(null, TimeSpan.Zero);

        // The work first, then the servers: a load in progress ends on its token, as Ctrl+C under its spinner ends it, rather
        // than reading the kill as a crash. The scope's notice is read only once its owner has joined the watcher this runs on.
        var scope = Volatile.Read(ref _killScopes);
        CancelScopes(all: true);

        var ids = _session.KillEmbedded();
        if (ids.Count == 0)
        {
            DiagnosticLog.Debug(AppCategory, KillSwitchIdleLog);
            return;
        }

        string notice = EmbeddedLlmText.KilledNotice(ids.Select(id => EmbeddedModelCatalog.Find(id, embedded.Catalog)?.Display ?? id).ToList());
        if (scope is null)
        {
            // Nothing of the model's running: the idle line, a pane over it, or work that is not the model's (a /claude turn).
            RunOrPost(() => FinishKill(notice));
            return;
        }

        scope.Notice = notice;
    }

    /// <summary>The kill's last act, on the screen's task: the session lets the embedded model go, and the notice.</summary>
    private void FinishKill(string notice)
    {
        _session.EmbeddedUnloaded();
        _transcript.Notice(notice);
    }

    /// <summary>
    /// Ends a running botchat and cancels the running work (2026-10-14, out of <see cref="KillSwitch"/> for the Web UI's Stop as well):
    /// every scope under <paramref name="all"/> (the kill switch, which unloads the model), else the <see cref="KillScopeKind.Work"/>
    /// ones alone (the Web UI's Stop: never the user's own editor, never a model's load). True when anything was cancelled.
    /// </summary>
    private bool CancelScopes(bool all)
    {
        bool any = false;
        if (Volatile.Read(ref _botLadder) is { } ladder)
        {
            ladder.End();
            any = true;
        }

        for (var outer = Volatile.Read(ref _killScopes); outer is not null; outer = outer.Outer)
        {
            if (all ? outer.Kind != KillScopeKind.Aside : outer.Kind is KillScopeKind.Work or KillScopeKind.Aside)
            {
                VoiceSession.SafeCancel(outer.Cts);
                any = true;
            }
        }

        return any;
    }

    /// <summary>What runs now, as the Web UI's header says it: model work (Stop shows) and a model's load alone.</summary>
    private (bool Working, bool Loading) ScopesNow()
    {
        bool working = false;
        bool loading = false;
        for (var outer = Volatile.Read(ref _killScopes); outer is not null; outer = outer.Outer)
        {
            working |= outer.Kind is KillScopeKind.Work or KillScopeKind.Aside;
            loading |= outer.Kind == KillScopeKind.Load;
        }

        return (working || Volatile.Read(ref _botLadder) is not null, loading && !working);
    }

    /// <summary>Marks <paramref name="cts"/> as the model's work until the scope is disposed: the kill switch cancels it, and the Web UI's Stop as well when it is <see cref="KillScopeKind.Work"/>.</summary>
    private KillScope BeginKillScope(CancellationTokenSource cts, KillScopeKind kind = KillScopeKind.Work)
    {
        var scope = new KillScope(this, cts, Volatile.Read(ref _killScopes), kind);
        Volatile.Write(ref _killScopes, scope);
        return scope;
    }

    /// <summary>
    /// One piece of the model's work the kill switch cancels (2026-10-01): its token source and the scope it runs inside.
    /// Disposed on the owning task once the work has wound down (the watcher joined): the scope is popped, and a kill that
    /// landed under it is finished there (<see cref="FinishKill"/>).
    /// </summary>
    private sealed class KillScope(ChatScreen screen, CancellationTokenSource cts, KillScope? outer, KillScopeKind kind) : IDisposable
    {
        private string? _notice;

        public CancellationTokenSource Cts { get; } = cts;

        /// <summary>What the scope holds (2026-10-14): whether the Web UI's Stop may cancel it.</summary>
        public KillScopeKind Kind { get; } = kind;

        public KillScope? Outer { get; } = outer;

        /// <summary>The kill's notice, set on the key's task when the kill landed under this scope.</summary>
        public string? Notice
        {
            get => Volatile.Read(ref _notice);
            set => Volatile.Write(ref _notice, value);
        }

        public void Dispose()
        {
            Volatile.Write(ref screen._killScopes, Outer);
            if (Interlocked.Exchange(ref _notice, null) is { } notice)
            {
                screen.FinishKill(notice);
            }
        }
    }
}
