using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Plans;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.Viewer;

namespace NeonSidekick.Tests;

/// <summary>
/// The saved-link tools (later still on 2026-10-13, the user's ask): <c>web_links</c> and <c>web_save_link</c> over the profile's
/// <c>browser-links.json</c>, the list <c>/browser list</c> and the Browser window's ☆ menu show; <c>current</c> through a fake window.
/// </summary>
public sealed class WebLinksToolsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new();
    private readonly FakeBrowserWindow _window = new();
    private int _changed;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private BrowserLinks Links() => new(_dir, _time);

    /// <summary>The hooks as the screen gives them on Windows: the page the fake shows, none while it says it cannot take a page.</summary>
    private WebLinkHooks WithWindow() => new(Links, _time,
        () => _window.Unavailable() is null ? _window.CurrentPageAsync() : Task.FromResult<(string Url, string? Title)?>(null),
        () => _changed++);

    private WebLinkHooks Headless() => new(Links, _time);

    private static async Task<string> Call(AIFunction tool, params (string Name, object? Value)[] pairs) =>
        (string)(await tool.InvokeAsync(new AIFunctionArguments(pairs.ToDictionary(p => p.Name, p => p.Value))))!;

    private Task<string> Save(params (string Name, object? Value)[] pairs) => Call(new WebSaveLinkTool(WithWindow()), pairs);

    [Fact]
    public void Names_Arguments_AndPlanMode_ArePinned()
    {
        Assert.Equal("web_links", WebLinksTool.ToolName);
        Assert.Equal("filter", WebLinksTool.FilterArgument);
        Assert.Equal(100, WebLinksTool.MaxListed);
        Assert.Equal("web_save_link", WebSaveLinkTool.ToolName);
        Assert.Equal(["action", "url", "name"], [WebSaveLinkTool.ActionArgument, WebSaveLinkTool.UrlArgument, WebSaveLinkTool.NameArgument]);
        Assert.Equal(["add", "remove", "rename", "current"], [WebSaveLinkTool.Add, WebSaveLinkTool.Remove, WebSaveLinkTool.Rename, WebSaveLinkTool.Current]);
        Assert.Contains("web_links", PlanTools.ReadOnly);
        Assert.Contains("web_save_link", PlanTools.Mutating);
        Assert.Contains("web_save_link", ChatScreen.QuietTools);
        Assert.DoesNotContain("web_links", ChatScreen.QuietTools);
    }

    [Fact]
    public void Descriptions_ArePinned_AndCurrentIsOfferedOnlyWithAWindow()
    {
        Assert.Equal(
            "Lists the user's saved web links (their bookmarks), numbered, each with its name, address and when it was saved; filter narrows them by words. open_url opens one.",
            new WebLinksTool(Headless()).Description);
        Assert.Equal(
            "Saves a web link to the user's saved links (their bookmarks), removes one, or renames one. Save only what the user asked to keep.",
            new WebSaveLinkTool(Headless()).Description);
        Assert.EndsWith(" url \"current\" is the page the app's Browser window shows.", new WebSaveLinkTool(WithWindow()).Description);

        string Url(WebLinkHooks hooks) => new WebSaveLinkTool(hooks).JsonSchema.GetProperty("properties").GetProperty("url").GetProperty("description").GetString()!;
        Assert.DoesNotContain("current", Url(Headless()));
        Assert.Contains("\"current\"", Url(WithWindow()));
    }

    [Fact]
    public async Task List_None_All_AndNumberedByTheirPlaceInTheWholeList()
    {
        Assert.Equal(BrowserLinkText.NoneSavedForModel, await Call(new WebLinksTool(Headless())));

        var links = Links();
        links.Add("https://docs.python.org/3/tutorial/", "Python — The Tutorial", out _);
        links.Add("https://example.com/", null, out _);
        links.Add("https://news.example.org/", "News", out _);
        var list = links.List();
        var zone = _time.LocalTimeZone;

        Assert.Equal(
            BrowserLinkText.ToolCaption(3, 3, "") + "\n" +
            BrowserLinkText.ToolRow(1, list[0], zone) + "\n" + BrowserLinkText.ToolRow(2, list[1], zone) + "\n" + BrowserLinkText.ToolRow(3, list[2], zone),
            await Call(new WebLinksTool(Headless())));

        // Words in any order, each in the name or the address; the numbers stay the whole list's.
        Assert.Equal(
            BrowserLinkText.ToolCaption(3, 1, "tutorial python") + "\n" + BrowserLinkText.ToolRow(1, list[0], zone),
            await Call(new WebLinksTool(Headless()), ("filter", "tutorial python")));
        Assert.Equal(
            BrowserLinkText.ToolCaption(3, 1, "example.org") + "\n" + BrowserLinkText.ToolRow(3, list[2], zone),
            await Call(new WebLinksTool(Headless()), ("filter", "example.org")));
        Assert.Equal(BrowserLinkText.NoneMatch("python news", 3), await Call(new WebLinksTool(Headless()), ("filter", "python news")));
    }

    [Fact]
    public void List_StopsAtTheCap_AndSaysHowManyMore()
    {
        var links = Enumerable.Range(1, WebLinksTool.MaxListed + 3).Select(i => new BrowserLink($"https://example.com/{i}", null, _time.GetUtcNow())).ToList();

        string answer = WebLinksTool.List(links, "", _time.LocalTimeZone);

        Assert.Equal(WebLinksTool.MaxListed + 2, answer.Split('\n').Length);
        Assert.EndsWith("\n" + BrowserLinkText.ToolMore(3), answer);
    }

    [Fact]
    public void MatchesWords_EveryWord_AnyOrder_BlankKeepsAll()
    {
        var link = new BrowserLink("https://docs.python.org/3/tutorial/", "The Tutorial", _time.GetUtcNow());

        Assert.True(BrowserLinkText.MatchesWords("python tutorial", link));
        Assert.True(BrowserLinkText.MatchesWords("  TUTORIAL  ", link));
        Assert.True(BrowserLinkText.MatchesWords("", link));
        Assert.True(BrowserLinkText.MatchesWords(null, link));
        Assert.False(BrowserLinkText.MatchesWords("python rust", link));
    }

    [Fact]
    public async Task Add_ABareAddress_IsMadeHttps_AndASecondAddSaysSaved()
    {
        Assert.Equal(BrowserLinkText.Saved(new BrowserLink("https://example.com/page", "Example", _time.GetUtcNow())), await Save(("action", "add"), ("url", "example.com/page"), ("name", "Example")));
        Assert.Equal(1, _changed);

        var saved = Assert.Single(Links().List());
        Assert.Equal("https://example.com/page", saved.Url);
        Assert.Equal(BrowserLinkText.AlreadySavedForModel(saved), await Save(("action", "add"), ("url", "https://example.com/page"), ("name", "Another")));
        Assert.Equal("Example", Assert.Single(Links().List()).Name);
    }

    [Fact]
    public async Task Add_Refusals()
    {
        Assert.Equal(BrowserLinkText.NotAWebAddress("news"), await Save(("action", "add"), ("url", "news")));
        Assert.Equal(BrowserLinkText.NotAWebAddress("ftp://example.com/"), await Save(("action", "add"), ("url", "ftp://example.com/")));
        Assert.Equal(BrowserLinkText.Refusal(BrowserLinkOutcome.Credentials), await Save(("action", "add"), ("url", "https://me:secret@example.com/")));
        Assert.Equal(BrowserLinkText.NotAnAction("bookmark"), await Save(("action", "bookmark"), ("url", "https://example.com/")));
        Assert.Empty(Links().List());
        Assert.Equal(0, _changed);
    }

    [Fact]
    public async Task Add_ANewerFile_IsRefused_AndLeftAlone()
    {
        Directory.CreateDirectory(_dir);
        string file = Path.Combine(_dir, BrowserLinks.FileName);
        File.WriteAllText(file, "{\"version\": 99, \"links\": []}");

        Assert.Equal(BrowserLinkText.Refusal(BrowserLinkOutcome.Newer), await Save(("action", "add"), ("url", "https://example.com/")));
        Assert.Equal("{\"version\": 99, \"links\": []}", File.ReadAllText(file));
    }

    [Fact]
    public async Task Current_IsTheWindowsPage_ItsTitleTheName()
    {
        _window.Current = ("https://example.com/docs", "The Docs");

        Assert.Equal(BrowserLinkText.Saved(new BrowserLink("https://example.com/docs", "The Docs", _time.GetUtcNow())), await Save(("action", "add"), ("url", "current")));
        Assert.Equal(BrowserLinkText.Renamed(Links().List()[0], "Docs"), await Save(("action", "rename"), ("url", "current"), ("name", "Docs")));
        Assert.Equal("Docs", Links().List()[0].Name);
        Assert.Equal(BrowserLinkText.Removed(Links().List()[0]), await Save(("action", "remove"), ("url", "CURRENT")));
        Assert.Empty(Links().List());
        Assert.Equal(3, _changed);
    }

    [Fact]
    public async Task Current_WithNoPage_ABotCheck_OrNoWindow_IsNothing()
    {
        Assert.Equal(BrowserLinkText.NothingCurrent, await Save(("action", "add"), ("url", "current")));

        _window.Current = ("https://example.com/check", "Just a moment...");
        _window.UnavailableReason = BrowserText.BotCheckWaiting;
        Assert.Equal(BrowserLinkText.NothingCurrent, await Save(("action", "add"), ("url", "current")));
        Assert.Equal(BrowserLinkText.NothingCurrent, await Save(("action", "remove"), ("url", "current")));

        Assert.Equal(BrowserLinkText.NothingCurrent, await Call(new WebSaveLinkTool(Headless()), ("action", "add"), ("url", "current")));
        Assert.Empty(Links().List());
    }

    [Fact]
    public async Task Current_ThatNeverAnswers_IsNothing_AfterTheWait()
    {
        _window.CurrentHold = true;
        var call = Save(("action", "add"), ("url", "current"));
        while (_window.CurrentAsks == 0)
        {
            await Task.Delay(10);
        }

        _time.Advance(BrowserLinks.CurrentPageWait + TimeSpan.FromSeconds(1));
        Assert.Equal(BrowserLinkText.NothingCurrent, await call.WaitAsync(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task RemoveAndRename_ByNumberOrAddress()
    {
        var links = Links();
        links.Add("https://a.example/", "A", out _);
        links.Add("https://b.example/", "B", out _);
        links.Add("https://c.example/", null, out _);

        var b = links.List()[1];
        Assert.Equal(BrowserLinkText.Renamed(b, "Bee"), await Save(("action", "rename"), ("url", "2"), ("name", "Bee")));
        Assert.Equal(BrowserLinkText.Renamed(b, null), await Save(("action", "rename"), ("url", "b.example"), ("name", " ")));
        Assert.Null(Links().List()[1].Name);

        var a = Links().List()[0];
        Assert.Equal(BrowserLinkText.Removed(a), await Save(("action", "remove"), ("url", "https://a.example/")));
        Assert.Equal(BrowserLinkText.Removed(Links().List()[1]), await Save(("action", "remove"), ("url", "2")));
        Assert.Equal("https://b.example/", Assert.Single(Links().List()).Url);

        Assert.Equal(BrowserLinkText.NotSavedForModel("7"), await Save(("action", "remove"), ("url", "7")));
        Assert.Equal(BrowserLinkText.NotSavedForModel("https://zz.example/"), await Save(("action", "rename"), ("url", "https://zz.example/"), ("name", "Z")));
        Assert.Equal(4, _changed);
    }

    [Fact]
    public async Task TheList_IsTheSameOne_TheBrowserCommandsUse()
    {
        await Save(("action", "add"), ("url", "https://example.com/"), ("name", "Example"));

        // /browser list's printed rows read the same file.
        var sink = new RecordingSink();
        ChatScreen.PrintBrowserLinks(Links().List(), sink);
        Assert.Contains(BrowserLinkText.PrintedRow(1, Links().List()[0]), sink.Notices);
    }

    private sealed class RecordingSink : NeonSidekick.UI.INoticeSink
    {
        public List<string> Notices { get; } = new();

        public void Notice(string text) => Notices.Add(text);

        public void Warning(string text) => Notices.Add(text);

        public void Error(string text) => Notices.Add(text);
    }
}
