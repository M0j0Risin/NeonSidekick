using NeonSidekick.Audits;
using NeonSidekick.Diagnostics;
using NeonSidekick.Files;
using NeonSidekick.Plans;
using NeonSidekick.Settings;

namespace NeonSidekick.App;

/// <summary>
/// <c>/code-audit</c> headless (2026-10-18): the screen's words and refusals as <c>[notice]</c> / <c>[error]</c> lines, every pass sent as the
/// next line without reading stdin (the gap review's slot), and the end said with the no-pane lines (<c>/code-audit fix</c>, <c>open</c>),
/// since there is no pane. A limit of 0 is <see cref="AppSettingsData.MaxCodeAuditPasses"/> (no Esc here); under <c>Advisor confirm</c> a
/// reviewer other than the model falls back to the model (no confirm to ask on).
/// </summary>
public sealed partial class SidekickApp
{
    /// <summary>
    /// A <c>/code-audit</c> line: the text to send as the next turn (a pass's message, or plan mode's requirement for <c>fix</c>), or null when
    /// the line is done (a list, a report, a refusal).
    /// </summary>
    private async Task<string?> HeadlessAuditAsync(AuditSession audit, PlanSession plan, WorkingDirectory files, Git.GitAccess git, string args, Func<PlanPassMeter> meter, CancellationToken cancellationToken)
    {
        string? Existing(string word)
        {
            if (AuditPath.Of(files, word, out _) is not { } path || files.Resolve(path, forWrite: false, out string full) != FileOutcome.Ok)
            {
                return null;
            }

            return File.Exists(full) || Directory.Exists(full) ? path : null;
        }

        var parsed = AuditCommand.Parse(args, Existing);
        if (parsed.Error is { } error)
        {
            await HeadlessErrorAsync(error).ConfigureAwait(false);
            return null;
        }

        if (parsed.Verb == AuditVerb.Open)
        {
            await HeadlessShowAuditAsync(files, parsed.Name).ConfigureAwait(false);
            return null;
        }

        if (parsed.Verb == AuditVerb.Fix)
        {
            if (plan.Active)
            {
                await HeadlessErrorAsync(AuditText.PlanningError).ConfigureAwait(false);
                return null;
            }

            if (!EffectiveSettings.LlmOfferTools)
            {
                await HeadlessErrorAsync(PlanText.NeedsToolsError).ConfigureAwait(false);
                return null;
            }

            if (await HeadlessReportPathAsync(files, parsed.Name).ConfigureAwait(false) is not { } fixPath)
            {
                return null;
            }

            if (AuditFiles.Read(files, fixPath) is not { } fixing)
            {
                await HeadlessErrorAsync(AuditText.ReportUnreadable(fixPath)).ConfigureAwait(false);
                return null;
            }

            string name = Path.GetFileNameWithoutExtension(fixPath);
            var open = fixing.Open.Select(f => f.Id).ToList();
            if (open.Count == 0)
            {
                await HeadlessErrorAsync(AuditText.NoOpenFindings(name)).ConfigureAwait(false);
                return null;
            }

            fixing.Header = fixing.Header with { Status = AuditStatus.Fixing };
            if (AuditFiles.Save(files, fixPath, fixing, _time.GetLocalNow()) is { } failed)
            {
                await HeadlessNoticeLineAsync("[notice] " + failed).ConfigureAwait(false);
            }

            await HeadlessNoticeLineAsync("[notice] " + AuditText.FixingNotice(name, parsed.Name is null)).ConfigureAwait(false);
            plan.Enter(AuditText.FixRequirement(fixPath, open));
            await HeadlessNoticeLineAsync("[notice] " + PlanText.EnteredNotice).ConfigureAwait(false);
            return plan.Requirement;
        }

        // A start or a resume: the screen's refusals (plan mode on, tools off, read_file not offered).
        if (plan.Active)
        {
            await HeadlessErrorAsync(AuditText.PlanningError).ConfigureAwait(false);
            return null;
        }

        if (!EffectiveSettings.LlmOfferTools)
        {
            await HeadlessErrorAsync(AuditText.NeedsToolsError).ConfigureAwait(false);
            return null;
        }

        if (!EffectiveSettings.FileTools || ToolsText.DisabledSet(EffectiveSettings.ToolsDisabled).Contains(Llm.Tools.ReadFileTool.ToolName))
        {
            await HeadlessErrorAsync(AuditText.NeedsReadFileError).ConfigureAwait(false);
            return null;
        }

        string path;
        AuditScopeRequest request;
        IReadOnlyList<string> focus;
        string brief;
        PlanReviewer? named;
        if (parsed.Verb == AuditVerb.Resume)
        {
            if (await HeadlessReportPathAsync(files, parsed.Name).ConfigureAwait(false) is not { } resumed)
            {
                return null;
            }

            if (AuditFiles.Read(files, resumed) is not { } report)
            {
                await HeadlessErrorAsync(AuditText.ReportUnreadable(resumed)).ConfigureAwait(false);
                return null;
            }

            var (fromHeader, problem) = AuditCommand.ScopeFromHeader(report.Header.Scope, Existing);
            if (problem is not null)
            {
                await HeadlessErrorAsync(problem).ConfigureAwait(false);
                return null;
            }

            path = resumed;
            request = fromHeader!;
            focus = report.Header.Focus;
            brief = report.Header.Brief;
            named = parsed.Reviewer ?? PlanText.ParseReviewer(report.Header.Reviewer);
        }
        else
        {
            path = "";
            request = parsed.Scope!;
            focus = parsed.Focus ?? AuditFocus.Normalize(EffectiveSettings.CodeAuditFocus);
            brief = parsed.Brief;
            named = parsed.Reviewer;
        }

        var resolved = AuditScopes.Resolve(files, git, request, cancellationToken);
        if (resolved.Scope is not { } scope)
        {
            await HeadlessErrorAsync(resolved.Error ?? AuditText.NothingToAudit("")).ConfigureAwait(false);
            return null;
        }

        var reviewer = named ?? PlanReviewer.Model;
        if (reviewer != PlanReviewer.Model && HeadlessAdvisorsOffered() && EffectiveSettings.AdvisorConfirm)
        {
            // No confirm seam headless: every advisor call would be refused, so the model audits alone.
            reviewer = PlanReviewer.Model;
            await HeadlessNoticeLineAsync("[notice] " + AuditText.ConfirmFallbackNotice).ConfigureAwait(false);
        }

        int limit = parsed.Passes ?? EffectiveSettings.CodeAuditPasses;
        if (limit <= 0)
        {
            await HeadlessNoticeLineAsync("[notice] " + AuditText.HeadlessCapNotice(AppSettingsData.MaxCodeAuditPasses, fromCommand: parsed.Passes is not null)).ConfigureAwait(false);
        }

        var now = _time.GetLocalNow();
        if (parsed.Verb == AuditVerb.Resume)
        {
            var report = AuditFiles.Read(files, path)!;
            report.Header = report.Header with { Status = AuditStatus.Auditing, Reviewer = PlanText.ReviewerWord(reviewer) };
            if (AuditFiles.Save(files, path, report, now) is { } failed)
            {
                await HeadlessErrorAsync(failed).ConfigureAwait(false);
                return null;
            }

            await HeadlessNoticeLineAsync("[notice] " + AuditText.ResumedNotice(Path.GetFileNameWithoutExtension(path), parsed.Name is null)).ConfigureAwait(false);
        }
        else
        {
            path = AuditFiles.Choose(files, scope.NameLabel, now);
            var report = new AuditReport
            {
                Label = scope.Label,
                Header = new AuditHeader(AuditStatus.Auditing, AuditText.PassesLine(0, 0, 0, scope.Files.Count + scope.MoreFiles), scope.Words, focus, brief, PlanText.ReviewerWord(reviewer), 1, now, now),
                CoverageScope = scope.Files.Count + scope.MoreFiles,
            };
            if (AuditFiles.Save(files, path, report, now) is { } failed)
            {
                await HeadlessErrorAsync(failed).ConfigureAwait(false);
                return null;
            }
        }

        // confirm's clean count kept when the fallback above made the model the reviewer, as the gap review's.
        int clean = named == PlanReviewer.Confirm ? Math.Max(2, EffectiveSettings.CodeAuditCleanPasses) : EffectiveSettings.CodeAuditCleanPasses;
        var loop = audit.Start(path, scope, focus, brief, reviewer, HeadlessGapLimit(limit), clean);
        if (parsed.Verb == AuditVerb.Start)
        {
            await HeadlessNoticeLineAsync("[notice] " + AuditText.StartedNotice(scope.Label, scope.Files.Count, scope.MoreFiles, scope.Skipped, audit.Focus, loop.Limit, path)).ConfigureAwait(false);
        }

        if (loop.CleanLowered)
        {
            await HeadlessNoticeLineAsync("[notice] " + AuditText.CleanLoweredNotice(loop.Limit, loop.CleanAsked)).ConfigureAwait(false);
        }

        return HeadlessBeginAuditPass(audit, files, loop, meter());
    }

    /// <summary>The next pass begun, its message (the reviewer the model when no advisor is offered).</summary>
    private string HeadlessBeginAuditPass(AuditSession audit, WorkingDirectory files, PlanGapLoop loop, PlanPassMeter? meter)
    {
        bool offered = HeadlessAdvisorsOffered();
        loop.BeginPass(AuditFiles.Read(audit.Files, audit.Path!)?.StallBody(), meter, offered);
        string focus = AuditFocus.PassFocus(audit.Focus, loop.Confirmation).Short;
        return AuditText.PassMessage(audit.Scope!.Label, audit.Path!, loop.Pass, loop.Limit, offered ? loop.PassReviewer : PlanReviewer.Model, focus, loop.CleanStreak);
    }

    /// <summary>
    /// After a headless turn while an audit runs, the screen's <c>TakeAuditPass</c> without a pane or a queue: the coverage merged, the pass
    /// judged and said into <paramref name="lines"/>, then the next pass's message, or null once the audit is over (its summary, the report's
    /// status, the stop's line and the no-pane lines).
    /// </summary>
    private string? HeadlessAuditAfterTurn(AuditSession audit, WorkingDirectory files, IReadOnlyList<string> tools, bool cancelled, List<string> lines, PlanPassMeter? meter)
    {
        if (audit.Running is not { Pending: false } loop || audit.Path is not { } path)
        {
            return null;
        }

        // The report is the audit's own folder's, whatever the working directory is now (2026-10-19); a move ends the audit, as on the screen.
        bool moved = audit.Moved;
        string root = audit.Root!;
        files = audit.Files;
        AuditFiles.MergeCoverage(files, path, audit.ViewedNow());
        string? body = AuditFiles.Read(files, path)?.StallBody();
        var stop = moved ? PlanGapStop.Stopped : loop.Next(!cancelled, userSpoke: false, body, meter);
        bool advisorAsked = tools.Contains(Llm.Tools.NeonAdvisorTool.ToolName, StringComparer.Ordinal);
        DiagnosticLog.Info(ChatScreen.AppCategory, AuditText.PassLogLine(loop.Pass, loop.PassGaps.Count, loop.PassMinor.Count, loop.PassClean, advisorAsked, loop.History.Count > 0 ? loop.History[^1] : null));
        var coverage = audit.Coverage();
        if (stop is not { } reason)
        {
            lines.Add(loop.PassGaps.Count > 0
                ? AuditText.PassDoneNotice(loop.Pass, loop.Limit, loop.PassGaps.Count, loop.PassMinor.Count, coverage.Read, coverage.Scope)
                : AuditText.CleanPassNotice(loop.Pass, loop.Limit, loop.CleanCount, loop.CleanPasses));
            return HeadlessBeginAuditPass(audit, files, loop, meter);
        }

        string name = audit.Name;
        lines.AddRange(AuditText.ReviewSummaryLines(loop.History, reason, loop.CleanPasses, audit.Focus));
        int clean = reason == PlanGapStop.Converged ? loop.CleanPasses : loop.PassClean ? loop.CleanCount : 0;
        bool anyOpen = false;
        if (AuditFiles.Read(files, path) is { } report)
        {
            report.CoverageRead = coverage.Read;
            report.CoverageScope = coverage.Scope;
            report.Header = report.Header with
            {
                Status = reason == PlanGapStop.Converged ? AuditStatus.Converged : AuditStatus.Stopped,
                Passes = AuditText.PassesLine(loop.Pass, clean, coverage.Read, coverage.Scope, reason),
            };
            anyOpen = report.OpenCount > 0;
            if (AuditFiles.Save(files, path, report, _time.GetLocalNow()) is { } failed)
            {
                lines.Add(failed);
            }
        }

        audit.Stop();
        lines.Add(moved ? AuditText.WorkdirMovedNotice(name, root)
            : reason == PlanGapStop.Converged ? AuditText.ConvergedNotice(name, loop.Pass, loop.CleanPasses, coverage.Read, coverage.Scope)
            : AuditText.StopNotice(reason, name, loop.Pass));
        if (reason is PlanGapStop.Converged or PlanGapStop.Limit or PlanGapStop.Stalled or PlanGapStop.Unconfirmed)
        {
            lines.AddRange(AuditText.NoPaneLines(name, anyOpen));
        }

        return null;
    }

    /// <summary>The report a name names, or the newest; null, said, when there is none.</summary>
    private async Task<string?> HeadlessReportPathAsync(WorkingDirectory files, string? name)
    {
        if (name is null)
        {
            if (AuditFiles.List(files).FirstOrDefault(r => !r.Unreadable) is { } first)
            {
                return first.Path;
            }

            await HeadlessNoticeLineAsync("[notice] " + AuditText.NoReportsNotice).ConfigureAwait(false);
            return null;
        }

        if (AuditFiles.Resolve(files, name) is { } path)
        {
            return path;
        }

        await HeadlessErrorAsync(AuditText.NoSuchReport(name)).ConfigureAwait(false);
        return null;
    }

    /// <summary><c>/code-audit open [name]</c> headless: the list, or the report's lines, as notices.</summary>
    private async Task HeadlessShowAuditAsync(WorkingDirectory files, string? name)
    {
        if (name is null)
        {
            var reports = AuditFiles.List(files);
            if (reports.Count == 0)
            {
                await HeadlessNoticeLineAsync("[notice] " + AuditText.NoReportsNotice).ConfigureAwait(false);
                return;
            }

            foreach (var report in reports)
            {
                await HeadlessNoticeLineAsync("[notice] " + AuditText.ListLine(report).Trim()).ConfigureAwait(false);
            }

            return;
        }

        if (AuditFiles.Resolve(files, name) is not { } path)
        {
            await HeadlessErrorAsync(AuditText.NoSuchReport(name)).ConfigureAwait(false);
            return;
        }

        if (AuditFiles.ReadWhole(files, path) is not { } text)
        {
            await HeadlessErrorAsync(AuditText.ReportUnreadable(path)).ConfigureAwait(false);
            return;
        }

        foreach (string line in AuditReport.Normalize(text).TrimEnd('\n').Split('\n'))
        {
            await HeadlessNoticeLineAsync("[notice] " + line).ConfigureAwait(false);
        }
    }

    private Task HeadlessErrorAsync(string error) =>
        HeadlessLineAsync(HeadlessReplyPrefix + "[error] " + (error.StartsWith("Error: ", StringComparison.Ordinal) ? error[7..] : error));
}
