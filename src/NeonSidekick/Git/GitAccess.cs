using System.Globalization;
using LibGit2Sharp;
using NeonSidekick.Diagnostics;
using NeonSidekick.Files;

namespace NeonSidekick.Git;

/// <summary>
/// The git tools' one door to a repository (2026-09-20): the working-directory sandbox applied to
/// git — a repository is looked for from a sandbox path upward (<see cref="FindGitEntry"/>: the app's own walk) and
/// used only when its working tree is the sandbox root or a folder inside it, so every path the model
/// passes or sees stays sandbox-relative — and the one place a <see cref="Repository"/> is opened: per
/// call, in a <c>using</c>, never cached or shared (libgit2's handles are not thread-safe; the reflection
/// job may call a tool while a turn runs). <c>Diff.Compare&lt;T&gt;</c> is named nowhere here: it kills the
/// process under NativeAOT (libgit2sharp#2082, the spike of 2026-09-20), so every diff is
/// <see cref="UnifiedDiff"/> over blob and file texts and every "files changed" list a walk of two trees.
/// The reads and writes are synchronous; the tools run them off the turn's thread. A model's mistake is
/// a <see cref="GitOutcome"/> the tool turns into an <c>Error:</c> sentence, never a Warning.
/// <para>
/// A second NativeAOT rule (the published exe, later on 2026-09-20): LibGit2Sharp's <c>GitBuf</c> — a
/// <c>[StructLayout]</c> <em>class</em> native code writes the answer into — comes back empty under
/// NativeAOT (the class is marshalled in, never out), so every API over it answers nothing:
/// <c>Repository.Discover</c> (null), <c>git_message_prettify</c> behind <c>CommitOptions.PrettifyMessage</c>,
/// <c>Branch.TrackedBranch</c> / <c>TrackingDetails</c> / <c>RemoteName</c> / <c>UpstreamBranchCanonicalName</c>.
/// So the repository is found by <see cref="FindGitEntry"/>, a commit is made with
/// <c>PrettifyMessage = false</c> over <see cref="Prettify"/>, and the upstream is read from the
/// repository's config (<see cref="Upstream"/>) with <c>ObjectDatabase.CalculateHistoryDivergence</c> for the
/// ahead / behind counts. The smoke probe exercises all three.
/// </para>
/// </summary>
public sealed class GitAccess
{
    /// <summary>The log category of every git line.</summary>
    public const string Category = "Git";

    /// <summary>The libgit2 dll the NativeBinaries package puts beside the exe (<c>git2-&lt;commit&gt;.dll</c>; the smoke checks it).</summary>
    public const string NativeLibraryFileName = "git2-5853918.dll";

    /// <summary>The same libgit2 on macOS (2026-10-06): <c>lib</c> and <c>.dylib</c> around the same commit, flat beside the exe from the package's <c>runtimes/osx-arm64/native</c>.</summary>
    public const string MacNativeLibraryFileName = "libgit2-5853918.dylib";

    /// <summary>What a bare <c>ref</c> means: the checked-out commit.</summary>
    public const string Head = "HEAD";

    /// <summary>Characters of a sha the tools show.</summary>
    public const int ShortShaLength = 7;

    public const int MaxStatusEntries = 200;
    public const int MaxChangedFiles = 200;
    public const int MaxBlameLines = 200;
    public const int MaxBranches = 100;
    public const int MaxTags = 100;

    /// <summary>
    /// How many commits in a row older than <c>since</c> end a log walk (2026-11-15, the tool-arguments review's #87): git's own slop for a
    /// sort that isn't strictly by time.
    /// </summary>
    public const int SinceSlop = 5;
    public const int MaxStashes = 50;
    public const int MaxPathsPerCall = 100;
    public const int MaxTreeEntries = WorkingDirectory.MaxInfoEntries;

    /// <summary>Commits a path-filtered log walks before it stops (a path with few touches in a long history).</summary>
    public const int MaxLogWalk = 10_000;

    /// <summary>The most a file's text is read for <c>gitlib_show</c> (<see cref="WorkingDirectory.MaxReadChars"/>).</summary>
    public const int MaxShowChars = WorkingDirectory.MaxReadChars;

    private readonly WorkingDirectory _files;
    private readonly TimeProvider _time;

    /// <param name="files">The sandbox: its root is read on every call, so <c>/cwd</c> needs no rebind.</param>
    /// <param name="time">The clock a commit's signature is stamped with.</param>
    public GitAccess(WorkingDirectory files, TimeProvider time)
    {
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <summary>The sandbox the repositories are looked for under.</summary>
    public WorkingDirectory Files => _files;

    /// <summary>The zone the tools show a commit's moment in: the clock's.</summary>
    public TimeZoneInfo Zone => _time.LocalTimeZone;

    /// <summary>The clock's now, in its zone: what <c>gitlib_log</c>'s ages count back from (2026-11-15).</summary>
    public DateTimeOffset Now => _time.GetLocalNow();

    // ---- discovery ----

    /// <summary>
    /// Where the repository for the sandbox path <paramref name="relative"/> is: the path judged by the
    /// sandbox, the repository found from it upward, kept only when its working tree is the root or a
    /// folder inside it. <paramref name="full"/> is the path's full form; <paramref name="location"/> null
    /// unless <see cref="GitOutcome.Ok"/>.
    /// </summary>
    public GitOutcome Locate(string relative, out RepoLocation? location, out string full, out string detail)
    {
        location = null;
        detail = "";
        switch (_files.Resolve(relative, forWrite: false, out full))
        {
            case FileOutcome.Ok:
                break;
            default:
                detail = (relative ?? "").Trim();
                return GitOutcome.OutsideRoot;
        }

        string root = _files.Root;
        string start = full;
        if (File.Exists(start))
        {
            start = Path.GetDirectoryName(start) ?? start;
        }

        // A path that is not there yet (or no more) still names a place: the nearest folder above it decides.
        while (!Directory.Exists(start))
        {
            string? parent = Path.GetDirectoryName(start);
            if (parent is null || !WorkingDirectory.IsInside(root, parent))
            {
                return GitOutcome.NoRepository;
            }

            start = parent;
        }

        // Repository.Discover answers null on the published exe (GitBuf, see the class note): the walk is the app's own.
        if (FindGitEntry(start) is not { } gitDirectory)
        {
            return GitOutcome.NoRepository;
        }

        try
        {
            using var repo = new Repository(gitDirectory);
            if (repo.Info.IsBare || repo.Info.WorkingDirectory is null)
            {
                detail = Path.TrimEndingDirectorySeparator(gitDirectory);
                return GitOutcome.Bare;
            }

            string workTree = WorkingDirectory.InRootSpelling(root, Path.TrimEndingDirectorySeparator(Path.GetFullPath(repo.Info.WorkingDirectory)), RealPath.Of);
            if (!WorkingDirectory.IsInside(root, workTree))
            {
                detail = workTree;
                return GitOutcome.AboveSandbox;
            }

            location = new RepoLocation(WorkingDirectory.InRootSpelling(root, Path.TrimEndingDirectorySeparator(Path.GetFullPath(repo.Info.Path)), RealPath.Of), workTree, _files.Relative(workTree, isDirectory: true));
            return GitOutcome.Ok;
        }
        catch (RepositoryNotFoundException)
        {
            return GitOutcome.NoRepository;
        }
        catch (LibGit2SharpException ex)
        {
            return Classify(ex, out detail);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            detail = ex.Message;
            return GitOutcome.Failed;
        }
    }

    /// <summary>
    /// The folder holding the first <c>.git</c> entry at or above <paramref name="start"/> (git's own discovery
    /// walk: a <c>.git</c> folder, a <c>.git</c> file naming one — a worktree or a submodule — which libgit2
    /// reads when the folder is opened, or a bare repository's own folder), or null up to the drive's root.
    /// </summary>
    public static string? FindGitEntry(string start)
    {
        ArgumentNullException.ThrowIfNull(start);
        string? folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(start));
        while (folder is not null)
        {
            string entry = Path.Combine(folder, ".git");
            if (Directory.Exists(entry) || File.Exists(entry) || LooksLikeGitDirectory(folder))
            {
                return folder;
            }

            folder = Path.GetDirectoryName(folder);
        }

        return null;
    }

    /// <summary>git's own test for a bare repository's folder: <c>HEAD</c> beside <c>objects</c> and <c>refs</c>.</summary>
    private static bool LooksLikeGitDirectory(string folder) =>
        File.Exists(Path.Combine(folder, "HEAD")) && Directory.Exists(Path.Combine(folder, "objects")) && Directory.Exists(Path.Combine(folder, "refs"));

    /// <summary>A full path inside the working tree as libgit2 spells it: relative, <c>/</c> separated, <c>""</c> for the tree itself; null when it lies outside the tree.</summary>
    public static string? RepoPath(RepoLocation location, string full)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(full);
        string trimmed = Path.TrimEndingDirectorySeparator(full);
        if (string.Equals(trimmed, location.WorkTree, StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }

        return WorkingDirectory.IsInside(location.WorkTree, trimmed)
            ? trimmed[(location.WorkTree.Length + 1)..].Replace('\\', '/')
            : null;
    }

    /// <summary>A libgit2 path back in the sandbox's spelling: relative to the root, <c>\</c> separated.</summary>
    public string SandboxPath(RepoLocation location, string repoPath)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(repoPath);
        return _files.Relative(Path.Combine(location.WorkTree, repoPath.Replace('/', Path.DirectorySeparatorChar)));
    }

    // ---- reads ----

    /// <summary>The working tree's state under <paramref name="relative"/> (<c>""</c> = the whole tree).</summary>
    public GitStatusReport Status(string relative) => Run(relative, GitStatusReport.Refused, (repo, location, full) =>
    {
        if (RepoPath(location, full) is not { } prefix)
        {
            return GitStatusReport.Refused(GitOutcome.NotInRepository, _files.Relative(full));
        }

        var staged = new List<GitEntry>();
        var unstaged = new List<GitEntry>();
        var untracked = new List<GitEntry>();
        var conflicts = new List<GitEntry>();
        bool truncated = false;
        int count = 0;
        foreach (var entry in StatusEntries(repo, location, prefix, recurseUntracked: false))
        {
            if (count++ >= MaxStatusEntries)
            {
                truncated = true;
                break;
            }

            string path = SandboxPath(location, entry.FilePath);
            var state = entry.State;
            if (state.HasFlag(FileStatus.Conflicted))
            {
                conflicts.Add(new GitEntry('U', path));
                continue;
            }

            if (IndexCode(state) is { } indexCode)
            {
                staged.Add(new GitEntry(indexCode, path, indexCode == 'R' && entry.HeadToIndexRenameDetails is { } r ? SandboxPath(location, r.OldFilePath) : null));
            }

            if (state.HasFlag(FileStatus.NewInWorkdir))
            {
                untracked.Add(new GitEntry('?', entry.FilePath.EndsWith('/') ? path + Path.DirectorySeparatorChar : path));
            }
            else if (WorkdirCode(state) is { } workdirCode)
            {
                unstaged.Add(new GitEntry(workdirCode, path, workdirCode == 'R' && entry.IndexToWorkDirRenameDetails is { } r ? SandboxPath(location, r.OldFilePath) : null));
            }
        }

        var head = repo.Head;
        var upstream = Upstream(repo, head);
        return new GitStatusReport(GitOutcome.Ok, "", BranchName(repo), repo.Info.IsHeadUnborn, repo.Info.IsHeadDetached, head.Tip is null ? null : Short(head.Tip),
            upstream?.Name, upstream?.Ahead, upstream?.Behind, staged, unstaged, untracked, conflicts, truncated);
    });

    /// <summary>
    /// The commits reachable from <paramref name="reference"/> (blank = HEAD), newest first, at most <paramref name="max"/>; under a file or
    /// folder <paramref name="relative"/> only those that changed it, and only those <paramref name="filter"/> keeps (2026-11-15).
    /// </summary>
    public GitLogReport Log(string relative, string reference, int max, GitLogFilter? filter = null) => Log(relative, reference, max, filter, MaxLogWalk);

    /// <summary><see cref="Log(string, string, int, GitLogFilter?)"/> with the walk's cap given (tests reach it over a small repository).</summary>
    internal GitLogReport Log(string relative, string reference, int max, GitLogFilter? filter, int walkCap) => Run(relative, GitLogReport.Refused, (repo, location, full) =>
    {
        if (RepoPath(location, full) is not { } prefix)
        {
            return GitLogReport.Refused(GitOutcome.NotInRepository, _files.Relative(full));
        }

        var found = TryCommit(repo, reference, out var tip, out string detail);
        if (found != GitOutcome.Ok)
        {
            return GitLogReport.Refused(found, detail);
        }

        max = Math.Max(1, max);
        var keep = filter is { Any: true } ? filter : null;
        bool skipping = prefix.Length > 0 || keep is not null;
        var commits = new List<GitCommitInfo>();
        bool truncated = false;
        bool walkCapped = false;
        bool maybeMore = false;
        int walked = 0;
        int older = 0;
        foreach (var commit in repo.Commits.QueryBy(new CommitFilter { IncludeReachableFrom = tip, SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Time }))
        {
            if (walked++ >= walkCap)
            {
                // With commits skipped, only the newest were searched; with none, every one walked counted, so more are known to exist. With
                // the list already full, nothing past it is known either way (#146): "more may be before them", not a short search.
                maybeMore = skipping && commits.Count == max;
                walkCapped = skipping && !maybeMore;
                truncated = !skipping;
                break;
            }

            // git's slop rule on the date Keeps reads (#87): the sort isn't strictly by time, so the walk stops only after SinceSlop commits in a
            // row older than since. A complete search: no cap note.
            if (keep?.Since is { } since)
            {
                older = commit.Committer.When < since ? older + 1 : 0;
                if (older >= SinceSlop)
                {
                    break;
                }
            }

            // The cheap test first (#33): Keeps reads the commit, Touches its trees.
            if ((keep is not null && !Keeps(commit, keep)) || (prefix.Length > 0 && !Touches(commit, prefix)))
            {
                continue;
            }

            if (commits.Count == max)
            {
                truncated = true;
                break;
            }

            commits.Add(Info(commit));
        }

        return new GitLogReport(GitOutcome.Ok, "", ReferenceName(repo, reference), prefix.Length == 0 ? null : _files.Relative(full, Directory.Exists(full)), commits, truncated)
        {
            Filter = keep,
            WalkCapped = walkCapped,
            WalkCap = walkCap,
            MaybeMore = maybeMore,
        };
    });

    /// <summary>One commit (<paramref name="relative"/> = <c>""</c>: its header and the files it changed) or the path at that commit (a file's text, a folder's entries).</summary>
    public GitShowReport Show(string reference, string relative) => Run(relative, GitShowReport.Refused, (repo, location, full) =>
    {
        if (RepoPath(location, full) is not { } prefix)
        {
            return GitShowReport.Refused(GitOutcome.NotInRepository, _files.Relative(full));
        }

        var found = TryCommit(repo, reference, out var commit, out string detail);
        if (found != GitOutcome.Ok)
        {
            return GitShowReport.Refused(found, detail);
        }

        var info = Info(commit!);
        if (prefix.Length == 0)
        {
            var (changes, truncated) = Compare(repo, location, commit!.Parents.FirstOrDefault()?.Tree, commit.Tree, "");
            return new GitShowReport(GitOutcome.Ok, "", info, changes.Select(c => c.Change).ToList(), truncated, null, null, 0, false, []);
        }

        var entry = commit![prefix];
        if (entry is null)
        {
            return GitShowReport.Refused(GitOutcome.Missing, _files.Relative(full));
        }

        string shown = SandboxPath(location, prefix);
        if (entry.TargetType == TreeEntryTargetType.Tree)
        {
            var tree = (Tree)entry.Target;
            var names = tree.Take(WorkingDirectory.MaxEntries).Select(e => e.TargetType == TreeEntryTargetType.Tree ? e.Name + "/" : e.Name).ToList();
            return new GitShowReport(GitOutcome.Ok, "", info, [], false, shown + Path.DirectorySeparatorChar, null, 0, tree.Count > WorkingDirectory.MaxEntries, names);
        }

        if (entry.TargetType != TreeEntryTargetType.Blob)
        {
            return GitShowReport.Refused(GitOutcome.Missing, shown);
        }

        var blob = (Blob)entry.Target;
        var read = BlobText(blob, out string text);
        if (read != GitOutcome.Ok)
        {
            return GitShowReport.Refused(read, shown);
        }

        var lines = UnifiedDiff.Split(text);
        bool cut = text.Length > MaxShowChars;
        return new GitShowReport(GitOutcome.Ok, "", info, [], false, shown, cut ? text[..MaxShowChars] : text, lines.Lines.Count, cut, []);
    });

    /// <summary>The changes <paramref name="request"/> names, narrowed to its path, the patch cut at <paramref name="maxLines"/>.</summary>
    public GitDiffReport Diff(GitDiffRequest request, int maxLines) => DiffCore(request, maxLines, pathsOnly: false, MaxChangedFiles, CancellationToken.None);

    /// <summary>The most files <see cref="DiffPaths"/> lists before it says it was cut.</summary>
    public const int MaxDiffPaths = 5000;

    /// <summary>
    /// The files <paramref name="request"/>'s changes touch, with no patch and no counts (2026-10-20, a review: a code audit's git scope and a
    /// Ralph pass's committed files asked <see cref="Diff"/> for one line, which still read and diffed every file, uncancellable, and stopped at
    /// <see cref="MaxChangedFiles"/>): at most <see cref="MaxDiffPaths"/>, <see cref="GitDiffReport.FilesTruncated"/> past it;
    /// <paramref name="cancellationToken"/> throws between entries.
    /// </summary>
    public GitDiffReport DiffPaths(GitDiffRequest request, CancellationToken cancellationToken = default) => DiffCore(request, 1, pathsOnly: true, MaxDiffPaths, cancellationToken);

    private GitDiffReport DiffCore(GitDiffRequest request, int maxLines, bool pathsOnly, int cap, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Run(request.Path, (o, d) => GitDiffReport.Refused(request.Kind, o, d), (repo, location, full) =>
        {
            if (RepoPath(location, full) is not { } prefix)
            {
                return GitDiffReport.Refused(request.Kind, GitOutcome.NotInRepository, _files.Relative(full));
            }

            List<FileDiff> files;
            bool filesTruncated;
            string label;
            switch (request.Kind)
            {
                case GitDiffKind.Unstaged:
                    (files, filesTruncated) = WorkdirDiff(repo, location, prefix, staged: false, pathsOnly, cap, cancellationToken);
                    label = "";
                    break;
                case GitDiffKind.Staged:
                    (files, filesTruncated) = WorkdirDiff(repo, location, prefix, staged: true, pathsOnly, cap, cancellationToken);
                    label = "";
                    break;
                case GitDiffKind.Commit:
                {
                    var found = TryCommit(repo, request.Reference ?? "", out var commit, out string detail);
                    if (found != GitOutcome.Ok)
                    {
                        return GitDiffReport.Refused(request.Kind, found, detail);
                    }

                    (files, filesTruncated) = Compare(repo, location, commit!.Parents.FirstOrDefault()?.Tree, commit.Tree, prefix, pathsOnly, cap, cancellationToken);
                    // The commit's subject is its author's words: fenced in place (2026-11-15, the web-tools plan's §3), the note after the diff.
                    label = Short(commit) + " (" + GitText.Outside(commit.MessageShort) + ")";
                    break;
                }

                default:
                {
                    var foundFrom = TryCommit(repo, request.From ?? "", out var from, out string detail);
                    if (foundFrom != GitOutcome.Ok)
                    {
                        return GitDiffReport.Refused(request.Kind, foundFrom, detail);
                    }

                    var foundTo = TryCommit(repo, request.To ?? "", out var to, out detail);
                    if (foundTo != GitOutcome.Ok)
                    {
                        return GitDiffReport.Refused(request.Kind, foundTo, detail);
                    }

                    (files, filesTruncated) = Compare(repo, location, from!.Tree, to!.Tree, prefix, pathsOnly, cap, cancellationToken);
                    label = Short(from) + " to " + Short(to);
                    break;
                }
            }

            if (prefix.Length > 0 && files.Count == 0 && request.Kind is GitDiffKind.Commit or GitDiffKind.Range && !Directory.Exists(full) && !File.Exists(full))
            {
                // A path that is in neither tree: say so rather than "no changes".
                return GitDiffReport.Refused(request.Kind, GitOutcome.Missing, _files.Relative(full));
            }

            int added = files.Sum(f => f.Change.Added);
            int deleted = files.Sum(f => f.Change.Deleted);
            var all = new List<string>();
            foreach (var file in files)
            {
                all.AddRange(file.Patch);
            }

            maxLines = Math.Max(1, maxLines);
            bool truncated = all.Count > maxLines;
            string patch = string.Join("\n", truncated ? all.Take(maxLines) : all);
            return new GitDiffReport(GitOutcome.Ok, "", request.Kind, label, files.Select(f => f.Change).ToList(), filesTruncated, patch, added, deleted, truncated, all.Count);
        });
    }

    /// <summary>Who last changed each line of the file <paramref name="relative"/> at <paramref name="reference"/> (blank = HEAD), over the window <paramref name="fromLine"/>–<paramref name="toLine"/> (1-based; null = the start / <see cref="MaxBlameLines"/> on).</summary>
    public GitBlameReport Blame(string relative, string reference, int? fromLine, int? toLine) => Run(relative, GitBlameReport.Refused, (repo, location, full) =>
    {
        if (RepoPath(location, full) is not { Length: > 0 } repoPath)
        {
            return GitBlameReport.Refused(RepoPath(location, full) is null ? GitOutcome.NotInRepository : GitOutcome.Missing, _files.Relative(full));
        }

        var found = TryCommit(repo, reference, out var commit, out string detail);
        if (found != GitOutcome.Ok)
        {
            return GitBlameReport.Refused(found, detail);
        }

        string shown = SandboxPath(location, repoPath);
        var entry = commit![repoPath];
        if (entry is null || entry.TargetType != TreeEntryTargetType.Blob)
        {
            return GitBlameReport.Refused(GitOutcome.Missing, shown);
        }

        var read = BlobText((Blob)entry.Target, out string text);
        if (read != GitOutcome.Ok)
        {
            return GitBlameReport.Refused(read, shown);
        }

        var lines = UnifiedDiff.Split(text).Lines;
        int total = lines.Count;
        if (total == 0)
        {
            return new GitBlameReport(GitOutcome.Ok, "", shown, ReferenceName(repo, reference), 0, 0, 0, [], 0);
        }

        int from = Math.Clamp(fromLine ?? 1, 1, total);
        int to = Math.Clamp(toLine ?? from + MaxBlameLines - 1, from, Math.Min(total, from + MaxBlameLines - 1));
        var hunks = repo.Blame(repoPath, new BlameOptions { StartingAt = commit, MinLine = from, MaxLine = to });
        var blamed = new List<GitBlameLine>(to - from + 1);
        var shas = new HashSet<string>(StringComparer.Ordinal);
        for (int n = from; n <= to; n++)
        {
            var hunk = hunks.HunkForLine(n - 1);
            var last = hunk.FinalCommit;
            shas.Add(last.Sha);
            blamed.Add(new GitBlameLine(n, Short(last), hunk.FinalSignature.Name, hunk.FinalSignature.When, lines[n - 1]));
        }

        return new GitBlameReport(GitOutcome.Ok, "", shown, ReferenceName(repo, reference), from, to, total, blamed, shas.Count);
    });

    /// <summary>The local branches, the remote-tracking branches and the tags, each capped.</summary>
    public GitRefsReport Refs(string relative) => Run(relative, GitRefsReport.Refused, (repo, location, full) =>
    {
        var local = new List<GitRef>();
        var remote = new List<GitRef>();
        bool truncated = false;
        foreach (var branch in repo.Branches.OrderBy(b => b.FriendlyName, StringComparer.Ordinal))
        {
            var list = branch.IsRemote ? remote : local;
            if (list.Count >= MaxBranches)
            {
                truncated = true;
                continue;
            }

            list.Add(new GitRef(branch.FriendlyName, Short(branch.Tip), branch.Tip?.MessageShort ?? "", branch.IsCurrentRepositoryHead, branch.IsRemote ? null : Upstream(repo, branch)?.Name));
        }

        // The tags as gitlib_tag lists them (2026-11-15, the tool-arguments review's #85: one ordered, capped read for both).
        var (tags, tagsTruncated) = TagList(repo);
        return new GitRefsReport(GitOutcome.Ok, "", repo.Info.IsHeadDetached ? null : BranchName(repo), repo.Info.IsHeadDetached, local, remote, tags, truncated || tagsTruncated);
    });

    /// <summary>The tags alone (2026-11-15, <c>gitlib_tag</c>'s list; the tool-arguments review's #25, #26, #85, #130): <see cref="TagList"/>.</summary>
    public GitTagsReport Tags(string relative) => Run(relative, GitTagsReport.Refused, (repo, location, full) =>
    {
        var (tags, truncated) = TagList(repo);
        return new GitTagsReport(GitOutcome.Ok, "", tags, truncated);
    });

    /// <summary>
    /// The tags newest target first (2026-11-15, #25, #26): by the peeled commit's committer date, then the name; a tag of a tree or blob last.
    /// Cut at <see cref="MaxTags"/> after the sort, so the newest are the ones kept (#85: the cut once fell on the names). Rows by <see cref="TagRow"/>.
    /// </summary>
    private static (List<GitRef> Tags, bool Truncated) TagList(Repository repo)
    {
        var sorted = repo.Tags
            .Select(tag => (Tag: tag, Commit: tag.PeeledTarget as Commit))
            .OrderBy(t => t.Commit is null ? 1 : 0)
            .ThenByDescending(t => t.Commit?.Committer.When ?? DateTimeOffset.MinValue)
            .ThenBy(t => t.Tag.FriendlyName, StringComparer.Ordinal)
            .ToList();
        return (sorted.Take(MaxTags).Select(t => TagRow(t.Tag, t.Commit)).ToList(), sorted.Count > MaxTags);
    }

    /// <summary>One tag's row (2026-11-15, #130): its name, its target's short sha, and the commit's subject, or an annotated tag's own subject, marked.</summary>
    private static GitRef TagRow(Tag tag, Commit? target)
    {
        string subject = tag.Annotation is { } annotation
            ? GitText.AnnotatedSubject((annotation.Message ?? "").Split('\n')[0].TrimEnd())
            : target?.MessageShort ?? "";
        return new GitRef(tag.FriendlyName, target is null ? tag.Target.Sha[..ShortShaLength] : Short(target), subject);
    }

    /// <summary>
    /// Whether <paramref name="name"/> can be a new branch's or tag's (2026-11-15, the tool-arguments review's #4, #24, #52, #64, #79, #111, #139,
    /// #142: one check for both creators and a rename's new name). <see cref="GitOutcome.BadName"/> for a name git can't hold, a leading
    /// <c>-</c>, <c>HEAD</c> or a <c>refs/</c> prefix; <see cref="GitOutcome.NameConflict"/> for one of the same kind; <see cref="GitOutcome.NameTaken"/>
    /// for one that already names another kind of ref, a remote branch, a folder of refs (<c>v1</c> beside <c>v1/rc1</c>) or an object (a short
    /// sha: <c>facade</c> is a fine name until a commit starts with it), so a name always reads one way.
    /// </summary>
    private static GitOutcome CheckNewName(Repository repo, string name, bool tag, out string detail)
    {
        detail = name;
        string space = tag ? "refs/tags/" : "refs/heads/";
        if (name.Length == 0 || !Reference.IsValidName(space + name) || name.StartsWith('-')
            || name.Equals(Head, StringComparison.OrdinalIgnoreCase) || name.StartsWith("refs/", StringComparison.Ordinal))
        {
            return GitOutcome.BadName;
        }

        bool sameKind = tag ? repo.Tags[name] is not null : repo.Branches[name] is { IsRemote: false };
        if (sameKind)
        {
            return GitOutcome.NameConflict;
        }

        string? taken = (tag ? repo.Branches[name] is { IsRemote: false } : repo.Tags[name] is not null) ? (tag ? GitText.TakenByBranch : GitText.TakenByTag)
            : repo.Branches[name] is { IsRemote: true } ? GitText.TakenByRemote
            : FolderClash(repo, space, name) ? GitText.TakenByFolder
            : Resolves(repo, name) ? GitText.TakenByObject
            : null;
        if (taken is null)
        {
            return GitOutcome.Ok;
        }

        detail = GitText.NameTakenDetail(name, taken);
        return GitOutcome.NameTaken;
    }

    /// <summary>Whether a ref in <paramref name="space"/> sits under <paramref name="name"/> as a folder, or <paramref name="name"/> under one.</summary>
    private static bool FolderClash(Repository repo, string space, string name) =>
        repo.Refs.Select(r => r.CanonicalName).Where(c => c.StartsWith(space, StringComparison.Ordinal)).Select(c => c[space.Length..])
            .Any(other => other.StartsWith(name + "/", StringComparison.Ordinal) || name.StartsWith(other + "/", StringComparison.Ordinal));

    /// <summary>Whether <paramref name="name"/> already resolves to an object (a short or full sha); ambiguous counts, as <see cref="TryCommit"/> reads it.</summary>
    private static bool Resolves(Repository repo, string name)
    {
        try
        {
            return repo.Lookup(name) is not null;
        }
        catch (AmbiguousSpecificationException)
        {
            return true;
        }
        catch (LibGit2SharpException)
        {
            return false;
        }
    }

    /// <summary>
    /// The files git knows under <paramref name="relative"/> (2026-10-18, a code audit's scope inside a repository): the index's entries, and
    /// with <paramref name="includeUntracked"/> the untracked files too (every one, folders walked; ignored files and submodules out), all in
    /// the sandbox's spelling; at most <see cref="MaxTrackedFiles"/> in all, <see cref="GitTrackedReport.Truncated"/> past it.
    /// <para>2026-10-19, the review of 8331ebc9: uncapped until then, and the untracked files came from libgit2's recursive status, one
    /// native call no token reaches, so a large untracked, unignored folder ran for minutes after Esc (and blocked headless). Now status
    /// lists untracked folders unrecursed and each is walked by <see cref="WorkingDirectory.ListFiles"/> (its entry and time budget, the
    /// token, never into a reparse point), a nested repository's folder left out as status leaves it, an ignored file dropped.</para>
    /// <para>2026-10-19, the second review: <paramref name="includeTracked"/> false skips the index (a <c>diff</c> scope wants the untracked
    /// files alone; past the cap the index left them none, and nothing said so), the cap then the untracked files' alone.</para>
    /// </summary>
    public GitTrackedReport TrackedFiles(string relative, bool includeUntracked, CancellationToken cancellationToken = default, bool includeTracked = true) =>
        Run(relative, GitTrackedReport.Refused, (repo, location, full) =>
    {
        if (RepoPath(location, full) is not { } prefix)
        {
            return GitTrackedReport.Refused(GitOutcome.NotInRepository, _files.Relative(full));
        }

        string under = prefix.Length == 0 ? "" : prefix + "/";
        var tracked = new List<string>();
        bool truncated = false;
        foreach (var entry in includeTracked ? repo.Index : Enumerable.Empty<IndexEntry>())
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The index's path in libgit2's spelling whatever the platform hands back.
            string path = entry.Path.Replace('\\', '/');
            if (under.Length == 0 || path.StartsWith(under, StringComparison.Ordinal) || string.Equals(path, prefix, StringComparison.Ordinal))
            {
                if (tracked.Count >= MaxTrackedFiles)
                {
                    truncated = true;
                    break;
                }

                tracked.Add(SandboxPath(location, path));
            }
        }

        var untracked = new List<string>();
        if (includeUntracked && !truncated)
        {
            foreach (var entry in StatusEntries(repo, location, prefix, recurseUntracked: false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!entry.State.HasFlag(FileStatus.NewInWorkdir))
                {
                    continue;
                }

                int room = MaxTrackedFiles - tracked.Count - untracked.Count;
                if (room <= 0)
                {
                    truncated = true;
                    break;
                }

                if (!entry.FilePath.EndsWith('/'))
                {
                    untracked.Add(SandboxPath(location, entry.FilePath));
                    continue;
                }

                // An untracked folder: walked here, budgeted and cancellable.
                untracked.AddRange(WalkUntracked(repo, location, entry.FilePath, room, cancellationToken, out bool cut));
                truncated |= cut;
            }
        }

        return new GitTrackedReport(GitOutcome.Ok, "", tracked, untracked, location.RootRelative, truncated);
    });

    /// <summary>
    /// The files of the untracked folder <paramref name="folderRepoPath"/> (status's entry, a trailing <c>/</c>), sandbox-relative, at most
    /// <paramref name="room"/>: walked by <see cref="WorkingDirectory.ListFiles"/> (its budget, the token, never into a reparse point), a nested
    /// repository's folder left out as status leaves it, and an ignored folder never entered (2026-10-20, a review: its files spent the walk's
    /// budget before each was dropped as ignored, so a new folder's ignored <c>node_modules</c> left its sources unlisted); an ignored file dropped.
    /// </summary>
    private List<string> WalkUntracked(Repository repo, RepoLocation location, string folderRepoPath, int room, CancellationToken cancellationToken, out bool cut)
    {
        cut = false;
        string folder = Path.Combine(location.WorkTree, folderRepoPath.TrimEnd('/').Replace('/', Path.DirectorySeparatorChar));
        if (IsNestedRepository(folder))
        {
            return [];
        }

        string RepoPathOf(string fullPath) => Path.GetRelativePath(location.WorkTree, fullPath).Replace('\\', '/');
        var walked = _files.ListFiles(_files.Relative(folder), room, name => string.Equals(name, ".git", StringComparison.OrdinalIgnoreCase), cancellationToken,
            skipFolder: full => IsNestedRepository(full) || repo.Ignore.IsPathIgnored(RepoPathOf(full) + "/"));
        cut = walked.Cut;
        var files = new List<string>();
        foreach (string file in walked.Files)
        {
            if (!repo.Ignore.IsPathIgnored(RepoPathOf(Path.Combine(_files.Root, file))))
            {
                files.Add(file);
            }
        }

        return files;
    }

    /// <summary>Whether a folder is a repository of its own (its own <c>.git</c>, a folder or a file): status leaves it out.</summary>
    private static bool IsNestedRepository(string folder) => Directory.Exists(Path.Combine(folder, ".git")) || File.Exists(Path.Combine(folder, ".git"));

    /// <summary>The most files <see cref="TrackedFiles"/> lists, tracked and untracked together, before it says it was cut.</summary>
    public const int MaxTrackedFiles = WorkingDirectory.MaxListEntries;

    /// <summary>The most paths <see cref="ChangedPaths"/> returns before it says it was cut.</summary>
    public const int MaxChangedPaths = 5000;

    /// <summary>
    /// Every path changed in the working tree under <paramref name="relative"/> (2026-10-18, a Ralph pass's evidence): modified, added, deleted, renamed
    /// and untracked files, each in the sandbox's spelling, an untracked folder walked to its files (<see cref="Status"/> lists it as one entry);
    /// ignored files and submodules out, at most <see cref="MaxChangedPaths"/>.
    /// <para>2026-10-20, a review: libgit2's recursive status was one native call no token reached, run before and after every pass, so a large
    /// untracked, unignored folder held the loop for minutes past Esc; untracked folders are walked as <see cref="TrackedFiles"/> walks them now
    /// (<see cref="WalkUntracked"/>), under <paramref name="cancellationToken"/>. A rename staged in the index names its old path too (only a
    /// rename in the working tree did, so <c>git mv</c>'s source was lost).</para>
    /// </summary>
    public GitChangedReport ChangedPaths(string relative, CancellationToken cancellationToken = default) => Run(relative, GitChangedReport.Refused, (repo, location, full) =>
    {
        if (RepoPath(location, full) is not { } prefix)
        {
            return GitChangedReport.Refused(GitOutcome.NotInRepository, _files.Relative(full));
        }

        var paths = new List<string>();
        bool truncated = false;
        foreach (var entry in StatusEntries(repo, location, prefix, recurseUntracked: false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            int room = MaxChangedPaths - paths.Count;
            if (room <= 0)
            {
                truncated = true;
                break;
            }

            if (entry.FilePath.EndsWith('/'))
            {
                if (entry.State.HasFlag(FileStatus.NewInWorkdir))
                {
                    paths.AddRange(WalkUntracked(repo, location, entry.FilePath, room, cancellationToken, out bool cut));
                    truncated |= cut;
                }

                continue;
            }

            paths.Add(SandboxPath(location, entry.FilePath));
            if (entry.HeadToIndexRenameDetails is { OldFilePath: { } staged })
            {
                paths.Add(SandboxPath(location, staged));
            }

            if (entry.IndexToWorkDirRenameDetails is { OldFilePath: { } moved })
            {
                paths.Add(SandboxPath(location, moved));
            }
        }

        return new GitChangedReport(GitOutcome.Ok, "", paths.Distinct(StringComparer.Ordinal).ToList(), truncated);
    });

    /// <summary>
    /// The commit <paramref name="a"/> and <paramref name="b"/> last shared (2026-10-18, <c>/code-audit branch</c>): LibGit2Sharp's
    /// <c>ObjectDatabase.FindMergeBase</c>, in-process; null in <see cref="GitMergeBaseReport.Sha"/> when they share none. HEAD's sha comes with it,
    /// so the caller can tell a merge-base that is HEAD itself (nothing on the branch since the base).
    /// </summary>
    public GitMergeBaseReport MergeBase(string relative, string a, string b) => Run(relative, GitMergeBaseReport.Refused, (repo, location, full) =>
    {
        var foundA = TryCommit(repo, a, out var first, out string detail);
        if (foundA != GitOutcome.Ok)
        {
            return GitMergeBaseReport.Refused(foundA, detail);
        }

        var foundB = TryCommit(repo, b, out var second, out detail);
        if (foundB != GitOutcome.Ok)
        {
            return GitMergeBaseReport.Refused(foundB, detail);
        }

        var shared = repo.ObjectDatabase.FindMergeBase(first!, second!);
        return new GitMergeBaseReport(GitOutcome.Ok, "", shared?.Sha, repo.Head.Tip?.Sha);
    });

    // ---- writes ----

    /// <summary>A branch <paramref name="name"/> at <paramref name="startPoint"/> (blank = HEAD), checked out when <paramref name="switchTo"/>.</summary>
    public GitBranchResult CreateBranch(string relative, string name, string startPoint, bool switchTo) => Run(relative, GitBranchResult.Refused, (repo, location, full) =>
    {
        name = name.Trim();
        var found = TryCommit(repo, startPoint, out var commit, out string detail);
        if (found != GitOutcome.Ok)
        {
            return GitBranchResult.Refused(found, detail);
        }

        var named = CheckNewName(repo, name, tag: false, out string nameDetail);
        if (named != GitOutcome.Ok)
        {
            return GitBranchResult.Refused(named, nameDetail);
        }

        Branch branch;
        try
        {
            branch = repo.Branches.Add(name, commit!);
        }
        catch (NameConflictException)
        {
            return GitBranchResult.Refused(GitOutcome.NameConflict, name);
        }

        DiagnosticLog.Debug(Category, BranchCreatedLogLine(name, Short(commit!)));
        if (!switchTo)
        {
            return new GitBranchResult(GitOutcome.Ok, "", name, Short(commit!));
        }

        var switched = Switch(repo, branch);
        return switched == GitOutcome.Ok
            ? new GitBranchResult(GitOutcome.Ok, "", name, Short(commit!))
            : new GitBranchResult(switched, "", name, Short(commit!));
    });

    /// <summary>
    /// A new commit undoing <paramref name="reference"/> (2026-11-15, feature idea #32; rebuilt in memory the same day, the tool-arguments
    /// review's #117, the user's call): nothing lost, history never rewritten. Every refusal comes before anything is written (#149): unborn;
    /// the ref; an operation in progress (#40); unmerged entries; a detached HEAD (#96); a merge commit; a commit off the branch checked out
    /// (#91); no identity; a staged or tracked change (#57, #63; untracked files don't count). Then libgit2 reverts in memory
    /// (<c>git_revert_commit</c>: no index, no work tree, no callback): a conflict is <see cref="GitOutcome.RevertConflicted"/> and a tree equal
    /// to HEAD's <see cref="GitOutcome.NothingToRevert"/>, nothing written either way. The paths it touches (<see cref="TouchedPaths"/>, the
    /// app's own tree walk) are then judged: a submodule pointer, a tree too large to walk whole, the write guard (#21), something on disk in
    /// the way of an added path (#8, #20), an assume-unchanged entry (#73), a glob character (libgit2 reads checkout paths as globs). Only then
    /// the commit object, a safe checkout of the touched paths, and the branch's own ref moved (never HEAD itself, which would detach it). A
    /// checkout or ref move that fails puts back the revert's own writes (<see cref="PutBackPaths"/>) and answers <see cref="GitOutcome.Failed"/>.
    /// <paramref name="message"/> blank once tidied takes git's default (<see cref="GitText.RevertMessage"/>).
    /// </summary>
    public GitRevertResult Revert(string relative, string reference, string message) => Revert(relative, reference, message, MaxTreeEntries);

    /// <summary>
    /// <see cref="Revert(string, string, string)"/> with the tree walk's <paramref name="treeLimit"/> as a seam (#86's kind): a real cut needs
    /// 10,001 clean files on disk.
    /// </summary>
    internal GitRevertResult Revert(string relative, string reference, string message, int treeLimit) => Run(relative, GitRevertResult.Refused, (repo, location, full) =>
    {
        if (repo.Info.IsHeadUnborn)
        {
            return GitRevertResult.Refused(GitOutcome.Unborn);
        }

        var found = TryCommit(repo, reference, out var target, out string detail);
        if (found != GitOutcome.Ok)
        {
            return GitRevertResult.Refused(found, detail);
        }

        if (repo.Info.CurrentOperation != CurrentOperation.None)
        {
            return GitRevertResult.Refused(GitOutcome.OperationInProgress, GitText.OperationWord(repo.Info.CurrentOperation));
        }

        // Conflicts a stash pop leaves have no operation in progress.
        if (repo.Index.Conflicts.Any())
        {
            return GitRevertResult.Refused(GitOutcome.Conflicts);
        }

        if (repo.Info.IsHeadDetached)
        {
            return GitRevertResult.Refused(GitOutcome.Detached);
        }

        if (target!.Parents.Count() > 1)
        {
            return GitRevertResult.Refused(GitOutcome.MergeCommit, Short(target));
        }

        // A commit off the branch (#91): its undo would apply changes the branch never had.
        var head = repo.Head.Tip!;
        if (repo.ObjectDatabase.FindMergeBase(target, head) is not { } mergeBase || mergeBase.Id != target.Id)
        {
            return GitRevertResult.Refused(GitOutcome.NotOnBranch, GitText.NotOnBranchDetail(Short(target), BranchName(repo)));
        }

        var identity = Sign(repo, message, GitText.RevertMessage(target.MessageShort, target.Sha), out var signature, out string text);
        if (identity != GitOutcome.Ok)
        {
            return GitRevertResult.Refused(identity);
        }

        // The whole work tree, never the path's prefix: path only picks the repository, and a revert touches files anywhere in it.
        if (HasTrackedChange(repo))
        {
            return GitRevertResult.Refused(GitOutcome.DirtyTree);
        }

        string targetShort = Short(target);
        var merged = repo.ObjectDatabase.RevertCommit(target, head, 0, new MergeTreeOptions());
        if (merged.Status == MergeTreeStatus.Conflicts)
        {
            var conflicted = merged.Conflicts.Select(c => (c.Ours ?? c.Theirs ?? c.Ancestor).Path).Distinct(StringComparer.Ordinal).Select(p => SandboxPath(location, p)).ToList();
            DiagnosticLog.Debug(Category, RevertConflictedLogLine(targetShort, conflicted.Count));
            return GitRevertResult.Refused(GitOutcome.RevertConflicted, GitText.ConflictsIn(targetShort, FileText.Names(conflicted)));
        }

        if (merged.Tree.Id == head.Tree.Id)
        {
            return GitRevertResult.Refused(GitOutcome.NothingToRevert, targetShort);
        }

        var touched = TouchedPaths(head.Tree, merged.Tree, treeLimit);
        if (touched.Cut)
        {
            return GitRevertResult.Refused(GitOutcome.TooLargeToRevert, targetShort);
        }

        if (touched.Gitlink is { } gitlink)
        {
            return GitRevertResult.Refused(GitOutcome.SubmoduleChange, SandboxPath(location, gitlink));
        }

        var refusal = JudgeTouched(repo, location, touched);
        if (refusal is not null)
        {
            return refusal;
        }

        // The commit from the revert's tree, never the index (#59); the message tidied already, so libgit2's prettify (a GitBuf, empty under
        // NativeAOT) stays off.
        var commit = repo.ObjectDatabase.CreateCommit(signature!, signature!, text, merged.Tree, [head], prettifyMessage: false);
        string workTree = location.WorkTree;
        try
        {
            // No callbacks (a native callback is unproven under AOT); the refusals above rule out what SAFE would refuse.
            repo.CheckoutPaths(commit.Sha, touched.Paths, new CheckoutOptions { CheckoutModifiers = CheckoutModifiers.None });
            RemoveByHand(repo, workTree, touched.Removed);
            repo.Refs.UpdateTarget(repo.Head.Reference.ResolveToDirectReference(), commit.Id, GitText.RevertReflog(commit.MessageShort));
        }
        catch (CheckoutConflictException)
        {
            // Found before libgit2 writes a file: nothing to put back.
            var changed = StatusEntries(repo, location, "", recurseUntracked: false).Select(e => e.FilePath).Where(touched.Paths.Contains).Select(p => SandboxPath(location, p)).ToList();
            return GitRevertResult.Refused(GitOutcome.CheckoutConflict, FileText.Names(changed));
        }
        catch (Exception ex) when (ex is LibGit2SharpException or IOException or UnauthorizedAccessException)
        {
            // A file another program holds, a held branch lock: the revert's own writes put back, HEAD where it was.
            var left = PutBackPaths(repo, workTree, head, touched.Paths).Select(p => SandboxPath(location, p)).ToList();
            return GitRevertResult.Refused(GitOutcome.Failed, GitText.RevertFailed(LogText.Excerpt(ex.Message), left.Count == 0 ? "" : FileText.Names(left)));
        }

        DiagnosticLog.Debug(Category, RevertedLogLine(targetShort, Short(commit), BranchName(repo)));
        return new GitRevertResult(GitOutcome.Ok, "", targetShort, Short(commit), BranchName(repo), commit.MessageShort, touched.Paths.Count);
    });

    /// <summary>
    /// What a revert's touched paths are refused for, before anything is written (2026-11-15, the tool-arguments review's refusals 8–11): the
    /// write guard in the file tools' words (#21; a project file's ask comes with it), something on disk in an added path's way (#8, #20), an
    /// assume-unchanged entry status can't see (#73), and a glob character (<see cref="GlobFree"/>). Null when all pass.
    /// </summary>
    private GitRevertResult? JudgeTouched(Repository repo, RepoLocation location, TouchedSet touched)
    {
        foreach (string path in touched.Paths)
        {
            string shown = SandboxPath(location, path);
            var resolved = _files.Resolve(shown, forWrite: true, out _);
            if (resolved != FileOutcome.Ok)
            {
                return WriteGuard.IsRefusal(resolved)
                    ? GitRevertResult.Refused(GitOutcome.Guarded, FileText.Error(resolved, shown, "revert"))
                    : GitRevertResult.Refused(GitOutcome.OutsideRoot, shown);
            }
        }

        var inTheWay = touched.Added.Where(p => InTheWay(location.WorkTree, p)).Select(p => SandboxPath(location, p)).ToList();
        if (inTheWay.Count > 0)
        {
            return GitRevertResult.Refused(GitOutcome.InTheWay, FileText.Names(inTheWay));
        }

        var assumed = touched.Paths.Where(p => repo.Index[p]?.AssumeUnchanged == true).Select(p => SandboxPath(location, p)).ToList();
        if (assumed.Count > 0)
        {
            return GitRevertResult.Refused(GitOutcome.AssumeUnchanged, FileText.Names(assumed));
        }

        var globbed = touched.Paths.Where(p => !GlobFree(p)).Select(p => SandboxPath(location, p)).ToList();
        return globbed.Count > 0 ? GitRevertResult.Refused(GitOutcome.GlobPath, FileText.Names(globbed)) : null;
    }

    /// <summary>
    /// Whether something on disk is in the way of a file the revert adds at <paramref name="repoPath"/> (2026-11-15, the tool-arguments
    /// review's #8, #20): a file or a folder at the path, or a file at one of its parent folders. Untracked and ignored alike: libgit2 would
    /// overwrite an ignored file. <see cref="File.Exists(string)"/> matches case as the disk does.
    /// </summary>
    private static bool InTheWay(string workTree, string repoPath)
    {
        string full = Path.Combine(workTree, repoPath.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(full) || Directory.Exists(full))
        {
            return true;
        }

        for (string? parent = Path.GetDirectoryName(full); parent is not null && Path.TrimEndingDirectorySeparator(parent).Length > workTree.Length; parent = Path.GetDirectoryName(parent))
        {
            if (File.Exists(parent))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether <paramref name="repoPath"/> holds none of <c>*</c>, <c>?</c> or <c>[</c> (2026-11-15). LibGit2Sharp's <c>CheckoutPaths</c> passes
    /// paths as pathspecs, so libgit2 reads them as globs: <c>[1].txt</c> would miss itself and match <c>1.txt</c>. No escape helps (a
    /// backslash becomes a slash on Windows), so such a path is refused; revert and discard both ask it.
    /// </summary>
    internal static bool GlobFree(string repoPath) => repoPath.IndexOfAny(GlobCharacters) < 0;

    private static readonly char[] GlobCharacters = ['*', '?', '['];

    /// <summary>
    /// Files a revert deleted that the checkout left on disk, removed by hand (2026-11-15): the file deleted, then staged so the index drops it.
    /// Safe because the revert refused a tree with any tracked change.
    /// </summary>
    private static void RemoveByHand(Repository repo, string workTree, IReadOnlySet<string> removed)
    {
        var left = new List<string>();
        foreach (string path in removed)
        {
            string full = Path.Combine(workTree, path.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(full))
            {
                File.Delete(full);
            }

            if (repo.Index[path] is not null)
            {
                left.Add(path);
            }
        }

        if (left.Count > 0)
        {
            Commands.Stage(repo, left);
        }
    }

    /// <summary>
    /// Puts back what a failed revert wrote (2026-11-15, the one put-back left once the revert ran in memory): the touched paths HEAD has,
    /// checked out from it by force (the clean check means force only overwrites the revert's own writes), and those HEAD lacks, deleted and
    /// dropped from the index. Each part tried even when one fails. The paths still changed afterwards, by status, so the answer can name them.
    /// </summary>
    private static List<string> PutBackPaths(Repository repo, string workTree, Commit head, IReadOnlyList<string> touched)
    {
        var inHead = touched.Where(p => head.Tree[p] is not null).ToList();
        var added = touched.Where(p => head.Tree[p] is null).ToList();
        try
        {
            if (inHead.Count > 0)
            {
                repo.CheckoutPaths(head.Sha, inHead, new CheckoutOptions { CheckoutModifiers = CheckoutModifiers.Force });
            }
        }
        catch (Exception ex) when (ex is LibGit2SharpException or IOException or UnauthorizedAccessException)
        {
            // The files left are named below.
        }

        try
        {
            RemoveByHand(repo, workTree, added.ToHashSet(StringComparer.Ordinal));
        }
        catch (Exception ex) when (ex is LibGit2SharpException or IOException or UnauthorizedAccessException)
        {
            // The files left are named below.
        }

        try
        {
            return touched.Where(p => repo.RetrieveStatus(p) is not (FileStatus.Unaltered or FileStatus.Nonexistent)).ToList();
        }
        catch (Exception ex) when (ex is LibGit2SharpException or IOException or UnauthorizedAccessException)
        {
            return [.. touched];
        }
    }

    /// <summary>
    /// The paths a revert's tree changes against HEAD's (2026-11-15, the tool-arguments review's #77, #117), repository-relative, with no count
    /// cap: what the disk actually changes. The added and removed among them apart, a changed submodule pointer named (the checkout wouldn't
    /// move it), and <see cref="TouchedSet.Cut"/> when the walk passed <paramref name="limit"/> entries, since a partial list would leave
    /// files that disagree with the commit. The entry-id comparison is <see cref="Walk"/>'s (<see cref="Unchanged"/>).
    /// </summary>
    private static TouchedSet TouchedPaths(Tree from, Tree to, int limit)
    {
        var paths = new List<string>();
        var added = new HashSet<string>(StringComparer.Ordinal);
        var removed = new HashSet<string>(StringComparer.Ordinal);
        string? gitlink = null;
        int visited = 0;
        bool cut = false;
        Visit(from, to, "");
        return new TouchedSet(paths, added, removed, gitlink, cut);

        void Visit(Tree? before, Tree? after, string prefix)
        {
            var names = new SortedSet<string>(StringComparer.Ordinal);
            var b = new Dictionary<string, TreeEntry>(StringComparer.Ordinal);
            var a = new Dictionary<string, TreeEntry>(StringComparer.Ordinal);
            foreach (var entry in before ?? Enumerable.Empty<TreeEntry>())
            {
                b[entry.Name] = entry;
                names.Add(entry.Name);
            }

            foreach (var entry in after ?? Enumerable.Empty<TreeEntry>())
            {
                a[entry.Name] = entry;
                names.Add(entry.Name);
            }

            foreach (string name in names)
            {
                if (cut || gitlink is not null)
                {
                    return;
                }

                if (++visited > limit)
                {
                    cut = true;
                    return;
                }

                b.TryGetValue(name, out var x);
                a.TryGetValue(name, out var y);
                if (Unchanged(x, y))
                {
                    continue;
                }

                string path = prefix + name;
                if (x?.TargetType == TreeEntryTargetType.GitLink || y?.TargetType == TreeEntryTargetType.GitLink)
                {
                    gitlink = path;
                    return;
                }

                bool xTree = x?.TargetType == TreeEntryTargetType.Tree, yTree = y?.TargetType == TreeEntryTargetType.Tree;
                if ((x is null || xTree) && (y is null || yTree))
                {
                    Visit(x?.Target as Tree, y?.Target as Tree, path + "/");
                    continue;
                }

                if (xTree)
                {
                    Visit((Tree)x!.Target, null, path + "/");
                }

                if (yTree)
                {
                    Visit(null, (Tree)y!.Target, path + "/");
                }

                bool xBlob = x?.TargetType == TreeEntryTargetType.Blob, yBlob = y?.TargetType == TreeEntryTargetType.Blob;
                if (xBlob || yBlob)
                {
                    paths.Add(path);
                    if (!xBlob)
                    {
                        added.Add(path);
                    }

                    if (!yBlob)
                    {
                        removed.Add(path);
                    }
                }
            }
        }
    }

    /// <summary>A revert's touched paths (<see cref="TouchedPaths"/>).</summary>
    private sealed record TouchedSet(IReadOnlyList<string> Paths, IReadOnlySet<string> Added, IReadOnlySet<string> Removed, string? Gitlink, bool Cut);

    /// <summary>Two tree entries that are one: the same object and mode (<see cref="Walk"/>, <see cref="TouchedPaths"/>).</summary>
    private static bool Unchanged(TreeEntry? before, TreeEntry? after) =>
        before is not null && after is not null && before.Target.Id == after.Target.Id && before.Mode == after.Mode;

    /// <summary>
    /// Whether the repository holds a staged change or a changed tracked file (2026-11-15, the tool-arguments review's #57, #63): the revert's
    /// own status read, untracked files and rename detection off, submodules out as <see cref="StatusEntries"/> has them, stopping at the first.
    /// </summary>
    private static bool HasTrackedChange(Repository repo) =>
        repo.RetrieveStatus(new StatusOptions
        {
            IncludeUntracked = false,
            IncludeIgnored = false,
            ExcludeSubmodules = true,
            DetectRenamesInIndex = false,
            DetectRenamesInWorkDir = false,
        }).Any(e => IsTrackedChange(e.State));

    /// <summary>A status entry that is a staged change or a changed tracked file: the letters <c>gitlib_status</c> shows (#57: one helper).</summary>
    private static bool IsTrackedChange(FileStatus state) => IndexCode(state) is not null || WorkdirCode(state) is not null;

    /// <summary>
    /// The identity and message a commit the app makes is signed with (2026-11-15, the tool-arguments review's #140: shared by
    /// <see cref="Commit"/> and <see cref="Revert(string, string, string)"/>): <paramref name="message"/> tidied (<see cref="Prettify"/>), or
    /// <paramref name="fallback"/> when that leaves nothing, and <see cref="Identity"/>.
    /// </summary>
    private GitOutcome Sign(Repository repo, string message, string fallback, out Signature? signature, out string text)
    {
        string pretty = Prettify(message);
        text = pretty.Length > 0 ? pretty : fallback;
        return Identity(repo, out signature);
    }

    /// <summary>
    /// A tag <paramref name="name"/> on <paramref name="reference"/> (blank = HEAD; 2026-11-15, feature idea #32): lightweight, or annotated with
    /// <paramref name="message"/> (blank once tidied counts as none), <see cref="Identity"/> as its tagger. Never moves an existing tag, and never
    /// takes a local branch's name: libgit2 resolves a short name as a tag before a branch, so a tag <c>main</c> would take over <c>ref: main</c>
    /// in every git tool. An annotated tag's object is written as bytes (<see cref="TagObject"/>): LibGit2Sharp's tag APIs run the message through
    /// <c>git_message_prettify</c>, the GitBuf call that comes back empty under NativeAOT.
    /// </summary>
    public GitTagResult CreateTag(string relative, string name, string reference, string message) => Run(relative, GitTagResult.Refused, (repo, location, full) =>
    {
        name = name.Trim();
        var found = TryCommit(repo, reference, out var commit, out string detail);
        if (found != GitOutcome.Ok)
        {
            return GitTagResult.Refused(found, detail);
        }

        var named = CheckNewName(repo, name, tag: true, out string nameDetail);
        if (named != GitOutcome.Ok)
        {
            return GitTagResult.Refused(named, nameDetail);
        }

        string pretty = Prettify(message);
        // A NUL or a line git would take for a signature's start (2026-11-15, the tool-arguments review's #135): git cuts a tag's message at
        // the last such line anywhere in it.
        if (pretty.Contains('\0', StringComparison.Ordinal))
        {
            return GitTagResult.Refused(GitOutcome.BadMessage);
        }

        if (pretty.Split('\n').FirstOrDefault(line => SignatureMarkers.Any(marker => line.StartsWith(marker, StringComparison.Ordinal))) is { } signatureLine)
        {
            return GitTagResult.Refused(GitOutcome.BadMessage, signatureLine);
        }

        try
        {
            if (pretty.Length == 0)
            {
                repo.Tags.Add(name, commit!);
            }
            else
            {
                var identity = Identity(repo, out var tagger);
                if (identity != GitOutcome.Ok)
                {
                    return GitTagResult.Refused(identity);
                }

                // A line break or NUL in the tagger would write a second header line into the tag object (#97).
                if (tagger!.Name.IndexOfAny(HeaderBreakers) >= 0 || tagger.Email.IndexOfAny(HeaderBreakers) >= 0)
                {
                    return GitTagResult.Refused(GitOutcome.BadTagger);
                }

                var id = repo.ObjectDatabase.Write<TagAnnotation>(TagObject(commit!.Sha, name, tagger!, pretty));
                repo.Refs.Add("refs/tags/" + name, id);
            }
        }
        catch (NameConflictException)
        {
            return GitTagResult.Refused(GitOutcome.NameConflict, name);
        }

        DiagnosticLog.Debug(Category, TaggedLogLine(name, Short(commit!), pretty.Length > 0));
        return new GitTagResult(GitOutcome.Ok, "", name, Short(commit!), pretty.Length > 0);
    });

    /// <summary>The four lines git takes for a signature's start in a tag's message (<c>gpg-interface.c</c>'s <c>parse_signed_buffer</c>, read 2026-11-15).</summary>
    private static readonly string[] SignatureMarkers =
    [
        "-----BEGIN PGP SIGNATURE-----",
        "-----BEGIN PGP MESSAGE-----",
        "-----BEGIN SIGNED MESSAGE-----",
        "-----BEGIN SSH SIGNATURE-----",
    ];

    private static readonly char[] HeaderBreakers = ['\r', '\n', '\0'];

    /// <summary>
    /// An annotated tag object's bytes, as git writes one: <c>object</c>, <c>type commit</c>, <c>tag</c>, <c>tagger name &lt;email&gt; seconds ±hhmm</c>,
    /// a blank line, the message (already tidied). Pure.
    /// </summary>
    public static byte[] TagObject(string sha, string name, Signature tagger, string message)
    {
        ArgumentNullException.ThrowIfNull(tagger);
        var offset = tagger.When.Offset;
        string zone = (offset < TimeSpan.Zero ? "-" : "+") + offset.Duration().ToString("hhmm", CultureInfo.InvariantCulture);
        string text = "object " + sha + "\ntype commit\ntag " + name + "\ntagger " + tagger.Name + " <" + tagger.Email + "> "
            + tagger.When.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) + " " + zone + "\n\n" + message;
        return System.Text.Encoding.UTF8.GetBytes(text);
    }

    /// <summary>Checks the local branch <paramref name="name"/> out, never over local changes.</summary>
    public GitBranchResult SwitchBranch(string relative, string name) => Run(relative, GitBranchResult.Refused, (repo, location, full) =>
    {
        name = name.Trim();
        var branch = repo.Branches[name];
        if (branch is null || branch.IsRemote)
        {
            return GitBranchResult.Refused(GitOutcome.RefNotFound, name);
        }

        if (branch.Tip is null)
        {
            return GitBranchResult.Refused(GitOutcome.Unborn, name);
        }

        var switched = Switch(repo, branch);
        return switched == GitOutcome.Ok
            ? new GitBranchResult(GitOutcome.Ok, "", name, Short(branch.Tip))
            : GitBranchResult.Refused(switched, name);
    });

    /// <summary>Renames the local branch <paramref name="name"/> to <paramref name="newName"/>.</summary>
    public GitBranchResult RenameBranch(string relative, string name, string newName) => Run(relative, GitBranchResult.Refused, (repo, location, full) =>
    {
        name = name.Trim();
        newName = newName.Trim();
        var branch = repo.Branches[name];
        if (branch is null || branch.IsRemote)
        {
            return GitBranchResult.Refused(GitOutcome.RefNotFound, name);
        }

        // A rename makes a name too (#4): the creators' check.
        var named = CheckNewName(repo, newName, tag: false, out string nameDetail);
        if (named != GitOutcome.Ok)
        {
            return GitBranchResult.Refused(named, nameDetail);
        }

        try
        {
            var renamed = repo.Branches.Rename(branch, newName);
            DiagnosticLog.Debug(Category, BranchRenamedLogLine(name, newName));
            return new GitBranchResult(GitOutcome.Ok, "", newName, Short(renamed.Tip), name);
        }
        catch (NameConflictException)
        {
            return GitBranchResult.Refused(GitOutcome.NameConflict, newName);
        }
    });

    /// <summary>
    /// Stages (or, with <paramref name="unstage"/>, unstages) <paramref name="paths"/> — sandbox-relative, or
    /// <c>.</c> for everything changed under <paramref name="relative"/>; a path that is neither in the tree nor in
    /// the index is <see cref="GitOutcome.Missing"/>. (The sandbox's <c>.trash</c> was never staged until 2026-10-01, when
    /// File safe edits and its <c>.trash</c> went, the user's call: a folder of that name is a folder like any other.)
    /// </summary>
    public GitStageResult Stage(string relative, IReadOnlyList<string> paths, bool unstage)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return Run(relative, GitStageResult.Refused, (repo, location, full) =>
        {
            if (paths.Count > MaxPathsPerCall)
            {
                return GitStageResult.Refused(GitOutcome.TooManyPaths);
            }

            if (RepoPath(location, full) is not { } prefix)
            {
                return GitStageResult.Refused(GitOutcome.NotInRepository, _files.Relative(full));
            }

            var repoPaths = new List<string>();
            foreach (var path in paths)
            {
                string text = path.Trim();
                string? under = null;
                if (text.Length == 0 || text == ".")
                {
                    under = prefix;
                }
                else
                {
                    // A read (2026-10-22): staging writes the index, never the file, so the write guard has no say (git add AGENTS.md).
                    if (_files.Resolve(text, forWrite: false, out string each) != FileOutcome.Ok)
                    {
                        return GitStageResult.Refused(GitOutcome.OutsideRoot, text);
                    }

                    if (RepoPath(location, each) is not { } repoPath)
                    {
                        return GitStageResult.Refused(GitOutcome.NotInRepository, text);
                    }

                    if (repoPath.Length == 0)
                    {
                        under = "";   // the working tree itself: everything in it
                    }
                    else if (!repoPaths.Contains(repoPath, StringComparer.Ordinal))
                    {
                        // libgit2 stages a name it cannot find silently; a path neither on disk nor in the index is a mistake.
                        if (!File.Exists(each) && !Directory.Exists(each) && repo.Index[repoPath] is null && !repo.Index.Any(i => i.Path.StartsWith(repoPath + "/", StringComparison.Ordinal)))
                        {
                            return GitStageResult.Refused(GitOutcome.Missing, text);
                        }

                        repoPaths.Add(repoPath);
                    }
                }

                if (under is null)
                {
                    continue;
                }

                // "." (or the tree itself): every changed path under the folder, one by one.
                foreach (var entry in StatusEntries(repo, location, under, recurseUntracked: true))
                {
                    bool wanted = unstage ? IndexCode(entry.State) is not null : entry.State.HasFlag(FileStatus.NewInWorkdir) || WorkdirCode(entry.State) is not null;
                    if (wanted && !repoPaths.Contains(entry.FilePath, StringComparer.Ordinal))
                    {
                        repoPaths.Add(entry.FilePath);
                    }
                }
            }

            string folder = _files.Relative(full, isDirectory: true);
            if (repoPaths.Count == 0)
            {
                return new GitStageResult(GitOutcome.Ok, "", [], folder, unstage);
            }

            try
            {
                if (unstage)
                {
                    Commands.Unstage(repo, repoPaths);
                }
                else
                {
                    Commands.Stage(repo, repoPaths);
                }
            }
            catch (UnmatchedPathException ex)
            {
                return GitStageResult.Refused(GitOutcome.Missing, LogText.Excerpt(ex.Message));
            }

            DiagnosticLog.Debug(Category, StagedLogLine(repoPaths.Count, unstage));
            return new GitStageResult(GitOutcome.Ok, "", repoPaths.Select(p => SandboxPath(location, p)).ToList(), folder, unstage);
        });
    }

    /// <summary>A commit of the index with <paramref name="message"/>, signed from git config; <paramref name="amend"/> rewrites the last one.</summary>
    public GitCommitResult Commit(string relative, string message, bool amend, bool allowEmpty) => Run(relative, GitCommitResult.Refused, (repo, location, full) =>
    {
        if (repo.Index.Conflicts.Any())
        {
            return GitCommitResult.Refused(GitOutcome.Conflicts);
        }

        if (amend && repo.Info.IsHeadUnborn)
        {
            return GitCommitResult.Refused(GitOutcome.NothingToAmend);
        }

        // libgit2 refuses an empty commit only against a parent's tree: an unborn repository would get an empty root commit.
        if (!amend && !allowEmpty && !StatusEntries(repo, location, "", recurseUntracked: false).Any(e => IndexCode(e.State) is not null))
        {
            return GitCommitResult.Refused(GitOutcome.NothingStaged);
        }

        var identity = Sign(repo, message, "", out var signature, out string text);
        if (identity != GitOutcome.Ok)
        {
            return GitCommitResult.Refused(identity);
        }

        Commit commit;
        try
        {
            // PrettifyMessage would go through git_message_prettify's GitBuf (empty on the published exe): the tidying is Prettify's.
            commit = repo.Commit(text, signature!, signature!, new CommitOptions { AmendPreviousCommit = amend, AllowEmptyCommit = allowEmpty, PrettifyMessage = false });
        }
        catch (EmptyCommitException)
        {
            return GitCommitResult.Refused(GitOutcome.NothingStaged);
        }
        catch (UnbornBranchException)
        {
            return GitCommitResult.Refused(GitOutcome.NothingToAmend);
        }

        var (changes, _) = Compare(repo, location, commit.Parents.FirstOrDefault()?.Tree, commit.Tree, "");
        DiagnosticLog.Debug(Category, CommittedLogLine(Short(commit), BranchName(repo), amend));
        return new GitCommitResult(GitOutcome.Ok, "", Short(commit), BranchName(repo), commit.MessageShort, changes.Count, changes.Sum(c => c.Change.Added), changes.Sum(c => c.Change.Deleted), amend);
    });

    /// <summary>The stashes, newest first (<c>stash@{0}</c>), capped.</summary>
    public GitStashResult Stashes(string relative) => Run(relative, GitStashResult.Refused, (repo, location, full) =>
    {
        var (list, truncated) = StashList(repo);
        return new GitStashResult(GitOutcome.Ok, "", -1, "", 0, list, truncated);
    });

    /// <summary>Saves the working tree's changes (and, with <paramref name="includeUntracked"/>, the untracked files) as <c>stash@{0}</c> and restores HEAD.</summary>
    public GitStashResult StashPush(string relative, string message, bool includeUntracked) => Run(relative, GitStashResult.Refused, (repo, location, full) =>
    {
        if (repo.Info.IsHeadUnborn)
        {
            return GitStashResult.Refused(GitOutcome.Unborn);
        }

        var identity = Identity(repo, out var signature);
        if (identity != GitOutcome.Ok)
        {
            return GitStashResult.Refused(identity);
        }

        var stash = repo.Stashes.Add(signature!, string.IsNullOrWhiteSpace(message) ? null : message.Trim(), includeUntracked ? StashModifiers.IncludeUntracked : StashModifiers.Default);
        if (stash is null)
        {
            return GitStashResult.Refused(GitOutcome.NothingToStash);
        }

        var (changes, _) = Compare(repo, location, stash.Base.Tree, stash.WorkTree.Tree, "");
        int files = changes.Count + (stash.Untracked?.Tree.Count ?? 0);
        DiagnosticLog.Debug(Category, StashedLogLine(files));
        var (list, truncated) = StashList(repo);
        return new GitStashResult(GitOutcome.Ok, "", 0, StashMessage(stash), files, list, truncated);
    });

    /// <summary>Applies <c>stash@{index}</c> to the working tree — and drops it when <paramref name="pop"/>, unless it conflicts (then it is kept).</summary>
    public GitStashResult StashApply(string relative, int index, bool pop) => Run(relative, GitStashResult.Refused, (repo, location, full) =>
    {
        var stash = repo.Stashes.ElementAtOrDefault(index);
        if (index < 0 || stash is null)
        {
            return GitStashResult.Refused(GitOutcome.NoStash, index.ToString(CultureInfo.InvariantCulture));
        }

        string message = StashMessage(stash);
        var (changes, _) = Compare(repo, location, stash.Base.Tree, stash.WorkTree.Tree, "");
        int files = changes.Count + (stash.Untracked?.Tree.Count ?? 0);
        var options = new StashApplyOptions { ApplyModifiers = StashApplyModifiers.ReinstateIndex };
        StashApplyStatus status;
        try
        {
            status = pop ? repo.Stashes.Pop(index, options) : repo.Stashes.Apply(index, options);
        }
        catch (CheckoutConflictException)
        {
            return GitStashResult.Refused(GitOutcome.CheckoutConflict, message);
        }

        switch (status)
        {
            case StashApplyStatus.Applied:
                DiagnosticLog.Debug(Category, StashAppliedLogLine(index, pop));
                var (list, truncated) = StashList(repo);
                return new GitStashResult(GitOutcome.Ok, "", index, message, files, list, truncated);
            case StashApplyStatus.Conflicts:
                return GitStashResult.Refused(GitOutcome.Conflicts, message);
            case StashApplyStatus.UncommittedChanges:
                return GitStashResult.Refused(GitOutcome.CheckoutConflict, message);
            default:
                return GitStashResult.Refused(GitOutcome.NoStash, index.ToString(CultureInfo.InvariantCulture));
        }
    });

    /// <summary>
    /// Throws away local changes: with <paramref name="paths"/>, those paths put back as they are at
    /// <paramref name="reference"/> (blank = HEAD), index and working tree; without, the whole tree reset
    /// hard to it (untracked files kept). Logged at Info: it is the one git act that loses work.
    /// </summary>
    public GitDiscardResult Discard(string relative, IReadOnlyList<string> paths, string reference)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return Run(relative, GitDiscardResult.Refused, (repo, location, full) =>
        {
            if (paths.Count > MaxPathsPerCall)
            {
                return GitDiscardResult.Refused(GitOutcome.TooManyPaths);
            }

            var found = TryCommit(repo, reference, out var commit, out string detail);
            if (found != GitOutcome.Ok)
            {
                return GitDiscardResult.Refused(found, detail);
            }

            string shownReference = ReferenceName(repo, reference);
            if (paths.Count == 0)
            {
                int files = StatusEntries(repo, location, "", recurseUntracked: false).Count(e => IsTrackedChange(e.State));
                repo.Reset(ResetMode.Hard, commit!);
                DiagnosticLog.Info(Category, DiscardedLogLine(null, shownReference, Short(commit!)));
                return new GitDiscardResult(GitOutcome.Ok, "", [], shownReference, Short(commit!), commit!.MessageShort, files);
            }

            var repoPaths = new List<string>();
            foreach (var path in paths)
            {
                string text = path.Trim();
                string? under = null;
                if (text.Length == 0 || text == ".")
                {
                    under = RepoPath(location, full) ?? "";
                }
                else
                {
                    // A write: a discard rewrites the file, so the write guard's refusal is said in its own words (2026-10-22).
                    var resolved = _files.Resolve(text, forWrite: true, out string each);
                    if (resolved != FileOutcome.Ok)
                    {
                        return WriteGuard.IsRefusal(resolved)
                            ? GitDiscardResult.Refused(GitOutcome.Guarded, FileText.Error(resolved, text, "restore"))
                            : GitDiscardResult.Refused(GitOutcome.OutsideRoot, text);
                    }

                    if (RepoPath(location, each) is not { } repoPath)
                    {
                        return GitDiscardResult.Refused(GitOutcome.NotInRepository, text);
                    }

                    if (repoPath.Length == 0)
                    {
                        under = "";
                    }
                    else
                    {
                        // A named path with a glob character (2026-11-15, beyond the review: pass 24): under Force, [x].txt would also
                        // throw away x.txt's changes. A "." expansion isn't asked: every path it names is being discarded anyway.
                        if (!GlobFree(repoPath))
                        {
                            return GitDiscardResult.Refused(GitOutcome.GlobPath, text);
                        }

                        if (commit![repoPath] is null)
                        {
                            return GitDiscardResult.Refused(GitOutcome.Missing, text);
                        }

                        if (!repoPaths.Contains(repoPath, StringComparer.Ordinal))
                        {
                            repoPaths.Add(repoPath);
                        }
                    }
                }

                if (under is null)
                {
                    continue;
                }

                // "." (or the tree itself): every tracked path changed under the folder, so an untracked file is never touched.
                foreach (var entry in StatusEntries(repo, location, under, recurseUntracked: false))
                {
                    if (IsTrackedChange(entry.State) && commit![entry.FilePath] is not null && !repoPaths.Contains(entry.FilePath, StringComparer.Ordinal))
                    {
                        repoPaths.Add(entry.FilePath);
                    }
                }
            }

            if (repoPaths.Count == 0)
            {
                return new GitDiscardResult(GitOutcome.Ok, "", [], shownReference, Short(commit!), commit!.MessageShort, 0);
            }

            int changed = StatusEntries(repo, location, "", recurseUntracked: false)
                .Count(e => IsTrackedChange(e.State) && repoPaths.Any(p => e.FilePath == p || e.FilePath.StartsWith(p + "/", StringComparison.Ordinal)));
            repo.CheckoutPaths(commit!.Sha, repoPaths, new CheckoutOptions { CheckoutModifiers = CheckoutModifiers.Force });
            var shown = repoPaths.Select(p => SandboxPath(location, p)).ToList();
            DiagnosticLog.Info(Category, DiscardedLogLine(shown.Count, shownReference, Short(commit)));
            return new GitDiscardResult(GitOutcome.Ok, "", shown, shownReference, Short(commit), commit.MessageShort, changed);
        });
    }

    /// <summary>
    /// <c>/gituser [force]</c> (2026-09-21): <c>user.email</c> and <c>user.name</c> written into the
    /// repository at the working directory's root — its <c>.git/config</c>, <see cref="ConfigurationLevel.Local"/>,
    /// never the global file. Either key already set there and no <paramref name="force"/> is left as it
    /// is (<c>Written</c> false, the values found carried back for the notice); <paramref name="force"/>
    /// replaces both. The frame's refusals as ever: no repository at or above the root is
    /// <see cref="GitOutcome.NoRepository"/>. Logged at Info.
    /// </summary>
    public GitIdentityResult SetLocalIdentity(string email, string name, bool force) => Run("", GitIdentityResult.Refused, (repo, location, full) =>
    {
        ArgumentNullException.ThrowIfNull(email);
        ArgumentNullException.ThrowIfNull(name);
        string? haveEmail = repo.Config.Get<string>("user.email", ConfigurationLevel.Local)?.Value;
        string? haveName = repo.Config.Get<string>("user.name", ConfigurationLevel.Local)?.Value;
        if (!force && (haveEmail is not null || haveName is not null))
        {
            return new GitIdentityResult(GitOutcome.Ok, "", haveEmail ?? "", haveName ?? "", Written: false);
        }

        repo.Config.Set("user.email", email, ConfigurationLevel.Local);
        repo.Config.Set("user.name", name, ConfigurationLevel.Local);
        DiagnosticLog.Info(Category, IdentitySetLogLine(name, email));
        return new GitIdentityResult(GitOutcome.Ok, "", email, name, Written: true);
    });

    /// <summary>Removes a local branch (never the one checked out), a tag, or <c>stash@{index}</c>. Logged at Info.</summary>
    public GitDeleteResult Delete(string relative, GitDeleteKind kind, string name, int index) => Run(relative, (o, d) => GitDeleteResult.Refused(kind, o, d), (repo, location, full) =>
    {
        name = name.Trim();
        switch (kind)
        {
            case GitDeleteKind.Branch:
            {
                var branch = repo.Branches[name];
                if (branch is null || branch.IsRemote)
                {
                    return GitDeleteResult.Refused(kind, GitOutcome.RefNotFound, name);
                }

                if (branch.IsCurrentRepositoryHead)
                {
                    return GitDeleteResult.Refused(kind, GitOutcome.CurrentBranch, name);
                }

                string sha = Short(branch.Tip);
                repo.Branches.Remove(branch);
                DiagnosticLog.Info(Category, DeletedLogLine(kind, name, sha));
                return new GitDeleteResult(GitOutcome.Ok, "", kind, name, sha, "");
            }

            case GitDeleteKind.Tag:
            {
                var tag = repo.Tags[name];
                if (tag is null)
                {
                    return GitDeleteResult.Refused(kind, GitOutcome.RefNotFound, name);
                }

                string sha = tag.PeeledTarget is Commit c ? Short(c) : tag.Target.Sha[..ShortShaLength];
                repo.Tags.Remove(tag);
                DiagnosticLog.Info(Category, DeletedLogLine(kind, name, sha));
                return new GitDeleteResult(GitOutcome.Ok, "", kind, name, sha, "");
            }

            default:
            {
                var stash = repo.Stashes.ElementAtOrDefault(index);
                if (index < 0 || stash is null)
                {
                    return GitDeleteResult.Refused(kind, GitOutcome.NoStash, index.ToString(CultureInfo.InvariantCulture));
                }

                string message = StashMessage(stash);
                string sha = Short(stash.WorkTree);
                string label = StashName(index);
                repo.Stashes.Remove(index);
                DiagnosticLog.Info(Category, DeletedLogLine(kind, label, sha));
                return new GitDeleteResult(GitOutcome.Ok, "", kind, label, sha, message);
            }
        }
    });

    // ---- the pieces ----

    /// <summary><c>stash@{n}</c>.</summary>
    public static string StashName(int index) => "stash@{" + index.ToString(CultureInfo.InvariantCulture) + "}";

    /// <summary>git's message tidying (<c>git_message_prettify</c> without the comment stripping): LF line ends, trailing whitespace off each line, runs of blank lines folded to one, no leading blanks, one newline at the end.</summary>
    public static string Prettify(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var lines = new List<string>();
        foreach (var raw in WorkingDirectory.NormalizeNewlines(message).Split('\n'))
        {
            string line = raw.TrimEnd();
            if (line.Length == 0 && (lines.Count == 0 || lines[^1].Length == 0))
            {
                continue;
            }

            lines.Add(line);
        }

        while (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines.Count == 0 ? "" : string.Join("\n", lines) + "\n";
    }

    /// <summary>The remote-tracking branch a local branch follows, with how far apart the two are.</summary>
    public sealed record UpstreamInfo(string Name, int? Ahead, int? Behind);

    /// <summary>
    /// The upstream of <paramref name="branch"/> from the repository's config (<c>branch.&lt;name&gt;.remote</c> +
    /// <c>branch.&lt;name&gt;.merge</c> → <c>refs/remotes/&lt;remote&gt;/&lt;branch&gt;</c>), the counts through
    /// <c>CalculateHistoryDivergence</c>; null without one. Never <c>Branch.TrackedBranch</c> (GitBuf, see the class note).
    /// </summary>
    public static UpstreamInfo? Upstream(Repository repo, Branch branch)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(branch);
        if (branch.IsRemote || branch.Tip is null)
        {
            return null;
        }

        string remote = repo.Config.Get<string>("branch." + branch.FriendlyName + ".remote")?.Value ?? "";
        string merge = repo.Config.Get<string>("branch." + branch.FriendlyName + ".merge")?.Value ?? "";
        const string heads = "refs/heads/";
        if (remote.Length == 0 || !merge.StartsWith(heads, StringComparison.Ordinal))
        {
            return null;
        }

        string name = remote == "." ? merge[heads.Length..] : remote + "/" + merge[heads.Length..];
        var tracked = repo.Branches[remote == "." ? name : "refs/remotes/" + name];
        if (tracked?.Tip is null)
        {
            return new UpstreamInfo(name, null, null);
        }

        var divergence = repo.ObjectDatabase.CalculateHistoryDivergence(branch.Tip, tracked.Tip);
        return new UpstreamInfo(name, divergence.AheadBy, divergence.BehindBy);
    }

    /// <summary>The committer git config names, stamped now; <see cref="GitOutcome.NoIdentity"/> when <c>user.name</c> / <c>user.email</c> are not set anywhere.</summary>
    public GitOutcome Identity(Repository repo, out Signature? signature)
    {
        ArgumentNullException.ThrowIfNull(repo);
        signature = null;
        try
        {
            // Null when user.name / user.email are set nowhere (0.32 returns rather than throws).
            signature = repo.Config.BuildSignature(_time.GetLocalNow());
            return signature is null ? GitOutcome.NoIdentity : GitOutcome.Ok;
        }
        catch (LibGit2SharpException)
        {
            return GitOutcome.NoIdentity;
        }
    }

    /// <summary>The commit <paramref name="reference"/> names (blank = HEAD; a tag is peeled), or why not.</summary>
    public static GitOutcome TryCommit(Repository repo, string reference, out Commit? commit, out string detail)
    {
        ArgumentNullException.ThrowIfNull(repo);
        commit = null;
        detail = "";
        string spec = (reference ?? "").Trim();
        if (spec.Length == 0 || spec.Equals(Head, StringComparison.OrdinalIgnoreCase))
        {
            commit = repo.Head.Tip;
            return commit is null ? GitOutcome.Unborn : GitOutcome.Ok;
        }

        detail = spec;
        GitObject? found;
        try
        {
            found = repo.Lookup(spec);
        }
        catch (AmbiguousSpecificationException)
        {
            return GitOutcome.RefAmbiguous;
        }
        catch (LibGit2SharpException)
        {
            return GitOutcome.RefNotFound;
        }

        while (found is TagAnnotation tag)
        {
            found = tag.Target;
        }

        if (found is not Commit c)
        {
            return GitOutcome.RefNotFound;
        }

        commit = c;
        detail = "";
        return GitOutcome.Ok;
    }

    /// <summary>The first <see cref="ShortShaLength"/> characters of a commit's sha; <c>""</c> for none.</summary>
    public static string Short(Commit? commit) => commit is null ? "" : commit.Sha[..ShortShaLength];

    /// <summary>
    /// Whether <paramref name="commit"/> passes <paramref name="filter"/> (2026-11-15): its committer date within the window, both ends inclusive
    /// (the tool-arguments review's #28: <c>git log --since</c> filters by commit date, and shows the author date, as the rows do), the author's
    /// name or email holding <see cref="GitLogFilter.Author"/> and the message <see cref="GitLogFilter.Grep"/>, case ignored.
    /// </summary>
    private static bool Keeps(Commit commit, GitLogFilter filter)
    {
        var when = commit.Committer.When;
        return (filter.Since is not { } since || when >= since)
            && (filter.Until is not { } until || when <= until)
            && (filter.Author is not { } author
                || commit.Author.Name.Contains(author, StringComparison.OrdinalIgnoreCase)
                || commit.Author.Email.Contains(author, StringComparison.OrdinalIgnoreCase))
            && (filter.Grep is not { } grep || commit.Message.Contains(grep, StringComparison.OrdinalIgnoreCase));
    }

    private static GitCommitInfo Info(Commit commit) =>
        new(commit.Sha, Short(commit), commit.Author.Name, commit.Author.When, commit.MessageShort, commit.Message);

    /// <summary>The branch HEAD is on: its name, <c>(no branch)</c> detached, the unborn branch's name after <c>git init</c>.</summary>
    private static string BranchName(Repository repo) => repo.Info.IsHeadDetached ? "(no branch)" : repo.Head.FriendlyName;

    /// <summary>How a ref reads in a result: the branch for a bare HEAD, the ref as typed otherwise.</summary>
    private static string ReferenceName(Repository repo, string reference)
    {
        string spec = (reference ?? "").Trim();
        return spec.Length == 0 || spec.Equals(Head, StringComparison.OrdinalIgnoreCase)
            ? (repo.Info.IsHeadDetached ? Head : repo.Head.FriendlyName)
            : spec;
    }

    private static string StashMessage(Stash stash) => (stash.Message ?? "").Trim();

    private GitOutcome Switch(Repository repo, Branch branch)
    {
        if (repo.Index.Conflicts.Any())
        {
            return GitOutcome.Conflicts;
        }

        try
        {
            Commands.Checkout(repo, branch);
            DiagnosticLog.Debug(Category, SwitchedLogLine(branch.FriendlyName));
            return GitOutcome.Ok;
        }
        catch (CheckoutConflictException)
        {
            return GitOutcome.CheckoutConflict;
        }
    }

    private (IReadOnlyList<GitStashInfo> List, bool Truncated) StashList(Repository repo)
    {
        var list = new List<GitStashInfo>();
        bool truncated = false;
        int i = 0;
        foreach (var stash in repo.Stashes)
        {
            if (list.Count >= MaxStashes)
            {
                truncated = true;
                break;
            }

            list.Add(new GitStashInfo(i, StashMessage(stash), Short(stash.WorkTree)));
            i++;
        }

        return (list, truncated);
    }

    /// <summary>The status entries under <paramref name="prefix"/> (<c>""</c> = all), ignored files and submodules out (the sandbox's <c>.trash</c> too until 2026-10-01).</summary>
    private IEnumerable<StatusEntry> StatusEntries(Repository repo, RepoLocation location, string prefix, bool recurseUntracked)
    {
        var options = new StatusOptions
        {
            IncludeUntracked = true,
            RecurseUntrackedDirs = recurseUntracked,
            IncludeIgnored = false,
            ExcludeSubmodules = true,
            DetectRenamesInIndex = true,
            DetectRenamesInWorkDir = true,
        };
        if (prefix.Length > 0)
        {
            options.PathSpec = [prefix];
        }

        foreach (var entry in repo.RetrieveStatus(options))
        {
            if (entry.State == FileStatus.Unaltered || entry.State.HasFlag(FileStatus.Ignored))
            {
                continue;
            }

            yield return entry;
        }
    }

    private static char? IndexCode(FileStatus state)
    {
        if (state.HasFlag(FileStatus.NewInIndex))
        {
            return 'A';
        }

        if (state.HasFlag(FileStatus.RenamedInIndex))
        {
            return 'R';
        }

        if (state.HasFlag(FileStatus.DeletedFromIndex))
        {
            return 'D';
        }

        if (state.HasFlag(FileStatus.TypeChangeInIndex))
        {
            return 'T';
        }

        if (state.HasFlag(FileStatus.ModifiedInIndex))
        {
            return 'M';
        }

        return null;
    }

    private static char? WorkdirCode(FileStatus state)
    {
        if (state.HasFlag(FileStatus.RenamedInWorkdir))
        {
            return 'R';
        }

        if (state.HasFlag(FileStatus.DeletedFromWorkdir))
        {
            return 'D';
        }

        if (state.HasFlag(FileStatus.TypeChangeInWorkdir))
        {
            return 'T';
        }

        if (state.HasFlag(FileStatus.ModifiedInWorkdir))
        {
            return 'M';
        }

        return null;
    }

    /// <summary>Whether <paramref name="commit"/> changed <paramref name="repoPath"/> against its first parent (a root commit: whether it holds it).</summary>
    private static bool Touches(Commit commit, string repoPath)
    {
        var entry = commit[repoPath];
        var parent = commit.Parents.FirstOrDefault();
        if (parent is null)
        {
            return entry is not null;
        }

        var before = parent[repoPath];
        if (entry is null)
        {
            return before is not null;
        }

        return before is null || before.Target.Id != entry.Target.Id;
    }

    /// <summary>A blob's text, or why it has none: binary, or over <see cref="WorkingDirectory.MaxTextFileBytes"/>.</summary>
    private static GitOutcome BlobText(Blob blob, out string text)
    {
        text = "";
        if (blob.IsBinary)
        {
            return GitOutcome.Binary;
        }

        if (blob.Size > WorkingDirectory.MaxTextFileBytes)
        {
            return GitOutcome.TooBig;
        }

        text = blob.GetContentText();
        return GitOutcome.Ok;
    }

    private static UnifiedDiff.Text? BlobLines(Blob? blob)
    {
        if (blob is null)
        {
            return UnifiedDiff.Text.Empty;
        }

        return BlobText(blob, out string text) == GitOutcome.Ok ? UnifiedDiff.Split(text) : null;
    }

    private static UnifiedDiff.Text? FileLines(string full)
    {
        if (!File.Exists(full))
        {
            return UnifiedDiff.Text.Empty;
        }

        var info = new FileInfo(full);
        if (info.Length > WorkingDirectory.MaxTextFileBytes)
        {
            return null;
        }

        byte[] bytes = File.ReadAllBytes(full);
        return WorkingDirectory.LooksBinary(bytes) ? null : UnifiedDiff.Split(WorkingDirectory.Decode(bytes, out _));
    }

    /// <summary>One file's change with its patch lines (the labels, the hunks; <c>Binary files differ</c> for a side without text).</summary>
    private sealed record FileDiff(GitChange Change, IReadOnlyList<string> Patch);

    private FileDiff FileChange(RepoLocation location, char code, string repoPath, string? oldRepoPath, UnifiedDiff.Text? before, UnifiedDiff.Text? after)
    {
        string path = SandboxPath(location, repoPath);
        string? oldPath = oldRepoPath is null ? null : SandboxPath(location, oldRepoPath);
        string oldLabel = code == 'A' ? "/dev/null" : "a/" + (oldRepoPath ?? repoPath);
        string newLabel = code == 'D' ? "/dev/null" : "b/" + repoPath;
        if (before is null || after is null)
        {
            return new FileDiff(new GitChange(code, path, oldPath, 0, 0, true), ["--- " + oldLabel, "+++ " + newLabel, "Binary files differ"]);
        }

        var hunks = UnifiedDiff.Hunks(before.Value, after.Value);
        if (hunks is null)
        {
            return new FileDiff(new GitChange(code, path, oldPath, 0, 0, true), ["--- " + oldLabel, "+++ " + newLabel, "Too many distinct lines to diff"]);
        }

        var (added, deleted) = UnifiedDiff.Count(hunks);
        string patch = UnifiedDiff.Format(oldLabel, newLabel, hunks);
        var lines = patch.Length == 0 ? [] : patch.TrimEnd('\n').Split('\n').ToList();
        return new FileDiff(new GitChange(code, path, oldPath, added, deleted, false), lines);
    }

    /// <summary>The index against the working tree (<paramref name="staged"/> false: what <c>git diff</c> shows, untracked files out) or HEAD against the index.</summary>
    private (List<FileDiff> Files, bool Truncated) WorkdirDiff(Repository repo, RepoLocation location, string prefix, bool staged, bool pathsOnly, int cap, CancellationToken cancellationToken)
    {
        var files = new List<FileDiff>();
        bool truncated = false;
        var head = repo.Head.Tip?.Tree;
        foreach (var entry in StatusEntries(repo, location, prefix, recurseUntracked: false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (files.Count >= cap)
            {
                truncated = true;
                break;
            }

            if (staged)
            {
                if (IndexCode(entry.State) is not { } code)
                {
                    continue;
                }

                string? oldPath = code == 'R' ? entry.HeadToIndexRenameDetails?.OldFilePath : null;
                if (pathsOnly)
                {
                    files.Add(PathChange(location, code, entry.FilePath, oldPath));
                    continue;
                }

                var before = code == 'A' ? UnifiedDiff.Text.Empty : BlobLines(head?[oldPath ?? entry.FilePath]?.Target as Blob);
                var after = code == 'D' ? UnifiedDiff.Text.Empty : BlobLines(IndexBlob(repo, entry.FilePath));
                files.Add(FileChange(location, code, entry.FilePath, oldPath, before, after));
            }
            else
            {
                if (WorkdirCode(entry.State) is not { } code)
                {
                    continue;
                }

                string? oldPath = code == 'R' ? entry.IndexToWorkDirRenameDetails?.OldFilePath : null;
                if (pathsOnly)
                {
                    files.Add(PathChange(location, code, entry.FilePath, oldPath));
                    continue;
                }

                var before = BlobLines(IndexBlob(repo, oldPath ?? entry.FilePath));
                var after = code == 'D' ? UnifiedDiff.Text.Empty : FileLines(Path.Combine(location.WorkTree, entry.FilePath.Replace('/', Path.DirectorySeparatorChar)));
                files.Add(FileChange(location, code, entry.FilePath, oldPath, before, after));
            }
        }

        return (files, truncated);
    }

    /// <summary>One file's change with no patch and no counts (<see cref="DiffPaths"/>).</summary>
    private FileDiff PathChange(RepoLocation location, char code, string repoPath, string? oldRepoPath) =>
        new(new GitChange(code, SandboxPath(location, repoPath), oldRepoPath is null ? null : SandboxPath(location, oldRepoPath), 0, 0, false), []);

    private static Blob? IndexBlob(Repository repo, string repoPath)
    {
        var entry = repo.Index[repoPath];
        return entry is null ? null : repo.Lookup<Blob>(entry.Id);
    }

    /// <summary>
    /// Every file that differs between two trees (<paramref name="from"/> null: everything in <paramref name="to"/> is
    /// new), under <paramref name="prefix"/> alone when given, capped at <see cref="MaxChangedFiles"/> — a walk of the
    /// two trees by entry id, never libgit2's diff. Renames show as a delete and an add.
    /// </summary>
    private (List<FileDiff> Files, bool Truncated) Compare(Repository repo, RepoLocation location, Tree? from, Tree to, string prefix, bool pathsOnly = false, int cap = MaxChangedFiles, CancellationToken cancellationToken = default)
    {
        var files = new List<FileDiff>();
        var state = new WalkState(files, pathsOnly, cap, cancellationToken);
        if (prefix.Length == 0)
        {
            Walk(location, from, to, "", state);
            return (files, state.Truncated);
        }

        var before = from?[prefix];
        var after = to[prefix];
        if (before is null && after is null)
        {
            return (files, false);
        }

        if ((before?.TargetType ?? TreeEntryTargetType.Tree) == TreeEntryTargetType.Tree && (after?.TargetType ?? TreeEntryTargetType.Tree) == TreeEntryTargetType.Tree)
        {
            Walk(location, before?.Target as Tree, after?.Target as Tree, prefix + "/", state);
        }
        else
        {
            Entry(location, prefix, before, after, state);
        }

        return (files, state.Truncated);
    }

    private sealed class WalkState(List<FileDiff> files, bool pathsOnly, int cap, CancellationToken cancellationToken)
    {
        public List<FileDiff> Files { get; } = files;
        public bool PathsOnly { get; } = pathsOnly;
        public int Cap { get; } = cap;
        public CancellationToken Token { get; } = cancellationToken;
        public int Visited { get; set; }
        public bool Truncated { get; set; }
    }

    private void Walk(RepoLocation location, Tree? from, Tree? to, string prefix, WalkState state)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        var before = new Dictionary<string, TreeEntry>(StringComparer.Ordinal);
        var after = new Dictionary<string, TreeEntry>(StringComparer.Ordinal);
        if (from is not null)
        {
            foreach (var e in from)
            {
                before[e.Name] = e;
                names.Add(e.Name);
            }
        }

        if (to is not null)
        {
            foreach (var e in to)
            {
                after[e.Name] = e;
                names.Add(e.Name);
            }
        }

        foreach (var name in names)
        {
            if (state.Truncated)
            {
                return;
            }

            state.Token.ThrowIfCancellationRequested();
            if (++state.Visited > MaxTreeEntries || state.Files.Count >= state.Cap)
            {
                state.Truncated = true;
                return;
            }

            before.TryGetValue(name, out var b);
            after.TryGetValue(name, out var a);
            if (Unchanged(b, a))
            {
                continue;
            }

            bool bothTrees = (b?.TargetType ?? TreeEntryTargetType.Tree) == TreeEntryTargetType.Tree && (a?.TargetType ?? TreeEntryTargetType.Tree) == TreeEntryTargetType.Tree;
            if (bothTrees)
            {
                Walk(location, b?.Target as Tree, a?.Target as Tree, prefix + name + "/", state);
            }
            else
            {
                Entry(location, prefix + name, b, a, state);
            }
        }
    }

    /// <summary>One path that differs: a blob added, deleted or modified; a submodule or a blob-for-tree swap shows as the blobs it is.</summary>
    private void Entry(RepoLocation location, string repoPath, TreeEntry? before, TreeEntry? after, WalkState state)
    {
        var b = before?.TargetType == TreeEntryTargetType.Blob ? (Blob)before.Target : null;
        var a = after?.TargetType == TreeEntryTargetType.Blob ? (Blob)after.Target : null;
        if (before?.TargetType == TreeEntryTargetType.Tree)
        {
            Walk(location, (Tree)before.Target, null, repoPath + "/", state);
        }

        if (after?.TargetType == TreeEntryTargetType.Tree)
        {
            Walk(location, null, (Tree)after.Target, repoPath + "/", state);
        }

        if (b is null && a is null)
        {
            return;
        }

        char code = b is null ? 'A' : a is null ? 'D' : 'M';
        if (state.PathsOnly)
        {
            state.Files.Add(PathChange(location, code, repoPath, null));
            return;
        }

        state.Files.Add(FileChange(location, code, repoPath, null, b is null ? UnifiedDiff.Text.Empty : BlobLines(b), a is null ? UnifiedDiff.Text.Empty : BlobLines(a)));
    }

    // ---- the frame ----

    /// <summary>Locates the repository for <paramref name="relative"/>, opens it for <paramref name="act"/> alone and maps what libgit2 refuses.</summary>
    private T Run<T>(string relative, Func<GitOutcome, string, T> refused, Func<Repository, RepoLocation, string, T> act)
    {
        var located = Locate(relative ?? "", out var location, out string full, out string detail);
        if (located != GitOutcome.Ok)
        {
            // The sentence names the path as the model gave it, or the root itself when that is what it named.
            return refused(located, detail.Length > 0 ? detail : _files.Relative(full) is { Length: > 0 } shown ? shown : full);
        }

        try
        {
            using var repo = new Repository(location!.GitDirectory);
            return act(repo, location, full);
        }
        catch (RepositoryNotFoundException)
        {
            return refused(GitOutcome.NoRepository, "");
        }
        catch (LibGit2SharpException ex)
        {
            return refused(Classify(ex, out detail), detail);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return refused(GitOutcome.Failed, LogText.Excerpt(ex.Message));
        }
    }

    /// <summary>libgit2's <c>safe.directory</c> refusal apart from everything else.</summary>
    private static GitOutcome Classify(LibGit2SharpException ex, out string detail)
    {
        detail = LogText.Excerpt(ex.Message);
        return ex.Message.Contains("not owned by current user", StringComparison.OrdinalIgnoreCase) ? GitOutcome.NotOwned : GitOutcome.Failed;
    }

    // ---- log lines (pinned) ----

    public static string IdentitySetLogLine(string name, string email) => "Set user.name " + name + " and user.email " + email + " in the repository's config";

    public static string StagedLogLine(int count, bool unstage) => (unstage ? "Unstaged " : "Staged ") + count.ToString(CultureInfo.InvariantCulture) + (count == 1 ? " path" : " paths");
    public static string CommittedLogLine(string sha, string branch, bool amend) => (amend ? "Amended " : "Committed ") + sha + " on " + branch;
    public static string RevertedLogLine(string target, string sha, string branch) => "Reverted " + target + " with " + sha + " on " + branch;
    public static string RevertConflictedLogLine(string target, int paths) => "Reverting " + target + " conflicted in " + paths.ToString(CultureInfo.InvariantCulture) + (paths == 1 ? " path" : " paths") + "; nothing written";
    public static string TaggedLogLine(string name, string sha, bool annotated) => "Tagged " + sha + " as " + name + (annotated ? " (annotated)" : " (lightweight)");
    public static string BranchCreatedLogLine(string name, string sha) => "Created branch " + name + " at " + sha;
    public static string BranchRenamedLogLine(string name, string newName) => "Renamed branch " + name + " to " + newName;
    public static string SwitchedLogLine(string name) => "Switched to " + name;
    public static string StashedLogLine(int files) => "Stashed " + files.ToString(CultureInfo.InvariantCulture) + (files == 1 ? " file" : " files");
    public static string StashAppliedLogLine(int index, bool pop) => (pop ? "Popped " : "Applied ") + StashName(index);
    public static string DiscardedLogLine(int? paths, string reference, string sha) =>
        paths is { } n
            ? "Discarded changes in " + n.ToString(CultureInfo.InvariantCulture) + (n == 1 ? " path" : " paths") + " back to " + reference + " (" + sha + ")"
            : "Reset hard to " + reference + " (" + sha + ")";
    public static string DeletedLogLine(GitDeleteKind kind, string name, string sha) => "Deleted " + kind.ToString().ToLowerInvariant() + " " + name + " (was " + sha + ")";
}
