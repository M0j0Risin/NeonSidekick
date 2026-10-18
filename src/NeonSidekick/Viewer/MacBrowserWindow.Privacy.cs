using System.Runtime.Versioning;
using NeonSidekick.Diagnostics;
using static NeonSidekick.Viewer.AppKitNative;

namespace NeonSidekick.Viewer;

// ── Privacy, clears, the list of visits and the saved links (2026-10-18, the Mac Browser plan's Phase 2) ──

/// <summary>
/// The Mac Browser window's privacy, as Windows': under <c>Web browser privacy</c> <c>clear-history</c> the window's own list of visits lives
/// only while it is open (its file deleted at the close, and as a backstop at the open); under <c>keep-history</c> it is kept in the
/// profile's <c>browser-history.json</c>, saved 2 s after a visit. WebKit keeps no browsing history beyond the open view's back and forward
/// list, so the list and its file are all a history clear takes; <c>/browser clear all</c> also empties the profile's WebKit store
/// (<c>removeDataOfTypes:</c>, every type). A clear while a check waits is refused (<see cref="BrowserText.ClearDuringCheck"/>). The saved
/// links are Windows' (<see cref="BrowserLinks"/>): the ☆ menu, ⌘D, ⌘⇧O and ⌘⌥B.
/// </summary>
internal sealed partial class MacBrowserWindow
{
    private static readonly TimeSpan HistorySaveDelay = TimeSpan.FromSeconds(2);

    private readonly string _historyFile;
    private readonly BrowserLinks _links;
    private readonly MainTimer _historySave;
    private readonly List<TaskCompletionSource<string?>> _pendingClears = [];
    private readonly List<TaskCompletionSource<(string Url, string? Title)?>> _pendingPages = [];
    private IReadOnlyList<BrowserHistoryEntry> _history = [];
    private bool _historyDirty;
    private BrowserPrivacyKind _privacy;

    /// <summary>The window's own profile's privacy: the setting while that profile is loaded, else the last value read while it was (Windows' rule).</summary>
    private BrowserPrivacyKind PrivacyNow()
    {
        var now = MacBrowserWindows.PrivacyNow();
        if (MacBrowserWindows.IsLoaded(Profile))
        {
            _privacy = now;
        }

        return _privacy;
    }

    /// <summary>See <see cref="IBrowserWindow.ClearAsync"/>, for this window's profile.</summary>
    public Task<string?> ClearAsync(BrowserClear kind)
    {
        var done = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pendingClears)
        {
            _pendingClears.Add(done);
        }

        if (!Post(() => BeginClear(kind, done)))
        {
            SettleClear(done, BrowserText.Closed);
        }

        return done.Task;
    }

    private void BeginClear(BrowserClear kind, TaskCompletionSource<string?> done)
    {
        if (_jobs.Check is { Finishing: false })
        {
            SettleClear(done, BrowserText.ClearDuringCheck);   // the check's cookies are what it waits for
            return;
        }

        ForgetHistory();
        if (kind != BrowserClear.All || _store == 0)
        {
            DiagnosticLog.Info("Browser", "The Browser window's history cleared (/browser clear).");
            SettleClear(done, null);
            return;
        }

        var timeout = new MainTimer(() => SettleClear(done, BrowserText.MacClearTimedOut));
        timeout.Start(MacBrowserWindows.RemoteTimeout);
        MacBrowserKit.RemoveAll(_store, () =>
        {
            timeout.Stop();
            DiagnosticLog.Info("Browser", "The Browser window's data cleared (/browser clear).");
            SettleClear(done, null);
        });
    }

    private void SettleClear(TaskCompletionSource<string?> done, string? failure)
    {
        lock (_pendingClears)
        {
            _pendingClears.Remove(done);
        }

        done.TrySetResult(failure);
    }

    private void EndClears(string why)
    {
        TaskCompletionSource<string?>[] waiting;
        lock (_pendingClears)
        {
            waiting = [.. _pendingClears];
            _pendingClears.Clear();
        }

        foreach (var done in waiting)
        {
            done.TrySetResult(why);
        }
    }

    // ── The window's own list of visits ───────────────────────────────────

    private void LoadHistory() => _history = BrowserHistory.Load(_historyFile);

    // A page completed: on top of the list, saved shortly under keep-history.
    private void RecordVisit()
    {
        string? source = Source;
        if (PdfView.IsOurs(source) || WebUi.WebUiAuth.IsSignInUrl(source))
        {
            return;   // a staged PDF, a Web UI sign-in link: no visit to remember
        }

        var recorded = BrowserHistory.Record(_history, source, _pageTitle, TimeProvider.System.GetUtcNow());
        if (!ReferenceEquals(recorded, _history))
        {
            _history = recorded;
            HistoryChanged();
        }
    }

    private void RetitleVisit(string title)
    {
        var retitled = BrowserHistory.Retitle(_history, Source, title);
        if (!ReferenceEquals(retitled, _history))
        {
            _history = retitled;
            HistoryChanged();
        }
    }

    private void HistoryChanged()
    {
        _historyDirty = true;
        if (PrivacyNow() == BrowserPrivacyKind.KeepHistory)
        {
            _historySave.Start(HistorySaveDelay);
        }
    }

    private void SaveHistory()
    {
        if (_historyDirty && PrivacyNow() == BrowserPrivacyKind.KeepHistory && BrowserHistory.Save(_historyFile, _history))
        {
            _historyDirty = false;
        }
    }

    // At the close: the list saved under keep-history; under clear-history, a file left from keep-history goes.
    private void KeepHistoryAtClose()
    {
        _historySave.Stop();
        if (PrivacyNow() == BrowserPrivacyKind.KeepHistory)
        {
            SaveHistory();
        }
        else
        {
            if (File.Exists(_historyFile))
            {
                BrowserHistory.Delete(_historyFile);
            }

            DiagnosticLog.Info("Browser", "The Browser window's history cleared as it closed (clear-history).");
        }
    }

    private void ForgetHistory()
    {
        _historySave.Stop();
        _history = [];
        _historyDirty = false;
        if (File.Exists(_historyFile))
        {
            BrowserHistory.Delete(_historyFile);
        }
    }

    // ── The page and the saved links ──────────────────────────────────────

    /// <summary>See <see cref="IBrowserWindow.CurrentPageAsync"/>: the page as the window shows it, null for one that is no web page.</summary>
    public Task<(string Url, string? Title)?> CurrentPageAsync()
    {
        var done = new TaskCompletionSource<(string Url, string? Title)?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pendingPages)
        {
            _pendingPages.Add(done);
        }

        if (!Post(() => SettlePage(done, _web != 0 && Source is { } source && VideoPage.IsWebLink(source) ? (source, _pageTitle) : null)))
        {
            SettlePage(done, null);
        }

        return done.Task;
    }

    /// <summary>See <see cref="IBrowserWindow.LinksChanged"/>.</summary>
    public void LinksChanged() => Post(RefreshSaved);

    private void SettlePage(TaskCompletionSource<(string Url, string? Title)?> done, (string Url, string? Title)? page)
    {
        lock (_pendingPages)
        {
            _pendingPages.Remove(done);
        }

        done.TrySetResult(page);
    }

    private void EndPages()
    {
        TaskCompletionSource<(string Url, string? Title)?>[] waiting;
        lock (_pendingPages)
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
        bool saved = _links.Find(Source) is not null;
        if (saved != _barState.Saved)
        {
            _barState = _barState with { Saved = saved };
            _symbols.Remove(BrowserToolItem.Links);
            RefreshBar();
        }
    }

    // A saved page that loaded names its link when it had no name yet.
    private void FillLinkName()
    {
        if (_barState.Saved && _links.FillName(Source, _pageTitle))
        {
            DiagnosticLog.Debug("Browser", "A saved link took its page's title as its name.");
        }
    }

    // ⌘D and the menu's first row: the page saved (its title as the name) or, saved already, taken off.
    private void ToggleSaved(string? url, string? title)
    {
        if (BrowserLinks.Refused(url) is not null)
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
}
