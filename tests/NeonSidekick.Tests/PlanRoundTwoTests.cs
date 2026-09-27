using NeonSidekick.App;
using NeonSidekick.Files;
using NeonSidekick.Plans;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>Plan mode's second round (2026-09-26): progress, the plan-like reply, the progress header, finding and listing plans, the parser's two new words, opening a plan.</summary>
public class PlanRoundTwoTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new();
    private readonly WorkingDirectory _files;

    private static readonly DateTimeOffset T0 = new(2026, 9, 26, 10, 0, 0, TimeSpan.FromHours(1));

    public PlanRoundTwoTests()
    {
        _files = new WorkingDirectory(() => _dir, _time);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private void WritePlan(string name, string text)
    {
        Directory.CreateDirectory(Path.Combine(_dir, ".neon", "plans"));
        File.WriteAllText(Path.Combine(_dir, ".neon", "plans", name), text);
    }

    // ── Progress and the plan-like reply ────────────────────────────────────

    [Fact]
    public void Progress_CountsTaskLines_TickedOrNot_OutsideCodeFences()
    {
        string markdown = """
            # Plan
            - [x] one
            - [ ] two
              * [X] nested, ticked
            + [ ] plus
            1. [x] numbered
            2) [ ] numbered with a paren
            - [-] not a box
            - [ ]no space after is not a box
            -[ ] no space before is not a list item
            ```
            - [ ] in a fence
            ```
            ~~~md
            - [x] in a tilde fence
            ~~~
            """;

        Assert.Equal((3, 6), PlanDocument.Progress(markdown));
        Assert.Equal((0, 0), PlanDocument.Progress("no boxes at all"));
    }

    [Theory]
    [InlineData("- [ ] a\n- [ ] b", true)]
    [InlineData("# Plan\n1. a\n2. b\n3. c", true)]
    [InlineData("## Steps\n- a\n- b\n- c", true)]
    [InlineData("- a\n- b\n- c", false)]
    [InlineData("# Title\n- a\n- b", false)]
    [InlineData("- [ ] only one", false)]
    [InlineData("What should the guide cover?", false)]
    [InlineData("```\n# not a heading\n- a\n- b\n- c\n```", false)]
    public void LooksLikePlan_TwoTasks_OrAHeadingAndThreeItems(string reply, bool expected) =>
        Assert.Equal(expected, PlanDocument.LooksLikePlan(reply));

    [Fact]
    public void FirstHeading_IsTheFirstHeadingsText()
    {
        Assert.Equal("Guide", PlanDocument.FirstHeading("intro\n\n## Guide\n# Later"));
        Assert.Null(PlanDocument.FirstHeading("no heading\n#\n"));
    }

    // ── The header's progress line and the two new statuses ─────────────────

    [Fact]
    public void Header_ProgressLine_RoundTrips_AndIsLeftOutWithoutProgress()
    {
        var header = new PlanHeader(PlanStatus.Incomplete, 2, T0, T0, "r", 3, 7);

        string text = PlanDocument.Render(header, "T", "- [ ] x");

        Assert.Contains("status: incomplete\n", text);
        Assert.Contains("progress: 3/7\n", text);
        Assert.Equal(header, PlanDocument.TryParse(text));
        Assert.DoesNotContain("progress:", PlanDocument.Render(header with { StepsDone = null, StepsTotal = null }, "T", "x"));
    }

    [Fact]
    public void WithStatus_WritesTheProgress_OrKeepsTheFilesOwn()
    {
        string text = PlanDocument.Render(new PlanHeader(PlanStatus.Approved, 1, T0, T0, "r"), "T", "- [x] a\n- [ ] b");

        string incomplete = PlanDocument.WithStatus(text, PlanStatus.Incomplete, T0, "r", (1, 2));
        string done = PlanDocument.WithStatus(incomplete, PlanStatus.Done, T0, "r");

        Assert.Equal((PlanStatus.Incomplete, 1, 2), (PlanDocument.TryParse(incomplete)!.Status, PlanDocument.TryParse(incomplete)!.StepsDone, PlanDocument.TryParse(incomplete)!.StepsTotal));
        Assert.Equal((PlanStatus.Done, 1, 2), (PlanDocument.TryParse(done)!.Status, PlanDocument.TryParse(done)!.StepsDone, PlanDocument.TryParse(done)!.StepsTotal));
        Assert.Equal(PlanDocument.Body(text), PlanDocument.Body(done));
    }

    // ── Finding and listing plans ───────────────────────────────────────────

    [Theory]
    [InlineData("guide")]
    [InlineData("guide.md")]
    [InlineData(".neon/plans/guide.md")]
    [InlineData(@"plans\guide.md")]
    [InlineData("plans/guide")]           // the folder as it was before .neon (2026-09-26)
    [InlineData(@".neon\plans\guide")]
    [InlineData(" guide ")]
    public void Resolve_TakesTheNameInItsThreeForms(string name)
    {
        WritePlan("guide.md", "# Guide");

        Assert.Equal(".neon/plans/guide.md", PlanFiles.Resolve(_files, name));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("../guide")]
    [InlineData("sub/guide")]
    [InlineData(".neon/plans/../guide")]
    [InlineData("")]
    public void Resolve_RefusesWhatIsNotAPlanDirectlyUnderPlans(string name)
    {
        WritePlan("guide.md", "# Guide");
        File.WriteAllText(Path.Combine(_dir, "guide.md"), "outside");

        Assert.Null(PlanFiles.Resolve(_files, name));
    }

    /// <summary>The reason plans live under .neon (2026-09-26): a user's own plans folder is neither found nor listed, nor written to.</summary>
    [Fact]
    public void AUsersOwnPlansFolder_IsNotThePlans()
    {
        Assert.Equal(".neon/plans", PlanSlug.Folder);
        Directory.CreateDirectory(Path.Combine(_dir, "plans"));
        File.WriteAllText(Path.Combine(_dir, "plans", "mine.md"), "# Mine\n- [ ] theirs");

        Assert.Null(PlanFiles.Resolve(_files, "mine"));
        Assert.Null(PlanFiles.Resolve(_files, "plans/mine.md"));
        Assert.Empty(PlanFiles.List(_files));

        var session = new PlanSession();
        session.Enter("r");
        var (saved, _) = PlanFiles.Save(session, _files, _time, "Mine", "- [ ] ours", "mine");
        Assert.Equal(".neon/plans/mine.md", saved!.Path);
        Assert.Equal("# Mine\n- [ ] theirs", File.ReadAllText(Path.Combine(_dir, "plans", "mine.md")));
        Assert.True(File.Exists(Path.Combine(_dir, ".neon", "plans", "mine.md")));
    }

    [Fact]
    public void List_IsNewestFirst_WithTitleStatusAndSteps()
    {
        Assert.Empty(PlanFiles.List(_files));
        WritePlan("old.md", PlanDocument.Render(new PlanHeader(PlanStatus.Done, 1, T0, T0, "r"), "Old", "- [x] a"));
        WritePlan("new.md", PlanDocument.Render(new PlanHeader(PlanStatus.Incomplete, 3, T0, T0.AddDays(1), "r"), "New", "# New plan\n- [x] a\n- [ ] b"));
        WritePlan("bare.md", "no header, no heading");
        File.SetLastWriteTime(Path.Combine(_dir, ".neon", "plans", "bare.md"), new DateTime(2020, 1, 1));
        File.WriteAllText(Path.Combine(_dir, ".neon", "plans", "notes.txt"), "not a plan");

        var plans = PlanFiles.List(_files);

        Assert.Equal(["new", "old", "bare"], plans.Select(p => p.Name));
        Assert.Equal(new PlanListing(".neon/plans/new.md", "New plan", PlanStatus.Incomplete, 1, 2, T0.AddDays(1)), plans[0]);
        Assert.Equal(("bare", null), (plans[2].Title, plans[2].Status));
        Assert.Equal("  new — New plan (incomplete · 1/2 · 2026-09-27 10:00)", PlanText.ListLine(plans[0]));
        Assert.Equal("  bare — bare (no header)", PlanText.ListLine(plans[2]));
        Assert.Equal("incomplete · 1/2 · New plan", PlanText.CompletionNote(plans[0]));
    }

    // ── The parser's two new words ──────────────────────────────────────────

    [Theory]
    [InlineData("open", false, "List", "")]
    [InlineData("open", true, "List", "")]
    [InlineData("OPEN Guide", false, "Open", "Guide")]
    [InlineData("open the pod bay doors", true, "Open", "the pod bay doors")]
    [InlineData("save", true, "Save", "")]
    [InlineData("save my-plan", true, "Save", "my-plan")]
    [InlineData("save the file first", true, "Detail", "")]
    [InlineData("save", false, "Enter", "")]
    [InlineData("save the whales", false, "Enter", "")]
    public void ParsePlanArgs_OpenAndSave(string args, bool planning, string expected, string expectedRest)
    {
        Assert.Equal(expected, ChatScreen.ParsePlanArgs(args, planning, out string rest).ToString());
        Assert.Equal(expectedRest, rest);
    }

    // ── Opening a plan ──────────────────────────────────────────────────────

    [Fact]
    public void Session_Open_FixesThePath_SoTheNextSaveIsTheNextRevision()
    {
        var session = new PlanSession();
        session.Enter("something else");
        session.Approve(fresh: false);

        session.Open(".neon/plans/guide.md", "Guide", " Add a guide ", 4, T0);

        Assert.True(session.Active);
        Assert.Equal((".neon/plans/guide.md", "Add a guide", 4, T0), (session.Path, session.Requirement, session.Revision, session.Created));
        Assert.Null(session.Approved);
        Directory.CreateDirectory(Path.Combine(_dir, ".neon", "plans"));
        var (saved, error) = PlanFiles.Save(session, _files, _time, "Guide", "- [ ] a", "ignored");
        Assert.Null(error);
        Assert.Equal((".neon/plans/guide.md", 5), (saved!.Path, saved.Revision));
        Assert.Equal(T0, PlanDocument.TryParse(File.ReadAllText(Path.Combine(_dir, ".neon", "plans", "guide.md")))!.Created);
    }

    [Fact]
    public void Texts_SayWhatHappened()
    {
        Assert.Equal("📝 Plan mode on .neon/plans/g.md (was incomplete, 2 of 5 steps done).", PlanText.OpenedNotice(".neon/plans/g.md", PlanStatus.Incomplete, 2, 5));
        Assert.Equal("📝 Plan mode on .neon/plans/g.md.", PlanText.OpenedNotice(".neon/plans/g.md", null, 0, 0));
        Assert.Equal("📝 .neon/plans/g.md: 2 of 5 steps done, marked incomplete — /plan open g picks it up.", PlanText.IncompleteNotice(".neon/plans/g.md", 2, 5));
        Assert.StartsWith("I've reopened the plan in .neon/plans/g.md (2 of 5 steps done). ", PlanText.OpenMessage(".neon/plans/g.md", 2, 5));
        Assert.Contains("that are not ticked yet", PlanText.ExecuteMessage(".neon/plans/g.md"));
        Assert.Contains("Never write the plan only in your reply", PlanText.Directive("r", null, 0));
    }
}
