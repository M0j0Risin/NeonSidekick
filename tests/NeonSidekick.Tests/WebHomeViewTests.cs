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
/// Home Assistant's state in a word and a sentence, the scenes, and the cards' figures.
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
        var shown = new AppSettingsData { HomeBarItems = ["area:den", "shopping:list", "entity:lock.front"] };
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

    [Fact]
    public void TheScenes_AreTheVisibleOnes_ByAreaThenName_NamedByTheirStates()
    {
        var live = Live(HaLiveStatus.Live, Entity("scene.movie", "2026-10-15", "{\"friendly_name\":\"Movie night\"}"));
        var scenes = WebHomeView.Scenes(live);
        Assert.Equal([("scene.movie", "Movie night"), ("scene.bedtime", "Bedtime")], scenes.Select(s => (s.Id, s.Name)));
        Assert.Empty(WebHomeView.Scenes(new HaLiveSnapshot(HaLiveStatus.Connecting, new Dictionary<string, HaLiveEntity>(), null, 0, null)));
    }

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
