using NeonSidekick.Diagnostics;
using NeonSidekick.Files;
using NeonSidekick.Plans;
using NeonSidekick.Sessions;
using NeonSidekick.Settings;
using NeonSidekick.Shell;

namespace NeonSidekick.App;

/// <summary>
/// What a Ralph loop's start needs of where it runs (2026-10-19, the fourth review: the screen and headless each carried the whole start, and each
/// fix of the three reviews before was made twice, the copies drifting): where its lines go, a check's refusal now, a check run, and the answer to a
/// failing baseline — the screen's confirm, or headless's rule.
/// </summary>
internal interface IRalphStartHost
{
    Task NoticeAsync(string text);

    Task ErrorAsync(string text);

    Task TailAsync(string tail);

    /// <summary>Why <paramref name="command"/> may not run now (the police, a shell not installed, headless's allow list); <paramref name="off"/> for <c>Shell command policy</c> off.</summary>
    string? Refusal(string command, ShellKind shell, out bool off);

    /// <summary>The first script <paramref name="command"/> runs that isn't there yet (<see cref="RalphVerifier.MissingScript"/>); null when none.</summary>
    string? MissingScript(string command);

    /// <summary>A baseline check run (the refusal asked again: <see cref="RalphCheckKind.Refused"/>, said already); null when it could not run.</summary>
    Task<RalphCheckResult?> CheckAsync(string command, ShellKind shell, string label, CancellationToken cancellationToken);

    /// <summary>
    /// A failing final baseline's answer: true to start anyway, the steps still checked. The host says what a no means. A failing verify asks nothing
    /// since 2026-11-24 (the session log review): it is pending, tried after each step until it first passes.
    /// </summary>
    Task<bool> StartAnywayAsync(string command, CancellationToken cancellationToken);
}

/// <summary>How a loop's baseline ended (<see cref="RalphStart.BaselineAsync"/>).</summary>
internal enum RalphBaselineOutcome
{
    /// <summary>The loop may start (with <see cref="RalphBaseline.FinalOnly"/> and <see cref="RalphBaseline.Unchecked"/> as said).</summary>
    Go,

    /// <summary>A check was refused (the host said why): nothing starts.</summary>
    Refused,

    /// <summary>A check was stopped (Esc, the page's Stop) or could not run: nothing starts.</summary>
    Cancelled,

    /// <summary>A check failed and the answer was no: nothing starts.</summary>
    Declined,
}

/// <summary>A baseline's outcome and what the run is fixed to by it: final-only checking, or no checks at all (the policy off).</summary>
internal sealed record RalphBaseline(RalphBaselineOutcome Outcome, bool FinalOnly, bool Unchecked);

/// <summary>What a loop's end said and left (<see cref="RalphStart.End"/>): its lines (a warning flagged), and whether the plan's stored state goes.</summary>
internal sealed record RalphEnded(IReadOnlyList<(string Text, bool Warning)> Lines, bool Forget);

/// <summary>
/// A Ralph loop's start and end, shared by the screen (<c>ChatScreen.BeginLockedRalphAsync</c>, <c>EndRalph</c>) and headless
/// (<c>SidekickApp.HeadlessRalphBeginLockedAsync</c>, <c>HeadlessRalphEndAsync</c>) (2026-10-19, the fourth review: written twice, they had drifted —
/// headless ran the final command whenever verify was distinct, and refused where the screen asked): the recovery of a pass the last run left
/// unjudged, the police and the policy, the baseline, the snapshot, the stored state and the log's head, and the end's summary, marks and unlock.
/// </summary>
internal static class RalphStart
{
    /// <summary>
    /// A pass the last run left unjudged put back (<see cref="RalphJudge.RecoverInPass"/>), its notices said: <c>Go</c> false when it deleted
    /// unfinished steps (the error said), <c>Changed</c> when the plan must be read again.
    /// </summary>
    public static async Task<(bool Go, bool Changed)> RecoverAsync(IRalphStartHost host, WorkingDirectory files, string path)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (RalphJudge.RecoverInPass(files, path) is not { } recovered)
        {
            return (true, false);
        }

        foreach (string notice in recovered.Notices)
        {
            await host.NoticeAsync(notice).ConfigureAwait(false);
        }

        if (recovered.Removed.Count > 0)
        {
            await host.ErrorAsync(RalphText.InPassRemovedError(path, recovered.Removed)).ConfigureAwait(false);
            return (false, recovered.Changed);
        }

        return (true, recovered.Changed);
    }

    /// <summary>
    /// The police and the policy over both commands (a refusal stops the start; the policy off runs nothing, the steps unchecked this run), then the
    /// baseline: the verify command and the final one, in that order. A failing verify asks nothing (2026-11-24, the session log review: a yes to "start
    /// anyway?" left every step unchecked by the app): the check is pending (<see cref="RalphBaseline.FinalOnly"/>), said, tried after each step until it
    /// first passes, and the final baseline isn't run, since a project with no harness yet fails both for one reason. A verify that passes now adopts
    /// it, a resume's pending one too (<paramref name="finalOnly"/>). A failing final one asks "start anyway?", a yes keeping the steps checked
    /// (2026-10-19, the fourth review: a final check that tests the work still to do fails before the loop by design). A check whose script isn't there yet (2026-10-24, the user's report: a plan whose step writes the script its check runs
    /// was refused, and with the police off its failing baseline would have dropped the step checks) is skipped with a notice, police on or off, no
    /// question asked and the steps still checked; the pre-check lets it past the police (<c>missingScriptsPass</c>), and each run asks them again.
    /// </summary>
    public static async Task<RalphBaseline> BaselineAsync(IRalphStartHost host, string? verify, string? final, bool finalOnly, ShellKind shell, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host);
        foreach (string command in new[] { verify, final }.OfType<string>().Where(c => c.Length > 0).Distinct(StringComparer.Ordinal))
        {
            if (host.Refusal(command, shell, out bool off) is { } refused)
            {
                await host.ErrorAsync(refused).ConfigureAwait(false);
                return new RalphBaseline(RalphBaselineOutcome.Refused, finalOnly, false);
            }

            if (off)
            {
                // This run goes unchecked (the snapshot's Unchecked); the approved commands stay in the stored state and the log for a resume.
                await host.NoticeAsync(RalphText.PolicyOffUncheckedNotice).ConfigureAwait(false);
                return new RalphBaseline(RalphBaselineOutcome.Go, finalOnly, true);
            }
        }

        string? stepCheck = verify;
        bool pending = finalOnly;
        foreach (string command in new[] { stepCheck, final }.OfType<string>().Where(c => c.Length > 0).Distinct(StringComparer.Ordinal))
        {
            // A check whose script isn't there yet is skipped, whatever the police: the plan writes it, so it can't pass before a step.
            if (host.MissingScript(command) is { } missing)
            {
                await NotThereAsync(host, command, missing, shell).ConfigureAwait(false);
                continue;
            }

            var baseline = await host.CheckAsync(command, shell, RalphText.BaselineLabel, cancellationToken).ConfigureAwait(false);
            if (baseline is null or { Kind: RalphCheckKind.Cancelled })
            {
                return new RalphBaseline(RalphBaselineOutcome.Cancelled, finalOnly, false);
            }

            if (baseline.Kind == RalphCheckKind.Refused)
            {
                return new RalphBaseline(RalphBaselineOutcome.Refused, finalOnly, false);
            }

            // The per-step command (one that is the final command too counts as it).
            bool isVerify = string.Equals(command, stepCheck, StringComparison.Ordinal);
            if (baseline.Passed)
            {
                pending &= !isVerify;   // a pending check that passes now is adopted at once
                continue;
            }

            // The script went missing between the two asks (a race): skipped the same way.
            if (baseline is { Kind: RalphCheckKind.NotThereYet, Script: { } gone })
            {
                await NotThereAsync(host, command, gone, shell).ConfigureAwait(false);
                continue;
            }

            await host.TailAsync(baseline.Tail).ConfigureAwait(false);
            if (isVerify)
            {
                // Pending: tried after each step, adopted the first time it passes, else run once at the end in place of a final that is missing.
                await host.NoticeAsync(RalphText.BaselinePendingNotice(command)).ConfigureAwait(false);
                return new RalphBaseline(RalphBaselineOutcome.Go, true, false);
            }

            if (!await host.StartAnywayAsync(command, cancellationToken).ConfigureAwait(false))
            {
                return new RalphBaseline(RalphBaselineOutcome.Declined, pending, false);
            }
        }

        return new RalphBaseline(RalphBaselineOutcome.Go, pending, false);
    }

    /// <summary>A baseline check skipped because <paramref name="script"/> isn't there yet: said and logged.</summary>
    private static async Task NotThereAsync(IRalphStartHost host, string command, string script, ShellKind shell)
    {
        DiagnosticLog.Info(ShellKinds.Category, $"ralph baseline: skipped, script not there yet ('{script}') — {ShellKinds.Name(shell)} {command}");
        await host.NoticeAsync(RalphText.BaselineNotThereNotice(command, script)).ConfigureAwait(false);
    }

    /// <summary>
    /// Every step ticked: the final check (when there is one) run through <paramref name="host"/>, its fix owed or the loop done (2026-10-20, a review:
    /// the screen and headless each had a copy, already apart — a check that could not run was the user's stop on one and a throw on the other).
    /// Esc stops; a refusal, a check that could not start or could not run (null) stops as no stop the user caused; a failure the first time is said
    /// with its tail, its fix pass owed.
    /// </summary>
    public static async Task<RalphStop?> FinalCheckAsync(IRalphHost host, RalphLoop loop, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(loop);
        if (loop.Snapshot.FinalCheck is not { } final)
        {
            return RalphStop.Done;
        }

        var result = await host.CheckAsync(final, RalphJudge.ShellOf(loop.Snapshot), RalphText.CheckLabel(final), cancellationToken).ConfigureAwait(false);
        if (result is { Kind: RalphCheckKind.Cancelled } || cancellationToken.IsCancellationRequested)
        {
            return RalphStop.Stopped;
        }

        if (result is null or { Kind: RalphCheckKind.Refused or RalphCheckKind.NotRun })
        {
            if (result is { Kind: RalphCheckKind.NotRun } notRun)
            {
                host.Notice(RalphText.CheckNotRunError(final, notRun.Tail));
            }

            return RalphStop.CheckRefused;
        }

        var stop = loop.FinalResult(result.Passed, result.Tail);
        if (stop is null)
        {
            host.Notice(RalphText.FinalFailedOnceNotice(final));
            host.Tail(result.Tail);
        }

        return stop;
    }

    /// <summary>What a run is fixed to at its start, from the settings now (<paramref name="checkpoint"/> 0 headless: nobody to answer it).</summary>
    public static RalphSnapshot Snapshot(string path, string root, string body, string? verify, string? final, RalphBaseline baseline, ShellKind shell, AppSettingsData effective, int checkpoint, int? passes, RalphLogDocument? log)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(effective);
        string title = PlanDocument.FirstHeading(body) ?? System.IO.Path.GetFileNameWithoutExtension(path);
        return new RalphSnapshot(path, root, title, verify, final, baseline.FinalOnly, ShellKinds.Name(shell), RalphReviewerMode.Resolve(effective), checkpoint,
            passes ?? Math.Clamp(effective.RalphLoopPasses, AppSettingsData.MinRalphLoopPasses, AppSettingsData.MaxRalphLoopPasses),
            Math.Clamp(effective.RalphLoopPassesPerStep, AppSettingsData.MinRalphLoopPassesPerStep, AppSettingsData.MaxRalphLoopPassesPerStep),
            Math.Clamp(effective.RalphLoopCheckTimeoutSeconds, AppSettingsData.MinRalphLoopCheckTimeoutSeconds, AppSettingsData.MaxRalphLoopCheckTimeoutSeconds),
            Math.Max(0, effective.RalphLoopTokenBudget), RalphLog.PassCount(log) + 1, Unchecked: baseline.Unchecked);
    }

    /// <summary>The check timeout from the settings now: a baseline's, before a loop's snapshot holds one.</summary>
    public static int CheckTimeoutSeconds(AppSettingsData effective) =>
        Math.Clamp(effective.RalphLoopCheckTimeoutSeconds, AppSettingsData.MinRalphLoopCheckTimeoutSeconds, AppSettingsData.MaxRalphLoopCheckTimeoutSeconds);

    /// <summary>The plan state a running loop is stored as, for a resume.</summary>
    public static StoredPlan Stored(RalphSnapshot snapshot, string requirement)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new StoredPlan { Path = snapshot.Path, Requirement = requirement, Ralph = true, Verify = snapshot.Verify, Final = snapshot.Final, FinalOnly = snapshot.FinalOnly, Shell = snapshot.Shell, Root = snapshot.Root };
    }

    /// <summary>The log's head as a run begins.</summary>
    public static RalphLogHead Head(RalphSnapshot snapshot, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new RalphLogHead(snapshot.Path, snapshot.Verify, snapshot.Final, snapshot.FinalOnly, snapshot.Shell, now);
    }

    /// <summary>
    /// The loop over in its own folder: the summary, the files a failing step left changed, the plan marked done (else incomplete while steps are
    /// left), the stop's notice, the log's head saying how it stopped, the lock let go. The caller says the lines, forgets or pauses the stored plan.
    /// <paramref name="canAsk"/> is false headless, where the stop's notice tells the user to tick a checked step's box (<see cref="RalphText.StopNotice"/>).
    /// <paramref name="clicked"/> (2026-11-23): a pause 🔁's double-click asked for, worded so.
    /// </summary>
    public static RalphEnded End(WorkingDirectory files, RalphLoop loop, RalphStop stop, string requirement, TimeProvider time, bool canAsk = true, bool clicked = false)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(loop);
        ArgumentNullException.ThrowIfNull(time);
        string path = loop.Snapshot.Path;
        var lines = RalphText.SummaryLines(loop.History, stop).Select(l => (l, false)).ToList();
        var counts = PlanFiles.ReadWhole(files, path) is { } text ? PlanSteps.Counts(PlanSteps.Parse(PlanDocument.Body(text))) : (0, 0);
        // A step that keeps failing, or one waiting for the user's check (2026-10-21: the files to look at when checking it), lists what its passes changed.
        if (stop is RalphStop.StepFailing or RalphStop.NeedsUser && loop.Current is { } failing && loop.ChangedBy(failing) is { Count: > 0 } changed)
        {
            lines.Add((RalphText.LeftChangedLine(changed), false));
        }

        bool forget = false;
        if (stop == RalphStop.Locked)
        {
            // Another instance holds the plan now (2026-10-20, a review, RalphLog.StillHeld): its plan and its log are that one's to write.
            lines.Add((RalphText.StopNotice(stop, path, counts.Item1, counts.Item2), true));
            return new RalphEnded(lines, forget);
        }

        if (stop == RalphStop.Done)
        {
            if (PlanFiles.MarkFile(files, path, PlanStatus.Done, time.GetLocalNow(), requirement, counts) is { } problem)
            {
                lines.Add((problem, true));
            }

            lines.Add((RalphText.StopNotice(stop, path, counts.Item1, counts.Item2), false));
            lines.Add((PlanText.DoneNotice(path), false));
            lines.Add((PlanText.ComesTogetherNotice, false));
            // A fix plan done (2026-11-22, the session review): the resume that checks its fixes held, its requirement the file's own (the screen's
            // argument is the stored plan's, empty after a restart).
            if (PlanFiles.ReadWhole(files, path) is { } marked && Audits.AuditTicks.ReauditHintFor(files, PlanDocument.TryParse(marked)?.Requirement) is { } hint)
            {
                lines.Add((hint, false));
            }

            forget = true;
        }
        else if (stop == RalphStop.PlanGone)
        {
            lines.Add((RalphText.StopNotice(stop, path, counts.Item1, counts.Item2), false));
            forget = true;
        }
        else
        {
            if (counts.Item2 > 0)
            {
                PlanFiles.MarkFile(files, path, PlanStatus.Incomplete, time.GetLocalNow(), requirement, counts);
            }

            lines.Add((RalphText.StopNotice(stop, path, counts.Item1, counts.Item2, canAsk, clicked), false));
            // A stall or a failing step after a failed review says why, after its notice (2026-10-21, RalphLoop.LastReviewFail).
            if (stop is RalphStop.Stalled or RalphStop.StepFailing && loop.LastReviewFail is { Length: > 0 } reason)
            {
                lines.Add((RalphText.LastReviewLine(reason), false));
            }
        }

        if (RalphLog.Read(files, path)?.Head is { } head)
        {
            RalphLog.SetHead(files, path, head with { Stopped = RalphText.StopWord(stop) });
        }

        RalphLog.Unlock(files, path, RalphLog.Me);
        return new RalphEnded(lines, forget);
    }

    /// <summary>
    /// A resume ended before its loop was built (2026-10-22, a review: the by-hand question a resume asks first comes before the baseline now, so a Stop
    /// or nobody to answer ends a run that never began, and a failing baseline no longer refuses a resume that would stop there anyway): the files the
    /// waiting step's passes changed for a <see cref="RalphStop.NeedsUser"/> (<paramref name="changed"/>, from its log entry: no pass of this run made
    /// them, 2026-10-22, a review), the stop's notice over the plan's counts (<paramref name="canAsk"/> as <see cref="RalphText.StopNotice"/>'s), and the
    /// log's head saying how it stopped. The plan stays as it was; the lock is the caller's to let go.
    /// </summary>
    public static IReadOnlyList<string> EndBeforeLoop(WorkingDirectory files, string path, RalphStop stop, IReadOnlyList<string> changed, bool canAsk)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(changed);
        var counts = PlanFiles.ReadWhole(files, path) is { } text ? PlanSteps.Counts(PlanSteps.Parse(PlanDocument.Body(text))) : (0, 0);
        var lines = new List<string>(2);
        if (stop == RalphStop.NeedsUser && changed.Count > 0)
        {
            lines.Add(RalphText.LeftChangedLine(changed));
        }

        lines.Add(RalphText.StopNotice(stop, path, counts.Item1, counts.Item2, canAsk));
        if (RalphLog.Read(files, path)?.Head is { } head)
        {
            RalphLog.SetHead(files, path, head with { Stopped = RalphText.StopWord(stop) });
        }

        return lines;
    }

    /// <summary>
    /// Whether the user caused <paramref name="stop"/> (Esc, a message, the checkpoint's or the by-hand question's Stop): the stop tone rings for every
    /// other, <see cref="RalphStop.NeedsUser"/> among them.
    /// </summary>
    public static bool UserCaused(RalphStop stop) => stop is RalphStop.Stopped or RalphStop.Paused or RalphStop.UserStopped;

}
