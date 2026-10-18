using LibGit2Sharp;
using NeonSidekick.Files;
using NeonSidekick.Git;

namespace NeonSidekick.App;

public static partial class SmokeChecks
{
    /// <summary>
    /// <c>git:roundtrip</c> (2026-09-20): the git tools' whole path on the published binary — a temp sandbox
    /// with a repository made by LibGit2Sharp (a local identity, so the machine's git config never matters),
    /// then every <see cref="GitAccess"/> operation the eleven tools call, in the order a session would:
    /// status on the unborn tree, stage, commit, log, a branch created and switched to, a change, the diff
    /// unstaged / staged / by commit (the app's own <see cref="UnifiedDiff"/>: LibGit2Sharp's
    /// <c>Diff.Compare&lt;T&gt;</c> killed the process under NativeAOT in the spike — libgit2sharp#2082 — so
    /// nothing here may call it), blame, a stash pushed and popped, a path discarded, the branch deleted.
    /// Three legs stand in for LibGit2Sharp's <c>GitBuf</c> answers, which come back empty on the published
    /// exe (the <see cref="GitAccess"/> note): the repository is found from a nested path (never
    /// <c>Repository.Discover</c>), the first commit's message carries whitespace the app's own
    /// <see cref="GitAccess.Prettify"/> tidies, and a remote-tracking ref with the branch's config gives
    /// <c>ahead 1 of origin/main</c> from the config and the history, never <c>Branch.TrackedBranch</c>.
    /// Since 2026-10-18 the code audit's two reads too: <see cref="GitAccess.TrackedFiles"/> lists the committed file and
    /// <see cref="GitAccess.MergeBase"/> finds where the side branch left main.
    /// The library declares no AOT support: the JIT proves nothing, this line does.
    /// </summary>
    public static SmokeCheck ProbeGit()
    {
        const string name = "git:roundtrip";
        string dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.smoke", Guid.NewGuid().ToString("N"));
        try
        {
            string root = Path.Combine(dir, "files");
            Directory.CreateDirectory(root);
            var version = GlobalSettings.Version;
            Repository.Init(root);
            using (var repo = new Repository(root))
            {
                repo.Config.Set("user.name", "Smoke", ConfigurationLevel.Local);
                repo.Config.Set("user.email", "smoke@example.invalid", ConfigurationLevel.Local);
                repo.Config.Set("core.autocrlf", false, ConfigurationLevel.Local);
                // The unborn branch is named by the machine's init.defaultBranch, master without one
                // (a CI runner); the probe's upstream and switch legs name main, so it is pinned (2026-09-21).
                repo.Refs.UpdateTarget("HEAD", "refs/heads/main");
            }

            var files = new WorkingDirectory(() => root, TimeProvider.System);
            var git = new GitAccess(files, TimeProvider.System);
            string file = Path.Combine(root, "notes.txt");
            var legs = new List<string>();

            File.WriteAllText(file, "one\n");
            Directory.CreateDirectory(Path.Combine(root, "src", "deep"));
            var unborn = git.Status(Path.Combine("src", "deep", "missing.txt"));   // found from a nested path that is not there: the walk, never Repository.Discover
            if (unborn.Outcome != GitOutcome.Ok || !unborn.Unborn || git.Status("").Untracked.Count != 1)
            {
                return Fail(name, "status", unborn.Outcome, unborn.Detail);
            }

            legs.Add("init, found from a nested path, 1 untracked");
            var staged = git.Stage("", ["notes.txt"], unstage: false);
            if (staged.Outcome != GitOutcome.Ok || staged.Paths.Count != 1)
            {
                return Fail(name, "stage", staged.Outcome, staged.Detail);
            }

            legs.Add("staged");
            var first = git.Commit("", "first  \n\n\n", amend: false, allowEmpty: false);
            if (first.Outcome != GitOutcome.Ok || first.Files != 1 || first.Subject != "first")
            {
                return Fail(name, "commit", first.Outcome, first.Detail + " subject=" + first.Subject);
            }

            // An upstream the way a clone would have one: the remote-tracking ref and the branch's two config keys.
            using (var repo = new Repository(root))
            {
                repo.Refs.Add("refs/remotes/origin/main", repo.Head.Tip.Sha);
                repo.Config.Set("branch.main.remote", "origin", ConfigurationLevel.Local);
                repo.Config.Set("branch.main.merge", "refs/heads/main", ConfigurationLevel.Local);
            }

            var tracked = git.Status("");
            if (tracked.Outcome != GitOutcome.Ok || tracked.Upstream != "origin/main" || tracked.Ahead != 0 || tracked.Behind != 0)
            {
                return Fail(name, "upstream", tracked.Outcome, tracked.Detail + " upstream=" + tracked.Upstream);
            }

            var branch = git.CreateBranch("", "feature", "", switchTo: true);
            if (branch.Outcome != GitOutcome.Ok)
            {
                return Fail(name, "branch", branch.Outcome, branch.Detail);
            }

            legs.Add("branch created and switched");
            File.WriteAllText(file, "one\ntwo\n");
            var unstagedDiff = git.Diff(new GitDiffRequest(GitDiffKind.Unstaged, ""), 500);
            if (unstagedDiff.Outcome != GitOutcome.Ok || unstagedDiff.Added != 1 || unstagedDiff.Deleted != 0 || !unstagedDiff.Patch.Contains("+two", StringComparison.Ordinal))
            {
                return Fail(name, "diff unstaged", unstagedDiff.Outcome, unstagedDiff.Detail + " " + unstagedDiff.Patch);
            }

            git.Stage("", ["."], unstage: false);
            var stagedDiff = git.Diff(new GitDiffRequest(GitDiffKind.Staged, ""), 500);
            if (stagedDiff.Outcome != GitOutcome.Ok || stagedDiff.Added != 1)
            {
                return Fail(name, "diff staged", stagedDiff.Outcome, stagedDiff.Detail);
            }

            var second = git.Commit("", "second", amend: false, allowEmpty: false);
            if (second.Outcome != GitOutcome.Ok || second.Subject != "second")
            {
                return Fail(name, "commit 2", second.Outcome, second.Detail);
            }

            legs.Add("committed ×2");
            var byCommit = git.Diff(new GitDiffRequest(GitDiffKind.Commit, "", Reference: "HEAD"), 500);
            if (byCommit.Outcome != GitOutcome.Ok || byCommit.Added != 1 || byCommit.Files.Count != 1)
            {
                return Fail(name, "diff by commit", byCommit.Outcome, byCommit.Detail);
            }

            legs.Add("diff +1 −0 unstaged / staged / by commit");
            var log = git.Log("", "", 10);
            if (log.Outcome != GitOutcome.Ok || log.Commits.Count != 2 || log.Commits[0].Subject != "second")
            {
                return Fail(name, "log", log.Outcome, log.Detail);
            }

            legs.Add("2 commits logged");
            var blame = git.Blame("notes.txt", "", null, null);
            if (blame.Outcome != GitOutcome.Ok || blame.Lines.Count != 2 || blame.Commits != 2)
            {
                return Fail(name, "blame", blame.Outcome, blame.Detail);
            }

            legs.Add("blame 2 lines by 2 commits");
            var show = git.Show("HEAD", "notes.txt");
            if (show.Outcome != GitOutcome.Ok || show.Text != "one\ntwo\n")
            {
                return Fail(name, "show", show.Outcome, show.Detail);
            }

            File.WriteAllText(file, "one\ntwo\nthree\n");
            var stashed = git.StashPush("", "wip", includeUntracked: false);
            if (stashed.Outcome != GitOutcome.Ok || stashed.Files != 1 || !git.Status("").Clean)
            {
                return Fail(name, "stash push", stashed.Outcome, stashed.Detail);
            }

            var popped = git.StashApply("", 0, pop: true);
            if (popped.Outcome != GitOutcome.Ok || git.Stashes("").Stashes.Count != 0 || File.ReadAllText(file) != "one\ntwo\nthree\n")
            {
                return Fail(name, "stash pop", popped.Outcome, popped.Detail);
            }

            legs.Add("stashed and popped");
            var discarded = git.Discard("", ["notes.txt"], "");
            if (discarded.Outcome != GitOutcome.Ok || discarded.Paths.Count != 1 || File.ReadAllText(file) != "one\ntwo\n")
            {
                return Fail(name, "discard", discarded.Outcome, discarded.Detail);
            }

            legs.Add("1 path discarded");
            var back = git.SwitchBranch("", "main");
            if (back.Outcome != GitOutcome.Ok)
            {
                return Fail(name, "switch", back.Outcome, back.Detail);
            }

            File.WriteAllText(file, "one\ntwo\nmain\n");
            git.Stage("", ["."], unstage: false);
            var third = git.Commit("", "third", amend: false, allowEmpty: false);
            var ahead = git.Status("");
            if (third.Outcome != GitOutcome.Ok || ahead.Upstream != "origin/main" || ahead.Ahead != 1 || ahead.Behind != 0)
            {
                return Fail(name, "ahead", ahead.Outcome, ahead.Detail + " ahead=" + ahead.Ahead);
            }

            legs.Add("ahead 1 of origin/main");
            // The code audit's two reads (2026-10-18): the index's files, and where feature left main (the first commit).
            var known = git.TrackedFiles("", includeUntracked: true);
            if (known.Outcome != GitOutcome.Ok || known.Tracked.Count != 1 || known.Tracked[0] != "notes.txt")
            {
                return Fail(name, "tracked files", known.Outcome, known.Detail + " " + string.Join(",", known.Tracked));
            }

            var fork = git.MergeBase("", "feature", "main");
            if (fork.Outcome != GitOutcome.Ok || fork.Sha is null || !fork.Sha.StartsWith(first.Short, StringComparison.Ordinal))
            {
                return Fail(name, "merge base", fork.Outcome, fork.Detail + " sha=" + fork.Sha);
            }

            legs.Add("1 tracked file, merge base found");
            var deleted = git.Delete("", GitDeleteKind.Branch, "feature", 0);
            if (deleted.Outcome != GitOutcome.Ok || git.Refs("").Local.Count != 1)
            {
                return Fail(name, "delete", deleted.Outcome, deleted.Detail);
            }

            legs.Add("branch deleted");
            return new SmokeCheck(name, true, $"libgit2 {version.LibGit2CommitSha} ({version.Features}); " + string.Join(", ", legs));
        }
        catch (Exception ex)
        {
            return new SmokeCheck(name, false, $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            DeleteTree(dir);
        }
    }

    private static SmokeCheck Fail(string name, string leg, GitOutcome outcome, string detail) =>
        new(name, false, $"{leg}: {outcome}" + (detail.Length > 0 ? " (" + detail + ")" : ""));

    /// <summary>Removes a temp repository: libgit2 writes read-only objects, which <c>Directory.Delete</c> refuses without the attribute cleared.</summary>
    private static void DeleteTree(string dir)
    {
        try
        {
            if (!Directory.Exists(dir))
            {
                return;
            }

            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(f, FileAttributes.Normal);
            }

            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
