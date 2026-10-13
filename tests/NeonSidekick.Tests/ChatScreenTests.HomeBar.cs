using NeonSidekick.App;
using NeonSidekick.HomeAssistant;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// The home bar on the screen (2026-10-12): <c>/homebar</c> and Ctrl+W hide it and show it again with its items; the bar
/// draws the chips over the live feed (a scripted Home Assistant), a click shows a chip's note, a double-click calls its
/// service over the socket and the chip follows the change; an unlock asks first.
/// </summary>
public partial class ChatScreenTests
{
    private static readonly ConsoleKeyInfo CtrlW = new('\x17', ConsoleKey.W, shift: false, alt: false, control: true);

    private FakeHaServer HomeServer()
    {
        var server = new FakeHaServer()
            .Area("den", "Den")
            .Entity("light.desk", "off", """{"friendly_name":"Desk"}""", area: "den")
            .Entity("lock.front", "locked", """{"friendly_name":"Front Door"}""");
        _haWire = server.Connect;
        _settings.Update(d =>
        {
            d.TtsOutput = false;
            d.HomeAssistantUrl = "http://ha.test:8123";
            d.HomeAssistantToken = "secret";
            d.ToolbarItems = [];
            d.PerformanceBarItems = [];
        });
        return server;
    }

    /// <summary>Waits on the real clock, the screen's ticking on the manual one, until <paramref name="done"/>.</summary>
    private void TickUntil(Func<bool> done)
    {
        var give = DateTime.UtcNow.AddSeconds(10);
        while (!done() && DateTime.UtcNow < give)
        {
            _time.Advance(ScreenPane.Tick);
            Thread.Sleep(10);
        }
    }

    [Fact]
    public async Task HomeBar_TogglesKeepingItsItems_AndCtrlW_IsTheBareWord()
    {
        HomeServer();
        _settings.Update(d => d.HomeBarItems = ["entity:light.desk"]);
        var shown = new List<int>();
        Action<ScriptedInput> Then(Action<ScriptedInput> step) => input =>
        {
            shown.Add(HomeBarItems.Resolve(_settings.Current.HomeBarItems).Count);
            step(input);
        };
        StepsWhenIdle(
            Line("/homebar"),
            Then(input => input.Push(CtrlW)),
            Then(input => PushLine(input, "/homebar off")),
            Then(input => PushLine(input, "/homebar on")),
            Then(input => PushLine(input, "/homebar sideways")),
            Line("/exit"));

        string output = await RunAsync();

        Assert.Equal([0, 1, 0, 1], shown);
        Assert.Contains(HomeBarText.Hidden, output);
        Assert.Contains(HomeBarText.Shown, output);
        Assert.Contains(HomeBarText.Usage, output);
        Assert.Equal(["entity:light.desk"], _settings.Current.HomeBarItems);
        Assert.Empty(_chat.Requests);
    }

    [Fact]
    public async Task HomeBar_UnderTheHintRow_AClickShowsTheNote_ADoubleClickSwitchesTheLight()
    {
        var server = HomeServer();
        _settings.Update(d => d.HomeBarItems = ["entity:light.desk"]);
        _console.Profile.Height = 40;
        _console.Profile.Width = 120;
        _geometry = new ScreenGeometry(() => null, () => 100);   // the hint row at 102, the home bar at 103, its chips centred (2026-10-12)
        bool drawn = false;
        bool noted = false;
        var input = Scripted();
        input.OnWait = () =>
        {
            input.OnWait = null;
            TickUntil(() => Output.Contains("  💡 Desk ", StringComparison.Ordinal) && server.Subscribed.Count > 0);
            drawn = Output.Contains("  💡 Desk ", StringComparison.Ordinal);
            input.PushClick(58, 103);
            input.OnWait = () =>
            {
                noted = Output.Contains("Desk: off — double-click to switch on", StringComparison.Ordinal);
                input.OnWait = () =>
                {
                    input.OnWait = null;
                    TickUntil(() => server.StateOf("light.desk") == "on");
                    PushLine(input, "/exit");
                };
                input.PushClick(58, 103);
            };
        };

        await RunAsync();

        Assert.True(drawn);
        Assert.True(noted);
        Assert.Equal("light.toggle", Assert.Single(server.Calls).Service);
        Assert.Equal("on", server.StateOf("light.desk"));
        Assert.Empty(_chat.Requests);
    }

    /// <summary>
    /// A double-click on the home bar's blank space opens <c>/settings</c> (2026-10-12, the user's ask: as on the other bars; a
    /// spent click until then), and switches nothing.
    /// </summary>
    [Fact]
    public async Task HomeBar_ADoubleClickOnItsBlanks_OpensTheSettings()
    {
        var server = HomeServer();
        _settings.Update(d => d.HomeBarItems = ["entity:light.desk"]);
        _console.Profile.Height = 40;
        _console.Profile.Width = 120;
        _geometry = new ScreenGeometry(() => null, () => 100);   // the hint row at 102, the home bar at 103, its chips centred (2026-10-12)
        int mark = -1;
        bool opened = false;
        var input = Scripted();
        input.OnWait = () =>
        {
            input.OnWait = null;
            TickUntil(() => Output.Contains("  💡 Desk ", StringComparison.Ordinal) && server.Subscribed.Count > 0);
            mark = Output.Length;
            input.PushClick(20, 103);
            input.PushClick(20, 103);
            input.OnWait = () =>
            {
                opened = Output[mark..].Contains(SettingsMenu.Title + " │ Client · Servers · Advisors · Voice", StringComparison.Ordinal);
                input.OnWait = null;
                input.Push(Keys.Escape);
                PushLine(input, "/exit");
            };
        };

        await RunAsync();

        Assert.True(mark >= 0);
        Assert.True(opened);
        Assert.Empty(server.Calls);
        Assert.Equal("off", server.StateOf("light.desk"));
    }

    [Fact]
    public async Task HomeBar_AnUnlock_AsksFirst_AndANoLeavesItLocked()
    {
        var server = HomeServer();
        _settings.Update(d => d.HomeBarItems = ["entity:lock.front"]);
        _console.Profile.Height = 40;
        _console.Profile.Width = 120;
        _geometry = new ScreenGeometry(() => null, () => 100);
        bool asked = false;
        var input = Scripted();
        input.OnWait = () =>
        {
            input.OnWait = null;
            TickUntil(() => Output.Contains("  🔒 Front Door ", StringComparison.Ordinal) && server.Subscribed.Count > 0);
            input.PushClick(58, 103);
            input.PushClick(58, 103);
            input.OnWait = () =>
            {
                asked = Output.Contains("Unlock Front Door?", StringComparison.Ordinal);
                input.OnWait = null;
                input.Push(Keys.Escape);   // no
                PushLine(input, "/exit");
            };
        };

        await RunAsync();

        Assert.True(asked);
        Assert.Empty(server.Calls);
        Assert.Equal("locked", server.StateOf("lock.front"));
    }

    [Fact]
    public async Task HomeBar_TheRoomPane_DrawsAChangeThatComesWhileItWaits_ItsRowAndItsCount()
    {
        // The user's report (2026-10-12): a light switched from its own page still read off back on the room's list, since the
        // call's answer came after the list was drawn. Here the change comes from the server while the list waits, as that
        // answer does (a call's own timing would race the redraw).
        var server = HomeServer();
        _settings.Update(d => d.HomeBarItems = ["area:den"]);
        _console.Profile.Height = 40;
        _console.Profile.Width = 120;
        _geometry = new ScreenGeometry(() => null, () => 100);
        bool before = false;
        bool followed = false;
        var input = Scripted();
        input.OnWait = () =>
        {
            input.OnWait = null;
            TickUntil(() => Output.Contains("  🏠 Den ", StringComparison.Ordinal) && server.Subscribed.Count > 0);
            PushLine(input, "/homebar show den");
            input.OnWait = () =>
            {
                before = Output.Contains("💡 Desk  off", StringComparison.Ordinal) && Output.Contains("Den · 0/1", StringComparison.Ordinal);
                server.Change("light.desk", "on");

                // The clock ticks until the bar has it (the pane's watcher sees it on the same ticks), then the read returns
                // with nothing pushed, so the cancelled wait draws the list again and reads once more.
                TickUntil(() => Output.Contains("  🏠 Den 1/1", StringComparison.Ordinal));
                for (int i = 0; i < 3; i++)
                {
                    _time.Advance(ScreenPane.Tick);
                    Thread.Sleep(10);
                }

                input.OnWait = () =>
                {
                    input.OnWait = null;
                    followed = Output.Contains("💡 Desk  on", StringComparison.Ordinal) && Output.Contains("Den · 1/1", StringComparison.Ordinal);
                    input.Push(Keys.Escape);
                    PushLine(input, "/exit");
                };
            };
        };

        await RunAsync();

        Assert.True(before);
        Assert.True(followed);
    }

    [Fact]
    public async Task HomeBar_Name_LabelsAChip_TakesItOff_AndRefusesWhatIsNotOnTheBar()
    {
        HomeServer();
        _settings.Update(d => d.HomeBarItems = ["entity:light.desk"]);
        string? afterSet = null;
        StepsWhenIdle(
            Line("/homebar name entity:light.desk = D"),
            input =>
            {
                afterSet = _settings.Current.HomeBarAliases?.GetValueOrDefault("entity:light.desk");
                PushLine(input, "/homebar name D =");   // the emoji alone (later on 2026-10-12)
            },
            Line("/homebar name Desk = default"),
            Line("/homebar name Kettle = K"),
            Line("/homebar name Desk"),
            Line("/exit"));

        string output = await RunAsync();

        Assert.Equal("D", afterSet);
        Assert.Null(_settings.Current.HomeBarAliases);
        Assert.Contains("as \"D\"", output);
        Assert.Contains("as its emoji alone", output);
        Assert.Contains("by its own name again", output);
        Assert.Contains(HomeBarText.NotOnBar("Kettle"), output);
        Assert.Contains(HomeBarText.Usage, output);   // no "="
        Assert.Empty(_chat.Requests);
    }

    [Fact]
    public void HomeRoomKey_ChangesWithAMembersState_OrTheCount_NotWithAnotherEntity()
    {
        static HaLiveEntity Light(string id, string state) => new(id, state, new Dictionary<string, System.Text.Json.JsonElement>(), null);
        string[] members = ["light.a", "light.b"];
        var drawn = ChatScreen.HomeRoomKey(members, new Dictionary<string, HaLiveEntity> { ["light.a"] = Light("light.a", "off"), ["light.b"] = Light("light.b", "off") }, "0/2");

        Assert.NotEqual(drawn, ChatScreen.HomeRoomKey(members, new Dictionary<string, HaLiveEntity> { ["light.a"] = Light("light.a", "on"), ["light.b"] = Light("light.b", "off") }, "0/2"));
        Assert.NotEqual(drawn, ChatScreen.HomeRoomKey(members, new Dictionary<string, HaLiveEntity> { ["light.a"] = Light("light.a", "off"), ["light.b"] = Light("light.b", "off") }, "1/2"));
        Assert.Equal(drawn, ChatScreen.HomeRoomKey(members, new Dictionary<string, HaLiveEntity> { ["light.a"] = Light("light.a", "off"), ["light.b"] = Light("light.b", "off"), ["switch.x"] = Light("switch.x", "on") }, "0/2"));
    }

    /// <summary><c>/homebar emoji</c> (later on 2026-10-12, the user's ask): sets an item's emoji, the bar draws it, default or a blank takes it off, a word is refused.</summary>
    [Fact]
    public async Task HomeBar_Emoji_SetsAChipsEmoji_TakesItOff_AndRefusesAWord()
    {
        HomeServer();
        _settings.Update(d => d.HomeBarItems = ["entity:light.desk"]);
        _console.Profile.Height = 40;
        _console.Profile.Width = 120;
        _geometry = new ScreenGeometry(() => null, () => 100);
        string? afterSet = null;
        bool drawn = false;
        StepsWhenIdle(
            Line("/homebar emoji entity:light.desk = 🦊"),
            input =>
            {
                afterSet = _settings.Current.HomeBarGlyphs?.GetValueOrDefault("entity:light.desk");
                TickUntil(() => Output.Contains("  🦊 Desk ", StringComparison.Ordinal));
                drawn = Output.Contains("  🦊 Desk ", StringComparison.Ordinal);
                PushLine(input, "/homebar emoji Desk = default");
            },
            Line("/homebar emoji Desk = 🐙"),
            Line("/homebar emoji Desk ="),
            Line("/homebar emoji Desk = fox"),
            Line("/homebar emoji Kettle = 🦊"),
            Line("/exit"));

        string output = await RunAsync();

        Assert.Equal("🦊", afterSet);
        Assert.True(drawn);   // the bar drew it
        Assert.Null(_settings.Current.HomeBarGlyphs);
        Assert.Contains(" as 🦊", output);
        Assert.Contains(HomeBarText.GlyphSet("Desk", "🐙"), output);   // by its friendly name, which only its chip has
        Assert.Contains(HomeBarText.GlyphReset("Desk", HomeChips.LightGlyph), output);
        Assert.Contains(HomeBarText.NotEmoji("fox"), output);
        Assert.Contains(HomeBarText.NotOnBar("Kettle"), output);
        Assert.Empty(_chat.Requests);
    }

    /// <summary>
    /// The shopping list's 🛒 (later on 2026-10-12, the user's ask): no Home Assistant, the lines to get counted as /shopping changes
    /// them, 🛒 0 still on the bar; a double-click on it opens the /shopping pane, as /homebar show (a right-click's line) does; it has no
    /// name for /homebar name.
    /// </summary>
    [Fact]
    public async Task HomeBar_TheShoppingChip_CountsTheLinesToGet_AndOpensTheList()
    {
        _settings.Update(d => { d.TtsOutput = false; d.HomeBarItems = [HomeBarItems.Shopping]; });
        _console.Profile.Height = 40;
        _console.Profile.Width = 120;
        _geometry = new ScreenGeometry(() => null, () => 100);   // the home bar at 103, " 🛒 0 " centred
        bool one = false;
        bool none = false;
        int mark = -1;
        bool clicked = false;
        StepsWhenIdle(
            Line("/shopping add milk"),
            Line("/shopping add eggs"),
            Line("/shopping tick milk"),
            input =>
            {
                TickUntil(() => Output.Contains(" 🛒 1 ", StringComparison.Ordinal));
                one = Output.Contains(" 🛒 1 ", StringComparison.Ordinal);
                PushLine(input, "/shopping tick eggs");
            },
            input =>
            {
                TickUntil(() => Output.Contains(" 🛒 0 ", StringComparison.Ordinal));
                none = Output.Contains(" 🛒 0 ", StringComparison.Ordinal);
                mark = Output.Length;
                input.PushClick(59, 103);
                input.PushClick(59, 103);
                input.Push(Keys.Escape);
            },
            input =>
            {
                clicked = Output[mark..].Contains(Shopping.ShoppingText.PaneCaption(0, 2), StringComparison.Ordinal);
                PushLine(input, "/homebar show " + HomeBarItems.Shopping);
                input.Push(Keys.Escape);
            },
            Line("/homebar name Shopping list = Cart"),
            Line("/exit"));

        string output = await RunAsync();

        Assert.True(one);
        Assert.True(none);
        Assert.True(clicked);   // the double-click: the list's own pane
        Assert.Contains(Shopping.ShoppingText.PaneCaption(0, 2), output[mark..]);
        Assert.Contains(HomeBarText.Nameless(HomeBarText.ShoppingName), output);
        Assert.Null(_settings.Current.HomeBarAliases);
        Assert.Empty(_chat.Requests);
    }
}
