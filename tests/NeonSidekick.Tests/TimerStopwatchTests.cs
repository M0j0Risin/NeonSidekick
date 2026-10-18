using Microsoft.Extensions.AI;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.Timers;
using NeonSidekick.UI;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace NeonSidekick.Tests;

/// <summary>
/// The stopwatch (2026-10-18, the user's ask: <c>/timer stopwatch</c>, a simple count-up, no laps): on the board beside the timers,
/// its words, and what the model's timer tools make of it.
/// </summary>
public class TimerStopwatchTests : IDisposable
{
    private readonly ManualTimeProvider _time = new();
    private readonly List<int> _signals = [];
    private readonly TimerBoard _board;

    public TimerStopwatchTests()
    {
        _board = new TimerBoard(_time, () => _signals.Add(_signals.Count));
    }

    public void Dispose() => _board.Dispose();

    // ── The board ───────────────────────────────────────────────────────────

    [Fact]
    public void StartStopwatch_StartsOne_ItsElapsedFollowsTheClock_RoundedDown()
    {
        var result = _board.StartStopwatch(" stopwatch ");

        Assert.Equal(TimerStartOutcome.Started, result.Outcome);
        Assert.True(result.Timer.Stopwatch);
        Assert.Equal("stopwatch", result.Timer.Name);
        Assert.Equal(TimeSpan.Zero, result.Timer.Elapsed);
        Assert.Equal(TimeSpan.Zero, result.Timer.Duration);
        Assert.Equal(TimeSpan.Zero, result.Timer.Remaining);
        Assert.Equal(new DateTimeOffset(2026, 9, 11, 14, 5, 30, TimeSpan.FromHours(-7)), result.Timer.DueLocal);   // when it started
        _time.Advance(TimeSpan.FromSeconds(192.9));

        var now = Assert.Single(_board.Snapshot());
        Assert.Equal(TimeSpan.FromSeconds(192), now.Elapsed);
        Assert.Equal(new DateTimeOffset(2026, 9, 11, 14, 5, 30, TimeSpan.FromHours(-7)), now.DueLocal);
        Assert.False(now.Ringing);
    }

    [Fact]
    public void AStopwatch_NeverAlerts_EvenAfter25Hours_AndATimerBesideItStillRingsOnTime()
    {
        _board.StartStopwatch("stopwatch");
        _board.Start("tea", TimeSpan.FromMinutes(3));

        _time.Advance(TimeSpan.FromMinutes(3));
        Assert.True(_board.TryTakeAlert(out var alert));
        Assert.Equal("tea", alert.Name);
        Assert.False(_board.TryTakeAlert(out _));
        _board.Stop("tea", out _);
        _time.Advance(TimeSpan.FromHours(25));

        Assert.False(_board.TryTakeAlert(out _));
        Assert.False(_board.HasRinging);
        Assert.Single(_signals);   // tea's alone
        Assert.Equal(TimeSpan.FromSeconds(3 * 60 + 25 * 3600), Assert.Single(_board.Snapshot()).Elapsed);
    }

    [Fact]
    public void ADuplicateName_IsRefusedEitherWay_IgnoringCase()
    {
        _board.StartStopwatch("Tea");
        _time.Advance(TimeSpan.FromSeconds(30));

        var asStopwatch = _board.StartStopwatch("tea");
        var asTimer = _board.Start("TEA", TimeSpan.FromMinutes(1));
        _board.Start("eggs", TimeSpan.FromMinutes(5));
        var overTimer = _board.StartStopwatch("eggs");

        Assert.Equal(TimerStartOutcome.Duplicate, asStopwatch.Outcome);
        Assert.True(asStopwatch.Timer.Stopwatch);
        Assert.Equal(TimeSpan.FromSeconds(30), asStopwatch.Timer.Elapsed);
        Assert.Equal(TimerStartOutcome.Duplicate, asTimer.Outcome);
        Assert.True(asTimer.Timer.Stopwatch);
        Assert.Equal(TimerStartOutcome.Duplicate, overTimer.Outcome);
        Assert.False(overTimer.Timer.Stopwatch);
        Assert.Equal(2, _board.Count);
        Assert.Equal(TimerStartOutcome.BadName, _board.StartStopwatch("all").Outcome);
    }

    [Fact]
    public void Stopwatches_CountTowardMaxTimers()
    {
        for (int i = 0; i < TimerBoard.MaxTimers - 1; i++)
        {
            _board.Start("t" + i, TimeSpan.FromMinutes(5));
        }

        Assert.Equal(TimerStartOutcome.Started, _board.StartStopwatch("last").Outcome);
        Assert.Equal(TimerStartOutcome.Full, _board.StartStopwatch("over").Outcome);
        Assert.Equal(TimerStartOutcome.Full, _board.Start("over", TimeSpan.FromMinutes(1)).Outcome);
    }

    [Fact]
    public void Snapshot_RunningTimers_ThenStopwatchesOldestFirst_ThenTheRinging()
    {
        _board.Start("short", TimeSpan.FromMinutes(1));
        _board.StartStopwatch("older");
        _time.Advance(TimeSpan.FromSeconds(5));
        _board.StartStopwatch("newer");
        _board.Start("long", TimeSpan.FromMinutes(30));
        _board.Start("mid", TimeSpan.FromMinutes(10));
        _time.Advance(TimeSpan.FromMinutes(1));   // short rings

        Assert.Equal(["mid", "long", "older", "newer", "short"], _board.Snapshot().Select(s => s.Name));
    }

    [Fact]
    public void Stop_ReturnsTheElapsedTime_AndAcknowledgeLeavesIt_StopAllTakesIt()
    {
        _board.StartStopwatch("stopwatch");
        _board.Start("tea", TimeSpan.FromMinutes(1));
        _time.Advance(TimeSpan.FromMinutes(2));

        Assert.Equal(1, _board.Acknowledge());   // the ringing tea only
        Assert.Equal(1, _board.Count);
        Assert.True(_board.Stop("STOPWATCH", out var removed));
        Assert.True(removed.Stopwatch);
        Assert.Equal(TimeSpan.FromMinutes(2), removed.Elapsed);
        _board.StartStopwatch("again");
        Assert.Equal(1, _board.StopAll());
        Assert.Equal(0, _board.Count);
    }

    // ── The words ───────────────────────────────────────────────────────────

    private static readonly DateTimeOffset Started = new(2026, 9, 11, 14, 2, 0, TimeSpan.FromHours(-7));

    private static TimerSnapshot Stopwatch(string name, int elapsedSeconds) =>
        new(name, TimeSpan.Zero, TimeSpan.Zero, Started, false, Stopwatch: true, Elapsed: TimeSpan.FromSeconds(elapsedSeconds));

    [Fact]
    public void Words_TheStopwatchsSentences_ArePinned()
    {
        Assert.Equal("stopwatch", TimerText.StopwatchName);
        Assert.Equal("the stopwatch", TimerText.StopwatchLabel("stopwatch"));
        Assert.Equal("the stopwatch", TimerText.StopwatchLabel("Stopwatch"));
        Assert.Equal("the tea stopwatch", TimerText.StopwatchLabel("tea"));
        Assert.Equal("started the stopwatch at 14:02", TimerText.StopwatchStarted(Stopwatch("stopwatch", 0)));
        Assert.Equal("stopwatch: 3 minutes 12 seconds elapsed (started at 14:02)", TimerText.Line(Stopwatch("stopwatch", 192)));
        Assert.Equal("stopped the stopwatch at 3 minutes 12 seconds", TimerText.Stopped(Stopwatch("stopwatch", 192)));
        Assert.Equal("stopped the tea stopwatch at 0 seconds", TimerText.Stopped(Stopwatch("tea", 0)));
        Assert.Equal("Error: a stopwatch called 'x' is running (3 minutes 12 seconds elapsed); stop it first or use another name", TimerText.Duplicate(Stopwatch("x", 192)));
        Assert.Equal("Stopwatch \"tea\" started", TimerBoard.StopwatchStartedLogLine("tea"));
    }

    [Fact]
    public void StatusLine_AStopwatchCountsUpWithItsGlyph_AmongTheTimers_AndAloneLeadsWithIt()
    {
        var tea = new TimerSnapshot("tea", TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(252), Started, false);

        Assert.Equal("⏰ tea 04:12 · ⏱️ stopwatch 03:12", TimerText.StatusLine([tea, Stopwatch("stopwatch", 192)]));
        Assert.Equal("⏱️ stopwatch 03:12", TimerText.StatusLine([Stopwatch("stopwatch", 192)]));
        Assert.Equal("⏱️ stopwatch 01:00:05", TimerText.StatusLine([Stopwatch("stopwatch", 3605)]));
    }

    [Fact]
    public void TheStopwatchGlyph_IsTwoCells_ByOurCountAndSpectres()
    {
        Assert.Equal("⏱️", TimerText.StopwatchGlyph);
        Assert.Equal(2, TextCells.Width(TimerText.StopwatchGlyph));
        Assert.Equal(2, new Segment(TimerText.StopwatchGlyph).CellCount());
    }

    // ── The model's tools ───────────────────────────────────────────────────

    [Fact]
    public async Task Tools_ListShowsAStopwatch_StopStopsIt_AndStartOnItsNameGetsTheStopwatchWording()
    {
        var start = new StartTimerTool(_board);
        var stop = new StopTimerTool(_board);
        var list = new ListTimersTool(_board);
        _board.StartStopwatch("stopwatch");
        _time.Advance(TimeSpan.FromSeconds(192));

        Assert.Equal("stopwatch: 3 minutes 12 seconds elapsed (started at 14:05)", await list.InvokeAsync(new AIFunctionArguments()));
        Assert.Equal(
            "Error: a stopwatch called 'stopwatch' is running (3 minutes 12 seconds elapsed); stop it first or use another name",
            await start.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { [StartTimerTool.NameArgument] = "stopwatch", [StartTimerTool.MinutesArgument] = 5 })));
        Assert.Equal("stopped the stopwatch at 3 minutes 12 seconds", await stop.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { [StopTimerTool.NameArgument] = "Stopwatch" })));
        Assert.Equal(0, _board.Count);
    }

    [Fact]
    public void Tools_TheDescriptionsNameTheStopwatch()
    {
        Assert.Equal(
            "Every running timer or stopwatch and how long each has left, or for a stopwatch how long it has run. Call it to answer how much time remains on a timer, or which timers exist.",
            new ListTimersTool(_board).Description);
        Assert.Equal(
            "Stops a running timer or stopwatch by name, or silences one that has gone off. Call list_timers first if unsure of the name.",
            new StopTimerTool(_board).Description);
    }
}
