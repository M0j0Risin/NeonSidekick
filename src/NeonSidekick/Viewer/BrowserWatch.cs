using NeonSidekick.Web;

namespace NeonSidekick.Viewer;

/// <summary>What the Browser window does after a look at its page (<see cref="BrowserWatch.Decide"/>).</summary>
public enum BrowserWatchStep
{
    /// <summary>Not yet: the page is loading, changing, or still a bot check.</summary>
    Wait,

    /// <summary>Through: the page has loaded and settled with no check showing; read it and hand it back.</summary>
    Read,

    /// <summary>Loaded and settled with no check showing, but with an error status (or no page at all): no person can solve that.</summary>
    Refused,
}

/// <summary>
/// One look at the Browser window's page (<see cref="HeadlessBrowser.PrintExpression"/>, <see cref="BrowserAnswers.TryReadLook"/>): its ready
/// state, its DOM's fingerprint, its own HTTP status (<c>responseStatus</c>; 0 where the engine gives none, as WebKit), its address, and its
/// document (<c>performance.timeOrigin</c>: a new document has a new one, a <c>pushState</c> keeps it; used as an id only).
/// </summary>
public readonly record struct BrowserLook(string State, (int, uint) Print, int Status, string Url, double Document);

/// <summary>
/// When the Browser window's page is through its bot check (2026-10-13, <see cref="BotCheck"/>): the headless browser's watch
/// (<c>HeadlessBrowser.WatchPageAsync</c>) made pure for a window whose looks arrive as script answers. Each look is the page's
/// ready state, its DOM's fingerprint, its status and its document (<see cref="BrowserLook"/>); the DOM itself is read
/// (<see cref="WantsRead"/>, then <see cref="Read"/>) when it differs from the one last read and that read is
/// <see cref="DomSettle.Quiet"/> old, and once more when the page is ready and changed since (<see cref="Stale"/>). Unlike the
/// headless browser's it never takes a check's page as it is (<see cref="DomSettle.ChallengeLimit"/> infinite): a person is solving
/// it, and the caller's clock (<see cref="BotCheck.SolveTimeout"/>) ends the wait. Pure: time is the caller's.
///
/// <para>The document showing is judged with its own status (2026-10-26, the review of fd46f9c6, the user's call: the redesign). The
/// status came only with a navigation's completion, after the page's pictures and scripts, so the watch had waited for loads, matched
/// navigation ids and capped the wait; a document now carries its status in each look, and the watch keeps its state per document: a
/// new one starts its settle, its read and its status afresh. The document showing when the check began (<see cref="Before"/>) is never
/// judged, nor a page that is no web page (the blank start page). A 4xx on a document a check was seen on waits: it is the check's own
/// page, solved or not, about to move on (Cloudflare's "Verification successful" interstitial).</para>
/// </summary>
public sealed class BrowserWatch
{
    private DomSettle _settle = NewSettle();
    private (int, uint)? _last;
    private (int, uint)? _readPrint;
    private TimeSpan _readAt;
    private bool _thin;
    private double? _document;
    private readonly HashSet<double> _challengeSeenOn = [];

    private static DomSettle NewSettle() => new() { ChallengeLimit = Timeout.InfiniteTimeSpan };

    /// <summary>The document showing when the check began (its look's <see cref="BrowserLook.Document"/>): never judged. Null when unknown.</summary>
    public double? Before { get; set; }

    /// <summary>The DOM last read of the document showing; null before its first read.</summary>
    public string? Html { get; private set; }

    /// <summary>Whether the DOM last read is a bot check's (<see cref="BotCheck.StillGated"/>).</summary>
    public bool Gated { get; private set; }

    /// <summary>The document showing's HTTP status (0: unknown).</summary>
    public int Status { get; private set; }

    /// <summary>The document showing's address, as its look gave it.</summary>
    public string Url { get; private set; } = "";

    /// <summary>Whether the document showing is the window's error page: its address did not load (no name, no connection, refused).</summary>
    public bool LoadFailed { get; private set; }

    /// <summary>
    /// <paramref name="look"/>'s document taken as the one showing: a new one starts afresh (its settle, its read, its status
    /// <paramref name="status"/>, whether it is the window's <paramref name="errorPage"/>); the same one keeps what it had.
    /// </summary>
    private void Showing(BrowserLook look, int status, bool errorPage)
    {
        if (_document == look.Document)
        {
            return;
        }

        _document = look.Document;
        _settle = NewSettle();
        _last = null;
        _readPrint = null;
        _readAt = TimeSpan.Zero;
        _thin = false;
        Html = null;
        Gated = false;
        Status = status;
        Url = look.Url;
        LoadFailed = errorPage;
    }

    // Whether the document showing may be judged at all: not the one from before the check, and a web page or the window's error page.
    private bool Judged => _document is { } document && document != Before
        && (LoadFailed || (Uri.TryCreate(Url, UriKind.Absolute, out var at) && WebFetcher.IsHttp(at)));

    /// <summary>
    /// Whether a look should read the DOM first: <paramref name="look"/>'s document taken as the one showing, with its
    /// <paramref name="status"/> and whether it is the window's <paramref name="errorPage"/> (the window's say, <c>IBrowserCheckPage.Describe</c>).
    /// </summary>
    public bool WantsRead(TimeSpan now, BrowserLook look, int status, bool errorPage)
    {
        Showing(look, status, errorPage);
        return Judged && look.Print != _readPrint && (_readPrint is null || now - _readAt >= DomSettle.Quiet);
    }

    /// <summary>Whether the DOM last read is older than <paramref name="print"/>: a ready page is read once more before it is taken.</summary>
    public bool Stale((int, uint) print) => print != _readPrint;

    /// <summary>
    /// The DOM of <paramref name="document"/> read at <paramref name="now"/> on <paramref name="url"/>, judged: a check, a thin page. False,
    /// and nothing kept, when it is no longer the document showing (the next page came in between the look and the read).
    /// </summary>
    public bool Read(TimeSpan now, string html, Uri url, double document)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(url);
        if (_document != document)
        {
            return false;
        }

        Html = html;
        _readPrint = HeadlessBrowser.Fingerprint(html);
        _readAt = now;
        var page = HtmlToMarkdown.Convert(html, url);
        Gated = BotCheck.StillGated(html, page.Markdown);
        if (Gated)
        {
            _challengeSeenOn.Add(document);
        }

        _thin = DomSettle.LooksThin(html, page.Markdown);
        return true;
    }

    /// <summary>
    /// One look at <paramref name="now"/>, after any read it wanted (<see cref="WantsRead"/> took its document). True when the page is ready
    /// to be taken (<see cref="Decide"/> then says how); never for the document from before the check or a page that is no web page.
    /// </summary>
    public bool Observe(TimeSpan now, BrowserLook look, int status, bool errorPage)
    {
        Showing(look, status, errorPage);
        bool changed = look.Print != _last;
        _last = look.Print;
        bool ready = _settle.Observe(now, look.State is "interactive" or "complete", changed, Gated, _thin);
        return Judged && ready;
    }

    /// <summary>
    /// What a ready page is: <see cref="BrowserWatchStep.Read"/>, or <see cref="BrowserWatchStep.Refused"/> for the window's error page or an
    /// error status; <see cref="BrowserWatchStep.Wait"/> while it is a check, unread, not to be judged, or a 4xx on a document a check was seen on.
    /// </summary>
    public BrowserWatchStep Decide()
    {
        if (!Judged || Html is null || Gated)
        {
            return BrowserWatchStep.Wait;
        }

        if (LoadFailed)
        {
            return BrowserWatchStep.Refused;
        }

        if (Status >= 400)
        {
            return _document is { } document && _challengeSeenOn.Contains(document) ? BrowserWatchStep.Wait : BrowserWatchStep.Refused;
        }

        return BrowserWatchStep.Read;
    }
}
