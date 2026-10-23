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
        var words = new MonitorWords(names, null, board.State.Topology.Monitors);
        IReadOnlyList<string> Texts(string typed) => MonitorCommand.Complete(typed, board.State, words).Select(i => i.Text).ToList();

        Assert.Equal(["list", "extend", "duplicate", "1", "\"LG ULTRAWIDE\"", "2", "XG27UQDMS"], Texts(""));
        Assert.Equal(["2 input", "2 only", "2 primary"], Texts("2 "));
        Assert.Equal(["2 input DisplayPort-1", "2 input Work laptop", "2 input HDMI-2"], Texts("2 input "));
        Assert.Equal(["2 input Work laptop"], Texts("2 input W"));
        Assert.Equal(["\"LG ULTRAWIDE\" input", "\"LG ULTRAWIDE\" only", "\"LG ULTRAWIDE\" primary"], Texts("\"LG ULTRAWIDE\" "));
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

        var (list, listFailed) = await SidekickApp.MonitorLinesAsync(actions, effective, "list", CancellationToken.None);
        Assert.False(listFailed);
        Assert.Equal("Monitors: extended", list[0]);
        Assert.StartsWith("2 XG27UQDMS · DisplayPort · 3840×2160 at 60 Hz · input DisplayPort-1", list[2], StringComparison.Ordinal);

        await SidekickApp.MonitorLinesAsync(actions, effective, "1 input hdmi-1", CancellationToken.None);
        var (change, failed) = await SidekickApp.MonitorLinesAsync(actions, effective, "2 input hdmi-2", CancellationToken.None);   // the last screen: unasked headless
        Assert.False(failed);
        Assert.Equal([MonitorText.Switched("2 XG27UQDMS", "HDMI-2")], change);
        Assert.Equal(["input A:4353 11", "input A:4355 12"], fake.Changes);

        var bare = await SidekickApp.MonitorLinesAsync(actions, effective, "", CancellationToken.None);
        Assert.Equal([MonitorText.NeedsScreen], bare.Lines);
        Assert.True(bare.Failed);
        Assert.Equal(MonitorText.NeedsScreen, (await SidekickApp.MonitorLinesAsync(actions, effective, "2", CancellationToken.None)).Lines[0]);
        Assert.Equal(MonitorText.NoneHere, (await SidekickApp.MonitorLinesAsync(null, effective, "list", CancellationToken.None)).Lines[0]);
        Assert.Equal(MonitorText.Usage, (await SidekickApp.MonitorLinesAsync(actions, effective, "sideways now", CancellationToken.None)).Lines[0]);
    }
}
