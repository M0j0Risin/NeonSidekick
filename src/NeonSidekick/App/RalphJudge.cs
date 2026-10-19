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

/// <summary>
/// What judging a pass found: the facts for the loop, and what the log keeps. <paramref name="Target"/> is the step's line as the judge left it and
/// <paramref name="Parent"/> the parent it ticked (2026-10-19, the third review: the checkpoint's Redo unticked the step's line as it was before the
/// pass, and left a parent the judge had ticked standing).
/// </summary>
internal sealed record RalphJudged(RalphPassFacts Facts, string Check, string? Tail, string Review, IReadOnlyList<string> Flags, string Diff, PlanStep? Target = null, PlanStep? Parent = null);

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

        // The task lines put back as they were (Restore: every other box, every step's words, the pass's own box too when it was stopped, the lines it
        // added taken out); the steps it deleted said, and the loop stops on them (2026-10-19, the third review: deleting the steps left let a plan read
        // as done) with no check run and its own tick put back too (2026-10-19, the fourth review: the verify and the review ran first, for nothing).
        var restore = Restore(text, lines, step, ownBoxBack: cancelled);
        if (restore.Removed.Count > 0 && !cancelled)
        {
            restore = Restore(text, lines, step, ownBoxBack: true);
        }

        var removed = restore.Removed;
        var (written, asLeft) = Apply(host.Files, path, text, restore);
        if (written)
        {
            text = restore.Text;
            foreach (string notice in RestoreNotices(restore, asLeft))
            {
                host.Notice(notice);
            }

            if (restore.OwnBoxPutBack)
            {
                host.Log($"ralph: pass {loop.Pass} ended with its own box ticked and no judgment to stand on; put back");
            }

            host.Log($"ralph: pass {loop.Pass} changed {restore.BoxesPutBack} other boxes and the words of {restore.WordsPutBack} steps, and added {restore.Added}; put back");
        }

        if (removed.Count > 0)
        {
            host.Notice(RalphText.StepsRemovedNotice(removed));
            host.Log($"ralph: pass {loop.Pass} deleted {removed.Count} unfinished steps");
        }

        PlanStep? own = restore.Own;
        var after = await Task.Run(() => RalphChanges.Snapshot(host.Files, host.Git, CancellationToken.None)).ConfigureAwait(false);
        var delta = RalphChanges.Delta(before, after, host.Files, host.Git, step?.NamedPaths ?? []);
        if (cancelled)
        {
            return new RalphJudged(new RalphPassFacts(false, false, delta.Changed.Count > 0, false, failed, true, delta.Changed, removed), "none", null, "off", delta.Flags, delta.Diff);
        }

        if (removed.Count > 0)
        {
            return new RalphJudged(new RalphPassFacts(false, false, delta.Changed.Count > 0, false, failed, false, delta.Changed, removed), "none", null, "off", delta.Flags, delta.Diff);
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
            return new RalphJudged(new RalphPassFacts(report?.Outcome == RalphOutcome.Done, false, delta.Changed.Count > 0, false, failed, false, delta.Changed, removed), "none", null, "off", delta.Flags, delta.Diff);
        }

        bool split = false;
        bool stands = false;
        bool checkFailed = false;
        string check = "none";
        string? tail = null;
        string review = "off";
        // The step's line as the pass left it, its words put back: every box below is set there.
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
                    if (result is { Kind: RalphCheckKind.Refused })
                    {
                        // Refused as it was due (the host said why): unticked, and the loop stops as no stop the user caused (2026-10-19, the fourth review).
                        SetBox(host, path, target, false);
                        return new RalphJudged(new RalphPassFacts(false, false, delta.Changed.Count > 0, false, failed, false, delta.Changed, removed, CheckRefused: true), check, null, review, delta.Flags, delta.Diff);
                    }

                    if (result is { Kind: RalphCheckKind.Cancelled } || cancellationToken.IsCancellationRequested)
                    {
                        SetBox(host, path, target, false);
                        host.Notice(RalphText.CheckStoppedNotice);
                        return new RalphJudged(new RalphPassFacts(false, false, delta.Changed.Count > 0, false, failed, true, delta.Changed, removed), check, null, review, delta.Flags, delta.Diff);
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
                        return new RalphJudged(new RalphPassFacts(false, false, delta.Changed.Count > 0, false, failed, true, delta.Changed, removed), check, tail, "stopped", delta.Flags, delta.Diff);
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
        PlanStep? parentTicked = null;
        if (stands && PlanFiles.ReadWhole(host.Files, path) is { } latest)
        {
            var parsed = PlanSteps.Parse(latest);
            if (PlanSteps.Find(latest, target) is { } standing && PlanSteps.ParentOf(parsed, standing) is { Ticked: false } parent && parent.Children.All(c => c.Ticked))
            {
                SetBox(host, path, parent, true);
                parentTicked = parent;
                host.Notice(RalphText.ParentTickedNotice(parent.Text));
            }
        }

        MarkProgress(host.Files, path, requirement, time);
        return new RalphJudged(new RalphPassFacts(stands, checkFailed, delta.Changed.Count > 0, split, failed, false, delta.Changed, removed), check, tail, review, delta.Flags, delta.Diff, target, parentTicked);
    }

    /// <summary>A step's box set in the file (the loop's own tick), quietly; a step no longer where it was is left (a log line).</summary>
    public static void SetBox(IRalphHost host, string path, PlanStep step, bool ticked)
    {
        if (PlanFiles.SetStepBox(host.Files, path, step, ticked) is { } problem)
        {
            host.Log("ralph: " + problem);
        }
    }

    /// <summary>What <see cref="Restore"/> made of the plan after a pass: <paramref name="Added"/> counts the task lines it took out.</summary>
    internal sealed record RalphRestore(string Text, PlanStep? Own, int BoxesPutBack, int WordsPutBack, bool OwnBoxPutBack, IReadOnlyList<string> Removed, int Added = 0);

    /// <summary>
    /// The plan's task lines after a pass (<paramref name="text"/>) put back as they were before it (<paramref name="lines"/>, <see cref="PlanSteps.Flat"/>
    /// of the file whole), each line paired with the one it was (<see cref="PlanSteps.Align"/>, then a line still unpaired with one of the same words:
    /// a moved line): every other step's box (the loop ticks; the model never decides another step), and every step's words — the pass's own too
    /// (2026-10-19, the third review: a pass that reworded its line, or a later one, turned its step into other work, and gave its step fresh counters,
    /// past its pass limit). A line the pass added is taken out with its own lines under it (2026-10-19, the fourth review: kept unticked, it became a
    /// step nobody approved, which later passes carried out). The pass's own box stays as the model left it unless <paramref name="ownBoxBack"/> (a
    /// stopped pass). <see cref="RalphRestore.Removed"/> holds the unfinished steps the pass deleted, which cannot be written back. Pure.
    /// </summary>
    internal static RalphRestore Restore(string text, IReadOnlyList<PlanStep> lines, PlanStep? step, bool ownBoxBack)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(lines);
        var now = PlanSteps.Flat(PlanSteps.Parse(text));
        var pairs = PlanSteps.Align(lines, now);
        var paired = new HashSet<int>(pairs.Where(p => p >= 0));
        for (int i = 0; i < now.Count; i++)
        {
            if (pairs[i] < 0)
            {
                int moved = Enumerable.Range(0, lines.Count).FirstOrDefault(k => !paired.Contains(k) && string.Equals(lines[k].Text, now[i].Text, StringComparison.Ordinal), -1);
                if (moved >= 0)
                {
                    pairs[i] = moved;
                    paired.Add(moved);
                }
            }
        }

        // The pass's own line: the one its step's became, else the step found by its words.
        int own = -1;
        if (step is not null)
        {
            own = Array.FindIndex(pairs, p => p >= 0 && lines[p].Line == step.Line);
            if (own < 0 && PlanSteps.Find(text, step) is { } found)
            {
                own = now.ToList().FindIndex(s => s.Line == found.Line);
            }
        }

        var edits = new List<(PlanStep At, bool Ticked, string Words)>();
        var added = new List<PlanStep>();
        int boxes = 0;
        int words = 0;
        bool ownBack = false;
        for (int i = 0; i < now.Count; i++)
        {
            var line = now[i];
            if (pairs[i] < 0 && i != own)
            {
                added.Add(line);
                continue;
            }

            var was = pairs[i] >= 0 ? lines[pairs[i]] : null;
            bool ticked = i == own && !ownBoxBack ? line.Ticked : was?.Ticked ?? false;
            string wording = was?.Text ?? line.Text;
            if (ticked != line.Ticked)
            {
                if (i == own)
                {
                    ownBack = true;
                }
                else
                {
                    boxes++;
                }
            }

            if (!string.Equals(wording, line.Text, StringComparison.Ordinal))
            {
                words++;
            }

            if (ticked != line.Ticked || !string.Equals(wording, line.Text, StringComparison.Ordinal))
            {
                edits.Add((line, ticked, wording));
            }
        }

        var removed = lines.Where((line, k) => !paired.Contains(k) && !line.Ticked).Select(line => line.Text).ToList();
        string restored = edits.Count == 0 ? text : PlanSteps.WithTaskLines(text, edits);
        // The added lines go last: the edits were placed by line, and taking lines out moves those after them.
        var (taken, gone) = added.Count == 0 ? (restored, (IReadOnlyList<int>)[]) : PlanSteps.WithoutTaskLines(restored, added);
        int ownLine = own < 0 ? -1 : now[own].Line - gone.Count(g => g < now[own].Line);
        var ownNow = own < 0 ? null : PlanSteps.Flat(PlanSteps.Parse(taken)).FirstOrDefault(s => s.Line == ownLine);
        return new RalphRestore(taken, ownNow, boxes, words, ownBack, removed, gone.Count == 0 ? 0 : added.Count);
    }

    /// <summary>
    /// <paramref name="restore"/> written over the plan (<paramref name="text"/> as the pass left it) when it changed anything; when it put back
    /// another step's box, words or an added line, the plan as the pass left it is kept beside the log first (<see cref="RalphLog.KeepAsLeft"/>,
    /// 2026-10-19, the fourth review: the judge cannot tell the user's own edits during a pass from the model's, and reverted them with no trace).
    /// Whether it was written, and the copy's path.
    /// </summary>
    internal static (bool Written, string? AsLeft) Apply(WorkingDirectory files, string path, string text, RalphRestore restore)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(restore);
        if (string.Equals(restore.Text, text, StringComparison.Ordinal))
        {
            return (false, null);
        }

        string? asLeft = restore.BoxesPutBack + restore.WordsPutBack + restore.Added > 0 ? RalphLog.KeepAsLeft(files, path, text) : null;
        return (files.WriteText(path, restore.Text, overwrite: true).Outcome == FileOutcome.Ok, asLeft);
    }

    /// <summary>What a written <paramref name="restore"/> says: the boxes and words put back, the lines taken out, and where the plan as the pass left it is.</summary>
    internal static IReadOnlyList<string> RestoreNotices(RalphRestore restore, string? asLeft)
    {
        ArgumentNullException.ThrowIfNull(restore);
        var notices = new List<string>();
        if (restore.BoxesPutBack > 0)
        {
            notices.Add(RalphText.BoxesPutBackNotice(restore.BoxesPutBack));
        }

        if (restore.WordsPutBack > 0)
        {
            notices.Add(RalphText.WordsPutBackNotice(restore.WordsPutBack));
        }

        if (restore.Added > 0)
        {
            notices.Add(RalphText.StepsAddedNotice(restore.Added));
        }

        if (asLeft is not null)
        {
            notices.Add(RalphText.AsLeftNotice(asLeft));
        }

        return notices;
    }

    /// <summary>
    /// A pass that ended without a judgment (its loop left with the conversation: a profile switch, a replaced conversation; 2026-10-19, the third
    /// review): the plan's lines put back as a stopped pass's are (<see cref="Restore"/>, its own box too), and the log's in-pass mark cleared; true
    /// when a box or words were put back.
    /// </summary>
    public static bool PutBackUnjudged(WorkingDirectory files, string path, IReadOnlyList<PlanStep> lines, PlanStep? step)
    {
        ArgumentNullException.ThrowIfNull(files);
        bool changed = false;
        if (PlanFiles.ReadWhole(files, path) is { } text)
        {
            changed = Apply(files, path, text, Restore(text, lines, step, ownBoxBack: true)).Written;
        }

        RalphLog.ClearInPass(files, path);
        return changed;
    }

    /// <summary>What <see cref="RecoverInPass"/> did: the notices it has to say, whether the plan changed, and the unfinished steps the pass deleted.</summary>
    internal sealed record RalphRecovered(IReadOnlyList<string> Notices, bool Changed, IReadOnlyList<string> Removed);

    /// <summary>
    /// A start on a plan whose last run ended in the middle of a pass (a crash, a kill: the log's in-pass mark, <see cref="RalphLog.InPassOf"/>;
    /// 2026-10-19, the third review): the plan put back as the judge puts back a stopped pass (<see cref="Restore"/> over the task lines the mark keeps,
    /// 2026-10-19, the fourth review: only the pass's own box went back, so other boxes it ticked, steps it reworded or added stood, and a resume passed
    /// them by), and the mark cleared. A log the previous build wrote keeps no lines: its step's box alone goes back. The steps the pass deleted
    /// (<see cref="RalphRecovered.Removed"/>) refuse that start; the next one runs. Null with no mark.
    /// </summary>
    public static RalphRecovered? RecoverInPass(WorkingDirectory files, string path)
    {
        ArgumentNullException.ThrowIfNull(files);
        var head = RalphLog.Read(files, path)?.Head;
        var stale = RalphLog.InPassOf(head);
        if (head?.InPassLines is not { } lines)
        {
            if (stale is null)
            {
                return null;
            }

            bool putBack = PlanFiles.ReadWhole(files, path) is { } old && PlanSteps.Find(old, stale) is { Ticked: true, IsLeaf: true } ticked
                && PlanFiles.SetStepBox(files, path, ticked, false) is null;
            RalphLog.ClearInPass(files, path);
            return new RalphRecovered(putBack ? [RalphText.InPassPutBackNotice(stale.Text)] : [], putBack, []);
        }

        var notices = new List<string>();
        IReadOnlyList<string> removed = [];
        bool changed = false;
        if (PlanFiles.ReadWhole(files, path) is { } text)
        {
            var restore = Restore(text, lines, stale, ownBoxBack: true);
            removed = restore.Removed;
            (changed, string? asLeft) = Apply(files, path, text, restore);
            if (changed)
            {
                if (restore.OwnBoxPutBack && stale is not null)
                {
                    notices.Add(RalphText.InPassPutBackNotice(stale.Text));
                }

                notices.AddRange(RestoreNotices(restore, asLeft));
            }
        }

        RalphLog.ClearInPass(files, path);
        return new RalphRecovered(notices, changed, removed);
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
            judged.Check, judged.Tail, judged.Flags, judged.Facts.Changed, judged.Review, null, time.GetLocalNow(), record?.Elapsed, record?.Tokens,
            // finish_step's notes for the next pass on the step (2026-10-19, the third review: read and dropped), carried by RecentNotes from here.
            report?.Notes);
        var head = new RalphLogHead(snapshot.Path, snapshot.Verify, snapshot.Final, snapshot.FinalOnly, snapshot.Shell, time.GetLocalNow());
        if (RalphLog.Read(files, snapshot.Path)?.Head is { } kept)
        {
            head = head with { Started = kept.Started };
        }

        return RalphLog.Append(files, snapshot.Path, head, entry, report?.Learned ?? []);
    }
}
