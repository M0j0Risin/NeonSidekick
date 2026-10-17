using NeonSidekick.Diagnostics;
using NeonSidekick.UI;
using NeonSidekick.Web;
using NeonSidekick.WebUi;
using Spectre.Console;

namespace NeonSidekick.App;

// ── Bot checks in the Browser window (2026-10-13) ─────────────────────────

internal sealed partial class ChatScreen
{
    /// <summary>The Browser window (<c>Viewer.BrowserWindow.Window</c> on Windows, a fake in tests); null where there is none, and no bot check is then shown to anyone.</summary>
    private readonly Viewer.IBrowserWindow? _browserWindow;

    /// <summary>"Allow for this session" on the bot-check pane (<c>Web browser botcheck window</c> <c>ask</c>); forgotten with the session.</summary>
    private bool _botCheckAllowed;

    /// <summary>The hosts whose bot check the user declined in the running turn: the turn's later calls there are answered without asking.</summary>
    private readonly HashSet<string> _botCheckDeclined = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>One bot check at a time: the window is one, and a bot's and the main chat's tools share it.</summary>
    private readonly SemaphoreSlim _botCheckGate = new(1, 1);

    /// <summary>
    /// The web tools' bot check shown to the user (2026-10-13, the user's ask; <see cref="BotCheckSolver"/>), on the turn task: none
    /// without a window, under <c>disabled</c>, or under <c>ask</c> with no pane to ask on (null leaves the tool's refusal as it was);
    /// a host declined earlier in the turn answered without asking; under <c>ask</c> the allow pane (deny / once / this session), under
    /// <c>automatic</c> straight on. Then the window opens on the address and the pane waits beside it — ESC or Give up ends the wait,
    /// as the window's own close does — until the page is through, for at most <see cref="BotCheck.SolveTimeout"/>.
    /// </summary>
    /// <remarks>
    /// Both panes are on the Web UI's page too since 2026-10-17 (the user's ask: a check met from the phone waited at the PC until they were
    /// home): the allow through <see cref="AllowBothAsync"/>, the wait as a <c>botcheck</c> card (<see cref="WaitBothAsync"/>) that shows the
    /// window and drives it under <i>Web UI bot check control</i> (<c>ChatScreen.BotCheckWeb.cs</c>).
    /// </remarks>
    private async Task<BotCheckResult?> SolveBotCheckAsync(BotCheckRequest request, CancellationToken turnToken)
    {
        if (_browserWindow is not { } window)
        {
            return null;
        }

        var mode = BotcheckWindow.Resolve(_effective());
        if (mode == BotcheckKind.Disabled)
        {
            return null;
        }

        string url = request.Url.AbsoluteUri;
        lock (_botCheckDeclined)
        {
            if (_botCheckDeclined.Contains(request.Url.Host))
            {
                return BotCheckResult.Ended(BotCheckEnd.Declined, url);
            }
        }

        if (mode == BotcheckKind.Ask && !_botCheckAllowed)
        {
            if (!_pane.Enabled)
            {
                return null;
            }

            string caption = BotCheckText.AskCaption(request);
            var allow = await AllowBothAsync(BotCheckText.AskTitle, caption, null, token => _cameraMenu.AllowAsync(caption, token, BotCheckText.AskTitle), turnToken).ConfigureAwait(false);
            if (allow is not { } choice)
            {
                return null;
            }

            if (choice == CameraAllow.Deny)
            {
                lock (_botCheckDeclined)
                {
                    _botCheckDeclined.Add(request.Url.Host);
                }

                DiagnosticLog.Info(WebFetcher.Category, $"The user declined the bot check on {request.Url.Host}.");
                return BotCheckResult.Ended(BotCheckEnd.Declined, url);
            }

            _botCheckAllowed = choice == CameraAllow.Session;
        }

        await _botCheckGate.WaitAsync(turnToken).ConfigureAwait(false);
        try
        {
            return await SolveInWindowAsync(window, request, turnToken).ConfigureAwait(false);
        }
        finally
        {
            _botCheckGate.Release();
        }
    }

    /// <summary>The window on the check and the wait beside it, at the PC and on the page: ESC or Give up ends it; the window's answer either way.</summary>
    private async Task<BotCheckResult> SolveInWindowAsync(Viewer.IBrowserWindow window, BotCheckRequest request, CancellationToken turnToken)
    {
        using var timeout = new CancellationTokenSource(BotCheck.SolveTimeout, _time);
        using var solving = CancellationTokenSource.CreateLinkedTokenSource(turnToken, timeout.Token);
        var solve = window.SolveAsync(request, solving.Token);
        bool gaveUp = await WaitBothAsync(window, request, solve, turnToken).ConfigureAwait(false);
        if (gaveUp)
        {
            await solving.CancelAsync().ConfigureAwait(false);
        }

        var result = await solve.ConfigureAwait(false);
        if (result.End == BotCheckEnd.GaveUp && timeout.IsCancellationRequested && !gaveUp && !turnToken.IsCancellationRequested)
        {
            result = result with { End = BotCheckEnd.TimedOut };
        }

        DiagnosticLog.Info(WebFetcher.Category, $"The bot check on {request.Url.Host} in the Browser window: {result.End}.");
        return result;
    }

    /// <summary>
    /// The wait beside the window (2026-10-17): a <c>botcheck</c> card on the page, opened first (so a turn from the page never leaves a pane at the
    /// PC with nothing on the page), then the PC's waiting pane. True when the user gave up on either side, false once the window answered. The
    /// pane's false (the window answered, no watcher to run it) never ends the wait on its own: the card stays up until the window answers or
    /// someone gives up. Each side claims before it counts (<see cref="WebAskHandle.TryClaim"/>), as <see cref="AskBothAsync"/>'s do; not that
    /// helper itself, which returns at once when no watcher runs the pane. While the wait runs it is the live check
    /// (<see cref="_botCheckLive"/>), the only door the page's pictures and input reach the window through.
    /// </summary>
    private async Task<bool> WaitBothAsync(Viewer.IBrowserWindow window, BotCheckRequest request, Task<BotCheckResult> solve, CancellationToken turnToken)
    {
        bool live = _effective().WebUiBotCheckControl;
        var handle = _webUi.Asks.Open(new WebAskSpec("botcheck", BotCheckText.WaitTitle, [new WebChoice(BotCheckGiveUpChoice, BotCheckText.GiveUpRow)],
            Body: BotCheckText.WaitCaption(request), Live: live));
        using var webAnswered = new CancellationTokenSource();
        var check = new BotCheckLive(handle.Id, window, request.Url.Host);
        SetBotCheckLive(check);
        try
        {
            Task<bool> pane = _pane.Enabled
                ? RunCameraPaneAsync(async token =>
                {
                    using var either = CancellationTokenSource.CreateLinkedTokenSource(token, webAnswered.Token);
                    bool gave = await WaitBesideAsync(request, solve, either.Token).ConfigureAwait(false);
                    return gave && !webAnswered.IsCancellationRequested && handle.TryClaim(AskSource.Pc);
                }, false, turnToken)
                : Task.FromResult(false);
            var waiting = new List<Task> { solve, pane, handle.Answered };
            while (true)
            {
                var first = await Task.WhenAny(waiting).ConfigureAwait(false);
                if (first == handle.Answered)
                {
                    var answer = await handle.Answered.ConfigureAwait(false);
                    VoiceSession.SafeCancel(webAnswered);
                    string said = handle.Describe(answer);
                    DiagnosticLog.Info(WebUiCategory, WebUiText.AnsweredLogLine(BotCheckText.WaitTitle, said, handle.Remote));
                    _transcript.Notice(WebUiText.AnsweredNotice(said));
                    await Quietly(pane).ConfigureAwait(false);
                    return true;
                }

                if (first == pane)
                {
                    if (pane.IsCompletedSuccessfully && pane.Result)
                    {
                        return true;
                    }

                    waiting.Remove(pane);   // no answer from the PC: the window and the page still may
                    continue;
                }

                // The window answered: the PC's pane closes on its own (it races the same solve).
                handle.TryClaim(AskSource.Pc);
                VoiceSession.SafeCancel(webAnswered);
                await Quietly(pane).ConfigureAwait(false);
                return false;
            }
        }
        finally
        {
            SetBotCheckLive(null);
            check.End();
            _webUi.Asks.Close(handle);
        }
    }

    /// <summary>The bot check card's one choice.</summary>
    internal const string BotCheckGiveUpChoice = "giveup";

    // A pane's task awaited for its end alone: a turn's cancel under it is the turn's to report.
    private static async Task Quietly(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>The waiting pane, until the window answers (false) or the user gives up on it (true: ESC, Give up, or the pane closed under it).</summary>
    private async Task<bool> WaitBesideAsync(BotCheckRequest request, Task<BotCheckResult> solve, CancellationToken cancellationToken)
    {
        using var answered = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var page = BotCheckWaitPage(request);
        try
        {
            var pick = _menuPane.PickAsync(page, 0, answered.Token);
            var first = await Task.WhenAny(solve, pick).ConfigureAwait(false);
            if (first == solve)
            {
                await answered.CancelAsync().ConfigureAwait(false);
                await pick.ConfigureAwait(false);
                return false;
            }

            return true;
        }
        finally
        {
            _menuPane.Close();
        }
    }

    /// <summary>A bot check declined in the last turn may be shown again in this one.</summary>
    private void ForgetBotCheckDeclines()
    {
        lock (_botCheckDeclined)
        {
            _botCheckDeclined.Clear();
        }
    }

    /// <summary>The waiting pane's page: its one row, the wait as its caption. Pure.</summary>
    public static MenuPage BotCheckWaitPage(BotCheckRequest request) =>
        new(BotCheckText.WaitTitle, [Markup.Escape(BotCheckText.GiveUpRow)], BotCheckText.WaitHint)
        {
            Caption = BotCheckText.WaitCaption(request),
        };
}
