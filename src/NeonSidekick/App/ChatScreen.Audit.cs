using NeonSidekick.Audits;
using NeonSidekick.Diagnostics;
using NeonSidekick.Files;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Plans;
using NeonSidekick.Settings;
using NeonSidekick.UI;
using NeonSidekick.WebUi;

namespace NeonSidekick.App;

/// <summary>
/// The code audit on the screen (2026-10-18, <c>/code-audit</c>, the user's idea: <c>/plan gaps</c>' engine for code). A pass is one ordinary
/// turn on the main chat: its rules ride in the system prompt as a <see cref="PlanTurn"/> (<see cref="AuditSession.Turn"/>), its tools are the
/// read-only ones <see cref="AuditTools.Allowed"/> keeps plus <c>present_audit</c>, and after each turn <see cref="TakeAuditPass"/> decides on
/// the next pass, in <see cref="TakeGapPass"/>'s shape over the same <see cref="PlanGapLoop"/>. A stop that ended on its own puts the report
/// on the done pane (<see cref="AuditDoneMenu"/>) at the idle line; Esc, a message typed, a pass that reported nothing say how to go on.
/// 🔎 and the pass lead the strip while one runs.
/// </summary>
internal sealed partial class ChatScreen
{
    /// <summary>The /sys Tools tab's name for the group that holds <c>present_audit</c>.</summary>
    public const string AuditGroupName = "Audit";

    /// <summary>The strip with the audit's glyph and pass ahead of the rest while a pass runs (<c>🔎 2/5</c>). Pinned.</summary>
    public static string AuditStrip(string? pass, string strip)
    {
        ArgumentNullException.ThrowIfNull(strip);
        if (pass is null)
        {
            return strip;
        }

        string lead = AuditText.Glyph + " " + pass;
        return strip.Length == 0 ? lead : lead + GlyphSeparator + strip;
    }

    /// <summary>What a turn of the audit leaves to do once it ends: the next pass's message, or the report to put on the done pane.</summary>
    private sealed record AuditAfterTurn(string? Message, string? DonePath);

    /// <summary>A word's <see cref="AuditPath"/> when it names a file or folder in the working directory, else null (the root is the empty string).</summary>
    private string? ExistingAuditPath(string word)
    {
        if (AuditPath.Of(_files, word, out _) is not { } path || _files.Resolve(path, forWrite: false, out string full) != FileOutcome.Ok)
        {
            return null;
        }

        return File.Exists(full) || Directory.Exists(full) ? path : null;
    }

    /// <summary>The <c>/code-audit open|fix|resume</c> completion list: every report, newest first, with its status.</summary>
    private IReadOnlyList<CompletionItem> AuditChoices() =>
        AuditFiles.List(_files, _audit.Path).Select(r => new CompletionItem(r.Name, AuditText.CompletionNote(r))).ToList();

    /// <summary>
    /// Why an audit cannot start now, or null (2026-10-18): plan mode on, tools off, no assistant, or a turn that would not offer
    /// <c>read_file</c> (File tools off, or <c>read_file</c> off on <c>/tools</c>) — a pass could verify nothing.
    /// </summary>
    private string? AuditRefusal(AppSettingsData effective)
    {
        if (_plan.Active)
        {
            return AuditText.PlanningError;
        }

        if (!effective.LlmOfferTools)
        {
            return AuditText.NeedsToolsError;
        }

        if (_session.Assistant is null)
        {
            return NoAssistantError;
        }

        return effective.FileTools && Without(_fileTools, TurnDisabled(effective)).Any(t => t.Name == ReadFileTool.ToolName) ? null : AuditText.NeedsReadFileError;
    }

    /// <summary><c>/code-audit …</c>, idle only (Deferred under a reply; refused under a <c>/botchat</c> in <see cref="Pend"/>).</summary>
    private async Task<bool> HandleCodeAuditAsync(string args, IReadOnlyList<ImageAttachment> images, CancellationToken cancellationToken)
    {
        var parsed = AuditCommand.Parse(args, ExistingAuditPath);
        if (parsed.Error is { } error)
        {
            _transcript.Error(error);
            return false;
        }

        switch (parsed.Verb)
        {
            case AuditVerb.Open:
                await ShowAuditAsync(args, parsed.Name, cancellationToken).ConfigureAwait(false);
                return false;
            case AuditVerb.Fix:
                return await FixAuditAsync(parsed.Name, cancellationToken).ConfigureAwait(false);
            case AuditVerb.Resume:
                return await ResumeAuditAsync(parsed.Name, parsed.Reviewer, parsed.Passes, images, cancellationToken).ConfigureAwait(false);
            default:
                return await StartAuditAsync(parsed, images, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A new audit: the refusals, the scope resolved off the screen's thread under a Work scope (Esc stops a slow listing before any report
    /// is made), the report written, the loop started and pass 1 sent with the line's pictures.
    /// </summary>
    private async Task<bool> StartAuditAsync(AuditArguments parsed, IReadOnlyList<ImageAttachment> images, CancellationToken cancellationToken)
    {
        var effective = _effective();
        if (AuditRefusal(effective) is { } refused)
        {
            _transcript.Error(refused);
            return false;
        }

        if (await ResolveAuditScopeAsync(parsed.Scope!, cancellationToken).ConfigureAwait(false) is not { } scope)
        {
            return false;
        }

        var focus = parsed.Focus ?? AuditFocus.Normalize(effective.CodeAuditFocus);
        var reviewer = parsed.Reviewer ?? PlanReviewer.Model;
        var now = _time.GetLocalNow();
        string path = AuditFiles.Choose(_files, scope.NameLabel, now);
        var report = new AuditReport
        {
            Label = scope.Label,
            Header = new AuditHeader(AuditStatus.Auditing, AuditText.PassesLine(0, 0, 0, scope.Files.Count + scope.MoreFiles), scope.Words, focus, parsed.Brief,
                PlanText.ReviewerWord(reviewer), 1, now, now),
            CoverageScope = scope.Files.Count + scope.MoreFiles,
        };
        if (AuditFiles.Save(_files, path, report, now) is { } failed)
        {
            _transcript.Error(failed.StartsWith("Error: ", StringComparison.Ordinal) ? failed[7..] : failed);
            return false;
        }

        int limit = parsed.Passes ?? effective.CodeAuditPasses;
        var loop = _audit.Start(path, scope, focus, parsed.Brief, reviewer, limit, effective.CodeAuditCleanPasses);
        MarkAuditBaseline();
        images = AuditPictures(images);
        _transcript.Notice(AuditText.StartedNotice(scope.Label, scope.Files.Count, scope.MoreFiles, scope.Skipped, focus, loop.Limit, path, scope.Cut));
        DiagnosticLog.Info(AppCategory, $"code audit: started {path}, {scope.Files.Count} files, focus {AuditFocus.Label(focus)}, reviewer {PlanText.ReviewerWord(reviewer)}");
        return await SendPassAsync(BeginAuditPass(loop), images, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The scope resolved off the screen's thread under a spinner and the watch; null (said) when it was refused or stopped.</summary>
    private async Task<AuditScope?> ResolveAuditScopeAsync(AuditScopeRequest request, CancellationToken cancellationToken)
    {
        AuditScopeResult? result = null;
        bool stopped = await WaitUnderWatchAsync(token => _transcript.WithSpinnerAsync(AuditText.ListingSpinner, async () =>
        {
            result = await Task.Run(() => AuditScopes.Resolve(_files, _git, request, token), token).ConfigureAwait(false);
            return true;
        }), KeySource.IsTurnCancel, cancellationToken).ConfigureAwait(false);
        if (stopped || result is null)
        {
            _transcript.Notice(AuditText.ListingStoppedNotice);
            return null;
        }

        if (result.Error is { } error)
        {
            _transcript.Error(error);
            return null;
        }

        return result.Scope;
    }

    /// <summary>
    /// The report <paramref name="name"/> names, or the newest when none (said back, <paramref name="newest"/>); null, with the error said, when
    /// there is no such report.
    /// </summary>
    private string? AuditReportPath(string? name, out bool newest)
    {
        newest = name is null;
        if (name is null)
        {
            if (AuditFiles.List(_files, _audit.Path).FirstOrDefault(r => !r.Unreadable) is { } first)
            {
                return first.Path;
            }

            _transcript.Notice(AuditText.NoReportsNotice);
            return null;
        }

        if (AuditFiles.Resolve(_files, name) is { } path)
        {
            return path;
        }

        _transcript.Error(AuditText.NoSuchReport(name));
        return null;
    }

    /// <summary>
    /// <c>/code-audit resume [name] [reviewer] [passes]</c> and the pane's More passes…: a fresh loop on the report, its file kept and its status
    /// back to auditing, the scope, focus and brief from its header, the scope listed again so new files count.
    /// </summary>
    private async Task<bool> ResumeAuditAsync(string? name, PlanReviewer? reviewer, int? passes, IReadOnlyList<ImageAttachment> images, CancellationToken cancellationToken, bool said = true)
    {
        var effective = _effective();
        if (AuditRefusal(effective) is { } refused)
        {
            _transcript.Error(refused);
            return false;
        }

        if (AuditReportPath(name, out bool newest) is not { } path)
        {
            return false;
        }

        if (AuditFiles.Read(_files, path) is not { } report)
        {
            _transcript.Error(AuditText.ReportUnreadable(path));
            return false;
        }

        var (request, problem) = AuditCommand.ScopeFromHeader(report.Header.Scope, ExistingAuditPath);
        if (problem is not null)
        {
            _transcript.Error(problem);
            return false;
        }

        if (await ResolveAuditScopeAsync(request!, cancellationToken).ConfigureAwait(false) is not { } scope)
        {
            return false;
        }

        // The report as it stands after the listing (2026-10-20, a review: the copy read before it was saved back, so a finding ticked or edited in
        // an editor while the spinner ran was lost).
        if (AuditFiles.Read(_files, path) is not { } fresh)
        {
            _transcript.Error(AuditText.ReportUnreadable(path));
            return false;
        }

        report = fresh;
        var who = reviewer ?? PlanText.ParseReviewer(report.Header.Reviewer) ?? PlanReviewer.Model;
        report.Header = report.Header with { Status = AuditStatus.Auditing, Reviewer = PlanText.ReviewerWord(who) };
        if (AuditFiles.Save(_files, path, report, _time.GetLocalNow()) is { } failed)
        {
            _transcript.Error(failed.StartsWith("Error: ", StringComparison.Ordinal) ? failed[7..] : failed);
            return false;
        }

        var loop = _audit.Start(path, scope, report.Header.Focus, report.Header.Brief, who, passes ?? effective.CodeAuditPasses, effective.CodeAuditCleanPasses);
        MarkAuditBaseline();
        images = AuditPictures(images);
        if (said)
        {
            _transcript.Notice(AuditText.ResumedNotice(System.IO.Path.GetFileNameWithoutExtension(path), newest));
        }

        if (scope.Cut)
        {
            _transcript.Notice(AuditText.ScopeCutNotice);
        }

        DiagnosticLog.Info(AppCategory, $"code audit: resumed {path}, {scope.Files.Count} files, reviewer {PlanText.ReviewerWord(who)}");
        return await SendPassAsync(BeginAuditPass(loop), images, cancellationToken).ConfigureAwait(false);
    }

    /// <summary><c>/code-audit open [name]</c>: the reports' list, or one report's text, as lines (the info pane; a card on the page).</summary>
    private async Task ShowAuditAsync(string args, string? name, CancellationToken cancellationToken)
    {
        if (name is null)
        {
            var reports = AuditFiles.List(_files, _audit.Path);
            if (reports.Count == 0)
            {
                _transcript.Notice(AuditText.NoReportsNotice);
                return;
            }

            await ShowLinesAsync(TreeText.PaneLabel(AuditText.Word, args), reports.Select(AuditText.ListLine).ToList(), _transcript, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (AuditFiles.Resolve(_files, name) is not { } path)
        {
            _transcript.Error(AuditText.NoSuchReport(name));
            return;
        }

        if (AuditFiles.ReadWhole(_files, path) is not { } text)
        {
            _transcript.Error(AuditText.ReportUnreadable(path));
            return;
        }

        await ShowLinesAsync(TreeText.PaneLabel(AuditText.Word, args), AuditReport.Normalize(text).TrimEnd('\n').Split('\n'), _transcript, cancellationToken).ConfigureAwait(false);
    }

    /// <summary><c>/code-audit fix [name]</c>: plan mode on the report's open findings, its status fixing.</summary>
    private async Task<bool> FixAuditAsync(string? name, CancellationToken cancellationToken)
    {
        if (_plan.Active)
        {
            _transcript.Error(AuditText.PlanningError);
            return false;
        }

        if (AuditReportPath(name, out bool newest) is not { } path)
        {
            return false;
        }

        return await FixAuditReportAsync(path, newest, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Fix via plan: the report marked fixing, then plan mode entered on its open findings (<see cref="EnterPlanAsync"/>).</summary>
    private async Task<bool> FixAuditReportAsync(string path, bool newest, CancellationToken cancellationToken)
    {
        if (AuditFiles.Read(_files, path) is not { } report)
        {
            _transcript.Error(AuditText.ReportUnreadable(path));
            return false;
        }

        string name = System.IO.Path.GetFileNameWithoutExtension(path);
        var open = report.Open.Select(f => f.Id).ToList();
        if (open.Count == 0)
        {
            _transcript.Error(AuditText.NoOpenFindings(name));
            return false;
        }

        if (!_effective().LlmOfferTools || _session.Assistant is null)
        {
            _transcript.Error(!_effective().LlmOfferTools ? PlanText.NeedsToolsError : NoAssistantError);
            return false;
        }

        report.Header = report.Header with { Status = AuditStatus.Fixing };
        if (AuditFiles.Save(_files, path, report, _time.GetLocalNow()) is { } failed)
        {
            _transcript.Warning(failed);
        }

        _transcript.Notice(AuditText.FixingNotice(name, newest));
        return await EnterPlanAsync(AuditText.FixRequirement(path, open), [], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The next pass begun: the loop's pass over the report's findings and retractions (the stall body), its meter, a short limit's notice once,
    /// and the pass's message with its own reviewer (the model when no advisor is offered) and focus.
    /// </summary>
    private string BeginAuditPass(PlanGapLoop loop) => BeginAuditPass(loop, AuditFiles.Read(_audit.Files, _audit.Path!)?.StallBody());

    /// <summary><see cref="BeginAuditPass(PlanGapLoop)"/> over the stall body the caller read just now (2026-10-20, a review: read again for each pass).</summary>
    private string BeginAuditPass(PlanGapLoop loop, string? body)
    {
        bool offered = AdvisorPassOffered();
        string path = _audit.Path!;
        loop.BeginPass(body, PlanMeter(), offered);
        if (loop.Pass == 1 && loop.CleanLowered)
        {
            _transcript.Notice(AuditText.CleanLoweredNotice(loop.Limit, loop.CleanAsked));
        }

        var reviewer = offered ? loop.PassReviewer : PlanReviewer.Model;
        string focus = AuditFocus.PassFocus(_audit.Focus, loop.Confirmation).Short;
        return AuditText.PassMessage(_audit.Scope!.Label, path, loop.Pass, loop.Limit, reviewer, focus, loop.CleanStreak);
    }

    /// <summary>
    /// A pass's message sent as a turn (a gap pass's too), echoed since no line was typed. An audit's pass that never reached a turn (2026-10-20, a
    /// review: <see cref="RunMessageAsync"/> returns before one when there is no assistant or the model cannot see a picture, and the audit stayed
    /// mid-pass, every later turn narrowed to its tools and judged as its pass) is recorded and the audit stopped.
    /// </summary>
    private async Task<bool> SendPassAsync(string message, IReadOnlyList<ImageAttachment> images, CancellationToken cancellationToken)
    {
        _transcript.User(message);
        _passMessage = message;
        var loop = _audit.Running;
        int pass = loop?.Pass ?? 0;
        long judged = _auditJudged;
        bool exit = await RunMessageAsync(message, images, cancellationToken).ConfigureAwait(false);
        if (loop is not null && ReferenceEquals(_audit.Running, loop) && loop.Pass == pass && _auditJudged == judged)
        {
            _transcript.Notice(AuditText.PassNotSentNotice(_audit.Name));
            loop.Next(false, false, null, PlanMeter());
            EndAudit(loop, PlanGapStop.Stopped);
        }

        return exit;
    }

    /// <summary>How many passes <see cref="TakeAuditPass"/> has judged: a pass's send tells by it whether its turn ran.</summary>
    private long _auditJudged;

    /// <summary>The lines queued, held and sent from the page as the audit began (<see cref="MarkAuditBaseline"/>).</summary>
    private (long Typed, long Pended, long Sent) _auditBaseline;

    /// <summary>
    /// The lines so far as an audit begins or resumes (2026-10-20, a review, the Ralph loop's rule: the queue's count stopped it after pass 1 on a
    /// line waiting before it began, while under Queue mode manual a typed line, and a page's message, never stopped it).
    /// </summary>
    private void MarkAuditBaseline() => _auditBaseline = (_queue.Added, PendedCount, _webUi.LinesAdded);

    /// <summary>A message typed on the PC, held, or sent from the page since the audit began: it stops the audit after its pass.</summary>
    private bool AuditUserSpoke() => _queue.Added != _auditBaseline.Typed || PendedCount != _auditBaseline.Pended || _webUi.LinesAdded != _auditBaseline.Sent;

    /// <summary>The line's pictures, less those the model cannot see (said): a picture it cannot see would stop the pass before it began.</summary>
    private IReadOnlyList<ImageAttachment> AuditPictures(IReadOnlyList<ImageAttachment> images)
    {
        if (ModelSeesPictures() || images.All(i => i.IsDocument))
        {
            return images;
        }

        _transcript.Notice(AuditText.PicturesUnseenNotice);
        return images.Where(i => i.IsDocument).ToList();
    }

    /// <summary>
    /// The pass message <see cref="SendPassAsync"/> is handing to <see cref="RunMessageAsync"/>, taken (by reference) at its top: a pass withdrawn
    /// by Esc puts no draft back (2026-10-19, the second review: the audit had stopped, and Enter on the restored row sent "Code audit pass 3…"
    /// as an ordinary turn with no audit running; the Ralph loop's pass had this rule already).
    /// </summary>
    private string? _passMessage;

    /// <summary>
    /// After a turn (<see cref="RunMessageAsync"/>, right after <see cref="TakeGapPass"/>, for every outcome) while an audit runs: the coverage
    /// merged into the sidecar, the pass judged (<see cref="PlanGapLoop.Next"/>), said, then the next pass or the audit's end. Null when nothing follows.
    /// </summary>
    private AuditAfterTurn? TakeAuditPass(TurnOutcome outcome)
    {
        if (_audit.Running is not { Pending: false } loop || _audit.Path is not { } path)
        {
            return null;
        }

        _auditJudged++;
        if (_audit.Moved)
        {
            // The working directory changed under it (2026-10-19): the next pass would read another folder's code. Ended in its own folder's report,
            // the pass recorded first (2026-10-20, a review: the summary left it out while the report counted it).
            _transcript.Notice(AuditText.WorkdirMovedNotice(_audit.Name, _audit.Root!));
            DiagnosticLog.Info(AppCategory, $"code audit: stopped {path}: the working directory moved from {_audit.Root}");
            loop.Next(false, false, null, PlanMeter());
            return EndAudit(loop, PlanGapStop.Stopped, moved: true);
        }

        bool onItsOwn = outcome == TurnOutcome.Continue && !_lastTurnCancelled && !_lastTurnFailed;
        // The coverage worked out once for the merge, the notice and the end (2026-10-20, a review: three times, each a read of the sidecar and a
        // look at every scope file).
        var coverage = _audit.Coverage();
        if (AuditFiles.MergeCoverage(_audit.Files, path, coverage.Viewed) is { } problem)
        {
            DiagnosticLog.Info(AppCategory, "code audit: " + problem);
        }

        string? body = AuditFiles.Read(_audit.Files, path)?.StallBody();
        var stop = loop.Next(onItsOwn, AuditUserSpoke(), body, PlanMeter());
        var cost = loop.History.Count > 0 ? loop.History[^1] : null;
        bool advisorAsked = _lastTrace is { } trace && trace.ToolNames.Contains(NeonAdvisorTool.ToolName, StringComparer.Ordinal);
        DiagnosticLog.Info(AppCategory, AuditText.PassLogLine(loop.Pass, loop.PassGaps.Count, loop.PassMinor.Count, loop.PassClean, advisorAsked, cost));
        if (stop is not { } reason)
        {
            _transcript.Notice(loop.PassGaps.Count > 0
                ? AuditText.PassDoneNotice(loop.Pass, loop.Limit, loop.PassGaps.Count, loop.PassMinor.Count, coverage.Read, coverage.Scope)
                : AuditText.CleanPassNotice(loop.Pass, loop.Limit, loop.CleanCount, loop.CleanPasses));
            return new AuditAfterTurn(BeginAuditPass(loop, body), null);
        }

        return EndAudit(loop, reason, coverage: coverage);
    }

    /// <summary>
    /// An audit over: the summary of its passes, the report's status and passes line, the stop's notice; for a stop that ended on its own
    /// (converged, limit, stalled, unconfirmed) the report back on the done pane at the idle line. The loop is forgotten. <paramref name="moved"/>
    /// (the working directory changed, its own notice said): no stop line and no pane, the report written in the audit's own folder.
    /// </summary>
    private AuditAfterTurn? EndAudit(PlanGapLoop loop, PlanGapStop reason, bool moved = false, AuditCoverage? coverage = null)
    {
        string path = _audit.Path!;
        string name = _audit.Name;
        var files = _audit.Files;
        foreach (string line in AuditText.ReviewSummaryLines(loop.History, reason, loop.CleanPasses, _audit.Focus))
        {
            _transcript.Notice(line);
        }

        coverage ??= _audit.Coverage();
        int clean = reason == PlanGapStop.Converged ? loop.CleanPasses : loop.PassClean ? loop.CleanCount : 0;
        if (AuditFiles.Read(files, path) is { } report)
        {
            report.CoverageRead = coverage.Read;
            report.CoverageScope = coverage.Scope;
            report.Header = report.Header with
            {
                Status = reason == PlanGapStop.Converged ? AuditStatus.Converged : AuditStatus.Stopped,
                Passes = AuditText.PassesLine(loop.Pass, clean, coverage.Read, coverage.Scope, reason),
            };
            if (AuditFiles.Save(files, path, report, _time.GetLocalNow()) is { } failed)
            {
                _transcript.Warning(failed);
            }
        }

        _audit.Stop();
        if (moved)
        {
            return null;
        }

        _transcript.Notice(reason == PlanGapStop.Converged
            ? AuditText.ConvergedNotice(name, loop.Pass, loop.CleanPasses, coverage.Read, coverage.Scope)
            : AuditText.StopNotice(reason, name, loop.Pass));
        return reason is PlanGapStop.Converged or PlanGapStop.Limit or PlanGapStop.Stalled or PlanGapStop.Unconfirmed ? new AuditAfterTurn(null, path) : null;
    }

    /// <summary>
    /// An audit stopped with the conversation (2026-10-18: <c>/clear</c>, <c>/new</c>, a session load, a profile switch, a pane's hand-over):
    /// said and forgotten, nothing written (the working directory may be another profile's now); the report lists as stopped.
    /// </summary>
    private void StopAuditOnReset()
    {
        if (_audit.Active)
        {
            _transcript.Notice(AuditText.LeftOnResetNotice(_audit.Name));
            _audit.Stop();
        }
    }

    /// <summary>
    /// The done pane at the idle line: the open findings as a short table first (most severe first, at most 40), then the pane through
    /// <see cref="RaceAsync{T}"/> (the page too from a web command), the choice acted on. With no pane to ask on, the no-pane lines.
    /// </summary>
    private async Task<bool> AskAuditDoneAsync(string path, CancellationToken cancellationToken)
    {
        if (AuditFiles.Read(_files, path) is not { } report)
        {
            _transcript.Error(AuditText.ReportUnreadable(path));
            return false;
        }

        string name = System.IO.Path.GetFileNameWithoutExtension(path);
        foreach (var (severity, line) in AuditText.TableLines(report.Open.ToList(), path))
        {
            _transcript.Notice(line, ToneOf(severity));
        }

        bool hasFix = report.OpenCount > 0;
        if (!_pane.Enabled && !FromWebNow)
        {
            foreach (string line in AuditText.NoPaneLines(name, hasFix))
            {
                _transcript.Notice(line);
            }

            return false;
        }

        var done = new AuditDone(name, path, AuditText.DoneTitle(name, report.OpenBySeverity(), report.CoverageRead, report.CoverageScope),
            AuditText.DoneCaption(report.OpenCount, report.SettledCount, path), hasFix);
        var reviewer = PlanText.ParseReviewer(report.Header.Reviewer) ?? PlanReviewer.Model;
        var outcome = await RaceAsync<AuditVerdict?>(AuditSpec(done, report), async token => await _auditMenu.AskAsync(done, token).ConfigureAwait(false),
            answer => AuditVerdictOf(answer, reviewer), "The code audit pane failed: ", cancellationToken).ConfigureAwait(false);
        var verdict = outcome.Value ?? new AuditVerdict(AuditChoice.Keep);
        DiagnosticLog.Info(AppCategory, "code audit: " + verdict.Choice.ToString().ToLowerInvariant() + (verdict.Reviewer is { } r ? " (" + PlanText.ReviewerWord(r) + ")" : "") + " " + path);
        switch (verdict.Choice)
        {
            case AuditChoice.Fix when hasFix:
                return await FixAuditReportAsync(path, newest: false, cancellationToken).ConfigureAwait(false);
            case AuditChoice.More:
                return await ResumeAuditAsync(name, verdict.Reviewer ?? reviewer, null, [], cancellationToken, said: false).ConfigureAwait(false);
            case AuditChoice.Discard:
                if (!await ConfirmAsync(AuditText.DiscardQuestion(path), cancellationToken).ConfigureAwait(false))
                {
                    _transcript.Notice(AuditText.KeptNotice(name));
                    return false;
                }

                var deleted = AuditFiles.Discard(_files, path);
                if (deleted.Outcome != FileOutcome.Ok)
                {
                    _transcript.Error(FileText.Error(deleted.Outcome, path, "delete", deleted.Detail));
                    return false;
                }

                _transcript.Notice(AuditText.DiscardedNotice(path, deleted.Way == DeleteWay.Recycled));
                return false;
            default:
                _transcript.Notice(AuditText.KeptNotice(name));
                return false;
        }
    }

    /// <summary>The page's form of the done pane: the counts line, the report's text, and the same choices as the PC's rows (no fix with nothing open).</summary>
    internal static WebAskSpec AuditSpec(AuditDone done, AuditReport report)
    {
        ArgumentNullException.ThrowIfNull(done);
        ArgumentNullException.ThrowIfNull(report);
        return new WebAskSpec("audit", done.Title, AuditDoneMenu.Entries(done.HasFix).Select(e => new WebChoice(e.Id, e.Row)).ToList(),
            Body: done.Caption, Markdown: report.RenderBody());
    }

    /// <summary>The page's answer as the pane would give it: More passes takes the last run's reviewer (the reviewer page stays the PC's).</summary>
    internal static AuditVerdict AuditVerdictOf(WebAnswerRequest answer, PlanReviewer reviewer) => answer.Choice switch
    {
        "fix" => new AuditVerdict(AuditChoice.Fix),
        "more" => new AuditVerdict(AuditChoice.More, reviewer),
        "discard" => new AuditVerdict(AuditChoice.Discard),
        _ => new AuditVerdict(AuditChoice.Keep),
    };

    /// <summary>A severity in the theme's existing roles: critical and high as errors, medium as warnings, low dim.</summary>
    public static NoticeTone ToneOf(AuditSeverity severity) => severity switch
    {
        AuditSeverity.Critical or AuditSeverity.High => NoticeTone.Bad,
        AuditSeverity.Medium => NoticeTone.Warn,
        _ => NoticeTone.Dim,
    };

    /// <summary><c>present_audit</c>'s seam, on the turn task: each call's changes as 🔎 notices (its call line is never drawn: <see cref="QuietTools"/>).</summary>
    private void OnAuditReported(AuditCallReport report)
    {
        foreach (var finding in report.Added)
        {
            _transcript.Notice(AuditText.FindingNotice(report.Pass, finding), ToneOf(finding.Severity));
        }

        foreach (var finding in report.Reopened)
        {
            _transcript.Notice(AuditText.ReopenedNotice(report.Pass, finding), ToneOf(finding.Severity));
        }

        foreach (var (id, reason) in report.Retracted)
        {
            _transcript.Notice(AuditText.RetractedNotice(report.Pass, id, reason), NoticeTone.Dim);
        }

        foreach (var finding in report.Revised)
        {
            _transcript.Notice(AuditText.RevisedNotice(report.Pass, finding), ToneOf(finding.Severity));
        }

        if (report.Minor > 0)
        {
            _transcript.Notice(AuditText.MinorNotice(report.Pass, report.Minor), NoticeTone.Dim);
        }

        foreach (string question in report.Questions)
        {
            _transcript.Notice(AuditText.QuestionNotice(report.Pass, question));
        }

        if (report.Clean)
        {
            _transcript.Notice(AuditText.CleanNotice(report.Pass, report.CleanCount, report.CleanNeeded, report.Checked));
        }
    }

    /// <summary>
    /// Plan mode on for <paramref name="requirement"/> and the requirement sent (2026-10-18, out of <see cref="HandlePlanAsync"/>'s Enter so
    /// <c>Fix via plan</c> and <c>/code-audit fix</c> enter it the same way): the tools and assistant checks, the resets, the chrome, the notice.
    /// </summary>
    private async Task<bool> EnterPlanAsync(string requirement, IReadOnlyList<ImageAttachment> images, CancellationToken cancellationToken)
    {
        if (!_effective().LlmOfferTools)
        {
            _transcript.Error(PlanText.NeedsToolsError);
            return false;
        }

        if (_session.Assistant is null)
        {
            _transcript.Error(NoAssistantError);
            return false;
        }

        _plan.Enter(requirement);
        _executingPlan = null;
        _planDraftReply = null;
        SyncPlanChrome();
        SavePlanState();
        _transcript.Notice(PlanText.EnteredNotice);
        _transcript.User(_plan.Requirement);
        return await RunMessageAsync(_plan.Requirement, images, cancellationToken).ConfigureAwait(false);
    }
}
