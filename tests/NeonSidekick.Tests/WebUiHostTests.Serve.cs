using System.Net;
using System.Net.Http.Headers;
using System.Text;
using NeonSidekick.Diagnostics;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// Requests through Tailscale Serve (2026-10-31): the test's client is loopback, as Serve's proxy is, so its <c>X-Forwarded-*</c> headers are
/// read — the sign-ins name the tailnet device and the proxy, and each device fills its own rate-limit bucket. Since 2026-11-01 (the review):
/// <c>lan</c> refuses Funnel's and a public forwarded address, Funnel's and other proxied sign-ins share one bucket, and the cookie is Secure
/// behind an https proxy.
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
        Assert.Equal(WebUiText.TailscaleServeName, origin.Through);
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
        Assert.All(_backend.Origins, o => Assert.Equal(WebUiText.TailscaleServeName, o.Through));
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

    /// <summary>2026-11-01, the review's P1-2, P7-1: forged public addresses through a plain forwarder share one sign-in bucket; a tailnet device keeps its own.</summary>
    [Fact]
    public async Task ForgedForwardedAddresses_ShareOneSignInBucket_ButATailnetDeviceKeepsItsOwn()
    {
        var statuses = new List<HttpStatusCode>();
        for (int i = 1; i <= 8; i++)
        {
            using var response = await _client.SendAsync(ThroughServe(Get(WebUiAuth.SignInPath + "?t=wrong", signedIn: false), "8.8.8." + i, host: "caddy.lan"));
            statuses.Add(response.StatusCode);
        }

        Assert.Equal(5, statuses.Count(s => s == HttpStatusCode.Forbidden));   // five a minute among them all
        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
        using var tailnet = await _client.SendAsync(ThroughServe(Get(WebUiAuth.SignInPath + "?t=wrong", signedIn: false), "100.64.1.9"));
        Assert.Equal(HttpStatusCode.Forbidden, tailnet.StatusCode);
        Assert.True(WebUiHost.SharesSignIn(WebUiOrigin.Of(IPAddress.Loopback, "h.ts.net", "203.0.113.9", "?1")));
        Assert.False(WebUiHost.SharesSignIn(WebUiOrigin.Of(IPAddress.Loopback, "h.ts.net", "100.64.1.2", null)));
        Assert.False(WebUiHost.SharesSignIn(new WebUiOrigin(IPAddress.Loopback)));   // direct: never
    }

    /// <summary>2026-11-01, the review's P2-3: an X-Forwarded-For alone from loopback names the device, through a proxy on this computer.</summary>
    [Fact]
    public async Task AForwardedAddress_WithoutAHost_IsAProxyOnThisComputer()
    {
        var request = Get(WebUiAuth.SignInPath + "?t=" + _token, signedIn: false);
        request.Headers.Add("X-Forwarded-For", "192.168.1.60");
        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        var origin = Assert.Single(_backend.Origins);
        Assert.Equal(IPAddress.Parse("192.168.1.60"), origin.Remote);
        Assert.Equal(WebUiText.ProxyOnThisComputer, origin.Through);
    }

    /// <summary>2026-11-01, the review's P2-2: the cookie is Secure behind an https proxy alone.</summary>
    [Fact]
    public async Task TheCookie_IsSecure_OnlyBehindAnHttpsProxy()
    {
        using var https = await _client.SendAsync(ThroughServe(Get(WebUiAuth.SignInPath + "?t=" + _token, signedIn: false), "100.64.1.2"));
        using var plain = await _client.SendAsync(Get(WebUiAuth.SignInPath + "?t=" + _token, signedIn: false));

        Assert.Contains(https.Headers.GetValues("Set-Cookie"), c => c.Contains("secure", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(plain.Headers.GetValues("Set-Cookie"), c => c.Contains("secure", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 2026-11-01, the review's P1-3: under <c>lan</c>, Funnel's request is refused even from a documentation-block address LanPolicy counts as
    /// private, and so is a public forwarded address; the log names the origin. A tailnet IPv6 device is let in. Under <c>local</c> both pass.
    /// </summary>
    [Fact]
    public async Task UnderLan_FunnelAndAPublicForwardedAddress_AreRefused_ButTheTailnetIsNot()
    {
        using (var local = await _client.SendAsync(ThroughServe(Get("/health", signedIn: false), "8.8.8.8", funnel: true)))
        {
            Assert.Equal(HttpStatusCode.OK, local.StatusCode);   // local: Funnel's requests are let in (the docs' warning)
        }

        await _host.DisposeAsync();
        _client.Dispose();
        _allow = Web.LanPolicy.IsPrivate;
        await StartAsync(WebUiAudience.LocalAreaNetwork);
        var lines = new List<string>();
        void Record(DiagnosticEvent e)
        {
            lock (lines)
            {
                lines.Add(e.Message);
            }
        }

        DiagnosticLog.Emitted += Record;
        try
        {
            using var funnel = await _client.SendAsync(ThroughServe(Get("/health", signedIn: false), "203.0.113.9", funnel: true));
            using var publicAddress = await _client.SendAsync(ThroughServe(Get("/health", signedIn: false), "8.8.8.8", host: "caddy.lan"));
            using var tailnet = await _client.SendAsync(ThroughServe(Get("/health", signedIn: false), "fd7a:115c:a1e0::1"));
            using var direct = await _client.SendAsync(Get("/health", signedIn: false));

            Assert.Equal(HttpStatusCode.Forbidden, funnel.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, publicAddress.StatusCode);
            Assert.Equal(HttpStatusCode.OK, tailnet.StatusCode);
            Assert.Equal(HttpStatusCode.OK, direct.StatusCode);
        }
        finally
        {
            DiagnosticLog.Emitted -= Record;
        }

        lock (lines)
        {
            Assert.Contains("Web UI: refused a connection from 203.0.113.9 through Tailscale Funnel, from the internet (Web UI reach lan refuses the internet).", lines);
            Assert.Contains("Web UI: refused a connection from 8.8.8.8 through the proxy at caddy.lan (not a private address).", lines);
        }
    }
}
