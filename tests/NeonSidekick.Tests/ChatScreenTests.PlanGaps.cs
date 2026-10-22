using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Plans;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;
using Spectre.Console.Testing;

namespace NeonSidekick.Tests;

/// <summary>
/// Plan mode's gap-closing loop on the screen (2026-10-13, the user's ask): Close gaps… on the approval pane (or <c>/plan gaps</c>)
/// sends a pass per turn until one finds no gaps, which brings the pane back; a limit, a stall or a pass with no revision stops it.
/// </summary>
public partial class ChatScreenTests
{
    private const string RevisedPlanMarkdown = "# Guide\n\n## Steps\n- [ ] Write it\n  Done when: guide.md exists\n- [ ] Test it\n  Done when: the guide's check passes\n";

    /// <summary>A pass's <c>present_plan</c>; with no gaps, a <c>checked</c> line too (2026-10-17: every clean report says what it checked).</summary>
    private static ChatResponseUpdate PresentGapsCall(string id, string markdown, params string[] gaps)
    {
        var args = new Dictionary<string, object?>
        {
            [PresentPlanTool.TitleArgument] = "Guide",
            [PresentPlanTool.MarkdownArgument] = markdown,
            [PresentPlanTool.GapsArgument] = gaps,
        };
        if (gaps.Length == 0)
        {
            args[PresentPlanTool.CheckedArgument] = new[] { "the steps" };
        }

        return FakeChatClient.Call(id, PresentPlanTool.ToolName, args);
    }

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
        _chat.Enqueue(PresentGapsCall("p3", RevisedPlanMarkdown));                      // 4: pass 2 finds none: clean 1 of 2, no pane (2026-10-17)
        _chat.EnqueueText("Pass 2 clean.");                                             // 5
        _chat.Enqueue(PresentGapsCall("p4", RevisedPlanMarkdown));                      // 6: pass 3 confirms: the pane, approved
        _chat.EnqueueText("Approved, starting.");                                       // 7
        _chat.EnqueueText("Done.");                                                     // 8: carried out

        string output = await RunAsync();

        Assert.Equal(9, _chat.Requests.Count);
        Assert.Equal(PlanText.CloseGapsResult(PlanPath), PlanResultOf(_chat.Requests[1], "p1"));
        Assert.Equal(PlanText.GapPassMessage(PlanPath, 1, 5, PlanReviewer.Model), LastUserText(_chat.Requests[2]));
        Assert.Contains("A gap review is running (pass 1 of 5)", _chat.Requests[2][0].Text);
        Assert.Contains(PresentPlanTool.ToolName, OfferedNames(_chat.Options[2]));
        Assert.DoesNotContain(WriteFileTool.ToolName, OfferedNames(_chat.Options[2]));
        Assert.Equal(PlanText.GapPassResult(PlanPath, 2, 1, bodyChars: PlanGapLoopTests.BodyChars(RevisedPlanMarkdown)), PlanResultOf(_chat.Requests[3], "p2"));
        Assert.Equal(PlanText.GapPassMessage(PlanPath, 2, 5, PlanReviewer.Model), LastUserText(_chat.Requests[4]));
        Assert.Contains("A gap review is running (pass 2 of 5)", _chat.Requests[4][0].Text);
        Assert.Contains("only 2 passes in a row with no gaps return the plan to the user", _chat.Requests[4][0].Text);
        Assert.DoesNotContain("this pass checks again", _chat.Requests[4][0].Text);
        // Pass 2 clean: no pane, the streak said; pass 3 is told to look again, with the first focus.
        Assert.Equal(PlanText.CleanPassResult(PlanPath, 2, 1, 2, bodyChars: PlanGapLoopTests.BodyChars(RevisedPlanMarkdown)), PlanResultOf(_chat.Requests[5], "p3"));
        Assert.Equal(PlanText.GapPassMessage(PlanPath, 3, 5, PlanReviewer.Model, cleanSoFar: 1), LastUserText(_chat.Requests[6]));
        Assert.Contains("this pass checks again (clean pass 2 of 2)", _chat.Requests[6][0].Text);
        Assert.Contains(PlanText.ConfirmFocuses[0], _chat.Requests[6][0].Text);
        Assert.Equal(PlanText.ApprovedResult(PlanPath), PlanResultOf(_chat.Requests[7], "p4"));
        Assert.Equal(PlanText.ExecuteMessage(PlanPath), LastUserText(_chat.Requests[8]));
        Assert.DoesNotContain("gap review", _chat.Requests[8][0].Text);
        // The pane twice, its row and the note; each pass said; no new revision for an unchanged plan.
        Assert.Contains(PlanText.CloseGapsRow, output);
        Assert.Contains(PlanText.ApprovalKeys, output);
        Assert.DoesNotContain(PlanText.ReviewerTitle, output);
        Assert.Contains(PlanText.PassNoticeLines(1, 5, 2, ["Add a test step"])[0], output);
        Assert.Contains("· Add a test step", output);
        Assert.Contains(PlanText.CleanPassNotice(2, 5, 1, 2), output);
        Assert.Contains("· checked: the steps", output);
        Assert.Contains(PlanText.ConvergedNotice(PlanPath, 3, 1, 2), output);
        Assert.Contains(PlanText.ConvergedNote(3, 1, 2), output);
        // The summary ahead of the converged notice, said once.
        int summary = output.IndexOf(PlanText.Glyph + " Gap review, 3 passes", StringComparison.Ordinal);
        Assert.True(summary >= 0 && summary < output.IndexOf(PlanText.ConvergedNotice(PlanPath, 3, 1, 2), StringComparison.Ordinal));
        Assert.Contains("pass 3: clean (order), confirmed", output);
        Assert.Contains("Test it", File.ReadAllText(PlanFile));
        var header = PlanDocument.TryParse(File.ReadAllText(PlanFile))!;
        Assert.Equal(2, header.Revision);
        // The review recorded in the header (2026-10-17), over the body as it stood; approval kept it.
        Assert.Equal(PlanText.ReviewLine(3, 1, PlanGapStop.Converged, 2, PlanDocument.Fingerprint(PlanDocument.Body(File.ReadAllText(PlanFile)))), header.Review);
        Assert.Contains("Done.", output);
    }

    /// <summary>
    /// A reflection running when a review starts waits for the review's end (2026-10-21, the user's 70-pass log: the one a 5-pass review's end
    /// started was paused by every next pass and put back in the slot after it, so it started again between passes, 67 starts and 65
    /// cancels): paused once by the first pass, held in the slot after it, and run once the review is over.
    /// </summary>
    [Fact]
    public async Task PlanGaps_AReflectionPausedByAPass_WaitsInTheSlot_UntilTheReviewEnds()
    {
        _settings.Update(d => { d.ReflectionAutoLearn = true; d.ReflectionYieldsToTurns = true; });
        var events = new List<string>();
        Action<NeonSidekick.Diagnostics.DiagnosticEvent> capture = e => { if (e.Category == NeonSidekick.Skills.SkillCatalog.Category) { lock (events) { events.Add(e.Message); } } };
        PlanGapsFixture([[Keys.Escape]],
            Line("/plan Add a guide"),
            input => { WaitForRequests(3); PushLine(input, "/plan gaps 2"); },   // the reflection's request open first
            Key(Keys.Escape),
            input => { WaitForRequests(8); PushLine(input, "/exit"); });
        _chat.Enqueue([.. Enumerable.Range(1, 5).Select(i => FakeChatClient.Call("c" + i, GetCurrentTimeTool.ToolName)), PresentPlanCall()]);   // 0: six calls, presented; Esc
        _chat.EnqueueText("Noted.");                                                                           // 1: the turn ends: a reflection starts
        _chat.Enqueue(FakeChatClient.Call("r1", SkillEditorTool.ToolName, CreateSkillArgs("clock-check")));   // 2: the reflection, held until pass 1 pauses it
        _chat.Enqueue(PresentGapsCall("p2", RevisedPlanMarkdown, "Add a test step"));                         // 3: pass 1 of 2
        _chat.EnqueueText("Pass 1 done.");                                                                     // 4: the paused one back in the slot, held
        _chat.Enqueue(PresentGapsCall("p3", RevisedPlanMarkdown + "- [ ] Ship it\n", "Add a ship step"));    // 5: pass 2 of 2, the limit
        _chat.EnqueueText("Pass 2 done.");                                                                     // 6: the review over
        _chat.Enqueue(FakeChatClient.Call("r2", SkillEditorTool.ToolName, CreateSkillArgs("clock-check")));   // 7: the reflection, run again
        _chat.BeforeUpdateOf = async (r, i, token) =>
        {
            if (r == 2 && i == 0)
            {
                await Task.Delay(Timeout.Infinite, token);
            }
        };

        NeonSidekick.Diagnostics.DiagnosticLog.Emitted += capture;
        try
        {
            await RunAsync();
        }
        finally
        {
            NeonSidekick.Diagnostics.DiagnosticLog.Emitted -= capture;
        }

        Assert.Equal(8, _chat.Requests.Count);
        Assert.Equal(NeonSidekick.Skills.SkillLearner.Request(null), _chat.Requests[2][^1].Text);
        Assert.Equal(PlanText.GapPassMessage(PlanPath, 1, 2, PlanReviewer.Model), LastUserText(_chat.Requests[3]));
        Assert.Equal(PlanText.GapPassMessage(PlanPath, 2, 2, PlanReviewer.Model), LastUserText(_chat.Requests[5]));   // not the reflection
        Assert.Equal(NeonSidekick.Skills.SkillLearner.Request(null), _chat.Requests[7][^1].Text);
        List<string> lines;
        lock (events)
        {
            lines = [.. events];
        }

        Assert.Single(lines, l => l == ChatScreen.LearnPausedLogLine);   // paused by pass 1 only: never started again under the review
        Assert.Single(lines, l => l == "Reflection starts from the slot.");
    }
    /// <summary>
    /// No reflection on a review's last pass (2026-10-22, the user's call after a review: it reflected on every pass's calls and proposed a skill for the
    /// review loop itself): the passes add nothing to the tally either.
    /// </summary>
    [Fact]
    public async Task PlanGaps_TheLastPass_StartsNoReflection()
    {
        _settings.Update(d => { d.ReflectionAutoLearn = true; d.PlanGapPasses = 1; });
        PlanGapsFixture([[Keys.Escape]],
            Line("/plan Add a guide"),
            input => { WaitForRequests(2); PushLine(input, "/plan gaps"); },
            Key(Keys.Escape),
            Line("/exit"));
        _chat.Enqueue(PresentPlanCall());                                                                            // 0: presented; Esc
        _chat.EnqueueText("Noted.");                                                                                 // 1
        _chat.Enqueue([.. Enumerable.Range(1, 5).Select(i => FakeChatClient.Call("c" + i, GetCurrentTimeTool.ToolName)), PresentGapsCall("p2", RevisedPlanMarkdown, "Add a test step")]);   // 2: pass 1 of 1
        _chat.EnqueueText("Pass 1 done.");                                                                           // 3: the limit

        string output = await RunAsync();

        Assert.Contains(PlanText.GapStopNotice(PlanGapStop.Limit, 1, 1), output);
        Assert.Equal(4, _chat.Requests.Count);
        Assert.DoesNotContain(_chat.Requests, r => r[^1].Text == NeonSidekick.Skills.SkillLearner.Request(null));
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

    /// <summary>
    /// The start of <paramref name="line"/> as the terminal draws it in <see cref="NoticeTone.Progress"/> (2026-10-21): bold in the tertiary
    /// accent. Only the start, so a line the window wraps still matches.
    /// </summary>
    private static string ProgressStart(string line)
    {
        var tertiary = Theme.Tertiary;
        return $"\u001b[1;38;2;{tertiary.R};{tertiary.G};{tertiary.B}m" + line[..Math.Min(line.Length, 24)];
    }

    /// <summary>A review's pass lines and its done line stand out (2026-10-21, the user's ask): the tertiary style; each gap under a pass stays plain.</summary>
    [Fact]
    public async Task PlanGaps_ThePassAndDoneLines_AreTheProgressStyle_AndTheirItemsPlain()
    {
        PlanFixture([[Keys.Char('g'), Keys.Enter], [Keys.Char('a'), Keys.Enter]], "/plan Add a guide", "/exit");
        _console.EmitAnsiSequences();
        _chat.Enqueue(PresentPlanCall());
        _chat.EnqueueText("Closing the gaps.");
        _chat.Enqueue(PresentGapsCall("p2", RevisedPlanMarkdown, "Add a test step"));
        _chat.EnqueueText("Pass 1 done.");
        _chat.Enqueue(PresentGapsCall("p3", RevisedPlanMarkdown));
        _chat.EnqueueText("Pass 2 clean.");
        _chat.Enqueue(PresentGapsCall("p4", RevisedPlanMarkdown));
        _chat.EnqueueText("Approved, starting.");
        _chat.EnqueueText("Done.");

        string output = await RunAsync();

        Assert.Contains(ProgressStart(PlanText.PassNoticeLines(1, 5, 2, ["Add a test step"])[0]), output);
        Assert.Contains(ProgressStart(PlanText.CleanPassNotice(2, 5, 1, 2)), output);
        Assert.Contains(ProgressStart(PlanText.ConvergedNotice(PlanPath, 3, 1, 2)), output);
        Assert.Contains("Add a test step", output);
        Assert.DoesNotContain(ProgressStart("  · Add a test step"), output);
    }

    /// <summary>A review stopped at its limit says so in the progress style too (2026-10-21).</summary>
    [Fact]
    public async Task PlanGaps_TheStopLine_IsTheProgressStyle()
    {
        _settings.Update(d => d.PlanGapPasses = 1);
        PlanGapsFixture([[Keys.Char('g'), Keys.Enter]], Line("/plan Add a guide"), Key(Keys.Escape), Line("/exit"));
        _console.EmitAnsiSequences();
        _chat.Enqueue(PresentPlanCall());
        _chat.EnqueueText("Closing the gaps.");
        _chat.Enqueue(PresentGapsCall("p2", RevisedPlanMarkdown, "Add a test step"));
        _chat.EnqueueText("Pass 1 done.");

        string output = await RunAsync();

        Assert.Contains(ProgressStart(PlanText.GapStopNotice(PlanGapStop.Limit, 1, 1)), output);
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
        _settings.Update(d => d.PlanGapCleanPasses = 1);   // the second run's one clean pass ends it; confirming is not this test's subject
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
        // Canceled on the converged pane (2026-10-17): the review is still said and recorded before plan mode ends, never lost.
        Assert.Contains(PlanText.ConvergedNotice(PlanPath, 1, 0), output);
        Assert.Equal(PlanText.ReviewLine(1, 0, PlanGapStop.Converged, 1, PlanDocument.Fingerprint(PlanDocument.Body(File.ReadAllText(PlanFile)))), PlanDocument.TryParse(File.ReadAllText(PlanFile))!.Review);
        Assert.Equal(PlanStatus.Cancelled, PlanDocument.TryParse(File.ReadAllText(PlanFile))!.Status);
    }

    [Fact]
    public async Task PlanGaps_TheWordsThatCannotRun()
    {
        PlanFixture([], "/plan gaps", "/plan Add a guide", "/plan gaps", "/plan gaps advsior", "/plan gaps 101", "/exit");
        _chat.EnqueueText("What should it cover?");

        string output = await RunAsync();

        Assert.Single(_chat.Requests);
        Assert.Contains(PlanText.NotPlanningError, output);
        Assert.Contains(PlanText.NothingPresentedError, output);
        Assert.Contains(PlanText.UnknownReviewerError("advsior"), output);
        Assert.Contains(PlanText.GapPassesRangeError("101"), output);
    }

    [Fact]
    public async Task PlanGaps_ACount_CapsThisRun_InsteadOfTheSetting()
    {
        // 2026-10-17, the user's ask: /plan gaps 2 runs two passes while Plan gap max passes says 5.
        PlanGapsFixture([[Keys.Escape]], Line("/plan Add a guide"), Line("/plan gaps 2"), Key(Keys.Escape), Line("/exit"));
        _chat.Enqueue(PresentPlanCall());                                                                      // 0: presented; Esc keeps refining
        _chat.EnqueueText("Noted.");                                                                           // 1
        _chat.Enqueue(PresentGapsCall("p2", RevisedPlanMarkdown, "Add a test step"));                         // 2: pass 1 of 2
        _chat.EnqueueText("Pass 1 done.");                                                                     // 3
        _chat.Enqueue(PresentGapsCall("p3", RevisedPlanMarkdown + "- [ ] Ship it\n", "Add a ship step"));    // 4: pass 2 of 2, the limit
        _chat.EnqueueText("Pass 2 done.");                                                                     // 5

        string output = await RunAsync();

        Assert.Equal(PreFlipDefaults.Data().PlanGapPasses, _settings.Current.PlanGapPasses);   // the setting untouched: the fixture's 5 (the default is 50 since 2026-10-21)
        Assert.Equal(6, _chat.Requests.Count);
        Assert.Equal(PlanText.GapPassMessage(PlanPath, 1, 2, PlanReviewer.Model), LastUserText(_chat.Requests[2]));
        Assert.Contains("A gap review is running (pass 1 of 2)", _chat.Requests[2][0].Text);
        Assert.Equal(PlanText.GapPassMessage(PlanPath, 2, 2, PlanReviewer.Model), LastUserText(_chat.Requests[4]));
        Assert.Contains(PlanText.GapStopNotice(PlanGapStop.Limit, 2, 2), output);
        Assert.Contains(PlanText.StoppedNote(PlanGapStop.Limit, 2, 1), output);
        Assert.Contains($"Plan: {PlanPath} (revision 3)", output);
    }

    [Fact]
    public async Task PlanGaps_WithAdvisors_ThePickerAsksWho_AndTheAdvisorRuleRides()
    {
        AdvisorProfiles();
        _settings.Update(d => d.PlanGapCleanPasses = 1);
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
        _settings.Update(d => d.PlanGapCleanPasses = 1);
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
