using Microsoft.Extensions.AI;
using NeonSidekick.Plans;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// The gap review's clean passes on the screen (2026-10-17, the user's report: one "0 gaps" pass ended a review the next would still have
/// found plenty in): a confirming pass, the limit coming first, one clean pass as before, a short limit's notice, the <c>confirm</c>
/// reviewer, a converged review never lost on its own pane; and <c>/plan chat</c> / <c>/plan ready</c>.
/// </summary>
public partial class ChatScreenTests
{
    private PlanHeader PlanHeaderNow => PlanDocument.TryParse(File.ReadAllText(PlanFile))!;

    private string PlanBodyNow => PlanDocument.Body(File.ReadAllText(PlanFile));

    [Fact]
    public async Task PlanGaps_AConfirmingPassThatFindsGaps_ResetsTheStreak_AndTheReviewGoesOn()
    {
        PlanFixture([[Keys.Char('g'), Keys.Enter], [Keys.Char('c'), Keys.Enter]], "/plan Add a guide", "/exit");
        _chat.Enqueue(PresentPlanCall());                                               // 0: Close gaps…
        _chat.EnqueueText("Closing.");                                                  // 1
        _chat.Enqueue(PresentGapsCall("p2", PlanMarkdown));                             // 2: pass 1 clean
        _chat.EnqueueText("Clean.");                                                    // 3
        _chat.Enqueue(PresentGapsCall("p3", RevisedPlanMarkdown, "Add a test step"));  // 4: pass 2 confirms, finds one
        _chat.EnqueueText("Found one.");                                                // 5
        _chat.Enqueue(PresentGapsCall("p4", RevisedPlanMarkdown));                      // 6: pass 3 clean again
        _chat.EnqueueText("Clean.");                                                    // 7
        _chat.Enqueue(PresentGapsCall("p5", RevisedPlanMarkdown));                      // 8: pass 4 confirms: the pane, canceled
        _chat.EnqueueText("Canceled.");                                                 // 9

        string output = await RunAsync();

        Assert.Equal(10, _chat.Requests.Count);
        Assert.Equal(PlanText.CleanPassResult(PlanPath, 1, 1, 2), PlanResultOf(_chat.Requests[3], "p2"));
        Assert.Equal(PlanText.GapPassMessage(PlanPath, 2, 5, PlanReviewer.Model, cleanSoFar: 1), LastUserText(_chat.Requests[4]));
        Assert.Contains(PlanText.ConfirmFocuses[0], _chat.Requests[4][0].Text);
        Assert.Equal(PlanText.GapPassMessage(PlanPath, 3, 5, PlanReviewer.Model), LastUserText(_chat.Requests[6]));
        Assert.DoesNotContain("this pass checks again", _chat.Requests[6][0].Text);
        Assert.Equal(PlanText.GapPassMessage(PlanPath, 4, 5, PlanReviewer.Model, cleanSoFar: 1), LastUserText(_chat.Requests[8]));
        Assert.Contains(PlanText.ConfirmFocuses[1], _chat.Requests[8][0].Text);   // the next focus, not the first again
        Assert.Contains(PlanText.ConvergedNotice(PlanPath, 4, 1, 2), output);
        Assert.Contains("pass 2: 1 gap (order)", output);
        Assert.Contains("pass 4: clean (verification), confirmed", output);
        Assert.DoesNotContain("The pass limit (", output);
        Assert.Equal(PlanStatus.Cancelled, PlanHeaderNow.Status);
        Assert.Equal(PlanText.ReviewLine(4, 1, PlanGapStop.Converged, 2, PlanDocument.Fingerprint(PlanBodyNow)), PlanHeaderNow.Review);
    }

    [Fact]
    public async Task PlanGaps_TheLimitBeforeTheConfirmation_IsUnconfirmed_AndThePaneComesBack()
    {
        _settings.Update(d => d.PlanGapPasses = 2);
        PlanGapsFixture([[Keys.Char('g'), Keys.Enter]], Line("/plan Add a guide"), Key(Keys.Escape), Line("/exit"));
        _chat.Enqueue(PresentPlanCall());
        _chat.EnqueueText("Closing.");
        _chat.Enqueue(PresentGapsCall("p2", RevisedPlanMarkdown, "Add a test step"));   // pass 1 of 2
        _chat.EnqueueText("Pass 1 done.");
        _chat.Enqueue(PresentGapsCall("p3", RevisedPlanMarkdown));                       // pass 2 of 2: clean, but no pass left
        _chat.EnqueueText("Clean.");

        string output = await RunAsync();

        Assert.Equal(6, _chat.Requests.Count);
        Assert.Contains(PlanText.GapStopNotice(PlanGapStop.Unconfirmed, 2, 1), output);
        Assert.Contains(PlanText.StoppedNote(PlanGapStop.Unconfirmed, 2, 0, 1, 2), output);
        Assert.Contains("pass 2: clean, cut short", output);
        Assert.DoesNotContain(PlanText.CleanPassNotice(2, 2, 1, 2), output);   // no pass follows: the stop says it
        Assert.DoesNotContain("The pass limit (", output);
        Assert.Equal(PlanText.ReviewLine(2, 1, PlanGapStop.Unconfirmed, 2, PlanDocument.Fingerprint(PlanBodyNow)), PlanHeaderNow.Review);
        Assert.Equal(PlanReviewMark.None, PlanReview.Mark(PlanHeaderNow.Review, PlanBodyNow));
    }

    [Fact]
    public async Task PlanGaps_AtTheLimit_WithGapsLeft_RecordsIt()
    {
        _settings.Update(d => d.PlanGapPasses = 1);
        PlanGapsFixture([[Keys.Char('g'), Keys.Enter]], Line("/plan Add a guide"), Key(Keys.Escape), Line("/exit"));
        _chat.Enqueue(PresentPlanCall());
        _chat.EnqueueText("Closing.");
        _chat.Enqueue(PresentGapsCall("p2", RevisedPlanMarkdown, "Add a test step"));
        _chat.EnqueueText("Pass 1 done.");

        string output = await RunAsync();

        Assert.Contains("pass 1: 1 gap, limit", output);
        Assert.Equal(PlanText.ReviewLine(1, 1, PlanGapStop.Limit, 1, PlanDocument.Fingerprint(PlanBodyNow)), PlanHeaderNow.Review);
        Assert.EndsWith("stopped at the limit with gaps left (body " + PlanDocument.Fingerprint(PlanBodyNow) + ")", PlanHeaderNow.Review);
    }

    [Fact]
    public async Task PlanGaps_OneCleanPass_EndsAsBefore_ReviewedNotConfirmed_AndKeepRefiningSaysItOnce()
    {
        _settings.Update(d => d.PlanGapCleanPasses = 1);
        PlanFixture([[Keys.Char('g'), Keys.Enter], [Keys.Escape]], "/plan Add a guide", "/exit");
        _chat.Enqueue(PresentPlanCall());
        _chat.EnqueueText("Closing.");
        _chat.Enqueue(PresentGapsCall("p2", PlanMarkdown));   // pass 1 clean: the pane at once; Esc keeps refining
        _chat.EnqueueText("Noted.");

        string output = await RunAsync();

        Assert.Equal(4, _chat.Requests.Count);
        Assert.Equal(PlanText.RefineResult(PlanPath, null), PlanResultOf(_chat.Requests[3], "p2"));
        Assert.Contains(PlanText.ConvergedNote(1, 0), output);
        Assert.Equal(1, CountOccurrences(LastTranscript(output), PlanText.ConvergedNotice(PlanPath, 1, 0)));
        Assert.Contains("pass 1: clean, no gaps left", output);
        Assert.EndsWith(PlanText.ReviewOneCleanPass + " (body " + PlanDocument.Fingerprint(PlanBodyNow) + ")", PlanHeaderNow.Review);
        Assert.Equal(PlanReviewMark.Reviewed, PlanReview.Mark(PlanHeaderNow.Review, PlanBodyNow));
    }

    [Fact]
    public async Task PlanGaps_ALimitBelowTheCleanCount_LowersIt_WithANotice()
    {
        PlanGapsFixture([[Keys.Escape], [Keys.Escape]], Line("/plan Add a guide"), Line("/plan gaps 1"), Line("/exit"));
        _chat.Enqueue(PresentPlanCall());                     // Esc keeps refining
        _chat.EnqueueText("Noted.");
        _chat.Enqueue(PresentGapsCall("p2", PlanMarkdown));   // /plan gaps 1: its one clean pass converges
        _chat.EnqueueText("Noted.");

        string output = await RunAsync();

        Assert.Equal(4, _chat.Requests.Count);
        string transcript = LastTranscript(output);
        int notice = transcript.IndexOf(PlanText.CleanLoweredNotice(1, 2), StringComparison.Ordinal);
        Assert.True(notice >= 0 && notice < transcript.IndexOf(PlanText.ConvergedNotice(PlanPath, 1, 0), StringComparison.Ordinal));
        Assert.Equal(1, CountOccurrences(transcript, PlanText.CleanLoweredNotice(1, 2)));
        Assert.Contains(PlanText.ConvergedNotice(PlanPath, 1, 0), output);
        Assert.Contains(PlanText.ConvergedNote(1, 0), output);
    }

    [Fact]
    public async Task PlanGaps_Confirm_TheModelCloses_AndAnAdvisorTakesTheConfirmingPass()
    {
        AdvisorProfiles();
        PlanFixture([[Keys.Char('g'), Keys.Enter, Keys.Char('c'), Keys.Enter], [Keys.Char('c'), Keys.Enter]], "/plan Add a guide", "/exit");
        _chat.Enqueue(PresentPlanCall());
        _chat.EnqueueText("Closing.");
        _chat.Enqueue(PresentGapsCall("p2", PlanMarkdown));   // pass 1, the model's: clean
        _chat.EnqueueText("Clean.");
        _chat.Enqueue(PresentGapsCall("p3", PlanMarkdown));   // pass 2, the advisor's: clean, converged; canceled
        _chat.EnqueueText("Canceled.");

        string output = await RunAsync();

        Assert.Contains(PlanText.ReviewerRows[3], output);
        Assert.Equal(PlanText.GapPassMessage(PlanPath, 1, 5, PlanReviewer.Model), LastUserText(_chat.Requests[2]));
        Assert.Contains("Review it yourself; do not consult advisors on this pass.", _chat.Requests[2][0].Text);
        Assert.DoesNotContain("You have advisors with", _chat.Requests[2][0].Text);
        Assert.Equal(PlanText.GapPassMessage(PlanPath, 2, 5, PlanReviewer.Advisor, cleanSoFar: 1), LastUserText(_chat.Requests[4]));
        Assert.Contains("put the plan's text and this pass's focus in the question", _chat.Requests[4][0].Text);
        Assert.Contains("You have advisors with", _chat.Requests[4][0].Text);
        Assert.Contains(PlanText.ConvergedNotice(PlanPath, 2, 0, 2), output);
        Assert.Contains("pass 2: clean (order, advisor), confirmed", output);
    }

    [Fact]
    public async Task PlanGaps_Confirm_WithNoAdvisorOffered_EveryPassIsTheModels()
    {
        PlanGapsFixture([[Keys.Escape], [Keys.Escape]], Line("/plan Add a guide"), Line("/plan gaps confirm"), Line("/exit"));
        _chat.Enqueue(PresentPlanCall());
        _chat.EnqueueText("Noted.");
        _chat.Enqueue(PresentGapsCall("p2", PlanMarkdown));
        _chat.EnqueueText("Clean.");
        _chat.Enqueue(PresentGapsCall("p3", PlanMarkdown));
        _chat.EnqueueText("Noted.");

        string output = await RunAsync();

        Assert.Equal(6, _chat.Requests.Count);
        Assert.Equal(PlanText.GapPassMessage(PlanPath, 1, 5, PlanReviewer.Model), LastUserText(_chat.Requests[2]));
        Assert.Equal(PlanText.GapPassMessage(PlanPath, 2, 5, PlanReviewer.Model, cleanSoFar: 1), LastUserText(_chat.Requests[4]));
        Assert.Contains("No advisors are offered: review it yourself.", _chat.Requests[4][0].Text);
        Assert.Contains(PlanText.ConvergedNotice(PlanPath, 2, 0, 2), output);
    }

    [Fact]
    public async Task PlanGaps_CloseGapsOnTheConvergedPane_SaysTheFirstReview_ThenTheNewOneRuns()
    {
        _settings.Update(d => d.PlanGapCleanPasses = 1);
        PlanFixture([[Keys.Char('g'), Keys.Enter], [Keys.Char('g'), Keys.Enter], [Keys.Char('c'), Keys.Enter]], "/plan Add a guide", "/exit");
        _chat.Enqueue(PresentPlanCall());
        _chat.EnqueueText("Closing.");
        _chat.Enqueue(PresentGapsCall("p2", RevisedPlanMarkdown));   // review 1, pass 1: a revision, clean: converged; Close gaps… again
        _chat.EnqueueText("Again.");
        _chat.Enqueue(PresentGapsCall("p3", RevisedPlanMarkdown + "- [ ] Ship it\n", "Add a ship step"));   // review 2, pass 1: a gap
        _chat.EnqueueText("Pass 1 done.");
        _chat.Enqueue(PresentGapsCall("p4", RevisedPlanMarkdown + "- [ ] Ship it\n"));                       // review 2, pass 2: clean; canceled
        _chat.EnqueueText("Canceled.");

        string output = await RunAsync();

        Assert.Equal(8, _chat.Requests.Count);
        Assert.Equal(PlanText.CloseGapsResult(PlanPath), PlanResultOf(_chat.Requests[3], "p2"));
        Assert.Equal(PlanText.GapPassMessage(PlanPath, 1, 5, PlanReviewer.Model), LastUserText(_chat.Requests[4]));
        // The first review (one pass, nothing closed) said once its turn ended; the second's own after its two passes.
        Assert.Contains(PlanText.ConvergedNotice(PlanPath, 1, 0), output);
        Assert.Contains(PlanText.Glyph + " Gap review, 1 pass", output);
        Assert.Contains(PlanText.ConvergedNotice(PlanPath, 2, 1), output);
        Assert.Contains(PlanText.Glyph + " Gap review, 2 passes", output);
        Assert.Equal(PlanStatus.Cancelled, PlanHeaderNow.Status);
        Assert.NotNull(PlanHeaderNow.Review);
    }

    // ── /plan chat and /plan ready ──────────────────────────────────────────

    [Fact]
    public async Task PlanChat_SendsNothing_TheFirstMessageIsTheOpening_AndReadyTooSoonRefuses()
    {
        PlanFixture([], "/plan chat", "/plan ready", "Let's plan a guide", "/exit");
        _chat.EnqueueText("What should it cover?");

        string output = await RunAsync();

        var request = Assert.Single(_chat.Requests);
        Assert.Contains(PlanText.ChatEnteredNotice, output);
        Assert.Contains(PlanText.ReadyTooSoonError, output);
        Assert.Contains(PlanText.TalkingPlaceholder, output);
        Assert.Contains("The conversation began with: \"Let's plan a guide\"", request[0].Text);
        Assert.Contains(PlanText.TalkingLine, request[0].Text);
        Assert.DoesNotContain("Their requirement", request[0].Text);
        Assert.Equal("Let's plan a guide", LastUserText(request));
    }

    [Fact]
    public async Task PlanChat_APresentationFromWords_SettlesTheTitle_AndEndsTalking()
    {
        PlanFixture([[Keys.Escape], [Keys.Escape]], "/plan chat", "Let's plan a guide", "ok, write it up", "retitle it", "/exit");
        _chat.EnqueueText("What should it cover?");   // 0
        _chat.Enqueue(PresentPlanCall());              // 1: "write it up": presented, Esc
        _chat.EnqueueText("Saved.");                   // 2
        _chat.Enqueue(FakeChatClient.Call("p9", Llm.Tools.PresentPlanTool.ToolName, new Dictionary<string, object?>
        {
            [Llm.Tools.PresentPlanTool.TitleArgument] = "Contributing guide",
            [Llm.Tools.PresentPlanTool.MarkdownArgument] = RevisedPlanMarkdown,
        }));                                           // 3: a second revision with a new title, Esc
        _chat.EnqueueText("Retitled.");                // 4

        string output = await RunAsync();

        Assert.Equal(5, _chat.Requests.Count);
        Assert.Contains(PlanText.TalkingLine, _chat.Requests[1][0].Text);
        Assert.Contains("Their requirement: \"Guide\"", _chat.Requests[3][0].Text);
        Assert.DoesNotContain(PlanText.TalkingLine, _chat.Requests[3][0].Text);
        Assert.DoesNotContain("The conversation began with", _chat.Requests[3][0].Text);
        Assert.Equal("Guide", PlanHeaderNow.Requirement);      // later revisions keep the first title
        Assert.Equal(2, PlanHeaderNow.Revision);
        Assert.Contains(PlanText.Placeholder, output);
    }

    [Fact]
    public async Task PlanChat_APlanLikeReply_GivesNoHint_ButPlanSaveKeepsIt_AndEndsTalking()
    {
        SaveFixture(Line("/plan chat"), Line("Let's plan a guide"), Line("/plan save"), Key(Keys.Escape), Line("hello"), Line("/exit"));
        _chat.EnqueueText(PlanLikeReply);
        _chat.EnqueueText("Hi.");

        string output = await RunAsync();

        Assert.DoesNotContain(PlanText.UnpresentedHint, output);
        Assert.Contains(PlanText.SavedNotice(".neon/plans/guide.md", 1), output);
        string file = File.ReadAllText(Path.Combine(PlansFolder, "guide.md"));
        Assert.Equal("Guide", PlanDocument.TryParse(file)!.Requirement);
        Assert.Equal(2, _chat.Requests.Count);
        Assert.Contains("Their requirement: \"Guide\"", _chat.Requests[1][0].Text);
        Assert.DoesNotContain(PlanText.TalkingLine, _chat.Requests[1][0].Text);
    }

    [Fact]
    public async Task PlanReady_BeforeAPresentation_AsksForIt_AfterOne_OpensThePane_AndApprovingRunsIt()
    {
        PlanGapsFixture([[Keys.Escape]], Line("/plan Add a guide"), Line("/plan ready"), Line("/plan ready"), Key(Keys.Char('a')), Key(Keys.Enter), Line("/exit"));
        _chat.EnqueueText("A question first: which framework?");   // 0
        _chat.Enqueue(PresentPlanCall());                            // 1: /plan ready: presented; Esc
        _chat.EnqueueText("Waiting.");                               // 2
        _chat.EnqueueText("Done.");                                  // 3: /plan ready again: the pane, approved, carried out

        await RunAsync();

        Assert.Equal(4, _chat.Requests.Count);
        Assert.Equal(PlanText.ReadyMessage, LastUserText(_chat.Requests[1]));
        Assert.Equal(PlanText.ExecuteMessage(PlanPath), LastUserText(_chat.Requests[3]));
        Assert.Equal(1, PlanHeaderNow.Revision);
        Assert.NotEqual(PlanStatus.Draft, PlanHeaderNow.Status);   // approved, then incomplete once carried out unticked
    }

    [Fact]
    public async Task PlanChat_ResumedAfterAPresentation_ReadyAsksAgain_AndAGapReviewEndsTalking()
    {
        _settings.Update(d => d.PlanGapCleanPasses = 1);
        PlanFixture([[Keys.Escape], [Keys.Escape]], "/plan chat", "Let's plan a guide", "/plan ready", "/plan chat", "/plan ready", "/plan chat", "/plan gaps", "/exit");
        _chat.EnqueueText("What for?");                       // 0
        _chat.Enqueue(PresentPlanCall());                     // 1: /plan ready: presented; Esc
        _chat.EnqueueText("Saved.");                          // 2
        _chat.EnqueueText("Here it is again.");               // 3: resumed, then /plan ready: the message again
        _chat.Enqueue(PresentGapsCall("p2", PlanMarkdown));   // 4: resumed, then /plan gaps: the pass, clean; Esc
        _chat.EnqueueText("Noted.");                          // 5

        string output = await RunAsync();

        Assert.Equal(6, _chat.Requests.Count);
        Assert.Contains(PlanText.ChatResumedNotice, output);
        Assert.Contains(PlanText.TalkingLine, _chat.Requests[0][0].Text);
        Assert.DoesNotContain(PlanText.TalkingLine, _chat.Requests[1][0].Text);
        Assert.Equal(PlanText.ReadyMessage, LastUserText(_chat.Requests[3]));
        Assert.Equal(PlanText.GapPassMessage(PlanPath, 1, 5, PlanReviewer.Model), LastUserText(_chat.Requests[4]));
        Assert.Contains("A gap review is running", _chat.Requests[4][0].Text);
        Assert.DoesNotContain(PlanText.TalkingLine, _chat.Requests[4][0].Text);
    }
}
