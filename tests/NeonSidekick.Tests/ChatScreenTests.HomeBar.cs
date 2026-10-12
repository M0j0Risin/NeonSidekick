using NeonSidekick.App;
using NeonSidekick.HomeAssistant;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// The home bar on the screen (2026-10-12): <c>/homebar</c> and Ctrl+Alt+W hide it and show it again with its items; the bar
/// draws the chips over the live feed (a scripted Home Assistant), a click shows a chip's note, a double-click calls its
/// service over the socket and the chip follows the change; an unlock asks first.
/// </summary>
public partial class ChatScreenTests
{
    private static readonly ConsoleKeyInfo CtrlAltW = new('\x17', ConsoleKey.W, shift: false, alt: true, control: true);

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
    public async Task HomeBar_TogglesKeepingItsItems_AndCtrlAltW_IsTheBareWord()
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
            Then(input => input.Push(CtrlAltW)),
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
        _geometry = new ScreenGeometry(() => null, () => 100);   // the hint row at 102, the home bar at 103
        bool drawn = false;
        bool noted = false;
        var input = Scripted();
        input.OnWait = () =>
        {
            input.OnWait = null;
            TickUntil(() => Output.Contains("⌂  💡 Desk ", StringComparison.Ordinal) && server.Subscribed.Count > 0);
            drawn = Output.Contains("⌂  💡 Desk ", StringComparison.Ordinal);
            input.PushClick(3, 103);
            input.OnWait = () =>
            {
                noted = Output.Contains("Desk: off — double-click to switch on", StringComparison.Ordinal);
                input.OnWait = () =>
                {
                    input.OnWait = null;
                    TickUntil(() => server.StateOf("light.desk") == "on");
                    PushLine(input, "/exit");
                };
                input.PushClick(3, 103);
            };
        };

        await RunAsync();

        Assert.True(drawn);
        Assert.True(noted);
        Assert.Equal("light.toggle", Assert.Single(server.Calls).Service);
        Assert.Equal("on", server.StateOf("light.desk"));
        Assert.Empty(_chat.Requests);
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
            TickUntil(() => Output.Contains("⌂  🔒 Front Door ", StringComparison.Ordinal) && server.Subscribed.Count > 0);
            input.PushClick(3, 103);
            input.PushClick(3, 103);
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
}
