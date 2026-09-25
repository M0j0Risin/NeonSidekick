using NeonSidekick.App;
using NeonSidekick.Skills;

namespace NeonSidekick.Tests;

public class SkillEditorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly SkillRoots _roots;

    public SkillEditorTests()
    {
        _roots = new SkillRoots(Path.Combine(_dir, "profile", "skills"), Path.Combine(_dir, "skills"), Path.Combine(_dir, ".agents", "skills"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string FileOf(SkillScope scope, string name) => Path.Combine(_roots.Of(scope), name, SkillCatalog.FileName);

    // The editor over the test roots; the external folder unread unless a test says so.
    private SkillEditResult Create(SkillScope scope, string name, string description, string instructions, bool external = false) =>
        SkillEditor.Create(_roots, scope, name, description, instructions, external);

    private SkillEditResult Update(SkillScope scope, string name, string? description, string? instructions, bool external = false) =>
        SkillEditor.Update(_roots, scope, name, description, instructions, external);

    /// <summary>A skill written by hand under <paramref name="scope"/>, as another client or the user would.</summary>
    private void Put(SkillScope scope, string name, string description = "d", string body = "i")
    {
        Directory.CreateDirectory(Path.Combine(_roots.Of(scope), name));
        File.WriteAllText(FileOf(scope, name), $"---\nname: {name}\ndescription: {description}\n---\n\n{body}\n");
    }

    [Fact]
    public void Create_WritesTheFolderAndTheFile_ThenTheCatalogReadsIt()
    {
        var result = Create(SkillScope.Profile, " haiku ", "Writes haiku.\nUse when asked for one.", "# Haiku\r\n\r\nFive, seven, five.");

        Assert.Equal(SkillEditOutcome.Created, result.Outcome);
        Assert.Equal("haiku", result.Name);
        Assert.Equal(SkillScope.Profile, result.Scope);
        string text = File.ReadAllText(FileOf(SkillScope.Profile, "haiku"));
        Assert.Equal("---\nname: haiku\ndescription: Writes haiku. Use when asked for one.\n---\n\n# Haiku\n\nFive, seven, five.\n", text);
        Assert.Equal(text.Length, result.Bytes);
        Assert.Equal((byte)'-', File.ReadAllBytes(FileOf(SkillScope.Profile, "haiku"))[0]);   // no BOM
        var catalog = new SkillCatalog(() => _roots);
        catalog.Scan(external: false);
        Assert.Equal(["haiku"], catalog.Skills.Select(s => s.Name));
        Assert.Equal("Writes haiku. Use when asked for one.", catalog.Skills[0].Description);
        Assert.Equal("# Haiku\n\nFive, seven, five.", SkillCatalog.ReadBody(catalog.Skills[0]).Text);
    }

    [Fact]
    public void Create_RefusesABadName_AnExistingFolder_AndEmptyOrOverlongParts()
    {
        Assert.Equal(SkillEditOutcome.BadName, Create(SkillScope.Global, "Bad Name", "d", "i").Outcome);
        Assert.Equal(SkillEditOutcome.EmptyDescription, Create(SkillScope.Global, "x", "  \n ", "i").Outcome);
        var tooLong = Create(SkillScope.Global, "x", new string('d', 1025), "i");
        Assert.Equal(SkillEditOutcome.DescriptionTooLong, tooLong.Outcome);
        Assert.Equal(1025, tooLong.Length);
        Assert.Equal(SkillEditOutcome.EmptyInstructions, Create(SkillScope.Global, "x", "d", " ").Outcome);
        var bigBody = Create(SkillScope.Global, "x", "d", new string('i', SkillEditor.MaxInstructionChars + 1));
        Assert.Equal(SkillEditOutcome.InstructionsTooLong, bigBody.Outcome);
        Assert.Equal(SkillEditor.MaxInstructionChars + 1, bigBody.Length);
        Assert.False(Directory.Exists(Path.Combine(_roots.Global, "x")));

        Assert.Equal(SkillEditOutcome.Created, Create(SkillScope.Global, "x", "d", "i").Outcome);
        Assert.Equal(SkillEditOutcome.Exists, Create(SkillScope.Global, "x", "d2", "i2").Outcome);
        Assert.Equal("description: d", File.ReadAllText(FileOf(SkillScope.Global, "x")).Split('\n')[2]);   // the first write stands
    }

    [Fact]
    public void Update_ReplacesTheDescriptionOrTheBodyOrBoth_AndKeepsTheOtherLines()
    {
        Directory.CreateDirectory(Path.Combine(_roots.Global, "pdf"));
        File.WriteAllText(FileOf(SkillScope.Global, "pdf"), "---\nname: pdf\ndescription: Old.\nlicense: MIT\nmetadata:\n  author: me\n---\n\nOld body.\n");

        var described = Update(SkillScope.Global, "pdf", "New description.", null);
        Assert.Equal(SkillEditOutcome.Updated, described.Outcome);
        Assert.Equal("---\nname: pdf\ndescription: New description.\nlicense: MIT\nmetadata:\n  author: me\n---\n\nOld body.\n", File.ReadAllText(FileOf(SkillScope.Global, "pdf")));

        var rewritten = Update(SkillScope.Global, "pdf", "  ", "New body.");
        Assert.Equal(SkillEditOutcome.Updated, rewritten.Outcome);
        Assert.Equal("---\nname: pdf\ndescription: New description.\nlicense: MIT\nmetadata:\n  author: me\n---\n\nNew body.\n", File.ReadAllText(FileOf(SkillScope.Global, "pdf")));

        Assert.Equal(SkillEditOutcome.Updated, Update(SkillScope.Global, "pdf", "Both.", "Both body.").Outcome);
        Assert.Equal("---\nname: pdf\ndescription: Both.\nlicense: MIT\nmetadata:\n  author: me\n---\n\nBoth body.\n", File.ReadAllText(FileOf(SkillScope.Global, "pdf")));
    }

    [Fact]
    public void Update_RefusesAMissingSkill_NothingToChange_ABadName_AndTheCaps()
    {
        Assert.Equal(SkillEditOutcome.Missing, Update(SkillScope.Profile, "gone", "d", "i").Outcome);
        Assert.Equal(SkillEditOutcome.BadName, Update(SkillScope.Profile, "..", "d", "i").Outcome);
        Create(SkillScope.Profile, "x", "d", "i");
        Assert.Equal(SkillEditOutcome.NothingToChange, Update(SkillScope.Profile, "x", null, null).Outcome);
        Assert.Equal(SkillEditOutcome.NothingToChange, Update(SkillScope.Profile, "x", " ", "").Outcome);
        Assert.Equal(SkillEditOutcome.DescriptionTooLong, Update(SkillScope.Profile, "x", new string('d', 1025), null).Outcome);
        Assert.Equal(SkillEditOutcome.InstructionsTooLong, Update(SkillScope.Profile, "x", null, new string('i', SkillEditor.MaxInstructionChars + 1)).Outcome);
        Assert.Equal("---\nname: x\ndescription: d\n---\n\ni\n", File.ReadAllText(FileOf(SkillScope.Profile, "x")));
    }

    [Fact]
    public void Update_OfAnUnparseableFile_NeedsBothParts_ThenRewritesIt()
    {
        Directory.CreateDirectory(Path.Combine(_roots.Profile, "broken"));
        File.WriteAllText(FileOf(SkillScope.Profile, "broken"), "no frontmatter at all");

        var half = Update(SkillScope.Profile, "broken", "d", null);
        Assert.Equal(SkillEditOutcome.Unparseable, half.Outcome);
        Assert.Equal(SkillFrontmatter.NoFenceProblem, half.Detail);

        Assert.Equal(SkillEditOutcome.Updated, Update(SkillScope.Profile, "broken", "d", "i").Outcome);
        Assert.Equal("---\nname: broken\ndescription: d\n---\n\ni\n", File.ReadAllText(FileOf(SkillScope.Profile, "broken")));
    }

    // ── One name, one skill across the roots (2026-09-16: the weather-info duplicate) ──

    [Fact]
    public void Find_ChecksTheRootsInPrecedenceOrder_TheExternalOnlyWhileRead()
    {
        Assert.Null(SkillEditor.Find(_roots, "weather-info", external: true));

        Put(SkillScope.External, "weather-info");
        Assert.Equal(SkillScope.External, SkillEditor.Find(_roots, "weather-info", external: true));
        Assert.Null(SkillEditor.Find(_roots, "weather-info", external: false));   // unread: invisible

        Put(SkillScope.Global, "weather-info");
        Assert.Equal(SkillScope.Global, SkillEditor.Find(_roots, "weather-info", external: true));

        Put(SkillScope.Profile, "weather-info");
        Assert.Equal(SkillScope.Profile, SkillEditor.Find(_roots, "weather-info", external: true));

        // A folder without a SKILL.md is not a skill.
        Directory.CreateDirectory(Path.Combine(_roots.Global, "bare"));
        Assert.Null(SkillEditor.Find(_roots, "bare", external: true));
    }

    [Fact]
    public void Create_RefusesANameThatIsASkillInAnotherRoot_AndWritesNothing()
    {
        Put(SkillScope.Global, "weather-info", "Reports the weather.", "Old body.");

        // The bug: asked to update the global skill, a model created a profile copy that shadowed it.
        var copy = Create(SkillScope.Profile, "weather-info", "Reports the weather and wind.", "New body.");
        Assert.Equal(SkillEditOutcome.ExistsElsewhere, copy.Outcome);
        Assert.Equal(SkillScope.Global, copy.Scope);   // where it lives, for the sentence
        Assert.False(Directory.Exists(Path.Combine(_roots.Profile, "weather-info")));
        Assert.Contains("description: Reports the weather.\n", File.ReadAllText(FileOf(SkillScope.Global, "weather-info")));

        // The other way round too.
        Put(SkillScope.Profile, "haiku");
        var global = Create(SkillScope.Global, "haiku", "d", "i");
        Assert.Equal(SkillEditOutcome.ExistsElsewhere, global.Outcome);
        Assert.Equal(SkillScope.Profile, global.Scope);
        Assert.False(Directory.Exists(Path.Combine(_roots.Global, "haiku")));

        // A bare folder in the other root is not a skill and blocks nothing.
        Directory.CreateDirectory(Path.Combine(_roots.Global, "notes"));
        Assert.Equal(SkillEditOutcome.Created, Create(SkillScope.Profile, "notes", "d", "i").Outcome);

        // The same root still answers Exists, as before.
        Assert.Equal(SkillEditOutcome.Exists, Create(SkillScope.Global, "weather-info", "d", "i").Outcome);
    }

    [Fact]
    public void Create_RefusesANameInTheExternalFolder_OnlyWhileItIsRead()
    {
        Put(SkillScope.External, "weather-info");

        var refused = Create(SkillScope.Profile, "weather-info", "d", "i", external: true);
        Assert.Equal(SkillEditOutcome.ExternalReadOnly, refused.Outcome);
        Assert.Equal(SkillScope.External, refused.Scope);
        Assert.False(Directory.Exists(Path.Combine(_roots.Profile, "weather-info")));

        // The folder unread: a skill there is not in the catalog, so a new one of the name is no copy.
        Assert.Equal(SkillEditOutcome.Created, Create(SkillScope.Profile, "weather-info", "d", "i", external: false).Outcome);
    }

    [Fact]
    public void Update_ChangesTheSkillWhereItLives_WhateverScopeIsNamed()
    {
        Put(SkillScope.Global, "weather-info", "Reports the weather.", "Old body.");

        var moved = Update(SkillScope.Profile, "weather-info", "Reports the weather and wind.", null);
        Assert.Equal(SkillEditOutcome.Updated, moved.Outcome);
        Assert.Equal(SkillScope.Global, moved.Scope);   // the real scope, for the sentence
        Assert.Equal("---\nname: weather-info\ndescription: Reports the weather and wind.\n---\n\nOld body.\n", File.ReadAllText(FileOf(SkillScope.Global, "weather-info")));
        Assert.False(Directory.Exists(Path.Combine(_roots.Profile, "weather-info")));

        // In both roots: the named one is changed, nothing redirected.
        Put(SkillScope.Profile, "weather-info", "Profile copy.", "Profile body.");
        Assert.Equal(SkillScope.Profile, Update(SkillScope.Profile, "weather-info", "Changed.", null).Scope);
        Assert.Contains("description: Changed.\n", File.ReadAllText(FileOf(SkillScope.Profile, "weather-info")));
        Assert.Contains("description: Reports the weather and wind.\n", File.ReadAllText(FileOf(SkillScope.Global, "weather-info")));

        // Only in the external folder: read-only, not "missing" (which would send the model to create).
        Put(SkillScope.External, "shared");
        Assert.Equal(SkillEditOutcome.ExternalReadOnly, Update(SkillScope.Profile, "shared", "d", null, external: true).Outcome);
        Assert.Equal(SkillEditOutcome.Missing, Update(SkillScope.Profile, "shared", "d", null, external: false).Outcome);
        Assert.Equal("---\nname: shared\ndescription: d\n---\n\ni\n", File.ReadAllText(FileOf(SkillScope.External, "shared")));   // untouched either way
    }

    [Fact]
    public void Scopes_TheWritableTwo_ParseByWord_TheExternalNever()
    {
        Assert.Equal(["profile", "global"], SkillScopes.Writable);
        Assert.True(SkillScopes.TryParseWritable(" Profile ", out var scope) && scope == SkillScope.Profile);
        Assert.True(SkillScopes.TryParseWritable("GLOBAL", out scope) && scope == SkillScope.Global);
        Assert.False(SkillScopes.TryParseWritable("external", out _));
        Assert.False(SkillScopes.TryParseWritable("", out _));
        Assert.False(SkillScopes.TryParseWritable(null, out _));
        Assert.Equal("profile", SkillScopes.Name(SkillScope.Profile));
        Assert.Equal("global", SkillScopes.Name(SkillScope.Global));
        Assert.Equal("external", SkillScopes.Name(SkillScope.External));
        Assert.Equal(Path.Combine(_dir, ".agents", "skills"), _roots.Of(SkillScope.External));
    }

    /// <summary>A scanned skill under <paramref name="scope"/>, as the pane holds it.</summary>
    private Skill Scanned(SkillScope scope, string name, bool external = false)
    {
        var catalog = new SkillCatalog(() => _roots);
        catalog.Scan(external);
        return catalog.Skills.Concat(catalog.Shadowed).Single(s => s.Name == name && s.Scope == scope);
    }

    /// <summary>The pane's move (2026-09-18): the folder renamed under the other root with its bundled files, the catalog reading it there on the next scan, its version unchanged (the name set is).</summary>
    [Fact]
    public void Move_RenamesTheFolderUnderTheOtherRoot_WithItsFiles_AndTheCatalogFollows()
    {
        Put(SkillScope.Profile, "haiku", "Writes haiku.");
        File.WriteAllText(Path.Combine(_roots.Profile, "haiku", "notes.txt"), "n");
        var catalog = new SkillCatalog(() => _roots);
        catalog.Scan(external: false);
        int version = catalog.Version;
        var skill = catalog.Skills.Single();

        var result = SkillEditor.Move(_roots, skill, SkillScope.Global);

        Assert.Equal(SkillEditOutcome.Moved, result.Outcome);
        Assert.Equal(("haiku", SkillScope.Global), (result.Name, result.Scope));
        Assert.False(Directory.Exists(Path.Combine(_roots.Profile, "haiku")));
        Assert.Equal("n", File.ReadAllText(Path.Combine(_roots.Global, "haiku", "notes.txt")));
        catalog.Scan(external: false);
        Assert.Equal([("haiku", SkillScope.Global)], catalog.Skills.Select(s => (s.Name, s.Scope)));
        Assert.Equal(version, catalog.Version);

        // And back, into a root that is not there yet: made on the way.
        Directory.Delete(_roots.Profile, recursive: true);
        Assert.Equal(SkillEditOutcome.Moved, SkillEditor.Move(_roots, Scanned(SkillScope.Global, "haiku"), SkillScope.Profile).Outcome);
        Assert.True(File.Exists(FileOf(SkillScope.Profile, "haiku")));
    }

    /// <summary>A skill whose name differs from its folder moves by the folder: the collision check and the rename are the folder's.</summary>
    [Fact]
    public void Move_GoesByTheFolderName_NotTheSkillsName()
    {
        Directory.CreateDirectory(Path.Combine(_roots.Profile, "pdf"));
        File.WriteAllText(Path.Combine(_roots.Profile, "pdf", SkillCatalog.FileName), "---\nname: pdf-processing\ndescription: d\n---\n\ni\n");
        Put(SkillScope.Global, "pdf-processing");
        var skill = Scanned(SkillScope.Profile, "pdf-processing");
        Assert.Equal("pdf", skill.FolderName);

        Assert.Equal(SkillEditOutcome.Moved, SkillEditor.Move(_roots, skill, SkillScope.Global).Outcome);
        Assert.True(File.Exists(Path.Combine(_roots.Global, "pdf", SkillCatalog.FileName)));
        Assert.True(File.Exists(FileOf(SkillScope.Global, "pdf-processing")));   // the other one untouched
    }

    [Fact]
    public void Move_RefusesExternalEitherWay_TheSameScope_AnExistingDestination_AndAFolderNotUnderItsRoot()
    {
        Put(SkillScope.Profile, "haiku");
        Put(SkillScope.Global, "haiku");
        Put(SkillScope.External, "pdf");
        var profile = Scanned(SkillScope.Profile, "haiku", external: true);
        var external = Scanned(SkillScope.External, "pdf", external: true);

        var toExternal = SkillEditor.Move(_roots, profile, SkillScope.External);
        Assert.Equal((SkillEditOutcome.ExternalReadOnly, SkillScope.External), (toExternal.Outcome, toExternal.Scope));
        Assert.Equal(SkillEditOutcome.ExternalReadOnly, SkillEditor.Move(_roots, external, SkillScope.Profile).Outcome);
        Assert.Equal(SkillEditOutcome.NothingToChange, SkillEditor.Move(_roots, profile, SkillScope.Profile).Outcome);

        // The global root holds haiku already: refused, the result's scope the destination, both folders untouched.
        var exists = SkillEditor.Move(_roots, profile, SkillScope.Global);
        Assert.Equal((SkillEditOutcome.Exists, SkillScope.Global), (exists.Outcome, exists.Scope));
        Assert.True(File.Exists(FileOf(SkillScope.Profile, "haiku")));
        Assert.True(File.Exists(FileOf(SkillScope.Global, "haiku")));

        // A hand-built record pointing off its root, or at a folder that went: Missing, nothing moved.
        var stray = new Skill("stray", "d", SkillScope.Profile, Path.Combine(_dir, "elsewhere", "stray"));
        Directory.CreateDirectory(stray.Directory);
        File.WriteAllText(stray.FilePath, "x");
        Assert.Equal(SkillEditOutcome.Missing, SkillEditor.Move(_roots, stray, SkillScope.Global).Outcome);
        Assert.True(File.Exists(stray.FilePath));
        Assert.Equal(SkillEditOutcome.Missing, SkillEditor.Move(_roots, new Skill("gone", "d", SkillScope.Profile, Path.Combine(_roots.Profile, "gone")), SkillScope.Global).Outcome);
    }

    /// <summary>The pane's rename (2026-09-21): the folder under its own root and the frontmatter's name line, the other lines and the files carried; the catalog follows.</summary>
    [Fact]
    public void Rename_MovesTheFolder_RewritesTheNameLine_KeepsTheRest_AndTheCatalogFollows()
    {
        Directory.CreateDirectory(Path.Combine(_roots.Profile, "haiku", "scripts"));
        File.WriteAllText(Path.Combine(_roots.Profile, "haiku", SkillCatalog.FileName), "---\nname: haiku\ndescription: Writes haiku.\nlicense: MIT\n---\n\nbody\n");
        File.WriteAllText(Path.Combine(_roots.Profile, "haiku", "scripts", "run.py"), "p");
        var catalog = new SkillCatalog(() => _roots);
        catalog.Scan(external: false);
        var skill = catalog.Skills.Single();

        var result = SkillEditor.Rename(_roots, skill, " my-haiku ");

        Assert.Equal(SkillEditOutcome.Renamed, result.Outcome);
        Assert.Equal(("my-haiku", SkillScope.Profile), (result.Name, result.Scope));
        Assert.False(Directory.Exists(Path.Combine(_roots.Profile, "haiku")));
        Assert.Equal("---\nname: my-haiku\ndescription: Writes haiku.\nlicense: MIT\n---\n\nbody\n", File.ReadAllText(FileOf(SkillScope.Profile, "my-haiku")));
        Assert.Equal("p", File.ReadAllText(Path.Combine(_roots.Profile, "my-haiku", "scripts", "run.py")));
        catalog.Scan(external: false);
        Assert.Equal([("my-haiku", SkillScope.Profile)], catalog.Skills.Select(s => (s.Name, s.Scope)));
        Assert.Empty(catalog.Skills.Single().Warning ?? "");

        // A skill whose name differs from its folder: renamed to the name, the mismatch gone with it.
        Directory.CreateDirectory(Path.Combine(_roots.Global, "pdf"));
        File.WriteAllText(Path.Combine(_roots.Global, "pdf", SkillCatalog.FileName), "---\nname: pdf-processing\ndescription: d\n---\n\ni\n");
        Assert.Equal(SkillEditOutcome.Renamed, SkillEditor.Rename(_roots, Scanned(SkillScope.Global, "pdf-processing"), "pdf-processing").Outcome);
        Assert.True(File.Exists(FileOf(SkillScope.Global, "pdf-processing")));
        Assert.False(Directory.Exists(Path.Combine(_roots.Global, "pdf")));
    }

    [Fact]
    public void Rename_RefusesExternal_ABadName_TheSameName_AnExistingFolder_ANameElsewhere_AFolderNotUnderItsRoot_AndAnUnparseableFile()
    {
        Put(SkillScope.Profile, "haiku");
        Put(SkillScope.Profile, "taken");
        Put(SkillScope.Global, "pdf");
        Put(SkillScope.External, "ext");
        var haiku = Scanned(SkillScope.Profile, "haiku", external: true);

        Assert.Equal(SkillEditOutcome.ExternalReadOnly, SkillEditor.Rename(_roots, Scanned(SkillScope.External, "ext", external: true), "other").Outcome);
        Assert.Equal(SkillEditOutcome.BadName, SkillEditor.Rename(_roots, haiku, "My Haiku").Outcome);
        Assert.Equal(SkillEditOutcome.BadName, SkillEditor.Rename(_roots, haiku, "").Outcome);
        Assert.Equal(SkillEditOutcome.NothingToChange, SkillEditor.Rename(_roots, haiku, "haiku").Outcome);
        var exists = SkillEditor.Rename(_roots, haiku, "taken");
        Assert.Equal((SkillEditOutcome.Exists, SkillScope.Profile), (exists.Outcome, exists.Scope));
        var elsewhere = SkillEditor.Rename(_roots, haiku, "pdf");
        Assert.Equal((SkillEditOutcome.ExistsElsewhere, SkillScope.Global), (elsewhere.Outcome, elsewhere.Scope));
        var external = SkillEditor.Rename(_roots, haiku, "ext");   // the external root read whatever the setting says
        Assert.Equal((SkillEditOutcome.ExternalReadOnly, SkillScope.External), (external.Outcome, external.Scope));
        Assert.True(File.Exists(FileOf(SkillScope.Profile, "haiku")));

        var stray = new Skill("stray", "d", SkillScope.Profile, Path.Combine(_dir, "elsewhere", "stray"));
        Directory.CreateDirectory(stray.Directory);
        File.WriteAllText(stray.FilePath, "x");
        Assert.Equal(SkillEditOutcome.Missing, SkillEditor.Rename(_roots, stray, "moved").Outcome);
        Assert.True(File.Exists(stray.FilePath));

        // A file whose frontmatter cannot be read: refused, nothing moved.
        File.WriteAllText(FileOf(SkillScope.Profile, "haiku"), "no frontmatter here\n");
        var unparseable = SkillEditor.Rename(_roots, haiku, "fresh");
        Assert.Equal(SkillEditOutcome.Unparseable, unparseable.Outcome);
        Assert.NotEmpty(unparseable.Detail);
        Assert.True(File.Exists(FileOf(SkillScope.Profile, "haiku")));
        Assert.False(Directory.Exists(Path.Combine(_roots.Profile, "fresh")));
    }

    /// <summary>The pane's delete (2026-09-18): the folder and everything in it; refused for the external root and for a folder not right under its root, so a hand-built record can never take a folder elsewhere.</summary>
    [Fact]
    public void Delete_RemovesTheFolderAndItsFiles_RefusesExternal_AndAFolderNotUnderItsRoot()
    {
        Put(SkillScope.Global, "haiku");
        Directory.CreateDirectory(Path.Combine(_roots.Global, "haiku", "scripts"));
        File.WriteAllText(Path.Combine(_roots.Global, "haiku", "scripts", "run.py"), "p");
        Put(SkillScope.External, "pdf");

        var result = SkillEditor.Delete(_roots, Scanned(SkillScope.Global, "haiku"));

        Assert.Equal((SkillEditOutcome.Deleted, "haiku", SkillScope.Global), (result.Outcome, result.Name, result.Scope));
        Assert.False(Directory.Exists(Path.Combine(_roots.Global, "haiku")));
        Assert.True(Directory.Exists(_roots.Global));

        Assert.Equal(SkillEditOutcome.ExternalReadOnly, SkillEditor.Delete(_roots, Scanned(SkillScope.External, "pdf", external: true)).Outcome);
        Assert.True(File.Exists(FileOf(SkillScope.External, "pdf")));

        var stray = new Skill("stray", "d", SkillScope.Global, Path.Combine(_dir, "elsewhere", "stray"));
        Directory.CreateDirectory(stray.Directory);
        File.WriteAllText(stray.FilePath, "x");
        Assert.Equal(SkillEditOutcome.Missing, SkillEditor.Delete(_roots, stray).Outcome);
        Assert.True(File.Exists(stray.FilePath));
        // The root itself, dressed as a skill: not under the root, refused.
        Assert.Equal(SkillEditOutcome.Missing, SkillEditor.Delete(_roots, new Skill("skills", "d", SkillScope.Global, _roots.Global)).Outcome);
        Assert.True(Directory.Exists(_roots.Global));
    }

    [Fact]
    public void Roots_ForTheSettings_AreTheProfilesTheHomesAndTheExternal()
    {
        using var settings = new NeonSidekick.Settings.AppSettings(_dir);
        var roots = SkillRoots.For(settings, Path.Combine(_dir, "ext"));

        Assert.Equal(Path.Combine(settings.ProfileDirectory, "skills"), roots.Profile);
        Assert.Equal(Path.Combine(_dir, "skills"), roots.Global);
        Assert.Equal(Path.Combine(_dir, "ext"), roots.External);
        Assert.Equal(settings.ProfileSkillsDirectory, roots.Profile);
        Assert.Equal(settings.GlobalSkillsDirectory, roots.Global);
        Assert.EndsWith(Path.Combine(".agents", "skills"), SkillRoots.DefaultExternalDirectory());
        Assert.True(Path.IsPathRooted(SkillRoots.DefaultExternalDirectory()));
    }
}
