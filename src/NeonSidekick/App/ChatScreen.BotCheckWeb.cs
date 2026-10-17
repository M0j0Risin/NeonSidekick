using System.Net;
using NeonSidekick.Diagnostics;
using NeonSidekick.Viewer;
using NeonSidekick.WebUi;

namespace NeonSidekick.App;

// ── A bot check driven from the Web UI (2026-10-17) ───────────────────────

internal sealed partial class ChatScreen
{
    /// <summary>How long a picture of the window is served again to every card that asks before a new one is taken.</summary>
    internal static readonly TimeSpan BotCheckFrameReuse = TimeSpan.FromMilliseconds(250);

    /// <summary>The bot check whose wait runs now (<see cref="WaitBothAsync"/>), under <see cref="_botCheckLiveGate"/>; null with none.</summary>
    private BotCheckLive? _botCheckLive;

    private readonly Lock _botCheckLiveGate = new();

    /// <summary>
    /// The live check's door for the page (2026-10-17): its card's ask id, the window it runs in and its host, the pictures taken for it (one at
    /// a time per width, each served again for <see cref="BotCheckFrameReuse"/>), and whether a page has driven it yet. Its captures run under
    /// its own token, never a request's, so one page dropping its request never cancels the picture another waits on; <see cref="End"/>
    /// cancels them when the wait is over.
    /// </summary>
    private sealed class BotCheckLive(string askId, IBrowserWindow window, string host)
    {
        private readonly CancellationTokenSource _ending = new();
        private readonly Dictionary<int, (Task<BrowserFrame?> Task, long At)> _captures = [];
        private int _driven;

        public string AskId { get; } = askId;

        public IBrowserWindow Window { get; } = window;

        public string Host { get; } = host;

        /// <summary>True for the first page input of the check alone.</summary>
        public bool FirstDrive() => Interlocked.Exchange(ref _driven, 1) == 0;

        /// <summary>The picture at <paramref name="width"/>: the one on its way, or one taken less than <see cref="BotCheckFrameReuse"/> ago, else a new one.</summary>
        public Task<BrowserFrame?> Capture(int width, TimeProvider time)
        {
            lock (_captures)
            {
                if (_captures.TryGetValue(width, out var last) && (!last.Task.IsCompleted || time.GetElapsedTime(last.At) < BotCheckFrameReuse))
                {
                    return last.Task;
                }

                var task = Window.FrameAsync(width, _ending.Token);
                _captures[width] = (task, time.GetTimestamp());
                return task;
            }
        }

        /// <summary>
        /// The wait is over: the pictures on their way are given up. Not disposed: a request that found the check just before it ended may
        /// still ask for its token, which then reads as cancelled.
        /// </summary>
        public void End() => VoiceSession.SafeCancel(_ending);
    }

    private void SetBotCheckLive(BotCheckLive? live)
    {
        lock (_botCheckLiveGate)
        {
            _botCheckLive = live;
        }
    }

    private BotCheckLive? BotCheckLiveFor(string? askId)
    {
        lock (_botCheckLiveGate)
        {
            return _botCheckLive is { } live && string.Equals(live.AskId, askId, StringComparison.Ordinal) ? live : null;
        }
    }

    /// <summary>
    /// A bot check card's picture (a Kestrel thread): Forbidden with <i>Web UI bot check control</i> off, NotFound for an ask that is not the live
    /// check's (or one over meanwhile), else the window's picture, null when it didn't draw in time (the host's 503).
    /// </summary>
    private async Task<(WebOutcome Outcome, WebBotCheckFrame? Frame)> WebBotCheckFrameAsync(string askId, int width, CancellationToken cancellationToken)
    {
        if (!_effective().WebUiBotCheckControl)
        {
            return (WebOutcome.Forbidden, null);
        }

        if (BotCheckLiveFor(askId) is not { } live)
        {
            return (WebOutcome.NotFound, null);
        }

        try
        {
            var frame = await live.Capture(width, _time).WaitAsync(cancellationToken).ConfigureAwait(false);
            return (WebOutcome.Ok, frame is null ? null : new WebBotCheckFrame(frame.Jpeg, frame.Tag));
        }
        catch (OperationCanceledException)
        {
            // The request dropped, or the check ended under the picture.
            return (WebOutcome.NotFound, null);
        }
    }

    /// <summary>
    /// A bot check card's input (a Kestrel thread): Forbidden with the control off, NotFound for an ask that is not the live check's, Refused for an
    /// input that can't be sent, else sent into the window. The first of a check is logged with the page's address and flashed on the PC; the
    /// rest at Debug, by kind alone, never the typed text.
    /// </summary>
    private async Task<WebReply> WebBotCheckInputAsync(WebBotCheckInput request, IPAddress? remote, CancellationToken cancellationToken)
    {
        if (!_effective().WebUiBotCheckControl)
        {
            return new WebReply(WebOutcome.Forbidden, WebUiText.BotCheckControlOff);
        }

        if (BotCheckLiveFor(request.AskId) is not { } live)
        {
            return new WebReply(WebOutcome.NotFound, WebUiText.BotCheckGone);
        }

        if (BotCheckInputOf(request) is not { } input)
        {
            return new WebReply(WebOutcome.Refused, WebUiText.BadRequest);
        }

        if (BrowserRemote.Check(input) is { } refusal)
        {
            return new WebReply(WebOutcome.Refused, refusal);
        }

        if (live.FirstDrive())
        {
            DiagnosticLog.Info(WebUiCategory, WebUiText.BotCheckDrivenLogLine(live.Host, remote));
            _webUi.FlashPc(WebUiText.BotCheckDrivenFlash(live.Host));
        }

        DiagnosticLog.Debug(WebUiCategory, "Web UI: a bot check " + input.Kind.ToString().ToLowerInvariant() + " from the page.");
        try
        {
            return await live.Window.InputAsync(input, cancellationToken).ConfigureAwait(false)
                ? WebReply.Ok
                : new WebReply(WebOutcome.NotFound, WebUiText.BotCheckGone);
        }
        catch (OperationCanceledException)
        {
            return new WebReply(WebOutcome.NotFound, WebUiText.BotCheckGone);
        }
    }

    /// <summary>The page's input as the window takes it; null for a kind it doesn't know. Pure.</summary>
    internal static BrowserInput? BotCheckInputOf(WebBotCheckInput request)
    {
        ArgumentNullException.ThrowIfNull(request);
        BrowserInputKind? kind = request.Kind switch
        {
            "tap" => BrowserInputKind.Tap,
            "down" => BrowserInputKind.Down,
            "up" => BrowserInputKind.Up,
            "scroll" => BrowserInputKind.Scroll,
            "text" => BrowserInputKind.Text,
            "key" => BrowserInputKind.Key,
            _ => null,
        };
        return kind is { } k ? new BrowserInput(k, request.X, request.Y, request.DeltaY, request.Text, request.Key) : null;
    }
}
