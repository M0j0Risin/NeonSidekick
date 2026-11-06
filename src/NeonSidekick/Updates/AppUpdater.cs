using NeonSidekick.App;
using NeonSidekick.Speech;

namespace NeonSidekick.Updates;

/// <summary>What <c>.update/</c> says is waiting for the next exit.</summary>
/// <param name="ReadyVersion">The version of a stage that passed its checks, or null.</param>
/// <param name="RollbackTarget">The version a pending rollback goes back to, or null.</param>
/// <param name="Leftovers">A kept stage with no <c>ready</c>, or a partial download: nothing installs from them, and <c>/update cancel</c> can still delete them. Read only when asked for.</param>
/// <param name="ReadyUnknown">A <c>ready</c> whose manifest couldn't be read just now (2026-11-04, reviews P9-6, P11-9): a passing hold read as nothing there, so the marker jumped and <c>/update cancel</c> found nothing.</param>
/// <param name="RollbackUnknown">The same of a pending rollback, its earlier manifest gone or held.</param>
public sealed record UpdateDiskState(string? ReadyVersion, string? RollbackTarget, bool Leftovers = false, bool ReadyUnknown = false, bool RollbackUnknown = false);

/// <summary>
/// The screen's door to the updater (2026-11-04, self-update): a <see cref="SidekickApp"/> factory parameter, <see cref="AppUpdater"/> in the app, <c>Fakes/FakeUpdater</c> in tests, so no test can reach GitHub, and null in
/// headless and check modes, which never check, stage or swap.
/// </summary>
public interface IUpdater
{
    /// <summary>The running version.</summary>
    string Running { get; }

    /// <summary>What the start's step left the screen to say: "Updated to X", "Back on X" or why a window close's swap failed. Null for nothing.</summary>
    string? StartLine { get; }

    /// <summary>Called once the screen has printed <see cref="StartLine"/>, with or without one: the start's journal and failure file go only now, so a start that ended before the idle line loses nothing.</summary>
    void AcknowledgeStart();

    /// <summary>Whether this folder holds a package's manifest. Without one the check makes no request and <c>/update</c> says it can't update.</summary>
    bool IsPackage { get; }

    /// <summary>Reads <c>.update/</c>: another copy may have staged or asked for a rollback since this one looked. It parses only when something changed (2026-11-04, reviews P8-17, P11-18: this ran at every idle line, on the screen's task). <paramref name="withLeftovers"/> also walks for what <c>/update cancel</c> could delete.</summary>
    UpdateDiskState ReadDisk(bool withLeftovers = false);

    /// <summary>Asks GitHub. Never throws for a network failure: that's a <see cref="UpdateCheckKind.Failed"/> answer.</summary>
    Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken);

    /// <summary>The reason this folder can't take a download of that size, or null.</summary>
    string? Preflight(long archiveBytes);

    /// <summary>Downloads, verifies and probes.</summary>
    Task<UpdateStageResult> StageAsync(UpdateRelease release, IProgress<UpdateStagePhase>? progress, CancellationToken cancellationToken);

    /// <summary>Drops the stage under the staging lock, with the partial download when <paramref name="alsoParts"/>. The reason it couldn't, or null.</summary>
    string? DropStage(bool alsoParts);

    /// <summary>Deletes a partial or a kept stage for a release that's no longer out, if nothing is ready (2026-11-04, review P18-5: nothing else would ever delete it). The reason it couldn't, or null.</summary>
    string? DropLeftovers();

    /// <summary>
    /// Checks the version in <c>.update/previous</c> and, if it passes, asks for the rollback. A stage stays on disk until the exit, so <c>/update cancel</c> brings it back. The reason it refused, or null with <paramref name="target"/> set, and <paramref name="docsStaying"/> naming the docs whose current copy stays.
    /// </summary>
    string? AskRollback(CancellationToken cancellationToken, out string? target, out IReadOnlyList<string> docsStaying);

    /// <summary>Takes back a pending rollback. The reason it couldn't, or null.</summary>
    string? CancelRollback();

    /// <summary>The running version's saved release notes, scrubbed, or null with none.</summary>
    string? RunningNotes();
}

/// <summary>The real <see cref="IUpdater"/>, over the install's <see cref="UpdateContext"/> and a client with no timeout of its own: the check limits itself, and a download mustn't be cut at 100 s.</summary>
public sealed class AppUpdater : IUpdater
{
    private readonly UpdateContext _context;
    private readonly HttpClient _http;
    private readonly UpdateSource _source;
    private readonly UpdateStartResult? _start;
    private readonly Lock _diskGate = new();
    private (DateTime Ready, DateTime Rollback, UpdateDiskState State)? _disk;

    public AppUpdater(UpdateContext context, UpdateStartResult? start = null, HttpClient? http = null, UpdateSource? source = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _start = start;
        _http = http ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _source = source ?? UpdateSource.GitHub(AboutText.RepositoryUrl);
    }

    public string Running => _context.Running.ToString();

    public string? StartLine => _start?.Line;

    public void AcknowledgeStart() => UpdateStartup.Acknowledge(_context.Folder, _start?.JournalMayGo == true, _start?.FailureShown == true);

    // Mid-swap too (2026-11-04, review P12-11): /update can say why a killed swap's folder can't update, rather than that it isn't a package.
    public bool IsPackage => _context.Folder.IsPackageOrMidSwap;

    public UpdateDiskState ReadDisk(bool withLeftovers = false)
    {
        var folder = _context.Folder;
        var ready = Stamp(folder.Ready);
        var rollback = Stamp(folder.Rollback);
        UpdateDiskState state;
        lock (_diskGate)
        {
            // One it couldn't read is tried again, or a passing hold would stick for the session.
            if (_disk is { } last && last.Ready == ready && last.Rollback == rollback && !last.State.ReadyUnknown && !last.State.RollbackUnknown)
            {
                state = last.State;
            }
            else
            {
                state = ParseDisk(folder);
                _disk = (ready, rollback, state);
            }
        }

        return withLeftovers ? state with { Leftovers = HasLeftovers(folder) } : state;
    }

    private static UpdateDiskState ParseDisk(UpdateFolder folder)
    {
        bool rollback = File.Exists(folder.Rollback);
        bool stage = File.Exists(folder.Ready);
        string? target = rollback ? UpdateManifest.Load(folder.PreviousManifest).Manifest?.Version : null;
        string? ready = stage ? UpdateManifest.Load(folder.StagingManifest).Manifest?.Version : null;
        return new UpdateDiskState(ready, target, ReadyUnknown: stage && ready is null, RollbackUnknown: rollback && target is null);
    }

    private static DateTime Stamp(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diagnostics.DiagnosticLog.Info(UpdateJournal.Category, ex.Message);
            return DateTime.MinValue;
        }
    }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken)
    {
        // Before anything else (2026-11-04, review P12-11): a stage over this folder would fail at preflight anyway, and the journal is the reason.
        if (UpdateJournal.IsUnsettled(_context.Folder))
        {
            return new UpdateCheckResult(UpdateCheckKind.Failed, Reason: UpdateText.UnsettledSwap);
        }

        // The platform is the installed manifest's RID, not the machine's, so an x64 build under emulation asks for what it is.
        if (UpdateManifest.ReadFile(_context.Folder.Manifest, out string? problem) is not { } installed)
        {
            return new UpdateCheckResult(UpdateCheckKind.Failed, Reason: problem ?? UpdateText.NotAPackage);
        }

        return await Check(installed.Rid).CheckAsync(cancellationToken).ConfigureAwait(false);
    }

    public string? Preflight(long archiveBytes) => Stage().Preflight(archiveBytes);

    public Task<UpdateStageResult> StageAsync(UpdateRelease release, IProgress<UpdateStagePhase>? progress, CancellationToken cancellationToken) =>
        Stage().StageAsync(release, progress, cancellationToken);

    public string? DropStage(bool alsoParts)
    {
        // Nothing there asks nothing of the lock, whose folder may not exist. A start's tidy holds it for a moment (2026-11-04, reviews P12-15, P13-6): that read as another copy downloading.
        var folder = _context.Folder;
        if (!File.Exists(folder.Ready) && !Directory.Exists(folder.Staging) && !Directory.Exists(folder.Staged) && !(alsoParts && Directory.Exists(folder.StagingParts)))
        {
            return null;
        }

        using var staging = UpdateLock.TryExclusiveRetrying(folder.StagingLock, UpdateStage.LockWait, Thread.Sleep, out bool held);
        if (staging is null)
        {
            return held ? UpdateText.AnotherCopyStaging : UpdateText.StagingLockCantOpen;
        }

        if (!UpdateStage.Drop(folder, alsoParts))
        {
            return UpdateText.UnsettledSwap;   // "Update canceled." over a stage still there would be false (review P12-8)
        }

        // What matters is ready: while that stands, the stage still installs at quit, and "Update canceled." would be false (2026-11-04, review P3-4). A folder a cleanup left without it installs nothing, and the next start retries it.
        return File.Exists(folder.Ready) ? UpdateText.CouldntDelete(folder.Ready) : null;
    }

    public string? DropLeftovers()
    {
        var folder = _context.Folder;
        if (File.Exists(folder.Ready) || !HasLeftovers(folder))
        {
            return null;
        }

        using var staging = UpdateLock.TryExclusive(folder.StagingLock, out bool held);
        if (staging is null)
        {
            return held ? UpdateText.AnotherCopyStaging : UpdateText.StagingLockCantOpen;
        }

        // Read again under the lock: another copy's stage may have just finished.
        if (File.Exists(folder.Ready))
        {
            return null;
        }

        return UpdateStage.Drop(folder, alsoParts: true) ? null : UpdateText.UnsettledSwap;
    }

    public string? AskRollback(CancellationToken cancellationToken, out string? target, out IReadOnlyList<string> docsStaying)
    {
        target = null;
        docsStaying = [];
        var folder = _context.Folder;
        try
        {
            // Its own refusals, as a stage's: settling an unsettled swap at exit would delete what this asks for (2026-11-04, review P2-3), and a renamed copy's exit doesn't swap, so "Going back when you quit" would be false (review P4-5).
            if (UpdateJournal.IsUnsettled(folder))
            {
                return UpdateText.UnsettledSwap;
            }

            if (UpdateManifest.ReadFile(folder.Manifest, out _) is { } installed && !_context.IsListedExe(installed))
            {
                return UpdateText.RenamedCopy(installed.Exe);
            }

            if (UpdateRollback.Check(_context, cancellationToken, out string? reason, out _) is not { } plan)
            {
                return reason ?? UpdateText.NothingToGoBackTo;
            }

            // The stage stays until the exit (review P3-13): the swap's finish deletes it if this goes ahead, and /update cancel brings it back if it doesn't.
            File.WriteAllText(folder.Rollback, "");
            target = plan.Target.Version;
            docsStaying = plan.DocsStaying;
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Called on the screen's path (review P1-8): a read-only folder reached the crash catch.
            return ex.Message;
        }
    }

    public string? CancelRollback()
    {
        try
        {
            File.Delete(_context.Folder.Rollback);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diagnostics.DiagnosticLog.Warn(UpdateJournal.Category, ex.Message);
            return UpdateText.CouldntDelete(_context.Folder.Rollback);
        }
    }

    // A partial, or a stage with no ready: a kept test failure's, or one a kill left.
    private static bool HasLeftovers(UpdateFolder folder)
    {
        try
        {
            return (Directory.Exists(folder.Staging) && !File.Exists(folder.Ready))
                || (Directory.Exists(folder.StagingParts) && Directory.EnumerateFileSystemEntries(folder.StagingParts).Any());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diagnostics.DiagnosticLog.Info(UpdateJournal.Category, ex.Message);
            return false;
        }
    }

    public string? RunningNotes() =>
        UpdateManifest.ReadFile(_context.Folder.Manifest, out _) is { } installed ? UpdateProofs.Notes(_context.Folder, installed) : null;

    private UpdateCheck Check(string rid) => new(_http, _source, _context.Running, rid);

    private UpdateStage Stage()
    {
        string rid = UpdateManifest.ReadFile(_context.Folder.Manifest, out _)?.Rid ?? "";
        return new UpdateStage(_context, new ModelStore(_context.Folder.Root, _http, UpdateJournal.Category), Check(rid), new UpdateProbeProcess());
    }
}
