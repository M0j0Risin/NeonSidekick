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
        Assert.Equal(["ask", "automatic", "disabled"], BotcheckWindow.Names);
        Assert.Equal("automatic", BotcheckWindow.Name(BotcheckKind.Automatic));
        Assert.Equal("asks before opening the Browser window on a bot check", BotcheckWindow.Describe("ask"));
        Assert.Equal("opens the Browser window on a bot check at once", BotcheckWindow.Describe("automatic"));
        Assert.Equal("never opens it; the tool reports the refusal", BotcheckWindow.Describe("disabled"));
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

        static OpenedDownload Opened(int status, FetchOutcome outcome = FetchOutcome.Blocked, TimeSpan? wait = null) =>
            new(outcome, "https://example.com/f.zip", "https://example.com/f.zip", status, "", "", 0, "HTTP " + status) { RetryAfter = wait };
        Assert.True(BotCheck.Offers(Opened(403)));
        Assert.True(BotCheck.Offers(Opened(503)));
        Assert.False(BotCheck.Offers(Opened(503, wait: TimeSpan.FromSeconds(5))));
        Assert.False(BotCheck.Offers(Opened(429)));
        Assert.False(BotCheck.Offers(Opened(404, FetchOutcome.Failed)));
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
        Assert.Equal("Solve the check for example.com in the Browser window; it closes by itself once the page is through. ESC gives up.", BotCheckText.WaitCaption(request));

        string url = "https://example.com/";
        Assert.Equal("; the user declined to solve it in the Browser window", BotCheckText.For(BotCheckResult.Ended(BotCheckEnd.Declined, url)));
        Assert.Equal("; the user gave up on it in the Browser window", BotCheckText.For(BotCheckResult.Ended(BotCheckEnd.GaveUp, url)));
        Assert.Equal("; it was still a bot check after 5 minutes in the Browser window", BotCheckText.For(BotCheckResult.Ended(BotCheckEnd.TimedOut, url)));
        Assert.Equal("; the Browser window got HTTP 403 too, with no check to solve", BotCheckText.For(BotCheckResult.Ended(BotCheckEnd.Refused, url, status: 403)));
        Assert.Equal("; the Browser window could not load it either", BotCheckText.For(BotCheckResult.Ended(BotCheckEnd.Refused, url)));
        Assert.Equal("; the Browser window could not open: no runtime", BotCheckText.For(BotCheckResult.Ended(BotCheckEnd.Unavailable, url, "no runtime")));
        Assert.Equal("", BotCheckText.For(FakeBrowserWindow.Cleared(request, Article)));

        Assert.Equal("The Browser window closed; 2 cookies for example.com, shop.example.com handed to the web tools.", BotCheckText.Closed(2, ["example.com", "shop.example.com"]));
        Assert.Equal("The Browser window closed; 1 cookie for example.com handed to the web tools.", BotCheckText.Closed(1, ["example.com"]));
        Assert.Equal("The Browser window closed; no cookies to hand to the web tools.", BotCheckText.Closed(0, ["example.com"]));
        Assert.Equal("Opened https://example.com/ in the Browser window; close it to hand its cookies to the web tools.", BotCheckText.Opened(url));
        Assert.StartsWith("'notes' is not an http or https address. Usage: /browser [url]", BotCheckText.NotAUrl("notes"));
        Assert.Equal("browser window", WebText.WindowEngine);
    }

    // ── The window's pure parts ──────────────────────────────────────────────

    [Fact]
    public void Watch_WaitsOnTheCheck_ThenReadsThePageItLetsThrough()
    {
        var url = new Uri("https://example.com/");
        var watch = new BrowserWatch();
        var page = (Html: Cloudflare, Print: HeadlessBrowser.Fingerprint(Cloudflare));

        Assert.False(watch.Observe(TimeSpan.Zero, "complete", page.Print));   // nothing loaded yet: the blank page is no answer
        watch.Navigated(true, 403);
        Assert.True(watch.WantsRead(TimeSpan.Zero, page.Print));
        watch.Read(TimeSpan.Zero, page.Html, url);
        Assert.True(watch.Gated);
        Assert.False(watch.WantsRead(TimeSpan.FromSeconds(1), page.Print));
        Assert.False(watch.Observe(TimeSpan.FromSeconds(1), "complete", page.Print));
        Assert.False(watch.Observe(TimeSpan.FromMinutes(4), "complete", page.Print));   // never taken as it is: a person is solving it
        Assert.Equal(BrowserWatchStep.Wait, watch.Decide());

        // Solved: the page reloads into the article.
        watch.Navigated(true, 200);
        var article = HeadlessBrowser.Fingerprint(Article);
        var now = TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(1);
        Assert.True(watch.WantsRead(now, article));
        watch.Read(now, Article, url);
        Assert.False(watch.Gated);
        Assert.True(watch.ChallengeSeen);
        Assert.False(watch.Observe(now, "complete", article));
        Assert.True(watch.Observe(now + TimeSpan.FromSeconds(2), "complete", article));
        Assert.False(watch.Stale(article));
        Assert.Equal(BrowserWatchStep.Read, watch.Decide());
        Assert.Equal(Article, watch.Html);
        Assert.Equal(200, watch.Status);
    }

    [Fact]
    public void Watch_RefusesAnErrorPage_WithNoCheck_AndAPageThatDidNotLoad()
    {
        var url = new Uri("https://example.com/");
        var denied = "<html><body><h1>Access Denied</h1><p>You don't have permission.</p></body></html>";
        var watch = new BrowserWatch();
        watch.Navigated(true, 403);
        watch.Read(TimeSpan.Zero, denied, url);
        Assert.False(watch.Observe(TimeSpan.Zero, "complete", HeadlessBrowser.Fingerprint(denied)));
        Assert.True(watch.Observe(TimeSpan.FromSeconds(2), "complete", HeadlessBrowser.Fingerprint(denied)));
        Assert.Equal(BrowserWatchStep.Refused, watch.Decide());

        var failed = new BrowserWatch();
        failed.Navigated(false, 0);
        Assert.True(failed.LoadFailed);
        failed.Read(TimeSpan.Zero, Article, url);
        Assert.Equal(BrowserWatchStep.Refused, failed.Decide());

        Assert.Equal(BrowserWatchStep.Wait, new BrowserWatch().Decide());   // unread
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
        Assert.Equal(Path.Combine("home", "webview2-browser"), BrowserPage.UserDataFolder("home"));
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
        Assert.True(BrowserAnswers.TryReadPrint("{\"s\":\"complete\",\"n\":12,\"x\":3456}", out string state, out var print));
        Assert.Equal("complete", state);
        Assert.Equal((12, 3456u), print);
        Assert.False(BrowserAnswers.TryReadPrint("{\"s\":\"loading\",\"n\":0,\"x\":1}", out _, out _));
        Assert.False(BrowserAnswers.TryReadPrint("null", out _, out _));
        Assert.False(BrowserAnswers.TryReadPrint("not json", out _, out _));

        Assert.True(BrowserAnswers.TryReadPage("{\"s\":\"complete\",\"h\":\"<html></html>\"}", out string html));
        Assert.Equal("<html></html>", html);
        Assert.False(BrowserAnswers.TryReadPage("{\"h\":\"\"}", out _));
        Assert.False(BrowserAnswers.TryReadPage(null, out _));

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
        _http.Map("https://example.com/f.bin", HttpStatusCode.Forbidden, "denied", "text/plain");
        var download = new DownloadFileTool(_web, _files, () => _settings, Solve);
        string again = await download.DownloadAsync("https://example.com/f.bin", null, overwrite: false, CancellationToken.None);
        Assert.Equal("Error: 'https://example.com/f.bin' refused the request (HTTP 403 Forbidden" + BotCheckText.ClearedButClientRefused + ")", again);

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

    [Fact]
    public async Task Pdf_AnyOtherEnding_PrintsAsBefore_AndNobodyToAsk_OrDisabled_FetchesNothingFirst()
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
        Assert.Empty(_http.Requests);
        Assert.Empty(_asked);
        Assert.Equal(3, _browser.PdfRuns.Count);
    }
}
