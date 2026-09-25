using NeonSidekick.Web;

namespace NeonSidekick.Tests;

public class WebTextTests
{
    [Fact]
    public void Constants_ArePinned()
    {
        Assert.Equal("DuckDuckGo", WebText.DuckDuckGoName);
        Assert.Equal("SearXNG", WebText.SearxngName);
        Assert.Equal("Error: max_results must be 1 to 20", WebText.BadResultCount);
        Assert.Equal("Error: open_url takes at most 5 links at a time", WebText.TooManyLinks);
    }

    [Fact]
    public void OpenUrl_Sentences_ArePinned()
    {
        Assert.Equal("Opened https://example.com/ in your browser", WebText.OpenedOne("https://example.com/"));
        Assert.Equal("Opened 3 links in your browser:", WebText.OpenedHeader(3));
        Assert.Equal("- https://example.com/", WebText.OpenedLine("https://example.com/"));
        Assert.Equal("Error: could not open 'https://example.com/' in your browser: no application", WebText.OpenFailed("https://example.com/", "no application"));
        Assert.Equal("Error: '3' is not a list of links for 'urls'", WebText.BadUrlList("urls", " 3 "));
        Assert.Equal("Error: none of the 2 links could be opened", WebText.NoneOpened(2));
    }

    [Fact]
    public void Errors_StartWithError_AndNameTheThing()
    {
        Assert.Equal("Error: 'ftp://x/' is not an http or https URL", WebText.NotHttp("ftp://x/"));
        Assert.Equal("Error: '192.168.1.1' is on this machine or the local network, which Web browser network mode 'internet' keeps off limits", WebText.LanRefused("192.168.1.1"));   // "the setting Browser allow LAN" until 2026-09-18
        Assert.Equal("Error: 'example.com' is on the internet, which Web browser network mode 'local_area_network' keeps off limits", WebText.InternetRefused("example.com"));
        Assert.Equal("Error: 'https://a/' did not answer within 30s", WebText.Timeout("https://a/", TimeSpan.FromSeconds(30)));
        Assert.Equal("Error: 'https://a/' refused the request (HTTP 403 Forbidden)", WebText.Blocked("https://a/", "HTTP 403 Forbidden"));
        Assert.Equal("Error: 'https://a/x.pdf' is application/pdf (1.2 MB); only web pages and text are readable", WebText.Binary("https://a/x.pdf", "application/pdf", 1_234_567));
        Assert.Equal("Error: the headless browser could not load 'https://a/': exit code 1", WebText.BrowserFailed("https://a/", "exit code 1"));
        Assert.Equal("Error: could not fetch 'https://a/': No such host is known", WebText.CouldNot("https://a/", "No such host is known"));
        Assert.Equal("Error: offset 50000 is past the end of the page (2,100 characters)", WebText.OffsetPastEnd(50000, 2100));
        Assert.Equal("Error: DuckDuckGo refused the search (HTTP 429 Too Many Requests); try again in a minute", WebText.SearchRefused("DuckDuckGo", "HTTP 429 Too Many Requests"));
        Assert.Equal("Error: SearXNG at http://localhost:8080 refused the JSON format (HTTP 403); add json to search.formats in its settings.yml", WebText.SearxngRefused("http://localhost:8080"));
        Assert.Equal("Error: SearXNG at http://localhost:8080 did not answer (connection refused)", WebText.SearxngUnreachable("http://localhost:8080", "connection refused"));
        Assert.Equal("Error: DuckDuckGo could not be searched (no answer)", WebText.SearchFailed("DuckDuckGo", "no answer"));
    }

    [Fact]
    public void Error_MapsEveryOutcome()
    {
        Assert.Equal(WebText.NoUrl, WebText.Error(FetchOutcome.NoUrl, "", ""));
        Assert.Equal(WebText.NotHttp("x"), WebText.Error(FetchOutcome.NotHttp, "x", ""));
        Assert.Equal(WebText.LanRefused("h"), WebText.Error(FetchOutcome.LanRefused, "https://h/", "h"));
        Assert.Equal(WebText.InternetRefused("h"), WebText.Error(FetchOutcome.InternetRefused, "https://h/", "h"));
        Assert.Equal(WebText.Timeout("u", WebFetcher.FetchTimeout), WebText.Error(FetchOutcome.Timeout, "u", ""));
        Assert.Equal(WebText.Blocked("u", "d"), WebText.Error(FetchOutcome.Blocked, "u", "d"));
        Assert.Equal("Error: sentence", WebText.Error(FetchOutcome.Binary, "u", "Error: sentence"));
        Assert.Equal(WebText.NoBrowser, WebText.Error(FetchOutcome.NoBrowser, "u", ""));
        Assert.Equal(WebText.BrowserFailed("u", "d"), WebText.Error(FetchOutcome.BrowserFailed, "u", "d"));
        Assert.Equal(WebText.CouldNot("u", "d"), WebText.Error(FetchOutcome.Failed, "u", "d"));
    }

    [Fact]
    public void FetchHeader_NamesTheWindow_AndTheContinuation()
    {
        Assert.Equal("Example Domain — https://example.com/ (chars 1–2,100 of 2,100, http)", WebText.FetchHeader("Example Domain", "https://example.com/", 1, 2100, 2100, "http", null));
        Assert.Equal("(untitled) — https://a/ (chars 1–32,000 of 88,400; continue with offset 32000, headless browser)", WebText.FetchHeader("", "https://a/", 1, 32000, 88400, "headless browser", 32000));
        Assert.Equal("(untitled) — https://a/ (no readable text, http)", WebText.EmptyPage(" ", "https://a/", "http"));
    }

    [Fact]
    public void Results_AreNumbered_WithSnippetsUnder()
    {
        var results = new[]
        {
            new SearchResult("Async fn in traits", "https://blog.rust-lang.org/a", "Stabilised in 1.75."),
            new SearchResult("", "https://docs.rs/async-trait", ""),
        };

        Assert.Equal(
            "Searched \"rust async traits\" (2 results, DuckDuckGo):\n1. Async fn in traits — https://blog.rust-lang.org/a\n   Stabilised in 1.75.\n2. (untitled) — https://docs.rs/async-trait",
            WebText.Results("rust async traits", results, "DuckDuckGo"));
        Assert.Equal("Searched \"x\" (1 result, SearXNG):", WebText.SearchHeader("x", 1, "SearXNG"));
        Assert.Equal("Searched \"x\" (DuckDuckGo): no results", WebText.Results("x", [], "DuckDuckGo"));
    }

    [Fact]
    public void Size_IsHuman()
    {
        Assert.Equal("512 B", WebText.Size(512));
        Assert.Equal("213.4 KB", WebText.Size(213_400));
        Assert.Equal("1.2 MB", WebText.Size(1_234_567));
        Assert.Equal("5 MB", WebText.Size(5_000_000));
    }
}
