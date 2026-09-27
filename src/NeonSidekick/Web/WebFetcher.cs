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
public sealed record FetchResult(FetchOutcome Outcome, string Url, string FinalUrl, int Status, string MediaType, PageText? Page, string Text, string Engine, string Detail, bool Truncated)
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
/// The fetch behind <c>web_fetch</c> and the search engines' pages: an HTTP client that looks like
/// Chrome (<see cref="BrowserHeaders"/>), redirects followed by hand so every hop is judged by the
/// LAN rule and re-headed, the body read to <see cref="MaxDownloadBytes"/> and decoded by
/// <see cref="PageCharset"/>; and, per the mode, the headless browser (<see cref="IHeadlessBrowser"/>)
/// when the client's answer was a refusal (403 / 429 / 503, a challenge page) or a script shell with
/// nothing readable. Every failure is a <see cref="FetchOutcome"/> with a detail, never an exception;
/// only cancellation escapes.
/// </summary>
public sealed class WebFetcher
{
    /// <summary>The ceiling on one leg (the HTTP request, or the browser run).</summary>
    public static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(30);

    /// <summary>How much of a page's body is read; the rest is cut and the result marked truncated.</summary>
    public const int MaxDownloadBytes = 5_000_000;

    /// <summary>The largest file <see cref="DownloadAsync"/> saves (2026-09-18); over it the download is refused, never cut.</summary>
    public const long MaxFileDownloadBytes = 50_000_000;

    /// <summary>The ceiling on reading a file's body once its headers arrived (the headers under <see cref="FetchTimeout"/> as every leg).</summary>
    public static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(2);

    public const int MaxRedirects = 10;

    /// <summary>Under this many characters of readable text, a page full of scripts is a shell the browser should render.</summary>
    public const int ShellTextChars = 200;

    /// <summary>A challenge page is short; a long page that merely mentions a captcha is not one.</summary>
    public const int ChallengeTextChars = 1500;

    /// <summary>The log category of every web line.</summary>
    public const string Category = "Web";
    private readonly HttpClient _http;
    private readonly IHeadlessBrowser _browser;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve;

    /// <param name="resolve">Name resolution for the LAN rule's pre-check; <c>Dns.GetHostAddressesAsync</c> in the app, a lookup in tests.</param>
    public WebFetcher(HttpClient http, IHeadlessBrowser browser, Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _browser = browser ?? throw new ArgumentNullException(nameof(browser));
        _resolve = resolve ?? ((host, ct) => Dns.GetHostAddressesAsync(host, ct));
    }

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

        if (await RefusalAsync(url.Host, options, cancellationToken).ConfigureAwait(false) is { } refused)
        {
            return FetchResult.Fail(refused, url.AbsoluteUri, url.Host);
        }

        switch (options.Engine)
        {
            case FetchEngine.HttpClient:
                return await HttpAsync(url, options, cancellationToken).ConfigureAwait(false);
            case FetchEngine.Chromium:
                return await BrowserAsync(url, options, cancellationToken).ConfigureAwait(false);
        }

        var first = await HttpAsync(url, options, cancellationToken).ConfigureAwait(false);
        bool shell = first.Ok && first.Page is not null && LooksLikeShell(first.Text, first.Page.Markdown);
        if (!(first.Outcome == FetchOutcome.Blocked || shell))
        {
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
    /// is asked. The socket (<see cref="LanPolicy.ConnectAsync"/>) judges again on what it connects to.
    /// </summary>
    private async Task<FetchOutcome?> RefusalAsync(string host, FetchOptions options, CancellationToken cancellationToken)
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
    /// (<see cref="BrowserHeaders"/>), under its own <see cref="FetchTimeout"/> budget and judged by
    /// <see cref="RefusalAsync"/> before it is followed, up to <see cref="MaxRedirects"/>. The arrival
    /// is the caller's to dispose; a failure is the fetch result to answer with. Exceptions escape to
    /// the caller's <see cref="Failure"/>.
    /// </summary>
    private async Task<(Arrival? Arrived, FetchResult? Failed)> FollowAsync(Uri url, FetchOptions options, CancellationToken cancellationToken)
    {
        var current = url;
        var referer = options.Referer;
        for (int hop = 0; hop <= MaxRedirects; hop++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            HttpResponseMessage? response = null;
            try
            {
                budget.CancelAfter(FetchTimeout);
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                BrowserHeaders.Apply(request, referer);
                if (options.Accept is { } accept)
                {
                    request.Headers.Remove("Accept");
                    request.Headers.TryAddWithoutValidation("Accept", accept);
                }

                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget.Token).ConfigureAwait(false);
                int status = (int)response.StatusCode;
                if (status is >= 300 and < 400 && response.Headers.Location is { } location)
                {
                    var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                    if (!IsHttp(next))
                    {
                        return (null, FetchResult.Fail(FetchOutcome.NotHttp, next.OriginalString, "", status));
                    }

                    if (await RefusalAsync(next.Host, options, cancellationToken).ConfigureAwait(false) is { } refused)
                    {
                        return (null, FetchResult.Fail(refused, url.AbsoluteUri, next.Host, status));
                    }

                    DiagnosticLog.Debug(Category, $"GET {current} → {status.ToString(CultureInfo.InvariantCulture)} to {next}");
                    referer = current;
                    current = next;
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
                return new FetchResult(outcome, url.AbsoluteUri, current.AbsoluteUri, status, mediaType, page, text, WebText.HttpEngine, detail, truncated);
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
    /// The download behind <c>download_file</c> (2026-09-18): the HTTP leg's walk (<see cref="FollowAsync"/>,
    /// the LAN rule at every hop and at the socket) and the body whole, whatever its media type, up
    /// to <see cref="MaxFileDownloadBytes"/> — a <c>Content-Length</c> over it is refused unread, a
    /// body that outgrows it is refused after (<see cref="FetchOutcome.TooBig"/>; a cut file is a
    /// broken file). The body is read under <see cref="DownloadTimeout"/>. The headless browser is
    /// never tried (it dumps a DOM, it hands back no bytes) and no page cache is consulted. A 4xx / 5xx
    /// answer is <see cref="FetchOutcome.Blocked"/> or <see cref="FetchOutcome.Failed"/> as a page's is.
    /// </summary>
    public async Task<DownloadResult> DownloadAsync(Uri url, FetchOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(options);
        if (!IsHttp(url))
        {
            return DownloadResult.Fail(FetchOutcome.NotHttp, url.OriginalString, "");
        }

        if (await RefusalAsync(url.Host, options, cancellationToken).ConfigureAwait(false) is { } refused)
        {
            return DownloadResult.Fail(refused, url.AbsoluteUri, url.Host);
        }

        var current = url;
        var ceiling = FetchTimeout;
        var watch = Stopwatch.StartNew();
        try
        {
            var (arrived, failed) = await FollowAsync(url, options, cancellationToken).ConfigureAwait(false);
            if (failed is not null)
            {
                return new DownloadResult(failed.Outcome, failed.Url, failed.FinalUrl, failed.Status, "", "", [], failed.Detail);
            }

            using var arrival = arrived!;
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
                return new DownloadResult(outcome, url.AbsoluteUri, current.AbsoluteUri, status, mediaType, fileName, [], detail);
            }

            long declared = response.Content.Headers.ContentLength ?? 0;
            if (declared > MaxFileDownloadBytes)
            {
                DiagnosticLog.Info(Category, $"GET {current} → {status.ToString(CultureInfo.InvariantCulture)} {mediaType}, {declared.ToString(CultureInfo.InvariantCulture)} B declared: over the cap.");
                return DownloadResult.Fail(FetchOutcome.TooBig, url.AbsoluteUri, WebText.TooBig(url.AbsoluteUri, declared, MaxFileDownloadBytes), status);
            }

            ceiling = DownloadTimeout;
            using var reading = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            reading.CancelAfter(DownloadTimeout);
            var (bytes, truncated) = await ReadAsync(response, MaxFileDownloadBytes, reading.Token).ConfigureAwait(false);
            if (truncated)
            {
                DiagnosticLog.Info(Category, $"GET {current} → {status.ToString(CultureInfo.InvariantCulture)} {mediaType}: the body outgrew the cap.");
                return DownloadResult.Fail(FetchOutcome.TooBig, url.AbsoluteUri, WebText.TooBig(url.AbsoluteUri, declared, MaxFileDownloadBytes), status);
            }

            DiagnosticLog.Info(Category, $"GET {current} → {status.ToString(CultureInfo.InvariantCulture)} {(mediaType.Length == 0 ? "(no type)" : mediaType)}, {bytes.Length.ToString(CultureInfo.InvariantCulture)} B downloaded in {watch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture)} ms.");
            return new DownloadResult(FetchOutcome.Ok, url.AbsoluteUri, current.AbsoluteUri, status, mediaType, fileName, bytes, "");
        }
        catch (Exception ex) when (IsFetchFailure(ex, cancellationToken))
        {
            var failed = Failure(ex, url, current, ceiling);
            return new DownloadResult(failed.Outcome, failed.Url, failed.FinalUrl, failed.Status, "", "", [], failed.Detail);
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

    private async Task<FetchResult> BrowserAsync(Uri url, FetchOptions options, CancellationToken cancellationToken, string? executable = null)
    {
        executable ??= _browser.Locate(options.BrowserPath);
        if (executable is null)
        {
            return FetchResult.Fail(FetchOutcome.NoBrowser, url.AbsoluteUri, "", engine: WebText.BrowserEngine);
        }

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
