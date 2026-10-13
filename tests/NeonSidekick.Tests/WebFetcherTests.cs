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
        Assert.Equal(BrowserIdentity.Fallback.UserAgent, seen!.Headers.UserAgent.ToString());
        Assert.Equal("none", seen.Headers.GetValues("Sec-Fetch-Site").Single());
        Assert.Equal("navigate", seen.Headers.GetValues("Sec-Fetch-Mode").Single());
        Assert.Equal("en-US,en;q=0.9", seen.Headers.AcceptLanguage.ToString());
        Assert.Equal(BrowserIdentity.Fallback.SecChUa, seen.Headers.GetValues("Sec-CH-UA").Single());
        Assert.Equal("u=0, i", seen.Headers.GetValues("Priority").Single());
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
    public async Task ARedirect_KeepsTheFetchsOwnReferer_AndFoldsTheSiteOverTheChain()
    {
        // Code review, 2026-10-11: each hop named the hop before it as its Referer and judged the site afresh from there.
        var seen = new List<HttpRequestMessage>();
        HttpResponseMessage Moved(string to) => new(HttpStatusCode.MovedPermanently) { Headers = { Location = new Uri(to) } };
        _http.Map("http://example.com/page", (r, _) => { seen.Add(r); return Task.FromResult(Moved("https://www.example.com/page")); });
        _http.Map("https://example.com/typed", (r, _) => { seen.Add(r); return Task.FromResult(Moved("https://www.example.com/page")); });
        _http.Map("https://www.example.com/page", (r, _) => { seen.Add(r); return Task.FromResult(Html(HttpStatusCode.OK, Article)); });

        await Fetcher().FetchAsync(new Uri("http://example.com/page"), Options() with { Referer = DuckDuckGoSearch.Origin }, CancellationToken.None);
        await Fetcher().FetchAsync(new Uri("https://example.com/typed"), Options(), CancellationToken.None);
        await Fetcher().FetchAsync(new Uri("https://example.com/typed"), Options() with { Referer = new Uri("https://example.com/from#frag") }, CancellationToken.None);

        // A click from the results page: none to the plain-http hop (a downgrade), the results page's origin after; cross-site throughout.
        Assert.False(seen[0].Headers.Contains("Referer"));
        Assert.Equal(DuckDuckGoSearch.Origin.AbsoluteUri, seen[1].Headers.GetValues("Referer").Single());
        Assert.All(seen.Take(2), r => Assert.Equal("cross-site", r.Headers.GetValues("Sec-Fetch-Site").Single()));

        // A typed address stays typed, redirected or not.
        Assert.All(seen.Skip(2).Take(2), r =>
        {
            Assert.False(r.Headers.Contains("Referer"));
            Assert.Equal("none", r.Headers.GetValues("Sec-Fetch-Site").Single());
        });

        // From a page on the site: the whole URL (no fragment) to its own origin, the origin alone to www; same-origin, then same-site.
        Assert.Equal("https://example.com/from", seen[4].Headers.GetValues("Referer").Single());
        Assert.Equal("same-origin", seen[4].Headers.GetValues("Sec-Fetch-Site").Single());
        Assert.Equal("https://example.com/", seen[5].Headers.GetValues("Referer").Single());
        Assert.Equal("same-site", seen[5].Headers.GetValues("Sec-Fetch-Site").Single());
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
    public async Task TheBrowsersCookies_GoIntoTheJar_AndTheClientClaimsThatBrowserOnTheirHosts()
    {
        // 2026-10-11, the user's ask: a clearance the browser earned lets the client in next time, as the browser it was earned by.
        var jar = new CookieContainer();
        var fetcher = new WebFetcher(new HttpClient(_http), _browser, (host, _) => Task.FromResult(_dns[host]), jar);
        var requests = new List<HttpRequestMessage>();
        bool cleared = false;
        _http.Map("https://example.com/", (r, _) => { requests.Add(r); return Task.FromResult(cleared ? Html(HttpStatusCode.OK, Article) : Html(HttpStatusCode.Forbidden, Cloudflare)); });
        _http.Map("https://www.iana.org/", (r, _) => { requests.Add(r); return Task.FromResult(Html(HttpStatusCode.OK, Article)); });
        _browser.Html = Article;
        _browser.Identity = new BrowserIdentity(BrowserBrand.Edge, 155, BrowserPlatform.Windows);
        _browser.FinalUrl = "https://example.com/";
        _browser.Cookies.Add(new BrowserCookie("cf_clearance", "abc", ".example.com", "/", -1, true, true));
        _browser.Cookies.Add(new BrowserCookie("other", "x", ".example.org", "/", -1, true, false));

        var first = await fetcher.FetchAsync(new Uri("https://example.com/"), Options(), CancellationToken.None);
        cleared = true;
        var second = await fetcher.FetchAsync(new Uri("https://example.com/"), Options(), CancellationToken.None);
        await fetcher.FetchAsync(new Uri("https://www.iana.org/"), Options(), CancellationToken.None);

        Assert.Equal(WebText.BrowserEngine, first.Engine);
        Assert.Equal(WebText.HttpEngine, second.Engine);
        Assert.Single(_browser.Runs);
        Assert.Equal("cf_clearance=abc", jar.GetCookieHeader(new Uri("https://example.com/")));
        // The first request claimed Chrome, as every host does until the browser hands it cookies; then Edge on that host alone.
        string chrome = (_browser.Identity with { Brand = BrowserBrand.Chrome }).UserAgent;
        Assert.Equal(chrome, requests[0].Headers.UserAgent.ToString());
        Assert.Equal(_browser.Identity.UserAgent, requests[1].Headers.UserAgent.ToString());
        Assert.Equal(_browser.Identity.SecChUa, requests[1].Headers.GetValues("Sec-CH-UA").Single());
        Assert.Equal(chrome, requests[2].Headers.UserAgent.ToString());
    }

    [Fact]
    public async Task TheClaim_CoversTheCookiesScope_ForAsLongAsTheyLast_AndOnlyForCookiesTheJarTook()
    {
        // Code review, 2026-10-11: the claim was by exact host and for ever, from every cookie handed over rather than those the jar took.
        var time = new ManualTimeProvider();
        var jar = new CookieContainer();
        var fetcher = new WebFetcher(new HttpClient(_http), _browser, (host, _) => Task.FromResult(_dns.TryGetValue(host, out var a) ? a : throw new System.Net.Sockets.SocketException(11001)), jar, time);
        var agents = new List<(string Url, string Agent)>();
        foreach (string page in new[] { "https://shop.example.com/", "https://www.iana.org/" })
        {
            _http.Map(page, (r, _) => { agents.Add((page, r.Headers.UserAgent.ToString())); return Task.FromResult(Html(HttpStatusCode.OK, Article)); });
        }

        _http.Map("https://example.com/", HttpStatusCode.Forbidden, Cloudflare, "text/html");
        _browser.Html = Article;
        _browser.Identity = new BrowserIdentity(BrowserBrand.Edge, 155, BrowserPlatform.Windows);
        _browser.Cookies.Add(new BrowserCookie("cf_clearance", "abc", ".example.com", "/", time.GetUtcNow().ToUnixTimeSeconds() + 60, true, true));
        _browser.Cookies.Add(new BrowserCookie("bad", "a;b", "www.iana.org", "/", -1, true, false));

        Assert.Equal(WebText.BrowserEngine, (await fetcher.FetchAsync(new Uri("https://example.com/"), Options(), CancellationToken.None)).Engine);
        await fetcher.FetchAsync(new Uri("https://shop.example.com/"), Options(), CancellationToken.None);
        await fetcher.FetchAsync(new Uri("https://www.iana.org/"), Options(), CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(61));
        await fetcher.FetchAsync(new Uri("https://shop.example.com/"), Options(), CancellationToken.None);

        string chrome = (_browser.Identity with { Brand = BrowserBrand.Chrome }).UserAgent;
        Assert.Equal(
            [("https://shop.example.com/", _browser.Identity.UserAgent), ("https://www.iana.org/", chrome), ("https://shop.example.com/", chrome)],
            agents);
    }

    [Fact]
    public async Task TheBrowsersCookies_StayOut_WhenItStillShowsABotCheck_OrThereIsNoJar()
    {
        var jar = new CookieContainer();
        var withJar = new WebFetcher(new HttpClient(_http), _browser, (host, _) => Task.FromResult(_dns[host]), jar);
        var requests = new List<HttpRequestMessage>();
        _http.Map("https://example.com/", (r, _) => { requests.Add(r); return Task.FromResult(Html(HttpStatusCode.Forbidden, "<p>denied</p>")); });
        _browser.Identity = new BrowserIdentity(BrowserBrand.Edge, 155, BrowserPlatform.Windows);
        _browser.Cookies.Add(new BrowserCookie("__cf_bm", "abc", ".example.com", "/", -1, true, true));

        _browser.Html = Cloudflare;
        Assert.Equal(FetchOutcome.Blocked, (await withJar.FetchAsync(new Uri("https://example.com/"), Options(), CancellationToken.None)).Outcome);
        await withJar.FetchAsync(new Uri("https://example.com/"), Options(), CancellationToken.None);
        Assert.Equal(0, jar.Count);

        // No jar: the page is the browser's, and the client goes on claiming Chrome there.
        var without = Fetcher();
        _browser.Html = Article;
        Assert.True((await without.FetchAsync(new Uri("https://example.com/"), Options(), CancellationToken.None)).Ok);
        await without.FetchAsync(new Uri("https://example.com/"), Options(), CancellationToken.None);
        Assert.All(requests, r => Assert.DoesNotContain("Edg/", r.Headers.UserAgent.ToString(), StringComparison.Ordinal));
        Assert.Equal(4, requests.Count);
    }

    // ── Rate limits and pacing (2026-10-11) ─────────────────────────────────

    private readonly ManualTimeProvider _time = new();

    private WebFetcher Timed(TimeSpan hostInterval = default) =>
        new(new HttpClient(_http), _browser, (host, _) => Task.FromResult(_dns[host]), time: _time) { HostInterval = hostInterval };

    private static HttpResponseMessage RateLimited(HttpStatusCode status, TimeSpan wait, string body = "<p>slow down</p>")
    {
        var response = Html(status, body);
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(wait);
        return response;
    }

    /// <summary>Waits until the fetch under test sits on a timer of <see cref="_time"/>, so an advance reaches it.</summary>
    private async Task OnATimerAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (_time.TimerCount == 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "the fetch never waited on the clock");
            await Task.Delay(5);
        }
    }

    [Fact]
    public void RetryAfterOf_ReadsSecondsOrADate()
    {
        var now = ManualTimeProvider.DefaultUtcNow;
        Assert.Equal(TimeSpan.FromSeconds(30), WebFetcher.RetryAfterOf(new(TimeSpan.FromSeconds(30)), now));
        Assert.Equal(TimeSpan.FromMinutes(2), WebFetcher.RetryAfterOf(new(now.AddMinutes(2)), now));
        Assert.Equal(TimeSpan.Zero, WebFetcher.RetryAfterOf(new(now.AddMinutes(-2)), now));
        Assert.Null(WebFetcher.RetryAfterOf(null, now));
    }

    [Fact]
    public async Task AShortRetryAfter_IsWaitedOut_AndThePageAskedForOnceMore()
    {
        int asked = 0;
        _http.Map("https://example.com/", (_, _) => Task.FromResult(++asked == 1 ? RateLimited(HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(3)) : Html(HttpStatusCode.OK, Article)));

        var fetch = Timed().FetchAsync(new Uri("https://example.com/"), Options(), CancellationToken.None);
        await OnATimerAsync();
        Assert.False(fetch.IsCompleted);
        Assert.Equal(1, asked);
        _time.Advance(TimeSpan.FromSeconds(3));
        var page = await fetch;

        Assert.True(page.Ok);
        Assert.Equal(WebText.HttpEngine, page.Engine);
        Assert.Equal(2, asked);
        Assert.Empty(_browser.Runs);
    }

    [Fact]
    public async Task ALongRetryAfter_IsReported_NotWaited_AndTheBrowserIsNotSentIn()
    {
        _http.Map("https://example.com/", (_, _) => Task.FromResult(RateLimited(HttpStatusCode.TooManyRequests, TimeSpan.FromMinutes(2))));
        _browser.Html = Article;

        var page = await Timed().FetchAsync(new Uri("https://example.com/"), Options(), CancellationToken.None);

        Assert.Equal(FetchOutcome.Blocked, page.Outcome);
        Assert.Equal(WebText.Blocked("https://example.com/", "HTTP 429 Too Many Requests" + WebText.AskedToWait(TimeSpan.FromMinutes(2))), page.Error);
        Assert.Single(_http.Requests);
        Assert.Empty(_browser.Runs);
    }

    [Fact]
    public async Task ARateLimitThatHolds_AfterTheWait_IsTheAnswer_WithNoBrowser()
    {
        _http.Map("https://example.com/", (_, _) => Task.FromResult(RateLimited(HttpStatusCode.ServiceUnavailable, TimeSpan.FromSeconds(1))));
        _browser.Html = Article;

        var fetch = Timed().FetchAsync(new Uri("https://example.com/"), Options(), CancellationToken.None);
        await OnATimerAsync();
        _time.Advance(TimeSpan.FromSeconds(1));
        var page = await fetch;

        Assert.Equal(FetchOutcome.Blocked, page.Outcome);
        Assert.EndsWith(WebText.AskedToWait(TimeSpan.FromSeconds(1)) + ")", page.Error);
        Assert.Equal(2, _http.Requests.Count);
        Assert.Empty(_browser.Runs);
    }

    [Fact]
    public async Task ARateLimit_RefusedAgainWithNoWaitNamed_StillKeepsTheBrowserOut()
    {
        // Code review, 2026-10-11: the second answer named no wait, so the rate limit was forgotten and the browser sent in.
        int asked = 0;
        _http.Map("https://example.com/", (_, _) => Task.FromResult(++asked == 1 ? RateLimited(HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(3)) : Html(HttpStatusCode.TooManyRequests, "<p>slow down</p>")));
        _browser.Html = Article;

        var fetch = Timed().FetchAsync(new Uri("https://example.com/"), Options(), CancellationToken.None);
        await OnATimerAsync();
        _time.Advance(TimeSpan.FromSeconds(3));
        var page = await fetch;

        Assert.Equal(FetchOutcome.Blocked, page.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(3), page.RetryAfter);
        Assert.EndsWith(WebText.AskedToWait(TimeSpan.FromSeconds(3)) + ")", page.Error);
        Assert.Equal(2, asked);
        Assert.Empty(_browser.Runs);
    }

    [Fact]
    public async Task AChallengePage_ThatNamesAWait_IsStillABotCheck_ForTheBrowser()
    {
        _http.Map("https://example.com/", (_, _) => Task.FromResult(RateLimited(HttpStatusCode.ServiceUnavailable, TimeSpan.FromSeconds(5), Cloudflare)));
        _browser.Html = Article;

        var page = await Timed().FetchAsync(new Uri("https://example.com/"), Options(), CancellationToken.None);

        Assert.True(page.Ok);
        Assert.Equal(WebText.BrowserEngine, page.Engine);
        Assert.Single(_http.Requests);
    }

    [Fact]
    public async Task Pacing_SpacesOneHostsRequests_AndLeavesOtherHostsAlone()
    {
        _http.Map("https://example.com/", HttpStatusCode.OK, Article, "text/html");
        _http.Map("https://www.iana.org/", HttpStatusCode.OK, Article, "text/html");
        var fetcher = Timed(TimeSpan.FromSeconds(1));

        Assert.True((await fetcher.FetchAsync(new Uri("https://example.com/a"), Options(), CancellationToken.None)).Ok);
        var again = fetcher.FetchAsync(new Uri("https://example.com/b"), Options(), CancellationToken.None);
        Assert.True((await fetcher.FetchAsync(new Uri("https://www.iana.org/"), Options(), CancellationToken.None)).Ok);
        await OnATimerAsync();

        Assert.False(again.IsCompleted);
        Assert.Equal(2, _http.Requests.Count);
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.True((await again).Ok);
        Assert.Equal("https://example.com/b", _http.Requests[^1].Uri.AbsoluteUri);
    }

    /// <summary>Code review, 2026-10-11: the browser sent in after the HTTP leg waited for that leg's own turn, up to a second for nothing.</summary>
    [Fact]
    public async Task Pacing_TheBrowserAfterTheHttpLeg_GoesAtOnce_AndTheNextRequestStillWaits()
    {
        _http.Map("https://example.com/", HttpStatusCode.Forbidden, "<p>denied</p>", "text/html");
        _browser.Html = Article;
        var fetcher = Timed(TimeSpan.FromSeconds(1));

        var first = fetcher.FetchAsync(new Uri("https://example.com/"), Options(), CancellationToken.None);
        Assert.Same(first, await Task.WhenAny(first, Task.Delay(TimeSpan.FromSeconds(10))));
        Assert.True((await first).Ok);
        Assert.Single(_browser.Runs);

        var next = fetcher.FetchAsync(new Uri("https://example.com/"), Options(), CancellationToken.None);
        await OnATimerAsync();
        Assert.False(next.IsCompleted);
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.True((await next).Ok);
    }

    /// <summary>Code review, 2026-10-11: a cancelled wait kept its slot, so the next request waited a turn for nothing.</summary>
    [Fact]
    public async Task Pacing_ACancelledWait_GivesItsTurnBack()
    {
        _http.Map("https://example.com/", HttpStatusCode.OK, Article, "text/html");
        var fetcher = Timed(TimeSpan.FromSeconds(1));
        Assert.True((await fetcher.FetchAsync(new Uri("https://example.com/a"), Options(), CancellationToken.None)).Ok);

        using var cancel = new CancellationTokenSource();
        var cancelled = fetcher.FetchAsync(new Uri("https://example.com/b"), Options(), cancel.Token);
        await OnATimerAsync();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        _time.Advance(TimeSpan.FromSeconds(1));

        var next = fetcher.FetchAsync(new Uri("https://example.com/c"), Options(), CancellationToken.None);
        Assert.Same(next, await Task.WhenAny(next, Task.Delay(TimeSpan.FromSeconds(10))));
        Assert.True((await next).Ok);
        Assert.Equal(2, _http.Requests.Count);
    }

    [Fact]
    public async Task Headers_GoInChromesOrder_WithAnAcceptGivenInItsPlace()
    {
        var requests = new List<HttpRequestMessage>();
        _http.Map("https://example.com/", (r, _) => { requests.Add(r); return Task.FromResult(Html(HttpStatusCode.OK, Article)); });

        await Fetcher().FetchAsync(new Uri("https://example.com/"), Options(), CancellationToken.None);
        await Fetcher().FetchAsync(new Uri("https://example.com/b"), Options() with { Referer = new Uri("https://other.org/"), Accept = "application/vnd.github+json" }, CancellationToken.None);

        Assert.Equal(BrowserHeaders.Order.Where(h => h != "Referer"), requests[0].Headers.Select(h => h.Key));
        Assert.Equal(BrowserHeaders.Order, requests[1].Headers.Select(h => h.Key));
        Assert.Equal("application/vnd.github+json", requests[1].Headers.GetValues("Accept").Single());
        Assert.Equal("gzip, deflate, br", string.Join(", ", requests[0].Headers.GetValues("Accept-Encoding")));
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
        Assert.Equal("Refused example.com: a public address under lan", WebFetcher.RefusedLogLine("example.com", FetchOutcome.InternetRefused, NetworkReach.LocalAreaNetwork));
        Assert.Equal("GET http://twofaced.example/: refused at the socket, twofaced.example is a LAN address", WebFetcher.SocketRefusedLogLine(new Uri("http://twofaced.example/"), "twofaced.example", FetchOutcome.LanRefused));
        Assert.Equal("Web", WebFetcher.Category);
    }
}
