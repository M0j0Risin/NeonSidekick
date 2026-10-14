using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Plans;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// Plan mode's gap-closing loop on the screen (2026-10-13, the user's ask): Close gaps… on the approval pane (or <c>/plan gaps</c>)
/// sends a pass per turn until one finds no gaps, which brings the pane back; a limit, a stall or a pass with no revision stops it.
/// </summary>
public partial class ChatScreenTests
{
    private const string RevisedPlanMarkdown = "# Guide\n\n## Steps\n- [ ] Write it\n- [ ] Test it\n";

    private static ChatResponseUpdate PresentGapsCall(string id, string markdown, params string[] gaps) =>
        FakeChatClient.Call(id, PresentPlanTool.ToolName, new Dictionary<string, object?>
        {
            [PresentPlanTool.TitleArgument] = "Guide",
            [PresentPlanTool.MarkdownArgument] = markdown,
            [PresentPlanTool.GapsArgument] = gaps,
        });

    /// <summary><see cref="PlanFixture"/> with idle steps of any kind: the pane a stopped loop brings back waits at the idle line.</summary>
    private void PlanGapsFixture(ConsoleKeyInfo[][] answers, params Action<ScriptedInput>[] steps)
    {
        _settings.Update(d => d.TtsOutput = false);
        _console.Profile.Height = 40;
        _geometry = new ScreenGeometry(() => null);
        var input = Scripted();
        StepsWhenIdle(steps);
        var idle = input.OnWait!;
        var pending = new Queue<ConsoleKeyInfo[]>(answers);
        Task? answered = null;
        input.OnWait = () =>
        {
            if (_keys is { PendingLine: { IsCompleted: false } pane })
            {
                if (!ReferenceEquals(pane, answered) && pending.TryDequeue(out var keys))
                {
                    answered = pane;
                    input.Push(keys);
                }

                return;
            }

            idle();
        };
    }

    [Fact]
    public async Task PlanGaps_PassesUntilNoneAreLeft_ThenThePaneAgain_AndApproveRunsIt()
    {
        PlanFixture([[Keys.Char('g'), Keys.Enter], [Keys.Char('a'), Keys.Enter]], "/plan Add a guide", "/exit");
        _chat.Enqueue(PresentPlanCall());                                               // 0: presented; Close gaps… (no advisors: the model)
        _chat.EnqueueText("Closing the gaps.");                                         // 1
        _chat.Enqueue(PresentGapsCall("p2", RevisedPlanMarkdown, "Add a test step"));  // 2: pass 1 closes one
        _chat.EnqueueText("Pass 1 done.");                                              // 3
        _chat.Enqueue(PresentGapsCall("p3", RevisedPlanMarkdown));                      // 4: pass 2 finds none: the pane, approved
        _chat.EnqueueText("Approved, starting.");                                       // 5
        _chat.EnqueueText("Done.");                                                     // 6: carried out

        string output = await RunAsync();

        Assert.Equal(7, _chat.Requests.Count);
        Assert.Equal(PlanText.CloseGapsResult(PlanPath), PlanResultOf(_chat.Requests[1], "p1"));
        Assert.Equal(PlanText.GapPassMessage(PlanPath, 1, 5, PlanReviewer.Model), LastUserText(_chat.Requests[2]));
        Assert.Contains("A gap review is running (pass 1 of 5)", _chat.Requests[2][0].Text);
        Assert.Contains(PresentPlanTool.ToolName, OfferedNames(_chat.Options[2]));
        Assert.DoesNotContain(WriteFileTool.ToolName, OfferedNames(_chat.Options[2]));
        Assert.Equal(PlanText.GapPassResult(PlanPath, 2, 1), PlanResultOf(_chat.Requests[3], "p2"));
        Assert.Equal(PlanText.GapPassMessage(PlanPath, 2, 5, PlanReviewer.Model), LastUserText(_chat.Requests[4]));
        Assert.Contains("A gap review is running (pass 2 of 5)", _chat.Requests[4][0].Text);
        Assert.Equal(PlanText.ApprovedResult(PlanPath), PlanResultOf(_chat.Requests[5], "p3"));
        Assert.Equal(PlanText.ExecuteMessage(PlanPath), LastUserText(_chat.Requests[6]));
        Assert.DoesNotContain("gap review", _chat.Requests[6][0].Text);
        // The pane twice, its row and the note; each pass said; no new revision for an unchanged plan.
        Assert.Contains(PlanText.CloseGapsRow, output);
        Assert.Contains(PlanText.ApprovalKeys, output);
        Assert.DoesNotContain(PlanText.ReviewerTitle, output);
        Assert.Contains(PlanText.PassNoticeLines(1, 5, 2, ["Add a test step"])[0], output);
        Assert.Contains("· Add a test step", output);
        Assert.Contains(PlanText.ConvergedNotice(PlanPath, 2, 1), output);
        Assert.Contains(PlanText.ConvergedNote(2, 1), output);
        Assert.Contains("Test it", File.ReadAllText(PlanFile));
        Assert.Equal(2, PlanDocument.TryParse(File.ReadAllText(PlanFile))!.Revision);
        Assert.Contains("Done.", output);
    }

    [Fact]
    public async Task PlanGaps_ChosenOnThePane_AMessageQueuedUnderTheReply_GoesFirst()
    {
        // 2026-10-14, the review: the first pass went ahead of a message queued under the reply.
        PlanFixture([[Keys.Char('g'), Keys.Enter]], "/plan Add a guide", "/exit");
        _chat.Enqueue(PresentPlanCall());           // 0: presented; Close gaps…
        _chat.EnqueueText("Closing the gaps.");     // 1: a message typed under it
        _chat.EnqueueText("Noted.");                // 2: the message, no pass
        PushLineDuringTurn(2, "Use xUnit");

        string output = await RunAsync();

        Assert.Equal(3, _chat.Requests.Count);
        Assert.Equal("Use xUnit", LastUserText(_chat.Requests[2]));
        Assert.Contains(PlanText.GapsYieldedNotice, output);
        Assert.DoesNotContain("A gap review is running", _chat.Requests[2][0].Text);
    }

    [Fact]
    public async Task PlanGaps_AtTheLimit_ThePaneComesBack_WithWhatIsLeft()
    {
        _settings.Update(d => d.PlanGapPasses = 1);
        // The pane after a stop is an idle-line pane (the /plan save shape): its ESC is an idle step.
        PlanGapsFixture([[Keys.Char('g'), Keys.Enter]], Line("/plan Add a guide"), Key(Keys.Escape), Line("/exit"));
        _chat.Enqueue(PresentPlanCall());
        _chat.EnqueueText("Closing the gaps.");
        _chat.Enqueue(PresentGapsCall("p2", RevisedPlanMarkdown, "Add a test step"));
        _chat.EnqueueText("Pass 1 done.");

        string output = await RunAsync();

        Assert.Equal(4, _chat.Requests.Count);
        Assert.Equal(PlanText.GapPassMessage(PlanPath, 1, 1, PlanReviewer.Model), LastUserText(_chat.Requests[2]));
        Assert.Contains(PlanText.GapStopNotice(PlanGapStop.Limit, 1, 1), output);
        Assert.Contains(PlanText.StoppedNote(PlanGapStop.Limit, 1, 1), output);
        // ESC on the pane: still planning, revision 2 waiting for /plan approve.
        Assert.Contains($"Plan: {PlanPath} (revision 2)", output);
    }

    [Fact]
    public async Task PlanGaps_APassWithNoRevision_Stops_AndPlanGapsStartsAgain()
    {
        PlanFixture([[Keys.Char('g'), Keys.Enter], [Keys.Char('c'), Keys.Enter]], "/plan Add a guide", "/plan gaps", "/exit");
        _chat.Enqueue(PresentPlanCall());
        _chat.EnqueueText("Closing the gaps.");
        _chat.EnqueueText("Which test framework do you use?");      // pass 1: a question in prose, no revision
        _chat.Enqueue(PresentGapsCall("p2", PlanMarkdown));          // /plan gaps: pass 1 again, nothing left; cancelled on the pane
        _chat.EnqueueText("Canceled.");

        string output = await RunAsync();

        Assert.Equal(5, _chat.Requests.Count);
        Assert.Contains(PlanText.GapStopNotice(PlanGapStop.NoRevision, 1, 0), output);
        Assert.Equal(PlanText.GapPassMessage(PlanPath, 1, 5, PlanReviewer.Model), LastUserText(_chat.Requests[3]));
        Assert.Equal(PlanText.CancelledResult(PlanPath), PlanResultOf(_chat.Requests[4], "p2"));
        Assert.DoesNotContain(PlanText.ConvergedNotice(PlanPath, 1, 0), output);
    }

    [Fact]
    public async Task PlanGaps_TheWordsThatCannotRun()
    {
        PlanFixture([], "/plan gaps", "/plan Add a guide", "/plan gaps", "/plan gaps advsior", "/exit");
        _chat.EnqueueText("What should it cover?");

        string output = await RunAsync();

        Assert.Single(_chat.Requests);
        Assert.Contains(PlanText.NotPlanningError, output);
        Assert.Contains(PlanText.NothingPresentedError, output);
        Assert.Contains(PlanText.UnknownReviewerError("advsior"), output);
    }

    [Fact]
    public async Task PlanGaps_WithAdvisors_ThePickerAsksWho_AndTheAdvisorRuleRides()
    {
        AdvisorProfiles();
        PlanFixture([[Keys.Char('g'), Keys.Enter, Keys.Char('a'), Keys.Enter], [Keys.Char('c'), Keys.Enter]], "/plan Add a guide", "/exit");
        _chat.Enqueue(PresentPlanCall());
        _chat.EnqueueText("Closing the gaps.");
        _chat.Enqueue(PresentGapsCall("p2", PlanMarkdown));
        _chat.EnqueueText("Canceled.");

        string output = await RunAsync();

        Assert.Contains(PlanText.ReviewerTitle, output);
        Assert.Equal(PlanText.GapPassMessage(PlanPath, 1, 5, PlanReviewer.Advisor), LastUserText(_chat.Requests[2]));
        Assert.Contains("Ask at least one advisor with neon_advisor", _chat.Requests[2][0].Text);
        Assert.Contains(PlanText.AdvisorsLine([new AdvisorEntry("dba", "database architect")]), _chat.Requests[2][0].Text);
    }

    [Fact]
    public async Task PlanGaps_TheModelReviewing_LeavesTheAdvisorsOut_AndNoPickerWhenTheToolIsOff()
    {
        AdvisorProfiles();
        _settings.Update(d => d.ToolsDisabled = [NeonAdvisorTool.ToolName]);
        PlanFixture([[Keys.Char('g'), Keys.Enter], [Keys.Char('c'), Keys.Enter]], "/plan Add a guide", "/exit");
        _chat.Enqueue(PresentPlanCall());
        _chat.EnqueueText("Closing the gaps.");
        _chat.Enqueue(PresentGapsCall("p2", PlanMarkdown));
        _chat.EnqueueText("Canceled.");

        string output = await RunAsync();

        Assert.DoesNotContain(PlanText.ReviewerTitle, output);
        Assert.Contains("Review it yourself; do not consult advisors on this pass.", _chat.Requests[2][0].Text);
        Assert.DoesNotContain("You have advisors with", _chat.Requests[2][0].Text);
    }
}
