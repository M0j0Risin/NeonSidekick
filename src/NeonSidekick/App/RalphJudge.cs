using NeonSidekick.Files;
using NeonSidekick.Git;
using NeonSidekick.Plans;
using NeonSidekick.Shell;

namespace NeonSidekick.App;

/// <summary>
/// What the Ralph loop's judge needs of where it runs (2026-10-18): the screen's transcript and watch, or headless's lines — the files, the
/// repository, where notices and a check's tail go, the check runner, the reviewer, and the log line.
/// </summary>
internal interface IRalphHost
{
    WorkingDirectory Files { get; }

    GitAccess? Git { get; }

    void Notice(string text);

    void Tail(string tail);

    void Log(string line);

    /// <summary>A check run (Esc and the page's Stop cancel it: <see cref="RalphCheckKind.Cancelled"/>); null when it could not run.</summary>
    Task<RalphCheckResult?> CheckAsync(string command, ShellKind shell, string label, CancellationToken cancellationToken);

    /// <summary>The review's verdict; null when it was stopped.</summary>
    Task<RalphVerdict?> ReviewAsync(RalphLoop loop, PlanStep step, FinishReport? report, RalphDelta delta, CancellationToken cancellationToken);
}

/// <summary>What judging a pass found: the facts for the loop, and what the log keeps.</summary>
internal sealed record RalphJudged(RalphPassFacts Facts, string Check, string? Tail, string Review, IReadOnlyList<string> Flags, string Diff);

/// <summary>
/// The Ralph loop's judgment of a pass (2026-10-18), shared by the screen and headless (<see cref="IRalphHost"/>): the plan read again, the boxes put
/// back, the report's outcome acted on, the checks run, the parent ticked, the header's progress written; and the pass's log entry.
/// </summary>
internal static class RalphJudge
{
    /// <summary>
    /// After a pass's turn: the plan read again, another step's box changed during the pass put back; then the report's outcome — <c>blocked</c>
    /// unticks and stops, <c>split</c> writes the children (no checks), <c>done</c> ticks and checks, <c>partial</c> or none with the model's own tick
    /// still checked. The checks: the verify command (a failure, a timeout or Esc unticks, its tail the next pass's note), the evidence, the review
    /// (a fail unticks). A parent whose children all stand is ticked. The header's progress follows. <paramref name="step"/> was parsed from the
    /// plan file whole (<see cref="PlanSteps.Parse"/>), so its line is the file's; <paramref name="lines"/> are the file's task lines before the pass
    /// (<see cref="PlanSteps.Flat"/> of that same parse). A stopped pass decides nothing: its own box goes back as well (2026-10-19, the second review:
    /// a tick the model wrote before Esc stood with no check, and a resume passed the step by).
    /// </summary>
    public static async Task<RalphJudged> JudgeAsync(IRalphHost host, RalphLoop loop, PlanStep? step, int total, IReadOnlyList<PlanStep> lines, RalphSnapshotOfFiles before, bool hadPictures, bool cancelled, bool failed, string requirement, TimeProvider time, CancellationToken cancellationToken)
    {
        var snapshot = loop.Snapshot;
        string path = snapshot.Path;
        var report = loop.Report;
        cancelled |= cancellationToken.IsCancellationRequested;
        if (PlanFiles.ReadWhole(host.Files, path) is not { } text)
        {
            return new RalphJudged(RalphPassFacts.None with { Cancelled = cancelled, TurnFailed = failed }, "none", null, "off", [], "");
        }

        // The task lines now, each paired with the one it was (2026-10-19, the second review: matched by their words, a line the pass reworded,
        // added or deleted escaped the put-back, and a reworded step of its own could not be unticked). The pass's own line is the one its step's
        // became, else the step found by its words.
        var now = PlanSteps.Flat(PlanSteps.Parse(text));
        var pairs = PlanSteps.Align(lines, now);
        PlanStep? own = step is null ? null : OwnLine(lines, now, pairs, step) ?? PlanSteps.Find(text, step);

        // Another step's box the pass changed: put back (the loop ticks; the model never decides another step), a new line's unticked (the loop
        // never ticked it). One write for them all.
        int putBack = 0;
        string restored = text;
        for (int i = 0; i < now.Count; i++)
        {
            var line = now[i];
            bool was = pairs[i] >= 0 && lines[pairs[i]].Ticked;
            if ((own is null || line.Line != own.Line) && line.Ticked != was && PlanSteps.WithBox(restored, line, was) is { } next)
            {
                restored = next;
                putBack++;
            }
        }

        if (putBack > 0 && host.Files.WriteText(path, restored, overwrite: true).Outcome == FileOutcome.Ok)
        {
            text = restored;
            host.Notice(RalphText.BoxesPutBackNotice(putBack));
            host.Log($"ralph: pass {loop.Pass} changed {putBack} other boxes; put back");
        }

        var after = await Task.Run(() => RalphChanges.Snapshot(host.Files, host.Git, CancellationToken.None)).ConfigureAwait(false);
        var delta = RalphChanges.Delta(before, after, host.Files, host.Git, step?.NamedPaths ?? []);
        if (cancelled)
        {
            if (own is { Ticked: true } && step is { Ticked: false })
            {
                SetBox(host, path, own, false);
                host.Log($"ralph: pass {loop.Pass} was stopped with its own box ticked; put back");
            }

            return new RalphJudged(new RalphPassFacts(false, false, delta.Changed.Count > 0, false, failed, true, delta.Changed), "none", null, "off", delta.Flags, delta.Diff);
        }

        if (failed && report is null && hadPictures)
        {
            // A failed pass that carried the plan's pictures: the retry goes without them (a text-only server refuses a request with pictures).
            loop.DropPicturesNext = true;
            host.Notice(RalphText.PicturesDroppedNotice);
        }

        if (failed && report is null)
        {
            host.Notice(RalphText.RetryNotice);
        }

        if (step is null)
        {
            // The final fix: its check is the final check's, run next.
            return new RalphJudged(new RalphPassFacts(report?.Outcome == RalphOutcome.Done, false, delta.Changed.Count > 0, false, failed, false, delta.Changed), "none", null, "off", delta.Flags, delta.Diff);
        }

        bool split = false;
        bool stands = false;
        bool checkFailed = false;
        string check = "none";
        string? tail = null;
        string review = "off";
        // The step's line as the pass left it (reworded, it is still this step's): every box below is set there.
        var target = own ?? step;
        bool modelTicked = own?.Ticked ?? false;
        switch (report?.Outcome)
        {
            case RalphOutcome.Blocked:
                SetBox(host, path, target, false);
                host.Notice(RalphText.BlockedNotice(report.Summary));
                break;
            case RalphOutcome.Split:
                if (PlanFiles.ReadWhole(host.Files, path) is { } whole && PlanSteps.WithChildren(whole, target, report.Substeps) is { } withChildren
                    && host.Files.WriteText(path, withChildren, overwrite: true).Outcome == FileOutcome.Ok)
                {
                    split = true;
                    SetBox(host, path, target, false);
                    host.Notice(RalphText.SplitNotice(report.Substeps.Count, step.Text));
                }

                break;
            default:
                bool ticked = report?.Outcome == RalphOutcome.Done || modelTicked;
                if (!ticked)
                {
                    if (report?.Outcome == RalphOutcome.Partial && delta.Changed.Count > 0)
                    {
                        host.Notice(RalphText.PartialNotice(delta.Changed.Count));
                    }

                    break;
                }

                SetBox(host, path, target, true);
                if (delta.NothingChanged && report?.Outcome == RalphOutcome.Done)
                {
                    host.Notice(RalphText.NothingChangedNotice);
                }

                if (snapshot.StepCheck is { } verify)
                {
                    var result = await host.CheckAsync(verify, ShellOf(snapshot), RalphText.CheckLabel(verify), cancellationToken).ConfigureAwait(false);
                    check = result?.Word ?? "not run";
                    if (result is { Kind: RalphCheckKind.Cancelled } || cancellationToken.IsCancellationRequested)
                    {
                        SetBox(host, path, target, false);
                        host.Notice(RalphText.CheckStoppedNotice);
                        return new RalphJudged(new RalphPassFacts(false, false, delta.Changed.Count > 0, false, failed, true, delta.Changed), check, null, review, delta.Flags, delta.Diff);
                    }

                    if (result is not { Passed: true })
                    {
                        SetBox(host, path, target, false);
                        checkFailed = true;
                        tail = result?.Tail;
                        host.Notice(result is { Kind: RalphCheckKind.TimedOut } ? RalphText.CheckTimedOutNotice(verify, snapshot.CheckTimeoutSeconds) : RalphText.CheckFailedNotice(verify));
                        host.Tail(tail ?? "");
                        loop.PendingNotes = RalphText.CheckFailedNote(verify, RalphLog.CleanTail(tail ?? ""));
                        break;
                    }
                }

                if (snapshot.Reviewer != RalphReviewer.Off)
                {
                    var reviewed = await host.ReviewAsync(loop, step, report, delta, cancellationToken).ConfigureAwait(false);
                    if (reviewed is not { } verdict || cancellationToken.IsCancellationRequested)
                    {
                        // Esc under the review stops the loop, the step unticked.
                        SetBox(host, path, target, false);
                        host.Notice(RalphText.CheckStoppedNotice);
                        return new RalphJudged(new RalphPassFacts(false, false, delta.Changed.Count > 0, false, failed, true, delta.Changed), check, tail, "stopped", delta.Flags, delta.Diff);
                    }

                    review = verdict.Kind switch
                    {
                        RalphVerdictKind.Pass => "pass" + (verdict.Reason.Length > 0 ? ": " + verdict.Reason : ""),
                        RalphVerdictKind.Fail => "fail" + (verdict.Reason.Length > 0 ? ": " + verdict.Reason : ""),
                        _ => "unclear",
                    };

                    if (verdict.Kind == RalphVerdictKind.Fail)
                    {
                        SetBox(host, path, target, false);
                        host.Notice(RalphText.ReviewFailedNotice(verdict.Reason));
                        loop.PendingNotes = RalphText.ReviewFailedNote(verdict.Reason);
                        break;
                    }

                    if (verdict.Kind == RalphVerdictKind.Unclear)
                    {
                        host.Notice(RalphText.ReviewUnclearNotice);
                    }
                }

                stands = true;
                host.Notice(snapshot.StepCheck is null && snapshot.Reviewer == RalphReviewer.Off
                    ? RalphText.StepUncheckedNotice(step.Index, total, step.Text)
                    : RalphText.StepDoneNotice(step.Index, total, step.Text));
                break;
        }

        // A parent whose children all stand is ticked by the app (found by the step's own line, the file's: never another parent's child of the same words).
        if (stands && PlanFiles.ReadWhole(host.Files, path) is { } latest)
        {
            var parsed = PlanSteps.Parse(latest);
            if (PlanSteps.Find(latest, target) is { } standing && PlanSteps.ParentOf(parsed, standing) is { Ticked: false } parent && parent.Children.All(c => c.Ticked))
            {
                SetBox(host, path, parent, true);
                host.Notice(RalphText.ParentTickedNotice(parent.Text));
            }
        }

        MarkProgress(host.Files, path, requirement, time);
        return new RalphJudged(new RalphPassFacts(stands, checkFailed, delta.Changed.Count > 0, split, failed, false, delta.Changed), check, tail, review, delta.Flags, delta.Diff);
    }

    /// <summary>A step's box set in the file (the loop's own tick), quietly; a step no longer where it was is left (a log line).</summary>
    public static void SetBox(IRalphHost host, string path, PlanStep step, bool ticked)
    {
        if (PlanFiles.SetStepBox(host.Files, path, step, ticked) is { } problem)
        {
            host.Log("ralph: " + problem);
        }
    }

    /// <summary>The line <paramref name="step"/>'s became under the pass (<see cref="PlanSteps.Align"/>'s pair of its line); null when it went or cannot be told.</summary>
    private static PlanStep? OwnLine(IReadOnlyList<PlanStep> lines, IReadOnlyList<PlanStep> now, int[] pairs, PlanStep step)
    {
        for (int i = 0; i < now.Count; i++)
        {
            if (pairs[i] >= 0 && lines[pairs[i]].Line == step.Line)
            {
                return now[i];
            }
        }

        return null;
    }

    /// <summary>The header's status and progress over the loop's leaves: incomplete while any is left.</summary>
    public static void MarkProgress(WorkingDirectory files, string path, string requirement, TimeProvider time)
    {
        if (PlanFiles.ReadWhole(files, path) is not { } text)
        {
            return;
        }

        var counts = PlanSteps.Counts(PlanSteps.Parse(PlanDocument.Body(text)));
        var header = PlanDocument.TryParse(text);
        if (header is not { Status: PlanStatus.Incomplete } || header.StepsDone != counts.Done || header.StepsTotal != counts.Total)
        {
            PlanFiles.MarkFile(files, path, PlanStatus.Incomplete, time.GetLocalNow(), requirement.Length > 0 ? requirement : header?.Requirement ?? "", counts);
        }
    }

    /// <summary>The shell a snapshot names (PowerShell for one it does not know).</summary>
    public static ShellKind ShellOf(RalphSnapshot snapshot) => ShellKinds.TryParse(snapshot.Shell, out var kind) ? kind : ShellKind.PowerShell;

    /// <summary>The pass's entry in the log, its learned facts merged; the error sentence when it could not be written.</summary>
    public static string? LogPass(WorkingDirectory files, RalphLoop loop, PlanStep? step, RalphJudged judged, TimeProvider time)
    {
        var snapshot = loop.Snapshot;
        var record = loop.History.Count > 0 ? loop.History[^1] : null;
        var report = loop.Report;
        var entry = new RalphLogEntry(
            loop.Pass, step?.Index ?? 0, step?.Text ?? RalphText.FinalFixStep, record?.Outcome ?? "none", report?.Summary ?? "(no finish_step)", report?.Verified ?? [],
            judged.Check, judged.Tail, judged.Flags, judged.Facts.Changed, judged.Review, null, time.GetLocalNow(), record?.Elapsed, record?.Tokens);
        var head = new RalphLogHead(snapshot.Path, snapshot.Verify, snapshot.Final, snapshot.FinalOnly, snapshot.Shell, time.GetLocalNow());
        if (RalphLog.Read(files, snapshot.Path)?.Head is { } kept)
        {
            head = head with { Started = kept.Started };
        }

        return RalphLog.Append(files, snapshot.Path, head, entry, report?.Learned ?? []);
    }
}
