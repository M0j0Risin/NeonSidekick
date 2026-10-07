using NeonSidekick.App;
using NeonSidekick.Skills;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>A skill's lock (2026-10-07, the user's ask): the sidecar, the editor's refusals, the catalog, the prompt and the Offered tab's cell.</summary>
public class SkillLockTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly SkillRoots _roots;
    private static readonly DateTimeOffset At = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    public SkillLockTests()
    {
        _roots = new SkillRoots(Path.Combine(_dir, "profile", "skills"), Path.Combine(_dir, "skills"), Path.Combine(_dir, ".agents", "skills"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string Folder(SkillScope scope, string name) => Path.Combine(_roots.Of(scope), name);

    private void Put(SkillScope scope, string name)
    {
        Directory.CreateDirectory(Folder(scope, name));
        File.WriteAllText(Path.Combine(Folder(scope, name), SkillCatalog.FileName), $"---\nname: {name}\ndescription: d\n---\n\ni\n");
    }

    private Skill SkillOf(string name)
    {
        var catalog = new SkillCatalog(() => _roots);
        catalog.Scan(external: false);
        return catalog.Skills.Single(s => s.Name == name);
    }

    [Fact]
    public void TheSidecar_LocksAndUnlocks()
    {
        Put(SkillScope.Profile, "haiku");
        string folder = Folder(SkillScope.Profile, "haiku");
        Assert.False(SkillLock.IsLocked(folder));

        Assert.Null(SkillLock.Set(folder, true, At));
        Assert.True(SkillLock.IsLocked(folder));
        Assert.StartsWith("locked 2026-10-07T12:00:00Z", File.ReadAllText(Path.Combine(folder, SkillLock.FileName)), StringComparison.Ordinal);
        Assert.True(SkillOf("haiku").Locked);

        Assert.Null(SkillLock.Set(folder, false, At));
        Assert.False(SkillLock.IsLocked(folder));
        Assert.Null(SkillLock.Set(folder, false, At));   // unlocking an unlocked skill is nothing
        Assert.False(SkillOf("haiku").Locked);
        Assert.Equal(".neon-lock", SkillLock.FileName);
    }

    /// <summary>Every change the editor makes is refused while the lock is there, and works again once it goes.</summary>
    [Fact]
    public void TheEditor_RefusesEveryChange_OnALockedSkill()
    {
        Put(SkillScope.Profile, "haiku");
        File.WriteAllText(Path.Combine(Folder(SkillScope.Profile, "haiku"), "notes.md"), "old");
        SkillLock.Set(Folder(SkillScope.Profile, "haiku"), true, At);
        var skill = SkillOf("haiku");
        string before = File.ReadAllText(skill.FilePath);
        var time = TimeProvider.System;

        Assert.Equal(SkillEditOutcome.Locked, SkillEditor.Update(_roots, SkillScope.Profile, "haiku", "new", null, false).Outcome);
        Assert.Equal(SkillEditOutcome.Locked, SkillEditor.Update(_roots, SkillScope.Global, "haiku", null, "body", false).Outcome);   // found where it lives
        Assert.Equal(SkillEditOutcome.Locked, SkillEditor.WriteFile(_roots, SkillScope.Profile, "haiku", "more.md", "x", false, time).Outcome);
        Assert.Equal(SkillEditOutcome.Locked, SkillEditor.EditFile(_roots, SkillScope.Profile, "haiku", "notes.md", "old", "new", false, false, time).Outcome);
        Assert.Equal(SkillEditOutcome.Locked, SkillEditor.Restore(_roots, SkillScope.Profile, skill.Directory, "haiku", SkillCatalog.FileName, "x").Outcome);
        Assert.Equal(SkillEditOutcome.Locked, SkillEditor.Move(_roots, skill, SkillScope.Global).Outcome);
        Assert.Equal(SkillEditOutcome.Locked, SkillEditor.Rename(_roots, skill, "verse").Outcome);
        Assert.Equal(SkillEditOutcome.Locked, SkillEditor.Delete(_roots, skill).Outcome);
        Assert.Equal(before, File.ReadAllText(skill.FilePath));
        Assert.Equal("old", File.ReadAllText(Path.Combine(skill.Directory, "notes.md")));
        Assert.False(File.Exists(Path.Combine(skill.Directory, "more.md")));
        Assert.True(Directory.Exists(skill.Directory));

        SkillLock.Set(skill.Directory, false, At);
        Assert.Equal(SkillEditOutcome.Updated, SkillEditor.Update(_roots, SkillScope.Profile, "haiku", "new", null, false).Outcome);
    }

    /// <summary>The sidecar is the app's: a file action never writes it, and load_skill never lists it.</summary>
    [Fact]
    public void TheSidecar_IsProtected_AndUnlisted()
    {
        Put(SkillScope.Profile, "haiku");
        File.WriteAllText(Path.Combine(Folder(SkillScope.Profile, "haiku"), "notes.md"), "n");

        Assert.Equal(SkillEditOutcome.ProtectedFile, SkillEditor.WriteFile(_roots, SkillScope.Profile, "haiku", SkillLock.FileName, "x", false, TimeProvider.System).Outcome);
        Assert.False(SkillLock.IsLocked(Folder(SkillScope.Profile, "haiku")));

        SkillLock.Set(Folder(SkillScope.Profile, "haiku"), true, At);
        Assert.Equal(["notes.md"], SkillCatalog.Resources(SkillOf("haiku"), out _));
    }

    [Fact]
    public void TheModel_IsToldTheSkillIsLocked_UpFront_AndOnARefusal()
    {
        var locked = new Skill("haiku", "Writes haiku.", SkillScope.Profile, Folder(SkillScope.Profile, "haiku")) { Locked = true };
        var open = new Skill("verse", "Writes verse.", SkillScope.Profile, Folder(SkillScope.Profile, "verse"));

        string catalog = SkillsPrompt.Catalog([locked, open]);

        Assert.Contains("<name>haiku</name>\n    <description>Writes haiku.</description>\n    " + SkillsPrompt.LockedTag + "\n  </skill>", catalog);
        Assert.Single(catalog.Split(SkillsPrompt.LockedTag)[1..]);
        Assert.Equal("<locked>the user locked this skill: it can be loaded, not changed</locked>", SkillsPrompt.LockedTag);
        Assert.Equal(SkillText.Locked("haiku"), SkillText.Edited(new SkillEditResult(SkillEditOutcome.Locked, "haiku", SkillScope.Profile)));
        Assert.Equal("Error: skill 'haiku' is locked by the user; it cannot be changed until they unlock it on /skills. To keep what these turns taught, create a companion skill (a new name) instead", SkillText.Locked("haiku"));
    }

    /// <summary>The Offered tab's version cell (the user's pick): 🔒v4 on a locked skill, the others padded by the lock's cells while any is locked.</summary>
    [Fact]
    public void TheVersionCell_LeadsALockedSkillWithTheLock_AndPadsTheOthers()
    {
        Assert.Equal(2, TextCells.Width(SkillRecordText.LockGlyph));
        var locked = new Skill("pdf-tools", "d", SkillScope.Profile, "p") { Locked = true };
        var open = new Skill("haiku", "d", SkillScope.Profile, "h");
        var external = new Skill("ext", "d", SkillScope.External, "e");
        var versions = new Dictionary<string, int> { ["pdf-tools"] = 4, ["haiku"] = 1 };
        SkillsFacts Facts(params Skill[] skills) => new(true, skills, [], [], _roots, s => versions.TryGetValue(s.Name, out int v) ? v : null);

        var some = Facts(locked, open, external);
        Assert.Equal("🔒v4", SkillsText.VersionText(some, locked));
        Assert.Equal("  v1", SkillsText.VersionText(some, open));
        Assert.Equal("  ", SkillsText.VersionText(some, external));
        Assert.Equal(4, SkillsText.VersionWidth(some));
        Assert.Equal("🔒v4  ", SkillsText.VersionCell(some, locked, 4));
        Assert.Equal("  v1  ", SkillsText.VersionCell(some, open, 4));

        var none = Facts(open);   // nothing locked: the column as it was
        Assert.Equal("v1", SkillsText.VersionText(none, open));
        Assert.Equal(2, SkillsText.VersionWidth(none));

        Assert.Equal(SkillRecordText.LockedFooter, SkillsMenu.LockedLast(locked));
        Assert.Null(SkillsMenu.LockedLast(open));
        Assert.Equal("w · " + SkillRecordText.LockedFooter, SkillsMenu.LockedLast(locked with { Warning = "w" }));
        Assert.Equal("🔒 Locked: only you can change it; unlock it on its page.", SkillRecordText.LockedFooter);
        Assert.Equal("(🔒 haiku is locked: unlock it first)", SkillRecordText.LockedNotice("haiku"));
        Assert.Equal("(🔒 locked haiku)", SkillRecordText.LockSetNotice("haiku", true));
        Assert.Equal("(🔓 unlocked haiku)", SkillRecordText.LockSetNotice("haiku", false));
    }
}
