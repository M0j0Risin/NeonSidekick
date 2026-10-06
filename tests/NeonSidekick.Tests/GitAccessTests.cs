using LibGit2Sharp;
using NeonSidekick.Diagnostics;
using NeonSidekick.Git;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// <see cref="GitAccess"/> over real temp repositories (LibGit2Sharp under the JIT; the published exe's
/// <c>git:roundtrip</c> is the AOT half): the sandbox rule on discovery, every operation's report, and the
/// diffs made without <c>Diff.Compare</c>.
/// </summary>
public sealed class GitAccessTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly ManualTimeProvider _time = new();
    private readonly Files.WorkingDirectory _files;
    private readonly GitAccess _git;

    public GitAccessTests()
    {
        _root = Path.Combine(_dir, "files");
        Directory.CreateDirectory(_root);
        _files = new Files.WorkingDirectory(() => _root, _time);
        _git = new GitAccess(_files, _time);
    }

    public void Dispose() => DeleteTree(_dir);

    internal static void DeleteTree(string dir)
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

    /// <summary>A repository at <paramref name="folder"/> (the root by default) with a local identity and <c>main</c> as its branch, so the machine's git config never matters.</summary>
    internal static string Init(string folder)
    {
        Directory.CreateDirectory(folder);
        Repository.Init(folder);
        using var repo = new Repository(folder);
        repo.Config.Set("user.name", "Test User", ConfigurationLevel.Local);
        repo.Config.Set("user.email", "test@example.invalid", ConfigurationLevel.Local);
        repo.Config.Set("core.autocrlf", false, ConfigurationLevel.Local);   // the machine's global autocrlf would rewrite every checkout
        PinUnbornHead(repo);
        return folder;
    }

    /// <summary>
    /// Points the unborn HEAD at <c>refs/heads/main</c>. libgit2 names the first branch after
    /// <c>init.defaultBranch</c> and falls back to <c>master</c>; a developer's machine sets it to
    /// <c>main</c>, a CI runner sets nothing, and every "On branch main" assertion in the suite
    /// failed on the first release run (2026-09-21). Set here, the name is the test's, not the host's.
    /// </summary>
    internal static void PinUnbornHead(Repository repo) => repo.Refs.UpdateTarget("HEAD", "refs/heads/main");

    private static int _commits;

    private string Init() => Init(_root);

    /// <summary>Writes <paramref name="relative"/> under <paramref name="folder"/>, stages it and commits with <paramref name="message"/>; the sha.</summary>
    internal static string CommitFile(string folder, string relative, string content, string message)
    {
        string full = Path.Combine(folder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        using var repo = new Repository(folder);
        Commands.Stage(repo, relative);
        // Each commit a minute later than the one before, so a time-sorted log is deterministic.
        var sig = new Signature("Test User", "test@example.invalid", new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.FromHours(-7)).AddMinutes(Interlocked.Increment(ref _commits)));
        return repo.Commit(message, sig, sig).Sha;
    }

    private void Write(string relative, string content)
    {
        string full = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    // ---- discovery ----

    [Fact]
    public void NativeLibraryFileName_MatchesTheBundledLibgit2()
    {
        Assert.Equal(GitAccess.NativeLibraryFileName, "git2-" + GlobalSettings.Version.LibGit2CommitSha + ".dll");
    }

    [Fact]
    public void Init_PinsTheUnbornHeadToMain_WhateverTheMachineDefault()
    {
        Directory.CreateDirectory(_root);
        Repository.Init(_root);
        using (var repo = new Repository(_root))
        {
            repo.Refs.UpdateTarget("HEAD", "refs/heads/master");   // what a runner with no init.defaultBranch produces
            Assert.Equal("master", repo.Head.FriendlyName);
        }

        Init();   // a second Init over an existing repository leaves HEAD alone; only the pin renames it

        Assert.Equal("main", _git.Status("").Branch);
        Write("a.txt", "one\n");
        _git.Stage("", ["a.txt"], unstage: false);
        Assert.Equal("main", _git.Commit("", "first", amend: false, allowEmpty: false).Branch);
    }

    [Fact]
    public void Locate_FindsTheRepositoryAtTheRoot()
    {
        Init();

        Assert.Equal(GitOutcome.Ok, _git.Locate("", out var location, out _, out _));
        Assert.Equal(_root, location!.WorkTree);
        Assert.Equal("", location.RootRelative);
        Assert.Equal(Path.Combine(_root, ".git"), location.GitDirectory);
    }

    [Fact]
    public void Locate_FindsANestedRepository_FromAPathInsideIt()
    {
        Init(Path.Combine(_root, "proj"));
        Write(Path.Combine("proj", "src", "a.txt"), "x");

        Assert.Equal(GitOutcome.Ok, _git.Locate(Path.Combine("proj", "src", "a.txt"), out var location, out _, out _));
        Assert.Equal(Path.Combine(_root, "proj"), location!.WorkTree);
        Assert.Equal("proj" + Path.DirectorySeparatorChar, location.RootRelative);
        // The nested repository is not seen from the root itself.
        Assert.Equal(GitOutcome.NoRepository, _git.Locate("", out _, out _, out _));
    }

    [Fact]
    public void Locate_RefusesARepositoryAboveTheSandbox()
    {
        Init(_dir);   // the repo's root is the sandbox's parent

        Assert.Equal(GitOutcome.AboveSandbox, _git.Locate("", out var location, out _, out string detail));
        Assert.Null(location);
        Assert.Equal(_dir, detail);
        Assert.Equal(GitOutcome.AboveSandbox, _git.Status("").Outcome);
        Assert.Equal(_dir, _git.Status("").Detail);
    }

    // Unix paths through Unix path rules: on Windows Path reads them with its own separators, and RealPath is null there anyway.
    [UnixTheory]
    [InlineData("/var/folders/x/files", "/private/var/folders/x/files/.git", "/var/folders/x/files/.git")]
    [InlineData("/var/folders/x/files", "/private/var/folders/x/files/proj", "/var/folders/x/files/proj")]
    [InlineData("/var/folders/x/files", "/private/var/folders/x", "/var/folders/x")]                                   // above the root: named the user's way for the refusal
    [InlineData("/var/folders/x/files", "/private/var/folders/x/filesmore", "/var/folders/x/filesmore")]
    [InlineData("/var/folders/x/files", "/var/folders/x/files/proj", "/var/folders/x/files/proj")]                     // already the root's spelling
    [InlineData("/var/folders/x/files", "/elsewhere/repo", "/elsewhere/repo")]                                         // under no real path of theirs
    [InlineData("/Users/me/repo", "/Users/other/repo", "/Users/other/repo")]                                           // no links on the way
    public void InRootSpelling_PutsLibgit2sRealPathBackUnderTheRootsSpelling(string root, string path, string expected)
    {
        // A fake realpath for a Mac: /var is a link to /private/var, nothing else is a link.
        static string? Real(string folder) => folder == "/var" || folder.StartsWith("/var/", StringComparison.Ordinal) ? "/private" + folder : folder;

        Assert.Equal(expected, Files.WorkingDirectory.InRootSpelling(root, path, Real));
        Assert.Equal(path, Files.WorkingDirectory.InRootSpelling(root, path, _ => null));   // no real path (Windows): as libgit2 gave it
    }

    [Fact]
    public void Locate_RefusesAPathOutsideTheSandbox_AndABareRepository()
    {
        Assert.Equal(GitOutcome.OutsideRoot, _git.Locate("..\\other", out _, out _, out _));
        Assert.Equal(GitOutcome.NoRepository, _git.Locate("", out _, out _, out _));
        Repository.Init(Path.Combine(_root, "bare.git"), isBare: true);
        Assert.Equal(GitOutcome.Bare, _git.Locate("bare.git", out _, out _, out string detail));
        Assert.Equal(Path.Combine(_root, "bare.git"), detail);
    }

    [Fact]
    public void Locate_FromAPathThatIsNotThere_UsesTheNearestFolder()
    {
        Init();

        Assert.Equal(GitOutcome.Ok, _git.Locate(Path.Combine("gone", "deeper", "file.txt"), out var location, out _, out _));
        Assert.Equal(_root, location!.WorkTree);
    }

    [Fact]
    public void RepoPath_AndSandboxPath_TranslateBothWays()
    {
        Init(Path.Combine(_root, "proj"));
        _git.Locate("proj", out var location, out _, out _);

        Assert.Equal("", GitAccess.RepoPath(location!, Path.Combine(_root, "proj")));
        Assert.Equal("src/a.txt", GitAccess.RepoPath(location!, Path.Combine(_root, "proj", "src", "a.txt")));
        Assert.Null(GitAccess.RepoPath(location!, Path.Combine(_root, "other", "b.txt")));
        Assert.Equal(Path.Combine("proj", "src", "a.txt"), _git.SandboxPath(location!, "src/a.txt"));
        // A path the nested repository does not hold: discovery from it finds nothing; named among a call's paths it is refused.
        Assert.Equal(GitOutcome.NoRepository, _git.Status("other").Outcome);
        Assert.Equal(GitOutcome.NotInRepository, _git.Stage("proj", [Path.Combine("other", "b.txt")], unstage: false).Outcome);
    }

    // ---- the GitBuf detours (the published exe, later on 2026-09-20) ----

    [Fact]
    public void FindGitEntry_WalksUp_ToAFolderOrAFile_OrNothing()
    {
        Init();
        Directory.CreateDirectory(Path.Combine(_root, "src", "deep"));
        Assert.Equal(_root, GitAccess.FindGitEntry(Path.Combine(_root, "src", "deep")));
        Assert.Equal(_root, GitAccess.FindGitEntry(_root + Path.DirectorySeparatorChar));
        Assert.Null(GitAccess.FindGitEntry(Path.Combine(_dir, "elsewhere")));
        // A .git file (a worktree, a submodule) counts as an entry too.
        string linked = Path.Combine(_dir, "linked");
        Directory.CreateDirectory(linked);
        File.WriteAllText(Path.Combine(linked, ".git"), "gitdir: " + Path.Combine(_root, ".git") + "\n");
        Assert.Equal(linked, GitAccess.FindGitEntry(linked));
        // A bare repository is its own git directory.
        string bare = Path.Combine(_dir, "bare.git");
        Repository.Init(bare, isBare: true);
        Assert.Equal(bare, GitAccess.FindGitEntry(bare));
        Assert.Equal(bare, GitAccess.FindGitEntry(Path.Combine(bare, "refs")));   // from inside it: the walk stops at the bare folder
    }

    [Fact]
    public void Prettify_IsGitsMessageTidying()
    {
        Assert.Equal("first\n", GitAccess.Prettify("first  \r\n\r\n\r\n"));
        Assert.Equal("subject\n\nbody line\nmore\n", GitAccess.Prettify("\n\nsubject\n\n\n\nbody line  \nmore"));
        Assert.Equal("", GitAccess.Prettify("  \n\n"));
        Assert.Equal("x\n", GitAccess.Prettify("x"));
    }

    [Fact]
    public void Upstream_ComesFromTheConfig_AndTheDivergence_NeverTheTrackedBranch()
    {
        Init();
        string first = CommitFile(_root, "a.txt", "one\n", "first");
        using (var repo = new Repository(_root))
        {
            Assert.Null(GitAccess.Upstream(repo, repo.Head));
            repo.Refs.Add("refs/remotes/origin/main", first);
            repo.Config.Set("branch.main.remote", "origin", ConfigurationLevel.Local);
            repo.Config.Set("branch.main.merge", "refs/heads/main", ConfigurationLevel.Local);
            Assert.Equal(new GitAccess.UpstreamInfo("origin/main", 0, 0), GitAccess.Upstream(repo, repo.Head));
        }

        var status = _git.Status("");
        Assert.Equal(("origin/main", 0, 0), (status.Upstream, status.Ahead, status.Behind));
        Assert.Equal("On branch main (up to date with origin/main): clean", GitText.Status(status));

        CommitFile(_root, "a.txt", "two\n", "second");
        var ahead = _git.Status("");
        Assert.Equal(("origin/main", 1, 0), (ahead.Upstream, ahead.Ahead, ahead.Behind));
        Assert.Equal("origin/main", _git.Refs("").Local.Single(b => b.Name == "main").Upstream);

        using (var repo = new Repository(_root))
        {
            // A configured upstream whose ref is not there yet (before the first fetch): named, no counts.
            repo.Config.Set("branch.main.merge", "refs/heads/other", ConfigurationLevel.Local);
            Assert.Equal(new GitAccess.UpstreamInfo("origin/other", null, null), GitAccess.Upstream(repo, repo.Head));
        }
    }

    // ---- status ----

    [Fact]
    public void Status_ReadsAnUnbornRepository_ThenTheStagedAndCleanStates()
    {
        Init();
        Write("notes.txt", "one\n");

        var unborn = _git.Status("");
        Assert.Equal(GitOutcome.Ok, unborn.Outcome);
        Assert.True(unborn.Unborn);
        Assert.Equal("main", unborn.Branch);
        Assert.Null(unborn.HeadShort);
        Assert.Equal(["notes.txt"], unborn.Untracked.Select(e => e.Path));
        Assert.Empty(unborn.Staged);

        Assert.Equal(GitOutcome.Ok, _git.Stage("", ["notes.txt"], unstage: false).Outcome);
        var staged = _git.Status("");
        Assert.Equal(['A'], staged.Staged.Select(e => e.Code));
        Assert.Empty(staged.Untracked);

        var committed = _git.Commit("", "first", amend: false, allowEmpty: false);
        Assert.Equal(GitOutcome.Ok, committed.Outcome);
        var clean = _git.Status("");
        Assert.True(clean.Clean);
        Assert.False(clean.Unborn);
        Assert.Equal(7, clean.HeadShort!.Length);
        Assert.Null(clean.Upstream);

        Write("notes.txt", "one\ntwo\n");
        File.Delete(Path.Combine(_root, "notes.txt"));
        Write("notes.txt", "one\ntwo\n");
        var modified = _git.Status("");
        Assert.Equal([new GitEntry('M', "notes.txt")], modified.Unstaged);
    }

    [Fact]
    public void Status_UnderAFolder_ListsThatFolderAlone()
    {
        Init();
        CommitFile(_root, "a.txt", "a\n", "a");
        Write(Path.Combine("src", "b.txt"), "b\n");
        Write("c.txt", "c\n");

        var under = _git.Status("src");
        Assert.Equal(["src" + Path.DirectorySeparatorChar], under.Untracked.Select(e => e.Path));
        var all = _git.Status("");
        Assert.Equal(["c.txt", "src" + Path.DirectorySeparatorChar], all.Untracked.Select(e => e.Path));
    }

    // ---- log / show ----

    [Fact]
    public void Log_ListsNewestFirst_CapsAndFiltersByPath()
    {
        Init();
        string first = CommitFile(_root, "a.txt", "a\n", "first");
        string second = CommitFile(_root, "b.txt", "b\n", "second");
        string third = CommitFile(_root, "a.txt", "a2\n", "third");

        var log = _git.Log("", "", 10);
        Assert.Equal(GitOutcome.Ok, log.Outcome);
        Assert.Equal("main", log.Reference);
        Assert.Equal(["third", "second", "first"], log.Commits.Select(c => c.Subject));
        Assert.Equal(third[..7], log.Commits[0].Short);
        Assert.Equal("Test User", log.Commits[0].Author);
        Assert.False(log.Truncated);

        var capped = _git.Log("", "", 2);
        Assert.Equal(2, capped.Commits.Count);
        Assert.True(capped.Truncated);

        var byPath = _git.Log("a.txt", "", 10);
        Assert.Equal("a.txt", byPath.Path);
        Assert.Equal(["third", "first"], byPath.Commits.Select(c => c.Subject));

        var from = _git.Log("", second[..7], 10);
        Assert.Equal(["second", "first"], from.Commits.Select(c => c.Subject));
        Assert.Equal(second[..7], from.Reference);
        Assert.Equal(first[..7], from.Commits[1].Short);

        Assert.Equal(GitOutcome.RefNotFound, _git.Log("", "nope", 10).Outcome);
        Assert.Equal("nope", _git.Log("", "nope", 10).Detail);
    }

    [Fact]
    public void Log_OnAnUnbornRepository_IsUnborn()
    {
        Init();
        Assert.Equal(GitOutcome.Unborn, _git.Log("", "", 10).Outcome);
    }

    [Fact]
    public void Show_ACommit_ItsFilesAndAFileAtIt()
    {
        Init();
        CommitFile(_root, "a.txt", "one\n", "first");
        string second = CommitFile(_root, Path.Combine("src", "b.txt"), "b1\nb2\n", "second\n\nMore about it.\n");
        CommitFile(_root, "a.txt", "one\ntwo\n", "third");

        var show = _git.Show(second[..7], "");
        Assert.Equal(GitOutcome.Ok, show.Outcome);
        Assert.Equal("second", show.Commit!.Subject);
        Assert.Contains("More about it.", show.Commit.Message);
        Assert.Equal([new GitChange('A', Path.Combine("src", "b.txt"), null, 2, 0, false)], show.Changes);

        var file = _git.Show("HEAD", "a.txt");
        Assert.Equal("one\ntwo\n", file.Text);
        Assert.Equal(2, file.Lines);
        Assert.Equal("a.txt", file.Path);

        var before = _git.Show("HEAD~1", "a.txt");
        Assert.Equal("one\n", before.Text);

        var folder = _git.Show("HEAD", "src");
        Assert.Null(folder.Text);
        Assert.Equal(["b.txt"], folder.Entries);
        Assert.Equal("src" + Path.DirectorySeparatorChar, folder.Path);

        Assert.Equal(GitOutcome.Missing, _git.Show("HEAD", "nope.txt").Outcome);
        Assert.Equal(GitOutcome.RefNotFound, _git.Show("zzz", "").Outcome);
    }

    [Fact]
    public void Show_ATag_PeelsToItsCommit_AndARootCommitListsEverythingAdded()
    {
        Init();
        string first = CommitFile(_root, "a.txt", "one\n", "first");
        using (var repo = new Repository(_root))
        {
            repo.Tags.Add("v1", repo.Head.Tip, new Signature("T", "t@example.invalid", DateTimeOffset.Now), "the tag");
        }

        var show = _git.Show("v1", "");
        Assert.Equal(first[..7], show.Commit!.Short);
        Assert.Equal(['A'], show.Changes.Select(c => c.Code));
    }

    // ---- diff ----

    [Fact]
    public void Diff_Unstaged_Staged_Commit_AndRange_WithoutLibgit2sDiff()
    {
        Init();
        string first = CommitFile(_root, "a.txt", "one\ntwo\nthree\n", "first");
        Write("a.txt", "one\n2\nthree\n");

        var unstaged = _git.Diff(new GitDiffRequest(GitDiffKind.Unstaged, ""), 500);
        Assert.Equal(GitOutcome.Ok, unstaged.Outcome);
        Assert.Equal([new GitChange('M', "a.txt", null, 1, 1, false)], unstaged.Files);
        Assert.Equal("--- a/a.txt\n+++ b/a.txt\n@@ -1,3 +1,3 @@\n one\n-two\n+2\n three", unstaged.Patch);
        Assert.Equal(1, unstaged.Added);
        Assert.Equal(1, unstaged.Deleted);
        Assert.False(unstaged.Truncated);
        Assert.Equal(7, unstaged.TotalLines);

        Assert.Empty(_git.Diff(new GitDiffRequest(GitDiffKind.Staged, ""), 500).Files);
        _git.Stage("", ["a.txt"], unstage: false);
        var staged = _git.Diff(new GitDiffRequest(GitDiffKind.Staged, ""), 500);
        Assert.Equal([new GitChange('M', "a.txt", null, 1, 1, false)], staged.Files);
        Assert.Empty(_git.Diff(new GitDiffRequest(GitDiffKind.Unstaged, ""), 500).Files);

        var committed = _git.Commit("", "second", amend: false, allowEmpty: false);
        Assert.Equal(1, committed.Files);
        var byCommit = _git.Diff(new GitDiffRequest(GitDiffKind.Commit, "", Reference: "HEAD"), 500);
        Assert.Equal(committed.Short + " (second)", byCommit.Label);
        Assert.Equal([new GitChange('M', "a.txt", null, 1, 1, false)], byCommit.Files);

        CommitFile(_root, "b.txt", "b\n", "third");
        var range = _git.Diff(new GitDiffRequest(GitDiffKind.Range, "", From: first[..7], To: "HEAD"), 500);
        Assert.Equal(first[..7] + " to " + _git.Log("", "", 1).Commits[0].Short, range.Label);
        Assert.Equal(['M', 'A'], range.Files.Select(f => f.Code));
        Assert.Equal(["a.txt", "b.txt"], range.Files.Select(f => f.Path));

        var narrowed = _git.Diff(new GitDiffRequest(GitDiffKind.Range, "b.txt", From: first[..7], To: "HEAD"), 500);
        Assert.Equal(["b.txt"], narrowed.Files.Select(f => f.Path));
        Assert.Equal("--- /dev/null\n+++ b/b.txt\n@@ -0,0 +1 @@\n+b", narrowed.Patch);

        var cut = _git.Diff(new GitDiffRequest(GitDiffKind.Range, "", From: first[..7], To: "HEAD"), 3);
        Assert.True(cut.Truncated);
        Assert.Equal(3, cut.Patch.Split('\n').Length);
        Assert.Equal(11, cut.TotalLines);

        Assert.Equal(GitOutcome.RefNotFound, _git.Diff(new GitDiffRequest(GitDiffKind.Commit, "", Reference: "nope"), 500).Outcome);
        Assert.Equal(GitOutcome.Missing, _git.Diff(new GitDiffRequest(GitDiffKind.Commit, "zzz.txt", Reference: "HEAD"), 500).Outcome);
    }

    [Fact]
    public void Diff_ADeletedFile_ABinaryFile_AndANestedFolder()
    {
        Init();
        CommitFile(_root, Path.Combine("src", "a.txt"), "one\n", "first");
        File.WriteAllBytes(Path.Combine(_root, "pic.bin"), [0, 1, 2, 0, 3]);
        CommitFile(_root, "pic.bin", "", "second");
        File.WriteAllBytes(Path.Combine(_root, "pic.bin"), [0, 9, 9, 0, 3]);
        File.Delete(Path.Combine(_root, "src", "a.txt"));

        var unstaged = _git.Diff(new GitDiffRequest(GitDiffKind.Unstaged, ""), 500);
        Assert.Equal([new GitChange('M', "pic.bin", null, 0, 0, true), new GitChange('D', Path.Combine("src", "a.txt"), null, 0, 1, false)], unstaged.Files);
        Assert.Contains("Binary files differ", unstaged.Patch);
        Assert.Contains("--- a/src/a.txt\n+++ /dev/null\n@@ -1 +0,0 @@\n-one", unstaged.Patch);

        var under = _git.Diff(new GitDiffRequest(GitDiffKind.Unstaged, "src"), 500);
        Assert.Equal([Path.Combine("src", "a.txt")], under.Files.Select(f => f.Path));
    }

    [Fact]
    public void Diff_ACrlfOnlyChange_ShowsNothing()
    {
        Init();
        CommitFile(_root, "a.txt", "one\ntwo\n", "first");
        Write("a.txt", "one\r\ntwo\r\n");

        // Both sides are LF-normalised (the app's edit rule); git status still counts the file modified.
        var unstaged = _git.Diff(new GitDiffRequest(GitDiffKind.Unstaged, ""), 500);
        Assert.Equal([new GitChange('M', "a.txt", null, 0, 0, false)], unstaged.Files);
        Assert.Equal("", unstaged.Patch);
    }

    // ---- blame ----

    [Fact]
    public void Blame_NamesTheCommitOfEachLine_OverAWindow()
    {
        Init();
        string first = CommitFile(_root, "a.txt", "one\ntwo\nthree\n", "first");
        string second = CommitFile(_root, "a.txt", "one\n2\nthree\nfour\n", "second");

        var blame = _git.Blame("a.txt", "", null, null);
        Assert.Equal(GitOutcome.Ok, blame.Outcome);
        Assert.Equal("a.txt", blame.Path);
        Assert.Equal("main", blame.Reference);
        Assert.Equal((1, 4, 4), (blame.From, blame.To, blame.TotalLines));
        Assert.Equal(2, blame.Commits);
        Assert.Equal([first[..7], second[..7], first[..7], second[..7]], blame.Lines.Select(l => l.Short));
        Assert.Equal(["one", "2", "three", "four"], blame.Lines.Select(l => l.Text));
        Assert.Equal([1, 2, 3, 4], blame.Lines.Select(l => l.Number));

        var window = _git.Blame("a.txt", first[..7], 2, 3);
        Assert.Equal((2, 3, 3), (window.From, window.To, window.TotalLines));
        Assert.Equal(["two", "three"], window.Lines.Select(l => l.Text));
        Assert.All(window.Lines, l => Assert.Equal(first[..7], l.Short));

        Assert.Equal(GitOutcome.Missing, _git.Blame("nope.txt", "", null, null).Outcome);
        Assert.Equal(GitOutcome.Missing, _git.Blame("", "", null, null).Outcome);
    }

    // ---- branches ----

    [Fact]
    public void Branches_Create_Switch_Rename_List_AndTheirRefusals()
    {
        Init();
        string first = CommitFile(_root, "a.txt", "one\n", "first");

        var created = _git.CreateBranch("", "feature", "", switchTo: false);
        Assert.Equal((GitOutcome.Ok, "feature", first[..7]), (created.Outcome, created.Name, created.Short));
        Assert.Equal(GitOutcome.NameConflict, _git.CreateBranch("", "feature", "", switchTo: false).Outcome);
        Assert.Equal(GitOutcome.RefNotFound, _git.CreateBranch("", "other", "nope", switchTo: false).Outcome);

        var switched = _git.SwitchBranch("", "feature");
        Assert.Equal(GitOutcome.Ok, switched.Outcome);
        Assert.Equal("feature", _git.Status("").Branch);
        Assert.Equal(GitOutcome.RefNotFound, _git.SwitchBranch("", "nope").Outcome);

        CommitFile(_root, "a.txt", "feature\n", "on feature");
        var refs = _git.Refs("");
        Assert.Equal("feature", refs.Current);
        Assert.Equal(["feature", "main"], refs.Local.Select(b => b.Name));
        Assert.True(refs.Local[0].Current);
        Assert.False(refs.Local[1].Current);
        Assert.Equal("on feature", refs.Local[0].Subject);
        Assert.Empty(refs.Remote);
        Assert.Empty(refs.Tags);

        var renamed = _git.RenameBranch("", "feature", "topic");
        Assert.Equal((GitOutcome.Ok, "topic", "feature"), (renamed.Outcome, renamed.Name, renamed.OldName));
        Assert.Equal(GitOutcome.NameConflict, _git.RenameBranch("", "topic", "main").Outcome);
        Assert.Equal(GitOutcome.RefNotFound, _git.RenameBranch("", "feature", "x").Outcome);

        // A switch never overwrites local changes.
        Write("a.txt", "dirty\n");
        var conflict = _git.SwitchBranch("", "main");
        Assert.Equal(GitOutcome.CheckoutConflict, conflict.Outcome);
        Assert.Equal("dirty\n", File.ReadAllText(Path.Combine(_root, "a.txt")));

        var createdAndSwitched = _git.CreateBranch("", "another", "main", switchTo: true);
        Assert.Equal(GitOutcome.CheckoutConflict, createdAndSwitched.Outcome);
        Assert.Equal("another", createdAndSwitched.Name);   // created, not switched
        Assert.NotNull(new Repository(_root).Branches["another"]);
    }

    [Fact]
    public void CreateBranch_OnAnUnbornRepository_IsUnborn()
    {
        Init();
        Assert.Equal(GitOutcome.Unborn, _git.CreateBranch("", "feature", "", switchTo: false).Outcome);
    }

    // ---- stage / commit ----

    [Fact]
    public void Stage_ADot_StagesEverythingUnderTheFolder()
    {
        Init();
        CommitFile(_root, "a.txt", "a\n", "first");
        Write("a.txt", "a2\n");
        Write(Path.Combine("src", "b.txt"), "b\n");
        Write(Path.Combine("src", "deep", "c.txt"), "c\n");

        var under = _git.Stage("src", ["."], unstage: false);
        Assert.Equal(GitOutcome.Ok, under.Outcome);
        Assert.Equal([Path.Combine("src", "b.txt"), Path.Combine("src", "deep", "c.txt")], under.Paths);
        Assert.Equal("src" + Path.DirectorySeparatorChar, under.Folder);

        var all = _git.Stage("", ["."], unstage: false);
        Assert.Equal(["a.txt"], all.Paths);
        Assert.Equal(['A', 'A', 'M'], _git.Status("").Staged.OrderBy(e => e.Code).Select(e => e.Code));
        Assert.Empty(_git.Status("").Untracked);

        var nothing = _git.Stage("", ["."], unstage: false);
        Assert.Empty(nothing.Paths);

        var unstaged = _git.Stage("", ["a.txt", "src/b.txt"], unstage: true);
        Assert.Equal([ "a.txt", Path.Combine("src", "b.txt") ], unstaged.Paths);
        Assert.True(unstaged.Unstage);
        Assert.Equal([Path.Combine("src", "deep", "c.txt")], _git.Status("").Staged.Select(e => e.Path));

        Assert.Equal(GitOutcome.OutsideRoot, _git.Stage("", ["../x"], unstage: false).Outcome);
        Assert.Equal(GitOutcome.Missing, _git.Stage("", ["nope.txt"], unstage: false).Outcome);
        Assert.Equal(["a.txt", Path.Combine("src", "b.txt")], _git.Stage("", ["./"], unstage: false).Paths);   // the root itself = everything under it
        Assert.Equal(GitOutcome.TooManyPaths, _git.Stage("", Enumerable.Repeat("a.txt", GitAccess.MaxPathsPerCall + 1).ToList(), unstage: false).Outcome);
    }

    [Fact]
    public void Commit_RefusesNothingStaged_AndAmends()
    {
        Init();
        Assert.Equal(GitOutcome.NothingToAmend, _git.Commit("", "x", amend: true, allowEmpty: false).Outcome);
        Assert.Equal(GitOutcome.NothingStaged, _git.Commit("", "x", amend: false, allowEmpty: false).Outcome);

        Write("a.txt", "one\n");
        _git.Stage("", ["a.txt"], unstage: false);
        var first = _git.Commit("", "first", amend: false, allowEmpty: false);
        Assert.Equal((GitOutcome.Ok, "main", "first", 1, 1, 0, false), (first.Outcome, first.Branch, first.Subject, first.Files, first.Added, first.Deleted, first.Amended));

        var amended = _git.Commit("", "first, reworded", amend: true, allowEmpty: false);
        Assert.Equal((GitOutcome.Ok, "first, reworded", true), (amended.Outcome, amended.Subject, amended.Amended));
        Assert.Single(_git.Log("", "", 10).Commits);

        var empty = _git.Commit("", "empty", amend: false, allowEmpty: true);
        Assert.Equal((GitOutcome.Ok, 0), (empty.Outcome, empty.Files));

        using var repo = new Repository(_root);
        Assert.Equal(_time.GetLocalNow(), repo.Head.Tip.Author.When);   // stamped from the app's clock
    }

    [Fact]
    public void Commit_WithoutAnIdentityAnywhere_IsNoIdentity()
    {
        Directory.CreateDirectory(_root);
        Repository.Init(_root);   // no local identity
        Write("a.txt", "one\n");
        _git.Stage("", ["a.txt"], unstage: false);
        string empty = Path.Combine(_dir, "no-config");
        Directory.CreateDirectory(empty);
        var levels = new[] { ConfigurationLevel.Global, ConfigurationLevel.Xdg, ConfigurationLevel.System, ConfigurationLevel.ProgramData };
        var saved = levels.ToDictionary(l => l, l => GlobalSettings.GetConfigSearchPaths(l).ToArray());
        try
        {
            foreach (var level in levels)
            {
                GlobalSettings.SetConfigSearchPaths(level, empty);
            }

            Assert.Equal(GitOutcome.NoIdentity, _git.Commit("", "x", amend: false, allowEmpty: false).Outcome);
        }
        finally
        {
            foreach (var level in levels)
            {
                GlobalSettings.SetConfigSearchPaths(level, saved[level]);
            }
        }
    }

    // ---- identity (/gituser, 2026-09-21) ----

    [Fact]
    public void SetLocalIdentity_WritesTheTwoKeys_KeepsASectionAlreadyThere_UnlessForced_AndNeedsARepository()
    {
        Assert.Equal(GitOutcome.NoRepository, _git.SetLocalIdentity("a@x", "A", force: false).Outcome);

        Repository.Init(_root);   // no local identity
        var written = _git.SetLocalIdentity("a@x", "A", force: false);
        Assert.Equal(GitOutcome.Ok, written.Outcome);
        Assert.True(written.Written);
        Assert.Equal(("a@x", "A"), (written.Email, written.Name));

        var kept = _git.SetLocalIdentity("b@x", "B", force: false);
        Assert.Equal(GitOutcome.Ok, kept.Outcome);
        Assert.False(kept.Written);
        Assert.Equal(("a@x", "A"), (kept.Email, kept.Name));   // what is there, for the notice

        var forced = _git.SetLocalIdentity("b@x", "B", force: true);
        Assert.True(forced.Written);
        using var repo = new Repository(_root);
        Assert.Equal("b@x", repo.Config.Get<string>("user.email", ConfigurationLevel.Local)!.Value);
        Assert.Equal("B", repo.Config.Get<string>("user.name", ConfigurationLevel.Local)!.Value);
        Assert.Equal("Set user.name B and user.email b@x in the repository's config", GitAccess.IdentitySetLogLine("B", "b@x"));

        // Half a section (the name alone) counts as one: kept until forced.
        repo.Config.Unset("user.email", ConfigurationLevel.Local);
        var half = _git.SetLocalIdentity("c@x", "C", force: false);
        Assert.False(half.Written);
        Assert.Equal(("", "B"), (half.Email, half.Name));
    }

    // ---- stash ----

    [Fact]
    public void Stash_Push_List_Pop_Apply_AndTheirRefusals()
    {
        Init();
        Assert.Equal(GitOutcome.Unborn, _git.StashPush("", "x", includeUntracked: false).Outcome);
        CommitFile(_root, "a.txt", "one\n", "first");
        Assert.Equal(GitOutcome.NothingToStash, _git.StashPush("", "x", includeUntracked: false).Outcome);
        Assert.Empty(_git.Stashes("").Stashes);

        Write("a.txt", "one\ntwo\n");
        Write("new.txt", "n\n");
        var pushed = _git.StashPush("", "wip", includeUntracked: false);
        Assert.Equal((GitOutcome.Ok, 0, 1), (pushed.Outcome, pushed.Index, pushed.Files));
        Assert.Equal("On main: wip", pushed.Message);
        Assert.Equal("one\n", File.ReadAllText(Path.Combine(_root, "a.txt")));
        Assert.True(File.Exists(Path.Combine(_root, "new.txt")));   // untracked kept

        var list = _git.Stashes("");
        Assert.Equal([new GitStashInfo(0, "On main: wip", pushed.Stashes[0].Short)], list.Stashes);

        Assert.Equal(GitOutcome.NoStash, _git.StashApply("", 3, pop: true).Outcome);
        Assert.Equal("3", _git.StashApply("", 3, pop: true).Detail);

        var applied = _git.StashApply("", 0, pop: false);
        Assert.Equal((GitOutcome.Ok, "On main: wip", 1), (applied.Outcome, applied.Message, applied.Files));
        Assert.Equal("one\ntwo\n", File.ReadAllText(Path.Combine(_root, "a.txt")));
        Assert.Single(_git.Stashes("").Stashes);

        _git.Discard("", ["a.txt"], "");
        var popped = _git.StashApply("", 0, pop: true);
        Assert.Equal(GitOutcome.Ok, popped.Outcome);
        Assert.Empty(_git.Stashes("").Stashes);
        Assert.Equal("one\ntwo\n", File.ReadAllText(Path.Combine(_root, "a.txt")));

        var withUntracked = _git.StashPush("", "", includeUntracked: true);
        Assert.Equal(2, withUntracked.Files);
        Assert.False(File.Exists(Path.Combine(_root, "new.txt")));
        Assert.Equal("On main:", withUntracked.Message);   // libgit2's own wording for a stash without a message
    }

    // ---- discard / delete ----

    [Fact]
    public void Discard_PutsPathsBack_OrResetsHard()
    {
        Init();
        string first = CommitFile(_root, "a.txt", "one\n", "first");
        CommitFile(_root, "b.txt", "b\n", "second");
        Write("a.txt", "changed\n");
        Write("b.txt", "changed\n");
        Write("untracked.txt", "u\n");
        _git.Stage("", ["b.txt"], unstage: false);

        var paths = _git.Discard("", ["a.txt"], "");
        Assert.Equal((GitOutcome.Ok, 1, "main"), (paths.Outcome, paths.Files, paths.Reference));
        Assert.Equal(["a.txt"], paths.Paths);
        Assert.Equal("one\n", File.ReadAllText(Path.Combine(_root, "a.txt")));
        Assert.Equal("changed\n", File.ReadAllText(Path.Combine(_root, "b.txt")));

        Assert.Equal(GitOutcome.Missing, _git.Discard("", ["untracked.txt"], "").Outcome);
        Assert.Equal(GitOutcome.RefNotFound, _git.Discard("", ["a.txt"], "nope").Outcome);

        var reset = _git.Discard("", [], first[..7]);
        Assert.Equal((GitOutcome.Ok, 1, first[..7], "first"), (reset.Outcome, reset.Files, reset.Short, reset.Subject));
        Assert.Empty(reset.Paths);
        Assert.False(File.Exists(Path.Combine(_root, "b.txt")));   // the branch moved back to the first commit
        Assert.True(File.Exists(Path.Combine(_root, "untracked.txt")));
        Assert.Single(_git.Log("", "", 10).Commits);
    }

    [Fact]
    public void Delete_ABranch_ATag_AStash_AndTheRefusals()
    {
        Init();
        string first = CommitFile(_root, "a.txt", "one\n", "first");
        _git.CreateBranch("", "feature", "", switchTo: false);
        using (var repo = new Repository(_root))
        {
            repo.Tags.Add("v1", repo.Head.Tip);
        }

        Write("a.txt", "two\n");
        _git.StashPush("", "keep", includeUntracked: false);

        Assert.Equal(GitOutcome.CurrentBranch, _git.Delete("", GitDeleteKind.Branch, "main", 0).Outcome);
        Assert.Equal(GitOutcome.RefNotFound, _git.Delete("", GitDeleteKind.Branch, "nope", 0).Outcome);
        var branch = _git.Delete("", GitDeleteKind.Branch, "feature", 0);
        Assert.Equal((GitOutcome.Ok, GitDeleteKind.Branch, "feature", first[..7]), (branch.Outcome, branch.Kind, branch.Name, branch.Short));
        Assert.Equal(["main"], _git.Refs("").Local.Select(b => b.Name));

        var tag = _git.Delete("", GitDeleteKind.Tag, "v1", 0);
        Assert.Equal((GitOutcome.Ok, "v1", first[..7]), (tag.Outcome, tag.Name, tag.Short));
        Assert.Equal(GitOutcome.RefNotFound, _git.Delete("", GitDeleteKind.Tag, "v1", 0).Outcome);

        Assert.Equal(GitOutcome.NoStash, _git.Delete("", GitDeleteKind.Stash, "", 4).Outcome);
        var stash = _git.Delete("", GitDeleteKind.Stash, "", 0);
        Assert.Equal((GitOutcome.Ok, "stash@{0}", "On main: keep"), (stash.Outcome, stash.Name, stash.Message));
        Assert.Empty(_git.Stashes("").Stashes);
    }

    // ---- the log ----

    [Fact]
    public void TheDestructiveActs_LogAtInfo_UnderGit()
    {
        Init();
        CommitFile(_root, "a.txt", "one\n", "first");
        _git.CreateBranch("", "feature", "", switchTo: false);
        Write("a.txt", "two\n");
        var lines = new List<(DiagnosticLevel Level, string Message)>();
        void Capture(DiagnosticEvent e)
        {
            if (e.Category == GitAccess.Category)
            {
                lines.Add((e.Level, e.Message));
            }
        }

        DiagnosticLog.Emitted += Capture;
        try
        {
            _git.Discard("", ["a.txt"], "");
            _git.Delete("", GitDeleteKind.Branch, "feature", 0);
            _git.Stage("", ["a.txt"], unstage: false);
        }
        finally
        {
            DiagnosticLog.Emitted -= Capture;
        }

        string sha = _git.Log("", "", 1).Commits[0].Short;
        Assert.Equal(
            [(DiagnosticLevel.Info, "Discarded changes in 1 path back to main (" + sha + ")"), (DiagnosticLevel.Info, "Deleted branch feature (was " + sha + ")")],
            lines.Where(l => l.Level == DiagnosticLevel.Info));
        Assert.DoesNotContain(lines, l => l.Level > DiagnosticLevel.Info);
        Assert.Equal("Staged 1 path", GitAccess.StagedLogLine(1, false));
        Assert.Equal("Unstaged 2 paths", GitAccess.StagedLogLine(2, true));
        Assert.Equal("Amended abc1234 on main", GitAccess.CommittedLogLine("abc1234", "main", true));
        Assert.Equal("Reset hard to main (abc1234)", GitAccess.DiscardedLogLine(null, "main", "abc1234"));
        Assert.Equal("Deleted stash stash@{1} (was abc1234)", GitAccess.DeletedLogLine(GitDeleteKind.Stash, "stash@{1}", "abc1234"));
        Assert.Equal("Popped stash@{0}", GitAccess.StashAppliedLogLine(0, true));
    }
}
