using NeonSidekick.Audits;
using NeonSidekick.Files;
using NeonSidekick.Plans;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// A fix plan's findings ticked by the app (2026-11-22, the session review: the model was told to tick the report's boxes, which <c>WriteGuard</c>
/// refuses, so no finding was ever settled): the report read back from the requirement, the ids a step closes, the tick under the report's lock, the
/// sync at a plan's end, the coverage warning and the re-audit hint.
/// </summary>
public sealed class AuditTicksTests : IDisposable
{
    private const string ReportPath = ".neon/audits/tree-2026-11-22.md";
    private const string PlanPath = ".neon/plans/fix-audit-findings-a1-a4.md";
    private readonly AuditSandbox _box = new();

    public void Dispose() => _box.Dispose();

    private static AuditFinding Finding(string id, bool settled = false, bool retracted = false, IReadOnlyList<string>? notes = null) =>
        new(id, AuditSeverity.High, "correctness", "src/a.cs", 3, "Problem " + id, "Detail.", null, null, 1, settled, retracted, retracted ? "not real" : null, notes ?? []);

    private void SaveReport(params AuditFinding[] findings)
    {
        var now = _box.Time.GetLocalNow();
        var report = new AuditReport { Label = "tree", Header = new AuditHeader(AuditStatus.Fixing, "3 passes", "", AuditFocus.Dimensions, "", "model", 1, now, now) };
        foreach (var finding in findings)
        {
            (finding.Retracted ? report.Retracted : report.Findings).Add(finding);
        }
        Assert.Null(AuditFiles.Save(_box.Files, ReportPath, report, now));
    }

    private AuditReport Report() => AuditFiles.Read(_box.Files, ReportPath)!;

    private void SavePlan(string steps, string? requirement = null)
    {
        var now = _box.Time.GetLocalNow();
        string header = PlanDocument.HeaderText(new PlanHeader(PlanStatus.Approved, 1, now, now, requirement ?? AuditText.FixRequirement(ReportPath, ["A1", "A2", "A3", "A4"])));
        _box.Write(PlanPath, header + "\n# Fix the audit's findings\n\n## Steps\n\n" + steps);
    }

    private string? Save(string path, AuditReport report) => AuditFiles.Save(_box.Files, path, report, _box.Time.GetLocalNow());

    [Fact]
    public void FixReportOf_ReadsTheRequirementBack_OldWordingAndNew()
    {
        Assert.Equal(ReportPath, AuditText.FixReportOf(AuditText.FixRequirement(ReportPath, ["A1", "A3"])));
        Assert.Equal(".neon/audits/my (odd) name.md", AuditText.FixReportOf(AuditText.FixRequirement(".neon/audits/my (odd) name.md", ["A1"])));
        // The wording this session's plans were made with.
        Assert.Equal(ReportPath, AuditText.FixReportOf("Plan the fixes for the open findings in .neon/audits/tree-2026-11-22.md (A1, A2); each step names the findings it closes; tick a finding's box in the report once fixed."));
        Assert.Null(AuditText.FixReportOf("Add a guide"));
        Assert.Null(AuditText.FixReportOf(null));
        Assert.DoesNotContain("tick a finding's box", AuditText.FixRequirement(ReportPath, ["A1"]));
        Assert.Contains("the app ticks them in the report as each step passes", AuditText.FixRequirement(ReportPath, ["A1"]));
    }

    [Theory]
    [InlineData("**Step 1 — Delete the scratch file (closes A1).** In the root", "A1")]
    [InlineData("Fix both (closes A1, A3)", "A1,A3")]
    [InlineData("Fix them, closes A2 and A5", "A2,A5")]
    [InlineData("Range (closes A3–A5)", "A3,A4,A5")]
    [InlineData("Range (Closes a5-a3)", "A3,A4,A5")]
    [InlineData("A slip (closes A1-A900)", "A1,A900")]
    [InlineData("Fix it (see A9) (closes A2)", "A2")]
    [InlineData("Nothing to close here (see A9)", "")]
    [InlineData("closes A1 and adds tests", "A1")]
    [InlineData("Fix (closes **A1**, **A3**)", "A1,A3")]                 // 2026-11-23, review #1: emphasis on every id
    [InlineData("Fix (closes `A2` and `A4`)", "A2,A4")]
    [InlineData("Fix, closes **A3**–**A5**", "A3,A4,A5")]                // and on a range's ends
    [InlineData("Fix, closes A１２", "")]                                 // review #43: other digits are no id, and never throw
    public void ClosedBy_ReadsTheIdsAfterCloses(string step, string ids) =>
        Assert.Equal(ids, string.Join(",", AuditFinding.ClosedBy(step)));

    [Fact]
    public void Settle_TicksWithANote_SkipsTheUnknownAndRetracted_AndWritesNothingWhenNothingChanges()
    {
        SaveReport(Finding("A1"), Finding("A2", retracted: true), Finding("A3", settled: true));

        var settled = AuditFiles.Settle(_box.Files, ReportPath, [new AuditTick("A1", true, "ticked when step 1 of p passed"), new AuditTick("A2", true, "x"), new AuditTick("A3", true, "x"), new AuditTick("A9", true, "x")], Save, out string? refusal);

        Assert.Null(refusal);
        Assert.Equal(["A1"], settled!.Ticked);
        Assert.Equal(["A2", "A9"], settled.Skipped);
        var report = Report();
        Assert.True(report.Findings[0].Settled);
        Assert.Equal("ticked when step 1 of p passed", report.Findings[0].Notes[^1]);
        Assert.Empty(report.Findings.Single(f => f.Id == "A3").Notes);   // already settled: untouched

        int saves = 0;
        Assert.Empty(AuditFiles.Settle(_box.Files, ReportPath, [new AuditTick("A1", true, "again")], (p, r) => { saves++; return Save(p, r); }, out _)!.Ticked);
        Assert.Equal(0, saves);
    }

    [Fact]
    public void Settle_IsRefused_WhileAnotherNeonSidekickHoldsTheReport()
    {
        SaveReport(Finding("A1"));
        var alive = OwnerLock.LiveOwner;
        OwnerLock.LiveOwner = owner => owner.ProcessId == 424242 || owner == OwnerLock.Me;
        try
        {
            _box.Write(AuditFiles.LockPathFor(ReportPath), "pid: 424242\nstarted: 2026-10-29T09:00:00.0000000+00:00\n");
            Assert.Null(AuditFiles.Settle(_box.Files, ReportPath, [new AuditTick("A1", true, "x")], Save, out string? refusal));
            Assert.Equal(AuditText.LockedError(ReportPath), refusal);
            Assert.False(Report().Findings[0].Settled);

            var outcome = AuditTicks.Sync(_box.Files, PlanPathWith("- [x] One (closes A1)\n  Done when: it builds\n"), Save);
            Assert.Equal(AuditText.TickRefusedNotice(["A1"], "tree-2026-11-22", AuditText.LockedError(ReportPath)), outcome.Refusal);
        }
        finally
        {
            OwnerLock.LiveOwner = alive;
        }
    }

    private string PlanPathWith(string steps)
    {
        SavePlan(steps);
        return PlanPath;
    }

    [Fact]
    public void Tick_SettlesTheStepsIds_AndSaysTheReportsCounts()
    {
        SaveReport(Finding("A1"), Finding("A2"), Finding("A3"), Finding("A4", retracted: true));
        SavePlan("- [x] **Step 1 — Fix two (closes A1, A2).** In src\n  Done when: it builds\n- [ ] Step 2 (closes A3)\n  Done when: it builds\n");
        var step = PlanSteps.Parse(PlanDocument.Body(File.ReadAllText(_box.Full(PlanPath))))[0];

        var outcome = AuditTicks.Tick(_box.Files, PlanPath, [step], Save);

        Assert.Equal([AuditText.TickedNotice(["A1", "A2"], "tree-2026-11-22", 2, 3)], outcome.Notices);
        Assert.Equal("🔎 Ticked A1, A2 in tree-2026-11-22 (2 of 3 fixed).", outcome.Notices[0]);
        Assert.Equal(AuditText.SettledNote("fix-audit-findings-a1-a4", 1), Report().Findings[0].Notes[^1]);
        Assert.Equal("ticked when step 1 of fix-audit-findings-a1-a4 passed", AuditText.SettledNote("fix-audit-findings-a1-a4", 1));

        // A plan that is no fix plan ticks nothing.
        SavePlan("- [x] One (closes A3)\n  Done when: x\n", requirement: "Add a guide");
        Assert.Same(AuditTickOutcome.None, AuditTicks.Tick(_box.Files, PlanPath, [step], Save));
    }

    [Fact]
    public void Sync_SettlesTickedSteps_UnsettlesOnlyItsOwnTicks_AndLeavesTheUsersAlone()
    {
        string plan = "fix-audit-findings-a1-a4";
        SaveReport(
            Finding("A1"),                                                                 // a ticked step names it: settled
            Finding("A2", settled: true, notes: [AuditText.SettledNote(plan, 2)]),         // this plan ticked it, its step redone: unsettled
            Finding("A3", settled: true),                                                  // the user ticked it by hand, an unticked step names it: stays
            Finding("A4", settled: true, notes: [AuditText.SettledNote("another-plan", 1)]));   // another plan's tick, no step here names it: stays
        SavePlan("- [x] One (closes A1)\n  Done when: x\n- [ ] Two (closes A2, A3)\n  Done when: x\n");

        var outcome = AuditTicks.Sync(_box.Files, PlanPath, Save);

        var report = Report();
        Assert.True(report.Findings[0].Settled);
        Assert.False(report.Findings[1].Settled);
        Assert.Equal(AuditText.UnsettledNote(plan, 2), report.Findings[1].Notes[^1]);
        Assert.True(report.Findings[2].Settled);
        Assert.True(report.Findings[3].Settled);
        Assert.Equal([AuditText.TickedNotice(["A1"], "tree-2026-11-22", 3, 4), AuditText.UntickedNotice(["A2"], "tree-2026-11-22", 3, 4)], outcome.Notices);

        // Again: nothing left to change, nothing said.
        Assert.Empty(AuditTicks.Sync(_box.Files, PlanPath, Save).Notices);
    }

    /// <summary>A finding two steps close (2026-11-23, review #26): the first step's tick waits; the second's settles it.</summary>
    [Fact]
    public void Tick_AFindingTwoStepsClose_WaitsForTheSecond()
    {
        SaveReport(Finding("A1"), Finding("A2"));
        SavePlan("- [x] One (closes A1, A2)\n  Done when: x\n- [ ] Two (closes A2)\n  Done when: x\n");
        var first = PlanSteps.Parse(PlanDocument.Body(File.ReadAllText(_box.Full(PlanPath))))[0];

        AuditTicks.Tick(_box.Files, PlanPath, [first], Save);
        Assert.Equal([true, false], Report().Findings.Select(f => f.Settled));

        SavePlan("- [x] One (closes A1, A2)\n  Done when: x\n- [x] Two (closes A2)\n  Done when: x\n");
        var second = PlanSteps.Parse(PlanDocument.Body(File.ReadAllText(_box.Full(PlanPath))))[1];
        AuditTicks.Tick(_box.Files, PlanPath, [second], Save);
        Assert.True(Report().Findings[1].Settled);
        Assert.Equal(AuditText.SettledNote("fix-audit-findings-a1-a4", 2), Report().Findings[1].Notes[^1]);
    }

    /// <summary>The sync's twin of the rule above (2026-11-23, review #26): a finding a ticked and an unticked step name stays open.</summary>
    [Fact]
    public void Sync_AFindingAnUntickedStepAlsoNames_StaysOpen()
    {
        SaveReport(Finding("A1"), Finding("A2"));
        SavePlan("- [x] One (closes A1, A2)\n  Done when: x\n- [ ] Two (closes A2)\n  Done when: x\n");

        AuditTicks.Sync(_box.Files, PlanPath, Save);

        Assert.Equal([true, false], Report().Findings.Select(f => f.Settled));
    }

    /// <summary>A finding this plan ticked that someone reopened since (2026-11-23, review #80): a sync leaves it open.</summary>
    [Fact]
    public void Sync_LeavesOpen_AFindingReopenedAfterThisPlansTick()
    {
        string plan = "fix-audit-findings-a1-a4";
        SaveReport(Finding("A1", notes: [AuditText.SettledNote(plan, 1), "reopened: still broken"]), Finding("A2", notes: [AuditText.UnsettledNote(plan, 1)]));
        SavePlan("- [x] One (closes A1, A2)\n  Done when: x\n");

        AuditTicks.Sync(_box.Files, PlanPath, Save);

        Assert.Equal([false, true], Report().Findings.Select(f => f.Settled));   // A2, unticked by this plan's redo, is ticked again
    }

    /// <summary>A parent step naming a finding (2026-11-23, review #96): its note names the parent's last leaf, never step 0.</summary>
    [Fact]
    public void Tick_AParentsNote_NamesItsLastLeaf()
    {
        SaveReport(Finding("A1"));
        SavePlan("- [x] Parent (closes A1)\n  - [x] One\n    Done when: x\n  - [x] Two\n    Done when: x\n");
        var parent = PlanSteps.Parse(PlanDocument.Body(File.ReadAllText(_box.Full(PlanPath))))[0];

        AuditTicks.Tick(_box.Files, PlanPath, [parent], Save);

        Assert.Equal(AuditText.SettledNote("fix-audit-findings-a1-a4", parent.Children[^1].Index), Report().Findings[0].Notes[^1]);
        Assert.NotEqual(0, parent.Children[^1].Index);
    }

    /// <summary>A note of another plan whose name ends in this one's (2026-11-23, review #81): not this plan's.</summary>
    [Fact]
    public void IsSettledNoteOf_NeedsAStepNumber()
    {
        Assert.True(AuditText.IsSettledNoteOf(AuditText.SettledNote("fix", 1234), "fix"));
        Assert.False(AuditText.IsSettledNoteOf("ticked when step 2 of review of fix passed", "fix"));
        Assert.True(AuditText.IsUnsettledNoteOf(AuditText.UnsettledNote("fix", 3), "fix"));
        Assert.False(AuditText.IsUnsettledNoteOf("unticked: step 3 of review of fix was redone", "fix"));
    }

    [Fact]
    public void Sync_AReportThatIsntThere_IsLoggedAndSkipped()
    {
        SavePlan("- [x] One (closes A1)\n  Done when: x\n");

        var outcome = AuditTicks.Sync(_box.Files, PlanPath, Save);

        Assert.Empty(outcome.Notices);
        Assert.Null(outcome.Refusal);
        Assert.Contains(outcome.Logs, l => l.Contains("isn't there", StringComparison.Ordinal));
    }

    [Fact]
    public void OpenIds_TheCoverageWarning_AndTheReauditHint()
    {
        SaveReport(Finding("A1"), Finding("A2", settled: true), Finding("A3", retracted: true), Finding("A6"));
        string requirement = AuditText.FixRequirement(ReportPath, ["A1", "A6"]);

        Assert.Equal(["A1", "A6"], AuditTicks.OpenIds(_box.Files, requirement));
        Assert.Null(AuditTicks.OpenIds(_box.Files, "Add a guide"));
        Assert.Null(AuditTicks.OpenIds(_box.Files, AuditText.FixRequirement(".neon/audits/gone.md", ["A1"])));

        string body = "# Fix\n\n## Steps\n\n- [ ] One (closes A1)\n  Done when: x\n";
        var reading = PlanFit.Read(body, null, reviewed: true, int.MaxValue, ["A1", "A6"]);
        Assert.Equal(RalphText.FitUnclosedFindings(["A6"]), reading.Unclosed);
        Assert.Contains(reading.Unclosed, reading.Shape);
        Assert.Contains(reading.Unclosed, reading.Problems);
        Assert.Empty(PlanFit.Structural(body));
        Assert.Null(PlanFit.Read(body, null, reviewed: true, int.MaxValue).Unclosed);

        Assert.Equal("🔎 /code-audit resume tree-2026-11-22 checks the fixes held and looks for new problems.", AuditTicks.ReauditHintFor(_box.Files, requirement));
        Assert.Null(AuditTicks.ReauditHintFor(_box.Files, "Add a guide"));
        Assert.Null(AuditTicks.ReauditHintFor(_box.Files, AuditText.FixRequirement(".neon/audits/gone.md", ["A1"])));
    }
}
