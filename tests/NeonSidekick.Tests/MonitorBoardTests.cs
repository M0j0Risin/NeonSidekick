using NeonSidekick.Monitors;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// The board (2026-10-23, the monitors plan): nothing read before the first demand, the 2 s topology clock only while a lease is held, the
/// reads on open and after a change, a failed read kept, a duplicated source, a monitor off the desktop, a profile's words, and dispose.
/// </summary>
public sealed class MonitorBoardTests
{
    private readonly FakeMonitorSystem _fake = new();

    [Fact]
    public void NothingIsRead_BeforeTheFirstDemand()
    {
        using var board = new MonitorBoard(_fake, new ManualTimeProvider(), quick: true);
        Assert.Empty(_fake.Calls);
        Assert.Same(MonitorSnapshot.Empty, board.State.Topology);
    }

    [Fact]
    public async Task TheClock_RunsOnlyWhileALeaseIsHeld()
    {
        var time = new ManualTimeProvider();
        using var board = new MonitorBoard(_fake, time, quick: true);
        time.Advance(TimeSpan.FromSeconds(10));
        Assert.Empty(_fake.Calls);

        var first = board.Watch();
        var second = board.Watch();
        time.Advance(MonitorBoard.TopologyEvery);
        await MonitorCallsTests.Until(() => _fake.Count("query") >= 1);
        first.Dispose();
        first.Dispose();
        Assert.True(board.Watching);
        int before = _fake.Count("query");
        time.Advance(MonitorBoard.TopologyEvery);
        await MonitorCallsTests.Until(() => _fake.Count("query") > before);

        second.Dispose();
        Assert.False(board.Watching);
        await Task.Delay(50);
        int after = _fake.Count("query");
        time.Advance(MonitorBoard.TopologyEvery * 3);
        await Task.Delay(50);
        Assert.Equal(after, _fake.Count("query"));
    }

    [Fact]
    public async Task Open_ReadsTheTopologyAndEveryInput_CapabilitiesOnce()
    {
        using var board = new MonitorBoard(_fake, quick: true);
        long version = board.State.Version;
        int changed = 0;
        board.Changed += () => Interlocked.Increment(ref changed);
        await board.OpenAsync(CancellationToken.None);
        var xg = board.State.Topology.ByTarget(FakeMonitorSystem.Xg)!;
        var inputs = board.State.InputsOf(xg);
        Assert.Equal((byte)0x0F, inputs.Current);
        Assert.Equal(new byte[] { 0x0F, 0x11, 0x12 }, inputs.Offered);
        Assert.True(inputs.Reported && inputs.CanSwitch);
        Assert.True(board.State.Version > version);
        Assert.True(changed > 0);

        await board.OpenAsync(CancellationToken.None);
        Assert.Equal(1, _fake.Count("caps " + FakeMonitorSystem.Xg));
        Assert.Equal(2, _fake.Count("read " + FakeMonitorSystem.Xg));
    }

    [Fact]
    public async Task AChange_ReadsAgain_AndTheMarkIsInTheState()
    {
        using var board = new MonitorBoard(_fake, quick: true);
        await board.OpenAsync(CancellationToken.None);
        var xg = board.State.Topology.ByTarget(FakeMonitorSystem.Xg)!;
        var words = MonitorWords.None(board.State.Topology.Monitors);
        await board.Actions.ApplyAsync(MonitorChange.SwitchInput(xg, 0x11), words, CancellationToken.None);
        Assert.Equal((byte)0x11, board.State.InputsOf(xg).Current);
        Assert.Equal((byte)0x0F, board.State.AwayOf(xg));
    }

    [Fact]
    public async Task AFailedRead_KeepsTheLastValue_MarkedStale_AFirstFailure_IsTheProblem()
    {
        using var board = new MonitorBoard(_fake, quick: true);
        _fake.Of(FakeMonitorSystem.Lg).DdcFail = MonitorText.NoDdc;
        await board.OpenAsync(CancellationToken.None);
        var lg = board.State.Topology.ByTarget(FakeMonitorSystem.Lg)!;
        var xg = board.State.Topology.ByTarget(FakeMonitorSystem.Xg)!;
        Assert.Equal(MonitorText.NoDdc, board.State.InputsOf(lg).Problem);
        Assert.False(board.State.InputsOf(lg).CanSwitch);

        _fake.Of(FakeMonitorSystem.Xg).DdcFail = MonitorText.NoAnswer;
        await board.ReadInputsAsync(CancellationToken.None);
        Assert.Equal((byte)0x0F, board.State.InputsOf(xg).Current);
        Assert.True(board.State.InputsOf(xg).Stale);
        Assert.Null(board.State.InputsOf(xg).Problem);
    }

    [Fact]
    public async Task NoCapabilities_GiveTheCommonFour_Unreported()
    {
        _fake.Of(FakeMonitorSystem.Xg).CapsFail = MonitorText.NoDdc;
        using var board = new MonitorBoard(_fake, quick: true);
        await board.OpenAsync(CancellationToken.None);
        var inputs = board.State.InputsOf(board.State.Topology.ByTarget(FakeMonitorSystem.Xg)!);
        Assert.Equal(Mccs.CommonInputs, inputs.Offered);
        Assert.False(inputs.Reported);
    }

    [Fact]
    public async Task Duplicated_AndOffTheDesktop_StandForTheInputs()
    {
        using var board = new MonitorBoard(_fake, quick: true);
        await board.Actions.ApplyAsync(MonitorChange.Arrange(MonitorLayout.Duplicate), MonitorWords.None([]), CancellationToken.None);
        await board.ReadInputsAsync(CancellationToken.None);
        Assert.All(board.State.Topology.Monitors, m => Assert.Equal(MonitorText.SharedSource, board.State.InputsOf(m).Problem));

        await board.Actions.ApplyAsync(MonitorChange.Arrange(MonitorLayout.Only, board.State.Topology.ByTarget(FakeMonitorSystem.Lg)), MonitorWords.None([]), CancellationToken.None);
        await board.ReadInputsAsync(CancellationToken.None);
        Assert.Equal(MonitorText.OffDesktop, board.State.InputsOf(board.State.Topology.ByTarget(FakeMonitorSystem.Xg)!).Problem);
        Assert.Equal((byte)0x0F, board.State.InputsOf(board.State.Topology.ByTarget(FakeMonitorSystem.Lg)!).Current);
    }

    [Fact]
    public async Task AFailedTopologyRead_KeepsTheLastSnapshot()
    {
        using var board = new MonitorBoard(_fake, quick: true);
        await board.RefreshAsync(CancellationToken.None);
        var kept = board.State.Topology;
        _fake.QueryFail = MonitorText.OnlyFromDesktop;
        Assert.Same(kept, await board.RefreshAsync(CancellationToken.None));
        Assert.Same(kept, board.State.Topology);
    }

    [Fact]
    public async Task AProfileSwitch_KeepsTheSnapshot_TheWordsFollowTheNames()
    {
        using var board = new MonitorBoard(_fake, quick: true);
        await board.OpenAsync(CancellationToken.None);
        var state = board.State;
        var xg = state.Topology.ByTarget(FakeMonitorSystem.Xg)!;
        var one = new MonitorWords(new Dictionary<string, string> { ["ausaa34-uid4355/0f"] = "This PC" }, null, state.Topology.Monitors);
        var other = new MonitorWords(new Dictionary<string, string> { ["ausaa34-uid4355/0f"] = "Desk PC" }, null, state.Topology.Monitors);
        Assert.Equal("This PC (DisplayPort-1)", one.Describe(xg, 0x0F));
        Assert.Equal("Desk PC (DisplayPort-1)", other.Describe(xg, 0x0F));
        Assert.Same(state, board.State);
    }

    [Fact]
    public async Task Dispose_NeverWaitsOnARunningCall()
    {
        var board = new MonitorBoard(_fake, quick: true);
        _fake.Block = new ManualResetEventSlim(false);
        var open = board.OpenAsync(CancellationToken.None);
        await MonitorCallsTests.Until(() => _fake.Count("query") == 1);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        board.Dispose();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1));
        _fake.Block.Set();
        await open.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
