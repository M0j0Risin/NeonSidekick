using NeonSidekick.App;
using NeonSidekick.Llm;
using NeonSidekick.Monitors;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// <c>/monitor</c> (2026-10-23, the monitors plan): its parse — the verbs, names, quotes, an input with spaces —, the monitor lookup, the
/// completion from the snapshot alone, its mid-turn class, the check flag, the rule the tools bring, and headless's lines.
/// </summary>
public sealed class MonitorCommandTests
{
    [Theory]
    [InlineData("", MonitorVerb.Pane, null, null)]
    [InlineData("list", MonitorVerb.List, null, null)]
    [InlineData(" EXTEND ", MonitorVerb.Extend, null, null)]
    [InlineData("duplicate", MonitorVerb.Duplicate, null, null)]
    [InlineData("2", MonitorVerb.Show, "2", null)]
    [InlineData("xg27", MonitorVerb.Show, "xg27", null)]
    [InlineData("2 only", MonitorVerb.Only, "2", null)]
    [InlineData("2 Primary", MonitorVerb.Primary, "2", null)]
    [InlineData("2 input hdmi-1", MonitorVerb.Input, "2", "hdmi-1")]
    [InlineData("2 input Work laptop", MonitorVerb.Input, "2", "Work laptop")]   // the rest of the line, spaces and all
    [InlineData("2 input", MonitorVerb.Input, "2", null)]
    [InlineData("\"LG ULTRAWIDE\" only", MonitorVerb.Only, "LG ULTRAWIDE", null)]
    [InlineData("\"list\"", MonitorVerb.Show, "list", null)]   // a monitor named like a verb, reached quoted
    [InlineData("2 pc", MonitorVerb.Pc, "2", null)]   // this PC's input (later on 2026-10-23): bare says what is set
    [InlineData("2 PC DisplayPort-1", MonitorVerb.Pc, "2", "DisplayPort-1")]
    [InlineData("2 pc now", MonitorVerb.Pc, "2", "now")]
    [InlineData("2 pc auto", MonitorVerb.Pc, "2", "auto")]
    [InlineData("xg27 pc Work laptop", MonitorVerb.Pc, "xg27", "Work laptop")]   // the rest of the line, as after input
    public void Parse_TheForms(string args, MonitorVerb verb, string? monitor, string? input)
    {
        var parsed = MonitorCommand.Parse(args);
        Assert.NotNull(parsed);
        Assert.Equal(new MonitorArgs(verb, monitor, input), parsed);
    }

    [Theory]
    [InlineData("sideways now")]
    [InlineData("list all")]
    [InlineData("2 only now")]
    [InlineData("\"unclosed only")]
    [InlineData("\"\" only")]
    public void Parse_AnyOtherForm_IsTheUsage(string args) => Assert.Null(MonitorCommand.Parse(args));

    [Fact]
    public void Changes_AreTheFormsThatChangeSomething()
    {
        Assert.True(MonitorCommand.Parse("2 input hdmi-1")!.Changes);
        Assert.True(MonitorCommand.Parse("extend")!.Changes);
        Assert.False(MonitorCommand.Parse("list")!.Changes);
        Assert.False(MonitorCommand.Parse("2")!.Changes);
        Assert.False(MonitorCommand.Parse("2 pc hdmi-1")!.Changes);   // a setting: nothing on the monitor changes
    }

    [Fact]
    public void ThePcWords_ArePinned()
    {
        // Contract words (the command line's): later on 2026-10-23.
        Assert.Equal("pc", MonitorCommand.PcWord);
        Assert.Equal("now", MonitorCommand.NowWord);
        Assert.Equal("auto", MonitorCommand.AutoWord);
        Assert.Contains(MonitorCommand.PcWord, MonitorCommand.MonitorVerbs);
    }

    [Fact]
    public void Find_ByNumber_WholeName_Prefix_AndTheRefusals()
    {
        var fake = new FakeMonitorSystem();
        fake.Screens.Add(new FakeMonitorSystem.Display { Target = "A:9", Name = "LG ULTRAGEAR", Active = false });
        var snapshot = fake.Query();

        Assert.Equal(FakeMonitorSystem.Xg, MonitorCommand.Find(snapshot, "2", out _)!.Target);
        Assert.Equal(FakeMonitorSystem.Xg, MonitorCommand.Find(snapshot, "xg", out _)!.Target);
        Assert.Equal(FakeMonitorSystem.Lg, MonitorCommand.Find(snapshot, "lg ultrawide", out _)!.Target);
        Assert.Null(MonitorCommand.Find(snapshot, "lg", out string? ambiguous));
        Assert.Equal(MonitorText.Ambiguous("lg", ["1 LG ULTRAWIDE", "3 LG ULTRAGEAR"]), ambiguous);
        Assert.Null(MonitorCommand.Find(snapshot, "7", out string? none));
        Assert.Equal(MonitorText.NoSuchMonitor("7", [1, 2, 3]), none);
        Assert.Null(MonitorCommand.Find(snapshot, "dell", out string? named));
        Assert.Equal(MonitorText.NoSuchMonitorName("dell", ["1 LG ULTRAWIDE", "2 XG27UQDMS", "3 LG ULTRAGEAR"]), named);
        Assert.Equal("\"LG ULTRAWIDE\"", MonitorCommand.Spelled(snapshot.Monitors[0]));
        Assert.Equal("XG27UQDMS", MonitorCommand.Spelled(snapshot.Monitors[1]));
    }

    [Fact]
    public async Task Complete_OneWordAtATime_FromTheSnapshot()
    {
        var fake = new FakeMonitorSystem();
        using var board = new MonitorBoard(fake, quick: true);
        await board.OpenAsync(CancellationToken.None);
        int reads = fake.Calls.Count;
        var names = new Dictionary<string, string> { ["ausaa34-uid4355/11"] = "Work laptop" };
        var words = new MonitorWords(names, null, board.State.Topology.Monitors, ["ausaa34-uid4355/0f"]);
        var items = (string typed) => MonitorCommand.Complete(typed, board.State, words);
        IReadOnlyList<string> Texts(string typed) => items(typed).Select(i => i.Text).ToList();

        Assert.Equal(["list", "extend", "duplicate", "1", "\"LG ULTRAWIDE\"", "2", "XG27UQDMS"], Texts(""));
        Assert.Equal(["2 input", "2 pc", "2 only", "2 primary"], Texts("2 "));
        Assert.Equal(["2 input DisplayPort-1", "2 input Work laptop", "2 input HDMI-2"], Texts("2 input "));
        Assert.Equal(["2 input Work laptop"], Texts("2 input W"));
        Assert.Equal(["2 pc DisplayPort-1", "2 pc Work laptop", "2 pc HDMI-2", "2 pc now", "2 pc auto"], Texts("2 pc "));   // later on 2026-10-23
        Assert.Equal(["2 pc Work laptop"], Texts("2 pc W"));
        Assert.EndsWith(" · " + MonitorText.ThisPcTag, items("2 input ").Single(i => i.Text == "2 input DisplayPort-1").Note);   // the picked one
        Assert.DoesNotContain(MonitorText.ThisPcTag, items("2 input ").Single(i => i.Text == "2 input HDMI-2").Note);
        Assert.Equal(["\"LG ULTRAWIDE\" input", "\"LG ULTRAWIDE\" pc", "\"LG ULTRAWIDE\" only", "\"LG ULTRAWIDE\" primary"], Texts("\"LG ULTRAWIDE\" "));
        Assert.Equal(reads, fake.Calls.Count);   // never a read while typing
    }

    [Fact]
    public void MidTurn_TheListAtOnce_ThePaneAndTheChangesOnTheWatcher()
    {
        Assert.Equal(MidTurnClass.Quick, ChatScreen.MidTurnPolicy(SlashCommand.Monitor, "list"));
        Assert.Equal(MidTurnClass.Pane, ChatScreen.MidTurnPolicy(SlashCommand.Monitor, ""));
        Assert.Equal(MidTurnClass.Pane, ChatScreen.MidTurnPolicy(SlashCommand.Monitor, "2"));
        Assert.Equal(MidTurnClass.Pane, ChatScreen.MidTurnPolicy(SlashCommand.Monitor, "2 input hdmi-1"));
        Assert.Equal(SlashCommand.Monitor, SlashCommands.Parse("/monitor 2 only").Command);
    }

    [Fact]
    public void TheCheckFlag()
    {
        var options = SidekickOptions.Parse([SidekickOptions.MonitorCheckFlag]);
        Assert.Equal("--monitor-check", SidekickOptions.MonitorCheckFlag);
        Assert.True(options.MonitorCheck);
        Assert.True(options.IsCheck);
        Assert.Equal("monitor-check", options.Mode);
        Assert.Contains("--monitor-check", SidekickOptions.Usage, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRule_RidesWithTheTools()
    {
        Assert.Contains(Assistant.MonitorRule, Assistant.AdminTail(AdminRules.Monitors, eventLog: false), StringComparison.Ordinal);
        Assert.DoesNotContain(Assistant.MonitorRule, Assistant.AdminTail(AdminRules.Directory, eventLog: false), StringComparison.Ordinal);
        Assert.Equal(
            "Switching a monitor's input away from this PC may take it off this PC's desktop until its own buttons or the other machine bring it back; say so when you switch one.",
            Assistant.MonitorRule);   // the prompt's text, pinned

        using var board = new MonitorBoard(new FakeMonitorSystem(), quick: true);
        var tools = ChatScreen.MonitorTools(board, () => new AppSettingsData(), null);
        var on = ChatScreen.ComposeTurnTools(new TurnToolInputs { Standing = [], Monitors = tools, MonitorsEnabled = true });
        var off = ChatScreen.ComposeTurnTools(new TurnToolInputs { Standing = [], Monitors = tools, MonitorsEnabled = false });
        Assert.Equal(["monitor_list", "monitor_input", "monitor_layout"], on.Tools.Select(t => t.Name));
        Assert.Equal(AdminRules.Monitors, on.Rules.Admin & AdminRules.Monitors);
        Assert.Empty(off.Tools);
        var planned = ChatScreen.ComposeTurnTools(new TurnToolInputs { Standing = [], Monitors = tools, MonitorsEnabled = true, PlanReadOnly = true });
        Assert.Equal(["monitor_list"], planned.Tools.Select(t => t.Name));
    }

    [Fact]
    public async Task Headless_TheListAndAChange_UnaskedAndThePaneFormsNeedTheScreen()
    {
        var fake = new FakeMonitorSystem();
        var actions = new MonitorActions(new MonitorCalls(fake, retryGap: TimeSpan.Zero), settle: TimeSpan.Zero);
        var effective = new AppSettingsData();

        var (list, listFailed) = await SidekickApp.MonitorLinesAsync(actions, effective, "list", _ => { }, CancellationToken.None);
        Assert.False(listFailed);
        Assert.Equal("Monitors: extended", list[0]);
        Assert.StartsWith("2 XG27UQDMS · DisplayPort · 3840×2160 at 60 Hz · input DisplayPort-1", list[2], StringComparison.Ordinal);

        await SidekickApp.MonitorLinesAsync(actions, effective, "1 input hdmi-1", _ => { }, CancellationToken.None);
        var (change, failed) = await SidekickApp.MonitorLinesAsync(actions, effective, "2 input hdmi-2", _ => { }, CancellationToken.None);   // the last screen: unasked headless
        Assert.False(failed);
        Assert.Equal([MonitorText.Switched("2 XG27UQDMS", "HDMI-2")], change);
        Assert.Equal(["input A:4353 11", "input A:4355 12"], fake.Changes);

        var bare = await SidekickApp.MonitorLinesAsync(actions, effective, "", _ => { }, CancellationToken.None);
        Assert.Equal([MonitorText.NeedsScreen], bare.Lines);
        Assert.True(bare.Failed);
        Assert.Equal(MonitorText.NeedsScreen, (await SidekickApp.MonitorLinesAsync(actions, effective, "2", _ => { }, CancellationToken.None)).Lines[0]);
        Assert.Equal(MonitorText.NoneHere, (await SidekickApp.MonitorLinesAsync(null, effective, "list", _ => { }, CancellationToken.None)).Lines[0]);
        Assert.Equal(MonitorText.Usage, (await SidekickApp.MonitorLinesAsync(actions, effective, "sideways now", _ => { }, CancellationToken.None)).Lines[0]);
    }

    [Fact]
    public async Task TheCheck_SaysTheCablesView_AndStillPasses()
    {
        // --monitor-check (later on 2026-10-23): no settings, so the cable alone; still read-only and a pass.
        var fake = new FakeMonitorSystem();
        fake.Of(FakeMonitorSystem.Xg).Input = 0x11;
        var console = new Spectre.Console.Testing.TestConsole();
        console.Profile.Width = 400;
        int exit = await MonitorCheck.RunAsync(console, fake, TimeProvider.System, CancellationToken.None);
        Assert.Equal(0, exit);
        Assert.Contains("XG27UQDMS: HDMI-1 0x11 in ", console.Output, StringComparison.Ordinal);
        Assert.Contains(MonitorText.CheckElsewhere("DisplayPort"), console.Output, StringComparison.Ordinal);
        Assert.Equal(1, CountOf(console.Output, MonitorText.CheckElsewhere("DisplayPort")));   // the LG on DisplayPort-1 says nothing
        Assert.Empty(fake.Changes);
    }

    [Fact]
    public async Task TheList_A0x00Read_IsNotRead_AndOffersNoSuchInput()
    {
        var fake = new FakeMonitorSystem();
        fake.Of(FakeMonitorSystem.Xg).Input = 0x00;   // an LG-style all-zero reply
        using var board = new MonitorBoard(fake, quick: true);
        await board.OpenAsync(CancellationToken.None);
        var lines = MonitorText.ListLines(board.State, MonitorWords.None(board.State.Topology.Monitors));
        Assert.Equal("Monitors: extended", lines[0]);   // can't tell: no "another input"
        Assert.Contains(MonitorText.ZeroRead, lines[2], StringComparison.Ordinal);
        Assert.DoesNotContain("(0x00)", lines[2], StringComparison.Ordinal);
    }

    private static int CountOf(string text, string part)
    {
        int count = 0;
        for (int at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    [Fact]
    public async Task Headless_Pc_SavesThroughTheUpdate_NowReadsAfresh_AutoClears()
    {
        // Later on 2026-10-23: this PC's input from headless, saved through the settings' own update, nothing asked or switched.
        var fake = new FakeMonitorSystem();
        var actions = new MonitorActions(new MonitorCalls(fake, retryGap: TimeSpan.Zero), settle: TimeSpan.Zero);
        var data = new AppSettingsData();
        void Update(Action<AppSettingsData> change) => change(data);

        var (set, setFailed) = await SidekickApp.MonitorLinesAsync(actions, data, "2 pc hdmi-1", Update, CancellationToken.None);
        Assert.False(setFailed);
        Assert.Equal([MonitorText.PcSet("2 XG27UQDMS", "HDMI-1")], set);
        Assert.Equal(["ausaa34-uid4355/11"], data.MonitorPcInputs);   // the key and entry shape, pinned
        Assert.Empty(fake.Changes);

        var bare = await SidekickApp.MonitorLinesAsync(actions, data, "2 pc", Update, CancellationToken.None);
        Assert.Equal(MonitorText.PcNow("2 XG27UQDMS", "HDMI-1", null, "DisplayPort"), bare.Lines[0]);

        fake.Of(FakeMonitorSystem.Xg).Input = 0x0F;
        var (now, _) = await SidekickApp.MonitorLinesAsync(actions, data, "2 pc now", Update, CancellationToken.None);
        Assert.Equal([MonitorText.PcSet("2 XG27UQDMS", "DisplayPort-1 (0x0F)")], now);
        Assert.Equal(["ausaa34-uid4355/0f"], data.MonitorPcInputs);   // one entry a monitor

        fake.Of(FakeMonitorSystem.Xg).Input = 0x00;   // an LG-style all-zero reply: saves nothing
        var (zero, zeroFailed) = await SidekickApp.MonitorLinesAsync(actions, data, "2 pc now", Update, CancellationToken.None);
        Assert.True(zeroFailed);
        Assert.Equal([MonitorText.PcNoRead("2 XG27UQDMS")], zero);
        Assert.Equal(["ausaa34-uid4355/0f"], data.MonitorPcInputs);

        var (auto, _) = await SidekickApp.MonitorLinesAsync(actions, data, "2 pc auto", Update, CancellationToken.None);
        Assert.Single(auto);
        Assert.Null(data.MonitorPcInputs);

        var (unknown, unknownFailed) = await SidekickApp.MonitorLinesAsync(actions, data, "2 pc vga-9", Update, CancellationToken.None);
        Assert.True(unknownFailed);
        Assert.StartsWith("2 XG27UQDMS has no input 'vga-9'", unknown[0], StringComparison.Ordinal);
    }
}
