using NeonSidekick.Audits;
using NeonSidekick.Camera;
using NeonSidekick.UI;
using NeonSidekick.Viewer;

namespace NeonSidekick.App;

/// <summary>
/// The lower rule's jobs group (2026-11-23, the user's ask, #17 and #18): left to right the update's marker, the loop under way (🔁 a Ralph
/// loop, 🔎 a code audit, ⟳ a <c>/loop</c>), an embedded model's download, the MCP, voice and speech setups, the ComfyUI pictures still
/// rendering, the reflection, the botchat's idle extra servers, the camera held open, and the running processes' count — each drawn whole,
/// each with its own double-click (<see cref="LowerRuleClick"/>). The loop, the download and the pictures were the status bar's until then;
/// the setups, the reflection, the extras and the camera until 2026-11-25 (the user's ask: unchecking Status hid them, the camera too).
/// </summary>
internal sealed partial class ChatScreen
{
    /// <summary>
    /// A loop's "end after this pass" (2026-11-23, #17/#18): asked by a double-click on its item on the lower rule, read after each pass; its
    /// token ends a wait between passes at once (<see cref="DelayOrEndedAsync"/>). One per run of the loop, let go at its end.
    /// </summary>
    private sealed class PassEnd : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();

        /// <summary>Whether the double-click came.</summary>
        public bool Asked => _cts.IsCancellationRequested;

        /// <summary>Cancelled by the double-click.</summary>
        public CancellationToken Token => _cts.Token;

        /// <summary>The double-click: any thread, harmless twice or after the loop let it go.</summary>
        public void Ask()
        {
            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void Dispose() => _cts.Dispose();
    }

    // A /loop's item (⟳ 3/10, LoopText.RuleTitle) while it runs, and its end after this pass; set by LoopPassesAsync and RunLoopedCommandAsync.
    private volatile string? _loopRule;
    private volatile PassEnd? _loopEnd;

    // A Ralph loop's end after this pass (RunRalphAsync) and a code audit's (MarkAuditBaseline).
    private volatile PassEnd? _ralphEnd;
    private volatile PassEnd? _auditEnd;

    /// <summary>
    /// The loop's item: the Ralph loop's place, else the audit's pass, else the <c>/loop</c>'s — one runs at a time, each being a turn — with
    /// <see cref="LoopText.EndingTail"/> once a double-click asked it to end; null with none.
    /// </summary>
    private string? LoopItemText()
    {
        if (_ralphStrip is { } ralph)
        {
            return ralph + (_ralphEnd is { Asked: true } ? LoopText.EndingTail : "");
        }

        if (_audit.StripPass is { } pass)
        {
            return AuditText.Glyph + " " + pass + (_auditEnd is { Asked: true } ? LoopText.EndingTail : "");
        }

        return _loopRule is { } loop ? loop + (_loopEnd is { Asked: true } ? LoopText.EndingTail : "") : null;
    }

    /// <summary>
    /// The lower rule's items now, left to right, each only while it has something to say. Any thread (the pane's draw and tick). The queued
    /// count first (2026-11-26, the user's ask: off the status bar, pinned at the rule's left): the queue, the Web UI's waiting lines and a PC line
    /// sent now that waits, as the status bar's count read them.
    /// </summary>
    private IReadOnlyList<LowerRuleItem> LowerRuleItemsNow()
    {
        var items = new List<LowerRuleItem>(12);
        if (_queue.Count + _webUi.WaitingCount + PendedPcLines().Count is > 0 and var queued)
        {
            items.Add(new(LowerRuleKind.Queued, QueuedRulePart(queued)));
        }

        if (UpdateMarkerNow() is { Length: > 0 } update)
        {
            items.Add(new(LowerRuleKind.Update, update));
        }

        if (LoopItemText() is { Length: > 0 } loop)
        {
            items.Add(new(LowerRuleKind.Loop, loop));
        }

        if (_jobs.Strip(static kind => kind == BackgroundJobKind.EmbeddedDownload) is { Length: > 0 } download)
        {
            items.Add(new(LowerRuleKind.Download, download));
        }

        foreach (var (job, kind) in SetupItems)
        {
            if (_jobs.Strip(k => k == job) is { Length: > 0 } setup)
            {
                items.Add(new(kind, setup));
            }
        }

        if (_pendingPictures.Glyph is { Length: > 0 } pictures)
        {
            items.Add(new(LowerRuleKind.Pictures, pictures));
        }

        if (_session.IsLearning)
        {
            items.Add(new(LowerRuleKind.Learning, LearnStripGlyph));
        }

        if (BotExtrasIdle)
        {
            items.Add(new(LowerRuleKind.Extras, BotChat.ExtrasGlyph));
        }

        if (_camera.Live)
        {
            items.Add(new(LowerRuleKind.Camera, CameraText.Glyph));
        }

        if (ProcessWindowText.RuleTitle(_processes.Running) is { Length: > 0 } processes)
        {
            items.Add(new(LowerRuleKind.Processes, processes));
        }

        return items;
    }

    /// <summary>The setups behind the line, in the rule's order (2026-11-25), each its own item so a double-click cancels that one.</summary>
    private static readonly (BackgroundJobKind Job, LowerRuleKind Kind)[] SetupItems =
    [
        (BackgroundJobKind.Mcp, LowerRuleKind.Mcp),
        (BackgroundJobKind.Voice, LowerRuleKind.Voice),
        (BackgroundJobKind.Speech, LowerRuleKind.Speech),
    ];

    /// <summary>
    /// A double-click on the lower rule's <paramref name="kind"/>, under a reply or at the idle line: the word to run for the update's marker
    /// (<c>/update</c>), the processes' count (<c>/process</c>) and the queued count (<c>/queue</c>, 2026-11-26); otherwise it acts here and answers null — the loop ends after the pass in
    /// progress, the download, a setup or the reflection is canceled (their ends said by the drains), the pictures are canceled while the
    /// reply goes on, the camera is let go at once (2026-11-25), its notice queued as the reflection's is so it waits for a running reply.
    /// The extras' 🧟 is <c>/botchat --kill</c>: the word at idle; on the watcher (<paramref name="watched"/>) the line held for the idle line
    /// here, as Ctrl+Alt+K's is, since a clicked word has no row behind it and a deferred one would be declined.
    /// </summary>
    private string? LowerRuleClick(LowerRuleKind kind, bool watched = false)
    {
        switch (kind)
        {
            case LowerRuleKind.Update:
                return Updates.UpdateText.Word;
            case LowerRuleKind.Processes:
                return ProcessWindowText.Word;
            case LowerRuleKind.Queued:
                return SlashCommands.QueueWord;
            case LowerRuleKind.Loop:
                AskLoopEnd();
                return null;
            case LowerRuleKind.Download:
                _jobs.Cancel(BackgroundJobKind.EmbeddedDownload);
                return null;
            case LowerRuleKind.Pictures:
                DrainPictures();
                return null;
            case LowerRuleKind.Mcp:
                _jobs.Cancel(BackgroundJobKind.Mcp);
                return null;
            case LowerRuleKind.Voice:
                _jobs.Cancel(BackgroundJobKind.Voice);
                return null;
            case LowerRuleKind.Speech:
                _jobs.Cancel(BackgroundJobKind.Speech);
                return null;
            case LowerRuleKind.Learning:
                CancelLearnNow();
                return null;
            case LowerRuleKind.Camera:
                // Never RunOrPost: clicks are live on the watcher in waits that are no turn (/claude, a compaction, a listen), where it would
                // write the transcript beside its one writer, and a posted act would wait for the next message's turn.
                _learnNotices.Enqueue(CameraText.Off(CameraOff()));
                SignalAlert();
                return null;
            case LowerRuleKind.Extras when watched:
                HoldExtrasKill();
                return null;
            case LowerRuleKind.Extras:
                return BotChat.KillLine;
            default:
                return null;
        }
    }

    /// <summary>
    /// The 🧟's <c>/botchat --kill</c> double-clicked on the watcher (2026-11-25): held as a typed line would be, with the deferred notice while a
    /// turn runs (the queue, or the pending lines under <c>Queue mode</c> <c>disabled</c>), else pending for the idle line.
    /// </summary>
    private void HoldExtrasKill()
    {
        string line = BotChat.KillLine;
        var submitted = new SubmittedLine(line, line, [], line);
        if (_turnRunning)
        {
            Post(() => _transcript.Notice(MidTurnDeferredNotice(CommandWord(line))));
            if (_botChatRunning || !QueueLine(submitted))
            {
                Pend(submitted);
            }
        }
        else
        {
            Pend(submitted);
        }
    }

    /// <summary>The loop under way asked to end after its pass: the Ralph loop's, else the audit's, else the <c>/loop</c>'s (<see cref="LoopItemText"/>'s order).</summary>
    private void AskLoopEnd()
    {
        if (_ralphStrip is not null && _ralphEnd is { } ralph)
        {
            ralph.Ask();
        }
        else if (_audit.StripPass is not null && _auditEnd is { } audit)
        {
            audit.Ask();
        }
        else if (_loopRule is not null && _loopEnd is { } loop)
        {
            loop.Ask();
        }

        _webUi.StatusNudge();
    }

    /// <summary>
    /// A wait between passes that the loop's double-click ends at once (2026-11-23, #17/#18): <paramref name="ended"/> is linked into the delay
    /// alone and its cancel swallowed, so the watch around it (<see cref="WaitUnderWatchAsync"/>, <see cref="LoopWaitAsync"/>) never reads it as
    /// Esc; the loop then sees its <see cref="PassEnd.Asked"/> and says the ended notice.
    /// </summary>
    private async Task DelayOrEndedAsync(TimeSpan wait, CancellationToken ended, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, ended);
        try
        {
            await Task.Delay(wait, _time, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ended.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
        }
    }
}
