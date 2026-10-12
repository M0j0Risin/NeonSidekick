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

    [Fact]
    public void Flip_AddsAtTheEnd_OrTakesOut_AndRemoveTakesAnIndex()
    {
        Assert.Equal(["area:den", "entity:light.x"], HomeBarItems.Flip(["area:den"], "entity:Light.X"));
        Assert.Equal(["area:den"], HomeBarItems.Flip(["area:den", "entity:light.x"], "entity:light.x"));
        Assert.Equal(["a"], HomeBarItems.Remove(["a", "b"], 1));
        Assert.Equal(["a", "b"], HomeBarItems.Remove(["a", "b"], 5));
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
    public void ALight_ShowsItsBrightness_OnItsOwnColour_AndToggles()
    {
        var lamp = Entity("light.den_lamp", "on", """{"friendly_name":"Den Lamp","brightness":204,"rgb_color":[255,200,0]}""");
        var chip = HomeChips.Build("entity:light.den_lamp", Entities(lamp), Catalog(), HomeChipMark.None, stale: false);

        Assert.Equal(HomeChips.LightGlyph, chip.Glyph);
        Assert.Equal("Den Lamp", chip.Name);
        Assert.Equal("Lamp", chip.Short);   // the area's name dropped
        Assert.Equal("80%", chip.Value);
        Assert.Equal(new Color(255, 200, 0), chip.Style.Background);
        Assert.Equal(Color.Black, chip.Style.Foreground);   // a light slab reads in black
        Assert.Equal(HomeChipAction.Call, chip.Action);
        Assert.Equal("light.toggle", chip.Call!.Name);
        Assert.Contains("double-click to switch off", chip.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Off_IsTheOffSlab_Unavailable_IsDimWithAQuestion_AndStaleIsDim()
    {
        var off = HomeChips.Build("entity:switch.k", Entities(Entity("switch.k", "off")), null, HomeChipMark.None, false);
        Assert.Equal(Theme.ToolbarOff, off.Style);

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
        Assert.Equal(Theme.ToolbarOff, dark.Style);
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
        Assert.Equal(Theme.Warn, door.Style.Background);
        Assert.Equal(HomeChips.DoorGlyph, door.Glyph);

        var leak = HomeChips.Build("entity:binary_sensor.l", Entities(Entity("binary_sensor.l", "on", """{"device_class":"moisture"}""")), null, HomeChipMark.None, false);
        Assert.Equal("wet", leak.Value);
        Assert.Equal(Theme.Bad, leak.Style.Background);
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

        Assert.Equal(1, TextCells.Width(HomeBarText.StatusGlyph));
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
        Assert.Equal(Color.White, HomeChips.Slab(new Color(20, 20, 60)).Foreground);
    }

    // ─── the row ────────────────────────────────────────────────────────────────

    private static HomeChip Chip(string name, string value = "", string shortName = "") =>
        new("entity:light." + name.ToLowerInvariant(), HomeChips.LightGlyph, name, shortName.Length > 0 ? shortName : name, value, Theme.ToolbarOff, HomeChipAction.Call, null, "");

    private static string Text(HomeRow row) => string.Concat(row.Segments.Select(s => s.Text));

    [Fact]
    public void Render_TheStatusThenTheChips_LeftAligned_WithTheirSpans()
    {
        var row = HomeBar.Render([Chip("Desk", "80%"), Chip("Hall")], HaLiveStatus.Live, 80)!;

        Assert.Equal("⌂  💡 Desk 80%   💡 Hall ", Text(row));
        Assert.Equal(1, row.Pages);
        Assert.Equal(new HomeSpan(0, 1, HomeTarget.Status), row.Spans[0]);
        Assert.Equal(new HomeSpan(2, 13, HomeTarget.Chip, 0), row.Spans[1]);
        Assert.Equal(new HomeSpan(16, 9, HomeTarget.Chip, 1), row.Spans[2]);
        Assert.Equal(HomeTarget.Chip, HomeBar.HitAt(row.Spans, 5)!.Value.Target);
        Assert.Null(HomeBar.HitAt(row.Spans, 15));   // the blank between
        Assert.Equal(1, HomeBar.HitAt(row.Spans, 16)!.Value.Index);
        Assert.Null(HomeBar.Render([], HaLiveStatus.Live, 80));
    }

    [Fact]
    public void Render_ShortensTheNames_ThenDropsThem_ThenPages()
    {
        var chips = new[] { Chip("Living Room Lamp", "50%", "Lamp"), Chip("Kitchen Spot", "", "Spot"), Chip("Hallway Ceiling", "", "Ceiling") };
        Assert.Contains("Living Room Lamp", Text(HomeBar.Render(chips, HaLiveStatus.Live, 80)!), StringComparison.Ordinal);
        Assert.Equal("⌂  💡 Lamp 50%   💡 Spot   💡 Ceiling ", Text(HomeBar.Render(chips, HaLiveStatus.Live, 40)!));
        Assert.Equal("⌂  💡 50%   💡   💡 ", Text(HomeBar.Render(chips, HaLiveStatus.Live, 22)!));

        var paged = HomeBar.Render(chips, HaLiveStatus.Live, 16)!;
        Assert.True(paged.Pages > 1);
        Assert.StartsWith("⌂ ‹ ", Text(paged), StringComparison.Ordinal);
        Assert.EndsWith(" ›", Text(paged), StringComparison.Ordinal);
        Assert.DoesNotContain(paged.Spans, s => s.Target == HomeTarget.Prev);   // the first page: nothing back
        Assert.Contains(paged.Spans, s => s.Target == HomeTarget.Next);
        var last = HomeBar.Render(chips, HaLiveStatus.Live, 16, page: 99)!;
        Assert.Contains(last.Spans, s => s.Target == HomeTarget.Prev);
        Assert.DoesNotContain(last.Spans, s => s.Target == HomeTarget.Next);
    }

    [Fact]
    public void AChipWiderThanAPage_IsCut_AndTheRowNeverOverflows()
    {
        // The review's finding (2026-10-12): a playing title on a narrow window was a page alone, uncut, and wrapped the row.
        var wide = Chip("Tv", "▸ A very long title indeed") with { Short = "Tv" };
        foreach (int cells in new[] { 30, 20, 12, 8, 3 })
        {
            var row = HomeBar.Render([wide, Chip("Desk")], HaLiveStatus.Live, cells)!;
            Assert.True(TextCells.Width(Text(row)) <= cells, cells + ": " + Text(row));
        }

        Assert.Equal("⌂ ", Text(HomeBar.Render([wide], HaLiveStatus.Live, 6)!));   // no room for a page: the glyph alone
    }

    [Fact]
    public void TheKey_MovesWithAStyle_EvenWhenNoTextDoes()
    {
        var red = Chip("Desk") with { Style = new Style(Color.White, new Color(255, 0, 0)) };
        var blue = red with { Style = new Style(Color.White, new Color(0, 0, 255)) };
        var a = HomeBar.Render([red], HaLiveStatus.Live, 40)!;
        var b = HomeBar.Render([blue], HaLiveStatus.Live, 40)!;
        Assert.Equal(Text(a), Text(b));
        Assert.NotEqual(a.Key, b.Key);
    }

    [Fact]
    public void AChipsMark_FollowsIt()
    {
        Assert.Equal(" 💡 Desk…", HomeBar.ChipText(Chip("Desk") with { Mark = HomeChipMark.Pending }, HomeBar.Fit.Full));
        Assert.Equal(" 💡 Desk!", HomeBar.ChipText(Chip("Desk") with { Mark = HomeChipMark.Failed }, HomeBar.Fit.Full));
    }

    [Fact]
    public void TheStatusGlyph_TakesTheFeedsColour()
    {
        Assert.Equal(Theme.Accent, HomeBar.StatusStyle(HaLiveStatus.Live));
        Assert.Equal(Theme.Warn, HomeBar.StatusStyle(HaLiveStatus.Retrying).Foreground);
        Assert.Equal(Theme.Bad, HomeBar.StatusStyle(HaLiveStatus.Unauthorized).Foreground);
    }

    // ─── keys and the pane ──────────────────────────────────────────────────────

    [Fact]
    public void CtrlAltW_IsTheHomeBar_InBothSpellings()
    {
        Assert.Equal("/homebar", Keys.ShortcutLine(new ConsoleKeyInfo('\0', ConsoleKey.W, shift: false, alt: true, control: true)));
        Assert.Equal("/homebar", Keys.ShortcutLine(new ConsoleKeyInfo('\x17', ConsoleKey.W, shift: false, alt: true, control: true)));
        Assert.Null(Keys.ShortcutLine(new ConsoleKeyInfo('\x17', ConsoleKey.W, shift: false, alt: false, control: true)));
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
        Assert.Equal("Usage: /homebar [on|off|edit] — show or hide the home bar, or choose what is on it", HomeBarText.Usage);
    }
}
