using System.Net;
using System.Text.Json;
using NeonSidekick.App;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// The 🗂️ sheet's Queue tab over HTTP (2026-11-16, plan lucid-roaming-lantern #42): the view behind the cookie with an ETag (a 404 without a
/// screen), the four changes behind the cookie and the header, a remove's body naming one side only, the modes' words read by the /queue
/// parsers (any other a 400), a Conflict a 409 with the sentence; the chat page links manage.js, served signed out as the other scripts.
/// </summary>
public sealed partial class WebUiHostTests
{
    private static WebQueueView QueueViewOf(params string[] pc) =>
        new([.. pc.Select((label, i) => new WebQueuePcLine(i, label, 0))], [new WebQueueLine(3, "from the phone", false)], "automatic", "empty", false, true, QueueMode.Names, QueueCancelMode.Names);

    [Fact]
    public async Task TheQueue_IsReadBehindTheCookie_WithAnETag_AndIs404WithoutAScreen()
    {
        using var none = await _client.SendAsync(Get(WebUiHost.QueuePath));
        Assert.Equal(HttpStatusCode.NotFound, none.StatusCode);

        _backend.QueueView = QueueViewOf("one");
        using var unsigned = await _client.SendAsync(Get(WebUiHost.QueuePath, signedIn: false));
        using var first = await _client.SendAsync(Get(WebUiHost.QueuePath));
        Assert.Equal(HttpStatusCode.Unauthorized, unsigned.StatusCode);
        var view = JsonSerializer.Deserialize(await first.Content.ReadAsStringAsync(), WebUiJsonContext.Default.WebQueueView)!;
        Assert.Equal([new WebQueuePcLine(0, "one", 0)], view.Pc);
        Assert.Equal([new WebQueueLine(3, "from the phone", false)], view.Page);
        Assert.Equal(("automatic", "empty", true), (view.Mode, view.CancelMode, view.SendOffered));
        Assert.Equal(QueueMode.Names, view.Modes);

        var again = Get(WebUiHost.QueuePath);
        again.Headers.IfNoneMatch.Add(first.Headers.ETag!);
        using var same = await _client.SendAsync(again);
        Assert.Equal(HttpStatusCode.NotModified, same.StatusCode);

        _backend.QueueView = QueueViewOf("one", "two");
        var changed = Get(WebUiHost.QueuePath);
        changed.Headers.IfNoneMatch.Add(first.Headers.ETag!);
        using var moved = await _client.SendAsync(changed);
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
    }

    [Theory]
    [InlineData(WebUiHost.QueueRemovePath, "{\"key\":3}", "remove key 3")]
    [InlineData(WebUiHost.QueueRemovePath, "{\"index\":0,\"label\":\"one\"}", "remove 0 one")]
    [InlineData(WebUiHost.QueueSendPath, "{}", "send")]
    [InlineData(WebUiHost.QueueModePath, "{\"mode\":\"manual\"}", "mode Manual")]
    [InlineData(WebUiHost.QueueModePath, "{\"mode\":\"OFF\"}", "mode Disabled")]
    [InlineData(WebUiHost.QueueCancelPath, "{\"cancel\":\"hold\"}", "cancel Hold")]
    public async Task TheQueuesChanges_NeedTheCookieAndTheHeader_AreActs_AndReachTheBackend(string path, string json, string action)
    {
        using var unsigned = await _client.SendAsync(Post(path, json, signedIn: false));
        using var crossSite = await _client.SendAsync(Post(path, json, csrf: false));
        Assert.Equal((HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden), (unsigned.StatusCode, crossSite.StatusCode));
        Assert.Empty(_backend.QueueActions);

        using var done = await _client.SendAsync(Post(path, json));
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        Assert.Equal([action], _backend.QueueActions);

        _backend.QueueReply = new WebReply(WebOutcome.Conflict, WebUiText.LineGone);
        using var stale = await _client.SendAsync(Post(path, json));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(WebUiText.LineGone, JsonSerializer.Deserialize(await stale.Content.ReadAsStringAsync(), WebUiJsonContext.Default.WebResult)!.Message);

        Assert.Equal(WebRateBucket.Act, WebRateLimit.BucketOf(path, post: true));
        Assert.Equal(WebTab.Chat, WebUiHost.TabOf(path));
    }

    [Theory]
    [InlineData(WebUiHost.QueueRemovePath, "{}")]   // neither side
    [InlineData(WebUiHost.QueueRemovePath, "{\"key\":3,\"index\":0,\"label\":\"one\"}")]   // both
    [InlineData(WebUiHost.QueueRemovePath, "{\"index\":0}")]   // a PC line needs its label
    [InlineData(WebUiHost.QueueModePath, "{\"mode\":\"bogus\"}")]
    [InlineData(WebUiHost.QueueModePath, "{}")]
    [InlineData(WebUiHost.QueueCancelPath, "{\"cancel\":\"manual\"}")]
    [InlineData(WebUiHost.QueueCancelPath, "{\"mode\":\"hold\"}")]   // the cancel's own field
    public async Task ABadQueueBody_IsA400_AndReachesNothing(string path, string json)
    {
        using var bad = await _client.SendAsync(Post(path, json));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Empty(_backend.QueueActions);
    }

    [Fact]
    public async Task TheChatPage_LinksTheSheetsScript_ServedSignedOut()
    {
        var page = await PageAsync("/");
        Assert.Contains("manage.js", page.Body, StringComparison.Ordinal);
        Assert.Contains("id=\"manage-button\"", page.Body, StringComparison.Ordinal);

        using var script = await _client.SendAsync(Get("/manage.js", signedIn: false));
        Assert.Equal(HttpStatusCode.OK, script.StatusCode);
        Assert.True(WebUiAssets.IsStatic("manage.js"));

        using var signedOut = await _client.SendAsync(Get("/", signedIn: false));
        Assert.DoesNotContain("manage.js", await signedOut.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
}
