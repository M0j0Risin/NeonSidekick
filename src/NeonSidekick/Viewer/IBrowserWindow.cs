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
/// The Browser window as the app sees it (2026-10-13, the bot checks): <see cref="BrowserWindow.Window"/> on Windows, null elsewhere
/// (the macOS build leaves it out in v1), a fake in tests. One window per process.
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

    /// <summary>The open window closed without handing anything over (the app's exit); true when one was open.</summary>
    bool Close();
}
