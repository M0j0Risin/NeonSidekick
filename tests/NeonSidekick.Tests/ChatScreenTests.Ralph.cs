using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Plans;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// The Ralph loop on the screen (2026-10-18, the plan at ~/.claude/plans/ok-we-re-in-plan-shimmering-canyon.md): a plan run one step a pass, each
/// pass a fresh conversation whose <c>finish_step</c> the app judges with the plan's verify command (<see cref="FakeRalphVerifier"/>), the evidence
/// and a review; the baseline and its start anyway, a failed check's tail in the next pass, Esc under a check, blocked, split, a stall, the pause on
/// a typed message and the resume, the checkpoint, the lock, the asking tools, the approval pane's row and the picker's button.
/// </summary>
public partial class ChatScreenTests
{
    private const string RalphSteps = "# Guide\n\n## Steps\n- [ ] One\n  Done when: one.md exists\n- [ ] Two\n  Done when: two.md exists\n";
    private const string RalphVerify = "dotnet build";

    /// <summary>The add-guide plan with <paramref name="body"/>'s steps and its checks, on disk before the run.</summary>
    private void RalphPlan(string body = RalphSteps, string? verify = RalphVerify, string? final = null, PlanStatus status = PlanStatus.Incomplete)
    {
        Directory.CreateDirectory(PlansFolder);
        var at = new DateTimeOffset(2026, 9, 20, 9, 0, 0, TimeSpan.Zero);
        File.WriteAllText(PlanFile, PlanDocument.Render(new PlanHeader(status, 2, at, at, "Add a guide", Verify: verify, Final: final), "Guide", body));
    }

    /// <summary>
    /// The pane on, the review off (the reviewer's own tests turn it on) and a silent stop tone unless a test asks for one; <paramref name="steps"/>
    /// one per idle wait. The loop's panes (the confirm, the baseline's question, the checkpoint) open at the idle line, not under a reply, so their
    /// answers are steps too, in turn.
    /// </summary>
    private void RalphFixture(params Action<ScriptedInput>[] steps)
    {
        _settings.Update(d => { d.TtsOutput = false; d.RalphLoopReviewer = "off"; d.RalphLoopStopTone = "silent"; });
        _console.Profile.Height = 40;
        _geometry = new ScreenGeometry(() => null);
        StepsWhenIdle(steps);
    }

    private static readonly Action<ScriptedInput> Yes = input => input.Push([Keys.Char('y'), Keys.Enter]);
    private static readonly Action<ScriptedInput> No = input => input.Push([Keys.Char('n'), Keys.Enter]);

    private static Action<ScriptedInput> Answer(params ConsoleKeyInfo[] keys) => input => input.Push(keys);

    private static ChatResponseUpdate FinishCall(string id, string json) =>
        FakeChatClient.Call(id, FinishStepTool.ToolName, JsonDocument.Parse(json).RootElement.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone()));

    /// <summary>One pass's two replies: its finish_step, then a short line.</summary>
    private void RalphPass(string id, string outcome = "done", string summary = "Did it.", string extra = "")
    {
        string verified = outcome == "done" ? ""","verified":["dotnet build: ok"]""" : "";
        _chat.Enqueue(FinishCall(id, "{\"outcome\":\"" + outcome + "\",\"summary\":\"" + summary + "\"" + verified + extra + "}"));
        _chat.EnqueueText("Pass " + id + " over.");
    }

    private string RalphPlanText => File.ReadAllText(PlanFile);

    private string RalphLogFile => Path.Combine(_settings.ProfileDirectory, Files.WorkingDirectory.DefaultFolderName, RalphLog.PathFor(PlanPath));

    private string RalphLockFile => Path.Combine(_settings.ProfileDirectory, Files.WorkingDirectory.DefaultFolderName, RalphLog.LockPathFor(PlanPath));

    [Fact]
    public async Task Ralph_ANamedPlan_Confirmed_RunsEachStepInAFreshConversation_ChecksIt_AndMarksItDone()
    {
        RalphPlan();
        RalphFixture(Line("/plan ralph add-guide"), Yes, Line("/exit"));
        RalphPass("1");
        RalphPass("2");

        string output = await RunAsync();

        Assert.Equal(4, _chat.Requests.Count);
        // The baseline, then each step's check.
        Assert.Equal([RalphVerify, RalphVerify, RalphVerify], _ralphVerifier.Commands);
        // Each pass a fresh conversation: the system message and the pass's message, finish_step offered beside every tool.
        Assert.Equal(_chat.Requests[0].Count, _chat.Requests[2].Count);   // the opening's seeded calls and the pass's message: nothing of pass 1
        Assert.Single(_chat.Requests[2], m => m.Role == ChatRole.User);
        Assert.Contains("One", LastUserText(_chat.Requests[0]));
        Assert.Contains("Two", LastUserText(_chat.Requests[2]));
        Assert.Contains(FinishStepTool.ToolName, OfferedNames(_chat.Options[0]));
        Assert.Contains(WriteFileTool.ToolName, OfferedNames(_chat.Options[0]));
        Assert.Contains(FinishStepTool.ToolName, _chat.Requests[0][0].Text);
        Assert.DoesNotContain("PLAN MODE", _chat.Requests[0][0].Text);
        // The file: both ticked, done; the log beside it, two passes; the lock let go.
        Assert.Contains("- [x] One", RalphPlanText);
        Assert.Contains("- [x] Two", RalphPlanText);
        Assert.Equal(PlanStatus.Done, PlanFileStatus);
        Assert.Equal(2, RalphLog.PassCount(RalphLog.Parse(File.ReadAllText(RalphLogFile))));
        Assert.False(File.Exists(RalphLockFile));
        // The transcript: the started line, each pass's line, each step standing, the summary and the stop.
        Assert.Contains(RalphText.StartedNotice(PlanPath, 2, 6, finalOnly: false), output);
        Assert.Contains(RalphText.TranscriptLine(1, 1, 2, "One"), output);
        Assert.Contains(RalphText.TranscriptLine(2, 2, 2, "Two"), output);
        Assert.Contains(RalphText.StepDoneNotice(1, 2, "One"), output);
        Assert.Contains(RalphText.StepDoneNotice(2, 2, "Two"), output);
        Assert.Contains(RalphText.StopNotice(RalphStop.Done, PlanPath, 2, 2), output);
        Assert.Contains(PlanText.DoneNotice(PlanPath), output);
    }

    private static ChatResponseUpdate VerdictCall(string id, string verdict, string reason) =>
        FakeChatClient.Call(id, RalphReview.ToolName, new Dictionary<string, object?> { [RalphReview.VerdictArgument] = verdict, [RalphReview.ReasonArgument] = reason });

    [Fact]
    public async Task Ralph_TheModelReviewer_AFail_UnticksTheStep_AndTheNextPassGetsTheReason()
    {
        RalphPlan();
        RalphFixture(Line("/plan ralph add-guide"), Yes, Line("/exit"));
        _settings.Update(d => d.RalphLoopReviewer = "model");
        RalphPass("1");
        _chat.Enqueue(VerdictCall("v1", "fail", "one.md has no headings"));
        RalphPass("2");
        _chat.Enqueue(VerdictCall("v2", "pass", "the headings are there"));
        RalphPass("3");
        _chat.Enqueue(VerdictCall("v3", "pass", "done"));

        string output = await RunAsync();

        Assert.Equal(9, _chat.Requests.Count);
        // The review: one request, no conversation, only step_verdict offered, the step and its check in the message.
        Assert.Equal([RalphReview.ToolName], OfferedNames(_chat.Options[2]));
        Assert.Equal(RalphText.ReviewerSystem, _chat.Requests[2][0].Text);
        Assert.Contains("One", LastUserText(_chat.Requests[2]));
        Assert.Contains(RalphText.ReviewFailedNotice("one.md has no headings"), output);
        // The next pass is step one again, told why.
        Assert.Contains("One", LastUserText(_chat.Requests[3]));
        Assert.Contains("one.md has no headings", _chat.Requests[3][0].Text);
        Assert.Equal(PlanStatus.Done, PlanFileStatus);
    }

    [Fact]
    public void RalphReviewers_PickTheAdvisor_OnlyWhenOneIsOffered_AndLogTheVerdict()
    {
        Assert.True(RalphReviewers.ByAdvisor(RalphReviewer.Advisor, advisorOffered: true));
        Assert.False(RalphReviewers.ByAdvisor(RalphReviewer.Advisor, advisorOffered: false));
        Assert.True(RalphReviewers.ByAdvisor(RalphReviewer.Any, advisorOffered: true));
        Assert.False(RalphReviewers.ByAdvisor(RalphReviewer.Any, advisorOffered: false));
        Assert.False(RalphReviewers.ByAdvisor(RalphReviewer.Model, advisorOffered: true));
        Assert.Equal("ralph: review of pass 3 by an advisor: fail — no tests", RalphReviewers.LogLine(3, byAdvisor: true, new RalphVerdict(RalphVerdictKind.Fail, "no tests")));
        Assert.Equal("ralph: review of pass 1 by the model: unclear", RalphReviewers.LogLine(1, byAdvisor: false, RalphVerdict.Unclear()));
    }

    [Fact]
    public void RalphReviewers_TheRequest_CutsTheDiffToAQuarterOfTheWindow()
    {
        var step = PlanSteps.Parse(RalphSteps)[0];
        var delta = new RalphDelta(["one.md"], new string('x', 20_000), false, [], false);

        string small = RalphReviewers.Request(step, null, delta, contextTokens: 4_000);
        string whole = RalphReviewers.Request(step, null, delta, contextTokens: 0);

        Assert.Contains("(cut to fit the reviewer's window)", small);
        Assert.True(small.Length < whole.Length);
        Assert.Contains("(the pass made no report)", whole);
    }

    [Fact]
    public async Task Ralph_ANo_KeepsThePlan_AndSendsNothing()
    {
        RalphPlan();
        RalphFixture(Line("/plan ralph add-guide"), No, Line("/exit"));

        string output = await RunAsync();

        Assert.Empty(_chat.Requests);
        Assert.Empty(_ralphVerifier.Commands);
        Assert.Contains(ChatScreen.KeptNotice, output);
        Assert.Equal(PlanStatus.Incomplete, PlanFileStatus);
    }

    [Fact]
    public async Task Ralph_AFailingBaseline_StartAnyway_LeavesTheStepsUnchecked_AndChecksOnceAtTheEnd()
    {
        RalphPlan();
        RalphFixture(Line("/plan ralph add-guide"), Yes, Yes, Line("/exit"));
        _ralphVerifier.Fail("error CS0103: the name 'x' does not exist");
        RalphPass("1");
        RalphPass("2");

        string output = await RunAsync();

        Assert.Equal([RalphVerify, RalphVerify], _ralphVerifier.Commands);   // the baseline, then once as the final check
        Assert.Contains("error CS0103", output);
        Assert.Contains(RalphText.StartedNotice(PlanPath, 2, 7, finalOnly: true), output);   // three a step and one for the final check's fix
        Assert.Contains(RalphText.StepUncheckedNotice(1, 2, "One"), output);
        Assert.Equal(PlanStatus.Done, PlanFileStatus);
    }

    [Fact]
    public async Task Ralph_AFailingBaseline_No_StartsNothing_AndLetsTheLockGo()
    {
        RalphPlan();
        RalphFixture(Line("/plan ralph add-guide"), Yes, No, Line("/exit"));
        _ralphVerifier.Fail();

        string output = await RunAsync();

        Assert.Empty(_chat.Requests);
        Assert.Contains(RalphText.BaselineRefusedNotice, output);
        Assert.Equal(PlanStatus.Incomplete, PlanFileStatus);
        Assert.False(File.Exists(RalphLockFile));
    }

    [Fact]
    public async Task Ralph_AFailedCheck_UnticksTheStep_AndTheNextPassGetsItsOutput()
    {
        RalphPlan();
        RalphFixture(Line("/plan ralph add-guide"), Yes, Line("/exit"));
        _ralphVerifier.Pass().Fail("error CS1002: ; expected");
        RalphPass("1");
        RalphPass("2");
        RalphPass("3");

        string output = await RunAsync();

        Assert.Equal(6, _chat.Requests.Count);
        Assert.Contains(RalphText.CheckFailedNotice(RalphVerify), output);
        // The second pass is step one again, its directive carrying the check's tail.
        Assert.Contains("One", LastUserText(_chat.Requests[2]));
        Assert.Contains("error CS1002", _chat.Requests[2][0].Text);
        Assert.DoesNotContain("error CS1002", _chat.Requests[4][0].Text);
        Assert.Equal(PlanStatus.Done, PlanFileStatus);
    }

    [Fact]
    public async Task Ralph_EscDuringTheCheck_UnticksAndStops_PlanShowSaysPaused_AndPlanRalphResumes()
    {
        RalphPlan();
        RalphFixture(Line("/plan ralph add-guide"), Yes, Line("/plan show"), Line("/plan ralph"), Line("/exit"));
        _ralphVerifier.HoldRun = 2;   // the first step's check
        _ralphVerifier.Holding = () => Scripted().Push(Keys.Escape);
        RalphPass("1");
        RalphPass("2");
        RalphPass("3");

        string output = await RunAsync();

        Assert.Contains(RalphText.CheckStoppedNotice, output);
        Assert.Contains(RalphText.StopNotice(RalphStop.Stopped, PlanPath, 0, 2), output);
        Assert.Contains(RalphText.ShowLines(PlanPath, 0, 2, 1, paused: true, RalphLog.PathFor(PlanPath))[0], output);
        // Resumed with no confirm: the baseline again, step one again (pass 2), then step two; done.
        Assert.Contains(RalphText.ResumedNotice(PlanPath, 2, 6), output);
        Assert.Contains(RalphText.TranscriptLine(2, 1, 2, "One"), output);
        Assert.Equal(6, _chat.Requests.Count);
        Assert.Equal(PlanStatus.Done, PlanFileStatus);
        Assert.Empty(_tonePlayback.Writes);
    }

    [Fact]
    public async Task Ralph_ThePolicyOffMidLoop_RefusesTheNextCheck_AndStops()
    {
        // 2026-10-19, the review: the police and the policy were judged once, at the start; a pass may rewrite what the command runs.
        RalphPlan();
        RalphFixture(Line("/plan ralph add-guide"), Yes, Line("/exit"));
        _ralphVerifier.Ran = run => { if (run == 1) { _settings.Update(d => d.ShellCommandPolicy = "off"); } };
        RalphPass("1");

        string output = await RunAsync();

        Assert.Equal([RalphVerify], _ralphVerifier.Commands);                  // the baseline alone
        Assert.Contains(RalphText.CheckRefusedError(RalphVerify, RalphText.PolicyOffNowReason), output);
        Assert.Contains(RalphText.StopNotice(RalphStop.Stopped, PlanPath, 0, 2), output);
        Assert.Contains("- [ ] One", RalphPlanText);
        Assert.Equal(2, _chat.Requests.Count);
    }

    [Fact]
    public async Task Ralph_APausedLoop_ResumesOnlyInTheFolderItRanIn()
    {
        // 2026-10-19, the review: its approved commands would have run in whatever folder the working directory had become.
        RalphPlan();
        string elsewhere = Path.Combine(_settings.ProfileDirectory, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        RalphFixture(Line("/plan ralph add-guide"), Yes, input => { _settings.Update(d => d.WorkingDirectory = elsewhere); Line("/plan ralph")(input); }, Line("/exit"));
        RalphPass("1", "blocked", "Need a name.");

        string output = await RunAsync();

        Assert.Contains(RalphText.StopNotice(RalphStop.Blocked, PlanPath, 0, 2), output);
        string refused = RalphText.ResumeElsewhereError(PlanPath, "<root>");
        Assert.Contains(refused[..refused.IndexOf("<root>", StringComparison.Ordinal)], output);   // the error up to the folder it ran in
        Assert.Equal([RalphVerify], _ralphVerifier.Commands);                  // nothing run again
        Assert.Equal(2, _chat.Requests.Count);
    }

    [Fact]
    public async Task Ralph_StepsOfTheSameWords_AreEachTickedOnTheirOwnLine()
    {
        // 2026-10-19, the review: a step was found by its body line in the whole file, and a twin could not be ticked (run again to the cap).
        RalphPlan("# Guide\n\n## Steps\n- [ ] Add tests\n  Done when: a passes\n- [ ] Add tests\n  Done when: b passes\n");
        RalphFixture(Line("/plan ralph add-guide"), Yes, Line("/exit"));
        RalphPass("1");
        RalphPass("2");

        string output = await RunAsync();

        Assert.Equal(4, _chat.Requests.Count);
        Assert.Equal(2, RalphPlanText.Split('\n').Count(l => l.TrimEnd('\r') == "- [x] Add tests"));
        Assert.Contains(RalphText.StopNotice(RalphStop.Done, PlanPath, 2, 2), output);
        Assert.Equal(PlanStatus.Done, PlanFileStatus);
    }

    /// <summary>
    /// The plan file rewritten by <paramref name="edit"/> as the first pass's reply begins (the model's own edit, as a write tool would make it);
    /// <paramref name="then"/> at its second update, the reply held until the turn is cancelled.
    /// </summary>
    private void RalphEditDuringFirstPass(Func<string, string> edit, Action? then = null) =>
        _chat.BeforeUpdateOf = async (request, update, token) =>
        {
            if (request == 0 && update == 0)
            {
                File.WriteAllText(PlanFile, edit(RalphPlanText));
            }

            if (request == 0 && update == 1 && then is not null)
            {
                then();
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(5, CancellationToken.None);
                }
            }
        };

    [Fact]
    public async Task Ralph_AStoppedPass_PutsBackTheTickItsModelWrote()
    {
        // 2026-10-19, the second review: the stopped pass's own box was skipped by the put-back, so a resume passed the step by, never checked.
        RalphPlan();
        RalphFixture(Line("/plan ralph add-guide"), Yes, Line("/exit"));
        _chat.EnqueueText("Working on it. ", "Still going.");
        RalphEditDuringFirstPass(text => text.Replace("- [ ] One", "- [x] One", StringComparison.Ordinal), () => Scripted().Push(Keys.Escape));

        string output = await RunAsync();

        Assert.Contains(RalphText.StopNotice(RalphStop.Stopped, PlanPath, 0, 2), output);
        Assert.Contains("- [ ] One", RalphPlanText);
        Assert.Equal([RalphVerify], _ralphVerifier.Commands);   // the baseline alone
    }

    [Fact]
    public async Task Ralph_AnotherLineThePassReworded_HasItsBoxPutBack()
    {
        // 2026-10-19, the second review: boxes were matched by their words, so a reworded line kept the tick the model gave it.
        RalphPlan();
        RalphFixture(Line("/plan ralph add-guide"), Yes, Line("/exit"));
        RalphEditDuringFirstPass(text => text.Replace("- [ ] Two", "- [x] Two, early", StringComparison.Ordinal));
        RalphPass("1");
        RalphPass("2");

        string output = await RunAsync();

        Assert.Contains(RalphText.BoxesPutBackNotice(1), output);
        Assert.Contains("Two, early", LastUserText(_chat.Requests[2]));   // worked in a pass of its own
        Assert.Equal(4, _chat.Requests.Count);
        Assert.Equal(PlanStatus.Done, PlanFileStatus);
    }

    [Fact]
    public async Task Ralph_ItsOwnStepReworded_IsUnticked_WhenItsCheckFails()
    {
        // 2026-10-19, the second review: the step was looked for by its words, so a reworded one stayed ticked after its check failed.
        RalphPlan();
        RalphFixture(Line("/plan ralph add-guide"), Yes, Line("/exit"));
        _ralphVerifier.Pass().Fail("error CS1002: ; expected");
        RalphEditDuringFirstPass(text => text.Replace("- [ ] One", "- [x] One, all of it", StringComparison.Ordinal));
        RalphPass("1");
        RalphPass("2");
        RalphPass("3");

        string output = await RunAsync();

        Assert.Contains(RalphText.CheckFailedNotice(RalphVerify), output);
        Assert.Contains("One, all of it", LastUserText(_chat.Requests[2]));   // worked again, not passed by
        Assert.Equal(6, _chat.Requests.Count);
        Assert.Equal(PlanStatus.Done, PlanFileStatus);
    }

    [Fact]
    public async Task Ralph_TheWorkingDirectoryMovedMidPass_TheLoopEndsInItsOwnFolder()
    {
        // 2026-10-19, the second review: the judge, the stop's writes and the unlock followed the setting to the new folder.
        RalphPlan();
        string elsewhere = Path.Combine(_settings.ProfileDirectory, "elsewhere");
        string otherPlan = Path.Combine(elsewhere, PlanPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(otherPlan)!);
        File.WriteAllText(otherPlan, RalphPlanText);   // a plan of the same name there
        string otherText = File.ReadAllText(otherPlan);
        RalphFixture(Line("/plan ralph add-guide"), Yes, Line("/exit"));
        RalphEditDuringFirstPass(text => { _settings.Update(d => d.WorkingDirectory = elsewhere); return text; });
        RalphPass("1");

        string output = await RunAsync();

        Assert.Contains(RalphText.StopNotice(RalphStop.WorkdirMoved, PlanPath, 1, 2), output);
        Assert.Equal(otherText, File.ReadAllText(otherPlan));
        Assert.Contains("- [x] One", RalphPlanText);                  // judged where it ran
        Assert.False(File.Exists(RalphLockFile));
        Assert.Equal(ChatScreen.RalphStopWord(RalphStop.WorkdirMoved), RalphLog.Parse(File.ReadAllText(RalphLogFile)).Head!.Stopped);
        Assert.False(Directory.Exists(Path.Combine(elsewhere, ".neon", "plans", "add-guide")));   // no log or lock there
    }

    [Fact]
    public async Task Ralph_ACheckThatThrows_EndsTheLoop_AndTheNextTurnMayAsk()
    {
        // 2026-10-19, the second review: a throw skipped the loop's end, leaving its pass under way (the asking tools withheld), the lock held.
        RalphPlan();
        RalphFixture(Line("/plan ralph add-guide"), Yes, Line("hello"), Line("/exit"));
        _ralphVerifier.Ran = run => { if (run == 2) { throw new InvalidOperationException("the disk went away"); } };
        RalphPass("1");
        _chat.EnqueueText("Hi.");

        string output = await RunAsync();

        Assert.Contains(RalphText.LoopFailedError("InvalidOperationException: the disk went away"), output);
        Assert.Contains(RalphText.StopNotice(RalphStop.Stopped, PlanPath, 1, 2), output);
        Assert.False(File.Exists(RalphLockFile));
        Assert.Contains(AskUserTool.ToolName, OfferedNames(_chat.Options[2]));
    }

    [Fact]
    public async Task Ralph_AReviewThatTimesOut_IsUnclear_AndTheLoopGoesOn()
    {
        // 2026-10-19, the second review: an HTTP client's timeout is a TaskCanceledException, which the review let out as a stop of its own.
        RalphPlan();
        RalphFixture(Line("/plan ralph add-guide"), Yes, Line("/exit"));
        _settings.Update(d => d.RalphLoopReviewer = "model");
        RalphPass("1");
        _chat.EnqueueText("never seen");
        RalphPass("2");
        _chat.Enqueue(VerdictCall("v2", "pass", "fine"));
        _chat.BeforeUpdateOf = (request, _, _) =>
            request == 2 ? throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.") : Task.CompletedTask;

        string output = await RunAsync();

        Assert.Contains(RalphText.ReviewUnclearNotice, output);
        Assert.Contains(RalphText.StepDoneNotice(1, 2, "One"), output);
        Assert.Equal(PlanStatus.Done, PlanFileStatus);
    }

    [Fact]
    public async Task Ralph_StartedWithThePolicyOff_ResumedWithItOn_RunsThePlansChecks()
    {
        // 2026-10-19, the second review: the start saved the checks as none, so the resume ran none either.
        RalphPlan();
        string? kept = null;
        RalphFixture(Line("/plan ralph add-guide"), Yes,
            input => { kept = RalphLog.Parse(File.ReadAllText(RalphLogFile)).Head!.Verify; _settings.Update(d => d.ShellCommandPolicy = "yolo"); Line("/plan ralph")(input); },
            Line("/exit"));
        _settings.Update(d => d.ShellCommandPolicy = "off");
        RalphPass("1", "blocked", "Need a name.");
        RalphPass("2");
        RalphPass("3");

        string output = await RunAsync();

        Assert.Contains(RalphText.PolicyOffUncheckedNotice, output);
        Assert.Equal(RalphVerify, kept);                                         // the log kept the approved command
        Assert.Equal([RalphVerify, RalphVerify, RalphVerify], _ralphVerifier.Commands);   // none at the start; the resume's baseline and each step's
        Assert.Equal(PlanStatus.Done, PlanFileStatus);
    }

    [Fact]
    public async Task Ralph_FinalOnly_PausedBeforeTheFinalCheck_ResumesIntoIt()
    {
        // 2026-10-19, the review: the resume read Final alone, and under final-only (start anyway) the verify command is the final check.
        RalphPlan("# Guide\n\n## Steps\n- [ ] One\n  Done when: one.md exists\n");
        RalphFixture(Line("/plan ralph add-guide"), Yes, Yes, Line("/plan ralph"), Line("/exit"));
        _ralphVerifier.Fail("error CS0103: the name 'x' does not exist");
        _chat.BeforeUpdateOf = async (request, update, ct) =>
        {
            if (request == 0 && update == 0)
            {
                PushLine("how is it going?");
                await UntilAsync(() => _running!.QueuedCount == 1, ct);
            }
        };
        RalphPass("1");
        _chat.EnqueueText("Step one stands.");

        string output = await RunAsync();

        Assert.Contains(RalphText.StopNotice(RalphStop.Paused, PlanPath, 1, 1), output);
        Assert.Equal([RalphVerify, RalphVerify], _ralphVerifier.Commands);    // the baseline, then the final check on the resume
        Assert.Equal(PlanStatus.Done, PlanFileStatus);
    }

    [Fact]
    public async Task Ralph_ABlockedStep_StopsTheLoop_UntickedWithTheReason_AndRingsTheStopTone()
    {
        RalphPlan();
        RalphFixture(Line("/plan ralph add-guide"), Yes, Line("/exit"));
        _settings.Update(d => d.RalphLoopStopTone = "chime");
        RalphPass("1", "blocked", "The guide needs a product name only you know.");

        string output = await RunAsync();

        Assert.Equal(2, _chat.Requests.Count);
        Assert.Contains(RalphText.BlockedNotice("The guide needs a product name only you know."), output);
        Assert.Contains(RalphText.StopNotice(RalphStop.Blocked, PlanPath, 0, 2), output);
        Assert.Contains("- [ ] One", RalphPlanText);
        Assert.Equal(PlanStatus.Incomplete, PlanFileStatus);
        await UntilAsync(() => _tonePlayback.Writes.Count > 0, CancellationToken.None);
    }

    [Fact]
    public async Task Ralph_ASplit_WritesTheSubsteps_RunsEach_ThenTicksTheParent()
    {
        RalphPlan();
        RalphFixture(Line("/plan ralph add-guide"), Yes, Line("/exit"));
        RalphPass("1", "split", "Too big for one pass.", ""","substeps":[{"text":"Outline","done_when":"headings exist"},{"text":"Body","done_when":"each heading has text"}]""");
        RalphPass("2");
        RalphPass("3");
        RalphPass("4");

        string output = await RunAsync();

        Assert.Equal(8, _chat.Requests.Count);
        Assert.Contains(RalphText.SplitNotice(2, "One"), output);
        Assert.Contains("Outline", LastUserText(_chat.Requests[2]));
        Assert.Contains("Body", LastUserText(_chat.Requests[4]));
        Assert.Contains(RalphText.ParentTickedNotice("One"), output);
        Assert.Contains("- [x] Outline", RalphPlanText);
        Assert.Contains("- [x] Body", RalphPlanText);
        Assert.Contains("- [x] One", RalphPlanText);
        Assert.Equal(PlanStatus.Done, PlanFileStatus);
    }

    [Fact]
    public async Task Ralph_TwoPassesWithNoProgress_Stall()
    {
        RalphPlan();
        RalphFixture(Line("/plan ralph add-guide"), Yes, Line("/exit"));
        RalphPass("1", "partial", "Looked around.");
        RalphPass("2", "partial", "Looked again.");

        string output = await RunAsync();

        Assert.Equal(4, _chat.Requests.Count);
        Assert.Contains(RalphText.StopNotice(RalphStop.Stalled, PlanPath, 0, 2), output);
        Assert.Contains(RalphText.Glyph + " Ralph loop, 2 passes", output);
        Assert.Contains("  pass 2: step 1, partial", output);
    }

    [Fact]
    public async Task Ralph_AMessageTypedDuringAPass_PausesTheLoopAfterIt_ThenRuns()
    {
        RalphPlan();
        RalphFixture(Line("/plan ralph add-guide"), Yes, Line("/exit"));
        _chat.BeforeUpdateOf = async (request, update, ct) =>
        {
            if (request == 0 && update == 0)
            {
                PushLine("how is it going?");
                await UntilAsync(() => _running!.QueuedCount == 1, ct);
            }
        };
        RalphPass("1");
        _chat.EnqueueText("Step one stands.");

        string output = await RunAsync();

        Assert.Equal(3, _chat.Requests.Count);
        Assert.Contains(RalphText.StopNotice(RalphStop.Paused, PlanPath, 1, 2), output);
        Assert.Equal("how is it going?", LastUserText(_chat.Requests[2]));
        Assert.DoesNotContain(FinishStepTool.ToolName, OfferedNames(_chat.Options[2]));
        Assert.Contains("- [x] One", RalphPlanText);
    }

    [Fact]
    public async Task Ralph_WithoutACheckpoint_ThePassIsOfferedNoAskingTool_WithOne_ItIs()
    {
        RalphPlan();
        RalphFixture(Line("/plan ralph add-guide"), Yes, Line("/plan ralph add-guide"), Line("/exit"));
        RalphPass("1", "blocked", "Stop here.");
        RalphPass("2", "blocked", "Stop here.");

        // The second run (the paused loop resumed, no confirm) with a checkpoint every step: the first stopped on blocked.
        _chat.BeforeUpdateOf = (request, _, _) =>
        {
            if (request == 1)
            {
                _settings.Update(d => d.RalphLoopCheckpoint = 1);
            }

            return Task.CompletedTask;
        };

        await RunAsync();

        Assert.DoesNotContain(AskUserTool.ToolName, OfferedNames(_chat.Options[0]));
        Assert.Contains(AskUserTool.ToolName, OfferedNames(_chat.Options[2]));
    }

    [Fact]
    public async Task Ralph_TheCheckpoint_Redo_UnticksTheStep_AndTheNextPassGetsTheNote()
    {
        RalphPlan();
        _settings.Update(d => d.RalphLoopCheckpoint = 1);
        RalphFixture(
            Line("/plan ralph add-guide"), Yes,
            Answer(Keys.Char('r'), Keys.Enter), Line("use short headings"),   // the first checkpoint: redo, with a note
            Answer(Keys.Char('k'), Keys.Enter),                               // the step again: keep
            Answer(Keys.Char('k'), Keys.Enter),                               // step two: keep
            Line("/exit"));
        RalphPass("1");
        RalphPass("2");
        RalphPass("3");

        string output = await RunAsync();

        Assert.Contains(RalphText.CheckpointTitle, output);
        Assert.Equal(6, _chat.Requests.Count);
        Assert.Contains("One", LastUserText(_chat.Requests[2]));
        Assert.Contains("use short headings", _chat.Requests[2][0].Text);
        Assert.Equal(PlanStatus.Done, PlanFileStatus);
    }

    [Fact]
    public async Task Ralph_ALockHeldByAnotherLiveApp_Refuses()
    {
        RalphPlan();
        Directory.CreateDirectory(Path.GetDirectoryName(RalphLockFile)!);
        File.WriteAllText(RalphLockFile, "pid: 999999\nstarted: 2026-10-18T09:00:00.0000000+00:00\n");
        RalphFixture(Line("/plan ralph add-guide"), Yes, Line("/exit"));
        var idle = Scripted().OnWait!;
        bool seamSet = false;
        Scripted().OnWait = () =>
        {
            if (!seamSet && _running is not null)
            {
                seamSet = true;
                _running.RalphLockAlive = _ => true;
            }

            idle();
        };

        string output = await RunAsync();

        Assert.Empty(_chat.Requests);
        Assert.Contains(RalphText.LockedError(PlanPath), output);
        Assert.Contains("pid: 999999", File.ReadAllText(RalphLockFile));
    }

    [Fact]
    public async Task Ralph_AStaleLock_IsTakenOver()
    {
        RalphPlan();
        Directory.CreateDirectory(Path.GetDirectoryName(RalphLockFile)!);
        File.WriteAllText(RalphLockFile, "pid: 999999\nstarted: 2026-10-18T09:00:00.0000000+00:00\n");
        RalphFixture(Line("/plan ralph add-guide"), Yes, Line("/exit"));
        RalphPass("1");
        RalphPass("2");

        await RunAsync();

        Assert.Equal(PlanStatus.Done, PlanFileStatus);
        Assert.False(File.Exists(RalphLockFile));
    }

    [Fact]
    public async Task Ralph_APlanWithNoSteps_IsRefused()
    {
        RalphPlan("# Guide\n\nJust prose.\n");
        RalphFixture(Line("/plan ralph add-guide"), Yes, Line("/exit"));

        string output = await RunAsync();

        Assert.Empty(_chat.Requests);
        Assert.Contains(RalphText.NoStepsError, output);
    }

    [Fact]
    public async Task Ralph_ADonePlan_IsRefused_AndANameThatIsNoPlan_ErrorsThenOpensThePicker()
    {
        RalphPlan(status: PlanStatus.Done);
        RalphFixture(Line("/plan ralph add-guide"), Line("/plan ralph nosuch"), Key(Keys.Escape), Line("/exit"));

        string output = await RunAsync();

        Assert.Contains(RalphText.NotRunnableError(PlanPath, PlanStatus.Done), output);
        Assert.Contains(PlanText.NoSuchPlanError("nosuch"), output);
        Assert.Contains(PlanText.PickerTitle, output);
        Assert.Empty(_chat.Requests);
    }

    [Fact]
    public async Task Ralph_FromTheApprovalPane_TheLRow_LeavesPlanMode_AndRunsTheLoop()
    {
        _settings.Update(d => { d.RalphLoopReviewer = "off"; d.RalphLoopStopTone = "silent"; });
        PlanFixture([[Keys.Char('l'), Keys.Enter]], "/plan Add a guide", "/exit");
        _chat.Enqueue(FakeChatClient.Call("p1", PresentPlanTool.ToolName, new Dictionary<string, object?>
        {
            [PresentPlanTool.TitleArgument] = "Guide",
            [PresentPlanTool.NameArgument] = "add-guide",
            [PresentPlanTool.MarkdownArgument] = RalphSteps,
            [PresentPlanTool.VerifyArgument] = RalphVerify,
        }));
        _chat.EnqueueText("Approved.");
        RalphPass("1");
        RalphPass("2");

        string output = await RunAsync();

        Assert.Equal(6, _chat.Requests.Count);
        Assert.Contains(RalphText.ApproveRalphRow, output);
        Assert.Contains("Ralph loop: ", output);   // the caption's check line
        Assert.Equal(_chat.Requests[2].Count, _chat.Requests[4].Count);   // both passes fresh: nothing of the planning
        Assert.Single(_chat.Requests[2], m => m.Role == ChatRole.User);
        Assert.DoesNotContain("PLAN MODE", _chat.Requests[2][0].Text);
        Assert.Contains(FinishStepTool.ToolName, OfferedNames(_chat.Options[2]));
        Assert.Equal([RalphVerify, RalphVerify, RalphVerify], _ralphVerifier.Commands);
        Assert.Equal(PlanStatus.Done, PlanFileStatus);
    }

    [Fact]
    public async Task Ralph_ThePickersLButton_StartsTheLoop_AndItsFooterShowsTheLog()
    {
        RalphPlan();
        _console.Profile.Width = 160;
        RalphFixture(Line("/plan open"), Answer(Keys.Char(PlanText.RalphKey)), Yes, Line("/exit"));
        RalphPass("1", "blocked", "Need a name.");

        string output = await RunAsync();

        Assert.Contains(PlanText.PickerKeys(PlanPickMode.Open, ralphButton: true), output);
        Assert.Contains(RalphText.StopNotice(RalphStop.Blocked, PlanPath, 0, 2), output);
        Assert.Equal(2, _chat.Requests.Count);
    }

    [Fact]
    public async Task Ralph_BareWithNothingPaused_SaysSo_AndOpensThePickerInRalphMode()
    {
        RalphPlan();
        RalphFixture(Line("/plan ralph"), Key(Keys.Escape), Line("/exit"));

        string output = await RunAsync();

        Assert.Contains(RalphText.NothingPausedNotice, output);
        Assert.Contains(PlanText.PickerKeys(PlanPickMode.Ralph, ralphButton: true), output);
        Assert.Empty(_chat.Requests);
    }

    [Fact]
    public async Task Ralph_TheSessionRow_LogsThePassesShortLine_NotItsMessage()
    {
        RalphPlan();
        RalphFixture(Line("/plan ralph add-guide"), Yes, Line("/exit"));
        RalphPass("1", "blocked", "Need a name.");

        await RunAsync();

        using var store = new Sessions.SessionStore(_settings.ProfileDirectory, _time);
        var summary = Assert.Single(store.List(10));
        Assert.Equal(RalphText.SessionTitle("Guide", 1, 2, 1), summary.Title);
        Assert.Equal(RalphText.TranscriptLine(1, 1, 2, "One"), Assert.Single(store.Load(summary.Id)!.Turns).UserText);
    }
}
