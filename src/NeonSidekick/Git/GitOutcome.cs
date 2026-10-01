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

    /// <summary>Anything else libgit2 or the file system refused; the detail carries the message.</summary>
    Failed,
}
