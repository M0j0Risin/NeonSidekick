using NeonSidekick.App;
using NeonSidekick.Perf;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;
using Spectre.Console.Testing;

namespace NeonSidekick.Tests;

/// <summary>The performance bar on the screen (2026-09-29): the setting draws it under the toolbar, the sampler reads the machine only while it shows; since 2026-09-30 the checked meters alone.</summary>
public partial class ChatScreenTests
{
    [Fact]
    public async Task ShowPerformanceBar_DrawsTheCheckedMeters_AsTheLastRow_AndNoneSamplesNothing()
    {
        _console.Profile.Height = 40;
        _console.Profile.Width = 120;
        _geometry = new ScreenGeometry(() => null, () => 100);
        _settings.Update(d => { d.PerformanceBarItems = ["cpu", "gpu", "vram"]; d.PerformanceBarLook = "text"; });   // the numbers look (led is the default since 2026-10-02)
        _perfSource.Next = new PerfSnapshot(34, 62, null, 91);
        var input = Scripted();
        bool shown = false;
        input.OnWait = () =>
        {
            input.OnWait = null;
            _time.Advance(ScreenPane.Tick);   // the sampler's first reading
            _time.Advance(ScreenPane.Tick);   // the pane's tick draws it
            shown = Output.Contains("CPU  34% · " + Perf.PerfText.VramLabel + "  91%", StringComparison.Ordinal);   // RAM unchecked, GPU unread
            _settings.Update(d => d.PerformanceBarItems = []);
            _time.Advance(ScreenPane.Tick);   // the row goes, the sampler stops
            PushLine(input, "/exit");
        };

        await RunAsync();

        Assert.True(shown);   // no GPU load read: its meter left out
        Assert.True(_perfSource.Disposed);
        int samples = _perfSource.Samples;
        Assert.True(samples >= 1);
        _time.Advance(PerfSampler.Interval * 3);
        Assert.Equal(samples, _perfSource.Samples);
    }

    /// <summary>The bar's groups on the screen (later on 2026-10-13, the user's ask): CPU pinned left starts the row, VRAM pinned right ends it.</summary>
    [Fact]
    public async Task ShowPerformanceBar_DrawsThePinnedGroups_LeftAndRight()
    {
        _console.Profile.Height = 40;
        _console.Profile.Width = 120;
        _geometry = new ScreenGeometry(() => null, () => 100);
        _settings.Update(d => { d.PerformanceBarItems = ["cpu", "ram", "vram"]; d.PerformanceBarLook = "text"; d.PerformanceBarLeft = ["cpu"]; d.PerformanceBarRight = ["vram"]; });
        _perfSource.Next = new PerfSnapshot(34, 62, null, 91);
        var input = Scripted();
        string drawn = "";
        input.OnWait = () =>
        {
            input.OnWait = null;
            _time.Advance(ScreenPane.Tick);   // the sampler's first reading
            _time.Advance(ScreenPane.Tick);   // the pane's tick draws it
            drawn = Output;
            PushLine(input, "/exit");
        };

        await RunAsync();

        string vram = Perf.PerfText.VramLabel;
        string plain = System.Text.RegularExpressions.Regex.Replace(drawn, "\\x1b\\[[0-9;?]*[A-Za-z]", "");
        Assert.Matches("CPU  34% {20,}RAM  62% {20,}" + vram + "  91%", plain);
        Assert.DoesNotContain("34% · RAM", plain);   // groups, not one run
    }

    /// <summary>
    /// <c>/perfbar</c> (later on 2026-09-29, the user's ask; the checklist's since 2026-09-30): bare it shows the bar (CPU, RAM,
    /// GPU and VRAM the first time) and hides it again, keeping its meters for the next show; a look sets it; anything else is
    /// the usage error. Never a turn.
    /// </summary>
    [Fact]
    public async Task Perf_Toggles_KeepingTheMeters_AndALookSetsIt()
    {
        _settings.Update(d => d.TtsOutput = false);
        var shown = new List<string>();
        Action<ScriptedInput> Then(string line) => input =>
        {
            shown.Add(PerfBarItems.Value(_settings.Current.PerformanceBarItems, _settings.Current.PerformanceBarLook));   // what the line before left
            PushLine(input, line);
        };
        StepsWhenIdle(
            Line("/perfbar"),
            Then("/perfbar gauge"),
            Then("/perfbar"),
            Then("/perfbar"),
            Then("/perfbar bars"),
            Line("/exit"));

        string output = await RunAsync();

        Assert.Equal(["CPU, RAM, GPU, " + Perf.PerfText.VramLabel + " · led", "CPU, RAM, GPU, " + Perf.PerfText.VramLabel + " · gauge", "off", "CPU, RAM, GPU, " + Perf.PerfText.VramLabel + " · gauge"], shown);
        Assert.Contains(PerfText.BarNotice("led"), output);   // the default look, led since 2026-10-02
        Assert.Contains(PerfText.BarNotice("off"), output);
        Assert.Contains(PerfText.BarNotice("gauge"), output);
        Assert.Contains(PerfText.UsageError, output);
        Assert.Equal("gauge", _settings.Current.PerformanceBarLook);
        Assert.Equal(["cpu", "ram", "gpu", "vram"], _settings.Current.PerformanceBarItems);
        Assert.Empty(_chat.Requests);
    }

    /// <summary>
    /// <c>/toolbar edit</c> and <c>/perfbar edit</c> (2026-10-12, the user's ask: "just like there is a /homebar edit path"): each
    /// opens its row's checklist on the pane, N unchecks every item there and saves at once, and ESC closes it — no turn. Under a
    /// reply the edit waits on the watcher as a pane does, while the bare word and the others stay quick.
    /// </summary>
    [Fact]
    public async Task ToolbarEdit_AndPerfbarEdit_OpenTheirChecklists()
    {
        _settings.Update(d => { d.TtsOutput = false; d.ToolbarItems = ["tools", "usage"]; d.PerformanceBarItems = ["cpu", "ram"]; });
        _console.Profile.Width = 120;
        _console.Profile.Height = 60;
        _geometry = new ScreenGeometry(() => null);
        PushLine("/toolbar edit");
        _console.Input.PushKey(Keys.Char('n'));   // none
        _console.Input.PushKey(Keys.Escape);
        PushLine("/perfbar EDIT");
        _console.Input.PushKey(Keys.Char('n'));
        _console.Input.PushKey(Keys.Escape);
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(SettingsMenu.ToolbarToggleKeys, output);
        Assert.Contains(" › " + SettingsMenu.FieldName(SettingsField.ToolbarItems) + " ", output);
        Assert.Contains(" › " + SettingsMenu.FieldName(SettingsField.ShowPerformanceBar) + " ", output);
        Assert.Empty(ToolbarItems.Resolve(_settings.Current.ToolbarItems));
        Assert.Empty(PerfBarItems.Resolve(_settings.Current.PerformanceBarItems));
        Assert.DoesNotContain(ToolbarItems.UsageError, output);
        Assert.DoesNotContain(PerfText.UsageError, output);
        Assert.Empty(_chat.Requests);

        Assert.Equal(MidTurnClass.Pane, ChatScreen.MidTurnPolicy(SlashCommand.Toolbar, "edit"));
        Assert.Equal(MidTurnClass.Pane, ChatScreen.MidTurnPolicy(SlashCommand.Perf, " Edit "));
        Assert.Equal(MidTurnClass.Quick, ChatScreen.MidTurnPolicy(SlashCommand.Toolbar, ""));
        Assert.Equal(MidTurnClass.Quick, ChatScreen.MidTurnPolicy(SlashCommand.Perf, "gauge"));
        Assert.Equal("choose the toolbar's items and their order", ToolbarItems.DescribeWord(ToolbarItems.EditWord));
        Assert.Equal("choose the bar's meters and their order", PerfBarMode.Describe(PerfBarMode.EditWord));
    }

    /// <summary>
    /// <c>/toolbar</c> (later on 2026-09-30, the user's ask: "same as how /perf works for the perfbar"): bare it hides the toolbar
    /// and shows it again with the items it had; <c>off</c> and <c>on</c> say which; Ctrl+T (Ctrl+Alt+B until later still on 2026-10-01) is the bare word; anything
    /// else is the usage error. Never a turn.
    /// </summary>
    [Fact]
    public async Task Tb_Toggles_KeepingTheItems_AndCtrlT_IsTheBareWord()
    {
        _settings.Update(d => { d.TtsOutput = false; d.ToolbarItems = ["tools", "usage"]; });
        var shown = new List<string>();
        Action<ScriptedInput> Then(Action<ScriptedInput> step) => input =>
        {
            shown.Add(ToolbarItems.Value(_settings.Current.ToolbarItems));   // what the step before left
            step(input);
        };
        StepsWhenIdle(
            Line("/toolbar"),
            Then(input => PushLine(input, "/toolbar")),
            Then(input => PushLine(input, "/toolbar off")),
            Then(input => input.Push(Keys.CtrlT)),
            Then(input => PushLine(input, "/toolbar on")),
            Then(input => PushLine(input, "/toolbar sideways")),
            Line("/exit"));

        string output = await RunAsync();

        Assert.Equal(["off", "2 of 47", "off", "2 of 47", "2 of 47"], shown);   // 41 since 📈 went (2026-10-14); 38 until ✴️ went (2026-10-10), 38 again with 🛒 (2026-10-11), 39 with Model name (2026-10-12)
        Assert.Contains(ToolbarItems.Notice(false), output);
        Assert.Contains(ToolbarItems.Notice(true), output);
        Assert.Contains(ToolbarItems.UsageError, output);
        Assert.Equal(["tools", "usage"], _settings.Current.ToolbarItems);
        Assert.Equal(["tools", "usage"], _settings.Current.ToolbarLastItems);
        Assert.Empty(_chat.Requests);
    }

    /// <summary>
    /// The toolbar's 🪪 (later on 2026-09-29, the user's ask), checked alone: the ID card at column 0 opens the profile picker, no
    /// transcript row. The rising chart beside it, the performance bar shown or hidden, was retired on 2026-10-14 (the user's ask);
    /// /perfbar and Ctrl+F stay.
    /// </summary>
    [Fact]
    public async Task ToolbarItems_TheIdCard_OpensTheProfilePicker()
    {
        _settings.Update(d => { d.TtsOutput = false; d.ToolbarItems = ["profile"]; });
        _console.Profile.Height = 40;
        _console.Profile.Width = 240;
        _geometry = new ScreenGeometry(() => null, () => 100);   // the toolbar at 103
        StepsWhenIdle(
            input => { input.PushClick(0, 103); input.PushClick(1, 103); },   // 🪪: the profile picker
            Key(Keys.Escape),
            Line("/exit"));

        string output = await RunAsync();

        Assert.Contains("\n" + ScreenPane.ToolbarRow(ChatScreen.ProfileToolGlyph, "", 239), output);
        Assert.Contains(Titled(SettingsMenu.ProfileTitle), output);
        Assert.DoesNotContain("› /profile", output);
        Assert.Empty(_chat.Requests);
    }
}
