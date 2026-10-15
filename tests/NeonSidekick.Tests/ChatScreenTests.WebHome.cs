using NeonSidekick.App;
using NeonSidekick.HomeAssistant;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.Viewer;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// The Web UI's Home tab on the screen (2026-10-15, the plan's Part D) over the scripted Home Assistant: a read holds the tiles' states with the
/// bar hidden and lets them go after the lease; a tap switches a light and marks the PC's chip; an unlock asks first; nothing acts with
/// <i>Web UI home controls</i> off or off the bar's reach; the video remote and the meters' reads.
/// </summary>
public partial class ChatScreenTests
{
    private FakeHaServer HomeTab(bool controls = true)
    {
        var server = HomeServer();
        _settings.Update(d =>
        {
            d.HomeBarItems = [];   // the bar hidden: the tab shows its last items
            d.HomeBarLastItems = ["entity:light.desk", "entity:lock.front"];
            d.WebUiHomeControls = controls;
        });
        return server;
    }

    private IWebHome HomeSide => Web.Home!;

    [Fact]
    public async Task HomeTab_HoldsItsTilesStates_WithTheBarHidden_AndLetsThemGoAfterTheLease()
    {
        var server = HomeTab();
        bool held = false;
        bool readMeters = false;
        bool letGo = false;
        bool metersGone = false;
        var input = WhenIdle(
            i =>
            {
                HomeSide.Read();
                TickUntil(() => server.Subscribed.Contains("light.desk"));
                held = server.Subscribed.Contains("light.desk");
                TickUntil(() => _perfSource.LastReads == NeonSidekick.Perf.PerfReads.All);
                readMeters = _perfSource.LastReads == NeonSidekick.Perf.PerfReads.All;   // the card's meters, the perf bar off
                _time.Advance(ChatScreen.HomeWebLease + TimeSpan.FromSeconds(1));
                TickUntil(() => !server.Open);
                letGo = !server.Open;   // the feed let go, its linger over
                metersGone = _perfSource.Disposed;
                PushLine(i, "/exit");
            });

        await RunAsync(input);

        Assert.True(held);
        Assert.True(readMeters);
        Assert.True(letGo);
        Assert.True(metersGone);
    }

    [Fact]
    public async Task HomeTab_ATapSwitchesTheLight_AndAnUnlockAsksFirst()
    {
        var server = HomeTab();
        server.HoldStates = true;   // the calls answered, no state moved: the pending mark stays to be read
        WebReply unlockFirst = default;
        WebReply unlocked = default;
        WebHome? after = null;
        var input = WhenIdle(
            i =>
            {
                HomeSide.Read();
                TickUntil(() => server.Subscribed.Contains("lock.front") && HomeSide.Read().Tiles.All(t => t.Tone != "stale"));
                Assert.Equal(WebOutcome.Ok, HomeSide.Tap(new WebHomeTapRequest { Ref = "entity:light.desk" }, null).Outcome);
                after = HomeSide.Read();
                unlockFirst = HomeSide.Tap(new WebHomeTapRequest { Ref = "entity:lock.front" }, null);
                unlocked = HomeSide.Tap(new WebHomeTapRequest { Ref = "entity:lock.front", Confirmed = true }, null);
                TickUntil(() => server.Calls.Count == 2);
                PushLine(i, "/exit");
            });

        await RunAsync(input);

        Assert.Equal("pending", after!.Tiles.First(t => t.Ref == "entity:light.desk").Mark);   // the PC's chip marked as well
        Assert.Equal(WebOutcome.Conflict, unlockFirst.Outcome);
        Assert.Equal(HomeBarText.Confirm(HaServiceCall.On("lock", "unlock", "lock.front"), "Front Door"), unlockFirst.Prompt);
        Assert.Equal(WebOutcome.Ok, unlocked.Outcome);
        Assert.Equal(["light.toggle", "lock.unlock"], server.Calls.Select(c => c.Service));
    }

    [Fact]
    public async Task HomeTab_WithControlsOff_RefusesEveryAction_AndOffTheBarNothingIsReached()
    {
        var server = HomeTab(controls: false);
        var replies = new List<WebReply>();
        WebHomeSheet? outside = null;
        WebReply offBar = default;
        var input = WhenIdle(
            i =>
            {
                replies.Add(HomeSide.Tap(new WebHomeTapRequest { Ref = "entity:light.desk" }, null));
                replies.Add(HomeSide.Act(new WebHomeActRequest { Ref = "entity:light.desk", Entity = "light.desk", Index = 0, Label = "x" }, null));
                replies.Add(HomeSide.Scene(new WebHomeSceneRequest { Id = "scene.any" }, null));
                replies.Add(HomeSide.Video(new WebHomeVideoRequest { Action = "play" }, null));
                replies.Add(HomeSide.SayAsync(new WebHomeSayRequest { Text = "hi" }, null, CancellationToken.None).GetAwaiter().GetResult());
                _settings.Update(d => d.WebUiHomeControls = true);
                outside = HomeSide.Sheet("entity:light.kitchen");
                offBar = HomeSide.Act(new WebHomeActRequest { Ref = "entity:lock.front", Entity = "light.kitchen", Index = 0, Label = "x" }, null);
                PushLine(i, "/exit");
            });

        await RunAsync(input);

        Assert.All(replies, r => Assert.Equal((WebOutcome.Forbidden, WebUiText.HomeControlsOff), (r.Outcome, r.Message)));
        Assert.Null(outside);
        Assert.Equal(WebOutcome.NotFound, offBar.Outcome);
        Assert.Empty(server.Calls);
    }

    [Fact]
    public async Task HomeTab_TheVideoRemote_TellsThePcsWindow()
    {
        HomeTab();
        var player = new FakeVideoPlayer();
        _videoPlayer = player;
        WebReply none = default;
        var input = WhenIdle(
            i =>
            {
                none = HomeSide.Video(new WebHomeVideoRequest { Action = "play" }, null);
                player.Play(new VideoRequest("aqz-KE-bpKQ"));
                Assert.NotNull(HomeSide.Read().Video);
                HomeSide.Video(new WebHomeVideoRequest { Action = "pause" }, null);
                HomeSide.Video(new WebHomeVideoRequest { Action = "louder" }, null);
                HomeSide.Video(new WebHomeVideoRequest { Action = "mute" }, null);
                PushLine(i, "/exit");
            });

        await RunAsync(input);

        Assert.Equal(WebOutcome.Conflict, none.Outcome);
        Assert.Equal([VideoCommandKind.Pause, VideoCommandKind.Volume, VideoCommandKind.Mute], player.Commands.Select(c => c.Kind));
    }
}
