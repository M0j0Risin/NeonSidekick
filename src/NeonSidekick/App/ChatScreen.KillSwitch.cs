using NeonSidekick.Diagnostics;
using NeonSidekick.EmbeddedLlm;

namespace NeonSidekick.App;

// ── The embedded model's kill switch, Ctrl+Alt+X (2026-10-01) ─────────────

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
        Volatile.Read(ref _botLadder)?.End();
        var scope = Volatile.Read(ref _killScopes);
        for (var outer = scope; outer is not null; outer = outer.Outer)
        {
            VoiceSession.SafeCancel(outer.Cts);
        }

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

    /// <summary>Marks <paramref name="cts"/> as the model's work until the scope is disposed: the kill switch cancels it.</summary>
    private KillScope BeginKillScope(CancellationTokenSource cts)
    {
        var scope = new KillScope(this, cts, Volatile.Read(ref _killScopes));
        Volatile.Write(ref _killScopes, scope);
        return scope;
    }

    /// <summary>
    /// One piece of the model's work the kill switch cancels (2026-10-01): its token source and the scope it runs inside.
    /// Disposed on the owning task once the work has wound down (the watcher joined): the scope is popped, and a kill that
    /// landed under it is finished there (<see cref="FinishKill"/>).
    /// </summary>
    private sealed class KillScope(ChatScreen screen, CancellationTokenSource cts, KillScope? outer) : IDisposable
    {
        private string? _notice;

        public CancellationTokenSource Cts { get; } = cts;

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
