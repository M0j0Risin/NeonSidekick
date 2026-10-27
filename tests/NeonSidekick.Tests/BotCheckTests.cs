using System.Net;
using System.Text;
using NeonSidekick.Files;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Pdf;
using NeonSidekick.Printing;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.Viewer;
using NeonSidekick.Web;

namespace NeonSidekick.Tests;

/// <summary>
/// The bot checks shown to the user in the Browser window (2026-10-13): the setting, which refusals offer the window
/// (<see cref="BotCheck"/>), the wording, the window's pure parts (<see cref="BrowserWatch"/>, <see cref="BrowserPage"/>,
/// <see cref="BrowserKeys"/>, <see cref="BrowserAnswers"/>), the fetcher's hand-off, and the four tools over a stub client, a fake
/// headless browser and a fake solver. The window itself is the smoke's (<c>browser:webview2</c>) and <c>WebViewTests</c>'.
/// </summary>
public sealed class BotCheckTests : IDisposable
{
    private const string Article = "<html><head><title>Example Domain</title></head><body><h1>Example Domain</h1><p>This domain is for use in illustrative examples in documents.</p></body></html>";
    private const string Cloudflare = "<html><head><title>Just a moment...</title></head><body><div id=\"cf-chl-widget\">Checking your browser before accessing the site.</div><script src=\"/cdn-cgi/challenge-platform/h/b/orchestrate/jsch/v1\"></script></body></html>";
    private const string DuckAnomaly = "<html><body><div class=\"anomaly-modal__title\">Unfortunately, bots use DuckDuckGo too.</div><form id=\"challenge-form\"></form></body></html>";
    private const string DuckResults = "<html><body><div class=\"result\"><a class=\"result__a\" href=\"https://example.com/\">Example Domain</a><a class=\"result__snippet\">For use in examples.</a></div></body></html>";

    private readonly StubHttpMessageHandler _http = new();
    private readonly FakeHeadlessBrowser _browser = new();
    private readonly ManualTimeProvider _time = new();
    private readonly AppSettingsData _settings = new() { WebTools = true, FileTools = true };
    private readonly CookieContainer _jar = new();
    private readonly WebAccess _web;
    private readonly List<BotCheckRequest> _asked = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly WorkingDirectory _files;
    private Func<BotCheckRequest, BotCheckResult?> _answer;

    public BotCheckTests()
    {
        _web = new WebAccess(new HttpClient(_http), _browser, _time, (host, _) => Task.FromResult(host switch
        {
            "example.com" or "shop.example.com" => new[] { IPAddress.Parse("93.184.216.34") },
            "nas.home" => [IPAddress.Parse("192.168.1.10")],
            _ => throw new System.Net.Sockets.SocketException(11001),
        }), _ => { }, _jar);
        _root = Path.Combine(_dir, "files");
        Directory.CreateDirectory(_root);
        _files = new WorkingDirectory(() => _root, _time);
        _answer = request => FakeBrowserWindow.Cleared(request, Article);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private Task<BotCheckResult?> Solve(BotCheckRequest request, CancellationToken cancellationToken)
    {
        _asked.Add(request);
        return Task.FromResult(_answer(request));
    }

    private static HttpResponseMessage Html(HttpStatusCode status, string body) => StubHttpMessageHandler.Json(status, body, "text/html");

    private static FetchResult Refused(int status, string text = "<p>denied</p>", TimeSpan? retryAfter = null, FetchOutcome outcome = FetchOutcome.Blocked) =>
        new(outcome, "https://example.com/", "https://example.com/", status, "text/html", HtmlToMarkdown.Convert(text, new Uri("https://example.com/")), text, WebText.HttpEngine, "HTTP " + status, false, retryAfter);

    // ── The setting ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("ask", BotcheckKind.Ask)]
    [InlineData(" Automatic ", BotcheckKind.Automatic)]
    [InlineData("disabled", BotcheckKind.Disabled)]
    [InlineData("always", BotcheckKind.Ask)]   // a hand-edited word: the default
    public void Setting_ResolvesTheSavedWord(string saved, BotcheckKind kind)
    {
        Assert.Equal(kind, BotcheckWindow.Resolve(new AppSettingsData { WebBrowserBotcheckWindow = saved }));
        Assert.Equal("ask", new AppSettingsData().WebBrowserBotcheckWindow);   // the user's default (2026-10-13)
        Assert.Equal(["ask", "automatic", "off"], BotcheckWindow.Names);
        Assert.Equal("automatic", BotcheckWindow.Name(BotcheckKind.Automatic));
        Assert.Equal("asks before opening the Browser window on a bot check", BotcheckWindow.Describe("ask"));
        Assert.Equal("opens the Browser window on a bot check at once", BotcheckWindow.Describe("automatic"));
        Assert.Equal("never opens it; the tool reports the refusal", BotcheckWindow.Describe("off"));
        Assert.Equal("", BotcheckWindow.Describe("nope"));
    }

    // ── What offers the window ───────────────────────────────────────────────

    [Fact]
    public void Offers_ABotCheck_NeverARateLimitOrAnotherFailure()
    {
        Assert.True(BotCheck.Offers(Refused(403)));
        Assert.True(BotCheck.Offers(Refused(503)));
        Assert.True(BotCheck.Offers(Refused(200, Cloudflare)));                  // a 200 challenge page
        Assert.True(BotCheck.Offers(Refused(429, Cloudflare)));                  // a check, whatever the status
        Assert.False(BotCheck.Offers(Refused(429)));                             // a bare 429: the rate limit it most often is
        Assert.False(BotCheck.Offers(Refused(503, retryAfter: TimeSpan.FromSeconds(30))));
        Assert.False(BotCheck.Offers(Refused(404, outcome: FetchOutcome.Failed)));
        Assert.False(BotCheck.Offers(Refused(0, outcome: FetchOutcome.Timeout)));
        Assert.False(BotCheck.Offers(FetchResult.Fail(FetchOutcome.LanRefused, "http://nas.home/", "nas.home")));
        Assert.False(BotCheck.Offers(Refused(200, Article, outcome: FetchOutcome.Ok)));

        // A refusal with no page on it (2026-10-20, the code review): an API's JSON, S3's AccessDenied XML. A check is a page.
        Assert.False(BotCheck.Offers(new FetchResult(FetchOutcome.Blocked, "https://api.github.com/", "https://api.github.com/", 403, "application/json", null, "{\"message\":\"forbidden\"}", WebText.HttpEngine, "HTTP 403", false)));
        Assert.False(BotCheck.Offers(new FetchResult(FetchOutcome.Blocked, "https://b.s3.amazonaws.com/k", "https://b.s3.amazonaws.com/k", 403, "application/xml", null, "<Error><Code>AccessDenied</Code></Error>", WebText.HttpEngine, "HTTP 403", false)));

        static OpenedDownload Opened(int status, FetchOutcome outcome = FetchOutcome.Blocked, TimeSpan? wait = null, string mediaType = "") =>
            new(outcome, "https://example.com/f.zip", "https://example.com/f.zip", status, mediaType, "", 0, "HTTP " + status) { RetryAfter = wait };
        Assert.True(BotCheck.Offers(Opened(403)));
        Assert.True(BotCheck.Offers(Opened(403, mediaType: "text/html")));
        Assert.True(BotCheck.Offers(Opened(503)));
        Assert.False(BotCheck.Offers(Opened(503, wait: TimeSpan.FromSeconds(5))));
        Assert.False(BotCheck.Offers(Opened(429)));
        Assert.False(BotCheck.Offers(Opened(404, FetchOutcome.Failed)));
        Assert.False(BotCheck.Offers(Opened(403, mediaType: "application/xml")));
        Assert.False(BotCheck.Offers(Opened(403, mediaType: "text/plain")));
    }

    /// <summary>
    /// A rate limit sent as a 403 (2026-10-20, the code review: GitHub's spent limit was offered as a bot check): its <c>Retry-After</c>, or a
    /// <c>…-remaining: 0</c> with its reset — a Unix time, or seconds — is the wait; a limit not spent, or another status, is none.
    /// </summary>
    [Theory]
    [InlineData(403, "x-ratelimit-remaining", "0", "x-ratelimit-reset", "1800000060", 60)]   // GitHub's: a Unix time
    [InlineData(429, "ratelimit-remaining", "0", "ratelimit-reset", "30", 30)]               // the IETF draft's: seconds
    [InlineData(403, "x-rate-limit-remaining", "0", "x-rate-limit-reset", "1799999000", 0)] // a reset gone by: no wait left
    [InlineData(403, "x-ratelimit-remaining", "12", "x-ratelimit-reset", "1800000060", -1)] // a limit not spent
    [InlineData(403, "x-ratelimit-remaining", "0", "x-ratelimit-reset", "soon", -1)]        // no reset to read
    [InlineData(404, "x-ratelimit-remaining", "0", "x-ratelimit-reset", "30", -1)]          // no refusal of that kind
    public void RateLimitWait_ReadsASpentLimitsReset(int status, string remaining, string left, string reset, string at, int seconds)
    {
        using var response = new HttpResponseMessage((HttpStatusCode)status);
        response.Headers.TryAddWithoutValidation(remaining, left);
        response.Headers.TryAddWithoutValidation(reset, at);
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        Assert.Equal(seconds < 0 ? null : TimeSpan.FromSeconds(seconds), WebFetcher.RateLimitWait(response.Headers, status, now));

        using var secondary = new HttpResponseMessage(HttpStatusCode.Forbidden);
        secondary.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(90));
        Assert.Equal(TimeSpan.FromSeconds(90), WebFetcher.RateLimitWait(secondary.Headers, 403, now));   // GitHub's secondary limit
    }

    [Fact]
    public async Task WebFetch_ARateLimitSentAsA403_IsNoBotCheck_AndSendsNoBrowser()
    {
        _http.Map("https://example.com/api", (_, _) =>
        {
            var response = Html(HttpStatusCode.Forbidden, "<p>API rate limit exceeded</p>");
            response.Headers.TryAddWithoutValidation("x-ratelimit-remaining", "0");
            response.Headers.TryAddWithoutValidation("x-ratelimit-reset", _time.GetUtcNow().AddMinutes(30).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
            return Task.FromResult(response);
        });
        _browser.Html = Article;

        string result = await new WebFetchTool(_web, () => _settings, Solve).FetchAsync("https://example.com/api", null, CancellationToken.None);

        Assert.Contains("the site asks to wait", result, StringComparison.Ordinal);
        Assert.Empty(_asked);
        Assert.Empty(_browser.Runs);
    }

    [Fact]
    public void StillGated_AndWhyOf()
    {
        Assert.True(BotCheck.StillGated(Cloudflare, HtmlToMarkdown.Convert(Cloudflare, new Uri("https://example.com/")).Markdown));
        Assert.True(BotCheck.StillGated(DuckAnomaly, "Unfortunately, bots use DuckDuckGo too."));
        Assert.False(BotCheck.StillGated(Article, "Example Domain"));
        string longPage = "<p>" + new string('x', 2000) + " a captcha and bots use DuckDuckGo too</p>";
        Assert.False(BotCheck.StillGated(longPage, new string('x', 2000)));   // a long page that only mentions them

        Assert.Equal("HTTP 403 Forbidden", BotCheck.WhyOf("HTTP 403 Forbidden; the headless browser failed too: the headless browser got a bot challenge page"));
        Assert.Equal("a bot challenge page", BotCheck.WhyOf(" a bot challenge page "));
        Assert.Equal(TimeSpan.FromMinutes(5), BotCheck.SolveTimeout);
    }

    // ── The wording ──────────────────────────────────────────────────────────

    [Fact]
    public void Text_IsPinned()
    {
        var request = new BotCheckRequest(new Uri("https://example.com/page"), BotCheckUse.Fetch, "HTTP 403 Forbidden");
        Assert.Equal("example.com wants a person to pass a bot check (HTTP 403 Forbidden) before reading https://example.com/page. Open it in the Browser window to solve it?", BotCheckText.AskCaption(request));
        Assert.Equal("example.com wants a person to pass a bot check before searching https://example.com/page. Open it in the Browser window to solve it?", BotCheckText.AskCaption(request with { Use = BotCheckUse.Search, Why = "" }));
        Assert.Equal(["reading", "searching", "downloading", "printing"], new[] { BotCheckUse.Fetch, BotCheckUse.Search, BotCheckUse.Download, BotCheckUse.Pdf }.Select(BotCheckText.Doing));
        Assert.Equal("Solve the check for example.com in the Browser window; it closes by itself once the page is through. Esc gives up.", BotCheckText.WaitCaption(request));

        string url = "https://example.com/";
        Assert.Equal("; the user declined to solve it in the Browser window", BotCheckText.For(BotCheckResult.Ended(BotCheckEnd.Declined, url)));
        Assert.Equal("; the user gave up on it in the Browser window", BotCheckText.For(BotCheckResult.Ended(BotCheckEnd.GaveUp, url)));
        Assert.Equal("; it was still a bot check after 5 minutes in the Browser window", BotCheckText.For(BotCheckResult.Ended(BotCheckEnd.TimedOut, url)));
        Assert.Equal("; the Browser window got HTTP 403 too, with no check to solve", BotCheckText.For(BotCheckResult.Ended(BotCheckEnd.Refused, url, status: 403)));
        Assert.Equal("; the Browser window could not load it either", BotCheckText.For(BotCheckResult.Ended(BotCheckEnd.Refused, url)));
        Assert.Equal("; the Browser window could not open: no runtime", BotCheckText.For(BotCheckResult.Ended(BotCheckEnd.Unavailable, url, "no runtime")));
        Assert.Equal("", BotCheckText.For(FakeBrowserWindow.Cleared(request, Article)));

        // The window's log line as a check ends, both windows' (2026-10-26): the status and the detail, each when there is one.
        Assert.Equal("The bot check on example.com ended: Cleared (HTTP 200).", BrowserText.CheckEndedLogLine("example.com", FakeBrowserWindow.Cleared(request, Article)));
        Assert.Equal("The bot check on example.com ended: Refused (HTTP 403).", BrowserText.CheckEndedLogLine("example.com", BotCheckResult.Ended(BotCheckEnd.Refused, url, status: 403)));
        Assert.Equal("The bot check on example.com ended: Refused (HTTP 404; the page did not load).", BrowserText.CheckEndedLogLine("example.com", BotCheckResult.Ended(BotCheckEnd.Refused, url, BrowserText.DidNotLoad, 404)));
        Assert.Equal("The bot check on example.com ended: Unavailable (the Browser window closed).", BrowserText.CheckEndedLogLine("example.com", BotCheckResult.Ended(BotCheckEnd.Unavailable, url, BrowserText.Closed)));
        Assert.Equal("The bot check on example.com ended: GaveUp.", BrowserText.CheckEndedLogLine("example.com", BotCheckResult.Ended(BotCheckEnd.GaveUp, url)));

        Assert.Equal("The Browser window closed; 2 cookies for example.com, shop.example.com handed to the web tools.", BotCheckText.Closed(2, ["example.com", "shop.example.com"]));
        Assert.Equal("The Browser window closed; 1 cookie for example.com handed to the web tools.", BotCheckText.Closed(1, ["example.com"]));
        Assert.Equal("The Browser window closed; no cookies to hand to the web tools.", BotCheckText.Closed(0, ["example.com"]));
        Assert.Equal("Opened https://example.com/ in the Browser window; close it to hand its cookies to the web tools.", BotCheckText.Opened(url));
        Assert.StartsWith("'notes' is not an http or https address. Usage: /browser [url]", BotCheckText.NotAUrl("notes"));
        Assert.Equal("browser window", WebText.WindowEngine);
    }

    // ── The window's pure parts ──────────────────────────────────────────────

    // A look at a document (2026-10-26, the redesign: the watch keeps its state per document, its status the document's own).
    private static BrowserLook Look(string html, int status, double document, string url = "https://example.com/", string state = "complete") =>
        new(state, HeadlessBrowser.Fingerprint(html), status, url, document);

    // One look as the shared watch makes it: the DOM read first when wanted, then the look observed. Whether the page is ready.
    private static bool See(BrowserWatch watch, TimeSpan now, string html, int status, double document, string url = "https://example.com/", bool errorPage = false)
    {
        var look = Look(html, status, document, url);
        if (watch.WantsRead(now, look, status, errorPage))
        {
            Assert.True(watch.Read(now, html, new Uri("https://example.com/"), document));
        }

        return watch.Observe(now, look, status, errorPage);
    }

    [Fact]
    public void Watch_WaitsOnTheCheck_ThenReadsTheNextDocument_WithItsOwnStatus()
    {
        var watch = new BrowserWatch();
        Assert.False(See(watch, TimeSpan.Zero, Cloudflare, 403, 1));
        Assert.True(watch.Gated);
        Assert.False(See(watch, TimeSpan.FromSeconds(2), Cloudflare, 403, 1));
        Assert.False(See(watch, TimeSpan.FromMinutes(4), Cloudflare, 403, 1));   // never taken as it is: a person is solving it
        Assert.Equal(BrowserWatchStep.Wait, watch.Decide());

        // Solved: the site goes on to the article, a new document. Its status is its own, interactive long before its pictures load
        // (the user's report, 2026-10-26, session 795: it was taken under the check's 403).
        var now = TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(1);
        Assert.False(See(watch, now, Article, 200, 2));
        Assert.False(watch.Gated);
        Assert.Equal(200, watch.Status);
        Assert.True(See(watch, now + TimeSpan.FromSeconds(2), Article, 200, 2));
        Assert.False(watch.Stale(HeadlessBrowser.Fingerprint(Article)));
        Assert.Equal(BrowserWatchStep.Read, watch.Decide());
        Assert.Equal(Article, watch.Html);
    }

    // R18 of the 2026-10-26 review: Cloudflare's "Verification successful. Waiting for … to respond" shows on the check's own 403 document.
    [Fact]
    public void Watch_A4xxDocumentACheckWasSeenOn_Waits_ForTheNextDocument()
    {
        var watch = new BrowserWatch();
        Assert.False(See(watch, TimeSpan.Zero, Cloudflare, 403, 1));
        string passed = "<html><body><h1>example.com</h1><p>Verification successful. Waiting for example.com to respond...</p></body></html>";
        Assert.False(See(watch, TimeSpan.FromSeconds(5), passed, 403, 1));
        Assert.False(watch.Gated);
        Assert.True(See(watch, TimeSpan.FromSeconds(8), passed, 403, 1));
        Assert.Equal(BrowserWatchStep.Wait, watch.Decide());   // the check's page about to move on, not a refusal

        Assert.False(See(watch, TimeSpan.FromSeconds(9), Article, 200, 2));
        Assert.True(See(watch, TimeSpan.FromSeconds(11), Article, 200, 2));
        Assert.Equal(BrowserWatchStep.Read, watch.Decide());
    }

    [Fact]
    public void Watch_RefusesAnErrorStatus_WithNoCheck_AndTheErrorPage()
    {
        var denied = "<html><body><h1>Access Denied</h1><p>You don't have permission.</p></body></html>";
        var watch = new BrowserWatch();
        Assert.False(See(watch, TimeSpan.Zero, denied, 403, 1));
        Assert.True(See(watch, TimeSpan.FromSeconds(2), denied, 403, 1));
        Assert.Equal(BrowserWatchStep.Refused, watch.Decide());
        Assert.False(watch.LoadFailed);
        Assert.Equal(403, watch.Status);

        var failed = new BrowserWatch();
        string error = "<html><body><h1>Hmmm… can't reach this page</h1></body></html>";
        Assert.False(See(failed, TimeSpan.Zero, error, 0, 1, "chrome-error://chromewebdata/", errorPage: true));
        Assert.True(See(failed, TimeSpan.FromSeconds(2), error, 0, 1, "chrome-error://chromewebdata/", errorPage: true));
        Assert.True(failed.LoadFailed);
        Assert.Equal(BrowserWatchStep.Refused, failed.Decide());

        Assert.Equal(BrowserWatchStep.Wait, new BrowserWatch().Decide());   // nothing seen
    }

    [Fact]
    public void Watch_NeverJudgesThePageFromBefore_NorABlankPage()
    {
        var watch = new BrowserWatch { Before = 1 };
        Assert.False(See(watch, TimeSpan.Zero, Article, 200, 1));
        Assert.False(See(watch, TimeSpan.FromMinutes(1), Article, 200, 1));
        Assert.Null(watch.Html);   // not even read
        Assert.Equal(BrowserWatchStep.Wait, watch.Decide());

        string blank = "<html><head></head><body></body></html>";
        Assert.False(See(watch, TimeSpan.FromMinutes(1), blank, 0, 2, "about:blank"));
        Assert.False(See(watch, TimeSpan.FromMinutes(2), blank, 0, 2, "about:blank"));
        Assert.Equal(BrowserWatchStep.Wait, watch.Decide());

        Assert.False(See(watch, TimeSpan.FromMinutes(2), Article, 200, 3));
        Assert.True(See(watch, TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(2), Article, 200, 3));
        Assert.Equal(BrowserWatchStep.Read, watch.Decide());
    }

    [Fact]
    public void Watch_KeepsADocumentsStatus_ThroughAPushState_AndStartsANewOneAfresh()
    {
        var watch = new BrowserWatch();
        Assert.False(See(watch, TimeSpan.Zero, Article, 200, 1));
        string moved = Article + "<!-- page 2 -->";
        Assert.False(See(watch, TimeSpan.FromSeconds(2), moved, 0, 1, "https://example.com/?page=2"));   // pushState: the same document
        Assert.Equal(200, watch.Status);
        Assert.Equal("https://example.com/", watch.Url);

        Assert.False(See(watch, TimeSpan.FromSeconds(3), Cloudflare, 503, 2));   // a new document: its read, settle and status its own
        Assert.Equal(503, watch.Status);
        Assert.True(watch.Gated);
        Assert.Equal(BrowserWatchStep.Wait, watch.Decide());
    }

    [Fact]
    public void Watch_DropsARead_FromAnotherDocument()
    {
        var watch = new BrowserWatch();
        Assert.True(watch.WantsRead(TimeSpan.Zero, Look(Article, 200, 1), 200, errorPage: false));
        Assert.True(watch.WantsRead(TimeSpan.Zero, Look(Cloudflare, 403, 2), 403, errorPage: false));   // the next document's look came first
        Assert.False(watch.Read(TimeSpan.Zero, Article, new Uri("https://example.com/"), 1));
        Assert.Null(watch.Html);
        Assert.Equal(403, watch.Status);
    }

    [Theory]
    [InlineData("https://example.com/a", true)]
    [InlineData("http://example.com/", true)]
    [InlineData("about:blank", true)]
    [InlineData("file:///C:/Windows/", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("data:text/html,hi", false)]
    [InlineData("ms-msdt:/id", false)]
    [InlineData("search-ms:query=x", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Page_AllowsTheWebAndTheBlankPage(string? uri, bool allowed)
    {
        Assert.Equal(allowed, BrowserPage.Allows(uri));
        Assert.Equal(allowed && uri != "about:blank" ? new Uri(uri!).Host : null, BrowserPage.HostOf(uri));
    }

    [Fact]
    public void Page_TheFolder_TheIdentity_TheKeys_AndTheTitle()
    {
        Assert.Equal(Path.Combine("profiles", "work", "webview2-browser"), BrowserPage.UserDataFolder(Path.Combine("profiles", "work")));   // the profile's own since later on 2026-10-13
        Assert.Equal(new BrowserIdentity(BrowserBrand.Edge, 155, BrowserIdentity.CurrentPlatform), BrowserPage.IdentityOf("155.0.3485.66"));
        Assert.Equal(BrowserIdentity.FallbackMajor, BrowserPage.IdentityOf(null).Major);
        Assert.Equal(BrowserIdentity.FallbackMajor, BrowserPage.IdentityOf("12.0").Major);
        Assert.Equal("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/155.0.0.0 Safari/537.36 Edg/155.0.0.0",
            (BrowserPage.IdentityOf("155.0.1.2") with { Platform = BrowserPlatform.Windows }).UserAgent);   // WebView2 155's own, read 2026-10-13

        Assert.Equal(BrowserKeyAction.Close, BrowserKeys.ActionFor(BrowserKeys.VkW, control: true, alt: false));
        Assert.Equal(BrowserKeyAction.None, BrowserKeys.ActionFor(BrowserKeys.VkW, control: false, alt: false));
        Assert.Equal(BrowserKeyAction.Reload, BrowserKeys.ActionFor(BrowserKeys.VkF5, control: false, alt: false));
        Assert.Equal(BrowserKeyAction.Back, BrowserKeys.ActionFor(BrowserKeys.VkLeft, control: false, alt: true));
        Assert.Equal(BrowserKeyAction.None, BrowserKeys.ActionFor(BrowserKeys.VkLeft, control: false, alt: false));
        Assert.Equal(BrowserKeyAction.None, BrowserKeys.ActionFor('C', control: true, alt: false));   // the page's copy

        Assert.Equal("NeonSidekick browser", BrowserText.Title(null, solving: false));
        Assert.Equal("Example Domain · NeonSidekick browser", BrowserText.Title(" Example Domain ", solving: false));
        Assert.Equal("Bot check · Just a moment... · NeonSidekick browser", BrowserText.Title("Just a moment...", solving: true));
        Assert.Contains("in use by another copy of the app", BrowserText.Failed("Environment", unchecked((int)0x8007139F)), StringComparison.Ordinal);
        Assert.Equal("the Browser window's browser could not start (WebView2 Controller, 0x80004005)", BrowserText.Failed("Controller", unchecked((int)0x80004005)));
    }

    [Fact]
    public void Answers_ReadTheScriptsAndTheCookies()
    {
        Assert.True(BrowserAnswers.TryReadLook("{\"s\":\"complete\",\"n\":12,\"x\":3456}", out var look));
        Assert.Equal("complete", look.State);
        Assert.Equal((12, 3456u), look.Print);
        Assert.Equal(0, look.Status);   // an answer from before 2026-10-26's fields, or WebKit's: no status
        Assert.False(BrowserAnswers.TryReadLook("{\"s\":\"loading\",\"n\":0,\"x\":1}", out _));
        Assert.False(BrowserAnswers.TryReadLook("null", out _));
        Assert.False(BrowserAnswers.TryReadLook("not json", out _));
        // A hash past 2³¹ as WebView2 writes it, a whole double (2026-10-26, the live test: such a page was never read).
        Assert.True(BrowserAnswers.TryReadLook("{\"n\":1544,\"s\":\"interactive\",\"x\":4083221934.0}", out look));
        Assert.Equal((1544, 4083221934u), look.Print);
        Assert.False(BrowserAnswers.TryReadLook("{\"n\":12,\"x\":1.5}", out _));
        Assert.False(BrowserAnswers.TryReadLook("{\"n\":12,\"x\":4294967296.0}", out _));
        Assert.False(BrowserAnswers.TryReadLook("{\"n\":12,\"x\":-1}", out _));
        // The document's own status, address and time origin (2026-10-26, the redesign).
        Assert.True(BrowserAnswers.TryReadLook("{\"s\":\"complete\",\"n\":12,\"x\":1,\"st\":403,\"u\":\"https://example.com/a\",\"o\":1761500000123.4}", out look));
        Assert.Equal(new BrowserLook("complete", (12, 1u), 403, "https://example.com/a", 1761500000123.4), look);
        Assert.True(BrowserAnswers.TryReadLook("{\"n\":12,\"x\":1,\"st\":12345}", out look));
        Assert.Equal(0, look.Status);   // no HTTP status

        Assert.True(BrowserAnswers.TryReadPage("{\"s\":\"complete\",\"h\":\"<html></html>\",\"o\":7.5}", out string html, out double document));
        Assert.Equal("<html></html>", html);
        Assert.Equal(7.5, document);
        Assert.False(BrowserAnswers.TryReadPage("{\"h\":\"\"}", out _, out _));
        Assert.False(BrowserAnswers.TryReadPage(null, out _, out _));

        string cookies = "{\"cookies\":[{\"name\":\"cf_clearance\",\"value\":\"a\",\"domain\":\".example.com\",\"path\":\"/\",\"expires\":-1,\"session\":true,\"secure\":true,\"httpOnly\":true},"
            + "{\"name\":\"other\",\"value\":\"b\",\"domain\":\"elsewhere.org\",\"path\":\"/\",\"expires\":-1,\"session\":true}]}";
        var read = BrowserAnswers.CookiesFor(cookies, ["shop.example.com"]);
        Assert.Equal("cf_clearance", Assert.Single(read).Name);
        Assert.Empty(BrowserAnswers.CookiesFor("oops", ["example.com"]));
    }

    // ── The fetcher's hand-off ───────────────────────────────────────────────

    [Fact]
    public async Task Fetcher_TakesTheWindowsPage_AndClaimsItsBrowserOnTheHost()
    {
        var agents = new List<string>();
        _http.Map("https://example.com/", (r, _) => { agents.Add(r.Headers.UserAgent.ToString()); return Task.FromResult(Html(HttpStatusCode.OK, Article)); });
        var request = new BotCheckRequest(new Uri("https://example.com/"), BotCheckUse.Fetch, "HTTP 403");
        var solved = FakeBrowserWindow.Cleared(request, Article) with { Truncated = true };

        var page = _web.Fetcher.FromWindow(request.Url, solved);
        await _web.Fetcher.FetchAsync(new Uri("https://example.com/"), WebAccess.Options(_settings), CancellationToken.None);

        Assert.True(page.Ok);
        Assert.Equal(WebText.WindowEngine, page.Engine);
        Assert.Equal("Example Domain", page.Page!.Title);
        Assert.True(page.Truncated);
        Assert.Equal("cf_clearance=solved", _jar.GetCookieHeader(new Uri("https://example.com/")));
        Assert.Equal(solved.Identity.UserAgent, Assert.Single(agents));
        Assert.Equal(0, _web.Fetcher.Adopt([], solved.Identity, request.Url));
    }

    /// <summary>
    /// <c>/browser clear all</c> and a profile switch (later on 2026-10-13): the window's cookies taken back out of the jar and its Edge
    /// claim dropped; a cookie a plain fetch set stays.
    /// </summary>
    [Fact]
    public async Task Fetcher_ForgetAdopted_TakesTheWindowsCookiesOut_AndKeepsAPlainFetchs()
    {
        var agents = new List<string>();
        _http.Map("https://example.com/", (r, _) => { agents.Add(r.Headers.UserAgent.ToString()); return Task.FromResult(Html(HttpStatusCode.OK, Article)); });
        _jar.Add(new Uri("https://example.com/"), new Cookie("plain", "kept", "/"));
        var request = new BotCheckRequest(new Uri("https://example.com/"), BotCheckUse.Fetch, "HTTP 403");
        var solved = FakeBrowserWindow.Cleared(request, Article);
        _web.Fetcher.FromWindow(request.Url, solved);

        Assert.Equal(1, _web.Fetcher.ForgetAdopted());
        Assert.Equal(0, _web.Fetcher.ForgetAdopted());   // nothing left to forget
        await _web.Fetcher.FetchAsync(new Uri("https://example.com/"), WebAccess.Options(_settings), CancellationToken.None);

        Assert.Equal("plain=kept", _jar.GetCookieHeader(new Uri("https://example.com/")));
        Assert.NotEqual(solved.Identity.UserAgent, Assert.Single(agents));   // no longer Edge there
    }

    [Fact]
    public async Task Fetcher_PastBotCheck_EachEnding()
    {
        var url = new Uri("https://example.com/");
        var refused = Refused(403);
        var options = WebAccess.Options(_settings);

        var cleared = await _web.Fetcher.PastBotCheckAsync(url, refused, options, Solve, BotCheckUse.Fetch, CancellationToken.None);
        Assert.Equal(WebText.WindowEngine, cleared.Engine);
        Assert.Equal(new BotCheckRequest(url, BotCheckUse.Fetch, "HTTP 403"), Assert.Single(_asked));

        _answer = r => BotCheckResult.Ended(BotCheckEnd.Declined, r.Url.AbsoluteUri);
        var declined = await _web.Fetcher.PastBotCheckAsync(url, refused, options, Solve, BotCheckUse.Fetch, CancellationToken.None);
        Assert.Equal("Error: 'https://example.com/' refused the request (HTTP 403; the user declined to solve it in the Browser window)", declined.Error);

        _answer = _ => null;
        Assert.Same(refused, await _web.Fetcher.PastBotCheckAsync(url, refused, options, Solve, BotCheckUse.Fetch, CancellationToken.None));

        // The check ended on the LAN under internet: not taken.
        _answer = r => FakeBrowserWindow.Cleared(r, Article) with { FinalUrl = "http://nas.home/" };
        var lan = await _web.Fetcher.PastBotCheckAsync(url, refused, options, Solve, BotCheckUse.Fetch, CancellationToken.None);
        Assert.False(lan.Ok);
        Assert.EndsWith("the Browser window ended on http://nas.home/, which Web browser network mode keeps off limits)", lan.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// A check that ended on another site (2026-10-20, the code review: the window's kept profile handed a signed-in site's cookies and page
    /// to the web tools): refused, nothing put in the jar; the same site on another host (www, https) is taken.
    /// </summary>
    [Fact]
    public async Task Fetcher_PastBotCheck_AnEndingOnAnotherSite_IsRefused_AndItsCookiesNotTaken()
    {
        var url = new Uri("https://example.com/");
        var options = WebAccess.Options(_settings);
        var mail = new BrowserCookie("SID", "signed-in", ".google.com", "/", -1, true, true);
        _answer = r => FakeBrowserWindow.Cleared(r, Article) with { FinalUrl = "https://mail.google.com/mail/u/0/", Cookies = [mail] };

        var elsewhere = await _web.Fetcher.PastBotCheckAsync(url, Refused(403), options, Solve, BotCheckUse.Fetch, CancellationToken.None);

        Assert.False(elsewhere.Ok);
        Assert.EndsWith(BotCheckText.LeftTheSite("https://mail.google.com/mail/u/0/") + ")", elsewhere.Error, StringComparison.Ordinal);
        Assert.Equal("", _jar.GetCookieHeader(new Uri("https://mail.google.com/")));

        _answer = r => FakeBrowserWindow.Cleared(r, Article) with { FinalUrl = "https://www.example.com/welcome" };
        Assert.True((await _web.Fetcher.PastBotCheckAsync(url, Refused(403), options, Solve, BotCheckUse.Fetch, CancellationToken.None)).Ok);

        Assert.True(BotCheck.LeftTheSite(url, "https://accounts.example.org/"));
        Assert.False(BotCheck.LeftTheSite(url, "http://shop.example.com/"));
        Assert.False(BotCheck.LeftTheSite(url, "about:blank"));
        Assert.Equal(["example.com", "example.com"], BrowserJobs.CheckHosts(url, "https://mail.google.com/"));   // the window reads none of its cookies
        Assert.Equal(["example.com", "shop.example.com"], BrowserJobs.CheckHosts(url, "https://shop.example.com/"));
    }

    /// <summary>
    /// A PDF past a bot check (2026-10-14, the PDF plan's gap 22): the window cancels a download during a check and hands back its cookies,
    /// so the address is asked once more over HTTP with them — a PDF read there; a file still refused names download_file.
    /// </summary>
    [Fact]
    public async Task Fetcher_PastBotCheck_AFile_IsFetchedAgainWithTheCookies()
    {
        var url = new Uri("https://example.com/paper.pdf");
        var refused = Refused(403);
        var options = WebAccess.Options(_settings);
        string? cookie = null;
        bool refusing = false;
        _http.Map("https://example.com/paper.pdf", (r, _) =>
        {
            cookie = _jar.GetCookieHeader(r.RequestUri!);
            return Task.FromResult(refusing
                ? Html(HttpStatusCode.Forbidden, "denied")
                : StubHttpMessageHandler.Bytes(HttpStatusCode.OK, Encoding.ASCII.GetBytes("%PDF-1.7\n%%EOF\n"), "application/pdf"));
        });
        _browser.PdfReadPages.Add(new PdfPageRead(1, "Abstract", null, 0, 0));
        _answer = r => FakeBrowserWindow.Cleared(r, null) with { WasFile = true };   // its clearance cookie for the host

        var read = await _web.Fetcher.PastBotCheckAsync(url, refused, options, Solve, BotCheckUse.Fetch, CancellationToken.None);

        Assert.True(read.Ok, read.Error);
        Assert.Equal(WebText.PdfEngine, read.Engine);
        Assert.Contains("cf_clearance=solved", cookie, StringComparison.Ordinal);

        refusing = true;
        var still = await _web.Fetcher.PastBotCheckAsync(url, refused, options, Solve, BotCheckUse.Fetch, CancellationToken.None);
        Assert.False(still.Ok);
        Assert.EndsWith(WebText.FileAfterBotCheck + ")", still.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Download_ReadsTheRetryAfter()
    {
        _http.Map("https://example.com/f.zip", (_, _) =>
        {
            var response = Html(HttpStatusCode.ServiceUnavailable, "busy");
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return Task.FromResult(response);
        });

        using var opened = await _web.Fetcher.OpenDownloadAsync(new Uri("https://example.com/f.zip"), WebAccess.Options(_settings), 1000, CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(30), opened.RetryAfter);
        Assert.False(BotCheck.Offers(opened));
    }

    // ── The tools ────────────────────────────────────────────────────────────

    /// <summary>example.com refuses with Cloudflare's check, and the headless browser gets the check too.</summary>
    private void GatedSite()
    {
        _http.Map("https://example.com/", HttpStatusCode.Forbidden, Cloudflare, "text/html");
        _browser.Html = Cloudflare;
    }

    [Fact]
    public async Task WebFetch_ShowsTheCheck_TakesThePage_AndCachesIt()
    {
        GatedSite();
        var fetch = new WebFetchTool(_web, () => _settings, Solve);

        string first = await fetch.FetchAsync("https://example.com/", null, CancellationToken.None);
        string second = await fetch.FetchAsync("https://example.com/", null, CancellationToken.None);

        Assert.StartsWith("Example Domain — https://example.com/ (chars 1–", first, StringComparison.Ordinal);
        Assert.Contains(", browser window)", first, StringComparison.Ordinal);
        Assert.Equal(first, second);
        var asked = Assert.Single(_asked);
        Assert.Equal(BotCheckUse.Fetch, asked.Use);
        Assert.Equal("HTTP 403 Forbidden", asked.Why);
    }

    [Fact]
    public async Task WebFetch_ADeclinedCheck_IsTheRefusalSaidSo_AndNotCached()
    {
        GatedSite();
        _answer = r => BotCheckResult.Ended(BotCheckEnd.Declined, r.Url.AbsoluteUri);
        var fetch = new WebFetchTool(_web, () => _settings, Solve);

        string result = await fetch.FetchAsync("https://example.com/", null, CancellationToken.None);
        await fetch.FetchAsync("https://example.com/", null, CancellationToken.None);

        Assert.StartsWith("Error: 'https://example.com/' refused the request (HTTP 403 Forbidden; the headless browser failed too", result, StringComparison.Ordinal);
        Assert.EndsWith("; the user declined to solve it in the Browser window)", result, StringComparison.Ordinal);
        Assert.Equal(2, _asked.Count);   // a failure is never cached; the screen answers the turn's second ask itself
    }

    [Fact]
    public async Task WebFetch_NoSolver_OrARateLimit_LeavesTheRefusalAsItWas()
    {
        _http.Map("https://example.com/limited", (_, _) =>
        {
            var response = Html(HttpStatusCode.TooManyRequests, "<p>slow down</p>");
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(60));
            return Task.FromResult(response);
        });
        GatedSite();
        string without = await new WebFetchTool(_web, () => _settings).FetchAsync("https://example.com/", null, CancellationToken.None);
        Assert.DoesNotContain("Browser window", without, StringComparison.Ordinal);

        string limited = await new WebFetchTool(_web, () => _settings, Solve).FetchAsync("https://example.com/limited", null, CancellationToken.None);
        Assert.Contains("the site asks to wait", limited, StringComparison.Ordinal);
        Assert.Empty(_asked);
    }

    [Fact]
    public async Task WebSearch_DuckDuckGosCheck_IsShown_AndItsResultsPageRead()
    {
        _http.Map("https://html.duckduckgo.com/html/", HttpStatusCode.OK, DuckAnomaly, "text/html");
        _answer = r => FakeBrowserWindow.Cleared(r, DuckResults);
        var search = new WebSearchTool(_web, () => _settings, Solve);

        string result = await search.SearchAsync("example", null, CancellationToken.None);

        Assert.Equal("Searched \"example\" (1 result, DuckDuckGo):\n1. Example Domain — https://example.com/\n   For use in examples.", result);
        var asked = Assert.Single(_asked);
        Assert.Equal(BotCheckUse.Search, asked.Use);
        Assert.Equal(DuckDuckGoSearch.QueryUrl("example"), asked.Url);
        Assert.Equal(DuckDuckGoSearch.BotCheckDetail, asked.Why);
        Assert.Equal("cf_clearance=solved", _jar.GetCookieHeader(new Uri("https://html.duckduckgo.com/html/")));

        _answer = r => BotCheckResult.Ended(BotCheckEnd.GaveUp, r.Url.AbsoluteUri);
        _time.Advance(DuckDuckGoSearch.MinInterval);   // its pace, on the test's clock
        Assert.Equal("Error: DuckDuckGo refused the search (a bot check; the user gave up on it in the Browser window); try again in a minute", await search.SearchAsync("other", null, CancellationToken.None));
    }

    /// <summary>The search's hand-off judges where the window ended as the other tools' do (2026-10-20, the code review: it judged nothing).</summary>
    [Fact]
    public async Task WebSearch_AnEndingOnAnotherSite_IsRefused_AndItsCookiesNotTaken()
    {
        _http.Map("https://html.duckduckgo.com/html/", HttpStatusCode.OK, DuckAnomaly, "text/html");
        _answer = r => FakeBrowserWindow.Cleared(r, DuckResults) with { FinalUrl = "https://example.com/" };
        var search = new WebSearchTool(_web, () => _settings, Solve);

        string result = await search.SearchAsync("example", null, CancellationToken.None);

        Assert.Contains(BotCheckText.LeftTheSite("https://example.com/"), result, StringComparison.Ordinal);
        Assert.Equal("", _jar.GetCookieHeader(new Uri("https://html.duckduckgo.com/html/")));
    }

    [Fact]
    public async Task Download_ShowsTheCheck_ThenAsksAgainWithItsCookies()
    {
        int asks = 0;
        _http.Map("https://example.com/f.bin", (_, _) => Task.FromResult(++asks == 1
            ? Html(HttpStatusCode.Forbidden, Cloudflare)
            : StubHttpMessageHandler.Bytes(HttpStatusCode.OK, Encoding.ASCII.GetBytes("payload"), "application/octet-stream")));
        var download = new DownloadFileTool(_web, _files, () => _settings, Solve);

        string result = await download.DownloadAsync("https://example.com/f.bin", null, overwrite: false, CancellationToken.None);

        Assert.StartsWith("downloaded f.bin (7 B, application/octet-stream) from https://example.com/f.bin", result, StringComparison.Ordinal);
        Assert.Equal(BotCheckUse.Download, Assert.Single(_asked).Use);
        Assert.Equal("payload", File.ReadAllText(Path.Combine(_root, "f.bin")));
        Assert.Equal(2, asks);
    }

    [Fact]
    public async Task Download_RefusedAgain_OrDeclined_SaysSo_AndARateLimitIsNotShown()
    {
        _http.Map("https://example.com/f.bin", HttpStatusCode.Forbidden, "<p>denied</p>", "text/html");
        var download = new DownloadFileTool(_web, _files, () => _settings, Solve);
        string again = await download.DownloadAsync("https://example.com/f.bin", null, overwrite: false, CancellationToken.None);
        Assert.Equal("Error: 'https://example.com/f.bin' refused the request (HTTP 403 Forbidden" + BotCheckText.ClearedButClientRefused + ")", again);

        // A refusal served as XML (S3's AccessDenied, 2026-10-20) is no page to solve: not shown.
        _asked.Clear();
        _http.Map("https://example.com/h.bin", HttpStatusCode.Forbidden, "<Error><Code>AccessDenied</Code></Error>", "application/xml");
        Assert.Equal("Error: 'https://example.com/h.bin' refused the request (HTTP 403 Forbidden)", await download.DownloadAsync("https://example.com/h.bin", null, overwrite: false, CancellationToken.None));
        Assert.Empty(_asked);
        _asked.Clear();

        _answer = r => BotCheckResult.Ended(BotCheckEnd.Declined, r.Url.AbsoluteUri);
        string declined = await download.DownloadAsync("https://example.com/f.bin", null, overwrite: false, CancellationToken.None);
        Assert.EndsWith("; the user declined to solve it in the Browser window)", declined, StringComparison.Ordinal);

        _asked.Clear();
        _http.Map("https://example.com/g.bin", HttpStatusCode.TooManyRequests, "slow", "text/plain");
        await download.DownloadAsync("https://example.com/g.bin", null, overwrite: false, CancellationToken.None);
        Assert.Empty(_asked);
        Assert.Empty(Directory.GetFiles(_root));
    }

    private PdfConverter Pdf(BotCheckSolver? solver) =>
        new(_web.Browser, _web.Fetcher, new PrintService(new FakePrintSpooler(), _files, () => _settings, _time), _files, () => _settings, _time, Path.Combine(_dir, "temp"), TimeSpan.FromMilliseconds(400), solver);

    [Fact]
    public async Task Pdf_ABotCheckedPage_IsPrintedByTheWindow()
    {
        GatedSite();
        _answer = r =>
        {
            File.WriteAllBytes(r.PdfPath!, Encoding.ASCII.GetBytes("%PDF-1.7 window"));
            return FakeBrowserWindow.Cleared(r, Article) with { PdfWritten = true };
        };

        string result = await Pdf(Solve).ConvertAsync(new PdfRequest(Url: new Uri("https://example.com/")), CancellationToken.None);

        Assert.Equal("Made example.com.pdf (15 B) from https://example.com/ with the Browser window", result);
        Assert.Equal(BotCheckUse.Pdf, Assert.Single(_asked).Use);
        Assert.Empty(_browser.PdfRuns);
        Assert.Equal("cf_clearance=solved", _jar.GetCookieHeader(new Uri("https://example.com/")));
    }

    /// <summary>
    /// The window's PDF of a page that ended on another site (2026-10-20, the code review): not used, and never where the headless browser
    /// prints, so its own print is the one made — the window's file was left at that path and taken for the headless browser's.
    /// </summary>
    [Fact]
    public async Task Pdf_AWindowPdfOfAnotherSite_IsDropped_AndTheHeadlessPrintIsTheOneMade()
    {
        GatedSite();
        string? windowPath = null;
        _answer = r =>
        {
            windowPath = r.PdfPath;
            File.WriteAllBytes(r.PdfPath!, Encoding.ASCII.GetBytes("%PDF-1.7 the other site's page"));
            return FakeBrowserWindow.Cleared(r, Article) with { PdfWritten = true, FinalUrl = "https://accounts.example.org/" };
        };

        string result = await Pdf(Solve).ConvertAsync(new PdfRequest(Url: new Uri("https://example.com/")), CancellationToken.None);

        Assert.Equal("Made example.com.pdf (21 B) from https://example.com/ with msedge", result);
        var print = Assert.Single(_browser.PdfRuns);
        Assert.NotEqual(windowPath, print.Output);
        Assert.False(File.Exists(windowPath));
        Assert.Equal("%PDF-1.7 fake browser", File.ReadAllText(Path.Combine(_root, "example.com.pdf")));
        Assert.Equal("", _jar.GetCookieHeader(new Uri("https://example.com/")));
    }

    /// <summary>The bot-check probe under the default mode asks over HTTP alone (2026-10-20, the code review): a page of scripts starts no headless read.</summary>
    [Fact]
    public async Task Pdf_TheProbe_SendsNoHeadlessRead_ForAPageThatIsNoRefusal()
    {
        _http.Map("https://example.com/", HttpStatusCode.OK, "<html><body><div id=\"app\"></div><script src=\"/app.js\"></script></body></html>", "text/html");

        await Pdf(Solve).ConvertAsync(new PdfRequest(Url: new Uri("https://example.com/")), CancellationToken.None);

        Assert.Empty(_browser.Runs);
        Assert.Single(_browser.PdfRuns);
        Assert.Empty(_asked);
    }

    [Fact]
    public async Task Pdf_AnyOtherEnding_PrintsAsBefore_AndNobodyToAsk_OrDisabled_ProbesOverHttpOnly()
    {
        GatedSite();
        _answer = r => BotCheckResult.Ended(BotCheckEnd.GaveUp, r.Url.AbsoluteUri);
        string gaveUp = await Pdf(Solve).ConvertAsync(new PdfRequest(Url: new Uri("https://example.com/")), CancellationToken.None);
        Assert.Equal("Made example.com.pdf (21 B) from https://example.com/ with msedge", gaveUp);
        Assert.Single(_browser.PdfRuns);

        _http.Requests.Clear();
        _asked.Clear();
        await Pdf(null).ConvertAsync(new PdfRequest(Url: new Uri("https://example.com/"), To: "two.pdf"), CancellationToken.None);
        _settings.WebBrowserBotcheckWindow = "disabled";
        await Pdf(Solve).ConvertAsync(new PdfRequest(Url: new Uri("https://example.com/"), To: "three.pdf"), CancellationToken.None);
        // One HTTP probe each for its media type (2026-10-22, review T3: with nobody to ask, a PDF served from a path without .pdf went to
        // the print), never a person asked.
        Assert.Equal(2, _http.Requests.Count);
        Assert.Empty(_asked);
        Assert.Equal(3, _browser.PdfRuns.Count);
    }
}
