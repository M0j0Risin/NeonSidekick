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
    public void AMovedLine_KeepsItsBox_AndANewLineIsUnticked()
    {
        string after = "# Guide\n\n## Steps\n- [ ] One\n  Done when: one.md exists\n- [ ] Two\n- [ ] Three\n- [ ] Four\n- [x] Zero\n- [x] Five, new\n";

        var restore = RalphJudge.Restore(after, Lines(Before), Step(Before, "One"), ownBoxBack: false);

        Assert.Contains("- [x] Zero\n", restore.Text);
        Assert.Contains("- [ ] Five, new\n", restore.Text);
        Assert.Equal(1, restore.BoxesPutBack);
        Assert.Empty(restore.Removed);
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
            Assert.Null(RalphLog.MarkInPass(files, plan, step));
            File.WriteAllText(Path.Combine(dir, ".neon", "plans", "guide.md"), Before.Replace("- [ ] One", "- [x] One, all done", StringComparison.Ordinal));

            Assert.True(RalphJudge.PutBackUnjudged(files, plan, lines, step));

            Assert.Equal(Before.TrimEnd(), File.ReadAllText(Path.Combine(dir, ".neon", "plans", "guide.md")).TrimEnd());
            Assert.Null(RalphLog.Read(files, plan)!.Head!.InPassStep);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
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
