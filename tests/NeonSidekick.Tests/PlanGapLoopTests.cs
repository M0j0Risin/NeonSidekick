using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Files;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Plans;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// Plan mode's gap-closing loop (2026-10-13, the user's ask: Close gaps… on the approval pane, <c>/plan gaps</c>): the loop's
/// decisions, the directive's rules, the pane's rows and what <c>present_plan</c> does while a pass runs.
/// </summary>
public class PlanGapLoopTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new();
    private readonly WorkingDirectory _files;
    private readonly PlanSession _session = new();
    private readonly List<PlanPresentation> _presented = new();
    private PlanVerdict _verdict = new(PlanChoice.Refine);
    private readonly PresentPlanTool _tool;

    private const string Plan = "# Guide\n\n## Steps\n- [ ] Write it\n  Done when: guide.md exists\n";   // its check since 2026-10-22: a clean gap pass on a step without one is refused
    private const string Revised = "# Guide\n\n## Steps\n- [ ] Write it\n  Done when: guide.md exists\n- [ ] Test it\n  Done when: the link check passes\n";

    public PlanGapLoopTests()
    {
        _files = new WorkingDirectory(() => _dir, _time);
        _tool = new PresentPlanTool(_session, _files, _time, (plan, _) =>
        {
            _presented.Add(plan);
            return Task.FromResult(_verdict);
        }, () => 3);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>present_plan's arguments; <paramref name="checked"/> and <paramref name="minor"/> (2026-10-17) only when given.</summary>
    internal static AIFunctionArguments Args(string title, string markdown, object? gaps = null, object? @checked = null, object? minor = null)
    {
        var args = new Dictionary<string, object?>
        {
            [PresentPlanTool.TitleArgument] = title,
            [PresentPlanTool.MarkdownArgument] = markdown,
        };
        if (gaps is not null)
        {
            args[PresentPlanTool.GapsArgument] = gaps;
        }

        if (@checked is not null)
        {
            args[PresentPlanTool.CheckedArgument] = @checked;
        }

        if (minor is not null)
        {
            args[PresentPlanTool.MinorArgument] = minor;
        }

        return new AIFunctionArguments(args);
    }

    /// <summary>A clean report's checked list (2026-10-17): every empty gaps list during a review comes with one.</summary>
    internal static readonly string[] Checked = ["the steps"];

    /// <summary>A plan presented once (refined on the pane), then a loop on its first pass.</summary>
    private async Task<PlanGapLoop> PassOneAsync(string markdown = Plan, string title = "Guide")
    {
        _session.Enter("Add a guide");
        await _tool.InvokeAsync(Args(title, markdown));
        var loop = _session.StartClosing(PlanReviewer.Model, 3);
        loop.BeginPass(PlanDocument.Body(File.ReadAllText(Path.Combine(_dir, _session.Path!.Replace('/', Path.DirectorySeparatorChar)))));
        _presented.Clear();
        return loop;
    }

    // ── The loop ────────────────────────────────────────────────────────────

    [Fact]
    public void Loop_ARetryAndAFollowUp_OnceAPass_NeverAfterARecord()
    {
        // 2026-11-17, the gap-audit-reliability plan's steps 2 and 3.
        var loop = new PlanGapLoop(PlanReviewer.Model, 5);
        Assert.False(loop.RetryLeft);   // pending: no pass yet
        loop.BeginPass("a");
        Assert.True(loop.RetryLeft && loop.FollowUpLeft);
        Assert.True(loop.TakeRetry());
        Assert.False(loop.TakeRetry());
        Assert.True(loop.TakeFollowUp());
        Assert.False(loop.FollowUpLeft);

        loop.NoteAdvisor(true);
        loop.NoteAdvisor(false);
        Assert.True(loop.AdvisorAskedThisPass);   // ORed across the pass's turns

        loop.BeginPass("b");
        Assert.True(loop.RetryLeft && loop.FollowUpLeft);
        Assert.False(loop.AdvisorAskedThisPass);
        loop.Record(["one"]);
        Assert.False(loop.RetryLeft);
        Assert.False(loop.TakeFollowUp());
        Assert.Equal(TimeSpan.FromSeconds(15), PlanGapLoop.RetryDelay);
    }

    [Fact]
    public void Session_APause_KeepsTheLoop_ResumeRestoresIt_AndExitStartOrApproveDropIt()
    {
        _session.Enter("R");
        var loop = _session.StartClosing(PlanReviewer.Model, 5, 2);
        loop.BeginPass("b");
        loop.MarkClean(["x"]);
        _session.PauseClosing("f1");
        Assert.Null(_session.Closing);
        Assert.Same(loop, _session.Paused);

        Assert.Null(_session.ResumeClosing("f2"));   // the plan changed
        Assert.Same(loop, _session.Paused);
        _session.EnterChat();
        Assert.Null(_session.Paused);   // plan mode left and entered again

        _session.Enter("R");
        loop = _session.StartClosing(PlanReviewer.Model, 5, 2);
        loop.BeginPass("b");
        _session.PauseClosing("f1");
        Assert.Same(loop, _session.ResumeClosing("f1"));
        Assert.Same(loop, _session.Closing);
        Assert.False(_session.Talking);
        Assert.Null(_session.Paused);

        _session.PauseClosing("f1");
        _session.StartClosing(PlanReviewer.Model, 5);
        Assert.Null(_session.Paused);   // a new review

        _session.StopClosing();
        _session.StartClosing(PlanReviewer.Model, 5).BeginPass("b");
        _session.PauseClosing("f1");
        _session.Approve(fresh: false);
        Assert.Null(_session.Paused);
    }

    [Fact]
    public void Loop_StartsPending_AndEachPassBeginsAfresh()
    {
        var loop = new PlanGapLoop(PlanReviewer.Advisor, 5);

        Assert.True(loop.Pending);
        Assert.Equal(0, loop.Pass);
        loop.BeginPass("a");
        loop.Record(["one", "two"]);
        loop.BeginPass("b");

        Assert.False(loop.Pending);
        Assert.Equal(2, loop.Pass);
        Assert.Empty(loop.PassGaps);
        Assert.Equal(["one", "two"], loop.LastGaps);
        Assert.Equal(2, loop.Closed);
        Assert.Equal(0, loop.Presented);
    }

    [Fact]
    public void Next_EachReasonToStop_AndNullToGoOn()
    {
        PlanGapLoop Pass(int limit = 5)
        {
            var loop = new PlanGapLoop(PlanReviewer.Model, limit);
            loop.BeginPass("before");
            return loop;
        }

        var converged = Pass();
        converged.MarkConverged();
        Assert.Equal(PlanGapStop.Converged, converged.Next(endedOnItsOwn: true, userSpoke: false, "before"));

        var gaps = Pass();
        gaps.Record(["a"]);
        Assert.Equal(PlanGapStop.Stopped, gaps.Next(endedOnItsOwn: false, userSpoke: false, "after"));
        Assert.Equal(PlanGapStop.NoRevision, Pass().Next(endedOnItsOwn: true, userSpoke: false, "after"));
        Assert.Equal(PlanGapStop.NoRevision, gaps.Next(endedOnItsOwn: true, userSpoke: false, null));
        Assert.Equal(PlanGapStop.Stalled, gaps.Next(endedOnItsOwn: true, userSpoke: false, "before"));
        Assert.Equal(PlanGapStop.UserSpoke, gaps.Next(endedOnItsOwn: true, userSpoke: true, "after"));
        Assert.Null(gaps.Next(endedOnItsOwn: true, userSpoke: false, "after"));

        var last = Pass(limit: 1);
        last.Record(["a"]);
        Assert.Equal(PlanGapStop.Limit, last.Next(endedOnItsOwn: true, userSpoke: false, "after"));
    }

    [Fact]
    public void Next_TheSameGapsAgain_IsAStall_OrderAndCaseAside()
    {
        var loop = new PlanGapLoop(PlanReviewer.Model, 5);
        loop.BeginPass("one");
        loop.Record(["Name the file", "add a test"]);
        Assert.Null(loop.Next(true, false, "two"));
        loop.BeginPass("two");
        loop.Record([" Add a test ", "name the FILE"]);

        Assert.Equal(PlanGapStop.Stalled, loop.Next(true, false, "three"));
    }

    [Fact]
    public void Next_AGapPassBackToAnEarlierPassesBody_IsAStall()
    {
        // 2026-11-18 (phase 3 item 2, the cycle check): pass 3 undid pass 2's revision, back to the plan pass 1 found.
        var loop = new PlanGapLoop(PlanReviewer.Model, 5);
        loop.BeginPass("one");
        loop.Record(["Name the file"]);
        Assert.Null(loop.Next(true, false, "two"));
        loop.BeginPass("two");
        loop.Record(["Add a test"]);
        Assert.Null(loop.Next(true, false, "three"));
        loop.BeginPass("three");
        loop.Record(["Drop the test"]);

        Assert.Equal(PlanGapStop.Stalled, loop.Next(true, false, "one"));
    }

    [Fact]
    public void Next_ACleanPassOnAnEarlierBody_IsNoStall()
    {
        var loop = new PlanGapLoop(PlanReviewer.Model, 5, cleanPasses: 3);
        loop.BeginPass("one");
        loop.Record(["Name the file"]);
        Assert.Null(loop.Next(true, false, "two"));
        loop.BeginPass("two");
        loop.MarkClean();

        Assert.Null(loop.Next(true, false, "one"));
    }

    [Fact]
    public void ShapeRefusal_OnceAPass_AndBackInTheNext()
    {
        var loop = new PlanGapLoop(PlanReviewer.Model, 5);
        Assert.False(loop.ShapeRefusalLeft);   // no pass yet
        loop.BeginPass("one");
        Assert.True(loop.TakeShapeRefusal());
        Assert.False(loop.ShapeRefusalLeft);
        Assert.False(loop.TakeShapeRefusal());
        loop.Record(["Add a test"]);
        Assert.Null(loop.Next(true, false, "two"));
        loop.BeginPass("two");

        Assert.True(loop.ShapeRefusalLeft);
    }

    [Fact]
    public void GapPassMessage_NamesWhatTheAppMeasured_OnlyWhenThereIsSomething()
    {
        string plain = PlanText.GapPassMessage("p.md", 1, 5, PlanReviewer.Model);

        Assert.Equal(plain, PlanText.GapPassMessage("p.md", 1, 5, PlanReviewer.Model, measured: []));
        Assert.Equal(plain, PlanText.GapPassMessage("p.md", 1, 5, PlanReviewer.Model, measured: null));
        Assert.Equal(plain + " The app measured the plan on disk: 2 steps have no indented Done when line. Each is a gap to close in this pass.",
            PlanText.GapPassMessage("p.md", 1, 5, PlanReviewer.Model, measured: [RalphText.FitNoDoneWhen(2)]));
        Assert.EndsWith(" " + PlanText.MeasuredOnDiskSentence([RalphText.FitNoDoneWhen(2)]),
            PlanText.GapPassMessage("p.md", 2, 5, PlanReviewer.Model, cleanSoFar: 1, measured: [RalphText.FitNoDoneWhen(2)]));
    }

    [Fact]
    public void GapStopNotice_Stalled_NamesAReturnToAnEarlierVersion()
    {
        Assert.Contains("the plan stopped changing, went back to an earlier pass's version, or the same gaps came back.", PlanText.GapStopNotice(PlanGapStop.Stalled, 3, 5));
    }

    [Fact]
    public void Structural_IsTheShapeProblemsAlone()
    {
        Assert.Equal([RalphText.FitNoDoneWhen(1)], PlanFit.Structural("# T\n\n## Steps\n- [ ] Write it\n  Done when: it exists\n- [ ] Test it\n"));
        Assert.Empty(PlanFit.Structural("# T\n\n## Steps\n- [ ] Write it\n  Done when: it exists\n"));
    }

    [Fact]
    public void Next_NoLimit_NeverStopsOnTheCount()
    {
        var loop = new PlanGapLoop(PlanReviewer.Model, 0);
        for (int pass = 1; pass <= 30; pass++)
        {
            loop.BeginPass("body " + (pass - 1));
            loop.Record(["gap " + pass]);
            Assert.Null(loop.Next(true, false, "body " + pass));
        }

        Assert.Equal(30, loop.Pass);
        Assert.Equal(30, loop.Closed);
    }

    [Fact]
    public void Session_ExitEndsTheLoop_AndTheRowNeverKeepsIt()
    {
        _session.Enter("x");
        _session.StartClosing(PlanReviewer.Any, 5).BeginPass("b");

        var stored = _session.ToStored()!;
        var back = new PlanSession();
        back.Restore(stored);
        _session.Exit();

        Assert.Null(back.Closing);
        Assert.Null(_session.Closing);
    }

    // ── The directive ───────────────────────────────────────────────────────

    private static readonly IReadOnlyList<AdvisorEntry> Advisors = [new("architect", "software architect")];

    [Fact]
    public void Directive_WithNoLoop_IsTheDirectiveAsEver()
    {
        Assert.Equal(PlanText.Directive("R", "p.md", 2, Advisors), PlanText.Directive("R", "p.md", 2, Advisors, null));
        Assert.Contains(PlanText.AdvisorsLine(Advisors), PlanText.Directive("R", "p.md", 2, Advisors, null));
    }

    [Fact]
    public void Directive_ModelReviews_DropsTheAdvisorsBullet_AndSaysSo()
    {
        var gap = new PlanGapState(2, 5, PlanReviewer.Model);
        string directive = PlanText.Directive("R", "p.md", 2, Advisors, gap);

        Assert.DoesNotContain(PlanText.AdvisorsLine(Advisors), directive);
        Assert.Contains(PlanText.GapReviewLines(gap, advisorsOffered: true), directive);
        Assert.Contains("A gap review is running (pass 2 of 5)", directive);
        Assert.Contains("Review it yourself; do not consult advisors on this pass.", directive);
        Assert.Contains("Wording and style are not gaps.", directive);
        Assert.EndsWith("The plan so far is in p.md (revision 2); present_plan overwrites it with the next revision.", directive);
    }

    [Fact]
    public void Directive_AdvisorReviews_KeepsTheBullet_AndAsksOne_ElseFallsBackToTheModel()
    {
        var gap = new PlanGapState(1, 0, PlanReviewer.Advisor);
        string offered = PlanText.Directive("R", "p.md", 1, Advisors, gap);
        string none = PlanText.Directive("R", "p.md", 1, null, gap);

        Assert.Contains(PlanText.AdvisorsLine(Advisors), offered);
        // A pass's advisor sees only the question and context (2026-11-17): the directive says so where the pass offers advisors.
        Assert.Contains(PlanText.AdvisorsLine(Advisors) + " " + PlanText.PassAdvisorSentence, offered);
        Assert.DoesNotContain(PlanText.PassAdvisorSentence, PlanText.Directive("R", "p.md", 1, Advisors, null));
        Assert.DoesNotContain(PlanText.PassAdvisorSentence, PlanText.Directive("R", "p.md", 1, Advisors, new PlanGapState(1, 5, PlanReviewer.Model)));
        Assert.Contains("Ask at least one advisor with neon_advisor", offered);
        Assert.Contains("A gap review is running (pass 1)", offered);
        Assert.DoesNotContain("neon_advisor", none);
        Assert.Contains("No advisors are offered: review it yourself.", none);
    }

    [Fact]
    public void Directive_AnyReviewer_IsTheAdvisorsBullet_WithNoSentenceOfItsOwn()
    {
        var gap = new PlanGapState(1, 5, PlanReviewer.Any);
        string directive = PlanText.Directive("R", "p.md", 1, Advisors, gap);

        Assert.Contains(PlanText.AdvisorsLine(Advisors), directive);
        Assert.DoesNotContain("Ask at least one advisor", directive);
        Assert.DoesNotContain("Review it yourself", directive);
    }

    [Fact]
    public void Turn_CarriesThePass_OnlyOnceItHasBegun()
    {
        _session.Enter("R");
        var loop = _session.StartClosing(PlanReviewer.Model, 5);

        Assert.DoesNotContain("gap review", _session.Turn(_tool)!.Directive);
        loop.BeginPass("b");
        Assert.Contains("A gap review is running (pass 1 of 5)", _session.Turn(_tool)!.Directive);
        loop.MarkConverged();
        Assert.DoesNotContain("gap review", _session.Turn(_tool)!.Directive);
    }

    // ── The words ───────────────────────────────────────────────────────────

    [Fact]
    public void Words_ThePassMessage_TheNotices_AndTheReviewers()
    {
        Assert.Equal("Gap review pass 2 of 5 for the plan in p.md (reviewer: advisor): find and close its gaps, then call present_plan.", PlanText.GapPassMessage("p.md", 2, 5, PlanReviewer.Advisor));
        Assert.Equal("Gap review pass 7 for the plan in p.md (reviewer: model): find and close its gaps, then call present_plan.", PlanText.GapPassMessage("p.md", 7, 0, PlanReviewer.Model));
        Assert.Equal(PlanReviewer.Any, PlanText.ParseReviewer(" ANY "));
        Assert.Null(PlanText.ParseReviewer("advsior"));
        Assert.Equal("Guide — p.md (revision 3) · no gaps left after 2 passes, 1 gap closed", PlanText.Caption(new PlanPresentation("Guide", "p.md", 3, "") { Note = PlanText.ConvergedNote(2, 1) }));

        var lines = PlanText.PassNoticeLines(1, 5, 2, [.. Enumerable.Range(1, 12).Select(i => "gap " + i)]);
        Assert.Equal(PlanText.Glyph + " Gap review pass 1 of 5: revision 2 saved, 12 gaps closed.", lines[0]);
        Assert.Equal(PlanText.PassNoticeGaps + 2, lines.Count);
        Assert.Equal("  · gap 1", lines[1]);
        Assert.Equal("  … and 2 more", lines[^1]);
        Assert.Equal("plan gaps: pass 1, revision 2, 2 gaps closed, an advisor asked: a | b", PlanText.PassLogLine(1, 2, ["a", "b"], advisorAsked: true));
        Assert.Equal("present_plan: closegaps (advisor)", ChatScreen.PlanVerdictLogLine(PlanChoice.CloseGaps, PlanReviewer.Advisor));
        Assert.Equal("present_plan: approve", ChatScreen.PlanVerdictLogLine(PlanChoice.Approve, null));
    }

    [Fact]
    public void Pane_CloseGapsIsTheFourthRow_TheOldLettersKeepTheirRows()
    {
        // Ralph loop after Approve fresh since 2026-10-18 (l), the later rows one down.
        Assert.Equal([PlanChoice.Approve, PlanChoice.ApproveFresh, PlanChoice.ApproveRalph, PlanChoice.Refine, PlanChoice.CloseGaps, PlanChoice.Cancel], PlanApprovalMenu.Choices);
        Assert.Equal(PlanChoice.ApproveRalph, PlanApprovalMenu.Choices[PlanApprovalMenu.Hotkeys['l']]);
        Assert.Equal(PlanChoice.Approve, PlanApprovalMenu.Choices[PlanApprovalMenu.Hotkeys['a']]);
        Assert.Equal(PlanChoice.ApproveFresh, PlanApprovalMenu.Choices[PlanApprovalMenu.Hotkeys['f']]);
        Assert.Equal(PlanChoice.Refine, PlanApprovalMenu.Choices[PlanApprovalMenu.Hotkeys['r']]);
        Assert.Equal(PlanChoice.CloseGaps, PlanApprovalMenu.Choices[PlanApprovalMenu.Hotkeys['g']]);
        Assert.Equal(PlanChoice.Cancel, PlanApprovalMenu.Choices[PlanApprovalMenu.Hotkeys['c']]);
        Assert.Equal(PlanChoice.Refine, PlanApprovalMenu.Choices[PlanApprovalMenu.StartRow]);
        Assert.Equal(PlanApprovalMenu.Choices.Count, PlanApprovalMenu.Rows.Count);
        Assert.Equal(PlanReviewer.Advisor, (PlanReviewer)PlanApprovalMenu.ReviewerHotkeys['a']);
        Assert.Equal(PlanReviewer.Any, (PlanReviewer)PlanApprovalMenu.ReviewerHotkeys['y']);
        Assert.Equal(PlanReviewer.Confirm, (PlanReviewer)PlanApprovalMenu.ReviewerHotkeys['c']);   // 2026-10-17; the main page's c (Cancel plan) is its own map
        Assert.Equal(4, PlanApprovalMenu.ReviewerPage(new PlanPresentation("G", "p.md", 1, "")).Rows.Count);
    }

    // ── present_plan while a pass runs ──────────────────────────────────────

    [Fact]
    public async Task Tool_APassWithGaps_SavesARevision_NoPane_AndRecordsThem()
    {
        var loop = await PassOneAsync();

        object? result = await _tool.InvokeAsync(Args("Guide", Revised, new[] { "Add a test step", "  " }));

        Assert.Equal(PlanText.GapPassResult(_session.Path!, 2, 1, bodyChars: BodyChars(Revised)), result);
        Assert.Empty(_presented);
        Assert.Equal(["Add a test step"], loop.PassGaps);
        Assert.Equal(1, loop.Presented);
        Assert.Equal(2, _session.Revision);
    }

    [Fact]
    public async Task Tool_APassOnAOneStepPlan_CarriesWhatTheAppMeasured()
    {
        // Later on 2026-10-18 (the user's 170,000-character sample): the measured fit, not only the rules; no-check left out.
        await PassOneAsync();
        string big = "# Guide\n\n## Context\n\n" + new string('c', PlanFit.OneStepChars) + "\n\n## Steps\n- [ ] Write the whole guide\n";

        object? refused = await _tool.InvokeAsync(Args("Guide", big, new[] { "Add context" }));   // 2026-11-18: sent back once a pass, then taken
        Assert.StartsWith("Error: nothing was saved: the app measured: 1 step: one pass does the whole plan; 1 step has no indented Done when line.", Assert.IsType<string>(refused));
        object? result = await _tool.InvokeAsync(Args("Guide", big, new[] { "Add context" }));

        string text = Assert.IsType<string>(result);
        Assert.EndsWith(" The app measured: 1 step: one pass does the whole plan; 1 step has no indented Done when line. Each is a gap; close it in the next pass: no pass is clean while one stands.", text);
        Assert.DoesNotContain(RalphText.FitNoCheck, text);
    }

    [Fact]
    public async Task Tool_APassOnAWellShapedPlan_CarriesNothingMeasured()
    {
        await PassOneAsync();
        const string good = "# Guide\n\n## Steps\n- [ ] Write it\n  Done when: guide.md exists\n- [ ] Test it\n  Done when: the link check passes\n";

        object? result = await _tool.InvokeAsync(Args("Guide", good, new[] { "Add done when lines" }));

        Assert.Equal(PlanText.GapPassResult(_session.Path!, 2, 1, bodyChars: BodyChars(good)), result);
    }

    /// <summary>
    /// The body's count after every pass (2026-10-21, the user's 70-pass log: the app said nothing between 20,000 and 24,000, and the model
    /// trimmed by the file's bytes, header in): characters of the body, under or over <see cref="PlanText.PlanSizeLimit"/>, its header left out.
    /// </summary>
    [Fact]
    public async Task Tool_EveryPass_SaysTheBodysCount_AgainstTheLimit_HeaderLeftOut()
    {
        await PassOneAsync();
        // Multi-byte punctuation: more bytes on disk than characters in the body.
        string over = "# Guide\n\n## Context\n\n" + new string('—', PlanText.PlanSizeLimit) + "\n\n## Steps\n- [ ] Write it\n  Done when: guide.md exists\n- [ ] Test it\n  Done when: the link check passes\n";

        string text = Assert.IsType<string>(await _tool.InvokeAsync(Args("Guide", over, new[] { "Add context" })));

        int body = BodyChars(over);
        Assert.Contains(PlanText.SizeSentence(body), text);
        Assert.Contains("over the 20,000 limit", text);
        Assert.Equal(PlanDocument.Body(System.IO.File.ReadAllText(System.IO.Path.Combine(_files.Root, _session.Path!))).Length, body);
        Assert.Equal("The app counted the plan's body at 19,999 characters (its header left out), under the 20,000 limit; go by this count, not the file's size in bytes.", PlanText.SizeSentence(19_999));
        Assert.Equal("The app counted the plan's body at 20,001 characters (its header left out), over the 20,000 limit; go by this count, not the file's size in bytes.", PlanText.SizeSentence(20_001));
        Assert.Contains("under 20,000 characters", PlanText.PlanSizeLine);   // the limit the rules name is the one the result counts against
        Assert.Contains(PlanText.PlanSizeLimit.ToString("N0", System.Globalization.CultureInfo.InvariantCulture), PlanText.GapReviewLines(new PlanGapState(1, 5, PlanReviewer.Model), advisorsOffered: false));
    }

    /// <summary>The body's length a pass's result says (2026-10-21, <see cref="PlanText.SizeSentence"/>): what <see cref="PlanDocument.SavedBody"/> writes.</summary>
    internal static int BodyChars(string markdown, string title = "Guide") => PlanDocument.SavedBody(title, markdown).Length;

    [Fact]
    public async Task Tool_APassUnderASmallWindow_MeasuresTheLengthAgainstIt()
    {
        var tool = new PresentPlanTool(_session, _files, _time, (_, _) => Task.FromResult(_verdict), () => 3, inlineLimit: () => 40);
        await PassOneAsync();
        const string good = "# Guide\n\n## Steps\n- [ ] Write it\n  Done when: guide.md exists\n- [ ] Test it\n  Done when: the link check passes\n";

        object? result = await tool.InvokeAsync(Args("Guide", good, new[] { "Add done when lines" }));

        // The length is no gap (2026-10-22, the user's call after a review): said apart, after the gaps.
        Assert.Contains(" Also, the plan is ", Assert.IsType<string>(result));
        Assert.EndsWith("; a pass sends it whole only up to 40: a shorter plan suits a Ralph loop better, but this alone is not a gap.", (string)result!);
        Assert.DoesNotContain("The app measured", (string)result!);
    }

    [Fact]
    public async Task Tool_APassWithNoGaps_Converges_AndThePaneShowsTheNote_NoNewRevisionWhenUnchanged()
    {
        // A body with no heading of its own: the file has "# Guide" added, and the same markdown again is still no change.
        var loop = await PassOneAsync("## Steps\n- [ ] Write it\n  Done when: guide.md exists\n");

        object? result = await _tool.InvokeAsync(Args("Guide", "## Steps\n- [ ] Write it\n  Done when: guide.md exists\n", Array.Empty<string>(), Checked));

        Assert.Equal(PlanText.RefineResult(_session.Path!, null), result);
        var shown = Assert.Single(_presented);
        Assert.Equal(1, shown.Revision);
        Assert.Equal(1, _session.Revision);
        Assert.Equal(PlanText.ConvergedNote(1, 0), shown.Note);
        Assert.True(loop.Converged);
        Assert.Equal(PlanGapStop.Converged, loop.Next(true, false, "anything"));
    }

    [Fact]
    public async Task Tool_AfterConverging_ThePaneIsTheUsualOne()
    {
        var loop = await PassOneAsync();
        await _tool.InvokeAsync(Args("Guide", Plan, @checked: Checked));
        _presented.Clear();

        object? again = await _tool.InvokeAsync(Args("Guide", Revised, new[] { "ignored now" }));

        Assert.True(loop.Converged);
        Assert.Null(Assert.Single(_presented).Note);
        Assert.Equal(PlanText.RefineResult(_session.Path!, null), again);
        Assert.Empty(loop.PassGaps);
    }

    [Fact]
    public async Task Tool_BadGaps_AndAFailedCall_AreNotCounted()
    {
        var loop = await PassOneAsync();

        object? bad = await _tool.InvokeAsync(Args("Guide", Revised, JsonDocument.Parse("[1, 2]").RootElement));
        object? blank = await _tool.InvokeAsync(Args("Guide", "  ", new[] { "x" }));

        Assert.Equal(PlanText.GapsInvalidResult("[1, 2]"), bad);
        Assert.Equal(PlanText.MarkdownRequired, blank);
        Assert.Equal(0, loop.Presented);
        Assert.Equal(1, _session.Revision);
        Assert.Equal(PlanGapStop.NoRevision, loop.Next(true, false, "x"));
    }

    [Fact]
    public async Task Tool_ALoneString_IsOneGap()
    {
        var loop = await PassOneAsync();

        await _tool.InvokeAsync(Args("Guide", Revised, "Add a test step"));

        Assert.Equal(["Add a test step"], loop.PassGaps);
    }

    [Fact]
    public async Task Tool_OutsideALoop_GapsAreIgnored()
    {
        _session.Enter("Add a guide");

        object? result = await _tool.InvokeAsync(Args("Guide", Plan, JsonDocument.Parse("[1]").RootElement));

        Assert.Equal(PlanText.RefineResult(_session.Path!, null), result);
        Assert.Single(_presented);
    }

    [Fact]
    public async Task Tool_CloseGapsOnThePane_StartsALoop_WithTheLimitAndReviewer()
    {
        _session.Enter("Add a guide");
        _verdict = new PlanVerdict(PlanChoice.CloseGaps, Reviewer: PlanReviewer.Advisor);

        object? result = await _tool.InvokeAsync(Args("Guide", Plan));

        Assert.Equal(PlanText.CloseGapsResult(_session.Path!), result);
        var loop = Assert.IsType<PlanGapLoop>(_session.Closing);
        Assert.True(loop.Pending);
        Assert.Equal(PlanReviewer.Advisor, loop.Reviewer);
        Assert.Equal(3, loop.Limit);
        Assert.Null(_session.Approved);
    }

    [Fact]
    public void LightChanges_KeepTheStreak_AndAreCountedInThePassRecord()
    {
        // 2026-11-17, phase 3 (Code audit low keeps streak): a clean report's light changes are never gaps, so the streak, the stall check and the
        // confirmations read them as clean; the pass's record counts them, and the next pass reads them as LastLight.
        var loop = new PlanGapLoop(PlanReviewer.Model, 5, 3);
        loop.BeginPass("a");
        Assert.Equal(0, loop.LastLight);
        loop.MarkClean(["x"], null, ["src/a.cs — small"]);
        Assert.Equal((true, 1, 0), (loop.PassClean, loop.PassLight.Count, loop.PassGaps.Count));
        Assert.True(loop.StreakLight);
        Assert.Null(loop.Next(true, false, "a"));
        Assert.Equal(1, loop.History[^1].Light);
        Assert.True(loop.StreakLight);   // after Next, before the next pass: the pass recorded is not counted twice

        loop.BeginPass("a");
        Assert.Equal((1, 1, 0), (loop.CleanStreak, loop.LastLight, loop.PassLight.Count));
        Assert.True(loop.StreakLight);   // the streak this pass would extend holds pass 1's
        loop.MarkClean(["y"]);
        Assert.Null(loop.Next(true, false, "a"));
        Assert.Equal([1, 0], loop.History.Select(p => p.Light));

        loop.BeginPass("a");
        Assert.Equal((2, 0), (loop.CleanStreak, loop.LastLight));
        Assert.True(loop.WouldConverge);
        loop.MarkConverged(["z"]);
        Assert.Equal(PlanGapStop.Converged, loop.Next(true, false, "a"));
        Assert.True(loop.StreakLight);
    }

    [Fact]
    public void LightChanges_AfterAGap_CountThePassOnce_AndAGapPassBreaksAStreakOfThem()
    {
        var loop = new PlanGapLoop(PlanReviewer.Model, 5, 2);
        loop.BeginPass("a");
        loop.Record(["a gap"]);
        loop.MarkClean(null, null, ["light"]);
        Assert.Equal(1, loop.CleanCount);
        Assert.Null(loop.Next(true, false, "b"));
        loop.BeginPass("b");
        Assert.Equal(1, loop.CleanStreak);
        loop.Record(["another"]);
        Assert.Null(loop.Next(true, false, "c"));
        loop.BeginPass("c");
        Assert.Equal((0, 0), (loop.CleanStreak, loop.LastLight));
        Assert.False(loop.StreakLight);
    }

    [Fact]
    public void TheShadow_LivesOnTheLoop_AndAPausedReviewKeepsIt()
    {
        // 2026-11-17, phase 3: the plan shadow counts like the audit's; a review paused and resumed keeps its loop, so its shadow streak too.
        var loop = new PlanGapLoop(PlanReviewer.Model, 5, 2);
        loop.BeginPass("a");
        loop.Shadow.Note(1, serious: false, [(int)PlanGapSeverity.Low]);
        var one = loop.Shadow.End(1);
        Assert.Equal((1, true, false), (one.Streak, one.Called, one.Serious));
        Assert.Equal([0, 0, 1, 0], one.Severities);
        Assert.Equal(0, new PlanGapLoop(PlanReviewer.Model, 5, 2).Shadow.Streak);
        Assert.Equal(1, loop.Shadow.Streak);
    }
}
