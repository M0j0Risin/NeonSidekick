using System.Net;
using NeonSidekick.App;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;
using NeonSidekick.Viewer;
using NeonSidekick.Web;

namespace NeonSidekick.Tests;

/// <summary>A web tool's bot check on the screen (2026-10-13): the ask pane under each <c>Web browser botcheck window</c>, the waiting pane, and <c>/browser</c>.</summary>
public partial class ChatScreenTests
{
    /// <summary>The fake Browser window the chat screen gets; null (the default) is a screen with none.</summary>
    private FakeBrowserWindow? _browserWindow;

    private const string Gated = "<html><head><title>Just a moment...</title></head><body><div id=\"cf-chl-widget\">Checking your browser before accessing the site.</div></body></html>";

    /// <summary>
    /// A model reply that is <paramref name="calls"/> <c>web_fetch</c> calls of a site behind Cloudflare's check, then <paramref name="reply"/>;
    /// <paramref name="answer"/> pushed at the first pane's own wait (the ask pane, or the waiting pane under <c>automatic</c>).
    /// </summary>
    private void BotCheckFixture(ConsoleKeyInfo[] answer, string reply, int calls = 1, string mode = "ask", bool window = true)
    {
        _browserWindow = window ? _browserWindow ?? new FakeBrowserWindow() : null;
        _http.Map("https://example.com/", HttpStatusCode.Forbidden, Gated, "text/html");
        _settings.Update(d => { d.TtsOutput = false; d.WebTools = true; d.WebBrowserBotcheckWindow = mode; });
        _console.Profile.Height = 40;
        _geometry = new ScreenGeometry(() => null);
        for (int i = 1; i <= calls; i++)
        {
            _chat.Enqueue(FakeChatClient.Call("c" + i, WebFetchTool.ToolName, new Dictionary<string, object?> { [WebFetchTool.UrlArgument] = "https://example.com/" }));
        }

        _chat.EnqueueText(reply);
        var input = Scripted();
        StepsWhenIdle(Line("read it"), Line("/exit"));
        var idle = input.OnWait!;
        bool answered = false;
        input.OnWait = () =>
        {
            if (_keys is { PendingLine.IsCompleted: false })
            {
                if (!answered)
                {
                    answered = true;
                    input.Push(answer);
                }

                return;
            }

            idle();
        };
    }

    private List<string?> WebResults() => Results(_chat.Requests[^1]).Select(r => r.Result as string).ToList();

    [Fact]
    public async Task BotCheck_Ask_AllowOnce_OpensTheWindow_AndThePageIsTheAnswer()
    {
        BotCheckFixture([Keys.Char('o'), Keys.Enter], "Read.");

        string output = await RunAsync();

        Assert.Contains(BotCheckText.AskTitle, output);
        Assert.Contains("example.com wants a person to pass a bot check", output);
        var asked = Assert.Single(_browserWindow!.Solves);
        Assert.Equal(new Uri("https://example.com/"), asked.Url);
        Assert.Equal(BotCheckUse.Fetch, asked.Use);
        string result = Assert.Single(WebResults())!;
        Assert.StartsWith("Example Domain — https://example.com/ (chars 1–", result);
        Assert.Contains(", browser window)", result);
    }

    [Fact]
    public async Task BotCheck_Ask_Deny_IsTheModelsAnswer_AndTheTurnsNextCallIsNotAsked()
    {
        BotCheckFixture([Keys.Enter], "Understood.", calls: 2);

        await RunAsync();

        Assert.Empty(_browserWindow!.Solves);
        Assert.All(WebResults(), r => Assert.EndsWith("; the user declined to solve it in the Browser window)", r));
        Assert.Equal(2, WebResults().Count);
    }

    [Fact]
    public async Task BotCheck_Ask_AllowForTheSession_OpensTwiceWithoutAskingAgain()
    {
        _browserWindow = new FakeBrowserWindow { Answer = r => BotCheckResult.Ended(BotCheckEnd.Refused, r.Url.AbsoluteUri, status: 403) };
        BotCheckFixture([Keys.Char('s'), Keys.Enter], "Two.", calls: 2);

        await RunAsync();

        Assert.Equal(2, _browserWindow!.Solves.Count);
        Assert.All(WebResults(), r => Assert.EndsWith("; the Browser window got HTTP 403 too, with no check to solve)", r));
    }

    [Fact]
    public async Task BotCheck_Automatic_NoAsk_TheWaitingPane_AndEscGivesUp()
    {
        _browserWindow = new FakeBrowserWindow { Hold = true };
        BotCheckFixture([Keys.Escape], "Gave up.", mode: "automatic");

        string output = await RunAsync();

        Assert.DoesNotContain("wants a person to pass", output);
        Assert.Contains("Solve the check for example.com in the Browser window", output);
        Assert.True(_browserWindow!.CancelSeen);
        Assert.EndsWith("; the user gave up on it in the Browser window)", Assert.Single(WebResults()));
    }

    [Theory]
    [InlineData("disabled", true)]
    [InlineData("ask", false)]
    public async Task BotCheck_Disabled_OrNoWindow_LeavesTheRefusalAsItWas(string mode, bool window)
    {
        BotCheckFixture([], "No.", mode: mode, window: window);

        string output = await RunAsync();

        Assert.DoesNotContain("wants a person to pass", output);
        Assert.Equal("Error: 'https://example.com/' refused the request (HTTP 403 Forbidden; no headless browser was found to try instead)", Assert.Single(WebResults()));
        Assert.Empty(_browserWindow?.Solves ?? []);
    }

    [Fact]
    public async Task BrowserCommand_OpensThePage_AndItsCookiesGoToTheWebTools_AsItCloses()
    {
        _browserWindow = new FakeBrowserWindow
        {
            Visit = new BrowserVisit([new BrowserCookie("session", "s", "example.com", "/", -1, true, true)], ["example.com"], new BrowserIdentity(BrowserBrand.Edge, 155, BrowserPlatform.Windows)),
        };
        PushLine("/browser example.com/login");
        PushLine("/browser");
        PushLine("/browser not a url");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Equal([new Uri("https://example.com/login"), null], _browserWindow.Browses);
        Assert.Contains(BotCheckText.Opened("https://example.com/login"), output);
        Assert.Contains(BotCheckText.Opened(BrowserPage.Blank), output);
        Assert.Contains("The Browser window closed;", output);   // no jar in the fixture's web access: none taken
        Assert.Contains("'not a url' is not an http or https address.", output);
    }

    /// <summary>A bare <c>/browser</c> opens <c>Web browser home page</c> (later on 2026-10-13, the navigation bar).</summary>
    [Fact]
    public async Task BrowserCommand_Bare_OpensTheHomePage()
    {
        _browserWindow = new FakeBrowserWindow();
        _settings.Update(d => d.WebBrowserHomePage = "start.example");
        PushLine("/browser");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Equal([new Uri("https://start.example/")], _browserWindow.Browses);
        Assert.Contains(BotCheckText.Opened("https://start.example/"), output);
    }

    /// <summary>The Web tools page's open-browser button (later on 2026-10-13, the user's ask): O opens the window on the home page, the page staying.</summary>
    [Fact]
    public async Task WebToolsPage_OpenBrowser_OpensTheWindowOnTheHomePage_AndThePageStays()
    {
        _browserWindow = new FakeBrowserWindow();
        _settings.Update(d => d.TtsOutput = false);
        _console.Profile.Height = 40;
        _geometry = new ScreenGeometry(() => null);
        StepsWhenIdle(
            Line("/tools web"),
            input => input.Push(Keys.Char('o')),
            input => input.Push(Keys.Escape),
            Line("/exit"));

        string output = await RunAsync();

        Assert.Equal([null], _browserWindow.Browses);
        Assert.Contains(SettingsMenu.OpenBrowserButton, output);
        Assert.Contains(SettingsMenu.WebToggleOpenKeys, output);
        Assert.Contains(SettingsMenu.OpenedBrowserNotice, output);
        Assert.Contains(BotCheckText.Opened(BrowserPage.Blank), output);
        Assert.Equal("auto", _settings.Current.WebBrowserMode);
    }

    /// <summary><c>/browser clear history</c> (later on 2026-10-13): no question, the window's clear, the notice.</summary>
    [Fact]
    public async Task BrowserClear_History_ClearsWithNoQuestion()
    {
        _browserWindow = new FakeBrowserWindow();
        PushLine("/browser clear history");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Equal([BrowserClear.History], _browserWindow.Clears);
        Assert.Contains(BotCheckText.ClearedHistory, output);
        Assert.DoesNotContain(BotCheckText.ClearAllQuestion, output);
    }

    /// <summary><c>/browser clear all</c> asks first at the idle line: a yes clears everything, a no keeps it.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BrowserClear_All_AsksFirst(bool yes)
    {
        _browserWindow = new FakeBrowserWindow();
        _settings.Update(d => d.TtsOutput = false);
        _console.Profile.Height = 40;
        _geometry = new ScreenGeometry(() => null);
        StepsWhenIdle(
            Line("/browser clear all"),
            input => input.Push(yes ? [Keys.Down, Keys.Enter] : [Keys.Enter]),   // the cursor starts on No
            Line("/exit"));

        string output = await RunAsync();

        Assert.Contains(BotCheckText.ClearAllQuestion, output);
        if (yes)
        {
            Assert.Equal([BrowserClear.All], _browserWindow.Clears);
            Assert.Contains(BotCheckText.ClearedAll, output);
        }
        else
        {
            Assert.Empty(_browserWindow.Clears);
            Assert.Contains(ChatScreen.KeptNotice, output);
        }
    }

    /// <summary>A bare or wrong word after clear is the usage; a failed clear says why.</summary>
    [Fact]
    public async Task BrowserClear_Usage_AndAFailure()
    {
        _browserWindow = new FakeBrowserWindow { ClearAnswer = BrowserText.ClearDuringCheck };
        PushLine("/browser clear");
        PushLine("/browser clear bogus");
        PushLine("/browser clear history");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Equal(2, output.Split(BotCheckText.ClearUsage).Length - 1);
        Assert.Contains(BotCheckText.ClearFailed(BrowserText.ClearDuringCheck), output);
        Assert.Equal([BrowserClear.History], _browserWindow.Clears);
        Assert.Equal("Usage: /browser clear history|all — history clears the pages the Browser window visited; all also clears its cookies and sign-ins", BotCheckText.ClearUsage);
        Assert.Equal("The Browser window was not cleared: x.", BotCheckText.ClearFailed("x"));
    }

    /// <summary>A profile switch closes the Browser window, its data being each profile's own (later on 2026-10-13).</summary>
    [Fact]
    public async Task ProfileSwitch_ClosesTheBrowserWindow()
    {
        _browserWindow = new FakeBrowserWindow();
        _settings.Update(d => d.TtsOutput = false);
        Profiles.Create(_dir, "work", AppSettings.Copy(_settings.Current));
        PushLine("/profile work");
        PushLine("/exit");

        await RunAsync();

        Assert.True(_browserWindow.Closes >= 1);
    }

    [Fact]
    public async Task BrowserCommand_WithNoWindow_SaysThereIsNone()
    {
        PushLine("/browser https://example.com/");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(BotCheckText.BrowserNotHere, output);
    }
}
