using NeonSidekick.Skills;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

public class SkillArchiveTests
{
    private static SkillArchive Open(byte[] bytes)
    {
        var archive = SkillArchive.TryOpen(bytes, out string? error);
        Assert.True(archive is not null, error);
        return archive!;
    }

    [Fact]
    public void TryOpen_RefusesWhatIsNoZip()
    {
        Assert.Null(SkillArchive.TryOpen("<html>404</html>"u8.ToArray(), out string? error));
        Assert.Equal(SkillInstallText.NotZipError, error);
        Assert.Null(SkillArchive.TryOpen([(byte)'P', (byte)'K', 3, 4, 0, 0], out error));
        Assert.StartsWith("The archive could not be read: ", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Candidates_StripCodeloadsRoot_FindEveryLayout_AndReadTheCommit()
    {
        using var archive = Open(SkillZip.Build(
        [
            ("skills/pdf/SKILL.md", SkillZip.SkillMd("pdf")),
            ("skills/pdf/scripts/fill.py", "x"),
            (".claude/skills/docs/SKILL.md", SkillZip.SkillMd("docs")),
            (".agents/skills/cat/sub/SKILL.md", SkillZip.SkillMd("sub")),
            ("node_modules/pkg/SKILL.md", SkillZip.SkillMd("hidden")),
            (".git/SKILL.md", SkillZip.SkillMd("hidden2")),
            ("a/b/c/d/e/f/g/SKILL.md", SkillZip.SkillMd("deep")),
            ("README.md", "# r"),
        ]));

        Assert.Equal(SkillZip.Commit, archive.Commit);
        var candidates = archive.Candidates();
        Assert.Equal([".agents/skills/cat/sub", ".claude/skills/docs", "skills/pdf"], candidates.Select(c => c.Folder));
        var pdf = candidates.Single(c => c.Name == "pdf");
        Assert.Equal(["scripts/fill.py", "SKILL.md"], pdf.Files.Select(f => f.Path));
        Assert.Null(pdf.Refusal);
        Assert.Equal("# Steps\n\nDo it.", pdf.Body);
        Assert.Equal("pdf", pdf.FolderName);
    }

    [Fact]
    public void Candidates_NarrowToTheSubPath_AndLeaveNestedSkillsOut()
    {
        using var archive = Open(SkillZip.Build(
        [
            ("SKILL.md", SkillZip.SkillMd("outer")),
            ("notes.md", "n"),
            ("inner/SKILL.md", SkillZip.SkillMd("inner")),
            ("inner/ref.md", "r"),
        ], root: "", comment: null));

        Assert.Null(archive.Commit);
        var all = archive.Candidates();
        var outer = all.Single(c => c.Folder.Length == 0);
        Assert.Equal("outer", outer.Name);
        Assert.Equal("", outer.FolderName);
        Assert.Equal(["notes.md", "SKILL.md"], outer.Files.Select(f => f.Path));
        Assert.Equal(["inner"], archive.Candidates("inner/").Select(c => c.Folder));
        Assert.Empty(archive.Candidates("nowhere"));
    }

    [Fact]
    public void Match_ByFrontmatterNameFirst_ThenFolder()
    {
        using var archive = Open(SkillZip.Build(
        [
            ("skills/alpha/SKILL.md", SkillZip.SkillMd("beta")),
            ("skills/beta/SKILL.md", SkillZip.SkillMd("gamma")),
        ]));
        var candidates = archive.Candidates();

        Assert.Equal("skills/alpha", SkillArchive.Match(candidates, "beta")!.Folder);
        Assert.Equal("skills/beta", SkillArchive.Match(candidates, "gamma")!.Folder);
        Assert.Equal("skills/alpha", SkillArchive.Match(candidates, "alpha")!.Folder);
        Assert.Null(SkillArchive.Match(candidates, "delta"));
    }

    [Fact]
    public void ABadFrontmatter_OrName_IsARefusal()
    {
        using var archive = Open(SkillZip.Build(
        [
            ("a/SKILL.md", "no fence"),
            ("b/SKILL.md", SkillZip.SkillMd("Bad_Name")),
        ]));
        var candidates = archive.Candidates();

        Assert.Equal(SkillFrontmatter.NoFenceProblem, candidates[0].Refusal);
        Assert.Equal("a", candidates[0].Name);
        Assert.Equal(SkillInstallText.BadNameRefusal("Bad_Name"), candidates[1].Refusal);
    }

    [Theory]
    [InlineData("s/CON.txt")]
    [InlineData("s/nul")]
    [InlineData("s/trailing.")]
    [InlineData("s/trailing ")]
    [InlineData("s/a:b")]
    public void UnsafePaths_AreRefusals(string path)
    {
        using var archive = Open(SkillZip.Build([("s/SKILL.md", SkillZip.SkillMd("s")), (path, "x")]));
        Assert.Equal(SkillInstallText.UnsafePathRefusal(path[2..]), archive.Candidates().Single().Refusal);
    }

    [Fact]
    public void IsSafePath_RefusesTraversalAndRoots()
    {
        Assert.True(SkillArchive.IsSafePath("scripts/run.py"));
        Assert.True(SkillArchive.IsSafePath("CONFIG.md"));
        Assert.False(SkillArchive.IsSafePath("../x"));
        Assert.False(SkillArchive.IsSafePath("a/./b"));
        Assert.False(SkillArchive.IsSafePath("/abs"));
        Assert.False(SkillArchive.IsSafePath("C:/x"));
        Assert.False(SkillArchive.IsSafePath("a//b"));
        Assert.False(SkillArchive.IsSafePath(""));
    }

    [Fact]
    public void ACaseCollision_IsARefusal_AndALink_IsSkipped()
    {
        using var archive = Open(SkillZip.Build([("s/SKILL.md", SkillZip.SkillMd("s")), ("s/Read.md", "a"), ("s/read.md", "b")]));
        Assert.Contains(archive.Candidates().Single().Refusal, new[] { SkillInstallText.CaseCollisionRefusal("Read.md"), SkillInstallText.CaseCollisionRefusal("read.md") });

        using var linked = Open(SkillZip.Build([("s/SKILL.md", SkillZip.SkillMd("s"))], links: ["s/evil"]));
        var candidate = linked.Candidates().Single();
        Assert.Null(candidate.Refusal);
        Assert.Equal(["evil"], candidate.Skipped);
        Assert.Equal(["SKILL.md"], candidate.Files.Select(f => f.Path));
    }

    [Fact]
    public void TheCaps_AreRefusals()
    {
        var many = Enumerable.Range(0, SkillArchive.MaxSkillFiles).Select(i => ($"s/f{i}.md", "x")).Append(("s/SKILL.md", SkillZip.SkillMd("s")));
        using (var archive = Open(SkillZip.Build(many)))
        {
            Assert.Equal(SkillInstallText.TooManyFilesRefusal(SkillArchive.MaxSkillFiles + 1, SkillArchive.MaxSkillFiles), archive.Candidates().Single().Refusal);
        }

        string big = new('x', (int)SkillArchive.MaxSkillFileBytes + 1);
        using (var archive = Open(SkillZip.Build([("s/SKILL.md", SkillZip.SkillMd("s")), ("s/big.txt", big)])))
        {
            Assert.Equal(SkillInstallText.FileTooBigRefusal("big.txt", big.Length, SkillArchive.MaxSkillFileBytes), archive.Candidates().Single().Refusal);
        }

        using (var archive = Open(SkillZip.Build([("s/SKILL.md", SkillZip.SkillMd("s", body: new string('y', (int)SkillArchive.MaxSkillMdBytes)))])))
        {
            Assert.StartsWith("its SKILL.md is ", archive.Candidates().Single().Refusal, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Extract_WritesTheFile()
    {
        using var archive = Open(SkillZip.Build([("s/SKILL.md", SkillZip.SkillMd("s")), ("s/a.txt", "hello")]));
        var file = archive.Candidates().Single().Files.Single(f => f.Path == "a.txt");
        string target = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N") + ".txt");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        try
        {
            Assert.True(archive.Extract(file, target));
            Assert.Equal("hello", File.ReadAllText(target));
            Assert.False(archive.Extract(file with { Length = 2 }, target));   // a header that under-declares
            Assert.False(File.Exists(target));
        }
        finally
        {
            File.Delete(target);
        }
    }

    [Fact]
    public void LooksLikeZip_ReadsTheMagic()
    {
        Assert.True(SkillArchive.LooksLikeZip("PK\u0003\u0004"u8));
        Assert.True(SkillArchive.LooksLikeZip("PK\u0005\u0006"u8));
        Assert.False(SkillArchive.LooksLikeZip("PK"u8));
        Assert.False(SkillArchive.LooksLikeZip("{\"a\":1}"u8));
    }
}
