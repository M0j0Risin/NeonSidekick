using System.Net;
using System.Text.Json;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// Each tab only while its controls are on (later on 2026-10-15, the user's call): a page whose tab is off goes to <c>/</c>, <c>/</c> with Chat off
/// goes to the first tab that is on or the page that says none is, a tab's APIs answer a 404 the page reads as "go to /", the pulse answers
/// whatever is on, and an open chat stream ends when Chat is turned off. The strip's pick from the page, behind the cookie, the header and Chat.
/// </summary>
public sealed partial class WebUiHostTests
{
    private async Task<(HttpStatusCode Status, string? Location, string Body)> PageAsync(string path)
    {
        using var response = await _client.SendAsync(Get(path));
        return (response.StatusCode, response.Headers.Location?.OriginalString, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task WithChatOff_SlashGoesToHome_ElseShopping_ElseThePageThatSaysNoneIsOn()
    {
        _backend.TabsOn = new WebTabs(Chat: false, Home: true, Shopping: true);
        Assert.Equal((HttpStatusCode.SeeOther, "/home"), (await PageAsync("/")) is var home ? (home.Status, home.Location) : default);

        _backend.TabsOn = new WebTabs(Chat: false, Home: false, Shopping: true);
        Assert.Equal((HttpStatusCode.SeeOther, "/shopping"), (await PageAsync("/index.html")) is var shopping ? (shopping.Status, shopping.Location) : default);

        _backend.TabsOn = new WebTabs(Chat: false, Home: false, Shopping: false);
        var none = await PageAsync("/");
        Assert.Equal(HttpStatusCode.OK, none.Status);
        Assert.Contains("notabs.js", none.Body, StringComparison.Ordinal);
        Assert.Contains("Web UI chat controls", none.Body, StringComparison.Ordinal);
        Assert.Contains("Web UI home controls", none.Body, StringComparison.Ordinal);
        Assert.Contains("Web UI shopping controls", none.Body, StringComparison.Ordinal);

        // Signed out, the sign-in page as ever, whatever is on.
        using var signedOut = await _client.SendAsync(Get("/", signedIn: false));
        Assert.Equal(HttpStatusCode.OK, signedOut.StatusCode);
        Assert.Contains("signin.js", await signedOut.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/home")]
    [InlineData("/home.html")]
    [InlineData("/shopping")]
    [InlineData("/shopping.html")]
    public async Task APageWhoseTabIsOff_GoesToSlash_NeverCached(string path)
    {
        _backend.TabsOn = new WebTabs(Chat: true, Home: false, Shopping: false);

        using var response = await _client.SendAsync(Get(path));

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Equal("/", response.Headers.Location?.OriginalString);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    [Theory]
    [InlineData("/api/events", WebTab.Chat)]
    [InlineData("/api/usage", WebTab.Chat)]
    [InlineData("/api/entry/4", WebTab.Chat)]
    [InlineData("/api/picture/0123456789abcdef0123456789abcdef/1/thumb", WebTab.Chat)]
    [InlineData("/api/shopping", WebTab.Shopping)]
    [InlineData("/api/home", WebTab.Home)]
    [InlineData("/api/home/sheet?ref=x", WebTab.Home)]
    public async Task AReadOfATabThatIsOff_Is404_WithTabOff_AndItsSentence(string path, WebTab tab)
    {
        _backend.TabsOn = new WebTabs(tab != WebTab.Chat, tab != WebTab.Home, tab != WebTab.Shopping);

        using var response = await _client.SendAsync(Get(path));
        var body = JsonSerializer.Deserialize(await response.Content.ReadAsStringAsync(), WebUiJsonContext.Default.WebResult)!;

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.True(body.TabOff);
        Assert.Equal(WebUiText.TabOff(tab), body.Message);
    }

    [Theory]
    [InlineData("/api/send", "{\"text\":\"hi\"}", WebTab.Chat)]
    [InlineData("/api/stop", "{}", WebTab.Chat)]
    [InlineData("/api/answer", "{\"askId\":\"a\",\"choice\":\"b\"}", WebTab.Chat)]
    [InlineData("/api/complete", "{\"text\":\"/\",\"cursor\":1,\"seq\":1}", WebTab.Chat)]
    [InlineData("/api/strip/select", "{\"id\":1}", WebTab.Chat)]
    [InlineData("/api/shopping/check", "{\"id\":1,\"on\":true}", WebTab.Shopping)]
    [InlineData("/api/shopping/store", "{\"id\":1}", WebTab.Shopping)]
    [InlineData("/api/home/tap", "{\"ref\":\"x\"}", WebTab.Home)]
    public async Task APostToATabThatIsOff_Is404_AndNeverReachesTheBackend(string path, string json, WebTab tab)
    {
        _backend.TabsOn = new WebTabs(tab != WebTab.Chat, tab != WebTab.Home, tab != WebTab.Shopping);
        _backend.StripIds[1] = true;

        using var response = await _client.SendAsync(Post(path, json));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("\"tabOff\":true", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Empty(_backend.Sent);
        Assert.Equal(0, _backend.Stops);
        Assert.Empty(_backend.Answers);
        Assert.Empty(_backend.Completed);
        Assert.Empty(_backend.StripPicks);
        Assert.Empty(_backend.Checks);
        Assert.Empty(_backend.Stores);
    }

    [Fact]
    public async Task ThePulse_AnswersWithEveryTabOff_AndTheOtherTabsStillWork()
    {
        _backend.TabsOn = new WebTabs(Chat: false, Home: false, Shopping: false);
        _backend.PulseNow = new WebPulse(false, 0, null, "", "tag", ChatTab: false, HomeTab: false, ShoppingTab: false);
        using var pulse = await _client.SendAsync(Get("/api/pulse"));
        Assert.Equal(HttpStatusCode.OK, pulse.StatusCode);
        Assert.Contains("\"chatTab\":false", await pulse.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // Chat off leaves the shopping list where it is.
        _backend.TabsOn = new WebTabs(Chat: false, Home: false, Shopping: true);
        using var shopping = await _client.SendAsync(Get("/api/shopping"));
        Assert.Equal(HttpStatusCode.OK, shopping.StatusCode);
        using var page = await _client.SendAsync(Get("/shopping"));
        Assert.Contains("shopping.js", await page.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOpenStream_EndsOnceChatIsTurnedOff()
    {
        using var response = await _client.SendAsync(Get("/api/events"), HttpCompletionOption.ResponseHeadersRead);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        Assert.Equal("event: snapshot", await reader.ReadLineAsync());

        _backend.TabsOn = new WebTabs(Chat: false, Home: true, Shopping: true);
        _backend.Events.Publish(new WebEvent("state", () => "{}"));   // the next batch is never written: the stream ends first

        string? line;
        var lines = new List<string>();
        while ((line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20))) is not null)
        {
            lines.Add(line);
        }

        Assert.DoesNotContain("event: state", lines);
    }

    [Fact]
    public async Task TheStripsPick_ReachesTheBackend_AndOneItDoesntHoldIs404()
    {
        _backend.StripIds[7] = true;

        using var picked = await _client.SendAsync(Post("/api/strip/select", "{\"id\":7}"));
        using var gone = await _client.SendAsync(Post("/api/strip/select", "{\"id\":8}"));
        using var unsigned = await _client.SendAsync(Post("/api/strip/select", "{\"id\":7}", signedIn: false));
        using var crossSite = await _client.SendAsync(Post("/api/strip/select", "{\"id\":7}", csrf: false));

        Assert.Equal(HttpStatusCode.OK, picked.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        Assert.Contains(JsonSerializer.Serialize(WebUiText.NoSuchPicture), await gone.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.DoesNotContain("tabOff", await gone.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Unauthorized, unsigned.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, crossSite.StatusCode);
        Assert.Equal([7], _backend.StripPicks);
        Assert.Equal(WebRateBucket.Act, WebRateLimit.BucketOf("/api/strip/select", post: true));
    }

    [Fact]
    public async Task TheNothingOnPagesScript_NeedsNoCookie_AndThePageIsNotStatic()
    {
        using var script = await _client.SendAsync(Get("/notabs.js", signedIn: false));
        Assert.Equal(HttpStatusCode.OK, script.StatusCode);
        Assert.False(WebUiAssets.IsStatic(WebUiAssets.NoTabsPage));
        using var page = await _client.SendAsync(Get("/notabs.html", signedIn: false));
        Assert.Equal(HttpStatusCode.NotFound, page.StatusCode);   // served at / alone
    }

    [Theory]
    [InlineData("/api/pulse", null)]
    [InlineData("/api/events", WebTab.Chat)]
    [InlineData("/api/strip/select", WebTab.Chat)]
    [InlineData("/api/entry/12", WebTab.Chat)]
    [InlineData("/api/picture/r/1/full", WebTab.Chat)]
    [InlineData("/api/shopping", WebTab.Shopping)]
    [InlineData("/api/shopping/store", WebTab.Shopping)]
    [InlineData("/api/shoppingx", null)]
    [InlineData("/api/home", WebTab.Home)]
    [InlineData("/api/home/video", WebTab.Home)]
    [InlineData("/api/homes", null)]
    public void TabOf_NamesEachApisTab(string path, WebTab? tab)
    {
        Assert.Equal(tab, WebUiHost.TabOf(path));
    }
}
