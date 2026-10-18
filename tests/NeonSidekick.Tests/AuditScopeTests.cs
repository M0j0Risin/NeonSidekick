using LibGit2Sharp;
using NeonSidekick.Audits;
using NeonSidekick.Git;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// A code audit's scope (2026-10-18): the walk and the repository's index, the exclusions in their order, the 5,000 cut, and the git
/// scopes on temp repositories pinned to <c>main</c>.
/// </summary>
public sealed class AuditScopeTests : IDisposable
{
    private readonly AuditSandbox _box = new();

    public void Dispose() => _box.Dispose();

    private AuditScopeResult Resolve(AuditScopeKind kind, params string[] paths) =>
        AuditScopes.Resolve(_box.Files, _box.Git, new AuditScopeRequest(kind, paths), CancellationToken.None);

    [Fact]
    public void Tree_Walked_LeavesOutTheSkips_SecretsBinariesAndTheAppFolder()
    {
        _box.Write("src/a.cs", "a");
        _box.Write("src/bin/Debug/x.cs", "skipped folder");
        _box.Write("node_modules/m.js", "skipped");
        _box.Write(".neon/plans/p.md", "app folder");
        _box.Write(".env", "SECRET=1");
        _box.Write("certs/site.pem", "pem");
        _box.WriteBinary("logo.png");
        _box.Write("web/app.min.js", "minified");
        _box.Write("web/vendor/lib.js", "vendored");
        _box.Write("Form1.Designer.cs", "generated");
        _box.Write("package-lock.json", "{}");
        var result = Resolve(AuditScopeKind.Tree);
        Assert.Null(result.Error);
        var scope = result.Scope!;
        Assert.Equal(["src/a.cs"], scope.Files);
        Assert.Equal(2, scope.Secret);
        Assert.Equal(1, scope.Binary);
        Assert.Equal(4, scope.Vendored);
        Assert.Equal("2 secret-looking, 1 binary, 4 vendored or generated", scope.Skipped);
        Assert.Equal("the working directory", scope.Label);
        Assert.Equal("tree", scope.NameLabel);
        Assert.Equal("", scope.Words);
    }

    [Fact]
    public void ANamedPath_IsTakenWhole_AsFarAsTheVendoredSkipsGo_ButNeverSecrets()
    {
        _box.Write("assets/pdflib/pdf-lib.min.js", "x");
        _box.Write("assets/pdflib/.env", "s");
        var scope = Resolve(AuditScopeKind.Paths, "assets/pdflib").Scope!;
        Assert.Equal(["assets/pdflib/pdf-lib.min.js"], scope.Files);
        Assert.Equal(1, scope.Secret);
        Assert.Equal("assets/pdflib", scope.Label);
    }

    [Fact]
    public void AScopeLeftEmpty_IsNothingToAudit_NamingTheCounts()
    {
        _box.Write("only/.env", "s");
        _box.WriteBinary("only/blob.bin");
        var result = Resolve(AuditScopeKind.Paths, "only");
        Assert.Null(result.Scope);
        Assert.Equal(AuditText.NothingToAudit(AuditText.NoFilesLeft("only", "1 secret-looking, 1 binary")), result.Error);
    }

    [Fact]
    public void PastTheCap_TheListIsCut_TheRestCounted_AfterTheExclusions()
    {
        for (int i = 0; i < AuditText.MaxScopeFiles + 3; i++)
        {
            _box.Write($"many/f{i:D5}.txt", "x");
        }

        _box.Write("many/.env", "s");
        var scope = Resolve(AuditScopeKind.Paths, "many").Scope!;
        Assert.Equal(AuditText.MaxScopeFiles, scope.Files.Count);
        Assert.Equal(3, scope.MoreFiles);
        Assert.Equal(1, scope.Secret);
    }

    [Fact]
    public void InsideARepo_TheIndexAndTheUntracked_NeverTheIgnored()
    {
        GitAccessTests.Init(_box.Root);
        GitAccessTests.CommitFile(_box.Root, "src/a.cs", "a", "first");
        GitAccessTests.CommitFile(_box.Root, ".gitignore", "out/\n", "ignore");
        _box.Write("src/new.cs", "untracked");
        _box.Write("out/build.cs", "ignored");
        var scope = Resolve(AuditScopeKind.Tree).Scope!;
        Assert.Equal([".gitignore", "src/a.cs", "src/new.cs"], scope.Files);
        var tracked = _box.Git.TrackedFiles("", includeUntracked: true);
        Assert.Equal(GitOutcome.Ok, tracked.Outcome);
        Assert.Contains(Path.Combine("src", "a.cs"), tracked.Tracked);
        Assert.Contains(Path.Combine("src", "new.cs"), tracked.Untracked);
    }

    [Fact]
    public void ARepoInASubfolder_MapsIntoTheSandbox_TwoSideBySide_Walk()
    {
        string one = GitAccessTests.Init(Path.Combine(_box.Root, "one"));
        GitAccessTests.CommitFile(one, "a.cs", "a", "first");
        string two = GitAccessTests.Init(Path.Combine(_box.Root, "two"));
        GitAccessTests.CommitFile(two, "b.cs", "b", "first");
        Assert.Equal(["one/a.cs"], Resolve(AuditScopeKind.Paths, "one").Scope!.Files);
        Assert.Equal(["one/a.cs", "two/b.cs"], Resolve(AuditScopeKind.Tree).Scope!.Files);
    }

    [Fact]
    public void ASandboxInsideARepo_AGitScopeIsRefused_ATreeIsWalked()
    {
        GitAccessTests.Init(_box.Dir);
        _box.Write("a.cs", "a");
        var git = Resolve(AuditScopeKind.Diff);
        Assert.Null(git.Scope);
        Assert.Contains("above the working directory", git.Error);
        Assert.Equal(["a.cs"], Resolve(AuditScopeKind.Tree).Scope!.Files);
    }

    [Fact]
    public void Diff_TheWorkingChanges_UntrackedToo_AndACleanTreeIsNothing()
    {
        GitAccessTests.Init(_box.Root);
        GitAccessTests.CommitFile(_box.Root, "a.cs", "a", "first");
        GitAccessTests.CommitFile(_box.Root, "gone.cs", "g", "second");
        Assert.Equal(AuditText.NothingToAudit(AuditText.CleanTree), Resolve(AuditScopeKind.Diff).Error);
        Assert.Equal(AuditText.NothingToAudit(AuditText.NothingStaged), Resolve(AuditScopeKind.Staged).Error);
        _box.Write("a.cs", "changed");
        _box.Write("new.cs", "untracked");
        File.Delete(_box.Full("gone.cs"));
        var scope = Resolve(AuditScopeKind.Diff).Scope!;
        Assert.Equal(["a.cs", "new.cs"], scope.Files);
        Assert.Equal(["gone.cs"], scope.Deleted);
        Assert.Equal("diff", scope.Label);
    }

    [Fact]
    public void Staged_TheIndexAlone_ARenameUnderItsNewPath()
    {
        GitAccessTests.Init(_box.Root);
        GitAccessTests.CommitFile(_box.Root, "old.cs", "same content for the rename", "first");
        using (var repo = new Repository(_box.Root))
        {
            Commands.Move(repo, "old.cs", "renamed.cs");
        }

        _box.Write("unstaged.cs", "not staged");
        var scope = Resolve(AuditScopeKind.Staged).Scope!;
        Assert.Contains("renamed.cs", scope.Files);
        Assert.DoesNotContain("unstaged.cs", scope.Files);
    }

    [Fact]
    public void Branch_SinceTheMergeBase_AndOnTheBaseItIsNothing()
    {
        GitAccessTests.Init(_box.Root);
        string fork = GitAccessTests.CommitFile(_box.Root, "base.cs", "b", "first");
        Assert.Equal(AuditText.NothingToAudit(AuditText.OnBase("main")), Resolve(AuditScopeKind.Branch).Error);
        Assert.Equal(GitOutcome.Ok, _box.Git.CreateBranch("", "feature", "", switchTo: true).Outcome);
        GitAccessTests.CommitFile(_box.Root, "feature.cs", "f", "on the branch");
        var merge = _box.Git.MergeBase("", "feature", "main");
        Assert.Equal(fork, merge.Sha);
        var scope = AuditScopes.Resolve(_box.Files, _box.Git, new AuditScopeRequest(AuditScopeKind.Branch, []), CancellationToken.None).Scope!;
        Assert.Equal(["feature.cs"], scope.Files);
        Assert.Equal("main", scope.Base);
        Assert.Equal("branch main..HEAD", scope.Label);
        Assert.Equal("branch main", scope.Words);
        // A detached HEAD is fine: HEAD is the tip.
        using (var repo = new Repository(_box.Root))
        {
            Commands.Checkout(repo, repo.Head.Tip);
        }

        Assert.Equal(["feature.cs"], Resolve(AuditScopeKind.Branch).Scope!.Files);
    }

    [Fact]
    public void Branch_WithNoMainOrMaster_AsksForABase()
    {
        string repo = GitAccessTests.Init(_box.Root);
        GitAccessTests.CommitFile(repo, "a.cs", "a", "first");
        using (var r = new Repository(repo))
        {
            r.Branches.Rename("main", "trunk");
        }

        Assert.Equal(AuditText.NoBaseBranch, Resolve(AuditScopeKind.Branch).Error);
    }

    [Theory]
    [InlineData("web/app.min.js", true)]
    [InlineData("a/b/vendor/x.cs", true)]
    [InlineData("Cargo.lock", true)]
    [InlineData("x.g.i.cs", true)]
    [InlineData("src/bin/x.cs", true)]
    [InlineData("src/main.cs", false)]
    public void LooksVendored(string path, bool expected) => Assert.Equal(expected, AuditScopes.LooksVendored(path));

    [Theory]
    [InlineData(".env", true)]
    [InlineData("conf/.env.local", true)]
    [InlineData("id_rsa.pub", true)]
    [InlineData("keys/server.KEY", true)]
    [InlineData("vault.kdbx", true)]
    [InlineData("environment.cs", false)]
    public void LooksSecret(string path, bool expected) => Assert.Equal(expected, AuditScopes.LooksSecret(path));
}
