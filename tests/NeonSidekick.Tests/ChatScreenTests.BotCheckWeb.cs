using System.Net;
using NeonSidekick.App;
using NeonSidekick.Camera;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;
using NeonSidekick.Viewer;
using NeonSidekick.Web;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// A bot check answered and driven from the Web UI (2026-10-17, the user's ask: one met from the phone waited at the PC until they were home):
/// the allow ask and the wait as cards on the page beside the PC's panes, the live card's picture and input reaching the window only while the
/// wait runs, and <i>Web UI bot check control</i>.
/// </summary>
public partial class ChatScreenTests
{
    /// <summary>
    /// <paramref name="act"/> run off the turn task for each ask the page gets of <paramref name="kind"/>, once each, as a page would; what it
    /// throws is kept for the test to rethrow. The hook goes on at the model's first request, once the screen's session exists.
    /// </summary>
    private List<Exception> OnThePage(string kind, Func<WebUiSession, WebAskHandle, Task> act)
    {
        var failures = new List<Exception>();
        var seen = new HashSet<string>();
        bool hooked = false;
        _chat.BeforeUpdate = (_, _) =>
        {
            if (!hooked)
            {
                hooked = true;
                var web = Web;
                web.Asks.Changed += () =>
                {
                    foreach (var ask in web.Asks.OpenAsks())
                    {
                        if (ask.Spec.Kind == kind && ask.Winner is null && seen.Add(ask.Id))
                        {
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    await act(web, ask);
                                }
                                catch (Exception e)
                                {
                                    lock (failures)
                                    {
                                        failures.Add(e);
                                    }
                                }
                            });
                        }
                    }
                };
            }

            return Task.CompletedTask;
        };
        return failures;
    }

    private static Task Answered(WebUiSession web, WebAskHandle ask, string choice)
    {
        Assert.Equal(WebOutcome.Ok, web.Answer(new WebAnswerRequest { AskId = ask.Id, Choice = choice }, IPAddress.Loopback).Outcome);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task BotCheck_AllowedFromThePage_OpensTheWindow_AndThePcSaysWhoAnswered()
    {
        var failures = OnThePage("allow", (web, ask) => Answered(web, ask, "once"));
        BotCheckFixture([], "Read.");

        string output = await RunAsync();

        Assert.Empty(failures);
        Assert.Single(_browserWindow!.Solves);
        Assert.Contains(", browser window)", Assert.Single(WebResults()));
        Assert.Contains(WebUiText.AnsweredNotice(CameraText.AllowOnceRow), output);
        Assert.Empty(Web.Asks.OpenAsks());
    }

    [Fact]
    public async Task BotCheck_DeniedFromThePage_IsDeclined_AndTheTurnsNextCallIsNotAsked()
    {
        int asked = 0;
        var failures = OnThePage("allow", (web, ask) =>
        {
            Interlocked.Increment(ref asked);
            return Answered(web, ask, "deny");
        });
        BotCheckFixture([], "Understood.", calls: 2);

        await RunAsync();

        Assert.Empty(failures);
        Assert.Equal(1, asked);
        Assert.Empty(_browserWindow!.Solves);
        Assert.All(WebResults(), r => Assert.EndsWith("; the user declined to solve it in the Browser window)", r));
    }

    [Fact]
    public async Task BotCheck_GivenUpFromThePage_CancelsTheWindow_AndClosesBothSides()
    {
        _browserWindow = new FakeBrowserWindow { Hold = true };
        WebAskSpec? card = null;
        var failures = OnThePage("botcheck", (web, ask) =>
        {
            card = ask.Spec;
            return Answered(web, ask, ChatScreen.BotCheckGiveUpChoice);
        });
        BotCheckFixture([], "Gave up.", mode: "automatic");

        string output = await RunAsync();

        Assert.Empty(failures);
        Assert.True(card!.Live);
        Assert.Equal(BotCheckText.WaitTitle, card.Title);
        Assert.True(_browserWindow!.CancelSeen);
        Assert.EndsWith("; the user gave up on it in the Browser window)", Assert.Single(WebResults()));
        Assert.Contains(WebUiText.AnsweredNotice(BotCheckText.GiveUpRow), output);
        Assert.Empty(Web.Asks.OpenAsks());
    }

    [Fact]
    public async Task BotCheck_TheLiveCard_SeesAndDrivesTheWindow_OnlyWhileTheWaitRuns()
    {
        _browserWindow = new FakeBrowserWindow { Hold = true };
        string? cardId = null;
        var frames = new List<(WebOutcome, WebBotCheckFrame?)>();
        var replies = new List<WebReply>();
        var failures = OnThePage("botcheck", async (web, ask) =>
        {
            cardId = ask.Id;
            frames.Add(await web.BotCheckFrameAsync(ask.Id, 640, CancellationToken.None));
            frames.Add(await web.BotCheckFrameAsync(ask.Id, 640, CancellationToken.None));   // the same moment: the picture taken once
            _time.Advance(ChatScreen.BotCheckFrameReuse);
            frames.Add(await web.BotCheckFrameAsync(ask.Id, 640, CancellationToken.None));   // a new one
            frames.Add(await web.BotCheckFrameAsync("a-stale-id", 640, CancellationToken.None));
            replies.Add(await web.BotCheckInputAsync(new WebBotCheckInput { AskId = ask.Id, Kind = "tap", X = 0.25, Y = 0.5 }, IPAddress.Loopback, CancellationToken.None));
            replies.Add(await web.BotCheckInputAsync(new WebBotCheckInput { AskId = ask.Id, Kind = "text", Text = "Main St" }, IPAddress.Loopback, CancellationToken.None));
            replies.Add(await web.BotCheckInputAsync(new WebBotCheckInput { AskId = ask.Id, Kind = "key", Key = "Escape" }, IPAddress.Loopback, CancellationToken.None));
            replies.Add(await web.BotCheckInputAsync(new WebBotCheckInput { AskId = ask.Id, Kind = "swipe" }, IPAddress.Loopback, CancellationToken.None));
            replies.Add(await web.BotCheckInputAsync(new WebBotCheckInput { AskId = "a-stale-id", Kind = "tap", X = 0.5, Y = 0.5 }, IPAddress.Loopback, CancellationToken.None));
            _browserWindow.EndHeld();   // the page got through: the card closes on its own
        });
        BotCheckFixture([], "Read.", mode: "automatic");

        string output = await RunAsync();

        Assert.Empty(failures);
        Assert.Equal([WebOutcome.Ok, WebOutcome.Ok, WebOutcome.Ok, WebOutcome.NotFound], frames.Select(f => f.Item1));
        Assert.Equal(FakeBrowserWindow.TinyJpeg, frames[0].Item2!.Jpeg);
        Assert.Equal(2, _browserWindow!.FrameAsks);
        Assert.Equal([WebOutcome.Ok, WebOutcome.Ok, WebOutcome.Refused, WebOutcome.Refused, WebOutcome.NotFound], replies.Select(r => r.Outcome));
        Assert.Equal(BrowserText.RemoteNoSuchKey, replies[2].Message);
        Assert.Equal([new BrowserInput(BrowserInputKind.Tap, 0.25, 0.5), new BrowserInput(BrowserInputKind.Text, Text: "Main St")], _browserWindow.Inputs);
        Assert.Contains(", browser window)", Assert.Single(WebResults()));
        Assert.Empty(Web.Asks.OpenAsks());
        Assert.DoesNotContain(WebUiText.AnsweredNotice(BotCheckText.GiveUpRow), output);

        // Over: the card's id reaches nothing now.
        Assert.Equal(WebOutcome.NotFound, (await Web.BotCheckFrameAsync(cardId!, 640, CancellationToken.None)).Outcome);
        Assert.Equal(WebOutcome.NotFound, (await Web.BotCheckInputAsync(new WebBotCheckInput { AskId = cardId, Kind = "tap" }, null, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task BotCheck_WithTheControlOff_TheCardHasNoLiveView_AndThePageCanNotReachTheWindow()
    {
        _browserWindow = new FakeBrowserWindow { Hold = true };
        WebAskSpec? card = null;
        (WebOutcome, WebBotCheckFrame?) frame = default;
        WebReply input = default;
        var failures = OnThePage("botcheck", async (web, ask) =>
        {
            card = ask.Spec;
            frame = await web.BotCheckFrameAsync(ask.Id, 640, CancellationToken.None);
            input = await web.BotCheckInputAsync(new WebBotCheckInput { AskId = ask.Id, Kind = "tap", X = 0.5, Y = 0.5 }, IPAddress.Loopback, CancellationToken.None);
            await Answered(web, ask, ChatScreen.BotCheckGiveUpChoice);
        });
        BotCheckFixture([], "Gave up.", mode: "automatic");
        _settings.Update(d => d.WebUiBotCheckControl = false);

        await RunAsync();

        Assert.Empty(failures);
        Assert.False(card!.Live);
        Assert.Equal((WebOutcome.Forbidden, null), frame);
        Assert.Equal(WebOutcome.Forbidden, input.Outcome);
        Assert.Equal(WebUiText.BotCheckControlOff, input.Message);
        Assert.Equal(0, _browserWindow!.FrameAsks);
        Assert.Empty(_browserWindow.Inputs);
        Assert.True(_browserWindow.CancelSeen);
    }

    [Fact]
    public async Task BotCheck_ThePagesStop_DuringTheWait_EndsTheCheck_AndTheCard()
    {
        _browserWindow = new FakeBrowserWindow { Hold = true };
        var failures = OnThePage("botcheck", (web, _) =>
        {
            web.Stop(IPAddress.Loopback);
            return Task.CompletedTask;
        });
        BotCheckFixture([], "Never.", mode: "automatic");

        await RunAsync();

        Assert.Empty(failures);
        Assert.True(_browserWindow!.CancelSeen);
        Assert.Empty(Web.Asks.OpenAsks());
    }

    [Fact]
    public async Task BotCheck_EscAtThePc_GivesUp_AndClosesThePagesCard()
    {
        _browserWindow = new FakeBrowserWindow { Hold = true };
        int cards = 0;
        var failures = OnThePage("botcheck", (_, _) =>
        {
            Interlocked.Increment(ref cards);
            return Task.CompletedTask;
        });
        BotCheckFixture([Keys.Escape], "Gave up.", mode: "automatic");

        string output = await RunAsync();

        Assert.Empty(failures);
        Assert.Equal(1, cards);
        Assert.True(_browserWindow!.CancelSeen);
        Assert.EndsWith("; the user gave up on it in the Browser window)", Assert.Single(WebResults()));
        Assert.DoesNotContain(WebUiText.AnsweredNotice(BotCheckText.GiveUpRow), output);
        Assert.Empty(Web.Asks.OpenAsks());
    }

    [Theory]
    [InlineData("tap", BrowserInputKind.Tap)]
    [InlineData("down", BrowserInputKind.Down)]
    [InlineData("up", BrowserInputKind.Up)]
    [InlineData("scroll", BrowserInputKind.Scroll)]
    [InlineData("text", BrowserInputKind.Text)]
    [InlineData("key", BrowserInputKind.Key)]
    public void BotCheckInputOf_ReadsEachKind_AndRefusesAnUnknownOne(string kind, BrowserInputKind expected)
    {
        var input = ChatScreen.BotCheckInputOf(new WebBotCheckInput { Kind = kind, X = 0.1, Y = 0.2, DeltaY = -30, Text = "t", Key = "Tab" });
        Assert.Equal(new BrowserInput(expected, 0.1, 0.2, -30, "t", "Tab"), input);
        Assert.Null(ChatScreen.BotCheckInputOf(new WebBotCheckInput { Kind = "Tap" }));
        Assert.Null(ChatScreen.BotCheckInputOf(new WebBotCheckInput()));
    }
}
