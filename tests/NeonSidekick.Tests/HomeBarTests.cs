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
        // Centred as the meters are (later on 2026-10-12, the user's ask), no ⌂ ahead of them: 22 cells of chips in 80, 29 blanks first.
        var row = HomeBar.Render([Chip("Desk", "80%"), Chip("Hall")], 80)!;

        Assert.Equal(new string(' ', 29) + " 💡 Desk 80%  💡 Hall ", Text(row));
        Assert.Equal(1, row.Pages);
        Assert.Equal(2, row.Spans.Count);
        Assert.Equal(new HomeSpan(29, 13, HomeTarget.Chip, 0), row.Spans[0]);
        Assert.Equal(new HomeSpan(42, 9, HomeTarget.Chip, 1), row.Spans[1]);
        Assert.Null(HomeBar.HitAt(row.Spans, 28));   // the run ahead is a blank: the toolbar's, /settings on a double-click
        Assert.Equal(HomeTarget.Chip, HomeBar.HitAt(row.Spans, 32)!.Value.Target);
        Assert.Equal(0, HomeBar.HitAt(row.Spans, 41)!.Value.Index);   // no gap between the chips (2026-10-12): the first's last blank
        Assert.Equal(1, HomeBar.HitAt(row.Spans, 42)!.Value.Index);
        Assert.Null(HomeBar.Render([], 80));
    }

    /// <summary>The odd cell of the room goes to the right, as the meters' does: 9 cells in 20 is 5 blanks ahead, 6 after.</summary>
    [Fact]
    public void TheChips_AreCentred_TheOddCellAtTheRight()
    {
        var row = HomeBar.Render([Chip("Desk")], 20)!;

        Assert.Equal("      💡 Desk ", Text(row));
        Assert.Equal(new HomeSpan(5, 9, HomeTarget.Chip, 0), row.Spans[0]);
        Assert.Equal(" 💡 Desk ", Text(HomeBar.Render([Chip("Desk")], 9)!));   // a row just wide enough: no run ahead
    }

    [Fact]
    public void Render_ShortensTheNames_ThenDropsThem_ThenPages()
    {
        var chips = new[] { Chip("Living Room Lamp", "50%", "Lamp"), Chip("Kitchen Spot", "", "Spot"), Chip("Hallway Ceiling", "", "Ceiling") };
        Assert.Contains("Living Room Lamp", Text(HomeBar.Render(chips, 80)!), StringComparison.Ordinal);
        Assert.Equal("    💡 Lamp 50%  💡 Spot  💡 Ceiling ", Text(HomeBar.Render(chips, 40)!));
        Assert.Equal("    💡 50%  💡  💡 ", Text(HomeBar.Render(chips, 22)!));

        var paged = HomeBar.Render(chips, 14)!;
        Assert.True(paged.Pages > 1);
        Assert.StartsWith(" ‹  💡 ", Text(paged), StringComparison.Ordinal);   // centred too: 12 cells in 14
        Assert.Equal(new HomeSpan(3, 8, HomeTarget.Chip, 0), paged.Spans.Single(s => s.Target == HomeTarget.Chip));
        Assert.EndsWith(" ›", Text(paged), StringComparison.Ordinal);
        Assert.DoesNotContain(paged.Spans, s => s.Target == HomeTarget.Prev);   // the first page: nothing back
        Assert.Contains(paged.Spans, s => s.Target == HomeTarget.Next);
        var last = HomeBar.Render(chips, 14, page: 99)!;
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
        Assert.Equal(" 💡 Desk…", HomeBar.ChipText(Chip("Desk") with { Mark = HomeChipMark.Pending }, HomeBar.Fit.Full));
        Assert.Equal(" 💡 Desk!", HomeBar.ChipText(Chip("Desk") with { Mark = HomeChipMark.Failed }, HomeBar.Fit.Full));
    }

    // ─── keys and the pane ──────────────────────────────────────────────────────

    [Fact]
    public void CtrlW_IsTheHomeBar_InBothSpellings_AndCtrlAltW_IsNothingNow()
    {
        Assert.Equal("/homebar", Keys.ShortcutLine(new ConsoleKeyInfo('\0', ConsoleKey.W, shift: false, alt: false, control: true)));
        Assert.Equal("/homebar", Keys.ShortcutLine(new ConsoleKeyInfo('\x17', ConsoleKey.W, shift: false, alt: false, control: true)));
        Assert.Null(Keys.ShortcutLine(new ConsoleKeyInfo('\x17', ConsoleKey.W, shift: false, alt: true, control: true)));
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
        Assert.Equal("Usage: /homebar [on|off|edit|name <item> = <alias>] — show or hide the home bar, choose what is on it, or rename an item on it", HomeBarText.Usage);
        Assert.Equal("The home bar shows Den PC Monitor as \"PC\"", HomeBarText.Renamed("Den PC Monitor", "PC"));
        Assert.Equal("The home bar shows Den PC Monitor by its own name again", HomeBarText.Unnamed("Den PC Monitor"));
    }

    // ─── aliases ────────────────────────────────────────────────────────────────

    [Fact]
    public void Rename_SetsReplacesAndClears_OnABlankOrTheOwnName()
    {
        var one = HomeBarItems.Rename(null, "entity:Light.Den_PC", " PC ", "Den PC Monitor")!;
        Assert.Equal("PC", one["entity:light.den_pc"]);
        Assert.Equal("PC", HomeBarItems.AliasOf(one, "ENTITY:light.den_pc"));

        var two = HomeBarItems.Rename(one, "area:den", "Office", "Den")!;
        Assert.Equal(2, two.Count);
        Assert.Equal("Screen", HomeBarItems.Rename(two, "entity:light.den_pc", "Screen", "Den PC Monitor")!["entity:light.den_pc"]);

        Assert.Null(HomeBarItems.AliasOf(HomeBarItems.Rename(two, "entity:light.den_pc", "  ", "Den PC Monitor"), "entity:light.den_pc"));
        Assert.Null(HomeBarItems.Rename(one, "entity:light.den_pc", "den pc monitor", "Den PC Monitor"));   // the own name, any case: none left
        Assert.Null(HomeBarItems.AliasOf(null, "entity:light.den_pc"));
        Assert.Equal(one, HomeBarItems.Rename(one, "bogus", "X", "Y"));
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
        Assert.Contains(" PC ", HomeBar.ChipText(chip, HomeBar.Fit.Full), StringComparison.Ordinal);
        Assert.DoesNotContain("Monitor", HomeBar.ChipText(chip, HomeBar.Fit.Full), StringComparison.Ordinal);

        var plain = HomeChips.Build("entity:light.den_pc_monitor", Entities(monitor), Catalog(), HomeChipMark.None, stale: false);
        Assert.Null(plain.Label);
        Assert.Contains("Den PC Monitor", HomeBar.ChipText(plain, HomeBar.Fit.Full), StringComparison.Ordinal);
    }
}
