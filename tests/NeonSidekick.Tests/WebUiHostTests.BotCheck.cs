using System.Net;
using System.Text.Json;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// A bot check's card at the host (2026-10-17): the picture of the Browser window (tagged, a 304 unchanged, 403 with the control off, 404 for a
/// check that is over, 503 when the window didn't draw, the width clamped) and the card's input (200, 403, 404, 422, the cross-site rule), their
/// bucket and their tab.
/// </summary>
public sealed partial class WebUiHostTests
{
    private static string Input(string? askId, string kind, double x = 0.5, double y = 0.5, string? key = null) =>
        JsonSerializer.Serialize(new WebBotCheckInput { AskId = askId, Kind = kind, X = x, Y = y, Key = key }, WebUiJsonContext.Default.WebBotCheckInput);

    private static async Task<string?> MessageOf(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("message").GetString();

    [Fact]
    public async Task ABotCheckPicture_IsAJpegWithItsTag_A304Unchanged_AndEachRefusalItsStatus()
    {
        _backend.BotCheckLiveId = "a7";
        using var first = await _client.SendAsync(Get(WebUiHost.BotCheckFramePath + "?ask=a7&w=640"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("image/jpeg", first.Content.Headers.ContentType?.MediaType);
        Assert.Equal("\"aaaabbbbccccdddd\"", first.Headers.ETag?.Tag);
        Assert.Equal(FakeBrowserWindow.TinyJpeg, await first.Content.ReadAsByteArrayAsync());
        Assert.Contains("no-store", first.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
        Assert.Equal("same-origin", first.Headers.GetValues("Cross-Origin-Resource-Policy").Single());

        var again = Get(WebUiHost.BotCheckFramePath + "?ask=a7&w=640");
        again.Headers.IfNoneMatch.Add(first.Headers.ETag!);
        using var unchanged = await _client.SendAsync(again);
        Assert.Equal(HttpStatusCode.NotModified, unchanged.StatusCode);
        Assert.Empty(await unchanged.Content.ReadAsByteArrayAsync());

        using var stale = await _client.SendAsync(Get(WebUiHost.BotCheckFramePath + "?ask=a6&w=640"));
        Assert.Equal(HttpStatusCode.NotFound, stale.StatusCode);
        Assert.Equal(WebUiText.BotCheckGone, await MessageOf(stale));

        _backend.BotCheckPicture = null;
        using var stalled = await _client.SendAsync(Get(WebUiHost.BotCheckFramePath + "?ask=a7&w=640"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, stalled.StatusCode);

        _backend.BotCheckControl = false;
        using var off = await _client.SendAsync(Get(WebUiHost.BotCheckFramePath + "?ask=a7&w=640"));
        Assert.Equal(HttpStatusCode.Forbidden, off.StatusCode);
        Assert.Equal(WebUiText.BotCheckControlOff, await MessageOf(off));

        using var signedOut = await _client.SendAsync(Get(WebUiHost.BotCheckFramePath + "?ask=a7&w=640", signedIn: false));
        Assert.Equal(HttpStatusCode.Unauthorized, signedOut.StatusCode);
    }

    [Theory]
    [InlineData("w=10", WebUiHost.BotCheckMinWidth)]
    [InlineData("w=99999", WebUiHost.BotCheckMaxWidth)]
    [InlineData("w=800", 800)]
    [InlineData("w=-5", WebUiHost.BotCheckMaxWidth)]
    [InlineData("", WebUiHost.BotCheckMaxWidth)]
    public async Task ABotCheckPicture_ItsWidth_IsClamped(string query, int width)
    {
        _backend.BotCheckLiveId = "a1";
        using var r = await _client.SendAsync(Get(WebUiHost.BotCheckFramePath + "?ask=a1&" + query));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(width, Assert.Single(_backend.BotCheckWidths));
    }

    [Fact]
    public async Task ABotCheckInput_Answers200_403_404_And422_AndNeedsThePagesHeader()
    {
        _backend.BotCheckLiveId = "a7";
        using (var ok = await _client.SendAsync(Post(WebUiHost.BotCheckInputPath, Input("a7", "tap", 0.25, 0.75))))
        {
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        var sent = Assert.Single(_backend.BotCheckInputs);
        Assert.Equal(("tap", 0.25, 0.75), (sent.Kind, sent.X, sent.Y));

        using (var refused = await _client.SendAsync(Post(WebUiHost.BotCheckInputPath, Input("a7", "key", key: "Escape"))))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        }

        using (var stale = await _client.SendAsync(Post(WebUiHost.BotCheckInputPath, Input("a6", "tap"))))
        {
            Assert.Equal(HttpStatusCode.NotFound, stale.StatusCode);
        }

        using (var crossSite = await _client.SendAsync(Post(WebUiHost.BotCheckInputPath, Input("a7", "tap"), csrf: false)))
        {
            Assert.Equal(HttpStatusCode.Forbidden, crossSite.StatusCode);
        }

        using (var bad = await _client.SendAsync(Post(WebUiHost.BotCheckInputPath, "not json")))
        {
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        }

        _backend.BotCheckControl = false;
        using (var off = await _client.SendAsync(Post(WebUiHost.BotCheckInputPath, Input("a7", "tap"))))
        {
            Assert.Equal(HttpStatusCode.Forbidden, off.StatusCode);
            Assert.Equal(WebUiText.BotCheckControlOff, await MessageOf(off));
        }

        Assert.Single(_backend.BotCheckInputs);
        Assert.Equal(WebRateBucket.BotCheck, WebRateLimit.BucketOf(WebUiHost.BotCheckInputPath, post: true));
        Assert.Null(WebRateLimit.BucketOf(WebUiHost.BotCheckFramePath, post: false));
        Assert.Equal((30d, 10d), WebRateLimit.Rate(WebRateBucket.BotCheck));
        Assert.Equal(WebTab.Chat, WebUiHost.TabOf(WebUiHost.BotCheckInputPath));
        Assert.Equal(WebTab.Chat, WebUiHost.TabOf(WebUiHost.BotCheckFramePath));
    }

    [Fact]
    public async Task ABotCheckCard_WithTheChatTabOff_IsTheTabOff404()
    {
        _backend.BotCheckLiveId = "a7";
        _backend.TabsOn = new WebTabs(false, true, true);
        using var picture = await _client.SendAsync(Get(WebUiHost.BotCheckFramePath + "?ask=a7"));
        Assert.Equal(HttpStatusCode.NotFound, picture.StatusCode);
        Assert.Contains("\"tabOff\":true", await picture.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        using var input = await _client.SendAsync(Post(WebUiHost.BotCheckInputPath, Input("a7", "tap")));
        Assert.Equal(HttpStatusCode.NotFound, input.StatusCode);
        Assert.Empty(_backend.BotCheckWidths);
        Assert.Empty(_backend.BotCheckInputs);
    }
}
