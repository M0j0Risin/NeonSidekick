using NeonSidekick.Web;

namespace NeonSidekick.Viewer;

/// <summary>What a <c>/browser</c> visit left when the user closed the window: the cookies for the hosts it went to, those hosts, and the browser that earned them.</summary>
public sealed record BrowserVisit(IReadOnlyList<BrowserCookie> Cookies, IReadOnlyList<string> Hosts, BrowserIdentity Identity)
{
    public static BrowserVisit Empty { get; } = new([], [], BrowserIdentity.Fallback);

    /// <summary>Why the window closed with nothing (its browser would not start); null for a close by the user or the app.</summary>
    public string? Failure { get; init; }
}

/// <summary>
/// The Browser window as the app sees it (2026-10-13, the bot checks): <see cref="BrowserWindow.Window"/> on Windows,
/// <c>MacBrowserWindows.Window</c> on macOS 14 or later with a window server (2026-10-18, the Mac Browser plan: WebKit), null elsewhere,
/// a fake in tests. One window per process.
/// </summary>
public interface IBrowserWindow
{
    /// <summary>
    /// The window on <paramref name="request"/>'s address until the user has it through its bot check: the page, its cookies (and its PDF
    /// for <see cref="BotCheckUse.Pdf"/>), then the window closes — unless <c>/browser</c> opened it, when it stays. Cancelling
    /// <paramref name="cancellationToken"/> ends it as <see cref="BotCheckEnd.GaveUp"/>, as the user closing the window does. Never throws
    /// for a window that cannot be had: that is <see cref="BotCheckEnd.Unavailable"/>, with the reason.
    /// </summary>
    Task<BotCheckResult> SolveAsync(BotCheckRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// The window on <paramref name="url"/> (the blank page for null) for the user to browse (<c>/browser</c>): completes when they close it,
    /// with the cookies for the hosts it went to. Throws <see cref="InvalidOperationException"/> with the reason when no window can be had.
    /// </summary>
    Task<BrowserVisit> BrowseAsync(Uri? url);

    /// <summary>The open window closed without handing anything over (the app's exit, a profile switch); true when one was open.</summary>
    bool Close();

    /// <summary>
    /// The loaded profile's browser data cleared (<c>/browser clear history|all</c>, later on 2026-10-13): the history (and the window's
    /// own list of visits) or everything its profile keeps. With no window open one is started hidden for it and closed after. Null when
    /// done, else why not: no window could be had, a bot check waits in it, the runtime refused.
    /// </summary>
    Task<string?> ClearAsync(BrowserClear kind);

    /// <summary>
    /// The page the open window shows, its address and title (a bare <c>/browser save</c>, later on 2026-10-13); null with no window open
    /// (none is started for it) or one that closed before it answered. Never hangs on a closing window; the caller caps the wait.
    /// </summary>
    Task<(string Url, string? Title)?> CurrentPageAsync();

    /// <summary>The saved links changed outside the window (the pane): its ☆ is read again. Nothing with no window open.</summary>
    void LinksChanged();

    /// <summary>
    /// Why a page cannot be put in the window now, or null when it can (later still on 2026-10-13, <c>Web open links in</c>): no WebView2
    /// runtime or loader (<see cref="BrowserText.NoRuntime"/>, <see cref="BrowserText.NoLoader"/>), or a bot check waiting in it
    /// (<see cref="BrowserText.BotCheckWaiting"/>: an open would end it as given up). Starts no window; cheap enough for every call.
    /// </summary>
    string? Unavailable();

    /// <summary>
    /// A picture of what the window shows while a bot check runs in it (2026-10-17, the Web UI's live view): a JPEG at most
    /// <paramref name="maxWidth"/> pixels wide and its tag. Null with no check running, no window open (none is started for it), or a view
    /// that did not draw in time. Never throws.
    /// </summary>
    Task<BrowserFrame?> FrameAsync(int maxWidth, CancellationToken cancellationToken) => Task.FromResult<BrowserFrame?>(null);

    /// <summary>
    /// The page's input (<see cref="BrowserInput"/>, one <see cref="BrowserRemote.Check"/> passed) sent into the window's bot check: true once
    /// sent; false with no check running, one finishing, no window open, or a view that did not answer in time. Never throws.
    /// </summary>
    Task<bool> InputAsync(BrowserInput input, CancellationToken cancellationToken) => Task.FromResult(false);

    /// <summary>
    /// The browser data a profile about to be deleted keeps outside its folder let go (2026-10-18, the Mac Browser plan: <c>/profile delete</c>):
    /// nothing on Windows, whose WebView2 profile is a folder inside the profile's; on a Mac the WebKit data store the profile's
    /// <c>webkit-browser.id</c> names, removed. Never the loaded profile's (that one cannot be deleted). Never throws.
    /// </summary>
    Task ForgetProfileAsync(string profileDirectory) => Task.CompletedTask;
}
