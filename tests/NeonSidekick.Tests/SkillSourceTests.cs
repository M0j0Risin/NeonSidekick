using NeonSidekick.Skills;

namespace NeonSidekick.Tests;

public class SkillSourceTests
{
    private static SkillSource Parse(string text)
    {
        Assert.True(SkillSource.TryParse(text, out var source, out string? error), error);
        return source!;
    }

    private static string Refuse(string text)
    {
        Assert.False(SkillSource.TryParse(text, out var source, out string? error));
        Assert.Null(source);
        return error!;
    }

    [Theory]
    [InlineData("pdf")]
    [InlineData("excel reports")]
    [InlineData("  react / testing ")]
    public void Words_AreASearch(string text)
    {
        var source = Parse(text);
        Assert.Equal(SkillSourceKind.Search, source.Kind);
        Assert.Equal(text.Trim(), source.Query);
        Assert.Null(source.ArchiveUrl);
    }

    [Fact]
    public void OwnerRepo_AndOwnerRepoSkill_AreGitHub()
    {
        var repo = Parse("anthropics/skills");
        Assert.Equal(SkillSourceKind.GitHub, repo.Kind);
        Assert.Equal(("anthropics", "skills", SkillSource.DefaultRef, (string?)null, (string?)null), (repo.Owner, repo.Repo, repo.Ref, repo.SubPath, repo.SkillId));
        Assert.Equal("https://codeload.github.com/anthropics/skills/zip/HEAD", repo.ArchiveUrl!.AbsoluteUri);
        Assert.Equal("anthropics/skills", repo.Label);

        var one = Parse("anthropics/skills.git/pdf");
        Assert.Equal(("anthropics", "skills", "pdf"), (one.Owner, one.Repo, one.SkillId));
        Assert.Equal("anthropics/skills", one.RepoName);
    }

    [Fact]
    public void TheListingsUrls_AreTheApisAndRaws_AndTheFolderUrlReadsBack()
    {
        const string sha = "33375500bcea98d610eb30ce10ac4e59b89c390d";
        var tree = Parse("https://github.com/anthropics/skills/tree/main/skills/pdf");
        Assert.Equal("https://api.github.com/repos/anthropics/skills/commits/main", tree.CommitUrl.AbsoluteUri);
        Assert.Equal("https://api.github.com/repos/anthropics/skills/git/trees/" + sha + "?recursive=1", tree.TreeUrl(sha).AbsoluteUri);
        Assert.Equal("https://raw.githubusercontent.com/anthropics/skills/" + sha + "/skills/my%20pdf/a%23b.md", tree.RawUrl(sha, "skills/my pdf/a#b.md").AbsoluteUri);
        Assert.Equal("https://github.com/anthropics/skills/tree/" + sha, tree.FolderUrl(sha, "").AbsoluteUri);

        var back = Parse(tree.FolderUrl(sha, "skills/pdf").AbsoluteUri);
        Assert.Equal((sha, "skills/pdf"), (back.Ref, back.SubPath));
    }

    [Fact]
    public void GitHubUrls_TakeTheRefAndThePath()
    {
        var root = Parse("https://github.com/anthropics/skills");
        Assert.Equal(("anthropics", "skills", "HEAD", (string?)null), (root.Owner, root.Repo, root.Ref, root.SubPath));

        var bare = Parse("github.com/anthropics/skills.git");
        Assert.Equal(("anthropics", "skills"), (bare.Owner, bare.Repo));

        var tree = Parse("https://github.com/anthropics/skills/tree/main/skills/pdf");
        Assert.Equal(("main", "skills/pdf"), (tree.Ref, tree.SubPath));
        Assert.Equal("https://codeload.github.com/anthropics/skills/zip/main", tree.ArchiveUrl!.AbsoluteUri);
        Assert.Equal("anthropics/skills@main", tree.Label);

        var blob = Parse("https://github.com/anthropics/skills/blob/v2/skills/pdf/SKILL.md");
        Assert.Equal(("v2", "skills/pdf"), (blob.Ref, blob.SubPath));

        var treeRoot = Parse("https://github.com/o/r/tree/dev");
        Assert.Equal(("dev", (string?)null), (treeRoot.Ref, treeRoot.SubPath));
    }

    [Fact]
    public void AZipUrl_IsAZip()
    {
        var zip = Parse("https://example.com/files/skill.ZIP?x=1");
        Assert.Equal(SkillSourceKind.Zip, zip.Kind);
        Assert.Equal(zip.ZipUrl, zip.ArchiveUrl);
        Assert.Equal("https://example.com/files/skill.ZIP?x=1", zip.RepoName);
    }

    [Theory]
    [InlineData("http://github.com/o/r")]
    [InlineData("https://example.com/page")]
    [InlineData("https://github.com/o")]
    [InlineData("https://github.com/o/r/issues/1")]
    [InlineData("https://github.com/o/r/blob/main/README.md")]
    [InlineData("https://github.com/o/r/tree")]
    [InlineData("ftp://example.com/x.zip")]
    public void OtherUrls_AreRefused(string text) =>
        Assert.Equal(SkillInstallText.UnsupportedUrlError(text), Refuse(text));

    [Theory]
    [InlineData("o/r/s/t")]
    [InlineData("o/../r")]
    [InlineData("o/r/Bad_Name")]
    [InlineData("o!/r")]
    public void BadRepositories_AreRefused(string text) =>
        Assert.Equal(SkillInstallText.BadRepoError(text), Refuse(text));

    [Fact]
    public void Blank_IsTheUsage() => Assert.Equal(SkillInstallText.UsageError, Refuse("  "));

    [Fact]
    public void SplitFlags_TakesTheFlagsWhereverTheyStand()
    {
        Assert.Equal(("pdf", (SkillScope?)SkillScope.Global, true), SkillSource.SplitFlags("--yes pdf --GLOBAL"));
        Assert.Equal(("excel reports", (SkillScope?)SkillScope.Profile, false), SkillSource.SplitFlags("excel --global reports --profile"));
        Assert.Equal(("o/r", (SkillScope?)null, false), SkillSource.SplitFlags(" o/r "));
    }
}
