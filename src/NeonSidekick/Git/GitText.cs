using System.Globalization;
using System.Text;

namespace NeonSidekick.Git;

/// <summary>
/// The sentences the git tools answer with: pure statics, every string pinned, the
/// <see cref="Web.WebText"/> pattern. A result is what the model reads and, cut to its first line
/// (<see cref="Note"/>), the one dim <c>🛠️</c> line the transcript shows, so every multi-line answer opens
/// with a header that stands on its own. Every error starts with <c>Error:</c>. Invariant culture;
/// a moment is shown in the caller's zone as <c>yyyy-MM-dd HH:mm</c>.
/// </summary>
public static class GitText
{
    public const string Legend = "A added · M modified · D deleted · R renamed · T type changed · ? untracked · U conflict";
    /// <summary>
    /// <c>gitlib_discard</c> given a <c>paths</c> that names nothing (2026-11-15, the tool-arguments review's #50): read as "everything" it
    /// hard-reset the tree.
    /// </summary>
    public const string DiscardNamedNothing = "Error: paths names nothing; name the paths, or leave paths out to discard everything. Nothing was discarded.";

    public const string NoPaths = "Error: give the paths to act on (\".\" for everything changed under \"path\")";
    public const string NoMessage = "Error: give the commit message";
    public const string NoName = "Error: give the branch's name";
    public const string NoNewName = "Error: give the branch's new name";
    public const string NoReference = "Error: give the commit to show (a sha, a branch, a tag, HEAD~1)";
    public const string NoBlamePath = "Error: give the file to blame";
    public const string NothingStaged = "Error: nothing is staged; stage the changes with gitlib_stage first";
    public const string NothingToAmend = "Error: there is no commit to amend yet";
    public const string NothingToStash = "Error: nothing to stash: the working tree is clean";
    public const string NoCommits = "Error: the repository has no commits yet";
    public const string NoIdentity = "Error: git has no user.name / user.email for commits; ask the user to run git config --global user.name \"…\" and git config --global user.email \"…\", then try again";
    public const string CheckoutConflict = "Error: that would overwrite local changes; commit or stash them first";
    public const string Conflicts = "Error: the index has unmerged conflicts; ask the user to resolve them first";
    public const string BadDiffArguments = "Error: give \"ref\" alone, \"from\" with \"to\", or \"staged\" — not a mix";

    /// <summary>A <c>gitlib_revert</c> with no <c>ref</c> (2026-11-15): never read as HEAD, which would revert the latest commit unasked. Pinned.</summary>
    public const string NoRevertReference = "Error: give the commit to revert (a sha, a branch, a tag, HEAD~1)";

    /// <summary>A revert while something is staged or a tracked file is changed (2026-11-15). Pinned.</summary>
    public const string DirtyTree = "Error: there are uncommitted changes (staged, or in tracked files); commit or stash them first, so the revert's commit holds only the revert";

    /// <summary>A <c>gitlib_tag</c> given a ref or a message but no name (2026-11-15): answered by the tool itself. Pinned.</summary>
    public const string TagNeedsName = "Error: give the tag's name to make one; without ref and message, gitlib_tag lists the tags";

    /// <summary>The tag list when there are none (2026-11-15). Pinned.</summary>
    public const string NoTags = "No tags";

    /// <summary>A revert of a merge commit (2026-11-15). Pinned.</summary>
    public static string MergeCommit(string sha) => $"Error: {sha} is a merge commit; which parent to keep is the user's call, so ask them to revert it";

    /// <summary>A revert that would change nothing (2026-11-15). Pinned.</summary>
    public static string NothingToRevert(string sha) => $"Error: reverting {sha} would change nothing (its changes are undone already); nothing was committed";

    /// <summary>A revert that conflicts (2026-11-15; run in memory, nothing was written); <paramref name="detail"/> is <see cref="ConflictsIn"/>. Pinned.</summary>
    public static string RevertConflicts(string detail) => $"Error: reverting {detail}; nothing changed";

    /// <summary><c>abc1234 conflicts in a.txt, b.txt</c>, the paths whole (2026-11-15, the tool-arguments review's #124).</summary>
    public static string ConflictsIn(string sha, string names) => sha + " conflicts in " + names;

    /// <summary>
    /// git's default message for a revert (2026-11-15, the tool-arguments review's #9: moved here from <see cref="GitAccess"/>), used when the
    /// model gives none. Pinned against git's own wording.
    /// </summary>
    public static string RevertMessage(string subject, string sha) => "Revert \"" + subject + "\"\n\nThis reverts commit " + sha + ".\n";

    /// <summary>The reflog line a revert's ref move writes (2026-11-15).</summary>
    public static string RevertReflog(string subject) => "revert: " + subject;

    /// <summary>The word for an operation in progress (2026-11-15, #40), as git's own messages name it.</summary>
    public static string OperationWord(LibGit2Sharp.CurrentOperation operation) => operation switch
    {
        LibGit2Sharp.CurrentOperation.Merge => "merge",
        LibGit2Sharp.CurrentOperation.Revert or LibGit2Sharp.CurrentOperation.RevertSequence => "revert",
        LibGit2Sharp.CurrentOperation.CherryPick or LibGit2Sharp.CurrentOperation.CherryPickSequence => "cherry-pick",
        LibGit2Sharp.CurrentOperation.Bisect => "bisect",
        LibGit2Sharp.CurrentOperation.ApplyMailbox or LibGit2Sharp.CurrentOperation.ApplyMailboxOrRebase => "git am",
        _ => "rebase",
    };

    /// <summary>A revert while git is mid-operation (2026-11-15, #40). Pinned.</summary>
    public static string OperationInProgress(string operation) => $"Error: a {operation} is in progress in the repository; ask the user to finish or abort it first";

    /// <summary>A revert on a detached HEAD (2026-11-15, #96). Pinned.</summary>
    public const string Detached = "Error: HEAD is detached (no branch is checked out); switch to a branch first, so the revert lands on it";

    /// <summary>The detail of a revert of a commit off the branch (2026-11-15, #91).</summary>
    public static string NotOnBranchDetail(string sha, string branch) => sha + " is not in " + branch + "'s history";

    /// <summary>A revert of a commit off the branch (2026-11-15, #91). Pinned.</summary>
    public static string NotOnBranch(string detail) => $"Error: {detail}; revert only a commit on the branch checked out";

    /// <summary>Something on disk where a revert would add a file (2026-11-15, #8, #20). Pinned.</summary>
    public static string InTheWay(string names) => $"Error: the revert would add {names}, but something git doesn't track is there already; ask the user to move it aside first";

    /// <summary>A touched path marked assume-unchanged (2026-11-15, #73). Pinned.</summary>
    public static string AssumeUnchanged(string names) => $"Error: {names} is marked assume-unchanged, so its local changes can't be seen; ask the user to clear the mark (git update-index --no-assume-unchanged) first";

    /// <summary>A path holding a glob character (2026-11-15): the git library would read it as a pattern. Pinned.</summary>
    public static string GlobPath(string names) => $"Error: {names} holds *, ? or [, which the git library reads as a pattern; use git for it";

    /// <summary>A revert that would move a submodule's pointer (2026-11-15). Pinned.</summary>
    public static string SubmoduleChange(string path) => $"Error: the revert would move the submodule {path}; revert it with git";

    /// <summary>A revert whose tree is too large to walk whole (2026-11-15). Pinned.</summary>
    public static string TooLargeToRevert(string sha) => $"Error: reverting {sha} touches a tree too large to walk here; revert it with git";

    /// <summary>A checkout refused for local changes, naming the paths (2026-11-15, a revert's). Pinned.</summary>
    public static string CheckoutConflictIn(string names) => $"Error: that would overwrite local changes in {names}; commit or stash them first";

    /// <summary>
    /// The detail of a revert whose checkout or ref move failed (2026-11-15): libgit2's message, and the files the put-back couldn't restore
    /// when there are any.
    /// </summary>
    public static string RevertFailed(string why, string left) => left.Length == 0 ? why + "; nothing was changed" : why + "; " + left + " may be left changed, so check gitlib_status";

    /// <summary>A refused revert's sentence (2026-11-15): <see cref="Error"/>, but a checkout conflict names its paths.</summary>
    public static string RevertError(GitRevertResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Outcome == GitOutcome.CheckoutConflict && result.Detail.Length > 0 ? CheckoutConflictIn(result.Detail) : Error(result.Outcome, result.Detail);
    }

    /// <summary>A branch or tag name git can't hold, or one that would read two ways (2026-11-15). Pinned.</summary>
    public static string BadRefName(string name) => $"Error: '{Llm.Tools.ArgumentText.Echo(name)}' can't be a branch or tag name (git can't hold it, or it would read as something else)";

    /// <summary>A new name that already names something else (2026-11-15, the tool-arguments review's #24, #64, #139). Pinned.</summary>
    public static string NameTaken(string detail) => $"Error: {detail}; pick another name, so it reads one way in every git tool";

    /// <summary><c>'main' already names a branch</c>: <see cref="NameTaken"/>'s detail.</summary>
    public static string NameTakenDetail(string name, string what) => $"'{Llm.Tools.ArgumentText.Echo(name)}' already names {what}";

    public const string TakenByBranch = "a branch";
    public const string TakenByTag = "a tag";
    public const string TakenByRemote = "a remote branch";
    public const string TakenByFolder = "a folder of refs, or sits in one";
    public const string TakenByObject = "a commit or another object (as a sha)";

    /// <summary>A tag named <c>list</c> (2026-11-15): the model meant the list. Pinned.</summary>
    public const string TagNamedList = "Error: 'list' isn't taken as a tag name here; leave name out to list the tags";

    /// <summary>
    /// An annotated tag with no identity to sign it (2026-11-15, the tool-arguments review's #94): <see cref="NoIdentity"/> speaks of commits, and
    /// a lightweight tag needs none. Pinned.
    /// </summary>
    public const string NoTagIdentity = "Error: git has no user.name / user.email to sign an annotated tag; ask the user to set them (/gituser), or leave message out for a lightweight tag";

    /// <summary>A tagger with a line break or NUL in the name or email (2026-11-15, #97). Pinned.</summary>
    public const string BadTagger = "Error: git's user.name or user.email holds a line break or NUL, which would corrupt the tag; ask the user to fix it";

    /// <summary>A tag message holding NUL (2026-11-15, #135). Pinned.</summary>
    public const string MessageHasNul = "Error: the tag message holds a NUL character; leave it out";

    /// <summary>A tag message line git would read as a signature's start (2026-11-15, #135). Pinned.</summary>
    public static string SignatureLine(string line) => $"Error: the tag message's line '{Llm.Tools.ArgumentText.Echo(line)}' would read to git as a signature; leave that line out";

    /// <summary>An annotated tag's row subject (2026-11-15, #130): its own message's first line, marked.</summary>
    public static string AnnotatedSubject(string subject) => "(annotated) " + subject;

    /// <summary>A tag row's subject: an annotated tag's marked (<see cref="AnnotatedSubject"/>, the mark outside), the words fenced (#25).</summary>
    private static string TagSubject(GitRef tag) => tag.Annotated ? AnnotatedSubject(Outside(tag.Subject)) : Outside(tag.Subject);

    /// <summary>
    /// A ref name from another clone (2026-11-16, the web-tools review's #20): a remote-tracking branch, an upstream, a tag. A clone carries
    /// whoever named them, so they are fenced as its commits' words are; the user's local branches stay bare.
    /// </summary>
    private static string ForeignName(string name) => Llm.Tools.UntrustedText.Fenced(name);

    public static string NoRepository(string root) => $"Error: '{root}' is not inside a git repository; /cwd into one, or ask the user to git init it";
    public static string AboveSandbox(string workTree) => $"Error: the repository's root '{workTree}' is above the working directory, so the git tools cannot reach it; the user can /cwd to the repository's root";
    public static string Bare(string path) => $"Error: '{path}' is a bare repository (no working tree)";
    public static string NotOwned(string detail) => $"Error: git refuses the repository ({detail}); the user can allow it with git config --global --add safe.directory <path>";
    public static string OutsideRoot(string path) => $"Error: '{path}' is outside the working directory";
    public static string NotInRepository(string path) => $"Error: '{path}' is not inside the repository";
    public static string Missing(string path) => $"Error: '{path}' is not there";
    public static string RefNotFound(string reference) => $"Error: '{reference}' names no commit, branch or tag";
    public static string RefAmbiguous(string reference) => $"Error: '{reference}' is ambiguous; give a longer sha or the full name";
    public static string NoStash(string index) => $"Error: there is no stash@{{{index}}}";
    public static string NameConflict(string name) => $"Error: '{Llm.Tools.ArgumentText.Echo(name)}' exists already";
    public static string CannotDeleteCurrentBranch(string name) => $"Error: '{name}' is the branch checked out; switch to another first";
    public static string CannotStageRoot(string path) => $"Error: '{path}' is the repository itself; give \".\" to stage everything under \"path\"";
    public static string Binary(string path) => $"Error: '{path}' is binary";
    public static string TooBig(string path) => $"Error: '{path}' is too big to read as text";
    public static string TooManyPaths(int max) => $"Error: at most {N(max)} paths in one call";
    public static string BadAction(string action) => $"Error: '{action}' is not an action here";
    public static string BadKind(string kind) => $"Error: '{kind}' is not a kind here";
    public static string BadLogCount(int min, int max) => $"Error: max_commits must be {N(min)} to {N(max)}";
    public static string BadDiffLines(int min, int max) => $"Error: max_lines must be {N(min)} to {N(max)}";
    public static string BadLine(string argument) => $"Error: {argument} must be 1 or more";
    public static string Failed(string detail) => $"Error: git refused ({detail})";
    public static string CreatedButNotSwitched(string name, string sha, string why) => $"Created branch {name} at {sha}, but the switch failed: {why}";

    /// <summary>The transcript's one-line note for a result: its first line.</summary>
    public static string Note(string result)
    {
        ArgumentNullException.ThrowIfNull(result);
        int newline = result.IndexOf('\n');
        return newline < 0 ? result : result[..newline];
    }

    /// <summary>The sentence for a refused outcome; the detail names the path, the ref or the message it needs.</summary>
    public static string Error(GitOutcome outcome, string detail) => outcome switch
    {
        GitOutcome.OutsideRoot => OutsideRoot(detail),
        GitOutcome.NoRepository => NoRepository(detail),
        GitOutcome.AboveSandbox => AboveSandbox(detail),
        GitOutcome.Bare => Bare(detail),
        GitOutcome.NotOwned => NotOwned(detail),
        GitOutcome.NotInRepository => NotInRepository(detail),
        GitOutcome.Missing => Missing(detail),
        GitOutcome.RefNotFound => RefNotFound(detail),
        GitOutcome.RefAmbiguous => RefAmbiguous(detail),
        GitOutcome.Unborn => NoCommits,
        GitOutcome.NothingStaged => NothingStaged,
        GitOutcome.NothingToAmend => NothingToAmend,
        GitOutcome.NothingToStash => NothingToStash,
        GitOutcome.NoStash => NoStash(detail),
        GitOutcome.Conflicts => Conflicts,
        GitOutcome.CheckoutConflict => CheckoutConflict,
        GitOutcome.NameConflict => NameConflict(detail),
        GitOutcome.CurrentBranch => CannotDeleteCurrentBranch(detail),
        GitOutcome.NoIdentity => NoIdentity,
        GitOutcome.Binary => Binary(detail),
        GitOutcome.TooBig => TooBig(detail),
        GitOutcome.TooManyPaths => TooManyPaths(GitAccess.MaxPathsPerCall),
        GitOutcome.BadArguments => CannotStageRoot(detail),
        GitOutcome.Guarded => detail,
        GitOutcome.DirtyTree => DirtyTree,
        GitOutcome.MergeCommit => MergeCommit(detail),
        GitOutcome.NothingToRevert => NothingToRevert(detail),
        GitOutcome.BadName => BadRefName(detail),
        GitOutcome.NameTaken => NameTaken(detail),
        GitOutcome.BadTagger => BadTagger,
        GitOutcome.BadMessage => detail.Length == 0 ? MessageHasNul : SignatureLine(detail),
        GitOutcome.RevertConflicted => RevertConflicts(detail),
        GitOutcome.OperationInProgress => OperationInProgress(detail),
        GitOutcome.Detached => Detached,
        GitOutcome.NotOnBranch => NotOnBranch(detail),
        GitOutcome.InTheWay => InTheWay(detail),
        GitOutcome.AssumeUnchanged => AssumeUnchanged(detail),
        GitOutcome.GlobPath => GlobPath(detail),
        GitOutcome.SubmoduleChange => SubmoduleChange(detail),
        GitOutcome.TooLargeToRevert => TooLargeToRevert(detail),
        _ => Failed(detail),
    };

    // ---- status ----

    /// <summary><c>On branch main (ahead 2 of origin/main): 1 staged, 2 modified, 1 untracked</c>, then the sections.</summary>
    public static string Status(GitStatusReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var sb = new StringBuilder();
        sb.Append(report.Detached ? "Detached at " + report.HeadShort : "On branch " + report.Branch);
        var notes = new List<string>();
        if (report.Unborn)
        {
            notes.Add("no commits yet");
        }

        if (report.Upstream is { } upstream)
        {
            if (report.Ahead is > 0 && report.Behind is > 0)
            {
                notes.Add($"ahead {N(report.Ahead.Value)}, behind {N(report.Behind.Value)} of {upstream}");
            }
            else if (report.Ahead is > 0)
            {
                notes.Add($"ahead {N(report.Ahead.Value)} of {upstream}");
            }
            else if (report.Behind is > 0)
            {
                notes.Add($"behind {N(report.Behind.Value)} of {upstream}");
            }
            else
            {
                notes.Add($"up to date with {upstream}");
            }
        }

        if (notes.Count > 0)
        {
            sb.Append(" (").Append(string.Join(", ", notes)).Append(')');
        }

        sb.Append(": ");
        if (report.Clean)
        {
            sb.Append("clean");
            return sb.ToString();
        }

        var counts = new List<string>();
        if (report.Conflicts.Count > 0)
        {
            counts.Add(Count(report.Conflicts.Count, "conflict"));
        }

        if (report.Staged.Count > 0)
        {
            counts.Add(N(report.Staged.Count) + " staged");
        }

        if (report.Unstaged.Count > 0)
        {
            counts.Add(N(report.Unstaged.Count) + " modified");
        }

        if (report.Untracked.Count > 0)
        {
            counts.Add(N(report.Untracked.Count) + " untracked");
        }

        sb.Append(string.Join(", ", counts));
        if (report.Truncated)
        {
            sb.Append(" (the first ").Append(N(GitAccess.MaxStatusEntries)).Append(" entries)");
        }

        Section(sb, "conflicts", report.Conflicts);
        Section(sb, "staged", report.Staged);
        Section(sb, "unstaged", report.Unstaged);
        Section(sb, "untracked", report.Untracked);
        sb.Append('\n').Append(Legend);
        return sb.ToString();
    }

    private static void Section(StringBuilder sb, string title, IReadOnlyList<GitEntry> entries)
    {
        if (entries.Count == 0)
        {
            return;
        }

        sb.Append('\n').Append(title).Append(':');
        foreach (var entry in entries)
        {
            sb.Append('\n').Append(entry.Code).Append("  ").Append(entry.OldPath is { } old ? old + " → " + entry.Path : entry.Path);
        }
    }

    // ---- log ----

    /// <summary>
    /// <c>20 commits on main, newest first:</c> / <c>12 commits touching src\a.cs on main, newest first:</c> then one row per commit. The filters
    /// are named (2026-11-15): <c>5 commits by bob matching "parser" since 2026-11-08 00:00 on main, newest first:</c>, the times in the user's
    /// zone and the model's text echoed short; a walk that stopped at its cap while skipping commits says only the newest were searched.
    /// </summary>
    public static string Log(GitLogReport report, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(zone);
        var sb = new StringBuilder();
        string filters = Filters(report.Filter, zone);
        string searched = report.WalkCapped ? " (searched the newest " + report.WalkCap.ToString("N0", CultureInfo.InvariantCulture) + " commits)" : "";
        if (report.Commits.Count == 0)
        {
            sb.Append(report.Path is { } none ? $"No commits touch {none}" : "No commits").Append(filters).Append(" on ").Append(report.Reference).Append(searched);
            return sb.ToString();
        }

        sb.Append(Count(report.Commits.Count, "commit"));
        if (report.Path is { } path)
        {
            sb.Append(" touching ").Append(path);
        }

        sb.Append(filters).Append(" on ").Append(report.Reference).Append(", newest first");
        if (report.Truncated)
        {
            sb.Append(" (more before them)");
        }
        else if (report.MaybeMore)
        {
            sb.Append(" (more may be before them)");
        }

        sb.Append(searched);

        sb.Append(':');
        foreach (var commit in report.Commits)
        {
            sb.Append('\n').Append(CommitRow(commit, zone));
        }

        return Llm.Tools.UntrustedText.WithNote(sb.ToString());
    }

    /// <summary>The filters as the header names them (2026-11-15): <c> by bob matching "parser" since … until …</c>; empty for none.</summary>
    private static string Filters(GitLogFilter? filter, TimeZoneInfo zone)
    {
        if (filter is null)
        {
            return "";
        }

        var sb = new StringBuilder();
        if (filter.Author is { } author)
        {
            sb.Append(" by ").Append(Llm.Tools.ArgumentText.Echo(author));
        }

        if (filter.Grep is { } grep)
        {
            sb.Append(" matching \"").Append(Llm.Tools.ArgumentText.Echo(grep)).Append('"');
        }

        if (filter.Since is { } since)
        {
            sb.Append(" since ").Append(Moment(since, zone));
        }

        if (filter.Until is { } until)
        {
            sb.Append(" until ").Append(Moment(until, zone));
        }

        return sb.ToString();
    }

    /// <summary>A filter's moment in the header (2026-11-15, the tool-arguments review's #56): <see cref="When"/>, with the seconds when they aren't zero.</summary>
    public static string Moment(DateTimeOffset when, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(when, zone);
        return local.Second == 0 ? When(when, zone) : local.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    /// <summary>A window that ends before it starts (2026-11-15). Pinned.</summary>
    public const string SinceAfterUntil = "Error: since is later than until; nothing could match.";

    /// <summary>
    /// <c>abc1234 2026-09-20 14:05 ⟪Chris⟫: ⟪subject⟫</c>: the author and the subject are whoever made the commit's words, fenced in place
    /// (2026-11-15, the web-tools plan's §3).
    /// </summary>
    public static string CommitRow(GitCommitInfo commit, TimeZoneInfo zone) =>
        $"{commit.Short} {When(commit.When, zone)} {Outside(commit.Author)}: {Outside(commit.Subject)}";

    /// <summary>
    /// A commit's words (an author, a subject, a message) as an answer shows them (2026-11-15, the web-tools plan's §3): fenced, since a
    /// clone carries whoever wrote them; blank stays blank. An annotated tag's mark is the row's (<see cref="GitRef.Annotated"/>, 2026-11-16, the
    /// web-tools review's #25), never read back out of the words.
    /// </summary>
    public static string Outside(string? text) => string.IsNullOrWhiteSpace(text) ? text ?? "" : Llm.Tools.UntrustedText.Fenced(text);

    // ---- show ----

    /// <summary>A commit's header, message and files; a file's text; a folder's entries.</summary>
    public static string Show(GitShowReport report, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(zone);
        var commit = report.Commit!;
        var sb = new StringBuilder();
        if (report.Path is null)
        {
            // The author, subject and message fenced (2026-11-15, the web-tools plan's §3); the files changed are the app's list.
            sb.Append("Commit ").Append(commit.Short).Append(" by ").Append(Outside(commit.Author)).Append(" at ").Append(When(commit.When, zone)).Append(": ").Append(Outside(commit.Subject));
            string body = commit.Message.Trim();
            if (body.Length > commit.Subject.Length)
            {
                sb.Append('\n').Append(Outside(body));
            }

            sb.Append('\n').Append(FilesChanged(report.Changes, report.ChangesTruncated));
            return Llm.Tools.UntrustedText.WithNote(sb.ToString());
        }

        if (report.Text is null)
        {
            sb.Append(report.Path).Append(" at ").Append(commit.Short).Append(" (").Append(Count(report.Entries.Count, "entry", "entries")).Append(report.Cut ? ", the first " + N(Files.WorkingDirectory.MaxEntries) : "").Append("):");
            foreach (var entry in report.Entries)
            {
                sb.Append('\n').Append(entry);
            }

            return sb.ToString();
        }

        sb.Append(report.Path).Append(" at ").Append(commit.Short).Append(" (").Append(Count(report.Lines, "line")).Append(report.Cut ? ", cut at " + N(GitAccess.MaxShowChars) + " characters" : "").Append("):\n");
        sb.Append(report.Text);
        return sb.ToString();
    }

    /// <summary><c>Files changed (3, +10 −3): M src\a.cs (+4 −1)</c> …</summary>
    public static string FilesChanged(IReadOnlyList<GitChange> changes, bool truncated)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.Count == 0)
        {
            return "No files changed";
        }

        var sb = new StringBuilder();
        sb.Append("Files changed (").Append(N(changes.Count)).Append(truncated ? "+" : "").Append(", +").Append(N(changes.Sum(c => c.Added))).Append(" −").Append(N(changes.Sum(c => c.Deleted))).Append("):");
        foreach (var change in changes)
        {
            sb.Append('\n').Append(ChangeRow(change));
        }

        if (truncated)
        {
            sb.Append("\n… and more (the first ").Append(N(GitAccess.MaxChangedFiles)).Append(" shown)");
        }

        return sb.ToString();
    }

    public static string ChangeRow(GitChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        string path = change.OldPath is { } old ? old + " → " + change.Path : change.Path;
        return change.Binary ? $"{change.Code} {path} (binary)" : $"{change.Code} {path} (+{N(change.Added)} −{N(change.Deleted)})";
    }

    // ---- diff ----

    /// <summary><c>Unstaged changes (2 files, +10 −3):</c> the file rows, a blank line, the patch, a cut note when it stopped early.</summary>
    public static string Diff(GitDiffReport report, string path)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(path);
        string what = report.Kind switch
        {
            GitDiffKind.Unstaged => "Unstaged changes",
            GitDiffKind.Staged => "Staged changes",
            GitDiffKind.Commit => "Changes in " + Labelled(report),
            _ => "Changes from " + report.Label,
        };
        string under = path.Length > 0 ? " under " + path : "";
        if (report.Files.Count == 0)
        {
            return report.Kind switch
            {
                GitDiffKind.Unstaged => "No unstaged changes" + under,
                GitDiffKind.Staged => "No staged changes" + under,
                _ => "No changes " + (report.Kind == GitDiffKind.Commit ? "in " + Labelled(report) : "from " + report.Label) + under,
            };
        }

        var sb = new StringBuilder();
        sb.Append(what).Append(under).Append(" (").Append(Count(report.Files.Count, "file")).Append(report.FilesTruncated ? "+" : "").Append(", +").Append(N(report.Added)).Append(" −").Append(N(report.Deleted)).Append("):");
        foreach (var change in report.Files)
        {
            sb.Append('\n').Append(ChangeRow(change));
        }

        if (report.FilesTruncated)
        {
            sb.Append("\n… and more (the first ").Append(N(GitAccess.MaxChangedFiles)).Append(" files shown)");
        }

        if (report.Patch.Length > 0)
        {
            sb.Append("\n\n").Append(report.Patch);
        }

        if (report.Truncated)
        {
            sb.Append('\n').Append(DiffCutSuffix(report.TotalLines));
        }

        return sb.ToString();
    }

    public static string DiffCutSuffix(int total) => $"[… cut: {N(total)} lines in all; raise max_lines or narrow with path]";

    // ---- blame ----

    /// <summary><c>Blame of src\a.cs lines 1–120 of 120 at main (5 commits):</c> then <c>abc1234 2026-09-01 Chris   12│ text</c> per line.</summary>
    public static string Blame(GitBlameReport report, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(zone);
        if (report.TotalLines == 0)
        {
            return $"{report.Path} at {report.Reference} is empty";
        }

        var sb = new StringBuilder();
        sb.Append("Blame of ").Append(report.Path).Append(" lines ").Append(N(report.From)).Append('–').Append(N(report.To)).Append(" of ").Append(N(report.TotalLines))
          .Append(" at ").Append(report.Reference).Append(" (").Append(Count(report.Commits, "commit")).Append("):");
        int width = N(report.To).Length;
        // Each author fenced in place (2026-11-15, the web-tools plan's §3); the lines are the file's, as read_file leaves them.
        var authors = report.Lines.Select(l => Outside(l.Author)).ToList();
        int author = authors.Count == 0 ? 0 : authors.Max(a => a.Length);
        for (int i = 0; i < report.Lines.Count; i++)
        {
            var line = report.Lines[i];
            sb.Append('\n').Append(line.Short).Append(' ').Append(Day(line.When, zone)).Append(' ').Append(authors[i].PadRight(author)).Append(' ').Append(N(line.Number).PadLeft(width)).Append('│').Append(line.Text);
        }

        if (report.To < report.TotalLines)
        {
            sb.Append("\n[… ").Append(N(report.TotalLines - report.To)).Append(" more lines; continue with from_line ").Append(N(report.To + 1)).Append(']');
        }

        return authors.Any(a => a.Length > 0) ? sb.Append('\n').Append(Llm.Tools.UntrustedText.QuoteNote).ToString() : sb.ToString();
    }

    // A commit diff's label with its subject fenced beside it: abc1234 (⟪subject⟫).
    private static string Labelled(GitDiffReport report) =>
        report.Subject is { } subject ? report.Label + " (" + Outside(subject) + ")" : report.Label;

    // ---- refs ----

    /// <summary><c>3 branches (current: main), 2 remote-tracking, 4 tags:</c> then the rows.</summary>
    public static string Refs(GitRefsReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var sb = new StringBuilder();
        sb.Append(Count(report.Local.Count, "branch", "branches"));
        sb.Append(report.Detached ? " (HEAD detached)" : report.Current is { } current ? " (current: " + current + ")" : "");
        if (report.Remote.Count > 0)
        {
            sb.Append(", ").Append(N(report.Remote.Count)).Append(" remote-tracking");
        }

        if (report.Tags.Count > 0)
        {
            sb.Append(", ").Append(Count(report.Tags.Count, "tag"));
        }

        sb.Append(report.Truncated ? " (cut at the caps):" : ":");
        foreach (var b in report.Local)
        {
            sb.Append('\n').Append(b.Current ? "* " : "  ").Append(b.Name).Append(' ').Append(b.Short).Append(b.Upstream is { } up ? " [" + ForeignName(up) + "]" : "").Append(' ').Append(Outside(b.Subject));
        }

        foreach (var b in report.Remote)
        {
            sb.Append("\n  ").Append(ForeignName(b.Name)).Append(' ').Append(b.Short).Append(' ').Append(Outside(b.Subject));
        }

        foreach (var t in report.Tags)
        {
            sb.Append("\n  tag ").Append(ForeignName(t.Name)).Append(' ').Append(t.Short).Append(' ').Append(TagSubject(t));
        }

        return Llm.Tools.UntrustedText.WithNote(sb.ToString());
    }

    public static string Created(GitBranchResult result) => $"Created branch {result.Name} at {result.Short}";

    // ---- revert / tag (2026-11-15) ----

    /// <summary><c>Reverted abc1234 with def5678 on main: Revert "…" (2 files)</c>: a first line that stands alone. Pinned.</summary>
    public static string Reverted(GitRevertResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return Llm.Tools.UntrustedText.WithNote($"Reverted {result.Target} with {result.Short} on {result.Branch}: {Outside(result.Subject)} ({Count(result.Files, "file")})");
    }

    /// <summary><c>Tagged abc1234 as v1.0 (annotated)</c>. Pinned.</summary>
    public static string Tagged(GitTagResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return $"Tagged {result.Short} as {result.Name} ({(result.Annotated ? "annotated" : "lightweight")})";
    }

    /// <summary><c>3 tags, newest first:</c> then a row each, as <see cref="Refs"/> shows them; <see cref="NoTags"/> for none. Pinned.</summary>
    public static string Tags(GitTagsReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (report.Tags.Count == 0)
        {
            return NoTags;
        }

        var sb = new StringBuilder(Count(report.Tags.Count, "tag")).Append(", newest first");
        sb.Append(report.Truncated ? " (the newest " + N(GitAccess.MaxTags) + "):" : ":");
        foreach (var t in report.Tags)
        {
            sb.Append("\n  ").Append(ForeignName(t.Name)).Append(' ').Append(t.Short).Append(' ').Append(TagSubject(t));
        }

        return Llm.Tools.UntrustedText.WithNote(sb.ToString());
    }
    public static string CreatedAndSwitched(GitBranchResult result) => $"Created branch {result.Name} at {result.Short} and switched to it";
    public static string Switched(GitBranchResult result) => $"Switched to branch {result.Name} ({result.Short})";
    public static string Renamed(GitBranchResult result) => $"Renamed branch {result.OldName} to {result.Name}";

    // ---- stage / commit ----

    public static string Staged(GitStageResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        string verb = result.Unstage ? "Unstaged" : "Staged";
        if (result.Paths.Count == 0)
        {
            return $"Nothing to {(result.Unstage ? "unstage" : "stage")}" + (result.Folder.Length > 0 ? " under " + result.Folder : "");
        }

        return $"{verb} {Count(result.Paths.Count, "path")}: {string.Join(", ", result.Paths)}";
    }

    public static string Committed(GitCommitResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return $"{(result.Amended ? "Amended" : "Committed")} {result.Short} on {result.Branch}: {result.Subject} ({Count(result.Files, "file")}, +{N(result.Added)} −{N(result.Deleted)})";
    }

    // ---- stash ----

    // Stash messages fenced (2026-11-16, the web-tools review's #68): the default "WIP on main: abc <subject>" quotes HEAD's subject, a clone's words.
    public static string Stashed(GitStashResult result) =>
        Llm.Tools.UntrustedText.WithNote($"Stashed {Count(result.Files, "file")} as {GitAccess.StashName(0)}: {Outside(result.Message)}");

    public static string StashApplied(GitStashResult result, bool pop) =>
        Llm.Tools.UntrustedText.WithNote($"{(pop ? "Popped" : "Applied")} {GitAccess.StashName(result.Index)}: {Outside(result.Message)} ({Count(result.Files, "file")} back)");

    public static string Stashes(GitStashResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Stashes.Count == 0)
        {
            return "No stashes";
        }

        var sb = new StringBuilder();
        sb.Append(Count(result.Stashes.Count, "stash", "stashes")).Append(result.Truncated ? " (the first " + N(GitAccess.MaxStashes) + "):" : ":");
        foreach (var s in result.Stashes)
        {
            sb.Append('\n').Append(GitAccess.StashName(s.Index)).Append(' ').Append(s.Short).Append(' ').Append(Outside(s.Message));
        }

        return Llm.Tools.UntrustedText.WithNote(sb.ToString());
    }

    // ---- discard / delete ----

    public static string Discarded(GitDiscardResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Paths.Count == 0
            ? Llm.Tools.UntrustedText.WithNote($"Reset hard to {result.Reference} ({result.Short}: {Outside(result.Subject)}): {Count(result.Files, "file")} changed; untracked files left alone")
            : $"Discarded changes in {Count(result.Paths.Count, "path")} (back to {result.Reference}): {string.Join(", ", result.Paths)}";
    }

    public static string Deleted(GitDeleteResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Kind switch
        {
            GitDeleteKind.Branch => $"Deleted branch {result.Name} (was {result.Short})",
            GitDeleteKind.Tag => $"Deleted tag {result.Name} (was {result.Short})",
            _ => Llm.Tools.UntrustedText.WithNote($"Dropped {result.Name}: {Outside(result.Message)}"),
        };
    }

    // ---- helpers ----

    /// <summary><c>yyyy-MM-dd HH:mm</c> in <paramref name="zone"/>.</summary>
    public static string When(DateTimeOffset when, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(when, zone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary><c>yyyy-MM-dd</c> in <paramref name="zone"/>.</summary>
    public static string Day(DateTimeOffset when, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(when, zone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string Count(int n, string singular, string? plural = null) =>
        N(n) + " " + (n == 1 ? singular : plural ?? singular + "s");

    private static string N(int n) => n.ToString(CultureInfo.InvariantCulture);
}
