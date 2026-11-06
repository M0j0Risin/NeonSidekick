using System.Globalization;
using NeonSidekick.Diagnostics;
using NeonSidekick.Files;

namespace NeonSidekick.Updates;

/// <summary>The file operations a swap makes, a seam so a test can stop one at any step (a kill) or fail one (a locked file).</summary>
public interface IUpdateFiles
{
    void Move(string from, string to);

    void MakeFolder(string folder);

    void RemoveFolder(string folder);
}

/// <summary>The real ones: renames on one volume, and none overwrites anything.</summary>
public sealed class UpdateFiles : IUpdateFiles
{
    public void Move(string from, string to)
    {
        if (Directory.Exists(from))
        {
            Directory.Move(from, to);
        }
        else
        {
            File.Move(from, to, overwrite: false);
        }
    }

    public void MakeFolder(string folder) => Directory.CreateDirectory(folder);

    public void RemoveFolder(string folder) => Directory.Delete(folder, recursive: false);
}

/// <summary>What became of a swap.</summary>
/// <param name="Done">Every file moved.</param>
/// <param name="Reason">Why not, in words for the quit line.</param>
/// <param name="MayBeMixed">True only when a failed swap's undo stopped too, leaving the journal for the next start (2026-11-04, review P12-16: it was "Undone", set too for refusals where nothing had moved).</param>
public sealed record UpdateSwapResult(bool Done, string? Reason = null, bool MayBeMixed = false);

/// <summary>What <see cref="UpdateSwap.Settle"/> did with an unfinished journal.</summary>
public enum UpdateSettled
{
    /// <summary>The running version is the target: only the cleanup was left, and it's done.</summary>
    Finished,

    /// <summary>The running version is the source: the moves are undone, and the journal is gone.</summary>
    Undone,

    /// <summary>The undo couldn't finish: the journal stays for the next start, and the folder may hold a mix.</summary>
    UndoStopped,

    /// <summary>Neither version, and the folder's manifest names a third: a package unpacked by hand over an interrupted swap. Nothing moved, and the journal is gone.</summary>
    Neither,

    /// <summary>
    /// A copy that can't say what the folder holds (2026-11-04, reviews P11-1, P15-2): another version with the folder's manifest missing or at the journal's own versions, or a renamed copy of the target over a swap that never finished. Nothing moved, and the journal stays for the listed exe.
    /// </summary>
    LeftAlone,

    /// <summary>The undo found something in its way, and the folder's manifest and every file it lists say the running version anyway: a package unpacked by hand over a killed swap. The journal and <c>ready</c> are gone (the moves took files from the stage), and there's nothing to say.</summary>
    AlreadyWhole,
}

/// <summary>
/// The swap at exit (2026-11-04, self-update; the user's pick: in place, with no helper process and no change of layout). Works because a running exe and its loaded DLLs can be
/// renamed on one volume, though not overwritten: the old ones move to <c>.update/previous</c>, the new ones in, with the exe last, so a copy started mid-swap is the
/// old exe, and the journal says what to do with an interrupted one. Only files that differ move: on v0.5.6 that's a few dozen of 617, which shrinks the crash window,
/// <c>previous</c> and a window close's work.
///
/// <para>Unlisted files (<c>logs/</c>, the user's strays) are never touched, with one exception: a stray at a path the new version adds, or a stray where it needs a folder, has to move. Otherwise moving the new file in would fail, the swap would undo, and the stage
/// would fail the same way at every exit. Such a file, and any listed file that no longer matches its hash, goes to <c>.update/kept/&lt;source version&gt;/</c> (2026-11-04, reviews P4-1, P2-7, the user's call), never to <c>previous/</c>: rotating that deleted them with no notice, and a rollback would have put an
/// unverified file beside the exe. So <c>previous/</c> and <c>outgoing/</c> hold only verified files, by an allow-list from the recheck, and the app never deletes <c>kept/</c>. An undo puts a kept file back; a rollback doesn't, and the next start's line names the folder. Folders follow the files: a swap makes the ones new files need and removes the
/// ones it leaves empty, so a new file can take a dropped folder's name, and an undo or a rollback reverses both.</para>
///
/// <para>Pure file moves over <see cref="IUpdateFiles"/>: what decides whether a swap may run at all (the recheck, the lock, which copy this is) is the caller's.</para>
/// </summary>
public sealed class UpdateSwap
{
    /// <summary>How many times a locked file is tried (antivirus scans hold files), small enough for a window close's 4 s.</summary>
    public const int MoveTries = 4;

    /// <summary>The pause between tries.</summary>
    public static readonly TimeSpan MoveRetryPause = TimeSpan.FromMilliseconds(150);

    // What a file manager leaves in any folder it shows (2026-11-04, review P11-8): a .DS_Store in previous/ refused every rollback, for a folder the docs invite people to open.
    private static readonly HashSet<string> FolderMetadata = new(StringComparer.OrdinalIgnoreCase) { ".DS_Store", "Thumbs.db", "desktop.ini" };

    private readonly UpdateFolder _folder;
    private readonly IUpdateFiles _files;
    private readonly Action<TimeSpan> _pause;

    public UpdateSwap(UpdateFolder folder, IUpdateFiles? files = null, Action<TimeSpan>? pause = null)
    {
        _folder = folder ?? throw new ArgumentNullException(nameof(folder));
        _files = files ?? new UpdateFiles();
        _pause = pause ?? Thread.Sleep;
    }

    /// <summary>Called between moves: a simulation build's <c>--update-swap-delay</c>, which gives the "killed halfway" scenarios something to hit.</summary>
    public Action? BetweenSteps { get; init; }

    /// <summary>
    /// Which staged files move in: every one whose listed hash differs from the installed manifest's, is new, or whose install copy no longer matches (a user's edit, a damaged disk): the manifests
    /// alone would call that unchanged and leave it in the new version. A listed file missing from the install counts as changed, since it has no entry in <paramref name="installHashes"/>.
    /// </summary>
    public static List<string> Changed(UpdateManifest installed, UpdateManifest staged, IReadOnlyDictionary<string, string> installHashes, StringComparer comparer, bool moveAll = false)
    {
        ArgumentNullException.ThrowIfNull(installed);
        ArgumentNullException.ThrowIfNull(staged);
        ArgumentNullException.ThrowIfNull(installHashes);
        var was = installed.Hashes(comparer);

        // By assignment, since a list on disk can hold keys that collide under this volume's case rule, and that must not throw on the exit's path (2026-11-04, review P3-6).
        var onDisk = new Dictionary<string, string>(comparer);
        foreach (var (path, hash) in installHashes)
        {
            onDisk[path] = hash;
        }

        var changed = new List<string>();
        foreach (var file in staged.Files)
        {
            bool same = !moveAll
                && was.TryGetValue(file.Path, out string? listed) && UpdateHash.Same(listed, file.Sha256)
                && onDisk.TryGetValue(file.Path, out string? actual) && UpdateHash.Same(actual, file.Sha256);
            if (!same)
            {
                changed.Add(file.Path);
            }
        }

        return changed;
    }

    /// <summary>What the installed manifest lists and the staged one doesn't.</summary>
    public static List<string> Dropped(UpdateManifest installed, UpdateManifest staged, StringComparer comparer)
    {
        ArgumentNullException.ThrowIfNull(installed);
        ArgumentNullException.ThrowIfNull(staged);
        var keeps = staged.Hashes(comparer);
        return [.. installed.Files.Select(f => f.Path).Where(p => !keeps.ContainsKey(p))];
    }

    /// <summary>
    /// The journal for an update, read from the disk as it stands. <paramref name="moved"/> move in from <c>staging/</c>; <paramref name="dropped"/> leave. The old set of <c>previous</c> is set aside
    /// first and restored on an undo, so a failed update can still roll back. <paramref name="verified"/> are the installed files that match the installed manifest: only they go to <c>previous/</c>, and whatever else has to move goes to <c>kept/</c>.
    /// With <paramref name="proofsVouch"/> false the proofs in <c>release/</c> aren't this version's (a package unpacked by hand over the folder): they go to <c>outgoing/</c> and go with it, so proofs in <c>previous/release</c> that don't vouch can only mean tampering.
    /// </summary>
    public UpdateJournal PlanUpdate(string source, string target, string oldExe, string newExe, IReadOnlyCollection<string> moved, IReadOnlyCollection<string> dropped, IReadOnlyCollection<string> verified, bool proofsVouch = true)
    {
        var steps = new List<UpdateStep>();
        string previous = _folder.Relative(_folder.Previous);
        if (Directory.Exists(_folder.Previous))
        {
            steps.Add(UpdateStep.Move(previous, _folder.Relative(_folder.PreviousOld)));
        }

        steps.Add(UpdateStep.MakeFolder(previous));
        var proofs = new List<UpdateStep>();
        if (Directory.Exists(_folder.Release))
        {
            if (proofsVouch)
            {
                proofs.Add(UpdateStep.Move(_folder.Relative(_folder.Release), _folder.Relative(_folder.PreviousRelease)));
            }
            else
            {
                string outgoing = _folder.Relative(_folder.Outgoing);
                proofs.Add(UpdateStep.MakeFolder(outgoing));
                proofs.Add(UpdateStep.Move(_folder.Relative(_folder.Release), outgoing + "/" + UpdateFolder.ReleaseName));
            }
        }

        var into = new List<UpdateStep> { UpdateStep.MakeFolder(_folder.Relative(_folder.Release)) };
        foreach (string name in new[] { UpdateFolder.ShaName, UpdateFolder.SigName, UpdateFolder.NotesName })
        {
            string from = Path.Combine(_folder.Staged, name);
            if (File.Exists(from))
            {
                into.Add(UpdateStep.Move(_folder.Relative(from), _folder.Relative(Path.Combine(_folder.Release, name))));
            }
        }

        Exchange(steps, previous, _folder.Relative(_folder.Staging), oldExe, newExe, moved, dropped, verified, source, proofs, into);
        return new UpdateJournal { Source = source, Target = target, Steps = steps };
    }

    /// <summary>
    /// The journal for going back: <paramref name="incoming"/>, the files in <c>previous</c> the check passed, move in, and what the installed version has in their place, or added, moves to <c>outgoing/</c>, for an undo, or to <c>kept/</c> if it isn't in
    /// <paramref name="verified"/>. Files the two versions share stay put, so a file altered by hand that both share stays as it is.
    /// </summary>
    public UpdateJournal PlanRollback(string source, string target, string currentExe, string oldExe, IReadOnlyCollection<string> incoming, IReadOnlyCollection<string> added, IReadOnlyCollection<string> verified)
    {
        var steps = new List<UpdateStep>();
        string outgoing = _folder.Relative(_folder.Outgoing);
        steps.Add(UpdateStep.MakeFolder(outgoing));
        var proofs = new List<UpdateStep>();
        if (Directory.Exists(_folder.Release))
        {
            proofs.Add(UpdateStep.Move(_folder.Relative(_folder.Release), outgoing + "/" + UpdateFolder.ReleaseName));
        }

        var into = new List<UpdateStep>();
        if (Directory.Exists(_folder.PreviousRelease))
        {
            into.Add(UpdateStep.Move(_folder.Relative(_folder.PreviousRelease), _folder.Relative(_folder.Release)));
        }

        Exchange(steps, outgoing, _folder.Relative(_folder.Previous), currentExe, oldExe, incoming, added, verified, source, proofs, into);
        return new UpdateJournal { Source = source, Target = target, Rollback = true, Steps = steps };
    }

    /// <summary>Runs a swap: the journal first, then each step. A failure undoes what was done. Only a kill leaves the journal unfinished for the next start.</summary>
    public UpdateSwapResult Run(UpdateJournal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);
        if (journal.BadPath() is { } bad)
        {
            return new UpdateSwapResult(false, UpdateText.CantRecordName(bad));
        }

        string? reason = PrepareAside();
        if (reason is not null)
        {
            return new UpdateSwapResult(false, reason);
        }

        try
        {
            journal.Save(_folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new UpdateSwapResult(false, ex.Message);
        }

        for (int index = 0; index < journal.Steps.Count; index++)
        {
            var step = journal.Steps[index];
            try
            {
                BetweenSteps?.Invoke();
                Do(step);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A folder that won't go matters only if a new file takes its name (2026-11-04, review P13-7): a terminal sitting in a dropped folder undid the whole swap at every exit. It stays, empty, and a later cleanup can have it.
                if (step.Kind == UpdateStepKind.RemoveFolder && !ALaterStepNeeds(journal.Steps, index, step.From!))
                {
                    DiagnosticLog.Info(UpdateJournal.Category, UpdateText.CleanupLeft(step.From!, ex.Message));
                    continue;
                }

                DiagnosticLog.Info(UpdateJournal.Category, UpdateText.SwapStopped(step.From ?? step.To ?? "", ex.Message));
                return new UpdateSwapResult(false, ex.Message, MayBeMixed: !Undo(journal));
            }
        }

        Finish(journal);
        return new UpdateSwapResult(true);
    }

    /// <summary>
    /// What an unfinished journal comes to, one rule for the start and the exit (2026-11-04, review P6-9: the two had drifted, and a journal of neither version reaching the exit stayed for good). The running version is the target: only the cleanup was left, if the moves did finish. It's the
    /// source: undo it, and it never finishes forward, because the old exe would then run on with new DLLs. Neither: a package unpacked by hand over an interrupted swap, where moving anything would leave a mix, so the journal goes and nothing moves, as long as the folder's manifest says that's what happened.
    ///
    /// <para>The target finishes only if the walk reaches the last step, or this exe is what the last step put there, or the folder is whole at this version (2026-11-04, review P15-2): a renamed copy of the target finished a half-done swap forward, deleted the new files it hadn't moved, and said "Updated to X" over the old exe. <paramref name="isRunningExe"/> says whether a name is this exe's.</para>
    ///
    /// <para>Hashing the install is the slow part, and <paramref name="cancellationToken"/> is the exit's budget (2026-11-05, review P22-12): a token that runs out reads as not whole, so the journal stays for the next start, which has no limit since it's the rare path and nothing else needs the folder yet.</para>
    ///
    /// <para>Only an undo stopped by what's in its way can be a package unpacked by hand (review P23-3): one stopped by a passing hold on its last steps also finds every file whole, and deleting its journal lost <c>previous.old</c>, the only earlier version. That one stays for the next start to finish. The stage goes with a hand-unpacked journal: the swap's moves took files from it, so the next quit would otherwise say "the update changed".</para>
    /// </summary>
    public UpdateSettled Settle(UpdateJournal journal, string running, Func<string, bool>? isRunningExe = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(journal);
        if (journal.Target == running)
        {
            if (ReachesTheEnd(journal) || (isRunningExe is not null && LastMoveTo(journal) is { } last && isRunningExe(last)) || IsWholeAt(running, cancellationToken))
            {
                Finish(journal);
                return UpdateSettled.Finished;
            }

            DiagnosticLog.Warn(UpdateJournal.Category, UpdateText.JournalLeftAlone(journal.Source, journal.Target, running));
            return UpdateSettled.LeftAlone;
        }

        if (journal.Source == running)
        {
            if (Undo(journal, out bool inTheWay))
            {
                return UpdateSettled.Undone;
            }

            if (inTheWay && IsWholeAt(running, cancellationToken))
            {
                UpdateFolder.DeleteQuietly(_folder.Ready);
                UpdateJournal.Delete(_folder);
                return UpdateSettled.AlreadyWhole;
            }

            return UpdateSettled.UndoStopped;
        }

        // Mid-swap the manifest is gone or at one of the journal's versions, and a stray copy of a third version deleted the only record of the moves (2026-11-04, review P11-1).
        if (UpdateManifest.Load(_folder.Manifest).Manifest is { } manifest && manifest.ParsedVersion.ToString() != journal.Source && manifest.ParsedVersion.ToString() != journal.Target)
        {
            DiagnosticLog.Warn(UpdateJournal.Category, UpdateText.JournalRejected(UpdateText.JournalNotThisVersion(journal.Source, journal.Target, running)));
            UpdateJournal.Delete(_folder);
            return UpdateSettled.Neither;
        }

        DiagnosticLog.Warn(UpdateJournal.Category, UpdateText.JournalLeftAlone(journal.Source, journal.Target, running));
        return UpdateSettled.LeftAlone;
    }

    /// <summary>
    /// Undoes a swap: a forward walk finds how far it got (<see cref="Walk"/>), and only that part is undone, in reverse, stopping at the first failure, so what's left is always the first steps of the run, as a kill mid-undo leaves it, and running again goes on from there. True when it finished, and the journal is gone.
    ///
    /// <para>2026-11-04, reviews P8-1 and P12-2. It used to walk the whole journal backward, reading "destination there, source gone" as proof a step ran, so a step that never ran, with its staged file quarantined, moved an untouched file out of the install, and a first pass that failed partway left a state the next read as a conflict for good. A move whose two paths are both there when the undo reaches it counts as a failure (review P3-10): deleting the journal would call a restore done that wasn't.</para>
    /// </summary>
    public bool Undo(UpdateJournal journal) => Undo(journal, out _);

    /// <summary><see cref="Undo(UpdateJournal)"/>, saying whether what stopped it was in its way, both paths of a move there, and not a failure that may pass.</summary>
    public bool Undo(UpdateJournal journal, out bool inTheWay)
    {
        ArgumentNullException.ThrowIfNull(journal);
        inTheWay = false;
        var walk = Walk(journal.Steps);
        if (walk.Conflict)
        {
            inTheWay = true;
            var at = journal.Steps[walk.Ran];
            DiagnosticLog.Warn(UpdateJournal.Category, UpdateText.UndoFoundBoth(at.From!, at.To!));
            return false;
        }

        for (int index = walk.Ran - 1; index >= 0; index--)
        {
            var step = journal.Steps[index];
            try
            {
                BetweenSteps?.Invoke();
                if (!UndoOne(step))
                {
                    inTheWay = true;
                    DiagnosticLog.Warn(UpdateJournal.Category, UpdateText.UndoFoundBoth(step.From!, step.To!));
                    return false;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                DiagnosticLog.Warn(UpdateJournal.Category, UpdateText.UndoStopped(step.From ?? step.To ?? "", ex.Message));
                return false;
            }
        }

        UpdateJournal.Delete(_folder);
        return true;
    }

    /// <summary>
    /// What a swap does last: delete <c>ready</c>, <c>rollback</c> and what's left of <c>staging/</c> and <c>staged/</c>, mark the journal finished, and only then delete <c>previous.old/</c>, <c>outgoing/</c> and a rollback's <c>previous/</c>. Tolerates files still in use, such as an old guard that hasn't exited: what it can't delete now, it retries next time. Also what the next start does under the new exe.
    ///
    /// <para>That order is the walk's (2026-11-04): those three are what its first step made or moved, so with one deleted and the journal unfinished, a kill just then read as a first step that never ran, and an old copy waiting on the lock undid nothing and ran on over new files. A save that fails deletes none of them. What a kill leaves after the save goes later: <c>previous.old/</c> and <c>outgoing/</c> as <see cref="UpdateStartup"/>'s tidy and the next swap do, a rollback's <c>previous/</c>, which holds no manifest by then and so reads as nothing to go back to, with the next swap's rotation.</para>
    /// </summary>
    public void Finish(UpdateJournal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);
        UpdateFolder.DeleteQuietly(_folder.Ready);
        UpdateFolder.DeleteQuietly(_folder.Rollback);
        UpdateFolder.DeleteQuietly(_folder.Staging);
        UpdateFolder.DeleteQuietly(_folder.StagingParts);
        UpdateFolder.DeleteQuietly(_folder.Staged);
        journal.Finished = true;
        try
        {
            journal.Save(_folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Warn(UpdateJournal.Category, UpdateText.JournalNotDeleted(ex.Message));
            return;
        }

        UpdateFolder.DeleteQuietly(_folder.PreviousOld);
        UpdateFolder.DeleteQuietly(_folder.Outgoing);
        if (journal.Rollback)
        {
            // A second /update rollback says there's nothing to go back to, not "going back" to the newer version; the check offers that one again instead.
            UpdateFolder.DeleteQuietly(_folder.Previous);
        }

        // A close's failure from before this swap is old news (2026-11-04, review P16-6): it printed "didn't install" under "Updated to" for the same version.
        UpdateFolder.DeleteQuietly(_folder.SwapFailed);
    }

    /// <summary>
    /// How far a run of <paramref name="steps"/> got, read from the disk, since the journal records no progress: steps run in order, so the first that didn't is where it stopped. A move with its destination there and its source gone ran; with neither there, go on (a source that vanished, or a file a later step moved on); with only its source there, it didn't run. A folder made exists, or it didn't run. A removed folder never decides, since one may have been left standing (review P13-7).
    ///
    /// <para>Both of a move's paths there is one of two things. If a later step puts something at its source, that's explained, and it ran: every changed file looks like this once its new copy has moved in. If not, what follows decides: a later step that ran, before any that didn't, means this one ran too, and something is in the way of restoring it, a conflict. Otherwise it never ran, with something in its way (an empty stray folder a kill got to first, a file that appeared since), and the walk stops there. A model of this rule held at every kill point of an update's, a stray folder's, a rollback's and a renamed exe's journal, and at every stop of the undo, and the earlier wording failed nearly all of them (the tests hold the same).</para>
    /// </summary>
    /// <returns><c>Ran</c>: how many of the first steps ran. <c>Conflict</c>: true with <c>Ran</c> the index of the step in the way.</returns>
    internal (int Ran, bool Conflict) Walk(IReadOnlyList<UpdateStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        for (int index = 0; index < steps.Count; index++)
        {
            switch (Look(steps[index]))
            {
                case Seen.NotRun:
                    return (index, false);
                case Seen.Both when !Explained(steps, index):
                    return (index, ALaterStepRan(steps, index));
            }
        }

        return (steps.Count, false);
    }

    private enum Seen
    {
        Ran,
        NotRun,
        Both,
        NoWord,
    }

    private Seen Look(UpdateStep step)
    {
        switch (step.Kind)
        {
            case UpdateStepKind.MakeFolder:
                return Directory.Exists(_folder.Full(step.To!)) ? Seen.Ran : Seen.NotRun;
            case UpdateStepKind.Move:
                bool there = UpdateFolder.Exists(_folder.Full(step.To!));
                bool source = UpdateFolder.Exists(_folder.Full(step.From!));
                return there && source ? Seen.Both : source ? Seen.NotRun : there ? Seen.Ran : Seen.NoWord;
            default:
                return Seen.NoWord;
        }
    }

    private bool Explained(IReadOnlyList<UpdateStep> steps, int index)
    {
        var comparer = CaseRule.Comparer(_folder.Install);
        for (int later = index + 1; later < steps.Count; later++)
        {
            if (steps[later].To is { } to && comparer.Equals(to, steps[index].From))
            {
                return true;
            }
        }

        return false;
    }

    // A folder that exists says nothing here: .update/kept can be there from an earlier swap.
    private bool ALaterStepRan(IReadOnlyList<UpdateStep> steps, int index)
    {
        for (int later = index + 1; later < steps.Count; later++)
        {
            var seen = Look(steps[later]);
            if (seen == Seen.NotRun)
            {
                return false;
            }

            if (steps[later].Kind == UpdateStepKind.Move && (seen == Seen.Ran || (seen == Seen.Both && Explained(steps, later))))
            {
                return true;
            }
        }

        return false;
    }

    private bool ALaterStepNeeds(IReadOnlyList<UpdateStep> steps, int index, string folder)
    {
        var comparer = CaseRule.Comparer(_folder.Install);
        var comparison = CaseRule.Comparison(_folder.Install);
        for (int later = index + 1; later < steps.Count; later++)
        {
            if (steps[later].To is { } to && (comparer.Equals(to, folder) || to.StartsWith(folder + "/", comparison)))
            {
                return true;
            }
        }

        return false;
    }

    // The step alone can read as run when its staged file vanished, so the last move's destination has to be there too.
    private bool ReachesTheEnd(UpdateJournal journal)
    {
        var walk = Walk(journal.Steps);
        return !walk.Conflict && walk.Ran == journal.Steps.Count && (LastMoveTo(journal) is not { } last || UpdateFolder.Exists(_folder.Full(last)));
    }

    private static string? LastMoveTo(UpdateJournal journal) => journal.Steps.LastOrDefault(step => step.Kind == UpdateStepKind.Move)?.To;

    // What a package unpacked by hand over a killed swap reads as: the undo can't tell it from a conflict, and the hashes can.
    private bool IsWholeAt(string running, CancellationToken cancellationToken)
    {
        if (UpdateManifest.Load(_folder.Manifest).Manifest is not { } manifest || manifest.ParsedVersion.ToString() != running)
        {
            return false;
        }

        try
        {
            return manifest.Files.All(file => File.Exists(_folder.Full(file.Path)) && UpdateHash.Same(UpdateHash.OfFile(_folder.Full(file.Path), cancellationToken), file.Sha256));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            DiagnosticLog.Info(UpdateJournal.Category, ex.Message);
            return false;
        }
    }

    /// <summary>The files under <paramref name="folder"/> with its manifest and <c>release/</c> left out, as relative paths.</summary>
    public static List<string> FilesHeldIn(string folder)
    {
        var held = new List<string>();
        if (!Directory.Exists(folder))
        {
            return held;
        }

        foreach (string file in UpdateFolder.EntriesUnder(folder))
        {
            string relative = Path.GetRelativePath(folder, file).Replace(Path.DirectorySeparatorChar, '/');
            if (relative != UpdateManifest.FileName && !relative.StartsWith(UpdateFolder.ReleaseName + "/", StringComparison.Ordinal) && !FolderMetadata.Contains(Path.GetFileName(file)))
            {
                held.Add(relative);
            }
        }

        return held;
    }

    // The heart of both plans: what leaves, what's left of the folders, and what comes in, with the exe last. The old exe leaves as late as it can, just before the new one comes in,
    // so the window with no exe at the folder's top is one rename wide. What leaves goes aside only if the recheck verified it; everything else, a stray folder's contents found only here included, goes to a kept folder of its own for this swap.
    private void Exchange(List<UpdateStep> steps, string aside, string source, string oldExe, string newExe, IReadOnlyCollection<string> incoming, IReadOnlyCollection<string> dropped, IReadOnlyCollection<string> verified, string fromVersion, List<UpdateStep> proofsOut, List<UpdateStep> proofsIn)
    {
        var comparer = CaseRule.Comparer(_folder.Install);
        var allowed = new HashSet<string>(verified, comparer);
        var gone = new HashSet<string>(dropped, comparer);
        var needed = new HashSet<string>(comparer);
        var strayFolders = new List<string>();
        foreach (string path in incoming)
        {
            for (string? parent = ParentOf(path); parent is not null; parent = ParentOf(parent))
            {
                needed.Add(parent);
                if (IsFileOrLink(_folder.Full(parent)))
                {
                    gone.Add(parent);   // a stray file where it needs a folder, or a link it would write through
                }
            }

            string full = _folder.Full(path);
            if (IsFileOrLink(full))
            {
                gone.Add(path);   // a changed file, or a stray at a path the new version adds
            }
            else if (Directory.Exists(full))
            {
                strayFolders.Add(path);   // a folder at a path it needs for a file: what's still in it goes aside too
                try
                {
                    foreach (string file in UpdateFolder.EntriesUnder(full))
                    {
                        gone.Add(_folder.Relative(file));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // It has to be emptied for the new file, so the update can't go on, but it can say which folder (2026-11-05, review P22-11): every quit failed with a bare "Access denied".
                    throw new IOException(UpdateText.StrayFolderUnreadable(path, ex.Message), ex);
                }
            }
        }

        gone.RemoveWhere(path => !IsFileOrLink(_folder.Full(path)));

        // What's behind a link isn't the install's to move (2026-11-05, review P23-2): a junction moved out whole, then a plan to move a file through it threw at every exit, and a dropped file left the link's real target outside the install. The link goes whole, or it stays, and a kept file behind a link that goes would be lost from the install, so that's refused, naming it.
        foreach (string link in gone.Where(path => Directory.Exists(_folder.Full(path)) && UpdateFolder.IsLink(_folder.Full(path))))
        {
            string under = link + "/";
            if (verified.FirstOrDefault(path => path.StartsWith(under, CaseRule.Comparison(_folder.Install)) && !incoming.Contains(path, comparer) && !dropped.Contains(path, comparer)) is { } behind)
            {
                throw new IOException(UpdateText.LinkHoldsKeptFile(link, behind));
            }
        }

        gone.RemoveWhere(IsBehindALink);
        strayFolders.RemoveAll(IsBehindALink);
        var made = new HashSet<string>(comparer);
        string kept = NewKeptFolder(fromVersion);
        string? oldExeOut = null;
        string? oldExeTo = null;
        foreach (string path in gone.Order(StringComparer.Ordinal))
        {
            bool stays = allowed.Contains(path);
            string to = (stays ? aside : kept) + "/" + path;
            MakeParents(steps, stays ? aside : UpdateFolder.Name, to, made);
            if (comparer.Equals(path, oldExe))
            {
                oldExeOut = path;
                oldExeTo = to;
                continue;
            }

            steps.Add(UpdateStep.Move(path, to));
        }

        if (File.Exists(_folder.Manifest))
        {
            steps.Add(UpdateStep.Move(UpdateManifest.FileName, aside + "/" + UpdateManifest.FileName));
        }

        steps.AddRange(proofsOut);
        foreach (string folder in EmptiedFolders(gone, strayFolders, needed, comparer))
        {
            steps.Add(UpdateStep.RemoveFolder(folder));
        }

        steps.AddRange(proofsIn);

        // A new file or folder taking the old exe's name, when the exe's own name changes (2026-11-04, reviews P7-5, P12-6): the old one has to leave first, or the step hits it, undoes, and fails the same at every exit. What takes the name moves in right after (review P12-4), the first of the incoming moves: a kill among the rest left nothing at that name, and nothing to run the undo.
        bool renamed = !comparer.Equals(oldExe, newExe);
        bool takesItsName = renamed && (incoming.Contains(oldExe, comparer) || needed.Contains(oldExe));
        if (oldExeOut is not null && takesItsName)
        {
            steps.Add(UpdateStep.Move(oldExeOut, oldExeTo!));
            oldExeOut = null;
        }

        foreach (string folder in needed.OrderBy(f => f.Length))
        {
            string full = _folder.Full(folder);
            if (!Directory.Exists(full) || gone.Contains(folder) || IsBehindALink(folder))
            {
                steps.Add(UpdateStep.MakeFolder(folder));
            }
        }

        var comparison = CaseRule.Comparison(_folder.Install);
        bool TakesItsName(string path) => takesItsName && (comparer.Equals(path, oldExe) || path.StartsWith(oldExe + "/", comparison));
        string? newExeIn = null;
        foreach (string path in incoming.OrderBy(path => TakesItsName(path) ? 0 : 1).ThenBy(path => path, StringComparer.Ordinal))
        {
            if (comparer.Equals(path, newExe))
            {
                newExeIn = path;
                continue;
            }

            steps.Add(UpdateStep.Move(source + "/" + path, path));
        }

        steps.Add(UpdateStep.Move(source + "/" + UpdateManifest.FileName, UpdateManifest.FileName));
        if (oldExeOut is not null)
        {
            steps.Add(UpdateStep.Move(oldExeOut, oldExeTo!));
        }

        if (newExeIn is not null)
        {
            steps.Add(UpdateStep.Move(source + "/" + newExeIn, newExeIn));
        }
    }

    // A new folder per swap, so names can't collide with what an earlier swap kept.
    private string NewKeptFolder(string version)
    {
        string name = version;
        for (int next = 2; Directory.Exists(Path.Combine(_folder.Kept, name)); next++)
        {
            name = string.Create(CultureInfo.InvariantCulture, $"{version}-{next}");
        }

        return _folder.Relative(_folder.Kept) + "/" + name;
    }

    private static void MakeParents(List<UpdateStep> steps, string above, string path, HashSet<string> made)
    {
        var parents = new Stack<string>();
        for (string? parent = ParentOf(path); parent is not null && parent.Length > above.Length; parent = ParentOf(parent))
        {
            parents.Push(parent);
        }

        while (parents.TryPop(out string? folder))
        {
            if (made.Add(folder))
            {
                steps.Add(UpdateStep.MakeFolder(folder));
            }
        }
    }

    // Deepest first. An emptied folder's own empty subfolders go with it, or removing it would fail and undo the swap at every exit.
    private List<string> EmptiedFolders(HashSet<string> gone, List<string> strayFolders, HashSet<string> needed, StringComparer comparer)
    {
        var candidates = new HashSet<string>(strayFolders, comparer);
        foreach (string path in gone)
        {
            for (string? parent = ParentOf(path); parent is not null; parent = ParentOf(parent))
            {
                candidates.Add(parent);
            }
        }

        var emptied = new HashSet<string>(comparer);
        foreach (string folder in candidates)
        {
            string full = _folder.Full(folder);
            if (!needed.Contains(folder) && Directory.Exists(full) && !UpdateFolder.IsLink(full) && IsAllGone(full, gone))
            {
                emptied.Add(folder);
                foreach (string sub in UpdateFolder.FoldersUnder(full))
                {
                    emptied.Add(_folder.Relative(sub));
                }
            }
        }

        return [.. emptied.OrderByDescending(f => f.Count(c => c == '/')).ThenBy(f => f, StringComparer.Ordinal)];
    }

    // A folder it can't read isn't all gone, and it stays (2026-11-04, review P8-11): a subfolder this account can't open, in a folder the new version drops, stopped the update at every exit.
    private bool IsAllGone(string fullFolder, HashSet<string> gone)
    {
        try
        {
            return UpdateFolder.EntriesUnder(fullFolder).All(file => gone.Contains(_folder.Relative(file)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Info(UpdateJournal.Category, ex.Message);
            return false;
        }
    }

    private static bool IsFileOrLink(string full) => File.Exists(full) || UpdateFolder.IsLink(full);

    private bool IsBehindALink(string path)
    {
        for (string? parent = ParentOf(path); parent is not null; parent = ParentOf(parent))
        {
            if (UpdateFolder.IsLink(_folder.Full(parent)))
            {
                return true;
            }
        }

        return false;
    }

    private static string? ParentOf(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash > 0 ? path[..slash] : null;
    }

    private string? PrepareAside()
    {
        // A leftover in the way of the rotation: what the last cleanup couldn't delete, retried now. If it's still held, this exit can't swap; the stage waits.
        UpdateFolder.DeleteQuietly(_folder.PreviousOld);
        UpdateFolder.DeleteQuietly(_folder.Outgoing);
        string? stuck = Directory.Exists(_folder.PreviousOld) ? _folder.PreviousOld : Directory.Exists(_folder.Outgoing) ? _folder.Outgoing : null;
        return stuck is null ? null : UpdateText.LeftoverInUse(stuck);
    }

    private void Do(UpdateStep step)
    {
        switch (step.Kind)
        {
            case UpdateStepKind.Move:
                Retry(() => _files.Move(_folder.Full(step.From!), _folder.Full(step.To!)));
                break;
            case UpdateStepKind.MakeFolder:
                _files.MakeFolder(_folder.Full(step.To!));
                break;
            case UpdateStepKind.RemoveFolder:
                Retry(() => _files.RemoveFolder(_folder.Full(step.From!)));
                break;
        }
    }

    // False when a move finds both of its paths there: nothing to overwrite, and the undo stops.
    private bool UndoOne(UpdateStep step)
    {
        switch (step.Kind)
        {
            case UpdateStepKind.Move:
                string from = _folder.Full(step.From!);
                string to = _folder.Full(step.To!);
                bool there = UpdateFolder.Exists(to);
                bool source = UpdateFolder.Exists(from);
                if (there && source)
                {
                    return false;
                }

                if (there)
                {
                    if (Path.GetDirectoryName(from) is { } parent)
                    {
                        _files.MakeFolder(parent);
                    }

                    Retry(() => _files.Move(to, from));
                }

                break;
            case UpdateStepKind.MakeFolder:
                string made = _folder.Full(step.To!);
                if (Directory.Exists(made) && DropMetadata(made))
                {
                    Retry(() => _files.RemoveFolder(made));
                }

                break;
            case UpdateStepKind.RemoveFolder:
                _files.MakeFolder(_folder.Full(step.From!));
                break;
        }

        return true;
    }

    // Whether the folder holds nothing else, with what a file manager left in it gone (2026-11-05, review P23-5): a .DS_Store in a folder the swap made kept it there, so the move before it found both paths and every start said the install may be mixed, over a hidden file.
    private static bool DropMetadata(string folder)
    {
        var entries = Directory.EnumerateFileSystemEntries(folder).ToList();
        if (!entries.All(entry => File.Exists(entry) && FolderMetadata.Contains(Path.GetFileName(entry))))
        {
            return false;
        }

        foreach (string entry in entries)
        {
            UpdateFolder.DeleteQuietly(entry);
        }

        return true;
    }

    private void Retry(Action move)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                move();
                return;
            }
            catch (Exception ex) when (attempt < MoveTries && ex is IOException or UnauthorizedAccessException)
            {
                _pause(MoveRetryPause);
            }
        }
    }

}
