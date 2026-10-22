using NeonSidekick.App;
using NeonSidekick.Files;
using NeonSidekick.Plans;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// The Ralph judge's put-back after a pass (2026-10-19, the third review, <see cref="RalphJudge.Restore"/>): every other step's box and every step's
/// words as they were before the pass, the pass's own box as the model left it (or put back when the pass was stopped), a moved line kept as it was,
/// and the unfinished steps the pass deleted named.
/// </summary>
public sealed class RalphJudgeTests
{
    private const string Before =
        "# Guide\n\n## Steps\n- [x] Zero\n- [ ] One\n  Done when: one.md exists\n- [ ] Two\n- [ ] Three\n- [ ] Four\n";

    private static IReadOnlyList<PlanStep> Lines(string text) => PlanSteps.Flat(PlanSteps.Parse(text));

    private static PlanStep Step(string text, string words) => Lines(text).Single(s => s.Text == words);

    [Fact]
    public void AWeakenedVerifyOrFinalLine_IsPutBack_TheBodyLeftAsItIs()
    {
        // 2026-10-20, a review: a pass could rewrite the header's checks, and a later run by name took them as the plan's.
        var header = new PlanHeader(PlanStatus.Approved, 2, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "Guide", Verify: "dotnet build", Final: "dotnet test");
        string before = PlanDocument.Render(header, "Guide", Before);
        string after = PlanDocument.Render(header with { Verify = "exit 0", Final = null }, "Guide", Before);

        var restore = RalphJudge.Restore(after, Lines(before), Step(before, "One"), ownBoxBack: false, checks: new PlanChecks("dotnet build", "dotnet test"));

        Assert.True(restore.ChecksPutBack);
        Assert.Equal(before, restore.Text);
        Assert.Contains(RalphText.ChecksPutBackNotice, RalphJudge.RestoreNotices(restore, null));
        Assert.False(RalphJudge.Restore(before, Lines(before), Step(before, "One"), ownBoxBack: false, checks: new PlanChecks("dotnet build", "dotnet test")).ChecksPutBack);
    }

    [Fact]
    public void AnInPassMark_OverAPlanChangedSince_PutsNothingBack()
    {
        // 2026-10-20, a review: a mark left by a crash put days-old lines back over a plan revised, approved or run since.
        string dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var files = new WorkingDirectory(() => dir, new ManualTimeProvider());
            const string plan = ".neon/plans/guide.md";
            var header = new PlanHeader(PlanStatus.Approved, 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "Guide");
            string text = PlanDocument.Render(header, "Guide", Before);
            Assert.Equal(FileOutcome.Ok, files.WriteText(plan, text, overwrite: true).Outcome);
            Assert.Null(RalphLog.SetHead(files, plan, new RalphLogHead(plan, null, null, false, "pwsh", DateTimeOffset.UnixEpoch)));
            Assert.Null(RalphLog.MarkInPass(files, plan, Step(text, "One"), Lines(text), headed: true, planStamp: PlanDocument.Stamp(text)));
            // Revised since (a new revision and time), its steps the user's now.
            string revised = PlanDocument.Render(header with { Revision = 2, Updated = DateTimeOffset.UnixEpoch.AddDays(2) }, "Guide",
                Before.Replace("- [ ] Two\n", "- [x] Two, done by a plain run\n- [ ] Two and a half\n", StringComparison.Ordinal));
            Assert.Equal(FileOutcome.Ok, files.WriteText(plan, revised, overwrite: true).Outcome);

            var recovered = RalphJudge.RecoverInPass(files, plan)!;

            Assert.False(recovered.Changed);
            Assert.Equal([RalphText.InPassStaleNotice("One")], recovered.Notices);
            Assert.Equal(revised, File.ReadAllText(Path.Combine(dir, ".neon", "plans", "guide.md")));
            Assert.False(RalphLog.InPass(RalphLog.Read(files, plan)!.Head));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void RewordedLines_GetTheirWordsBack_AnotherBoxIsPutBack_AndTheOwnTickStays()
    {
        string after = Before.Replace("- [ ] One", "- [x] One (done in one.md)", StringComparison.Ordinal)
            .Replace("- [ ] Three", "- [x] Three, trivially", StringComparison.Ordinal);

        var restore = RalphJudge.Restore(after, Lines(Before), Step(Before, "One"), ownBoxBack: false);

        Assert.Equal(Before.Replace("- [ ] One", "- [x] One", StringComparison.Ordinal), restore.Text);
        Assert.Equal(1, restore.BoxesPutBack);
        Assert.Equal(2, restore.WordsPutBack);
        Assert.False(restore.OwnBoxPutBack);
        Assert.True(restore.Own is { Text: "One", Ticked: true });
        Assert.Empty(restore.Removed);
    }

    [Fact]
    public void AStoppedPass_HasItsOwnTickPutBackToo()
    {
        string after = Before.Replace("- [ ] One", "- [x] One", StringComparison.Ordinal);

        var restore = RalphJudge.Restore(after, Lines(Before), Step(Before, "One"), ownBoxBack: true);

        Assert.Equal(Before, restore.Text);
        Assert.True(restore.OwnBoxPutBack);
        Assert.Equal(0, restore.BoxesPutBack);
    }

    [Fact]
    public void DeletedUnfinishedSteps_AreNamed_ADeletedFinishedOneIsNot()
    {
        string after = Before.Replace("- [x] Zero\n", "", StringComparison.Ordinal)
            .Replace("- [ ] Three\n- [ ] Four\n", "", StringComparison.Ordinal)
            .Replace("- [ ] One", "- [x] One", StringComparison.Ordinal);

        var restore = RalphJudge.Restore(after, Lines(Before), Step(Before, "One"), ownBoxBack: false);

        Assert.Equal(["Three", "Four"], restore.Removed);
        Assert.Equal(after, restore.Text);   // nothing else to put back
    }

    [Fact]
    public void AMovedLine_KeepsItsBox_AndANewLineIsTakenOut()
    {
        // 2026-10-19, the fourth review: a new line was kept unticked, a step nobody approved that later passes carried out.
        string after = "# Guide\n\n## Steps\n- [ ] One\n  Done when: one.md exists\n- [ ] Two\n- [ ] Three\n- [ ] Four\n- [x] Zero\n- [x] Five, new\n  Done when: five.md\n";

        var restore = RalphJudge.Restore(after, Lines(Before), Step(Before, "One"), ownBoxBack: false);

        Assert.Equal("# Guide\n\n## Steps\n- [ ] One\n  Done when: one.md exists\n- [ ] Two\n- [ ] Three\n- [ ] Four\n- [x] Zero\n", restore.Text);
        Assert.Equal(0, restore.BoxesPutBack);
        Assert.Equal(1, restore.Added);
        Assert.Empty(restore.Removed);
    }

    [Fact]
    public void AStepRewordedBesideAnAddedLine_GetsItsWordsBack_AndTheAddedLineGoes()
    {
        // 2026-10-19, the fourth review: a stretch of 1 line before and 2 after paired nothing, so the reworded step read as deleted and stood reworded.
        string after = Before.Replace("- [ ] One\n", "- [x] One, written up\n  - [ ] run scripts/setup.ps1 --force\n", StringComparison.Ordinal);

        var restore = RalphJudge.Restore(after, Lines(Before), Step(Before, "One"), ownBoxBack: false);

        Assert.Equal(Before.Replace("- [ ] One", "- [x] One", StringComparison.Ordinal), restore.Text);
        Assert.True(restore.Own is { Text: "One", Ticked: true });
        Assert.Equal(1, restore.WordsPutBack);
        Assert.Equal(1, restore.Added);
        Assert.Empty(restore.Removed);
    }

    [Fact]
    public void ARunThatEndedMidPass_HasThePlanPutBack_OtherBoxesAndAddedLinesToo_AndThePlanAsLeftKept()
    {
        // 2026-10-19, the fourth review: a crash mid-pass put back only the pass's own box; the boxes it ticked on later steps stood, unchecked.
        string dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var files = new WorkingDirectory(() => dir, new ManualTimeProvider());
            const string plan = ".neon/plans/guide.md";
            Assert.Equal(FileOutcome.Ok, files.WriteText(plan, Before, overwrite: true).Outcome);
            Assert.Null(RalphLog.SetHead(files, plan, new RalphLogHead(plan, null, null, false, "pwsh", DateTimeOffset.UnixEpoch)));
            Assert.Null(RalphLog.MarkInPass(files, plan, Step(Before, "One"), Lines(Before), headed: true));
            string crashed = Before.Replace("- [ ] One", "- [x] One", StringComparison.Ordinal).Replace("- [ ] Three", "- [x] Three", StringComparison.Ordinal)
                .Replace("- [ ] Four\n", "- [x] Four\n- [ ] Five\n", StringComparison.Ordinal);
            File.WriteAllText(Path.Combine(dir, ".neon", "plans", "guide.md"), crashed);

            var recovered = RalphJudge.RecoverInPass(files, plan)!;

            Assert.True(recovered.Changed);
            Assert.Empty(recovered.Removed);
            Assert.Equal(Before, File.ReadAllText(Path.Combine(dir, ".neon", "plans", "guide.md")));
            Assert.Contains(RalphText.InPassPutBackNotice("One"), recovered.Notices);
            Assert.Contains(RalphText.BoxesPutBackNotice(2), recovered.Notices);
            Assert.Contains(RalphText.StepsAddedNotice(1), recovered.Notices);
            Assert.Contains(RalphText.AsLeftNotice(RalphLog.AsLeftPathFor(plan)), recovered.Notices);
            Assert.Equal(crashed.TrimEnd(), File.ReadAllText(Path.Combine(dir, ".neon", "plans", "guide", RalphLog.AsLeftName)).TrimEnd());
            Assert.False(RalphLog.InPass(RalphLog.Read(files, plan)!.Head));
            Assert.Null(RalphJudge.RecoverInPass(files, plan));   // once
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void ARunThatEndedMidPass_HavingDeletedSteps_NamesThem()
    {
        string dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var files = new WorkingDirectory(() => dir, new ManualTimeProvider());
            const string plan = ".neon/plans/guide.md";
            Assert.Equal(FileOutcome.Ok, files.WriteText(plan, Before, overwrite: true).Outcome);
            Assert.Null(RalphLog.SetHead(files, plan, new RalphLogHead(plan, null, null, false, "pwsh", DateTimeOffset.UnixEpoch)));
            Assert.Null(RalphLog.MarkInPass(files, plan, Step(Before, "One"), Lines(Before), headed: true));
            File.WriteAllText(Path.Combine(dir, ".neon", "plans", "guide.md"), Before.Replace("- [ ] Three\n", "", StringComparison.Ordinal));

            Assert.Equal(["Three"], RalphJudge.RecoverInPass(files, plan)!.Removed);
            Assert.False(RalphLog.InPass(RalphLog.Read(files, plan)!.Head));   // the next start runs
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void TheInPassStep_OfWordsTwoLinesShare_IsFoundAfterALineAbove()
    {
        // 2026-10-19, the fourth review: rebuilt as the only one of its words, the second twin was lost once the model added a line above it.
        const string twins = "# Guide\n\n## Steps\n- [x] Update the tests\n- [ ] Write it\n- [ ] Update the tests\n";
        var lines = Lines(twins);
        var second = lines[2];
        var head = new RalphLogHead(".neon/plans/g.md", null, null, false, "pwsh", DateTimeOffset.UnixEpoch, InPassLine: second.Line, InPassStep: second.Text, InPassLines: lines);

        var step = RalphLog.InPassOf(head)!;
        string moved = twins.Replace("## Steps\n", "## Steps\nA note.\n", StringComparison.Ordinal).Replace("- [ ] Update the tests\n", "- [x] Update the tests\n", StringComparison.Ordinal);

        Assert.Equal((1, 2), (step.Occurrence, step.Twins));
        Assert.True(PlanSteps.Find(moved, step) is { Ticked: true } found && found.Line == second.Line + 1);
    }

    [Fact]
    public void APassLeftUnjudged_HasItsTickPutBack_AndTheLogsMarkCleared()
    {
        // A loop that left with the conversation (a profile switch) mid-pass: the pass is never judged.
        string dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var files = new WorkingDirectory(() => dir, new ManualTimeProvider());
            const string plan = ".neon/plans/guide.md";
            Assert.Equal(FileOutcome.Ok, files.WriteText(plan, Before, overwrite: true).Outcome);
            var lines = Lines(Before);
            var step = Step(Before, "One");
            Assert.Null(RalphLog.SetHead(files, plan, new RalphLogHead(plan, null, null, false, "pwsh", DateTimeOffset.UnixEpoch)));
            Assert.Null(RalphLog.MarkInPass(files, plan, step, lines, headed: true));
            File.WriteAllText(Path.Combine(dir, ".neon", "plans", "guide.md"), Before.Replace("- [ ] One", "- [x] One, all done", StringComparison.Ordinal));

            Assert.True(RalphJudge.PutBackUnjudged(files, plan, lines, step, headed: true));

            Assert.Equal(Before.TrimEnd(), File.ReadAllText(Path.Combine(dir, ".neon", "plans", "guide.md")).TrimEnd());
            Assert.Null(RalphLog.Read(files, plan)!.Head!.InPassStep);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    private const string WithAcceptance = Before + "\n## Acceptance\n- [ ] p95 under 200 ms\n  Notes: measured on the bench\n- [ ] docs updated\n";

    [Fact]
    public void ARenamedStepsHeading_IsPutBack_AndAnotherSectionsChecklistStays()
    {
        // 2026-10-19, the fifth review: renamed, every task line of the file read as a step, and Acceptance's were taken out as lines the pass added.
        string after = WithAcceptance.Replace("## Steps", "## Steps (revised)", StringComparison.Ordinal).Replace("- [ ] One", "- [x] One", StringComparison.Ordinal);

        var restore = RalphJudge.Restore(after, Lines(WithAcceptance), Step(WithAcceptance, "One"), ownBoxBack: false, headed: true);

        Assert.Equal(WithAcceptance.Replace("- [ ] One", "- [x] One", StringComparison.Ordinal), restore.Text);
        Assert.True(restore.HeadingPutBack);
        Assert.Equal(0, restore.Added);
        Assert.Empty(restore.Removed);
        Assert.True(restore.Own is { Text: "One", Ticked: true });
        Assert.Contains(RalphText.StepsHeadingPutBackNotice, RalphJudge.RestoreNotices(restore, null));
    }

    [Fact]
    public void ADeletedStepsHeading_IsPutBackAboveTheSteps()
    {
        string after = WithAcceptance.Replace("## Steps\n", "", StringComparison.Ordinal);

        var restore = RalphJudge.Restore(after, Lines(WithAcceptance), Step(WithAcceptance, "One"), ownBoxBack: false, headed: true);

        Assert.Equal(WithAcceptance, restore.Text);
        Assert.Equal(0, restore.Added);
        Assert.Empty(restore.Removed);
    }

    [Fact]
    public void AStepsHeadingAddedToAPlanWithout_IsTakenOut()
    {
        const string plain = "# Guide\n\n- [x] Zero\n- [ ] One\n- [ ] Two\n";
        string after = plain.Replace("- [ ] Two\n", "## Steps\n- [ ] Two\n", StringComparison.Ordinal);

        var restore = RalphJudge.Restore(after, Lines(plain), Step(plain, "One"), ownBoxBack: false, headed: false);

        Assert.Equal(plain, restore.Text);
        Assert.True(restore.HeadingPutBack);
    }

    [Fact]
    public void AWeakenedDoneWhen_IsPutBack_TheOwnStepsAndALaterOnes()
    {
        // 2026-10-19, the fifth review: only the task lines went back, so a pass could weaken a check and the passes after it read the weak one.
        const string plan = "# Guide\n\n## Steps\n- [ ] One\n  - **Done when:** dotnet test passes\n- [ ] Two\n- [ ] Three\n  Done when: three.md exists\n";
        string after = plan.Replace("dotnet test passes", "the file exists", StringComparison.Ordinal)
            .Replace("- [ ] Two\n", "- [ ] Two\n  Done when: nothing\n", StringComparison.Ordinal)
            .Replace("  Done when: three.md exists\n", "", StringComparison.Ordinal);

        var restore = RalphJudge.Restore(after, Lines(plan), Step(plan, "One"), ownBoxBack: false, headed: true);

        Assert.Equal(plan, restore.Text);
        Assert.Equal(3, restore.DoneWhenPutBack);
        Assert.True(restore.Own is { Text: "One", DoneWhen: "dotnet test passes" });
        Assert.Contains(RalphText.DoneWhenPutBackNotice(3), RalphJudge.RestoreNotices(restore, null));
    }

    [Fact]
    public void AnOldLogsLines_LeaveTheDoneWhenAlone()
    {
        // A log the previous build wrote kept no Done when: lines: none is taken out for it.
        string after = Before.Replace("one.md exists", "anything", StringComparison.Ordinal);
        var bare = Lines(Before).Select(l => l with { DoneWhen = null }).ToList();

        var restore = RalphJudge.Restore(after, bare, Step(Before, "One"), ownBoxBack: false, headed: null, doneWhenKnown: false);

        Assert.Equal(after, restore.Text);
        Assert.Equal(0, restore.DoneWhenPutBack);
    }

    [Fact]
    public void TheFinalFix_HasNoOwnLine_EveryChangedBoxGoesBack()
    {
        string done = Before.Replace("[ ]", "[x]", StringComparison.Ordinal);
        string after = done.Replace("- [x] Two", "- [ ] Two", StringComparison.Ordinal);

        var restore = RalphJudge.Restore(after, Lines(done), null, ownBoxBack: false);

        Assert.Equal(done, restore.Text);
        Assert.Null(restore.Own);
    }

    [Fact]
    public void LogPass_KeepsTheInPassMark_OnlyWhenThePlanWasNotRead()
    {
        // 2026-10-20, a review: a plan the judge could not read lost the mark that held its lines from before the pass, the only copy.
        string dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var files = new WorkingDirectory(() => dir, new ManualTimeProvider());
            const string plan = ".neon/plans/guide.md";
            var snapshot = new RalphSnapshot(plan, dir, "Guide", null, null, false, "pwsh", RalphReviewer.Off, 0, 0, 4, 600, 0, 1);
            var loop = new RalphLoop(snapshot, 4);
            var step = Step(Before, "One");
            Assert.Null(RalphLog.SetHead(files, plan, RalphStart.Head(snapshot, DateTimeOffset.UnixEpoch)));

            loop.BeginPass(step, null);
            Assert.Null(RalphLog.MarkInPass(files, plan, step, Lines(Before), headed: true));
            var unread = new RalphJudged(RalphPassFacts.None, "none", null, "off", [], "", PlanUnread: true);
            loop.EndPass(unread.Facts, null);
            Assert.Null(RalphJudge.LogPass(files, loop, step, unread, TimeProvider.System));
            Assert.True(RalphLog.InPass(RalphLog.Read(files, plan)!.Head));

            loop.BeginPass(step, null);
            var judged = new RalphJudged(RalphPassFacts.None, "none", null, "off", [], "");
            loop.EndPass(judged.Facts, null);
            Assert.Null(RalphJudge.LogPass(files, loop, step, judged, TimeProvider.System));
            Assert.False(RalphLog.InPass(RalphLog.Read(files, plan)!.Head));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    // ── The by-hand question (2026-10-21, the plan at ~/.claude/plans/i-put-you-in-snoopy-perlis.md) ──

    private const string ByHandBody =
        "# Guide\n\n## Steps\n- [ ] Write hello\n  Done when: `hello.py` exists\n" +
        "- [ ] Animate it\n  Done when: By hand: running python hello.py in a real terminal shows the animation\n- [ ] Docs\n  Done when: README names it\n";

    /// <summary>One judged pass over <see cref="ByHandBody"/>'s step of the words given: the model reported done and ticked it, a file changed.</summary>
    private sealed class ByHandRun : IDisposable
    {
        public const string Plan = ".neon/plans/guide.md";

        private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));

        public ByHandRun(string words, RalphReviewer reviewer, int checkpoint = 0)
        {
            Files = new WorkingDirectory(() => _dir, Time);
            Host = new FakeRalphHost(Files, Time);
            var header = new PlanHeader(PlanStatus.Approved, 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "Guide");
            string text = PlanDocument.Render(header, "Guide", ByHandBody);
            Assert.Equal(FileOutcome.Ok, Files.WriteText(Plan, text, overwrite: true).Outcome);
            var steps = PlanSteps.Parse(text);
            Step = PlanSteps.Leaves(steps).Single(s => s.Text == words);
            Lines = PlanSteps.Flat(steps);
            Before = RalphChanges.Snapshot(Files, null);
            Loop = new RalphLoop(new RalphSnapshot(Plan, _dir, "Guide", null, null, false, "pwsh", reviewer, checkpoint, 0, 3, 60, 0, 1), 3);
            Loop.BeginPass(Step, new PlanPassMeter(Time.GetUtcNow(), 0, 0));
            Assert.Null(Loop.Record(new FinishReport(RalphOutcome.Done, "the pass did it", ["ran it"], null, [], [])));
            Assert.Equal(FileOutcome.Ok, Files.WriteText("hello.py", "print('hi')\n", overwrite: true).Outcome);
            Assert.Equal(FileOutcome.Ok, Files.WriteText(Plan, PlanSteps.WithBox(text, Step, true)!, overwrite: true).Outcome);
        }

        public ManualTimeProvider Time { get; } = new();

        public WorkingDirectory Files { get; }

        public FakeRalphHost Host { get; }

        public PlanStep Step { get; }

        public IReadOnlyList<PlanStep> Lines { get; }

        public RalphSnapshotOfFiles Before { get; }

        public RalphLoop Loop { get; }

        public Task<RalphJudged> JudgeAsync(CancellationToken cancellationToken = default) =>
            RalphJudge.JudgeAsync(Host, Loop, Step, 3, Lines, headed: true, Before, false, false, false, "req", Time, cancellationToken);

        /// <summary>The step's box in the plan file now.</summary>
        public bool Ticked => PlanSteps.Leaves(PlanSteps.Parse(PlanFiles.ReadWhole(Files, Plan)!)).Single(s => s.Text == Step.Text).Ticked;

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task AByHandStep_AnsweredItWorks_Stands_AndItsWaitIsLeftOutOfThePass()
    {
        using var run = new ByHandRun("Animate it", RalphReviewer.Model);
        run.Host.Answers.Enqueue(new RalphCheckpointAnswer(RalphCheckpointChoice.Keep, null));

        var judged = await run.JudgeAsync();

        Assert.Equal(RalphHandCheck.Passed, judged.Facts.HandCheck);
        Assert.True(judged.Facts.Stands);
        Assert.True(run.Ticked);
        Assert.Equal(TimeSpan.FromMinutes(3), judged.Facts.Waited);
        Assert.Contains(RalphText.StepCheckedByYouNotice(2, 3, "Animate it"), run.Host.Notices);
        var asked = Assert.Single(run.Host.Asked);
        Assert.Equal("the pass did it", asked.Summary);
        Assert.Null(asked.Reason);
        Assert.Equal(judged.Diff, asked.Detail);
        Assert.Contains("hello.py", judged.Facts.Changed);
        Assert.Equal(1, run.Host.Reviews);   // the review ran first

        // The pass's own time leaves the wait out, and the log keeps the answer in its checkpoint field, the review in its own.
        run.Time.Advance(TimeSpan.FromSeconds(30));
        Assert.Null(run.Loop.EndPass(judged.Facts, new PlanPassMeter(run.Time.GetUtcNow(), 0, 0)));
        Assert.Equal(TimeSpan.FromSeconds(30), run.Loop.History[^1].Elapsed);
        Assert.Equal("done by hand", run.Loop.History[^1].Outcome);
        Assert.False(run.Loop.CheckpointDue);
        Assert.Null(RalphJudge.LogPass(run.Files, run.Loop, run.Step, judged, run.Time));
        var entry = RalphLog.Read(run.Files, ByHandRun.Plan)!.Entries[^1];
        Assert.Equal(RalphText.ByHandWorksLog, entry.Checkpoint);
        Assert.StartsWith("pass", entry.Review, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("redo")]
    [InlineData("stop")]
    [InlineData("nobody")]
    public async Task AByHandStep_AnsweredAnythingElse_IsUnticked(string answer)
    {
        using var run = new ByHandRun("Animate it", RalphReviewer.Off);
        if (answer != "nobody")
        {
            run.Host.Answers.Enqueue(answer == "redo" ? new RalphCheckpointAnswer(RalphCheckpointChoice.Redo, "the frames flicker") : new RalphCheckpointAnswer(RalphCheckpointChoice.Stop, null));
        }

        var judged = await run.JudgeAsync();

        Assert.False(judged.Facts.Stands);
        Assert.False(run.Ticked);
        Assert.Equal(0, run.Host.Reviews);   // the reviewer off: asked straight away
        Assert.Single(run.Host.Asked);
        var (check, notice, stop, log) = answer switch
        {
            "redo" => (RalphHandCheck.Redo, RalphText.ByHandRedoNotice, (RalphStop?)null, "by hand: redo: the frames flicker"),
            "stop" => (RalphHandCheck.Stop, RalphText.ByHandStoppedNotice, RalphStop.UserStopped, RalphText.ByHandStoppedLog),
            _ => (RalphHandCheck.NoOneToAsk, RalphText.NeedsYouNotice(2, run.Step.DoneWhen!), RalphStop.NeedsUser, RalphText.NeedsYouLog),
        };
        Assert.Equal(check, judged.Facts.HandCheck);
        Assert.Contains(notice, run.Host.Notices);
        Assert.Equal(answer == "redo" ? "the frames flicker" : null, judged.ByHand?.Note);
        Assert.Equal(stop, run.Loop.EndPass(judged.Facts, null));
        Assert.Null(RalphJudge.LogPass(run.Files, run.Loop, run.Step, judged, run.Time));
        Assert.Equal(log, RalphLog.Read(run.Files, ByHandRun.Plan)!.Entries[^1].Checkpoint);
    }

    [Fact]
    public async Task AnUnmarkedStep_TheReviewerSaysNeedsUser_IsAsked_WithTheReason_UnderACheckpoint()
    {
        using var run = new ByHandRun("Write hello", RalphReviewer.Model, checkpoint: 1);
        run.Host.Verdict = new RalphVerdict(RalphVerdictKind.NeedsUser, "only a person can see the window");
        run.Host.Answers.Enqueue(new RalphCheckpointAnswer(RalphCheckpointChoice.Keep, null));

        var judged = await run.JudgeAsync();

        Assert.Equal(RalphHandCheck.Passed, judged.Facts.HandCheck);
        Assert.Equal("only a person can see the window", Assert.Single(run.Host.Asked).Reason);
        Assert.Equal("needs user: only a person can see the window", judged.Review);
        Assert.Contains(RalphText.ReviewNeedsUserNotice("only a person can see the window"), run.Host.Notices);
    }

    [Fact]
    public async Task AnUnmarkedStep_AnUnclearReview_StandsAsBefore_NotAsked()
    {
        using var run = new ByHandRun("Write hello", RalphReviewer.Model);
        run.Host.Verdict = RalphVerdict.Unclear();

        var judged = await run.JudgeAsync();

        Assert.True(judged.Facts.Stands);
        Assert.Equal(RalphHandCheck.None, judged.Facts.HandCheck);
        Assert.Empty(run.Host.Asked);
        Assert.Contains(RalphText.ReviewUnclearNotice, run.Host.Notices);
    }

    [Fact]
    public async Task AMarkedStep_AFailedReview_UnticksWithoutAsking_AndTheReasonIsKeptForTheStop()
    {
        using var run = new ByHandRun("Animate it", RalphReviewer.Model);
        run.Host.Verdict = new RalphVerdict(RalphVerdictKind.Fail, "no animation loop in the code");
        run.Host.Answers.Enqueue(new RalphCheckpointAnswer(RalphCheckpointChoice.Keep, null));

        var judged = await run.JudgeAsync();

        Assert.False(judged.Facts.Stands);
        Assert.False(run.Ticked);
        Assert.Empty(run.Host.Asked);
        Assert.Equal(RalphHandCheck.None, judged.Facts.HandCheck);
        Assert.Equal("no animation loop in the code", run.Loop.LastReviewFail);
        run.Loop.EndPass(judged.Facts, null);
        run.Loop.BeginPass(run.Step, null);
        Assert.Null(run.Loop.LastReviewFail);   // cleared each pass, never a stale reason
    }

    [Fact]
    public async Task AMarkedStep_AnUnclearReview_IsAsked()
    {
        using var run = new ByHandRun("Animate it", RalphReviewer.Model);
        run.Host.Verdict = RalphVerdict.Unclear();
        run.Host.Answers.Enqueue(new RalphCheckpointAnswer(RalphCheckpointChoice.Keep, null));

        var judged = await run.JudgeAsync();

        Assert.Single(run.Host.Asked);
        Assert.Equal(RalphHandCheck.Passed, judged.Facts.HandCheck);
        Assert.DoesNotContain(RalphText.ReviewUnclearNotice, run.Host.Notices);
    }

    [Fact]
    public async Task ACancelUnderTheQuestion_IsAStop_EvenWhenItsAnswerWasItWorks_AndKeepsTheQuestionForTheResume()
    {
        using var run = new ByHandRun("Animate it", RalphReviewer.Off);
        using var cts = new CancellationTokenSource();
        run.Host.OnAsk = cts.Cancel;
        run.Host.Answers.Enqueue(new RalphCheckpointAnswer(RalphCheckpointChoice.Keep, null));

        var judged = await run.JudgeAsync(cts.Token);

        Assert.True(judged.Facts.Cancelled);
        Assert.False(judged.Facts.Stands);
        Assert.False(run.Ticked);
        Assert.Equal(TimeSpan.FromMinutes(3), judged.Facts.Waited);
        Assert.Equal(RalphStop.Stopped, run.Loop.EndPass(judged.Facts, null));
        // 2026-10-22, a review: the page's Stop wrote no mark, and a resume ran the finished pass again before asking.
        Assert.Equal(RalphHandCheck.Stop, judged.Facts.HandCheck);
        Assert.Null(RalphJudge.LogPass(run.Files, run.Loop, run.Step, judged, run.Time));
        Assert.Equal(RalphText.ByHandStoppedLog, RalphLog.Read(run.Files, ByHandRun.Plan)!.Entries[^1].Checkpoint);
    }

    // ── 2026-10-22, a review ─────────────────────────────────────────────────

    [Fact]
    public async Task AnUnmarkedStep_TheReviewerSaysNeedsUser_UnderCheckpoint0_StandsUnchecked_NotAsked()
    {
        // The user's call: an unattended loop is never stopped by a model's judgment.
        using var run = new ByHandRun("Write hello", RalphReviewer.Model);
        run.Host.Verdict = new RalphVerdict(RalphVerdictKind.NeedsUser, "only a person can see the window");

        var judged = await run.JudgeAsync();

        Assert.True(judged.Facts.Stands);
        Assert.True(run.Ticked);
        Assert.Equal(RalphHandCheck.None, judged.Facts.HandCheck);
        Assert.Empty(run.Host.Asked);
        Assert.Contains(RalphText.ReviewNeedsUserUncheckedNotice("only a person can see the window"), run.Host.Notices);
        Assert.Null(run.Loop.EndPass(judged.Facts, null));
    }

    [Fact]
    public async Task AByHandStep_StaysUnticked_WhileTheQuestionWaits_AndIsAskedWithTheStepCount()
    {
        using var run = new ByHandRun("Animate it", RalphReviewer.Off);
        bool? tickedWhileAsked = null;
        run.Host.OnAsk = () => tickedWhileAsked = run.Ticked;
        run.Host.Answers.Enqueue(new RalphCheckpointAnswer(RalphCheckpointChoice.Keep, null));

        var judged = await run.JudgeAsync();

        Assert.False(tickedWhileAsked);   // the model ticked it; the plan shows it open until the user says it works
        Assert.True(run.Ticked);
        Assert.True(judged.Facts.Stands);
        Assert.Equal([3], run.Host.AskedTotals);
    }

    [Fact]
    public async Task AByHandStep_WithNobodyToAsk_RunsNoReview_AndNeedsYou()
    {
        using var run = new ByHandRun("Animate it", RalphReviewer.Model);
        run.Host.CanAsk = false;

        var judged = await run.JudgeAsync();

        Assert.Equal(0, run.Host.Reviews);
        Assert.Empty(run.Host.Asked);
        Assert.False(run.Ticked);
        Assert.Equal(RalphHandCheck.NoOneToAsk, judged.Facts.HandCheck);
        Assert.Contains(RalphText.NeedsYouNotice(2, "running python hello.py in a real terminal shows the animation"), run.Host.Notices);
        Assert.Equal(RalphStop.NeedsUser, run.Loop.EndPass(judged.Facts, null));
    }

    [Fact]
    public async Task AByHandRedosNote_IsRedacted_InTheLog()
    {
        using var run = new ByHandRun("Animate it", RalphReviewer.Off);
        run.Host.Answers.Enqueue(new RalphCheckpointAnswer(RalphCheckpointChoice.Redo, "it fails: sqlcmd -S db -U sa -P Hunter2"));

        var judged = await run.JudgeAsync();
        run.Loop.EndPass(judged.Facts, null);

        Assert.Null(RalphJudge.LogPass(run.Files, run.Loop, run.Step, judged, run.Time));
        string checkpoint = RalphLog.Read(run.Files, ByHandRun.Plan)!.Entries[^1].Checkpoint!;
        Assert.DoesNotContain("Hunter2", checkpoint, StringComparison.Ordinal);
        Assert.Contains("-P <redacted>", checkpoint, StringComparison.Ordinal);
    }
}
