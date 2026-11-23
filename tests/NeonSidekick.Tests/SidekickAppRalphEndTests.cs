using Microsoft.Extensions.AI;
using NeonSidekick.Audits;
using NeonSidekick.Files;
using NeonSidekick.Llm;
using NeonSidekick.Plans;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// The end of a Ralph loop and the start of a code audit headless (2026-11-22, the session review): the line after a loop that ran a pass is a
/// new session, the last pass's row saved with the loop's end; a pass that ends without <c>finish_step</c> is asked once; a fix plan ticks its
/// findings and its done end points at <c>/code-audit resume</c>; <c>/code-audit</c> starts a new session.
/// </summary>
public partial class SidekickAppTests
{
    private List<Sessions.SessionRecord> HeadlessSessionRecords()
    {
        using var store = new Sessions.SessionStore(_settings.ProfileDirectory);
        return [.. store.List(0).Select(s => store.Load(s.Id)!)];
    }

    [Fact]
    public async Task Headless_PlanRalph_TheLineAfterADoneLoop_IsANewSession()
    {
        ServerOn1234("llama");
        HeadlessRalphPlan();
        HeadlessRalphPass("1");
        HeadlessRalphPass("2");
        _chat.EnqueueText("Nothing.");

        string output = await Headless("/plan ralph add-guide\nwhat next?\n");

        Assert.Equal(5, _chat.Requests.Count);
        Assert.Equal("what next?", HeadlessLastUser(_chat.Requests[4]));
        Assert.Single(_chat.Requests[4], ConversationHistory.IsTurnStart);
        Assert.DoesNotContain("/code-audit resume", output);
        var records = HeadlessSessionRecords();
        Assert.Single(Assert.Single(records, r => r.Turns.Any(t => t.UserText == "what next?")).Turns);
        // The last pass's row holds the loop's end: no plan still being carried out.
        var lastPass = Assert.Single(records, r => r.Turns.Any(t => t.UserText == RalphText.TranscriptLine(2, 2, 2, "Two")));
        Sessions.SessionHistory.FromJson(lastPass.HistoryJson, out _, out var executing);
        Assert.Null(executing);
    }

    [Fact]
    public async Task Headless_PlanRalph_APassThatEndsWithoutFinishStep_IsAskedOnce()
    {
        ServerOn1234("llama");
        HeadlessRalphPlan();
        _chat.EnqueueText("\n\n");
        HeadlessRalphPass("1");
        HeadlessRalphPass("2");

        string output = await Headless("/plan ralph add-guide\n");

        Assert.Equal(5, _chat.Requests.Count);
        Assert.Equal(RalphText.NudgeMessage, HeadlessLastUser(_chat.Requests[1]));
        Assert.Contains(_chat.Requests[1], m => m.Role == ChatRole.User && m.Text == HeadlessLastUser(_chat.Requests[0]));   // the pass's own conversation
        Assert.Single(output.Split('\n'), l => l.Contains(RalphText.NudgeLine(1), StringComparison.Ordinal));
        Assert.Contains("[notice] " + RalphText.StepDoneNotice(1, 2, "One"), output);
        Assert.Equal(PlanStatus.Done, HeadlessPlanStatus);
    }

    /// <summary>The screen's twin (2026-11-23, review #34): a failed nudge leaves the pass judged by its own clean turn, with no retry notice.</summary>
    [Fact]
    public async Task Headless_PlanRalph_AFailedNudge_JudgesThePassByItsOwnTurn()
    {
        ServerOn1234("llama");
        HeadlessRalphPlan();
        _chat.EnqueueText("\n\n");
        _chat.EnqueueText("unused");
        _chat.BeforeUpdateOf = (request, _, _) => request == 1 ? throw new InvalidOperationException("the nudge failed") : Task.CompletedTask;
        HeadlessRalphPass("1");
        HeadlessRalphPass("2");

        string output = await Headless("/plan ralph add-guide\n");

        Assert.Equal(RalphText.NudgeMessage, HeadlessLastUser(_chat.Requests[1]));
        Assert.DoesNotContain(RalphText.RetryNotice, output);
        Assert.Equal(PlanStatus.Done, HeadlessPlanStatus);
    }

    [Fact]
    public async Task Headless_PlanRalph_AFixPlan_TicksItsFindings_AndPointsAtAResume()
    {
        ServerOn1234("llama");
        const string report = ".neon/audits/tree-2026-11-22.md";
        var files = new WorkingDirectory(() => HeadlessAuditRoot, TimeProvider.System);
        var now = DateTimeOffset.Now;
        var audit = new AuditReport { Label = "tree", Header = new AuditHeader(AuditStatus.Fixing, "3 passes", "", AuditFocus.Dimensions, "", "model", 1, now, now) };
        audit.Findings.Add(new AuditFinding("A1", AuditSeverity.High, "correctness", "src/a.cs", 3, "Problem A1", "Detail.", null, null, 1, false, false, null, []));
        Directory.CreateDirectory(HeadlessAuditRoot);
        Assert.Null(AuditFiles.Save(files, report, audit, now));
        HeadlessRalphPlan(body: "# Fix\n\n## Steps\n- [ ] One (closes A1)\n");
        string text = File.ReadAllText(HeadlessPlanFile);
        File.WriteAllText(HeadlessPlanFile, text.Replace("Add a guide", AuditText.FixRequirement(report, ["A1"]), StringComparison.Ordinal));
        HeadlessRalphPass("1");

        string output = await Headless("/plan ralph add-guide\n");

        Assert.True(AuditFiles.Read(files, report)!.Findings[0].Settled);
        Assert.Contains("[notice] " + AuditText.TickedNotice(["A1"], "tree-2026-11-22", 1, 1), output);
        Assert.Contains(AuditText.ReauditHint("tree-2026-11-22"), output);
    }

    [Fact]
    public async Task Headless_CodeAudit_AfterALine_StartsANewSession()
    {
        _settings.Update(d => d.CodeAuditCleanPasses = 1);
        ServerOn1234("llama");
        HeadlessAuditFiles();
        _chat.EnqueueText("Hi.");
        _chat.Enqueue(HeadlessAuditCall("a1", "{\"findings\":[{\"severity\":\"high\",\"dimension\":\"security\",\"file\":\"src/a.cs\",\"line\":3,\"title\":\"SQL built from input\",\"detail\":\"Joined.\"}]}"));
        _chat.EnqueueText("Pass 1.");
        _chat.Enqueue(HeadlessAuditCall("a2", "{\"checked\":[\"the queries\"]}"));
        _chat.EnqueueText("Pass 2.");

        await Headless("hello there\n/code-audit src\n");

        Assert.DoesNotContain(_chat.Requests[1], m => m.Text == "hello there" || m.Text == "Hi.");
        Assert.Single(Assert.Single(HeadlessSessionRecords(), r => r.Turns.Any(t => t.UserText == "hello there")).Turns);
    }
}
