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
}
