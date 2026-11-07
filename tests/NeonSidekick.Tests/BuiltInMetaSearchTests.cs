using System.Net;
using System.Net.Sockets;
using System.Text;
using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.Web;
using SearXNG.Net;
using SearXNG.Net.Engines;

namespace NeonSidekick.Tests;

public class BuiltInMetaSearchTests
{
    private static readonly FetchOptions Options = new(FetchEngine.HttpClient, "", NetworkReach.Internet);

    private readonly StubHttpMessageHandler _http = new();
    private readonly ManualTimeProvider _time = new();

    private static FakeEngine Failing(string name, EngineFailureKind kind) =>
        new(name, _ => throw new EngineException(kind, name + " said no"));

    private BuiltInMetaSearch Search(params IEngine[] engines) => new(engines, new HttpClient(_http), _time);

    /// <summary>Only proves AOT on the published exe, but it must pass here first.</summary>
    [Fact]
    public void SmokeProbe_MetaSearch_Passes()
    {
        var check = NeonSidekick.App.SmokeChecks.ProbeMetaSearch();
        Assert.True(check.Passed, check.Detail);
        Assert.Equal("5 results", check.Detail);
    }

    [Fact]
    public async Task SearchAsync_MergesTheEngines_AndKeepsToTheCount()
    {
        var one = new FakeEngine("one", new EngineResult("https://a.example/", " A \n title ", "a  snippet"), new EngineResult("https://b.example/", "B", ""));
        var two = new FakeEngine("two", new EngineResult("https://a.example/", "A", "other"), new EngineResult("https://c.example/", "C", ""));

        var outcome = await Search(one, two).For(new AppSettingsData()).SearchAsync("  native aot  ", 2, Options, CancellationToken.None);

        Assert.True(outcome.Ok);
        Assert.Equal(["https://a.example/", "https://b.example/"], outcome.Results.Select(r => r.Url));
        Assert.Equal("A title", outcome.Results[0].Title);
        Assert.Equal("native aot", Assert.Single(one.Queries).Query);
        Assert.Equal("en-US", one.Queries[0].Locale);
    }

    [Fact]
    public async Task SearchAsync_WithSomeEnginesFailing_StillAnswers()
    {
        var outcome = await Search(new FakeEngine("one", new EngineResult("https://a.example/", "A", "")), Failing("two", EngineFailureKind.Captcha))
            .For(new AppSettingsData()).SearchAsync("x", 5, Options, CancellationToken.None);

        Assert.True(outcome.Ok);
        Assert.Single(outcome.Results);
    }

    [Fact]
    public async Task SearchAsync_WhenTheEnginesAnswerWithNothing_IsNoResults_NotAnError()
    {
        var outcome = await Search(new FakeEngine("one"), new FakeEngine("two")).For(new AppSettingsData()).SearchAsync("x", 5, Options, CancellationToken.None);

        Assert.True(outcome.Ok);
        Assert.Empty(outcome.Results);
    }

    /// <summary>"No results" would say the web has nothing when an engine was never heard, and the log is all that would say otherwise.</summary>
    [Theory]
    [InlineData(EngineFailureKind.Captcha, "could not be searched")]
    [InlineData(EngineFailureKind.Timeout, "could not be searched")]
    public async Task SearchAsync_WithNoResults_AndAFailure_IsAnError_NamingIt(EngineFailureKind kind, string expected)
    {
        var outcome = await Search(new FakeEngine("one"), Failing("two", kind)).For(new AppSettingsData()).SearchAsync("x", 5, Options, CancellationToken.None);

        Assert.False(outcome.Ok);
        Assert.Contains(expected, outcome.Error);
        Assert.Contains("two: two said no", outcome.Error);
    }

    private static async Task<List<DiagnosticEvent>> LoggedDuring(Func<Task> act)
    {
        var events = new List<DiagnosticEvent>();
        Action<DiagnosticEvent> capture = e =>
        {
            if (e.Category == "Web" && e.Level >= DiagnosticLevel.Info && e.Message.StartsWith("Metasearch", StringComparison.Ordinal))
            {
                events.Add(e);
            }
        };
        DiagnosticLog.Emitted += capture;
        try
        {
            await act();
        }
        finally
        {
            DiagnosticLog.Emitted -= capture;
        }

        return events;
    }

    /// <summary>Warnings reach the transcript and headless output, and a slow or resting engine would print one on every search.</summary>
    [Fact]
    public async Task AFailure_IsLoggedQuietly_WhetherOrNotTheSearchAnswered()
    {
        var partly = await LoggedDuring(() => Search(new FakeEngine("one", new EngineResult("https://a.example/", "A", "")), Failing("two", EngineFailureKind.Captcha)).For(new AppSettingsData()).SearchAsync("x", 5, Options, CancellationToken.None));
        var wholly = await LoggedDuring(() => Search(Failing("two", EngineFailureKind.Captcha)).For(new AppSettingsData()).SearchAsync("x", 5, Options, CancellationToken.None));

        Assert.Contains(partly, e => e.Message.Contains("two: two said no", StringComparison.Ordinal));
        Assert.Contains(wholly, e => e.Message.Contains("two: two said no", StringComparison.Ordinal));
        Assert.All(partly.Concat(wholly), e => Assert.Equal(DiagnosticLevel.Info, e.Level));
    }

    /// <summary>No engine asked, so none is left resting after a switch back, and no wait: two in a row answer at once.</summary>
    [Fact]
    public async Task SearchAsync_UnderLanMode_AnswersAtOnce_AsksNobody()
    {
        var engine = new FakeEngine("one", new EngineResult("https://a.example/", "A", ""));
        var search = Search(engine);
        var lan = Options with { Reach = NetworkReach.LocalAreaNetwork };

        var first = search.For(new AppSettingsData()).SearchAsync("x", 5, lan, CancellationToken.None);
        var second = search.For(new AppSettingsData()).SearchAsync("y", 5, lan, CancellationToken.None);

        Assert.True(first.IsCompleted);
        Assert.True(second.IsCompleted);
        Assert.Equal(WebText.MetaSearchNeedsInternet, (await first).Error);
        Assert.Equal(WebText.MetaSearchNeedsInternet, (await second).Error);
        Assert.Empty(engine.Queries);

        // Nor did it use up the next real search's turn.
        var real = search.For(new AppSettingsData()).SearchAsync("z", 5, Options, CancellationToken.None);
        Assert.True(real.IsCompleted);
        Assert.Single((await real).Results);
    }

    [Fact]
    public async Task AnAnswerPastTheCap_FailsThatEngine_ButOneUnderItIsRead()
    {
        const string Url = "https://en.wikipedia.org/api/rest_v1/page/summary/Rust";
        static string Article(int padding) => "{\"title\":\"Rust\",\"content_urls\":{\"desktop\":{\"page\":\"https://en.wikipedia.org/wiki/Rust\"}},\"description\":\"" + new string('x', padding) + "\"}";

        var under = new StubHttpMessageHandler().Map(Url, (_, _) => Task.FromResult(StubHttpMessageHandler.Json(HttpStatusCode.OK, Article(400_000))));
        var read = await new BuiltInMetaSearch([new WikipediaEngine()], new HttpClient(under), _time).For(new AppSettingsData()).SearchAsync("Rust", 5, Options, CancellationToken.None);
        Assert.Equal("Rust", Assert.Single(read.Results).Title);

        var over = new StubHttpMessageHandler().Map(Url, (_, _) => Task.FromResult(StubHttpMessageHandler.Json(HttpStatusCode.OK, Article((int)BuiltInMetaSearch.MaxResponseBytes))));
        var refused = await new BuiltInMetaSearch([new WikipediaEngine()], new HttpClient(over), _time).For(new AppSettingsData()).SearchAsync("Rust", 5, Options, CancellationToken.None);
        Assert.False(refused.Ok);
        Assert.Contains("could not be searched", refused.Error);
        Assert.Contains("wikipedia", refused.Error);
    }

    /// <summary>Only the outer client's cap may buffer: this inner one would throw at the fifth byte if the handler made it read the body.</summary>
    [Fact]
    public async Task Handler_LeavesTheBodyToTheOuterClient()
    {
        var http = new StubHttpMessageHandler().Map("https://a.example/", (_, _) => Task.FromResult(StubHttpMessageHandler.Json(HttpStatusCode.OK, "longer than four bytes")));
        using var library = new HttpClient(new BuiltInMetaSearch.HostClientHandler(new HttpClient(http) { MaxResponseContentBufferSize = 4 }));

        using var response = await library.GetAsync("https://a.example/");

        Assert.Equal("longer than four bytes", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(EngineFailureKind.Captcha, "refused the search")]
    [InlineData(EngineFailureKind.TooManyRequests, "refused the search")]
    [InlineData(EngineFailureKind.AccessDenied, "refused the search")]
    [InlineData(EngineFailureKind.UnexpectedResponse, "could not be searched")]
    public async Task SearchAsync_WhenEveryEngineFails_SaysWhy(EngineFailureKind kind, string expected)
    {
        var outcome = await Search(Failing("one", kind)).For(new AppSettingsData()).SearchAsync("x", 5, Options, CancellationToken.None);

        Assert.False(outcome.Ok);
        Assert.StartsWith("Error: " + WebText.MetaSearchName, outcome.Error);
        Assert.Contains(expected, outcome.Error);
        Assert.Contains("one: one said no", outcome.Error);
        Assert.Empty(outcome.Results);
    }

    [Fact]
    public async Task SearchAsync_RemembersARestingEngine_BetweenSearches()
    {
        int calls = 0;
        var engine = new FakeEngine("one", _ =>
        {
            calls++;
            throw new EngineException(EngineFailureKind.TooManyRequests, "slow down", TimeSpan.FromMinutes(3));
        });
        var search = Search(engine);

        await search.For(new AppSettingsData()).SearchAsync("x", 5, Options, CancellationToken.None);
        _time.Advance(BuiltInMetaSearch.MinInterval);
        var second = await search.For(new AppSettingsData()).SearchAsync("y", 5, Options, CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.Contains("refused the search", second.Error);
    }

    [Fact]
    public async Task SearchAsync_SpacesItsSearches()
    {
        var search = Search(new FakeEngine("one", new EngineResult("https://a.example/", "A", "")));
        await search.For(new AppSettingsData()).SearchAsync("x", 5, Options, CancellationToken.None);

        var second = search.For(new AppSettingsData()).SearchAsync("y", 5, Options, CancellationToken.None);
        Assert.False(second.IsCompleted);

        _time.Advance(BuiltInMetaSearch.MinInterval);
        Assert.True((await second).Ok);
    }

    [Fact]
    public async Task SearchAsync_CanceledWhileWaiting_Throws()
    {
        var search = Search(new FakeEngine("one", new EngineResult("https://a.example/", "A", "")));
        await search.For(new AppSettingsData()).SearchAsync("x", 5, Options, CancellationToken.None);
        using var cancel = new CancellationTokenSource();

        var second = search.For(new AppSettingsData()).SearchAsync("y", 5, Options, cancel.Token);
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
    }

    [Fact]
    public async Task SearchAsync_WithNothingToAsk_SkipsThePause()
    {
        var search = Search(Failing("one", EngineFailureKind.Captcha)).For(new AppSettingsData());
        await search.SearchAsync("x", 5, Options, CancellationToken.None);

        var second = search.SearchAsync("y", 5, Options, CancellationToken.None);

        Assert.True(second.IsCompleted);
        Assert.Contains("refused the search", (await second).Error);
    }

    [Fact]
    public async Task SearchAsync_RestingAfterATimeout_CanBeRetried_AndAfterARefusal_IsRefused()
    {
        var timedOut = Search(Failing("one", EngineFailureKind.Timeout)).For(new AppSettingsData());
        await timedOut.SearchAsync("x", 5, Options, CancellationToken.None);
        _time.Advance(BuiltInMetaSearch.MinInterval);
        Assert.Contains("could not be searched", (await timedOut.SearchAsync("y", 5, Options, CancellationToken.None)).Error);

        var refused = Search(Failing("one", EngineFailureKind.AccessDenied)).For(new AppSettingsData());
        await refused.SearchAsync("x", 5, Options, CancellationToken.None);
        _time.Advance(BuiltInMetaSearch.MinInterval);
        Assert.Contains("refused the search", (await refused.SearchAsync("y", 5, Options, CancellationToken.None)).Error);
    }

    [Fact]
    public async Task SearchAsync_WhenEveryEngineRefuses_SaysRetryingWontHelp()
    {
        var outcome = await Search(Failing("one", EngineFailureKind.Captcha), Failing("two", EngineFailureKind.AccessDenied)).For(new AppSettingsData()).SearchAsync("x", 5, Options, CancellationToken.None);

        Assert.Contains("refused the search", outcome.Error);
    }

    [Theory]
    [InlineData("ftp://a.example/f")]
    [InlineData("mailto:a@example.com")]
    [InlineData("https://xn--\u00fc.de/")]
    public async Task SearchAsync_AHitThatGivesNoLink_TakesNoSlot_AndCountsAsNoResults(string url)
    {
        var outcome = await Search(new FakeEngine("one", new EngineResult(url, "A", "")), Failing("two", EngineFailureKind.Timeout)).For(new AppSettingsData()).SearchAsync("x", 1, Options, CancellationToken.None);
        Assert.False(outcome.Ok);

        var kept = await Search(new FakeEngine("one", new EngineResult(url, "A", ""), new EngineResult("https://good.example/", "B", ""))).For(new AppSettingsData()).SearchAsync("x", 1, Options, CancellationToken.None);
        Assert.Equal("https://good.example/", Assert.Single(kept.Results).Url);
    }

    /// <summary>The app's one rule for links: a look-alike host stays visible as what it really is.</summary>
    [Theory]
    [InlineData("https://xn--80ak6aa92e.example/", "https://xn--80ak6aa92e.example/")]
    [InlineData("https://a.example/\u00e4 b", "https://a.example/%C3%A4%20b")]
    public async Task SearchAsync_HandsOutTheLinkTheAppWouldShow(string url, string expected)
    {
        var outcome = await Search(new FakeEngine("one", new EngineResult(url, "A", ""))).For(new AppSettingsData()).SearchAsync("x", 5, Options, CancellationToken.None);

        Assert.Equal(expected, Assert.Single(outcome.Results).Url);
    }

    [Fact]
    public void TheRealEngine_NamesItself_AndSendsNoReferer()
    {
        var search = new BuiltInMetaSearch(new HttpClient(_http), _time);

        Assert.Equal(WebText.MetaSearchName, search.For(new AppSettingsData()).Name);
        Assert.Null(search.For(new AppSettingsData()).ResultReferer);
    }

    [Fact]
    public async Task TheRealEngines_FollowARedirect_ThroughTheAppsClient()
    {
        // The app's client follows none, and Wikipedia's API redirects a title to its article's.
        const string Article = """{"title":"Rust","content_urls":{"desktop":{"page":"https://en.wikipedia.org/wiki/Rust"}},"description":"Iron oxide"}""";
        var http = new StubHttpMessageHandler()
            .Map("https://en.wikipedia.org/api/rest_v1/page/summary/Rust", (_, _) => Task.FromResult(StubHttpMessageHandler.Json(HttpStatusCode.OK, Article)))
            .Map("https://en.wikipedia.org/api/rest_v1/page/summary/Old", (_, _) => Task.FromResult(Redirect("/api/rest_v1/page/summary/Rust")));
        var search = new BuiltInMetaSearch([new WikipediaEngine()], new HttpClient(http), _time);

        var outcome = await search.For(new AppSettingsData()).SearchAsync("Old", 5, Options, CancellationToken.None);

        Assert.Equal("Rust", Assert.Single(outcome.Results).Title);
    }

    private static HttpResponseMessage Redirect(string location, HttpStatusCode status = HttpStatusCode.Found)
    {
        var response = StubHttpMessageHandler.Json(status, "");
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }

    private static HttpClient Client(StubHttpMessageHandler http) =>
        new(new BuiltInMetaSearch.HostClientHandler(new HttpClient(http)));

    [Fact]
    public async Task Handler_SendsACopy_WithTheHeaders()
    {
        string? agent = null;
        var http = new StubHttpMessageHandler().Map("https://a.example/", (request, _) =>
        {
            agent = request.Headers.NonValidated.TryGetValues("User-Agent", out var values) ? values.ToString() : null;
            return Task.FromResult(StubHttpMessageHandler.Json(HttpStatusCode.OK, "fine"));
        });
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://a.example/");
        request.Headers.TryAddWithoutValidation("User-Agent", "Agent/1 (odd; value)");

        using var response = await Client(http).SendAsync(request);

        Assert.Equal("fine", await response.Content.ReadAsStringAsync());
        Assert.Equal("Agent/1 (odd; value)", agent);
    }

    [Theory]
    [InlineData("https://a.example/next", true)] // The same site keeps its cookies.
    [InlineData("https://b.example/next", false)] // Another site doesn't get them.
    public async Task Handler_SendsCookiesToTheirOwnSiteOnly(string target, bool kept)
    {
        bool? hadCookie = null;
        var http = new StubHttpMessageHandler()
            .Map(target, (request, _) =>
            {
                hadCookie = request.Headers.NonValidated.Contains("Cookie");
                return Task.FromResult(StubHttpMessageHandler.Json(HttpStatusCode.OK, "end"));
            })
            .Map("https://a.example/start", (_, _) => Task.FromResult(Redirect(target)));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://a.example/start");
        request.Headers.TryAddWithoutValidation("Cookie", "secret=1");

        using var response = await Client(http).SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(kept, hadCookie);
    }

    [Fact]
    public async Task Handler_WontFollowToPlainHttp()
    {
        var http = new StubHttpMessageHandler().Map("https://a.example/", (_, _) => Task.FromResult(Redirect("http://a.example/plain")));

        using var response = await Client(http).SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://a.example/"));

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Single(http.Requests);
    }

    [Fact]
    public async Task Handler_StopsAtTheLimit_AndLeavesTheRedirectForTheEngine()
    {
        var http = new StubHttpMessageHandler().Map("https://a.example/", (_, _) => Task.FromResult(Redirect("https://a.example/again")));

        using var response = await Client(http).SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://a.example/"));

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal(BuiltInMetaSearch.HostClientHandler.MaxRedirects + 1, http.Requests.Count);
    }

    [Fact]
    public async Task Handler_ARedirectedFormBecomesAGet_ButNeverResendsIt()
    {
        var found = new StubHttpMessageHandler()
            .Map("https://a.example/next", (_, _) => Task.FromResult(StubHttpMessageHandler.Json(HttpStatusCode.OK, "end")))
            .Map("https://a.example/form", (_, _) => Task.FromResult(Redirect("https://a.example/next")));
        using var post = new HttpRequestMessage(HttpMethod.Post, "https://a.example/form") { Content = new StringContent("a=1") };
        using var followed = await Client(found).SendAsync(post);
        Assert.Equal(HttpMethod.Get, found.Requests[^1].Method);

        var kept = new StubHttpMessageHandler().Map("https://a.example/form", (_, _) => Task.FromResult(Redirect("https://a.example/next", HttpStatusCode.TemporaryRedirect)));
        using var again = new HttpRequestMessage(HttpMethod.Post, "https://a.example/form") { Content = new StringContent("a=1") };
        using var left = await Client(kept).SendAsync(again);
        Assert.Equal(HttpStatusCode.TemporaryRedirect, left.StatusCode);
        Assert.Single(kept.Requests);
    }

    /// <summary>Over a real socket, as what matters is what the handler does with the jar. The app's own request is the control.</summary>
    [Fact]
    public async Task TheLibrarysRequests_StayOutOfTheAppsCookieJar()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var url = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/");
        var jar = new CookieContainer();
        jar.Add(url, new Cookie("browser", "1"));
        using var app = WebHttp.Create(new LanPolicy(() => NetworkReach.Both), jar);

        var served = ServeOnceAsync(listener, "engine=set");
        using var library = new HttpClient(new BuiltInMetaSearch.HostClientHandler(app));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Cookie", "preferences=2");
        using (await library.SendAsync(request))
        {
        }

        Assert.Equal(["preferences=2"], CookieHeaders(await served));
        Assert.Equal(["browser"], jar.GetCookies(url).Select(c => c.Name));

        served = ServeOnceAsync(listener, "app=set");
        using (await app.GetAsync(url))
        {
        }

        Assert.Equal(["browser=1"], CookieHeaders(await served));
        Assert.Equal(["app", "browser"], jar.GetCookies(url).Select(c => c.Name).Order());
    }

    [Fact]
    public async Task AnExemptRequest_StaysOutOfTheJarToo_WhenAskedTo()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var url = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/");
        var jar = new CookieContainer();
        jar.Add(url, new Cookie("browser", "1"));
        using var app = WebHttp.Create(new LanPolicy(() => NetworkReach.Both), jar);
        var served = ServeOnceAsync(listener, "engine=set");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Options.Set(LanPolicy.ExemptKey, true);
        request.Options.Set(WebHttp.NoCookiesKey, true);
        request.Headers.TryAddWithoutValidation("Cookie", "preferences=2");
        using (await app.SendAsync(request))
        {
        }

        Assert.Equal(["preferences=2"], CookieHeaders(await served));
        Assert.Equal(["browser"], jar.GetCookies(url).Select(c => c.Name));
    }

    [Fact]
    public async Task AnAnswerOf600KB_FailsThatEngine()
    {
        string body = "{\"title\":\"Rust\",\"content_urls\":{\"desktop\":{\"page\":\"https://en.wikipedia.org/wiki/Rust\"}},\"description\":\"" + new string('a', 600_000) + "\"}";
        var http = new StubHttpMessageHandler().Map("https://en.wikipedia.org/api/rest_v1/page/summary/Rust", (_, _) => Task.FromResult(StubHttpMessageHandler.Json(HttpStatusCode.OK, body)));
        var search = new BuiltInMetaSearch([new WikipediaEngine()], new HttpClient(http), _time);

        var outcome = await search.For(new AppSettingsData()).SearchAsync("Rust", 5, Options, CancellationToken.None);

        Assert.False(outcome.Ok);
    }

    /// <summary>A real engine's request to a literal public address under <c>lan</c>: refused at the socket, with no traffic at all, and the reason reaches the error.</summary>
    [Fact]
    public async Task UnderLan_TheSocketsRefusal_ReachesTheError()
    {
        using var app = WebHttp.Create(new LanPolicy(() => NetworkReach.LocalAreaNetwork));
        using var literal = new HttpClient(new ToLiteralAddress(app));
        var search = new BuiltInMetaSearch(literal, TimeProvider.System).For(new AppSettingsData { WebMetasearchEngines = ["wikipedia"] });

        var outcome = await search.SearchAsync("x", 3, new FetchOptions(FetchEngine.HttpClient, "", NetworkReach.Both), CancellationToken.None);

        Assert.False(outcome.Ok);
        Assert.Contains(WebText.InternetRefused("1.1.1.1"), outcome.Error, StringComparison.Ordinal);
    }

    /// <summary>Sends a request to a public address the policy can judge without any DNS.</summary>
    private sealed class ToLiteralAddress(HttpClient app) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            app.SendAsync(new HttpRequestMessage(request.Method, new UriBuilder(request.RequestUri!) { Host = "1.1.1.1" }.Uri), cancellationToken);
    }

    /// <summary>Hits it can't show are left out, and the log says so, or a search of only those would read as no results with no sign why.</summary>
    [Fact]
    public async Task AHitLeftOutForItsLink_IsLogged()
    {
        var engine = new FakeEngine("one", new EngineResult("ftp://a.example/f", "A", ""), new EngineResult("https://b.example/", "B", ""));

        var logged = await LoggedDuring(() => Search(engine).For(new AppSettingsData()).SearchAsync("x", 5, Options, CancellationToken.None));

        Assert.Contains(logged, e => e.Message.Contains("left out 1 hit", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NoHitLeftOut_LogsNothingAboutIt()
    {
        var engine = new FakeEngine("one", new EngineResult("https://b.example/", "B", ""));

        var logged = await LoggedDuring(() => Search(engine).For(new AppSettingsData()).SearchAsync("x", 5, Options, CancellationToken.None));

        Assert.DoesNotContain(logged, e => e.Message.Contains("left out", StringComparison.Ordinal));
    }

    private static IEnumerable<string> CookieHeaders(IEnumerable<string> head) =>
        head.Where(l => l.StartsWith("Cookie: ", StringComparison.OrdinalIgnoreCase)).Select(l => l["Cookie: ".Length..]);

    private static async Task<string[]> ServeOnceAsync(TcpListener listener, string setCookie)
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var client = await listener.AcceptTcpClientAsync(cancel.Token);
        var stream = client.GetStream();
        var head = new StringBuilder();
        var buffer = new byte[4096];
        while (!head.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            int read = await stream.ReadAsync(buffer, cancel.Token);
            if (read == 0)
            {
                break;
            }

            head.Append(Encoding.ASCII.GetString(buffer, 0, read));
        }

        await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nSet-Cookie: {setCookie}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), cancel.Token);
        return head.ToString().Split("\r\n");
    }

    /// <summary>Each search gets its own settings over the one instance, so a rest noted through one is honored through the next.</summary>
    [Fact]
    public async Task WebAccess_GivesAFreshSearch_OverTheOneInstance()
    {
        _http.Map("https://en.wikipedia.org/", HttpStatusCode.TooManyRequests, "");
        var web = new WebAccess(new HttpClient(_http), new FakeHeadlessBrowser { Executable = null }, _time);
        var settings = new AppSettingsData { WebSearchMethod = "metasearch", WebSearxngUrl = "http://localhost:8888", WebMetasearchEngines = ["wikipedia"] };

        var first = web.Engine(settings);
        var second = web.Engine(settings);

        Assert.NotSame(first, second);
        Assert.Equal(WebText.MetaSearchName, first.Name);
        Assert.Null(first.ResultReferer);
        Assert.Null(WebAccess.SearxngBase(settings));
        Assert.Same(web.DuckDuckGo, web.Engine(new AppSettingsData { WebSearchMethod = "duckduckgo" }));
        Assert.Equal(WebText.MetaSearchName, web.Engine(new AppSettingsData()).Name);   // the default since 2026-11-06

        await first.SearchAsync("Rust", 5, Options, CancellationToken.None);
        Assert.Single(_http.Requests);
        _time.Advance(BuiltInMetaSearch.MinInterval);
        var rested = await second.SearchAsync("Rust", 5, Options, CancellationToken.None);

        Assert.Single(_http.Requests);
        Assert.Contains("refused the search", rested.Error);
    }

    /// <summary>The menu's rows and the setting's words are these, so a renamed engine would otherwise stop being asked with nothing to say why.</summary>
    [Fact]
    public void TheEngineNames_AreTheRealEngines()
    {
        Assert.Equal(["bing", "brave", "duckduckgo", "startpage", "wikipedia"], BuiltInMetaSearch.EngineNames);
        Assert.Equal(BuiltInMetaSearch.EngineNames, BuiltInMetaSearch.RealEngines(_time).Select(e => e.Name));
    }

    [Fact]
    public async Task For_WithNoList_AsksThemAll()
    {
        var one = new FakeEngine("bing");
        var two = new FakeEngine("brave");

        await Search(one, two).For(new AppSettingsData()).SearchAsync("x", 5, Options, CancellationToken.None);

        Assert.Single(one.Queries);
        Assert.Single(two.Queries);
    }

    [Fact]
    public async Task For_AsksOnlyTheListed_ReadInAnyCase_AndWarnsOfAnUnknownName()
    {
        var one = new FakeEngine("bing", new EngineResult("https://a.example/", "A", ""));
        var two = new FakeEngine("brave", new EngineResult("https://b.example/", "B", ""));
        var warnings = new List<string>();
        Action<DiagnosticEvent> capture = e =>
        {
            if (e.Category == "Web" && e.Level == DiagnosticLevel.Warning)
            {
                warnings.Add(e.Message);
            }
        };
        DiagnosticLog.Emitted += capture;
        ISearchEngine search;
        try
        {
            search = Search(one, two).For(new AppSettingsData { WebMetasearchEngines = [" Bing ", "google"] });
        }
        finally
        {
            DiagnosticLog.Emitted -= capture;
        }

        var outcome = await search.SearchAsync("x", 5, Options, CancellationToken.None);

        Assert.Single(one.Queries);
        Assert.Empty(two.Queries);
        Assert.Single(outcome.Results);
        Assert.Contains("WebMetasearchEngines has 'google'", Assert.Single(warnings));
    }

    [Theory]
    [InlineData]
    [InlineData("google")]
    public async Task For_WithNoEngineItKnows_SaysSo_AndAsksNobody(params string[] saved)
    {
        var engine = new FakeEngine("bing", new EngineResult("https://a.example/", "A", ""));

        var outcome = await Search(engine).For(new AppSettingsData { WebMetasearchEngines = [.. saved] }).SearchAsync("x", 5, Options, CancellationToken.None);

        Assert.Equal(WebText.MetaSearchNoEngines, outcome.Error);
        Assert.Empty(engine.Queries);
    }

    /// <summary>What reaches the three engines that filter, through the real engines and the setting. Off is the user's default, 2026-11-06.</summary>
    [Theory]
    [InlineData(null, "adlt=off", "safesearch=off", "qadf=none")]
    [InlineData("off", "adlt=off", "safesearch=off", "qadf=none")]
    [InlineData("moderate", "adlt=moderate", "safesearch=moderate", "qadf=moderate")]
    [InlineData(" Strict ", "adlt=strict", "safesearch=strict", "qadf=heavy")]
    public async Task SafeSearch_ReachesBingBraveAndStartpage(string? word, string bing, string brave, string startpage)
    {
        string? bingUrl = null;
        string? braveCookie = null;
        string? startpageForm = null;
        var http = new StubHttpMessageHandler()
            .Map("https://www.bing.com/", (request, _) =>
            {
                bingUrl = request.RequestUri!.AbsoluteUri;
                return Task.FromResult(StubHttpMessageHandler.Json(HttpStatusCode.OK, ""));
            })
            .Map("https://search.brave.com/", (request, _) =>
            {
                braveCookie = request.Headers.NonValidated["Cookie"].ToString();
                return Task.FromResult(StubHttpMessageHandler.Json(HttpStatusCode.OK, ""));
            })
            .Map("https://www.startpage.com/", async (request, cancel) =>
            {
                if (request.Method == HttpMethod.Get)
                {
                    return StubHttpMessageHandler.Json(HttpStatusCode.OK, "<form id=\"search\"><input name=\"sc\" value=\"CODE\"></form>");
                }

                startpageForm = await request.Content!.ReadAsStringAsync(cancel);
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, "");
            });
        var settings = new AppSettingsData { WebMetasearchEngines = ["bing", "brave", "startpage"] };
        if (word is not null)
        {
            settings.WebMetasearchSafeSearch = word;
        }

        await new BuiltInMetaSearch(http: new HttpClient(http), time: _time).For(settings).SearchAsync("x", 5, Options, CancellationToken.None);

        Assert.Contains(bing, bingUrl);
        Assert.Contains(brave, braveCookie);
        Assert.Contains(startpage, startpageForm);
    }

    [Fact]
    public void SafeSearch_AWordItDoesNotKnow_ReadsAsOff_WithAWarning()
    {
        var warnings = new List<string>();
        Action<DiagnosticEvent> capture = e =>
        {
            if (e.Category == "Web" && e.Level == DiagnosticLevel.Warning)
            {
                warnings.Add(e.Message);
            }
        };
        DiagnosticLog.Emitted += capture;
        try
        {
            Search(new FakeEngine("bing")).For(new AppSettingsData { WebMetasearchSafeSearch = "spicy" });
        }
        finally
        {
            DiagnosticLog.Emitted -= capture;
        }

        Assert.Contains("WebMetasearchSafeSearch='spicy' is not one of off, moderate, strict. Using off.", Assert.Single(warnings));
    }
}
