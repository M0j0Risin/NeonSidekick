using NeonSidekick.Web;

namespace NeonSidekick.Tests;

/// <summary>The site and Referer rules a navigation's headers follow (code review, 2026-10-11).</summary>
public class BrowserHeadersTests
{
    private static Uri U(string url) => new(url);

    [Theory]
    [InlineData("example.com", "example.com")]
    [InlineData("www.example.com", "example.com")]
    [InlineData("a.b.example.com", "example.com")]
    [InlineData("www.bbc.co.uk", "bbc.co.uk")]
    [InlineData("shop.example.com.au", "example.com.au")]
    [InlineData("www.example.de", "example.de")]
    [InlineData("localhost", "localhost")]
    [InlineData("10.0.0.5", "10.0.0.5")]
    public void RegistrableDomain_IsTheLastTwoLabels_OrThreeUnderACountrysOwnSecondLevel(string host, string site)
    {
        Assert.Equal(site, BrowserHeaders.RegistrableDomain(host));
    }

    [Fact]
    public void SiteOf_FoldsOverTheChain_AsFetchMetadataDoes()
    {
        Assert.Equal("none", BrowserHeaders.SiteOf(null, [U("https://other.org/")]));
        Assert.Equal("same-origin", BrowserHeaders.SiteOf(U("https://example.com/a"), [U("https://example.com/b")]));
        Assert.Equal("same-site", BrowserHeaders.SiteOf(U("https://example.com/"), [U("https://www.example.com/")]));
        Assert.Equal("cross-site", BrowserHeaders.SiteOf(U("https://example.com/"), [U("https://other.org/")]));

        // The scheme counts: http to https on one host is another origin and another site.
        Assert.Equal("cross-site", BrowserHeaders.SiteOf(U("http://example.com/"), [U("https://example.com/")]));

        // Once cross-site, a hop back home does not undo it; a same-site hop stays same-site after a same-origin one.
        Assert.Equal("cross-site", BrowserHeaders.SiteOf(U("https://example.com/"), [U("https://other.org/"), U("https://example.com/x")]));
        Assert.Equal("same-site", BrowserHeaders.SiteOf(U("https://example.com/"), [U("https://www.example.com/"), U("https://example.com/x")]));
    }

    [Fact]
    public void RefererFor_IsChromesDefaultPolicy()
    {
        var from = U("https://user:pw@example.com/a/page?q=1#frag");
        Assert.Equal("https://example.com/a/page?q=1", BrowserHeaders.RefererFor(from, U("https://example.com/b")));
        Assert.Equal("https://example.com/", BrowserHeaders.RefererFor(from, U("https://www.example.com/")));
        Assert.Null(BrowserHeaders.RefererFor(from, U("http://example.com/")));
        Assert.Equal("http://example.com:8080/", BrowserHeaders.RefererFor(U("http://example.com:8080/x"), U("https://other.org/")));
    }

    [Fact]
    public void Apply_WithAReferer_SendsWhatThePolicyLetsThrough()
    {
        using var down = new HttpRequestMessage(HttpMethod.Get, "http://example.com/");
        BrowserHeaders.Apply(down, BrowserIdentity.Fallback, U("https://html.duckduckgo.com/"));
        Assert.False(down.Headers.Contains("Referer"));
        Assert.Equal("cross-site", down.Headers.GetValues("Sec-Fetch-Site").Single());

        using var site = new HttpRequestMessage(HttpMethod.Get, "https://www.example.com/");
        BrowserHeaders.Apply(site, BrowserIdentity.Fallback, U("https://example.com/page"));
        Assert.Equal("https://example.com/", site.Headers.GetValues("Referer").Single());
        Assert.Equal("same-site", site.Headers.GetValues("Sec-Fetch-Site").Single());
    }
}
