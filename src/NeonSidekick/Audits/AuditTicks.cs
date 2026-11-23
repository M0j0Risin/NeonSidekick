using NeonSidekick.Files;
using NeonSidekick.Plans;

namespace NeonSidekick.Audits;

/// <summary>
/// One change <see cref="AuditFiles.Settle"/> makes to a finding: its box set to <paramref name="Settled"/>, <paramref name="Note"/> added under it, only
/// when <paramref name="When"/> (if given) holds of the finding as it stands.
/// </summary>
public sealed record AuditTick(string Id, bool Settled, string Note, Func<AuditFinding, bool>? When = null);

/// <summary>What <see cref="AuditFiles.Settle"/> changed: the ids ticked and unticked, the ids it skipped (unknown or retracted), and the report as saved.</summary>
public sealed record AuditSettled(IReadOnlyList<string> Ticked, IReadOnlyList<string> Unticked, IReadOnlyList<string> Skipped, AuditReport Report);

/// <summary>
/// What a tick or a sync came to (2026-11-22): the notices to say, a refusal (another NeonSidekick held the report, or its save failed) and the log
/// lines; <see cref="None"/> when the plan is no fix plan or there was nothing to do.
/// </summary>
public sealed record AuditTickOutcome(IReadOnlyList<string> Notices, string? Refusal, IReadOnlyList<string> Logs)
{
    public static AuditTickOutcome None { get; } = new([], null, []);
}

/// <summary>
/// A fix plan's findings kept in step with its steps (2026-11-22, the session review: <see cref="AuditText.FixRequirement"/> told the model to tick the
/// report's boxes, which <c>WriteGuard</c> refuses under <c>.neon</c>, so no finding was ever settled). The plan's <c>requirement:</c> header names the
/// report (<see cref="AuditText.FixReportOf"/>), a step's line the findings it closes (<see cref="AuditFinding.ClosedBy"/>). <see cref="Tick"/> settles
/// a step's findings as the judge stands it; <see cref="Sync"/>, at a loop's or a plan's end, settles every finding a ticked step names and unsettles
/// one only unticked steps name whose last note is this plan's tick (never a box the user ticked by hand). The report's path is relative to the
/// plan's folder; a report that isn't there is logged and skipped.
/// </summary>
public static class AuditTicks
{
    /// <summary>The report a plan's text names (its header's requirement), or null for a plan that is no fix plan.</summary>
    public static string? ReportOf(string planText) => AuditText.FixReportOf(PlanDocument.TryParse(planText)?.Requirement);

    /// <summary>The plan's name as the notes say it: its file name without <c>.md</c>.</summary>
    public static string PlanName(string planPath) => AuditText.ReportName(planPath);

    /// <summary>
    /// The findings <paramref name="stood"/> (the step the judge stood, and the parent it ticked) close, settled in the report the plan at
    /// <paramref name="planPath"/> names; one that another unticked step also names waits for it (2026-11-23, review #26).
    /// </summary>
    public static AuditTickOutcome Tick(WorkingDirectory files, string planPath, IReadOnlyList<PlanStep> stood, Func<string, AuditReport, string?> save)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(stood);
        if (PlanFiles.ReadWhole(files, planPath) is not { } text || ReportOf(text) is not { } report)
        {
            return AuditTickOutcome.None;
        }

        string plan = PlanName(planPath);
        var others = PlanSteps.Flat(PlanSteps.Parse(PlanDocument.Body(text)))
            .Where(s => !s.Ticked && !stood.Any(t => t.Line == s.Line && string.Equals(t.Text, s.Text, StringComparison.Ordinal)))
            .ToList();
        var ticks = new List<AuditTick>();
        foreach (var step in stood)
        {
            foreach (string id in AuditFinding.ClosedBy(step.Text))
            {
                if (!ticks.Any(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase)) && !others.Any(s => Names(s, id)))
                {
                    ticks.Add(new AuditTick(id, true, AuditText.SettledNote(plan, NoteIndex(step))));
                }
            }
        }

        return ticks.Count == 0 ? AuditTickOutcome.None : Apply(files, report, ticks, save);
    }

    private static bool Names(PlanStep step, string id) => AuditFinding.ClosedBy(step.Text).Contains(id, StringComparer.OrdinalIgnoreCase);

    /// <summary>The step number a note names: a parent's is its last leaf's (2026-11-23, review #96: a parent's Index is 0).</summary>
    private static int NoteIndex(PlanStep step) => step.IsLeaf ? step.Index : NoteIndex(step.Children[^1]);

    /// <summary>
    /// Whether this plan ticked <paramref name="finding"/> and it was opened again since (2026-11-23, review #80): its last note of this plan's is a
    /// <see cref="AuditText.SettledNote"/>, yet it's open, so someone reopened it; a sync leaves it so.
    /// </summary>
    private static bool ReopenedSince(AuditFinding finding, string plan) =>
        finding.Notes.LastOrDefault(n => AuditText.IsSettledNoteOf(n, plan) || AuditText.IsUnsettledNoteOf(n, plan)) is { } last
        && AuditText.IsSettledNoteOf(last, plan);

    /// <summary>
    /// The report the plan at <paramref name="planPath"/> names brought in step with its steps: an id only ticked steps name settled, unless this plan
    /// ticked it before and it was reopened since (2026-11-23, review #80); an id any unticked step names (review #26: a finding two steps close waits
    /// for both) unsettled when its last note is this plan's <see cref="AuditText.SettledNote"/> (a tick the app made for this plan, never one the
    /// user made by hand). Covers a Redo, a resume's Keep, a plain execute and a tick refused under a lock.
    /// </summary>
    public static AuditTickOutcome Sync(WorkingDirectory files, string planPath, Func<string, AuditReport, string?> save)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (PlanFiles.ReadWhole(files, planPath) is not { } text || ReportOf(text) is not { } report)
        {
            return AuditTickOutcome.None;
        }

        string plan = PlanName(planPath);
        var steps = PlanSteps.Flat(PlanSteps.Parse(PlanDocument.Body(text)));
        var ticks = new List<AuditTick>();
        var settledBy = new Dictionary<string, PlanStep>(StringComparer.OrdinalIgnoreCase);
        var unsettledBy = new Dictionary<string, PlanStep>(StringComparer.OrdinalIgnoreCase);
        foreach (var step in steps)
        {
            foreach (string id in AuditFinding.ClosedBy(step.Text))
            {
                if (step.Ticked)
                {
                    settledBy.TryAdd(id, step);
                }
                else
                {
                    unsettledBy.TryAdd(id, step);
                }
            }
        }

        foreach (var (id, step) in settledBy.Where(s => !unsettledBy.ContainsKey(s.Key)))
        {
            ticks.Add(new AuditTick(id, true, AuditText.SettledNote(plan, NoteIndex(step)), f => !ReopenedSince(f, plan)));
        }

        foreach (var (id, step) in unsettledBy)
        {
            ticks.Add(new AuditTick(id, false, AuditText.UnsettledNote(plan, NoteIndex(step)), f => f.Notes.Count > 0 && AuditText.IsSettledNoteOf(f.Notes[^1], plan)));
        }

        return ticks.Count == 0 ? AuditTickOutcome.None : Apply(files, report, ticks, save);
    }

    /// <summary>
    /// The open, not retracted ids of the report a fix plan's <paramref name="requirement"/> names (2026-11-22, the coverage warning); null for a
    /// plan that is no fix plan, or a report that isn't there or doesn't read.
    /// </summary>
    public static IReadOnlyCollection<string>? OpenIds(WorkingDirectory files, string? requirement)
    {
        ArgumentNullException.ThrowIfNull(files);
        return AuditText.FixReportOf(requirement) is { } path && AuditFiles.Read(files, path) is { } report ? report.Open.Select(f => f.Id).ToList() : null;
    }

    /// <summary><see cref="OpenIds"/> for the plan at <paramref name="planPath"/>, its requirement the file header's (a loop resumed after a restart too).</summary>
    public static IReadOnlyCollection<string>? OpenIdsOfPlan(WorkingDirectory files, string planPath) =>
        PlanFiles.ReadWhole(files, planPath) is { } text ? OpenIds(files, PlanDocument.TryParse(text)?.Requirement) : null;

    /// <summary>
    /// <see cref="AuditText.ReauditHint"/> for a fix plan's <paramref name="requirement"/> (2026-11-22), or null when the plan is no fix plan or its
    /// report doesn't read (a resume of it would only fail).
    /// </summary>
    public static string? ReauditHintFor(WorkingDirectory files, string? requirement)
    {
        ArgumentNullException.ThrowIfNull(files);
        return AuditText.FixReportOf(requirement) is { } path && AuditFiles.Read(files, path) is not null ? AuditText.ReauditHint(AuditText.ReportName(path)) : null;
    }

    private static AuditTickOutcome Apply(WorkingDirectory files, string report, List<AuditTick> ticks, Func<string, AuditReport, string?> save)
    {
        string name = AuditText.ReportName(report);
        var logs = new List<string>();
        if (!AuditFiles.Exists(files, report))
        {
            logs.Add($"audit: {report} isn't there; {string.Join(", ", ticks.Select(t => t.Id))} not ticked");
            return new AuditTickOutcome([], null, logs);
        }

        var settled = AuditFiles.Settle(files, report, ticks, save, out string? refusal);
        if (refusal is not null)
        {
            var wanted = ticks.Select(t => t.Id).ToList();
            logs.Add($"audit: {string.Join(", ", wanted)} not ticked in {report}: {refusal}");
            return new AuditTickOutcome([], AuditText.TickRefusedNotice(wanted, name, refusal), logs);
        }

        if (settled is null)
        {
            logs.Add($"audit: {report} can't be read; nothing ticked");
            return new AuditTickOutcome([], null, logs);
        }

        if (settled.Skipped.Count > 0)
        {
            logs.Add($"audit: {report} holds no open or settled {string.Join(", ", settled.Skipped)}; skipped");
        }

        var notices = new List<string>();
        int done = settled.Report.SettledCount;
        int total = settled.Report.OpenCount + done;
        if (settled.Ticked.Count > 0)
        {
            notices.Add(AuditText.TickedNotice(settled.Ticked, name, done, total));
            logs.Add($"audit: ticked {string.Join(", ", settled.Ticked)} in {report}");
        }

        if (settled.Unticked.Count > 0)
        {
            notices.Add(AuditText.UntickedNotice(settled.Unticked, name, done, total));
            logs.Add($"audit: unticked {string.Join(", ", settled.Unticked)} in {report}");
        }

        return new AuditTickOutcome(notices, null, logs);
    }
}
