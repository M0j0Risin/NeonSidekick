using NeonSidekick.Files;
using NeonSidekick.Git;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Plans;
using NeonSidekick.Settings;
using NeonSidekick.Shell;

namespace NeonSidekick.App;

// ── The Ralph loop headless (2026-10-18, phase 7) ─────────────────────────

/// <summary>
/// The Ralph loop headless (2026-10-18): <c>/plan approve --ralph [passes]</c> while planning and <c>/plan ralph [name] [passes]</c> outside it, the
/// screen's rules without a pane — typing the command is the consent (no confirm), a failing baseline refuses with its tail (headless cannot ask
/// "start anyway?"), no checkpoint and no asking tools, the cap always. Each pass is the next line sent without reading stdin (the gap review's
/// slot), echoed as its short line; after its turn the pass is judged by the screen's judge (<see cref="RalphJudge"/> over
/// <see cref="HeadlessRalphHost"/>), logged, and the next queued or the loop ended. Between passes the conversation is forgotten (the history,
/// the usage, the session row, the Claude ids, the advisors' threads, the files seen) and nothing else.
/// </summary>
public sealed partial class SidekickApp
{
    /// <summary>What a headless loop works with, set once per run (the headless loop's locals); the shell's allow list too (2026-10-19, <see cref="HeadlessCheckRefusal"/>).</summary>
    private sealed record HeadlessRalphContext(LlmSession Session, GitAccess Git, NeonAdvisorTool Advisor, Interpreters Interpreters, IRalphVerifier Verifier, CommandAllowList AllowList);

    /// <summary>
    /// A headless loop and the pass under way. <see cref="Files"/> and <see cref="Git"/> are pinned to the folder it started in (2026-10-19, the second
    /// review: the run's working directory follows the settings, so a change mid-run sent the judge, the stop's writes and the unlock elsewhere).
    /// </summary>
    private sealed class HeadlessRalph(RalphLoop loop, FinishStepTool tool, string requirement, WorkingDirectory files, GitAccess git)
    {
        public RalphLoop Loop { get; } = loop;
        public FinishStepTool Tool { get; } = tool;
        public string Requirement { get; } = requirement;
        public WorkingDirectory Files { get; } = files;
        public GitAccess Git { get; } = git;
        public PlanTurn? Turn { get; set; }
        public PlanStep? Step { get; set; }
        public int Total { get; set; }
        public RalphSnapshotOfFiles? Before { get; set; }
        public IReadOnlyList<PlanStep> Lines { get; set; } = [];
        public bool HadPictures { get; set; }
        public string Line { get; set; } = "";
        public string Title { get; set; } = "";
    }

    /// <summary>The next line the headless loop sends without reading stdin: its text, its echo and its pictures.</summary>
    private sealed record HeadlessNext(string Text, string Echo, IReadOnlyList<ImageAttachment> Images);

    /// <summary>
    /// The headless judge's host: notices into a list printed after, the check run straight (its timeout the snapshot's, as the screen's: 2026-10-19,
    /// the second review, it read the live setting), the review the screen's reviewers; the loop's own folder and repository.
    /// </summary>
    private sealed class HeadlessRalphHost(SidekickApp app, HeadlessRalph ralph, HeadlessRalphContext context, List<string> notices) : IRalphHost
    {
        public WorkingDirectory Files => ralph.Files;

        public GitAccess? Git => ralph.Git;

        public void Notice(string text) => notices.Add(text);

        public void Tail(string tail) => notices.AddRange(RalphLog.CleanTail(tail).Split('\n').TakeLast(12).Where(l => l.Length > 0).Select(l => "  " + l));

        public void Log(string line) => Diagnostics.DiagnosticLog.Info(ChatScreen.AppCategory, line);

        public async Task<RalphCheckResult?> CheckAsync(string command, ShellKind shell, string label, CancellationToken cancellationToken)
        {
            // The police, the policy and the allow list again before every run (2026-10-19, the review): a pass may rewrite what the command runs.
            string? refused = HeadlessCheckRefusal(command, shell, Files, app.EffectiveSettings, context, out bool off);
            if (refused is not null || off)
            {
                notices.Add(RalphText.CheckRefusedError(command, refused ?? RalphText.PolicyOffNowReason));
                return new RalphCheckResult(RalphCheckKind.Cancelled, null, "", TimeSpan.Zero);
            }

            return await context.Verifier.RunAsync(command, shell, Files.EnsureExists(), TimeSpan.FromSeconds(ralph.Loop.Snapshot.CheckTimeoutSeconds), cancellationToken).ConfigureAwait(false);
        }

        public async Task<RalphVerdict?> ReviewAsync(RalphLoop loop, PlanStep step, FinishReport? report, RalphDelta delta, CancellationToken cancellationToken)
        {
            var effective = app.EffectiveSettings;
            bool byAdvisor = RalphReviewers.ByAdvisor(loop.Snapshot.Reviewer, app.HeadlessAdvisorsOffered() && !effective.AdvisorConfirm);
            string request = RalphReviewers.Request(step, report, delta, context.Session.ContextLength?.Tokens ?? 0);
            var (verdict, tokens) = byAdvisor
                ? await RalphReviewers.ByAdvisorAsync(context.Advisor, context.Session.Usage, request, effective, cancellationToken).ConfigureAwait(false)
                : await RalphReviewers.ByModelAsync(context.Session, Files, loop.Snapshot, step, request, effective.LlmPictureKeep, effective.LlmPictureMegabytes, ImageCodecs.Available, cancellationToken).ConfigureAwait(false);
            loop.AddReviewTokens(tokens);
            Log(RalphReviewers.LogLine(loop.Pass, byAdvisor, verdict));
            return cancellationToken.IsCancellationRequested ? null : verdict;
        }
    }

    /// <summary>
    /// <c>/plan approve --ralph</c>, <c>/plan ralph</c> and a bad count headless: true when the line was one of them (said; a pass queued in
    /// <see cref="HeadlessPlanState.Next"/> when the loop began).
    /// </summary>
    private async Task<bool> HeadlessRalphCommandAsync(PlanSession plan, HeadlessPlanState state, WorkingDirectory files, ChatScreen.PlanCommand command, string rest, CancellationToken cancellationToken)
    {
        switch (command)
        {
            case ChatScreen.PlanCommand.BadRalphPasses:
                await HeadlessLineAsync(HeadlessReplyPrefix + "[error] " + RalphText.BadPassesError(rest)).ConfigureAwait(false);
                return true;
            case ChatScreen.PlanCommand.ApproveRalph:
            {
                if (plan.Path is not { } path)
                {
                    await HeadlessLineAsync(HeadlessReplyPrefix + "[error] " + PlanText.NothingPresentedError).ConfigureAwait(false);
                    return true;
                }

                if (PlanFiles.ReadWhole(files, path) is not { } text)
                {
                    await HeadlessLineAsync(HeadlessReplyPrefix + "[error] " + PlanText.PlanUnreadableError(path)).ConfigureAwait(false);
                    return true;
                }

                var header = PlanDocument.TryParse(text);
                int? passes = rest.Length == 0 ? null : int.Parse(rest, System.Globalization.CultureInfo.InvariantCulture);
                await HeadlessRalphBeginAsync(plan, state, files, path, PlanDocument.Body(text), header?.Verify, header?.Final, plan.Requirement, passes, fromPlanning: true, stored: null, cancellationToken).ConfigureAwait(false);
                return true;
            }

            case ChatScreen.PlanCommand.Ralph:
            {
                if (plan.Active)
                {
                    await HeadlessLineAsync(HeadlessReplyPrefix + "[error] " + RalphText.WhilePlanningError).ConfigureAwait(false);
                    return true;
                }

                ChatScreen.RalphArgs(rest, out string name, out int? passes, out _);
                if (name.Length == 0)
                {
                    if (state.Executing is { Ralph: true, Path: { } elsewhere } away && !RalphSnapshot.SameRoot(away.Root, files.Root))
                    {
                        // Its approved commands are the folder's it ran in (2026-10-19, the review): never run here.
                        await HeadlessLineAsync(HeadlessReplyPrefix + "[error] " + RalphText.ResumeElsewhereError(elsewhere, away.Root ?? "?")).ConfigureAwait(false);
                        return true;
                    }

                    if (state.Executing is { Ralph: true, Path: { } paused } stored && PlanFiles.ReadWhole(files, paused) is { } pausedText)
                    {
                        await HeadlessRalphBeginAsync(plan, state, files, paused, PlanDocument.Body(pausedText), stored.Verify, stored.Final, stored.Requirement, passes, fromPlanning: false, stored, cancellationToken).ConfigureAwait(false);
                        return true;
                    }

                    await HeadlessNoticeLineAsync("[notice] " + RalphText.NothingPausedNotice).ConfigureAwait(false);
                    await HeadlessListPlansAsync(files).ConfigureAwait(false);
                    return true;
                }

                if (PlanFiles.Resolve(files, name) is not { } path)
                {
                    await HeadlessLineAsync(HeadlessReplyPrefix + "[error] " + PlanText.NoSuchPlanError(name)).ConfigureAwait(false);
                    await HeadlessListPlansAsync(files).ConfigureAwait(false);
                    return true;
                }

                if (PlanFiles.ReadWhole(files, path) is not { } text)
                {
                    await HeadlessLineAsync(HeadlessReplyPrefix + "[error] " + PlanText.PickerUnreadableError(path)).ConfigureAwait(false);
                    return true;
                }

                var header = PlanDocument.TryParse(text);
                if (header is { Status: PlanStatus.Done or PlanStatus.Cancelled } finished)
                {
                    await HeadlessLineAsync(HeadlessReplyPrefix + "[error] " + RalphText.NotRunnableError(path, finished.Status)).ConfigureAwait(false);
                    return true;
                }

                string body = PlanDocument.Body(text);
                string requirement = header is { Requirement.Length: > 0 } ? header.Requirement : PlanDocument.FirstHeading(body) ?? path;
                // A paused loop on it resumes only in the folder it ran in (2026-10-19); elsewhere the plan is this folder's, its header read afresh.
                var resumed = state.Executing is { Ralph: true, Path: { } same } held && string.Equals(same, path, StringComparison.Ordinal) && RalphSnapshot.SameRoot(held.Root, files.Root) ? held : null;
                await HeadlessRalphBeginAsync(plan, state, files, path, body, resumed?.Verify ?? header?.Verify, resumed?.Final ?? header?.Final, requirement, passes, fromPlanning: false, resumed, cancellationToken).ConfigureAwait(false);
                return true;
            }

            default:
                return false;
        }
    }

    /// <summary>
    /// A headless loop begun (the screen's <c>BeginRalphAsync</c> without a confirm): steps, lock, baseline, mark, snapshot, then its first pass queued.
    /// A plan that may not suit a loop (later on 2026-10-18, <see cref="PlanFit"/>) is said before the start notice, and the loop goes on; not on a resume.
    /// </summary>
    private async Task HeadlessRalphBeginAsync(PlanSession plan, HeadlessPlanState state, WorkingDirectory files, string path, string body, string? verify, string? final, string requirement, int? passes, bool fromPlanning, Sessions.StoredPlan? stored, CancellationToken cancellationToken)
    {
        var context = state.RalphContext!;
        var effective = EffectiveSettings;
        if (!effective.LlmOfferTools)
        {
            await HeadlessLineAsync(HeadlessReplyPrefix + "[error] " + RalphText.NeedsToolsError).ConfigureAwait(false);
            return;
        }

        var steps = PlanSteps.Parse(body);
        if (!PlanSteps.Leaves(steps).Any())
        {
            await HeadlessLineAsync(HeadlessReplyPrefix + "[error] " + RalphText.NoStepsError).ConfigureAwait(false);
            return;
        }

        // The folder from the lock on (2026-10-19, the second review): everything the loop writes and checks is this one's.
        files = files.PinnedAt(files.Root);
        var owner = RalphLog.Me;
        if (!RalphLog.TryLock(files, path, owner, RalphLog.OwnerAlive))
        {
            await HeadlessLineAsync(HeadlessReplyPrefix + "[error] " + RalphText.LockedError(path)).ConfigureAwait(false);
            return;
        }

        var shell = stored?.Shell is { } storedShell && ShellKinds.TryParse(storedShell, out var kind) ? kind : ShellKinds.Resolve(effective);
        bool finalOnly = stored?.FinalOnly ?? false;
        bool noChecks = false;
        foreach (string command in new[] { verify, final }.OfType<string>().Where(c => c.Length > 0).Distinct(StringComparer.Ordinal))
        {
            if (HeadlessCheckRefusal(command, shell, files, effective, context, out bool off) is { } police)
            {
                RalphLog.Unlock(files, path, owner);
                await HeadlessLineAsync(HeadlessReplyPrefix + "[error] " + police).ConfigureAwait(false);
                return;
            }

            if (off)
            {
                // This run goes unchecked (the snapshot's Unchecked); the approved commands stay in the stored state and the log for a resume.
                await HeadlessNoticeLineAsync("[notice] " + RalphText.PolicyOffUncheckedNotice).ConfigureAwait(false);
                noChecks = true;
                break;
            }
        }

        if (!noChecks && (verify is { Length: > 0 } || final is { Length: > 0 }))
        {
            // Said before anything runs (2026-10-19, the review): the plan's header is model-written, and headless has no confirm to show it on.
            await HeadlessNoticeLineAsync("[notice] " + RalphText.HeadlessChecksNotice(verify, final)).ConfigureAwait(false);
        }

        foreach (string command in (noChecks ? Array.Empty<string?>() : new[] { finalOnly ? null : verify, final }).OfType<string>().Where(c => c.Length > 0).Distinct(StringComparer.Ordinal))
        {
            int seconds = Math.Clamp(effective.RalphLoopCheckTimeoutSeconds, AppSettingsData.MinRalphLoopCheckTimeoutSeconds, AppSettingsData.MaxRalphLoopCheckTimeoutSeconds);
            var baseline = await context.Verifier.RunAsync(command, shell, files.EnsureExists(), TimeSpan.FromSeconds(seconds), cancellationToken).ConfigureAwait(false);
            if (baseline.Passed)
            {
                continue;
            }

            RalphLog.Unlock(files, path, owner);
            foreach (string line in RalphLog.CleanTail(baseline.Tail).Split('\n').TakeLast(12).Where(l => l.Length > 0))
            {
                await HeadlessNoticeLineAsync("[notice]   " + line).ConfigureAwait(false);
            }

            await HeadlessLineAsync(HeadlessReplyPrefix + "[error] " + RalphText.BaselineRefusedHeadless(command)).ConfigureAwait(false);
            return;
        }

        if (fromPlanning)
        {
            plan.Exit();
            state.Draft = null;
        }

        if (PlanFiles.MarkFile(files, path, PlanStatus.Approved, _time.GetLocalNow(), requirement) is { } problem)
        {
            await HeadlessNoticeLineAsync("[notice] " + problem).ConfigureAwait(false);
        }

        var log = RalphLog.Read(files, path);
        string title = PlanDocument.FirstHeading(body) ?? Path.GetFileNameWithoutExtension(path);
        var snapshot = new RalphSnapshot(path, files.Root, title, verify, final, finalOnly, ShellKinds.Name(shell), RalphReviewerMode.Resolve(effective),
            0,   // no checkpoint headless: nobody to answer it
            passes ?? Math.Clamp(effective.RalphLoopPasses, AppSettingsData.MinRalphLoopPasses, AppSettingsData.MaxRalphLoopPasses),
            Math.Clamp(effective.RalphLoopPassesPerStep, AppSettingsData.MinRalphLoopPassesPerStep, AppSettingsData.MaxRalphLoopPassesPerStep),
            Math.Clamp(effective.RalphLoopCheckTimeoutSeconds, AppSettingsData.MinRalphLoopCheckTimeoutSeconds, AppSettingsData.MaxRalphLoopCheckTimeoutSeconds),
            Math.Max(0, effective.RalphLoopTokenBudget), RalphLog.PassCount(log) + 1, Unchecked: noChecks);
        int remaining = PlanSteps.Leaves(steps).Count(s => !s.Ticked);
        var loop = new RalphLoop(snapshot, remaining);
        state.Ralph = new HeadlessRalph(loop, new FinishStepTool(() => state.Ralph?.Loop), requirement, files, new GitAccess(files, _time));
        state.Executing = new Sessions.StoredPlan { Path = path, Requirement = requirement, Ralph = true, Verify = verify, Final = final, FinalOnly = finalOnly, Shell = snapshot.Shell, Root = snapshot.Root };
        state.Shown = null;
        RalphLog.SetHead(files, path, new RalphLogHead(path, verify, final, finalOnly, snapshot.Shell, _time.GetLocalNow()));
        if (stored is null
            && PlanFit.Problems(body, new PlanChecks(verify, final), snapshot.Reviewer != RalphReviewer.Off,
                RalphText.InlineLimit((context.Session.ContextLength?.Tokens ?? 0) * Llm.ConversationCompactor.CharsPerToken)) is { Count: > 0 } problems)
        {
            await HeadlessNoticeLineAsync("[notice] " + RalphText.FitNotice(problems)).ConfigureAwait(false);
        }

        await HeadlessNoticeLineAsync("[notice] " + (stored is not null ? RalphText.ResumedNotice(path, remaining, loop.Cap) : RalphText.StartedNotice(path, remaining, loop.Cap, finalOnly))).ConfigureAwait(false);
        await HeadlessRalphQueueAsync(state, files, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The loop's next move headless: a pass queued in <see cref="HeadlessPlanState.Next"/>, the final check run, or the loop ended.</summary>
    private async Task HeadlessRalphQueueAsync(HeadlessPlanState state, WorkingDirectory sandbox, CancellationToken cancellationToken)
    {
        var ralph = state.Ralph!;
        var loop = ralph.Loop;
        var context = state.RalphContext!;
        var files = ralph.Files;
        string path = loop.Snapshot.Path;
        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                await HeadlessRalphEndAsync(state, files, RalphStop.Stopped).ConfigureAwait(false);
                return;
            }

            if (PlanFiles.ReadWhole(files, path) is not { } text)
            {
                await HeadlessRalphEndAsync(state, files, RalphStop.PlanGone).ConfigureAwait(false);
                return;
            }

            if (!RalphSnapshot.SameRoot(sandbox.Root, loop.Snapshot.Root))
            {
                await HeadlessRalphEndAsync(state, files, RalphStop.WorkdirMoved).ConfigureAwait(false);
                return;
            }

            string body = PlanDocument.Body(text);
            // The file whole (2026-10-19, the review): a step's line is the file's, so the loop's tick lands on that step and no other.
            var steps = PlanSteps.Parse(text);
            loop.SetChildren(steps);
            var next = loop.Next(steps, userSpoke: false);
            if (next.Kind == RalphNextKind.Stop)
            {
                await HeadlessRalphEndAsync(state, files, next.Stop ?? RalphStop.Stopped).ConfigureAwait(false);
                return;
            }

            if (next.Kind == RalphNextKind.FinalCheck)
            {
                if (loop.Snapshot.FinalCheck is not { } final)
                {
                    await HeadlessRalphEndAsync(state, files, RalphStop.Done).ConfigureAwait(false);
                    return;
                }

                string? refused = HeadlessCheckRefusal(final, RalphJudge.ShellOf(loop.Snapshot), files, EffectiveSettings, context, out bool policyOff);
                if (refused is not null || policyOff)
                {
                    await HeadlessNoticeLineAsync("[notice] " + RalphText.CheckRefusedError(final, refused ?? RalphText.PolicyOffNowReason)).ConfigureAwait(false);
                    await HeadlessRalphEndAsync(state, files, RalphStop.Stopped).ConfigureAwait(false);
                    return;
                }

                int seconds = loop.Snapshot.CheckTimeoutSeconds;
                var result = await context.Verifier.RunAsync(final, RalphJudge.ShellOf(loop.Snapshot), files.EnsureExists(), TimeSpan.FromSeconds(seconds), cancellationToken).ConfigureAwait(false);
                if (result.Kind == RalphCheckKind.Cancelled)
                {
                    await HeadlessRalphEndAsync(state, files, RalphStop.Stopped).ConfigureAwait(false);
                    return;
                }

                if (loop.FinalResult(result.Passed, result.Tail) is { } stop)
                {
                    await HeadlessRalphEndAsync(state, files, stop).ConfigureAwait(false);
                    return;
                }

                await HeadlessNoticeLineAsync("[notice] " + RalphText.FinalFailedOnceNotice(final)).ConfigureAwait(false);
                continue;
            }

            var effective = EffectiveSettings;
            var snapshot = loop.Snapshot;
            var step = next.Kind == RalphNextKind.FinalFix ? null : next.Step;
            int total = PlanSteps.Leaves(steps).Count();
            int index = step?.Index ?? total;
            string? finalTail = step is null ? loop.TakeFinalTail() : null;
            var log = RalphLog.Read(files, path);
            loop.BeginPass(step, HeadlessPlanMeter(context.Session));
            string? notes = RalphLog.JoinNotes(loop.TakeNotes(), step is null ? null : RalphLog.RecentNotes(log, step.Text));
            string directive = RalphText.Directive(path, index, total, step?.Text ?? RalphText.FinalFixStep, step?.DoneWhen, snapshot.StepCheck ?? (step is null ? snapshot.FinalCheck : null),
                snapshot.Reviewer != RalphReviewer.Off && step is not null, canAsk: false, notes, RalphLog.LearnedText(log), finalFix: step is null);
            ralph.Turn = new PlanTurn(directive, ralph.Tool);
            ralph.Step = step;
            ralph.Total = total;
            int windowChars = (context.Session.ContextLength?.Tokens ?? 0) * Llm.ConversationCompactor.CharsPerToken;
            string message = step is null
                ? RalphText.FinalFixMessage(path, snapshot.FinalCheck ?? "", finalTail ?? "")
                : RalphText.PassMessage(path, body, index, total, step.Text, step.DoneWhen, windowChars);
            IReadOnlyList<ImageAttachment> images = [];
            if (!loop.DropPicturesNext)
            {
                images = PlanReferences.ForRun(files, path, body, step, effective.LlmPictureKeep, effective.LlmPictureMegabytes, ImageCodecs.Available, out _);
            }

            loop.DropPicturesNext = false;
            ralph.HadPictures = images.Count > 0;
            ralph.Before = RalphChanges.Snapshot(files, ralph.Git, cancellationToken);
            // The task lines as the pass begins, the file's (steps was parsed from it whole): what the judge pairs the lines after with.
            ralph.Lines = PlanSteps.Flat(steps);
            ralph.Line = step is null ? RalphText.FinalFixLine(loop.Pass) : RalphText.TranscriptLine(loop.Pass, index, total, step.Text);
            ralph.Title = RalphText.SessionTitle(snapshot.Title, index, total, loop.Pass);
            Diagnostics.DiagnosticLog.Info(ChatScreen.AppCategory, $"ralph: pass {loop.Pass}, step {index} of {total}: {step?.Text ?? RalphText.FinalFixStep}");
            state.Next = new HeadlessNext(message, ralph.Line, images);
            return;
        }
    }

    /// <summary>After a headless pass's turn: judged, ended, logged and said; then the next pass queued or the loop ended.</summary>
    private async Task HeadlessRalphAfterTurnAsync(HeadlessPlanState state, WorkingDirectory sandbox, HeadlessTurn turn, CancellationToken cancellationToken)
    {
        var ralph = state.Ralph!;
        var loop = ralph.Loop;
        var files = ralph.Files;
        var notices = new List<string>();
        var host = new HeadlessRalphHost(this, ralph, state.RalphContext!, notices);
        RalphStop? stop;
        try
        {
            var judged = await RalphJudge.JudgeAsync(host, loop, ralph.Step, ralph.Total, ralph.Lines, ralph.Before!, ralph.HadPictures, turn.Cancelled, turn.Failed, ralph.Requirement, _time, cancellationToken).ConfigureAwait(false);
            stop = loop.EndPass(judged.Facts, HeadlessPlanMeter(state.RalphContext!.Session));
            if (RalphJudge.LogPass(files, loop, ralph.Step, judged, _time) is { } problem)
            {
                notices.Add(problem);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            stop = RalphStop.Stopped;
        }
        catch (Exception ex)
        {
            // The judgment threw (2026-10-19, the second review, the screen's twin): said, and the loop ends as a stop does, its lock let go.
            Diagnostics.DiagnosticLog.Error(ChatScreen.AppCategory, "ralph: the loop failed", ex);
            notices.Add(RalphText.LoopFailedError(Llm.Assistant.Explain(ex)));
            stop = RalphStop.Stopped;
        }

        ralph.Turn = null;
        foreach (string line in notices)
        {
            await HeadlessNoticeLineAsync("[notice] " + line).ConfigureAwait(false);
        }

        if (stop is { } stopped)
        {
            await HeadlessRalphEndAsync(state, files, stopped).ConfigureAwait(false);
            return;
        }

        await HeadlessRalphQueueAsync(state, sandbox, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A headless loop over: the summary, the stop's notice, the file and the state, the lock let go.</summary>
    private async Task HeadlessRalphEndAsync(HeadlessPlanState state, WorkingDirectory files, RalphStop stop)
    {
        if (state.Ralph is not { } ralph)
        {
            return;
        }

        var loop = ralph.Loop;
        string path = loop.Snapshot.Path;
        // The loop's own folder, whatever the settings say now (2026-10-19, the second review).
        files = ralph.Files;
        state.Ralph = null;
        state.Next = null;
        foreach (string line in RalphText.SummaryLines(loop.History, stop))
        {
            await HeadlessNoticeLineAsync("[notice] " + line.Trim()).ConfigureAwait(false);
        }

        var counts = PlanFiles.ReadWhole(files, path) is { } text ? PlanSteps.Counts(PlanSteps.Parse(PlanDocument.Body(text))) : (0, 0);
        if (stop == RalphStop.StepFailing && loop.Current is { } failing && loop.ChangedBy(failing) is { Count: > 0 } changed)
        {
            await HeadlessNoticeLineAsync("[notice] " + RalphText.LeftChangedLine(changed).Trim()).ConfigureAwait(false);
        }

        if (stop == RalphStop.Done)
        {
            PlanFiles.MarkFile(files, path, PlanStatus.Done, _time.GetLocalNow(), ralph.Requirement, counts);
            await HeadlessNoticeLineAsync("[notice] " + RalphText.StopNotice(stop, path, counts.Item1, counts.Item2)).ConfigureAwait(false);
            await HeadlessNoticeLineAsync("[notice] " + PlanText.DoneNotice(path)).ConfigureAwait(false);
            await HeadlessNoticeLineAsync("[notice] " + PlanText.ComesTogetherNotice).ConfigureAwait(false);
            state.Executing = null;
        }
        else
        {
            if (stop != RalphStop.PlanGone && counts.Item2 > 0)
            {
                PlanFiles.MarkFile(files, path, PlanStatus.Incomplete, _time.GetLocalNow(), ralph.Requirement, counts);
            }

            await HeadlessNoticeLineAsync("[notice] " + RalphText.StopNotice(stop, path, counts.Item1, counts.Item2)).ConfigureAwait(false);
            if (stop == RalphStop.PlanGone)
            {
                state.Executing = null;
            }
            else if (state.Executing is { Ralph: true } paused)
            {
                paused.Paused = true;
            }
        }

        if (RalphLog.Read(files, path)?.Head is { } head)
        {
            RalphLog.SetHead(files, path, head with { Stopped = ChatScreen.RalphStopWord(stop) });
        }

        RalphLog.Unlock(files, path, RalphLog.Me);
    }

    /// <summary>A headless loop dropped with the conversation or the run (its lock let go; the file keeps everything).</summary>
    private static void HeadlessRalphDrop(HeadlessPlanState state, WorkingDirectory files)
    {
        if (state.Ralph is { } ralph)
        {
            RalphLog.Unlock(ralph.Files, ralph.Loop.Snapshot.Path, RalphLog.Me);
            state.Ralph = null;
            state.Next = null;
        }
    }

    /// <summary>
    /// Why a check may not run headless now (2026-10-19, the review): the police's refusal, a shell not installed, or — under <c>Shell command
    /// policy</c> ask, where nobody can be asked — a command not on the allow list, as <c>run_command</c>'s would be; null when it may.
    /// <paramref name="off"/> says the policy is off (no refusal: the loop's start reads it as "the steps go unchecked").
    /// </summary>
    private static string? HeadlessCheckRefusal(string command, ShellKind shell, WorkingDirectory files, AppSettingsData effective, HeadlessRalphContext context, out bool off)
    {
        if (RalphVerifier.Refusal(command, shell, files.Root, files, effective, context.Interpreters.Locate, out off) is { } police)
        {
            return police;
        }

        if (!off && CommandPolicy.Resolve(effective) == CommandPolicyMode.Ask && CommandPrefix.All(command, shell) is var prefixes && (prefixes.Count == 0 || !context.AllowList.AllowsAll(prefixes)))
        {
            return RalphText.HeadlessAskRefusedError(command);
        }

        return null;
    }

}
