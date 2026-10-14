using NeonSidekick.Diagnostics;
using NeonSidekick.UI;
using NeonSidekick.Viewer;
using NeonSidekick.Web;

namespace NeonSidekick.App;

// ── /browser (2026-10-13) ─────────────────────────────────────────────────

internal sealed partial class ChatScreen
{
    /// <summary>How long a bare <c>/browser save</c> waits for the window to say what it shows (later on 2026-10-13).</summary>
    public static readonly TimeSpan CurrentPageWait = TimeSpan.FromSeconds(2);

    // The loaded profile's saved links (later on 2026-10-13): the one made for its directory, or a new one once a profile switch moved it.
    private BrowserLinks? _browserLinks;

    // The visit whose close is reported (later on 2026-10-13): while it runs, another open navigates the same window and stays quiet.
    private Task? _visit;

    /// <summary>
    /// <c>/browser</c> (2026-10-13, the user's ask, with the bot checks), parsed by <see cref="BrowserCommand"/>. <c>/browser [url]</c>: the
    /// Browser window on the page (without one, <c>Web browser home page</c>, the blank page when that is empty) for the user to browse —
    /// sign in to a site, get past its check — and when they close it, its cookies for every host it went to go to the web tools' HTTP
    /// client (<see cref="WebFetcher.Adopt(IReadOnlyList{BrowserCookie}?, BrowserIdentity, Uri)"/>), the notice saying for which. The
    /// window is its own thread, so an open returns at once; the close's notice comes through the flow sink. <c>/browser clear
    /// history|all</c> (later on 2026-10-13) clears what the window keeps for the loaded profile, <c>all</c> after a yes. The saved links
    /// (later still, the user's ask, <c>/youtube</c>'s verbs): <c>save [url [name]]</c> (no address: the page the window shows),
    /// <c>list [--clear]</c>, <c>unsave &lt;n|url&gt;</c> and <c>/browser &lt;n&gt;</c>; the list needs no window, so those work on a Mac
    /// too. Under a reply the pane, the questions and a bare <c>save</c> run on the watcher (<see cref="BrowserCommand.IsPane"/>), the rest on
    /// the turn task; <paramref name="sink"/> is the flow sink there, the transcript at the idle line.
    /// </summary>
    private async Task HandleBrowserAsync(string args, INoticeSink sink, CancellationToken cancellationToken)
    {
        var line = BrowserCommand.Parse(args);
        if (line.Error is { } error)
        {
            sink.Error(error);
            return;
        }

        switch (line.Verb)
        {
            case BrowserVerb.List:
                await ShowBrowserLinksAsync(sink, cancellationToken).ConfigureAwait(false);
                return;
            case BrowserVerb.ClearList:
                await ClearBrowserLinksAsync(sink, cancellationToken).ConfigureAwait(false);
                return;
            case BrowserVerb.Save when line.Url is { } address:
                SaveBrowserLink(address.AbsoluteUri, line.Name, sink);
                return;
            case BrowserVerb.Unsave:
            {
                var links = BrowserLinksNow();
                if (links.Resolve(line.Text) is not { } link)
                {
                    Show(BrowserLinkText.NotSaved(line.Text, links.List().Count), sink);
                    return;
                }

                Show(BrowserLinkText.Refusal(links.Remove(link.Url), line.Text) ?? BrowserLinkText.Removed(link), sink);
                _browserWindow?.LinksChanged();
                return;
            }
        }

        if (_browserWindow is not { } window)
        {
            sink.Error(BotCheckText.BrowserNeedsWindows);
            return;
        }

        switch (line.Verb)
        {
            case BrowserVerb.Open:
                OpenInBrowser(line.Url ?? BrowserAddress.Home(_effective().WebBrowserHomePage), sink);
                return;
            case BrowserVerb.OpenSaved:
            {
                var links = BrowserLinksNow();
                if (links.Resolve(line.Text) is { } link && Uri.TryCreate(link.Url, UriKind.Absolute, out var url))
                {
                    OpenInBrowser(url, sink);
                }
                else
                {
                    Show(BrowserLinkText.NotSaved(line.Text, links.List().Count), sink);
                }

                return;
            }

            case BrowserVerb.Save:
                await SaveCurrentPageAsync(window, sink).ConfigureAwait(false);
                return;
            case BrowserVerb.Clear when line.Clear == BrowserClear.All:
                // All asks first (sign-ins go), on the pane where there is one, at the idle line or on the watcher under a reply.
                if (!await ConfirmAsync(BotCheckText.ClearAllQuestion, cancellationToken).ConfigureAwait(false))
                {
                    sink.Notice(KeptNotice);
                    return;
                }

                _ = ClearBrowserAsync(window, BrowserClear.All);
                return;
            case BrowserVerb.Clear:
                _ = ClearBrowserAsync(window, BrowserClear.History);
                return;
        }
    }

    /// <summary>
    /// The one open every caller goes through (later on 2026-10-13): <c>/browser [url]</c>, <c>/browser &lt;n&gt;</c>, the saved links'
    /// pane and the Web tools page's Open browser button. The window on <paramref name="url"/>, the <see cref="BotCheckText.Opened"/> line
    /// returned (and said through <paramref name="sink"/> when there is one). While a visit runs (<see cref="_visit"/>) the open window
    /// goes there and its close is the first visit's to report, so two opens never give two close notices or hand the cookies over twice.
    /// </summary>
    private string OpenInBrowser(Uri? url, INoticeSink? sink)
    {
        string opened = BotCheckText.Opened(url?.AbsoluteUri ?? BrowserPage.Blank);
        if (_browserWindow is not { } window)
        {
            sink?.Error(BotCheckText.BrowserNeedsWindows);
            return "Error: " + BotCheckText.BrowserNeedsWindows;
        }

        sink?.Notice(opened);
        lock (_browserGate)
        {
            if (_visit is { IsCompleted: false })
            {
                _ = window.BrowseAsync(url);   // the same window, gone to the page; its close is the first visit's to report
                return opened;
            }

            _visit = BrowseAsync(window, url);
        }

        return opened;
    }

    private readonly Lock _browserGate = new();

    /// <summary>The loaded profile's saved links: the one made for its directory, or a new one once a profile switch moved it.</summary>
    private BrowserLinks BrowserLinksNow()
    {
        var links = _browserLinks;
        if (links is null || !string.Equals(links.Directory, Path.GetFullPath(_settings.ProfileDirectory), StringComparison.OrdinalIgnoreCase))
        {
            links = new BrowserLinks(_settings.ProfileDirectory, _time);
            _browserLinks = links;
        }

        return links;
    }

    private void SaveBrowserLink(string url, string? name, INoticeSink sink)
    {
        var outcome = BrowserLinksNow().Add(url, name, out var saved);
        Show(BrowserLinkText.Refusal(outcome) ?? (outcome == BrowserLinkOutcome.AlreadySaved ? BrowserLinkText.AlreadySaved(saved!) : BrowserLinkText.Saved(saved!)), sink);
        _browserWindow?.LinksChanged();
    }

    // A bare /browser save: the page the open window shows, asked for and waited on (at most CurrentPageWait), its title as the name.
    private async Task SaveCurrentPageAsync(IBrowserWindow window, INoticeSink sink)
    {
        (string Url, string? Title)? page;
        try
        {
            page = await window.CurrentPageAsync().WaitAsync(CurrentPageWait, _time).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            page = null;
        }

        if (page is not { } shown)
        {
            sink.Error(BrowserLinkText.NothingToSave);
            return;
        }

        SaveBrowserLink(shown.Url, BrowserLinks.UsableTitle(shown.Title, shown.Url), sink);
    }

    /// <summary>
    /// <c>/browser list</c> (later on 2026-10-13): the saved links on <see cref="BrowserLinksMenu"/>'s pane, Enter opening one in the
    /// Browser window; without the pane, the rows as notices through <paramref name="sink"/>.
    /// </summary>
    private Task ShowBrowserLinksAsync(INoticeSink sink, CancellationToken cancellationToken)
    {
        if (_pane.Enabled)
        {
            Func<BrowserLink, string>? open = _browserWindow is null ? null
                : link => Uri.TryCreate(link.Url, UriKind.Absolute, out var url) ? OpenInBrowser(url, sink: null) : "Error: " + BrowserLinkText.NotWeb;
            Action changed = () => _browserWindow?.LinksChanged();
            return new BrowserLinksMenu(BrowserLinksNow, sink, _menuPane, open, _input, changed, _time.LocalTimeZone).ShowAsync(cancellationToken);
        }

        PrintBrowserLinks(BrowserLinksNow().List(), sink);
        return Task.CompletedTask;
    }

    /// <summary>The saved links as notices: the count, then a numbered line each (<see cref="BrowserLinkText.PrintedRow"/>); none says so.</summary>
    internal static void PrintBrowserLinks(IReadOnlyList<BrowserLink> links, INoticeSink sink)
    {
        if (links.Count == 0)
        {
            sink.Notice(BrowserLinkText.NoneSaved);
            return;
        }

        sink.Notice(BrowserLinkText.Caption(links.Count));
        for (int i = 0; i < links.Count; i++)
        {
            sink.Notice(BrowserLinkText.PrintedRow(i + 1, links[i]));
        }
    }

    /// <summary><c>/browser list --clear</c> (later on 2026-10-13): one yes/no, then every saved link taken off; none saved is said, asked nothing.</summary>
    private async Task ClearBrowserLinksAsync(INoticeSink sink, CancellationToken cancellationToken)
    {
        var links = BrowserLinksNow();
        int count = links.List().Count;
        if (count == 0)
        {
            sink.Notice(BrowserLinkText.NoneSaved);
            return;
        }

        if (!await ConfirmAsync(BrowserLinkText.ClearPrompt(count), cancellationToken).ConfigureAwait(false))
        {
            sink.Notice(KeptNotice);
            return;
        }

        var outcome = links.Clear(out int cleared);
        Show(BrowserLinkText.Refusal(outcome) ?? (cleared == 0 ? BrowserLinkText.NoneSaved : BrowserLinkText.Cleared(cleared)), sink);
        _browserWindow?.LinksChanged();
    }

    // The clear on its own task, its answer through the flow sink (it may land while a reply runs).
    private async Task ClearBrowserAsync(IBrowserWindow window, BrowserClear kind)
    {
        try
        {
            if (await window.ClearAsync(kind).ConfigureAwait(false) is { } failure)
            {
                _flow.Error(BotCheckText.ClearFailed(failure));
                return;
            }

            if (kind == BrowserClear.All)
            {
                _web.Fetcher.ForgetAdopted();
            }

            _flow.Notice(kind == BrowserClear.All ? BotCheckText.ClearedAll : BotCheckText.ClearedHistory);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            DiagnosticLog.Error(WebFetcher.Category, "The Browser window's clear failed.", ex);
            _flow.Error(ex.Message);
        }
    }

    private async Task BrowseAsync(IBrowserWindow window, Uri? url)
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
