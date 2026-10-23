using System.Text.Json;
using NeonSidekick.App;
using NeonSidekick.HomeAssistant;
using NeonSidekick.Perf;
using NeonSidekick.Settings;
using NeonSidekick.Viewer;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// The Web UI's Home tab, its pure parts (2026-10-15): the items it shows, the tiles over the bar's own chips, a chip's tone, the tiles' reach,
/// Home Assistant's state in a word and a sentence, the rooms (the scenes' boxes until 2026-10-16), and the cards' figures.
/// </summary>
public sealed class WebHomeViewTests
{
    private static HaLiveEntity Entity(string id, string state, string attributes = "{}")
    {
        using var document = JsonDocument.Parse(attributes);
        var map = document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
        return new HaLiveEntity(id, state, map, null);
    }

    private static HaCatalog Catalog() => new(
        [],
        [new HaAreaEntry("den", "Den", null), new HaAreaEntry("hall", "Hall", null)],
        [],
        [
            new HaRegistryEntity("light.den_lamp", null, "den", null, false, false, null, []),
            new HaRegistryEntity("fan.den_fan", null, "den", null, false, false, null, []),
            new HaRegistryEntity("lock.front", null, "hall", null, false, false, null, []),
            new HaRegistryEntity("lock.back", null, "hall", null, false, false, null, []),
            new HaRegistryEntity("scene.movie", null, "den", "Movie", false, false, null, []),
            new HaRegistryEntity("scene.bedtime", null, null, "Bedtime", false, false, null, []),
            new HaRegistryEntity("scene.hidden", null, null, "Hidden", false, true, null, []),
        ],
        [],
        []);

    private static HaLiveSnapshot Live(HaLiveStatus status = HaLiveStatus.Live, params HaLiveEntity[] entities) =>
        new(status, entities.ToDictionary(e => e.Id, StringComparer.Ordinal), Catalog(), 1, null);

    [Fact]
    public void Items_AreTheBarsOrItsLast_WithoutTheShoppingChip()
    {
        var shown = new AppSettingsData { HomeBarItems = ["area:den", "shopping:list", "monitors:all", "entity:lock.front"] };   // the 🖥️ left out too (2026-10-23)
        Assert.Equal(["area:den", "entity:lock.front"], WebHomeView.Items(shown));

        var hidden = new AppSettingsData { HomeBarItems = [], HomeBarLastItems = ["entity:light.den_lamp", "shopping:list"] };
        Assert.Equal(["entity:light.den_lamp"], WebHomeView.Items(hidden));
    }

    [Fact]
    public void Tiles_AreTheBarsChips_InOrder_WithAliasesGlyphsAndGroupsAtDividers()
    {
        var live = Live(HaLiveStatus.Live, Entity("light.den_lamp", "on", "{\"friendly_name\":\"Den Lamp\"}"), Entity("lock.front", "locked", "{\"friendly_name\":\"Front\"}"));
        var aliases = new Dictionary<string, string> { ["entity:light.den_lamp"] = "Lamp" };
        var glyphs = new Dictionary<string, string> { ["entity:lock.front"] = "🚪" };

        var tiles = WebHomeView.Tiles(["entity:light.den_lamp", "divider:1", "entity:lock.front"], live, _ => HomeChipMark.None, aliases, glyphs, controls: true);

        Assert.Equal(["Lamp", "Front"], tiles.Select(t => t.Name));
        Assert.Equal([0, 1], tiles.Select(t => t.Group));
        Assert.Equal("🚪", tiles[1].Glyph);
        Assert.Equal(("call", "on"), (tiles[0].Action, tiles[0].Tone));
        Assert.NotNull(tiles[0].Color);
        Assert.Equal(("confirm", "off"), (tiles[1].Action, tiles[1].Tone));   // an unlock asks first, as on the bar
        Assert.True(tiles[1].Sheet);
    }

    [Fact]
    public void Tiles_WithControlsOff_DoNothing_AndCarryTheirMarks()
    {
        var live = Live(HaLiveStatus.Live, Entity("light.den_lamp", "off"));
        var tiles = WebHomeView.Tiles(["entity:light.den_lamp"], live, _ => HomeChipMark.Pending, null, null, controls: false);
        Assert.Equal(("none", "pending", "off"), (tiles[0].Action, tiles[0].Mark, tiles[0].Tone));
    }

    [Fact]
    public void Tones_FollowTheBarsLooks_AndAStaleFeedDimsEverything()
    {
        var live = Live(HaLiveStatus.Live,
            Entity("lock.back", "unlocked"),
            Entity("sensor.temp", "21.5", "{\"unit_of_measurement\":\"°C\"}"),
            Entity("light.den_lamp", "unavailable"));
        var tiles = WebHomeView.Tiles(["entity:lock.back", "entity:sensor.temp", "entity:light.den_lamp", "entity:fan.den_fan"], live, _ => HomeChipMark.None, null, null, controls: true);
        Assert.Equal(["alert", "plain", "stale", "stale"], tiles.Select(t => t.Tone));   // unlocked, a value, unavailable, not read yet

        var stale = WebHomeView.Tiles(["entity:lock.back"], Live(HaLiveStatus.Retrying, Entity("lock.back", "unlocked")), _ => HomeChipMark.None, null, null, controls: true);
        Assert.Equal("stale", stale[0].Tone);
    }

    [Fact]
    public void TheReach_IsTheEntityTilesAndTheRoomTilesMembers_AndNothingElse()
    {
        var reach = WebHomeView.Reach(["area:den", "entity:lock.front", "divider:1"], Catalog(), ChatScreen.HomeRoomDomains);
        Assert.Contains("area:den", reach);
        Assert.Contains("light.den_lamp", reach);
        Assert.Contains("fan.den_fan", reach);
        Assert.Contains("lock.front", reach);
        Assert.DoesNotContain("lock.back", reach);   // in a room, but that room is not on the bar
        Assert.DoesNotContain("scene.movie", reach);   // a scene: not a room domain
    }

    [Fact]
    public void TheStatus_IsAWordAndASentence()
    {
        Assert.Equal(("off", WebUiText.HomeNotSetUp), WebHomeView.StatusOf(Live(), configured: false));
        Assert.Equal(("live", (string?)null), WebHomeView.StatusOf(Live(), configured: true));
        Assert.Equal(("unauthorized", WebUiText.HomeUnauthorized), WebHomeView.StatusOf(Live(HaLiveStatus.Unauthorized), configured: true));
        Assert.Equal("connecting", WebHomeView.StatusOf(Live(HaLiveStatus.Connecting), configured: true).Status);
        var retrying = new HaLiveSnapshot(HaLiveStatus.Retrying, new Dictionary<string, HaLiveEntity>(), null, 1, "refused.");
        Assert.Equal(("retrying", WebUiText.HomeRetrying("refused.")), WebHomeView.StatusOf(retrying, configured: true));
        Assert.Contains("(refused)", WebUiText.HomeRetrying("refused."), StringComparison.Ordinal);
    }

    private static IReadOnlyList<WebHomeRoom> Rooms(HaLiveSnapshot live, IReadOnlyList<string> items, Func<string, HomeChipMark>? mark = null) =>
        WebHomeView.Rooms(live, items, mark ?? (_ => HomeChipMark.None), ChatScreen.HomeRoomDomains);

    [Fact]
    public void TheRooms_HoldTheVisibleScenes_NamedByTheirStates_AndEveryRoomWithDevices()
    {
        var live = Live(HaLiveStatus.Live, Entity("scene.movie", "2026-10-15", "{\"friendly_name\":\"Movie night\"}"));
        var rooms = Rooms(live, []);
        Assert.Equal([("den", "Den"), ("hall", "Hall"), (WebHomeView.NoAreaKey, HomeBarText.NoArea)], rooms.Select(g => (g.Key, g.Name)));   // Hall: its locks alone (2026-10-16)
        Assert.Equal([("scene.movie", "Movie night")], rooms[0].Scenes.Select(s => (s.Id, s.Name)));
        Assert.Empty(rooms[1].Scenes);
        Assert.Equal(["lock.back", "lock.front"], rooms[1].Devices.Select(d => d.Entity));
        Assert.Equal([("scene.bedtime", "Bedtime")], rooms[2].Scenes.Select(s => (s.Id, s.Name)));   // the hidden one left out
        Assert.Empty(rooms[2].Devices);
        Assert.Empty(Rooms(new HaLiveSnapshot(HaLiveStatus.Connecting, new Dictionary<string, HaLiveEntity>(), null, 0, null), []));
    }

    /// <summary>
    /// A house for the rooms (2026-10-16): Den with a lamp, its Hue room's group light, a TV, a lock and a thermostat; Kitchen with one light; a
    /// Hue zone on a device of its own over two Garden lights; and a lone sensor in the Garden (not a room pane's domain).
    /// </summary>
    private static HaCatalog RoomCatalog(params HaEntity[] states) => new(
        [],
        [new HaAreaEntry("den", "Den", null), new HaAreaEntry("kitchen", "Kitchen", null), new HaAreaEntry("garden", "Garden", null)],
        [new HaDevice("hue-zone", "Porch zone", null)],
        [
            new HaRegistryEntity("light.den_lamp", null, "den", null, false, false, null, []),
            new HaRegistryEntity("light.den_room", null, "den", null, false, false, null, []),
            new HaRegistryEntity("media_player.den_tv", null, "den", null, false, false, null, []),
            new HaRegistryEntity("lock.den_door", null, "den", null, false, false, null, []),
            new HaRegistryEntity("climate.den", null, "den", null, false, false, null, []),
            new HaRegistryEntity("light.kitchen", null, "kitchen", null, false, false, null, []),
            new HaRegistryEntity("light.porch_zone", "hue-zone", null, null, false, false, null, []),
            new HaRegistryEntity("light.porch_1", null, "garden", null, false, false, null, []),
            new HaRegistryEntity("light.porch_2", null, "garden", null, false, false, null, []),
            new HaRegistryEntity("sensor.soil", null, "garden", null, false, false, null, []),
        ],
        [],
        states);

    private static HaLiveSnapshot RoomLive(HaLiveStatus status = HaLiveStatus.Live, bool zoneLive = true) => new(status, new[]
    {
        Entity("light.den_lamp", "on", "{\"friendly_name\":\"Lamp\",\"brightness\":204,\"rgb_color\":[255,200,0]}"),
        Entity("light.den_room", "on", "{\"friendly_name\":\"Den room\",\"is_hue_group\":true,\"entity_id\":[\"light.den_lamp\"]}"),
        Entity("media_player.den_tv", "off", "{\"friendly_name\":\"TV\"}"),
        Entity("lock.den_door", "locked", "{\"friendly_name\":\"Door\"}"),
        Entity("climate.den", "heat", "{\"friendly_name\":\"Thermostat\",\"temperature\":21}"),
        Entity("light.kitchen", "off", "{\"friendly_name\":\"Kitchen\"}"),
        Entity("light.porch_1", "on", "{\"friendly_name\":\"Porch 1\"}"),
        Entity("light.porch_2", "off", "{\"friendly_name\":\"Porch 2\"}"),
    }.Concat(zoneLive ? [Entity("light.porch_zone", "on", "{\"friendly_name\":\"Porch zone\",\"is_hue_group\":true,\"entity_id\":[\"light.porch_1\",\"light.porch_2\"]}")] : [])
     .ToDictionary(e => e.Id, StringComparer.Ordinal), RoomCatalog(), 1, null);

    /// <summary>
    /// Every room with devices gets a box (2026-10-16): an area's switch is the bar's area chip with its lights on of all, its rows the room pane's
    /// domains in order with the group light left out; a zone's switch its group light, its rows the group's members.
    /// </summary>
    [Fact]
    public void TheRooms_AreasAndAZone_TheirSwitchesCountsAndRows()
    {
        var rooms = Rooms(RoomLive(), []);
        Assert.Equal(["den", "garden", "kitchen", "device:hue-zone"], rooms.Select(r => r.Key));   // the areas by name (no floors), then the zone

        var den = rooms[0];
        Assert.Equal(("area:den", WebUiText.RoomLightsOn(1, 1), (bool?)true, "on"), (den.Ref, den.Value, den.On, den.Tone));   // the group light not counted
        Assert.Equal("1 of 1 on", den.Value);
        Assert.NotNull(den.Color);
        Assert.Equal(["light.den_lamp", "media_player.den_tv", "lock.den_door", "climate.den"], den.Devices.Select(d => d.Entity));   // no group row

        // The control kinds: a switch for a light, its first action as a button for a player and a lock (asking for an unlock), none for a thermostat.
        var lamp = den.Devices[0];
        Assert.Equal(("switch", (bool?)true, HomeBarText.ToggleAction, false), (lamp.Control, lamp.On, lamp.Label, lamp.Ask));
        Assert.Equal("on · 80%", lamp.State);
        Assert.Equal(("Lamp", "on"), (lamp.Name, lamp.Tone));
        Assert.Equal(("button", HomeBarText.TurnOnAction, false), (den.Devices[1].Control, den.Devices[1].Label, den.Devices[1].Ask));
        Assert.Equal(("button", HomeBarText.UnlockAction, true), (den.Devices[2].Control, den.Devices[2].Label, den.Devices[2].Ask));
        Assert.Equal(("none", (string?)null), (den.Devices[3].Control, den.Devices[3].Label));

        // An area with only a light: a box, its switch off.
        var kitchen = rooms[2];
        Assert.Equal(("area:kitchen", "0 of 1 on", (bool?)false, "off"), (kitchen.Ref, kitchen.Value, kitchen.On, kitchen.Tone));
        Assert.Equal(["light.kitchen"], kitchen.Devices.Select(d => d.Entity));

        // The garden's lights are the zone's too: shown in both boxes; its sensor is no row.
        Assert.Equal(["light.porch_1", "light.porch_2"], rooms[1].Devices.Select(d => d.Entity));
        var zone = rooms[3];
        Assert.Equal(("Porch zone", "entity:light.porch_zone", "1 of 2 on", (bool?)true), (zone.Name, zone.Ref, zone.Value, zone.On));
        Assert.Equal(["light.porch_1", "light.porch_2"], zone.Devices.Select(d => d.Entity));
    }

    /// <summary>
    /// A row's name drops its box's in front (2026-10-17, the user's phone): "Den Lamp" in Den is "Lamp", the whole name kept as the row's
    /// name; a name that doesn't start with the box's, or is only the box's ("Kitchen" in Kitchen), has no short form.
    /// </summary>
    [Fact]
    public void TheRooms_ARowsShortName_IsItsNameWithoutTheBoxs()
    {
        var live = RoomLive();
        var entities = new Dictionary<string, HaLiveEntity>(live.Entities, StringComparer.Ordinal)
        {
            ["light.den_lamp"] = Entity("light.den_lamp", "on", "{\"friendly_name\":\"Den Lamp\",\"brightness\":204}"),
        };
        var rooms = Rooms(live with { Entities = entities }, []);

        var den = rooms.Single(r => r.Key == "den");
        Assert.Equal(("Den Lamp", "Lamp"), (den.Devices[0].Name, den.Devices[0].Short));
        Assert.Equal(("TV", (string?)null), (den.Devices[1].Name, den.Devices[1].Short));
        Assert.Equal(("Kitchen", (string?)null), (rooms.Single(r => r.Key == "kitchen").Devices[0].Name, rooms.Single(r => r.Key == "kitchen").Devices[0].Short));
        Assert.All(rooms.Single(r => r.Key == "device:hue-zone").Devices, d => Assert.Null(d.Short));   // "Porch 1" in "Porch zone": whole
    }

    /// <summary>A zone the feed doesn't hold yet is found from the catalog's state; a box with no light has no switch; a stale feed dims every switch.</summary>
    [Fact]
    public void TheRooms_AZoneFromTheCatalogsState_NoLightsNoSwitch_AndAStaleFeed()
    {
        using var attributes = JsonDocument.Parse("{\"is_hue_group\":true,\"entity_id\":[\"light.porch_1\",\"light.porch_2\"]}");
        var catalog = RoomCatalog(new HaEntity("light.porch_zone", "Porch zone", "on", null, attributes.RootElement.Clone(), null));
        var live = RoomLive(zoneLive: false) with { Catalog = catalog };
        var zone = Rooms(live, []).Single(r => r.Key == "device:hue-zone");
        Assert.Equal("entity:light.porch_zone", zone.Ref);
        Assert.Equal(["light.porch_1", "light.porch_2"], zone.Devices.Select(d => d.Entity));

        var hall = Rooms(Live(), []).Single(r => r.Key == "hall");   // locks only
        Assert.Null(hall.Ref);
        Assert.Null(hall.Value);

        Assert.All(Rooms(RoomLive(HaLiveStatus.Retrying), []).Where(r => r.Ref is not null), r => Assert.Equal("stale", r.Tone));
    }

    /// <summary>The rooms' marks are the tiles': a room's switch by its reference, a row by its <c>entity:</c> one.</summary>
    [Fact]
    public void TheRooms_CarryTheirMarks()
    {
        var rooms = Rooms(RoomLive(), [], reference => reference switch
        {
            "area:den" => HomeChipMark.Pending,
            "entity:lock.den_door" => HomeChipMark.Failed,
            _ => HomeChipMark.None,
        });
        Assert.Equal("pending", rooms[0].Mark);
        Assert.Equal([null, null, "failed", null], rooms[0].Devices.Select(d => d.Mark));
        Assert.Null(rooms[2].Mark);
    }

    /// <summary>The reach widens with the rooms (2026-10-16, the user's call): each box's switch and every row's device; never what no room shows.</summary>
    [Fact]
    public void TheReach_TakesTheRooms_SwitchesAndRows()
    {
        var live = RoomLive();
        var rooms = Rooms(live, []);
        var reach = WebHomeView.Reach([], live.Catalog, ChatScreen.HomeRoomDomains, rooms);
        Assert.Contains("area:den", reach);
        Assert.Contains("area:kitchen", reach);
        Assert.Contains("light.porch_zone", reach);
        Assert.Contains("lock.den_door", reach);
        Assert.Contains("light.porch_2", reach);
        Assert.DoesNotContain("sensor.soil", reach);
        Assert.DoesNotContain("light.den_room", reach);   // the group light: no row, and the area's switch covers it

        Assert.True(WebHomeView.Reaches("area:den", [], reach));
        Assert.True(WebHomeView.Reaches("entity:lock.den_door", [], reach));
        Assert.True(WebHomeView.Reaches("entity:light.porch_zone", [], reach));
        Assert.False(WebHomeView.Reaches("entity:sensor.soil", [], reach));
        Assert.True(WebHomeView.Reaches("entity:sensor.soil", ["entity:sensor.soil"], reach));   // a tile is its own reach
        Assert.False(WebHomeView.Reaches("area:attic", [], reach));
    }

    /// <summary>
    /// The scenes' boxes (2026-10-16): by area (the entity's own, else its device's), then a device's name for a scene with no area (a Hue
    /// zone), joining the area of that name; the home bar's rooms first in its order, then the catalog's (by floor, then name), the devices' by
    /// name, no area last.
    /// </summary>
    private static HaCatalog SceneCatalog() => new(
        [new HaFloor("up", "Upstairs", 1), new HaFloor("down", "Downstairs", 0)],
        [new HaAreaEntry("bed", "Bedroom", "up"), new HaAreaEntry("den", "Den", "down"), new HaAreaEntry("bath", "Bathroom", "up")],
        [
            new HaDevice("hue-den", "Den", null),            // a Hue room's device left without its area: joins Den by name
            new HaDevice("hue-zone", "Front Porch", null),   // a zone: its own box
            new HaDevice("hue-bath", "Bath Hue", "bath"),    // its area through the device
            new HaDevice("hue-up", "Upstairs zone", null),
        ],
        [
            new HaRegistryEntity("scene.den_relax", null, "den", null, false, false, null, []),
            new HaRegistryEntity("scene.den_bright", "hue-den", null, null, false, false, null, []),
            new HaRegistryEntity("scene.den", null, "den", null, false, false, null, []),
            new HaRegistryEntity("scene.bed_candle", null, "bed", null, false, false, null, []),
            new HaRegistryEntity("scene.bath_time", "hue-bath", null, null, false, false, null, []),
            new HaRegistryEntity("scene.porch_candle", "hue-zone", null, null, false, false, null, []),
            new HaRegistryEntity("scene.up_night", "hue-up", null, null, false, false, null, []),
            new HaRegistryEntity("scene.party", null, null, null, false, false, null, []),
        ],
        [],
        []);

    private static HaLiveSnapshot SceneLive(HaCatalog catalog) => new(HaLiveStatus.Live, new[]
    {
        Entity("scene.den_relax", "x", "{\"friendly_name\":\"Den Relax\"}"),
        Entity("scene.den_bright", "x", "{\"friendly_name\":\"Den Bright\"}"),
        Entity("scene.den", "x", "{\"friendly_name\":\"Den\"}"),
        Entity("scene.bed_candle", "x", "{\"friendly_name\":\"Bedroom Candle\"}"),
        Entity("scene.bath_time", "x", "{\"friendly_name\":\"Bathroom Bathtime\"}"),
        Entity("scene.porch_candle", "x", "{\"friendly_name\":\"Front Porch Candle\"}"),
        Entity("scene.up_night", "x", "{\"friendly_name\":\"Night\"}"),
        Entity("scene.party", "x", "{\"friendly_name\":\"Party\"}"),
    }.ToDictionary(e => e.Id, StringComparer.Ordinal), catalog, 1, null);

    [Fact]
    public void TheScenesBoxes_GoByArea_ThenDevice_ThenNone_TheBarsRoomsFirst()
    {
        var groups = Rooms(SceneLive(SceneCatalog()), ["entity:light.x", "area:bath", "divider:1", "area:den"]);
        Assert.Equal(["bath", "den", "bed", "device:hue-zone", "device:hue-up", WebHomeView.NoAreaKey], groups.Select(g => g.Key));
        Assert.Equal(["Bathroom", "Den", "Bedroom", "Front Porch", "Upstairs zone", HomeBarText.NoArea], groups.Select(g => g.Name));
        Assert.Equal([true, true, false, false, false, false], groups.Select(g => g.OnBar));

        // Den's three: the one hung on the Den-named device joined it; by name; labels without "Den " in front, a name that is only "Den" whole.
        var den = groups[1].Scenes;
        Assert.Equal(["scene.den", "scene.den_bright", "scene.den_relax"], den.Select(s => s.Id));
        Assert.Equal(["Den", "Bright", "Relax"], den.Select(s => s.Label));
        Assert.Equal("Den Relax", den[2].Name);   // the whole name stays for the title

        Assert.Equal("Bathtime", groups[0].Scenes[0].Label);           // "Bathroom Bathtime" in Bathroom
        Assert.Equal("Candle", groups[3].Scenes[0].Label);             // "Front Porch Candle" in Front Porch
        Assert.Equal("Night", groups[4].Scenes[0].Label);              // doesn't start with its box's name: whole
        Assert.Equal("Party", groups[5].Scenes[0].Label);

        // With no room on the bar: the catalog's order (Downstairs first, then Upstairs by name).
        Assert.Equal(["den", "bath", "bed"], Rooms(SceneLive(SceneCatalog()), []).Take(3).Select(g => g.Key));
    }

    [Fact]
    public void TheScenesBoxes_OfARestOnlyCatalog_GoByTheTemplatesAreas_OrNone()
    {
        var snapshot = new HaSnapshot(
            [new HaEntity("scene.den_relax", "Den Relax", "x", null, default, null), new HaEntity("scene.party", "Party", "x", null, default, null)],
            [new HaArea("den", "Den", ["scene.den_relax"])],
            DateTimeOffset.UnixEpoch);
        var catalog = HaCatalog.FromSnapshot(snapshot);
        var groups = Rooms(new HaLiveSnapshot(HaLiveStatus.Live, new Dictionary<string, HaLiveEntity>(), catalog, 1, null), []);
        Assert.Equal(["den", WebHomeView.NoAreaKey], groups.Select(g => g.Key));
        Assert.Equal("Relax", groups[0].Scenes[0].Label);
    }

    [Theory]
    [InlineData("Den Relax", "Den", "Relax")]
    [InlineData("den relax", "Den", "relax")]
    [InlineData("Den", "Den", "Den")]
    [InlineData("Den ", "Den", "Den ")]
    [InlineData("Denver Lights", "Den", "Denver Lights")]
    [InlineData("Relax", "Den", "Relax")]
    public void ASceneLabel_TakesItsBoxsNameOffItsFront_OnlyWithSomethingLeft(string name, string group, string label) =>
        Assert.Equal(label, WebHomeView.LabelIn(name, group));

    [Fact]
    public void ThePcCard_IsWholePercents_AndNoneWithNothingRead()
    {
        var pc = WebHomeView.Pc(new PerfSnapshot(12.4, 55.6, null, 99.9, 1.5e6, 2e3))!;
        Assert.Equal((12, 56, (int?)null, 100), (pc.Cpu, pc.Ram, pc.Gpu, pc.Vram));
        Assert.Equal(PerfText.Rate(1.5e6).Trim(), pc.Down);
        Assert.Null(WebHomeView.Pc(new PerfSnapshot(null, null, null, null)));
    }

    [Fact]
    public void TheVideoCard_IsTheWindowsSnapshot_OneBasedInAPlaylist()
    {
        Assert.Null(WebHomeView.Video(null));
        var video = WebHomeView.Video(new VideoSnapshot(VideoState.Playing, "abc", "Song", "Band", 12.34, 200, 60, false, PlaylistEntry: 2, PlaylistCount: 5))!;
        Assert.Equal(("playing", "Song", 12.3, 3, 5), (video.State, video.Title, video.Position, video.Entry, video.Count));
        Assert.Equal(WebUiText.VideoWhere(VideoPlace.Window), video.Where);
    }

    /// <summary>
    /// The card under YouTube open links in (2026-10-16): a held video reads <c>waiting</c>, never <c>opening</c>; one with no title yet sends its
    /// id or its playlist; where it plays from the snapshot's place.
    /// </summary>
    [Fact]
    public void TheVideoCard_ReadsAHeldVideoAsWaiting_TheIdForNoTitle_AndWhereItPlays()
    {
        var held = WebHomeView.Video(VideoSnapshot.Opening(new VideoRequest("aqz-KE-bpKQ")) with { Held = new VideoHold(true, DateTimeOffset.UnixEpoch), Place = VideoPlace.WebTab })!;
        Assert.Equal(("waiting", "aqz-KE-bpKQ"), (held.State, held.Title));
        Assert.Equal(WebUiText.VideoWhere(VideoPlace.WebTab), held.Where);
        Assert.NotEqual(WebUiText.VideoWhere(VideoPlace.Window), held.Where);

        var opening = WebHomeView.Video(VideoSnapshot.Opening(VideoRequest.Playlist("PLabcdefghijklmnopqrstu")))!;
        Assert.Equal(("opening", "playlist PLabcdefghijklmnopqrstu"), (opening.State, opening.Title));
    }

    [Fact]
    public void TheServer_IsNamedBySentinel_NeverWithAKey()
    {
        Assert.Equal("", WebHomeView.ServerOf(null));
        Assert.Equal(WebUiText.EmbeddedServer, WebHomeView.ServerOf(new Uri("http://embedded.localhost/v1")));
        Assert.Equal(WebUiText.ClaudeCodeServer, WebHomeView.ServerOf(new Uri("http://claude-code.localhost/v1")));
        Assert.Equal("192.168.1.5:8000", WebHomeView.ServerOf(new Uri("http://user:secret@192.168.1.5:8000/v1")));
    }
}
