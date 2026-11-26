using NeonSidekick.Files;
using NeonSidekick.Git;

namespace NeonSidekick.Audits;

/// <summary>What a code audit reads (2026-10-18): the whole tree, named paths, or a git change.</summary>
public enum AuditScopeKind
{
    /// <summary>The whole working directory.</summary>
    Tree,

    /// <summary>The files and folders named on the line.</summary>
    Paths,

    /// <summary>The working changes: unstaged, staged and untracked.</summary>
    Diff,

    /// <summary>The staged changes alone.</summary>
    Staged,

    /// <summary>The branch's commits since it left its base (merge-base..HEAD).</summary>
    Branch,

    /// <summary>One commit, or a range, on the checked-out branch, pinned to shas at the start (2026-11-25, <c>/code-audit commit</c>).</summary>
    Commits,
}

/// <summary>
/// A scope as the line asked for it: its kind, the paths named (<see cref="AuditPath"/> form) and a branch's base when one was named; since
/// 2026-11-25 a commit scope's revision as typed (<c>HEAD~1</c>, <c>v1..main</c>), <paramref name="Pinned"/> when it came from a report's header
/// (full shas), so one the repository lost is said as gone rather than unknown.
/// </summary>
public sealed record AuditScopeRequest(AuditScopeKind Kind, IReadOnlyList<string> Paths, string? Base = null, string? Revision = null, bool Pinned = false);

/// <summary>
/// A code audit's scope, resolved once at the start (2026-10-18): the kind, the paths named, a branch's base, the files to read (at most
/// <see cref="AuditText.MaxScopeFiles"/>, sorted, the one path form) and how many more there were, the files a git change deletes, what the
/// exclusions left out (secret-looking, binary, vendored or generated), and whether the walk was cut by its budget. A commit scope's
/// (2026-11-25): its pinned full shas (<paramref name="From"/> null for one commit, the merge base when <paramref name="FromMergeBase"/>), the
/// scoped files changed after <paramref name="To"/> (<paramref name="ChangedSinceCut"/> when that diff was cut), the files it changed that are
/// gone since, and the revision as typed, for <see cref="AuditText.CommitsNotice"/>. <paramref name="PinnedFrom"/> (2026-11-26, the review's #50/#88):
/// the typed start's full sha, which the header keeps, so a resume works the merge base out again and says it as the start did.
/// </summary>
public sealed record AuditScope(
    AuditScopeKind Kind,
    IReadOnlyList<string> Paths,
    string? Base,
    IReadOnlyList<string> Files,
    int MoreFiles,
    IReadOnlyList<string> Deleted,
    int Secret,
    int Binary,
    int Vendored,
    bool Cut = false,
    string? From = null,
    string? To = null,
    bool FromMergeBase = false,
    IReadOnlyList<string>? ChangedSince = null,
    bool ChangedSinceCut = false,
    int GoneSince = 0,
    string? Revision = null,
    string? PinnedFrom = null)
{
    /// <summary>The scoped files changed after the commits; empty for every other scope.</summary>
    public IReadOnlyList<string> ChangedSince { get; init; } = ChangedSince ?? [];

    /// <summary>
    /// How the notices and the pass message name it: a path, <c>diff</c>, <c>staged</c>, <c>branch main..HEAD</c>, <c>commit 1a2b3c4</c>,
    /// <c>commits 1a2b3c4..5e6f7a8</c>, <c>the working directory</c>. A commit scope's is its shas, never the typed revision (2026-11-25): it is the
    /// report's title, and the Web UI's markdown reads <c>~x~</c> and <c>^x^</c> as sub- and superscript; it also stays the same on a resume.
    /// </summary>
    public string Label => Kind switch
    {
        AuditScopeKind.Tree => "the working directory",
        AuditScopeKind.Paths => string.Join(", ", Paths),
        AuditScopeKind.Diff => Narrowed(AuditText.DiffWord),
        AuditScopeKind.Staged => Narrowed(AuditText.StagedWord),
        AuditScopeKind.Commits => Narrowed(From is { } from
            ? "commits " + AuditText.ShortSha(from) + AuditText.RangeDots + AuditText.ShortSha(To ?? "")
            : AuditText.CommitWord + " " + AuditText.ShortSha(To ?? "")),
        _ => Narrowed($"{AuditText.BranchWord} {Base}..HEAD"),
    };

    /// <summary>What the report's name is made of: <c>tree</c>, the paths, <c>diff</c>, <c>staged</c>, <c>branch main</c>, <c>commit 1a2b3c4</c>, <c>commits 1a2b3c4 5e6f7a8</c>.</summary>
    public string NameLabel => Kind switch
    {
        AuditScopeKind.Tree => "tree",
        AuditScopeKind.Paths => string.Join(" ", Paths),
        AuditScopeKind.Diff => AuditText.DiffWord,
        AuditScopeKind.Staged => AuditText.StagedWord,
        AuditScopeKind.Commits => From is { } from
            ? "commits " + AuditText.ShortSha(from) + " " + AuditText.ShortSha(To ?? "")
            : AuditText.CommitWord + " " + AuditText.ShortSha(To ?? ""),
        _ => AuditText.BranchWord + " " + Base,
    };

    /// <summary>
    /// The scope as the report's header keeps it, the words <c>/code-audit</c> takes (a blank-holding path quoted), so <c>resume</c> reads it
    /// back through the same splitter; empty for the whole tree. A commit scope keeps its full shas (2026-11-25): a resume never re-reads
    /// <c>HEAD~3</c>, and a full sha never comes back ambiguous.
    /// </summary>
    public string Words => string.Join(' ', Kind switch
    {
        AuditScopeKind.Tree => [],
        AuditScopeKind.Paths => Paths.Select(AuditCommand.PathWord),
        AuditScopeKind.Diff => [AuditText.DiffWord, .. Paths.Select(AuditCommand.PathWord)],
        AuditScopeKind.Staged => [AuditText.StagedWord, .. Paths.Select(AuditCommand.PathWord)],
        AuditScopeKind.Commits => [AuditText.CommitWord, From is { } from ? (PinnedFrom ?? from) + AuditText.RangeDots + To : To ?? "", .. Paths.Select(AuditCommand.PathWord)],
        _ => [AuditText.BranchWord, Base ?? "", .. Paths.Select(AuditCommand.PathWord)],
    });

    /// <summary>What the exclusions left out, as the notices say it.</summary>
    public string Skipped => AuditText.SkippedText(Secret, Binary, Vendored);

    /// <summary>A git change rather than files on disk.</summary>
    public bool IsGit => Kind is AuditScopeKind.Diff or AuditScopeKind.Staged or AuditScopeKind.Branch or AuditScopeKind.Commits;

    private string Narrowed(string word) => Paths.Count == 0 ? word : word + " of " + string.Join(", ", Paths);
}

/// <summary>A resolved scope, or the sentence saying why there is none.</summary>
public sealed record AuditScopeResult(AuditScope? Scope, string? Error);

/// <summary>
/// Resolving a code audit's scope (2026-10-18). Tree and path scopes: inside a repository the index's files and the untracked ones
/// (<see cref="GitAccess.TrackedFiles"/>), elsewhere a walk (<see cref="WorkingDirectory.ListFiles"/>) that never enters <see cref="WalkSkips"/>;
/// git scopes through <see cref="GitAccess.Diff"/> (and <see cref="GitAccess.MergeBase"/> for a branch and, since 2026-11-25, a commit range,
/// whose sides <see cref="GitAccess.ResolveCommit"/> pins). Every list then loses, in this order,
/// <c>.neon/</c>, the secret-looking files (<see cref="SecretPatterns"/>), the vendored and generated ones (<see cref="SkippedPatterns"/>, judged
/// on the part of the path below the named path holding it, so a path named through one is audited, <see cref="Vendored"/>) and the binary ones, before the <see cref="AuditText.MaxScopeFiles"/> cut; past the cut the rest is counted
/// by name alone, never opened. The repository is found by <see cref="GitAccess.Locate"/>, never <c>Repository.Discover</c>.
/// </summary>
public static class AuditScopes
{
    /// <summary>
    /// The folders a walk never enters below where it starts (<c>.git</c> and <c>.neon</c> anywhere): naming one on the line walks it, naming a
    /// folder above it does not (2026-10-19, the review: a named path's walk skipped only <c>.git</c> and <c>.neon</c>, so <c>/code-audit web</c>
    /// listed <c>web/node_modules</c> ahead of <c>web/src</c> and the cut fell on dependencies). Pinned.
    /// </summary>
    public static readonly IReadOnlyList<string> WalkSkips = [".git", ".neon", "bin", "obj", "node_modules", ".vs", "packages", "dist", "build", "target"];

    /// <summary>
    /// The folders whose files a list judges vendored wherever they come from, beside <see cref="SkippedPatterns"/>' <c>vendor/</c>-style ones
    /// (2026-10-19, the second review: judging by every <see cref="WalkSkips"/> folder dropped a monorepo's tracked <c>packages/&lt;name&gt;/src</c>,
    /// and a repository's tracked <c>bin/</c> scripts or <c>build/</c> files; git's ignore rules already keep build output out of its lists, so
    /// only a dependency folder is judged by name). Pinned.
    /// </summary>
    public static readonly IReadOnlyList<string> DependencyFolders = ["node_modules"];

    /// <summary>
    /// The vendored and generated files every scope's list leaves out unless the line names a path holding them (2026-10-18; this repository's
    /// <c>assets/pdflib/pdf-lib.min.js</c> would otherwise eat a pass): minified and map files, lockfiles, generated C#, and any path through a
    /// <c>vendor/</c>, <c>third_party/</c> or <c>external/</c> folder. Pinned.
    /// </summary>
    public static readonly IReadOnlyList<string> SkippedPatterns =
    [
        "*.min.js", "*.min.css", "*.map",
        "package-lock.json", "yarn.lock", "pnpm-lock.yaml", "packages.lock.json", "Cargo.lock", "poetry.lock",
        "*.g.cs", "*.g.i.cs", "*.Designer.cs",
        "vendor/", "third_party/", "external/",
    ];

    /// <summary>
    /// The secret-looking files every scope's list leaves out, a named path or not (2026-10-18): this only guides what a pass reads —
    /// <c>read_file</c> can still open them — and the directive says why. Pinned.
    /// </summary>
    /// <remarks>2026-10-19, the review: the SSH keys' other names, PuTTY's, Java's keystores and the package tools' and git's credential files joined.</remarks>
    public static readonly IReadOnlyList<string> SecretPatterns =
    [
        ".env", ".env.*", "*.pfx", "*.p12", "*.pem", "*.key", "*.p8", "*.kdbx", "*.ppk", "*.jks", "*.keystore",
        "id_rsa*", "id_dsa*", "id_ecdsa*", "id_ed25519*",
        ".netrc", "_netrc", ".npmrc", ".pypirc", ".git-credentials", ".pgpass", "credentials", "credentials.json", "credentials.xml",
    ];

    /// <summary>Whether a file's name looks like a secret's.</summary>
    public static bool LooksSecret(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string name = Name(path);
        return SecretPatterns.Any(p => System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(p, name, ignoreCase: true));
    }

    /// <summary>Whether a path is vendored or generated: a name pattern, a <c>vendor/</c>-style folder, or one <see cref="DependencyFolders"/> names.</summary>
    public static bool LooksVendored(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string name = Name(path);
        string[] folders = path.Split('/')[..^1];
        return SkippedPatterns.Where(p => !p.EndsWith('/')).Any(p => System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(p, name, ignoreCase: true))
            || folders.Any(f => SkippedPatterns.Any(p => p.EndsWith('/') && string.Equals(p[..^1], f, StringComparison.OrdinalIgnoreCase)))
            || folders.Any(f => DependencyFolders.Contains(f, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Whether <paramref name="path"/> is left out as vendored or generated under the paths named on the line (2026-10-19, the review: the
    /// walk let everything under a named folder through, a git scope nothing under one). Under the deepest of <paramref name="roots"/> holding
    /// it, a named path is taken whole as far as its files' names go (<c>assets/pdflib</c> audits its <c>.min.js</c>, <c>staged src/Gen</c> its
    /// <c>.g.cs</c>), but a vendored folder below it — <c>vendor/</c>-style or one <see cref="DependencyFolders"/> names — is still left out, so
    /// <c>vendor/mylib</c> named audits its files and <c>web</c> named still leaves out <c>web/node_modules</c>. No roots, or <c>""</c>, judge
    /// the whole path by <see cref="LooksVendored"/>.
    /// </summary>
    public static bool Vendored(string path, IReadOnlyList<string> roots)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(roots);
        string? root = roots.Where(r => AuditPath.Under(path, r)).OrderByDescending(r => r.Length).FirstOrDefault();
        if (root is null || root.Length == 0)
        {
            return LooksVendored(path);
        }

        if (AuditPath.Comparer.Equals(path, root))
        {
            return false;
        }

        string[] folders = path[(root.Length + 1)..].Split('/')[..^1];
        return folders.Any(f => SkippedPatterns.Any(p => p.EndsWith('/') && string.Equals(p[..^1], f, StringComparison.OrdinalIgnoreCase))
            || DependencyFolders.Contains(f, StringComparer.OrdinalIgnoreCase));
    }

    private static string Name(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash < 0 ? path : path[(slash + 1)..];
    }

    /// <summary>The scope <paramref name="request"/> asks for, or why there is none. Synchronous: the caller runs it off the screen's thread.</summary>
    public static AuditScopeResult Resolve(WorkingDirectory files, GitAccess git, AuditScopeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(git);
        ArgumentNullException.ThrowIfNull(request);
        return request.Kind is AuditScopeKind.Tree or AuditScopeKind.Paths
            ? Disk(files, git, request, cancellationToken)
            : Change(files, git, request, cancellationToken);
    }

    private static AuditScopeResult Disk(WorkingDirectory files, GitAccess git, AuditScopeRequest request, CancellationToken cancellationToken)
    {
        var roots = request.Kind == AuditScopeKind.Tree ? [""] : request.Paths;
        var candidates = new List<string>();
        bool cut = false;
        foreach (string root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (files.Resolve(root, forWrite: false, out string full) != FileOutcome.Ok)
            {
                return new AuditScopeResult(null, AuditText.NoSuchPath(root));
            }

            if (File.Exists(full))
            {
                candidates.Add(AuditPath.FromRelative(files.Relative(full)));
                continue;
            }

            if (!Directory.Exists(full))
            {
                return new AuditScopeResult(null, AuditText.NoSuchPath(root));
            }

            // Inside one repository: its index and the untracked files (ignored files out). Several side by side, or one above the sandbox: a walk.
            if (git.Locate(root, out _, out _, out _) == GitOutcome.Ok && git.TrackedFiles(root, includeUntracked: true, cancellationToken) is { Outcome: GitOutcome.Ok } known)
            {
                cut |= known.Truncated;
                candidates.AddRange(known.Tracked.Concat(known.Untracked).Select(AuditPath.FromRelative));
                continue;
            }

            var walked = files.ListFiles(root, WorkingDirectory.MaxListEntries, name => WalkSkips.Contains(name, StringComparer.OrdinalIgnoreCase), cancellationToken);
            cut |= walked.Cut;
            candidates.AddRange(walked.Files.Select(AuditPath.FromRelative));
        }

        var scope = Filter(files, request, candidates, [], cut, cancellationToken);
        return scope.Files.Count == 0
            ? new AuditScopeResult(null, AuditText.NothingToAudit(AuditText.NoFilesLeft(scope.Label, scope.Skipped)))
            : new AuditScopeResult(scope, null);
    }

    private static AuditScopeResult Change(WorkingDirectory files, GitAccess git, AuditScopeRequest request, CancellationToken cancellationToken)
    {
        string at = request.Paths.Count > 0 ? request.Paths[0] : "";
        var located = git.Locate(at, out _, out _, out string detail);
        if (located != GitOutcome.Ok)
        {
            return new AuditScopeResult(null, Bare(GitText.Error(located, detail.Length > 0 ? detail : files.Root)));
        }

        string? @base = request.Base;
        string? from = null;
        Pinned? pinned = null;
        if (request.Kind == AuditScopeKind.Commits)
        {
            var resolved = PinCommits(git, at, request);
            if (resolved.Error is { } error)
            {
                return new AuditScopeResult(null, error);
            }

            pinned = resolved.Commits;
        }
        else if (request.Kind == AuditScopeKind.Branch)
        {
            var refs = git.Refs(at);
            if (refs.Outcome != GitOutcome.Ok)
            {
                return new AuditScopeResult(null, Bare(GitText.Error(refs.Outcome, refs.Detail)));
            }

            @base ??= refs.Local.Any(r => r.Name == "main") ? "main" : refs.Local.Any(r => r.Name == "master") ? "master" : null;
            if (@base is null)
            {
                return new AuditScopeResult(null, AuditText.NoBaseBranch);
            }

            if (string.Equals(refs.Current, @base, StringComparison.Ordinal))
            {
                return new AuditScopeResult(null, AuditText.NothingToAudit(AuditText.OnBase(@base)));
            }

            var merge = git.MergeBase(at, @base, GitAccess.Head);
            if (merge.Outcome != GitOutcome.Ok)
            {
                return new AuditScopeResult(null, Bare(GitText.Error(merge.Outcome, merge.Detail)));
            }

            if (merge.Sha is null || string.Equals(merge.Sha, merge.Head, StringComparison.Ordinal))
            {
                return new AuditScopeResult(null, AuditText.NothingToAudit(AuditText.OnBase(@base)));
            }

            from = merge.Sha;
        }

        var changed = new List<string>();
        var deleted = new List<string>();
        bool cut = false;
        foreach (string path in request.Paths.Count > 0 ? request.Paths : [""])
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Paths alone, under the token (2026-10-20, a review: a one-line diff still read and diffed every file, uncancellable, and stopped at
            // 200 files with nothing said, so a 600-file branch was audited as if its 200 were all): a cut is said.
            var reports = request.Kind switch
            {
                AuditScopeKind.Diff => new[] { git.DiffPaths(new GitDiffRequest(GitDiffKind.Unstaged, path), cancellationToken), git.DiffPaths(new GitDiffRequest(GitDiffKind.Staged, path), cancellationToken) },
                AuditScopeKind.Staged => new[] { git.DiffPaths(new GitDiffRequest(GitDiffKind.Staged, path), cancellationToken) },
                AuditScopeKind.Commits => new[]
                {
                    pinned!.From is { } start
                        ? git.DiffPaths(new GitDiffRequest(GitDiffKind.Range, path, From: start, To: pinned.To), cancellationToken)
                        : git.DiffPaths(new GitDiffRequest(GitDiffKind.Commit, path, Reference: pinned.To), cancellationToken),
                },
                _ => new[] { git.DiffPaths(new GitDiffRequest(GitDiffKind.Range, path, From: from, To: GitAccess.Head), cancellationToken) },
            };
            foreach (var report in reports)
            {
                if (report.Outcome != GitOutcome.Ok)
                {
                    return new AuditScopeResult(null, Bare(GitText.Error(report.Outcome, report.Detail)));
                }

                cut |= report.FilesTruncated;

                foreach (var change in report.Files)
                {
                    (change.Code == 'D' ? deleted : changed).Add(AuditPath.FromRelative(change.Path));
                }
            }

            // The untracked files alone (2026-10-19, the second review: listed after the whole index, a large repository's cap left none), a cut said.
            if (request.Kind == AuditScopeKind.Diff && git.TrackedFiles(path, includeUntracked: true, cancellationToken, includeTracked: false) is { Outcome: GitOutcome.Ok } known)
            {
                cut |= known.Truncated;
                changed.AddRange(known.Untracked.Select(AuditPath.FromRelative));
            }
        }

        // A file deleted and then made again (deleted in the index, untracked on disk) is a file; one gone from disk is deleted.
        var kept = new HashSet<string>(changed, AuditPath.Comparer);
        var gone = deleted.Where(d => !kept.Contains(d)).Distinct(AuditPath.Comparer).Where(d => !AuditPath.InAppFolder(d) && !LooksSecret(d) && !Vendored(d, request.Paths)).ToList();
        var scope = Filter(files, request with { Base = @base }, changed, gone, cut, cancellationToken);
        if (pinned is not null)
        {
            scope = scope with { From = pinned.From, To = pinned.To, FromMergeBase = pinned.FromMergeBase, PinnedFrom = pinned.PinnedFrom };
        }

        if (scope.Files.Count == 0 && scope.Deleted.Count == 0)
        {
            string why = changed.Count + deleted.Count == 0
                ? request.Kind switch
                {
                    AuditScopeKind.Diff => AuditText.CleanTree,
                    AuditScopeKind.Staged => AuditText.NothingStaged,
                    AuditScopeKind.Commits => AuditText.CommitTouchesNothing(scope.Label),
                    _ => AuditText.NoCommits(@base!, GitAccess.Head),
                }
                // Every file gone since, and nothing left out for another reason (2026-11-25): said as such, not "no text file".
                : request.Kind == AuditScopeKind.Commits && scope.GoneSince > 0 && scope.Skipped.Length == 0
                    ? AuditText.AllGoneSince(scope.Label)
                    : AuditText.NoFilesLeft(scope.Label, scope.Skipped);
            return new AuditScopeResult(null, AuditText.NothingToAudit(why));
        }

        if (pinned is not null)
        {
            var since = ChangedSince(git, request.Paths, pinned.To, scope.Files, cancellationToken);
            if (since.Error is { } error)
            {
                return new AuditScopeResult(null, error);
            }

            scope = scope with { ChangedSince = since.Files, ChangedSinceCut = since.Cut };
        }

        return new AuditScopeResult(scope, null);
    }

    /// <summary>A commit scope's pinned shas: <see cref="From"/> null for one commit, the merge base when <see cref="FromMergeBase"/>; <see cref="PinnedFrom"/> the typed start's.</summary>
    private sealed record Pinned(string? From, string To, bool FromMergeBase, string? PinnedFrom);

    /// <summary>
    /// A commit scope's revision pinned to full shas (2026-11-25), or the one sentence that refuses it. The checks run in this order, so each
    /// case has one message: each side resolves; a range's sides differ (<see cref="AuditText.NoCommits"/>); its end, or the one commit, is on
    /// the checked-out branch (<see cref="AuditText.NotOnBranch"/>, the user's pick); the sides share history; the end isn't behind the start;
    /// and a start that isn't behind the end is swapped for their merge base (git's <c>a..b</c>: the commits on <c>b</c> not on <c>a</c>).
    /// </summary>
    private static (Pinned? Commits, string? Error) PinCommits(GitAccess git, string at, AuditScopeRequest request)
    {
        string revision = request.Revision ?? "";
        var range = AuditText.SplitRange(revision);
        string left = range?.Left ?? "";
        string right = range is { } r ? (r.Right.Length == 0 ? GitAccess.Head : r.Right) : revision;

        // An unknown revision typed on the line may be a brief's first word (commit messages are…); a pinned one is a commit the repository lost.
        string? Refusal(GitCommitIdReport report, string side)
        {
            if (report.Outcome == GitOutcome.RefNotFound && request.Pinned)
            {
                return AuditText.CommitGone(side);
            }

            string bare = Bare(GitText.Error(report.Outcome, report.Detail));
            return report.Outcome == GitOutcome.RefNotFound ? bare + " — " + AuditText.BriefAfterDashes : bare;
        }

        string? from = null;
        if (range is not null)
        {
            var start = git.ResolveCommit(at, left);
            if (start.Outcome != GitOutcome.Ok)
            {
                return (null, Refusal(start, left));
            }

            from = start.Sha;
        }

        var end = git.ResolveCommit(at, right);
        if (end.Outcome != GitOutcome.Ok)
        {
            return (null, Refusal(end, right));
        }

        string to = end.Sha!;
        string Typed(string side) => request.Pinned ? AuditText.ShortSha(side) : side;
        if (from is not null && string.Equals(from, to, StringComparison.Ordinal))
        {
            return (null, AuditText.NothingToAudit(AuditText.NoCommits(Typed(left), Typed(right))));
        }

        // On the checked-out branch: the end is HEAD or behind it.
        var onBranch = git.MergeBase(at, to, GitAccess.Head);
        if (onBranch.Outcome != GitOutcome.Ok)
        {
            return (null, Bare(GitText.Error(onBranch.Outcome, onBranch.Detail)));
        }

        if (!string.Equals(onBranch.Sha, to, StringComparison.Ordinal))
        {
            string named = Typed(right);
            string shown = string.Equals(AuditText.ShortSha(to), named, StringComparison.OrdinalIgnoreCase) || to.StartsWith(named, StringComparison.OrdinalIgnoreCase)
                ? named
                : $"{named} ({AuditText.ShortSha(to)})";
            var refs = git.Refs(at);
            string branch = refs.Outcome == GitOutcome.Ok && refs.Current is { } current ? current : AuditText.ShortSha(onBranch.Head ?? "");
            return (null, AuditText.NotOnBranch(shown, branch));
        }

        if (from is null)
        {
            return (new Pinned(null, to, false, null), null);
        }

        var merge = git.MergeBase(at, from, to);
        if (merge.Outcome != GitOutcome.Ok)
        {
            return (null, Bare(GitText.Error(merge.Outcome, merge.Detail)));
        }

        if (merge.Sha is null)
        {
            return (null, AuditText.NoSharedHistory(Typed(left), Typed(right)));
        }

        if (string.Equals(merge.Sha, to, StringComparison.Ordinal))
        {
            return (null, AuditText.RangeBackwards(Typed(left), Typed(right)));
        }

        bool swapped = !string.Equals(merge.Sha, from, StringComparison.Ordinal);
        return (new Pinned(merge.Sha, to, swapped, from), null);
    }

    /// <summary>
    /// The scope's files that differ between <paramref name="to"/> and the working tree (2026-11-25): the commits since, then the unstaged and
    /// staged changes, per named path, under the token; cut when a diff was.
    /// </summary>
    private static (IReadOnlyList<string> Files, bool Cut, string? Error) ChangedSince(
        GitAccess git, IReadOnlyList<string> paths, string to, IReadOnlyList<string> scoped, CancellationToken cancellationToken)
    {
        var inScope = new HashSet<string>(scoped, AuditPath.Comparer);
        var found = new HashSet<string>(AuditPath.Comparer);
        bool cut = false;
        foreach (string path in paths.Count > 0 ? paths : [""])
        {
            cancellationToken.ThrowIfCancellationRequested();
            var reports = new List<GitDiffReport>(3);
            var head = git.ResolveCommit(path, GitAccess.Head);
            if (head.Outcome == GitOutcome.Ok && !string.Equals(head.Sha, to, StringComparison.Ordinal))
            {
                reports.Add(git.DiffPaths(new GitDiffRequest(GitDiffKind.Range, path, From: to, To: GitAccess.Head), cancellationToken));
            }

            reports.Add(git.DiffPaths(new GitDiffRequest(GitDiffKind.Unstaged, path), cancellationToken));
            reports.Add(git.DiffPaths(new GitDiffRequest(GitDiffKind.Staged, path), cancellationToken));
            foreach (var report in reports)
            {
                if (report.Outcome != GitOutcome.Ok)
                {
                    return ([], false, Bare(GitText.Error(report.Outcome, report.Detail)));
                }

                cut |= report.FilesTruncated;
                foreach (var change in report.Files)
                {
                    string file = AuditPath.FromRelative(change.Path);
                    if (inScope.Contains(file))
                    {
                        found.Add(file);
                    }
                }
            }
        }

        return (found.OrderBy(f => f, StringComparer.Ordinal).ToList(), cut, null);
    }

    /// <summary>
    /// The candidates less the exclusions, in order: <c>.neon/</c>, the secret-looking files, the vendored and generated ones (below the paths
    /// named, <see cref="Vendored"/>), files that are gone, the binary ones (probed only until the cut), then the cut with the rest counted.
    /// </summary>
    private static AuditScope Filter(WorkingDirectory files, AuditScopeRequest request, IEnumerable<string> candidates, IReadOnlyList<string> deleted, bool cut, CancellationToken cancellationToken)
    {
        int secret = 0, binary = 0, vendored = 0, more = 0, gone = 0;
        var kept = new List<string>();
        foreach (string path in candidates.Distinct(AuditPath.Comparer).OrderBy(p => p, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (path.Length == 0 || AuditPath.InAppFolder(path))
            {
                continue;
            }

            if (LooksSecret(path))
            {
                secret++;
                continue;
            }

            if (Vendored(path, request.Paths))
            {
                vendored++;
                continue;
            }

            // A commit scope (2026-11-26, the review's #45/#77) resolves before the cut, so gone counts every changed file that is missing (a failed
            // resolve is no file of the folder's, skipped, never gone) and the cut counts only files still there. The other kinds keep the cut first,
            // so a whole tree past the cap never resolves what it won't read.
            bool commits = request.Kind == AuditScopeKind.Commits;
            string full = "";
            if (commits)
            {
                if (files.Resolve(path, forWrite: false, out full) != FileOutcome.Ok)
                {
                    continue;
                }

                if (!File.Exists(full))
                {
                    gone++;
                    continue;
                }
            }

            if (kept.Count >= AuditText.MaxScopeFiles)
            {
                more++;
                continue;
            }

            if (!commits && (files.Resolve(path, forWrite: false, out full) != FileOutcome.Ok || !File.Exists(full)))
            {
                gone++;
                continue;
            }

            if (!WorkingDirectory.IsTextFile(full))
            {
                binary++;
                continue;
            }

            kept.Add(path);
        }

        // A commit scope counts the files it changed that are gone since (2026-11-25); the other kinds say nothing of them, as before.
        return new AuditScope(request.Kind, request.Paths, request.Base, kept, more, deleted, secret, binary, vendored, cut,
            GoneSince: request.Kind == AuditScopeKind.Commits ? gone : 0, Revision: request.Revision);
    }

    /// <summary>A tool sentence without its <c>Error: </c> lead, for the screen's own error line.</summary>
    private static string Bare(string sentence) => sentence.StartsWith("Error: ", StringComparison.Ordinal) ? sentence[7..] : sentence;
}
