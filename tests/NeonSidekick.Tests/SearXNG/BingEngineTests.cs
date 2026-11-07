// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Net;
using NeonSidekick.Tests.Fakes;
using SearXNG.Net.Engines;

namespace SearXNG.Net.Tests;

public class BingEngineTests
{
    private static string Url(EngineQuery query) => BingEngine.BuildUrl(query).AbsoluteUri;

    [Theory]
    [InlineData("all", "https://www.bing.com/search?q=dotnet+aot&adlt=off")]
    [InlineData("de-DE", "https://www.bing.com/search?q=dotnet+aot&adlt=off&mkt=de-DE")]
    [InlineData("en-US", "https://www.bing.com/search?q=dotnet+aot&adlt=off&setlang=en")] // "bing just sends junk" for us, cn and ru.
    [InlineData("ru-RU", "https://www.bing.com/search?q=dotnet+aot&adlt=off&setlang=ru")]
    [InlineData("zh-CN", "https://www.bing.com/search?q=dotnet+aot&adlt=off&setlang=zh")]
    [InlineData("zh-HK", "https://www.bing.com/search?q=dotnet+aot&adlt=off&mkt=en-HK")] // The table's one odd mapping.
    public void BuildUrl_FollowsRequest(string locale, string expected)
    {
        Assert.Equal(expected, Url(new EngineQuery("dotnet aot", Locale: locale)));
    }

    [Theory]
    [InlineData(SafeSearch.Off, "off")]
    [InlineData(SafeSearch.Moderate, "moderate")]
    [InlineData(SafeSearch.Strict, "strict")]
    public void BuildUrl_MapsSafeSearch(SafeSearch level, string value)
    {
        Assert.EndsWith("&adlt=" + value, Url(new EngineQuery("x", SafeSearch: level)));
    }

    [Fact]
    public void BuildUrl_EncodesTheQuery()
    {
        Assert.Contains("?q=c%23+%26+d&", Url(new EngineQuery("c# & d")));
    }

    [Theory]
    [InlineData("all", "clear")]
    [InlineData("fr-CH", "fr-ch")]
    [InlineData("de", "de-de")] // A language goes to its own country.
    [InlineData("en", "en-us")] // SearXNG's exception for English.
    [InlineData("en-ZZ", "en-us")] // An unknown region falls back on the language.
    [InlineData("xx", "clear")] // A language Bing doesn't have.
    public void RegionFor_ApproximatesAsDocumented(string locale, string expected)
    {
        Assert.Equal(expected, BingEngine.RegionFor(locale));
    }

    /// <summary>A deliberate difference: SearXNG only finds a market in the exact case, so it sends en-in on to hi-in.</summary>
    [Theory]
    [InlineData("en-in", "en-in")]
    [InlineData("EN-IN", "en-in")]
    [InlineData("de-be", "de-be")]
    public void RegionFor_ReadsATagTheSameInAnyCase(string locale, string expected)
    {
        Assert.Equal(expected, BingEngine.RegionFor(locale));
    }

    /// <summary>A real page, reduced and scrubbed. Expected values were read from the fixture by hand.</summary>
    [Fact]
    public void Parse_TheCapturedPage_GivesTenResultsWithRealUrls()
    {
        var results = BingEngine.Parse(Fixtures.Read("bing-results.html"));

        Assert.Equal(10, results.Count);
        Assert.Equal(
            new EngineResult(
                "https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/",
                "Native AOT deployment overview - .NET | Microsoft Learn",
                "Jan 8, 2026 · Check out the Native AOT samples available in the dotnet/samples repository on GitHub. The samples include Linux …"),
            results[0]);
        Assert.Equal("https://learn.microsoft.com/en-us/aspnet/core/fundamentals/native-aot?view=aspnetcore-10.0", results[1].Url);
        Assert.Equal("https://www.reddit.com/r/dotnet/comments/1d6lfjh/are_you_using_native_aot_what_is_your_experience/", results[9].Url);
    }

    [Fact]
    public void Parse_TheCapturedPage_DecodesEntitiesInTitles()
    {
        var results = BingEngine.Parse(Fixtures.Read("bing-results.html"));

        // The &#183; that HtmlAgilityPack's InnerText left undecoded in the spike's comparison with lxml.
        Assert.Equal("docs/docs/core/deploying/native-aot/index.md at main · dotnet/docs", results[3].Title);
    }

    [Fact]
    public void Parse_TheCapturedPage_LeavesOutTheNoticeAndPagination()
    {
        var results = BingEngine.Parse(Fixtures.Read("bing-results.html"));

        Assert.DoesNotContain(results, r => r.Title.Contains("removed", StringComparison.Ordinal));
        Assert.All(results, r => Assert.DoesNotContain("bing.com", r.Url, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("<ol id=\"b_results\"><li class=\"b_no\">None</li></ol>")]
    [InlineData("<ol id=\"b_results\"><li class=\"b_algo\"><p>no link</p></li></ol>")]
    [InlineData("<ol id=\"b_results\"><li class=\"b_algo\"><h2><a href=\"\">no address</a></h2></li></ol>")]
    [InlineData("<ol id=\"b_results\"><li class=\"b_algo\"><h2><a href=\"https://a.example/\"></a></h2></li></ol>")]
    [InlineData("<ol id=\"b_results\"><li class=\"b_ad\"><h2><a href=\"https://a.example/\">an ad</a></h2></li></ol>")]
    public void Parse_SkipsWhatIsNotAResult(string html)
    {
        Assert.Empty(BingEngine.Parse(html));
    }

    [Fact]
    public void Parse_RemovesIconsFromSnippets_KeepingTheTextAfterThem()
    {
        var results = BingEngine.Parse(
            "<ol id=\"b_results\"><li class=\"b_algo\"><h2><a href=\"https://a.example/\">t</a></h2>" +
            "<p><span class=\"algoSlug_icon\">WEB</span>The snippet</p></li></ol>");

        Assert.Equal("The snippet", Assert.Single(results).Content);
    }

    [Fact]
    public void Parse_DecodesAmpersandsInAddresses()
    {
        var results = BingEngine.Parse("<ol id=\"b_results\"><li class=\"b_algo\"><h2><a href=\"https://a.example/?x=1&amp;y=2\">t</a></h2></li></ol>");

        Assert.Equal("https://a.example/?x=1&y=2", Assert.Single(results).Url);
    }

    [Theory]
    [InlineData("https://example.com/plain", "https://example.com/plain")] // Not a redirect.
    [InlineData("https://www.bing.com/ck/a?!&&ntb=1", "https://www.bing.com/ck/a?!&&ntb=1")] // No u.
    [InlineData("https://www.bing.com/ck/a?u=b1aGk&ntb=1", "https://www.bing.com/ck/a?u=b1aGk&ntb=1")] // Not an a1 value.
    [InlineData("https://www.bing.com/ck/a?u=a1!!!&ntb=1", "https://www.bing.com/ck/a?u=a1!!!&ntb=1")] // Not base64.
    [InlineData("https://www.bing.com/ck/a?!&&u=a1aHR0cHM6Ly9hLmV4YW1wbGUv&ntb=1", "https://a.example/")]
    [InlineData("https://www.bing.com/ck/a?u=a1aHR0cHM6Ly9hLmV4YW1wbGU_", "https://a.example?")] // Base64url alphabet, and no padding.
    public void DecodeRedirect_FollowsTheEncodedTarget(string href, string expected)
    {
        Assert.Equal(expected, BingEngine.DecodeRedirect(href));
    }

    [Fact]
    public async Task SearchAsync_FetchesAndParses()
    {
        var handler = new StubHandler(_ => StubHandler.Text(Fixtures.Read("bing-results.html")));
        using var http = new HttpClient(handler);

        var results = await new BingEngine().SearchAsync(new EngineQuery("dotnet native aot"), http, CancellationToken.None);

        Assert.Equal(10, results.Count);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://www.bing.com/search?q=dotnet+native+aot&adlt=off", request.RequestUri!.AbsoluteUri);
        Assert.Contains("Chrome/", request.Headers.UserAgent.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchAsync_ReportsARefusal()
    {
        using var http = new HttpClient(new StubHandler(_ => StubHandler.Text("no", HttpStatusCode.TooManyRequests)));

        var e = await Assert.ThrowsAsync<EngineException>(() => new BingEngine().SearchAsync(new EngineQuery("x"), http, CancellationToken.None));

        Assert.Equal(EngineFailureKind.TooManyRequests, e.Kind);
    }

    [Fact]
    public void Parse_APageWithNoResultsList_IsAFailure_NotNoResults()
    {
        var e = Assert.Throws<EngineException>(() => BingEngine.Parse("<html><body>Please wait</body></html>"));

        Assert.Equal(EngineFailureKind.UnexpectedResponse, e.Kind);
    }

    [Fact]
    public async Task ARefusal_NamesThePageThatRefused_NotTheOneAskedFor()
    {
        var blocked = new Uri("https://other.example/blocked");
        using var http = new HttpClient(new FinalAddressHandler(blocked));

        var e = await Assert.ThrowsAsync<EngineException>(() => new BingEngine().SearchAsync(new EngineQuery("x"), http, CancellationToken.None));

        Assert.Equal(blocked, e.RefusedUrl);
    }

    /// <summary>Refuses from another address, as a redirect the client followed does.</summary>
    private sealed class FinalAddressHandler(Uri final) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("no"), RequestMessage = new HttpRequestMessage(request.Method, final) });
    }
}
