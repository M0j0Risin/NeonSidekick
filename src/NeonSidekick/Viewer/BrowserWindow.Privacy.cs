using System.Globalization;
using NeonSidekick.Diagnostics;
using static NeonSidekick.Viewer.ViewerNative;

namespace NeonSidekick.Viewer;

// ── Privacy, clears and the history menu (later on 2026-10-13) ────────────

/// <summary>
/// The Browser window's privacy (later on 2026-10-13, the user's ask): under <c>Web browser privacy</c> <c>clear-history</c> its history is
/// cleared at every close (after a visit's cookies are read) and, as a backstop for a close that never finished, as it opens, before
/// its first page; <c>/browser clear history|all</c> (<see cref="ClearAsync"/>) clears on demand. Beside Chromium's history, which
/// WebView2 cannot read back, the window keeps its own list of visits (<see cref="BrowserHistory"/>) for the history menu (🕘, Ctrl+H):
/// in the profile's <c>browser-history.json</c> under <c>keep-history</c>, in memory only under <c>clear-history</c>.
/// </summary>
internal sealed unsafe partial class BrowserWindowThread
{
    private const uint MenuChosenMessage = WmApp + 2;

    /// <summary>How long a close waits for its clear (1.5 s when the app is exiting, inside <c>Close()</c>'s 2 s wait), and an open for its backstop clear.</summary>
    private static readonly TimeSpan CloseClearTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ExitClearTimeout = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan OpenClearTimeout = TimeSpan.FromSeconds(2);

    /// <summary>The 2 s between a visit recorded and the list saved under <c>keep-history</c>.</summary>
    private const uint HistorySaveDelayMs = 2000;

    private readonly string? _historyFile;
    private readonly bool _forClear;
    private readonly string? _profile;
    private readonly List<Action> _whenUsable = [];
    private readonly List<TaskCompletionSource<string?>> _pendingClears = [];
    private ContextMenuWindow? _menu;
    private IReadOnlyList<BrowserHistoryEntry> _history = [];
    // The list the open history menu showed (2026-10-14, the review: a row picked by its place in the live list went to another page
    // when a visit was recorded while the menu was open), as the links menu keeps _menuLinks.
    private IReadOnlyList<BrowserHistoryEntry> _menuHistory = [];
    private bool _historyDirty;
    private bool _openClearing;
    private bool _closeClearing;
    private bool _closeCleared;
    private bool _skipCloseClear;
    private BrowserPrivacyKind _privacy;

    /// <summary>
    /// The window's own profile's <c>Web browser privacy</c>: the setting in force while that profile is loaded, else the last value
    /// read while it was (2026-10-14, the review: a profile switch closes the old window after the store has loaded the new profile, and
    /// that close read the new profile's privacy, so keep-history's list was deleted under the other's clear-history). The setting is read
    /// before the profile, so a switch between the two reads is seen as a switch and the remembered value kept.
    /// </summary>
    private BrowserPrivacyKind PrivacyNow()
    {
        var now = BrowserWindow.PrivacyNow();
        if (_profile is null || BrowserWindow.IsLoaded(_profile))
        {
            _privacy = now;
        }

        return _privacy;
    }

    /// <summary>The view made, and any clear as it opened done: pages and clears may go.</summary>
    private bool Usable => _created && !_openClearing;

    /// <summary>See <see cref="IBrowserWindow.ClearAsync"/>; <paramref name="closeAfter"/> for a window started for the clear alone.</summary>
    public Task<string?> ClearAsync(BrowserClear kind, bool closeAfter)
    {
        var done = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _pendingClears.Add(done);
        }

        if (!Post(() => BeginClear(kind, done, closeAfter)))
        {
            SettleClear(done, _failure ?? BrowserText.Closed);
        }

        return done.Task;
    }

    private void SettleClear(TaskCompletionSource<string?> done, string? failure)
    {
        lock (_gate)
        {
            _pendingClears.Remove(done);
        }

        done.TrySetResult(failure);
    }

    // Whatever clear still waits as the window ends learns why.
    private void EndClears(string why)
    {
        TaskCompletionSource<string?>[] waiting;
        lock (_gate)
        {
            waiting = [.. _pendingClears];
            _pendingClears.Clear();
        }

        foreach (var done in waiting)
        {
            done.TrySetResult(why);
        }
    }

    private void BeginClear(BrowserClear kind, TaskCompletionSource<string?> done, bool closeAfter)
    {
        if (_job is { Finishing: false })
        {
            SettleClear(done, BrowserText.ClearDuringCheck);   // the check's cookies are what it waits for
            return;
        }

        if (!Usable)
        {
            _whenUsable.Add(() => BeginClear(kind, done, closeAfter));
            return;
        }

        ForgetHistory();
        bool started = _host!.ClearBrowsingData(kind, hr =>
        {
            _skipCloseClear = hr >= 0;
            DiagnosticLog.Info("Browser", hr >= 0
                ? $"The Browser window's {(kind == BrowserClear.All ? "data" : "history")} cleared (/browser clear)."
                : BrowserText.ClearFailed(hr) + ".");
            SettleClear(done, hr >= 0 ? null : BrowserText.ClearFailed(hr));
            CloseIfIdle(closeAfter);
        });
        if (!started)
        {
            SettleClear(done, BrowserText.ClearUnsupported);
            CloseIfIdle(closeAfter);
        }
    }

    // A window started for a clear alone closes after it, unless a check or a visit came meanwhile.
    private void CloseIfIdle(bool closeAfter)
    {
        if (closeAfter && _job is null && _visited is null)
        {
            PostMessageW(_hwnd, WmClose, IntPtr.Zero, IntPtr.Zero);
        }
    }

    // ── Clears at the open and the close (clear-history) ────────────────────

    // As the view is made under clear-history: the history a close never cleared (the app's exit, a crash, a kill) goes before the
    // first page, which waits for it. False when there is nothing to wait for.
    private bool StartOpenClear()
    {
        if (_forClear || PrivacyNow() != BrowserPrivacyKind.ClearHistory)
        {
            return false;
        }

        _openClearing = true;
        if (!_host!.ClearBrowsingData(BrowserClear.History, _ => EndOpenClear()))
        {
            _openClearing = false;
            return false;
        }

        SetTimer(_hwnd, OpenClearTimer, (uint)OpenClearTimeout.TotalMilliseconds, IntPtr.Zero);
        return true;
    }

    private void EndOpenClear()
    {
        KillTimer(_hwnd, OpenClearTimer);
        if (!_openClearing)
        {
            return;
        }

        _openClearing = false;
        BecomeUsable();
    }

    // The close's clear under clear-history, the window hidden while it runs; the close comes again when it answers or the timer
    // fires. False when there is none to run (keep-history, no view, a /browser clear just did it, it already ran).
    private bool StartCloseClear()
    {
        if (_closeCleared || _closeClearing || _skipCloseClear || _host is not { IsCreated: true } host
            || PrivacyNow() != BrowserPrivacyKind.ClearHistory)
        {
            return false;
        }

        _closeClearing = true;
        TerminalHandoff.ReturnFocus(_hwnd);
        _chrome.RememberPosition(BrowserWindow.Placed);
        ShowWindow(_hwnd, SwHide);
        var timeout = _noHarvest ? ExitClearTimeout : CloseClearTimeout;
        SetTimer(_hwnd, CloseClearTimer, (uint)timeout.TotalMilliseconds, IntPtr.Zero);
        if (!host.ClearBrowsingData(BrowserClear.History, hr => EndCloseClear(hr >= 0 ? null : "0x" + hr.ToString("X8", CultureInfo.InvariantCulture))))
        {
            EndCloseClear("could not start");
        }

        return true;
    }

    private void EndCloseClear(string? failure)
    {
        KillTimer(_hwnd, CloseClearTimer);
        if (!_closeClearing)
        {
            return;
        }

        _closeClearing = false;
        _closeCleared = true;
        DiagnosticLog.Info("Browser", failure is null ? "The Browser window's history cleared as it closed (clear-history)." : $"The Browser window's history was not cleared as it closed: {failure}.");
        PostMessageW(_hwnd, WmClose, IntPtr.Zero, IntPtr.Zero);
    }

    // ── The window's own list of visits ──────────────────────────────────────

    private void LoadHistory()
    {
        if (_historyFile is { } file && !_forClear)
        {
            _history = BrowserHistory.Load(file);
        }
    }

    // A page completed: on top of the list, saved shortly under keep-history. A clear just done is no longer the last word.
    private void RecordVisit()
    {
        string? source = _host?.Source;
        var recorded = BrowserHistory.Record(_history, source, _host?.Title ?? _pageTitle, TimeProvider.System.GetUtcNow());
        if (ReferenceEquals(recorded, _history))
        {
            return;   // not a web page (the blank one)
        }

        _history = recorded;
        _skipCloseClear = false;
        HistoryChanged();
    }

    private void RetitleVisit(string title)
    {
        var retitled = BrowserHistory.Retitle(_history, _host?.Source, title);
        if (!ReferenceEquals(retitled, _history))
        {
            _history = retitled;
            HistoryChanged();
        }
    }

    private void HistoryChanged()
    {
        _historyDirty = true;
        if (_historyFile is not null && PrivacyNow() == BrowserPrivacyKind.KeepHistory)
        {
            SetTimer(_hwnd, HistorySaveTimer, HistorySaveDelayMs, IntPtr.Zero);
        }
    }

    private void SaveHistory()
    {
        if (_historyDirty && _historyFile is { } file && PrivacyNow() == BrowserPrivacyKind.KeepHistory && BrowserHistory.Save(file, _history))
        {
            _historyDirty = false;
        }
    }

    // At the close: the list saved under keep-history; under clear-history, a file left from keep-history goes.
    private void KeepHistoryAtClose()
    {
        KillTimer(_hwnd, HistorySaveTimer);
        if (_historyFile is not { } file || _forClear)
        {
            return;
        }

        if (PrivacyNow() == BrowserPrivacyKind.KeepHistory)
        {
            SaveHistory();
        }
        else if (File.Exists(file))
        {
            BrowserHistory.Delete(file);
        }
    }

    // The list emptied and its file gone (a clear, the menu's Clear history).
    private void ForgetHistory()
    {
        KillTimer(_hwnd, HistorySaveTimer);
        _history = [];
        _historyDirty = false;
        if (_historyFile is { } file && File.Exists(file))
        {
            BrowserHistory.Delete(file);
        }
    }

    // ── The history menu ─────────────────────────────────────────────────────

    private void OpenHistoryMenu(bool keyboard)
    {
        if (_menu is null || _layout is null)
        {
            return;
        }

        var button = _layout.History;
        var corner = new Point { X = button.Left, Y = button.Bottom };
        ClientToScreen(_hwnd, &corner);
        _menuHistory = _history;
        var items = BrowserHistory.MenuItems(_menuHistory, TimeProvider.System.GetUtcNow(), TimeZoneInfo.Local);
        _menu.Open(items, corner.X, corner.Y, MenuStyle.For(NeonSidekick.UI.Theme.Current, _chrome.Themed()), keyboard);
    }

    private void RunHistoryCommand(int command)
    {
        if (command == BrowserHistory.ClearCommand)
        {
            ForgetHistory();
            if (_host is { IsCreated: true } host && !host.ClearBrowsingData(BrowserClear.History, hr => DiagnosticLog.Info("Browser", hr >= 0 ? "The Browser window's history cleared (its menu)." : BrowserText.ClearFailed(hr) + ".")))
            {
                DiagnosticLog.Info("Browser", BrowserText.ClearUnsupported + ".");
            }

            return;
        }

        if (command >= 1 && command <= _menuHistory.Count)
        {
            Go(_menuHistory[command - 1].Url);
            _host?.Focus();
        }
    }

    /// <summary>A key for the open history menu (the box and the window's, before <c>TranslateMessage</c>); true when it took it.</summary>
    private bool MenuKey(int virtualKey) =>
        _menu is { IsOpen: true } menu && GetKeyState(VkControl) >= 0 && GetKeyState(VkMenu) >= 0 && menu.Key(virtualKey);
}
