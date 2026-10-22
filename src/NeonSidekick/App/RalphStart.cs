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

    /// <summary>A baseline check run (the refusal asked again: <see cref="RalphCheckKind.Refused"/>, said already); null when it could not run.</summary>
    Task<RalphCheckResult?> CheckAsync(string command, ShellKind shell, string label, CancellationToken cancellationToken);

    /// <summary>
    /// A failing baseline's answer: true to start anyway. <paramref name="verify"/> says the per-step command failed (yes leaves the steps unchecked);
    /// false, only the final one (the steps stay checked). The host says what a no means.
    /// </summary>
    Task<bool> StartAnywayAsync(string command, bool verify, CancellationToken cancellationToken);
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
    /// baseline: the verify command (skipped under <paramref name="finalOnly"/>) and the final one, in that order. A failing verify asks "start anyway?"
    /// (yes: final-only checking, the final baseline not run); a failing final one asks too, but a yes keeps the steps checked (2026-10-19, the fourth
    /// review: it set final-only, dropping the per-step verify that had just passed, though a final check that tests the work still to do fails before
    /// the loop by design).
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

        string? stepCheck = finalOnly ? null : verify;
        foreach (string command in new[] { stepCheck, final }.OfType<string>().Where(c => c.Length > 0).Distinct(StringComparer.Ordinal))
        {
            var baseline = await host.CheckAsync(command, shell, RalphText.BaselineLabel, cancellationToken).ConfigureAwait(false);
            if (baseline is null or { Kind: RalphCheckKind.Cancelled })
            {
                return new RalphBaseline(RalphBaselineOutcome.Cancelled, finalOnly, false);
            }

            if (baseline.Kind == RalphCheckKind.Refused)
            {
                return new RalphBaseline(RalphBaselineOutcome.Refused, finalOnly, false);
            }

            if (baseline.Passed)
            {
                continue;
            }

            // The per-step command (one that is the final command too counts as it: a failure leaves it for the end).
            bool isVerify = string.Equals(command, stepCheck, StringComparison.Ordinal);
            await host.TailAsync(baseline.Tail).ConfigureAwait(false);
            if (!await host.StartAnywayAsync(command, isVerify, cancellationToken).ConfigureAwait(false))
            {
                return new RalphBaseline(RalphBaselineOutcome.Declined, finalOnly, false);
            }

            if (isVerify)
            {
                // Start anyway: the steps unchecked, the verify command run once at the end in place of a final that is missing.
                return new RalphBaseline(RalphBaselineOutcome.Go, true, false);
            }
        }

        return new RalphBaseline(RalphBaselineOutcome.Go, finalOnly, false);
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
    /// </summary>
    public static RalphEnded End(WorkingDirectory files, RalphLoop loop, RalphStop stop, string requirement, TimeProvider time, bool canAsk = true)
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

            lines.Add((RalphText.StopNotice(stop, path, counts.Item1, counts.Item2, canAsk), false));
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
