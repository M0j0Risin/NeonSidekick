using NeonSidekick.App;
using NeonSidekick.HomeAssistant;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Monitors;
using NeonSidekick.Plans;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// The monitors on the screen (2026-10-23, the monitors plan): <c>/monitor</c> with and without the pane, the monitors pane's tabs, rows,
/// captions and footers, a switch and a layout picked there, the last screen's question, the 🖥️ chip on the home bar, and the model's tools —
/// all over <see cref="FakeMonitorSystem"/> (this PC's LG ULTRAWIDE and XG27UQDMS), the board quick (no settle).
/// </summary>
public partial class ChatScreenTests
{
    /// <summary>The fake monitors the chat screen gets; null (the default) is a screen with no monitor system.</summary>
    private FakeMonitorSystem? _monitorSystem;

    private FakeMonitorSystem MonitorsFixture(bool pane)
    {
        _monitorSystem = new FakeMonitorSystem();
        _settings.Update(d => { d.TtsOutput = false; d.ToolbarItems = []; d.PerformanceBarItems = []; });
        if (pane)
        {
            _console.Profile.Height = 40;
            _console.Profile.Width = 120;
            _geometry = new ScreenGeometry(() => null);
        }
        else
        {
            _console.Profile.Width = 240;   // the list's lines and the questions unwrapped
        }

        return _monitorSystem;
    }

    /// <summary>
    /// <see cref="StepsWhenIdle"/> with a gate: each step waits for its condition (a monitor's reads landing on the pane) before it runs, so a
    /// key never lands on a tab still reading.
    /// </summary>
    private void GatedSteps(params (Func<bool> Ready, Action<ScriptedInput> Act)[] steps)
    {
        var input = Scripted();
        int next = 0;
        input.OnWait = () =>
        {
            if (next < steps.Length && _speech.Playing is null && _keys is not { PendingLine.IsCompleted: false } && steps[next].Ready())
            {
                steps[next++].Act(input);
            }
        };
    }

    private static (Func<bool>, Action<ScriptedInput>) Now(Action<ScriptedInput> act) => (() => true, act);

    private (Func<bool>, Action<ScriptedInput>) Shown(string text, Action<ScriptedInput> act) => (() => Output.Contains(text, StringComparison.Ordinal), act);

    private static void Until(Func<bool> done) => SpinWait.SpinUntil(done, TimeSpan.FromSeconds(10));

    [Fact]
    public async Task Monitor_WithoutThePane_BarePrintsTheList_AChangeIsTyped_AndTheListShowsTheMark()
    {
        var fake = MonitorsFixture(pane: false);
        PushLine("/monitor");
        PushLine("/monitor 2 input hdmi-1");
        PushLine("/monitor list");
        PushLine("/monitor 3");
        PushLine("/monitor sideways now");
        PushLine("/monitor 2 input");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains("Monitors: extended", output);
        Assert.Contains("1 LG ULTRAWIDE · DisplayPort · 3440×1440 at 159.96 Hz · primary · input DisplayPort-1", output);
        Assert.Contains(MonitorText.Switched("2 XG27UQDMS", "HDMI-1"), output);
        Assert.Equal(["input A:4355 11"], fake.Changes);
        Assert.Contains("Monitors: extended" + MonitorText.ElsewhereTail(["2 XG27UQDMS"], " · "), output);   // later on 2026-10-23
        Assert.Contains("input HDMI-1 · not this PC's (the app switched it from DisplayPort-1)", output);
        Assert.Contains(MonitorText.NoSuchMonitor("3", [1, 2]), output);
        Assert.Contains(MonitorText.Usage, output);
        Assert.Contains(MonitorText.InputUsage("XG27UQDMS", ["DisplayPort-1", "HDMI-1", "HDMI-2"]), output);
        Assert.Empty(_chat.Requests);
    }

    [Fact]
    public async Task Monitor_AnotherInputByItsButtons_TheListSaysSo_AndPcTypedSetsAndClears()
    {
        // The user's report (later on 2026-10-23): the XG27 on HDMI-1 by its own buttons read "extended" and nothing more.
        var fake = MonitorsFixture(pane: false);
        fake.Of(FakeMonitorSystem.Xg).Input = 0x11;
        PushLine("/monitor list");
        PushLine("/monitor 2 pc displayport-1");
        PushLine("/monitor list");
        PushLine("/monitor 2 pc");
        PushLine("/monitor 2 pc now");
        PushLine("/monitor 2 pc auto");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains("Monitors: extended" + MonitorText.ElsewhereTail(["2 XG27UQDMS"], " · "), output);
        Assert.Contains("input HDMI-1 · not this PC's (guessed from the DisplayPort cable)", output);
        Assert.Contains(MonitorText.PcSet("2 XG27UQDMS", "DisplayPort-1"), output);
        Assert.Contains("input HDMI-1 · not this PC's (DisplayPort-1)", output);
        Assert.Contains(MonitorText.PcNow("2 XG27UQDMS", "DisplayPort-1", null, "DisplayPort"), output);
        Assert.Contains(MonitorText.PcSet("2 XG27UQDMS", "HDMI-1 (0x11)"), output);   // now: the input it shows, read afresh
        Assert.Contains(MonitorText.PcCleared("2 XG27UQDMS", "DisplayPort-1", "DisplayPort"), output);
        Assert.Null(_settings.Current.MonitorPcInputs);
        Assert.Empty(fake.Changes);   // a setting: nothing switched
        Assert.Empty(_chat.Requests);
    }

    [Fact]
    public async Task MonitorsPane_ThisPcsInput_ThePickList_ReadsAfresh_SavesAndTags()
    {
        var fake = MonitorsFixture(pane: true);
        GatedSteps(
            Now(Line("/monitor 2")),
            Shown("  HDMI-2", input => input.Push(Keys.End, Keys.Up, Keys.Up, Keys.Enter)),   // This PC's input… (above Enter a code…, Input names…)
            Shown(MonitorText.PcTitle("2 XG27UQDMS"), input =>
            {
                fake.Of(FakeMonitorSystem.Xg).Input = 0x11;   // switched by its buttons after the pane read it
                input.Push(Keys.Home, Keys.Enter);           // the input it shows now
            }),
            Shown(MonitorText.PcSet("2 XG27UQDMS", "HDMI-1 (0x11)"), input => input.Push(Keys.Escape)),
            Now(Line("/exit")));

        string output = await RunAsync();

        Assert.Contains(MonitorText.PcRow(null, "DisplayPort-1"), output);   // before: the cable's guess
        Assert.Contains(MonitorText.PcNowRow("DisplayPort-1"), output);      // the label the pane had read
        Assert.Contains(MonitorText.PcGuessRow, output);
        Assert.Equal(["ausaa34-uid4355/11"], _settings.Current.MonitorPcInputs);   // the fresh read, not the label's
        Assert.Contains(MonitorText.PcRow("HDMI-1", null), output);           // back on the tab: the pick
        Assert.Contains("HDMI-1  " + MonitorText.ThisPcTag, output);
        Assert.Empty(fake.Changes);
    }

    [Fact]
    public async Task Monitor_TheLastScreen_AsksFirst_NoLeavesIt_YesSwitches()
    {
        var fake = MonitorsFixture(pane: false);
        PushLine("/monitor 1 input hdmi-1");      // the other monitor still shows this PC: unasked
        PushLine("/monitor 2 input hdmi-2");      // the last one: asked, No
        _console.Input.PushKey(Keys.Enter);
        PushLine("/monitor 2 input hdmi-2");      // again, Yes
        PickYes();
        PushLine("/exit");

        string output = await RunAsync();

        string question = MonitorText.LastScreenQuestion("2 XG27UQDMS", "HDMI-2");
        Assert.True(CountOf(output, question.Split('\n')[0]) >= 2, output);   // asked both times (the prompt redraws on ↓)
        Assert.Contains(MonitorText.Reason(question), output);
        Assert.Equal(1, CountOf(output, MonitorText.Declined));
        Assert.Equal(["input A:4353 11", "input A:4355 12"], fake.Changes);
        Assert.Contains(MonitorText.Switched("2 XG27UQDMS", "HDMI-2"), output);
    }

    [Fact]
    public async Task Monitor_Layouts_Typed_AndAMonitorOffTheDesktopIsRefusedAnInput()
    {
        var fake = MonitorsFixture(pane: false);
        PushLine("/monitor extend");
        PushLine("/monitor xg only");
        PushLine("/monitor \"LG ULTRAWIDE\" input hdmi-1");
        PushLine("/monitor extend");
        PushLine("/monitor 2 primary");
        PushLine("/monitor duplicate");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(MonitorText.AlreadyArranged("extended"), output);
        Assert.Contains(MonitorText.OnlyNow("1 XG27UQDMS"), output);
        Assert.Contains(MonitorText.OffDesktop, output);
        Assert.Contains(MonitorText.Arranged("extended"), output);
        Assert.Contains(MonitorText.MadePrimary("2 XG27UQDMS"), output);
        Assert.Contains(MonitorText.Arranged("duplicated"), output);
        Assert.Equal(["layout Only A:4355", "layout Extend", "layout Primary A:4355", "layout Duplicate"], fake.Changes);
    }

    /// <summary><c>/monitor list</c> typed during a reply prints at once, the reply running on (a Quick command).</summary>
    [Fact]
    public async Task MidTurn_MonitorList_PrintsAtOnce_TheReplyRunsOn()
    {
        var fake = MonitorsFixture(pane: false);
        MidTurnFixture(i =>
        {
            if (i == 0)
            {
                PushLine("/monitor list");
            }
        });

        string output = await RunAsync();

        Assert.Contains("Monitors: extended", output);
        Assert.Contains("2 XG27UQDMS · DisplayPort", output);
        Assert.DoesNotContain(ChatScreen.CancelledNotice, output);
        Assert.Single(_chat.Requests);
        Assert.Empty(fake.Changes);
    }

    [Fact]
    public async Task Monitor_NoMonitorSystem_SaysSo()
    {
        PushLine("/monitor list");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(MonitorText.NoneHere, output);
    }

    [Fact]
    public async Task MonitorsPane_AMonitorsTab_ItsInputsFillIn_EnterSwitches()
    {
        var fake = MonitorsFixture(pane: true);
        GatedSteps(
            Now(Line("/monitor 2")),
            Shown("  HDMI-2", input => input.Push(Keys.Down, Keys.Enter)),   // ● DisplayPort-1, then HDMI-1
            Now(input =>
            {
                Until(() => fake.Changes.Count > 0);
                PushLine(input, "/exit");
            }));

        string output = await RunAsync();

        Assert.Contains("● DisplayPort-1", output);
        Assert.Contains(MonitorText.ActionsHeading, output);
        Assert.Contains(MonitorText.OnlyThisRow, output);
        Assert.Contains(MonitorText.PrimaryThisRow, output);
        Assert.Contains(MonitorText.EnterCodeRow, output);
        Assert.Contains(MonitorText.InputNamesRow, output);
        Assert.Contains(MonitorText.InputFooter(0x0F, away: false), output);   // the cursor's row's footer: its code
        Assert.Equal(["input A:4355 11"], fake.Changes);
        Until(() => Output.Contains(MonitorText.Switched("2 XG27UQDMS", "HDMI-1"), StringComparison.Ordinal));
        Assert.Contains(MonitorText.Switched("2 XG27UQDMS", "HDMI-1"), Output);   // flashed on the hint row
    }

    [Fact]
    public async Task MonitorsPane_TheLayoutTab_LeavesOutWhatChangesNothing_AndARowApplies()
    {
        var fake = MonitorsFixture(pane: true);
        GatedSteps(
            Now(Line("/monitor")),
            Shown(MonitorText.LayoutNow(MonitorArrangement.Extended), input => input.Push(Keys.Down, Keys.Down, Keys.Enter)),   // Duplicate, Only 1, Only 2
            Now(input =>
            {
                Until(() => fake.Changes.Count > 0);   // the change runs in the background once the pane closes
                PushLine(input, "/exit");
            }));

        string output = await RunAsync();

        Assert.Contains(MonitorText.DuplicateRow, output);
        Assert.Contains(MonitorText.OnlyRow("1 LG ULTRAWIDE"), output);
        Assert.Contains(MonitorText.PrimaryRow("2 XG27UQDMS"), output);
        Assert.DoesNotContain(MonitorText.PrimaryRow("1 LG ULTRAWIDE"), output);   // already primary
        Assert.DoesNotContain(MonitorText.ExtendRow, output);   // already extended
        Assert.Contains(MonitorText.DuplicateFooter, output);
        Assert.Equal(["layout Only A:4355"], fake.Changes);
    }

    [Fact]
    public async Task MonitorsPane_OffTheDesktop_NotReported_AndEnterACode()
    {
        var fake = MonitorsFixture(pane: true);
        fake.Of(FakeMonitorSystem.Xg).Caps = "(prot(monitor)vcp(02 04))";   // no 60(…): the common four
        fake.Screens.Add(new FakeMonitorSystem.Display { Target = "A:4357", Name = "DELL P2419H", Active = false, DevicePath = @"\\?\DISPLAY#DELA0B1#5&1&UID4357#{x}", Mode = new MonitorMode(1920, 1080, 60, 0, 0) });
        GatedSteps(
            Now(Line("/monitor 2")),
            Shown("  DisplayPort-2", input => input.Push(Keys.End, Keys.Up, Keys.Enter)),   // Enter a code…
            Shown(MonitorText.EnterCodePrompt("2 XG27UQDMS"), input => PushLine(input, "1b")),
            Now(input =>
            {
                Until(() => fake.Changes.Count > 0);
                PushLine(input, "/monitor 3");
            }),
            Shown(MonitorText.OffDesktop, input => input.Push(Keys.Escape)),
            Line("/exit").ToGate());

        string output = await RunAsync();

        Assert.Contains(MonitorText.NotReported, output);
        Assert.Contains("  DisplayPort-2", output);
        Assert.Equal(["input A:4355 1B"], fake.Changes);
        Assert.Contains(MonitorText.OffDesktop, output);
    }

    [Fact]
    public async Task MonitorsPane_OneMonitorWithoutDdc_HasNothingToDo()
    {
        var fake = MonitorsFixture(pane: true);
        fake.Screens.RemoveAt(1);
        fake.Of(FakeMonitorSystem.Lg).DdcFail = MonitorText.NoDdc;
        GatedSteps(
            Now(Line("/monitor")),
            Shown(MonitorText.NothingToDo, input => input.Push(Keys.Escape)),
            Now(Line("/exit")));

        string output = await RunAsync();

        Assert.Contains(MonitorText.NothingToDo, output);
        Assert.DoesNotContain(MonitorText.LayoutTab + " ·", output);
        Assert.Empty(fake.Changes);
    }

    [Fact]
    public async Task MonitorsPane_InputNames_RenameRefusesATakenName_AndHides()
    {
        var fake = MonitorsFixture(pane: true);
        GatedSteps(
            Now(Line("/monitor 2")),
            Shown("  HDMI-2", input => input.Push(Keys.End, Keys.Enter)),   // Input names…
            Shown(MonitorText.NamesTitle("2 XG27UQDMS"), input => input.Push(Keys.Enter)),   // rename DisplayPort-1
            Shown(MonitorText.EditKeys, input => PushLine(input, "This PC")),
            Shown(MonitorText.Renamed("DisplayPort-1", "This PC"), input => input.Push(Keys.Down, Keys.Enter)),   // rename HDMI-1
            Now(input => PushLine(input, "hdmi-2")),
            Shown(MonitorText.NameTaken("hdmi-2"), input => input.Push(Keys.Down, Keys.Char('h'))),   // hide HDMI-2
            Shown(MonitorText.HiddenNow("HDMI-2"), input => input.Push(Keys.Escape, Keys.Escape)),
            Now(Line("/exit")));

        await RunAsync();

        Assert.Equal("This PC", _settings.Current.MonitorInputNames!["ausaa34-uid4355/0f"]);
        Assert.Single(_settings.Current.MonitorInputNames!);
        Assert.Equal(["ausaa34-uid4355/12"], _settings.Current.MonitorInputsHidden);
        Assert.Empty(fake.Changes);
    }

    [Fact]
    public async Task HomeBar_TheMonitorsChip_IsTheGlyphAlone_AClickNotes_ARightClickOpensThePane_NothingReadUntilThen()
    {
        var fake = MonitorsFixture(pane: true);
        _settings.Update(d => d.HomeBarItems = [HomeBarItems.Monitors]);
        _geometry = new ScreenGeometry(() => null, () => 100);   // the home bar at 103, the 🖥️ centred
        int queriesAtNote = -1;
        GatedSteps(
            Now(input =>
            {
                TickUntil(() => Output.Contains("  " + HomeChips.MonitorsGlyph, StringComparison.Ordinal));
                input.PushClick(59, 103);
            }),
            (() => Output.Contains(HomeBarText.MonitorsNote, StringComparison.Ordinal), input =>
            {
                queriesAtNote = fake.Count("query");
                input.PushClick(59, 103, MouseButton.Right);
            }),
            Shown(MonitorText.LayoutNow(MonitorArrangement.Extended), input => input.Push(Keys.Escape)),
            Now(Line("/exit")));

        string output = await RunAsync();

        Assert.Equal(0, queriesAtNote);   // drawn and clicked: nothing read
        Assert.Contains(MonitorText.LayoutNow(MonitorArrangement.Extended), output);
        Assert.True(fake.Count("query") > 0);
    }

    [Fact]
    public async Task HomeBar_WithoutAMonitorSystem_LeavesTheItemOut()
    {
        _settings.Update(d => { d.TtsOutput = false; d.HomeBarItems = [HomeBarItems.Monitors, HomeBarItems.Shopping]; d.ToolbarItems = []; d.PerformanceBarItems = []; });
        _console.Profile.Height = 40;
        _console.Profile.Width = 120;
        _geometry = new ScreenGeometry(() => null, () => 100);
        StepsWhenIdle(input =>
        {
            TickUntil(() => Output.Contains("  🛒 0", StringComparison.Ordinal));
            PushLine(input, "/exit");
        });

        string output = await RunAsync();

        Assert.Contains("🛒 0", output);
        // 🖥️ is the LLM line's glyph too, so the bar's own drawing is what is looked for: no 🖥️ chip beside the 🛒 one.
        Assert.DoesNotContain(HomeChips.MonitorsGlyph + "  🛒", output);
        Assert.DoesNotContain(HomeChips.MonitorsGlyph + " 🛒", output);
        Assert.Equal([HomeBarItems.Monitors, HomeBarItems.Shopping], _settings.Current.HomeBarItems);   // kept, unshown
    }

    [Fact]
    public async Task MonitorInput_YesOnThePane_Switches_NoIsTheAnswer_AndTheQuestionNamesTheChange()
    {
        var fake = MonitorsFixture(pane: true);
        _settings.Update(d => d.MonitorTools = true);
        AdminTurnFixture([[Keys.Down, Keys.Enter], [Keys.Enter]], "Done.",
            (MonitorInputTool.ToolName, new() { ["monitor"] = 2, ["input"] = "HDMI-1" }),
            (MonitorLayoutTool.ToolName, new() { ["action"] = "only", ["monitor"] = "XG27" }));

        string output = await RunAsync();

        Assert.Contains("The model asks to switch 2 XG27UQDMS to HDMI-1.", output);
        Assert.Equal(["input A:4355 11"], fake.Changes);
        var results = Results(_chat.Requests[^1]).Select(r => (string)r.Result!).ToList();
        Assert.Equal(MonitorText.Switched("2 XG27UQDMS", "HDMI-1"), results[0]);
        Assert.Equal(MonitorText.ToolDeclined, results[1]);
    }

    [Fact]
    public async Task MonitorTools_Offered_OnlyWithTheSwitchAndThePane_AndPlanModeKeepsTheListAlone()
    {
        MonitorsFixture(pane: true);
        var settings = new AppSettingsData { MonitorTools = true };
        Assert.True(ChatScreen.MonitorOffered(settings, available: true, pane: true));
        Assert.False(ChatScreen.MonitorOffered(settings, available: true, pane: false));
        Assert.False(ChatScreen.MonitorOffered(settings, available: false, pane: true));
        Assert.False(ChatScreen.MonitorOffered(new AppSettingsData(), available: true, pane: true));
        Assert.True(PlanTools.Allowed(MonitorListTool.ToolName));
        Assert.Contains(MonitorInputTool.ToolName, PlanTools.Mutating);
        Assert.Contains(MonitorLayoutTool.ToolName, PlanTools.Mutating);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task MonitorTools_TheirArguments_AndTheListIsTheCommandsLines()
    {
        var fake = new FakeMonitorSystem();
        using var board = new MonitorBoard(fake, quick: true);
        var effective = new AppSettingsData();
        bool? answer = true;
        var tools = ChatScreen.MonitorTools(board, () => effective, (_, _) => Task.FromResult(answer));
        var list = tools.Single(t => t.Name == MonitorListTool.ToolName);
        var input = tools.Single(t => t.Name == MonitorInputTool.ToolName);
        var layout = tools.Single(t => t.Name == MonitorLayoutTool.ToolName);

        string lines = (string)(await list.InvokeAsync(new()))!;
        Assert.Equal(string.Join('\n', MonitorText.ListLines(board.State, MonitorsMenu.WordsFor(effective, board.State.Topology))), lines);

        Assert.Equal(MonitorText.MissingArgument("input"), await input.InvokeAsync(new() { ["monitor"] = 2 }));
        Assert.Equal(MonitorText.MissingArgument("monitor"), await input.InvokeAsync(new() { ["input"] = "hdmi-1" }));
        Assert.Equal(MonitorText.BadAction, await layout.InvokeAsync(new() { ["action"] = "mirror" }));
        Assert.Equal(MonitorText.NeedsMonitor("only"), await layout.InvokeAsync(new() { ["action"] = "only" }));
        Assert.Equal(MonitorText.SwitchedUnlisted("2 XG27UQDMS", "0x1B"), await input.InvokeAsync(new() { ["monitor"] = "2", ["input"] = "0x1b" }));

        answer = null;
        Assert.Equal(MonitorText.NoPaneToAsk, await input.InvokeAsync(new() { ["monitor"] = 2, ["input"] = "hdmi-1" }));
        var noAsk = ChatScreen.MonitorTools(board, () => effective, confirm: null).Single(t => t.Name == MonitorInputTool.ToolName);
        Assert.Equal(MonitorText.NoPaneToAsk, await noAsk.InvokeAsync(new() { ["monitor"] = 2, ["input"] = "hdmi-1" }));
        Assert.Equal(["input A:4355 1B"], fake.Changes);
        Assert.Empty(ChatScreen.MonitorTools(null, () => effective, null));
    }
}

/// <summary>A plain step as a gated one that is always ready.</summary>
internal static class GatedStepExtensions
{
    public static (Func<bool>, Action<ScriptedInput>) ToGate(this Action<ScriptedInput> act) => (() => true, act);
}
