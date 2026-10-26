namespace NeonSidekick.Web;

/// <summary>Which tool met the bot check (<see cref="BotCheckRequest.Use"/>): what the pane's question names, and what comes back.</summary>
public enum BotCheckUse
{
    /// <summary><c>web_fetch</c>: the page the check let through is the answer.</summary>
    Fetch,

    /// <summary><c>web_search</c>: DuckDuckGo's results page, refused as a bot (<see cref="DuckDuckGoSearch.LooksRefused"/>).</summary>
    Search,

    /// <summary><c>download_file</c>: the clearance's cookies for the HTTP client's second try.</summary>
    Download,

    /// <summary><c>convert_to_pdf</c>: the page printed by the window itself (<see cref="BotCheckRequest.PdfPath"/>).</summary>
    Pdf,
}

/// <summary>A bot check for the user to solve in the Browser window: the address, the tool, why it was refused, and where a PDF goes.</summary>
public sealed record BotCheckRequest(Uri Url, BotCheckUse Use, string Why, string? PdfPath = null);

/// <summary>How a bot check in the Browser window ended (<see cref="BotCheckResult.End"/>).</summary>
public enum BotCheckEnd
{
    /// <summary>The check let the page through: its DOM, the address it ended on and its cookies came back.</summary>
    Cleared,

    /// <summary>The page loaded and settled with no check showing but an error status (<see cref="BotCheckResult.Status"/>): a refusal no person can solve.</summary>
    Refused,

    /// <summary>The user said no on the pane.</summary>
    Declined,

    /// <summary>The user gave up: ESC or Give up on the pane, or the window closed by hand.</summary>
    GaveUp,

    /// <summary>The check was still there after <see cref="BotCheck.SolveTimeout"/>.</summary>
    TimedOut,

    /// <summary>No window could be had (no WebView2 Runtime, no loader, its profile held by another copy of the app): <see cref="BotCheckResult.Detail"/> says why.</summary>
    Unavailable,
}

/// <summary>
/// What the Browser window brought back from a bot check (<see cref="BotCheckSolver"/>): how it <see cref="End"/>ed and, when
/// <see cref="BotCheckEnd.Cleared"/>, the page's DOM (<see cref="Html"/>, cut at <see cref="WebFetcher.MaxDownloadBytes"/> characters,
/// <see cref="Truncated"/>), the address it ended on, its HTTP status, the cookies the window holds for the two hosts and the
/// browser that earned them (<see cref="Identity"/>, for <see cref="WebFetcher.Adopt(IReadOnlyList{BrowserCookie}?, BrowserIdentity, Uri)"/>).
/// <see cref="PdfWritten"/>: the window printed the page to the request's <see cref="BotCheckRequest.PdfPath"/>. <see cref="WasFile"/>: the
/// address turned out to be a file the window would have downloaded (cancelled; the cookies are what the caller wants).
/// </summary>
public sealed record BotCheckResult(BotCheckEnd End, string? Html, string FinalUrl, int Status, IReadOnlyList<BrowserCookie> Cookies, BrowserIdentity Identity, string Detail)
{
    public bool Cleared => End == BotCheckEnd.Cleared;

    public bool PdfWritten { get; init; }

    public bool WasFile { get; init; }

    public bool Truncated { get; init; }

    /// <summary>An ending with nothing to bring back (anything but <see cref="BotCheckEnd.Cleared"/>).</summary>
    public static BotCheckResult Ended(BotCheckEnd end, string url, string detail = "", int status = 0) =>
        new(end, null, url, status, [], BrowserIdentity.Fallback, detail);
}

/// <summary>
/// Shows a bot check to the user and waits for it (<c>ChatScreen.SolveBotCheckAsync</c> over <c>Viewer.BrowserWindow</c>): null when
/// nobody can be asked — headless, off Windows, <c>Web browser botcheck window</c> on <c>disabled</c>, or <c>ask</c> with no pane —
/// which leaves the tool's refusal as it was.
/// </summary>
public delegate Task<BotCheckResult?> BotCheckSolver(BotCheckRequest request, CancellationToken cancellationToken);

/// <summary>
/// The hand-off from a web tool to the user (2026-10-13, the user's ask: the YouTube work's WebView2 used to let a person past a
/// check the headless browser cannot clear). A check that wants a click never clears in the headless browser
/// (<see cref="DomSettle.MostChallenge"/>), so <c>web_fetch</c>, <c>web_search</c>, <c>download_file</c> and <c>convert_to_pdf</c> each
/// ask a <see cref="BotCheckSolver"/> when their answer <see cref="Offers"/> one: the Browser window opens on the address, the user
/// solves the check there, and the page (or the PDF), with the cookies the window earned, comes back. Pure.
/// </summary>
public static class BotCheck
{
    /// <summary>The longest the window waits for a person (5 minutes): then the check is <see cref="BotCheckEnd.TimedOut"/>.</summary>
    public static readonly TimeSpan SolveTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Whether a fetch's answer is worth showing to a person: <see cref="FetchOutcome.Blocked"/> with no wait named (a
    /// <c>Retry-After</c> is a rate limit: the window would only add to it), and not a bare 429 — a 429 with a check's page on it
    /// is a check. So a 403, a 503 without a wait, a 200 challenge page and the headless browser's own challenge page all are;
    /// a timeout, a network-mode refusal or a 404 never. Code review, 2026-10-20: nor a refusal with no page on it (an API's JSON, S3's
    /// <c>AccessDenied</c> XML, plain text) — a check is a page a person sees — nor a 403 that names a spent rate limit
    /// (<see cref="WebFetcher.RateLimitWait"/>, GitHub's). Pinned.
    /// </summary>
    public static bool Offers(FetchResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Outcome != FetchOutcome.Blocked || result.RetryAfter is not null || result.Page is null)
        {
            return false;
        }

        return result.Status != 429 || WebFetcher.LooksLikeChallenge(result.Text, result.Page.Markdown);
    }

    /// <summary>
    /// Whether a download's refusal is worth showing to a person: <see cref="FetchOutcome.Blocked"/> as a 403, or a 503 with no wait
    /// named, served as a page (<see cref="WebFetcher.IsHtml"/>, no type counting as one, since 2026-10-20 as the fetch's); the body is
    /// not read, so a 429 is taken as the rate limit it most often is. Pinned.
    /// </summary>
    public static bool Offers(OpenedDownload opened)
    {
        ArgumentNullException.ThrowIfNull(opened);
        return opened.Outcome == FetchOutcome.Blocked && opened.RetryAfter is null && opened.Status is 403 or 503 && WebFetcher.IsHtml(opened.MediaType);
    }

    /// <summary>
    /// Whether the address a solved check ended on is another site than the one asked for (<see cref="BrowserHeaders.RegistrableDomain"/>;
    /// <c>example.com</c> to <c>www.example.com</c>, or http to https, is the same). Code review, 2026-10-20: the window's profile is kept
    /// between runs, so a page that sent the window on to a site the user is signed in to handed that session's cookies and its page to
    /// the web tools; such an ending is refused (<see cref="BotCheckText.LeftTheSite"/>) and its cookies never read. Pure; pinned.
    /// </summary>
    public static bool LeftTheSite(Uri url, string finalUrl)
    {
        ArgumentNullException.ThrowIfNull(url);
        return Uri.TryCreate(finalUrl, UriKind.Absolute, out var final) && WebFetcher.IsHttp(final) && !SameSite(url.IdnHost, final.IdnHost);
    }

    /// <summary>Whether two hosts are one site (<see cref="BrowserHeaders.RegistrableDomain"/>), whatever their scheme.</summary>
    public static bool SameSite(string host, string other) =>
        string.Equals(BrowserHeaders.RegistrableDomain(host), BrowserHeaders.RegistrableDomain(other), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The pane's short reason from a refusal's detail: its first clause (<c>HTTP 403 Forbidden</c> out of <c>HTTP 403 Forbidden; the
    /// headless browser failed too: …</c>). Pure; pinned.
    /// </summary>
    public static string WhyOf(string detail)
    {
        ArgumentNullException.ThrowIfNull(detail);
        int cut = detail.IndexOf(';', StringComparison.Ordinal);
        return (cut >= 0 ? detail[..cut] : detail).Trim();
    }

    /// <summary>
    /// Whether the window's page is still a check: a challenge page (<see cref="WebFetcher.LooksLikeChallenge"/>), or DuckDuckGo's
    /// "bots use DuckDuckGo too" on a short page (<see cref="DuckDuckGoSearch.LooksRefused"/>). Pure; pinned.
    /// </summary>
    public static bool StillGated(string html, string readable)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(readable);
        return WebFetcher.LooksLikeChallenge(html, readable)
            || (readable.Length <= WebFetcher.ChallengeTextChars && DuckDuckGoSearch.LooksRefused(html));
    }
}
