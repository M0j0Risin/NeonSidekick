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
    [InlineData("/api/prompts", WebTab.Chat)]
    [InlineData("/api/memory", WebTab.Chat)]
    [InlineData("/api/history", WebTab.Chat)]
    [InlineData("/api/entry/4", WebTab.Chat)]
    [InlineData("/api/picture/0123456789abcdef0123456789abcdef/1/thumb", WebTab.Chat)]
    [InlineData("/api/shopping", WebTab.Shopping)]
    [InlineData("/api/shopping/bought", WebTab.Shopping)]   // the 🕘 sheet (2026-10-16)
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
    [InlineData("/api/picture/delete", "{\"id\":1}", WebTab.Chat)]
    [InlineData("/api/prompts/save", "{\"text\":\"x\"}", WebTab.Chat)]
    [InlineData("/api/prompts/delete", "{\"id\":\"p1\"}", WebTab.Chat)]
    [InlineData("/api/memory/save", "{\"text\":\"x\"}", WebTab.Chat)]
    [InlineData("/api/memory/delete", "{\"text\":\"x\"}", WebTab.Chat)]
    [InlineData("/api/history/clear", "{}", WebTab.Chat)]
    [InlineData("/api/learn/cancel", "{}", WebTab.Chat)]
    [InlineData("/api/queue/resume", "{}", WebTab.Chat)]
    [InlineData("/api/shopping/check", "{\"id\":1,\"on\":true}", WebTab.Shopping)]
    [InlineData("/api/shopping/store", "{\"id\":1}", WebTab.Shopping)]
    [InlineData("/api/shopping/add", "{\"text\":\"milk\"}", WebTab.Shopping)]   // the list's own controls (2026-10-16)
    [InlineData("/api/shopping/readd", "{\"id\":1}", WebTab.Shopping)]
    [InlineData("/api/shopping/forget", "{\"id\":1}", WebTab.Shopping)]
    [InlineData("/api/shopping/quantity", "{\"id\":1,\"qty\":2}", WebTab.Shopping)]
    [InlineData("/api/shopping/rename", "{\"id\":1,\"name\":\"x\"}", WebTab.Shopping)]
    [InlineData("/api/shopping/remove", "{\"id\":1}", WebTab.Shopping)]
    [InlineData("/api/shopping/checkall", "{\"on\":true}", WebTab.Shopping)]
    [InlineData("/api/shopping/clear", "{}", WebTab.Shopping)]
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
        Assert.Empty(_backend.ShopCalls);
        Assert.Empty(_backend.PromptsSaved);
        Assert.Empty(_backend.PromptsDeleted);
        Assert.Empty(_backend.MemoriesSaved);
        Assert.Empty(_backend.MemoriesDeleted);
        Assert.Equal(0, _backend.HistoryClears);
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
    public async Task ThePrompts_AreReadBehindTheCookie_AndASendsAnswerCarriesOpen()
    {
        _backend.PromptList.AddRange(["one", "two"]);
        _backend.Reply = new WebReply(WebOutcome.Ok, Open: WebUiText.OpenPrompts);

        using var prompts = await _client.SendAsync(Get("/api/prompts"));
        using var unsigned = await _client.SendAsync(Get("/api/prompts", signedIn: false));
        using var sent = await _client.SendAsync(Post("/api/send", "{\"id\":\"a\",\"text\":\"/prompts\"}"));

        var sheet = JsonSerializer.Deserialize(await prompts.Content.ReadAsStringAsync(), WebUiJsonContext.Default.WebPrompts)!;
        Assert.Equal([new WebPrompt(1, "one", "p1"), new WebPrompt(2, "two", "p2")], sheet.Items);
        Assert.Equal("no-store", prompts.Headers.CacheControl?.ToString());
        Assert.Equal(HttpStatusCode.Unauthorized, unsigned.StatusCode);
        Assert.Equal(WebUiText.OpenPrompts, JsonSerializer.Deserialize(await sent.Content.ReadAsStringAsync(), WebUiJsonContext.Default.WebResult)!.Open);
        Assert.Equal(WebTab.Chat, WebUiHost.TabOf("/api/prompts"));
    }

    [Fact]
    public async Task ThePagesDelete_ReachesTheBackend_BehindTheCookieAndTheHeader_InTheActBucket()
    {
        using var deleted = await _client.SendAsync(Post("/api/picture/delete", "{\"id\":7}"));
        using var unsigned = await _client.SendAsync(Post("/api/picture/delete", "{\"id\":7}", signedIn: false));
        using var crossSite = await _client.SendAsync(Post("/api/picture/delete", "{\"id\":7}", csrf: false));
        _backend.DeleteReply = new WebReply(WebOutcome.NotFound, WebUiText.PictureNotHere);
        using var gone = await _client.SendAsync(Post("/api/picture/delete", "{\"id\":8}"));
        using var get = await _client.SendAsync(Get("/api/picture/delete"));

        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.Contains(JsonSerializer.Serialize(_backend.DeleteMessage), await deleted.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Unauthorized, unsigned.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, crossSite.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);   // a GET names no picture
        Assert.Equal([7, 8], _backend.Deleted);
        Assert.Equal(WebRateBucket.Act, WebRateLimit.BucketOf("/api/picture/delete", post: true));
        Assert.Equal(WebTab.Chat, WebUiHost.TabOf("/api/picture/delete"));
    }

    // ── The sheet (2026-10-16): prompts saved and deleted, memories, the history ──

    [Theory]
    [InlineData("/api/prompts/save", "{\"text\":\"Summarize.\"}")]
    [InlineData("/api/prompts/delete", "{\"id\":\"p1\"}")]
    [InlineData("/api/memory/save", "{\"text\":\"Likes tea.\"}")]
    [InlineData("/api/memory/delete", "{\"text\":\"Likes tea.\"}")]
    [InlineData("/api/history/clear", "{}")]
    public async Task TheSheetsChanges_NeedTheCookieAndTheHeader_AreActs_AndAnswerTheBackendsSentence(string path, string json)
    {
        _backend.PromptList.Add("one");
        _backend.MemoryList.Add(new WebMemory("Likes tea.", false));
        _backend.HistoryLines.Add("hi");

        using var unsigned = await _client.SendAsync(Post(path, json, signedIn: false));
        using var crossSite = await _client.SendAsync(Post(path, json, csrf: false));
        Assert.Equal((HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden), (unsigned.StatusCode, crossSite.StatusCode));
        Assert.Equal((0, 0, 0, 0, 0), (_backend.PromptsSaved.Count, _backend.PromptsDeleted.Count, _backend.MemoriesSaved.Count, _backend.MemoriesDeleted.Count, _backend.HistoryClears));

        using var done = await _client.SendAsync(Post(path, json));
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        var body = JsonSerializer.Deserialize(await done.Content.ReadAsStringAsync(), WebUiJsonContext.Default.WebResult)!;
        Assert.True(body.Ok);
        Assert.False(string.IsNullOrEmpty(body.Message));
        Assert.Equal(1, _backend.PromptsSaved.Count + _backend.PromptsDeleted.Count + _backend.MemoriesSaved.Count + _backend.MemoriesDeleted.Count + _backend.HistoryClears);

        _backend.ListReply = new WebReply(WebOutcome.Refused, "Error: no.");
        using var refused = await _client.SendAsync(Post(path, json));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);

        using var get = await _client.SendAsync(Get(path));
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);   // a change is a post
        Assert.Equal(WebRateBucket.Act, WebRateLimit.BucketOf(path, post: true));
        Assert.Null(WebRateLimit.BucketOf(path, post: false));
    }

    /// <summary>
    /// The status row's 🧠 (2026-10-17, the user's ask): a post with no body that reaches the backend's cancel once signed in and
    /// carrying the header, an act in its bucket, its Conflict a 409 with the sentence.
    /// </summary>
    [Fact]
    public async Task TheLearnCancel_NeedsTheCookieAndTheHeader_IsAnAct_AndAnswersTheBackend()
    {
        using var unsigned = await _client.SendAsync(Post(WebUiHost.LearnCancelPath, "{}", signedIn: false));
        using var crossSite = await _client.SendAsync(Post(WebUiHost.LearnCancelPath, "{}", csrf: false));
        Assert.Equal((HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden), (unsigned.StatusCode, crossSite.StatusCode));
        Assert.Equal(0, _backend.LearnCancels);

        using var done = await _client.SendAsync(Post(WebUiHost.LearnCancelPath, "{}"));
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        Assert.Equal(1, _backend.LearnCancels);

        _backend.LearnReply = new WebReply(WebOutcome.Conflict, WebUiText.NoReflection);
        using var none = await _client.SendAsync(Post(WebUiHost.LearnCancelPath, "{}"));
        Assert.Equal(HttpStatusCode.Conflict, none.StatusCode);
        var body = JsonSerializer.Deserialize(await none.Content.ReadAsStringAsync(), WebUiJsonContext.Default.WebResult)!;
        Assert.Equal(WebUiText.NoReflection, body.Message);

        Assert.Equal(WebRateBucket.Act, WebRateLimit.BucketOf(WebUiHost.LearnCancelPath, post: true));
        Assert.Equal(WebTab.Chat, WebUiHost.TabOf(WebUiHost.LearnCancelPath));
    }

    /// <summary>The held queue's Resume (2026-10-17): the same shape as the 🧠's cancel.</summary>
    [Fact]
    public async Task TheQueuesResume_NeedsTheCookieAndTheHeader_IsAnAct_AndAnswersTheBackend()
    {
        using var unsigned = await _client.SendAsync(Post(WebUiHost.QueueResumePath, "{}", signedIn: false));
        using var crossSite = await _client.SendAsync(Post(WebUiHost.QueueResumePath, "{}", csrf: false));
        Assert.Equal((HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden), (unsigned.StatusCode, crossSite.StatusCode));
        Assert.Equal(0, _backend.Resumes);

        using var done = await _client.SendAsync(Post(WebUiHost.QueueResumePath, "{}"));
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        Assert.Equal(1, _backend.Resumes);

        _backend.ResumeReply = new WebReply(WebOutcome.Conflict, WebUiText.NotHeld);
        using var none = await _client.SendAsync(Post(WebUiHost.QueueResumePath, "{}"));
        Assert.Equal(HttpStatusCode.Conflict, none.StatusCode);
        Assert.Equal(WebRateBucket.Act, WebRateLimit.BucketOf(WebUiHost.QueueResumePath, post: true));
    }

    [Theory]
    [InlineData("/api/prompts/save", "{\"words\":1")]
    [InlineData("/api/prompts/delete", "[]")]
    [InlineData("/api/memory/save", "nope")]
    [InlineData("/api/memory/delete", "{")]
    public async Task ASheetChangeThatDoesNotRead_Is400(string path, string json)
    {
        using var response = await _client.SendAsync(Post(path, json));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_backend.PromptsSaved);
        Assert.Empty(_backend.MemoriesSaved);
    }

    [Fact]
    public async Task TheMemories_AndTheHistory_AreReadBehindTheCookie_TheHistoryTagged()
    {
        _backend.MemoryList.AddRange([new WebMemory("Likes tea.", false), new WebMemory("Uses Obsidian.", true)]);
        _backend.MemoryModeOff = true;
        _backend.HistoryLines.AddRange(["first", "second"]);

        using var memories = await _client.SendAsync(Get("/api/memory"));
        using var unsigned = await _client.SendAsync(Get("/api/memory", signedIn: false));
        var sheet = JsonSerializer.Deserialize(await memories.Content.ReadAsStringAsync(), WebUiJsonContext.Default.WebMemories)!;
        Assert.Equal([new WebMemory("Likes tea.", false), new WebMemory("Uses Obsidian.", true)], sheet.Items);
        Assert.True(sheet.ModeOff);
        Assert.Equal("no-store", memories.Headers.CacheControl?.ToString());
        Assert.Equal(HttpStatusCode.Unauthorized, unsigned.StatusCode);

        using var history = await _client.SendAsync(Get("/api/history"));
        Assert.Equal(["first", "second"], JsonSerializer.Deserialize(await history.Content.ReadAsStringAsync(), WebUiJsonContext.Default.WebHistory)!.Lines);
        string etag = history.Headers.ETag!.ToString();
        var again = Get("/api/history");
        again.Headers.TryAddWithoutValidation("If-None-Match", etag);
        using var same = await _client.SendAsync(again);
        Assert.Equal(HttpStatusCode.NotModified, same.StatusCode);   // the same lines: no body

        lock (_backend.ListsGate)
        {
            _backend.HistoryLines.Add("third");
        }

        var changed = Get("/api/history");
        changed.Headers.TryAddWithoutValidation("If-None-Match", etag);
        using var fresh = await _client.SendAsync(changed);
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
        Assert.Equal(["first", "second", "third"], JsonSerializer.Deserialize(await fresh.Content.ReadAsStringAsync(), WebUiJsonContext.Default.WebHistory)!.Lines);
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
    [InlineData("/api/prompts/save", WebTab.Chat)]
    [InlineData("/api/prompts/delete", WebTab.Chat)]
    [InlineData("/api/memory", WebTab.Chat)]
    [InlineData("/api/memory/save", WebTab.Chat)]
    [InlineData("/api/memory/delete", WebTab.Chat)]
    [InlineData("/api/history", WebTab.Chat)]
    [InlineData("/api/history/clear", WebTab.Chat)]
    [InlineData("/api/learn/cancel", WebTab.Chat)]
    [InlineData("/api/queue/resume", WebTab.Chat)]
    [InlineData("/api/memoryx", null)]
    [InlineData("/api/entry/12", WebTab.Chat)]
    [InlineData("/api/picture/r/1/full", WebTab.Chat)]
    [InlineData("/api/shopping", WebTab.Shopping)]
    [InlineData("/api/shopping/store", WebTab.Shopping)]
    [InlineData("/api/shopping/bought", WebTab.Shopping)]
    [InlineData("/api/shopping/checkall", WebTab.Shopping)]
    [InlineData("/api/shoppingx", null)]
    [InlineData("/api/home", WebTab.Home)]
    [InlineData("/api/home/video", WebTab.Home)]
    [InlineData("/api/homes", null)]
    public void TabOf_NamesEachApisTab(string path, WebTab? tab)
    {
        Assert.Equal(tab, WebUiHost.TabOf(path));
    }
}
