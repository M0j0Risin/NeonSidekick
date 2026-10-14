using NeonSidekick.Diagnostics;
using static NeonSidekick.Viewer.ViewerNative;

namespace NeonSidekick.Viewer;

// ── The saved links (later on 2026-10-13) ─────────────────────────────────

/// <summary>
/// The Browser window's saved links (later on 2026-10-13, the user's ask: web links kept like <c>/youtube list</c>'s saved videos): the
/// bar's ☆ button (★ while the page is saved) opens their menu under it — <see cref="BrowserLinkText.SaveThisPage"/> or
/// <see cref="BrowserLinkText.RemoveThisPage"/>, then the links (<see cref="BrowserLinks.MenuItems"/>) — Ctrl+Shift+O opens it with the
/// keyboard on it, and Ctrl+D saves or unsaves the page with no menu. The list is the profile's <c>browser-links.json</c>, read when the
/// menu opens and the page changes (the screen's pane writes it too, and says so through <see cref="LinksChanged"/>); a menu acts on the
/// list it showed (<see cref="_menuLinks"/>), so a change made while it was open never sends a row to another link. A link saved by its
/// address alone takes the page's title as its name once the window has been there (<see cref="BrowserLinks.FillName"/>).
/// </summary>
internal sealed unsafe partial class BrowserWindowThread
{
    private readonly BrowserLinks? _links;
    private readonly List<TaskCompletionSource<(string Url, string? Title)?>> _pendingPages = [];
    private IReadOnlyList<BrowserLink> _menuLinks = [];
    private string? _menuPage;

    /// <summary>See <see cref="IBrowserWindow.CurrentPageAsync"/>: the page as the window's thread sees it, null for one that is no web page.</summary>
    public Task<(string Url, string? Title)?> CurrentPageAsync()
    {
        var done = new TaskCompletionSource<(string Url, string? Title)?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _pendingPages.Add(done);
        }

        if (!Post(() => SettlePage(done, _host is { IsCreated: true } host && VideoPage.IsWebLink(host.Source) ? (host.Source!, host.Title ?? _pageTitle) : null)))
        {
            SettlePage(done, null);
        }

        return done.Task;
    }

    /// <summary>See <see cref="IBrowserWindow.LinksChanged"/>.</summary>
    public void LinksChanged() => Post(RefreshSaved);

    private void SettlePage(TaskCompletionSource<(string Url, string? Title)?> done, (string Url, string? Title)? page)
    {
        lock (_gate)
        {
            _pendingPages.Remove(done);
        }

        done.TrySetResult(page);
    }

    // The window gone: every page asked for answers none.
    private void EndPages()
    {
        TaskCompletionSource<(string Url, string? Title)?>[] waiting;
        lock (_gate)
        {
            waiting = [.. _pendingPages];
            _pendingPages.Clear();
        }

        foreach (var done in waiting)
        {
            done.TrySetResult(null);
        }
    }

    /// <summary>The ☆ drawn for the page: filled while it is a saved link.</summary>
    private void RefreshSaved()
    {
        if (_links is null)
        {
            return;
        }

        SetBarState(_barState with { Saved = _links.Find(_barState.Source) is not null });
    }

    // A saved page that loaded names its link when it had no name yet (read only for a saved page, not at every title).
    private void FillLinkName()
    {
        if (_links is not null && _barState.Saved && _links.FillName(_host?.Source, _host?.Title ?? _pageTitle))
        {
            DiagnosticLog.Debug("Browser", "A saved link took its page's title as its name.");
        }
    }

    // Ctrl+D and the menu's first row: the page saved (its title as the name, when it is one) or, saved already, taken off.
    private void ToggleSaved(string? url, string? title)
    {
        if (_links is null || BrowserLinks.Refused(url) is not null)
        {
            return;
        }

        var outcome = _links.Find(url) is not null ? _links.Remove(url) : _links.Add(url, BrowserLinks.UsableTitle(title, url), out _);
        if (BrowserLinkText.Refusal(outcome) is { } refused)
        {
            DiagnosticLog.Warn("Browser", "The Browser window could not change the saved links: " + refused["Error: ".Length..]);
        }

        RefreshSaved();
    }

    private void OpenLinksMenu(bool keyboard)
    {
        if (_menu is null || _layout is null)
        {
            return;
        }

        var button = _layout.Links;
        var corner = new Point { X = button.Left, Y = button.Bottom };
        ClientToScreen(_hwnd, &corner);
        _menuLinks = _links?.List() ?? [];
        _menuPage = _host?.Source;
        var items = BrowserLinks.MenuItems(_menuLinks, _menuPage, writable: _links?.Writable() ?? false);
        _menu.Open(items, corner.X, corner.Y, MenuStyle.For(NeonSidekick.UI.Theme.Current, _chrome.Themed()), keyboard);
    }

    // A pick from either menu: the saved links' commands start at BrowserLinks.ToggleCommand, past every history row's.
    private void RunMenuCommand(int command)
    {
        if (command >= BrowserLinks.ToggleCommand)
        {
            RunLinksCommand(command);
        }
        else
        {
            RunHistoryCommand(command);
        }
    }

    private void RunLinksCommand(int command)
    {
        if (command == BrowserLinks.ToggleCommand)
        {
            // The page the menu opened over, not one the window may have gone on to since; its title only while it still shows.
            string? title = string.Equals(_menuPage, _host?.Source, StringComparison.Ordinal) ? _host?.Title ?? _pageTitle : null;
            ToggleSaved(_menuPage, title);
            return;
        }

        int index = command - BrowserLinks.RowBase;
        if (index >= 0 && index < _menuLinks.Count)
        {
            Go(_menuLinks[index].Url);
            _host?.Focus();
        }
    }
}
