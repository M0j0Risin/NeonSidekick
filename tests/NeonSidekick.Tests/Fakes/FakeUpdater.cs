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

    /// <summary>How many times the screen said it had printed the start's line.</summary>
    public int StartsAcknowledged { get; private set; }

    /// <summary>The docs a rollback leaves as they are.</summary>
    public IReadOnlyList<string> DocsStaying { get; set; } = [];

    /// <summary>The next checks' answer; a Latest by default.</summary>
    public UpdateCheckResult Answer { get; set; } = new(UpdateCheckKind.Latest);

    /// <summary>Held open until a test releases it, for a check that answers late.</summary>
    public TaskCompletionSource? CheckGate { get; set; }

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

    public UpdateDiskState ReadDisk() => new(ReadyOnDisk, RollbackOnDisk, LeftoversOnDisk);

    public void AcknowledgeStart() => StartsAcknowledged++;

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _checks);
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
            return await stage(release, progress, cancellationToken).ConfigureAwait(false);
        }

        progress?.Report(new UpdateStagePhase.Downloading(42));
        progress?.Report(new UpdateStagePhase.Checking());
        ReadyOnDisk = release.Version.ToString();
        return new UpdateStageResult(null);
    }

    public string? DropStage(bool alsoParts)
    {
        Drops.Add(alsoParts);
        if (DropRefusal is null)
        {
            ReadyOnDisk = null;
            LeftoversOnDisk = false;
        }

        return DropRefusal;
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
