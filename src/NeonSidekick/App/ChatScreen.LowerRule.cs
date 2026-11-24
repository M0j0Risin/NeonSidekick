using NeonSidekick.Audits;
using NeonSidekick.UI;
using NeonSidekick.Viewer;

namespace NeonSidekick.App;

/// <summary>
/// The lower rule's jobs group (2026-11-23, the user's ask, #17 and #18): left to right the update's marker, the loop under way (🔁 a Ralph
/// loop, 🔎 a code audit, ⟳ a <c>/loop</c>), an embedded model's download, the ComfyUI pictures still rendering, and the running processes'
/// count — each drawn whole, each with its own double-click (<see cref="LowerRuleClick"/>). The loop, the download and the pictures were the
/// status bar's until then.
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

    /// <summary>The lower rule's items now, left to right, each only while it has something to say. Any thread (the pane's draw and tick).</summary>
    private IReadOnlyList<LowerRuleItem> LowerRuleItemsNow()
    {
        var items = new List<LowerRuleItem>(5);
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

        if (_pendingPictures.Glyph is { Length: > 0 } pictures)
        {
            items.Add(new(LowerRuleKind.Pictures, pictures));
        }

        if (ProcessWindowText.RuleTitle(_processes.Running) is { Length: > 0 } processes)
        {
            items.Add(new(LowerRuleKind.Processes, processes));
        }

        return items;
    }

    /// <summary>
    /// A double-click on the lower rule's <paramref name="kind"/>, under a reply or at the idle line: the word to run for the update's marker
    /// (<c>/update</c>) and the processes' count (<c>/process</c>); otherwise it acts here and answers null — the loop ends after the pass in
    /// progress, the download is canceled (its end said by the jobs' drain), the pictures are canceled while the reply goes on.
    /// </summary>
    private string? LowerRuleClick(LowerRuleKind kind)
    {
        switch (kind)
        {
            case LowerRuleKind.Update:
                return Updates.UpdateText.Word;
            case LowerRuleKind.Processes:
                return ProcessWindowText.Word;
            case LowerRuleKind.Loop:
                AskLoopEnd();
                return null;
            case LowerRuleKind.Download:
                _jobs.Cancel(BackgroundJobKind.EmbeddedDownload);
                return null;
            case LowerRuleKind.Pictures:
                DrainPictures();
                return null;
            default:
                return null;
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
