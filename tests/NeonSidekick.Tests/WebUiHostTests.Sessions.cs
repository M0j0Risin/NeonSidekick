using System.Net;
using System.Text.Json;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// The 🗂️ sheet's Sessions tab over HTTP (2026-11-16, plan lucid-roaming-lantern #40): the view behind the cookie with its query and an ETag
/// (a 404 without a screen); a resume behind the cookie and the header, an Act, its body needing an id and a profile, a Conflict a 409.
/// </summary>
public sealed partial class WebUiHostTests
{
    private static WebSessionsView SessionsViewOf(params string[] titles) =>
        new("default", [.. titles.Select((t, i) => new WebSessionRow(i + 1, t, "today", "1 turn", "gemma", false))], [], true);

    [Fact]
    public async Task TheSessions_AreReadBehindTheCookie_WithTheQuery_AndAnETag_And404WithoutAScreen()
    {
        using var none = await _client.SendAsync(Get(WebUiHost.SessionsPath));
        Assert.Equal(HttpStatusCode.NotFound, none.StatusCode);

        _backend.SessionsFor = _ => SessionsViewOf("Trip");
        using var unsigned = await _client.SendAsync(Get(WebUiHost.SessionsPath, signedIn: false));
        Assert.Equal(HttpStatusCode.Unauthorized, unsigned.StatusCode);
        using var first = await _client.SendAsync(Get(WebUiHost.SessionsPath + "?q=tax%20forms"));
        var view = JsonSerializer.Deserialize(await first.Content.ReadAsStringAsync(), WebUiJsonContext.Default.WebSessionsView)!;
        Assert.Equal("Trip", Assert.Single(view.Rows).Title);
        Assert.Contains("tax forms", _backend.SessionQueries);

        var again = Get(WebUiHost.SessionsPath + "?q=tax%20forms");
        again.Headers.IfNoneMatch.Add(first.Headers.ETag!);
        using var same = await _client.SendAsync(again);
        Assert.Equal(HttpStatusCode.NotModified, same.StatusCode);

        Assert.Equal(WebTab.Chat, WebUiHost.TabOf(WebUiHost.SessionsPath));
    }

    [Fact]
    public async Task AResume_NeedsTheCookieAndTheHeader_IsAnAct_AndAConflictIsA409()
    {
        const string Json = "{\"id\":3,\"profile\":\"default\"}";
        using var unsigned = await _client.SendAsync(Post(WebUiHost.SessionResumePath, Json, signedIn: false));
        using var crossSite = await _client.SendAsync(Post(WebUiHost.SessionResumePath, Json, csrf: false));
        Assert.Equal((HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden), (unsigned.StatusCode, crossSite.StatusCode));
        Assert.Empty(_backend.SessionResumes);

        using var done = await _client.SendAsync(Post(WebUiHost.SessionResumePath, Json));
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        Assert.Equal([new WebSessionResume(3, "default")], _backend.SessionResumes);

        _backend.SessionReply = new WebReply(WebOutcome.Conflict, WebUiText.ProfileSwitched);
        using var stale = await _client.SendAsync(Post(WebUiHost.SessionResumePath, Json));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(WebUiText.ProfileSwitched, JsonSerializer.Deserialize(await stale.Content.ReadAsStringAsync(), WebUiJsonContext.Default.WebResult)!.Message);

        Assert.Equal(WebRateBucket.Act, WebRateLimit.BucketOf(WebUiHost.SessionResumePath, post: true));
        Assert.Equal(WebTab.Chat, WebUiHost.TabOf(WebUiHost.SessionResumePath));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"id\":3}")]
    [InlineData("{\"id\":3,\"profile\":\"\"}")]
    [InlineData("{\"id\":0,\"profile\":\"default\"}")]
    [InlineData("{\"id\":-2,\"profile\":\"default\"}")]
    public async Task ABadResumeBody_IsA400_AndReachesNothing(string json)
    {
        using var bad = await _client.SendAsync(Post(WebUiHost.SessionResumePath, json));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Empty(_backend.SessionResumes);
    }
}
