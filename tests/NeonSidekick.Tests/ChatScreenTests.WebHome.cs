using NeonSidekick.App;
using NeonSidekick.HomeAssistant;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.Viewer;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// The Web UI's Home tab on the screen (2026-10-15, the plan's Part D) over the scripted Home Assistant: a read holds the tiles' states with the
/// bar hidden and lets them go after the lease; a tap switches a light and marks the PC's chip; an unlock asks first; nothing acts with
/// <i>Web UI home controls</i> off or off the bar's and the rooms' reach; the rooms' switches and rows (2026-10-16); the video remote and the
/// meters' reads.
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
                outside = HomeSide.Sheet("entity:light.nowhere");   // in no room and not on the bar
                offBar = HomeSide.Act(new WebHomeActRequest { Ref = "entity:lock.front", Entity = "light.nowhere", Index = 0, Label = "x" }, null);
                PushLine(i, "/exit");
            });

        await RunAsync(input);

        Assert.All(replies, r => Assert.Equal((WebOutcome.Forbidden, WebUiText.HomeControlsOff), (r.Outcome, r.Message)));
        Assert.Null(outside);
        Assert.Equal(WebOutcome.NotFound, offBar.Outcome);
        Assert.Empty(server.Calls);
    }

    /// <summary>
    /// The rooms (2026-10-16, the user's ask): a room off the bar has a box; its header's tap (the bar's own area chip) switches its lights on by
    /// <c>area_id</c>, a zone's header toggles its group light, a row's switch toggles an off-bar light and marks the row; a lock's row asks first;
    /// an entity in no room is still out of reach.
    /// </summary>
    [Fact]
    public async Task HomeTab_TheRooms_SwitchARoom_AZone_AndARow_TheLockAsksFirst()
    {
        var server = HomeTab();
        server.Area("kitchen", "Kitchen")
            .Entity("light.kitchen", "off", """{"friendly_name":"Kitchen"}""", area: "kitchen")
            .Entity("lock.back", "locked", """{"friendly_name":"Back Door"}""", area: "kitchen")
            .Device("hue-zone", "Porch zone")
            .Entity("light.porch_zone", "off", """{"friendly_name":"Porch zone","is_hue_group":true,"entity_id":["light.desk"]}""", device: "hue-zone")
            .Entity("light.nowhere", "off");
        server.HoldStates = true;   // the calls answered, no state moved: the row's pending mark stays to be read
        IReadOnlyList<WebHomeRoom> rooms = [];
        WebReply room = default;
        WebReply zone = default;
        WebReply row = default;
        WebReply unlockFirst = default;
        WebReply nowhere = default;
        WebHome? after = null;
        var input = WhenIdle(
            i =>
            {
                HomeSide.Read();
                // Two reads at least: a zone's group light is wanted first, and the read after the feed has its attributes finds the zone.
                TickUntil(() => HomeSide.Read().Rooms is var now
                    && now.FirstOrDefault(r => r.Key == "kitchen") is { Ref: not null, Devices: [{ Control: "switch" }, ..] }
                    && now.Any(r => r.Key == "device:hue-zone"));
                rooms = HomeSide.Read().Rooms;
                room = HomeSide.Tap(new WebHomeTapRequest { Ref = "area:kitchen" }, null);
                zone = HomeSide.Tap(new WebHomeTapRequest { Ref = "entity:light.porch_zone" }, null);
                row = HomeSide.Act(new WebHomeActRequest { Ref = "entity:light.kitchen", Entity = "light.kitchen", Index = 0, Label = HomeBarText.ToggleAction }, null);
                after = HomeSide.Read();
                unlockFirst = HomeSide.Act(new WebHomeActRequest { Ref = "entity:lock.back", Entity = "lock.back", Index = 0, Label = HomeBarText.UnlockAction }, null);
                nowhere = HomeSide.Act(new WebHomeActRequest { Ref = "entity:light.nowhere", Entity = "light.nowhere", Index = 0, Label = HomeBarText.ToggleAction }, null);
                TickUntil(() => server.Calls.Count == 3);
                PushLine(i, "/exit");
            });

        await RunAsync(input);

        Assert.Contains(rooms, r => r.Key == "kitchen" && r.Ref == "area:kitchen" && r.Value == WebUiText.RoomLightsOn(0, 1));
        Assert.Contains(rooms, r => r.Key == "device:hue-zone" && r.Ref == "entity:light.porch_zone");
        Assert.Equal(WebOutcome.Ok, room.Outcome);
        Assert.Equal(WebOutcome.Ok, zone.Outcome);
        Assert.Equal(WebOutcome.Ok, row.Outcome);
        Assert.Equal("pending", after!.Rooms.Single(r => r.Key == "kitchen").Devices.Single(d => d.Entity == "light.kitchen").Mark);
        Assert.Equal(WebOutcome.Conflict, unlockFirst.Outcome);
        Assert.Equal(HomeBarText.Confirm(HaServiceCall.On("lock", "unlock", "lock.back"), "Back Door"), unlockFirst.Prompt);
        Assert.Equal(WebOutcome.NotFound, nowhere.Outcome);
        Assert.Equal(["light.turn_on", "light.toggle", "light.toggle"], server.Calls.Select(c => c.Service));
        Assert.Contains("kitchen", server.Calls[0].Target, StringComparison.Ordinal);       // by area_id
        Assert.Contains("light.porch_zone", server.Calls[1].Target, StringComparison.Ordinal);
        Assert.Contains("light.kitchen", server.Calls[2].Target, StringComparison.Ordinal);
    }

    /// <summary>The rooms' controls with <i>Web UI home controls</i> off (2026-10-16): a room's switch and a row's act refused like every action.</summary>
    [Fact]
    public async Task HomeTab_TheRooms_WithControlsOff_AreRefused()
    {
        var server = HomeTab(controls: false);
        WebReply room = default;
        WebReply row = default;
        var input = WhenIdle(
            i =>
            {
                room = HomeSide.Tap(new WebHomeTapRequest { Ref = "area:den" }, null);
                row = HomeSide.Act(new WebHomeActRequest { Ref = "entity:light.desk", Entity = "light.desk", Index = 0, Label = HomeBarText.ToggleAction }, null);
                PushLine(i, "/exit");
            });

        await RunAsync(input);

        Assert.Equal(WebOutcome.Forbidden, room.Outcome);
        Assert.Equal(WebOutcome.Forbidden, row.Outcome);
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
