using System.Net;
using System.Net.Http.Headers;
using System.Text;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// Requests through Tailscale Serve (2026-10-31): the test's client is loopback, as Serve's proxy is, so its <c>X-Forwarded-*</c> headers are
/// read — the sign-ins name the tailnet device and the proxy, and each device fills its own rate-limit bucket.
/// </summary>
public sealed partial class WebUiHostTests
{
    private static HttpRequestMessage ThroughServe(HttpRequestMessage request, string device, string host = "hoopdiethink.tail9511ae.ts.net", bool funnel = false)
    {
        request.Headers.Add("X-Forwarded-Host", host);
        request.Headers.Add("X-Forwarded-For", device);
        request.Headers.Add("X-Forwarded-Proto", "https");
        if (funnel)
        {
            request.Headers.Add("Tailscale-Funnel-Request", "?1");
        }

        return request;
    }

    [Fact]
    public async Task ASignInThroughServe_NamesTheTailnetDevice_AndServe()
    {
        using var response = await _client.SendAsync(ThroughServe(Get(WebUiAuth.SignInPath + "?t=" + _token, signedIn: false), "100.64.1.2"));

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        var origin = Assert.Single(_backend.Origins);
        Assert.Equal(IPAddress.Parse("100.64.1.2"), origin.Remote);
        Assert.Equal(WebUiOrigin.TailscaleServeName, origin.Through);
        Assert.Equal("(" + WebUiText.Glyph + "a browser signed in to the Web UI from 100.64.1.2 through Tailscale Serve)", WebUiText.SignedInNotice(origin));
    }

    [Fact]
    public async Task ASignInThroughFunnel_OrAnotherProxy_SaysSo()
    {
        using (await _client.SendAsync(ThroughServe(Get(WebUiAuth.SignInPath + "?t=" + _token, signedIn: false), "203.0.113.9", funnel: true)))
        {
        }

        using (await _client.SendAsync(ThroughServe(Get(WebUiAuth.SignInPath + "?t=" + _token, signedIn: false), "192.168.1.50", host: "caddy.lan")))
        {
        }

        var origins = _backend.Origins.ToList();
        Assert.Equal(2, origins.Count);
        Assert.True(origins[0].Funnel);
        Assert.Contains("through Tailscale Funnel, from the internet", WebUiText.SignedInNotice(origins[0]), StringComparison.Ordinal);
        Assert.Equal("the proxy at caddy.lan", origins[1].Through);
    }

    [Fact]
    public async Task APastedAndAOneTimeSignIn_ThroughServe_NameTheDeviceToo()
    {
        var pasted = new HttpRequestMessage(HttpMethod.Post, WebUiAuth.SignInPath)
        {
            Content = new StringContent("{\"link\":\"" + _token + "\"}", Encoding.UTF8),
        };
        pasted.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        pasted.Headers.Add(WebUiHost.CsrfHeader, "1");
        using (var response = await _client.SendAsync(ThroughServe(pasted, "100.64.1.3")))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        string once = _host.OneTimeLinkAt("https://hoopdiethink.tail9511ae.ts.net")!;
        Assert.StartsWith("https://hoopdiethink.tail9511ae.ts.net/signin?once=", once, StringComparison.Ordinal);
        using (var response = await _client.SendAsync(ThroughServe(Get(new Uri(once).PathAndQuery, signedIn: false), "100.64.1.4")))
        {
            Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        }

        Assert.Equal([IPAddress.Parse("100.64.1.3"), IPAddress.Parse("100.64.1.4")], _backend.Origins.Select(o => o.Remote).ToList());
        Assert.All(_backend.Origins, o => Assert.Equal(WebUiOrigin.TailscaleServeName, o.Through));
    }

    [Fact]
    public async Task TwoServeDevices_FillTheirOwnSignInBuckets_AndLoopbackKeepsItsOwn()
    {
        async Task<HttpStatusCode> WrongSignIn(string? device)
        {
            var request = Get(WebUiAuth.SignInPath + "?t=wrong", signedIn: false);
            using var response = await _client.SendAsync(device is null ? request : ThroughServe(request, device));
            return response.StatusCode;
        }

        var first = new List<HttpStatusCode>();
        for (int i = 0; i < 8; i++)
        {
            first.Add(await WrongSignIn("100.64.1.2"));
        }

        Assert.Contains(HttpStatusCode.TooManyRequests, first);   // the first device spent its bucket
        Assert.Equal(HttpStatusCode.Forbidden, await WrongSignIn("100.64.1.5"));   // another device has its own
        Assert.Equal(HttpStatusCode.Forbidden, await WrongSignIn(null));   // /webui browser's loopback too
    }
}
