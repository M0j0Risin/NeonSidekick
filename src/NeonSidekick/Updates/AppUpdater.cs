using NeonSidekick.App;
using NeonSidekick.Speech;

namespace NeonSidekick.Updates;

/// <summary>What <c>.update/</c> says is waiting for the next exit.</summary>
/// <param name="ReadyVersion">The version of a stage that passed its checks, or null.</param>
/// <param name="RollbackTarget">The version a pending rollback goes back to, or null.</param>
/// <param name="Leftovers">A kept stage with no <c>ready</c>, or a partial download: nothing installs from them, and <c>/update cancel</c> can still delete them.</param>
public sealed record UpdateDiskState(string? ReadyVersion, string? RollbackTarget, bool Leftovers = false);

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

    /// <summary>Reads <c>.update/</c>: another copy may have staged or asked for a rollback since this one looked.</summary>
    UpdateDiskState ReadDisk();

    /// <summary>Asks GitHub. Never throws for a network failure: that's a <see cref="UpdateCheckKind.Failed"/> answer.</summary>
    Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken);

    /// <summary>The reason this folder can't take a download of that size, or null.</summary>
    string? Preflight(long archiveBytes);

    /// <summary>Downloads, verifies and probes.</summary>
    Task<UpdateStageResult> StageAsync(UpdateRelease release, IProgress<UpdateStagePhase>? progress, CancellationToken cancellationToken);

    /// <summary>Drops the stage under the staging lock, with the partial download when <paramref name="alsoParts"/>. The reason it couldn't, or null.</summary>
    string? DropStage(bool alsoParts);

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
    private readonly bool _journalMayGo;
    private readonly bool _failureShown;

    public AppUpdater(UpdateContext context, string? startLine, HttpClient? http = null, UpdateSource? source = null, bool journalMayGo = false, bool failureShown = false)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        StartLine = startLine;
        _journalMayGo = journalMayGo;
        _failureShown = failureShown;
        _http = http ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _source = source ?? UpdateSource.GitHub(AboutText.RepositoryUrl);
    }

    public string Running => _context.Running.ToString();

    public string? StartLine { get; }

    public void AcknowledgeStart() => UpdateStartup.Acknowledge(_context.Folder, _journalMayGo, _failureShown);

    public bool IsPackage => _context.Folder.IsPackage;

    public UpdateDiskState ReadDisk()
    {
        var folder = _context.Folder;
        string? target = File.Exists(folder.Rollback) ? UpdateManifest.ReadFile(folder.PreviousManifest, out _)?.Version : null;
        string? ready = File.Exists(folder.Ready) ? UpdateManifest.ReadFile(folder.StagingManifest, out _)?.Version : null;
        return new UpdateDiskState(ready, target, HasLeftovers(folder));
    }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken)
    {
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
        using var staging = UpdateLock.TryExclusive(_context.Folder.StagingLock);
        if (staging is null)
        {
            return UpdateText.AnotherCopyStaging;
        }

        UpdateStage.Drop(_context.Folder, alsoParts);

        // What matters is ready: while that stands, the stage still installs at quit, and "Update canceled." would be false (2026-11-04, review P3-4). A folder a cleanup left without it installs nothing, and the next start retries it.
        return File.Exists(_context.Folder.Ready) ? UpdateText.CouldntDelete(_context.Folder.Ready) : null;
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

            if (UpdateRollback.Check(_context, cancellationToken, out string? reason) is not { } plan)
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
