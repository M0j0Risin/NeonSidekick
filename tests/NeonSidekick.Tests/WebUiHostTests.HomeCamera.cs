using System.Net;
using System.Text.Json;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// A camera's still on the Home tab's sheet (2026-10-27, the ring-integration plan's step 5): a GET behind the cookie and <c>X-Neon</c>, never
/// cached and kept to this origin; 404 out of reach, 502 with Home Assistant's reason, 429 past its own bucket, nothing for a HEAD.
/// </summary>
public sealed partial class WebUiHostTests
{
    private HttpRequestMessage CameraGet(string reference, bool csrf = true)
    {
        var request = Get(WebUiHost.CameraPath + "?ref=" + Uri.EscapeDataString(reference));
        if (csrf)
        {
            request.Headers.Add(WebUiHost.CsrfHeader, "1");
        }

        return request;
    }

    [Fact]
    public async Task ACamerasStill_IsServed_NeverCached_AndKeptToThisOrigin()
    {
        var home = new FakeHome();
        _backend.Home = home;

        using var response = await _client.SendAsync(CameraGet("entity:camera.porch"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(4, response.Content.Headers.ContentLength);
        Assert.Equal([0xFF, 0xD8, 0xFF, 0xD9], await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("same-origin", Assert.Single(response.Headers.GetValues("Cross-Origin-Resource-Policy")));
        Assert.Equal(["camera entity:camera.porch"], home.Calls);
    }

    [Fact]
    public async Task ACamerasStill_OutOfReach_Is404_AndHomeAssistantsReason_Is502()
    {
        var home = new FakeHome();
        _backend.Home = home;

        using var outside = await _client.SendAsync(CameraGet("entity:camera.back"));
        Assert.Equal(HttpStatusCode.NotFound, outside.StatusCode);
        var gone = JsonSerializer.Deserialize(await outside.Content.ReadAsStringAsync(), WebUiJsonContext.Default.WebResult)!;
        Assert.Equal(WebUiText.HomeChanged, gone.Message);
        Assert.Null(gone.TabOff);

        home.Still = new WebCameraAnswer(null, new WebReply(WebOutcome.Refused, "Porch Camera: the camera is off"));
        using var refused = await _client.SendAsync(CameraGet("entity:camera.porch"));
        Assert.Equal(HttpStatusCode.BadGateway, refused.StatusCode);
        Assert.Equal("Porch Camera: the camera is off", JsonSerializer.Deserialize(await refused.Content.ReadAsStringAsync(), WebUiJsonContext.Default.WebResult)!.Message);
    }

    [Fact]
    public async Task ACamerasStill_WithoutTheHeader_OrAnotherSitesAsk_IsRefused_AndHomeAssistantIsNeverAsked()
    {
        var home = new FakeHome();
        _backend.Home = home;

        using var noHeader = await _client.SendAsync(CameraGet("entity:camera.porch", csrf: false));
        using var crossSite = CameraGet("entity:camera.porch");
        crossSite.Headers.Add("Sec-Fetch-Site", "cross-site");
        using var planted = await _client.SendAsync(crossSite);
        using var signedOut = new HttpRequestMessage(HttpMethod.Get, WebUiHost.CameraPath + "?ref=entity%3Acamera.porch");
        signedOut.Headers.Add(WebUiHost.CsrfHeader, "1");
        using var anonymous = await _client.SendAsync(signedOut);
        using var head = CameraGet("entity:camera.porch");
        head.Method = HttpMethod.Head;
        using var headed = await _client.SendAsync(head);

        Assert.Equal(HttpStatusCode.Forbidden, noHeader.StatusCode);
        Assert.Equal(WebUiText.PostRefused, JsonSerializer.Deserialize(await noHeader.Content.ReadAsStringAsync(), WebUiJsonContext.Default.WebResult)!.Message);
        Assert.Equal(HttpStatusCode.Forbidden, planted.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, headed.StatusCode);
        Assert.Empty(home.Calls);
    }

    [Fact]
    public async Task ACamerasStills_PastTheirBucket_Are429_WithRetryAfter_AndAHeadSpendsNothing()
    {
        // The limits on a clock that stands still, so a slow runner never refills a token between the asks.
        await _host.DisposeAsync();
        _client.Dispose();
        _host = new WebUiHost(_backend, Path.GetTempPath(), new Fakes.ManualTimeProvider());
        await _host.StartAsync(new WebUiBinding(WebUiAudience.Local, 0, _token, IPAddress.Loopback), CancellationToken.None);
        _client = new HttpClient(new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false })
        {
            BaseAddress = new Uri("http://127.0.0.1:" + _host.Port + "/"),
            Timeout = TimeSpan.FromSeconds(30),
        };
        var home = new FakeHome();
        _backend.Home = home;
        Assert.Equal(WebRateBucket.Camera, WebRateLimit.BucketOf(WebUiHost.CameraPath, post: false, get: true));
        Assert.Null(WebRateLimit.BucketOf(WebUiHost.CameraPath, post: false, get: false));
        Assert.Null(WebRateLimit.BucketOf("/api/home/sheet", post: false, get: true));
        Assert.Contains("camera pictures", WebUiText.RateLimitedLogLine(IPAddress.Loopback, WebRateBucket.Camera), StringComparison.Ordinal);
        var (burst, perSecond) = WebRateLimit.Rate(WebRateBucket.Camera);
        Assert.Equal((3d, 1d), (burst, perSecond));

        for (int i = 0; i < 3; i++)
        {
            using var head = CameraGet("entity:camera.porch");
            head.Method = HttpMethod.Head;
            using var headed = await _client.SendAsync(head);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, headed.StatusCode);
        }

        for (int i = 0; i < (int)burst; i++)
        {
            using var ok = await _client.SendAsync(CameraGet("entity:camera.porch"));
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        using var limited = await _client.SendAsync(CameraGet("entity:camera.porch"));
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.RetryAfter?.Delta >= TimeSpan.FromSeconds(1));
        Assert.Equal(WebUiText.SlowDown, JsonSerializer.Deserialize(await limited.Content.ReadAsStringAsync(), WebUiJsonContext.Default.WebResult)!.Message);
        Assert.Equal((int)burst, home.Calls.Count);
    }
}
