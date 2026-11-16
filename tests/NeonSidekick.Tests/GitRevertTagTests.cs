using LibGit2Sharp;
using NeonSidekick.Diagnostics;
using NeonSidekick.Git;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// <see cref="GitAccess"/>'s log filters, revert and tag (2026-11-15, feature ideas #44 and #32) over real temp repositories: each filter and
/// the walk cap's note, the revert's refusals (nothing written) and its one put-back (HEAD, files and no revert state left), the tags read back
/// through LibGit2Sharp.
/// </summary>
public sealed class GitRevertTagTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly ManualTimeProvider _time = new();
    private readonly GitAccess _git;

    public GitRevertTagTests()
    {
        _root = Path.Combine(_dir, "files");
        Directory.CreateDirectory(_root);
        _git = new GitAccess(new Files.WorkingDirectory(() => _root, _time), _time);
    }

    public void Dispose() => GitAccessTests.DeleteTree(_dir);

    private string Init() => GitAccessTests.Init(_root);

    private string Commit(string relative, string content, string message) => GitAccessTests.CommitFile(_root, relative, content, message);

    private static readonly DateTimeOffset Base = new(2026, 9, 1, 12, 0, 0, TimeSpan.FromHours(-7));

    /// <summary>A commit by <paramref name="name"/> at <paramref name="minutes"/> past <see cref="Base"/>; the sha.</summary>
    private string CommitAs(string relative, string content, string message, string name, string email, int minutes)
    {
        Write(relative, content);
        using var repo = new Repository(_root);
        Commands.Stage(repo, relative);
        var sig = new Signature(name, email, Base.AddMinutes(minutes));
        return repo.Commit(message, sig, sig).Sha;
    }

    /// <summary>Deletes <paramref name="relative"/> and commits it (with whatever else is written beforehand, staged); the sha.</summary>
    private string CommitDelete(string relative, string message, params string[] alsoStage)
    {
        File.Delete(Path.Combine(_root, relative));
        using var repo = new Repository(_root);
        Commands.Stage(repo, relative);
        foreach (string other in alsoStage)
        {
            Commands.Stage(repo, other);
        }

        var sig = new Signature("Test User", "test@example.invalid", Base.AddHours(1));
        return repo.Commit(message, sig, sig).Sha;
    }

    private void Write(string relative, string content)
    {
        string full = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private string Read(string relative) => File.ReadAllText(Path.Combine(_root, relative));

    private (string Head, CurrentOperation Operation, bool Dirty) State()
    {
        using var repo = new Repository(_root);
        return (repo.Head.Tip.Sha, repo.Info.CurrentOperation, repo.RetrieveStatus(new StatusOptions { IncludeUntracked = true }).IsDirty);
    }

    // ---- log filters ----

    [Fact]
    public void Log_FiltersByTime_Author_AndMessage_EachEndInclusive()
    {
        Init();
        CommitAs("a.txt", "1\n", "Add the parser", "Bob Example", "bob@example.invalid", 1);
        CommitAs("a.txt", "2\n", "Fix the lexer", "Alice Example", "alice@example.invalid", 2);
        CommitAs("a.txt", "3\n", "Tidy the PARSER tests", "bob", "robert@example.invalid", 3);
        CommitAs("a.txt", "4\n", "Release notes", "Carol", "carol@example.invalid", 4);

        string[] Subjects(GitLogFilter filter) => _git.Log("", "", 10, filter).Commits.Select(c => c.Subject).ToArray();

        Assert.Equal(["Tidy the PARSER tests", "Add the parser"], Subjects(new GitLogFilter(Author: "BOB")));
        Assert.Equal(["Fix the lexer"], Subjects(new GitLogFilter(Author: "alice@")));   // the email too
        Assert.Equal(["Tidy the PARSER tests", "Add the parser"], Subjects(new GitLogFilter(Grep: "parser")));
        Assert.Equal(["Tidy the PARSER tests", "Fix the lexer"], Subjects(new GitLogFilter(Since: Base.AddMinutes(2), Until: Base.AddMinutes(3))));
        Assert.Equal(["Release notes"], Subjects(new GitLogFilter(Since: Base.AddMinutes(4))));
        Assert.Equal(["Add the parser"], Subjects(new GitLogFilter(Until: Base.AddMinutes(1))));
        Assert.Equal(["Tidy the PARSER tests"], Subjects(new GitLogFilter(Since: Base.AddMinutes(2), Author: "bob", Grep: "tests")));
        Assert.Empty(Subjects(new GitLogFilter(Author: "dave")));

        var report = _git.Log("", "", 1, new GitLogFilter(Author: "bob"));
        Assert.True(report.Truncated);   // a second match exists past max
        Assert.False(report.WalkCapped);
        Assert.Equal(new GitLogFilter(Author: "bob"), report.Filter);
        Assert.Null(_git.Log("", "", 10, new GitLogFilter()).Filter);   // no filter set reads as none
    }

    [Fact]
    public void Log_TheWalkCap_WithAFilter_SaysOnlyTheNewestWereSearched_InTheEmptyCaseToo()
    {
        Init();
        for (int i = 1; i <= 5; i++)
        {
            Commit(i == 1 ? "b.txt" : "a.txt", i.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n", "commit " + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        var none = _git.Log("", "", 10, new GitLogFilter(Grep: "commit 1"), walkCap: 3);
        Assert.Empty(none.Commits);
        Assert.True(none.WalkCapped);
        Assert.False(none.Truncated);
        Assert.Equal("No commits matching \"commit 1\" on main (searched the newest 3 commits)", GitText.Log(none, ManualTimeProvider.DefaultZone));

        var some = _git.Log("", "", 10, new GitLogFilter(Grep: "commit"), walkCap: 3);
        Assert.Equal(3, some.Commits.Count);
        Assert.StartsWith("3 commits matching \"commit\" on main, newest first (searched the newest 3 commits):", GitText.Log(some, ManualTimeProvider.DefaultZone), StringComparison.Ordinal);

        // A path skips commits as a filter does: the cap can't know there are more.
        var byPath = _git.Log("b.txt", "", 10, null, walkCap: 3);
        Assert.Empty(byPath.Commits);
        Assert.True(byPath.WalkCapped);
        Assert.Equal("No commits touch b.txt on main (searched the newest 3 commits)", GitText.Log(byPath, ManualTimeProvider.DefaultZone));

        // Nothing skipped: every walked commit counted, so more are known to exist.
        var plain = _git.Log("", "", 10, null, walkCap: 3);
        Assert.True(plain.Truncated);
        Assert.False(plain.WalkCapped);
        Assert.Contains("(more before them):", GitText.Log(plain, ManualTimeProvider.DefaultZone), StringComparison.Ordinal);
    }

    [Fact]
    public void Log_TheHeader_NamesEveryFilter_InTheUsersZone_AndEchoesTheTexts()
    {
        var report = new GitLogReport(GitOutcome.Ok, "", "main", null, [], false)
        {
            Filter = new GitLogFilter(new DateTimeOffset(2026, 11, 8, 7, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 9, 7, 0, 0, TimeSpan.Zero), "bob", "a\nb"),
        };
        Assert.Equal("No commits by bob matching \"a b\" since 2026-11-08 00:00 until 2026-11-09 00:00 on main", GitText.Log(report, ManualTimeProvider.DefaultZone));
    }

    // ---- revert ----

    [Fact]
    public void Revert_CommitsTheUndo_WithGitsDefaultMessage_AndLeavesNoRevertState()
    {
        Init();
        Commit("a.txt", "one\n", "first");
        string second = Commit("a.txt", "one\ntwo\n", "second");

        var result = _git.Revert("", "HEAD", "  ");

        Assert.Equal(GitOutcome.Ok, result.Outcome);
        Assert.Equal((second[..7], "main", "Revert \"second\"", 1), (result.Target, result.Branch, result.Subject, result.Files));
        Assert.Equal("one\n", Read("a.txt"));
        using var repo = new Repository(_root);
        Assert.Equal("Revert \"second\"\n\nThis reverts commit " + second + ".\n", repo.Head.Tip.Message);
        Assert.Equal(GitText.RevertMessage("second", second), repo.Head.Tip.Message);
        Assert.Equal(result.Short, repo.Head.Tip.Sha[..7]);
        Assert.Equal(second, repo.Head.Tip.Parents.Single().Sha);
        Assert.Equal("Test User", repo.Head.Tip.Author.Name);
        Assert.Equal(CurrentOperation.None, repo.Info.CurrentOperation);
        Assert.False(repo.Info.IsHeadDetached);   // the branch's own ref moved, never HEAD itself
        Assert.Equal("main", repo.Head.FriendlyName);
        Assert.False(repo.RetrieveStatus().IsDirty);   // index and work tree both match the new commit
        Assert.Equal($"Reverted {second[..7]} with {result.Short} on main: Revert \"second\" (1 file)", GitText.Reverted(result));
    }

    [Fact]
    public void Revert_WithAMessage_UsesIt_Tidied_AndRecreatesAFileTheTargetDeleted()
    {
        Init();
        Commit("a.txt", "one\n", "first");
        Commit("b.txt", "bee\n", "second");
        string gone = CommitDelete("b.txt", "drop b");

        var result = _git.Revert("", gone[..7], "Bring b back  \n\n\n");

        Assert.Equal(GitOutcome.Ok, result.Outcome);
        Assert.Equal("bee\n", Read("b.txt"));
        using var repo = new Repository(_root);
        Assert.Equal("Bring b back\n", repo.Head.Tip.Message);
        Assert.Equal(CurrentOperation.None, repo.Info.CurrentOperation);
        Assert.False(repo.RetrieveStatus().IsDirty);
    }

    /// <summary>A revert that deletes a file (2026-11-15, the in-memory revert): gone from the disk and the index, HEAD still on its branch.</summary>
    [Fact]
    public void Revert_ThatDeletesAFile_RemovesItFromDiskAndIndex_AndHeadStaysOnTheBranch()
    {
        Init();
        Commit("a.txt", "one\n", "first");
        Commit("c.txt", "sea\n", "add c");

        var result = _git.Revert("", "HEAD", "");

        Assert.Equal(GitOutcome.Ok, result.Outcome);
        Assert.False(File.Exists(Path.Combine(_root, "c.txt")));
        using var repo = new Repository(_root);
        Assert.Null(repo.Index["c.txt"]);
        Assert.Null(repo.Head.Tip["c.txt"]);
        Assert.False(repo.Info.IsHeadDetached);
        Assert.Equal("main", repo.Head.FriendlyName);
        Assert.False(repo.RetrieveStatus().IsDirty);
    }

    [Fact]
    public void Revert_Refuses_Unborn_AnUnknownRef_Changes_AndAMergeCommit_ButNotAnUntrackedFile()
    {
        Init();
        Assert.Equal(GitOutcome.Unborn, _git.Revert("", "HEAD", "").Outcome);
        Commit("a.txt", "one\n", "first");
        string second = Commit("a.txt", "one\ntwo\n", "second");
        Assert.Equal((GitOutcome.RefNotFound, "nope"), (_git.Revert("", "nope", "").Outcome, _git.Revert("", "nope", "").Detail));

        Write("a.txt", "changed\n");
        Assert.Equal(GitOutcome.DirtyTree, _git.Revert("", "HEAD", "").Outcome);
        Assert.Equal(GitText.DirtyTree, GitText.Error(GitOutcome.DirtyTree, ""));
        Write("a.txt", "one\ntwo\n");

        Write("c.txt", "new\n");
        using (var repo = new Repository(_root))
        {
            Commands.Stage(repo, "c.txt");
        }

        Assert.Equal(GitOutcome.DirtyTree, _git.Revert("", "HEAD", "").Outcome);   // staged, even in a file the revert never touches
        using (var repo = new Repository(_root))
        {
            Commands.Unstage(repo, "c.txt");
        }

        // Untracked doesn't count: the revert runs beside it.
        Assert.Equal(GitOutcome.Ok, _git.Revert("", second, "").Outcome);
        Assert.Equal("new\n", Read("c.txt"));
        File.Delete(Path.Combine(_root, "c.txt"));

        // A merge commit: which parent to keep is the user's call.
        using (var repo = new Repository(_root))
        {
            var main = repo.Head.Tip;
            var side = repo.CreateBranch("side", main.Parents.First());
            Commands.Checkout(repo, side);
        }

        Commit("s.txt", "side\n", "on side");
        string merge;
        using (var repo = new Repository(_root))
        {
            Commands.Checkout(repo, repo.Branches["main"]);
            var sig = new Signature("Test User", "test@example.invalid", Base.AddHours(2));
            var merged = repo.Merge(repo.Branches["side"], sig, new MergeOptions { FastForwardStrategy = FastForwardStrategy.NoFastForward });
            merge = merged.Commit.Sha;
        }

        var refused = _git.Revert("", "HEAD", "");
        Assert.Equal((GitOutcome.MergeCommit, merge[..7]), (refused.Outcome, refused.Detail));
        Assert.Equal($"Error: {merge[..7]} is a merge commit; which parent to keep is the user's call, so ask them to revert it", GitText.Error(refused.Outcome, refused.Detail));
        Assert.Equal(merge, State().Head);
    }

    /// <summary>The tool-arguments review's #40, #91, #96: an operation in progress, a commit off the branch and a detached HEAD each refuse, untouched.</summary>
    [Fact]
    public void Revert_Refuses_AnOperationInProgress_ACommitOffTheBranch_AndADetachedHead()
    {
        Init();
        string first = Commit("a.txt", "one\n", "first");
        Commit("a.txt", "two\n", "second");

        string mergeHead = Path.Combine(_root, ".git", "MERGE_HEAD");
        File.WriteAllText(mergeHead, first + "\n");
        var busy = _git.Revert("", "HEAD", "");
        Assert.Equal((GitOutcome.OperationInProgress, "merge"), (busy.Outcome, busy.Detail));
        Assert.StartsWith("Error: a merge is in progress", GitText.Error(busy.Outcome, busy.Detail), StringComparison.Ordinal);
        File.Delete(mergeHead);

        string side;
        using (var repo = new Repository(_root))
        {
            Commands.Checkout(repo, repo.CreateBranch("side", first));
        }

        side = Commit("s.txt", "side\n", "on side");
        using (var repo = new Repository(_root))
        {
            Commands.Checkout(repo, repo.Branches["main"]);
        }

        var before = State();
        var off = _git.Revert("", side, "");
        Assert.Equal((GitOutcome.NotOnBranch, GitText.NotOnBranchDetail(side[..7], "main")), (off.Outcome, off.Detail));
        Assert.Equal(before, State());

        using (var repo = new Repository(_root))
        {
            Commands.Checkout(repo, repo.Head.Tip.Sha);
        }

        Assert.Equal(GitOutcome.Detached, _git.Revert("", "HEAD", "").Outcome);
        Assert.Equal(GitText.Detached, GitText.Error(GitOutcome.Detached, ""));
    }

    [Fact]
    public void Revert_ThatConflicts_WritesNothing_AndNamesItsPaths()
    {
        Init();
        Commit("a.txt", "one\n", "first");
        Commit("b.txt", "bee\n", "add b");
        Write("a.txt", "two\n");
        string target = CommitDelete("b.txt", "drop b, change a", "a.txt");
        Commit("a.txt", "three\n", "third");
        var before = State();

        var result = _git.Revert("", target, "");

        Assert.Equal((GitOutcome.RevertConflicted, target[..7] + " conflicts in a.txt"), (result.Outcome, result.Detail));
        Assert.Equal($"Error: reverting {target[..7]} conflicts in a.txt; nothing changed", GitText.Error(result.Outcome, result.Detail));
        Assert.Equal(before, State());
        Assert.Equal("three\n", Read("a.txt"));
        Assert.False(File.Exists(Path.Combine(_root, "b.txt")));
        using var repo = new Repository(_root);
        Assert.Empty(repo.Index.Conflicts);
    }

    [Fact]
    public void Revert_OfChangesUndoneAlready_IsNothingToRevert_AndCommitsNothing()
    {
        Init();
        Commit("a.txt", "one\n", "first");
        string second = Commit("a.txt", "two\n", "second");
        Commit("a.txt", "one\n", "undo by hand");
        var before = State();

        var result = _git.Revert("", second, "");

        Assert.Equal((GitOutcome.NothingToRevert, second[..7]), (result.Outcome, result.Detail));
        Assert.Equal(GitText.NothingToRevert(second[..7]), GitText.Error(result.Outcome, result.Detail));
        Assert.Equal(before, State());
    }

    /// <summary>
    /// The tool-arguments review's #8, #20: an untracked file, an ignored file, an ignored folder at a path the revert adds, or a file at one of
    /// its parent folders, each refuses and is left as it was (libgit2 would overwrite an ignored file).
    /// </summary>
    [Fact]
    public void Revert_IntoSomethingGitDoesntTrack_IsInTheWay_AndItIsKept()
    {
        Init();
        Commit("a.txt", "one\n", "first");
        Commit("b.txt", "bee\n", "add b");
        string gone = CommitDelete("b.txt", "drop b");
        string head = State().Head;

        Write("b.txt", "mine\n");
        var untracked = _git.Revert("", gone, "");
        Assert.Equal((GitOutcome.InTheWay, "b.txt"), (untracked.Outcome, untracked.Detail));
        Assert.Equal("mine\n", Read("b.txt"));
        Assert.StartsWith("Error: the revert would add b.txt", GitText.Error(untracked.Outcome, untracked.Detail), StringComparison.Ordinal);

        string ignoring = Commit(".gitignore", "b.txt\n", "ignore b");
        Assert.Equal(GitOutcome.InTheWay, _git.Revert("", gone, "").Outcome);   // an ignored file
        Assert.Equal("mine\n", Read("b.txt"));
        File.Delete(Path.Combine(_root, "b.txt"));

        Write(Path.Combine("b.txt", "inside.txt"), "kept\n");
        Assert.Equal(GitOutcome.InTheWay, _git.Revert("", gone, "").Outcome);   // an ignored folder
        Assert.Equal("kept\n", Read(Path.Combine("b.txt", "inside.txt")));
        Directory.Delete(Path.Combine(_root, "b.txt"), recursive: true);

        Commit(Path.Combine("d", "e.txt"), "e\n", "add d/e");
        string dropE = CommitDelete("d/e.txt", "drop d/e");
        Directory.Delete(Path.Combine(_root, "d"));   // the folder the delete left empty
        Write("d", "a file named d\n");
        var parent = _git.Revert("", dropE, "");
        Assert.Equal(GitOutcome.InTheWay, parent.Outcome);   // a file where its folder goes
        Assert.Contains("e.txt", parent.Detail, StringComparison.Ordinal);
        Assert.Equal("a file named d\n", Read("d"));
        using var repo = new Repository(_root);
        Assert.Equal(dropE, repo.Head.Tip.Sha);
        Assert.NotEqual(head, ignoring);
        Assert.Equal(CurrentOperation.None, repo.Info.CurrentOperation);
    }

    /// <summary>The write guard (#21) and a glob character refuse before anything is written; discard refuses a glob path too (pass 24).</summary>
    [Fact]
    public void Revert_Refuses_AGuardedPath_AndAGlobPath_AndDiscardRefusesTheGlobToo()
    {
        Init();
        Commit(Path.Combine(".neon", "plans", "x.md"), "plan\n", "a plan");
        Commit(Path.Combine(".neon", "plans", "x.md"), "plan two\n", "plan again");
        var guarded = _git.Revert("", "HEAD", "");
        Assert.Equal(GitOutcome.Guarded, guarded.Outcome);
        Assert.StartsWith("Error: ", guarded.Detail, StringComparison.Ordinal);
        Assert.Equal("plan two\n", Read(Path.Combine(".neon", "plans", "x.md")));

        Commit("x.txt", "ex\n", "x");
        Commit("[x].txt", "one\n", "bracket");
        string head = Commit("[x].txt", "two\n", "bracket two");
        var globbed = _git.Revert("", "HEAD", "");
        Assert.Equal((GitOutcome.GlobPath, "[x].txt"), (globbed.Outcome, globbed.Detail));
        Assert.Equal("Error: [x].txt holds *, ? or [, which the git library reads as a pattern; use git for it", GitText.Error(globbed.Outcome, globbed.Detail));
        Assert.Equal(head, State().Head);

        Write("x.txt", "my change\n");
        var discarded = _git.Discard("", ["[x].txt"], "");
        Assert.Equal(GitOutcome.GlobPath, discarded.Outcome);
        Assert.Equal("my change\n", Read("x.txt"));
    }

    /// <summary>
    /// The tool-arguments review's #73: a touched path marked assume-unchanged refuses, since status can't see its local changes. LibGit2Sharp
    /// reads the bit but can't set it, so the test sets it in the index by hand: version 2, one entry, its flags at 12 + 60, the bit 0x8000,
    /// then the trailing SHA-1 recomputed.
    /// </summary>
    [Fact]
    public void Revert_Refuses_AnAssumeUnchangedPath()
    {
        Init();
        Commit("a.txt", "one\n", "first");
        Commit("a.txt", "two\n", "second");
        string index = Path.Combine(_root, ".git", "index");
        byte[] bytes = File.ReadAllBytes(index);
        Assert.Equal("DIRC", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal((2, 1), (bytes[7], bytes[11]));
        bytes[12 + 60] |= 0x80;
        IndexChecksum(bytes).CopyTo(bytes, bytes.Length - 20);
        File.WriteAllBytes(index, bytes);
        using (var repo = new Repository(_root))
        {
            Assert.True(repo.Index["a.txt"].AssumeUnchanged);
        }

        var result = _git.Revert("", "HEAD", "");

        Assert.Equal((GitOutcome.AssumeUnchanged, "a.txt"), (result.Outcome, result.Detail));
        Assert.Equal("two\n", Read("a.txt"));
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5350", Justification = "git's index checksum is SHA-1 by format.")]
    private static byte[] IndexChecksum(byte[] bytes) => System.Security.Cryptography.SHA1.HashData(bytes.AsSpan(0, bytes.Length - 20));

    /// <summary>A revert that would move a submodule's pointer refuses (the checkout wouldn't move it), and so does a tree walk cut short.</summary>
    [Fact]
    public void Revert_Refuses_ASubmoduleChange_AndATreeTooLargeToWalk()
    {
        Init();
        Commit("a.txt", "one\n", "first");
        Commit("b.txt", "bee\n", "b");
        Commit("c.txt", "sea\n", "c");
        Commit("d.txt", "dee\n", "d");
        Commit("a.txt", "two\n", "change a");

        var large = _git.Revert("", "HEAD", "", treeLimit: 3);
        Assert.Equal(GitOutcome.TooLargeToRevert, large.Outcome);
        Assert.Equal(GitOutcome.Ok, _git.Revert("", "HEAD", "", treeLimit: 10).Outcome);

        using (var repo = new Repository(_root))
        {
            var head = repo.Head.Tip;
            var definition = TreeDefinition.From(head);
            definition.AddGitLink("sub", head.Id);
            var tree = repo.ObjectDatabase.CreateTree(definition);
            var sig = new Signature("Test User", "test@example.invalid", Base.AddHours(4));
            var withSub = repo.ObjectDatabase.CreateCommit(sig, sig, "add sub", tree, [head], prettifyMessage: false);
            repo.Refs.UpdateTarget(repo.Refs["refs/heads/main"], withSub.Id);
            repo.Reset(ResetMode.Mixed);
        }

        var before = State().Head;
        var sub = _git.Revert("", "HEAD", "");
        Assert.Equal((GitOutcome.SubmoduleChange, "sub"), (sub.Outcome, sub.Detail));
        Assert.Equal(before, State().Head);
    }

    [Fact]
    public void Revert_UnderAStrayIndexLock_Fails_AndChangesNothing()
    {
        Init();
        Commit("a.txt", "one\n", "first");
        Commit("a.txt", "one\ntwo\n", "second");
        var before = State();
        string lockFile = Path.Combine(_root, ".git", "index.lock");
        File.WriteAllText(lockFile, "");
        try
        {
            var result = _git.Revert("", "HEAD", "");

            Assert.NotEqual(GitOutcome.Ok, result.Outcome);
            Assert.Equal("one\ntwo\n", Read("a.txt"));
        }
        finally
        {
            File.Delete(lockFile);
        }

        Assert.Equal(before, State());
    }

    /// <summary>
    /// A ref move that fails after the checkout (a held branch lock, another git process): the revert's own writes are put back, a changed file
    /// restored and a recreated one removed, and HEAD, the index and the files are as they were.
    /// </summary>
    [Fact]
    public void Revert_UnderAHeldBranchLock_PutsItsWritesBack()
    {
        Init();
        Commit("a.txt", "one\n", "first");
        Commit("b.txt", "bee\n", "add b");
        Write("a.txt", "two\n");
        CommitDelete("b.txt", "drop b, change a", "a.txt");
        var before = State();
        string lockFile = Path.Combine(_root, ".git", "refs", "heads", "main.lock");
        File.WriteAllText(lockFile, "");
        GitRevertResult result;
        try
        {
            result = _git.Revert("", "HEAD", "");
        }
        finally
        {
            File.Delete(lockFile);
        }

        Assert.Equal(GitOutcome.Failed, result.Outcome);
        Assert.EndsWith("; nothing was changed", result.Detail, StringComparison.Ordinal);
        Assert.Equal(before, State());
        Assert.Equal("two\n", Read("a.txt"));
        Assert.False(File.Exists(Path.Combine(_root, "b.txt")));
        using var repo = new Repository(_root);
        Assert.Null(repo.Index["b.txt"]);
    }

    /// <summary>A file another program holds open (common on Windows) fails the checkout partway; the put-back leaves HEAD and the files as they were.</summary>
    [WindowsFact]
    public void Revert_WithATouchedFileHeldOpen_PutsItsWritesBack()
    {
        Init();
        Commit("a.txt", "one\n", "first");
        Commit("b.txt", "bee\n", "add b");
        Write("a.txt", "two\n");
        Write("b.txt", "bee two\n");
        using (var repo = new Repository(_root))
        {
            Commands.Stage(repo, ["a.txt", "b.txt"]);
            var sig = new Signature("Test User", "test@example.invalid", Base.AddHours(5));
            repo.Commit("change both", sig, sig);
        }

        var before = State();
        GitRevertResult result;
        using (new FileStream(Path.Combine(_root, "b.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            result = _git.Revert("", "HEAD", "");
        }

        Assert.Equal(GitOutcome.Failed, result.Outcome);
        Assert.Equal(before, State());
        Assert.Equal(("two\n", "bee two\n"), (Read("a.txt"), Read("b.txt")));
    }

    // ---- tag ----

    [Fact]
    public void CreateTag_Lightweight_AndAnnotated_ReadBack_OnAnyRef()
    {
        Init();
        string first = Commit("a.txt", "one\n", "first");
        string second = Commit("a.txt", "two\n", "second");

        var light = _git.CreateTag("", "v1", "", "   ");
        Assert.Equal((GitOutcome.Ok, "v1", second[..7], false), (light.Outcome, light.Name, light.Short, light.Annotated));
        Assert.Equal($"Tagged {second[..7]} as v1 (lightweight)", GitText.Tagged(light));

        var annotated = _git.CreateTag("", " v0.9 ", first[..7], "Release zero nine  \n\nThe notes.\n\n");
        Assert.Equal((GitOutcome.Ok, "v0.9", first[..7], true), (annotated.Outcome, annotated.Name, annotated.Short, annotated.Annotated));
        Assert.Equal($"Tagged {first[..7]} as v0.9 (annotated)", GitText.Tagged(annotated));

        using var repo = new Repository(_root);
        Assert.False(repo.Tags["v1"].IsAnnotated);
        Assert.Equal(second, repo.Tags["v1"].Target.Sha);
        var annotation = repo.Tags["v0.9"].Annotation;
        Assert.Equal("Release zero nine\n\nThe notes.\n", annotation.Message);
        Assert.Equal(first, annotation.Target.Sha);
        Assert.Equal(("Test User", "test@example.invalid"), (annotation.Tagger.Name, annotation.Tagger.Email));
        Assert.Equal(_time.GetLocalNow().ToUnixTimeSeconds(), annotation.Tagger.When.ToUnixTimeSeconds());
        Assert.Equal(_time.GetLocalNow().Offset, annotation.Tagger.When.Offset);
    }

    [Fact]
    public void CreateTag_Refuses_AnExistingTag_ABranchsName_ABadName_AndAnUnknownRef()
    {
        Init();
        Commit("a.txt", "one\n", "first");
        Assert.Equal(GitOutcome.Ok, _git.CreateTag("", "v1", "", "").Outcome);
        string tagged;
        using (var repo = new Repository(_root))
        {
            tagged = repo.Tags["v1"].Target.Sha;
        }

        Commit("a.txt", "two\n", "second");
        Assert.Equal((GitOutcome.NameConflict, "v1"), (_git.CreateTag("", "v1", "", "moved?").Outcome, _git.CreateTag("", "v1", "", "").Detail));
        using (var repo = new Repository(_root))
        {
            Assert.Equal(tagged, repo.Tags["v1"].Target.Sha);   // never moved
        }

        var main = _git.CreateTag("", "main", "", "");
        Assert.Equal((GitOutcome.NameTaken, GitText.NameTakenDetail("main", GitText.TakenByBranch)), (main.Outcome, main.Detail));
        var bad = _git.CreateTag("", "bad name..", "", "");
        Assert.Equal((GitOutcome.BadName, "bad name.."), (bad.Outcome, bad.Detail));
        Assert.Equal(GitText.BadRefName("bad name.."), GitText.Error(bad.Outcome, bad.Detail));
        Assert.Equal(GitOutcome.RefNotFound, _git.CreateTag("", "v2", "nope", "").Outcome);
        using (var repo = new Repository(_root))
        {
            Assert.Equal(["v1"], repo.Tags.Select(t => t.FriendlyName));
        }
    }

    [Fact]
    public void Tags_ListNewestTargetFirst_ThenByName_ATreeOrBlobLast_AndTheBranchListShowsThemSo()
    {
        Init();
        string first = CommitAs("a.txt", "one\n", "first", "Test User", "test@example.invalid", 1);
        Assert.Equal(GitText.NoTags, GitText.Tags(_git.Tags("")));
        string second = CommitAs("a.txt", "two\n", "second", "Test User", "test@example.invalid", 2);
        Assert.Equal(GitOutcome.Ok, _git.CreateTag("", "z-old", first, "").Outcome);
        Assert.Equal(GitOutcome.Ok, _git.CreateTag("", "b-new", "", "").Outcome);
        Assert.Equal(GitOutcome.Ok, _git.CreateTag("", "a-new", "", "Two\n\nThe body").Outcome);
        string blob;
        using (var repo = new Repository(_root))
        {
            var target = repo.Head.Tip["a.txt"].Target;
            blob = target.Sha;
            repo.Tags.Add("blobtag", target);
        }

        var report = _git.Tags("");
        Assert.Equal(GitOutcome.Ok, report.Outcome);
        Assert.False(report.Truncated);
        Assert.Equal(
            $"4 tags, newest first:\n  a-new {second[..7]} {GitText.AnnotatedSubject("Two")}\n  b-new {second[..7]} second\n  z-old {first[..7]} first\n  blobtag {blob[..7]} ",
            GitText.Tags(report));
        Assert.Equal(["a-new", "b-new", "z-old", "blobtag"], _git.Refs("").Tags.Select(t => t.Name));   // gitlib_branch list: the same read
        Assert.StartsWith("4 tags, newest first (the newest " + GitAccess.MaxTags.ToString(System.Globalization.CultureInfo.InvariantCulture) + "):\n",
            GitText.Tags(report with { Truncated = true }), StringComparison.Ordinal);
    }

    [Fact]
    public void NewRefNames_RefuseWhatGitCantHold_OrWhatWouldReadAsSomethingElse_ForBranchesTagsAndRenames()
    {
        Init();
        string first = Commit("a.txt", "one\n", "first");
        Assert.Equal(GitOutcome.Ok, _git.CreateTag("", "v1", "", "").Outcome);
        Assert.Equal(GitOutcome.Ok, _git.CreateTag("", "rel/rc1", "", "").Outcome);
        Assert.Equal(GitOutcome.Ok, _git.CreateBranch("", "feature", "", switchTo: false).Outcome);
        using (var repo = new Repository(_root))
        {
            repo.Refs.Add("refs/remotes/origin/topic", repo.Head.Tip.Id);
        }

        foreach (string bad in new[] { "bad name..", "-x", "HEAD", "head", "refs/heads/x", "a..b", "x.lock" })
        {
            var branch = _git.CreateBranch("", bad, "", switchTo: false);
            Assert.Equal((GitOutcome.BadName, bad), (branch.Outcome, branch.Detail));
            Assert.Equal(GitOutcome.BadName, _git.CreateTag("", bad, "", "").Outcome);
        }

        void Taken(GitOutcome outcome, string detail, string name, string what) => Assert.Equal((GitOutcome.NameTaken, GitText.NameTakenDetail(name, what)), (outcome, detail));
        var asTag = _git.CreateBranch("", "v1", "", switchTo: false);
        Taken(asTag.Outcome, asTag.Detail, "v1", GitText.TakenByTag);
        var asBranch = _git.CreateTag("", "feature", "", "");
        Taken(asBranch.Outcome, asBranch.Detail, "feature", GitText.TakenByBranch);
        var asRemote = _git.CreateBranch("", "origin/topic", "", switchTo: false);
        Taken(asRemote.Outcome, asRemote.Detail, "origin/topic", GitText.TakenByRemote);
        var folderOver = _git.CreateTag("", "rel", "", "");
        Taken(folderOver.Outcome, folderOver.Detail, "rel", GitText.TakenByFolder);
        var folderUnder = _git.CreateBranch("", "feature/x", "", switchTo: false);
        Taken(folderUnder.Outcome, folderUnder.Detail, "feature/x", GitText.TakenByFolder);
        var sha = _git.CreateBranch("", first[..7], "", switchTo: false);
        Taken(sha.Outcome, sha.Detail, first[..7], GitText.TakenByObject);
        var renamed = _git.RenameBranch("", "feature", "v1");
        Taken(renamed.Outcome, renamed.Detail, "v1", GitText.TakenByTag);
        Assert.Equal(GitOutcome.BadName, _git.RenameBranch("", "feature", "-f").Outcome);
        Assert.Equal((GitOutcome.NameConflict, "feature"), (_git.CreateBranch("", "feature", "", switchTo: false).Outcome, _git.CreateBranch("", "feature", "", switchTo: false).Detail));

        // A hex word is a fine name while no object starts with it.
        Assert.Equal(GitOutcome.Ok, _git.CreateBranch("", "facade", "", switchTo: false).Outcome);
        using (var repo = new Repository(_root))
        {
            Assert.Equal(["facade", "feature", "main"], repo.Branches.Where(b => !b.IsRemote).Select(b => b.FriendlyName).Order(StringComparer.Ordinal));
            Assert.Equal(["rel/rc1", "v1"], repo.Tags.Select(t => t.FriendlyName).Order(StringComparer.Ordinal));
        }
    }

    [Fact]
    public void CreateTag_Refuses_ANulOrASignatureLineInTheMessage_AndALineBreakInTheTagger()
    {
        Init();
        Commit("a.txt", "one\n", "first");
        var nul = _git.CreateTag("", "t0", "", "one\0two");
        Assert.Equal((GitOutcome.BadMessage, ""), (nul.Outcome, nul.Detail));
        Assert.Equal(GitText.MessageHasNul, GitText.Error(nul.Outcome, nul.Detail));
        foreach (string marker in new[] { "-----BEGIN PGP SIGNATURE-----", "-----BEGIN PGP MESSAGE-----", "-----BEGIN SIGNED MESSAGE-----", "-----BEGIN SSH SIGNATURE-----" })
        {
            var signed = _git.CreateTag("", "t1", "", "Release\n\n" + marker + " trailing\nmore");
            Assert.Equal((GitOutcome.BadMessage, marker + " trailing"), (signed.Outcome, signed.Detail));
            Assert.Equal(GitText.SignatureLine(marker + " trailing"), GitText.Error(signed.Outcome, signed.Detail));
        }

        Assert.Equal(GitOutcome.Ok, _git.CreateTag("", "t2", "", "Release\n\nsee -----BEGIN PGP SIGNATURE----- mid-line").Outcome);

        using (var repo = new Repository(_root))
        {
            repo.Config.Set("user.name", "Test\nUser", ConfigurationLevel.Local);
        }

        var tagger = _git.CreateTag("", "t3", "", "Release");
        Assert.Equal(GitOutcome.BadTagger, tagger.Outcome);
        Assert.Equal(GitText.BadTagger, GitText.Error(tagger.Outcome, tagger.Detail));
        Assert.Equal(GitOutcome.Ok, _git.CreateTag("", "t4", "", "").Outcome);   // a lightweight tag signs nothing
        using (var repo = new Repository(_root))
        {
            Assert.Equal(["t2", "t4"], repo.Tags.Select(t => t.FriendlyName).Order(StringComparer.Ordinal));
        }
    }

    // ---- the log's walk ----

    /// <summary>A commit authored at <paramref name="authored"/> and committed at <paramref name="committed"/> minutes past <see cref="Base"/>; the sha.</summary>
    private string CommitAt(string content, string message, int authored, int committed)
    {
        Write("a.txt", content);
        using var repo = new Repository(_root);
        Commands.Stage(repo, "a.txt");
        return repo.Commit(message, new Signature("Bob", "bob@example.invalid", Base.AddMinutes(authored)), new Signature("Bob", "bob@example.invalid", Base.AddMinutes(committed))).Sha;
    }

    [Fact]
    public void Log_Since_ReadsTheCommitDate_AndStopsAfterFiveOlderInARow_WithNoCapNote()
    {
        Init();
        for (int i = 1; i <= 7; i++)
        {
            CommitAt("old " + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n", "old " + i.ToString(System.Globalization.CultureInfo.InvariantCulture), i, i);
        }

        // Six rebased commits: written long ago, committed after since. Counting their author dates would stop the walk at the fifth.
        for (int i = 1; i <= 6; i++)
        {
            CommitAt("rebased " + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n", "rebased " + i.ToString(System.Globalization.CultureInfo.InvariantCulture), 0, 100 + i);
        }

        var report = _git.Log("", "", 50, new GitLogFilter(Since: Base.AddMinutes(100)), walkCap: 12);
        Assert.Equal(["rebased 6", "rebased 5", "rebased 4", "rebased 3", "rebased 2", "rebased 1"], report.Commits.Select(c => c.Subject));
        Assert.False(report.Truncated);
        Assert.False(report.WalkCapped);   // stopped by the slop rule after five older commits, under the cap of 12 (13 commits in all)
        Assert.False(report.MaybeMore);
        Assert.DoesNotContain("searched", GitText.Log(report, ManualTimeProvider.DefaultZone), StringComparison.Ordinal);
    }

    [Fact]
    public void Log_AtTheWalkCap_WithTheListFull_ReadsMoreMayBeBefore_AndUnfilteredMoreBefore()
    {
        Init();
        for (int i = 1; i <= 5; i++)
        {
            Commit("a.txt", i.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n", "commit " + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        var full = _git.Log("", "", 3, new GitLogFilter(Grep: "commit"), walkCap: 3);
        Assert.Equal(3, full.Commits.Count);
        Assert.True(full.MaybeMore);
        Assert.False(full.WalkCapped);
        Assert.False(full.Truncated);
        string text = GitText.Log(full, ManualTimeProvider.DefaultZone);
        Assert.Contains("(more may be before them):", text, StringComparison.Ordinal);
        Assert.DoesNotContain("searched", text, StringComparison.Ordinal);

        var plain = _git.Log("", "", 3, null, walkCap: 3);
        Assert.True(plain.Truncated);
        Assert.False(plain.MaybeMore);
        Assert.Contains("(more before them):", GitText.Log(plain, ManualTimeProvider.DefaultZone), StringComparison.Ordinal);
    }

    // ---- the log lines ----

    [Fact]
    public void TheRevertAndTheTag_LogAtDebug_NeverInfo()
    {
        Init();
        Commit("a.txt", "one\n", "first");
        string second = Commit("a.txt", "two\n", "second");
        Commit("a.txt", "three\n", "third");
        var lines = new List<(DiagnosticLevel Level, string Message)>();
        void Capture(DiagnosticEvent e)
        {
            if (e.Category == GitAccess.Category)
            {
                lines.Add((e.Level, e.Message));
            }
        }

        DiagnosticLog.Emitted += Capture;
        GitRevertResult conflicted, reverted;
        try
        {
            conflicted = _git.Revert("", second, "");
            reverted = _git.Revert("", "HEAD", "");
            _git.CreateTag("", "v1", "", "Notes");
        }
        finally
        {
            DiagnosticLog.Emitted -= Capture;
        }

        Assert.Equal(GitOutcome.RevertConflicted, conflicted.Outcome);
        Assert.Equal(
            [
                (DiagnosticLevel.Debug, GitAccess.RevertConflictedLogLine(second[..7], 1)),
                (DiagnosticLevel.Debug, GitAccess.RevertedLogLine(reverted.Target, reverted.Short, "main")),
                (DiagnosticLevel.Debug, GitAccess.TaggedLogLine("v1", reverted.Short, true)),
            ],
            lines);
        Assert.Equal("Reverting abc1234 conflicted in 2 paths; nothing written", GitAccess.RevertConflictedLogLine("abc1234", 2));
        Assert.Equal("Reverted abc1234 with def5678 on main", GitAccess.RevertedLogLine("abc1234", "def5678", "main"));
        Assert.Equal("Tagged abc1234 as v1.0 (lightweight)", GitAccess.TaggedLogLine("v1.0", "abc1234", false));
    }
}
