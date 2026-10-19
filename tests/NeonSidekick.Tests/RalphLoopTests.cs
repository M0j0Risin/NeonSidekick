using NeonSidekick.Plans;

namespace NeonSidekick.Tests;

/// <summary>
/// The Ralph loop's pure core (2026-10-18, <see cref="RalphLoop"/>): the cap, the next step, progress and the stops (stalled, a step failing,
/// the limit, the budget, blocked, stopped, paused), the split (once, top-level only, growing an automatic cap), the final check and its one fix
/// pass, the checkpoint's turn and its Redo.
/// </summary>
public sealed class RalphLoopTests
{
    private static RalphSnapshot Snapshot(int cap = 0, int perStep = 4, string? verify = "dotnet build", string? final = null, int checkpoint = 0, long budget = 0, bool finalOnly = false) =>
        new(".neon/plans/g.md", "/w", "Guide", verify, final, finalOnly, "pwsh", RalphReviewer.Off, checkpoint, cap, perStep, 600, budget, 1);

    private static IReadOnlyList<PlanStep> Steps(string body) => PlanSteps.Parse(body);

    private const string Three = "## Steps\n- [ ] One\n- [ ] Two\n- [ ] Three\n";

    private static RalphPassFacts Stood => new(true, false, true, false, false, false, ["a.cs"]);
    private static RalphPassFacts Failed => new(false, true, true, false, false, false, ["a.cs"]);
    private static RalphPassFacts Nothing => RalphPassFacts.None;

    private static FinishReport Done => new(RalphOutcome.Done, "did it", ["built"], null, [], []);

    [Theory]
    [InlineData(0, 3, false, 9)]
    [InlineData(0, 0, false, 1)]
    [InlineData(0, 3, true, 10)]
    [InlineData(5, 3, false, 5)]
    [InlineData(5, 3, true, 6)]   // a fixed cap keeps room for the final fix
    public void CapFor_IsThreeAStep_OrTheSetting_PlusTheFinalFix(int setting, int remaining, bool final, int expected) =>
        Assert.Equal(expected, RalphLoop.CapFor(setting, remaining, final));

    [Fact]
    public void Next_WorksTheFirstUntickedStep_AndAStepThatStands_MovesOn()
    {
        var loop = new RalphLoop(Snapshot(), 3);
        var next = loop.Next(Steps(Three), userSpoke: false);
        Assert.Equal((RalphNextKind.Step, "One"), (next.Kind, next.Step!.Text));

        loop.BeginPass(next.Step, null);
        Assert.True(loop.InPass);
        Assert.Null(loop.Record(Done));
        Assert.Equal(RalphText.AlreadyFinishedResult, loop.Record(Done));
        Assert.Null(loop.EndPass(Stood, null));

        Assert.Equal("Two", loop.Next(Steps("## Steps\n- [x] One\n- [ ] Two\n- [ ] Three\n"), false).Step!.Text);
        Assert.Equal(1, loop.Pass);
        Assert.Equal(RalphStop.Paused, loop.Next(Steps(Three), userSpoke: true).Stop);
        Assert.Equal(RalphNextKind.FinalCheck, loop.Next(Steps("## Steps\n- [x] One\n"), false).Kind);
        Assert.Equal(RalphText.NotInPassResult, loop.Record(Done));
    }

    [Fact]
    public void TwoPassesInARowWithoutProgress_Stall_AFailedTurnIsRetriedOnce()
    {
        var loop = new RalphLoop(Snapshot(), 3);
        var step = Steps(Three)[0];

        loop.BeginPass(step, null);
        Assert.Null(loop.EndPass(Nothing with { TurnFailed = true }, null));   // failed: retried
        loop.BeginPass(step, null);
        Assert.Equal(RalphStop.Stalled, loop.EndPass(Nothing with { TurnFailed = true }, null));
    }

    [Fact]
    public void AFinishedReportInAFailedTurn_IsJudgedOnItsOwn()
    {
        var loop = new RalphLoop(Snapshot(), 3);
        var step = Steps(Three)[0];

        loop.BeginPass(step, null);
        loop.Record(Done);
        Assert.Null(loop.EndPass(Stood with { TurnFailed = true }, null));
        loop.BeginPass(Steps(Three)[1], null);
        Assert.Null(loop.EndPass(Nothing with { TurnFailed = true }, null));   // one without progress: not stalled yet
    }

    [Fact]
    public void TwoFailedChecksInARow_StopOnTheStep_AndListWhatItChanged()
    {
        var loop = new RalphLoop(Snapshot(), 3);
        var step = Steps(Three)[0];

        loop.BeginPass(step, null);
        loop.Record(Done);
        Assert.Null(loop.EndPass(Failed, null));
        loop.BeginPass(step, null);
        loop.Record(Done);
        Assert.Equal(RalphStop.StepFailing, loop.EndPass(Failed with { Changed = ["b.cs"] }, null));
        Assert.Equal(["a.cs", "b.cs"], loop.ChangedBy(step));
    }

    [Fact]
    public void PartialWithChanges_IsProgress_UntilTheStepsPassesRunOut()
    {
        var loop = new RalphLoop(Snapshot(perStep: 3), 3);
        var step = Steps(Three)[0];
        var partial = new FinishReport(RalphOutcome.Partial, "half", [], null, [], []);
        var changed = new RalphPassFacts(false, false, true, false, false, false, ["a.cs"]);

        for (int i = 0; i < 2; i++)
        {
            loop.BeginPass(step, null);
            loop.Record(partial);
            Assert.Null(loop.EndPass(changed, null));
        }

        loop.BeginPass(step, null);
        loop.Record(partial);
        Assert.Equal(RalphStop.StepFailing, loop.EndPass(changed, null));
        Assert.Equal(3, loop.PassesOn(step));
    }

    [Fact]
    public void Blocked_Stopped_TheLimit_AndTheBudget()
    {
        var steps = Steps(Three);
        var blocked = new RalphLoop(Snapshot(), 3);
        blocked.BeginPass(steps[0], null);
        blocked.Record(new FinishReport(RalphOutcome.Blocked, "which database?", [], null, [], []));
        Assert.Equal(RalphStop.Blocked, blocked.EndPass(Nothing, null));

        var stopped = new RalphLoop(Snapshot(), 3);
        stopped.BeginPass(steps[0], null);
        Assert.Equal(RalphStop.Stopped, stopped.EndPass(Nothing with { Cancelled = true }, null));

        var capped = new RalphLoop(Snapshot(cap: 1), 3);
        capped.BeginPass(steps[0], null);
        capped.Record(Done);
        capped.EndPass(Stood, null);
        Assert.Equal(RalphStop.Limit, capped.Next(Steps("## Steps\n- [x] One\n- [ ] Two\n"), false).Stop);

        var budget = new RalphLoop(Snapshot(budget: 1000), 3);
        var t0 = DateTimeOffset.UnixEpoch;
        budget.BeginPass(steps[0], new PlanPassMeter(t0, 100, 0));
        budget.Record(Done);
        budget.EndPass(Stood, new PlanPassMeter(t0.AddSeconds(30), 900, 0));
        Assert.Equal(800, budget.RunTokens);
        budget.AddReviewTokens(300);
        Assert.Equal(RalphStop.Budget, budget.Next(steps, false).Stop);
        Assert.Equal((TimeSpan?)TimeSpan.FromSeconds(30), budget.History[0].Elapsed);
    }

    [Fact]
    public void ASplit_IsOnceATopLevelStep_AndGrowsAnAutomaticCap()
    {
        var steps = Steps(Three);
        var loop = new RalphLoop(Snapshot(), 3);
        Assert.Equal(9, loop.Cap);
        var split = new FinishReport(RalphOutcome.Split, "too big", [], null, [], [new("a", "x"), new("b", "y"), new("c", "z")]);

        loop.SetChildren(steps);
        loop.BeginPass(steps[0], null);
        Assert.Null(loop.Record(split));
        Assert.Null(loop.EndPass(Nothing with { Split = true }, null));
        Assert.Equal(15, loop.Cap);   // three a net new step: 3 sub-steps for 1

        // Its children may not split again.
        var after = Steps("## Steps\n- [ ] One\n  - [ ] a\n    Done when: x\n  - [ ] b\n    Done when: y\n- [ ] Two\n");
        loop.SetChildren(after);
        loop.BeginPass(PlanSteps.FirstUnticked(after), null);
        Assert.Equal(RalphText.SplitRefusedResult, loop.Record(split));

        // A fixed cap stays.
        var fixedCap = new RalphLoop(Snapshot(cap: 4), 3);
        fixedCap.SetChildren(steps);
        fixedCap.BeginPass(steps[0], null);
        fixedCap.Record(split);
        fixedCap.EndPass(Nothing with { Split = true }, null);
        Assert.Equal(4, fixedCap.Cap);
    }

    [Fact]
    public void TheFinalCheck_GetsOneFixPass_ThenFails()
    {
        var loop = new RalphLoop(Snapshot(final: "dotnet test"), 1);
        Assert.Equal(4, loop.Cap);
        var all = Steps("## Steps\n- [x] One\n");

        Assert.Equal(RalphNextKind.FinalCheck, loop.Next(all, false).Kind);
        Assert.Null(loop.FinalResult(passed: false, "1 failed"));
        Assert.Equal(RalphNextKind.FinalFix, loop.Next(all, false).Kind);
        Assert.Equal("1 failed", loop.TakeFinalTail());
        loop.BeginPass(null, null);
        Assert.True(loop.FixingFinal);
        Assert.Equal(RalphText.SplitFinalResult, loop.Record(new FinishReport(RalphOutcome.Split, "x", [], null, [], [new("a", "b"), new("c", "d")])));
        loop.Record(Done);
        Assert.Null(loop.EndPass(Stood, null));
        Assert.Equal(RalphNextKind.FinalCheck, loop.Next(all, false).Kind);
        Assert.Equal(RalphStop.FinalFailed, loop.FinalResult(passed: false, "still"));

        var passes = new RalphLoop(Snapshot(final: "dotnet test"), 1);
        Assert.Equal(RalphStop.Done, passes.FinalResult(passed: true, ""));
    }

    [Fact]
    public void FinalOnly_SkipsTheStepCheck_AndRunsVerifyAtTheEnd()
    {
        var snapshot = Snapshot(verify: "dotnet build", finalOnly: true);
        Assert.Null(snapshot.StepCheck);
        Assert.Equal("dotnet build", snapshot.FinalCheck);
        Assert.Equal("dotnet test", Snapshot(final: "dotnet test").FinalCheck);
        Assert.Null(Snapshot().FinalCheck);
    }

    [Fact]
    public void TheCheckpoint_IsDueEveryNStepsThatStand_AndRedoStartsTheStepAgain()
    {
        var loop = new RalphLoop(Snapshot(checkpoint: 2, perStep: 2), 3);
        var steps = Steps(Three);
        loop.BeginPass(steps[0], null);
        loop.Record(Done);
        loop.EndPass(Stood, null);
        Assert.False(loop.CheckpointDue);
        loop.BeginPass(steps[1], null);
        loop.Record(Done);
        loop.EndPass(Stood, null);
        Assert.True(loop.CheckpointDue);

        // A Redo is no failed check: the step's passes start over.
        loop.BeginPass(steps[2], null);
        loop.Record(Done);
        loop.EndPass(Failed, null);
        loop.Redo(steps[2], "use tabs");
        Assert.Equal(RalphText.RedoNote("use tabs"), loop.TakeNotes());
        Assert.Null(loop.TakeNotes());
        loop.BeginPass(steps[2], null);
        loop.Record(Done);
        Assert.Null(loop.EndPass(Failed, null));   // the first failure again, not the second
    }
}
