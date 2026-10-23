using NeonSidekick.App;
using NeonSidekick.Emojis;
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
        Assert.Equal("Home bar", SettingsMenu.FieldName(SettingsField.ShowHomeBar));   // "Show home bar" until 2026-10-12, the user's name
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

    /// <summary>
    /// Pinned left, center or right (later on 2026-10-13, the user's ask): the bar's tab shows the three groups under their headings;
    /// Shift+←/→ pin the item a group over, the Pin rows of Enter's page do it for a terminal that keeps the keys, and Delete takes an
    /// item off with its side.
    /// </summary>
    [Fact]
    public async Task ThePicker_PinsItemsLeftCenterOrRight_AndDeleteTakesTheSideToo()
    {
        _settings.Update(d =>
        {
            d.HomeAssistantUrl = "http://ha.test:8123";
            d.HomeAssistantToken = "secret";
            d.HomeBarItems = ["area:den", "entity:light.den_lamp", "entity:switch.kettle"];
        });
        var (menu, pane) = PaneMenu();
        menu.HomeCatalog = _ => Task.FromResult<(HaCatalog?, string?)>((HomeCatalog(), null));
        var shiftLeft = new ConsoleKeyInfo('\0', ConsoleKey.LeftArrow, shift: true, alt: false, control: false);
        var shiftRight = new ConsoleKeyInfo('\0', ConsoleKey.RightArrow, shift: true, alt: false, control: false);
        var delete = new ConsoleKeyInfo('\0', ConsoleKey.Delete, shift: false, alt: false, control: false);
        Push(
            shiftLeft,                                                               // the room left (the cursor opens on it, under the center's heading)
            Keys.Down, shiftRight,                                                   // the lamp right
            Keys.Enter, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter,      // its actions › Pin left: past the center to the left's end
            Keys.Escape);

        Assert.True(await menu.EditHomeBarAsync(own: true, CancellationToken.None));

        Assert.Equal(["area:den", "entity:light.den_lamp", "entity:switch.kettle"], _settings.Current.HomeBarItems);
        Assert.Equal(["area:den", "entity:light.den_lamp"], _settings.Current.HomeBarLeft);
        Assert.Null(_settings.Current.HomeBarRight);
        Assert.Contains(SettingsMenu.PinnedLeftHeading, Output);
        Assert.Contains(SettingsMenu.PinnedCenterHeading, Output);
        Assert.Contains(SettingsMenu.PinnedRightHeading, Output);
        Assert.Contains(SettingsMenu.EmptySideNote(right: false), Output);   // nothing on the left at first
        Assert.Contains(HomeBarText.PinCenterItem, Output);
        Assert.Contains(HomeBarText.PinLeftItem, Output);

        // Delete on the lamp (the left's last, the second row under its heading) takes it off, and its side with it.
        Push(Keys.Down, delete, Keys.Escape);
        Assert.True(await menu.EditHomeBarAsync(own: true, CancellationToken.None));
        Assert.Equal(["area:den", "entity:switch.kettle"], _settings.Current.HomeBarItems);
        Assert.Equal(["area:den"], _settings.Current.HomeBarLeft);
        pane.Dispose();
    }

    /// <summary>Shift+↓ off the center's last item makes it the right group's first (later on 2026-10-13), as on the sided checklists.</summary>
    [Fact]
    public async Task ThePicker_AStepOffTheCentersEnd_PinsTheItemRight()
    {
        _settings.Update(d =>
        {
            d.HomeAssistantUrl = "http://ha.test:8123";
            d.HomeAssistantToken = "secret";
            d.HomeBarItems = ["area:den", "entity:light.den_lamp"];
        });
        var (menu, pane) = PaneMenu();
        menu.HomeCatalog = _ => Task.FromResult<(HaCatalog?, string?)>((HomeCatalog(), null));
        var shiftDown = new ConsoleKeyInfo('\0', ConsoleKey.DownArrow, shift: true, alt: false, control: false);
        Push(Keys.Down, shiftDown, Keys.Escape);

        Assert.True(await menu.EditHomeBarAsync(own: true, CancellationToken.None));

        Assert.Equal(["entity:light.den_lamp"], _settings.Current.HomeBarRight);
        Assert.Null(_settings.Current.HomeBarLeft);
        Assert.Equal(["area:den", "entity:light.den_lamp"], _settings.Current.HomeBarItems);
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

    /// <summary>The bar's tab shows an item's own glyph, read from its state's attributes (2026-10-12: a TV was 🎵 there and 📺 on its chip).</summary>
    [Fact]
    public async Task ThePicker_ShowsAnItemsGlyphFromItsAttributes_AsItsChipDoes()
    {
        _settings.Update(d =>
        {
            d.HomeAssistantUrl = "http://ha.test:8123";
            d.HomeAssistantToken = "secret";
            d.HomeBarItems = ["entity:media_player.bravia"];
        });
        var (menu, pane) = PaneMenu();
        var attributes = System.Text.Json.JsonDocument.Parse("""{"device_class":"tv"}""").RootElement;
        var catalog = new HaCatalog([], [], [], [new HaRegistryEntity("media_player.bravia", null, null, null, false, false, null, [])], [], [new HaEntity("media_player.bravia", "BRAVIA", "on", null, attributes, null)]);
        menu.HomeCatalog = _ => Task.FromResult<(HaCatalog?, string?)>((catalog, null));
        Push(Keys.Escape);

        Assert.False(await menu.EditHomeBarAsync(own: true, CancellationToken.None));   // nothing changed

        Assert.Contains(" 1. " + HomeChips.TvGlyph + " BRAVIA", Output);
        pane.Dispose();
    }

    [Fact]
    public async Task ThePicker_RenamesAnItemOnTheBar_ShowsTheAlias_ABlankIsTheEmojiAlone_AndDefaultNameTakesItOff()
    {
        _settings.Update(d =>
        {
            d.HomeAssistantUrl = "http://ha.test:8123";
            d.HomeAssistantToken = "secret";
            d.HomeBarItems = ["entity:light.den_lamp"];
        });
        var (menu, pane) = PaneMenu();
        menu.HomeCatalog = _ => Task.FromResult<(HaCatalog?, string?)>((HomeCatalog(), null));
        Push(Keys.Enter, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter, Keys.Ctrl(ConsoleKey.A), Keys.Backspace);   // its actions › Rename
        _console.Input.PushText("Lamp");
        Push(Keys.Enter);
        Push(Keys.Right, Keys.Right, Keys.Left, Keys.Left);   // Lights shows it quoted; back on the bar
        Push(Keys.Enter, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter, Keys.Ctrl(ConsoleKey.A), Keys.Backspace, Keys.Enter);   // blank: the emoji alone
        Push(Keys.Enter, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter);   // its actions › Default name
        Push(Keys.Escape);

        Assert.True(await menu.EditHomeBarAsync(own: true, CancellationToken.None));

        Assert.Contains(HomeBarText.RenameItem, Output);
        Assert.Contains(HomeBarText.RenameKeys, Output);
        Assert.Contains(" 1. 💡 Lamp  Den Lamp · Den", Output);
        Assert.Contains("\"Lamp\" · light.den_lamp", Output);
        Assert.Contains(" 1. 💡   " + HomeBarText.EmojiAlone + " · Den Lamp · Den", Output);   // later on 2026-10-12: a blank is the emoji alone
        Assert.Contains(HomeBarText.DefaultNameItem + "  Den Lamp", Output);
        Assert.Null(_settings.Current.HomeBarAliases);
        pane.Dispose();
    }

    [Fact]
    public async Task ThePicker_WithoutHomeAssistant_OpensOnTheBarAndShopping_AndTheCartHasNoRename()
    {
        // Until later on 2026-10-12 it only said where to set Home Assistant up; the 🛒 chip needs none (the user's call).
        _console.Profile.Width = 120;
        var (menu, pane) = PaneMenu();
        menu.HomeCatalog = _ => throw new InvalidOperationException("never read");
        Push(Keys.Right, Keys.Char(' '));           // Shopping: Space puts the cart on the bar
        Push(Keys.Left, Keys.Enter, Keys.Escape);    // its actions, then back
        Push(Keys.Escape);

        Assert.True(await menu.EditHomeBarAsync(own: true, CancellationToken.None));

        Assert.Equal([HomeBarItems.Shopping], _settings.Current.HomeBarItems);
        Assert.Contains(HomeBarText.ShoppingTab, Output);
        Assert.DoesNotContain(HomeBarText.RoomsTab, Output);
        Assert.Contains("[x] " + HomeChips.ShoppingGlyph + " " + HomeBarText.ShoppingName, Output);
        Assert.Contains(" 1. " + HomeChips.ShoppingGlyph + " " + HomeBarText.ShoppingName + "  " + HomeBarText.ShoppingWhere, Output);
        Assert.Contains(HomeBarText.EmojiItem, Output);
        Assert.DoesNotContain(HomeBarText.RenameItem, Output);   // no name to change
        pane.Dispose();
    }

    /// <summary>
    /// The monitors' tab (2026-10-23, the monitors plan): only with the screen's hook, before Shopping, its row's note the monitors connected;
    /// Space puts the 🖥️ on the bar, and its actions page offers the emoji and no rename.
    /// </summary>
    [Fact]
    public async Task ThePicker_TheMonitorsTab_PutsTheItemOnTheBar_AndItHasNoRename()
    {
        _console.Profile.Width = 160;
        var (menu, pane) = PaneMenu();
        menu.HomeCatalog = _ => throw new InvalidOperationException("never read");
        menu.HomeMonitors = _ => Task.FromResult<IReadOnlyList<string>>(["1 LG ULTRAWIDE", "2 XG27UQDMS"]);
        Push(Keys.Right, Keys.Char(' '));           // Monitors: Space puts the 🖥️ on the bar
        Push(Keys.Left, Keys.Enter, Keys.Escape);    // its actions, then back
        Push(Keys.Escape);

        Assert.True(await menu.EditHomeBarAsync(own: true, CancellationToken.None));

        Assert.Equal([HomeBarItems.Monitors], _settings.Current.HomeBarItems);
        string strip = Output.Split('\n').First(l => l.Contains(HomeBarText.MonitorsTab, StringComparison.Ordinal) && l.Contains(HomeBarText.ShoppingTab, StringComparison.Ordinal));
        Assert.True(strip.IndexOf(HomeBarText.MonitorsTab, StringComparison.Ordinal) < strip.IndexOf(HomeBarText.ShoppingTab, StringComparison.Ordinal), strip);
        Assert.Contains("[x] " + HomeChips.MonitorsGlyph + " " + HomeBarText.MonitorsName, Output);
        Assert.Contains("1 LG ULTRAWIDE, 2 XG27UQDMS", Output);
        Assert.Contains(" 1. " + HomeChips.MonitorsGlyph + " " + HomeBarText.MonitorsName + "  " + HomeBarText.MonitorsWhere, Output);
        Assert.Contains(HomeBarText.EmojiItem, Output);
        Assert.DoesNotContain(HomeBarText.RenameItem, Output);
        pane.Dispose();
    }

    /// <summary>Without the hook (no monitor system: a Mac, a test without one) there is no Monitors tab, and a saved 🖥️ is marked not on this PC.</summary>
    [Fact]
    public async Task ThePicker_WithoutAMonitorSystem_HasNoMonitorsTab_AndMarksASavedItem()
    {
        _console.Profile.Width = 160;
        _settings.Update(d => d.HomeBarItems = [HomeBarItems.Monitors]);
        var (menu, pane) = PaneMenu();
        menu.HomeCatalog = _ => throw new InvalidOperationException("never read");
        Push(Keys.Escape);

        await menu.EditHomeBarAsync(own: true, CancellationToken.None);

        Assert.DoesNotContain(" " + HomeBarText.MonitorsTab + " ", Output.Split('\n').First(l => l.Contains(HomeBarText.ShoppingTab, StringComparison.Ordinal)));
        Assert.Contains(HomeBarText.MonitorsNotHere, Output);
        pane.Dispose();
    }

    [Fact]
    public async Task ThePicker_WithHomeAssistant_PutsShoppingLast()
    {
        _console.Profile.Width = 200;
        _settings.Update(d =>
        {
            d.HomeAssistantUrl = "http://ha.test:8123";
            d.HomeAssistantToken = "secret";
        });
        var (menu, pane) = PaneMenu();
        menu.HomeCatalog = _ => Task.FromResult<(HaCatalog?, string?)>((HomeCatalog(), null));
        Push(Keys.Escape);

        await menu.EditHomeBarAsync(own: true, CancellationToken.None);

        string strip = Output.Split('\n').First(l => l.Contains(HomeBarText.RoomsTab, StringComparison.Ordinal));
        Assert.True(strip.IndexOf(HomeBarText.OtherTab, StringComparison.Ordinal) < strip.IndexOf(HomeBarText.ShoppingTab, StringComparison.Ordinal), strip);
        pane.Dispose();
    }

    /// <summary>
    /// Change emoji (later on 2026-10-12, the user's ask): the actions page opens the emoji grid for the item, a pick shows on the bar's
    /// tab with the own glyph dim beside it, Default emoji puts the own one back, and so does the grid's 🚫 (its band says so).
    /// </summary>
    [Fact]
    public async Task ThePicker_ChangesAnItemsEmoji_AndDefaultEmojiOrTheGridsNoneTakesItOff()
    {
        _console.Profile.Width = 160;
        _settings.Update(d =>
        {
            d.HomeAssistantUrl = "http://ha.test:8123";
            d.HomeAssistantToken = "secret";
            d.HomeBarItems = ["entity:light.den_lamp"];
        });
        var (menu, pane) = PaneMenu();
        menu.HomeCatalog = _ => Task.FromResult<(HaCatalog?, string?)>((HomeCatalog(), null));
        Push(Keys.Enter, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter);   // its actions › Change emoji…
        _console.Input.PushText("fox");
        Push(Keys.Enter);                                                                     // 🦊, saved
        Push(Keys.Enter, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter);   // its actions › Default emoji
        Push(Keys.Enter, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter);
        _console.Input.PushText("fox");
        Push(Keys.Enter);                                                                     // 🦊 again
        Push(Keys.Enter, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter);
        _console.Input.PushText("fox");
        Push(Keys.Left, Keys.Enter);                                                          // the grid's 🚫, before the first result
        Push(Keys.Escape);

        Assert.True(await menu.EditHomeBarAsync(own: true, CancellationToken.None));
        string afterPick = Output;

        Assert.Contains(HomeBarText.EmojiItem, afterPick);
        Assert.Contains(HomeBarText.DefaultEmojiItem, afterPick);
        Assert.Contains(" 1. 🦊 Den Lamp", afterPick);
        Assert.Contains(HomeBarText.DefaultGlyphNote(HomeChips.LightGlyph), afterPick);
        Assert.Contains(HomeBarText.DefaultGlyphFooter(HomeChips.LightGlyph).Split("  ", 2)[1], afterPick);   // the cursor on 🚫: its band
        Assert.DoesNotContain(EmojiText.PreviewLine("🦊", "neon"), afterPick);   // not the avatar's
        Assert.DoesNotContain("this profile's avatar", afterPick);
        Assert.Null(_settings.Current.HomeBarGlyphs);
        pane.Dispose();
    }

    [Fact]
    public async Task ThePicker_EscOnTheEmojiGrid_KeepsTheEmoji()
    {
        _settings.Update(d =>
        {
            d.HomeAssistantUrl = "http://ha.test:8123";
            d.HomeAssistantToken = "secret";
            d.HomeBarItems = ["entity:light.den_lamp"];
            d.HomeBarGlyphs = new() { ["entity:light.den_lamp"] = "🦊" };
        });
        var (menu, pane) = PaneMenu();
        menu.HomeCatalog = _ => Task.FromResult<(HaCatalog?, string?)>((HomeCatalog(), null));
        Push(Keys.Enter, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter, Keys.Escape, Keys.Escape);

        Assert.False(await menu.EditHomeBarAsync(own: true, CancellationToken.None));

        Assert.Equal("🦊", _settings.Current.HomeBarGlyphs?["entity:light.den_lamp"]);
        Assert.Contains(" 1. 🦊 Den Lamp", Output);
        pane.Dispose();
    }
}
