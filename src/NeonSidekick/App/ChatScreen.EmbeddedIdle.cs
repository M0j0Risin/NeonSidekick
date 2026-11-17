using NeonSidekick.EmbeddedLlm;
using NeonSidekick.Settings;
using NeonSidekick.UI;

namespace NeonSidekick.App;

/// <summary>
/// The embedded model's idle unload on the screen (2026-11-16, plan lucid-roaming-lantern #44): the session's timer is armed with the screen's
/// busy check (<see cref="IdleBusy"/>: a Work or Aside scope, a botchat, a load, or an idle hold over a Ralph loop or a code audit's or gap
/// review's passes); its due is drained at the idle line (<see cref="DrainIdleUnload"/>), which unloads and says so; and the next message or
/// model command reloads it first (<see cref="EnsureLlmAsync"/>) under 🦙, Esc cancelling as at any load.
/// </summary>
internal sealed partial class ChatScreen
{
    private int _idleHolds;

    /// <summary>The screen's work between requests, read from the session's timer thread: lock-free reads only.</summary>
    private bool IdleBusy()
    {
        var (working, loading) = ScopesNow();
        return working || loading || Volatile.Read(ref _idleHolds) > 0;
    }

    /// <summary>
    /// Holds the model for a whole run that has gaps with no request in flight (a Ralph loop judging between passes, an audit or a gap review
    /// between its steps); its end starts the countdown from then.
    /// </summary>
    private IdleHold HoldIdle()
    {
        Interlocked.Increment(ref _idleHolds);
        return new IdleHold(this);
    }

    private readonly struct IdleHold(ChatScreen screen) : IDisposable
    {
        public void Dispose()
        {
            if (Interlocked.Decrement(ref screen._idleHolds) == 0)
            {
                screen._session.IdleFromNow();
            }
        }
    }

    /// <summary>
    /// The idle line's drain: an idle due unloads after the session's own checks again and says so (at the PC and on the page); a crash due
    /// marks the model unloaded, so the next message restarts it (the host's error line has already said what happened).
    /// </summary>
    private void DrainIdleUnload()
    {
        switch (_session.TakeUnloadDue())
        {
            case LlmSession.UnloadDue.Idle when _session.UnloadIfIdle() is { } done:
                _transcript.Notice(EmbeddedLlmText.IdleUnloadedNotice(done.Model, done.Idle, done.ParkedBehind, done.Reloads));
                break;

            case LlmSession.UnloadDue.Crashed:
                _session.MarkCrashed();
                break;
        }
    }

    /// <summary>
    /// The reload before a message or a command that needs the model (2026-11-16, #44): with an unloaded model, the connect under 🦙 (Esc
    /// cancels; <c>Queue cancel mode</c> then applies, as at any load); false only when that reload was tried and didn't make it, its notice or
    /// error already said, the unloaded model kept so the next message tries again. True otherwise: reloaded, already loaded, or nothing to
    /// reload (the caller's own check then refuses as it always did).
    /// </summary>
    private async Task<bool> EnsureLlmAsync(CancellationToken cancellationToken)
    {
        // A crash no idle line drained (a loop, a botchat or passes run on): marked here, so the request below restarts it rather than
        // hitting the dead server (2026-11-16, the review's #45).
        if (_session.TakeCrashDue())
        {
            _session.MarkCrashed();
        }

        if (_session.Assistant is not null || _session.IdleUnloaded is not { } kept)
        {
            return true;
        }

        // Nothing to reload any more (the review's #3 and #76): embedded servers turned off, or the model removed since. Forgotten, so the
        // caller's own check refuses as with no model, and a connect never scans its way to some other server.
        if (!EmbeddedOffered || _session.Embedded is not { } embedded || !embedded.State(kept.Model).IsInstalled)
        {
            _session.ForgetIdleUnload();
            return true;
        }

        long epoch = _session.IdleEpoch;
        var effective = AppSettings.Copy(_effective());
        effective.LlmUrl = kept.LlmUrl;
        effective.LlmModel = kept.Model.Id;
        await ConnectUnderLoadAsync(effective, quiet: true, cancellationToken).ConfigureAwait(false);
        if (_session.Assistant is not null)
        {
            return true;
        }

        _session.RestoreIdleUnload(kept, epoch);   // a kill switch pressed during the reload stays a kill (the review's #2)
        return false;
    }

    /// <summary>
    /// The model for a command that needs one (2026-11-16, the review's #11 and #27): the reload first (<see cref="EnsureLlmAsync"/>), then the
    /// refusal with no model at all. False when either said why. <see cref="RunMessageAsync"/> keeps its own pair: its failures put the line back.
    /// </summary>
    private async Task<bool> ModelOrSayAsync(CancellationToken cancellationToken)
    {
        if (!await EnsureLlmAsync(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        if (_session.Assistant is null)
        {
            _transcript.Error(NoAssistantError);
            return false;
        }

        return true;
    }

    /// <summary>
    /// A message that never reached the model goes back where it came from (2026-11-16, out of <see cref="RunMessageAsync"/>'s withdrawn arms, so
    /// a cancelled reload shares them): a Ralph pass or an audit's or gap review's pass puts nothing back and marks the turn cancelled; a page's
    /// line goes back to its draft with its chips; anything else to the input line, ahead of what was typed since.
    /// </summary>
    private void PutBackWithdrawn(string text, bool passMessage)
    {
        if (InRalphPass || passMessage)
        {
            _lastTurnCancelled = true;
            return;
        }

        if (_sentFromWeb)
        {
            WithdrawWebLine(_sentDraft ?? text, _sentWebAttachments);
            return;
        }

        _restoreDraft = _sentDraft ?? PasteBlocks.WithoutSavedPaths(text);
        _input.Chat.Load(_restoreDraft, prependToCurrent: true);
    }
}
