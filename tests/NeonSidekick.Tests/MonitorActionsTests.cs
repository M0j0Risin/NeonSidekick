using NeonSidekick.Monitors;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// The monitors' one runner (2026-10-23, the monitors plan): the retry after a gap, the 10 s cap on a stuck call (finishing in the background),
/// a crash turned into a sentence, a cancel — run with no board, as headless and <c>--monitor-check</c> use it.
/// </summary>
public sealed class MonitorCallsTests
{
    [Fact]
    public async Task ADdcFailure_IsTriedOnceMore()
    {
        var fake = new FakeMonitorSystem { FailNext = 1 };
        var calls = new MonitorCalls(fake, retryGap: TimeSpan.Zero);
        Assert.Equal(0x0F, await calls.RunAsync("read", s => s.CurrentInput(FakeMonitorSystem.Xg), retry: true, CancellationToken.None));
        Assert.Equal(2, fake.Count("read"));

        fake.FailNext = 2;
        var e = await Assert.ThrowsAsync<MonitorException>(() => calls.RunAsync("read", s => s.CurrentInput(FakeMonitorSystem.Xg), retry: true, CancellationToken.None));
        Assert.Equal(MonitorText.NoDdc, e.Message);

        fake.FailNext = 1;
        await Assert.ThrowsAsync<MonitorException>(() => calls.RunAsync("read", s => s.CurrentInput(FakeMonitorSystem.Xg), retry: false, CancellationToken.None));
    }

    [Fact]
    public async Task TheRetry_WaitsItsGap()
    {
        var time = new ManualTimeProvider();
        var fake = new FakeMonitorSystem { FailNext = 1 };
        var calls = new MonitorCalls(fake, time);
        var read = calls.RunAsync("read", s => s.CurrentInput(FakeMonitorSystem.Xg), retry: true, CancellationToken.None);
        await Until(() => fake.Count("read") == 1);
        await Task.Delay(50);
        Assert.False(read.IsCompleted);
        time.Advance(MonitorCalls.RetryGap - TimeSpan.FromMilliseconds(1));
        await Task.Delay(50);
        Assert.False(read.IsCompleted);
        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(0x0F, await read.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task AStuckCall_AnswersNoAnswerAfterTheCap_AndFinishesInTheBackground()
    {
        var time = new ManualTimeProvider();
        var fake = new FakeMonitorSystem { Block = new ManualResetEventSlim(false) };
        var calls = new MonitorCalls(fake, time);
        var read = calls.RunAsync("read", s => s.CurrentInput(FakeMonitorSystem.Xg), retry: true, CancellationToken.None);
        await Until(() => fake.Count("read") == 1 && time.TimerCount > 0);
        time.Advance(MonitorCalls.Cap);
        var e = await Assert.ThrowsAsync<MonitorException>(() => read.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(MonitorText.NoAnswer, e.Message);

        // The next call is not held by the stuck one.
        fake.Block.Set();
        Assert.Equal(0x0F, await calls.RunAsync("read", s => s.CurrentInput(FakeMonitorSystem.Xg), retry: false, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task ACancel_ReturnsAtOnce()
    {
        var fake = new FakeMonitorSystem { Block = new ManualResetEventSlim(false) };
        var calls = new MonitorCalls(fake);
        using var cts = new CancellationTokenSource();
        var read = calls.RunAsync("read", s => s.CurrentInput(FakeMonitorSystem.Xg), retry: false, cts.Token);
        await Until(() => fake.Count("read") == 1);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(10)));
        fake.Block.Set();
    }

    [Fact]
    public async Task ACrash_ComesOutAsASentence()
    {
        var fake = new FakeMonitorSystem { Crash = new InvalidOperationException("boom") };
        var calls = new MonitorCalls(fake);
        var e = await Assert.ThrowsAsync<MonitorException>(() => calls.RunAsync("query", s => s.Query(), retry: false, CancellationToken.None));
        Assert.Equal(MonitorText.Failed("boom"), e.Message);
    }

    internal static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("the condition never held");
            }

            await Task.Delay(5);
        }
    }
}

/// <summary>
/// The one path every change takes (2026-10-23, the monitors plan): each outcome, the refusals, the questions, and the app's own record of the
/// monitors it switched away (<see cref="MonitorAway"/>).
/// </summary>
public sealed class MonitorActionsTests
{
    private const string XgKey = "ausaa34-uid4355";
    private const string LgKey = "gsm7768-uid4353";

    private readonly FakeMonitorSystem _fake = new();
    private readonly MonitorActions _actions;

    public MonitorActionsTests()
    {
        _actions = new MonitorActions(new MonitorCalls(_fake, retryGap: TimeSpan.Zero), settle: TimeSpan.Zero);
    }

    private MonitorWords Words => MonitorWords.None(_fake.Query().Monitors);

    private MonitorInfo Monitor(string target) => _fake.Query().ByTarget(target)!;

    private Task<MonitorOutcome> Switch(string target, byte code) => _actions.ApplyAsync(MonitorChange.SwitchInput(Monitor(target), code), Words, CancellationToken.None);

    private Task<MonitorOutcome> Arrange(MonitorLayout kind, string? target = null) =>
        _actions.ApplyAsync(MonitorChange.Arrange(kind, target is null ? null : Monitor(target)), Words, CancellationToken.None);

    private Task<string?> Warning(MonitorChange change) => _actions.WarningAsync(change, Words, CancellationToken.None);

    [Fact]
    public async Task Switch_SetsTheInput_AndMarksWhereItWas()
    {
        var outcome = await Switch(FakeMonitorSystem.Xg, 0x11);
        Assert.Equal(MonitorOutcomeKind.Switched, outcome.Kind);
        Assert.Equal(MonitorText.Switched("2 XG27UQDMS", "HDMI-1"), outcome.Text);
        Assert.Equal(0x11, _fake.Of(FakeMonitorSystem.Xg).Input);
        Assert.Equal((byte)0x0F, _actions.Away.LeftFrom(XgKey));
    }

    [Fact]
    public async Task ASecondSwitchAway_KeepsTheInputFirstLeft_AndTheWayBackClearsIt()
    {
        await Switch(FakeMonitorSystem.Xg, 0x11);
        await Switch(FakeMonitorSystem.Xg, 0x12);
        Assert.Equal((byte)0x0F, _actions.Away.LeftFrom(XgKey));
        await Switch(FakeMonitorSystem.Xg, 0x0F);
        Assert.Null(_actions.Away.LeftFrom(XgKey));
    }

    [Fact]
    public async Task AReadOfTheInputLeft_ClearsTheMark()
    {
        await Switch(FakeMonitorSystem.Xg, 0x11);
        _fake.Of(FakeMonitorSystem.Xg).Input = 0x0F;   // its own buttons
        await _actions.ReadInputAsync(Monitor(FakeMonitorSystem.Xg), CancellationToken.None);
        Assert.Null(_actions.Away.LeftFrom(XgKey));
    }

    [Fact]
    public async Task ASwitchThatDropsTheMonitor_SaysItLeftTheDesktop_AndClearsTheMark()
    {
        _fake.Of(FakeMonitorSystem.Xg).DropsOn.Add(0x11);
        var outcome = await Switch(FakeMonitorSystem.Xg, 0x11);
        Assert.Equal(MonitorOutcomeKind.LeftDesktop, outcome.Kind);
        Assert.Equal(MonitorText.LeftDesktop("2 XG27UQDMS", "HDMI-1"), outcome.Text);
        Assert.Null(_actions.Away.LeftFrom(XgKey));
    }

    [Fact]
    public async Task Switch_AlreadyOn_Ignored_Unlisted_AndRefusals()
    {
        Assert.Equal(MonitorOutcomeKind.Unchanged, (await Switch(FakeMonitorSystem.Xg, 0x0F)).Kind);

        _fake.Of(FakeMonitorSystem.Xg).Ignores.Add(0x12);
        var ignored = await Switch(FakeMonitorSystem.Xg, 0x12);
        Assert.Equal(MonitorText.Ignored("2 XG27UQDMS", "HDMI-2", "DisplayPort-1"), ignored.Text);
        Assert.Null(_actions.Away.LeftFrom(XgKey));

        var unlisted = await _actions.ApplyAsync(MonitorChange.SwitchInput(Monitor(FakeMonitorSystem.Xg), 0x1B, unlisted: true), Words, CancellationToken.None);
        Assert.Equal(MonitorText.SwitchedUnlisted("2 XG27UQDMS", "0x1B"), unlisted.Text);

        _fake.Of(FakeMonitorSystem.Lg).DdcFail = MonitorText.NoDdc;
        Assert.Equal(MonitorOutcome.Refused(MonitorText.NoDdc), await Switch(FakeMonitorSystem.Lg, 0x11));
        Assert.Equal(1, _fake.Count("read " + FakeMonitorSystem.Lg) / 2);   // read, retried once; no set
        Assert.Equal(0, _fake.Count("set " + FakeMonitorSystem.Lg));
    }

    [Fact]
    public async Task AnInput_OfAMonitorOffTheDesktop_OrDuplicated_IsRefused()
    {
        var xg = Monitor(FakeMonitorSystem.Xg);
        await Arrange(MonitorLayout.Only, FakeMonitorSystem.Lg);
        var off = await _actions.ApplyAsync(MonitorChange.SwitchInput(xg, 0x11), Words, CancellationToken.None);
        Assert.Equal(MonitorOutcome.Refused(MonitorText.OffDesktop), off);

        await Arrange(MonitorLayout.Duplicate);
        var shared = await _actions.ApplyAsync(MonitorChange.SwitchInput(xg, 0x11), Words, CancellationToken.None);
        Assert.Equal(MonitorOutcome.Refused(MonitorText.SharedSource), shared);
    }

    [Fact]
    public async Task Layouts_ApplyAndSay_OrSayNothingChanged()
    {
        Assert.Equal(MonitorText.AlreadyArranged("extended"), (await Arrange(MonitorLayout.Extend)).Text);
        Assert.Equal(MonitorText.OnlyNow("1 XG27UQDMS"), (await Arrange(MonitorLayout.Only, FakeMonitorSystem.Xg)).Text);
        Assert.Equal(MonitorArrangement.Single, _fake.Query().Layout);
        Assert.Equal(MonitorText.AlreadyOnly("1 XG27UQDMS"), (await Arrange(MonitorLayout.Only, FakeMonitorSystem.Xg)).Text);
        Assert.Equal(MonitorText.Arranged("extended"), (await Arrange(MonitorLayout.Extend)).Text);
        Assert.Equal(MonitorText.MadePrimary("2 XG27UQDMS"), (await Arrange(MonitorLayout.Primary, FakeMonitorSystem.Xg)).Text);
        Assert.True(Monitor(FakeMonitorSystem.Xg).Primary);
        Assert.Equal(MonitorText.Arranged("duplicated"), (await Arrange(MonitorLayout.Duplicate)).Text);
        Assert.Equal(MonitorOutcome.Refused(MonitorText.PrimaryWhileDuplicated), await Arrange(MonitorLayout.Primary, FakeMonitorSystem.Lg));
        Assert.Equal(["layout Only A:4355", "layout Extend", "layout Primary A:4355", "layout Duplicate"], _fake.Changes);

        _fake.LayoutFail = MonitorText.OnlyFromDesktop;
        Assert.Equal(MonitorOutcome.Refused(MonitorText.OnlyFromDesktop), await Arrange(MonitorLayout.Extend));
    }

    [Fact]
    public void WouldChange_LeavesOutTheRowsThatChangeNothing()
    {
        var snapshot = _fake.Query();
        var lg = snapshot.ByTarget(FakeMonitorSystem.Lg)!;
        var xg = snapshot.ByTarget(FakeMonitorSystem.Xg)!;
        Assert.False(MonitorActions.WouldChange(MonitorChange.Arrange(MonitorLayout.Extend), snapshot));
        Assert.True(MonitorActions.WouldChange(MonitorChange.Arrange(MonitorLayout.Duplicate), snapshot));
        Assert.True(MonitorActions.WouldChange(MonitorChange.Arrange(MonitorLayout.Only, xg), snapshot));
        Assert.False(MonitorActions.WouldChange(MonitorChange.Arrange(MonitorLayout.Primary, lg), snapshot));
        Assert.True(MonitorActions.WouldChange(MonitorChange.Arrange(MonitorLayout.Primary, xg), snapshot));

        _fake.Screens.RemoveAt(1);
        var one = _fake.Query();
        Assert.False(MonitorActions.WouldChange(MonitorChange.Arrange(MonitorLayout.Duplicate), one));
        Assert.False(MonitorActions.WouldChange(MonitorChange.Arrange(MonitorLayout.Only, one.Monitors[0]), one));
    }

    [Fact]
    public async Task TheLastScreen_Asks_OnlyWhenNoOtherMonitorShowsThisPc()
    {
        Assert.Null(await Warning(MonitorChange.SwitchInput(Monitor(FakeMonitorSystem.Xg), 0x11)));

        await Switch(FakeMonitorSystem.Lg, 0x11);   // monitor 1 switched to the laptop by the app
        Assert.Equal(MonitorText.LastScreenQuestion("2 XG27UQDMS", "HDMI-1"), await Warning(MonitorChange.SwitchInput(Monitor(FakeMonitorSystem.Xg), 0x11)));
        // Switching monitor 1 again, or back, asks nothing: it doesn't show this PC now.
        Assert.Null(await Warning(MonitorChange.SwitchInput(Monitor(FakeMonitorSystem.Lg), 0x12)));
        Assert.Null(await Warning(MonitorChange.SwitchInput(Monitor(FakeMonitorSystem.Lg), 0x0F)));
    }

    [Fact]
    public async Task TheLastScreen_OneMonitorAlone_Asks()
    {
        await Arrange(MonitorLayout.Only, FakeMonitorSystem.Xg);
        Assert.NotNull(await Warning(MonitorChange.SwitchInput(Monitor(FakeMonitorSystem.Xg), 0x11)));
    }

    [Fact]
    public async Task TheMarkedMonitors_AreReadAgainFirst_SoOneBackByItsButtonsAsksNothing()
    {
        await Switch(FakeMonitorSystem.Lg, 0x11);
        _fake.Of(FakeMonitorSystem.Lg).Input = 0x0F;   // switched back by its own buttons
        Assert.Null(await Warning(MonitorChange.SwitchInput(Monitor(FakeMonitorSystem.Xg), 0x11)));
        Assert.Null(_actions.Away.LeftFrom(LgKey));
    }

    [Fact]
    public async Task OnlyAndPrimary_OnAMonitorMarkedAway_Ask_AndDuplicateWhileOneIs()
    {
        await Switch(FakeMonitorSystem.Xg, 0x11);
        var xg = Monitor(FakeMonitorSystem.Xg);
        Assert.Equal(MonitorText.AwayLayoutQuestion(MonitorText.OnlyRow("2 XG27UQDMS"), "2 XG27UQDMS", "DisplayPort-1"), await Warning(MonitorChange.Arrange(MonitorLayout.Only, xg)));
        Assert.Equal(MonitorText.AwayLayoutQuestion(MonitorText.PrimaryRow("2 XG27UQDMS"), "2 XG27UQDMS", "DisplayPort-1"), await Warning(MonitorChange.Arrange(MonitorLayout.Primary, xg)));
        Assert.Equal(MonitorText.DuplicateAwayQuestion("2 XG27UQDMS", "DisplayPort-1"), await Warning(MonitorChange.Arrange(MonitorLayout.Duplicate)));
        Assert.Null(await Warning(MonitorChange.Arrange(MonitorLayout.Only, Monitor(FakeMonitorSystem.Lg))));
        Assert.Null(await Warning(MonitorChange.Arrange(MonitorLayout.Extend)));
    }

    [Fact]
    public async Task TheMark_FollowsItsMonitor_WhenTheNumbersMove()
    {
        await Switch(FakeMonitorSystem.Xg, 0x11);
        _fake.Of(FakeMonitorSystem.Xg).Gdi = @"\\.\DISPLAY0";   // now first in the screen capture's order
        var xg = Monitor(FakeMonitorSystem.Xg);
        Assert.Equal(1, xg.Number);
        Assert.Equal(MonitorText.AwayLayoutQuestion(MonitorText.OnlyRow("1 XG27UQDMS"), "1 XG27UQDMS", "DisplayPort-1"), await Warning(MonitorChange.Arrange(MonitorLayout.Only, xg)));
    }
}
