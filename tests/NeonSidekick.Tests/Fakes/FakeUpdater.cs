using NeonSidekick.Updates;

namespace NeonSidekick.Tests.Fakes;

/// <summary>The screen's updater with nothing behind it (2026-11-04, self-update): scripted answers, its calls counted, so no test can reach GitHub.</summary>
public sealed class FakeUpdater : IUpdater
{
    public string Running { get; set; } = "0.5.6";

    public string? StartLine { get; set; }

    public bool IsPackage { get; set; } = true;

    public string? ReadyOnDisk { get; set; }

    public string? RollbackOnDisk { get; set; }

    public bool LeftoversOnDisk { get; set; }

    /// <summary>A stage whose version can't be read right now.</summary>
    public bool ReadyUnknown { get; set; }

    /// <summary>A rollback whose earlier version can't be read right now.</summary>
    public bool RollbackUnknown { get; set; }

    /// <summary>How many reads asked for the leftovers too, as the idle refresh doesn't (review P8-17).</summary>
    public int LeftoverReads { get; private set; }

    /// <summary>How many reads of the disk there have been.</summary>
    public int DiskReads { get; private set; }

    public string? LeftoversRefusal { get; set; }

    public int LeftoverDrops { get; private set; }

    /// <summary>How many times the screen said it had printed the start's line.</summary>
    public int StartsAcknowledged { get; private set; }

    /// <summary>The docs a rollback leaves as they are.</summary>
    public IReadOnlyList<string> DocsStaying { get; set; } = [];

    /// <summary>The next checks' answer; a Latest by default.</summary>
    public UpdateCheckResult Answer { get; set; } = new(UpdateCheckKind.Latest);

    /// <summary>Held open until a test releases it, for a check that answers late.</summary>
    public TaskCompletionSource? CheckGate { get; set; }

    /// <summary>Holds only the first check, the start's, so <c>/update</c>'s own can answer first.</summary>
    public TaskCompletionSource? FirstCheckGate { get; set; }

    /// <summary>What the first check answers, when it isn't <see cref="Answer"/>.</summary>
    public UpdateCheckResult? FirstAnswer { get; set; }

    /// <summary>Runs inside a drop, for one that takes its time.</summary>
    public Action? DropWork { get; set; }

    public int Checks => Volatile.Read(ref _checks);

    public string? PreflightRefusal { get; set; }

    /// <summary>Runs in place of a stage. Defaults to one that reports 42% and succeeds.</summary>
    public Func<UpdateRelease, IProgress<UpdateStagePhase>?, CancellationToken, Task<UpdateStageResult>>? Stage { get; set; }

    public List<UpdateRelease> Staged { get; } = [];

    public List<bool> Drops { get; } = [];

    public string? DropRefusal { get; set; }

    public string? RollbackRefusal { get; set; }

    public string? CancelRollbackRefusal { get; set; }

    /// <summary>Runs in place of the rollback's check, for one that takes its time. A refusal it returns stands.</summary>
    public Func<CancellationToken, string?>? RollbackCheck { get; set; }

    public string RollbackTarget { get; set; } = "0.5.5";

    public int RollbacksCanceled { get; private set; }

    public string? Notes { get; set; }

    public static UpdateRelease Release(string version = "0.5.7", string? notes = "the notes", long bytes = 231_000_000) =>
        new(UpdateFixture.V(version), notes, "https://github.com/M0j0Risin/NeonSidekick/releases/tag/v" + version, "NeonSidekick-v" + version + "-win-x64.zip", new Uri("https://github.com/a.zip"), bytes, new Uri("https://github.com/a.sha256"), new Uri("https://github.com/a.sig"));

    public static UpdateCheckResult Newer(string version = "0.5.7", string? notes = "the notes") =>
        new(UpdateCheckKind.Newer, Release(version, notes), Found: UpdateFixture.V(version));

    private int _checks;

    public UpdateDiskState ReadDisk(bool withLeftovers = false)
    {
        DiskReads++;
        LeftoverReads += withLeftovers ? 1 : 0;
        return new UpdateDiskState(ReadyOnDisk, RollbackOnDisk, withLeftovers && LeftoversOnDisk, ReadyUnknown, RollbackUnknown);
    }

    public void AcknowledgeStart() => StartsAcknowledged++;

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken)
    {
        bool first = Interlocked.Increment(ref _checks) == 1;
        if (first && FirstCheckGate is { } firstGate)
        {
            await firstGate.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
        }

        if (first && FirstAnswer is { } firstAnswer)
        {
            return firstAnswer;
        }

        if (CheckGate is { } gate)
        {
            // A limit of its own: a test whose gate is never opened fails here rather than hanging the run.
            await gate.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
        }

        return Answer;
    }

    public string? Preflight(long archiveBytes) => PreflightRefusal;

    public async Task<UpdateStageResult> StageAsync(UpdateRelease release, IProgress<UpdateStagePhase>? progress, CancellationToken cancellationToken)
    {
        Staged.Add(release);
        if (Stage is { } stage)
        {
            // A failed stage leaves what it downloaded, as the real one does (2026-11-04, review P14-10): a test of a failure and then a cancel means nothing without it.
            var result = await stage(release, progress, cancellationToken).ConfigureAwait(false);
            LeftoversOnDisk |= !result.Ok;
            return result;
        }

        progress?.Report(new UpdateStagePhase.Downloading(42));
        progress?.Report(new UpdateStagePhase.Checking());
        ReadyOnDisk = release.Version.ToString();
        return new UpdateStageResult(null);
    }

    public string? DropStage(bool alsoParts)
    {
        Drops.Add(alsoParts);
        DropWork?.Invoke();
        if (DropRefusal is null)
        {
            ReadyOnDisk = null;
            ReadyUnknown = false;
            LeftoversOnDisk &= !alsoParts;   // what the real one does (2026-11-04, review P14-9): a drop for a stage's own sake keeps a resumable download
        }

        return DropRefusal;
    }

    public int PartsDrops { get; private set; }

    public string? DropParts()
    {
        PartsDrops++;
        return null;
    }

    public string? DropLeftovers()
    {
        LeftoverDrops++;
        if (LeftoversRefusal is null && ReadyOnDisk is null)
        {
            LeftoversOnDisk = false;
        }

        return LeftoversRefusal;
    }

    public string? AskRollback(CancellationToken cancellationToken, out string? target, out IReadOnlyList<string> docsStaying)
    {
        target = null;
        docsStaying = [];
        if (RollbackCheck?.Invoke(cancellationToken) is { } checkedOut)
        {
            return checkedOut;
        }

        if (RollbackRefusal is { } refused)
        {
            return refused;
        }

        // The stage stays on disk (2026-11-04, review P3-13): /update cancel brings it back.
        RollbackOnDisk = RollbackTarget;
        target = RollbackTarget;
        docsStaying = DocsStaying;
        return null;
    }

    public string? CancelRollback()
    {
        RollbacksCanceled++;
        if (CancelRollbackRefusal is { } refused)
        {
            return refused;
        }

        RollbackOnDisk = null;
        return null;
    }

    public string? RunningNotes() => Notes;
}
