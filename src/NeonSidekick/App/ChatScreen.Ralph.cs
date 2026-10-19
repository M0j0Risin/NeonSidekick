using System.Diagnostics;
using NeonSidekick.Diagnostics;
using NeonSidekick.Files;
using NeonSidekick.Git;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Plans;
using NeonSidekick.Sessions;
using NeonSidekick.Settings;
using NeonSidekick.Shell;
using NeonSidekick.UI;
using NeonSidekick.WebUi;

namespace NeonSidekick.App;

// ── The Ralph loop (2026-10-18, the plan at ~/.claude/plans/ok-we-re-in-plan-shimmering-canyon.md) ──

/// <summary>
/// The Ralph loop on the screen (2026-10-18, the user's ask: Geoffrey Huntley's technique as a third way to carry out an approved plan, beside
/// Approve &amp; run and Approve, clear context &amp; run): many short passes, each a fresh conversation doing one step (<see cref="ResetForPass"/>, a
/// light reset that keeps the queue, the picture strip, the allows and the advisors' links), held together by the plan file and the app's
/// log beside it (<see cref="RalphLog"/>), the app — not the model — deciding whether a step stands: its verify command (<see cref="IRalphVerifier"/>),
/// the evidence (<see cref="RalphChanges"/>), a review, and a checkpoint every N steps when asked for. A flat driver (<see cref="RunRalphAsync"/>):
/// each pass is one <see cref="RunMessageAsync"/>, its directive riding the turn's own slot (<see cref="TurnToolInputs.Pass"/>, every tool kept) with
/// <c>finish_step</c>; nothing chains through the turn. A message typed during a pass pauses the loop after the pass is judged; Esc, the page's Stop
/// or a withdrawal stops it; <c>/plan ralph</c> resumes.
/// </summary>
internal sealed partial class ChatScreen
{
    /// <summary>The /sys Tools tab's name for the group that holds <c>finish_step</c> during a pass.</summary>
    public const string RalphGroupName = "Ralph";

    /// <summary>The loop running now; null between loops (a paused one is <see cref="_executingPlan"/> with <see cref="StoredPlan.Ralph"/>).</summary>
    private RalphLoop? _ralph;

    /// <summary>The pass under way's directive and tool for the turn's slot; null outside a pass.</summary>
    private PlanTurn? _ralphTurn;

    /// <summary>The pass under way's short line and session title: what its row logs instead of the whole message.</summary>
    private (string Line, string Title)? _ralphPass;

    /// <summary>The strip's text while a loop runs (one volatile string: <see cref="WebStatusNow"/> reads it off a request's thread).</summary>
    private volatile string? _ralphStrip;

    private readonly FinishStepTool _finishStep;
    private readonly IRalphVerifier _ralphVerifier;

    /// <summary>Whether a lock's owner still runs (a seam for the tests: by default the process and its start time).</summary>
    internal Func<RalphLog.LockOwner, bool> RalphLockAlive { get; set; } = OwnerAlive;

    private static readonly Lazy<RalphLog.LockOwner> s_lockOwner = new(() =>
    {
        using var me = Process.GetCurrentProcess();
        return new RalphLog.LockOwner(Environment.ProcessId, new DateTimeOffset(me.StartTime.ToUniversalTime(), TimeSpan.Zero));
    });

    private static bool OwnerAlive(RalphLog.LockOwner owner)
    {
        try
        {
            using var process = Process.GetProcessById(owner.ProcessId);
            return Math.Abs((process.StartTime.ToUniversalTime() - owner.Started.UtcDateTime).TotalSeconds) < 2;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>A pass is under way: the turn's slot, the guards in <see cref="RunMessageAsync"/>, the session row's title.</summary>
    private bool InRalphPass => _ralph is { InPass: true };

    /// <summary>The turn slot of a pass under way (<see cref="TurnToolInputs.Pass"/>), else null.</summary>
    private PlanTurn? RalphPassTurn() => InRalphPass ? _ralphTurn : null;

    /// <summary>Whether a pass may wait on the user: only while the checkpoint is on (an unattended loop never stops on a question).</summary>
    private bool RalphAsksBarred() => _ralph is { InPass: true, Snapshot.Checkpoint: 0 };

    /// <summary>The strip with the loop's glyph and place ahead of the rest while a loop runs. Pinned.</summary>
    public static string RalphStrip(string? text, string strip)
    {
        ArgumentNullException.ThrowIfNull(strip);
        return text is null ? strip : strip.Length == 0 ? text : text + GlyphSeparator + strip;
    }

    /// <summary>The <c>l</c> button on the plans picker: only outside plan mode (the plan being planned goes through its pane).</summary>
    private bool RalphButtonOffered() => !_plan.Active;

    /// <summary>Each listed plan's Ralph line from its log, read once as the picker opens; null with none.</summary>
    private IReadOnlyDictionary<string, string>? RalphLines(IReadOnlyList<PlanListing> plans)
    {
        var lines = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var plan in plans.Where(p => !p.Unreadable))
        {
            if (RalphLog.Read(_files, plan.Path) is { Entries.Count: > 0 } log)
            {
                lines[plan.Path] = RalphText.PickerLine(RalphLog.PassCount(log), plan.Done, plan.Total, log.Head?.Stopped);
            }
        }

        return lines.Count == 0 ? null : lines;
    }

    /// <summary>A plan picked with <c>l</c> (or Enter in Ralph mode): <c>/plan ralph &lt;name&gt;</c>'s path.</summary>
    private Task<bool> RalphPickedAsync(string path, CancellationToken cancellationToken) => RalphNamedAsync(path, null, cancellationToken);

    /// <summary>The 🔁 line a recorded <c>finish_step</c> writes (on the turn task, the tool's seam).</summary>
    private void RalphReported(FinishReport report) =>
        _transcript.Notice(RalphText.Glyph + " finish_step: " + report.Outcome.ToString().ToLowerInvariant() + " — " + Truncate(report.Summary, 200));

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    /// <summary>The known context window in characters, 0 when unknown: what a pass's message measures the plan against.</summary>
    private int RalphWindowChars() => (_session.ContextLength?.Tokens ?? 0) * ConversationCompactor.CharsPerToken;

    /// <summary>The most characters of a plan a pass sends whole (later on 2026-10-18, <see cref="RalphText.InlineLimit"/>): the fit's and the gap pass's measure.</summary>
    private int RalphInlineLimit() => RalphText.InlineLimit(RalphWindowChars());

    // ── /plan ralph ────────────────────────────────────────────────────────

    /// <summary>
    /// <c>/plan ralph [name] [passes]</c> outside plan mode: bare, the paused loop resumed (else the picker in Ralph mode, a notice first); with a
    /// name, that plan run (the picker after the error for a name that is no plan); <paramref name="passes"/> overrides the cap for this run.
    /// </summary>
    private async Task<bool> HandleRalphAsync(string name, int? passes, CancellationToken cancellationToken)
    {
        if (_plan.Active)
        {
            _transcript.Error(RalphText.WhilePlanningError);
            return false;
        }

        if (name.Length == 0)
        {
            if (_executingPlan is { Ralph: true, Path: { } paused })
            {
                return await ResumeRalphAsync(paused, passes, cancellationToken).ConfigureAwait(false);
            }

            _transcript.Notice(RalphText.NothingPausedNotice);
            return await ListPlansAsync(PlanPickMode.Ralph, cancellationToken, asNotices: FromWebNow && !_pane.Enabled).ConfigureAwait(false);
        }

        if (PlanFiles.Resolve(_files, name) is not { } path)
        {
            _transcript.Error(PlanText.NoSuchPlanError(name));
            return await ListPlansAsync(PlanPickMode.Ralph, cancellationToken, asNotices: FromWebNow).ConfigureAwait(false);
        }

        return await RalphNamedAsync(path, passes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>/plan ralph &lt;name&gt;</c> (and the picker's <c>l</c>): a plan not done or canceled, its checks confirmed on both sides (a draft approved
    /// by the same yes), the baseline, then the loop. A paused loop on the same plan resumes instead. Since later on 2026-10-18 the same question
    /// names the plan's fit problems (<see cref="PlanFit.Shape"/>, the check line already saying <c>unchecked</c>), so it is asked once.
    /// </summary>
    private async Task<bool> RalphNamedAsync(string path, int? passes, CancellationToken cancellationToken)
    {
        if (_executingPlan is { Ralph: true, Path: { } paused } && string.Equals(paused, path, StringComparison.Ordinal))
        {
            return await ResumeRalphAsync(path, passes, cancellationToken).ConfigureAwait(false);
        }

        if (RalphRefusal() is { } refused)
        {
            _transcript.Error(refused);
            return false;
        }

        if (PlanFiles.ReadWhole(_files, path) is not { } text)
        {
            _transcript.Error(PlanText.PickerUnreadableError(path));
            return false;
        }

        var header = PlanDocument.TryParse(text);
        if (header is { Status: PlanStatus.Done or PlanStatus.Cancelled } finished)
        {
            _transcript.Error(RalphText.NotRunnableError(path, finished.Status));
            return false;
        }

        var effective = _effective();
        string body = PlanDocument.Body(text);
        string checks = RalphText.PaneCheckLine(header is null ? null : new PlanChecks(header.Verify, header.Final), RalphReviewerMode.Resolve(effective), effective.RalphLoopTokenBudget, CommandPolicy.Resolve(effective) == CommandPolicyMode.Ask);
        var problems = PlanFit.Shape(body, RalphInlineLimit());
        string question = header is null or { Status: PlanStatus.Draft } ? RalphText.ConfirmDraftQuestion(path, checks, problems) : RalphText.ConfirmQuestion(path, checks, problems);
        if (!await RalphConfirmAsync(question, cancellationToken).ConfigureAwait(false))
        {
            _transcript.Notice(KeptNotice);
            return false;
        }

        string requirement = header is { Requirement.Length: > 0 } ? header.Requirement : PlanDocument.FirstHeading(body) ?? path;
        return await BeginRalphAsync(path, body, header?.Verify, header?.Final, requirement, passes, resume: false, fromPlanning: false, cancellationToken, fitAsked: true).ConfigureAwait(false);
    }

    /// <summary>The paused loop again (<c>/plan ralph</c>): no confirm; the file read again (a plan done meanwhile clears the state), the baseline, the loop.</summary>
    private async Task<bool> ResumeRalphAsync(string path, int? passes, CancellationToken cancellationToken)
    {
        if (RalphRefusal() is { } refused)
        {
            _transcript.Error(refused);
            return false;
        }

        var stored = _executingPlan!;
        if (PlanFiles.ReadWhole(_files, path) is not { } text)
        {
            _transcript.Notice(RalphText.StopNotice(RalphStop.PlanGone, path, 0, 0));
            _executingPlan = null;
            SavePlanState();
            return false;
        }

        string body = PlanDocument.Body(text);
        if (PlanSteps.FirstUnticked(PlanSteps.Parse(body)) is null && stored.Final is null)
        {
            // Every step ticked while it was paused: nothing left to run.
            var counts = PlanSteps.Counts(PlanSteps.Parse(body));
            PlanFiles.MarkFile(_files, path, PlanStatus.Done, _time.GetLocalNow(), stored.Requirement, counts);
            _transcript.Notice(PlanText.DoneNotice(path));
            _executingPlan = null;
            SavePlanState();
            return false;
        }

        return await BeginRalphAsync(path, body, stored.Verify, stored.Final, stored.Requirement, passes, resume: true, fromPlanning: false, cancellationToken, stored).ConfigureAwait(false);
    }

    /// <summary>What refuses a loop's start: no assistant, tools off, an audit running.</summary>
    private string? RalphRefusal() =>
        _session.Assistant is null ? NoAssistantError
        : !_effective().LlmOfferTools ? RalphText.NeedsToolsError
        : _audit.Active ? RalphText.UnderAuditError
        : null;

    /// <summary>A yes asked on the PC and the page at once (the idle line's race), the PC alone without the Web UI; true for a yes.</summary>
    private async Task<bool> RalphConfirmAsync(string question, CancellationToken cancellationToken)
    {
        if (!_menu.CanShowMenus() && !_effective().WebUi)
        {
            _transcript.Notice(TypedConfirm(question));
            var answer = await _input.ReadAsync(remember: false, allowEmpty: true, cancellationToken: cancellationToken).ConfigureAwait(false);
            return answer is InputResult.Submitted submitted && IsYes(submitted.Text);
        }

        var outcome = await RaceAsync<bool>(ConfirmSpec(question), async token => await _menu.ConfirmAsync(question, token).ConfigureAwait(false), a => a.Choice == "yes",
            "The Ralph loop's confirm pane failed: ", cancellationToken, bothSides: true).ConfigureAwait(false);
        return outcome.Value;
    }

    /// <summary>
    /// The plan being planned approved for a Ralph loop (the pane's row, <c>/plan approve --ralph</c>, an idle-line verdict): the baseline while plan
    /// mode is still on, then the loop. A plan with no steps is refused and stays refinable.
    /// </summary>
    private async Task<bool> ApproveRalphAsync(int? passes, CancellationToken cancellationToken)
    {
        if (_plan.Path is not { } path)
        {
            _transcript.Error(PlanText.NothingPresentedError);
            return false;
        }

        if (RalphRefusal() is { } refused)
        {
            _plan.ClearApproval();
            _transcript.Error(refused);
            return false;
        }

        if (PlanFiles.ReadWhole(_files, path) is not { } text)
        {
            _plan.ClearApproval();
            _transcript.Error(PlanText.PlanUnreadableError(path));
            return false;
        }

        var header = PlanDocument.TryParse(text);
        return await BeginRalphAsync(path, PlanDocument.Body(text), header?.Verify, header?.Final, _plan.Requirement, passes, resume: false, fromPlanning: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A loop begun on <paramref name="path"/>: no steps refused; the lock taken; the baseline (the verify and the final command) run, a failure asking
    /// "start anyway?" (yes: final-only checking; no: nothing started); the plan marked approved (plan mode left when it came from planning); the run
    /// snapshotted, said and saved; then <see cref="RunRalphAsync"/>. A plan that may not suit a loop (later on 2026-10-18, <see cref="PlanFit"/>) is
    /// asked about first, before the lock: never on a resume, nor when <paramref name="fitAsked"/> (<c>/plan ralph &lt;name&gt;</c>'s confirm named them).
    /// </summary>
    private async Task<bool> BeginRalphAsync(string path, string body, string? verify, string? final, string requirement, int? passes, bool resume, bool fromPlanning, CancellationToken cancellationToken, StoredPlan? stored = null, bool fitAsked = false)
    {
        var steps = PlanSteps.Parse(body);
        if (!PlanSteps.Leaves(steps).Any())
        {
            _plan.ClearApproval();
            _transcript.Error(RalphText.NoStepsError);
            return false;
        }

        if (!resume && !fitAsked
            && PlanFit.Problems(body, new PlanChecks(verify, final), RalphReviewerMode.Resolve(_effective()) != RalphReviewer.Off, RalphInlineLimit()) is { Count: > 0 } problems
            && !await RalphConfirmAsync(RalphText.FitQuestion(problems), cancellationToken).ConfigureAwait(false))
        {
            // The plan stays presented, as on the other refusals: /plan gaps tightens it, /plan approve --ralph asks again.
            _plan.ClearApproval();
            _transcript.Notice(RalphText.FitDeclinedNotice);
            return false;
        }

        if (!RalphLog.TryLock(_files, path, s_lockOwner.Value, RalphLockAlive))
        {
            _plan.ClearApproval();
            _transcript.Error(RalphText.LockedError(path));
            return false;
        }

        var effective = _effective();
        var shell = stored?.Shell is { } storedShell && ShellKinds.TryParse(storedShell, out var kind) ? kind : ShellKinds.Resolve(effective);
        bool finalOnly = stored?.FinalOnly ?? false;
        bool noChecks = false;
        // The baseline (later the per-step checks): the policy off runs nothing, the police refuse before anything starts.
        foreach (string command in new[] { verify, final }.OfType<string>().Where(c => c.Length > 0).Distinct(StringComparer.Ordinal))
        {
            if (RalphVerifier.Refusal(command, shell, _files.Root, _files, effective, _interpreters.Locate, out bool off) is { } police)
            {
                RalphLog.Unlock(_files, path, s_lockOwner.Value);
                _plan.ClearApproval();
                _transcript.Error(police);
                return false;
            }

            if (off)
            {
                noChecks = true;
                break;
            }
        }

        if (noChecks)
        {
            _transcript.Notice(RalphText.Glyph + " " + ShellText.CmdPolicyOff + ": the steps go unchecked.");
            verify = null;
            final = null;
        }

        foreach (string command in new[] { finalOnly ? null : verify, final }.OfType<string>().Where(c => c.Length > 0).Distinct(StringComparer.Ordinal))
        {
            var baseline = await RunRalphCheckAsync(command, shell, effective, RalphText.BaselineLabel, cancellationToken).ConfigureAwait(false);
            if (baseline is null || baseline.Kind == RalphCheckKind.Cancelled)
            {
                RalphLog.Unlock(_files, path, s_lockOwner.Value);
                _plan.ClearApproval();
                _transcript.Notice(CancelledNotice);
                return false;
            }

            if (baseline.Passed)
            {
                continue;
            }

            ShowTail(baseline.Tail);
            if (!await RalphConfirmAsync(RalphText.BaselineQuestion(command), cancellationToken).ConfigureAwait(false))
            {
                RalphLog.Unlock(_files, path, s_lockOwner.Value);
                _plan.ClearApproval();
                _transcript.Notice(RalphText.BaselineRefusedNotice);
                return false;
            }

            // Start anyway: the steps unchecked, the verify command run once at the end in place of a final that is missing.
            finalOnly = true;
            break;
        }

        if (fromPlanning)
        {
            _plan.Exit();
            _planDraftReply = null;
            SyncPlanChrome();
        }

        if (PlanFiles.MarkFile(_files, path, PlanStatus.Approved, _time.GetLocalNow(), requirement) is { } problem)
        {
            _transcript.Warning(problem);
        }

        var log = RalphLog.Read(_files, path);
        string title = PlanDocument.FirstHeading(body) ?? System.IO.Path.GetFileNameWithoutExtension(path);
        var snapshot = new RalphSnapshot(path, _files.Root, title, verify, final, finalOnly, ShellKinds.Name(shell), RalphReviewerMode.Resolve(effective),
            Math.Clamp(effective.RalphLoopCheckpoint, AppSettingsData.MinRalphLoopCheckpoint, AppSettingsData.MaxRalphLoopCheckpoint),
            passes ?? Math.Clamp(effective.RalphLoopPasses, AppSettingsData.MinRalphLoopPasses, AppSettingsData.MaxRalphLoopPasses),
            Math.Clamp(effective.RalphLoopPassesPerStep, AppSettingsData.MinRalphLoopPassesPerStep, AppSettingsData.MaxRalphLoopPassesPerStep),
            Math.Clamp(effective.RalphLoopCheckTimeoutSeconds, AppSettingsData.MinRalphLoopCheckTimeoutSeconds, AppSettingsData.MaxRalphLoopCheckTimeoutSeconds),
            Math.Max(0, effective.RalphLoopTokenBudget), RalphLog.PassCount(log) + 1);
        int remaining = PlanSteps.Leaves(steps).Count(s => !s.Ticked);
        var loop = new RalphLoop(snapshot, remaining);
        _ralph = loop;
        _executingPlan = new StoredPlan { Path = path, Requirement = requirement, Ralph = true, Verify = verify, Final = final, FinalOnly = finalOnly, Shell = snapshot.Shell, Root = snapshot.Root };
        _executingShown = null;
        RalphLog.SetHead(_files, path, new RalphLogHead(path, verify, final, finalOnly, snapshot.Shell, _time.GetLocalNow()));
        SavePlanState();
        _transcript.Notice(resume ? RalphText.ResumedNotice(path, remaining, loop.Cap) : RalphText.StartedNotice(path, remaining, loop.Cap, finalOnly));
        DiagnosticLog.Info(AppCategory, $"ralph: {(resume ? "resumed" : "started")} {path}, {remaining} steps, cap {loop.Cap}, reviewer {RalphReviewerMode.Name(snapshot.Reviewer)}, checkpoint {snapshot.Checkpoint}");
        return await RunRalphAsync(cancellationToken).ConfigureAwait(false);
    }

    // ── The driver ─────────────────────────────────────────────────────────

    /// <summary>The flat driver: pass after pass until a stop; true when the shell should exit.</summary>
    private async Task<bool> RunRalphAsync(CancellationToken cancellationToken)
    {
        var loop = _ralph!;
        string path = loop.Snapshot.Path;
        RalphStop? stop = null;
        bool exit = false;
        try
        {
            while (stop is null)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    stop = RalphStop.Stopped;
                    break;
                }

                if (PlanFiles.ReadWhole(_files, path) is not { } text)
                {
                    stop = RalphStop.PlanGone;
                    break;
                }

                if (!string.Equals(_files.Root, loop.Snapshot.Root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                {
                    stop = RalphStop.WorkdirMoved;
                    break;
                }

                string body = PlanDocument.Body(text);
                var steps = PlanSteps.Parse(body);
                loop.SetChildren(steps);
                var next = loop.Next(steps, QueueDrains && _queue.Count > 0);
                switch (next.Kind)
                {
                    case RalphNextKind.Stop:
                        stop = next.Stop;
                        break;
                    case RalphNextKind.FinalCheck:
                        stop = await RalphFinalCheckAsync(loop, cancellationToken).ConfigureAwait(false);
                        break;
                    default:
                        (exit, stop) = await RalphPassAsync(loop, next, body, steps, cancellationToken).ConfigureAwait(false);
                        if (exit)
                        {
                            stop ??= RalphStop.Stopped;
                        }

                        break;
                }
            }
        }
        finally
        {
            _ralphTurn = null;
            _ralphPass = null;
        }

        EndRalph(stop ?? RalphStop.Stopped);
        return exit;
    }

    /// <summary>
    /// One pass on <paramref name="next"/>'s step (or the final fix): the light reset, the directive and the message, the pictures, the snapshot, the
    /// short line, the turn, then the judgment and the log; the loop's verdict.
    /// </summary>
    private async Task<(bool Exit, RalphStop? Stop)> RalphPassAsync(RalphLoop loop, RalphNext next, string body, IReadOnlyList<PlanStep> steps, CancellationToken cancellationToken)
    {
        var effective = _effective();
        var snapshot = loop.Snapshot;
        string path = snapshot.Path;
        var step = next.Kind == RalphNextKind.FinalFix ? null : next.Step;
        var leaves = PlanSteps.Leaves(steps).ToList();
        int total = leaves.Count;
        int index = step?.Index ?? total;
        string? finalTail = step is null ? loop.TakeFinalTail() : null;
        var log = RalphLog.Read(_files, path);
        ResetForPass(loop.Pass + 1, index, total, snapshot.Title);
        loop.BeginPass(step, PlanMeter());
        string? notes = JoinNotes(loop.TakeNotes(), step is null ? null : RalphLog.RecentNotes(log, step.Text));
        bool reviewed = snapshot.Reviewer != RalphReviewer.Off;
        string directive = RalphText.Directive(path, index, total, step?.Text ?? RalphText.FinalFixStep, step?.DoneWhen, snapshot.StepCheck ?? (step is null ? snapshot.FinalCheck : null), reviewed && step is not null, canAsk: snapshot.Checkpoint > 0, notes, RalphLog.LearnedText(log), finalFix: step is null);
        _ralphTurn = new PlanTurn(directive, _finishStep);
        int windowChars = RalphWindowChars();
        string message = step is null
            ? RalphText.FinalFixMessage(path, snapshot.FinalCheck ?? "", finalTail ?? "")
            : RalphText.PassMessage(path, body, index, total, step.Text, step.DoneWhen, windowChars);
        IReadOnlyList<ImageAttachment> images = [];
        if (!loop.DropPicturesNext)
        {
            images = PlanReferences.ForRun(_files, path, body, step, effective.LlmPictureKeep, effective.LlmPictureMegabytes, ModelSeesPictures(), out string? unseen);
            if (unseen is not null && loop.Pass == snapshot.StartPass)
            {
                _transcript.Notice(unseen);
            }
        }

        loop.DropPicturesNext = false;
        var before = await Task.Run(() => RalphChanges.Snapshot(_files, _git, cancellationToken), cancellationToken).ConfigureAwait(false);
        var boxes = PlanSteps.Boxes(body);
        string line = step is null ? RalphText.FinalFixLine(loop.Pass) : RalphText.TranscriptLine(loop.Pass, index, total, step.Text);
        _ralphPass = (line, RalphText.SessionTitle(snapshot.Title, index, total, loop.Pass));
        _ralphStrip = RalphText.StripText(index, total, loop.Pass, final: step is null);
        _webUi.StatusNudge();
        _transcript.User(line);
        if (images.Count > 0)
        {
            _transcript.Notice(PlanText.ReferencesAttachedNotice(images.Count));
        }

        DiagnosticLog.Info(AppCategory, $"ralph: pass {loop.Pass}, step {index} of {total}: {step?.Text ?? RalphText.FinalFixStep}");
        bool exit;
        try
        {
            exit = await RunMessageAsync(message, images, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ralphPass = null;
        }

        var judged = await JudgeRalphPassAsync(loop, step, total, boxes, before, images.Count > 0, cancellationToken).ConfigureAwait(false);
        var stop = loop.EndPass(judged.Facts, PlanMeter());
        RalphLogPass(loop, step, judged);
        _ralphTurn = null;
        if (exit)
        {
            return (true, stop ?? RalphStop.Stopped);
        }

        if (stop is null && loop.CheckpointDue && step is not null)
        {
            stop = await RalphCheckpointAsync(loop, step, judged, cancellationToken).ConfigureAwait(false);
        }

        return (false, stop);
    }

    /// <summary>The screen as the judge's host (<see cref="IRalphHost"/>): the transcript, the watch over a check, the review.</summary>
    private sealed class ScreenRalphHost(ChatScreen screen) : IRalphHost
    {
        public WorkingDirectory Files => screen._files;

        public GitAccess? Git => screen._git;

        public void Notice(string text) => screen._transcript.Notice(text);

        public void Tail(string tail) => screen.ShowTail(tail);

        public void Log(string line) => DiagnosticLog.Info(AppCategory, line);

        public Task<RalphCheckResult?> CheckAsync(string command, ShellKind shell, string label, CancellationToken cancellationToken) =>
            screen.RunRalphCheckAsync(command, shell, screen._effective(), label, cancellationToken);

        public Task<RalphVerdict?> ReviewAsync(RalphLoop loop, PlanStep step, FinishReport? report, RalphDelta delta, CancellationToken cancellationToken) =>
            screen.ReviewRalphStepAsync(loop, step, report, delta, cancellationToken);
    }

    /// <summary>After a pass's turn: <see cref="RalphJudge.JudgeAsync"/> over the screen.</summary>
    private Task<RalphJudged> JudgeRalphPassAsync(RalphLoop loop, PlanStep? step, int total, IReadOnlyDictionary<string, bool> boxes, RalphSnapshotOfFiles before, bool hadPictures, CancellationToken cancellationToken) =>
        RalphJudge.JudgeAsync(new ScreenRalphHost(this), loop, step, total, boxes, before, hadPictures, _lastTurnCancelled, _lastTurnFailed, _executingPlan?.Requirement ?? "", _time, cancellationToken);

    /// <summary>A step's box set in the file (the loop's own tick), quietly.</summary>
    private void SetBox(string path, PlanStep step, bool ticked) => RalphJudge.SetBox(new ScreenRalphHost(this), path, step, ticked);

    /// <summary>The header's status and progress over the loop's leaves.</summary>
    private void MarkRalphProgress(string path) => RalphJudge.MarkProgress(_files, path, _executingPlan?.Requirement ?? "", _time);

    /// <summary>The pass's entry in the log, its learned facts merged (<see cref="RalphJudge.LogPass"/>), and a log line.</summary>
    private void RalphLogPass(RalphLoop loop, PlanStep? step, RalphJudged judged)
    {
        if (RalphJudge.LogPass(_files, loop, step, judged, _time) is { } problem)
        {
            _transcript.Warning(problem);
        }

        DiagnosticLog.Info(AppCategory, $"ralph: pass {loop.Pass} {(loop.History.Count > 0 ? loop.History[^1].Outcome : "none")}, check {judged.Check}, review {judged.Review}, {judged.Facts.Changed.Count} files changed");
    }

    /// <summary>Every step ticked: the final check (when there is one), its fix owed or the loop done.</summary>
    private async Task<RalphStop?> RalphFinalCheckAsync(RalphLoop loop, CancellationToken cancellationToken)
    {
        if (loop.Snapshot.FinalCheck is not { } final)
        {
            return RalphStop.Done;
        }

        var result = await RunRalphCheckAsync(final, ShellOf(loop.Snapshot), _effective(), RalphText.CheckLabel(final), cancellationToken).ConfigureAwait(false);
        if (result is null or { Kind: RalphCheckKind.Cancelled })
        {
            return RalphStop.Stopped;
        }

        var stop = loop.FinalResult(result.Passed, result.Tail);
        if (stop is null)
        {
            _transcript.Notice(RalphText.FinalFailedOnceNotice(final));
            ShowTail(result.Tail);
        }

        return stop;
    }

    private static ShellKind ShellOf(RalphSnapshot snapshot) => RalphJudge.ShellOf(snapshot);

    /// <summary>
    /// A check run under the busy row and the watch (Esc, the page's Stop and the kill switch end it: a <see cref="KillScopeKind.Work"/> scope); null when
    /// it could not run. The command's police and policy were judged at the start.
    /// </summary>
    private async Task<RalphCheckResult?> RunRalphCheckAsync(string command, ShellKind shell, AppSettingsData effective, string label, CancellationToken cancellationToken)
    {
        RalphCheckResult? result = null;
        int seconds = Math.Clamp(effective.RalphLoopCheckTimeoutSeconds, AppSettingsData.MinRalphLoopCheckTimeoutSeconds, AppSettingsData.MaxRalphLoopCheckTimeoutSeconds);
        if (_ralph is { } running)
        {
            seconds = running.Snapshot.CheckTimeoutSeconds;
        }

        string workdir;
        try
        {
            workdir = _files.EnsureExists();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            _transcript.Error(Files.FileText.CouldNot("create", _files.Root, ex.Message));
            return null;
        }

        bool stopped = await WaitUnderWatchAsync(async token =>
        {
            using (_transcript.BeginBusy(label))
            {
                result = await _ralphVerifier.RunAsync(command, shell, workdir, TimeSpan.FromSeconds(seconds), token).ConfigureAwait(false);
            }
        }, KeySource.IsTurnCancel, cancellationToken, pointer: true).ConfigureAwait(false);
        if (stopped && result is null)
        {
            return new RalphCheckResult(RalphCheckKind.Cancelled, null, "", TimeSpan.Zero);
        }

        return result;
    }

    /// <summary>A check's tail on the transcript, the last lines dim.</summary>
    private void ShowTail(string tail)
    {
        foreach (string line in RalphLog.CleanTail(tail).Split('\n').TakeLast(12))
        {
            if (line.Length > 0)
            {
                _transcript.ShellOutput(line, false);
            }
        }
    }

    private static string? JoinNotes(string? first, string? second) =>
        first is null ? second : second is null ? first : first + "\n" + second;

    // ── The light reset and the stop ───────────────────────────────────────

    /// <summary>
    /// A fresh conversation for a pass: the rule and the boundary notice, the history, the traces, the conversation's usage, the session row (a pass
    /// gets one of its own) and the title — never the queue, plan or audit state, the advisors' links, the picture strip or the allows.
    /// </summary>
    private void ResetForPass(int pass, int step, int total, string title)
    {
        using (_pane.Batch())
        {
            _transcript.Rule();
            _transcript.Notice(RalphText.Glyph + " " + RalphText.SessionTitle(title, step, total, pass));
        }

        _session.History.Clear();
        _lastTrace = null;
        _learnTrace = null;
        _session.Usage.ResetConversation();
        ForgetSessionRow(advisorLinks: false);
        RefreshSessionTitle();
    }

    /// <summary>The loop over: the summary, the stop's notice, the file and the state, the lock let go, then the stop tone for a stop the user did not cause.</summary>
    private void EndRalph(RalphStop stop)
    {
        if (_ralph is not { } loop)
        {
            return;
        }

        string path = loop.Snapshot.Path;
        _ralph = null;
        _ralphTurn = null;
        _ralphStrip = null;
        _webUi.StatusNudge();
        foreach (string line in RalphText.SummaryLines(loop.History, stop))
        {
            _transcript.Notice(line);
        }

        var counts = PlanFiles.ReadWhole(_files, path) is { } text ? PlanSteps.Counts(PlanSteps.Parse(PlanDocument.Body(text))) : (0, 0);
        if (stop == RalphStop.StepFailing && loop.Current is { } failing && loop.ChangedBy(failing) is { Count: > 0 } changed)
        {
            _transcript.Notice(RalphText.LeftChangedLine(changed));
        }

        if (stop == RalphStop.Done)
        {
            if (PlanFiles.MarkFile(_files, path, PlanStatus.Done, _time.GetLocalNow(), _executingPlan?.Requirement ?? "", counts) is { } problem)
            {
                _transcript.Warning(problem);
            }

            _transcript.Notice(RalphText.StopNotice(stop, path, counts.Item1, counts.Item2));
            _transcript.Notice(PlanText.DoneNotice(path));
            _transcript.Notice(PlanText.ComesTogetherNotice);
            _executingPlan = null;
        }
        else if (stop is RalphStop.PlanGone)
        {
            _transcript.Notice(RalphText.StopNotice(stop, path, counts.Item1, counts.Item2));
            _executingPlan = null;
        }
        else
        {
            if (counts.Item2 > 0)
            {
                PlanFiles.MarkFile(_files, path, PlanStatus.Incomplete, _time.GetLocalNow(), _executingPlan?.Requirement ?? "", counts);
            }

            _transcript.Notice(RalphText.StopNotice(stop, path, counts.Item1, counts.Item2));
            if (_executingPlan is { Ralph: true } paused)
            {
                paused.Paused = true;
            }
        }

        if (RalphLog.Read(_files, path)?.Head is { } head)
        {
            RalphLog.SetHead(_files, path, head with { Stopped = RalphStopWord(stop) });
        }

        RalphLog.Unlock(_files, path, s_lockOwner.Value);
        SavePlanState();
        DiagnosticLog.Info(AppCategory, $"ralph: stopped {path}: {RalphStopWord(stop)} after {loop.PassesThisRun} passes");
        if (stop is not (RalphStop.Stopped or RalphStop.Paused or RalphStop.UserStopped))
        {
            _ = _tones.PlayAsync(_effective().RalphLoopStopTone, CancellationToken.None);
        }
    }

    /// <summary>The word the log's head and the picker say a stop with.</summary>
    internal static string RalphStopWord(RalphStop stop) => stop switch
    {
        RalphStop.StepFailing => "step failing",
        RalphStop.FinalFailed => "final failed",
        RalphStop.PlanGone => "plan gone",
        RalphStop.WorkdirMoved => "workdir moved",
        RalphStop.UserStopped => "stopped at the checkpoint",
        _ => stop.ToString().ToLowerInvariant(),
    };

    /// <summary>A loop left with the conversation (a profile switch, <c>/new</c>, a hand-over reset): its lock let go, the state dropped; the file keeps everything.</summary>
    private void StopRalphOnReset()
    {
        if (_ralph is { } loop)
        {
            RalphLog.Unlock(_files, loop.Snapshot.Path, s_lockOwner.Value);
            _ralph = null;
            _ralphTurn = null;
            _ralphPass = null;
            _ralphStrip = null;
        }
    }

    /// <summary><c>/plan open</c> on the paused plan leaves the loop (its log kept): a notice says how to run it again.</summary>
    private void LeaveRalphOnOpen(string path)
    {
        if (_executingPlan is { Ralph: true, Path: { } paused } && string.Equals(paused, path, StringComparison.Ordinal))
        {
            _transcript.Notice(RalphText.LeftOnOpenNotice(path));
        }
    }

    /// <summary><c>/plan show</c> outside plan mode while a loop runs or is paused: where it stands; false with none.</summary>
    private bool ShowRalph()
    {
        if (_executingPlan is not { Ralph: true, Path: { } path } stored)
        {
            return false;
        }

        var counts = PlanFiles.ReadWhole(_files, path) is { } text ? PlanSteps.Counts(PlanSteps.Parse(PlanDocument.Body(text))) : (0, 0);
        int pass = _ralph?.Pass ?? RalphLog.PassCount(RalphLog.Read(_files, path));
        foreach (string line in RalphText.ShowLines(path, counts.Item1, counts.Item2, pass, _ralph is null || stored.Paused, RalphLog.PathFor(path)))
        {
            _transcript.Notice(line);
        }

        return true;
    }

    // ── The checkpoint ─────────────────────────────────────────────────────

    /// <summary>
    /// The checkpoint after a step that stood (2026-10-18, <c>LLM Ralph loop checkpoint</c> above 0): Keep, Redo with a note (the step unticked, the
    /// note the next pass's), View the changes, or Stop — on the PC and the page at once, waiting until answered.
    /// </summary>
    private async Task<RalphStop?> RalphCheckpointAsync(RalphLoop loop, PlanStep step, RalphJudged judged, CancellationToken cancellationToken)
    {
        string summary = loop.History.Count > 0 ? loop.History[^1].Outcome : "";
        string caption = RalphText.CheckpointCaption(step.Index, PlanStepsCount(loop), step.Text, loop.Report?.Summary ?? summary);
        var spec = new WebAskSpec("ralphcheck", RalphText.CheckpointTitle,
            [new WebChoice("keep", RalphText.CheckpointRows[0]), new WebChoice("redo", RalphText.CheckpointRows[1]), new WebChoice("stop", RalphText.CheckpointRows[3])],
            Body: caption, Detail: judged.Diff.Length == 0 ? RalphText.NoChangesLine : judged.Diff, DetailLabel: RalphText.CheckpointRows[2], TextChoice: "redo");
        var menu = new RalphCheckpointMenu(_menuPane, _input);
        var outcome = await RaceAsync<RalphCheckpointAnswer?>(spec, token => menu.AskAsync(caption, judged.Diff, token), a => a.Choice switch
        {
            "redo" => new RalphCheckpointAnswer(RalphCheckpointChoice.Redo, (a.Text ?? "").Trim()),
            "stop" => new RalphCheckpointAnswer(RalphCheckpointChoice.Stop, null),
            _ => new RalphCheckpointAnswer(RalphCheckpointChoice.Keep, null),
        }, "The Ralph checkpoint pane failed: ", cancellationToken, bothSides: true).ConfigureAwait(false);
        var answer = outcome.Value ?? new RalphCheckpointAnswer(RalphCheckpointChoice.Keep, null);
        DiagnosticLog.Info(AppCategory, $"ralph: checkpoint after pass {loop.Pass}: {answer.Choice.ToString().ToLowerInvariant()}");
        switch (answer.Choice)
        {
            case RalphCheckpointChoice.Redo:
                SetBox(loop.Snapshot.Path, step, false);
                loop.Redo(step, answer.Note);
                MarkRalphProgress(loop.Snapshot.Path);
                return null;
            case RalphCheckpointChoice.Stop:
                return RalphStop.UserStopped;
            default:
                return null;
        }
    }

    private int PlanStepsCount(RalphLoop loop) =>
        PlanFiles.ReadWhole(_files, loop.Snapshot.Path) is { } text ? PlanSteps.Counts(PlanSteps.Parse(PlanDocument.Body(text))).Total : 0;
}
