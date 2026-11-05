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
/// <param name="Undone">True when a failed swap's undo finished: the old version is whole and a stage stays for the next exit. False leaves the journal for the next start.</param>
public sealed record UpdateSwapResult(bool Done, string? Reason = null, bool Undone = false);

/// <summary>What <see cref="UpdateSwap.Settle"/> did with an unfinished journal.</summary>
public enum UpdateSettled
{
    /// <summary>The running version is the target: only the cleanup was left, and it's done.</summary>
    Finished,

    /// <summary>The running version is the source: the moves are undone, and the journal is gone.</summary>
    Undone,

    /// <summary>The undo couldn't finish: the journal stays for the next start, and the folder may hold a mix.</summary>
    UndoStopped,

    /// <summary>Neither version: a package unpacked by hand over an interrupted swap. Nothing moved, and the journal is gone.</summary>
    Neither,
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

    // Hidden and system files too: a stray dotfile still stands in a folder's way. A folder it can't read throws (2026-11-04, review P4-17): skipped, it read as empty, "not empty" then undid the swap at every exit with no cause named.
    private static readonly EnumerationOptions AllEntries = new() { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false };

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
            return new UpdateSwapResult(false, UpdateText.CantRecordName(bad), Undone: true);
        }

        string? reason = PrepareAside();
        if (reason is not null)
        {
            return new UpdateSwapResult(false, reason, Undone: true);
        }

        try
        {
            journal.Save(_folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new UpdateSwapResult(false, ex.Message, Undone: true);
        }

        foreach (var step in journal.Steps)
        {
            try
            {
                BetweenSteps?.Invoke();
                Do(step);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                DiagnosticLog.Info(UpdateJournal.Category, UpdateText.SwapStopped(step.From ?? step.To ?? "", ex.Message));
                return new UpdateSwapResult(false, ex.Message, Undone: Undo(journal));
            }
        }

        Finish(journal);
        return new UpdateSwapResult(true);
    }

    /// <summary>
    /// What an unfinished journal comes to, one rule for the start and the exit (2026-11-04, review P6-9: the two had drifted, and a journal of neither version reaching the exit stayed for good). The running version is the target: only the cleanup was left. It's the
    /// source: undo it, and it never finishes forward, because the old exe would then run on with new DLLs. Neither: a package unpacked by hand over an interrupted swap, where moving anything would leave a mix, so the journal goes and nothing moves.
    /// </summary>
    public UpdateSettled Settle(UpdateJournal journal, string running)
    {
        ArgumentNullException.ThrowIfNull(journal);
        if (journal.Target == running)
        {
            Finish(journal);
            return UpdateSettled.Finished;
        }

        if (journal.Source == running)
        {
            return Undo(journal) ? UpdateSettled.Undone : UpdateSettled.UndoStopped;
        }

        DiagnosticLog.Warn(UpdateJournal.Category, UpdateText.JournalRejected(UpdateText.JournalNotThisVersion(journal.Source, journal.Target, running)));
        UpdateJournal.Delete(_folder);
        return UpdateSettled.Neither;
    }

    /// <summary>
    /// Undoes a swap, walking its steps back. Each looks at what's there before it moves anything, so an interrupted undo just runs again. True when it finished, and the journal is gone.
    ///
    /// <para>A move with both of its paths present (2026-11-04, review P3-10) is ignored until the walk meets a step that did run: steps past the kill never ran and look just like that (<c>staging/x</c> and the old <c>x</c>), but steps run in order, so before a step
    /// that ran, both present means something is in the way of a real restore. That counts as not whole, so the journal stays: deleting it would call a restore done that wasn't.</para>
    /// </summary>
    public bool Undo(UpdateJournal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);
        bool whole = true;
        bool reached = false;
        foreach (var step in Enumerable.Reverse(journal.Steps))
        {
            try
            {
                BetweenSteps?.Invoke();
                if (step.Kind == UpdateStepKind.Move)
                {
                    bool there = Exists(_folder.Full(step.To!));
                    bool source = Exists(_folder.Full(step.From!));
                    if (there && source && reached)
                    {
                        DiagnosticLog.Warn(UpdateJournal.Category, UpdateText.UndoFoundBoth(step.From!, step.To!));
                        whole = false;
                        continue;
                    }

                    reached |= there && !source;
                }

                UndoOne(step);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                DiagnosticLog.Warn(UpdateJournal.Category, UpdateText.UndoStopped(step.From ?? step.To ?? "", ex.Message));
                whole = false;
            }
        }

        if (whole)
        {
            UpdateJournal.Delete(_folder);
        }

        return whole;
    }

    /// <summary>
    /// What a swap does last: delete <c>ready</c>, <c>rollback</c>, what's left of <c>staging/</c> and <c>staged/</c>, and <c>previous.old/</c> or <c>outgoing/</c>, then mark the journal
    /// finished. Tolerates files still in use, such as an old guard that hasn't exited: what it can't delete now, it retries next time. Also what the next start does under the new exe.
    /// </summary>
    public void Finish(UpdateJournal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);
        DeleteQuietly(_folder.Ready);
        DeleteQuietly(_folder.Rollback);
        UpdateFolder.DeleteQuietly(_folder.Staging);
        UpdateFolder.DeleteQuietly(_folder.StagingParts);
        UpdateFolder.DeleteQuietly(_folder.Staged);
        UpdateFolder.DeleteQuietly(_folder.PreviousOld);
        UpdateFolder.DeleteQuietly(_folder.Outgoing);
        if (journal.Rollback)
        {
            // A second /update rollback says there's nothing to go back to, not "going back" to the newer version; the check offers that one again instead.
            UpdateFolder.DeleteQuietly(_folder.Previous);
        }

        journal.Finished = true;
        try
        {
            journal.Save(_folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Warn(UpdateJournal.Category, UpdateText.JournalNotDeleted(ex.Message));
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

        foreach (string file in Directory.EnumerateFiles(folder, "*", AllEntries))
        {
            string relative = Path.GetRelativePath(folder, file).Replace(Path.DirectorySeparatorChar, '/');
            if (relative != UpdateManifest.FileName && !relative.StartsWith(UpdateFolder.ReleaseName + "/", StringComparison.Ordinal))
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
                if (File.Exists(_folder.Full(parent)))
                {
                    gone.Add(parent);   // a stray file where it needs a folder
                }
            }

            string full = _folder.Full(path);
            if (File.Exists(full))
            {
                gone.Add(path);   // a changed file, or a stray at a path the new version adds
            }
            else if (Directory.Exists(full))
            {
                strayFolders.Add(path);   // a folder at a path it needs for a file: what's still in it goes aside too
                foreach (string file in Directory.EnumerateFiles(full, "*", AllEntries))
                {
                    gone.Add(_folder.Relative(file));
                }
            }
        }

        gone.RemoveWhere(path => !File.Exists(_folder.Full(path)));
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
        foreach (string folder in needed.OrderBy(f => f.Length))
        {
            string full = _folder.Full(folder);
            if (!Directory.Exists(full) || gone.Contains(folder))
            {
                steps.Add(UpdateStep.MakeFolder(folder));
            }
        }

        // A new file taking the old exe's name, when the exe's own name changes (2026-11-04, review P7-5): the old one has to leave first, or the move hits it, undoes, and fails the same at every exit.
        if (oldExeOut is not null && !comparer.Equals(oldExe, newExe) && incoming.Contains(oldExe, comparer))
        {
            steps.Add(UpdateStep.Move(oldExeOut, oldExeTo!));
            oldExeOut = null;
        }

        string? newExeIn = null;
        foreach (string path in incoming.Order(StringComparer.Ordinal))
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
            if (!needed.Contains(folder) && Directory.Exists(full) && IsAllGone(full, gone))
            {
                emptied.Add(folder);
                foreach (string sub in Directory.EnumerateDirectories(full, "*", AllEntries))
                {
                    emptied.Add(_folder.Relative(sub));
                }
            }
        }

        return [.. emptied.OrderByDescending(f => f.Count(c => c == '/')).ThenBy(f => f, StringComparer.Ordinal)];
    }

    private bool IsAllGone(string fullFolder, HashSet<string> gone) =>
        Directory.EnumerateFiles(fullFolder, "*", AllEntries).All(file => gone.Contains(_folder.Relative(file)));

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

    private void UndoOne(UpdateStep step)
    {
        switch (step.Kind)
        {
            case UpdateStepKind.Move:
                string from = _folder.Full(step.From!);
                string to = _folder.Full(step.To!);
                if (Exists(to) && !Exists(from))
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
                if (Directory.Exists(made) && !Directory.EnumerateFileSystemEntries(made).Any())
                {
                    Retry(() => _files.RemoveFolder(made));
                }

                break;
            case UpdateStepKind.RemoveFolder:
                _files.MakeFolder(_folder.Full(step.From!));
                break;
        }
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

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Info(UpdateJournal.Category, UpdateText.CleanupLeft(path, ex.Message));
        }
    }
}
