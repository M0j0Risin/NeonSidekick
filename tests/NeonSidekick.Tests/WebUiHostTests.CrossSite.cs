using System.Net;
using NeonSidekick.Diagnostics;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// The server's fixes from a high-effort review of the Web UI (2026-10-19): a GET of an API another site started is refused, the sign-in link's
/// GET is rate-limited, a refused remote is logged once a minute, and two pictures of one length and write time keep their own thumbnails.
/// </summary>
public sealed partial class WebUiHostTests
{
    [Theory]
    [InlineData("cross-site", HttpStatusCode.Forbidden)]
    [InlineData("same-site", HttpStatusCode.Forbidden)]
    [InlineData("same-origin", HttpStatusCode.OK)]
    [InlineData("none", HttpStatusCode.OK)]
    [InlineData(null, HttpStatusCode.OK)]
    public async Task AnApiGet_AnotherSiteStarted_IsRefused(string? site, HttpStatusCode expected)
    {
        using var request = Get("/api/shopping");
        if (site is not null)
        {
            request.Headers.Add("Sec-Fetch-Site", site);
        }

        using var response = await _client.SendAsync(request);

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task TheEventStream_AnotherSiteOpened_IsRefused_BeforeItsSnapshot()
    {
        using var request = Get("/api/events");
        request.Headers.Add("Sec-Fetch-Site", "cross-site");

        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotEqual("text/event-stream", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task TheSignInLinksGet_IsRateLimited_AndAHeadSpendsNothing()
    {
        var (burst, _) = WebRateLimit.Rate(WebRateBucket.SignIn);
        for (int i = 0; i < 3; i++)
        {
            using var head = new HttpRequestMessage(HttpMethod.Head, WebUiAuth.SignInPath + "?once=x");
            using var headed = await _client.SendAsync(head);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, headed.StatusCode);
        }

        for (int i = 0; i < (int)burst; i++)
        {
            using var wrong = await _client.GetAsync(WebUiAuth.SignInPath + "?t=wrong");
            Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
        }

        using var limited = await _client.GetAsync(WebUiAuth.SignInPath + "?t=" + _token);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.NotNull(limited.Headers.RetryAfter);
        Assert.Empty(_backend.SignIns);
    }

    [Fact]
    public async Task ARefusedRemote_IsLoggedOnceAMinute_NotPerRequest()
    {
        await _host.DisposeAsync();
        _client.Dispose();
        _allow = _ => false;
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
            for (int i = 0; i < 5; i++)
            {
                using var response = await _client.GetAsync("/");
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            }
        }
        finally
        {
            DiagnosticLog.Emitted -= Record;
        }

        lock (lines)
        {
            // The address as Kestrel saw it (an IPv4-mapped one on a dual-stack socket): the line's words around it.
            string lead = WebUiText.RefusedRemoteLogLine(IPAddress.Loopback).Split("127.0.0.1")[0];
            Assert.Single(lines, m => m.StartsWith(lead, StringComparison.Ordinal));
        }
    }

    [WindowsFact]
    public async Task TwoFiles_OfOneLengthAndWriteTime_KeepTheirOwnThumbnails()
    {
        byte[] pink = NeonSidekick.App.SmokeChecks.SolidBmp(8, 4);
        byte[] black = (byte[])pink.Clone();
        Array.Fill(black, (byte)0, 54, black.Length - 54);
        string first = TempFile("a.bmp", pink);
        string second = TempFile("b.bmp", black);
        var stamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(first, stamp);
        File.SetLastWriteTimeUtc(second, stamp);
        _backend.Pictures[1] = new WebPictureSource("a.bmp", first, null, "image/bmp");
        _backend.Pictures[2] = new WebPictureSource("b.bmp", second, null, "image/bmp");

        using var a = await _client.SendAsync(Get(PictureUrl(1, "thumb")));
        using var b = await _client.SendAsync(Get(PictureUrl(2, "thumb")));

        Assert.Equal(HttpStatusCode.OK, a.StatusCode);
        Assert.Equal(HttpStatusCode.OK, b.StatusCode);
        Assert.NotEqual(await a.Content.ReadAsByteArrayAsync(), await b.Content.ReadAsByteArrayAsync());
    }
}
