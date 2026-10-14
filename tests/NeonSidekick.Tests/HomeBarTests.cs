using System.Text.Json;
using NeonSidekick.App;
using NeonSidekick.HomeAssistant;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.Tests;

/// <summary>
/// The home bar's pure pieces (2026-10-12): the items and their order (<see cref="HomeBarItems"/>), the chips kind by kind and
/// their clicks (<see cref="HomeChips"/>), the row and its fit (<see cref="HomeBar"/>), the picker's move keys
/// (<see cref="MenuPane.MoveOf"/>), the chord, and the chip pane's actions (<see cref="ChatScreen.HomeActions"/>).
/// </summary>
public sealed class HomeBarTests
{
    private static HaLiveEntity Entity(string id, string state, string attributes = "{}")
    {
        using var document = JsonDocument.Parse(attributes);
        var map = document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
        return new HaLiveEntity(id, state, map, null);
    }

    private static Dictionary<string, HaLiveEntity> Entities(params HaLiveEntity[] entities) => entities.ToDictionary(e => e.Id, StringComparer.Ordinal);

    private static HaCatalog Catalog() => new(
        [new HaFloor("ground", "Ground", 0)],
        [new HaAreaEntry("den", "Den", "ground")],
        [],
        [
            new HaRegistryEntity("light.den_lamp", null, "den", null, false, false, null, []),
            new HaRegistryEntity("light.den_spot", null, "den", null, false, false, null, []),
            new HaRegistryEntity("light.den_group", null, "den", null, false, false, null, []),
        ],
        [],
        []);

    // ─── items ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("area:den", "area:den")]
    [InlineData(" Area:den ", "area:den")]
    [InlineData("entity:Light.Desk", "entity:light.desk")]
    [InlineData("entity:light", null)]
    [InlineData("entity:light.", null)]
    [InlineData("area:", null)]
    [InlineData("light.desk", null)]
    [InlineData(null, null)]
    public void Normalize_KnowsTheTwoKinds(string? reference, string? normalized) =>
        Assert.Equal(normalized, HomeBarItems.Normalize(reference));

    [Fact]
    public void Resolve_KeepsTheSavedOrder_OnceEach_DroppingTheMalformed()
    {
        Assert.Equal(["entity:light.b", "area:den", "entity:light.a"], HomeBarItems.Resolve(["entity:light.b", "bogus", "area:den", "ENTITY:light.b", "entity:light.a", null!]));
        Assert.Empty(HomeBarItems.Resolve(null));
    }

    [Theory]
    [InlineData(2, -1, new[] { "a", "c", "b", "d" }, 1)]
    [InlineData(1, 1, new[] { "a", "c", "b", "d" }, 2)]
    [InlineData(0, -1, new[] { "a", "b", "c", "d" }, 0)]
    [InlineData(2, HomeBarItems.Top, new[] { "c", "a", "b", "d" }, 0)]
    [InlineData(1, HomeBarItems.Bottom, new[] { "a", "c", "d", "b" }, 3)]
    [InlineData(3, 9, new[] { "a", "b", "c", "d" }, 3)]
    public void Move_StepsOrJumps_ClampedToTheEnds(int index, int delta, string[] expected, int landed)
    {
        var (items, at) = HomeBarItems.Move(["a", "b", "c", "d"], index, delta);
        Assert.Equal(expected, items);
        Assert.Equal(landed, at);
    }

    /// <summary>
    /// The groups (later on 2026-10-13, the user's ask: pinned left, center or right): the two side lists split the bar's order, the rest
    /// is center; a side's id that is not on the bar is ignored; an empty side saves as null.
    /// </summary>
    [Fact]
    public void Sides_SplitTheOrder_TheRestIsCenter_AndAnEmptySideSavesAsNull()
    {
        var sides = HomeBarItems.Sides(["area:den", "entity:light.desk", "shopping:list", "divider:1"], [" AREA:den ", "entity:light.gone"], ["shopping:list"]);
        Assert.Equal(["area:den"], sides.Left);
        Assert.Equal(["entity:light.desk", "divider:1"], sides.Center);
        Assert.Equal(["shopping:list"], sides.Right);

        var (items, left, right) = HomeBarItems.SaveSides(sides);
        Assert.Equal(["area:den", "entity:light.desk", "divider:1", "shopping:list"], items);
        Assert.Equal(["area:den"], left);
        Assert.Equal(["shopping:list"], right);
        var center = HomeBarItems.SaveSides(HomeBarItems.Sides(["area:den"], null, null));
        Assert.Equal(["area:den"], center.Items);
        Assert.Null(center.Left);
        Assert.Null(center.Right);
        Assert.Equal(["area:den"], HomeBarItems.Sides(["area:den"], null, null).Center);   // the bar as it always was: all center
    }

    /// <summary>An item added lands at the center's end, whatever is pinned; one taken off leaves its side too; a new divider joins the center.</summary>
    [Fact]
    public void Flip_AddsAtTheCentersEnd_OrTakesOutOfItsGroup()
    {
        var sides = HomeBarItems.Sides(["area:den", "entity:light.desk"], ["area:den"], null);
        var added = HomeBarItems.Flip(sides, "entity:Light.X");
        Assert.Equal(["area:den"], added.Left);
        Assert.Equal(["entity:light.desk", "entity:light.x"], added.Center);
        var taken = HomeBarItems.Flip(added, "area:den");
        Assert.Empty(taken.Left);
        Assert.Equal(["entity:light.desk", "entity:light.x"], taken.All);
        Assert.Equal(["entity:light.desk"], HomeBarItems.Without(added, "entity:light.x").Center);
        Assert.Equal(["area:den"], HomeBarItems.Without(added, "entity:light.x").Left);

        var (divided, at) = HomeBarItems.AddDivider(HomeBarItems.Sides(["area:den", "shopping:list"], null, ["shopping:list"]));
        Assert.Equal(["area:den", "divider:1"], divided.Center);
        Assert.Equal(1, at);
    }

    [Fact]
    public void Toggle_HidesKeepingTheItems_ShowsThemAgain_AndAsksForThePickerTheFirstTime()
    {
        var hide = HomeBarItems.Toggle("", ["area:den"], null)!;
        Assert.Empty(hide.Items);
        Assert.Equal(["area:den"], hide.LastItems);

        var show = HomeBarItems.Toggle("", [], ["area:den"])!;
        Assert.Equal(["area:den"], show.Items);
        Assert.False(show.NeedsPicker);

        Assert.True(HomeBarItems.Toggle("on", null, null)!.NeedsPicker);
        Assert.Empty(HomeBarItems.Toggle("off", ["area:den"], null)!.Items);
        Assert.Equal(["area:den"], HomeBarItems.Toggle("ON", ["area:den"], null)!.Items);
        Assert.Null(HomeBarItems.Toggle("sideways", null, null));
    }

    [Fact]
    public void EntityIds_AreTheEntities_AndAnAreasLights()
    {
        Assert.Equal(["light.den_group", "light.den_lamp", "light.den_spot", "switch.x"], HomeBarItems.EntityIds(["area:den", "entity:switch.x"], Catalog()).Order(StringComparer.Ordinal));
        Assert.Equal(["switch.x"], HomeBarItems.EntityIds(["area:den", "entity:switch.x"], null));   // no registries yet
    }

    [Fact]
    public void Value_IsOff_OrTheCountAndTheFirstNames()
    {
        Assert.Equal("off", HomeBarItems.Value(null, null));
        Assert.Equal("4 · living room, desk lamp, kettle, …", HomeBarItems.Value(["area:living_room", "entity:light.desk_lamp", "entity:switch.kettle", "entity:lock.front"], null));
        Assert.Equal("1 · Den", HomeBarItems.Value(["area:den"], Catalog()));
    }

    // ─── chips ──────────────────────────────────────────────────────────────────

    [Fact]
    public void ALight_KeepsItsBrightnessInTheNote_InItsOwnColour_AndToggles()
    {
        var lamp = Entity("light.den_lamp", "on", """{"friendly_name":"Den Lamp","brightness":204,"rgb_color":[255,200,0]}""");
        var chip = HomeChips.Build("entity:light.den_lamp", Entities(lamp), Catalog(), HomeChipMark.None, stale: false);

        Assert.Equal(HomeChips.LightGlyph, chip.Glyph);
        Assert.Equal("Den Lamp", chip.Name);
        Assert.Equal("Lamp", chip.Short);   // the area's name dropped
        Assert.Equal("", chip.Value);   // no brightness on the chip (the user's call: it changed the widths)
        Assert.StartsWith("Den Lamp: on 80%", chip.Note, StringComparison.Ordinal);   // the note keeps it
        Assert.Equal(new Color(255, 200, 0), chip.Style.Foreground);   // the light's own colour as the text
        Assert.Equal(Color.Default, chip.Style.Background);   // on the page, no fill
        Assert.Equal(HomeChipAction.Call, chip.Action);
        Assert.Equal("light.toggle", chip.Call!.Name);
        Assert.Contains("double-click to switch off", chip.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Off_IsTheOffChip_Unavailable_IsDimWithAQuestion_AndStaleIsDim()
    {
        var off = HomeChips.Build("entity:switch.k", Entities(Entity("switch.k", "off")), null, HomeChipMark.None, false);
        Assert.Equal(HomeChips.Off, off.Style);

        var gone = HomeChips.Build("entity:switch.k", Entities(Entity("switch.k", "unavailable")), null, HomeChipMark.None, false);
        Assert.Equal("?", gone.Value);
        Assert.Equal(HomeChipAction.None, gone.Action);

        var stale = HomeChips.Build("entity:switch.k", Entities(Entity("switch.k", "on")), null, HomeChipMark.Pending, stale: true);
        Assert.Equal(Theme.DimText, stale.Style);
        Assert.Equal(HomeChipMark.Pending, stale.Mark);

        var waiting = HomeChips.Build("entity:switch.k", Entities(), null, HomeChipMark.None, false);
        Assert.Equal(HomeChipAction.None, waiting.Action);
        Assert.Equal("k", HomeChips.Build("entity:switch.k", Entities(), null, HomeChipMark.None, false).Name.Split('.')[^1]);
    }

    [Fact]
    public void AnArea_CountsItsLightsButTheGroups_AndSwitchesThemAll()
    {
        var entities = Entities(
            Entity("light.den_lamp", "on"),
            Entity("light.den_spot", "off"),
            Entity("light.den_group", "on", """{"is_hue_group":true}"""));
        var chip = HomeChips.Build("area:den", entities, Catalog(), HomeChipMark.None, false);

        Assert.Equal(HomeChips.AreaGlyph, chip.Glyph);
        Assert.Equal("Den", chip.Name);
        Assert.Equal("1/2", chip.Value);
        Assert.Equal(new HaServiceCall("light", "turn_off", [], "den").Name, chip.Call!.Name);
        Assert.Equal("den", chip.Call.AreaId);

        var dark = HomeChips.Build("area:den", Entities(Entity("light.den_lamp", "off"), Entity("light.den_spot", "off")), Catalog(), HomeChipMark.None, false);
        Assert.Equal("turn_on", dark.Call!.Service);
        Assert.Equal(HomeChips.Off, dark.Style);
    }

    [Theory]
    [InlineData("lock.front", "locked", "{}", HomeChipAction.Confirm, "unlock")]
    [InlineData("lock.front", "unlocked", "{}", HomeChipAction.Call, "lock")]
    [InlineData("cover.garage", "closed", """{"device_class":"garage"}""", HomeChipAction.Confirm, "open_cover")]
    [InlineData("cover.garage", "open", """{"device_class":"garage"}""", HomeChipAction.Call, "close_cover")]
    [InlineData("cover.blind", "closed", """{"device_class":"blind"}""", HomeChipAction.Call, "open_cover")]
    [InlineData("alarm_control_panel.house", "armed_away", "{}", HomeChipAction.Confirm, "alarm_disarm")]
    [InlineData("media_player.tv", "off", "{}", HomeChipAction.Call, "turn_on")]
    [InlineData("media_player.tv", "playing", "{}", HomeChipAction.Call, "media_play_pause")]
    [InlineData("scene.relax", "scening", "{}", HomeChipAction.Call, "turn_on")]
    [InlineData("script.bedtime", "on", "{}", HomeChipAction.Call, "turn_off")]
    public void TheRiskyOnesAsk_TheRestAct(string id, string state, string attributes, HomeChipAction action, string service)
    {
        var chip = HomeChips.Build("entity:" + id, Entities(Entity(id, state, attributes)), null, HomeChipMark.None, false);
        Assert.Equal(action, chip.Action);
        Assert.Equal(service, chip.Call!.Service);
    }

    /// <summary>A player's chip shows its play or pause mark, never its title (2026-10-12, the user's call); the note keeps the title.</summary>
    [Fact]
    public void APlayer_ShowsItsMark_NotItsTitle_WhichTheNoteKeeps()
    {
        var idle = HomeChips.Build("entity:media_player.tv", Entities(Entity("media_player.tv", "on", """{"device_class":"tv","media_title":"Smart TV"}""")), null, HomeChipMark.None, false);
        Assert.Equal("", idle.Value);
        Assert.Contains("Smart TV", idle.Note, StringComparison.Ordinal);
        Assert.Equal(HomeChips.TvGlyph, idle.Glyph);

        var playing = HomeChips.Build("entity:media_player.tv", Entities(Entity("media_player.tv", "playing", """{"media_title":"A very long title indeed"}""")), null, HomeChipMark.None, false);
        Assert.Equal(HomeBarText.PlayingMark.Trim(), playing.Value);
        Assert.Contains("A very long title indeed", playing.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAlarmThatWantsACode_OpensItsPane_InsteadOfAsking()
    {
        var chip = HomeChips.Build("entity:alarm_control_panel.house", Entities(Entity("alarm_control_panel.house", "armed_home", """{"code_format":"number"}""")), null, HomeChipMark.None, false);
        Assert.Equal(HomeChipAction.Detail, chip.Action);
        Assert.Null(chip.Call);
        Assert.Equal("home", chip.Value);
        Assert.Contains("needs a code", chip.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void AZone_ShowsHowManyPeopleItHolds_DimWhenEmpty_AndOpensItsPane()
    {
        var home = HomeChips.Build("entity:zone.home", Entities(Entity("zone.home", "2", """{"friendly_name":"Home"}""")), null, HomeChipMark.None, false);
        Assert.Equal(HomeChips.ZoneGlyph, home.Glyph);
        Assert.Equal("2", home.Value);
        Assert.Equal(new Style(Theme.Ink), home.Style);
        Assert.Equal(HomeChipAction.Detail, home.Action);
        Assert.Null(home.Call);
        Assert.Equal(HomeBarText.ZoneNote("Home", 2), home.Note);

        var work = HomeChips.Build("entity:zone.work", Entities(Entity("zone.work", "0")), null, HomeChipMark.None, false);
        Assert.Equal("0", work.Value);
        Assert.Equal(HomeChips.Off, work.Style);
        Assert.Empty(ChatScreen.HomeActions(Entity("zone.work", "0")));
    }

    [Fact]
    public void Sensors_ShowTheirValue_AndALowBatteryWarns()
    {
        var temperature = HomeChips.Build("entity:sensor.t", Entities(Entity("sensor.t", "21.47", """{"unit_of_measurement":"°C","device_class":"temperature"}""")), null, HomeChipMark.None, false);
        Assert.Equal("21.5°C", temperature.Value);
        Assert.Equal(HomeChipAction.Detail, temperature.Action);

        var power = HomeChips.Build("entity:sensor.p", Entities(Entity("sensor.p", "1200.0", """{"unit_of_measurement":"W","device_class":"power"}""")), null, HomeChipMark.None, false);
        Assert.Equal("1200 W", power.Value);
        Assert.Equal(HomeChips.PowerGlyph, power.Glyph);

        var battery = HomeChips.Build("entity:sensor.b", Entities(Entity("sensor.b", "8", """{"unit_of_measurement":"%","device_class":"battery"}""")), null, HomeChipMark.None, false);
        Assert.Equal(Theme.Bad, battery.Style.Foreground);

        var door = HomeChips.Build("entity:binary_sensor.d", Entities(Entity("binary_sensor.d", "on", """{"device_class":"door"}""")), null, HomeChipMark.None, false);
        Assert.Equal("open", door.Value);
        Assert.Equal(Theme.Warn, door.Style.Foreground);
        Assert.Equal(HomeChips.DoorGlyph, door.Glyph);

        var leak = HomeChips.Build("entity:binary_sensor.l", Entities(Entity("binary_sensor.l", "on", """{"device_class":"moisture"}""")), null, HomeChipMark.None, false);
        Assert.Equal("wet", leak.Value);
        Assert.Equal(Theme.Bad, leak.Style.Foreground);
    }

    [Fact]
    public void AThermostat_ShowsCurrentToTarget()
    {
        var chip = HomeChips.Build("entity:climate.hall", Entities(Entity("climate.hall", "heat", """{"current_temperature":19.5,"temperature":21,"hvac_action":"heating"}""")), null, HomeChipMark.None, false);
        Assert.Equal("19.5→21°", chip.Value);
        Assert.Equal(HomeChips.HeatGlyph, chip.Glyph);
        Assert.Equal(HomeChipAction.Detail, chip.Action);
    }

    [Fact]
    public void EveryGlyph_IsTwoCells_WithoutASelector()
    {
        foreach (string glyph in HomeChips.Glyphs)
        {
            Assert.Equal(2, TextCells.Width(glyph));
            Assert.DoesNotContain('️', glyph);
        }
    }

    [Fact]
    public void LightTint_ReadsRgb_ThenHs_ThenKelvin()
    {
        Assert.Equal(new Color(10, 20, 30), HomeChips.LightTint(Entity("light.x", "on", """{"rgb_color":[10,20,30],"hs_color":[0,100]}""")));
        Assert.Equal(new Color(255, 0, 0), HomeChips.LightTint(Entity("light.x", "on", """{"hs_color":[0,100]}""")));
        var warm = HomeChips.LightTint(Entity("light.x", "on", """{"color_temp_kelvin":2700}"""))!.Value;
        Assert.Equal(255, warm.R);
        Assert.True(warm.B < warm.G);
        Assert.Null(HomeChips.LightTint(Entity("light.x", "on", """{"brightness":3}""")));
    }

    /// <summary>
    /// The chips are coloured words on the page (2026-10-12, the user's call: fills, even quiet ones, were too much): on is the
    /// colour, off is dim, an alert is bold amber or red, and none has a background.
    /// </summary>
    [Fact]
    public void Chips_AreColouredWords_OnThePage_AlertsBold()
    {
        var lamp = HomeChips.Build("entity:light.k", Entities(Entity("light.k", "on", """{"color_temp_kelvin":2700}""")), null, HomeChipMark.None, false);
        var fan = HomeChips.Build("entity:fan.f", Entities(Entity("fan.f", "on")), null, HomeChipMark.None, false);
        var off = HomeChips.Build("entity:fan.f", Entities(Entity("fan.f", "off")), null, HomeChipMark.None, false);
        var garage = HomeChips.Build("entity:cover.g", Entities(Entity("cover.g", "open", """{"device_class":"garage"}""")), null, HomeChipMark.None, false);
        var blind = HomeChips.Build("entity:cover.b", Entities(Entity("cover.b", "open", """{"device_class":"blind"}""")), null, HomeChipMark.None, false);
        Assert.All(new[] { lamp, fan, off, garage, blind }, chip => Assert.Equal(Color.Default, chip.Style.Background));

        Assert.Equal(HomeChips.Lit(Theme.Secondary), fan.Style);
        Assert.Equal(HomeChips.Lit(Theme.Secondary), blind.Style);
        Assert.Equal(new Style(Theme.Dim), HomeChips.Off);
        Assert.Equal(new Style(Theme.Warn, decoration: Decoration.Bold), garage.Style);
    }

    /// <summary>A lit chip's colour too dark for the page is lifted toward the ink; a bright one is kept (2026-10-12).</summary>
    [Fact]
    public void Readable_LiftsADarkColour_AndKeepsABrightOne()
    {
        var navy = HomeChips.Readable(new Color(0, 0, 60));
        Assert.True(HomeChips.Luminance(navy) >= HomeChips.ReadableFloor);
        Assert.True(navy.B > navy.R);   // still bluish
        Assert.Equal(new Color(255, 200, 0), HomeChips.Readable(new Color(255, 200, 0)));
        Assert.Equal(Theme.Ink, HomeChips.Readable(Theme.Ink));
    }

    // ─── the row ────────────────────────────────────────────────────────────────

    private static HomeChip Chip(string name, string value = "", string shortName = "") =>
        new("entity:light." + name.ToLowerInvariant(), HomeChips.LightGlyph, name, shortName.Length > 0 ? shortName : name, value, HomeChips.Off, HomeChipAction.Call, null, "");

    private static string Text(HomeRow row) => string.Concat(row.Segments.Select(s => s.Text));

    [Fact]
    public void Render_TheChips_Centred_WithTheirSpans()
    {
        // Centred as the meters are (later on 2026-10-12, the user's ask), no ⌂ ahead of them: 19 cells of chips in 80, 30 blanks first.
        var row = HomeBar.Render([Chip("Desk", "80%"), Chip("Hall")], 80)!;

        Assert.Equal(new string(' ', 30) + "💡 Desk 80% 💡 Hall", Text(row));   // one blank between two chips (later still on 2026-10-13)
        Assert.Equal(1, row.Pages);
        Assert.Equal(2, row.Spans.Count);
        Assert.Equal(new HomeSpan(30, 11, HomeTarget.Chip, 0), row.Spans[0]);
        Assert.Equal(new HomeSpan(42, 7, HomeTarget.Chip, 1), row.Spans[1]);
        Assert.Null(HomeBar.HitAt(row.Spans, 29));   // the run ahead is a blank: the toolbar's, /settings on a double-click
        Assert.Equal(HomeTarget.Chip, HomeBar.HitAt(row.Spans, 32)!.Value.Target);
        Assert.Equal(0, HomeBar.HitAt(row.Spans, 40)!.Value.Index);
        Assert.Null(HomeBar.HitAt(row.Spans, 41));   // the blank between the chips is the bar's blank space
        Assert.Equal(1, HomeBar.HitAt(row.Spans, 42)!.Value.Index);
        Assert.Null(HomeBar.Render([], 80));
    }

    private static HomeChip Divider(int n) => HomeChips.Build("divider:" + n, new Dictionary<string, HaLiveEntity>(), null, HomeChipMark.None, stale: false);

    /// <summary>
    /// A divider (2026-10-13, the user's ask): a dim │ between two chips, no span (a click is a blank's); one at either end or beside
    /// another is left off (<see cref="HomeBar.ShownDividers"/>), and so is one at a page's edge.
    /// </summary>
    [Fact]
    public void Render_ADivider_IsADimBarBetweenChips_NoSpan()
    {
        var divider = Divider(1);
        Assert.True(divider.Divider);
        Assert.Equal(Theme.TabSeparator, divider.Style);
        Assert.Equal("│", HomeBar.ChipText(divider, HomeBar.Fit.Full));

        var row = HomeBar.Render([Chip("Desk"), divider, Chip("Hall")], 80)!;
        Assert.EndsWith(" 💡 Desk │ 💡 Hall", Text(row));   // the tool bar's 🎓 │ 💾
        Assert.Equal(2, row.Spans.Count);
        Assert.Equal([0, 2], row.Spans.Select(s => s.Index));
        int bar = Text(row).IndexOf('│', StringComparison.Ordinal);
        Assert.Null(HomeBar.HitAt(row.Spans, TextCells.Width(Text(row)[..bar])));
        Assert.Contains(row.Segments, s => s.Text == "│" && s.Style == Theme.TabSeparator);

        var stray = HomeBar.ShownDividers([Divider(1), Chip("Desk"), Divider(2), Divider(3), Chip("Hall"), Divider(4)]);
        Assert.Equal(["entity:light.desk", "divider:2", "entity:light.hall"], stray.Select(c => c.Reference));
        Assert.Empty(HomeBar.ShownDividers([Divider(1), Divider(2)]));
    }

    /// <summary>The divider's reference (2026-10-13): parsed and kept apart from the chips the commands name; no name to change, no count.</summary>
    [Fact]
    public void ADividersReference_IsItsOwn_AndNeverTheOneNamed()
    {
        Assert.True(HomeBarItems.TryParse(" Divider:2 ", out var kind, out string id));
        Assert.Equal(HomeItemKind.Divider, kind);
        Assert.Equal("2", id);
        Assert.Equal("divider:2", HomeBarItems.Normalize("DIVIDER:2"));
        Assert.Null(HomeBarItems.Normalize("divider:0"));
        Assert.True(HomeBarItems.IsNameless("divider:1"));
        Assert.True(HomeBarItems.IsDivider("divider:1"));
        Assert.Equal(["area:den", "divider:1", "divider:2"], HomeBarItems.Resolve(["area:den", "divider:1", "divider:2", "divider:1"]));
        Assert.Equal(["area:den", "divider:1"], HomeBarItems.AddDivider(HomeBarItems.Sides(["area:den"], null, null)).Sides.All);
        Assert.Equal("Divider", HomeBarItems.NameOf("divider:1", null));
        Assert.Null(HomeBarItems.Find(["divider:1", "area:den"], "Divider", null, null));
        Assert.Null(HomeBarItems.Find(["divider:1"], "divider:1", null, null));
        Assert.Equal("1 · den", HomeBarItems.Value(["divider:1", "area:den"], null));   // a divider is no item to count
        Assert.Empty(HomeBarItems.EntityIds(["divider:1"], null));
    }

    /// <summary>The odd cell of the room goes to the right, as the meters' does: 7 cells in 20 is 6 blanks ahead, 7 after.</summary>
    [Fact]
    public void TheChips_AreCentred_TheOddCellAtTheRight()
    {
        var row = HomeBar.Render([Chip("Desk")], 20)!;

        Assert.Equal("      💡 Desk", Text(row));
        Assert.Equal(new HomeSpan(6, 7, HomeTarget.Chip, 0), row.Spans[0]);
        Assert.Equal("💡 Desk", Text(HomeBar.Render([Chip("Desk")], 7)!));   // a row just wide enough: no run ahead
    }

    [Fact]
    public void Render_ShortensTheNames_ThenDropsThem_ThenPages()
    {
        var chips = new[] { Chip("Living Room Lamp", "50%", "Lamp"), Chip("Kitchen Spot", "", "Spot"), Chip("Hallway Ceiling", "", "Ceiling") };
        Assert.Contains("Living Room Lamp", Text(HomeBar.Render(chips, 80)!), StringComparison.Ordinal);
        Assert.Equal("     💡 Lamp 50% 💡 Spot 💡 Ceiling", Text(HomeBar.Render(chips, 40)!));
        Assert.Equal("     💡 50% 💡 💡", Text(HomeBar.Render(chips, 22)!));

        var paged = HomeBar.Render(chips, 11)!;
        Assert.True(paged.Pages > 1);
        Assert.StartsWith("‹ 💡 50% ›", Text(paged), StringComparison.Ordinal);   // centred too: 10 cells in 11, the odd cell at the right
        Assert.Equal(new HomeSpan(2, 6, HomeTarget.Chip, 0), paged.Spans.Single(s => s.Target == HomeTarget.Chip));
        Assert.EndsWith(" ›", Text(paged), StringComparison.Ordinal);
        Assert.DoesNotContain(paged.Spans, s => s.Target == HomeTarget.Prev);   // the first page: nothing back
        Assert.Contains(paged.Spans, s => s.Target == HomeTarget.Next);
        var last = HomeBar.Render(chips, 11, page: 99)!;
        Assert.Equal(" ‹ 💡 💡 ›", Text(last));   // a page's chips one blank apart, the arrows a blank off them; 9 cells in 11, centred
        Assert.Contains(last.Spans, s => s.Target == HomeTarget.Prev);
        Assert.DoesNotContain(last.Spans, s => s.Target == HomeTarget.Next);
    }

    /// <summary>
    /// Three groups (later on 2026-10-13, the user's ask): the left from the row's start, the right ending on its last cell, the center
    /// centred on the row; the spans count left, center, right.
    /// </summary>
    [Fact]
    public void Render_TheGroups_LeftAtTheStart_RightAtTheEnd_TheCenterCentred()
    {
        var row = HomeBar.Render([Chip("Desk")], [Chip("Hall")], [Chip("Lamp")], 40)!;

        Assert.Equal("💡 Desk" + new string(' ', 9) + "💡 Hall" + new string(' ', 10) + "💡 Lamp", Text(row));
        Assert.Equal([new HomeSpan(0, 7, HomeTarget.Chip, 0), new HomeSpan(16, 7, HomeTarget.Chip, 1), new HomeSpan(33, 7, HomeTarget.Chip, 2)], row.Spans);
        Assert.Equal(Text(HomeBar.Render([Chip("Hall")], 40)!), Text(HomeBar.Render([], [Chip("Hall")], [], 40)!));   // the center alone: as the bar always was

        // A wide left group pushes the center off the row's middle, GroupGap after it; the right alone ends on the last cell.
        var pushed = HomeBar.Render([Chip("Living Room Lamp")], [Chip("Desk")], [], 40)!;
        Assert.Equal("💡 Living Room Lamp" + new string(' ', HomeBar.GroupGap) + "💡 Desk", Text(pushed));
        Assert.Equal(new string(' ', 33) + "💡 Lamp", Text(HomeBar.Render([], [], [Chip("Lamp")], 40)!));
    }

    /// <summary>
    /// Too narrow (later on 2026-10-13, the user's calls): every name shrinks first; then the right group goes from its inner end, then
    /// the left from its; then the center pages alone. What goes leaves nothing behind, and a divider left at a group's cut edge goes too.
    /// </summary>
    [Fact]
    public void Render_TheNamesShrinkFirst_ThenTheRightGoes_ThenTheLeft_ThenTheCenterPages()
    {
        HomeChip[] left = [Chip("Left Lamp", "", "LL")];
        HomeChip[] center = [Chip("Center", "", "C")];
        HomeChip[] right = [Chip("Right Lamp", "", "RL")];
        Assert.Contains("Right Lamp", Text(HomeBar.Render(left, center, right, 44)!), StringComparison.Ordinal);
        Assert.Equal("💡 LL" + new string(' ', 8) + "💡 C" + new string(' ', 9) + "💡 RL", Text(HomeBar.Render(left, center, right, 31)!));
        Assert.Equal(3, HomeBar.Render(left, center, right, 14)!.Spans.Count);   // the glyphs alone, all three
        Assert.Equal([0, 1], HomeBar.Render(left, center, right, 13)!.Spans.Select(s => s.Index));   // the right gone first
        Assert.Equal([1], HomeBar.Render(left, center, right, 7)!.Spans.Select(s => s.Index));       // then the left

        // The right group goes from its inner end (its outer chip kept), the left from its; a divider at the cut edge goes with them.
        var wide = HomeBar.Render([Chip("A"), Chip("B")], [Chip("C")], [Chip("D"), Divider(1), Chip("E")], 20)!;
        Assert.Equal([0, 1, 2, 5], wide.Spans.Select(s => s.Index));
        Assert.DoesNotContain("│", Text(wide), StringComparison.Ordinal);
        Assert.Equal([0, 2], HomeBar.Render([Chip("A"), Chip("B")], [Chip("C")], [], 10)!.Spans.Select(s => s.Index));

        // The center alone pages, the groups gone; its spans still count from the left group's.
        var paged = HomeBar.Render([Chip("L")], [Chip("C1"), Chip("C2"), Chip("C3")], [Chip("R")], 7)!;
        Assert.Equal(3, paged.Pages);
        Assert.Equal(1, paged.Spans.Single(s => s.Target == HomeTarget.Chip).Index);
        Assert.Equal(3, HomeBar.Render([Chip("L")], [Chip("C1"), Chip("C2"), Chip("C3")], [Chip("R")], 7, page: 2)!.Spans.Single(s => s.Target == HomeTarget.Chip).Index);
        var tv = Chip("Tv", "▸ A very long title indeed") with { Short = "Tv" };
        Assert.Equal(0, HomeBar.Render([tv], [], [], 12)!.Spans.Single().Index);   // no center: the left pages in its place, cut to the page
    }

    [Fact]
    public void AChipWiderThanAPage_IsCut_AndTheRowNeverOverflows()
    {
        // The review's finding (2026-10-12): a playing title on a narrow window was a page alone, uncut, and wrapped the row.
        var wide = Chip("Tv", "▸ A very long title indeed") with { Short = "Tv" };
        foreach (int cells in new[] { 30, 20, 12, 8, 3 })
        {
            var row = HomeBar.Render([wide, Chip("Desk")], cells)!;
            Assert.True(TextCells.Width(Text(row)) <= cells, cells + ": " + Text(row));
        }

        Assert.Equal("", Text(HomeBar.Render([wide], 6)!));   // no room for a page: an empty row (the ⌂ alone until it went), the pane's layout kept
    }

    [Fact]
    public void TheKey_MovesWithAStyle_EvenWhenNoTextDoes()
    {
        var red = Chip("Desk") with { Style = new Style(Color.White, new Color(255, 0, 0)) };
        var blue = red with { Style = new Style(Color.White, new Color(0, 0, 255)) };
        var a = HomeBar.Render([red], 40)!;
        var b = HomeBar.Render([blue], 40)!;
        Assert.Equal(Text(a), Text(b));
        Assert.NotEqual(a.Key, b.Key);
    }

    [Fact]
    public void AChipsMark_FollowsIt()
    {
        Assert.Equal("💡 Desk…", HomeBar.ChipText(Chip("Desk") with { Mark = HomeChipMark.Pending }, HomeBar.Fit.Full));
        Assert.Equal("💡 Desk!", HomeBar.ChipText(Chip("Desk") with { Mark = HomeChipMark.Failed }, HomeBar.Fit.Full));
    }

    /// <summary>
    /// The chips one blank apart (later still on 2026-10-13, the user's ask): glyph-only chips pinned left sit three cells apart from
    /// column 0, under the tool bar's glyphs; a mark stands in the blank after its chip; the right group ends on the row's last cell.
    /// </summary>
    [Fact]
    public void BareChips_PinnedLeft_LineUpUnderTheToolbarGlyphs()
    {
        string[] glyphs = ["📎", "🧘‍♂️", "🌄", "🖥️", "🪴", "🛋️", "🕯️"];
        var bare = glyphs.Select((g, i) => Chip("Item" + i) with { Glyph = g, Label = "", Short = "", Bare = true }).ToList();
        var row = HomeBar.Render(bare, [], [], 80)!;

        Assert.Equal(string.Join(" ", glyphs), Text(row));
        Assert.Equal([0, 3, 6, 9, 12, 15, 18], row.Spans.Select(s => s.Start));
        string[] tools = ["⚙️", "🛠️", "🎓", "💾", "⏳", "🔒", "👮"];   // the tool bar's strip: each glyph and a blank, from column 0
        Assert.Equal(Enumerable.Range(0, 7).Select(i => tools.Take(i).Sum(g => TextCells.Width(g) + 1)), row.Spans.Select(s => s.Start));

        var marked = HomeBar.Render([bare[0] with { Mark = HomeChipMark.Pending }, bare[1], bare[2]], [], [], 80)!;
        Assert.Equal("📎…🧘‍♂️ 🌄", Text(marked));   // the mark in the blank: the chips keep their columns
        Assert.Equal([0, 3, 6], marked.Spans.Select(s => s.Start));

        var ends = HomeBar.Render([Chip("Desk")], [], [Chip("Hall"), Chip("Lamp")], 40)!;
        Assert.EndsWith("💡 Hall 💡 Lamp", Text(ends), StringComparison.Ordinal);
        Assert.Equal(40, TextCells.Width(Text(ends)));
        Assert.StartsWith("💡 Desk ", Text(ends), StringComparison.Ordinal);
    }

    // ─── keys and the pane ──────────────────────────────────────────────────────

    [Fact]
    public void CtrlW_IsTheHomeBar_InBothSpellings_AndCtrlAltW_IsThePromptsNow()
    {
        Assert.Equal("/homebar", Keys.ShortcutLine(new ConsoleKeyInfo('\0', ConsoleKey.W, shift: false, alt: false, control: true)));
        Assert.Equal("/homebar", Keys.ShortcutLine(new ConsoleKeyInfo('\x17', ConsoleKey.W, shift: false, alt: false, control: true)));
        Assert.Equal("/prompts", Keys.ShortcutLine(new ConsoleKeyInfo('\x17', ConsoleKey.W, shift: false, alt: true, control: true)));   // the saved prompts since 2026-10-13
        Assert.Null(Keys.ShortcutLine(new ConsoleKeyInfo('w', ConsoleKey.W, shift: false, alt: false, control: false)));
    }

    [Theory]
    [InlineData(ConsoleKey.UpArrow, true, false, -1)]
    [InlineData(ConsoleKey.UpArrow, false, true, -1)]
    [InlineData(ConsoleKey.DownArrow, true, false, 1)]
    [InlineData(ConsoleKey.Home, false, true, int.MinValue)]
    [InlineData(ConsoleKey.End, true, false, int.MaxValue)]
    [InlineData(ConsoleKey.Delete, false, false, 0)]
    public void MoveOf_TheMoveKeys(ConsoleKey key, bool shift, bool alt, int move) =>
        Assert.Equal(move, MenuPane.MoveOf(new ConsoleKeyInfo('\0', key, shift, alt, control: false)));

    [Theory]
    [InlineData(ConsoleKey.UpArrow, false, false, false)]
    [InlineData(ConsoleKey.UpArrow, true, false, true)]   // Ctrl+Shift+↑ is no move
    [InlineData(ConsoleKey.Delete, true, false, false)]
    [InlineData(ConsoleKey.Enter, false, false, false)]
    public void MoveOf_AnythingElse_IsNull(ConsoleKey key, bool shift, bool alt, bool control) =>
        Assert.Null(MenuPane.MoveOf(new ConsoleKeyInfo('\0', key, shift, alt, control)));

    [Fact]
    public void HomeActions_ALightsSteps_ALocksUnlockAsks_ASensorHasNone()
    {
        var light = ChatScreen.HomeActions(Entity("light.x", "off"));
        Assert.Equal([HomeBarText.ToggleAction, HomeBarText.TurnOnAction, HomeBarText.TurnOffAction, "Brightness 25%", "Brightness 50%", "Brightness 75%", "Brightness 100%"], light.Select(a => a.Label));
        Assert.Equal("""{"brightness_pct":25}""", light[3].Call.Data);

        var locked = ChatScreen.HomeActions(Entity("lock.front", "locked"));
        Assert.Equal((HomeBarText.UnlockAction, true), (locked[0].Label, locked[0].Ask));
        Assert.False(locked[1].Ask);

        var heat = ChatScreen.HomeActions(Entity("climate.hall", "heat", """{"temperature":21,"target_temp_step":0.5}"""));
        Assert.Equal("""{"temperature":21.5}""", heat[0].Call.Data);
        Assert.Equal("""{"temperature":20.5}""", heat[1].Call.Data);

        Assert.Empty(ChatScreen.HomeActions(Entity("sensor.t", "20")));
    }

    [Fact]
    public void Toggles_AreBareOnAndOff()
    {
        Assert.True(ChatScreen.HomeBarToggles(""));
        Assert.True(ChatScreen.HomeBarToggles(" On "));
        Assert.True(ChatScreen.HomeBarToggles("off"));
        Assert.False(ChatScreen.HomeBarToggles("edit"));
        Assert.False(ChatScreen.HomeBarToggles("tap entity:light.x"));
    }

    [Fact]
    public void TheWords_ArePinned()
    {
        Assert.Equal("Unlock Front Door?", HomeBarText.Confirm(HaServiceCall.On("lock", "unlock", "lock.front"), "Front Door"));
        Assert.Equal("Open Garage?", HomeBarText.Confirm(HaServiceCall.On("cover", "open_cover", "cover.garage"), "Garage"));
        Assert.Equal("Disarm House?", HomeBarText.Confirm(HaServiceCall.On("alarm_control_panel", "alarm_disarm", "alarm_control_panel.house"), "House"));
        Assert.Equal("2/5", HomeBarText.Count(2, 5));
        Assert.Equal("19→21°", HomeBarText.ClimateValue("19", "21"));
        Assert.Equal("home", HomeBarText.AlarmWord("armed_home"));
        Assert.Equal("Usage: /homebar [on|off|edit|name <item> = <alias>|emoji <item> = <emoji>] — show or hide the home bar, choose what is on it, or rename an item on it or change its emoji", HomeBarText.Usage);
        Assert.Equal("The home bar shows Den Lamp as 🦊", HomeBarText.GlyphSet("Den Lamp", "🦊"));
        Assert.Equal("The home bar shows Den Lamp with its own emoji again, 💡", HomeBarText.GlyphReset("Den Lamp", "💡"));
        Assert.Equal("'abc' is not one emoji: /homebar emoji <item> = <emoji>, or = default for the item's own", HomeBarText.NotEmoji("abc"));
        Assert.Equal(["on", "off", "edit", "name", "emoji"], HomeBarText.Words);
        Assert.Equal("Shopping list: 3 to get — double-click or right-click for the list", HomeBarText.ShoppingNote(3));
        Assert.Equal("Shopping list: the list is off — /shopping says why", HomeBarText.ShoppingNote(null));
        Assert.Equal("The Shopping list's chip shows no name to change: /homebar emoji changes its emoji", HomeBarText.Nameless("Shopping list"));
        Assert.Equal("The home bar shows Den PC Monitor as \"PC\"", HomeBarText.Renamed("Den PC Monitor", "PC"));
        Assert.Equal("The home bar shows Den PC Monitor by its own name again", HomeBarText.Unnamed("Den PC Monitor"));
    }

    // ─── aliases ────────────────────────────────────────────────────────────────

    [Fact]
    public void Rename_SetsReplacesAndClears_OnNullOrTheOwnName_ABlankIsTheEmojiAlone()
    {
        var one = HomeBarItems.Rename(null, "entity:Light.Den_PC", " PC ", "Den PC Monitor")!;
        Assert.Equal("PC", one["entity:light.den_pc"]);
        Assert.Equal("PC", HomeBarItems.AliasOf(one, "ENTITY:light.den_pc"));

        var two = HomeBarItems.Rename(one, "area:den", "Office", "Den")!;
        Assert.Equal(2, two.Count);
        Assert.Equal("Screen", HomeBarItems.Rename(two, "entity:light.den_pc", "Screen", "Den PC Monitor")!["entity:light.den_pc"]);

        Assert.Equal("", HomeBarItems.AliasOf(HomeBarItems.Rename(two, "entity:light.den_pc", "  ", "Den PC Monitor"), "entity:light.den_pc"));   // the emoji alone (later on 2026-10-12)
        Assert.Null(HomeBarItems.AliasOf(HomeBarItems.Rename(two, "entity:light.den_pc", null, "Den PC Monitor"), "entity:light.den_pc"));
        Assert.Equal("area:den", HomeBarItems.Find(["area:den"], "den", null, new Dictionary<string, string> { ["area:den"] = "" }));   // an empty alias names nothing
        Assert.Null(HomeBarItems.Rename(one, "entity:light.den_pc", "den pc monitor", "Den PC Monitor"));   // the own name, any case: none left
        Assert.Null(HomeBarItems.AliasOf(null, "entity:light.den_pc"));
        Assert.Equal(one, HomeBarItems.Rename(one, "bogus", "X", "Y"));
    }

    /// <summary>An empty alias (later on 2026-10-12, the user's ask): the chip's emoji alone, a room's count and a sensor's value gone too.</summary>
    [Fact]
    public void AnEmptyAlias_IsTheEmojiAlone_NoNameAndNoValue()
    {
        var aliases = new Dictionary<string, string> { ["area:den"] = "", ["entity:sensor.t"] = " " };
        var room = HomeChips.Build("area:den", Entities(Entity("light.den_lamp", "on"), Entity("light.den_spot", "off")), Catalog(), HomeChipMark.None, false, aliases);
        Assert.True(room.Bare);
        Assert.Equal(HomeChips.AreaGlyph, HomeBar.ChipText(room, HomeBar.Fit.Full));
        Assert.Equal(HomeChips.AreaGlyph, HomeBar.ChipText(room, HomeBar.Fit.Short));
        Assert.StartsWith("Den: ", room.Note, StringComparison.Ordinal);   // the click's note keeps the name and the count

        var sensor = HomeChips.Build("entity:sensor.t", Entities(Entity("sensor.t", "21.47")), null, HomeChipMark.Pending, false, aliases);
        Assert.Equal(sensor.Glyph + HomeBarText.PendingMark, HomeBar.ChipText(sensor, HomeBar.Fit.Full));   // the mark still shows

        Assert.Equal("emoji alone · light.den", HomeBarText.AliasNote("", "light.den"));
        Assert.Equal("The home bar shows Den as its emoji alone", HomeBarText.Bared("Den"));
    }

    [Fact]
    public void Find_TakesTheReference_TheAlias_TheName_OrTheId()
    {
        string[] items = ["area:den", "entity:light.den_lamp", "entity:light.den_spot"];
        var aliases = new Dictionary<string, string> { ["entity:light.den_spot"] = "Spot" };
        var catalog = new HaCatalog([], [new HaAreaEntry("den", "Den", null)], [], [], [], [new HaEntity("light.den_lamp", "Den Lamp", "on", null, default, null), new HaEntity("light.den_spot", "Den Spot", "on", null, default, null)]);

        Assert.Equal("entity:light.den_lamp", HomeBarItems.Find(items, "entity:light.den_lamp", catalog, aliases));
        Assert.Equal("entity:light.den_spot", HomeBarItems.Find(items, "spot", catalog, aliases));
        Assert.Equal("entity:light.den_lamp", HomeBarItems.Find(items, "Den Lamp", catalog, aliases));
        Assert.Equal("area:den", HomeBarItems.Find(items, "den", catalog, aliases));
        Assert.Null(HomeBarItems.Find(items, "entity:light.kitchen", catalog, aliases));
        Assert.Null(HomeBarItems.Find(items, " ", catalog, aliases));
    }

    [Fact]
    public void AnAlias_IsTheChipsLabelAndShortName_TheNoteKeepsTheName()
    {
        var monitor = Entity("light.den_pc_monitor", "on", """{"friendly_name":"Den PC Monitor"}""");
        var aliases = new Dictionary<string, string> { ["entity:light.den_pc_monitor"] = "PC" };
        var chip = HomeChips.Build("entity:light.den_pc_monitor", Entities(monitor), Catalog(), HomeChipMark.None, stale: false, aliases);

        Assert.Equal("PC", chip.Label);
        Assert.Equal("PC", chip.Short);
        Assert.Equal("Den PC Monitor", chip.Name);
        Assert.StartsWith("Den PC Monitor: on", chip.Note, StringComparison.Ordinal);
        Assert.Contains(" PC", HomeBar.ChipText(chip, HomeBar.Fit.Full), StringComparison.Ordinal);
        Assert.DoesNotContain("Monitor", HomeBar.ChipText(chip, HomeBar.Fit.Full), StringComparison.Ordinal);

        var plain = HomeChips.Build("entity:light.den_pc_monitor", Entities(monitor), Catalog(), HomeChipMark.None, stale: false);
        Assert.Null(plain.Label);
        Assert.Contains("Den PC Monitor", HomeBar.ChipText(plain, HomeBar.Fit.Full), StringComparison.Ordinal);
    }

    // ─── emoji of the user's own (later on 2026-10-12) ──────────────────────────

    [Fact]
    public void SetGlyph_SetsReplacesAndClears_OnABlankTheOwnGlyphOrAWord()
    {
        var one = HomeBarItems.SetGlyph(null, "entity:Light.Den_Lamp", " 🦊 ", "💡")!;
        Assert.Equal("🦊", one["entity:light.den_lamp"]);
        Assert.Equal("🦊", HomeBarItems.GlyphOf(one, "ENTITY:light.den_lamp"));

        var two = HomeBarItems.SetGlyph(one, "area:den", "🛋️", HomeChips.AreaGlyph)!;
        Assert.Equal(2, two.Count);
        Assert.Equal("🐙", HomeBarItems.SetGlyph(two, "entity:light.den_lamp", "🐙", "💡")!["entity:light.den_lamp"]);

        Assert.Null(HomeBarItems.GlyphOf(HomeBarItems.SetGlyph(two, "entity:light.den_lamp", "  ", "💡"), "entity:light.den_lamp"));
        Assert.Null(HomeBarItems.SetGlyph(one, "entity:light.den_lamp", "💡", "💡"));   // the own glyph: none left
        Assert.Null(HomeBarItems.SetGlyph(one, "entity:light.den_lamp", "fox", "💡"));   // not an emoji: taken off
        Assert.Null(HomeBarItems.SetGlyph(one, "entity:light.den_lamp", "🦊🦊", "💡"));
        Assert.Equal(one, HomeBarItems.SetGlyph(one, "bogus", "🐙", "💡"));
        Assert.Null(HomeBarItems.GlyphOf(null, "entity:light.den_lamp"));
        Assert.Null(HomeBarItems.GlyphOf(new Dictionary<string, string> { ["entity:light.den_lamp"] = "fox" }, "entity:light.den_lamp"));   // hand-edited: ignored
    }

    [Fact]
    public void AnEmoji_IsTheChipsGlyph_WhateverTheState_AndTheDefaultIsItsKinds()
    {
        var glyphs = new Dictionary<string, string> { ["entity:lock.front"] = "🚪", ["area:den"] = "🛋️" };
        var locked = HomeChips.Build("entity:lock.front", Entities(Entity("lock.front", "locked")), null, HomeChipMark.None, false, null, glyphs);
        var unlocked = HomeChips.Build("entity:lock.front", Entities(Entity("lock.front", "unlocked")), null, HomeChipMark.None, false, null, glyphs);
        Assert.Equal("🚪", locked.Glyph);
        Assert.Equal("🚪", unlocked.Glyph);
        Assert.Equal(HomeChipAction.Confirm, locked.Action);   // what it does is unchanged
        Assert.Equal("🛋️", HomeChips.Build("area:den", Entities(), Catalog(), HomeChipMark.None, false, null, glyphs).Glyph);
        Assert.Equal(HomeChips.LockedGlyph, HomeChips.Build("entity:lock.front", Entities(Entity("lock.front", "locked")), null, HomeChipMark.None, false).Glyph);

        Assert.Equal(HomeChips.AreaGlyph, HomeChips.DefaultGlyph("area:den", null));
        Assert.Equal(HomeChips.LockedGlyph, HomeChips.DefaultGlyph("entity:lock.front", null, Entity("lock.front", "locked")));
        Assert.Equal(HomeChips.GlyphFor("light.x", null), HomeChips.DefaultGlyph("entity:light.x", null));
        Assert.Equal(HomeChips.ToggleGlyph, HomeChips.DefaultGlyph("bogus", null));
    }

    // ─── the shopping list's 🛒 (later on 2026-10-12) ───────────────────────────

    [Fact]
    public void TheShoppingList_IsAnItem_WithNoNameToChange_AndNoEntities()
    {
        Assert.Equal("shopping:list", HomeBarItems.Shopping);
        Assert.Equal(HomeBarItems.Shopping, HomeBarItems.Normalize(" SHOPPING:List "));
        Assert.Null(HomeBarItems.Normalize("shopping:other"));
        Assert.True(HomeBarItems.TryParse("shopping:list", out var kind, out _));
        Assert.Equal(HomeItemKind.Shopping, kind);
        Assert.Equal([HomeBarItems.Shopping, "area:den"], HomeBarItems.Resolve(["shopping:list", "Shopping:LIST", "area:den"]));
        Assert.Equal(HomeBarText.ShoppingName, HomeBarItems.NameOf(HomeBarItems.Shopping, null));
        Assert.Equal("1 · Shopping list", HomeBarItems.Value([HomeBarItems.Shopping], null));
        Assert.Empty(HomeBarItems.EntityIds([HomeBarItems.Shopping], Catalog()));
        Assert.Equal(HomeBarItems.Shopping, HomeBarItems.Find([HomeBarItems.Shopping], "shopping list", null, null));

        Assert.True(HomeBarItems.IsNameless(HomeBarItems.Shopping));
        Assert.False(HomeBarItems.IsNameless("area:den"));
        Assert.Null(HomeBarItems.Rename(null, HomeBarItems.Shopping, "Cart", HomeBarText.ShoppingName));
        Assert.Null(HomeBarItems.AliasOf(new Dictionary<string, string> { [HomeBarItems.Shopping] = "Cart" }, HomeBarItems.Shopping));   // hand-edited: ignored
        Assert.Equal(HomeChips.ShoppingGlyph, HomeChips.DefaultGlyph(HomeBarItems.Shopping, null));
    }

    [Fact]
    public void TheShoppingChip_IsTheCartAndItsCount_LitWhileAnythingIsLeft_NeverStale()
    {
        var three = HomeChips.Build(HomeBarItems.Shopping, Entities(), null, HomeChipMark.None, stale: true, shoppingOpen: 3);
        Assert.Equal("🛒 3", HomeBar.ChipText(three, HomeBar.Fit.Full));
        Assert.Equal("🛒 3", HomeBar.ChipText(three, HomeBar.Fit.Short));
        Assert.Equal(HomeChips.Lit(Theme.Secondary), three.Style);   // not dimmed though Home Assistant's feed is down
        Assert.Equal(HomeChipAction.Detail, three.Action);
        Assert.Equal(HomeBarText.ShoppingNote(3), three.Note);

        var none = HomeChips.Build(HomeBarItems.Shopping, Entities(), null, HomeChipMark.None, stale: false, shoppingOpen: 0);
        Assert.Equal("🛒 0", HomeBar.ChipText(none, HomeBar.Fit.Full));
        Assert.Equal(HomeChips.Off, none.Style);

        var off = HomeChips.Build(HomeBarItems.Shopping, Entities(), null, HomeChipMark.None, stale: false);
        Assert.Equal("🛒 ?", HomeBar.ChipText(off, HomeBar.Fit.Full));
        Assert.Equal(Theme.DimText, off.Style);

        var aliases = new Dictionary<string, string> { [HomeBarItems.Shopping] = "Cart" };
        var glyphs = new Dictionary<string, string> { [HomeBarItems.Shopping] = "🧺" };
        Assert.Equal("🧺 2", HomeBar.ChipText(HomeChips.Build(HomeBarItems.Shopping, Entities(), null, HomeChipMark.None, false, aliases, glyphs, 2), HomeBar.Fit.Full));
    }
}
