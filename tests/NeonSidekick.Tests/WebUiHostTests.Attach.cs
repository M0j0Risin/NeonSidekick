using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using NeonSidekick.Files;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// A phone's upload over HTTP (2026-11-16, plan lucid-roaming-lantern #39): a raw body behind the cookie (401), <c>X-Neon</c> (403), a type
/// none of whose kind is CORS-safelisted (<c>text/plain</c> a 415 unread; <c>application/octet-stream</c> taken, a typeless JPEG's), a page id
/// (400), <c>Sec-Fetch-Site</c> cross-site and same-site a 403 (same-origin and none taken); 20 MB passes and a byte more is Kestrel's 413, the JSON
/// cap unchanged for <c>/api/send</c>; the backend's status written as it is; the <see cref="WebRateBucket.Attach"/> bucket spent past the checks;
/// a chip's ✕ a JSON post.
/// </summary>
public sealed partial class WebUiHostTests
{
    private const string AttachPage = "pageAAAAAAAAAAAAAAAAAA";

    private HttpRequestMessage Upload(byte[] body, string? type = "image/jpeg", string? page = AttachPage, bool csrf = true, bool signedIn = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, WebUiHost.AttachPath + (page is null ? "" : "?page=" + page)) { Content = new ByteArrayContent(body) };
        if (type is not null)
        {
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(type);
        }

        if (csrf)
        {
            request.Headers.Add(WebUiHost.CsrfHeader, "1");
        }

        if (signedIn)
        {
            request.Headers.Add("Cookie", Cookie);
        }

        return request;
    }

    [Fact]
    public async Task AnUpload_NeedsTheCookieTheHeaderAnAllowedTypeAndAPage_ThenReachesTheBackend()
    {
        byte[] body = [1, 2, 3];
        foreach (var (request, status) in new[]
        {
            (Upload(body, signedIn: false), HttpStatusCode.Unauthorized),
            (Upload(body, csrf: false), HttpStatusCode.Forbidden),
            (Upload(body, type: "text/plain"), HttpStatusCode.UnsupportedMediaType),
            (Upload(body, type: "application/json"), HttpStatusCode.UnsupportedMediaType),
            (Upload(body, type: null), HttpStatusCode.UnsupportedMediaType),
            (Upload(body, page: null), HttpStatusCode.BadRequest),
            (Upload(body, page: "short"), HttpStatusCode.BadRequest),
        })
        {
            using var answer = await _client.SendAsync(request);
            Assert.Equal(status, answer.StatusCode);
        }

        Assert.Empty(_backend.Attached);
        foreach (string type in new[] { "image/jpeg", "application/pdf", "application/octet-stream" })
        {
            using var taken = await _client.SendAsync(Upload(body, type));
            Assert.Equal(HttpStatusCode.OK, taken.StatusCode);
            var result = JsonSerializer.Deserialize(await taken.Content.ReadAsStringAsync(), WebUiJsonContext.Default.WebAttachResult)!;
            Assert.True(result.Ok);
            Assert.NotNull(result.Chip);
        }

        Assert.Equal(3, _backend.Attached.Count);
        Assert.All(_backend.Attached, a => Assert.Equal((AttachPage, 3), (a.Page, a.Bytes)));
        Assert.Equal(WebTab.Chat, WebUiHost.TabOf(WebUiHost.AttachPath));
        Assert.Null(WebRateLimit.BucketOf(WebUiHost.AttachPath, post: true));
        Assert.Equal(WebRateBucket.Act, WebRateLimit.BucketOf(WebUiHost.AttachForgetPath, post: true));
    }

    [Fact]
    public async Task ACrossSiteOrSameSiteUpload_Is403_SameOriginAndNoneAreTaken()
    {
        foreach (var (site, status) in new[] { ("cross-site", HttpStatusCode.Forbidden), ("same-site", HttpStatusCode.Forbidden), ("same-origin", HttpStatusCode.OK), ("none", HttpStatusCode.OK) })
        {
            var request = Upload([1]);
            request.Headers.Add("Sec-Fetch-Site", site);
            using var answer = await _client.SendAsync(request);
            Assert.Equal(status, answer.StatusCode);
        }

        Assert.Equal(2, _backend.Attached.Count);
    }

    [Fact]
    public async Task TwentyMegabytesPass_AByteMoreIs413_AndTheJsonCapStaysForSend()
    {
        using var whole = await _client.SendAsync(Upload(new byte[ImageFile.MaxFileBytes], "application/pdf"));
        Assert.Equal(HttpStatusCode.OK, whole.StatusCode);
        Assert.Equal((int)ImageFile.MaxFileBytes, _backend.Attached.Single().Bytes);

        using var over = await _client.SendAsync(Upload(new byte[ImageFile.MaxFileBytes + 1], "application/pdf"));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, over.StatusCode);
        Assert.Single(_backend.Attached);

        string big = "{\"text\":\"" + new string('a', (int)WebUiHost.MaxBodyBytes) + "\"}";
        using var send = await _client.SendAsync(Post("/api/send", big));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, send.StatusCode);
    }

    [Fact]
    public async Task TheBackendsStatus_IsWrittenAsItIs_WithItsWords()
    {
        foreach (var (code, words) in new[] { (409, WebUiText.AttachFull), (415, WebUiText.AttachNotPicture), (422, WebUiText.AttachHeic), (507, WebUiText.AttachTooMuch) })
        {
            _backend.AttachReply = (_, _) => new WebAttachReply(code, words, null);
            using var answer = await _client.SendAsync(Upload([9]));
            Assert.Equal(code, (int)answer.StatusCode);
            var result = JsonSerializer.Deserialize(await answer.Content.ReadAsStringAsync(), WebUiJsonContext.Default.WebResult)!;
            Assert.Equal((false, words), (result.Ok, result.Message));
        }
    }

    [Fact]
    public async Task TheEpoch_IsReadBeforeTheBody()
    {
        _backend.Epoch = 7;
        using var answer = await _client.SendAsync(Upload([1]));
        Assert.Equal(7, _backend.Attached.Single().Epoch);
    }

    [Fact]
    public async Task TheAttachBucket_GivesFourAtOnce_ThenA429()
    {
        for (int i = 0; i < 4; i++)
        {
            using var taken = await _client.SendAsync(Upload([1]));
            Assert.Equal(HttpStatusCode.OK, taken.StatusCode);
        }

        using var refused = await _client.SendAsync(Upload([1]));
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Equal(4, _backend.Attached.Count);

        // A refused check spends nothing: the bucket is taken past them.
        using var unsigned = await _client.SendAsync(Upload([1], csrf: false));
        Assert.Equal(HttpStatusCode.Forbidden, unsigned.StatusCode);
    }

    [Fact]
    public async Task AChipsForget_IsAJsonPost()
    {
        using var answer = await _client.SendAsync(Post(WebUiHost.AttachForgetPath, "{\"id\":\"abc\",\"page\":\"" + AttachPage + "\"}"));
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.Equal(("abc", AttachPage), (_backend.Forgotten.Single().Id, _backend.Forgotten.Single().Page));
    }
}
