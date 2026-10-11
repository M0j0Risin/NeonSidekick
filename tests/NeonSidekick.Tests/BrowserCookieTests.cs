using System.Net;
using System.Text.Json;
using NeonSidekick.Web;

namespace NeonSidekick.Tests;

public class BrowserCookieTests
{
    private static BrowserCookie Cookie(string name, string domain, string value = "v", string path = "/", double expires = -1, bool secure = true) =>
        new(name, value, domain, path, expires, secure, HttpOnly: true);

    [Fact]
    public void Parse_ReadsStorageGetCookies_AndSkipsANamelessOne()
    {
        using var answer = JsonDocument.Parse("""
            { "cookies": [
              { "name": "cf_clearance", "value": "abc", "domain": ".example.com", "path": "/", "expires": 1893456000.5, "size": 15, "httpOnly": true, "secure": true, "session": false, "sameSite": "None" },
              { "name": "sid", "value": "1", "domain": "www.example.com", "path": "/app", "expires": -1, "httpOnly": false, "secure": false, "session": true },
              { "name": "", "value": "x", "domain": ".example.com" },
              { "name": "nodomain", "value": "x" }
            ] }
            """);

        var cookies = BrowserCookie.Parse(answer.RootElement);

        Assert.Equal(
            new[]
            {
                new BrowserCookie("cf_clearance", "abc", ".example.com", "/", 1893456000.5, true, true),
                new BrowserCookie("sid", "1", "www.example.com", "/app", -1, false, false),
            },
            cookies);
        Assert.Empty(BrowserCookie.Parse(default));
    }

    [Fact]
    public void AppliesTo_IsTheDomainMatch_HostOnlyForItsHostAlone()
    {
        var domain = Cookie("a", ".example.com");
        var hostOnly = Cookie("b", "example.com");

        Assert.True(domain.AppliesTo("example.com"));
        Assert.True(domain.AppliesTo("www.EXAMPLE.com"));
        Assert.False(domain.AppliesTo("badexample.com"));
        Assert.False(domain.AppliesTo("example.org"));
        Assert.True(hostOnly.AppliesTo("example.com"));
        Assert.False(hostOnly.AppliesTo("www.example.com"));
        Assert.False(Cookie("c", ".").AppliesTo("example.com"));
    }

    [Fact]
    public void AddTo_KeepsHostOnlyAndDomainCookiesAsTheSiteSetThem()
    {
        var jar = new CookieContainer();

        Assert.True(Cookie("cf_clearance", ".example.com", "abc").AddTo(jar));
        Assert.True(Cookie("sid", "example.com", "1").AddTo(jar));

        Assert.Equal("cf_clearance=abc; sid=1", Header(jar, "https://example.com/page"));
        Assert.Equal("cf_clearance=abc", Header(jar, "https://www.example.com/"));
        Assert.Equal("", Header(jar, "https://example.org/"));
    }

    [Fact]
    public void AddTo_CarriesThePathTheSecureFlagAndTheExpiry()
    {
        var jar = new CookieContainer();

        Assert.True(Cookie("app", "example.com", path: "/app").AddTo(jar));
        Assert.True(Cookie("gone", "example.com", expires: 1_000_000_000).AddTo(jar));   // 2001: already expired
        Assert.True(Cookie("later", "example.com", expires: DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds()).AddTo(jar));

        Assert.Equal("app=v; later=v", Header(jar, "https://example.com/app/x"));
        Assert.Equal("later=v", Header(jar, "https://example.com/"));
        Assert.Equal("", Header(jar, "http://example.com/"));   // secure cookies stay off plain http
    }

    [Fact]
    public void AddTo_SkipsACookieDotNetRefuses()
    {
        var jar = new CookieContainer();

        Assert.False(Cookie("bad", ".example.com", "a;b").AddTo(jar));
        Assert.False(Cookie("$bad", ".example.com").AddTo(jar));
        Assert.Equal(0, jar.Count);
    }

    private static string Header(CookieContainer jar, string url) =>
        string.Join("; ", jar.GetCookieHeader(new Uri(url)).Split("; ", StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal));
}
