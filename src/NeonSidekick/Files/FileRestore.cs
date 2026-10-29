using System.Globalization;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.Files;

/// <summary>What happened to one path of a restore.</summary>
public enum RestoreOutcome
{
    /// <summary>Put back as it was.</summary>
    Restored,

    /// <summary>It already held what the restore would put there (put back by hand, or by an earlier restore stopped part-way).</summary>
    AlreadyBack,

    /// <summary>Changed since the turn and skipped (the user's answer), or a folder where a file was (or a file where a folder was), always skipped.</summary>
    Skipped,

    /// <summary>Its old bytes weren't kept (too big, a cap, an online-only file, the store failed): left as it is.</summary>
    NotSaved,

    /// <summary>The restore could not put it back (read-only now, refused by the guards, the root gone…).</summary>
    Failed,
}

/// <summary>One path a restore touches, under its checkpoint's root; <paramref name="Folder"/> when the lists show it as a folder.</summary>
public sealed record RestorePath(string Root, string Path, bool Folder = false)
{
    /// <summary>The full path.</summary>
    public string Full => FileJournal.FullOf(Root, Path);
}

/// <summary>A path changed since the turn (the user's edit, a shell command): restoring it means writing over that change.</summary>
/// <param name="KindChanged">
/// A folder now where a file was, or a file where a folder was, or a moved file changed since (its old bytes were never saved): always
/// skipped, even under "restore over".
/// </param>
public sealed record RestoreConflict(RestorePath Where, bool KindChanged);

/// <summary>One path's result, with the diff of what came back for a text file.</summary>
public sealed record RestoredFile(RestorePath Where, RestoreOutcome Outcome, string Detail = "", FileDiff? Diff = null);

/// <summary>
/// What a restore would do, worked out before any question (2026-10-28, the checkpoints plan): the readable checkpoints newest first (a
/// checkpoint this build can't read stops the list there, the newer ones kept), the ids no longer kept, the paths and the conflicts.
/// </summary>
public sealed class RestorePlan
{
    internal RestorePlan(
        IReadOnlyList<CheckpointFound> checkpoints,
        IReadOnlyList<string> noLongerKept,
        CheckpointFound? stoppedAt,
        IReadOnlyList<RestorePath> paths,
        IReadOnlyList<RestoreConflict> conflicts,
        IReadOnlySet<RestorePath> alreadyBack,
        PlanFacts facts)
    {
        Checkpoints = checkpoints;
        NoLongerKept = noLongerKept;
        StoppedAt = stoppedAt;
        Paths = paths;
        Conflicts = conflicts;
        AlreadyBack = alreadyBack;
        Facts = facts;
    }

    /// <summary>The checkpoints the restore replays, newest first (both roots of a pair).</summary>
    public IReadOnlyList<CheckpointFound> Checkpoints { get; }

    /// <summary>The ids asked for whose refs are gone (retention, the age rule, the size cap, a purge).</summary>
    public IReadOnlyList<string> NoLongerKept { get; }

    /// <summary>The first checkpoint this build can't read (newer version, damaged): the replay stops above it.</summary>
    public CheckpointFound? StoppedAt { get; }

    /// <summary>Every path the replay touches, in the order first met.</summary>
    public IReadOnlyList<RestorePath> Paths { get; }

    /// <summary>The paths changed since.</summary>
    public IReadOnlyList<RestoreConflict> Conflicts { get; }

    /// <summary>The paths already holding what the restore would put there.</summary>
    public IReadOnlySet<RestorePath> AlreadyBack { get; }

    internal PlanFacts Facts { get; }

    /// <summary>
    /// The paths the lists name (2026-10-28, review P7-04: one file in two new folders read "3 files"): <see cref="Paths"/> without a made folder
    /// that holds another made path, and without a move's destination (the move is named by its source); folders marked for a trailing separator.
    /// </summary>
    public IReadOnlyList<RestorePath> Listed => Paths.Where(Facts.IsListed).Select(Facts.Shown).ToList();

    /// <summary>The checkpoint ids the replay covers (each once).</summary>
    public IReadOnlyList<string> Ids => Checkpoints.Select(c => c.Id).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>The roots the replay writes into.</summary>
    public IReadOnlyList<string> Roots => Checkpoints.Select(c => c.Root).Distinct(WorkingDirectory.PathComparer).ToList();

    /// <summary>Whether any checkpoint lost some of its turn's changes.</summary>
    public bool Incomplete => Checkpoints.Any(c => c.Record!.Incomplete);

    /// <summary>The tools the replayed turns ran, each once.</summary>
    public IReadOnlyList<string> Tools => Checkpoints.SelectMany(c => c.Record!.Tools).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>Whether there is anything to put back.</summary>
    public bool IsEmpty => Checkpoints.Count == 0;
}

/// <summary>What a plan's walk learned about each path, for the run and the lists (2026-10-28, the review's fixes of the replay).</summary>
internal sealed class PlanFacts(IEqualityComparer<RestorePath> comparer)
{
    /// <summary>Each path's oldest entry: what the restore ends on.</summary>
    public Dictionary<RestorePath, CheckpointEntry> Oldest { get; } = new(comparer);

    /// <summary>The destinations of the plan's moves.</summary>
    public HashSet<RestorePath> MoveEnds { get; } = new(comparer);

    /// <summary>The paths an entry names as a folder.</summary>
    public HashSet<RestorePath> Folders { get; } = new(comparer);

    /// <summary>The made folders (Absent) holding another made path: left out of the lists.</summary>
    public HashSet<RestorePath> Holders { get; } = new(comparer);

    /// <summary>Whether the lists name the path: not a folder named by what's in it, not a move's destination (the move is named by its source).</summary>
    public bool IsListed(RestorePath where) =>
        !Holders.Contains(where)
        && !(Oldest.TryGetValue(where, out var entry) && entry is { Op: CheckpointOp.Moved, To: { } to }
            && WorkingDirectory.PathComparer.Equals(to, where.Path) && !IsCaseOnly(entry));

    public RestorePath Shown(RestorePath where) => Folders.Contains(where) ? where with { Folder = true } : where;

    /// <summary>Whether a move only changed its name's case.</summary>
    public static bool IsCaseOnly(CheckpointEntry entry) =>
        entry.Op == CheckpointOp.Moved && entry.To is { } to && !string.Equals(entry.Path, to, StringComparison.Ordinal)
        && string.Equals(entry.Path, to, StringComparison.OrdinalIgnoreCase);
}

/// <summary>What a restore did.</summary>
public sealed record RestoreReport(IReadOnlyList<RestoredFile> Files, bool Stopped, IReadOnlyList<string> Pictures)
{
    /// <summary>The checkpoint ids put back whole (spent): every one when the restore wasn't stopped and nothing failed for a reason that may pass.</summary>
    public IReadOnlyList<string> Spent { get; init; } = [];

    /// <summary>
    /// Some files couldn't be put back for a reason that may pass (a file open elsewhere, the folder gone for now; 2026-10-28, review P1-09):
    /// the checkpoints are kept, and <c>/undo</c> again tries them (what came back reads as already back).
    /// </summary>
    public bool Kept { get; init; }

    public int Count(RestoreOutcome outcome) => Files.Count(f => f.Outcome == outcome);
}

/// <summary>
/// Puts files back from checkpoints (2026-10-28, the checkpoints plan): each checkpoint's op log replayed backwards, newest first, through a
/// <see cref="WorkingDirectory"/> pinned at the checkpoint's root (<see cref="WorkingDirectory.PinnedAt"/>, so <see cref="WriteGuard"/> judges
/// every path as the turn's tools were), never journaled. Conflicts are found up front (<see cref="Plan"/>) and asked again just before the
/// writes (<see cref="Run"/>); a conflicting file goes to the bin only once its saved bytes are in hand, or is skipped. Attributes, the Unix
/// mode and the internet mark are set once per path at the end, from the oldest state restored. The token is read between files, never inside
/// one; a stopped restore keeps its checkpoints.
/// </summary>
public static class FileRestore
{
    /// <summary>The files a pane, a card or a question lists before "and N more".</summary>
    public const int ListedFiles = 20;

    /// <summary>
    /// Works out a restore of <paramref name="idsNewestFirst"/> (the turns' stamps): each id's checkpoints in every store (<paramref name="found"/>
    /// when the caller already has them, one <c>Find</c> per <c>/undo</c>, 2026-10-28, review P1-12), conflicts against each path's after-state
    /// in the newest checkpoint touching it, and the paths already back; one hash budget for the plan. <paramref name="cancellationToken"/>
    /// stops it between paths (thrown).
    /// </summary>
    public static RestorePlan Plan(ICheckpointStore store, string profileDirectory, IReadOnlyList<string> idsNewestFirst, IReadOnlyList<CheckpointFound>? found = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(profileDirectory);
        ArgumentNullException.ThrowIfNull(idsNewestFirst);
        found ??= store.Find(profileDirectory, idsNewestFirst.ToList());
        var checkpoints = new List<CheckpointFound>();
        var gone = new List<string>();
        CheckpointFound? stopped = null;
        foreach (string id in idsNewestFirst)
        {
            var mine = found.Where(f => string.Equals(f.Id, id, StringComparison.Ordinal)).ToList();
            if (mine.Count == 0)
            {
                gone.Add(id);
                continue;
            }

            if (mine.FirstOrDefault(f => f.Problem != CheckpointProblem.None) is { } bad)
            {
                stopped = bad;
                break;
            }

            checkpoints.AddRange(mine);
        }

        return Work(checkpoints, gone, stopped, cancellationToken);
    }

    private static RestorePlan Work(IReadOnlyList<CheckpointFound> checkpoints, IReadOnlyList<string> gone, CheckpointFound? stopped, CancellationToken cancellationToken)
    {
        var comparer = new PathKeyComparer();
        var facts = new PlanFacts(comparer);
        var paths = new List<RestorePath>();
        var seen = new HashSet<RestorePath>(comparer);
        var expected = new Dictionary<RestorePath, CheckpointState>(comparer);   // the newest checkpoint's after-state
        var target = new Dictionary<RestorePath, CheckpointState?>(comparer);    // the oldest entry's before-state (null = unknown)
        var newest = new Dictionary<RestorePath, CheckpointEntry>(comparer);     // the first entry the replay meets
        var caseOnly = new Dictionary<RestorePath, string>(comparer);            // a case-only move's old spelling
        foreach (var checkpoint in checkpoints)
        {
            var record = checkpoint.Record!;
            foreach (var state in record.After)
            {
                expected.TryAdd(new RestorePath(checkpoint.Root, state.Path), state);
            }

            // Newest first through the list, so each later (older) entry's before-state wins.
            foreach (var entry in Enumerable.Reverse(record.Entries))
            {
                foreach (var (path, before) in BeforeStates(entry))
                {
                    var where = new RestorePath(checkpoint.Root, path);
                    target[where] = before;
                    facts.Oldest[where] = entry;
                    newest.TryAdd(where, entry);
                    if (seen.Add(where))
                    {
                        paths.Add(where);
                    }

                    if ((entry.Directory && entry.Op != CheckpointOp.Link) || entry.Op == CheckpointOp.Folder)
                    {
                        facts.Folders.Add(where);
                    }
                }

                if (entry.Op == CheckpointOp.Moved && entry.To is { } to)
                {
                    facts.MoveEnds.Add(new RestorePath(checkpoint.Root, to));
                    if (PlanFacts.IsCaseOnly(entry))
                    {
                        caseOnly[new RestorePath(checkpoint.Root, entry.Path)] = entry.Path;
                    }
                }
            }
        }

        // The made folders that hold another made path (review P7-04): named by what's in them.
        foreach (var where in paths)
        {
            if (facts.Oldest.GetValueOrDefault(where) is { Op: CheckpointOp.Absent } && Parent(where) is { } parent
                && facts.Oldest.GetValueOrDefault(parent) is { Op: CheckpointOp.Absent, Directory: true })
            {
                facts.Holders.Add(parent);
            }
        }

        var conflicts = new List<RestoreConflict>();
        var back = new HashSet<RestorePath>(comparer);
        var roots = new Dictionary<string, bool>(WorkingDirectory.PathComparer);
        long hashed = 0;   // one budget for the whole plan (review P1-12)
        foreach (var where in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // A gone folder first (review P9-03): its paths all read Absent, which "already back" took for put back and spent.
            if (!roots.TryGetValue(where.Root, out bool there))
            {
                there = Directory.Exists(where.Root);
                roots[where.Root] = there;
            }

            if (!there)
            {
                continue;   // reported failed at the run
            }

            var now = FileJournal.StateOf(where.Root, where.Path, ref hashed);
            var want = target.GetValueOrDefault(where);
            // A case-only rename is back only under its old spelling (review P8-01): case-blind, the new name matched the old.
            bool spelledBack = !caseOnly.TryGetValue(where, out string? old)
                || string.Equals(FileJournal.RelativeOf(where.Root, where.Full), old, StringComparison.Ordinal);
            if (want is not null && now.Matches(want) && spelledBack)
            {
                back.Add(where);
                continue;
            }

            bool moved = newest.GetValueOrDefault(where) is { Op: CheckpointOp.Moved, Directory: false } move
                && move.To is { } to && comparer.Equals(new RestorePath(where.Root, to), where) && !PlanFacts.IsCaseOnly(move);
            if (expected.TryGetValue(where, out var after) && now.Matches(after))
            {
                // A moved file the turn changed after the move, unjournaled (review P8-02): its old bytes were never saved, so it can't go back.
                if (moved && newest[where].Hash is { } hash && now.Kind == CheckpointKind.File
                    && (now.Hash is { } current ? !string.Equals(current, hash, StringComparison.OrdinalIgnoreCase) : now.Size != newest[where].Size))
                {
                    conflicts.Add(new RestoreConflict(where, KindChanged: true));
                }

                continue;
            }

            bool kindChanged = moved
                || (now.Kind == CheckpointKind.Folder && want?.Kind is CheckpointKind.File or CheckpointKind.Link)
                || (now.Kind is CheckpointKind.File or CheckpointKind.Link && want?.Kind == CheckpointKind.Folder);
            conflicts.Add(new RestoreConflict(where, kindChanged));
        }

        return new RestorePlan(checkpoints, gone, stopped, paths, conflicts, back, facts);
    }

    /// <summary>The path's parent under the same root, or null at the root.</summary>
    private static RestorePath? Parent(RestorePath where)
    {
        int slash = where.Path.LastIndexOf('/');
        return slash > 0 ? new RestorePath(where.Root, where.Path[..slash]) : null;
    }

    /// <summary>What each path an entry names held before it ran (null when unknown).</summary>
    private static IEnumerable<(string Path, CheckpointState? Before)> BeforeStates(CheckpointEntry entry)
    {
        switch (entry.Op)
        {
            case CheckpointOp.Content:
                yield return (entry.Path, entry.Hash is null ? null : new CheckpointState { Path = entry.Path, Kind = CheckpointKind.File, Size = entry.Size, Hash = entry.Hash });
                break;
            case CheckpointOp.Absent:
                yield return (entry.Path, new CheckpointState { Path = entry.Path, Kind = CheckpointKind.Absent });
                break;
            case CheckpointOp.Folder:
                yield return (entry.Path, new CheckpointState { Path = entry.Path, Kind = CheckpointKind.Folder });
                break;
            case CheckpointOp.Link:
                yield return (entry.Path, new CheckpointState { Path = entry.Path, Kind = CheckpointKind.Link, Target = entry.Target });
                break;
            case CheckpointOp.Moved:
                yield return (entry.To!, new CheckpointState { Path = entry.To!, Kind = CheckpointKind.Absent });
                yield return (entry.Path, entry.Directory
                    ? new CheckpointState { Path = entry.Path, Kind = CheckpointKind.Folder }
                    : entry.Hash is null ? null : new CheckpointState { Path = entry.Path, Kind = CheckpointKind.File, Size = entry.Size, Hash = entry.Hash });
                break;
        }
    }

    /// <summary>
    /// Replays <paramref name="plan"/>: each checkpoint newest first, its entries backwards, through <paramref name="directoryFor"/>'s pinned
    /// working directory for its root. The paths are judged again first (2026-10-28, review P4-02: an edit saved while a question was open was
    /// written over): a path that became a conflict since is skipped whatever the answer, one no longer back is replayed. A conflict is skipped
    /// unless <paramref name="restoreOver"/>, which sends the current file to <paramref name="bin"/> (null: written over) only once its saved
    /// bytes are read (review P1-04); a kind change is always skipped; a path already back is left alone. The project file is allowed for the
    /// replay's writes (the user ordered the restore; <see cref="WriteGuard.IsProjectFile"/> holds it to the live working folder). Pictures
    /// deleted or replaced are named for the strip to forget.
    /// </summary>
    public static RestoreReport Run(
        RestorePlan plan,
        ICheckpointStore store,
        Func<string, WorkingDirectory> directoryFor,
        bool restoreOver,
        IRecycleBin? bin,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(directoryFor);
        RestorePlan now;
        try
        {
            now = Work(plan.Checkpoints, plan.NoLongerKept, plan.StoppedAt, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return new RestoreReport([], Stopped: true, []);
        }

        var comparer = new PathKeyComparer();
        var facts = now.Facts;
        var asked = new HashSet<RestorePath>(plan.Conflicts.Where(c => !c.KindChanged).Select(c => c.Where), comparer);
        var skip = new HashSet<RestorePath>(now.AlreadyBack, comparer);
        var outcome = new Dictionary<RestorePath, Result>(comparer);
        var toBin = new HashSet<RestorePath>(comparer);
        foreach (var conflict in now.Conflicts)
        {
            if (conflict.KindChanged || !restoreOver || !asked.Contains(conflict.Where))
            {
                skip.Add(conflict.Where);
                outcome[conflict.Where] = new Result(RestoreOutcome.Skipped);
            }
            else
            {
                toBin.Add(conflict.Where);
            }
        }

        foreach (var where in now.AlreadyBack)
        {
            outcome[where] = new Result(RestoreOutcome.AlreadyBack);
        }

        // A path whose oldest entry kept no bytes is left as it is, every entry of it (review P9-04: a newer entry was replayed, then reported
        // "not kept").
        foreach (var (where, entry) in facts.Oldest)
        {
            if (entry is { Op: CheckpointOp.Content, Blob: null } && !skip.Contains(where))
            {
                skip.Add(where);
                outcome[where] = new Result(RestoreOutcome.NotSaved);
            }
        }

        var directories = new Dictionary<string, WorkingDirectory?>(WorkingDirectory.PathComparer);
        WorkingDirectory? Dir(string root)
        {
            if (!directories.TryGetValue(root, out var directory))
            {
                directory = Directory.Exists(root) ? directoryFor(root) : null;
                directories[root] = directory;
            }

            return directory;
        }

        // The texts before, for the diffs (review P7-07): the first files with text, or a deleted file coming back, never a folder.
        var before = new Dictionary<RestorePath, string?>(comparer);
        foreach (var where in now.Paths.Where(p => !skip.Contains(p)))
        {
            if (before.Count >= ListedFiles)
            {
                break;
            }

            if (TextOf(where.Full) is { } text)
            {
                before[where] = text;
            }
            else if (!File.Exists(where.Full) && !Directory.Exists(where.Full) && facts.Oldest.GetValueOrDefault(where) is { Op: CheckpointOp.Content, Blob: not null })
            {
                before[where] = null;
            }
        }

        var meta = new Dictionary<RestorePath, CheckpointEntry>(comparer);
        var pictures = new List<string>();
        bool stopped = false;
        using (WriteGuard.AllowProjectFile())
        {
            foreach (var checkpoint in now.Checkpoints)
            {
                var record = checkpoint.Record!;
                var directory = Dir(checkpoint.Root);
                foreach (var entry in Enumerable.Reverse(record.Entries))
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        stopped = true;
                        break;
                    }

                    var where = new RestorePath(checkpoint.Root, entry.Path);
                    var to = entry.To is { } t ? new RestorePath(checkpoint.Root, t) : null;
                    if (skip.Contains(where) || (to is not null && skip.Contains(to)))
                    {
                        if (to is not null)
                        {
                            // Both ends of a move skipped together (review P2-01: the source was reported back when only the end was skipped).
                            outcome.TryAdd(where, new Result(RestoreOutcome.Skipped));
                            outcome.TryAdd(to, new Result(RestoreOutcome.Skipped));
                        }

                        continue;
                    }

                    if (outcome.TryGetValue(where, out var was) && was.Outcome == RestoreOutcome.Failed)
                    {
                        continue;   // a path that failed once is left as it stands
                    }

                    // A path the turns made: its contents in between never come back (review P6-07: each went to the bin as a phantom copy).
                    if (entry.Op is CheckpointOp.Content or CheckpointOp.Link or CheckpointOp.Folder
                        && facts.Oldest.GetValueOrDefault(where) is { Op: CheckpointOp.Absent } && !facts.MoveEnds.Contains(where))
                    {
                        continue;
                    }

                    if (directory is null)
                    {
                        Worst(outcome, where, new Result(RestoreOutcome.Failed, FileText.RestoreRootGone(checkpoint.Root), Retry: true));
                        continue;
                    }

                    var result = Apply(directory, checkpoint, store, entry, bin, toBin.Remove(where), pictures);
                    Worst(outcome, where, result);
                    if (to is not null)
                    {
                        Worst(outcome, to, result);
                    }

                    if (entry.Op == CheckpointOp.Content && result.Outcome == RestoreOutcome.Restored)
                    {
                        meta[where] = entry;
                    }
                    else if (entry.Op == CheckpointOp.Moved && to is not null)
                    {
                        // The moved file's saved state goes with it (review P12-04: a rename then an overwrite lost its read-only flag).
                        if (result.Outcome == RestoreOutcome.Restored && meta.Remove(to, out var carried))
                        {
                            meta[where] = carried;
                        }
                        else
                        {
                            meta.Remove(where);
                        }
                    }
                    else if (entry.Op == CheckpointOp.Absent)
                    {
                        meta.Remove(where);
                    }
                }

                if (stopped)
                {
                    break;
                }
            }
        }

        // Attributes, mode and mark once per path, from the oldest state restored (read-only last, so it never blocks a later step).
        foreach (var (where, entry) in meta)
        {
            PutBackMeta(where.Full, entry);
        }

        // A failure that may pass keeps the checkpoints (review P1-09): closed in Word, back online, /undo again finishes; what came back reads as back.
        bool retry = outcome.Values.Any(r => r.Outcome == RestoreOutcome.Failed && r.Retry);
        var spent = !stopped && !retry ? now.Ids : [];

        var files = new List<RestoredFile>();
        foreach (var where in now.Paths)
        {
            if (!outcome.TryGetValue(where, out var result))
            {
                if (stopped)
                {
                    continue;
                }

                result = new Result(RestoreOutcome.Restored);
            }

            // Listed paths only (review P7-04), and any other whose outcome the user must see.
            if (!facts.IsListed(where) && result.Outcome is RestoreOutcome.Restored or RestoreOutcome.AlreadyBack)
            {
                continue;
            }

            FileDiff? diff = null;
            if (result.Outcome == RestoreOutcome.Restored && before.TryGetValue(where, out string? old) && TextOf(where.Full) is { } text)
            {
                diff = FileDiff.Of(where.Path, old, text);
            }

            files.Add(new RestoredFile(facts.Shown(where), result.Outcome, result.Detail, diff));
        }

        return new RestoreReport(files, stopped, pictures) { Spent = spent, Kept = retry && !stopped };
    }

    /// <summary>One path's result; <paramref name="Retry"/> for a failure that may pass (review P1-09).</summary>
    private readonly record struct Result(RestoreOutcome Outcome, string Detail = "", bool Retry = false);

    /// <summary>A path's outcome kept at its worst (review P9-04: the last entry's won, an older "not kept" over a newer restore).</summary>
    private static void Worst(Dictionary<RestorePath, Result> outcome, RestorePath where, Result result)
    {
        if (!outcome.TryGetValue(where, out var was) || Rank(result.Outcome) >= Rank(was.Outcome))
        {
            outcome[where] = result;
        }
    }

    private static int Rank(RestoreOutcome outcome) => outcome switch
    {
        RestoreOutcome.AlreadyBack => 0,
        RestoreOutcome.Restored => 1,
        RestoreOutcome.NotSaved => 2,
        RestoreOutcome.Skipped => 3,
        _ => 4,
    };

    private static Result Apply(WorkingDirectory directory, CheckpointFound checkpoint, ICheckpointStore store, CheckpointEntry entry, IRecycleBin? bin, bool binFirst, List<string> pictures)
    {
        try
        {
            switch (entry.Op)
            {
                case CheckpointOp.Content:
                {
                    if (entry.Blob is not { } name)
                    {
                        return new Result(RestoreOutcome.NotSaved);
                    }

                    var resolved = directory.ResolveForRestore(entry.Path, linkItself: false, out string full);
                    if (resolved != FileOutcome.Ok)
                    {
                        return new Result(RestoreOutcome.Failed, FileText.RestoreRefused(resolved, entry.Path));
                    }

                    if (Directory.Exists(full))
                    {
                        return new Result(RestoreOutcome.Skipped);
                    }

                    using var content = store.OpenBlob(checkpoint, name);
                    if (content is null)
                    {
                        // Yours stays where it is (review P1-04): nothing went to the bin before the bytes were in hand.
                        return new Result(RestoreOutcome.Failed, FileText.RestoreBlobMissing);
                    }

                    bool existed = File.Exists(full);
                    if (binFirst && existed && directory.ResolveForRestore(entry.Path, linkItself: true, out string own) == FileOutcome.Ok)
                    {
                        // Restore over: your version to the bin now, right before the write.
                        FileDeletes.Remove(own, isDirectory: false, isLink: WorkingDirectory.RealLinkTarget(own) is not null, bin);
                    }

                    if (!directory.WriteRestored(full, content))
                    {
                        return new Result(RestoreOutcome.Failed, FileText.RestoreAppeared, Retry: true);
                    }

                    if (existed)
                    {
                        pictures.Add(full);
                    }

                    return new Result(RestoreOutcome.Restored);
                }

                case CheckpointOp.Absent:
                {
                    var resolved = directory.ResolveForRestore(entry.Path, linkItself: true, out string full);
                    if (resolved != FileOutcome.Ok)
                    {
                        return new Result(RestoreOutcome.Failed, FileText.RestoreRefused(resolved, entry.Path));
                    }

                    if (Directory.Exists(full) && WorkingDirectory.RealLinkTarget(full) is null)
                    {
                        if (Directory.EnumerateFileSystemEntries(full).Any())
                        {
                            // Something else is in it now (review P1-14): left, and said so.
                            return new Result(RestoreOutcome.Skipped);
                        }

                        Directory.Delete(full);
                        return new Result(RestoreOutcome.Restored);
                    }

                    if (File.Exists(full) || WorkingDirectory.RealLinkTarget(full) is not null)
                    {
                        FileDeletes.Remove(full, isDirectory: false, isLink: WorkingDirectory.RealLinkTarget(full) is not null, bin);
                        pictures.Add(full);
                    }

                    return new Result(RestoreOutcome.Restored);
                }

                case CheckpointOp.Moved:
                {
                    var from = directory.ResolveForRestore(entry.Path, linkItself: true, out string original);
                    var to = directory.ResolveForRestore(entry.To!, linkItself: true, out string moved);
                    if (from != FileOutcome.Ok || to != FileOutcome.Ok)
                    {
                        return new Result(RestoreOutcome.Failed, FileText.RestoreRefused(from != FileOutcome.Ok ? from : to, entry.Path));
                    }

                    // A case-only rename only where case is blind (review P3-09, the journal's and Transfer's rule): on a case-sensitive folder
                    // the old spelling is another file, which the guard below keeps.
                    string folder = Path.GetDirectoryName(original)!;
                    bool caseOnly = string.Equals(original, moved, StringComparison.OrdinalIgnoreCase) && !CaseRule.IsSensitive(folder);
                    if (!File.Exists(moved) && !Directory.Exists(moved))
                    {
                        return new Result(RestoreOutcome.Failed, FileText.RestoreMovedGone(entry.To!));
                    }

                    if (!caseOnly && (File.Exists(original) || Directory.Exists(original)))
                    {
                        return new Result(RestoreOutcome.Skipped);
                    }

                    // A moved file changed since (review P8-02): its old bytes were never saved, so it isn't moved back as if it were the same.
                    if (!entry.Directory && entry.Hash is { } hash && File.Exists(moved) && WorkingDirectory.RealLinkTarget(moved) is null
                        && !string.Equals(FileJournal.HashOf(moved), hash, StringComparison.OrdinalIgnoreCase))
                    {
                        return new Result(RestoreOutcome.Skipped);
                    }

                    Directory.CreateDirectory(folder);
                    if (Directory.Exists(moved))
                    {
                        // A folder, or a junction or folder link moved as itself (review P3-08: File.Move refused a folder link).
                        Directory.Move(moved, original);
                    }
                    else
                    {
                        File.Move(moved, original, overwrite: caseOnly);
                    }

                    pictures.Add(moved);
                    return new Result(RestoreOutcome.Restored);
                }

                case CheckpointOp.Folder:
                {
                    var resolved = directory.ResolveForRestore(entry.Path, linkItself: false, out string full);
                    if (resolved != FileOutcome.Ok)
                    {
                        return new Result(RestoreOutcome.Failed, FileText.RestoreRefused(resolved, entry.Path));
                    }

                    if (File.Exists(full))
                    {
                        return new Result(RestoreOutcome.Skipped);
                    }

                    Directory.CreateDirectory(full);
                    return new Result(RestoreOutcome.Restored);
                }

                case CheckpointOp.Link:
                {
                    var resolved = directory.ResolveForRestore(entry.Path, linkItself: true, out string full);
                    if (resolved != FileOutcome.Ok)
                    {
                        return new Result(RestoreOutcome.Failed, FileText.RestoreRefused(resolved, entry.Path));
                    }

                    string? standing = WorkingDirectory.RealLinkTarget(full) is not null ? LinkTargetOf(full) : null;
                    if (standing is not null && string.Equals(standing, entry.Target ?? "", StringComparison.Ordinal))
                    {
                        return new Result(RestoreOutcome.AlreadyBack);
                    }

                    if (standing is null && Directory.Exists(full))
                    {
                        return new Result(RestoreOutcome.Skipped);
                    }

                    if (standing is not null || File.Exists(full))
                    {
                        // A file written through the link replaced it (review P3-03): that file (or a link elsewhere) goes, the link comes back.
                        FileDeletes.Remove(full, isDirectory: false, isLink: standing is not null, bin);
                        pictures.Add(full);
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                    try
                    {
                        if (entry.Directory)
                        {
                            Directory.CreateSymbolicLink(full, entry.Target ?? "");
                        }
                        else
                        {
                            File.CreateSymbolicLink(full, entry.Target ?? "");
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && (ex.HResult & 0xFFFF) == PrivilegeNotHeld)
                    {
                        // The user's pick (review P9-02): a clear failure that spends, no junction code.
                        return new Result(RestoreOutcome.Failed, FileText.RestoreLinkNeedsPrivilege);
                    }

                    return new Result(RestoreOutcome.Restored);
                }
            }
        }
        catch (Exception ex) when (WorkingDirectory.IsFileFailure(ex))
        {
            DiagnosticLog.Warn(FileJournal.Category, $"Could not put back {entry.Path}: {ex.Message}");
            return new Result(RestoreOutcome.Failed, ex.Message, Retry: true);
        }

        return new Result(RestoreOutcome.Failed);
    }

    /// <summary>Windows' <c>ERROR_PRIVILEGE_NOT_HELD</c>: making a link without the right to.</summary>
    private const int PrivilegeNotHeld = 1314;

    private static string? LinkTargetOf(string full)
    {
        FileSystemInfo info = Directory.Exists(full) ? new DirectoryInfo(full) : new FileInfo(full);
        return info.LinkTarget;
    }

    /// <summary>
    /// The saved attributes (read-only last), Unix mode and internet mark put back: the mark put on, or taken off when there was none. On
    /// Windows the four saved flags are set as they were, taken off too when they weren't (2026-10-28, review P8-06: a file that gained Hidden
    /// kept it).
    /// </summary>
    private static void PutBackMeta(string full, CheckpointEntry entry)
    {
        try
        {
            if (!File.Exists(full))
            {
                return;
            }

            if (entry.Mark is { } mark)
            {
                MarkOfTheWeb.Put(Convert.FromBase64String(mark), full);
            }
            else
            {
                MarkOfTheWeb.Remove(full);
            }

            if (!OperatingSystem.IsWindows() && entry.Mode is { } mode)
            {
                File.SetUnixFileMode(full, (UnixFileMode)mode);
            }

            const FileAttributes Saved = FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive;
            if (OperatingSystem.IsWindows())
            {
                var attributes = (File.GetAttributes(full) & ~Saved) | ((FileAttributes)entry.Attributes & Saved);
                File.SetAttributes(full, attributes == 0 ? FileAttributes.Normal : attributes);
            }
        }
        catch (Exception ex) when (WorkingDirectory.IsFileFailure(ex) || ex is FormatException)
        {
            DiagnosticLog.Debug(FileJournal.Category, $"Could not put back {full}'s attributes: {ex.Message}");
        }
    }

    /// <summary>A file's text for a diff: null for none, a folder, one too big or not text.</summary>
    private static string? TextOf(string full)
    {
        try
        {
            if (!File.Exists(full) || new FileInfo(full).Length > WorkingDirectory.MaxTextFileBytes)
            {
                return null;
            }

            byte[] bytes = RegularFiles.ReadAllBytes(full);
            return WorkingDirectory.LooksBinary(bytes) ? null : TextFile.Decode(bytes, out _);
        }
        catch (Exception ex) when (WorkingDirectory.IsFileFailure(ex))
        {
            return null;
        }
    }

    /// <summary>A short count for a log line.</summary>
    public static string Summary(RestoreReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return string.Create(CultureInfo.InvariantCulture, $"{report.Count(RestoreOutcome.Restored)} restored, {report.Count(RestoreOutcome.Skipped)} skipped, {report.Count(RestoreOutcome.NotSaved)} not saved, {report.Count(RestoreOutcome.Failed)} failed");
    }

    /// <summary>
    /// Paths compared by root and relative path, the file system's way: the relative path by its root's <see cref="CaseRule"/> (2026-10-29, the
    /// Mac pass: <see cref="WorkingDirectory.PathComparer"/> is ordinal off Windows, so on a Mac's case-blind volume a case-only rename's two
    /// spellings were two paths and one file was reported put back twice).
    /// </summary>
    private sealed class PathKeyComparer : IEqualityComparer<RestorePath>
    {
        public bool Equals(RestorePath? x, RestorePath? y) =>
            x is not null && y is not null && WorkingDirectory.PathComparer.Equals(x.Root, y.Root) && CaseRule.Comparer(x.Root).Equals(x.Path, y.Path);

        public int GetHashCode(RestorePath obj) =>
            HashCode.Combine(WorkingDirectory.PathComparer.GetHashCode(obj.Root), CaseRule.Comparer(obj.Root).GetHashCode(obj.Path));
    }
}
