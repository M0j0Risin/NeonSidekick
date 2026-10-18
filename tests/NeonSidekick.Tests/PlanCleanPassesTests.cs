using System.Text.Json;
using NeonSidekick.Files;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Plans;
using NeonSidekick.Sessions;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;
using Spectre.Console.Testing;

namespace NeonSidekick.Tests;

/// <summary>
/// The gap review's clean passes in a row and what came with them (2026-10-17, the user's report: one "0 gaps" pass ended a review the next
/// pass would still have found plenty in): the loop's streak and its limit, the confirming focus, <c>present_plan</c>'s <c>checked</c> and
/// <c>minor</c>, the summary and each pass's cost, the <c>review:</c> line and <c>/plan open</c>'s mark, the <c>confirm</c> reviewer, and
/// <c>/plan chat</c>'s session state.
/// </summary>
public class PlanCleanPassesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new();
    private readonly WorkingDirectory _files;
    private readonly PlanSession _session = new();
    private readonly List<PlanPresentation> _presented = new();
    private PlanVerdict _verdict = new(PlanChoice.Refine);
    private readonly PresentPlanTool _tool;

    private const string Plan = "# Guide\n\n## Steps\n- [ ] Write it\n";
    private const string Revised = "# Guide\n\n## Steps\n- [ ] Write it\n- [ ] Test it\n";
    private static readonly string[] Checked = PlanGapLoopTests.Checked;

    public PlanCleanPassesTests()
    {
        _files = new WorkingDirectory(() => _dir, _time);
        _tool = new PresentPlanTool(_session, _files, _time, (plan, _) =>
        {
            _presented.Add(plan);
            return Task.FromResult(_verdict);
        }, () => 5, () => 2);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string FileText() => File.ReadAllText(Path.Combine(_dir, _session.Path!.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>A plan presented once, then a loop of <paramref name="clean"/> clean passes on its first pass.</summary>
    private async Task<PlanGapLoop> PassOneAsync(int clean = 2, int limit = 5, PlanReviewer reviewer = PlanReviewer.Model)
    {
        _session.Enter("Add a guide");
        await _tool.InvokeAsync(PlanGapLoopTests.Args("Guide", Plan));
        var loop = _session.StartClosing(reviewer, limit, clean);
        loop.BeginPass(PlanDocument.Body(FileText()));
        _presented.Clear();
        return loop;
    }

    private void NextPass(PlanGapLoop loop)
    {
        Assert.Null(loop.Next(true, false, PlanDocument.Body(FileText())));
        loop.BeginPass(PlanDocument.Body(FileText()));
    }

    // ── The loop ────────────────────────────────────────────────────────────

    [Fact]
    public void Loop_CleanThenClean_ConvergesAtTwo_AndClean_Gaps_CleanStartsAgain()
    {
        var loop = new PlanGapLoop(PlanReviewer.Model, 0, 2);
        loop.BeginPass("a");
        Assert.False(loop.WouldConverge);
        loop.MarkClean();
        Assert.Null(loop.Next(true, false, "a"));                // an unchanged body on a clean pass is no stall
        loop.BeginPass("a");
        Assert.Equal(1, loop.CleanStreak);
        Assert.Equal(1, loop.Confirmation);
        loop.Record(["found one"]);                               // the confirming pass finds a gap: the streak resets
        Assert.Null(loop.Next(true, false, "b"));
        loop.BeginPass("b");
        Assert.Equal(0, loop.CleanStreak);
        Assert.Equal(0, loop.Confirmation);
        loop.MarkClean();
        Assert.Null(loop.Next(true, false, "b"));
        loop.BeginPass("b");
        Assert.True(loop.WouldConverge);
        Assert.Equal(2, loop.Confirmation);                       // counted across streaks: the next focus
        loop.MarkConverged();
        Assert.Equal(PlanGapStop.Converged, loop.Next(true, false, "b"));
    }

    [Fact]
    public void Loop_OneCleanPass_IsTheBehaviorBefore()
    {
        var loop = new PlanGapLoop(PlanReviewer.Model, 5, 1);
        loop.BeginPass("a");
        Assert.True(loop.WouldConverge);
        Assert.Equal(1, new PlanGapLoop(PlanReviewer.Model, 5).CleanPasses);
    }

    [Fact]
    public void Loop_AGapThenNoneInOnePass_StartsAStreakOfOne_AndAPassNeverConfirmsItself()
    {
        var loop = new PlanGapLoop(PlanReviewer.Model, 0, 2);
        loop.BeginPass("a");
        loop.Record(["gap"]);
        Assert.Equal(1, loop.CleanCount);
        loop.MarkClean();
        Assert.Equal(1, loop.CleanCount);                        // a second clean report in the same pass adds nothing
        Assert.False(loop.WouldConverge);
        Assert.Null(loop.Next(true, false, "b"));
        loop.BeginPass("b");
        Assert.Equal(1, loop.CleanStreak);
        Assert.True(loop.WouldConverge);
    }

    [Fact]
    public void Loop_AGapPassWithAnUnchangedBody_StillStalls()
    {
        var loop = new PlanGapLoop(PlanReviewer.Model, 0, 2);
        loop.BeginPass("a");
        loop.Record(["gap"]);
        Assert.Equal(PlanGapStop.Stalled, loop.Next(true, false, "a"));
    }

    [Fact]
    public void Loop_AtTheLimit_ACleanPassIsUnconfirmed_AGapPassTheLimit()
    {
        var clean = new PlanGapLoop(PlanReviewer.Model, 2, 3);
        clean.BeginPass("a");
        clean.Record(["gap"]);
        Assert.Null(clean.Next(true, false, "b"));
        clean.BeginPass("b");
        clean.MarkClean();
        Assert.Equal(PlanGapStop.Unconfirmed, clean.Next(true, false, "b"));

        var gaps = new PlanGapLoop(PlanReviewer.Model, 1, 1);
        gaps.BeginPass("a");
        gaps.Record(["gap"]);
        Assert.Equal(PlanGapStop.Limit, gaps.Next(true, false, "b"));
    }

    [Theory]
    [InlineData(0, 0, 1, false)]   // clamped up
    [InlineData(0, 9, 5, false)]   // clamped down
    [InlineData(1, 2, 1, true)]    // a limit with no room lowers it
    [InlineData(2, 3, 2, true)]
    [InlineData(0, 2, 2, false)]   // no cap never lowers it
    [InlineData(5, 2, 2, false)]
    public void Loop_TheCleanCount_IsClamped_AndLoweredByAShortLimit(int limit, int clean, int expected, bool lowered)
    {
        var loop = new PlanGapLoop(PlanReviewer.Model, limit, clean);

        Assert.Equal(expected, loop.CleanPasses);
        Assert.Equal(lowered, loop.CleanLowered);
    }

    [Fact]
    public void Loop_Confirm_RaisesTheCountToTwo_ThenAShortLimitLowersIt_AndTheAdvisorTakesConfirmingPasses()
    {
        var raised = new PlanGapLoop(PlanReviewer.Confirm, 5, 1);
        Assert.Equal(2, raised.CleanPasses);
        Assert.False(raised.CleanLowered);
        var lowered = new PlanGapLoop(PlanReviewer.Confirm, 1, 1);
        Assert.Equal(1, lowered.CleanPasses);
        Assert.False(lowered.CleanLowered);                       // only confirm's raise undone: no notice

        raised.BeginPass("a");
        Assert.Equal(PlanReviewer.Model, raised.PassReviewer);
        raised.MarkClean();
        Assert.Null(raised.Next(true, false, "a"));
        raised.BeginPass("a");
        Assert.Equal(PlanReviewer.Advisor, raised.PassReviewer);
        raised.Record(["gap"]);
        Assert.Null(raised.Next(true, false, "b"));
        raised.BeginPass("b");
        Assert.Equal(PlanReviewer.Model, raised.PassReviewer);
        Assert.Equal(PlanReviewer.Any, new PlanGapLoop(PlanReviewer.Any, 5).PassReviewer);
    }

    [Fact]
    public void Loop_History_OnePerEndedPass_WithFocusAndCost()
    {
        var start = new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
        var loop = new PlanGapLoop(PlanReviewer.Confirm, 0, 2);
        loop.BeginPass("a", new PlanPassMeter(start, 1_000, 0));
        loop.Record(["g1", "g2"], ["typo"]);
        Assert.Null(loop.Next(true, false, "b", new PlanPassMeter(start.AddSeconds(30), 6_000, 0)));
        loop.BeginPass("b", new PlanPassMeter(start.AddSeconds(30), 6_000, 0));
        loop.MarkClean(["steps"]);
        Assert.Null(loop.Next(true, false, "b", new PlanPassMeter(start.AddSeconds(90), 9_000, 0)));
        loop.BeginPass("b", new PlanPassMeter(start.AddSeconds(90), 9_000, 0), advisorsOffered: true);
        loop.MarkConverged(["risks"]);
        Assert.Equal(PlanGapStop.Converged, loop.Next(true, false, "b", new PlanPassMeter(start.AddSeconds(150), 12_000, 4_000)));
        Assert.Equal(PlanGapStop.Converged, loop.Next(true, false, "b"));   // judged again: recorded once

        Assert.Equal(
            [
                new PlanPassRecord(1, 2, 1, false, -1, false, TimeSpan.FromSeconds(30), 5_000, 0),
                new PlanPassRecord(2, 0, 0, true, -1, false, TimeSpan.FromSeconds(60), 3_000, 0),
                new PlanPassRecord(3, 0, 0, true, 0, true, TimeSpan.FromSeconds(60), 3_000, 4_000),
            ],
            loop.History);
        Assert.Equal(1, loop.MinorFixed);
        Assert.Equal(2, loop.Closed);
        Assert.Equal(["risks"], loop.PassChecked);
    }

    [Fact]
    public void Loop_TheFifthConfirmation_WrapsToTheFirstFocus()
    {
        var loop = new PlanGapLoop(PlanReviewer.Model, 0, 5);
        loop.BeginPass("a");
        for (int i = 1; i <= 5; i++)
        {
            loop.MarkClean();
            if (i == 5)
            {
                loop.Record(["gap"]);
            }

            Assert.Null(loop.Next(true, false, "a" + i));
            loop.BeginPass("a" + i);
        }

        loop.MarkClean();
        Assert.Null(loop.Next(true, false, "x"));
        loop.BeginPass("x");
        Assert.Equal(5, loop.Confirmation);
        loop.MarkClean();
        loop.Next(true, false, "x");
        Assert.Equal(0, loop.History[^1].Focus);                  // the fifth confirmation: focus 1 again
        Assert.Equal(-1, loop.History[0].Focus);
    }

    // ── The directive ───────────────────────────────────────────────────────

    [Fact]
    public void Directive_TheFirstBullet_NamesTheStreak_AndOneCleanPassIsAsBefore()
    {
        string one = PlanText.GapReviewLines(new PlanGapState(1, 5, PlanReviewer.Model), advisorsOffered: false);
        string two = PlanText.GapReviewLines(new PlanGapState(1, 5, PlanReviewer.Model, 0, 2), advisorsOffered: false);

        Assert.StartsWith("- A gap review is running (pass 1 of 5): present_plan's gaps lists what each revision closed; only a pass with no gaps returns the plan to the user.\n", one);
        Assert.StartsWith("- A gap review is running (pass 1 of 5): present_plan's gaps lists what each revision closed; only 2 passes in a row with no gaps return the plan to the user.\n", two);
        Assert.DoesNotContain("checks again", two);
        Assert.Contains("- Then call present_plan with the whole revised plan and gaps, one short line for each gap this pass closed; put a small fix that is not a gap (wording, formatting, a clarification nobody would stop over) in minor instead. If you find no gaps, call present_plan with the plan as it stands (or with only minor fixes), gaps empty, and checked: one short line for each part you checked.", one);
    }

    [Fact]
    public void Directive_AConfirmingPass_HasItsBullet_WithThePassFocus()
    {
        string first = PlanText.GapReviewLines(new PlanGapState(2, 5, PlanReviewer.Model, 1, 2, 1), advisorsOffered: false);
        string second = PlanText.GapReviewLines(new PlanGapState(4, 5, PlanReviewer.Model, 1, 2, 2), advisorsOffered: false);

        Assert.Contains("- The last pass found no gaps; this pass checks again (clean pass 2 of 2). Review the plan as if seeing it for the first time, and look hardest at " + PlanText.ConfirmFocuses[0] + ", which an earlier pass may have skimmed; then the rest as usual. Report every gap you find; an empty gaps list is right only when you find none.\n", first);
        Assert.Contains(PlanText.ConfirmFocuses[1], second);
        Assert.DoesNotContain(PlanText.ConfirmFocuses[0], second);
    }

    private static readonly IReadOnlyList<AdvisorEntry> Advisors = [new("architect", "software architect")];

    [Fact]
    public void Directive_Confirm_ClosingPassIsTheModels_ConfirmingPassTheAdvisorsWithItsFocus()
    {
        string closing = PlanText.Directive("R", "p.md", 1, Advisors, new PlanGapState(1, 5, PlanReviewer.Confirm, 0, 2, 0, PlanReviewer.Model));
        string confirming = PlanText.Directive("R", "p.md", 1, Advisors, new PlanGapState(2, 5, PlanReviewer.Confirm, 1, 2, 1, PlanReviewer.Advisor));
        string none = PlanText.Directive("R", "p.md", 1, null, new PlanGapState(2, 5, PlanReviewer.Confirm, 1, 2, 1, PlanReviewer.Advisor));

        Assert.Contains("Review it yourself; do not consult advisors on this pass.", closing);
        Assert.DoesNotContain(PlanText.AdvisorsLine(Advisors), closing);
        Assert.Contains("- Ask at least one advisor with neon_advisor, the one whose role fits best, to find the gaps: put the plan's text and this pass's focus in the question, then close what they name.", confirming);
        Assert.Contains(PlanText.AdvisorsLine(Advisors), confirming);
        Assert.Contains("No advisors are offered: review it yourself.", none);
    }

    [Fact]
    public void Turn_AfterAClean_TheNextPassCarriesTheConfirmingBullet()
    {
        _session.Enter("R");
        var loop = _session.StartClosing(PlanReviewer.Model, 5, 2);
        loop.BeginPass("b");
        loop.MarkClean();
        Assert.Contains("A gap review is running (pass 1 of 5)", _session.Turn(_tool)!.Directive);
        Assert.Null(loop.Next(true, false, "b"));
        loop.BeginPass("b");

        Assert.Contains("this pass checks again (clean pass 2 of 2)", _session.Turn(_tool)!.Directive);
    }

    // ── present_plan ────────────────────────────────────────────────────────

    [Fact]
    public async Task Tool_PassOneClean_NoPane_ThePassTwoClean_ConvergesConfirmed()
    {
        var loop = await PassOneAsync();

        object? first = await _tool.InvokeAsync(PlanGapLoopTests.Args("Guide", Plan, Array.Empty<string>(), Checked));

        Assert.Equal(PlanText.CleanPassResult(_session.Path!, 1, 1, 2), first);
        Assert.Equal("No gaps found in this pass of .neon/plans/guide.md (revision 1), 1 of 2 clean passes in a row. Reply with one short line and call no more tools; the next review pass starts by itself.", first);
        Assert.Empty(_presented);
        Assert.True(loop.PassClean);
        Assert.Equal(Checked, loop.PassChecked);
        NextPass(loop);

        object? second = await _tool.InvokeAsync(PlanGapLoopTests.Args("Guide", Plan, Array.Empty<string>(), Checked));

        Assert.Equal(PlanText.RefineResult(_session.Path!, null), second);
        Assert.Equal(PlanText.ConvergedNote(2, 0, 2), Assert.Single(_presented).Note);
        Assert.EndsWith(", confirmed by 2 clean passes", Assert.Single(_presented).Note);
        Assert.Null(_session.Closing);
        Assert.Same(loop, _session.TakeFinished(out string? path));
        Assert.Equal(_session.Path, path);
    }

    [Fact]
    public async Task Tool_TwoCleanReportsInOnePass_NeverConverge()
    {
        var loop = await PassOneAsync();

        await _tool.InvokeAsync(PlanGapLoopTests.Args("Guide", Plan, Array.Empty<string>(), Checked));
        object? again = await _tool.InvokeAsync(PlanGapLoopTests.Args("Guide", Plan, Array.Empty<string>(), Checked));

        Assert.Equal(PlanText.CleanPassResult(_session.Path!, 1, 1, 2), again);
        Assert.False(loop.Converged);
        Assert.Empty(_presented);
    }

    [Fact]
    public async Task Tool_NoGapsWithoutChecked_IsRefused_NothingSavedOrCounted()
    {
        var loop = await PassOneAsync();

        object? missing = await _tool.InvokeAsync(PlanGapLoopTests.Args("Guide", Revised, Array.Empty<string>()));
        object? empty = await _tool.InvokeAsync(PlanGapLoopTests.Args("Guide", Revised, Array.Empty<string>(), new[] { "  " }));
        object? bad = await _tool.InvokeAsync(PlanGapLoopTests.Args("Guide", Revised, Array.Empty<string>(), JsonDocument.Parse("[1]").RootElement));
        object? badMinor = await _tool.InvokeAsync(PlanGapLoopTests.Args("Guide", Revised, new[] { "gap" }, null, JsonDocument.Parse("{}").RootElement));

        Assert.Equal(PlanText.CheckedRequiredResult, missing);
        Assert.Equal(PlanText.CheckedRequiredResult, empty);
        Assert.Equal(PlanText.CheckedInvalidResult("[1]"), bad);
        Assert.Equal(PlanText.MinorInvalidResult("{}"), badMinor);
        Assert.Equal(0, loop.Presented);
        Assert.Equal(1, _session.Revision);
        Assert.DoesNotContain("Test it", FileText());
    }

    [Fact]
    public async Task Tool_WithGaps_CheckedIsIgnored_AndMinorCountsApart()
    {
        var loop = await PassOneAsync();

        object? result = await _tool.InvokeAsync(PlanGapLoopTests.Args("Guide", Revised, new[] { "Add a test step" }, new[] { "ignored" }, new[] { "fix a typo", "reword the title" }));

        Assert.Equal(PlanText.GapPassResult(_session.Path!, 2, 1, 2), result);
        Assert.Equal("Revision 2 of .neon/plans/guide.md is saved with 1 gap closed and 2 minor fixes. Reply with one short line and call no more tools; the next review pass starts by itself.", result);
        Assert.Equal(1, loop.Closed);
        Assert.Equal(2, loop.MinorFixed);
        Assert.Empty(loop.PassChecked);
        Assert.Equal(["fix a typo", "reword the title"], loop.PassMinor);
    }

    [Fact]
    public async Task Tool_OnlyMinorFixes_IsClean_SavesItsRevision_AndTheStreakBuilds()
    {
        var loop = await PassOneAsync();

        object? result = await _tool.InvokeAsync(PlanGapLoopTests.Args("Guide", Revised, Array.Empty<string>(), Checked, new[] { "reword" }));

        Assert.Equal(PlanText.CleanPassResult(_session.Path!, 2, 1, 2, 1), result);
        Assert.Contains("(revision 2) with 1 minor fix, 1 of 2", (string)result!);
        Assert.Equal(2, _session.Revision);
        Assert.True(loop.PassClean);
        Assert.Equal(0, loop.Closed);
        NextPass(loop);
        Assert.Equal(1, loop.CleanStreak);
    }

    [Fact]
    public async Task Tool_OneCleanPass_StillConvergesAtOnce_ButMustSayWhatItChecked()
    {
        await PassOneAsync(clean: 1);

        Assert.Equal(PlanText.CheckedRequiredResult, await _tool.InvokeAsync(PlanGapLoopTests.Args("Guide", Plan, Array.Empty<string>())));
        await _tool.InvokeAsync(PlanGapLoopTests.Args("Guide", Plan, Array.Empty<string>(), Checked));

        Assert.Equal(PlanText.ConvergedNote(1, 0), Assert.Single(_presented).Note);
    }

    [Fact]
    public async Task Tool_CloseGapsOnTheConvergedPane_KeepsTheFinishedReview_AndStartsAnother()
    {
        var first = await PassOneAsync(clean: 1);
        _verdict = new PlanVerdict(PlanChoice.CloseGaps, Reviewer: PlanReviewer.Model);

        await _tool.InvokeAsync(PlanGapLoopTests.Args("Guide", Plan, Array.Empty<string>(), Checked));

        Assert.NotSame(first, _session.Closing);
        Assert.True(_session.Closing!.Pending);
        Assert.Equal(2, _session.Closing.CleanPasses);
        Assert.Same(first, _session.TakeFinished(out _));
        Assert.Null(_session.TakeFinished(out _));
    }

    [Fact]
    public async Task Session_FinishedSurvivesExit_ButNotRestoreOrForget()
    {
        await PassOneAsync(clean: 1);
        _verdict = new PlanVerdict(PlanChoice.Cancel);
        await _tool.InvokeAsync(PlanGapLoopTests.Args("Guide", Plan, Array.Empty<string>(), Checked));

        Assert.False(_session.Active);
        Assert.NotNull(_session.Finished);
        _session.ForgetFinished();
        Assert.Null(_session.Finished);

        var other = new PlanSession();
        other.Enter("x");
        other.StartClosing(PlanReviewer.Model, 5).BeginPass("b");
        other.Closing!.MarkConverged(["x"]);
        other.FinishClosing();
        other.Restore(null);
        Assert.Null(other.Finished);
    }

    // ── The words ───────────────────────────────────────────────────────────

    [Fact]
    public void Words_TheCleanPassSentences_ArePinned()
    {
        Assert.Equal("Gap review pass 3 of 5 for the plan in p.md (reviewer: model): the last pass found no gaps; review it again, close any you find, then call present_plan.", PlanText.GapPassMessage("p.md", 3, 5, PlanReviewer.Model, cleanSoFar: 1));
        Assert.Equal(PlanText.Glyph + " Gap review pass 2 of 5: no gaps found (1 of 2 clean passes); checking again.", PlanText.CleanPassNotice(2, 5, 1, 2));
        Assert.Equal(PlanText.Glyph + " Gap review pass 2 of 5: no gaps found (1 of 2 clean passes), 2 minor fixes; checking again.", PlanText.CleanPassNotice(2, 5, 1, 2, 2));
        Assert.Equal(PlanText.Glyph + " The pass limit (1) is below LLM plan gap clean passes (2): this review finishes on 1 clean pass.", PlanText.CleanLoweredNotice(1, 2));
        Assert.Equal(PlanText.Glyph + " The pass limit (2) is below LLM plan gap clean passes (3): this review finishes on 2 clean passes in a row.", PlanText.CleanLoweredNotice(2, 3));
        Assert.Equal("no gaps in the last pass, but the limit came after 1 of 2 clean passes", PlanText.StoppedNote(PlanGapStop.Unconfirmed, 5, 0, 1, 2));
        Assert.Equal(PlanText.Glyph + " Gap review stopped at its limit after 5 passes, 3 gaps closed: the last pass found no gaps, but there was no pass left to check it again.", PlanText.GapStopNotice(PlanGapStop.Unconfirmed, 5, 3));
        Assert.Equal("no gaps left after 3 passes, 1 gap closed, confirmed by 2 clean passes", PlanText.ConvergedNote(3, 1, 2));
        Assert.Equal(PlanText.Glyph + " Gap review done: no gaps left in p.md after 3 passes, 1 gap closed, confirmed by 2 clean passes.", PlanText.ConvergedNotice("p.md", 3, 1, 2));
        Assert.Equal(PlanText.Glyph + " Gap review done: no gaps left in p.md after 1 pass, 0 gaps closed.", PlanText.ConvergedNotice("p.md", 1, 0));
        Assert.Equal("Error: with gaps empty, checked is required: one short line for each part of the plan you checked this pass. Call present_plan again with it.", PlanText.CheckedRequiredResult);
        Assert.Equal("plan gaps: pass 2 clean (1 of 2): checked: steps | risks; minor: typo", PlanText.CleanPassLogLine(2, 1, 2, ["steps", "risks"], ["typo"]));
        Assert.Equal("plan gaps: pass 1, revision 2, 2 gaps closed, 1 minor fix: a | b; minor: c", PlanText.PassLogLine(1, 2, ["a", "b"], advisorAsked: false, ["c"]));
        Assert.Equal(4, PlanText.ConfirmFocuses.Count);
        Assert.Equal(["order", "verification", "risks", "assumptions"], PlanText.ConfirmFocusLabels);
        Assert.Equal("confirm", PlanText.ReviewerWord(PlanReviewer.Confirm));
        Assert.Equal(PlanReviewer.Confirm, PlanText.ParseReviewer("CONFIRM"));
        Assert.Equal("Close gaps (model, advisor confirms)", PlanText.ReviewerRows[3]);
    }

    [Fact]
    public void Words_TheNoticeLines_CapCheckedAndMinorAtTen()
    {
        var lines = PlanText.CleanPassNoticeLines(2, 5, 1, 2, [.. Enumerable.Range(1, 8).Select(i => "part " + i)], ["typo", "wording", "title"]);

        Assert.Equal(PlanText.CleanPassNotice(2, 5, 1, 2, 3), lines[0]);
        Assert.Equal("  · checked: part 1", lines[1]);
        Assert.Equal("  · minor: typo", lines[9]);
        Assert.Equal("  … and 1 more", lines[^1]);
        Assert.Equal(PlanText.PassNoticeGaps + 2, lines.Count);
        Assert.Equal(["  · gap", "  · minor: typo"], PlanText.PassNoticeLines(1, 5, 2, ["gap"], ["typo"]).Skip(1));
        Assert.Equal(PlanText.Glyph + " Gap review pass 1 of 5: revision 2 saved, 1 gap closed, 1 minor fix.", PlanText.PassNoticeLines(1, 5, 2, ["gap"], ["typo"])[0]);
    }

    [Fact]
    public void Words_TheSummary_ForEachEnding()
    {
        var at = TimeSpan.FromSeconds(112);
        IReadOnlyList<PlanPassRecord> history =
        [
            new(1, 4, 1, false, -1, false, at, 41_200, 0),
            new(2, 1, 0, false, -1, false, at, 41_200, 0),
            new(3, 0, 0, true, -1, false, at, 41_200, 0),
            new(4, 2, 0, false, 1, false, at, 41_200, 12_000),
            new(5, 0, 0, true, 0, true, at, 41_200, 0),
        ];

        Assert.Equal(
            [
                PlanText.Glyph + " Gap review, 5 passes, 9m 20s, 206.0k tokens:",
                "  pass 1: 4 gaps, 1 minor fix · 1m 52s · 41.2k tokens",
                "  pass 2: 1 gap · 1m 52s · 41.2k tokens",
                "  pass 3: clean · 1m 52s · 41.2k tokens",
                "  pass 4: 2 gaps (verification) · 1m 52s · 41.2k tokens (12.0k advisor)",
                "  pass 5: clean (order, advisor), confirmed · 1m 52s · 41.2k tokens",
            ],
            PlanText.ReviewSummaryLines(history, PlanGapStop.Converged, 2));
        Assert.Equal(
            [PlanText.Glyph + " Gap review, 1 pass:", "  pass 1: clean, no gaps left"],
            PlanText.ReviewSummaryLines([new PlanPassRecord(1, 0, 0, true, -1)], PlanGapStop.Converged, 1));
        Assert.EndsWith("pass 1: clean, cut short", PlanText.ReviewSummaryLines([new PlanPassRecord(1, 0, 0, true, -1)], PlanGapStop.Unconfirmed, 2)[^1]);
        Assert.EndsWith("pass 1: nothing presented, stopped", PlanText.ReviewSummaryLines([new PlanPassRecord(1, 0, 0, false, -1)], PlanGapStop.Stopped)[^1]);
        Assert.Equal(PlanText.Glyph + " Gap review, 1 pass, 4.0s:", PlanText.ReviewSummaryLines([new PlanPassRecord(1, 1, 0, false, -1, false, TimeSpan.FromSeconds(4), 0, 0)], PlanGapStop.Limit)[0]);   // no tokens reported: left out
        Assert.Empty(PlanText.ReviewSummaryLines([], PlanGapStop.Stopped));
    }

    // ── The review line ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(PlanGapStop.Converged, 2, "5 passes, 7 gaps closed, confirmed by 2 clean passes (body 3f9a1c07)", true, 2)]
    [InlineData(PlanGapStop.Converged, 1, "5 passes, 7 gaps closed, no gaps left after 1 clean pass (body 3f9a1c07)", true, 1)]
    [InlineData(PlanGapStop.Unconfirmed, 2, "5 passes, 7 gaps closed, cut short at the limit (body 3f9a1c07)", false, 1)]
    [InlineData(PlanGapStop.Limit, 2, "5 passes, 7 gaps closed, stopped at the limit with gaps left (body 3f9a1c07)", false, 1)]
    [InlineData(PlanGapStop.Stalled, 2, "5 passes, 7 gaps closed, stalled (body 3f9a1c07)", false, 1)]
    public void ReviewLine_EachOutcome_AndReadBack(PlanGapStop outcome, int clean, string line, bool converged, int cleanRead)
    {
        Assert.Equal(line, PlanText.ReviewLine(5, 7, outcome, clean, "3f9a1c07"));
        var back = PlanReview.TryParse(line)!;
        Assert.Equal(new PlanReview(5, 7, converged, cleanRead, "3f9a1c07"), back);
    }

    [Theory]
    [InlineData("")]
    [InlineData("five passes, confirmed (body 3f9a1c07)")]
    [InlineData("5 passes, 7 gaps closed, confirmed by 2 clean passes (body XYZ)")]
    public void ReviewLine_AGarbledLine_IsNoReview(string line) => Assert.Null(PlanReview.TryParse(line));

    [Fact]
    public void Fingerprint_TicksAside_CrlfAside_AWordChanged()
    {
        const string body = "# Guide\n\n- [ ] Write it\n1. [ ] Test it\n```\n- [x] in code\n```";

        Assert.Matches("^[0-9a-f]{8}$", PlanDocument.Fingerprint(body));
        Assert.Equal(PlanDocument.Fingerprint(body), PlanDocument.Fingerprint(body.Replace("- [ ] Write", "- [x] Write", StringComparison.Ordinal).Replace("1. [ ]", "1. [X]", StringComparison.Ordinal)));
        Assert.Equal(PlanDocument.Fingerprint(body), PlanDocument.Fingerprint(body.Replace("\n", "\r\n", StringComparison.Ordinal) + "\n\n"));
        Assert.NotEqual(PlanDocument.Fingerprint(body), PlanDocument.Fingerprint(body.Replace("Write", "Draft", StringComparison.Ordinal)));
        Assert.NotEqual(PlanDocument.Fingerprint(body), PlanDocument.Fingerprint(body.Replace("- [x] in code", "- [ ] in code", StringComparison.Ordinal)));   // a fence's lines are not task lines
    }

    [Fact]
    public void Header_TheReviewLine_WrittenOnlyWhenSet_AndReadBack()
    {
        var now = new DateTimeOffset(2026, 10, 17, 14, 2, 0, TimeSpan.Zero);
        var header = new PlanHeader(PlanStatus.Draft, 2, now, now, "R", 1, 3);

        Assert.DoesNotContain("review:", PlanDocument.HeaderText(header));
        string text = PlanDocument.HeaderText(header with { Review = "1 pass, 0 gaps closed, stalled (body 00000000)" });
        Assert.EndsWith("progress: 1/3\nreview: 1 pass, 0 gaps closed, stalled (body 00000000)\n---\n", text);
        Assert.Equal("1 pass, 0 gaps closed, stalled (body 00000000)", PlanDocument.TryParse(text + "\n# Body\n")!.Review);
        Assert.Null(PlanDocument.TryParse("---\nstatus: draft\nreview:\nwhatever: x\n---\n")!.Review);
    }

    [Fact]
    public async Task MarkReview_KeepsBodyAndStatus_ApprovalKeepsIt_ANewRevisionDropsIt_AndListMarksIt()
    {
        _session.Enter("Add a guide");
        await _tool.InvokeAsync(PlanGapLoopTests.Args("Guide", Plan));
        string path = _session.Path!;
        string body = PlanDocument.Body(FileText());
        string review = PlanText.ReviewLine(2, 1, PlanGapStop.Converged, 2, PlanDocument.Fingerprint(body));

        Assert.Null(PlanFiles.MarkReview(_files, path, review, _time.GetLocalNow(), "Add a guide"));
        Assert.Equal(body, PlanDocument.Body(FileText()));
        Assert.Equal(review, PlanDocument.TryParse(FileText())!.Review);
        Assert.Equal(PlanReviewMark.Confirmed, Assert.Single(PlanFiles.List(_files)).Review);
        Assert.Null(PlanFiles.MarkFile(_files, path, PlanStatus.Approved, _time.GetLocalNow(), "Add a guide"));
        Assert.Equal(review, PlanDocument.TryParse(FileText())!.Review);

        // Ticking a box is no edit; changing a word is.
        File.WriteAllText(Path.Combine(_dir, path.Replace('/', Path.DirectorySeparatorChar)), FileText().Replace("- [ ] Write it", "- [x] Write it", StringComparison.Ordinal));
        Assert.Equal(PlanReviewMark.Confirmed, Assert.Single(PlanFiles.List(_files)).Review);
        Assert.Contains("(approved · confirmed · 1/1", PlanText.ListLine(Assert.Single(PlanFiles.List(_files))));
        Assert.StartsWith("approved · confirmed · 1/1 · Guide", PlanText.CompletionNote(Assert.Single(PlanFiles.List(_files))));
        File.WriteAllText(Path.Combine(_dir, path.Replace('/', Path.DirectorySeparatorChar)), FileText().Replace("Write it", "Draft it", StringComparison.Ordinal));
        Assert.Equal(PlanReviewMark.EditedSince, Assert.Single(PlanFiles.List(_files)).Review);
        Assert.Contains("edited since review", PlanText.ListLine(Assert.Single(PlanFiles.List(_files))));

        // A single clean pass is reviewed; a cut-short line marks nothing; a new revision drops the line.
        PlanFiles.MarkReview(_files, path, PlanText.ReviewLine(1, 0, PlanGapStop.Converged, 1, PlanDocument.Fingerprint(PlanDocument.Body(FileText()))), _time.GetLocalNow(), "R");
        Assert.Equal(PlanReviewMark.Reviewed, Assert.Single(PlanFiles.List(_files)).Review);
        PlanFiles.MarkReview(_files, path, PlanText.ReviewLine(5, 9, PlanGapStop.Unconfirmed, 2, PlanDocument.Fingerprint(PlanDocument.Body(FileText()))), _time.GetLocalNow(), "R");
        Assert.Equal(PlanReviewMark.None, Assert.Single(PlanFiles.List(_files)).Review);
        await _tool.InvokeAsync(PlanGapLoopTests.Args("Guide", Revised));
        Assert.Null(PlanDocument.TryParse(FileText())!.Review);
    }

    // ── /plan chat's state ──────────────────────────────────────────────────

    [Fact]
    public void Session_EnterChat_Talking_NoteMessage_Settle_AndTheStoredRow()
    {
        _session.EnterChat();
        Assert.True(_session.Active);
        Assert.True(_session.Talking);
        Assert.Equal("", _session.Requirement);
        Assert.False(_session.NoteMessage("   "));                // a picture-only message leaves it for the next
        Assert.True(_session.NoteMessage("  Let's talk about a guide  "));
        Assert.False(_session.NoteMessage("something else"));
        Assert.Equal("Let's talk about a guide", _session.Requirement);
        Assert.True(_session.RequirementIsOpening);

        var back = new PlanSession();
        back.Restore(_session.ToStored());
        Assert.True(back.Talking);
        Assert.True(back.RequirementIsOpening);
        Assert.Equal("Let's talk about a guide", back.Requirement);

        var empty = new PlanSession();
        var fresh = new PlanSession();
        fresh.EnterChat();
        empty.Restore(fresh.ToStored());
        Assert.True(empty.Active);                                // talking with nothing said yet survives a restore too

        _session.Presented("p.md", "Contributing guide", 1, _time.GetLocalNow());
        Assert.False(_session.Talking);
        Assert.False(_session.RequirementIsOpening);
        Assert.Equal("Contributing guide", _session.Requirement);
        _session.Presented("p.md", "A new title", 2, _time.GetLocalNow());
        Assert.Equal("Contributing guide", _session.Requirement);   // later revisions keep it
        _session.ResumeTalking();
        Assert.True(_session.Talking);
        _session.StartClosing(PlanReviewer.Model, 5);
        Assert.False(_session.Talking);
        _session.Exit();
        Assert.False(_session.Talking);
        Assert.False(new StoredPlan().Talking);                   // an older row reads false
    }

    [Fact]
    public void Directive_Talking_TheOpenings_AndTheTalkingBullet_DefaultAsBefore()
    {
        string before = PlanText.Directive("R", null, 0, null, null, null, new PlanTalk(true, false));
        string opening = PlanText.Directive("let's talk", null, 0, null, null, null, new PlanTalk(true, true));
        string settled = PlanText.Directive("Guide", "p.md", 1, null, null, null, default);

        Assert.StartsWith("PLAN MODE is on. The user wants to talk a plan through before anything is done; they haven't said what it is for yet.\n", PlanText.Directive("", null, 0, null, null, null, new PlanTalk(true, false)));
        Assert.StartsWith("PLAN MODE is on. The user wants to talk a plan through before anything is done. The conversation began with: \"let's talk\"\n", opening);
        Assert.DoesNotContain("Their requirement", opening);
        Assert.Contains(PlanText.TalkingLine + "\n", opening);
        Assert.DoesNotContain("When no open question is left", opening);
        Assert.Contains("Their requirement: \"R\"", before);
        Assert.Equal(PlanText.Directive("Guide", "p.md", 1, null, null, null), settled);
        Assert.Contains("When no open question is left, call present_plan", settled);
    }

    [Fact]
    public void Words_ChatAndReady_ArePinned()
    {
        Assert.Equal("I'm ready: present the plan now with present_plan.", PlanText.ReadyMessage);
        Assert.Equal("Say what the plan is for first: your first message becomes its requirement", PlanText.ReadyTooSoonError);
        Assert.Equal(PlanText.Glyph + " I ❤️ it when a plan comes together.", PlanText.ComesTogetherNotice);
        Assert.Equal("Usage: /plan <what you want done>, or /plan chat to talk it through first — the model plans it with you and nothing is changed until you approve", PlanText.UsageError);
    }

    // ── The heart's space on a Mac ──────────────────────────────────────────

    [Fact]
    public void TextCells_TheHeartGetsItsSpace_OnlyWhereItIsDrawnOneCell()
    {
        Assert.Equal(PlanText.Glyph + " I ❤️  it when a plan comes together.", TextCells.SpaceSelectorSequences(PlanText.ComesTogetherNotice, narrow: true));
        Assert.Equal(PlanText.ComesTogetherNotice, TextCells.SpaceSelectorSequences(PlanText.ComesTogetherNotice, narrow: false));
        Assert.True(TextCells.NarrowOnMainScreen("Apple_Terminal"));
        Assert.False(TextCells.NarrowOnMainScreen("iTerm.app"));
        Assert.False(TextCells.NarrowOnMainScreen(null));
        Assert.False(TextCells.NarrowOnMainScreen("vscode"));
    }

    [Fact]
    public void TranscriptRenderer_ANotice_IsSpacedWhereTheHeartIsOneCell_TheWebFeedPlain()
    {
        var console = new TestConsole().Width(120);
        var feed = new NeonSidekick.WebUi.TranscriptFeed();
        var transcript = new TranscriptRenderer(console) { Feed = feed };
        bool was = TextCells.NarrowSelectorSequences;
        try
        {
            TextCells.NarrowSelectorSequences = true;
            transcript.Notice(PlanText.ComesTogetherNotice);
        }
        finally
        {
            TextCells.NarrowSelectorSequences = was;
        }

        Assert.Contains("I ❤️  it when", console.Output);
        Assert.Equal(PlanText.ComesTogetherNotice, Assert.Single(feed.Snapshot(text => text)).Text);
        Assert.Contains(PlanText.ComesTogetherNotice, TranscriptRenderer.NoticeMarkup(PlanText.ComesTogetherNotice));   // off again: Windows unchanged
    }
}
