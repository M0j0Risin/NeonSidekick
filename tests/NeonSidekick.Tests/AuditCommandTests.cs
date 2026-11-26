using NeonSidekick.Audits;
using NeonSidekick.Plans;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary><c>/code-audit</c>'s grammar (2026-10-18): words in any order, the brief's two starts, paths and their refusals, the verbs, completion.</summary>
public sealed class AuditCommandTests : IDisposable
{
    private readonly AuditSandbox _box = new();

    public AuditCommandTests()
    {
        _box.Write("src/Web/a.cs", "x");
        _box.Write("tests/t.cs", "x");
        _box.Write("My Folder/b.cs", "x");
        _box.Write(".neon/plans/p.md", "x");
    }

    public void Dispose() => _box.Dispose();

    private AuditArguments Parse(string args) => AuditCommand.Parse(args, _box.Existing);

    [Fact]
    public void Bare_IsTheWholeTree_WithTheSettingsFocus()
    {
        var parsed = Parse("");
        Assert.Equal(AuditVerb.Start, parsed.Verb);
        Assert.Equal(AuditScopeKind.Tree, parsed.Scope!.Kind);
        Assert.Null(parsed.Focus);
        Assert.Null(parsed.Reviewer);
        Assert.Null(parsed.Passes);
        Assert.Equal("", parsed.Brief);
    }

    [Fact]
    public void Words_InAnyOrder()
    {
        var parsed = Parse("security 3 src/Web advisor tests");
        Assert.Null(parsed.Error);
        Assert.Equal(PlanReviewer.Advisor, parsed.Reviewer);
        Assert.Equal(3, parsed.Passes);
        Assert.Equal(AuditScopeKind.Paths, parsed.Scope!.Kind);
        Assert.Equal(["src/Web"], parsed.Scope.Paths);
        Assert.Equal(["security", "tests"], parsed.Focus);
    }

    [Fact]
    public void AFocusWordIsAFocusWord_TheFolderNeedsItsSlash()
    {
        Assert.Equal(["tests"], Parse("tests").Focus);
        Assert.Equal(AuditScopeKind.Tree, Parse("tests").Scope!.Kind);
        Assert.Equal(["tests"], Parse("tests/").Scope!.Paths);
        Assert.Equal(["tests"], Parse("./tests").Scope!.Paths);
    }

    [Fact]
    public void AQuotedPathWithABlank_IsOnePath()
    {
        var parsed = Parse("\"My Folder\" security");
        Assert.Equal(["My Folder"], parsed.Scope!.Paths);
        Assert.Equal(["security"], parsed.Focus);
    }

    [Fact]
    public void TheBrief_StartsAtTheFirstOtherWord_OrAfterDoubleDash_RawTextKept()
    {
        Assert.Equal("look at \"auth\" first", Parse("src/Web look at \"auth\" first").Brief);
        Assert.Equal(["src/Web"], Parse("src/Web look at \"auth\" first").Scope!.Paths);
        var dashed = Parse("src/Web -- tests are \"flaky\"");
        Assert.Equal("tests are \"flaky\"", dashed.Brief);
        Assert.Null(dashed.Focus);
        Assert.Equal("fix-ups in the parser", Parse("-- fix-ups in the parser").Brief);
        Assert.Equal(AuditVerb.Start, Parse("-- fix-ups in the parser").Verb);
    }

    [Fact]
    public void Scopes_DiffStagedBranch()
    {
        Assert.Equal(AuditScopeKind.Diff, Parse("diff").Scope!.Kind);
        Assert.Equal(AuditScopeKind.Staged, Parse("staged confirm").Scope!.Kind);
        var branch = Parse("branch develop security");
        Assert.Equal(AuditScopeKind.Branch, branch.Scope!.Kind);
        Assert.Equal("develop", branch.Scope.Base);
        Assert.Equal(["security"], branch.Focus);
        var bare = Parse("branch security");
        Assert.Null(bare.Scope!.Base);
        Assert.Equal(["security"], bare.Focus);
        Assert.Equal(["src/Web"], Parse("diff src/Web").Scope!.Paths);
    }

    [Fact]
    public void Refusals()
    {
        Assert.Equal(AuditText.AppFolderPath, Parse(".neon/plans").Error);
        Assert.Equal(AuditText.AppFolderPath, Parse(".\\.neon\\plans").Error);
        Assert.Equal(AuditText.AppFolderPath, Parse(".neon").Error);
        Assert.Equal(AuditText.NoSuchPath("src/Wbe"), Parse("src/Wbe").Error);
        Assert.Equal(AuditText.NoSuchPath("missing.cs"), Parse("missing.cs").Error);
        Assert.Equal(AuditText.TwoReviewers, Parse("model advisor").Error);
        Assert.Equal(AuditText.TwoCounts, Parse("2 3").Error);
        Assert.Equal(AuditText.BadPasses("101"), Parse("101").Error);
        Assert.Equal(100, Parse("100").Passes);   // the most since 2026-10-21 (50 before)
        Assert.Equal(AuditText.BadPasses("-1"), Parse("-1").Error);
    }

    [Fact]
    public void Verbs_OnlyAsTheFirstWord()
    {
        Assert.Equal((AuditVerb.Open, (string?)null), (Parse("open").Verb, Parse("open").Name));
        Assert.Equal((AuditVerb.Open, (string?)"x"), (Parse("open x").Verb, Parse("open x").Name));
        Assert.Equal((AuditVerb.Fix, (string?)null), (Parse("FIX").Verb, Parse("FIX").Name));
        Assert.Equal("open", Parse("src/Web open the box").Brief.Split(' ')[0]);
        var resume = Parse("resume r1 advisor 4");
        Assert.Equal((AuditVerb.Resume, "r1", PlanReviewer.Advisor, 4), (resume.Verb, resume.Name!, resume.Reviewer!.Value, resume.Passes!.Value));
        var bare = Parse("resume confirm");
        Assert.Null(bare.Name);
        Assert.Equal(PlanReviewer.Confirm, bare.Reviewer);
        // A report named model is reached as resume model model, the name first.
        var named = Parse("resume model model");
        Assert.Equal(("model", PlanReviewer.Model), (named.Name!, named.Reviewer!.Value));
        Assert.Equal(AuditText.TwoReviewers, Parse("resume r1 model any").Error);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("src diff", true)]
    [InlineData("-- the brief", true)]
    [InlineData("fix", true)]
    [InlineData("resume x", true)]
    [InlineData("model model", true)]
    [InlineData("open", false)]
    [InlineData("OPEN x", false)]
    public void StartsPasses_IsEveryFormButOpen(string args, bool starts) => Assert.Equal(starts, AuditCommand.StartsPasses(args));

    [Fact]
    public void ScopeFromHeader_RefusesAPathThatIsGone()
    {
        var (_, error) = AuditCommand.ScopeFromHeader("src/Gone", _box.Existing);
        Assert.Equal(AuditText.NoSuchPath("src/Gone"), error);
        var (branch, none) = AuditCommand.ScopeFromHeader("branch main src/Web", _box.Existing);
        Assert.Null(none);
        Assert.Equal((AuditScopeKind.Branch, "main"), (branch!.Kind, branch.Base!));
        Assert.Equal(AuditScopeKind.Tree, AuditCommand.ScopeFromHeader("", _box.Existing).Scope!.Kind);
    }

    [Fact]
    public void Complete_TheVerbsTheReportsTheWordsAndQuotedFolders()
    {
        IReadOnlyList<CompletionItem> Reports() => [new("src-2026-10-18", "stopped")];
        IReadOnlyList<string> Folders(string prefix) => ["My Folder/", "src/"];
        Assert.Contains(AuditCommand.Complete("op", Reports, Folders), i => i.Text == "open");
        Assert.Contains(AuditCommand.Complete("open ", Reports, Folders), i => i.Text == "open src-2026-10-18");
        Assert.Contains(AuditCommand.Complete("resume ", Reports, Folders), i => i.Text == "resume model");
        Assert.Contains(AuditCommand.Complete("security ", Reports, Folders), i => i.Text == "security \"My Folder/\"");
        Assert.Contains(AuditCommand.Complete("sec", Reports, Folders), i => i.Text == "security");
        // The completed quoted folder parses back to the same path.
        Assert.Equal(["My Folder"], Parse("security \"My Folder/\"").Scope!.Paths);
    }

    // ---- commit <rev> and commit <from>..<to> (2026-11-25) ----

    [Theory]
    [InlineData("commit abc", "abc")]
    [InlineData("commit a..b", "a..b")]
    [InlineData("commit a..", "a..")]
    [InlineData("commit 1234567", "1234567")]
    [InlineData("commit feature/x", "feature/x")]
    [InlineData("commit feature/x..main", "feature/x..main")]
    public void Commit_TakesTheNextWordAsItsRevision_WhateverItLooksLike(string line, string revision)
    {
        var parsed = Parse(line);
        Assert.Null(parsed.Error);
        Assert.Equal(AuditScopeKind.Commits, parsed.Scope!.Kind);
        Assert.Equal(revision, parsed.Scope.Revision);
        Assert.False(parsed.Scope.Pinned);
        Assert.Null(parsed.Passes);
        Assert.Empty(parsed.Scope.Paths);
    }

    [Theory]
    [InlineData("commit")]
    [InlineData("commit \"\"")]
    [InlineData("commit --")]
    [InlineData("commit ..b")]
    [InlineData("commit ..")]
    public void Commit_WithNoRevision_IsRefused(string line) => Assert.Equal(AuditText.NoRevision, Parse(line).Error);

    [Fact]
    public void Commit_ThreeDots_AreRefused() => Assert.Equal(AuditText.ThreeDots, Parse("commit a...b").Error);

    [Fact]
    public void Commit_WithPathsFocusAndABrief_AfterIt()
    {
        var parsed = Parse("commit HEAD~1 src/Web security check the parser");
        Assert.Null(parsed.Error);
        Assert.Equal(AuditScopeKind.Commits, parsed.Scope!.Kind);
        Assert.Equal("HEAD~1", parsed.Scope.Revision);
        Assert.Equal(["src/Web"], parsed.Scope.Paths);
        Assert.Equal(["security"], parsed.Focus);
        Assert.Equal("check the parser", parsed.Brief);
        Assert.Equal("./commit", AuditCommand.PathWord("commit"));
    }

    /// <summary>One scope beside commit, in either order (2026-11-26, the review's #52/#57): refused, never read as the brief; a brief after -- still may say it.</summary>
    [Theory]
    [InlineData("diff commit x")]
    [InlineData("commit abc diff")]
    [InlineData("commit abc staged")]
    [InlineData("commit abc branch")]
    [InlineData("commit abc commit def")]
    [InlineData("branch commit abc")]
    public void Commit_BesideAnotherScope_IsRefused(string line) => Assert.Equal(AuditText.OneScope, Parse(line).Error);

    [Fact]
    public void Commit_InABriefAfterDashes_IsTheBrief()
    {
        var parsed = Parse("diff -- commit messages");
        Assert.Null(parsed.Error);
        Assert.Equal("commit messages", parsed.Brief);
    }

    /// <summary>Once a scope word is typed the completer offers no other (2026-11-26): the parser would refuse it.</summary>
    [Fact]
    public void Complete_OffersNoScopeWord_AfterOne()
    {
        IReadOnlyList<CompletionItem> Reports() => [];
        IReadOnlyList<string> Folders(string prefix) => [];
        string[] scopes = [AuditText.DiffWord, AuditText.StagedWord, AuditText.BranchWord, AuditText.CommitWord];
        Assert.Contains(AuditCommand.Complete("", Reports, Folders), i => i.Text == AuditText.DiffWord);
        Assert.DoesNotContain(AuditCommand.Complete("commit abc ", Reports, Folders), i => scopes.Contains(i.Text.Split(' ')[^1]));
        Assert.DoesNotContain(AuditCommand.Complete("diff ", Reports, Folders), i => scopes.Contains(i.Text.Split(' ')[^1]));
    }

    [Fact]
    public void Commit_ScopeFromHeader_RoundTrips_AndABadHeaderIsSaidNeverThrown()
    {
        string from = new string('a', 40), to = new string('b', 40);
        var (request, error) = AuditCommand.ScopeFromHeader($"commit {from}..{to} src/Web", _box.Existing);
        Assert.Null(error);
        Assert.Equal(AuditScopeKind.Commits, request!.Kind);
        Assert.Equal($"{from}..{to}", request.Revision);
        Assert.True(request.Pinned);
        Assert.Equal(["src/Web"], request.Paths);
        Assert.Equal(AuditText.NoRevision, AuditCommand.ScopeFromHeader("commit", _box.Existing).Error);
        Assert.Equal(AuditText.NoRevision, AuditCommand.ScopeFromHeader("commit ..x", _box.Existing).Error);
        Assert.Equal(AuditText.ThreeDots, AuditCommand.ScopeFromHeader("commit a...b", _box.Existing).Error);
    }

    [Fact]
    public void Complete_OffersCommit_ThenTheCommitsAlone()
    {
        int asked = 0;
        IReadOnlyList<CompletionItem> Reports() => [];
        IReadOnlyList<string> Folders(string prefix) => ["src/"];
        IReadOnlyList<CompletionItem> Commits()
        {
            asked++;
            return [new("1a2b3c4", "the newest"), new("5e6f7a8", "the one before")];
        }

        Assert.Contains(AuditCommand.Complete("com", Reports, Folders, Commits), i => i.Text == "commit" && i.Note == AuditText.CommitNote);
        Assert.Equal(0, asked);
        Assert.Equal(["commit 1a2b3c4", "commit 5e6f7a8"], AuditCommand.Complete("commit ", Reports, Folders, Commits).Select(i => i.Text));
        Assert.Equal(["security commit 1a2b3c4"], AuditCommand.Complete("security commit 1a2", Reports, Folders, Commits).Select(i => i.Text));
        Assert.Equal(["commit HEAD~3..1a2b3c4", "commit HEAD~3..5e6f7a8"], AuditCommand.Complete("commit HEAD~3..", Reports, Folders, Commits).Select(i => i.Text));
        Assert.DoesNotContain(AuditCommand.Complete("resume ", Reports, Folders, Commits), i => i.Text == "resume commit");
        Assert.DoesNotContain(AuditCommand.Complete("diff commit ", Reports, Folders, Commits), i => i.Text == "diff commit 1a2b3c4");
        Assert.Empty(AuditCommand.Complete("commit ", Reports, Folders));
    }
}
