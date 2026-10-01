namespace NeonSidekick.Git;

/// <summary>Where a repository was found for a sandbox path: its <c>.git</c>, its working tree (full, no trailing separator) and the tree's place under the sandbox root (<c>""</c> for the root itself, else <c>proj\</c>).</summary>
public sealed record RepoLocation(string GitDirectory, string WorkTree, string RootRelative);

/// <summary>One commit as the tools show it: the sha, its 7-character short form, the author's name and when, the subject line and the whole message.</summary>
public sealed record GitCommitInfo(string Sha, string Short, string Author, DateTimeOffset When, string Subject, string Message);

/// <summary>
/// One path in a status listing: <c>Code</c> is git's letter (<c>A</c> added, <c>M</c> modified, <c>D</c> deleted,
/// <c>R</c> renamed, <c>T</c> type changed, <c>?</c> untracked, <c>U</c> conflicted), <c>Path</c> the sandbox-relative path
/// and <c>OldPath</c> the previous one of a rename.
/// </summary>
public sealed record GitEntry(char Code, string Path, string? OldPath = null);

/// <summary>One file a diff, a commit or a stash changed: the code, the paths, the line counts, and whether it is binary (no counts then).</summary>
public sealed record GitChange(char Code, string Path, string? OldPath, int Added, int Deleted, bool Binary);

/// <summary>The working tree's state (<c>gitlib_status</c>).</summary>
public sealed record GitStatusReport(
    GitOutcome Outcome,
    string Detail,
    string Branch,
    bool Unborn,
    bool Detached,
    string? HeadShort,
    string? Upstream,
    int? Ahead,
    int? Behind,
    IReadOnlyList<GitEntry> Staged,
    IReadOnlyList<GitEntry> Unstaged,
    IReadOnlyList<GitEntry> Untracked,
    IReadOnlyList<GitEntry> Conflicts,
    bool Truncated)
{
    public static GitStatusReport Refused(GitOutcome outcome, string detail = "") => new(outcome, detail, "", false, false, null, null, null, null, [], [], [], [], false);

    public bool Clean => Staged.Count == 0 && Unstaged.Count == 0 && Untracked.Count == 0 && Conflicts.Count == 0;
}

/// <summary>The commits reachable from a ref, newest first (<c>gitlib_log</c>); <c>Path</c> set when only those touching it were asked for.</summary>
public sealed record GitLogReport(GitOutcome Outcome, string Detail, string Reference, string? Path, IReadOnlyList<GitCommitInfo> Commits, bool Truncated)
{
    public static GitLogReport Refused(GitOutcome outcome, string detail = "") => new(outcome, detail, "", null, [], false);
}

/// <summary>
/// One commit (<c>gitlib_show</c> without a path: its header and the files it changed) or one path at a commit
/// (with a path: a file's text — <c>Cut</c> when it stopped at the cap — or a folder's entries).
/// </summary>
public sealed record GitShowReport(
    GitOutcome Outcome,
    string Detail,
    GitCommitInfo? Commit,
    IReadOnlyList<GitChange> Changes,
    bool ChangesTruncated,
    string? Path,
    string? Text,
    int Lines,
    bool Cut,
    IReadOnlyList<string> Entries)
{
    public static GitShowReport Refused(GitOutcome outcome, string detail = "") => new(outcome, detail, null, [], false, null, null, 0, false, []);
}

/// <summary>Which two states a diff compares.</summary>
public enum GitDiffKind
{
    /// <summary>The index against the working tree (a bare call): what <c>git diff</c> shows.</summary>
    Unstaged,

    /// <summary>HEAD against the index: what <c>git diff --staged</c> shows.</summary>
    Staged,

    /// <summary>A commit against its first parent.</summary>
    Commit,

    /// <summary>Two refs.</summary>
    Range,
}

/// <summary>What a diff is asked over: the sandbox path it is narrowed to (<c>""</c> = everything), and the refs by kind.</summary>
public sealed record GitDiffRequest(GitDiffKind Kind, string Path, string? Reference = null, string? From = null, string? To = null);

/// <summary>A diff: the files with their counts, the unified patch (cut at <c>MaxLines</c>: <c>Truncated</c>, <c>TotalLines</c> the whole), the totals.</summary>
public sealed record GitDiffReport(
    GitOutcome Outcome,
    string Detail,
    GitDiffKind Kind,
    string Label,
    IReadOnlyList<GitChange> Files,
    bool FilesTruncated,
    string Patch,
    int Added,
    int Deleted,
    bool Truncated,
    int TotalLines)
{
    public static GitDiffReport Refused(GitDiffKind kind, GitOutcome outcome, string detail = "") => new(outcome, detail, kind, "", [], false, "", 0, 0, false, 0);
}

/// <summary>One blamed line: its number, the commit that last touched it and the text.</summary>
public sealed record GitBlameLine(int Number, string Short, string Author, DateTimeOffset When, string Text);

/// <summary>A file's lines with their last commits (<c>gitlib_blame</c>), over the window <c>From</c>–<c>To</c> of <c>TotalLines</c>.</summary>
public sealed record GitBlameReport(GitOutcome Outcome, string Detail, string Path, string Reference, int From, int To, int TotalLines, IReadOnlyList<GitBlameLine> Lines, int Commits)
{
    public static GitBlameReport Refused(GitOutcome outcome, string detail = "") => new(outcome, detail, "", "", 0, 0, 0, [], 0);
}

/// <summary>One branch or tag: its name, the short sha it points at, its subject, and for a local branch its upstream and whether it is checked out.</summary>
public sealed record GitRef(string Name, string Short, string Subject, bool Current = false, string? Upstream = null);

/// <summary>The branches and tags (<c>gitlib_branch list</c>): the local branches, the remote-tracking ones, the tags, each capped.</summary>
public sealed record GitRefsReport(GitOutcome Outcome, string Detail, string? Current, bool Detached, IReadOnlyList<GitRef> Local, IReadOnlyList<GitRef> Remote, IReadOnlyList<GitRef> Tags, bool Truncated)
{
    public static GitRefsReport Refused(GitOutcome outcome, string detail = "") => new(outcome, detail, null, false, [], [], [], false);
}

/// <summary>
/// What <c>/gituser</c> did (2026-09-21): <c>Written</c> = the two keys were set at the repository's
/// local level, <c>Email</c> / <c>Name</c> what they hold now; not written (and Ok) = a <c>[user]</c>
/// section was there already and <c>force</c> was not given, <c>Email</c> / <c>Name</c> the values found
/// (either may be empty when only the other was set).
/// </summary>
public sealed record GitIdentityResult(GitOutcome Outcome, string Detail, string Email, string Name, bool Written)
{
    public static GitIdentityResult Refused(GitOutcome outcome, string detail = "") => new(outcome, detail, "", "", false);
}

/// <summary>A branch created, switched to or renamed: its name (the new one for a rename, <c>OldName</c> the previous) and the short sha it stands at.</summary>
public sealed record GitBranchResult(GitOutcome Outcome, string Detail, string Name, string Short, string? OldName = null)
{
    public static GitBranchResult Refused(GitOutcome outcome, string detail = "") => new(outcome, detail, "", "");
}

/// <summary>Paths staged or unstaged: the sandbox-relative paths it touched, and the folder a <c>.</c> stood for.</summary>
public sealed record GitStageResult(GitOutcome Outcome, string Detail, IReadOnlyList<string> Paths, string Folder, bool Unstage)
{
    public static GitStageResult Refused(GitOutcome outcome, string detail = "") => new(outcome, detail, [], "", false);
}

/// <summary>A commit made: its short sha, the branch, the subject, the files and lines it changed against its parent, whether it amended.</summary>
public sealed record GitCommitResult(GitOutcome Outcome, string Detail, string Short, string Branch, string Subject, int Files, int Added, int Deleted, bool Amended)
{
    public static GitCommitResult Refused(GitOutcome outcome, string detail = "") => new(outcome, detail, "", "", "", 0, 0, 0, false);
}

/// <summary>One stash as listed: its index (<c>stash@{n}</c>), its message and the short sha of its working-tree commit.</summary>
public sealed record GitStashInfo(int Index, string Message, string Short);

/// <summary>A stash pushed, popped, applied or listed: the one acted on (<c>Index</c>, <c>Message</c>, the files it holds) and the list.</summary>
public sealed record GitStashResult(GitOutcome Outcome, string Detail, int Index, string Message, int Files, IReadOnlyList<GitStashInfo> Stashes, bool Truncated)
{
    public static GitStashResult Refused(GitOutcome outcome, string detail = "") => new(outcome, detail, -1, "", 0, [], false);
}

/// <summary>Changes discarded: the paths restored from <c>Reference</c> (empty = the whole tree reset to it, <c>Short</c> / <c>Subject</c> the commit), and how many files changed.</summary>
public sealed record GitDiscardResult(GitOutcome Outcome, string Detail, IReadOnlyList<string> Paths, string Reference, string Short, string Subject, int Files)
{
    public static GitDiscardResult Refused(GitOutcome outcome, string detail = "") => new(outcome, detail, [], "", "", "", 0);
}

/// <summary>What <c>gitlib_delete</c> removes.</summary>
public enum GitDeleteKind
{
    Branch,
    Tag,
    Stash,
}

/// <summary>A branch, tag or stash removed: its name (the stash's <c>stash@{n}</c>), the short sha it stood at, a stash's message.</summary>
public sealed record GitDeleteResult(GitOutcome Outcome, string Detail, GitDeleteKind Kind, string Name, string Short, string Message)
{
    public static GitDeleteResult Refused(GitDeleteKind kind, GitOutcome outcome, string detail = "") => new(outcome, detail, kind, "", "", "");
}
