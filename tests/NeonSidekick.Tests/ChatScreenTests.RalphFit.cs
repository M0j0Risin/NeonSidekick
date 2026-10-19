using NeonSidekick.Llm.Tools;
using NeonSidekick.Plans;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// A plan that may not suit a Ralph loop (later on 2026-10-18, the plan at ~/.claude/plans/snuggly-finding-comet.md, <see cref="PlanFit"/>): the
/// approval pane's caption names the problems, the loop's start asks (a no leaves the plan presented, <c>/plan approve --ralph</c> asks again),
/// <c>/plan ralph &lt;name&gt;</c> asks once, and a well-shaped plan, a short one-step plan, an unchecked plan with a reviewer and a resume ask nothing new.
/// </summary>
public partial class ChatScreenTests
{
    /// <summary>One step in a plan past <see cref="PlanFit.OneStepChars"/>, with its Done when line: the one-step problem alone.</summary>
    private static readonly string BigOneStep =
        "# Guide\n\n## Context\n\n" + new string('c', PlanFit.OneStepChars) + "\n\n## Steps\n- [ ] Everything\n  Done when: guide.md exists\n";

    private const string FitLead = "may not suit a Ralph loop";

    /// <summary>The model's present_plan of <paramref name="markdown"/>, then its line after the approval.</summary>
    private void PresentRalphPlan(string markdown, string? verify)
    {
        var args = new Dictionary<string, object?>
        {
            [PresentPlanTool.TitleArgument] = "Guide",
            [PresentPlanTool.NameArgument] = "add-guide",
            [PresentPlanTool.MarkdownArgument] = markdown,
        };
        if (verify is not null)
        {
            args[PresentPlanTool.VerifyArgument] = verify;
        }

        _chat.Enqueue(FakeChatClient.Call("p1", PresentPlanTool.ToolName, args));
        _chat.EnqueueText("Approved.");
    }

    [Fact]
    public async Task RalphFit_FromThePane_TheCaptionNamesTheProblems_TheStartAsks_AndNoLeavesThePlanPresented()
    {
        _settings.Update(d => { d.RalphLoopReviewer = "off"; d.RalphLoopStopTone = "silent"; });
        _console.Profile.Width = 400;
        PlanFixture([[Keys.Char('l'), Keys.Enter]], "/plan Add a guide", "n", "/plan show", "/exit");
        PresentRalphPlan(BigOneStep, verify: null);

        string output = await RunAsync();

        // The caption: the problems last, the no-check item left to "unchecked · reviewer off".
        Assert.Contains(PlanText.RalphCaptionLead + "unchecked · reviewer off · " + RalphText.PolicyAsksWarning + " · " + RalphText.FitOneStep, output);
        Assert.DoesNotContain(RalphText.FitOneStep + " · " + RalphText.FitNoCheck, output);
        // The question: every problem, the no-check one too.
        Assert.Contains(RalphText.FitQuestion([RalphText.FitOneStep, RalphText.FitNoCheck]), output);
        Assert.Contains(RalphText.FitDeclinedNotice, output);
        // Nothing started: no pass, no lock, the plan still presented and plan mode on.
        Assert.Equal(2, _chat.Requests.Count);
        Assert.Empty(_ralphVerifier.Commands);
        Assert.False(File.Exists(RalphLockFile));
        Assert.Equal(PlanStatus.Draft, PlanFileStatus);
        Assert.Contains($"  Plan: {PlanPath} (revision 1)", output);
    }

    [Fact]
    public async Task RalphFit_AfterANo_PlanApproveRalph_ConfirmsItsChecks_WithTheProblems_AndYesStartsTheLoop()
    {
        _settings.Update(d => { d.RalphLoopReviewer = "off"; d.RalphLoopStopTone = "silent"; });
        _console.Profile.Width = 400;
        PlanFixture([[Keys.Char('l'), Keys.Enter]], "/plan Add a guide", "n", "/plan approve --ralph", "y", "/exit");
        PresentRalphPlan(BigOneStep, RalphVerify);
        RalphPass("1");

        string output = await RunAsync();

        // The pane's row asks the fit question; the typed form (2026-10-19, the third review) confirms the commands it is about to run, the
        // problems in the same question, and never asks the fit question a second time.
        string question = RalphText.FitQuestion([RalphText.FitOneStep]);
        Assert.Equal(1, output.Split(question).Length - 1);
        Assert.Contains($"Run {PlanPath} as a Ralph loop? verify `{RalphVerify}`", output);
        Assert.Contains(RalphText.FitSentence([RalphText.FitOneStep]), output);
        Assert.Contains(RalphText.FitDeclinedNotice, output);
        Assert.Contains(RalphText.StartedNotice(PlanPath, 1, 3, finalOnly: false), output);
        Assert.Equal([RalphVerify, RalphVerify], _ralphVerifier.Commands);   // the baseline, then the step's check
        Assert.Equal(PlanStatus.Done, PlanFileStatus);
    }

    [Fact]
    public async Task RalphFit_PlanRalphByName_AsksOnce_TheProblemsInItsConfirm()
    {
        RalphPlan(BigOneStep);
        _console.Profile.Width = 400;
        RalphFixture(Line("/plan ralph add-guide"), Yes, Line("/exit"));
        RalphPass("1");

        string output = await RunAsync();

        Assert.Contains(RalphText.FitSentence([RalphText.FitOneStep]), output);
        Assert.DoesNotContain(RalphText.FitQuestion([RalphText.FitOneStep]), output);   // never asked a second time
        Assert.Contains(RalphText.StopNotice(RalphStop.Done, PlanPath, 1, 1), output);
        Assert.Equal(PlanStatus.Done, PlanFileStatus);
    }

    [Fact]
    public async Task RalphFit_AShortOneStepPlan_UncheckedWithAReviewer_AsksNothingNew()
    {
        _settings.Update(d => { d.RalphLoopReviewer = "model"; d.RalphLoopStopTone = "silent"; });
        _console.Profile.Width = 400;
        // Were the fit question asked, /exit would answer it and nothing would run.
        PlanFixture([[Keys.Char('l'), Keys.Enter]], "/plan Add a guide", "/exit");
        PresentRalphPlan("# Guide\n\n## Steps\n- [ ] Rename the variable\n  Done when: no file names the old one\n", verify: null);
        RalphPass("1");
        _chat.Enqueue(VerdictCall("v1", "pass", "renamed"));

        string output = await RunAsync();

        Assert.DoesNotContain(FitLead, output);
        Assert.Contains(PlanText.RalphCaptionLead + "unchecked · reviewer model · " + RalphText.PolicyAsksWarning, output);
        Assert.DoesNotContain(RalphText.PolicyAsksWarning + " · ", output);   // no problem after it
        Assert.DoesNotContain(RalphText.FitOneStep, output);
        Assert.Equal(PlanStatus.Done, PlanFileStatus);
    }

    [Fact]
    public async Task RalphFit_AResume_NeverAsks()
    {
        // Two steps with no Done when line: named in the one confirm; the resume after the Esc asks nothing.
        RalphPlan("# Guide\n\n## Steps\n- [ ] One\n- [ ] Two\n");
        _console.Profile.Width = 400;
        RalphFixture(Line("/plan ralph add-guide"), Yes, Line("/plan ralph"), Line("/exit"));
        _ralphVerifier.HoldRun = 2;   // the first step's check
        _ralphVerifier.Holding = () => Scripted().Push(Keys.Escape);
        RalphPass("1");
        RalphPass("2");
        RalphPass("3");

        string output = await RunAsync();

        Assert.Contains(RalphText.FitSentence([RalphText.FitNoDoneWhen(2)]), output);
        Assert.Contains(RalphText.ResumedNotice(PlanPath, 2, 6), output);
        Assert.DoesNotContain("This plan " + FitLead, output);
        Assert.Equal(PlanStatus.Done, PlanFileStatus);
    }
}
