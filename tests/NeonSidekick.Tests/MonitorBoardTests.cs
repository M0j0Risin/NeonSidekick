using NeonSidekick.Monitors;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// The board (2026-10-23, the monitors plan): nothing read before the first demand, the 2 s topology clock only while a lease is held, the
/// reads on open and after a change, a failed read kept, a duplicated source, a monitor off the desktop, a profile's words, and dispose. And
/// (2026-10-27, the user's pick) the current inputs before any capabilities, and a monitor's remembered list shown while its capabilities are read.
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
    public async Task OffTheDesktop_AReachedMonitor_IsRead_AFailure_IsItsProblem_NeverStale()
    {
        var screen = _fake.Of(FakeMonitorSystem.Xg);
        screen.Active = false;
        screen.ReachedOffDesktop = true;
        screen.Input = 0x11;
        using var board = new MonitorBoard(_fake, quick: true);
        await board.OpenAsync(CancellationToken.None);
        var xg = board.State.Topology.ByTarget(FakeMonitorSystem.Xg)!;
        Assert.False(xg.Active);
        var inputs = board.State.InputsOf(xg);
        Assert.Equal((byte)0x11, inputs.Current);
        Assert.Null(inputs.Problem);
        Assert.Equal(new byte[] { 0x0F, 0x11, 0x12 }, inputs.Offered);

        // Read: onlyUnread passes it by.
        int reads = _fake.Count("read " + FakeMonitorSystem.Xg);
        await board.ReadInputsAsync(CancellationToken.None, onlyUnread: true);
        Assert.Equal(reads, _fake.Count("read " + FakeMonitorSystem.Xg));

        // A failure off the desktop is its problem, not a stale value to switch from.
        screen.DdcFail = MonitorText.NoAnswer;
        await board.ReadInputsAsync(CancellationToken.None);
        Assert.Equal(MonitorText.NoAnswer, board.State.InputsOf(xg).Problem);
        Assert.False(board.State.InputsOf(xg).Stale);
    }

    [Fact]
    public async Task OffTheDesktop_AnUnreachedMonitor_IsAProblem_AndOnlyUnreadAsksNoMore()
    {
        _fake.Of(FakeMonitorSystem.Xg).Active = false;
        using var board = new MonitorBoard(_fake, quick: true);
        await board.OpenAsync(CancellationToken.None);
        var xg = board.State.Topology.ByTarget(FakeMonitorSystem.Xg)!;
        Assert.Equal(MonitorText.OffDesktop, board.State.InputsOf(xg).Problem);
        Assert.Null(board.State.InputsOf(xg).Current);
        Assert.False(board.State.InputsOf(xg).Reading);

        int reads = _fake.Count("read " + FakeMonitorSystem.Xg);
        await board.ReadInputsAsync(CancellationToken.None, onlyUnread: true);
        Assert.Equal(reads, _fake.Count("read " + FakeMonitorSystem.Xg));
    }

    [Fact]
    public async Task SwitchTo_OffTheDesktop_ThisPcsInput_AlsoAddsItBack()
    {
        var screen = _fake.Of(FakeMonitorSystem.Xg);
        screen.Active = false;
        screen.ReachedOffDesktop = true;
        screen.Input = 0x11;
        using var board = new MonitorBoard(_fake, quick: true);
        await board.OpenAsync(CancellationToken.None);
        var state = board.State;
        var xg = state.Topology.ByTarget(FakeMonitorSystem.Xg)!;
        var lg = state.Topology.ByTarget(FakeMonitorSystem.Lg)!;
        var none = MonitorWords.None(state.Topology.Monitors);

        // No pick: the cable's guess (DisplayPort-1 on a DisplayPort cable) is this PC's.
        Assert.Equal((byte)0x0F, state.PcInputOf(xg, none));
        Assert.True(state.SwitchTo(xg, 0x0F, none).Return);
        Assert.False(state.SwitchTo(xg, 0x12, none).Return);
        Assert.False(state.SwitchTo(xg, 0x12, none).Unlisted);
        Assert.True(state.SwitchTo(xg, 0x1B, none).Unlisted);

        // A pick wins over the guess.
        var picked = new MonitorWords(null, null, state.Topology.Monitors, ["ausaa34-uid4355/12"]);
        Assert.Equal((byte)0x12, state.PcInputOf(xg, picked));
        Assert.True(state.SwitchTo(xg, 0x12, picked).Return);
        Assert.False(state.SwitchTo(xg, 0x0F, picked).Return);

        // On the desktop it is never a return.
        Assert.False(state.SwitchTo(lg, 0x0F, none).Return);
        Assert.False(state.SwitchTo(xg, 0x0F, none).ExtendsAll);   // the only one off the desktop: Extend all adds just it
    }

    [Fact]
    public async Task SwitchTo_OffTheDesktop_ThisPcsInput_ExtendsAll_WithAnotherOffTheDesktop_OrADuplicate()
    {
        var screen = _fake.Of(FakeMonitorSystem.Xg);
        screen.Active = false;
        screen.ReachedOffDesktop = true;
        _fake.Screens.Add(new() { Target = "A:4357", Name = "DELL U2720Q", Active = false, Source = "A:2", Gdi = @"\\.\DISPLAY3", Mode = new MonitorMode(3840, 2160, 60, 0, 0) });
        using (var board = new MonitorBoard(_fake, quick: true))
        {
            await board.OpenAsync(CancellationToken.None);
            var state = board.State;
            var xg = state.Topology.ByTarget(FakeMonitorSystem.Xg)!;
            var none = MonitorWords.None(state.Topology.Monitors);
            var change = state.SwitchTo(xg, 0x0F, none);
            Assert.True(change.Return);
            Assert.True(change.ExtendsAll);   // the DELL comes back too (2026-10-27, the review)
            Assert.EndsWith(MonitorText.AndExtendAllWords, change.Describe(none), StringComparison.Ordinal);
        }

        var dell = _fake.Of("A:4357");
        dell.Active = true;
        dell.Source = "A:0";
        dell.Gdi = @"\\.\DISPLAY1";
        using (var board = new MonitorBoard(_fake, quick: true))
        {
            await board.OpenAsync(CancellationToken.None);
            var state = board.State;
            Assert.Equal(MonitorArrangement.Duplicated, state.Topology.Layout);
            var xg = state.Topology.ByTarget(FakeMonitorSystem.Xg)!;
            Assert.True(state.SwitchTo(xg, 0x0F, MonitorWords.None(state.Topology.Monitors)).ExtendsAll);   // the Duplicate ends
        }
    }

    [Fact]
    public async Task PcInputOf_AMonitorOffTheDesktop_NeverGuessesFromItsCurrentInput()
    {
        var screen = _fake.Of(FakeMonitorSystem.Lg);
        screen.Active = false;
        screen.ReachedOffDesktop = true;
        screen.Input = 0x10;   // DisplayPort-2: another machine's, off the desktop (2026-10-27, the review)
        using var board = new MonitorBoard(_fake, quick: true);
        await board.OpenAsync(CancellationToken.None);
        var state = board.State;
        var lg = state.Topology.ByTarget(FakeMonitorSystem.Lg)!;

        Assert.Null(state.PcInputOf(lg, MonitorWords.None(state.Topology.Monitors)));   // two DisplayPort inputs: no guess
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
    public async Task Open_ReadsEveryCurrentInput_BeforeAnyCapabilities()
    {
        using var board = new MonitorBoard(_fake, quick: true);
        await board.OpenAsync(CancellationToken.None);
        Assert.Equal(["query", "read " + FakeMonitorSystem.Lg, "read " + FakeMonitorSystem.Xg, "caps " + FakeMonitorSystem.Lg, "caps " + FakeMonitorSystem.Xg], _fake.Calls);
    }

    [Fact]
    public async Task ARememberedList_ShowsBeforeTheCapabilitiesAnswer_TheSameAnswer_IsNotSeen()
    {
        var seen = new List<(string Target, byte[] Inputs)>();
        using var board = new MonitorBoard(_fake, quick: true,
            remembered: (m, _) => m.Target == FakeMonitorSystem.Xg ? [0x0F, 0x11, 0x12] : null,
            seen: (m, _, inputs) => { lock (seen) { seen.Add((m.Target, [.. inputs])); } });
        _fake.CapsBlock = new ManualResetEventSlim(false);
        var open = board.OpenAsync(CancellationToken.None);
        await MonitorCallsTests.Until(() => _fake.Count("caps ") == 1);   // the LG's list held: the second round under way

        var xg = board.State.InputsOf(board.State.Topology.ByTarget(FakeMonitorSystem.Xg)!);
        Assert.Equal((false, (byte?)0x0F, true), (xg.Reading, xg.Current, xg.CanSwitch));
        Assert.Equal(new byte[] { 0x0F, 0x11, 0x12 }, xg.Offered);
        Assert.True(xg.Reported);
        Assert.True(board.State.InputsOf(board.State.Topology.ByTarget(FakeMonitorSystem.Lg)!).Reading);   // nothing remembered: as before

        _fake.CapsBlock.Set();
        await open.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal([FakeMonitorSystem.Lg], seen.Select(s => s.Target));   // the LG's first list; the XG's matched
        Assert.Equal(new byte[] { 0x11, 0x12, 0x0F, 0x10 }, seen[0].Inputs);
        Assert.False(board.State.InputsOf(board.State.Topology.ByTarget(FakeMonitorSystem.Lg)!).Reading);
    }

    [Fact]
    public async Task ADifferentAnswer_ReplacesTheRememberedList_AndIsSeen_AFailedRead_KeepsIt()
    {
        var seen = new List<byte[]>();
        IReadOnlyList<byte>? Remembered(MonitorInfo m, IReadOnlyList<MonitorInfo> connected) => m.Target == FakeMonitorSystem.Xg ? [0x0F, 0x11] : null;
        using (var board = new MonitorBoard(_fake, quick: true, remembered: Remembered, seen: (m, _, inputs) => { if (m.Target == FakeMonitorSystem.Xg) { seen.Add([.. inputs]); } }))
        {
            await board.OpenAsync(CancellationToken.None);
            Assert.Equal(new byte[] { 0x0F, 0x11, 0x12 }, board.State.InputsOf(board.State.Topology.ByTarget(FakeMonitorSystem.Xg)!).Offered);
            Assert.Equal([[0x0F, 0x11, 0x12]], seen);
        }

        seen.Clear();
        _fake.Of(FakeMonitorSystem.Xg).CapsFail = MonitorText.NoDdc;
        using (var board = new MonitorBoard(_fake, quick: true, remembered: Remembered, seen: (m, _, inputs) => seen.Add([.. inputs])))
        {
            await board.OpenAsync(CancellationToken.None);
            var xg = board.State.InputsOf(board.State.Topology.ByTarget(FakeMonitorSystem.Xg)!);
            Assert.Equal(new byte[] { 0x0F, 0x11 }, xg.Offered);   // kept, not the common four
            Assert.True(xg.Reported && !xg.Reading);
            Assert.Single(seen);   // the LG's alone
        }
    }

    [Fact]
    public async Task ASeenThatThrows_LeavesNoTabReading()
    {
        using var board = new MonitorBoard(_fake, quick: true, seen: (_, _, _) => throw new InvalidOperationException("the settings were busy"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => board.OpenAsync(CancellationToken.None));

        Assert.All(board.State.Topology.Monitors, m => Assert.False(board.State.InputsOf(m).Reading));   // 2026-10-27, the review
    }

    [Fact]
    public async Task AWarmCache_StillTellsAListTheRememberedOneLacks()
    {
        // A profile switched to remembers nothing yet: the cached lists are seen for it, so its file gets them (2026-10-27, the review).
        IReadOnlyList<byte>? remembered = null;
        bool same = true;
        var seen = new List<string>();
        using var board = new MonitorBoard(_fake, quick: true,
            remembered: (m, _) => same ? (m.Target == FakeMonitorSystem.Xg ? [0x0F, 0x11, 0x12] : [0x11, 0x12, 0x0F, 0x10]) : remembered,
            seen: (m, _, _) => { lock (seen) { seen.Add(m.Target); } });
        await board.OpenAsync(CancellationToken.None);
        Assert.Empty(seen);

        same = false;
        await board.ReadInputsAsync(CancellationToken.None);

        Assert.Equal(1, _fake.Count("caps " + FakeMonitorSystem.Xg));   // cached: not asked again
        Assert.Equal([FakeMonitorSystem.Lg, FakeMonitorSystem.Xg], seen);
    }

    [Fact]
    public async Task ASwitchAskedDuringTheCapabilitiesRound_GoesBeforeTheNextList()
    {
        using var hold = new ManualResetEventSlim(false);
        _fake.CapsBlock = hold;
        using var board = new MonitorBoard(_fake, quick: true);
        await board.RefreshAsync(CancellationToken.None);
        var xg = board.State.Topology.ByTarget(FakeMonitorSystem.Xg)!;
        var reading = board.ReadInputsAsync(CancellationToken.None);
        await MonitorCallsTests.Until(() => _fake.Count("caps ") == 1);   // the LG's list held

        var switching = board.Actions.ApplyAsync(MonitorChange.SwitchInput(xg, 0x11), MonitorWords.None(board.State.Topology.Monitors), CancellationToken.None);
        hold.Set();
        await switching.WaitAsync(TimeSpan.FromSeconds(10));
        await reading.WaitAsync(TimeSpan.FromSeconds(10));

        var calls = _fake.Calls.ToList();
        int set = calls.IndexOf("set " + FakeMonitorSystem.Xg + " 11");
        int xgCaps = calls.IndexOf("caps " + FakeMonitorSystem.Xg);
        Assert.True(set >= 0 && set < xgCaps, string.Join(", ", calls));   // the switch waited for no list (2026-10-27, the review)
    }

    [Fact]
    public async Task ANotReportedAnswer_IsNeverSeen_NorAMonitorWithNoKey()
    {
        var asked = new List<string>();
        var seen = new List<string>();
        _fake.Of(FakeMonitorSystem.Xg).Caps = "(prot(monitor)type(LCD)vcp(02 04))";   // lists no inputs
        _fake.Of(FakeMonitorSystem.Lg).DevicePath = null;   // no key
        using var board = new MonitorBoard(_fake, quick: true,
            remembered: (m, _) => { asked.Add(m.Target); return null; },
            seen: (m, _, _) => seen.Add(m.Target));
        await board.OpenAsync(CancellationToken.None);
        Assert.Null(board.State.Topology.ByTarget(FakeMonitorSystem.Lg)!.Key);
        Assert.Equal([FakeMonitorSystem.Xg], asked);
        Assert.Empty(seen);
        Assert.False(board.State.InputsOf(board.State.Topology.ByTarget(FakeMonitorSystem.Xg)!).Reported);
    }

    /// <summary>The settings' list through <see cref="MonitorWords"/>: a cable moved (the same model, a new port) finds the old list, and the answer moves it to the new key.</summary>
    [Fact]
    public async Task AMovedCable_FindsItsOldList_AndTheAnswerMovesItToTheNewKey()
    {
        List<string>? saved = ["ausaa34-uid9999/0f", "ausaa34-uid9999/11", "other-uid1/0f"];
        using var board = new MonitorBoard(_fake, quick: true,
            remembered: (m, connected) => MonitorWords.Remembered(saved, m, connected),
            seen: (m, connected, inputs) => saved = MonitorWords.Remember(saved, m, connected, inputs));
        _fake.CapsBlock = new ManualResetEventSlim(false);
        var open = board.OpenAsync(CancellationToken.None);
        await MonitorCallsTests.Until(() => _fake.Count("caps ") == 1);
        Assert.Equal(new byte[] { 0x0F, 0x11 }, board.State.InputsOf(board.State.Topology.ByTarget(FakeMonitorSystem.Xg)!).Offered);

        _fake.CapsBlock.Set();
        await open.WaitAsync(TimeSpan.FromSeconds(10));
        string lg = board.State.Topology.ByTarget(FakeMonitorSystem.Lg)!.Key!;
        Assert.Equal(["other-uid1/0f", lg + "/11", lg + "/12", lg + "/0f", lg + "/10", "ausaa34-uid4355/0f", "ausaa34-uid4355/11", "ausaa34-uid4355/12"], saved);   // uid9999's gone
        Assert.Equal(new byte[] { 0x0F, 0x11, 0x12 }, MonitorWords.Remembered(saved, board.State.Topology.ByTarget(FakeMonitorSystem.Xg)!, board.State.Topology.Monitors));
    }

    [Fact]
    public void RememberedAndRemember_AreNoneForAMonitorWithNoKey_AndTwoOfAModelFindNoOldList()
    {
        var keyless = Info("A:1", null);
        Assert.Null(MonitorWords.Remembered(["x-uid1/0f"], keyless, [keyless]));
        Assert.Equal(["x-uid1/0f"], MonitorWords.Remember(["x-uid1/0f"], keyless, [keyless], [0x11]));
        Assert.Null(MonitorWords.Remember(null, keyless, [keyless], [0x11]));

        var one = Info("A:2", "ausaa34-uid2");
        var two = Info("A:3", "ausaa34-uid3");
        Assert.Null(MonitorWords.Remembered(["ausaa34-uid9/0f"], one, [one, two]));   // which XG moved is unknown
        Assert.Equal(new byte[] { 0x0F }, MonitorWords.Remembered(["ausaa34-uid9/0f", "ausaa34-uid9/zz"], one, [one]));   // a code that doesn't parse is skipped
        Assert.Equal(["ausaa34-uid9/0f", "ausaa34-uid2/11"], MonitorWords.Remember(["ausaa34-uid9/0f"], one, [one, two], [0x11, 0x11]));   // the other's list stays

        static MonitorInfo Info(string target, string? key) => new(1, target, key, "XG", "DisplayPort", true, false, null, null, false);
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
