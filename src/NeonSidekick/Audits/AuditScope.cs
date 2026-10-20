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
}

/// <summary>A scope as the line asked for it: its kind, the paths named (<see cref="AuditPath"/> form) and a branch's base when one was named.</summary>
public sealed record AuditScopeRequest(AuditScopeKind Kind, IReadOnlyList<string> Paths, string? Base = null);

/// <summary>
/// A code audit's scope, resolved once at the start (2026-10-18): the kind, the paths named, a branch's base, the files to read (at most
/// <see cref="AuditText.MaxScopeFiles"/>, sorted, the one path form) and how many more there were, the files a git change deletes, what the
/// exclusions left out (secret-looking, binary, vendored or generated), and whether the walk was cut by its budget.
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
    bool Cut = false)
{
    /// <summary>How the notices and the pass message name it: a path, <c>diff</c>, <c>staged</c>, <c>branch main..HEAD</c>, <c>the working directory</c>.</summary>
    public string Label => Kind switch
    {
        AuditScopeKind.Tree => "the working directory",
        AuditScopeKind.Paths => string.Join(", ", Paths),
        AuditScopeKind.Diff => Narrowed(AuditText.DiffWord),
        AuditScopeKind.Staged => Narrowed(AuditText.StagedWord),
        _ => Narrowed($"{AuditText.BranchWord} {Base}..HEAD"),
    };

    /// <summary>What the report's name is made of: <c>tree</c>, the paths, <c>diff</c>, <c>staged</c>, <c>branch main</c>.</summary>
    public string NameLabel => Kind switch
    {
        AuditScopeKind.Tree => "tree",
        AuditScopeKind.Paths => string.Join(" ", Paths),
        AuditScopeKind.Diff => AuditText.DiffWord,
        AuditScopeKind.Staged => AuditText.StagedWord,
        _ => AuditText.BranchWord + " " + Base,
    };

    /// <summary>
    /// The scope as the report's header keeps it, the words <c>/code-audit</c> takes (a blank-holding path quoted), so <c>resume</c> reads it
    /// back through the same splitter; empty for the whole tree.
    /// </summary>
    public string Words => string.Join(' ', Kind switch
    {
        AuditScopeKind.Tree => [],
        AuditScopeKind.Paths => Paths.Select(AuditCommand.PathWord),
        AuditScopeKind.Diff => [AuditText.DiffWord, .. Paths.Select(AuditCommand.PathWord)],
        AuditScopeKind.Staged => [AuditText.StagedWord, .. Paths.Select(AuditCommand.PathWord)],
        _ => [AuditText.BranchWord, Base ?? "", .. Paths.Select(AuditCommand.PathWord)],
    });

    /// <summary>What the exclusions left out, as the notices say it.</summary>
    public string Skipped => AuditText.SkippedText(Secret, Binary, Vendored);

    /// <summary>A git change rather than files on disk.</summary>
    public bool IsGit => Kind is AuditScopeKind.Diff or AuditScopeKind.Staged or AuditScopeKind.Branch;

    private string Narrowed(string word) => Paths.Count == 0 ? word : word + " of " + string.Join(", ", Paths);
}

/// <summary>A resolved scope, or the sentence saying why there is none.</summary>
public sealed record AuditScopeResult(AuditScope? Scope, string? Error);

/// <summary>
/// Resolving a code audit's scope (2026-10-18). Tree and path scopes: inside a repository the index's files and the untracked ones
/// (<see cref="GitAccess.TrackedFiles"/>), elsewhere a walk (<see cref="WorkingDirectory.ListFiles"/>) that never enters <see cref="WalkSkips"/>;
/// git scopes through <see cref="GitAccess.Diff"/> (and <see cref="GitAccess.MergeBase"/> for a branch). Every list then loses, in this order,
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
        if (request.Kind == AuditScopeKind.Branch)
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
        if (scope.Files.Count == 0 && scope.Deleted.Count == 0)
        {
            string why = changed.Count + deleted.Count == 0
                ? request.Kind switch
                {
                    AuditScopeKind.Diff => AuditText.CleanTree,
                    AuditScopeKind.Staged => AuditText.NothingStaged,
                    _ => AuditText.NoCommits(@base!, GitAccess.Head),
                }
                : AuditText.NoFilesLeft(scope.Label, scope.Skipped);
            return new AuditScopeResult(null, AuditText.NothingToAudit(why));
        }

        return new AuditScopeResult(scope, null);
    }

    /// <summary>
    /// The candidates less the exclusions, in order: <c>.neon/</c>, the secret-looking files, the vendored and generated ones (below the paths
    /// named, <see cref="Vendored"/>), files that are gone, the binary ones (probed only until the cut), then the cut with the rest counted.
    /// </summary>
    private static AuditScope Filter(WorkingDirectory files, AuditScopeRequest request, IEnumerable<string> candidates, IReadOnlyList<string> deleted, bool cut, CancellationToken cancellationToken)
    {
        int secret = 0, binary = 0, vendored = 0, more = 0;
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

            if (kept.Count >= AuditText.MaxScopeFiles)
            {
                more++;
                continue;
            }

            if (files.Resolve(path, forWrite: false, out string full) != FileOutcome.Ok || !File.Exists(full))
            {
                continue;
            }

            if (!WorkingDirectory.IsTextFile(full))
            {
                binary++;
                continue;
            }

            kept.Add(path);
        }

        return new AuditScope(request.Kind, request.Paths, request.Base, kept, more, deleted, secret, binary, vendored, cut);
    }

    /// <summary>A tool sentence without its <c>Error: </c> lead, for the screen's own error line.</summary>
    private static string Bare(string sentence) => sentence.StartsWith("Error: ", StringComparison.Ordinal) ? sentence[7..] : sentence;
}
