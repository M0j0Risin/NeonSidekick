using System.Net;
using System.Text.Json;
using NeonSidekick.App;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// Later on 2026-10-15 on the screen: <c>/loop</c> from the page (the user's ask: it was never the page's), and the PC's picture strip in the page's
/// drawer — an imagined picture reaching it, the page's pick moving the PC's highlight and viewer (the picture already highlighted too), the
/// setting off leaving no strip, the PC's own step reaching the page, and each tab only while its controls are on.
/// </summary>
public partial class ChatScreenTests
{
    private static List<WebStrip?> StripEvents(WebEventHub.Subscription subscription)
    {
        var strips = new List<WebStrip?>();
        while (subscription.Reader.TryRead(out var item))
        {
            if (item.Name == "strip")
            {
                strips.Add(JsonSerializer.Deserialize(item.Json.Value, WebUiJsonContext.Default.WebStrip));
            }
        }

        return strips;
    }

    private static void WaitUntil(Func<bool> done)
    {
        Assert.True(SpinWait.SpinUntil(done, TimeSpan.FromSeconds(10)), "waited 10 s");
    }

    [Fact]
    public async Task ALoopFromThePage_SendsItsTurns_AndEndsWithThePcsDoneNotice()
    {
        _settings.Update(d => { d.TtsOutput = false; d.WebUiCommands = true; });
        _chat.EnqueueText("One.");
        _chat.EnqueueText("Two.");
        var input = WebSteps(_ => SendFromPage("/loop 2 hi"), i => PushLine(i, "/exit"));

        await RunAsync(input);

        Assert.Equal(2, _chat.Requests.Count);
        Assert.Equal("hi", LastUser(_chat.Requests[1]));
        Assert.Contains(Web.Feed.Snapshot(m => m), e => e.Text.Contains(ChatScreen.LoopDoneNotice(2), StringComparison.Ordinal));
        Assert.Empty(_pcOnlyPanes);
    }

    [Fact]
    public async Task ALoopFromThePage_StoppedBeforeItsFirstWord_GoesBackToThePagesDraft()
    {
        _settings.Update(d => { d.TtsOutput = false; d.WebUiCommands = true; });
        _chat.EnqueueText("never shown");
        WebEventHub.Subscription? subscription = null;
        _chat.BeforeUpdate = async (i, ct) =>
        {
            if (i == 0)
            {
                Assert.Equal(WebOutcome.Ok, Web.Stop(IPAddress.Loopback).Outcome);
                await Task.Delay(TimeSpan.FromSeconds(30), ct);   // the Stop ends it long before
            }
        };
        var input = WebSteps(
            _ =>
            {
                subscription = Web.Events.Subscribe();
                SendFromPage("/loop 2 hi");
            },
            i => PushLine(i, "/exit"));

        await RunAsync(input);

        Assert.Equal([("withdrawn", "/loop 2 hi")], TextBacks(subscription!));
        Assert.Single(_chat.Requests);   // the second pass never ran
        Assert.Contains("/loop 2 hi", _running!.InputHistoryForTests);   // in the history as sent (2026-10-16), withdrawn or not, as a PC line
        subscription!.Dispose();
    }

    [Fact]
    public async Task AnImaginedPicture_ReachesThePagesStrip_AndThePagesPick_MovesThePcsViewer_EvenOnTheHighlightedOne()
    {
        ComfyServer();
        PaneOf40Rows();
        var followed = new List<string>();
        _followViewer = path => { lock (followed) { followed.Add(path); } };
        int Followed() { lock (followed) { return followed.Count; } }
        WebStrip? strip = null;
        WebEventHub.Subscription? subscription = null;
        // Every step pushes a key: the script's next step comes at the read's next wait.
        StepsWhenIdle(
            input =>
            {
                subscription = Web.Events.Subscribe();
                PushLine(input, "/imagine a cat --seed 5");
            },
            input =>
            {
                strip = Web.Snapshot().Strip;
                Assert.Equal(WebOutcome.Ok, Web.SelectStripPicture(new WebStripSelect { Id = strip!.Pictures[0].Id }, IPAddress.Loopback).Outcome);
                WaitUntil(() => Followed() == 1);
                // The tile already highlighted: the PC's click would move nothing, the page's moves a viewer opened since.
                Assert.Equal(WebOutcome.Ok, Web.SelectStripPicture(new WebStripSelect { Id = strip.Pictures[0].Id }, IPAddress.Loopback).Outcome);
                WaitUntil(() => Followed() == 2);
                PushLine(input, "/exit");
            });

        await RunAsync();

        var picture = Assert.Single(strip!.Pictures);
        Assert.Equal("pony-5.png", picture.Name);
        Assert.NotNull(Web.Picture(picture.Id));   // served while the strip holds it
        Assert.Equal([ComfyPicture("pony-5.png"), ComfyPicture("pony-5.png")], followed);
        var events = StripEvents(subscription!);
        Assert.Contains(events, e => e is { Pictures.Count: 1 });
        Assert.Equal(picture.Id, Web.Snapshot().Strip!.Selected);
        Assert.Equal(WebOutcome.NotFound, Web.SelectStripPicture(new WebStripSelect { Id = picture.Id + 1000 }, null).Outcome);
        subscription!.Dispose();
    }

    [Fact]
    public async Task WithTheStripSettingOff_ThePageHasNoStrip_AndAPickIsNotFound()
    {
        ComfyServer();
        PaneOf40Rows();
        var followed = new List<string>();
        _followViewer = followed.Add;
        StepsWhenIdle(Line("/imagine a cat --seed 5"), input =>
        {
            _settings.Update(d => d.ComfyPictureStrip = false);
            PushLine(input, "/exit");
        });

        await RunAsync();

        Assert.Null(Web.Snapshot().Strip);
        Assert.Equal(WebOutcome.NotFound, Web.SelectStripPicture(new WebStripSelect { Id = 0 }, null).Outcome);
        Assert.Empty(followed);
    }

    [Fact]
    public async Task ThePcsOwnStep_SendsTheStrip_WithItsNewHighlight()
    {
        ComfyServer();
        PaneOf40Rows();
        WebEventHub.Subscription? subscription = null;
        StepsWhenIdle(Line("/imagine a cat --seed 5"), input =>
        {
            subscription = Web.Events.Subscribe();
            input.Push(Keys.Right);
        }, Line("/exit"));

        await RunAsync();

        var strip = Assert.Single(StripEvents(subscription!), s => s?.Selected is not null);
        Assert.Equal(strip!.Pictures[0].Id, strip.Selected);
        subscription!.Dispose();
    }

    [Fact]
    public async Task TheTabs_FollowTheirSettings_InTheStateThePulseAndPerRequest()
    {
        _settings.Update(d => { d.TtsOutput = false; d.WebUiChatControls = false; d.WebUiHomeControls = true; d.WebUiShoppingControls = false; });
        PushLine("/exit");

        await RunAsync();

        var state = Web.Snapshot().State;
        Assert.Equal((false, true, false), (state.ChatTab, state.HomeTab, state.ShoppingTab));
        Assert.Equal((false, true, false), (Web.Pulse().ChatTab, Web.Pulse().HomeTab, Web.Pulse().ShoppingTab));
        Assert.Equal(new WebTabs(false, true, false), Web.Tabs());
        _settings.Update(d => d.WebUiShoppingControls = true);
        Assert.True(Web.Tabs().Shopping);   // read at each request
    }
}
