using NeonSidekick.Diagnostics;
using NeonSidekick.Web;

namespace NeonSidekick.App;

// ── /browser (2026-10-13) ─────────────────────────────────────────────────

internal sealed partial class ChatScreen
{
    /// <summary>
    /// <c>/browser [url]</c> (2026-10-13, the user's ask, with the bot checks): the Browser window on the page (without one, the
    /// setting <c>Web browser home page</c>, the blank page when that is empty) for the user to browse — sign in to a site, get past its check — and when they close it, its cookies for every host it went
    /// to go to the web tools' HTTP client (<see cref="WebFetcher.Adopt(IReadOnlyList{BrowserCookie}?, BrowserIdentity, Uri)"/>), the
    /// notice saying for which. The window is its own thread, so this returns at once and is safe under a reply; the close's notice
    /// comes through the flow sink. Any thread.
    /// </summary>
    private void HandleBrowser(string args) => HandleBrowser(args, atIdle: false);

    /// <summary>
    /// <c>/browser</c> at the idle line (later on 2026-10-13): as <see cref="HandleBrowser(string)"/>, but <c>/browser clear all</c> asks
    /// its question here, awaited, before the clear goes off on its own task; under a reply that clear is refused (nothing may take the
    /// pane from the reply's watcher).
    /// </summary>
    private async Task HandleBrowserAtIdleAsync(string args, CancellationToken cancellationToken)
    {
        var words = args.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (_browserWindow is { } window && ClearKind(words) == Viewer.BrowserClear.All)
        {
            if (!await ConfirmAsync(BotCheckText.ClearAllQuestion, cancellationToken).ConfigureAwait(false))
            {
                _flow.Notice(KeptNotice);
                return;
            }

            _ = ClearBrowserAsync(window, Viewer.BrowserClear.All);
            return;
        }

        HandleBrowser(args, atIdle: true);
    }

    private void HandleBrowser(string args, bool atIdle)
    {
        if (_browserWindow is not { } window)
        {
            _flow.Error(BotCheckText.BrowserNeedsWindows);
            return;
        }

        // A bare /browser goes to Web browser home page (later on 2026-10-13, with the window's navigation bar), the blank page without one.
        string text = args.Trim();
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 0 && string.Equals(words[0], BotCheckText.ClearWord, StringComparison.OrdinalIgnoreCase))
        {
            HandleBrowserClear(window, words, atIdle);
            return;
        }

        Uri? url = text.Length == 0 ? Viewer.BrowserAddress.Home(_effective().WebBrowserHomePage) : null;
        if (text.Length > 0 && (url = WebFetcher.ParseUrl(text)) is null)
        {
            _flow.Error(BotCheckText.NotAUrl(text));
            return;
        }

        _flow.Notice(BotCheckText.Opened(url?.AbsoluteUri ?? Viewer.BrowserPage.Blank));
        _ = BrowseAsync(window, url);
    }

    /// <summary>The clear <c>/browser clear …</c>'s words ask for (<c>clear history</c>, <c>clear all</c>); null for anything else.</summary>
    private static Viewer.BrowserClear? ClearKind(string[] words) =>
        words.Length != 2 || !string.Equals(words[0], BotCheckText.ClearWord, StringComparison.OrdinalIgnoreCase) ? null
        : string.Equals(words[1], BotCheckText.ClearHistoryWord, StringComparison.OrdinalIgnoreCase) ? Viewer.BrowserClear.History
        : string.Equals(words[1], BotCheckText.ClearAllWord, StringComparison.OrdinalIgnoreCase) ? Viewer.BrowserClear.All
        : null;

    /// <summary>
    /// <c>/browser clear history|all</c> (later on 2026-10-13, the user's ask): the loaded profile's Browser window data cleared — its
    /// history and list of visits, or everything (cookies and sign-ins too; the cookies the web tools took from the window forgotten as
    /// well, <see cref="WebFetcher.ForgetAdopted"/>). <c>all</c> asks first at the idle line (<see cref="HandleBrowserAtIdleAsync"/>) and
    /// is refused under a reply. The window opens hidden for it when none is; the clear runs on its own task and says how it went through
    /// the flow sink.
    /// </summary>
    private void HandleBrowserClear(Viewer.IBrowserWindow window, string[] words, bool atIdle)
    {
        if (ClearKind(words) is not { } clear)
        {
            _flow.Error(BotCheckText.ClearUsage);
            return;
        }

        if (clear == Viewer.BrowserClear.All && !atIdle)
        {
            _flow.Error(BotCheckText.ClearAllAtIdle);
            return;
        }

        _ = ClearBrowserAsync(window, clear);
    }

    private async Task ClearBrowserAsync(Viewer.IBrowserWindow window, Viewer.BrowserClear kind)
    {
        try
        {
            if (await window.ClearAsync(kind).ConfigureAwait(false) is { } failure)
            {
                _flow.Error(BotCheckText.ClearFailed(failure));
                return;
            }

            if (kind == Viewer.BrowserClear.All)
            {
                _web.Fetcher.ForgetAdopted();
            }

            _flow.Notice(kind == Viewer.BrowserClear.All ? BotCheckText.ClearedAll : BotCheckText.ClearedHistory);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            DiagnosticLog.Error(WebFetcher.Category, "The Browser window's clear failed.", ex);
            _flow.Error(ex.Message);
        }
    }

    private async Task BrowseAsync(Viewer.IBrowserWindow window, Uri? url)
    {
        try
        {
            var visit = await window.BrowseAsync(url).ConfigureAwait(false);
            if (visit.Failure is { } failure)
            {
                _flow.Error(failure);
                return;
            }

            int taken = visit.Hosts.Count == 0 ? 0 : _web.Fetcher.Adopt(visit.Cookies, visit.Identity, new Uri("https://" + visit.Hosts[0] + "/"));
            _flow.Notice(BotCheckText.Closed(taken, visit.Hosts));
        }
        catch (InvalidOperationException ex)
        {
            _flow.Error(ex.Message);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            DiagnosticLog.Error(WebFetcher.Category, "The Browser window's visit failed.", ex);
            _flow.Error(ex.Message);
        }
    }
}
