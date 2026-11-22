using Microsoft.Extensions.AI;
using NeonSidekick.Plans;
using NeonSidekick.Settings;

namespace NeonSidekick.Tests;

/// <summary>
/// The gap review's clean passes and <c>/plan chat</c> headless (2026-10-17): the limit before the confirmation names <c>/plan approve</c>,
/// a short limit's notice, <c>confirm</c> under <i>Advisor confirm</i>, <c>/plan chat</c> and <c>/plan ready</c>, and the heart's space on
/// Terminal.app's main screen.
/// </summary>
public partial class SidekickAppTests
{
    [Fact]
    public async Task Headless_PlanGaps_TheLimitBeforeTheConfirmation_NamesPlanApprove()
    {
        _settings.Update(d => d.PlanGapCleanPasses = 2);   // the confirming pass is this test's subject (1 is the default since 2026-11-19)
        ServerOn1234("llama");
        _settings.Update(d => d.PlanGapPasses = 2);
        _chat.Enqueue(HeadlessPresentPlan());
        _chat.EnqueueText("Saved.");
        _chat.Enqueue(HeadlessPresentGaps("p2", "Test it"));
        _chat.EnqueueText("Pass 1 done.");
        _chat.Enqueue(HeadlessPresentGapsOver("p3", "# Guide\n\n- [ ] Write it\n  Done when: guide.md exists\n- [ ] Test it\n  Done when: Test it is done\n"));   // unchanged and clean: but no pass left
        _chat.EnqueueText("Clean.");

        string output = await Headless("/plan Add a guide\n/plan gaps\n");

        Assert.Equal(6, _chat.Requests.Count);
        Assert.Contains("[notice] " + PlanText.GapStopNotice(PlanGapStop.Unconfirmed, 2, 1) + " — /plan approve [--fresh | --ralph] starts it", output);
        Assert.Contains("pass 2: clean, cut short", output);
        Assert.EndsWith("cut short at the limit (body " + PlanDocument.Fingerprint(PlanDocument.Body(File.ReadAllText(HeadlessPlanFile))) + ")", PlanDocument.TryParse(File.ReadAllText(HeadlessPlanFile))!.Review);
    }

    /// <summary>Confirm focus by change headless (2026-11-18, Phase 3 item 3): a <c>Done when:</c>-only revision gives <i>verification</i>; off, <i>order</i>.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Headless_PlanGaps_ConfirmFocusByChange_ADoneWhenOnlyRevision_GivesVerification_ElseOrder(bool byChange)
    {
        _settings.Update(d => d.PlanGapCleanPasses = 2);   // the confirming pass is this test's subject (1 is the default since 2026-11-19)
        const string pinned = "# Guide\n\n- [ ] Write it\n  Done when: `test -f guide.md` exits 0\n";
        ServerOn1234("llama");
        _settings.Update(d => d.ConfirmFocusByChange = byChange);
        _chat.Enqueue(HeadlessPresentPlan());
        _chat.EnqueueText("Saved.");
        _chat.Enqueue(HeadlessPresentGapsOver("p2", pinned, "[medium] The check isn't a command"));
        _chat.EnqueueText("Found one.");
        _chat.Enqueue(HeadlessPresentGapsOver("p3", pinned));
        _chat.EnqueueText("Clean.");
        _chat.Enqueue(HeadlessPresentGapsOver("p4", pinned));
        _chat.EnqueueText("Confirmed.");
        var focus = new List<string>();
        Action<NeonSidekick.Diagnostics.DiagnosticEvent> capture = e => { if (e.Message.StartsWith(PlanText.FocusLogPrefix, StringComparison.Ordinal)) focus.Add(e.Message); };
        NeonSidekick.Diagnostics.DiagnosticLog.Emitted += capture;
        string output;
        try
        {
            output = await Headless("/plan Add a guide\n/plan gaps\n");
        }
        finally
        {
            NeonSidekick.Diagnostics.DiagnosticLog.Emitted -= capture;
        }

        Assert.Equal(8, _chat.Requests.Count);
        int area = byChange ? PlanFocusAreas.Verification : PlanFocusAreas.Order;
        Assert.Contains(PlanText.ConfirmFocuses[area], _chat.Requests[6][0].Text);
        Assert.Equal([byChange ? "plan gaps focus: pass 3: verification, by change (verification 1)" : "plan gaps focus: pass 3: order, in turn"], focus);
        Assert.Contains($"pass 3: clean ({PlanText.ConfirmFocusLabels[area]}), confirmed", output);
    }

    [Fact]
    public async Task Headless_PlanGaps_ALimitBelowTheCleanCount_SaysSo_AndOneCleanPassEndsIt()
    {
        _settings.Update(d => d.PlanGapCleanPasses = 2);   // the confirming pass is this test's subject (1 is the default since 2026-11-19)
        ServerOn1234("llama");
        _chat.Enqueue(HeadlessPresentPlan());
        _chat.EnqueueText("Saved.");
        _chat.Enqueue(HeadlessPresentGaps("p2", []));
        _chat.EnqueueText("Nothing left.");

        string output = await Headless("/plan Add a guide\n/plan gaps model 1\n");

        Assert.Equal(4, _chat.Requests.Count);
        Assert.Contains("[notice] " + PlanText.CleanLoweredNotice(1, 2), output);
        Assert.Contains("[notice] " + PlanText.ConvergedNotice(HeadlessPlanPath, 1, 0) + " — /plan approve [--fresh | --ralph] starts it", output);
        Assert.Contains("[notice] · checked: the steps", output);
    }

    [Fact]
    public async Task Headless_PlanGaps_Confirm_UnderAdvisorConfirm_IsTheModel_WithConfirmsCount()
    {
        ServerOn1234("llama");
        _settings.Update(d =>
        {
            d.PlanGapCleanPasses = 1;
            d.Advisors = true;
            d.AdvisorConfirm = true;
            d.AdvisorList = [new AdvisorEntry("dba", "database architect")];
        });
        _chat.Enqueue(HeadlessPresentPlan());
        _chat.EnqueueText("Saved.");
        _chat.Enqueue(HeadlessPresentGaps("p2", []));
        _chat.EnqueueText("Clean.");
        _chat.Enqueue(HeadlessPresentGaps("p3", []));
        _chat.EnqueueText("Clean again.");

        string output = await Headless("/plan Add a guide\n/plan gaps confirm\n");

        Assert.Contains("[notice] " + PlanText.GapsConfirmFallbackNotice, output);
        Assert.Equal(PlanText.GapPassMessage(HeadlessPlanPath, 1, 5, PlanReviewer.Model), HeadlessLastUser(_chat.Requests[2]));
        Assert.Equal(PlanText.CleanPassResult(HeadlessPlanPath, 1, 1, 2, bodyChars: PlanGapLoopTests.BodyChars("# Guide\n\n- [ ] Write it\n  Done when: guide.md exists\n")), _chat.Requests[3].SelectMany(m => m.Contents.OfType<FunctionResultContent>()).Single(r => r.CallId == "p2").Result);
        Assert.Equal(PlanText.GapPassMessage(HeadlessPlanPath, 2, 5, PlanReviewer.Model, cleanSoFar: 1), HeadlessLastUser(_chat.Requests[4]));
        Assert.Contains("[notice] " + PlanText.ConvergedNotice(HeadlessPlanPath, 2, 0, 2), output);
    }

    [Fact]
    public async Task Headless_PlanGaps_AskUserOff_ThePassIsToldNotToAsk()
    {
        ServerOn1234("llama");
        _settings.Update(d => { d.PlanGapCleanPasses = 1; d.PlanGapAskUser = false; });
        _chat.Enqueue(HeadlessPresentPlan());
        _chat.EnqueueText("Saved.");
        _chat.Enqueue(HeadlessPresentGaps("p2", []));
        _chat.EnqueueText("Clean.");

        await Headless("/plan Add a guide\n/plan gaps\n");

        Assert.Equal(4, _chat.Requests.Count);
        Assert.DoesNotContain(PlanText.GapNoAskLine, _chat.Requests[0][0].Text);   // the planning turn, no review yet
        Assert.Contains(PlanText.GapNoAskLine, _chat.Requests[2][0].Text);
        Assert.Contains(PlanText.GapOpenQuestionSentence, _chat.Requests[2][0].Text);
    }

    /// <summary>2026-10-21: headless never offers <c>ask_user</c>, so a pass is told not to ask with the setting on too.</summary>
    [Fact]
    public async Task Headless_PlanGaps_AskUserOn_StillCannotAsk()
    {
        ServerOn1234("llama");
        _settings.Update(d => { d.PlanGapCleanPasses = 1; d.PlanGapAskUser = true; });
        _chat.Enqueue(HeadlessPresentPlan());
        _chat.EnqueueText("Saved.");
        _chat.Enqueue(HeadlessPresentGaps("p2", []));
        _chat.EnqueueText("Clean.");

        await Headless("/plan Add a guide\n/plan gaps\n");

        Assert.Equal(4, _chat.Requests.Count);
        Assert.Contains(PlanText.GapNoAskLine, _chat.Requests[2][0].Text);
        Assert.Contains(PlanText.GapOpenQuestionSentence, _chat.Requests[2][0].Text);
        Assert.DoesNotContain(PlanText.GapAskSentence, _chat.Requests[2][0].Text);
    }

    [Fact]
    public async Task Headless_PlanChat_TheLineOpensIt_ReadyAsks_AndAfterAPresentationSaysWhereItStands()
    {
        ServerOn1234("llama");
        _chat.EnqueueText("What should it cover?");
        _chat.Enqueue(HeadlessPresentPlan());
        _chat.EnqueueText("Saved.");

        string output = await Headless("/plan chat\n/plan ready\nLet's plan a guide\n/plan ready\n/plan ready\n", exitCode: 4);

        Assert.Equal(3, _chat.Requests.Count);
        Assert.Contains("[notice] " + PlanText.ChatEnteredNotice, output);
        Assert.Contains("[error] " + PlanText.ReadyTooSoonError, output);
        Assert.Contains("The conversation began with: \"Let's plan a guide\"", _chat.Requests[0][0].Text);
        Assert.Contains(PlanText.TalkingLine, _chat.Requests[0][0].Text);
        Assert.Equal(PlanText.ReadyMessage, HeadlessLastUser(_chat.Requests[1]));
        Assert.Contains("[notice] Plan: " + HeadlessPlanPath + " (revision 1) — /plan approve [--fresh | --ralph] starts it", output);
        Assert.Equal("Guide", PlanDocument.TryParse(File.ReadAllText(HeadlessPlanFile))!.Requirement);
    }

    [Fact]
    public async Task Headless_ANotice_IsSpacedOnTerminalAppsMainScreen_NeverAReply()
    {
        ServerOn1234("llama");
        _chat.EnqueueText("# Guide\n\n- [ ] Write it\n- [ ] Link it");
        _chat.EnqueueText("Done, and I ❤️ it too.");
        string file = Path.Combine(HeadlessPlansFolder, "guide.md");
        _chat.BeforeUpdateOf = (request, _, _) =>
        {
            if (request == 1 && File.Exists(file))
            {
                File.WriteAllText(file, File.ReadAllText(file).Replace("- [ ]", "- [x]", StringComparison.Ordinal));
            }

            return Task.CompletedTask;
        };

        string output = await Headless("/plan Add a guide\n/plan save\n/plan approve\n", headlessNarrow: true);

        Assert.Contains("[notice] " + PlanText.Glyph + " I ❤️  it when a plan comes together.", output);
        Assert.Contains("Done, and I ❤️ it too.", output);                                         // a reply as it came
        Assert.Contains("[notice] " + PlanText.DoneNotice(".neon/plans/guide.md"), output);        // no selector sequence: unchanged
    }
}
