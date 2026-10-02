using System.Net;
using System.Text;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.Web;

namespace NeonSidekick.Tests;

public class WebFetcherTests
{
    private const string Article = "<html><head><title>Example Domain</title></head><body><h1>Example Domain</h1><p>This domain is for use in illustrative examples in documents.</p><p><a href=\"https://www.iana.org/domains/example\">More information...</a></p></body></html>";
    private const string Shell = "<html><head><title>App</title></head><body><div id=\"root\"></div><script src=\"/static/js/main.js\"></script></body></html>";
    private const string Cloudflare = "<html><head><title>Just a moment...</title></head><body><div id=\"cf-chl-widget\">Checking your browser before accessing the site.</div><script src=\"/cdn-cgi/challenge-platform/h/b/orchestrate/jsch/v1\"></script></body></html>";

    private readonly StubHttpMessageHandler _http = new();
    private readonly FakeHeadlessBrowser _browser = new();
    private readonly Dictionary<string, IPAddress[]> _dns = new(StringComparer.OrdinalIgnoreCase)
    {
        ["example.com"] = [IPAddress.Parse("93.184.216.34")],
        ["www.iana.org"] = [IPAddress.Parse("192.0.43.8")],
        ["intranet.corp"] = [IPAddress.Parse("10.1.2.3")],
        ["twofaced.example"] = [IPAddress.Parse("8.8.8.8"), IPAddress.Parse("192.168.0.9")],
    };

    private WebFetcher Fetcher() => new(new HttpClient(_http), _browser, (host, _) => Task.FromResult(_dns.TryGetValue(host, out var a) ? a : throw new System.Net.Sockets.SocketException(11001)));

    private static FetchOptions Options(FetchEngine engine = FetchEngine.Default, NetworkReach reach = NetworkReach.Internet) => new(engine, "", reach);

    private static HttpResponseMessage Html(HttpStatusCode status, string body, string mediaType = "text/html") =>
        StubHttpMessageHandler.Json(status, body, mediaType);

    [Fact]
    public async Task ParseUrl_TakesHttp_AndABareHost_AndNothingElse()
    {
        Assert.Equal("https://example.com/a?b=c", WebFetcher.ParseUrl(" https://example.com/a?b=c ")!.AbsoluteUri);
        Assert.Equal("http://example.com/", WebFetcher.ParseUrl("http://example.com")!.AbsoluteUri);
        Assert.Equal("https://example.com/docs", WebFetcher.ParseUrl("example.com/docs")!.AbsoluteUri);
        Assert.Equal("https://example.com/", WebFetcher.ParseUrl("<https://example.com/>")!.AbsoluteUri);
        Assert.Null(WebFetcher.ParseUrl("ftp://example.com/"));
        Assert.Null(WebFetcher.ParseUrl("file:///C:/x.txt"));
        Assert.Null(WebFetcher.ParseUrl(@"C:\notes.txt"));
        Assert.Null(WebFetcher.ParseUrl("/etc/passwd"));
        Assert.Null(WebFetcher.ParseUrl("notes"));
        Assert.Null(WebFetcher.ParseUrl(""));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Html_IsFetchedWithBrowserHeaders_AndConverted()
    {
        HttpRequestMessage? seen = null;
        _http.Map("https://example.com/", (r, _) => { seen = r; return Task.FromResult(Html(HttpStatusCode.OK, Article)); });

        var page = await Fetcher().FetchAsync(new Uri("https://example.com/"), Options(), CancellationToken.None);

        Assert.True(page.Ok);
        Assert.Equal(200, page.Status);
        Assert.Equal("text/html", page.MediaType);
        Assert.Equal("https://example.com/", page.FinalUrl);
        Assert.Equal(WebText.HttpEngine, page.Engine);
        Assert.Equal("Example Domain", page.Page!.Title);
        Assert.Equal("# Example Domain\n\nThis domain is for use in illustrative examples in documents.\n\n[More information...](https://www.iana.org/domains/example)", page.Page.Markdown);
        Assert.NotNull(seen);
        Assert.Equal(BrowserHeaders.UserAgent, seen!.Headers.UserAgent.ToString());
        Assert.Equal("none", seen.Headers.GetValues("Sec-Fetch-Site").Single());
        Assert.Equal("navigate", seen.Headers.GetValues("Sec-Fetch-Mode").Single());
        Assert.Equal("en-US,en;q=0.9", seen.Headers.AcceptLanguage.ToString());
        Assert.False(seen.Headers.Contains("Referer"));
        Assert.Empty(_browser.Runs);
    }

    [Fact]
    public async Task Referer_IsSent_WithSameOriginOrCrossSite()
    {
        var requests = new List<HttpRequestMessage>();
        _http.Map("https://example.com/", (r, _) => { requests.Add(r); return Task.FromResult(Html(HttpStatusCode.OK, Article)); });

        await Fetcher().FetchAsync(new Uri("https://example.com/a"), Options() with { Referer = new Uri("https://example.com/") }, CancellationToken.None);
        await Fetcher().FetchAsync(new Uri("https://example.com/b"), Options() with { Referer = new Uri("https://other.org/") }, CancellationToken.None);

        Assert.Equal("https://example.com/", requests[0].Headers.GetValues("Referer").Single());
        Assert.Equal("same-origin", requests[0].Headers.GetValues("Sec-Fetch-Site").Single());
        Assert.Equal("cross-site", requests[1].Headers.GetValues("Sec-Fetch-Site").Single());
    }

    [Fact]
    public async Task Redirects_AreFollowed_ByHand_WithTheFinalUrl()
    {
        _http.Map("http://example.com/old", (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.MovedPermanently) { Headers = { Location = new Uri("https://example.com/new") } }));
        _http.Map("https://example.com/new", (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("/final", UriKind.Relative) } }));
        _http.Map("https://example.com/final", HttpStatusCode.OK, Article, "text/html");

        var page = await Fetcher().FetchAsync(new Uri("http://example.com/old"), Options(), CancellationToken.None);

        Assert.True(page.Ok);
        Assert.Equal("http://example.com/old", page.Url);
        Assert.Equal("https://example.com/final", page.FinalUrl);
        Assert.Equal(new[] { "http://example.com/old", "https://example.com/new", "https://example.com/final" }, _http.Requests.Select(r => r.Uri.AbsoluteUri));
    }

    [Fact]
    public async Task TooManyRedirects_Fail()
    {
        _http.Map("https://example.com/loop", (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://example.com/loop") } }));

        var page = await Fetcher().FetchAsync(new Uri("https://example.com/loop"), Options(), CancellationToken.None);

        Assert.Equal(FetchOutcome.Failed, page.Outcome);
        Assert.Equal("Error: could not fetch 'https://example.com/loop': more than 10 redirects", page.Error);
        Assert.Equal(WebFetcher.MaxRedirects + 1, _http.Requests.Count);
    }

    [Fact]
    public async Task NotHttp_IsRefused_BeforeAnyRequest()
    {
        var page = await Fetcher().FetchAsync(new Uri("ftp://example.com/x"), Options(), CancellationToken.None);

        Assert.Equal(FetchOutcome.NotHttp, page.Outcome);
        Assert.Equal("Error: 'ftp://example.com/x' is not an http or https URL", page.Error);
        Assert.Empty(_http.Requests);
    }

    [Theory]
    [InlineData("http://192.168.1.229:1234/v1/models", "192.168.1.229")]
    [InlineData("http://localhost:8080/", "localhost")]
    [InlineData("http://[::1]:5000/", "[::1]")]
    [InlineData("http://intranet.corp/wiki", "intranet.corp")]
    [InlineData("https://twofaced.example/", "twofaced.example")]
    public async Task Lan_IsRefused_BeforeAnyRequest_UnderInternet(string url, string host)
    {
        // The default mode (the on/off Browser allow LAN off until 2026-09-18): a two-faced name is LAN by its worst address.
        var page = await Fetcher().FetchAsync(new Uri(url), Options(), CancellationToken.None);

        Assert.Equal(FetchOutcome.LanRefused, page.Outcome);
        Assert.Equal(WebText.LanRefused(host), page.Error);
        Assert.Empty(_http.Requests);
        Assert.Empty(_browser.Runs);
    }

    [Theory]
    [InlineData(NetworkReach.LocalAreaNetwork)]
    [InlineData(NetworkReach.Both)]
    public async Task Lan_IsReached_UnderLocalAreaNetwork_AndUnderBoth(NetworkReach reach)
    {
        _http.Map("http://intranet.corp/wiki", HttpStatusCode.OK, Article, "text/html");

        var page = await Fetcher().FetchAsync(new Uri("http://intranet.corp/wiki"), Options(reach: reach), CancellationToken.None);

        Assert.True(page.Ok);
    }

    [Theory]
    [InlineData("https://example.com/", "example.com")]
    [InlineData("https://www.iana.org/domains", "www.iana.org")]
    [InlineData("http://8.8.8.8/", "8.8.8.8")]
    public async Task Internet_IsRefused_BeforeAnyRequest_UnderLocalAreaNetwork(string url, string host)
    {
        // 2026-09-18: local_area_network is this machine and the LAN alone.
        var page = await Fetcher().FetchAsync(new Uri(url), Options(reach: NetworkReach.LocalAreaNetwork), CancellationToken.None);

        Assert.Equal(FetchOutcome.InternetRefused, page.Outcome);
        Assert.Equal(WebText.InternetRefused(host), page.Error);
        Assert.Empty(_http.Requests);
        Assert.Empty(_browser.Runs);
    }

    [Fact]
    public async Task ATwoFacedName_IsLan_RefusedUnderInternet_ReachedUnderLocalAreaNetwork()
    {
        _http.Map("https://twofaced.example/", HttpStatusCode.OK, Article, "text/html");

        Assert.Equal(FetchOutcome.LanRefused, (await Fetcher().FetchAsync(new Uri("https://twofaced.example/"), Options(), CancellationToken.None)).Outcome);
        Assert.True((await Fetcher().FetchAsync(new Uri("https://twofaced.example/"), Options(reach: NetworkReach.LocalAreaNetwork), CancellationToken.None)).Ok);
        Assert.True((await Fetcher().FetchAsync(new Uri("https://twofaced.example/"), Options(reach: NetworkReach.Both), CancellationToken.None)).Ok);
    }

    [Fact]
    public async Task ARedirectIntoTheLan_IsRefused()
    {
        _http.Map("https://example.com/go", (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("http://10.0.0.5/admin") } }));

        var page = await Fetcher().FetchAsync(new Uri("https://example.com/go"), Options(), CancellationToken.None);

        Assert.Equal(FetchOutcome.LanRefused, page.Outcome);
        Assert.Equal(WebText.LanRefused("10.0.0.5"), page.Error);
        Assert.Single(_http.Requests);
    }

    [Fact]
    public async Task ARedirectToTheInternet_IsRefused_UnderLocalAreaNetwork()
    {
        _http.Map("http://intranet.corp/go", (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://example.com/out") } }));

        var page = await Fetcher().FetchAsync(new Uri("http://intranet.corp/go"), Options(reach: NetworkReach.LocalAreaNetwork), CancellationToken.None);

        Assert.Equal(FetchOutcome.InternetRefused, page.Outcome);
        Assert.Equal(WebText.InternetRefused("example.com"), page.Error);
        Assert.Single(_http.Requests);
    }

    [Fact]
    public async Task ARedirectOffHttp_IsRefused()
    {
        _http.Map("https://example.com/go", (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("file:///C:/x") } }));

        var page = await Fetcher().FetchAsync(new Uri("https://example.com/go"), Options(), CancellationToken.None);

        Assert.Equal(FetchOutcome.NotHttp, page.Outcome);
    }

    [Theory]
    [InlineData(NetworkRefusal.Lan, FetchOutcome.LanRefused)]
    [InlineData(NetworkRefusal.Internet, FetchOutcome.InternetRefused)]
    public async Task TheSocketPolicy_Refusal_IsReported_ByItsReason(NetworkRefusal reason, FetchOutcome outcome)
    {
        // A name that resolved one way at the pre-check but the other at the socket: the handler's exception carries the host and the reason.
        _http.Map("https://example.com/", (_, _) => throw new NetworkRefusedException("example.com", reason));

        var page = await Fetcher().FetchAsync(new Uri("https://example.com/"), Options(reach: NetworkReach.Both), CancellationToken.None);

        Assert.Equal(outcome, page.Outcome);
        Assert.Equal(WebText.Error(outcome, "https://example.com/", "example.com"), page.Error);
    }

    [Fact]
    public async Task TheSocketPolicy_Refusal_WrappedByTheHandler_IsReportedTheSame()
    {
        // SocketsHttpHandler wraps what its connect callback threw in an HttpRequestException.
        _http.Map("https://example.com/", (_, _) => throw new HttpRequestException("connect failed", new NetworkRefusedException("example.com", NetworkRefusal.Lan)));

        var page = await Fetcher().FetchAsync(new Uri("https://example.com/"), Options(reach: NetworkReach.Both), CancellationToken.None);

        Assert.Equal(FetchOutcome.LanRefused, page.Outcome);
        Assert.Equal(WebText.LanRefused("example.com"), page.Error);
    }

    [Theory]
    [InlineData(NetworkReach.Internet)]
    [InlineData(NetworkReach.LocalAreaNetwork)]
    public async Task UnresolvableHost_IsNotJudged_TheRequestFails(NetworkReach reach)
    {
        var page = await Fetcher().FetchAsync(new Uri("https://nowhere.invalid/"), Options(reach: reach), CancellationToken.None);

        Assert.Equal(FetchOutcome.Failed, page.Outcome);
        Assert.StartsWith("Error: could not fetch 'https://nowhere.invalid/': No connection could be made", page.Error);
    }

    [Fact]
    public async Task Binary_IsRefused_ByType_WithoutReadingTheBody()
    {
        _http.Map("https://example.com/x.pdf", (_, _) =>
        {
            var response = StubHttpMessageHandler.Bytes(HttpStatusCode.OK, new byte[100], "application/pdf");
            response.Content.Headers.ContentLength = 1_234_567;
            return Task.FromResult(response);
        });

        var page = await Fetcher().FetchAsync(new Uri("https://example.com/x.pdf"), Options(), CancellationToken.None);

        Assert.Equal(FetchOutcome.Binary, page.Outcome);
        Assert.Equal("Error: 'https://example.com/x.pdf' is application/pdf (1.2 MB); only web pages and text are readable", page.Error);
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("application/json")]
    [InlineData("text/csv")]
    [InlineData("application/rss+xml")]
    [InlineData("application/ld+json")]
    public async Task Text_ComesBackAsItIs(string mediaType)
    {
        _http.Map("https://example.com/data", HttpStatusCode.OK, "{ \"a\": 1 }", mediaType);

        var page = await Fetcher().FetchAsync(new Uri("https://example.com/data"), Options(), CancellationToken.None);

        Assert.True(page.Ok);
        Assert.Null(page.Page);
        Assert.Equal("{ \"a\": 1 }", page.Text);
        Assert.Equal(mediaType, page.MediaType);
    }

    [Fact]
    public async Task NoContentType_IsReadAsHtml()
    {
        _http.Map("https://example.com/", (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(Article)) }));

        var page = await Fetcher().FetchAsync(new Uri("https://example.com/"), Options(), CancellationToken.None);

        Assert.True(page.Ok);
        Assert.Equal("Example Domain", page.Page!.Title);
    }

    [Fact]
    public async Task Charset_ComesFromTheHeader_ElseTheMeta()
    {
        var latin = PageCharset.Lookup("windows-1252")!;
        _http.Map("https://example.com/h", (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(latin.GetBytes("<p>caf\u00e9</p>")) { Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/html") { CharSet = "windows-1252" } } } }));
        _http.Map("https://example.com/m", (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(latin.GetBytes("<meta charset=\"windows-1252\"><p>caf\u00e9</p>")) { Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/html") } } }));

        Assert.Equal("café", (await Fetcher().FetchAsync(new Uri("https://example.com/h"), Options(), CancellationToken.None)).Page!.Markdown);
        Assert.Equal("café", (await Fetcher().FetchAsync(new Uri("https://example.com/m"), Options(), CancellationToken.None)).Page!.Markdown);
    }

    [Fact]
    public async Task ABodyOverTheCap_IsCut_AndMarked()
    {
        var big = new byte[WebFetcher.MaxDownloadBytes + 10];
        Array.Fill(big, (byte)'a');
        _http.Map("https://example.com/big", (_, _) => Task.FromResult(StubHttpMessageHandler.Bytes(HttpStatusCode.OK, big, "text/plain")));

        var page = await Fetcher().FetchAsync(new Uri("https://example.com/big"), Options(), CancellationToken.None);

        Assert.True(page.Ok);
        Assert.True(page.Truncated);
        Assert.Equal(WebFetcher.MaxDownloadBytes, page.Text.Length);
    }

    [Fact]
    public async Task NotFound_Fails_WithTheStatus()
    {
        _http.Map("https://example.com/gone", HttpStatusCode.NotFound, "<p>no</p>", "text/html");

        var page = await Fetcher().FetchAsync(new Uri("https://example.com/gone"), Options(), CancellationToken.None);

        Assert.Equal(FetchOutcome.Failed, page.Outcome);
        Assert.Equal("Error: could not fetch 'https://example.com/gone': HTTP 404 Not Found", page.Error);
        Assert.Empty(_browser.Runs);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "HTTP 403 Forbidden")]
    [InlineData(HttpStatusCode.TooManyRequests, "HTTP 429 Too Many Requests")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "HTTP 503 Service Unavailable")]
    public async Task Blocked_UnderDefault_TriesTheBrowser(HttpStatusCode status, string detail)
    {
        _http.Map("https://example.com/", (_, _) => Task.FromResult(Html(status, "<p>denied</p>")));
        _browser.Html = Article;

        var page = await Fetcher().FetchAsync(new Uri("https://example.com/"), Options(), CancellationToken.None);

        Assert.True(page.Ok);
        Assert.Equal(WebText.BrowserEngine, page.Engine);
        Assert.Equal("Example Domain", page.Page!.Title);
        Assert.Equal(new Uri("https://example.com/"), Assert.Single(_browser.Runs));
        Assert.Equal("", Assert.Single(_browser.LocateCalls));
        _ = detail;
    }

    [Fact]
    public async Task Blocked_UnderDefault_WithNoBrowser_SaysSo()
    {
        _http.Map("https://example.com/", HttpStatusCode.Forbidden, "<p>denied</p>", "text/html");
        _browser.Executable = null;

        var page = await Fetcher().FetchAsync(new Uri("https://example.com/"), Options(), CancellationToken.None);

        Assert.Equal(FetchOutcome.Blocked, page.Outcome);
        Assert.Equal("Error: 'https://example.com/' refused the request (HTTP 403 Forbidden; no headless browser was found to try instead)", page.Error);
        Assert.Empty(_browser.Runs);
    }

    [Fact]
    public async Task Blocked_UnderDefault_WithABrowserThatFails_NamesBoth()
    {
        _http.Map("https://example.com/", HttpStatusCode.Forbidden, "<p>denied</p>", "text/html");
        _browser.Html = null;
        _browser.Failure = "exit code 21";

        var page = await Fetcher().FetchAsync(new Uri("https://example.com/"), Options(), CancellationToken.None);

        Assert.Equal(FetchOutcome.Blocked, page.Outcome);
        Assert.Equal("Error: 'https://example.com/' refused the request (HTTP 403 Forbidden; the headless browser failed too: exit code 21)", page.Error);
    }

    [Fact]
    public async Task AChallengePage_CountsAsBlocked()
    {
        _http.Map("https://example.com/", HttpStatusCode.OK, Cloudflare, "text/html");
        _browser.Html = Article;

        var page = await Fetcher().FetchAsync(new Uri("https://example.com/"), Options(), CancellationToken.None);

        Assert.True(page.Ok);
        Assert.Equal(WebText.BrowserEngine, page.Engine);
        Assert.True(WebFetcher.LooksLikeChallenge(Cloudflare, "Checking your browser before accessing the site."));
        Assert.False(WebFetcher.LooksLikeChallenge(Article, "x"));
        Assert.False(WebFetcher.LooksLikeChallenge("<p>a captcha</p>", new string('x', WebFetcher.ChallengeTextChars + 1)));
    }

    [Fact]
    public async Task AChallengePage_UnderHttpClient_IsTheError()
    {
        _http.Map("https://example.com/", HttpStatusCode.OK, Cloudflare, "text/html");

        var page = await Fetcher().FetchAsync(new Uri("https://example.com/"), Options(FetchEngine.HttpClient), CancellationToken.None);

        Assert.Equal(FetchOutcome.Blocked, page.Outcome);
        Assert.Equal("Error: 'https://example.com/' refused the request (a bot challenge page)", page.Error);
        Assert.Empty(_browser.Runs);
    }

    [Fact]
    public async Task AShell_UnderDefault_IsRenderedByTheBrowser()
    {
        _http.Map("https://example.com/app", HttpStatusCode.OK, Shell, "text/html");
        _browser.Html = "<html><body><div id=\"root\"><h1>Dashboard</h1><p>Rendered by React.</p></div></body></html>";

        var page = await Fetcher().FetchAsync(new Uri("https://example.com/app"), Options(), CancellationToken.None);

        Assert.True(page.Ok);
        Assert.Equal(WebText.BrowserEngine, page.Engine);
        Assert.Equal("# Dashboard\n\nRendered by React.", page.Page!.Markdown);
        Assert.True(WebFetcher.LooksLikeShell(Shell, ""));
        Assert.False(WebFetcher.LooksLikeShell(Article, "some text"));
        Assert.False(WebFetcher.LooksLikeShell(Shell, new string('x', WebFetcher.ShellTextChars)));
    }

    [Fact]
    public async Task AShell_WithNoBrowser_OrAFailingOne_KeepsTheShell()
    {
        _http.Map("https://example.com/app", HttpStatusCode.OK, Shell, "text/html");
        _browser.Executable = null;

        var page = await Fetcher().FetchAsync(new Uri("https://example.com/app"), Options(), CancellationToken.None);

        Assert.True(page.Ok);
        Assert.Equal(WebText.HttpEngine, page.Engine);
        Assert.Equal("", page.Page!.Markdown);

        _browser.Executable = @"C:\x\msedge.exe";
        _browser.Html = null;
        page = await Fetcher().FetchAsync(new Uri("https://example.com/app"), Options(), CancellationToken.None);

        Assert.True(page.Ok);
        Assert.Equal(WebText.HttpEngine, page.Engine);
    }

    [Fact]
    public async Task Chromium_UsesTheBrowserAlone()
    {
        _http.Map("https://example.com/", HttpStatusCode.OK, Article, "text/html");
        _browser.Html = "<title>From the browser</title><p>dom</p>";

        var page = await Fetcher().FetchAsync(new Uri("https://example.com/"), Options(FetchEngine.Chromium), CancellationToken.None);

        Assert.True(page.Ok);
        Assert.Equal("From the browser", page.Page!.Title);
        Assert.Equal(WebText.BrowserEngine, page.Engine);
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task Chromium_WithNoBrowser_IsTheNoBrowserError()
    {
        _browser.Executable = null;

        var page = await Fetcher().FetchAsync(new Uri("https://example.com/"), Options(FetchEngine.Chromium), CancellationToken.None);

        Assert.Equal(FetchOutcome.NoBrowser, page.Outcome);
        Assert.Equal(WebText.NoBrowser, page.Error);
    }

    [Fact]
    public async Task Chromium_ConfiguredPath_IsWhatRuns()
    {
        _browser.Html = "<p>x</p>";

        await Fetcher().FetchAsync(new Uri("https://example.com/"), Options(FetchEngine.Chromium) with { BrowserPath = @"D:\tools\chrome.exe" }, CancellationToken.None);

        Assert.Equal(@"D:\tools\chrome.exe", Assert.Single(_browser.LocateCalls));
    }

    [Fact]
    public async Task Chromium_ThatFails_IsTheBrowserError()
    {
        _browser.Html = null;
        _browser.Failure = "no page within 30 s";

        var page = await Fetcher().FetchAsync(new Uri("https://example.com/"), Options(FetchEngine.Chromium), CancellationToken.None);

        Assert.Equal(FetchOutcome.BrowserFailed, page.Outcome);
        Assert.Equal("Error: the headless browser could not load 'https://example.com/': no page within 30 s", page.Error);
    }

    [Fact]
    public async Task Chromium_ThatGetsAChallenge_IsBlocked()
    {
        _browser.Html = Cloudflare;

        var page = await Fetcher().FetchAsync(new Uri("https://example.com/"), Options(FetchEngine.Chromium), CancellationToken.None);

        Assert.Equal(FetchOutcome.Blocked, page.Outcome);
        Assert.Equal("Error: 'https://example.com/' refused the request (the headless browser got a bot challenge page)", page.Error);
    }

    [Fact]
    public async Task HttpClient_NeverRunsTheBrowser()
    {
        _http.Map("https://example.com/", HttpStatusCode.Forbidden, "<p>denied</p>", "text/html");
        _browser.Html = Article;

        var page = await Fetcher().FetchAsync(new Uri("https://example.com/"), Options(FetchEngine.HttpClient), CancellationToken.None);

        Assert.Equal(FetchOutcome.Blocked, page.Outcome);
        Assert.Equal("Error: 'https://example.com/' refused the request (HTTP 403 Forbidden)", page.Error);
        Assert.Empty(_browser.Runs);
    }

    [Fact]
    public async Task ATimeout_IsTheTimeoutError()
    {
        _http.Map("https://example.com/slow", async (_, ct) => { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return Html(HttpStatusCode.OK, Article); });
        // The real ceiling is 30 s; the handler honours the linked token, so a short outer cancel stands in for it here.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Fetcher().FetchAsync(new Uri("https://example.com/slow"), Options(), cts.Token));
        Assert.Equal(TimeSpan.FromSeconds(30), WebFetcher.FetchTimeout);
    }

    // ── DownloadAsync (2026-09-18) ──────────────────────────────────────────

    private static HttpResponseMessage Bytes(byte[] body, string mediaType, string? disposition = null, long? length = null)
    {
        var response = StubHttpMessageHandler.Bytes(HttpStatusCode.OK, body, mediaType);
        if (disposition is not null)
        {
            response.Content.Headers.TryAddWithoutValidation("Content-Disposition", disposition);
        }

        if (length is not null)
        {
            response.Content.Headers.ContentLength = length;
        }

        return response;
    }

    [Fact]
    public async Task Download_ReadsAnyMediaType_Whole_FollowingTheRedirects_WithTheHeadersName()
    {
        byte[] pdf = [0x25, 0x50, 0x44, 0x46, 1, 2, 3];
        _http.Map("http://example.com/old", (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.MovedPermanently) { Headers = { Location = new Uri("https://example.com/report.pdf") } }));
        _http.Map("https://example.com/report.pdf", (_, _) => Task.FromResult(Bytes(pdf, "application/pdf", "attachment; filename=\"Q3 report.pdf\"")));

        var file = await Fetcher().DownloadAsync(new Uri("http://example.com/old"), Options(FetchEngine.Chromium), CancellationToken.None);

        Assert.True(file.Ok);
        Assert.Equal("http://example.com/old", file.Url);
        Assert.Equal("https://example.com/report.pdf", file.FinalUrl);
        Assert.Equal(200, file.Status);
        Assert.Equal("application/pdf", file.MediaType);
        Assert.Equal("Q3 report.pdf", file.FileName);
        Assert.Equal(pdf, file.Bytes);
        Assert.Equal("", file.Detail);
        Assert.Empty(_browser.Runs);   // the browser leg is never a download, whatever the mode
        Assert.Equal(new[] { "http://example.com/old", "https://example.com/report.pdf" }, _http.Requests.Select(r => r.Uri.AbsoluteUri));
    }

    [Fact]
    public void FileNameOf_TakesFilenameStar_ThenFilename_StrippingQuotesAndPaths_ElseNothing()
    {
        Assert.Equal("", WebFetcher.FileNameOf(Bytes([], "image/png")));
        Assert.Equal("a.png", WebFetcher.FileNameOf(Bytes([], "image/png", "attachment; filename=a.png")));
        Assert.Equal("a b.png", WebFetcher.FileNameOf(Bytes([], "image/png", "inline; filename=\"a b.png\"")));
        Assert.Equal("naïve.png", WebFetcher.FileNameOf(Bytes([], "image/png", "attachment; filename=\"x.png\"; filename*=UTF-8''na%C3%AFve.png")));
        Assert.Equal("evil.png", WebFetcher.FileNameOf(Bytes([], "image/png", "attachment; filename=\"..\\\\up/evil.png\"")));
        Assert.Equal("", WebFetcher.FileNameOf(Bytes([], "image/png", "attachment")));
    }

    [Fact]
    public async Task Download_OverTheCap_IsRefused_ByTheDeclaredLength_OrByTheBody_NeverCut()
    {
        // A Content-Length over the cap: refused unread. A body that outgrows it: refused after — the cap is 50 MB, so the test pins the sentences and the rule through a declared length.
        _http.Map("https://example.com/big", (_, _) => Task.FromResult(Bytes([1, 2, 3], "application/zip", length: WebFetcher.MaxFileDownloadBytes + 1)));

        var file = await Fetcher().DownloadAsync(new Uri("https://example.com/big"), Options(), CancellationToken.None);

        Assert.Equal(FetchOutcome.TooBig, file.Outcome);
        Assert.Equal("Error: 'https://example.com/big' is 50 MB, over the 50 MB download limit", file.Error);
        Assert.Empty(file.Bytes);
        Assert.Equal(50_000_000, WebFetcher.MaxFileDownloadBytes);
        Assert.Equal(TimeSpan.FromMinutes(2), WebFetcher.DownloadTimeout);
        Assert.Equal("Error: 'https://example.com/big' is over the 50 MB download limit", WebText.TooBig("https://example.com/big", 0, WebFetcher.MaxFileDownloadBytes));
        Assert.Equal("Error: 'https://example.com/big' is 60 MB, over the 50 MB download limit", WebText.TooBig("https://example.com/big", 60_000_000, WebFetcher.MaxFileDownloadBytes));
    }

    [Fact]
    public async Task Download_TheNetworkRule_AtTheFirstHost_AndAtARedirect()
    {
        _http.Map("https://example.com/go", (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("http://10.0.0.5/file.bin") } }));

        var hop = await Fetcher().DownloadAsync(new Uri("https://example.com/go"), Options(), CancellationToken.None);
        Assert.Equal(FetchOutcome.LanRefused, hop.Outcome);
        Assert.Equal(WebText.LanRefused("10.0.0.5"), hop.Error);
        Assert.Single(_http.Requests);

        var first = await Fetcher().DownloadAsync(new Uri("http://intranet.corp/file.bin"), Options(), CancellationToken.None);
        Assert.Equal(FetchOutcome.LanRefused, first.Outcome);
        Assert.Single(_http.Requests);

        var internet = await Fetcher().DownloadAsync(new Uri("https://example.com/x.bin"), Options(reach: NetworkReach.LocalAreaNetwork), CancellationToken.None);
        Assert.Equal(FetchOutcome.InternetRefused, internet.Outcome);
        Assert.Equal(WebText.InternetRefused("example.com"), internet.Error);

        Assert.Equal(FetchOutcome.NotHttp, (await Fetcher().DownloadAsync(new Uri("ftp://example.com/x"), Options(), CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Download_AnErrorStatus_IsBlockedOrFailed_WithNothingRead_AndNoBrowser()
    {
        _http.Map("https://example.com/gone", HttpStatusCode.NotFound, "no", "text/plain");
        _http.Map("https://example.com/wall", (_, _) => Task.FromResult(Html(HttpStatusCode.Forbidden, Cloudflare)));
        _browser.Executable = @"C:\edge.exe";
        _browser.Html = Article;

        var gone = await Fetcher().DownloadAsync(new Uri("https://example.com/gone"), Options(), CancellationToken.None);
        Assert.Equal(FetchOutcome.Failed, gone.Outcome);
        Assert.Equal("Error: could not fetch 'https://example.com/gone': HTTP 404 Not Found", gone.Error);
        Assert.Empty(gone.Bytes);

        var wall = await Fetcher().DownloadAsync(new Uri("https://example.com/wall"), Options(), CancellationToken.None);
        Assert.Equal(FetchOutcome.Blocked, wall.Outcome);
        Assert.Equal("Error: 'https://example.com/wall' refused the request (HTTP 403 Forbidden)", wall.Error);
        Assert.Empty(_browser.Runs);

        // A refused connection is the failure's message, as a page's is.
        var refused = await Fetcher().DownloadAsync(new Uri("https://example.com/nowhere"), Options(), CancellationToken.None);
        Assert.Equal(FetchOutcome.Failed, refused.Outcome);
        Assert.StartsWith("Error: could not fetch 'https://example.com/nowhere': ", refused.Error);
    }

    [Fact]
    public async Task Save_StreamsTheBody_UnderTheCap_ElseTooBig_AndAQuietServer_IsAStall()
    {
        // 2026-10-01 (download_file streamed to disk): no ceiling on the whole body, a stall timeout between reads instead.
        _http.Map("https://example.com/file.bin", (_, _) => Task.FromResult(TrickleStream.Response(new TrickleStream(new byte[200_000]), "application/octet-stream")));
        _http.Map("https://example.com/quiet.bin", (_, _) => Task.FromResult(TrickleStream.Response(new TrickleStream(new byte[10], hang: true), "application/octet-stream")));
        var fetcher = new WebFetcher(new HttpClient(_http), _browser, (host, _) => Task.FromResult(_dns[host])) { DownloadStallTimeout = TimeSpan.FromMilliseconds(200) };

        using (var opened = await fetcher.OpenDownloadAsync(new Uri("https://example.com/file.bin"), Options(), 1_000_000, CancellationToken.None))
        {
            Assert.True(opened.Ok);
            Assert.Equal(0, opened.Declared);
            using var target = new MemoryStream();
            var saved = await fetcher.SaveAsync(opened, target, 1_000_000, CancellationToken.None);
            Assert.True(saved.Ok);
            Assert.Equal(200_000, saved.Bytes);
            Assert.Equal(200_000, target.Length);
        }

        using (var opened = await fetcher.OpenDownloadAsync(new Uri("https://example.com/file.bin"), Options(), 100_000, CancellationToken.None))
        {
            var saved = await fetcher.SaveAsync(opened, Stream.Null, 100_000, CancellationToken.None);
            Assert.Equal(FetchOutcome.TooBig, saved.Outcome);
            Assert.Equal("Error: 'https://example.com/file.bin' is over the 100 KB download limit", saved.Error);
        }

        using (var opened = await fetcher.OpenDownloadAsync(new Uri("https://example.com/quiet.bin"), Options(), 1_000_000, CancellationToken.None))
        {
            var saved = await fetcher.SaveAsync(opened, Stream.Null, 1_000_000, CancellationToken.None);
            Assert.Equal(FetchOutcome.Timeout, saved.Outcome);
            Assert.Equal(WebText.Stalled("https://example.com/quiet.bin", TimeSpan.FromMilliseconds(200)), saved.Error);
        }

        Assert.Equal(TimeSpan.FromSeconds(60), WebFetcher.DefaultDownloadStallTimeout);
        Assert.Equal(TimeSpan.FromSeconds(60), Fetcher().DownloadStallTimeout);
        Assert.Equal("Error: 'https://example.com/x.bin' sent nothing for 60s; the download was dropped", WebText.Stalled("https://example.com/x.bin", TimeSpan.FromSeconds(60)));
    }

    [Fact]
    public async Task Download_TheCallersCancel_Escapes()
    {
        _http.Map("https://example.com/slow", async (_, ct) => { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return Bytes([1], "image/png"); });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Fetcher().DownloadAsync(new Uri("https://example.com/slow"), Options(), cts.Token));
    }

    [Fact]
    public void Types_AreClassified()
    {
        Assert.True(WebFetcher.IsHtml(""));
        Assert.True(WebFetcher.IsHtml("text/html"));
        Assert.True(WebFetcher.IsHtml("application/xhtml+xml"));
        Assert.False(WebFetcher.IsHtml("text/plain"));
        Assert.True(WebFetcher.IsText("text/markdown"));
        Assert.True(WebFetcher.IsText("application/json"));
        Assert.True(WebFetcher.IsText("image/svg+xml"));
        Assert.False(WebFetcher.IsText("image/png"));
        Assert.False(WebFetcher.IsText("application/pdf"));
        Assert.False(WebFetcher.IsText("application/octet-stream"));
        Assert.True(WebFetcher.IsBlockedStatus(403));
        Assert.True(WebFetcher.IsBlockedStatus(429));
        Assert.True(WebFetcher.IsBlockedStatus(503));
        Assert.False(WebFetcher.IsBlockedStatus(404));
        Assert.False(WebFetcher.IsBlockedStatus(500));
    }
    [Fact]
    public void RefusalLogLines_ArePinned()
    {
        Assert.Equal("Refused 10.0.0.5: a LAN address under internet", WebFetcher.RefusedLogLine("10.0.0.5", FetchOutcome.LanRefused, NetworkReach.Internet));
        Assert.Equal("Refused example.com: a public address under local_area_network", WebFetcher.RefusedLogLine("example.com", FetchOutcome.InternetRefused, NetworkReach.LocalAreaNetwork));
        Assert.Equal("GET http://twofaced.example/: refused at the socket, twofaced.example is a LAN address", WebFetcher.SocketRefusedLogLine(new Uri("http://twofaced.example/"), "twofaced.example", FetchOutcome.LanRefused));
        Assert.Equal("Web", WebFetcher.Category);
    }
}
