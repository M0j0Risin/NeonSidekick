using System.Net;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.Viewer;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// The one-time sign-in (later on 2026-10-15, <c>/webui browser</c>): a live code signs in once and leaves the address bar; used, unknown or
/// expired it is the sign-in page with 403; a HEAD spends nothing; a restart drops them; at most eight; and the sign-in links kept out of the
/// Browser window's saved links and redacted in its log line.
/// </summary>
public sealed partial class WebUiHostTests
{
    private static string CodeOf(string link) => link[(link.IndexOf("once=", StringComparison.Ordinal) + 5)..];

    [Fact]
    public async Task ALiveCode_SignsInOnce_ThenItIsTheSignInPage()
    {
        string link = _host.OneTimeLink("127.0.0.1")!;
        string path = "/signin?once=" + CodeOf(link);
        Assert.StartsWith("http://127.0.0.1:" + _host.Port + "/signin?once=", link, StringComparison.Ordinal);

        using var first = await _client.SendAsync(Get(path, signedIn: false));
        using var again = await _client.SendAsync(Get(path, signedIn: false));
        using var unknown = await _client.SendAsync(Get("/signin?once=nope", signedIn: false));

        Assert.Equal(HttpStatusCode.SeeOther, first.StatusCode);
        Assert.Equal("/", first.Headers.Location?.OriginalString);
        Assert.Contains(first.Headers.GetValues("Set-Cookie"), c => c.StartsWith(WebUiAuth.CookieName(_host.Port) + "=", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.Forbidden, again.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, unknown.StatusCode);
        Assert.False(unknown.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task AHead_SpendsNothing_SoTheGetAfterItSignsIn()
    {
        string path = "/signin?once=" + CodeOf(_host.OneTimeLink("127.0.0.1")!);

        using var head = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Head, path));
        using var get = await _client.SendAsync(Get(path, signedIn: false));

        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.False(head.Headers.Contains("Set-Cookie"));
        Assert.Equal(HttpStatusCode.SeeOther, get.StatusCode);
    }

    [Fact]
    public async Task ARestart_DropsTheCodes()
    {
        string path = "/signin?once=" + CodeOf(_host.OneTimeLink("127.0.0.1")!);
        await _host.StopAsync();
        Assert.Null(_host.OneTimeLink("127.0.0.1"));   // not serving: none
        await _host.StartAsync(new WebUiBinding(WebUiAudience.Local, 0, _token, IPAddress.Loopback), CancellationToken.None);   // the same host, started again
        using var client = new HttpClient(new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false }) { BaseAddress = new Uri("http://127.0.0.1:" + _host.Port + "/") };

        using var stale = await client.SendAsync(Get(path, signedIn: false));

        Assert.Equal(HttpStatusCode.Forbidden, stale.StatusCode);
    }

    [Fact]
    public void TheCodes_AreGoodOnce_ForAMinute_AtMostEight()
    {
        var time = new ManualTimeProvider();
        var codes = new WebUiOneTimeCodes(time);
        string first = codes.Mint();
        string expiring = codes.Mint();
        Assert.True(codes.Take(first));
        Assert.False(codes.Take(first));

        time.Advance(TimeSpan.FromSeconds(61));
        Assert.False(codes.Take(expiring));   // expired, and spent with it
        Assert.Equal(0, codes.Count);

        var minted = Enumerable.Range(0, 9).Select(_ => codes.Mint()).ToList();
        Assert.Equal(WebUiOneTimeCodes.MaxOutstanding, codes.Count);
        Assert.False(codes.Take(minted[0]));   // the ninth dropped the oldest
        Assert.True(codes.Take(minted[8]));
        codes.Clear();
        Assert.False(codes.Take(minted[1]));
        Assert.Matches("^[A-Za-z0-9_-]{43}$", minted[2]);
    }

    [Fact]
    public void SignInLinks_AreKnown_Redacted_AndNeverSaved()
    {
        Assert.True(WebUiAuth.IsSignInUrl("http://127.0.0.1:8765/signin?t=abc"));
        Assert.True(WebUiAuth.IsSignInUrl("http://192.168.1.2:8765/signin?once=abc"));
        Assert.False(WebUiAuth.IsSignInUrl("http://127.0.0.1:8765/"));
        Assert.False(WebUiAuth.IsSignInUrl("https://example.com/login?t=abc"));
        Assert.Equal("http://127.0.0.1:8765/signin?t=…", WebUiAuth.Redacted("http://127.0.0.1:8765/signin?t=abc"));
        Assert.Equal("http://127.0.0.1:8765/signin?once=…", WebUiAuth.Redacted("http://127.0.0.1:8765/signin?once=abc"));
        Assert.Equal("https://example.com/a?t=1", WebUiAuth.Redacted("https://example.com/a?t=1"));
        Assert.Equal(BrowserLinkOutcome.SignInLink, BrowserLinks.Refused("http://127.0.0.1:8765/signin?t=abc"));
        Assert.Contains("sign-in", BrowserLinkText.Refusal(BrowserLinkOutcome.SignInLink)!, StringComparison.Ordinal);
    }
}
