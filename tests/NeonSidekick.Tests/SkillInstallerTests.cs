using NeonSidekick.Skills;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

public class SkillInstallerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly SkillRoots _roots;

    public SkillInstallerTests()
    {
        _roots = new SkillRoots(Path.Combine(_dir, "profile", "skills"), Path.Combine(_dir, "skills"), Path.Combine(_dir, ".agents", "skills"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static SkillProvenance Origin(string repo = "anthropics/skills", string path = "skills/pdf") =>
        new() { Repo = repo, Path = path, Url = "https://codeload.github.com/" + repo + "/zip/HEAD", InstalledAt = DateTimeOffset.UnixEpoch };

    private static (SkillArchive Archive, SkillCandidate Candidate) Pdf(string script = "print('v1')\n", string? extra = null)
    {
        var files = new List<(string, string)> { ("skills/pdf/SKILL.md", SkillZip.SkillMd("pdf")), ("skills/pdf/scripts/run.py", script) };
        if (extra is not null)
        {
            files.Add(("skills/pdf/" + extra, "x"));
        }

        var archive = SkillArchive.TryOpen(SkillZip.Build(files), out _)!;
        return (archive, archive.Candidates().Single());
    }

    private void Put(SkillScope scope, string name, SkillProvenance? provenance = null)
    {
        string folder = Path.Combine(_roots.Of(scope), name);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, SkillCatalog.FileName), SkillZip.SkillMd(name));
        if (provenance is not null)
        {
            File.WriteAllText(Path.Combine(folder, SkillProvenance.FileName), provenance.ToJson());
        }
    }

    [Fact]
    public void Install_WritesTheFolderAndTheSidecar_AndTheCatalogOffersIt_WithoutTheSidecar()
    {
        var (archive, candidate) = Pdf();
        using (archive)
        {
            Assert.Equal(new SkillInstallCheck(SkillInstallOption.New), SkillInstaller.Check(_roots, "pdf", Origin(), external: false));
            var result = SkillInstaller.Install(_roots, SkillScope.Global, archive, candidate, Origin(), replace: false);

            Assert.True(result.Ok, result.Error);
            Assert.False(result.Updated);
            Assert.Equal(Path.Combine(Path.GetFullPath(_roots.Global), "pdf"), result.Directory);
            Assert.Equal("print('v1')\n", File.ReadAllText(Path.Combine(result.Directory, "scripts", "run.py")));
            var sidecar = SkillProvenance.Read(result.Directory)!;
            Assert.True(sidecar.SameOrigin(Origin()));
            Assert.Equal("https://codeload.github.com/anthropics/skills/zip/HEAD", sidecar.Url);
            Assert.Empty(Directory.GetDirectories(_dir, ".skill-*"));

            var catalog = new SkillCatalog(() => _roots);
            catalog.Scan(external: false);
            var skill = Assert.Single(catalog.Skills);
            Assert.Equal(("pdf", SkillScope.Global, (string?)null), (skill.Name, skill.Scope, skill.Warning));
            Assert.Equal(["scripts/run.py"], SkillCatalog.Resources(skill, out _));
        }
    }

    [Fact]
    public void Check_RefusesAHandMadeSkill_AndTheExternalOne_AndOffersAnUpdateOfTheSameSource()
    {
        Put(SkillScope.Profile, "pdf");
        Assert.Equal(new SkillInstallCheck(SkillInstallOption.Taken, SkillScope.Profile), SkillInstaller.Check(_roots, "pdf", Origin(), false));

        Put(SkillScope.Global, "docx", Origin(path: "skills/docx"));
        Assert.Equal(new SkillInstallCheck(SkillInstallOption.Update, SkillScope.Global), SkillInstaller.Check(_roots, "docx", Origin("ANTHROPICS/skills", "skills/docx"), false));
        Assert.Equal(new SkillInstallCheck(SkillInstallOption.Taken, SkillScope.Global), SkillInstaller.Check(_roots, "docx", Origin("openai/skills", "skills/docx"), false));

        Put(SkillScope.External, "xlsx");
        Assert.Equal(new SkillInstallCheck(SkillInstallOption.ExternalReadOnly, SkillScope.External), SkillInstaller.Check(_roots, "xlsx", Origin(), external: true));
        Assert.Equal(SkillInstallOption.New, SkillInstaller.Check(_roots, "xlsx", Origin(), external: false).Option);
    }

    [Fact]
    public void Install_ReplacesTheFolderWhole_OnAnUpdate_AndRefusesWithoutReplace()
    {
        var (first, firstCandidate) = Pdf(extra: "old.md");
        using (first)
        {
            Assert.True(SkillInstaller.Install(_roots, SkillScope.Profile, first, firstCandidate, Origin(), replace: false).Ok);
        }

        var (second, secondCandidate) = Pdf("print('v2')\n");
        using (second)
        {
            var refused = SkillInstaller.Install(_roots, SkillScope.Profile, second, secondCandidate, Origin(), replace: false);
            Assert.False(refused.Ok);
            Assert.Equal(SkillInstallText.TakenError("pdf", SkillScope.Profile), refused.Error);

            var updated = SkillInstaller.Install(_roots, SkillScope.Profile, second, secondCandidate, Origin(), replace: true);
            Assert.True(updated.Ok, updated.Error);
            Assert.True(updated.Updated);
            Assert.Equal("print('v2')\n", File.ReadAllText(Path.Combine(updated.Directory, "scripts", "run.py")));
            Assert.False(File.Exists(Path.Combine(updated.Directory, "old.md")));
            Assert.Empty(Directory.GetDirectories(Path.Combine(_dir, "profile"), ".skill-*"));
        }
    }

    [Fact]
    public void Install_ARefusedCandidate_WritesNothing()
    {
        using var archive = SkillArchive.TryOpen(SkillZip.Build([("s/SKILL.md", SkillZip.SkillMd("s")), ("s/CON", "x")]), out _)!;
        var candidate = archive.Candidates().Single();

        var result = SkillInstaller.Install(_roots, SkillScope.Profile, archive, candidate, Origin(), replace: false);

        Assert.False(result.Ok);
        Assert.Equal(SkillInstallText.UnsafePathRefusal("CON"), result.Error);
        Assert.False(Directory.Exists(_roots.Profile));
    }

    [Fact]
    public void Install_AFileThatLiesAboutItsSize_LeavesTheOldSkillAndNoStage()
    {
        var (first, firstCandidate) = Pdf();
        using (first)
        {
            Assert.True(SkillInstaller.Install(_roots, SkillScope.Profile, first, firstCandidate, Origin(), replace: false).Ok);
        }

        var (second, secondCandidate) = Pdf("print('v2 is longer')\n");
        using (second)
        {
            var lying = secondCandidate with { Files = secondCandidate.Files.Select(f => f with { Length = 3 }).ToList() };
            var result = SkillInstaller.Install(_roots, SkillScope.Profile, second, lying, Origin(), replace: true);

            Assert.False(result.Ok);
            Assert.Equal("print('v1')\n", File.ReadAllText(Path.Combine(_roots.Profile, "pdf", "scripts", "run.py")));
            Assert.Empty(Directory.GetDirectories(Path.Combine(_dir, "profile"), ".skill-*"));
        }
    }

    [Fact]
    public void Provenance_ReadsNothingFromABadSidecar()
    {
        string folder = Path.Combine(_dir, "x");
        Directory.CreateDirectory(folder);
        Assert.Null(SkillProvenance.Read(folder));
        File.WriteAllText(Path.Combine(folder, SkillProvenance.FileName), "{not json");
        Assert.Null(SkillProvenance.Read(folder));
    }
}
