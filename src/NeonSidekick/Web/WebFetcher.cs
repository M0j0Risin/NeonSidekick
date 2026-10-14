using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.Web;

/// <summary>
/// What one fetch is told: the engine (the setting <c>Web browser mode</c>), the browser path, where it may reach (the setting
/// <c>Web browser network mode</c>), the page it is coming from, and an <c>Accept</c> in place of Chrome's (2026-09-26: the GitHub
/// API's media types for <c>/skills add</c>; null keeps <see cref="BrowserHeaders.Accept"/>).
/// </summary>
public sealed record FetchOptions(FetchEngine Engine, string BrowserPath, NetworkReach Reach, Uri? Referer = null, string? Accept = null);

/// <summary>
/// A fetched page or the reason there is none. <see cref="Page"/> is the converted HTML;
/// <see cref="Text"/> the body of a plain-text, JSON, XML or CSV answer; <see cref="Engine"/> which
/// leg produced it (<see cref="WebText.HttpEngine"/> / <see cref="WebText.BrowserEngine"/>).
/// </summary>
/// <remarks><see cref="RetryAfter"/> (2026-10-11): how long a 429 or 503 answer asked the client to wait (its <c>Retry-After</c>), null without one.</remarks>
public sealed record FetchResult(FetchOutcome Outcome, string Url, string FinalUrl, int Status, string MediaType, PageText? Page, string Text, string Engine, string Detail, bool Truncated, TimeSpan? RetryAfter = null)
{
    public bool Ok => Outcome == FetchOutcome.Ok;

    public static FetchResult Fail(FetchOutcome outcome, string url, string detail, int status = 0, string engine = WebText.HttpEngine) =>
        new(outcome, url, url, status, "", null, "", engine, detail, false);

    /// <summary>The tool's sentence for a failed fetch.</summary>
    public string Error => WebText.Error(Outcome, Url, Detail);
}

/// <summary>
/// A downloaded file or the reason there is none (<see cref="WebFetcher.DownloadAsync"/>, 2026-09-18):
/// the body whole in <see cref="Bytes"/> (never cut — over the cap is a refusal), its media type as
/// the server said it, and <see cref="FileName"/> from a <c>Content-Disposition</c> header (its
/// path stripped, nothing sanitised), <c>""</c> without one. The HTTP leg alone, whatever the mode.
/// </summary>
public sealed record DownloadResult(FetchOutcome Outcome, string Url, string FinalUrl, int Status, string MediaType, string FileName, byte[] Bytes, string Detail)
{
    public bool Ok => Outcome == FetchOutcome.Ok;

    public static DownloadResult Fail(FetchOutcome outcome, string url, string detail, int status = 0) =>
        new(outcome, url, url, status, "", "", [], detail);

    /// <summary>The tool's sentence for a failed download.</summary>
    public string Error => WebText.Error(Outcome, Url, Detail);
}

/// <summary>
/// A download's head, its body not yet read (<see cref="WebFetcher.OpenDownloadAsync"/>, 2026-10-01): the outcome of the
/// walk, the final URL and status, the media type, the <c>Content-Disposition</c> name and the declared length (0 when none
/// was given). An Ok one holds the response — <see cref="WebFetcher.SaveAsync"/> streams its body — until disposed.
/// </summary>
public sealed class OpenedDownload : IDisposable
{
    private readonly HttpResponseMessage? _response;
    private readonly IDisposable? _owner;

    internal OpenedDownload(FetchOutcome outcome, string url, string finalUrl, int status, string mediaType, string fileName, long declared, string detail, HttpResponseMessage? response = null, IDisposable? owner = null)
    {
        Outcome = outcome;
        Url = url;
        FinalUrl = finalUrl;
        Status = status;
        MediaType = mediaType;
        FileName = fileName;
        Declared = declared;
        Detail = detail;
        _response = response;
        _owner = owner;
    }

    internal static OpenedDownload Fail(FetchOutcome outcome, string url, string detail, int status = 0) =>
        new(outcome, url, url, status, "", "", 0, detail);

    public FetchOutcome Outcome { get; }
    public string Url { get; }
    public string FinalUrl { get; }
    public int Status { get; }
    public string MediaType { get; }
    public string FileName { get; }
    public long Declared { get; }
    public string Detail { get; }

    /// <summary>How long a 429 or 503 refusal asked the client to wait (its <c>Retry-After</c>), null without one (2026-10-13: a rate limit is no bot check, <see cref="BotCheck.Offers(OpenedDownload)"/>).</summary>
    public TimeSpan? RetryAfter { get; init; }

    public bool Ok => Outcome == FetchOutcome.Ok;

    /// <summary>The tool's sentence for a failed download.</summary>
    public string Error => WebText.Error(Outcome, Url, Detail);

    internal HttpResponseMessage Response => _response ?? throw new InvalidOperationException("The download did not open.");

    public void Dispose() => _owner?.Dispose();
}

/// <summary>What <see cref="WebFetcher.SaveAsync"/> did with a body (2026-10-01): Ok with the bytes written, or the outcome and its sentence.</summary>
public sealed record SavedBody(FetchOutcome Outcome, string Url, long Bytes, string Detail)
{
    public bool Ok => Outcome == FetchOutcome.Ok;

    /// <summary>The tool's sentence for a failed save.</summary>
    public string Error => WebText.Error(Outcome, Url, Detail);
}

/// <summary>
/// The fetch behind <c>web_fetch</c> and the search engines' pages: an HTTP client that looks like
/// Chrome (<see cref="BrowserHeaders"/>), redirects followed by hand so every hop is judged by the
/// LAN rule and re-headed, the body read to <see cref="MaxDownloadBytes"/> and decoded by
/// <see cref="PageCharset"/>; and, per the mode, the headless browser (<see cref="IHeadlessBrowser"/>)
/// when the client's answer was a refusal (403 / 429 / 503, a challenge page) or a script shell with
/// nothing readable. Every failure is a <see cref="FetchOutcome"/> with a detail, never an exception;
/// only cancellation escapes.
/// <para>
/// Since 2026-10-11 (the user's ask: get the HTTP client past more bot checks) a page the browser read hands the client its
/// cookies (<see cref="BrowserDump.Cookies"/>, into the jar the client sends from), so a clearance the browser earned holds for
/// the next fetch from that site. Cloudflare ties its clearance to the user agent as well as the address, so from then on the
/// client claims for that host the browser that earned it (<see cref="ClaimFor"/>) rather than Chrome.
/// </para>
/// <para>
/// Later that day, the same ask: a site that limits by rate is asked politely. A 429 or 503 with a <c>Retry-After</c> is a rate
/// limit, not a bot check: a short wait (up to <see cref="MaxRetryWait"/>) is waited out and the page asked for once more, a
/// longer one is reported with the wait the site named, and the headless browser is not sent in while the limit stands — the
/// same address asking again within the second would only add to it. Requests to one host are spaced <see cref="HostInterval"/>
/// apart (<see cref="DefaultHostInterval"/> in the app, none unless set), so a turn that reads several pages from one site does
/// not arrive as a burst.
/// </para>
/// </summary>
public sealed class WebFetcher
{
    /// <summary>The ceiling on one leg (the HTTP request, or the browser run).</summary>
    public static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(30);

    /// <summary>How much of a page's body is read; the rest is cut and the result marked truncated.</summary>
    public const int MaxDownloadBytes = 5_000_000;

    /// <summary>The largest file <see cref="DownloadAsync"/> holds (2026-09-18; <c>download_file</c>'s too until 2026-10-01, when the <c>Web download max (MB)</c> setting took its place); over it the download is refused, never cut.</summary>
    public const long MaxFileDownloadBytes = 50_000_000;

    /// <summary>The ceiling on reading a file's body into memory once its headers arrived (<see cref="DownloadAsync"/>; the headers under <see cref="FetchTimeout"/> as every leg).</summary>
    public static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How long <see cref="SaveAsync"/> waits for the next byte before it drops the download (2026-10-01, the user's ask: the
    /// two-minute ceiling on the whole body went with the 50 MB cap, since a file of gigabytes takes longer than that on any link).
    /// </summary>
    public static readonly TimeSpan DefaultDownloadStallTimeout = TimeSpan.FromSeconds(60);

    /// <summary><see cref="DefaultDownloadStallTimeout"/>, shorter in a test.</summary>
    public TimeSpan DownloadStallTimeout { get; init; } = DefaultDownloadStallTimeout;

    public const int MaxRedirects = 10;

    /// <summary>The longest <c>Retry-After</c> a fetch waits out before asking once more; a longer one is reported, not waited.</summary>
    public static readonly TimeSpan MaxRetryWait = TimeSpan.FromSeconds(10);

    /// <summary>The gap between two requests to one host in the app (<see cref="WebAccess.Create"/>).</summary>
    public static readonly TimeSpan DefaultHostInterval = TimeSpan.FromSeconds(1);

    /// <summary>The gap between two requests to one host (the walk's first hop, a browser run); none unless set.</summary>
    public TimeSpan HostInterval { get; init; }

    /// <summary>How many hosts the pacing remembers before it drops those whose gap has passed.</summary>
    private const int MaxPacedHosts = 256;

    /// <summary>Under this many characters of readable text, a page full of scripts is a shell the browser should render.</summary>
    public const int ShellTextChars = 200;

    /// <summary>A challenge page is short; a long page that merely mentions a captcha is not one.</summary>
    public const int ChallengeTextChars = 1500;

    /// <summary>The log category of every web line.</summary>
    public const string Category = "Web";
    private readonly HttpClient _http;
    private readonly IHeadlessBrowser _browser;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve;
    private readonly CookieContainer? _cookies;
    private readonly TimeProvider _time;

    /// <summary>The cookie scopes (a cookie's <see cref="BrowserCookie.Domain"/>) the browser handed cookies for, and the browser that earned them: what the client claims there.</summary>
    private readonly ConcurrentDictionary<string, Earned> _earned = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every cookie <see cref="Adopt"/> put in the jar (later on 2026-10-13), so <see cref="ForgetAdopted"/> can take them out again; under its own lock.</summary>
    private readonly List<BrowserCookie> _adopted = [];

    /// <summary>The earliest each host may be asked again (<see cref="PaceAsync"/>), under <see cref="_paceGate"/>.</summary>
    private readonly Dictionary<string, DateTimeOffset> _nextSlot = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _paceGate = new();

    /// <param name="resolve">Name resolution for the LAN rule's pre-check; <c>Dns.GetHostAddressesAsync</c> in the app, a lookup in tests.</param>
    /// <param name="cookies">The jar <paramref name="http"/> sends from (<see cref="WebHttp.Create"/>), which the browser's cookies go into; null keeps them out.</param>
    /// <param name="time">The clock a <c>Retry-After</c> and the pacing wait on; the system's when null.</param>
    public WebFetcher(HttpClient http, IHeadlessBrowser browser, Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null, CookieContainer? cookies = null, TimeProvider? time = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _browser = browser ?? throw new ArgumentNullException(nameof(browser));
        _resolve = resolve ?? ((host, ct) => Dns.GetHostAddressesAsync(host, ct));
        _cookies = cookies;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// How long a <c>Retry-After</c> asks the client to wait at <paramref name="now"/>: its seconds, or the time to its date (none
    /// when that has passed); null without the header. Pure; pinned.
    /// </summary>
    public static TimeSpan? RetryAfterOf(System.Net.Http.Headers.RetryConditionHeaderValue? header, DateTimeOffset now) => header switch
    {
        { Delta: { } delta } => delta < TimeSpan.Zero ? TimeSpan.Zero : delta,
        { Date: { } date } => date > now ? date - now : TimeSpan.Zero,
        _ => null,
    };

    /// <summary>The <c>http</c> / <c>https</c> rule, applied to every URL and every redirect.</summary>
    public static bool IsHttp(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        return url.IsAbsoluteUri && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps);
    }

    /// <summary>
    /// What the model typed as a URL: an absolute http(s) URL as is, a bare <c>example.com/page</c>
    /// with <c>https://</c> put in front; null for anything else (a path, another scheme, blank).
    /// </summary>
    public static Uri? ParseUrl(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string trimmed = text.Trim().Trim('<', '>');
        if (trimmed.Length == 0)
        {
            return null;
        }

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var url) && IsHttp(url))
        {
            return url;
        }

        if (!trimmed.Contains("://", StringComparison.Ordinal) && !trimmed.StartsWith('/') && !trimmed.StartsWith('\\')
            && !(trimmed.Length > 1 && trimmed[1] == ':')
            && Uri.TryCreate("https://" + trimmed, UriKind.Absolute, out var https) && IsHttp(https) && https.Host.Contains('.'))
        {
            return https;
        }

        return null;
    }

    /// <summary>Whether a media type is a page.</summary>
    public static bool IsHtml(string mediaType) =>
        mediaType.Length == 0 || mediaType is "text/html" or "application/xhtml+xml";

    /// <summary>Whether a media type is text the model can read as it is.</summary>
    public static bool IsText(string mediaType) =>
        IsHtml(mediaType)
        || mediaType.StartsWith("text/", StringComparison.Ordinal)
        || mediaType is "application/json" or "application/xml" or "application/javascript" or "application/x-javascript" or "application/ld+json" or "application/rss+xml" or "application/atom+xml"
        || mediaType.EndsWith("+xml", StringComparison.Ordinal)
        || mediaType.EndsWith("+json", StringComparison.Ordinal);

    /// <summary>A bot gate's page: short, and naming itself. Pinned by tests.</summary>
    public static bool LooksLikeChallenge(string html, string readable)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(readable);
        if (readable.Length > ChallengeTextChars)
        {
            return false;
        }

        foreach (var marker in new[] { "Just a moment", "cf-chl", "_cf_chl", "captcha", "Checking your browser", "Attention Required", "Enable JavaScript and cookies to continue", "challenge-platform" })
        {
            if (html.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A single-page app's shell: scripts, a mount point, nothing readable. Pinned by tests.</summary>
    public static bool LooksLikeShell(string html, string readable)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(readable);
        if (readable.Length >= ShellTextChars || !html.Contains("<script", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var marker in new[] { "id=\"root\"", "id='root'", "id=\"app\"", "id='app'", "id=\"__next\"", "id=\"__nuxt\"", "ng-app", "data-reactroot", "<app-root", "id=\"main\"" })
        {
            if (html.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether a status is a refusal the browser might get past.</summary>
    public static bool IsBlockedStatus(int status) => status is 403 or 429 or 503;

    public async Task<FetchResult> FetchAsync(Uri url, FetchOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(options);
        if (!IsHttp(url))
        {
            return FetchResult.Fail(FetchOutcome.NotHttp, url.OriginalString, "");
        }

        if (await PreflightAsync(url.Host, options, cancellationToken).ConfigureAwait(false) is { } refused)
        {
            return FetchResult.Fail(refused, url.AbsoluteUri, url.Host);
        }

        switch (options.Engine)
        {
            case FetchEngine.HttpClient:
                return await PoliteHttpAsync(url, options, cancellationToken).ConfigureAwait(false);
            case FetchEngine.Chromium:
                return await BrowserAsync(url, options, cancellationToken).ConfigureAwait(false);
        }

        var first = await PoliteHttpAsync(url, options, cancellationToken).ConfigureAwait(false);
        bool shell = first.Ok && first.Page is not null && LooksLikeShell(first.Text, first.Page.Markdown);
        if (!(first.Outcome == FetchOutcome.Blocked || shell))
        {
            return first;
        }

        if (first.RetryAfter is not null)
        {
            DiagnosticLog.Info(Category, $"{url}: a rate limit, not a bot check; the headless browser would only add to it.");
            return first;
        }

        string why = shell ? "a script shell with nothing readable" : first.Detail;
        var executable = _browser.Locate(options.BrowserPath);
        if (executable is null)
        {
            DiagnosticLog.Info(Category, $"{url}: {why}; no headless browser to try instead.");
            return shell ? first : first with { Detail = first.Detail + WebText.NoBrowserToTry };
        }

        DiagnosticLog.Info(Category, $"{url}: {why}; trying the headless browser.");
        var second = await BrowserAsync(url, options, cancellationToken, executable).ConfigureAwait(false);
        if (second.Ok)
        {
            return second;
        }

        if (shell)
        {
            DiagnosticLog.Info(Category, $"{url}: the headless browser failed too ({second.Detail}); keeping the shell.");
            return first;
        }

        return first with { Detail = first.Detail + "; the headless browser failed too: " + second.Detail };
    }

    /// <summary>
    /// The pre-check ahead of any request, for the first host and every redirect: the outcome the
    /// network mode refuses <paramref name="host"/> with, or null to go on. A literal address or
    /// <c>localhost</c> is judged by spelling (2026-09-18: a public literal too, so no lookup), a name by its addresses (<see cref="LanPolicy.Judge"/>);
    /// an unresolvable name is not judged — the request itself will say so. Under <c>both</c> nothing
    /// is asked. The socket (<see cref="LanPolicy.ConnectAsync"/>) judges again on what it connects to. Public since 2026-10-03:
    /// <c>convert_to_pdf</c> asks it before the browser prints a web page, the one check that path gets.
    /// </summary>
    public async Task<FetchOutcome?> PreflightAsync(string host, FetchOptions options, CancellationToken cancellationToken)
    {
        if (options.Reach == NetworkReach.Both)
        {
            return null;
        }

        bool lan;
        if (LanPolicy.IsPrivateHost(host))
        {
            lan = true;
        }
        else if (IPAddress.TryParse(host.Trim('[', ']'), out _))
        {
            // A literal address that is not private: public by spelling, nothing to resolve.
            lan = false;
        }
        else
        {
            try
            {
                var addresses = await _resolve(host, cancellationToken).ConfigureAwait(false);
                lan = LanPolicy.AnyPrivate(addresses);
            }
            catch (SocketException)
            {
                return null;
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        FetchOutcome? refused = LanPolicy.Judge(options.Reach, lan) switch
        {
            NetworkRefusal.Lan => FetchOutcome.LanRefused,
            NetworkRefusal.Internet => FetchOutcome.InternetRefused,
            _ => null,
        };
        if (refused is { } outcome)
        {
            DiagnosticLog.Info(Category, RefusedLogLine(host, outcome, options.Reach));
        }

        return refused;
    }

    /// <summary>A host the network mode refused ahead of the request: <c>Refused 10.0.0.5: a LAN address under internet</c>. Pinned.</summary>
    public static string RefusedLogLine(string host, FetchOutcome outcome, NetworkReach reach) =>
        $"Refused {host}: {(outcome == FetchOutcome.LanRefused ? "a LAN address" : "a public address")} under {NetworkMode.Name(reach)}";

    /// <summary>Where the redirect walk arrived: the final response (headers read), its URL, and the hop's budget still running for the body.</summary>
    private sealed class Arrival(HttpResponseMessage response, Uri final, CancellationTokenSource budget) : IDisposable
    {
        public HttpResponseMessage Response { get; } = response;
        public Uri Final { get; } = final;
        public CancellationTokenSource Budget { get; } = budget;

        public void Dispose()
        {
            Response.Dispose();
            Budget.Dispose();
        }
    }

    /// <summary>
    /// The redirect walk both legs share (2026-09-18): GET after GET, each hop headed like Chrome
    /// (<see cref="BrowserHeaders"/>; since 2026-10-11, code review, every hop with the fetch's own <c>Referer</c> and
    /// <c>Sec-Fetch-Site</c> folded over the chain, <see cref="BrowserHeaders.SiteOf"/>), under its own <see cref="FetchTimeout"/> budget and judged by
    /// <see cref="PreflightAsync"/> before it is followed, up to <see cref="MaxRedirects"/>. The arrival
    /// is the caller's to dispose; a failure is the fetch result to answer with. Exceptions escape to
    /// the caller's <see cref="Failure"/>.
    /// </summary>
    private async Task<(Arrival? Arrived, FetchResult? Failed)> FollowAsync(Uri url, FetchOptions options, CancellationToken cancellationToken)
    {
        await PaceAsync(url.Host, cancellationToken).ConfigureAwait(false);
        var current = url;
        var chain = new List<Uri> { url };
        var claimed = HttpIdentity(options);
        for (int hop = 0; hop <= MaxRedirects; hop++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            HttpResponseMessage? response = null;
            try
            {
                budget.CancelAfter(FetchTimeout);
                using var request = new HttpRequestMessage(HttpMethod.Get, current);

                // Every hop comes from the page the fetch did (none when typed), its site folded over the chain, as Chrome's redirects do.
                BrowserHeaders.Apply(request, ClaimFor(current, claimed), options.Referer, options.Accept, BrowserHeaders.SiteOf(options.Referer, chain));

                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget.Token).ConfigureAwait(false);
                int status = (int)response.StatusCode;
                if (status is >= 300 and < 400 && response.Headers.Location is { } location)
                {
                    var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                    if (!IsHttp(next))
                    {
                        return (null, FetchResult.Fail(FetchOutcome.NotHttp, next.OriginalString, "", status));
                    }

                    if (await PreflightAsync(next.Host, options, cancellationToken).ConfigureAwait(false) is { } refused)
                    {
                        return (null, FetchResult.Fail(refused, url.AbsoluteUri, next.Host, status));
                    }

                    DiagnosticLog.Debug(Category, $"GET {current} → {status.ToString(CultureInfo.InvariantCulture)} to {next}");
                    current = next;
                    chain.Add(next);
                    continue;
                }

                var arrival = new Arrival(response, current, budget);
                response = null;
                budget = null;
                return (arrival, null);
            }
            finally
            {
                response?.Dispose();
                budget?.Dispose();
            }
        }

        return (null, FetchResult.Fail(FetchOutcome.Failed, url.AbsoluteUri, $"more than {MaxRedirects.ToString(CultureInfo.InvariantCulture)} redirects"));
    }

    /// <summary>Whether <paramref name="ex"/> is a failure the legs answer as a <see cref="FetchOutcome"/> (<see cref="Failure"/>); the caller's own cancellation never is.</summary>
    private static bool IsFetchFailure(Exception ex, CancellationToken cancellationToken) => ex switch
    {
        OperationCanceledException => !cancellationToken.IsCancellationRequested,
        NetworkRefusedException or HttpRequestException or IOException or InvalidOperationException or UriFormatException => true,
        _ => false,
    };

    /// <summary>The socket's own refusal (a name that resolved to the other side once connecting): <c>GET http://…: refused at the socket, 10.0.0.5 is a LAN address</c>. Pinned.</summary>
    public static string SocketRefusedLogLine(Uri current, string host, FetchOutcome outcome) =>
        $"GET {current}: refused at the socket, {host} is {(outcome == FetchOutcome.LanRefused ? "a LAN address" : "a public address")}";

    /// <summary>The outcome for an exception <see cref="IsFetchFailure"/> admitted, logged; <paramref name="ceiling"/> the budget a timeout ran out of.</summary>
    private static FetchResult Failure(Exception ex, Uri url, Uri current, TimeSpan ceiling)
    {
        switch (ex)
        {
            case NetworkRefusedException refused:
                DiagnosticLog.Info(Category, SocketRefusedLogLine(current, refused.Host, refused.Outcome));
                return FetchResult.Fail(refused.Outcome, url.AbsoluteUri, refused.Host);
            case HttpRequestException { InnerException: NetworkRefusedException refused }:
                // SocketsHttpHandler wraps what the connect callback threw.
                DiagnosticLog.Info(Category, SocketRefusedLogLine(current, refused.Host, refused.Outcome));
                return FetchResult.Fail(refused.Outcome, url.AbsoluteUri, refused.Host);
            case OperationCanceledException:
                DiagnosticLog.Info(Category, $"GET {current}: no answer within {Llm.LlmTimeouts.Format(ceiling)}.");
                return FetchResult.Fail(FetchOutcome.Timeout, url.AbsoluteUri, ceiling == FetchTimeout ? "" : WebText.Timeout(url.AbsoluteUri, ceiling));
            default:
                DiagnosticLog.Info(Category, $"GET {current} failed: {ex.Message}");
                return FetchResult.Fail(FetchOutcome.Failed, url.AbsoluteUri, ex.Message);
        }
    }

    /// <summary>
    /// The HTTP leg with a rate limit heeded (2026-10-11): an answer that names a <c>Retry-After</c> of at most <see cref="MaxRetryWait"/>
    /// is waited out on the clock and the page asked for once more; a longer one, or a second that names one again, is the answer,
    /// its detail saying how long the site asks to wait (<see cref="WebText.AskedToWait"/>). A second refused (429 / 503) with no wait
    /// named carries the first's, so the rate limit still keeps the headless browser out.
    /// </summary>
    private async Task<FetchResult> PoliteHttpAsync(Uri url, FetchOptions options, CancellationToken cancellationToken)
    {
        var first = await HttpAsync(url, options, cancellationToken).ConfigureAwait(false);
        if (first.RetryAfter is not { } wait)
        {
            return first;
        }

        string status = first.Status.ToString(CultureInfo.InvariantCulture);
        if (wait > MaxRetryWait)
        {
            DiagnosticLog.Info(Category, $"GET {url} → {status}, asked to wait {Llm.LlmTimeouts.Format(wait)}: longer than {Llm.LlmTimeouts.Format(MaxRetryWait)}, so not waited.");
            return first with { Detail = first.Detail + WebText.AskedToWait(wait) };
        }

        DiagnosticLog.Info(Category, $"GET {url} → {status}, asked to wait {Llm.LlmTimeouts.Format(wait)}; waiting, then asking once more.");
        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait, _time, cancellationToken).ConfigureAwait(false);
        }

        var second = await HttpAsync(url, options, cancellationToken).ConfigureAwait(false);
        if (second.RetryAfter is { } again)
        {
            return second with { Detail = second.Detail + WebText.AskedToWait(again) };
        }

        // Refused again with no wait named (code review, 2026-10-11): the limit still stands, so the first wait goes with it and the
        // headless browser stays out. A challenge page is a bot check, the browser's to try.
        bool challenge = second.Page is not null && LooksLikeChallenge(second.Text, second.Page.Markdown);
        return second.Outcome == FetchOutcome.Blocked && second.Status is 429 or 503 && !challenge
            ? second with { RetryAfter = wait, Detail = second.Detail + WebText.AskedToWait(wait) }
            : second;
    }

    /// <summary>
    /// Waits for <paramref name="host"/>'s turn: <see cref="HostInterval"/> after the last request to it began (the walk's first hop
    /// or a browser run), reserved under the lock so two fetches at once queue rather than both going. Nothing when no interval is set.
    /// <para>Code review, 2026-10-11: a wait cancelled (ESC) gives its slot back when no later request has queued behind it, and the
    /// log names the wait alone (it read "asked -1 s ago" with two queued). <paramref name="wait"/> false only marks the host as asked
    /// now (the browser sent in right after the HTTP leg's own turn, which need not wait a second more).</para>
    /// </summary>
    private async Task PaceAsync(string host, CancellationToken cancellationToken, bool wait = true)
    {
        if (HostInterval <= TimeSpan.Zero)
        {
            return;
        }

        TimeSpan delay;
        DateTimeOffset slot, reserved;
        lock (_paceGate)
        {
            var now = _time.GetUtcNow();
            var next = _nextSlot.TryGetValue(host, out var n) && n > now ? n : now;
            slot = wait ? next : now;
            delay = slot - now;
            reserved = slot + HostInterval;
            if (reserved > next)
            {
                _nextSlot[host] = reserved;
            }

            if (_nextSlot.Count > MaxPacedHosts)
            {
                foreach (var gone in _nextSlot.Where(e => e.Value <= now).Select(e => e.Key).ToList())
                {
                    _nextSlot.Remove(gone);
                }
            }
        }

        if (delay <= TimeSpan.Zero)
        {
            return;
        }

        DiagnosticLog.Debug(Category, $"{host}: its next turn is in {Llm.LlmTimeouts.Format(delay)}; waiting.");
        try
        {
            await Task.Delay(delay, _time, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            lock (_paceGate)
            {
                if (_nextSlot.TryGetValue(host, out var last) && last == reserved)
                {
                    _nextSlot[host] = slot;
                }
            }

            throw;
        }
    }

    private async Task<FetchResult> HttpAsync(Uri url, FetchOptions options, CancellationToken cancellationToken)
    {
        var current = url;
        var watch = Stopwatch.StartNew();
        try
        {
            var (arrived, failed) = await FollowAsync(url, options, cancellationToken).ConfigureAwait(false);
            if (failed is not null)
            {
                return failed;
            }

            using var arrival = arrived!;
            var response = arrival.Response;
            current = arrival.Final;
            int status = (int)response.StatusCode;
            var contentType = response.Content.Headers.ContentType;
            string mediaType = contentType?.MediaType?.Trim().ToLowerInvariant() ?? "";
            if (!IsText(mediaType))
            {
                long size = response.Content.Headers.ContentLength ?? 0;
                DiagnosticLog.Info(Category, $"GET {current} → {status.ToString(CultureInfo.InvariantCulture)} {mediaType} ({size.ToString(CultureInfo.InvariantCulture)} B), not text.");
                return FetchResult.Fail(FetchOutcome.Binary, url.AbsoluteUri, WebText.Binary(url.AbsoluteUri, mediaType, size), status);
            }

            var (bytes, truncated) = await ReadAsync(response, MaxDownloadBytes, arrival.Budget.Token).ConfigureAwait(false);
            string text = PageCharset.Decode(bytes, contentType?.CharSet);
            DiagnosticLog.Info(Category, $"GET {current} → {status.ToString(CultureInfo.InvariantCulture)} {(mediaType.Length == 0 ? "(no type)" : mediaType)}, {bytes.Length.ToString(CultureInfo.InvariantCulture)} B{(truncated ? " (cut)" : "")} in {watch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture)} ms.");
            PageText? page = IsHtml(mediaType) ? HtmlToMarkdown.Convert(text, current) : null;
            if (status >= 400)
            {
                string detail = $"HTTP {status.ToString(CultureInfo.InvariantCulture)} {response.ReasonPhrase}".TrimEnd();
                var outcome = IsBlockedStatus(status) ? FetchOutcome.Blocked : FetchOutcome.Failed;

                // A rate limit names its wait; a bot check that does (a 503 challenge page) is still a bot check, the browser's to try.
                bool challenge = page is not null && LooksLikeChallenge(text, page.Markdown);
                var retryAfter = status is 429 or 503 && !challenge ? RetryAfterOf(response.Headers.RetryAfter, _time.GetUtcNow()) : null;
                return new FetchResult(outcome, url.AbsoluteUri, current.AbsoluteUri, status, mediaType, page, text, WebText.HttpEngine, detail, truncated, retryAfter);
            }

            if (page is not null && LooksLikeChallenge(text, page.Markdown))
            {
                return new FetchResult(FetchOutcome.Blocked, url.AbsoluteUri, current.AbsoluteUri, status, mediaType, page, text, WebText.HttpEngine, "a bot challenge page", truncated);
            }

            return new FetchResult(FetchOutcome.Ok, url.AbsoluteUri, current.AbsoluteUri, status, mediaType, page, text, WebText.HttpEngine, "", truncated);
        }
        catch (Exception ex) when (IsFetchFailure(ex, cancellationToken))
        {
            return Failure(ex, url, current, FetchTimeout);
        }
    }

    /// <summary>
    /// The download behind <c>SkillHub</c>'s files (<c>download_file</c>'s until 2026-10-01, when it went to
    /// <see cref="OpenDownloadAsync"/> and <see cref="SaveAsync"/>, streamed to disk): the head as <see cref="OpenDownloadAsync"/>
    /// reads it under <see cref="MaxFileDownloadBytes"/>, then the body whole in memory, whatever its media type — a body that
    /// outgrows the cap is refused after (<see cref="FetchOutcome.TooBig"/>; a cut file is a broken file). The body is read
    /// under <see cref="DownloadTimeout"/>. No page cache is consulted.
    /// </summary>
    public async Task<DownloadResult> DownloadAsync(Uri url, FetchOptions options, CancellationToken cancellationToken)
    {
        using var opened = await OpenDownloadAsync(url, options, MaxFileDownloadBytes, cancellationToken).ConfigureAwait(false);
        if (!opened.Ok)
        {
            return new DownloadResult(opened.Outcome, opened.Url, opened.FinalUrl, opened.Status, opened.MediaType, opened.FileName, [], opened.Detail);
        }

        var current = new Uri(opened.FinalUrl);
        var watch = Stopwatch.StartNew();
        try
        {
            using var reading = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            reading.CancelAfter(DownloadTimeout);
            var (bytes, truncated) = await ReadAsync(opened.Response, MaxFileDownloadBytes, reading.Token).ConfigureAwait(false);
            if (truncated)
            {
                DiagnosticLog.Info(Category, $"GET {current} → {opened.Status.ToString(CultureInfo.InvariantCulture)} {opened.MediaType}: the body outgrew the cap.");
                return DownloadResult.Fail(FetchOutcome.TooBig, url.AbsoluteUri, WebText.TooBig(url.AbsoluteUri, opened.Declared, MaxFileDownloadBytes), opened.Status);
            }

            DiagnosticLog.Info(Category, $"GET {current} → {opened.Status.ToString(CultureInfo.InvariantCulture)} {(opened.MediaType.Length == 0 ? "(no type)" : opened.MediaType)}, {bytes.Length.ToString(CultureInfo.InvariantCulture)} B downloaded in {watch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture)} ms.");
            return new DownloadResult(FetchOutcome.Ok, url.AbsoluteUri, current.AbsoluteUri, opened.Status, opened.MediaType, opened.FileName, bytes, "");
        }
        catch (Exception ex) when (IsFetchFailure(ex, cancellationToken))
        {
            var failed = Failure(ex, url, current, DownloadTimeout);
            return new DownloadResult(failed.Outcome, failed.Url, failed.FinalUrl, failed.Status, "", "", [], failed.Detail);
        }
    }

    /// <summary>
    /// A download's head (2026-10-01, split from <see cref="DownloadAsync"/> when <c>download_file</c> went to disk): the HTTP
    /// leg's walk (<see cref="FollowAsync"/>, the LAN rule at every hop and at the socket) to the final answer, its headers
    /// read and its body not yet — a 4xx / 5xx answer is <see cref="FetchOutcome.Blocked"/> or <see cref="FetchOutcome.Failed"/>
    /// as a page's is, a <c>Content-Length</c> over <paramref name="maxBytes"/> is refused unread
    /// (<see cref="FetchOutcome.TooBig"/>). The headless browser is never tried (it dumps a DOM, it hands back no bytes). An
    /// open one holds the response until disposed; <see cref="SaveAsync"/> streams its body.
    /// </summary>
    public async Task<OpenedDownload> OpenDownloadAsync(Uri url, FetchOptions options, long maxBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(options);
        if (!IsHttp(url))
        {
            return OpenedDownload.Fail(FetchOutcome.NotHttp, url.OriginalString, "");
        }

        if (await PreflightAsync(url.Host, options, cancellationToken).ConfigureAwait(false) is { } refused)
        {
            return OpenedDownload.Fail(refused, url.AbsoluteUri, url.Host);
        }

        var current = url;
        try
        {
            var (arrived, failed) = await FollowAsync(url, options, cancellationToken).ConfigureAwait(false);
            if (failed is not null)
            {
                return new OpenedDownload(failed.Outcome, failed.Url, failed.FinalUrl, failed.Status, "", "", 0, failed.Detail);
            }

            var arrival = arrived!;
            bool kept = false;
            try
            {
                var response = arrival.Response;
                current = arrival.Final;
                int status = (int)response.StatusCode;
                string mediaType = response.Content.Headers.ContentType?.MediaType?.Trim().ToLowerInvariant() ?? "";
                string fileName = FileNameOf(response);
                if (status >= 400)
                {
                    string detail = $"HTTP {status.ToString(CultureInfo.InvariantCulture)} {response.ReasonPhrase}".TrimEnd();
                    DiagnosticLog.Info(Category, $"GET {current} → {detail}.");
                    var outcome = IsBlockedStatus(status) ? FetchOutcome.Blocked : FetchOutcome.Failed;
                    var retryAfter = status is 429 or 503 ? RetryAfterOf(response.Headers.RetryAfter, _time.GetUtcNow()) : null;
                    return new OpenedDownload(outcome, url.AbsoluteUri, current.AbsoluteUri, status, mediaType, fileName, 0, detail) { RetryAfter = retryAfter };
                }

                long declared = response.Content.Headers.ContentLength ?? 0;
                if (declared > maxBytes)
                {
                    DiagnosticLog.Info(Category, $"GET {current} → {status.ToString(CultureInfo.InvariantCulture)} {mediaType}, {declared.ToString(CultureInfo.InvariantCulture)} B declared: over the cap.");
                    return OpenedDownload.Fail(FetchOutcome.TooBig, url.AbsoluteUri, WebText.TooBig(url.AbsoluteUri, declared, maxBytes), status);
                }

                // The hop's budget timed the headers; the body has its own clock (DownloadTimeout, or SaveAsync's stall).
                arrival.Budget.CancelAfter(Timeout.InfiniteTimeSpan);
                kept = true;
                return new OpenedDownload(FetchOutcome.Ok, url.AbsoluteUri, current.AbsoluteUri, status, mediaType, fileName, declared, "", response, arrival);
            }
            finally
            {
                if (!kept)
                {
                    arrival.Dispose();
                }
            }
        }
        catch (Exception ex) when (IsFetchFailure(ex, cancellationToken))
        {
            var failed = Failure(ex, url, current, FetchTimeout);
            return new OpenedDownload(failed.Outcome, failed.Url, failed.FinalUrl, failed.Status, "", "", 0, failed.Detail);
        }
    }

    /// <summary>
    /// <paramref name="opened"/>'s body copied into <paramref name="target"/> (2026-10-01, <c>download_file</c> streamed to disk,
    /// the user's ask): a chunk at a time, never held whole, so the cap (<paramref name="maxBytes"/>, the <c>Web download max
    /// (MB)</c> setting) is disk, not memory. A body that outgrows it stops there (<see cref="FetchOutcome.TooBig"/>; the caller
    /// drops what was written). No total ceiling: a read that waits <see cref="DownloadStallTimeout"/> for a byte ends it
    /// (<see cref="FetchOutcome.Timeout"/>, <see cref="WebText.Stalled"/>), so a big file on a slow link finishes and a dead one
    /// does not hang the turn. The caller's cancel (ESC) escapes.
    /// </summary>
    public async Task<SavedBody> SaveAsync(OpenedDownload opened, Stream target, long maxBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(opened);
        ArgumentNullException.ThrowIfNull(target);
        var current = new Uri(opened.FinalUrl);
        var watch = Stopwatch.StartNew();
        long total = 0;
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            stall.CancelAfter(DownloadStallTimeout);
            using var body = await opened.Response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
            var chunk = new byte[64 * 1024];
            while (true)
            {
                stall.CancelAfter(DownloadStallTimeout);
                int read = await body.ReadAsync(chunk, stall.Token).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                // The disk's time is no stall of the server's.
                stall.CancelAfter(Timeout.InfiniteTimeSpan);
                total += read;
                if (total > maxBytes)
                {
                    DiagnosticLog.Info(Category, $"GET {current} → {opened.Status.ToString(CultureInfo.InvariantCulture)} {opened.MediaType}: the body outgrew the cap.");
                    return new SavedBody(FetchOutcome.TooBig, opened.Url, 0, WebText.TooBig(opened.Url, 0, maxBytes));
                }

                await target.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            await target.FlushAsync(cancellationToken).ConfigureAwait(false);
            DiagnosticLog.Info(Category, $"GET {current} → {opened.Status.ToString(CultureInfo.InvariantCulture)} {(opened.MediaType.Length == 0 ? "(no type)" : opened.MediaType)}, {total.ToString(CultureInfo.InvariantCulture)} B saved in {watch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture)} ms.");
            return new SavedBody(FetchOutcome.Ok, opened.Url, total, "");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            DiagnosticLog.Info(Category, $"GET {current}: nothing for {Llm.LlmTimeouts.Format(DownloadStallTimeout)} after {total.ToString(CultureInfo.InvariantCulture)} B; dropped.");
            return new SavedBody(FetchOutcome.Timeout, opened.Url, 0, WebText.Stalled(opened.Url, DownloadStallTimeout));
        }
        catch (Exception ex) when (IsFetchFailure(ex, cancellationToken))
        {
            var failed = Failure(ex, new Uri(opened.Url), current, DownloadStallTimeout);
            return new SavedBody(failed.Outcome, opened.Url, 0, failed.Detail);
        }
    }

    /// <summary>The file name a <c>Content-Disposition</c> header offers (<c>filename*</c> first, then <c>filename</c>, quotes and any path stripped), <c>""</c> without one. Pinned.</summary>
    public static string FileNameOf(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var disposition = response.Content.Headers.ContentDisposition;
        string? name = disposition?.FileNameStar ?? disposition?.FileName;
        if (string.IsNullOrWhiteSpace(name))
        {
            return "";
        }

        name = name.Trim().Trim('"').Trim();
        int cut = name.LastIndexOfAny(['/', '\\']);
        return cut >= 0 ? name[(cut + 1)..] : name;
    }

    /// <summary>
    /// The browser the HTTP client claims (2026-10-11): Chrome at the major version of the browser the headless leg would run —
    /// Edge and Brave share Chromium's numbering — else <see cref="BrowserIdentity.Fallback"/>. Chrome, whichever is installed:
    /// the commonest brand, and the HTTP leg's TLS fingerprint is no browser's anyway.
    /// </summary>
    private BrowserIdentity HttpIdentity(FetchOptions options) => _browser.Identify(options.BrowserPath) with { Brand = BrowserBrand.Chrome };

    /// <summary>
    /// What the client claims on <paramref name="url"/>'s host: the browser that handed it cookies sent there (<see cref="Adopt"/>; the
    /// narrowest such scope's), else <paramref name="claimed"/>. A scope whose cookies have all expired is dropped on the way.
    /// </summary>
    private BrowserIdentity ClaimFor(Uri url, BrowserIdentity claimed)
    {
        var now = _time.GetUtcNow();
        Earned? best = null;
        foreach (var entry in _earned)
        {
            var earned = entry.Value;
            if (earned.Until is { } until && until <= now)
            {
                _earned.TryRemove(entry);
                continue;
            }

            if (earned.Scope.AppliesTo(url.Host) && (best is null || earned.Scope.Domain.Length > best.Scope.Domain.Length))
            {
                best = earned;
            }
        }

        return best?.Identity ?? claimed;
    }

    /// <summary>
    /// The browser's cookies from a page it read (<see cref="BrowserDump.Cookies"/>, already those for the host asked for and the one the
    /// page ended on, <see cref="HeadlessBrowser"/>) put into the client's jar, and each scope the jar took one for — a host-only
    /// cookie's host, a domain cookie's domain and every host under it — marked with the browser that earned it (<see cref="ClaimFor"/>)
    /// until its last cookie there expires (a session cookie's, for the run).
    /// <para>Code review, 2026-10-11: the claim was by exact host and for ever, from every cookie handed over rather than those the jar
    /// took — a <c>.example.com</c> clearance went to <c>shop.example.com</c> under Chrome's name, and a host whose only cookie the jar
    /// refused was claimed as Edge with no clearance.</para>
    /// </summary>
    private void Adopt(BrowserDump dump, Uri url, FetchOptions options)
    {
        if (_cookies is not null && dump.Cookies is { Count: > 0 } cookies)
        {
            Adopt(cookies, _browser.Identify(options.BrowserPath), url);
        }
    }

    /// <summary>
    /// <paramref name="cookies"/> a browser earned on <paramref name="url"/> put into the client's jar, each scope the jar took one for
    /// marked with <paramref name="identity"/>, the browser that earned it (<see cref="ClaimFor"/>): the headless browser's
    /// (<see cref="BrowserAsync"/>), or since 2026-10-13 the Browser window's after a person solved a bot check there, or after
    /// <c>/browser</c> closed (<see cref="BotCheck"/>). Returns how many the jar took; none without a jar.
    /// </summary>
    public int Adopt(IReadOnlyList<BrowserCookie>? cookies, BrowserIdentity identity, Uri url)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(url);
        if (_cookies is null || cookies is not { Count: > 0 })
        {
            return 0;
        }

        var taken = cookies.Where(c => c.AddTo(_cookies)).ToList();
        if (taken.Count == 0)
        {
            return 0;
        }

        lock (_adopted)
        {
            _adopted.AddRange(taken);
        }

        var scopes = taken.GroupBy(c => c.Domain.ToLowerInvariant()).ToList();
        foreach (var scope in scopes)
        {
            DateTimeOffset? until = scope.Any(c => c.Expires <= 0) ? null : DateTimeOffset.UnixEpoch.AddSeconds(scope.Max(c => c.Expires));
            _earned[scope.Key] = new Earned(scope.First(), identity, until);
        }

        DiagnosticLog.Info(Category, $"{url}: {taken.Count.ToString(CultureInfo.InvariantCulture)} of the browser's cookies handed to the HTTP client, which claims {identity.BrandName} {identity.Major.ToString(CultureInfo.InvariantCulture)} on {string.Join(", ", scopes.Select(s => s.Key))} while they last.");
        return taken.Count;
    }

    /// <summary>
    /// The Browser window's cookies taken back out of the jar (later on 2026-10-13: <c>/browser clear all</c>, and a profile switch, the
    /// window's data being each profile's own): every cookie <see cref="Adopt"/> put there expired, and every Edge claim it earned
    /// dropped, so the client goes back to claiming the headless browser's brand there. Cookies from plain fetches stay. Returns how
    /// many cookies went.
    /// </summary>
    public int ForgetAdopted()
    {
        BrowserCookie[] adopted;
        lock (_adopted)
        {
            adopted = [.. _adopted];
            _adopted.Clear();
        }

        _earned.Clear();
        if (_cookies is null || adopted.Length == 0)
        {
            return 0;
        }

        int gone = 0;
        foreach (Cookie cookie in _cookies.GetAllCookies())
        {
            if (!cookie.Expired && adopted.Any(a => string.Equals(a.Name, cookie.Name, StringComparison.Ordinal)
                && string.Equals(a.Domain.TrimStart('.'), cookie.Domain.TrimStart('.'), StringComparison.OrdinalIgnoreCase)
                && string.Equals(a.Path.StartsWith('/') ? a.Path : "/", cookie.Path, StringComparison.Ordinal)))
            {
                cookie.Expired = true;
                gone++;
            }
        }

        DiagnosticLog.Info(Category, $"{gone.ToString(CultureInfo.InvariantCulture)} of the Browser window's cookies taken back out of the HTTP client.");
        return gone;
    }

    /// <summary>
    /// The page a person let through a bot check in the Browser window (2026-10-13, <see cref="BotCheck"/>) as a fetch's answer: its
    /// cookies adopted (<see cref="Adopt(IReadOnlyList{BrowserCookie}?, BrowserIdentity, Uri)"/>), so the next fetch from that site goes
    /// through the HTTP client, and its DOM converted as the headless browser's is, engine <see cref="WebText.WindowEngine"/>.
    /// </summary>
    public FetchResult FromWindow(Uri url, BotCheckResult solved)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(solved);
        string html = solved.Html ?? "";
        var final = Uri.TryCreate(solved.FinalUrl, UriKind.Absolute, out var ended) && IsHttp(ended) ? ended : url;
        Adopt(solved.Cookies, solved.Identity, final);
        var page = HtmlToMarkdown.Convert(html, final);
        DiagnosticLog.Info(Category, $"{url}: {html.Length.ToString(CultureInfo.InvariantCulture)} chars of DOM from the Browser window, past its bot check.");
        return new FetchResult(FetchOutcome.Ok, url.AbsoluteUri, final.AbsoluteUri, solved.Status > 0 ? solved.Status : 200, "text/html", page, html, WebText.WindowEngine, "", solved.Truncated);
    }

    /// <summary>
    /// A refused fetch shown to a person (2026-10-13, <see cref="BotCheck"/>): <paramref name="solver"/> asked to have
    /// <paramref name="url"/> through its bot check in the Browser window. Cleared, and the address it ended on still within the network
    /// mode (<see cref="PreflightAsync"/>), the window's page is the answer (<see cref="FromWindow"/>); any other ending is
    /// <paramref name="refused"/> with what happened added to its detail (<see cref="BotCheckText.For"/>); no one to ask (null) leaves
    /// it as it was.
    /// </summary>
    public async Task<FetchResult> PastBotCheckAsync(Uri url, FetchResult refused, FetchOptions options, BotCheckSolver solver, BotCheckUse use, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(refused);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(solver);
        var solved = await solver(new BotCheckRequest(url, use, BotCheck.WhyOf(refused.Detail)), cancellationToken).ConfigureAwait(false);
        if (solved is null)
        {
            return refused;
        }

        if (!solved.Cleared)
        {
            return refused with { Detail = refused.Detail + BotCheckText.For(solved) };
        }

        if (await LeftTheReachAsync(url, solved.FinalUrl, options, cancellationToken).ConfigureAwait(false))
        {
            return refused with { Detail = refused.Detail + BotCheckText.LeftTheReach(solved.FinalUrl) };
        }

        return FromWindow(url, solved);
    }

    /// <summary>Whether the address a bot check ended on is off a host the network mode lets the fetch reach (a redirect onto the LAN under <c>internet</c>, say).</summary>
    public async Task<bool> LeftTheReachAsync(Uri url, string finalUrl, FetchOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(options);
        return Uri.TryCreate(finalUrl, UriKind.Absolute, out var final) && IsHttp(final)
            && !string.Equals(final.Host, url.Host, StringComparison.OrdinalIgnoreCase)
            && await PreflightAsync(final.Host, options, cancellationToken).ConfigureAwait(false) is not null;
    }

    /// <summary>A scope the browser's cookies were taken for (one of them, for <see cref="BrowserCookie.AppliesTo"/>), the browser, and when the last of them expires (null: a session cookie).</summary>
    private sealed record Earned(BrowserCookie Scope, BrowserIdentity Identity, DateTimeOffset? Until);

    /// <param name="executable">The browser the default engine's fallback found, right after the HTTP leg; that leg took the host's
    /// turn, so this run only marks it (code review, 2026-10-11: it waited up to a second more for nothing). Null: located here, paced.</param>
    private async Task<FetchResult> BrowserAsync(Uri url, FetchOptions options, CancellationToken cancellationToken, string? executable = null)
    {
        bool fallback = executable is not null;
        executable ??= _browser.Locate(options.BrowserPath);
        if (executable is null)
        {
            return FetchResult.Fail(FetchOutcome.NoBrowser, url.AbsoluteUri, "", engine: WebText.BrowserEngine);
        }

        await PaceAsync(url.Host, cancellationToken, wait: !fallback).ConfigureAwait(false);
        var watch = Stopwatch.StartNew();
        var dump = await _browser.DumpDomAsync(executable, url, cancellationToken).ConfigureAwait(false);
        if (dump.Html is null)
        {
            DiagnosticLog.Info(Category, $"{Path.GetFileName(executable)} {url}: {dump.Detail}");
            return FetchResult.Fail(FetchOutcome.BrowserFailed, url.AbsoluteUri, dump.Detail, engine: WebText.BrowserEngine);
        }

        var page = HtmlToMarkdown.Convert(dump.Html, url);
        DiagnosticLog.Info(Category, $"{Path.GetFileName(executable)} {url}: {dump.Html.Length.ToString(CultureInfo.InvariantCulture)} chars of DOM in {watch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture)} ms.");
        if (LooksLikeChallenge(dump.Html, page.Markdown))
        {
            return new FetchResult(FetchOutcome.Blocked, url.AbsoluteUri, url.AbsoluteUri, 200, "text/html", page, dump.Html, WebText.BrowserEngine, "the headless browser got a bot challenge page", false);
        }

        Adopt(dump, url, options);
        return new FetchResult(FetchOutcome.Ok, url.AbsoluteUri, url.AbsoluteUri, 200, "text/html", page, dump.Html, WebText.BrowserEngine, "", false);
    }

    /// <summary>The body up to <paramref name="cap"/> bytes (<see cref="MaxDownloadBytes"/> for a page, <see cref="MaxFileDownloadBytes"/> for a file), and whether it was cut there.</summary>
    private static async Task<(byte[] Bytes, bool Truncated)> ReadAsync(HttpResponseMessage response, long cap, CancellationToken cancellationToken)
    {
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        while (buffer.Length < cap)
        {
            int wanted = (int)Math.Min(chunk.Length, cap - buffer.Length);
            int read = await stream.ReadAsync(chunk.AsMemory(0, wanted), cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                return (buffer.ToArray(), false);
            }

            buffer.Write(chunk, 0, read);
        }

        return (buffer.ToArray(), true);
    }
}
