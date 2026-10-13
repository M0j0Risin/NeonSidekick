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
    private void HandleBrowser(string args)
    {
        if (_browserWindow is not { } window)
        {
            _flow.Error(BotCheckText.BrowserNeedsWindows);
            return;
        }

        // A bare /browser goes to Web browser home page (later on 2026-10-13, with the window's navigation bar), the blank page without one.
        string text = args.Trim();
        Uri? url = text.Length == 0 ? Viewer.BrowserAddress.Home(_effective().WebBrowserHomePage) : null;
        if (text.Length > 0 && (url = WebFetcher.ParseUrl(text)) is null)
        {
            _flow.Error(BotCheckText.NotAUrl(text));
            return;
        }

        _flow.Notice(BotCheckText.Opened(url?.AbsoluteUri ?? Viewer.BrowserPage.Blank));
        _ = BrowseAsync(window, url);
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
