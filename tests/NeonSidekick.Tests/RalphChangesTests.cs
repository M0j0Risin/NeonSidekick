using LibGit2Sharp;
using NeonSidekick.Files;
using NeonSidekick.Git;
using NeonSidekick.Plans;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// A Ralph pass's evidence (2026-10-18, <see cref="RalphChanges"/>) over a real temp repository pinned to main, a working directory in a subfolder of
/// it, and a plain folder: what the pass changed (never what was dirty before), a new folder of files, a binary file, one too large to diff, and the
/// flags.
/// </summary>
public sealed class RalphChangesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new();

    public void Dispose() => GitAccessTests.DeleteTree(_dir);

    private (WorkingDirectory Files, GitAccess Git, string Root) Repo(string sub = "")
    {
        string repo = GitAccessTests.Init(Path.Combine(_dir, "repo"));
        GitAccessTests.CommitFile(repo, Path.Combine(sub, "app.cs"), "one\ntwo\n", "first");
        GitAccessTests.CommitFile(repo, Path.Combine(sub, "dirty.cs"), "a\n", "second");
        GitAccessTests.CommitFile(repo, "outside.cs", "x\n", "third");
        string root = Path.Combine(repo, sub);
        var files = new WorkingDirectory(() => root, _time);
        return (files, new GitAccess(files, _time), root);
    }

    private static void Write(string root, string relative, string text)
    {
        string full = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    [Fact]
    public void InARepository_ThePassesOwnChanges_AreListedAndDiffed()
    {
        var (files, git, root) = Repo();
        Write(root, "dirty.cs", "a\nbefore the pass\n");   // dirty before: the pass's diff starts from here
        var before = RalphChanges.Snapshot(files, git);
        Assert.True(before.Git);

        Write(root, "app.cs", "one\nTWO\n");
        Write(root, "dirty.cs", "a\nbefore the pass\nand after\n");
        Write(root, "fresh/deep/new.cs", "brand new\n");
        Write(root, ".neon/plans/p/ralph.md", "the log");          // the app's own: never evidence
        var after = RalphChanges.Snapshot(files, git);
        var delta = RalphChanges.Delta(before, after, files, git, ["app.cs", "missing.cs"]);

        Assert.Equal(["app.cs", "dirty.cs", "fresh/deep/new.cs"], delta.Changed);
        Assert.Contains("--- a/app.cs\n+++ b/app.cs\n@@ -1,2 +1,2 @@\n one\n-two\n+TWO\n", delta.Diff);
        Assert.Contains("+and after", delta.Diff);
        Assert.DoesNotContain("+before the pass", delta.Diff);
        Assert.Contains("--- /dev/null\n+++ b/fresh/deep/new.cs", delta.Diff);
        Assert.False(delta.NothingChanged);
        Assert.Equal(["missing.cs"], delta.NamedMissing);
        Assert.Contains(RalphText.FlagNamedMissing(["missing.cs"]), delta.Flags);
    }

    [Fact]
    public void TheSnapshotAfter_ReadsOnlyTheFilesThatChanged_AndAFileWhoseTextWasNotKept_IsListedWithNoDiff()
    {
        // 2026-10-19, the fifth review: every changed and untracked file was read whole before and after every pass.
        var (files, git, root) = Repo();
        Write(root, "dirty.cs", "a\nbefore the pass\n");
        Write(root, "bin/untouched.dll.txt", "build output\n");
        var before = RalphChanges.Snapshot(files, git);

        Write(root, "dirty.cs", "a\nbefore the pass\nand after\n");
        var after = RalphChanges.Snapshot(files, git, since: before);

        Assert.Same(before.Files["bin/untouched.dll.txt"], after.Files["bin/untouched.dll.txt"]);   // as it was: taken over unread
        Assert.NotSame(before.Files["dirty.cs"], after.Files["dirty.cs"]);
        Assert.Equal(["dirty.cs"], RalphChanges.Delta(before, after, files, git, []).Changed);

        // A file the budget left unread before the pass: listed, never diffed against nothing.
        var unread = before with { Files = new Dictionary<string, RalphFileMark>(before.Files) { ["dirty.cs"] = before.Files["dirty.cs"] with { Text = null, Unread = true } } };
        var delta = RalphChanges.Delta(unread, after, files, git, []);
        Assert.Equal(["dirty.cs"], delta.Changed);
        Assert.Contains(RalphText.NotKeptToDiff("dirty.cs"), delta.Diff);
        Assert.DoesNotContain("+and after", delta.Diff);
    }

    [Fact]
    public void ADirtyFilePastTheTextLimit_ChangedByThePass_IsNamedWithItsSize_NeverDiffedAsANewFile()
    {
        // 2026-10-19: its text was never read, and the delta took that as an empty file before the pass and HEAD's text after, so the reviewer saw
        // "a new file holding HEAD's version".
        var (files, git, root) = Repo();
        string big = new string('x', (int)RalphChanges.MaxTextBytes + 10) + "\n";
        Write(root, "app.cs", big);   // dirty before the pass, and past the limit
        var before = RalphChanges.Snapshot(files, git);
        Assert.True(before.Files["app.cs"] is { Unread: true, Text: null });

        Write(root, "app.cs", big + "and the pass's line\n");
        var after = RalphChanges.Snapshot(files, git, since: before);
        var delta = RalphChanges.Delta(before, after, files, git, []);

        Assert.Equal(["app.cs"], delta.Changed);
        Assert.Contains(RalphText.TooLargeToRead("app.cs", (after.Files["app.cs"].Length / 1024).ToString(System.Globalization.CultureInfo.InvariantCulture) + " KB"), delta.Diff);
        Assert.DoesNotContain("/dev/null", delta.Diff);
        Assert.DoesNotContain("+one", delta.Diff);   // HEAD's text, the wrong "after"
    }

    [Fact]
    public void WorkThePassCommitted_IsListedAndDiffed_FromHeadAsThePassBegan()
    {
        // 2026-10-19, the second review: only the working tree's status was read, so a file changed and committed was in neither snapshot.
        var (files, git, root) = Repo();
        var before = RalphChanges.Snapshot(files, git);
        Assert.NotNull(before.Head);

        GitAccessTests.CommitFile(Path.Combine(_dir, "repo"), "app.cs", "one\nTWO\n", "the pass's work");
        GitAccessTests.CommitFile(Path.Combine(_dir, "repo"), "added.cs", "new\n", "and a file");
        Write(root, "dirty.cs", "a\nleft dirty\n");
        var after = RalphChanges.Snapshot(files, git);
        var delta = RalphChanges.Delta(before, after, files, git, []);

        Assert.NotEqual(before.Head, after.Head);
        Assert.Equal(["added.cs", "app.cs", "dirty.cs"], delta.Changed);
        Assert.Contains("--- a/app.cs\n+++ b/app.cs\n@@ -1,2 +1,2 @@\n one\n-two\n+TWO\n", delta.Diff);
        Assert.Contains("--- /dev/null\n+++ b/added.cs", delta.Diff);
        Assert.Contains("+left dirty", delta.Diff);
        Assert.False(delta.NothingChanged);
    }

    [Fact]
    public void ASecretsFile_IsListedNeverDiffed_AndTheDiffIsRedacted()
    {
        // 2026-10-19, the third review: the diff goes to the reviewer (an advisor may be a hosted API), and a pass that edited .env sent it whole.
        var (files, git, root) = Repo();
        var before = RalphChanges.Snapshot(files, git);
        Write(root, ".env", "API_TOKEN=abc123secret\n");
        Write(root, "app.cs", "one\nvar url = \"https://admin:hunter2@db.example/x\";\n");
        var after = RalphChanges.Snapshot(files, git);

        var delta = RalphChanges.Delta(before, after, files, git, []);

        Assert.Equal([".env", "app.cs"], delta.Changed);
        Assert.Contains(RalphText.SecretNotDiffed(".env"), delta.Diff);
        Assert.DoesNotContain("abc123secret", delta.Diff);
        Assert.DoesNotContain("hunter2", delta.Diff);
        Assert.Contains("https://admin:", delta.Diff);
    }

    [Fact]
    public void AWorkingDirectoryInASubfolder_SeesOnlyItsOwn()
    {
        var (files, git, root) = Repo("sub");
        var before = RalphChanges.Snapshot(files, git);
        Write(root, "app.cs", "changed\n");
        File.WriteAllText(Path.Combine(_dir, "repo", "outside.cs"), "changed outside\n");

        var delta = RalphChanges.Delta(before, RalphChanges.Snapshot(files, git), files, git, []);

        Assert.Equal(["app.cs"], delta.Changed);
    }

    [Fact]
    public void ABinaryFile_IsListedWithItsSize_AndOneTooLarge_SaysSo()
    {
        var (files, git, root) = Repo();
        var before = RalphChanges.Snapshot(files, git);
        File.WriteAllBytes(Path.Combine(root, "blob.bin"), [1, 0, 2, 0, 3]);
        Write(root, "big.txt", string.Join('\n', Enumerable.Range(0, 70_000).Select(i => "line " + i)) + "\n");

        var delta = RalphChanges.Delta(before, RalphChanges.Snapshot(files, git), files, git, []);

        Assert.Equal(["big.txt", "blob.bin"], delta.Changed);
        Assert.Contains("(binary blob.bin, 5 bytes)", delta.Diff);
        Assert.Contains("(big.txt is too large to diff: 0 lines before, 70000 after)", delta.Diff);
        Assert.True(delta.Diff.Length <= RalphChanges.MaxDiffChars + 200);
    }

    [Fact]
    public void NothingChanged_IsAFlag_AndTestFilesTouched_AnotherOne()
    {
        var (files, git, root) = Repo();
        var before = RalphChanges.Snapshot(files, git);
        var idle = RalphChanges.Delta(before, RalphChanges.Snapshot(files, git), files, git, []);
        Assert.True(idle.NothingChanged);
        Assert.Equal([RalphText.FlagNothingChanged], idle.Flags);

        Write(root, "tests/AppTests.cs", "test\n");
        var tests = RalphChanges.Delta(before, RalphChanges.Snapshot(files, git), files, git, []);
        Assert.True(tests.TestsTouched);
        Assert.Contains(RalphText.FlagTestsTouched, tests.Flags);
    }

    [Theory]
    [InlineData("tests/AppTests.cs", true)]
    [InlineData("src/test_app.py", true)]
    [InlineData("web/app.spec.ts", true)]
    [InlineData("pkg/server_test.go", true)]
    [InlineData("src/Contest.cs", false)]
    [InlineData("src/AppTest.cs", true)]
    [InlineData("src/App.cs", false)]
    [InlineData("docs/testing.md", false)]
    public void IsTestPath_ReadsGenericNames(string path, bool expected) => Assert.Equal(expected, RalphChanges.IsTestPath(path));

    [Fact]
    public void APlainFolder_ListsChangesByStamp_WithNoDiff()
    {
        string root = Path.Combine(_dir, "plain");
        Directory.CreateDirectory(root);
        Write(root, "a.txt", "one");
        var files = new WorkingDirectory(() => root, _time);
        var git = new GitAccess(files, _time);
        var before = RalphChanges.Snapshot(files, git);
        Assert.False(before.Git);

        Write(root, "a.txt", "one more");
        Write(root, "new/b.txt", "b");
        var delta = RalphChanges.Delta(before, RalphChanges.Snapshot(files, git), files, git, []);

        Assert.Equal(["a.txt", "new/b.txt"], delta.Changed);
        Assert.Equal("", delta.Diff);
    }
}
