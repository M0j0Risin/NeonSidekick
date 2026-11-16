namespace NeonSidekick.Git;

/// <summary>Why a git operation did not do what was asked (<see cref="GitAccess"/>); <see cref="GitText"/> turns each into its sentence.</summary>
public enum GitOutcome
{
    Ok,

    /// <summary>The path leaves the working directory (the sandbox rule, <see cref="Files.WorkingDirectory.Resolve(string, bool, out string)"/>).</summary>
    OutsideRoot,

    /// <summary>No <c>.git</c> from the path up to the working directory's root.</summary>
    NoRepository,

    /// <summary>A repository was found, but its root is above the working directory: its files are outside the sandbox.</summary>
    AboveSandbox,

    /// <summary>A bare repository (no working tree): nothing to stage, diff or check out.</summary>
    Bare,

    /// <summary>libgit2's <c>safe.directory</c> refusal: the repository is owned by another account.</summary>
    NotOwned,

    /// <summary>The path is inside the sandbox but outside the repository's working tree (a sibling of a nested repository).</summary>
    NotInRepository,

    /// <summary>The file or folder is not there (in the working tree, or in the commit asked for).</summary>
    Missing,

    /// <summary>The ref, sha or branch names nothing.</summary>
    RefNotFound,

    /// <summary>The ref names more than one object.</summary>
    RefAmbiguous,

    /// <summary>HEAD has no commit yet (a fresh <c>git init</c>).</summary>
    Unborn,

    /// <summary>A commit with nothing in the index.</summary>
    NothingStaged,

    /// <summary>An amend with no commit to amend.</summary>
    NothingToAmend,

    /// <summary>A stash with a clean working tree.</summary>
    NothingToStash,

    /// <summary>No stash at that index.</summary>
    NoStash,

    /// <summary>The index holds unmerged entries.</summary>
    Conflicts,

    /// <summary>The checkout would overwrite local changes (never forced).</summary>
    CheckoutConflict,

    /// <summary>A branch or tag of that name exists already.</summary>
    NameConflict,

    /// <summary>The branch is the one checked out: it cannot be deleted.</summary>
    CurrentBranch,

    /// <summary>git config has no <c>user.name</c> / <c>user.email</c> to sign with.</summary>
    NoIdentity,

    /// <summary>The file is binary: no text to show, blame or diff.</summary>
    Binary,

    /// <summary>The file, or the pair of texts, is too big to read whole (<see cref="Files.WorkingDirectory.MaxTextFileBytes"/>, <see cref="UnifiedDiff.MaxDistinctLines"/>).</summary>
    TooBig,

    /// <summary>More paths than one call takes (<see cref="GitAccess.MaxPathsPerCall"/>).</summary>
    TooManyPaths,

    /// <summary>An argument combination that names nothing sensible (the tool's own check).</summary>
    BadArguments,

    /// <summary>A path the write guard refuses (2026-10-22, <see cref="Files.WriteGuard"/>); the detail is the file tools' own sentence.</summary>
    Guarded,

    /// <summary>A revert asked while something is staged or a tracked file is changed (2026-11-15): the revert's commit must hold only itself.</summary>
    DirtyTree,

    /// <summary>A revert of a merge commit (2026-11-15): which parent to keep is the user's call.</summary>
    MergeCommit,

    /// <summary>A revert that would change nothing: the commit's changes are undone already (2026-11-15). Nothing was committed.</summary>
    NothingToRevert,

    /// <summary>
    /// A branch or tag name git can't hold (2026-11-15, <c>Reference.IsValidName</c>), or one that would read two ways: a leading <c>-</c>,
    /// <c>HEAD</c>, a <c>refs/</c> prefix.
    /// </summary>
    BadName,

    /// <summary>A new branch's or tag's name that already names another kind of ref, a remote branch, a folder of refs or an object (2026-11-15); the detail says which.</summary>
    NameTaken,

    /// <summary>The tagger's name or email holds a line break or NUL (2026-11-15, the tool-arguments review's #97).</summary>
    BadTagger,

    /// <summary>A tag message with a NUL (detail empty) or a line git takes for a signature (the detail; 2026-11-15, #135).</summary>
    BadMessage,

    /// <summary>A revert that conflicts (2026-11-15; run in memory, so nothing was written); the detail is <c>abc1234 conflicts in a.txt, b.txt</c>.</summary>
    RevertConflicted,

    /// <summary>A merge, rebase, revert, cherry-pick, bisect or am in progress (2026-11-15, the tool-arguments review's #40); the detail names it.</summary>
    OperationInProgress,

    /// <summary>HEAD is detached (2026-11-15, #96): a revert would land on no branch.</summary>
    Detached,

    /// <summary>A revert of a commit that isn't in the checked-out branch's history (2026-11-15, #91); the detail says which and where.</summary>
    NotOnBranch,

    /// <summary>Something on disk where a revert would add a file (2026-11-15, #8, #20): untracked or ignored; the detail names the paths.</summary>
    InTheWay,

    /// <summary>A path a revert touches is marked assume-unchanged (2026-11-15, #73): status can't see its changes; the detail names it.</summary>
    AssumeUnchanged,

    /// <summary>A path holding <c>*</c>, <c>?</c> or <c>[</c> (2026-11-15): libgit2 would read it as a glob; the detail names it.</summary>
    GlobPath,

    /// <summary>A revert that would move a submodule's pointer (2026-11-15): the checkout wouldn't update it; the detail names it.</summary>
    SubmoduleChange,

    /// <summary>A revert whose tree walk passed <see cref="GitAccess.MaxTreeEntries"/> (2026-11-15): a partial list would leave files wrong.</summary>
    TooLargeToRevert,

    /// <summary>Anything else libgit2 or the file system refused; the detail carries the message.</summary>
    Failed,
}
