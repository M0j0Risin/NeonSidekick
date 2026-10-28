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

/// <summary>One path a restore touches, under its checkpoint's root.</summary>
public sealed record RestorePath(string Root, string Path)
{
    /// <summary>The full path.</summary>
    public string Full => FileJournal.FullOf(Root, Path);
}

/// <summary>A path changed since the turn (the user's edit, a shell command): restoring it means writing over that change.</summary>
/// <param name="KindChanged">A folder now where a file was, or a file where a folder was: always skipped, even under "restore over".</param>
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
        IReadOnlySet<RestorePath> alreadyBack)
    {
        Checkpoints = checkpoints;
        NoLongerKept = noLongerKept;
        StoppedAt = stoppedAt;
        Paths = paths;
        Conflicts = conflicts;
        AlreadyBack = alreadyBack;
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

/// <summary>What a restore did.</summary>
public sealed record RestoreReport(IReadOnlyList<RestoredFile> Files, bool Stopped, IReadOnlyList<string> Pictures)
{
    /// <summary>The checkpoint ids put back whole (spent): every one when the restore wasn't stopped.</summary>
    public IReadOnlyList<string> Spent { get; init; } = [];

    public int Count(RestoreOutcome outcome) => Files.Count(f => f.Outcome == outcome);
}

/// <summary>
/// Puts files back from checkpoints (2026-10-28, the checkpoints plan): each checkpoint's op log replayed backwards, newest first, through a
/// <see cref="WorkingDirectory"/> pinned at the checkpoint's root (<see cref="WorkingDirectory.PinnedAt"/>, so <see cref="WriteGuard"/> judges
/// every path as the turn's tools were), never journaled. Conflicts are found up front (<see cref="Plan"/>); <see cref="Run"/> then sends a
/// conflicting file to the bin before restoring over it, or skips it. Attributes, the Unix mode and the internet mark are set once per path at
/// the end, from the oldest state restored. The token is read between files, never inside one; a stopped restore keeps its checkpoints.
/// </summary>
public static class FileRestore
{
    /// <summary>The files a pane, a card or a question lists before "and N more".</summary>
    public const int ListedFiles = 20;

    /// <summary>
    /// Works out a restore of <paramref name="idsNewestFirst"/> (the turns' stamps): each id's checkpoints in every store, conflicts against
    /// each path's after-state in the newest checkpoint touching it, and the paths already back.
    /// </summary>
    public static RestorePlan Plan(ICheckpointStore store, string profileDirectory, IReadOnlyList<string> idsNewestFirst)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(profileDirectory);
        ArgumentNullException.ThrowIfNull(idsNewestFirst);
        var found = store.Find(profileDirectory, idsNewestFirst.ToList());
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

        return Work(checkpoints, gone, stopped);
    }

    private static RestorePlan Work(List<CheckpointFound> checkpoints, List<string> gone, CheckpointFound? stopped)
    {
        var comparer = new PathKeyComparer();
        var paths = new List<RestorePath>();
        var seen = new HashSet<RestorePath>(comparer);
        var expected = new Dictionary<RestorePath, CheckpointState>(comparer);   // the newest checkpoint's after-state
        var target = new Dictionary<RestorePath, CheckpointState?>(comparer);    // the oldest entry's before-state (null = unknown)
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
                    if (seen.Add(where))
                    {
                        paths.Add(where);
                    }
                }
            }
        }

        var conflicts = new List<RestoreConflict>();
        var back = new HashSet<RestorePath>(comparer);
        foreach (var where in paths)
        {
            long hashed = 0;
            var now = FileJournal.StateOf(where.Root, where.Path, ref hashed);
            var want = target.GetValueOrDefault(where);
            if (want is not null && now.Matches(want))
            {
                back.Add(where);
                continue;
            }

            if (!Directory.Exists(where.Root))
            {
                continue;   // reported failed at the run
            }

            if (expected.TryGetValue(where, out var after) && now.Matches(after))
            {
                continue;
            }

            bool kindChanged = (now.Kind == CheckpointKind.Folder && want?.Kind is CheckpointKind.File or CheckpointKind.Link)
                || (now.Kind is CheckpointKind.File or CheckpointKind.Link && want?.Kind == CheckpointKind.Folder);
            conflicts.Add(new RestoreConflict(where, kindChanged));
        }

        return new RestorePlan(checkpoints, gone, stopped, paths, conflicts, back);
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
    /// working directory for its root. A conflict is skipped unless <paramref name="restoreOver"/>, which first sends the current file to
    /// <paramref name="bin"/> (null: written over); a kind change is always skipped; a path already back is left alone. The project file is
    /// allowed for its own write (the user ordered the restore). Pictures deleted or replaced are named for the strip to forget.
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
        var comparer = new PathKeyComparer();
        var skip = new HashSet<RestorePath>(plan.AlreadyBack, comparer);
        var outcome = new Dictionary<RestorePath, (RestoreOutcome Outcome, string Detail)>(comparer);
        foreach (var conflict in plan.Conflicts)
        {
            if (conflict.KindChanged || !restoreOver)
            {
                skip.Add(conflict.Where);
                outcome[conflict.Where] = (RestoreOutcome.Skipped, "");
            }
        }

        foreach (var where in plan.AlreadyBack)
        {
            outcome[where] = (RestoreOutcome.AlreadyBack, "");
        }

        var directories = new Dictionary<string, WorkingDirectory>(WorkingDirectory.PathComparer);
        WorkingDirectory? Dir(string root)
        {
            if (!Directory.Exists(root))
            {
                return null;
            }

            if (!directories.TryGetValue(root, out var directory))
            {
                directory = directoryFor(root);
                directories[root] = directory;
            }

            return directory;
        }

        // The texts before, for the diffs (the first files only).
        var before = new Dictionary<RestorePath, string?>(comparer);
        foreach (var where in plan.Paths.Where(p => !skip.Contains(p)).Take(ListedFiles))
        {
            before[where] = TextOf(where.Full);
        }

        // Restore over: the current file to the bin first, so a restore never destroys the user's edit.
        if (restoreOver)
        {
            foreach (var conflict in plan.Conflicts.Where(c => !c.KindChanged))
            {
                string full = conflict.Where.Full;
                if (File.Exists(full) && !MovedTo(plan, conflict.Where) && Dir(conflict.Where.Root) is { } directory
                    && directory.ResolveForRestore(conflict.Where.Path, linkItself: true, out string resolved) == FileOutcome.Ok)
                {
                    try
                    {
                        FileDeletes.Remove(resolved, isDirectory: false, isLink: WorkingDirectory.RealLinkTarget(resolved) is not null, bin);
                    }
                    catch (Exception ex) when (WorkingDirectory.IsFileFailure(ex))
                    {
                        skip.Add(conflict.Where);
                        outcome[conflict.Where] = (RestoreOutcome.Failed, ex.Message);
                    }
                }
            }
        }

        var meta = new Dictionary<RestorePath, CheckpointEntry>(comparer);
        var pictures = new List<string>();
        var spent = new List<string>();
        bool stopped = false;
        foreach (var checkpoint in plan.Checkpoints)
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
                    continue;
                }

                if (outcome.TryGetValue(where, out var was) && was.Outcome == RestoreOutcome.Failed)
                {
                    continue;   // a path that failed once is left as it stands
                }

                if (directory is null)
                {
                    outcome[where] = (RestoreOutcome.Failed, FileText.RestoreRootGone(checkpoint.Root));
                    continue;
                }

                var result = Apply(directory, checkpoint, store, entry, bin, pictures);
                outcome[where] = result;
                if (to is not null)
                {
                    outcome[to] = result;
                }

                if (entry.Op == CheckpointOp.Content && result.Outcome == RestoreOutcome.Restored)
                {
                    meta[where] = entry;
                }
                else if (entry.Op is CheckpointOp.Absent or CheckpointOp.Moved)
                {
                    meta.Remove(where);
                }
            }

            if (stopped)
            {
                break;
            }
        }

        // Attributes, mode and mark once per path, from the oldest state restored (read-only last, so it never blocks a later step).
        foreach (var (where, entry) in meta)
        {
            PutBackMeta(where.Full, entry);
        }

        if (!stopped)
        {
            spent.AddRange(plan.Ids);
        }

        var files = new List<RestoredFile>();
        foreach (var where in plan.Paths)
        {
            if (!outcome.TryGetValue(where, out var result))
            {
                if (stopped)
                {
                    continue;
                }

                result = (RestoreOutcome.Restored, "");
            }

            FileDiff? diff = null;
            if (result.Outcome == RestoreOutcome.Restored && before.TryGetValue(where, out string? old) && TextOf(where.Full) is { } now)
            {
                diff = FileDiff.Of(where.Path, old, now);
            }

            files.Add(new RestoredFile(where, result.Outcome, result.Detail, diff));
        }

        return new RestoreReport(files, stopped, pictures) { Spent = spent };
    }

    /// <summary>Whether <paramref name="where"/> is a move's destination in the plan (moved back, never sent to the bin).</summary>
    private static bool MovedTo(RestorePlan plan, RestorePath where) =>
        plan.Checkpoints.Any(c => WorkingDirectory.PathComparer.Equals(c.Root, where.Root)
            && c.Record!.Entries.Any(e => e.Op == CheckpointOp.Moved && WorkingDirectory.PathComparer.Equals(e.To, where.Path)));

    private static (RestoreOutcome Outcome, string Detail) Apply(WorkingDirectory directory, CheckpointFound checkpoint, ICheckpointStore store, CheckpointEntry entry, IRecycleBin? bin, List<string> pictures)
    {
        bool project = IsProjectFile(directory, entry.Path) || (entry.To is { } t && IsProjectFile(directory, t));
        using var allowed = project ? WriteGuard.AllowProjectFile() : null;
        try
        {
            switch (entry.Op)
            {
                case CheckpointOp.Content:
                {
                    if (entry.Blob is not { } name)
                    {
                        return (RestoreOutcome.NotSaved, "");
                    }

                    var resolved = directory.ResolveForRestore(entry.Path, linkItself: false, out string full);
                    if (resolved != FileOutcome.Ok)
                    {
                        return (RestoreOutcome.Failed, FileText.RestoreRefused(resolved, entry.Path));
                    }

                    if (Directory.Exists(full))
                    {
                        return (RestoreOutcome.Skipped, "");
                    }

                    using var content = store.OpenBlob(checkpoint, name);
                    if (content is null)
                    {
                        return (RestoreOutcome.Failed, FileText.RestoreBlobMissing);
                    }

                    bool existed = File.Exists(full);
                    if (!directory.WriteRestored(full, content))
                    {
                        return (RestoreOutcome.Failed, FileText.RestoreAppeared);
                    }

                    if (existed)
                    {
                        pictures.Add(full);
                    }

                    return (RestoreOutcome.Restored, "");
                }

                case CheckpointOp.Absent:
                {
                    var resolved = directory.ResolveForRestore(entry.Path, linkItself: true, out string full);
                    if (resolved != FileOutcome.Ok)
                    {
                        return (RestoreOutcome.Failed, FileText.RestoreRefused(resolved, entry.Path));
                    }

                    if (Directory.Exists(full) && WorkingDirectory.RealLinkTarget(full) is null)
                    {
                        if (!Directory.EnumerateFileSystemEntries(full).Any())
                        {
                            Directory.Delete(full);
                        }

                        return (RestoreOutcome.Restored, "");
                    }

                    if (File.Exists(full) || WorkingDirectory.RealLinkTarget(full) is not null)
                    {
                        FileDeletes.Remove(full, isDirectory: false, isLink: WorkingDirectory.RealLinkTarget(full) is not null, bin);
                        pictures.Add(full);
                    }

                    return (RestoreOutcome.Restored, "");
                }

                case CheckpointOp.Moved:
                {
                    var from = directory.ResolveForRestore(entry.Path, linkItself: true, out string original);
                    var to = directory.ResolveForRestore(entry.To!, linkItself: true, out string moved);
                    if (from != FileOutcome.Ok || to != FileOutcome.Ok)
                    {
                        return (RestoreOutcome.Failed, FileText.RestoreRefused(from != FileOutcome.Ok ? from : to, entry.Path));
                    }

                    bool caseOnly = string.Equals(original, moved, StringComparison.OrdinalIgnoreCase);
                    if (!File.Exists(moved) && !Directory.Exists(moved))
                    {
                        return (RestoreOutcome.Failed, FileText.RestoreMovedGone(entry.To!));
                    }

                    if (!caseOnly && (File.Exists(original) || Directory.Exists(original)))
                    {
                        return (RestoreOutcome.Skipped, "");
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(original)!);
                    if (Directory.Exists(moved) && WorkingDirectory.RealLinkTarget(moved) is null)
                    {
                        Directory.Move(moved, original);
                    }
                    else
                    {
                        File.Move(moved, original, overwrite: caseOnly);
                    }

                    pictures.Add(moved);
                    return (RestoreOutcome.Restored, "");
                }

                case CheckpointOp.Folder:
                {
                    var resolved = directory.ResolveForRestore(entry.Path, linkItself: false, out string full);
                    if (resolved != FileOutcome.Ok)
                    {
                        return (RestoreOutcome.Failed, FileText.RestoreRefused(resolved, entry.Path));
                    }

                    if (File.Exists(full))
                    {
                        return (RestoreOutcome.Skipped, "");
                    }

                    Directory.CreateDirectory(full);
                    return (RestoreOutcome.Restored, "");
                }

                case CheckpointOp.Link:
                {
                    var resolved = directory.ResolveForRestore(entry.Path, linkItself: true, out string full);
                    if (resolved != FileOutcome.Ok)
                    {
                        return (RestoreOutcome.Failed, FileText.RestoreRefused(resolved, entry.Path));
                    }

                    if (File.Exists(full) || Directory.Exists(full))
                    {
                        return (RestoreOutcome.AlreadyBack, "");
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                    if (entry.Directory)
                    {
                        Directory.CreateSymbolicLink(full, entry.Target ?? "");
                    }
                    else
                    {
                        File.CreateSymbolicLink(full, entry.Target ?? "");
                    }

                    return (RestoreOutcome.Restored, "");
                }
            }
        }
        catch (Exception ex) when (WorkingDirectory.IsFileFailure(ex))
        {
            DiagnosticLog.Warn(FileJournal.Category, $"Could not put back {entry.Path}: {ex.Message}");
            return (RestoreOutcome.Failed, ex.Message);
        }

        return (RestoreOutcome.Failed, "");
    }

    private static bool IsProjectFile(WorkingDirectory directory, string relative) =>
        !relative.Contains('/', StringComparison.Ordinal)
        && Llm.ProjectFile.FileNames.Any(name => string.Equals(relative.TrimEnd('.', ' '), name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The saved attributes (read-only last), Unix mode and internet mark put back: the mark put on, or taken off when there was none.</summary>
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

            var attributes = (FileAttributes)entry.Attributes & (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive);
            if (OperatingSystem.IsWindows() && attributes != 0)
            {
                File.SetAttributes(full, (File.GetAttributes(full) & ~(FileAttributes.Hidden | FileAttributes.System)) | attributes);
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

    /// <summary>Paths compared by root and relative path, the file system's way.</summary>
    private sealed class PathKeyComparer : IEqualityComparer<RestorePath>
    {
        public bool Equals(RestorePath? x, RestorePath? y) =>
            x is not null && y is not null && WorkingDirectory.PathComparer.Equals(x.Root, y.Root) && WorkingDirectory.PathComparer.Equals(x.Path, y.Path);

        public int GetHashCode(RestorePath obj) =>
            HashCode.Combine(WorkingDirectory.PathComparer.GetHashCode(obj.Root), WorkingDirectory.PathComparer.GetHashCode(obj.Path));
    }
}
