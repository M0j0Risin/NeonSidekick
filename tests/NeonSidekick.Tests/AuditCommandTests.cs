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
        Assert.Equal(AuditText.BadPasses("51"), Parse("51").Error);
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
}
