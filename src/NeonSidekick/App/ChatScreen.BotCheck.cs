using NeonSidekick.Diagnostics;
using NeonSidekick.UI;
using NeonSidekick.Web;
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

            var allow = await RunCameraPaneAsync(token => _cameraMenu.AllowAsync(BotCheckText.AskCaption(request), token, BotCheckText.AskTitle), null, turnToken).ConfigureAwait(false);
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

    /// <summary>The window on the check and, when there is a pane, the wait beside it: ESC or Give up ends it; the window's answer either way.</summary>
    private async Task<BotCheckResult> SolveInWindowAsync(Viewer.IBrowserWindow window, BotCheckRequest request, CancellationToken turnToken)
    {
        using var timeout = new CancellationTokenSource(BotCheck.SolveTimeout, _time);
        using var solving = CancellationTokenSource.CreateLinkedTokenSource(turnToken, timeout.Token);
        var solve = window.SolveAsync(request, solving.Token);
        bool gaveUp = false;
        if (_pane.Enabled)
        {
            gaveUp = await RunCameraPaneAsync(token => WaitBesideAsync(request, solve, token), false, turnToken).ConfigureAwait(false);
            if (gaveUp)
            {
                await solving.CancelAsync().ConfigureAwait(false);
            }
        }

        var result = await solve.ConfigureAwait(false);
        if (result.End == BotCheckEnd.GaveUp && timeout.IsCancellationRequested && !gaveUp && !turnToken.IsCancellationRequested)
        {
            result = result with { End = BotCheckEnd.TimedOut };
        }

        DiagnosticLog.Info(WebFetcher.Category, $"The bot check on {request.Url.Host} in the Browser window: {result.End}.");
        return result;
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
