using NeonSidekick.Diagnostics;
using NeonSidekick.Skills;

namespace NeonSidekick.Tests;

public class SkillCatalogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly SkillRoots _roots;
    private readonly SkillCatalog _catalog;

    public SkillCatalogTests()
    {
        _roots = new SkillRoots(Path.Combine(_dir, "profile", "skills"), Path.Combine(_dir, "skills"), Path.Combine(_dir, ".agents", "skills"));
        _catalog = new SkillCatalog(() => _roots);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string Put(SkillScope scope, string folder, string text)
    {
        string directory = Path.Combine(_roots.Of(scope), folder);
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, SkillCatalog.FileName);
        File.WriteAllText(path, text);
        return directory;
    }

    private static string Skill(string name, string description = "Does things.") => "---\nname: " + name + "\ndescription: " + description + "\n---\n# " + name + "\n\nThe steps.\n";

    [Fact]
    public void MissingRoots_AreEmpty_AndNothingThrows()
    {
        _catalog.Scan(external: true);

        Assert.Empty(_catalog.Skills);
        Assert.Empty(_catalog.Shadowed);
        Assert.Empty(_catalog.Problems);
        Assert.True(_catalog.ExternalEnabled);
        Assert.Equal(0, _catalog.Version);
        Assert.Null(_catalog.Find("anything"));
        Assert.Null(_catalog.Find(""));
        Assert.Null(_catalog.Find(null));
    }

    [Fact]
    public void Scan_FindsEveryFolderWithASkillMd_OneLevelDown_ByName_AndIgnoresTheRest()
    {
        Put(SkillScope.Global, "zeta", Skill("zeta", "Last by name."));
        Put(SkillScope.Global, "alpha", Skill("alpha", "First by name."));
        Directory.CreateDirectory(Path.Combine(_roots.Global, "empty"));
        Directory.CreateDirectory(Path.Combine(_roots.Global, "nested", "deep"));
        File.WriteAllText(Path.Combine(_roots.Global, "nested", "deep", SkillCatalog.FileName), Skill("deep"));
        File.WriteAllText(Path.Combine(_roots.Global, "README.md"), "not a skill");
        File.WriteAllText(Path.Combine(_roots.Global, "alpha", "notes.md"), "beside it");

        _catalog.Scan(external: false);

        Assert.Equal(["alpha", "zeta"], _catalog.Skills.Select(s => s.Name));
        Assert.All(_catalog.Skills, s => Assert.Equal(SkillScope.Global, s.Scope));
        Assert.Equal("First by name.", _catalog.Skills[0].Description);
        Assert.Equal(Path.Combine(_roots.Global, "alpha"), _catalog.Skills[0].Directory);
        Assert.Equal(Path.Combine(_roots.Global, "alpha", "SKILL.md"), _catalog.Skills[0].FilePath);
        Assert.Equal("alpha", _catalog.Skills[0].FolderName);
        Assert.Null(_catalog.Skills[0].Warning);
        Assert.Empty(_catalog.Problems);
        Assert.Equal(1, _catalog.Version);
        Assert.Same(_catalog.Skills[1], _catalog.Find("zeta"));
        Assert.Null(_catalog.Find("Zeta"));   // the frontmatter spelling, ordinal
    }

    [Fact]
    public void Precedence_ProfileShadowsGlobalShadowsExternal_TheHiddenOnesListed_AndNotedOnce()
    {
        Put(SkillScope.Profile, "deploy", Skill("deploy", "The profile's."));
        Put(SkillScope.Global, "deploy", Skill("deploy", "The global one."));
        Put(SkillScope.External, "deploy", Skill("deploy", "Another client's."));
        Put(SkillScope.Global, "lint", Skill("lint", "Global lint."));
        Put(SkillScope.External, "lint", Skill("lint", "External lint."));
        var notes = new List<string>();
        Action<DiagnosticEvent> capture = e => { if (e.Category == SkillCatalog.Category && e.Level == DiagnosticLevel.Info && e.Message.Contains("shadowed", StringComparison.Ordinal)) notes.Add(e.Message); };
        DiagnosticLog.Emitted += capture;
        try
        {
            _catalog.Scan(external: true);
            _catalog.Scan(external: true);
        }
        finally
        {
            DiagnosticLog.Emitted -= capture;
        }

        Assert.Equal(["deploy", "lint"], _catalog.Skills.Select(s => s.Name));
        Assert.Equal([SkillScope.Profile, SkillScope.Global], _catalog.Skills.Select(s => s.Scope));
        Assert.Equal(["deploy", "deploy", "lint"], _catalog.Shadowed.Select(s => s.Name));
        Assert.Equal([SkillScope.Global, SkillScope.External, SkillScope.External], _catalog.Shadowed.Select(s => s.Scope));
        Assert.Equal([SkillScope.Profile, SkillScope.Profile, SkillScope.Global], _catalog.Shadowed.Select(s => s.ShadowedBy));
        Assert.Equal(3, notes.Count);   // once per hidden file, not per scan
        Assert.Equal(SkillCatalog.ShadowedNote(_catalog.Shadowed[0], _catalog.Skills[0]), notes[0]);
        Assert.Equal($"Skill 'deploy' in the global skills is shadowed by the one in the profile skills ({_catalog.Skills[0].Directory}).", notes[0]);
    }

    [Fact]
    public void External_IsScannedOnlyWhenAsked()
    {
        Put(SkillScope.External, "shared", Skill("shared"));

        _catalog.Scan(external: false);
        Assert.Empty(_catalog.Skills);
        Assert.False(_catalog.ExternalEnabled);

        _catalog.Scan(external: true);
        Assert.Equal(["shared"], _catalog.Skills.Select(s => s.Name));
        Assert.Equal(SkillScope.External, _catalog.Skills[0].Scope);
        Assert.Equal(1, _catalog.Version);   // the first scan found nothing (still version 0); the second changed the set
    }

    [Fact]
    public void LenientLoading_NameMismatchAndOverLength_LoadWithAWarning()
    {
        Put(SkillScope.Global, "folder", Skill("other-name"));
        string longName = new('a', 70);
        Put(SkillScope.Global, longName, Skill(longName, new string('d', 1100)));

        _catalog.Scan(external: false);

        Assert.Equal([longName, "other-name"], _catalog.Skills.Select(s => s.Name));
        Assert.Equal("name 'other-name' does not match the folder 'folder'", _catalog.Skills[1].Warning);
        Assert.Equal("the name is 70 characters; the limit is 64; the description is 1100 characters; the first 1024 are shown", _catalog.Skills[0].Warning);
        Assert.Equal(1024, _catalog.Skills[0].Description.Length);
        Assert.EndsWith("…", _catalog.Skills[0].Description);
    }

    [Fact]
    public void BadFiles_AreProblems_WarnedOncePerVersion_AndNeverOffered()
    {
        Put(SkillScope.Global, "no-description", "---\nname: no-description\n---\nbody");
        Put(SkillScope.Global, "no-fence", "# just markdown\n");
        var warnings = new List<string>();
        Action<DiagnosticEvent> capture = e => { if (e.Category == SkillCatalog.Category && e.Level == DiagnosticLevel.Warning) warnings.Add(e.Message); };
        DiagnosticLog.Emitted += capture;
        try
        {
            _catalog.Scan(external: false);
            _catalog.Scan(external: false);
        }
        finally
        {
            DiagnosticLog.Emitted -= capture;
        }

        Assert.Empty(_catalog.Skills);
        Assert.Equal(["no-description", "no-fence"], _catalog.Problems.Select(p => p.FolderName));
        Assert.Equal([SkillFrontmatter.NoDescriptionProblem, SkillFrontmatter.NoFenceProblem], _catalog.Problems.Select(p => p.Reason));
        Assert.Equal(2, warnings.Count);
        Assert.Equal(SkillCatalog.SkippedWarning(Path.Combine(_roots.Global, "no-description"), SkillFrontmatter.NoDescriptionProblem), warnings[0]);
        Assert.Equal($"Skill folder {Path.Combine(_roots.Global, "no-description")} skipped: the frontmatter has no description.", warnings[0]);
    }

    [Fact]
    public void ARewrite_IsPickedUpByTheNextScan_AndARemovedFolderDropsOut()
    {
        string directory = Put(SkillScope.Profile, "haiku", Skill("haiku", "Old words."));
        _catalog.Scan(external: false);
        Assert.Equal("Old words.", _catalog.Skills[0].Description);
        int version = _catalog.Version;

        File.SetLastWriteTimeUtc(Path.Combine(directory, SkillCatalog.FileName), DateTime.UtcNow.AddMinutes(-5));
        File.WriteAllText(Path.Combine(directory, SkillCatalog.FileName), Skill("haiku", "New words, same name."));
        _catalog.Scan(external: false);
        Assert.Equal("New words, same name.", _catalog.Skills[0].Description);
        Assert.Equal(version, _catalog.Version);   // the set of names did not change

        Directory.Delete(directory, recursive: true);
        _catalog.Scan(external: false);
        Assert.Empty(_catalog.Skills);
        Assert.Equal(version + 1, _catalog.Version);
    }

    [Fact]
    public void ReadBody_IsTheFileNow_FrontmatterStripped_CutAtTheCap_MissingAndUnparseableReported()
    {
        string directory = Put(SkillScope.Global, "haiku", Skill("haiku"));
        _catalog.Scan(external: false);
        var skill = _catalog.Skills[0];

        var body = SkillCatalog.ReadBody(skill);
        Assert.Equal(SkillCatalog.ReadOutcome.Ok, body.Outcome);
        Assert.Equal("# haiku\n\nThe steps.", body.Text);
        Assert.False(body.Truncated);

        File.WriteAllText(skill.FilePath, "---\nname: haiku\ndescription: d\n---\n" + new string('x', SkillCatalog.MaxBodyChars + 10));
        var cut = SkillCatalog.ReadBody(skill);
        Assert.True(cut.Truncated);
        Assert.Equal(SkillCatalog.MaxBodyChars, cut.Text.Length);

        File.WriteAllText(skill.FilePath, "no fence");
        Assert.Equal(SkillCatalog.ReadOutcome.Unparseable, SkillCatalog.ReadBody(skill).Outcome);
        Assert.Equal(SkillFrontmatter.NoFenceProblem, SkillCatalog.ReadBody(skill).Detail);

        Directory.Delete(directory, recursive: true);
        Assert.Equal(SkillCatalog.ReadOutcome.Missing, SkillCatalog.ReadBody(skill).Outcome);
    }

    [Fact]
    public void Resources_ListTheBundledFiles_RelativeWithForwardSlashes_Sorted_SkippingGitAndNodeModules_Capped()
    {
        string directory = Put(SkillScope.Global, "pdf", Skill("pdf"));
        Directory.CreateDirectory(Path.Combine(directory, "scripts"));
        Directory.CreateDirectory(Path.Combine(directory, "references"));
        Directory.CreateDirectory(Path.Combine(directory, ".git"));
        Directory.CreateDirectory(Path.Combine(directory, "node_modules", "x"));
        File.WriteAllText(Path.Combine(directory, "scripts", "extract.py"), "print()");
        File.WriteAllText(Path.Combine(directory, "references", "REFERENCE.md"), "ref");
        File.WriteAllText(Path.Combine(directory, ".git", "HEAD"), "ref: x");
        File.WriteAllText(Path.Combine(directory, "node_modules", "x", "index.js"), "");
        File.WriteAllText(Path.Combine(directory, "LICENSE.txt"), "MIT");
        _catalog.Scan(external: false);

        var files = SkillCatalog.Resources(_catalog.Skills[0], out bool more);

        Assert.Equal(["LICENSE.txt", "references/REFERENCE.md", "scripts/extract.py"], files);
        Assert.False(more);

        for (int i = 0; i < SkillCatalog.MaxResources; i++)
        {
            File.WriteAllText(Path.Combine(directory, "scripts", $"s{i:000}.py"), "");
        }

        files = SkillCatalog.Resources(_catalog.Skills[0], out more);
        Assert.True(more);
        Assert.Equal(SkillCatalog.MaxResources, files.Count);
    }

    [WindowsFact]
    public void ReadResource_StaysInsideTheSkillFolder_TextOnly()
    {
        string directory = Put(SkillScope.Global, "pdf", Skill("pdf"));
        Directory.CreateDirectory(Path.Combine(directory, "references"));
        File.WriteAllText(Path.Combine(directory, "references", "guide.md"), "line one\r\nline two");
        File.WriteAllBytes(Path.Combine(directory, "references", "blob.bin"), [1, 0, 2, 0]);
        File.WriteAllText(Path.Combine(_roots.Global, "secret.txt"), "outside");
        _catalog.Scan(external: false);
        var skill = _catalog.Skills[0];

        var ok = SkillCatalog.ReadResource(skill, "references/guide.md");
        Assert.Equal(SkillCatalog.ReadOutcome.Ok, ok.Outcome);
        Assert.Equal("line one\nline two", ok.Text);
        Assert.Equal(SkillCatalog.ReadOutcome.Ok, SkillCatalog.ReadResource(skill, @"references\guide.md").Outcome);
        Assert.Equal(SkillCatalog.ReadOutcome.Outside, SkillCatalog.ReadResource(skill, "../secret.txt").Outcome);
        Assert.Equal(SkillCatalog.ReadOutcome.Outside, SkillCatalog.ReadResource(skill, Path.Combine(_roots.Global, "secret.txt")).Outcome);
        Assert.Equal(SkillCatalog.ReadOutcome.Outside, SkillCatalog.ReadResource(skill, "").Outcome);
        Assert.Equal(SkillCatalog.ReadOutcome.IsDirectory, SkillCatalog.ReadResource(skill, "references").Outcome);
        Assert.Equal(SkillCatalog.ReadOutcome.Missing, SkillCatalog.ReadResource(skill, "references/gone.md").Outcome);
        Assert.Equal(SkillCatalog.ReadOutcome.NotText, SkillCatalog.ReadResource(skill, "references/blob.bin").Outcome);
    }

    /// <summary>The Unix twin of <see cref="ReadResource_StaysInsideTheSkillFolder_TextOnly"/> (2026-10-06, the macOS build): its paths with <c>/</c>.</summary>
    [UnixFact]
    public void ReadResource_StaysInsideTheSkillFolder_TextOnly_Unix()
    {
        string directory = Put(SkillScope.Global, "pdf", Skill("pdf"));
        Directory.CreateDirectory(Path.Combine(directory, "references"));
        File.WriteAllText(Path.Combine(directory, "references", "guide.md"), "line one\r\nline two");
        File.WriteAllBytes(Path.Combine(directory, "references", "blob.bin"), [1, 0, 2, 0]);
        File.WriteAllText(Path.Combine(_roots.Global, "secret.txt"), "outside");
        _catalog.Scan(external: false);
        var skill = _catalog.Skills[0];

        var ok = SkillCatalog.ReadResource(skill, "references/guide.md");
        Assert.Equal(SkillCatalog.ReadOutcome.Ok, ok.Outcome);
        Assert.Equal("line one\nline two", ok.Text);
        Assert.Equal(SkillCatalog.ReadOutcome.Ok, SkillCatalog.ReadResource(skill, @"references/guide.md").Outcome);
        Assert.Equal(SkillCatalog.ReadOutcome.Outside, SkillCatalog.ReadResource(skill, "../secret.txt").Outcome);
        Assert.Equal(SkillCatalog.ReadOutcome.Outside, SkillCatalog.ReadResource(skill, Path.Combine(_roots.Global, "secret.txt")).Outcome);
        Assert.Equal(SkillCatalog.ReadOutcome.Outside, SkillCatalog.ReadResource(skill, "").Outcome);
        Assert.Equal(SkillCatalog.ReadOutcome.IsDirectory, SkillCatalog.ReadResource(skill, "references").Outcome);
        Assert.Equal(SkillCatalog.ReadOutcome.Missing, SkillCatalog.ReadResource(skill, "references/gone.md").Outcome);
        Assert.Equal(SkillCatalog.ReadOutcome.NotText, SkillCatalog.ReadResource(skill, "references/blob.bin").Outcome);
    }

    /// <summary>A bundled file's own cap (2026-09-27): 64,000 characters, twice read_file's, since load_skill cannot page.</summary>
    [Fact]
    public void ReadResource_ReadsUpToItsOwnCap_ThenCutsWithTheNote()
    {
        Assert.Equal(64_000, SkillCatalog.MaxResourceChars);
        string directory = Put(SkillScope.Global, "emojese", Skill("emojese"));
        File.WriteAllText(Path.Combine(directory, "mapping.json"), new string('x', 50_000));
        File.WriteAllText(Path.Combine(directory, "huge.json"), new string('y', 70_000));
        _catalog.Scan(external: false);
        var skill = _catalog.Skills[0];

        var whole = SkillCatalog.ReadResource(skill, "mapping.json");
        Assert.Equal(50_000, whole.Text.Length);
        Assert.False(whole.Truncated);

        var cut = SkillCatalog.ReadResource(skill, "huge.json");
        Assert.Equal(SkillCatalog.MaxResourceChars, cut.Text.Length);
        Assert.True(cut.Truncated);
        Assert.EndsWith("\n\n(cut at 64,000 characters)\n</skill_file>", SkillText.File("emojese", "huge.json", cut.Text, cut.Truncated), StringComparison.Ordinal);
    }
}
