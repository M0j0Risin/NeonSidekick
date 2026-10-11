using System.Net;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.Web;

namespace NeonSidekick.Tests;

public class DuckDuckGoSearchTests
{
    /// <summary>A real results page (2026-09-15) for <c>rust async traits</c>: ten hits, the redirect links, a snippet under each.</summary>
    public static readonly string FixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "web", "ddg-results.html");

    private readonly StubHttpMessageHandler _http = new();
    private readonly FakeHeadlessBrowser _browser = new() { Executable = null };
    private readonly ManualTimeProvider _time = new();

    private DuckDuckGoSearch Engine() => new(new WebFetcher(new HttpClient(_http), _browser, (_, _) => Task.FromResult(new[] { IPAddress.Parse("52.142.124.215") })), _time);

    private static FetchOptions Options => new(FetchEngine.HttpClient, "", NetworkReach.Internet);

    [Fact]
    public void QueryUrl_IsTheHtmlEndpoint()
    {
        Assert.Equal("https://html.duckduckgo.com/html/?q=rust%20async%20traits&kl=us-en", DuckDuckGoSearch.QueryUrl(" rust async traits ").AbsoluteUri);
        Assert.Equal("https://html.duckduckgo.com/html/", DuckDuckGoSearch.Endpoint.AbsoluteUri);
        Assert.Equal(TimeSpan.FromMilliseconds(1500), DuckDuckGoSearch.MinInterval);
        Assert.Equal(WebText.DuckDuckGoName, Engine().Name);
    }

    [Fact]
    public void Parse_TheCapturedPage_GivesTenHits_WithRealUrls()
    {
        var results = DuckDuckGoSearch.Parse(File.ReadAllText(FixturePath));

        Assert.Equal(10, results.Count);
        Assert.Equal("https://doc.rust-lang.org/book/ch17-05-traits-for-async.html", results[0].Url);
        Assert.NotEqual("", results[0].Title);
        Assert.NotEqual("", results[0].Snippet);
        Assert.Equal("https://docs.rs/async-trait/latest/async_trait/", results[1].Url);
        Assert.All(results, r => Assert.StartsWith("https://", r.Url));
        Assert.All(results, r => Assert.DoesNotContain("duckduckgo.com/l/", r.Url));
        Assert.All(results, r => Assert.DoesNotContain("\n", r.Title));
        Assert.All(results, r => Assert.DoesNotContain("\n", r.Snippet));
        Assert.Equal(results.Select(r => r.Url).Distinct().Count(), results.Count);
    }

    [Fact]
    public void Parse_AHandMadePage_ReadsTitlesSnippetsAndSkipsAds()
    {
        const string html = """
            <div class="result results_links results_links_deep web-result">
              <h2 class="result__title"><a rel="nofollow" class="result__a" href="//duckduckgo.com/l/?uddg=https%3A%2F%2Fexample.com%2Fa&amp;rut=abc">First <b>hit</b></a></h2>
              <a class="result__snippet" href="//duckduckgo.com/l/?uddg=https%3A%2F%2Fexample.com%2Fa&amp;rut=abc">Snippet   one &amp; more</a>
            </div>
            <div class="result results_links result--ad">
              <h2 class="result__title"><a class="result__a" href="https://duckduckgo.com/y.js?ad_provider=x&amp;u3=https%3A%2F%2Fads.example">Buy now</a></h2>
              <div class="result__snippet">ad text</div>
            </div>
            <div class="result">
              <a class="result__a" href="https://direct.example/page">Second</a>
              <div class="result__snippet">Snippet two</div>
            </div>
            <a class="result__a" href="javascript:void(0)">Not a link</a>
            """;

        var results = DuckDuckGoSearch.Parse(html);

        Assert.Equal(2, results.Count);
        Assert.Equal(new SearchResult("First hit", "https://example.com/a", "Snippet one & more"), results[0]);
        Assert.Equal(new SearchResult("Second", "https://direct.example/page", "Snippet two"), results[1]);
    }

    [Fact]
    public void ResolveHref_DecodesTheRedirect()
    {
        Assert.Equal("https://doc.rust-lang.org/book/ch17-05-traits-for-async.html", DuckDuckGoSearch.ResolveHref("//duckduckgo.com/l/?uddg=https%3A%2F%2Fdoc.rust%2Dlang.org%2Fbook%2Fch17%2D05%2Dtraits%2Dfor%2Dasync.html&rut=adf1"));
        Assert.Equal("https://example.com/", DuckDuckGoSearch.ResolveHref("https://example.com/"));
        Assert.Null(DuckDuckGoSearch.ResolveHref("https://duckduckgo.com/y.js?ad_provider=bing"));
        Assert.Null(DuckDuckGoSearch.ResolveHref("//duckduckgo.com/l/?uddg=not%20a%20url&rut=1"));
        Assert.Null(DuckDuckGoSearch.ResolveHref(""));
        Assert.Null(DuckDuckGoSearch.ResolveHref(null));
    }

    [Fact]
    public void LooksRefused_OnTheBotPage()
    {
        Assert.True(DuckDuckGoSearch.LooksRefused("<div class=\"anomaly-modal__title\">Unfortunately, bots use DuckDuckGo too.</div>"));
        Assert.True(DuckDuckGoSearch.LooksRefused("<form id=\"challenge-form\">"));
        Assert.False(DuckDuckGoSearch.LooksRefused(File.ReadAllText(FixturePath)));
    }

    [Fact]
    public async Task Search_FetchesTheEndpoint_WithTheReferer_AndCapsTheCount()
    {
        HttpRequestMessage? seen = null;
        _http.Map("https://html.duckduckgo.com/html/", (r, _) => { seen = r; return Task.FromResult(StubHttpMessageHandler.Json(HttpStatusCode.OK, File.ReadAllText(FixturePath), "text/html")); });

        var outcome = await Engine().SearchAsync("rust async traits", 3, Options, CancellationToken.None);

        Assert.True(outcome.Ok);
        Assert.Equal(3, outcome.Results.Count);
        Assert.Equal("https://html.duckduckgo.com/html/?q=rust%20async%20traits&kl=us-en", seen!.RequestUri!.AbsoluteUri);
        Assert.Equal("https://html.duckduckgo.com/html/", seen.Headers.GetValues("Referer").Single());
        Assert.Equal("same-origin", seen.Headers.GetValues("Sec-Fetch-Site").Single());
        Assert.Equal(BrowserIdentity.Fallback.UserAgent, seen.Headers.UserAgent.ToString());
    }

    [Fact]
    public async Task Search_TheBotPage_IsARefusal()
    {
        _http.Map("https://html.duckduckgo.com/html/", HttpStatusCode.OK, "<html><body><div class=\"anomaly-modal__title\">Unfortunately, bots use DuckDuckGo too.</div></body></html>", "text/html");

        var outcome = await Engine().SearchAsync("x", 8, Options, CancellationToken.None);

        Assert.False(outcome.Ok);
        Assert.Equal("Error: DuckDuckGo refused the search (a bot check); try again in a minute", outcome.Error);
    }

    [Fact]
    public async Task Search_A429_IsARefusal_AndAnUnreachableEndpoint_AFailure()
    {
        _http.Map("https://html.duckduckgo.com/html/", HttpStatusCode.TooManyRequests, "<p>slow down</p>", "text/html");
        var outcome = await Engine().SearchAsync("x", 8, Options, CancellationToken.None);
        Assert.Equal("Error: DuckDuckGo refused the search (HTTP 429 Too Many Requests); try again in a minute", outcome.Error);

        var unreachable = await new DuckDuckGoSearch(new WebFetcher(new HttpClient(new StubHttpMessageHandler()), _browser, (_, _) => Task.FromResult(new[] { IPAddress.Parse("52.142.124.215") })), _time)
            .SearchAsync("x", 8, Options, CancellationToken.None);
        Assert.StartsWith("Error: DuckDuckGo could not be searched (No connection could be made", unreachable.Error);
    }

    [Fact]
    public async Task Search_IsRefused_UnderLocalAreaNetwork_LikeAnyFetch()
    {
        // 2026-09-18, the user's call: the endpoint is public, so the mode refuses the search before any request; SearXNG on the LAN is the way then.
        _http.Map("https://html.duckduckgo.com/html/", HttpStatusCode.OK, File.ReadAllText(FixturePath), "text/html");

        var outcome = await Engine().SearchAsync("x", 8, Options with { Reach = NetworkReach.LocalAreaNetwork }, CancellationToken.None);

        Assert.False(outcome.Ok);
        Assert.Equal(WebText.InternetRefused("html.duckduckgo.com"), outcome.Error);
        Assert.Empty(_http.Requests);
        Assert.True((await Engine().SearchAsync("x", 8, Options with { Reach = NetworkReach.Both }, CancellationToken.None)).Ok);
    }

    [Fact]
    public async Task Search_UnderDefault_TriesTheBrowser_WhenTheEndpointRefuses()
    {
        _http.Map("https://html.duckduckgo.com/html/", HttpStatusCode.Forbidden, "<p>no</p>", "text/html");
        _browser.Executable = @"C:\x\msedge.exe";
        _browser.Html = File.ReadAllText(FixturePath);

        var outcome = await Engine().SearchAsync("rust async traits", 8, Options with { Engine = FetchEngine.Default }, CancellationToken.None);

        Assert.True(outcome.Ok);
        Assert.Equal(8, outcome.Results.Count);
        Assert.Equal(new Uri("https://html.duckduckgo.com/html/?q=rust%20async%20traits&kl=us-en"), Assert.Single(_browser.Runs));
    }

    [Fact]
    public async Task Search_NoResults_IsOk_AndEmpty()
    {
        _http.Map("https://html.duckduckgo.com/html/", HttpStatusCode.OK, "<html><body><div class=\"no-results\">No results.</div></body></html>", "text/html");

        var outcome = await Engine().SearchAsync("xyzzy", 8, Options, CancellationToken.None);

        Assert.True(outcome.Ok);
        Assert.Empty(outcome.Results);
    }

    [Fact]
    public async Task Search_IsPaced_ByTheMinimumInterval()
    {
        _http.Map("https://html.duckduckgo.com/html/", HttpStatusCode.OK, File.ReadAllText(FixturePath), "text/html");
        var engine = Engine();

        await engine.SearchAsync("a", 1, Options, CancellationToken.None);
        var second = engine.SearchAsync("b", 1, Options, CancellationToken.None);
        await Task.Yield();

        Assert.False(second.IsCompleted);
        Assert.Single(_http.Requests);
        _time.Advance(DuckDuckGoSearch.MinInterval);
        await second;
        Assert.Equal(2, _http.Requests.Count);
    }
}
