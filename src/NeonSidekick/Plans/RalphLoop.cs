namespace NeonSidekick.Plans;

/// <summary>Why a Ralph loop stopped (2026-10-18).</summary>
public enum RalphStop
{
    /// <summary>Every step stands and the final check, when there is one, passed.</summary>
    Done,

    /// <summary>A pass reported <c>finish_step(blocked)</c>: the step needs the user.</summary>
    Blocked,

    /// <summary>Two passes in a row made no progress.</summary>
    Stalled,

    /// <summary>One step failed its checks twice in a row, or used up its passes.</summary>
    StepFailing,

    /// <summary>The pass cap was reached.</summary>
    Limit,

    /// <summary>The token budget was spent.</summary>
    Budget,

    /// <summary>Esc, the Web UI's Stop or a withdrawal during a pass.</summary>
    Stopped,

    /// <summary>A message was typed during a pass: the loop waits for <c>/plan ralph</c>.</summary>
    Paused,

    /// <summary>The final check failed again after its fix pass.</summary>
    FinalFailed,

    /// <summary>The baseline failed and the user would not start anyway.</summary>
    BaselineRefused,

    /// <summary>The plan file went, or can no longer be read whole.</summary>
    PlanGone,

    /// <summary>The working directory changed under the loop.</summary>
    WorkdirMoved,

    /// <summary>Another live process holds the plan's lock.</summary>
    Locked,

    /// <summary>The checkpoint's Stop.</summary>
    UserStopped,
}

/// <summary>What a pass said it did: <c>finish_step</c>'s outcome.</summary>
public enum RalphOutcome
{
    Done,
    Blocked,
    Partial,
    Split,
}

/// <summary>
/// A pass's report (2026-10-18, <c>finish_step</c>): the outcome, a summary, what the model checked, notes for the next pass, the facts worth keeping
/// for every pass after (<see cref="RalphLog"/>'s Learned section) and, for a split, the sub-steps.
/// </summary>
public sealed record FinishReport(RalphOutcome Outcome, string Summary, IReadOnlyList<string> Verified, string? Notes, IReadOnlyList<string> Learned, IReadOnlyList<PlanSubstep> Substeps);

/// <summary>
/// What a Ralph loop is fixed to at its start (2026-10-18, snapshotted so a settings change or a rewrite of the plan mid-run changes nothing): the
/// plan, the working directory's root, the checks (<paramref name="FinalOnly"/>: per-step checks skipped after a failing baseline the user started
/// anyway), the shell, the reviewer, the checkpoint, the cap setting, the passes a step may take, the check timeout, the token budget, and the pass
/// number to count on from (<see cref="RalphLog.PassCount"/> + 1 on a resume).
/// </summary>
public sealed record RalphSnapshot(
    string Path,
    string Root,
    string Title,
    string? Verify,
    string? Final,
    bool FinalOnly,
    string Shell,
    RalphReviewer Reviewer,
    int Checkpoint,
    int CapSetting,
    int PassesPerStep,
    int CheckTimeoutSeconds,
    long TokenBudget,
    int StartPass)
{
    /// <summary>The command run after each step: none while <see cref="FinalOnly"/>.</summary>
    public string? StepCheck => FinalOnly ? null : Verify;

    /// <summary>The command run once every step stands: <see cref="Final"/>, else under <see cref="FinalOnly"/> the verify command.</summary>
    public string? FinalCheck => Final ?? (FinalOnly ? Verify : null);
}

/// <summary>What the next pass works on (<see cref="RalphLoop.Next"/>).</summary>
public enum RalphNextKind
{
    /// <summary>A step: <see cref="RalphNext.Step"/>.</summary>
    Step,

    /// <summary>Every step stands: the final check runs now (or, with none, the loop is done).</summary>
    FinalCheck,

    /// <summary>A pass to make a failed final check pass, its tail in <see cref="RalphLoop.FinalTail"/>.</summary>
    FinalFix,

    /// <summary>The loop stops: <see cref="RalphNext.Stop"/>.</summary>
    Stop,
}

/// <summary>The loop's next move.</summary>
public readonly record struct RalphNext(RalphNextKind Kind, PlanStep? Step = null, RalphStop? Stop = null);

/// <summary>
/// The facts the screen gathered about a pass, after its turn (2026-10-18, <see cref="RalphLoop.EndPass"/>): whether its step stands (ticked and
/// every check passed), whether a check failed (the step was unticked), whether files changed, whether a split was written, whether the turn
/// failed (an error, a thrown failure) or was stopped (Esc, the page's Stop, a withdrawal), and the files it changed.
/// </summary>
public sealed record RalphPassFacts(bool Stands, bool CheckFailed, bool FilesChanged, bool Split, bool TurnFailed, bool Cancelled, IReadOnlyList<string> Changed)
{
    /// <summary>A pass with nothing to say: no progress, no failure.</summary>
    public static RalphPassFacts None { get; } = new(false, false, false, false, false, false, []);
}

/// <summary>
/// One ended pass as the summary and the log tell it (2026-10-18): its number, its step (0 for the final fix), the step's words, what it ended in,
/// whether the step stood, and — when both ends were metered — its time and tokens (the pass's own and its review's).
/// </summary>
public sealed record RalphPassRecord(int Pass, int Step, string StepText, string Outcome, bool Stood, TimeSpan? Elapsed = null, long? Tokens = null, long ReviewTokens = 0);

/// <summary>
/// A Ralph loop (2026-10-18, the user's ask: Geoffrey Huntley's technique as a third way to carry out an approved plan — many short passes, each a
/// fresh conversation doing exactly one step, held together only by files on disk, the app and not the model deciding whether a step stands). Pure:
/// the screen (and headless) read the plan, run the pass, the checks, the evidence and the review, and hand the facts to <see cref="EndPass"/>.
/// <para>Progress is a step that stands, a <c>partial</c> report with files changed, or a split (once per step). A failed turn is a pass with no
/// progress unless it reported; two passes in a row without progress stall the loop, so a failed turn is retried once. Two failed checks in a row on
/// one step, or the step at <see cref="RalphSnapshot.PassesPerStep"/>, stop it on that step. A split grows an automatic cap by three a new step; a
/// fixed one stays. The cap holds one pass more for the final fix (<see cref="CapFor"/>). The budget is checked before each pass.</para>
/// </summary>
public sealed class RalphLoop
{
    private sealed class StepState
    {
        public int Passes;
        public int FailedChecks;
        public readonly List<string> Changed = [];
    }

    private readonly Dictionary<string, StepState> _steps = new(StringComparer.Ordinal);
    private readonly HashSet<string> _split = new(StringComparer.Ordinal);
    private readonly List<RalphPassRecord> _history = [];
    private readonly bool _autoCap;
    private PlanPassMeter? _passStart;
    private int _noProgress;
    private int _passedSinceCheckpoint;
    private bool _finalFixed;

    /// <param name="snapshot">What the run is fixed to.</param>
    /// <param name="remaining">The leaves unticked as it starts, for an automatic cap.</param>
    public RalphLoop(RalphSnapshot snapshot, int remaining)
    {
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        _autoCap = snapshot.CapSetting <= 0;
        Cap = CapFor(snapshot.CapSetting, remaining, snapshot.FinalCheck is not null);
        Pass = Math.Max(0, snapshot.StartPass - 1);
    }

    public RalphSnapshot Snapshot { get; }

    /// <summary>The most passes this run may take.</summary>
    public int Cap { get; private set; }

    /// <summary>The pass number, counted on from earlier runs of the same plan; 0 before the first.</summary>
    public int Pass { get; private set; }

    /// <summary>The passes this run (start or resume) took.</summary>
    public int PassesThisRun { get; private set; }

    /// <summary>The tokens this run spent: its passes and its reviews.</summary>
    public long RunTokens { get; private set; }

    /// <summary>The step the pass under way works; null between passes and for the final fix.</summary>
    public PlanStep? Current { get; private set; }

    /// <summary>A pass is under way (<see cref="BeginPass"/> to <see cref="EndPass"/>).</summary>
    public bool InPass { get; private set; }

    /// <summary>The pass under way is the final fix.</summary>
    public bool FixingFinal { get; private set; }

    /// <summary>The report the pass under way made, once (<see cref="Record"/>).</summary>
    public FinishReport? Report { get; private set; }

    /// <summary>The failed final check's tail, for the fix pass's message.</summary>
    public string? FinalTail { get; private set; }

    /// <summary>The note the next pass gets (a failed check's tail, a checkpoint's Redo), taken by <see cref="TakeNotes"/>.</summary>
    public string? PendingNotes { get; set; }

    /// <summary>The next pass goes without the reference pictures (the retry after a failed pass that carried them).</summary>
    public bool DropPicturesNext { get; set; }

    /// <summary>The ended passes of this run, in order.</summary>
    public IReadOnlyList<RalphPassRecord> History => _history;

    /// <summary>A checkpoint is due after the last pass (<see cref="RalphSnapshot.Checkpoint"/> steps stood since the last one).</summary>
    public bool CheckpointDue { get; private set; }

    /// <summary>
    /// The pass cap for a run: <paramref name="setting"/> when above 0, else three a remaining step (at least 1), and one more for the final fix
    /// pass when there is a final check (<paramref name="final"/>), fixed or automatic alike. Pure.
    /// </summary>
    public static int CapFor(int setting, int remaining, bool final) => (setting > 0 ? setting : Math.Max(1, remaining * 3)) + (final ? 1 : 0);

    /// <summary>
    /// The next move over <paramref name="steps"/> (the plan read again): paused when the user spoke, the budget spent, the final fix when one is
    /// owed, the final check when every leaf is ticked, the cap, else the first unticked leaf.
    /// </summary>
    public RalphNext Next(IReadOnlyList<PlanStep> steps, bool userSpoke)
    {
        ArgumentNullException.ThrowIfNull(steps);
        if (userSpoke)
        {
            return new RalphNext(RalphNextKind.Stop, Stop: RalphStop.Paused);
        }

        if (Snapshot.TokenBudget > 0 && RunTokens >= Snapshot.TokenBudget)
        {
            return new RalphNext(RalphNextKind.Stop, Stop: RalphStop.Budget);
        }

        if (FinalTail is not null)
        {
            return PassesThisRun >= Cap ? new RalphNext(RalphNextKind.Stop, Stop: RalphStop.Limit) : new RalphNext(RalphNextKind.FinalFix);
        }

        if (PlanSteps.FirstUnticked(steps) is not { } step)
        {
            return new RalphNext(RalphNextKind.FinalCheck);
        }

        return PassesThisRun >= Cap ? new RalphNext(RalphNextKind.Stop, Stop: RalphStop.Limit) : new RalphNext(RalphNextKind.Step, step);
    }

    /// <summary>A pass begins on <paramref name="step"/> (null for the final fix), metered from <paramref name="meter"/>.</summary>
    public void BeginPass(PlanStep? step, PlanPassMeter? meter)
    {
        Pass++;
        PassesThisRun++;
        Current = step;
        FixingFinal = step is null;
        Report = null;
        InPass = true;
        CheckpointDue = false;
        _passStart = meter;
    }

    /// <summary>The notes the pass about to begin gets, once.</summary>
    public string? TakeNotes()
    {
        string? notes = PendingNotes;
        PendingNotes = null;
        return notes;
    }

    /// <summary>
    /// <c>finish_step</c>'s report for the pass under way: its refusal sentence when there is no pass, it already reported, or the split is not one
    /// this step may take; null when recorded.
    /// </summary>
    public string? Record(FinishReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (!InPass)
        {
            return RalphText.NotInPassResult;
        }

        if (Report is not null)
        {
            return RalphText.AlreadyFinishedResult;
        }

        if (report.Outcome == RalphOutcome.Split)
        {
            if (FixingFinal)
            {
                return RalphText.SplitFinalResult;
            }

            if (Current is not { } step || step.Index == 0 || !step.IsLeaf || _split.Contains(step.Text) || IsChild(step))
            {
                return RalphText.SplitRefusedResult;
            }
        }

        Report = report;
        return null;
    }

    /// <summary>The leaves the plan held as the pass began, to tell a child from a top-level step (the screen keeps it fresh with <see cref="SetChildren"/>).</summary>
    private readonly HashSet<string> _children = new(StringComparer.Ordinal);

    /// <summary>The words of every child step in the plan as it stands (a split's sub-steps may not split again).</summary>
    public void SetChildren(IReadOnlyList<PlanStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        _children.Clear();
        foreach (var child in steps.SelectMany(s => s.Children))
        {
            _children.Add(child.Text);
        }
    }

    private bool IsChild(PlanStep step) => _children.Contains(step.Text);

    /// <summary>Tokens a review spent, counted against the budget and into the pass's record.</summary>
    public void AddReviewTokens(long tokens)
    {
        if (tokens > 0)
        {
            RunTokens += tokens;
            _reviewTokens += tokens;
        }
    }

    private long _reviewTokens;

    /// <summary>
    /// The pass ended with <paramref name="facts"/> (and <paramref name="meter"/>, the usage now): recorded, then the loop's verdict — null to go on,
    /// else why it stops. A stopped pass stops at once; a blocked report stops; otherwise a step failing its checks twice in a row or at its pass
    /// limit, then two passes in a row without progress.
    /// </summary>
    public RalphStop? EndPass(RalphPassFacts facts, PlanPassMeter? meter)
    {
        ArgumentNullException.ThrowIfNull(facts);
        InPass = false;
        var step = Current;
        string key = step?.Text ?? "";
        var state = step is null ? null : _steps.TryGetValue(key, out var known) ? known : _steps[key] = new StepState();
        TimeSpan? elapsed = null;
        long? tokens = null;
        if (_passStart is { } start && meter is { } end)
        {
            elapsed = end.At - start.At;
            tokens = Math.Max(0, end.Tokens - start.Tokens) + Math.Max(0, end.AdvisorTokens - start.AdvisorTokens);
            RunTokens += tokens.Value;
        }

        string outcome = facts.Cancelled ? "stopped" : Report?.Outcome switch
        {
            RalphOutcome.Done => facts.Stands ? "done" : facts.CheckFailed ? "check failed" : "not done",
            RalphOutcome.Blocked => "blocked",
            RalphOutcome.Partial => facts.Stands ? "done" : "partial",
            RalphOutcome.Split => "split",
            _ => facts.TurnFailed ? "failed" : facts.Stands ? "done" : "no report",
        };
        _history.Add(new RalphPassRecord(Pass, step?.Index ?? 0, step?.Text ?? RalphText.FinalFixStep, outcome, facts.Stands, elapsed, tokens, _reviewTokens));
        _reviewTokens = 0;
        if (state is not null)
        {
            state.Changed.AddRange(facts.Changed.Where(c => !state.Changed.Contains(c, StringComparer.Ordinal)));
        }

        if (facts.Cancelled)
        {
            return RalphStop.Stopped;
        }

        if (Report?.Outcome == RalphOutcome.Blocked)
        {
            return RalphStop.Blocked;
        }

        if (FixingFinal)
        {
            // The final fix's progress is the final check's to say: it runs again next.
            FixingFinal = false;
            return null;
        }

        bool split = facts.Split && Report?.Outcome == RalphOutcome.Split;
        if (split && step is not null)
        {
            _split.Add(step.Text);
            if (_autoCap)
            {
                Cap += 3 * Math.Max(0, Report!.Substeps.Count - 1);
            }
        }

        bool reported = Report is not null;
        bool progress = facts.Stands || (Report?.Outcome == RalphOutcome.Partial && facts.FilesChanged) || split;
        if (!reported && facts.TurnFailed)
        {
            progress = false;
        }

        if (state is not null)
        {
            state.Passes++;
            state.FailedChecks = facts.CheckFailed ? state.FailedChecks + 1 : facts.Stands ? 0 : state.FailedChecks;
            if (!facts.Stands && !split && (state.FailedChecks >= 2 || state.Passes >= Snapshot.PassesPerStep))
            {
                return RalphStop.StepFailing;
            }
        }

        _noProgress = progress ? 0 : _noProgress + 1;
        if (_noProgress >= 2)
        {
            return RalphStop.Stalled;
        }

        if (facts.Stands && Snapshot.Checkpoint > 0 && ++_passedSinceCheckpoint >= Snapshot.Checkpoint)
        {
            _passedSinceCheckpoint = 0;
            CheckpointDue = true;
        }

        return null;
    }

    /// <summary>A checkpoint's Redo on <paramref name="step"/> (2026-10-18): its counters start again (the user's call is no failed check), the note going to the next pass.</summary>
    public void Redo(PlanStep step, string? note)
    {
        ArgumentNullException.ThrowIfNull(step);
        _steps.Remove(step.Text);
        _noProgress = 0;
        PendingNotes = string.IsNullOrWhiteSpace(note) ? null : RalphText.RedoNote(note);
    }

    /// <summary>
    /// The final check's result: passed is done; failed the first time, a fix pass is owed (<see cref="FinalTail"/>); failed after its fix,
    /// <see cref="RalphStop.FinalFailed"/>.
    /// </summary>
    public RalphStop? FinalResult(bool passed, string tail)
    {
        ArgumentNullException.ThrowIfNull(tail);
        if (passed)
        {
            FinalTail = null;
            return RalphStop.Done;
        }

        if (_finalFixed)
        {
            FinalTail = null;
            return RalphStop.FinalFailed;
        }

        _finalFixed = true;
        FinalTail = tail;
        return null;
    }

    /// <summary>The fix pass for the final check began: the tail goes to its message once.</summary>
    public string? TakeFinalTail()
    {
        string? tail = FinalTail;
        FinalTail = null;
        return tail;
    }

    /// <summary>The files the passes on <paramref name="step"/> changed this run (a <see cref="RalphStop.StepFailing"/> stop lists them).</summary>
    public IReadOnlyList<string> ChangedBy(PlanStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        return _steps.TryGetValue(step.Text, out var state) ? state.Changed : [];
    }

    /// <summary>The passes <paramref name="step"/> has taken this run.</summary>
    public int PassesOn(PlanStep step) => _steps.TryGetValue(step.Text, out var state) ? state.Passes : 0;
}
