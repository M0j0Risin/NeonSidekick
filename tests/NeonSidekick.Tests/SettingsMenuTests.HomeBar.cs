using NeonSidekick.App;
using NeonSidekick.HomeAssistant;
using NeonSidekick.Settings;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// The home bar's picker (2026-10-12, <c>/settings › Client › Chat › Show home bar</c>): its tabs, adding from Rooms and Lights,
/// the bar's order changed with the move keys and Delete, and the notice without Home Assistant.
/// </summary>
public partial class SettingsMenuTests
{
    private static HaCatalog HomeCatalog() => new(
        [new HaFloor("ground", "Ground", 0)],
        [new HaAreaEntry("den", "Den", "ground")],
        [],
        [
            new HaRegistryEntity("light.den_lamp", null, "den", null, false, false, null, []),
            new HaRegistryEntity("switch.kettle", null, null, null, false, false, null, []),
        ],
        [],
        [
            new HaEntity("light.den_lamp", "Den Lamp", "on", null, default, null),
            new HaEntity("switch.kettle", "Kettle", "off", null, default, null),
        ]);

    [Fact]
    public void ShowHomeBar_IsTheChatTabsRowAfterThePerformanceBar_OffByDefault()
    {
        var chat = SettingsMenu.TabFields[(int)SettingsTab.Chat].ToList();
        Assert.Equal(chat.IndexOf(SettingsField.ShowPerformanceBar) + 1, chat.IndexOf(SettingsField.ShowHomeBar));
        Assert.Equal("Show home bar", SettingsMenu.FieldName(SettingsField.ShowHomeBar));
        Assert.Equal("off", SettingsMenu.FieldValue(SettingsField.ShowHomeBar, new AppSettingsData(), "C:\\p"));
        Assert.Equal("2 · den, kettle", SettingsMenu.FieldValue(SettingsField.ShowHomeBar, new AppSettingsData { HomeBarItems = ["area:den", "entity:switch.kettle"] }, "C:\\p"));
    }

    [Fact]
    public async Task ThePicker_AddsFromRoomsAndLights_ThenTheMoveKeysAndDeleteChangeTheBar()
    {
        _settings.Update(d => { d.HomeAssistantUrl = "http://ha.test:8123"; d.HomeAssistantToken = "secret"; });
        var (menu, pane) = PaneMenu();
        menu.HomeCatalog = _ => Task.FromResult<(HaCatalog?, string?)>((HomeCatalog(), null));
        var shiftUp = new ConsoleKeyInfo('\0', ConsoleKey.UpArrow, shift: true, alt: false, control: false);
        var delete = new ConsoleKeyInfo('\0', ConsoleKey.Delete, shift: false, alt: false, control: false);
        Push(
            Keys.Right, Keys.Char(' '),                // Rooms: Den on
            Keys.Right, Keys.Char(' '),                // Lights: Den Lamp on
            Keys.Left, Keys.Left, Keys.Down, shiftUp,  // On the bar: the lamp up a place
            delete,                                    // and off again
            Keys.Escape);

        Assert.True(await menu.EditHomeBarAsync(own: true, CancellationToken.None));

        Assert.Equal(["area:den"], _settings.Current.HomeBarItems);
        Assert.Contains(HomeBarText.BarTab, Output);
        Assert.Contains(HomeBarText.RoomsTab, Output);
        Assert.Contains(HomeBarText.LightsTab, Output);
        Assert.Contains("[x] Den", Output);
        Assert.Contains(" 1. 💡 Den Lamp", Output);   // after the move, numbered first
        Assert.Contains(HomeBarText.BarHint, Output);
        Assert.False(pane.OverlayOpen);
        pane.Dispose();
    }

    [Fact]
    public async Task ThePicker_ListsZonesOnTheirOwnTab_AfterSecurity_AndAddsOne()
    {
        _settings.Update(d => { d.HomeAssistantUrl = "http://ha.test:8123"; d.HomeAssistantToken = "secret"; });
        var (menu, pane) = PaneMenu();
        var catalog = new HaCatalog([], [], [], [new HaRegistryEntity("zone.home", null, null, null, false, false, null, [])], [], [new HaEntity("zone.home", "Home", "1", null, default, null)]);
        menu.HomeCatalog = _ => Task.FromResult<(HaCatalog?, string?)>((catalog, null));
        var titles = SettingsMenu.HomeDomainTabs.Select(t => t.Title).ToList();
        Assert.Equal(titles.IndexOf(HomeBarText.SafetyTab) + 1, titles.IndexOf(HomeBarText.ZonesTab));
        Assert.DoesNotContain("zone", SettingsMenu.HomeHiddenDomains);

        // On the bar, Rooms, then the kinds up to Zones.
        Push([.. Enumerable.Repeat(Keys.Right, titles.IndexOf(HomeBarText.ZonesTab) + 2), Keys.Char(' '), Keys.Escape]);

        Assert.True(await menu.EditHomeBarAsync(own: true, CancellationToken.None));

        Assert.Equal(["entity:zone.home"], _settings.Current.HomeBarItems);
        Assert.Contains(HomeBarText.ZonesTab, Output);
        pane.Dispose();
    }

    [Fact]
    public async Task ThePicker_RenamesAnItemOnTheBar_ShowsTheAlias_AndABlankTakesItOff()
    {
        _settings.Update(d =>
        {
            d.HomeAssistantUrl = "http://ha.test:8123";
            d.HomeAssistantToken = "secret";
            d.HomeBarItems = ["entity:light.den_lamp"];
        });
        var (menu, pane) = PaneMenu();
        menu.HomeCatalog = _ => Task.FromResult<(HaCatalog?, string?)>((HomeCatalog(), null));
        Push(Keys.Enter, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter, Keys.Ctrl(ConsoleKey.A), Keys.Backspace);   // its actions › Rename
        _console.Input.PushText("Lamp");
        Push(Keys.Enter);
        Push(Keys.Right, Keys.Right, Keys.Left, Keys.Left);   // Lights shows it quoted; back on the bar
        Push(Keys.Enter, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter, Keys.Ctrl(ConsoleKey.A), Keys.Backspace, Keys.Enter);   // blank: off
        Push(Keys.Escape);

        Assert.True(await menu.EditHomeBarAsync(own: true, CancellationToken.None));

        Assert.Contains(HomeBarText.RenameItem, Output);
        Assert.Contains(HomeBarText.RenameKeys, Output);
        Assert.Contains(" 1. 💡 Lamp  Den Lamp · Den", Output);
        Assert.Contains("\"Lamp\" · light.den_lamp", Output);
        Assert.Null(_settings.Current.HomeBarAliases);
        pane.Dispose();
    }

    [Fact]
    public async Task ThePicker_WithoutHomeAssistant_SaysWhereToSetItUp()
    {
        var (menu, pane) = PaneMenu();
        menu.HomeCatalog = _ => throw new InvalidOperationException("never read");

        Assert.False(await menu.EditHomeBarAsync(own: true, CancellationToken.None));

        Assert.Contains(HomeBarText.NotConfigured, Output);
        pane.Dispose();
    }
}
