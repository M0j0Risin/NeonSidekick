using LibGit2Sharp;
using NeonSidekick.Diagnostics;
using NeonSidekick.Git;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// <see cref="GitAccess"/>'s log filters, revert and tag (2026-11-15, feature ideas #44 and #32) over real temp repositories: each filter and
/// the walk cap's note, the revert's refusals and put-backs (HEAD, files and no revert state left), the tags read back through LibGit2Sharp.
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
        Assert.Equal(result.Short, repo.Head.Tip.Sha[..7]);
        Assert.Equal("Test User", repo.Head.Tip.Author.Name);
        Assert.Equal(CurrentOperation.None, repo.Info.CurrentOperation);
        Assert.False(repo.RetrieveStatus().IsDirty);
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

    [Fact]
    public void Revert_ThatConflicts_IsPutBack_TheFileItRecreatedRemoved_AndItsPathsNamed()
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

    [Fact]
    public void Revert_IntoAnUntrackedFile_IsACheckoutConflict_AndTheFileIsKept()
    {
        Init();
        Commit("a.txt", "one\n", "first");
        Commit("b.txt", "bee\n", "add b");
        string gone = CommitDelete("b.txt", "drop b");
        Write("b.txt", "mine\n");
        string head = State().Head;

        var result = _git.Revert("", gone, "");

        Assert.Equal(GitOutcome.CheckoutConflict, result.Outcome);
        Assert.Equal("mine\n", Read("b.txt"));
        using var repo = new Repository(_root);
        Assert.Equal(head, repo.Head.Tip.Sha);
        Assert.Equal(CurrentOperation.None, repo.Info.CurrentOperation);
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
            Assert.DoesNotContain("putting it back failed", result.Detail, StringComparison.Ordinal);
            Assert.Equal("one\ntwo\n", Read("a.txt"));
        }
        finally
        {
            File.Delete(lockFile);
        }

        Assert.Equal(before, State());
    }

    [Fact]
    public void PutBack_UndoesAHandMadeRevert_AndDeletesTheFilesItRecreated()
    {
        Init();
        Commit("a.txt", "one\n", "first");
        Commit("b.txt", "bee\n", "add b");
        Write("a.txt", "two\n");
        string target = CommitDelete("b.txt", "drop b, change a", "a.txt");
        var before = State();
        using (var repo = new Repository(_root))
        {
            var sig = new Signature("Test User", "test@example.invalid", Base.AddHours(3));
            Assert.Equal(RevertStatus.Reverted, repo.Revert(repo.Lookup<Commit>(target), sig, new RevertOptions { CommitOnSuccess = false }).Status);
            Assert.Equal("bee\n", Read("b.txt"));
            Assert.Equal(CurrentOperation.Revert, repo.Info.CurrentOperation);

            GitAccess.PutBack(repo, repo.Info.WorkingDirectory, ["b.txt"]);
        }

        Assert.Equal(before, State());
        Assert.Equal("two\n", Read("a.txt"));
        Assert.False(File.Exists(Path.Combine(_root, "b.txt")));
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

        Assert.Equal((GitOutcome.NameConflict, "main"), (_git.CreateTag("", "main", "", "").Outcome, _git.CreateTag("", "main", "", "").Detail));
        var bad = _git.CreateTag("", "bad name..", "", "");
        Assert.Equal((GitOutcome.BadName, "bad name.."), (bad.Outcome, bad.Detail));
        Assert.Equal("Error: 'bad name..' is not a valid tag name", GitText.Error(bad.Outcome, bad.Detail));
        Assert.Equal(GitOutcome.RefNotFound, _git.CreateTag("", "v2", "nope", "").Outcome);
        using (var repo = new Repository(_root))
        {
            Assert.Equal(["v1"], repo.Tags.Select(t => t.FriendlyName));
        }
    }

    [Fact]
    public void Tags_ListAsTheBranchListShowsThem_AndNoneSaysSo()
    {
        Init();
        Commit("a.txt", "one\n", "first");
        Assert.Equal(GitText.NoTags, GitText.Tags(_git.Refs("")));
        string second = Commit("a.txt", "two\n", "second");
        _git.CreateTag("", "v1", "", "");
        _git.CreateTag("", "v2", "", "Two");

        var report = _git.Refs("");
        Assert.False(report.TagsTruncated);
        Assert.Equal($"2 tags:\n  v1 {second[..7]} second\n  v2 {second[..7]} second", GitText.Tags(report));
        // The cut reads TagsTruncated alone: a cut branch list (Truncated) never says the tags were cut.
        var cut = report with { Truncated = true, TagsTruncated = true };
        Assert.StartsWith("2 tags (cut at " + GitAccess.MaxTags.ToString(System.Globalization.CultureInfo.InvariantCulture) + "):\n", GitText.Tags(cut), StringComparison.Ordinal);
        Assert.StartsWith("2 tags:\n", GitText.Tags(report with { Truncated = true }), StringComparison.Ordinal);
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
                (DiagnosticLevel.Debug, GitAccess.RevertUndoneLogLine(second[..7], 1)),
                (DiagnosticLevel.Debug, GitAccess.RevertedLogLine(reverted.Target, reverted.Short, "main")),
                (DiagnosticLevel.Debug, GitAccess.TaggedLogLine("v1", reverted.Short, true)),
            ],
            lines);
        Assert.Equal("Reverting abc1234 conflicted in 2 paths; put back", GitAccess.RevertUndoneLogLine("abc1234", 2));
        Assert.Equal("Reverted abc1234 with def5678 on main", GitAccess.RevertedLogLine("abc1234", "def5678", "main"));
        Assert.Equal("Tagged abc1234 as v1.0 (lightweight)", GitAccess.TaggedLogLine("v1.0", "abc1234", false));
        Assert.Equal("Putting back the revert of abc1234 failed (locked); files it may have left: none", GitAccess.RevertPutBackFailedLogLine("abc1234", [], "locked"));
    }
}
