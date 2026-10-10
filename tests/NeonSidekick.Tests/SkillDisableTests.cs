using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Skills;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// A disabled skill (2026-10-09, the user's ask): the sidecar, the catalog's split (not offered, still holding its name), the editor's
/// and the reflection guard's refusals, the reflection's catalog, and the Offered tab's ⛔ cell and dim row.
/// </summary>
public class SkillDisableTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly SkillRoots _roots;
    private static readonly DateTimeOffset At = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    public SkillDisableTests()
    {
        _roots = new SkillRoots(Path.Combine(_dir, "profile", "skills"), Path.Combine(_dir, "skills"), Path.Combine(_dir, ".agents", "skills"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string Folder(SkillScope scope, string name) => Path.Combine(_roots.Of(scope), name);

    private void Put(SkillScope scope, string name, string description = "d")
    {
        Directory.CreateDirectory(Folder(scope, name));
        File.WriteAllText(Path.Combine(Folder(scope, name), SkillCatalog.FileName), $"---\nname: {name}\ndescription: {description}\n---\n\ni\n");
    }

    private SkillCatalog Scanned()
    {
        var catalog = new SkillCatalog(() => _roots);
        catalog.Scan(external: false);
        return catalog;
    }

    private Skill SkillOf(string name) => Scanned().All.Single(s => s.Name == name);

    [Fact]
    public void TheSidecar_DisablesAndEnables()
    {
        Put(SkillScope.Profile, "haiku");
        string folder = Folder(SkillScope.Profile, "haiku");
        Assert.False(SkillDisable.IsDisabled(folder));

        Assert.Null(SkillDisable.Set(folder, true, At));
        Assert.True(SkillDisable.IsDisabled(folder));
        Assert.StartsWith("disabled 2026-10-09T12:00:00Z", File.ReadAllText(Path.Combine(folder, SkillDisable.FileName)), StringComparison.Ordinal);
        Assert.True(SkillOf("haiku").Disabled);

        Assert.Null(SkillDisable.Set(folder, false, At));
        Assert.False(SkillDisable.IsDisabled(folder));
        Assert.Null(SkillDisable.Set(folder, false, At));   // enabling an enabled skill is nothing
        Assert.False(SkillOf("haiku").Disabled);
        Assert.Equal(".neon-disabled", SkillDisable.FileName);
    }

    /// <summary>A disabled skill leaves the offered list for Disabled and All, still holds its name over a global twin, and load_skill's schema moves.</summary>
    [Fact]
    public void TheCatalog_OffersItNot_ButKeepsItsName()
    {
        Put(SkillScope.Profile, "haiku");
        Put(SkillScope.Global, "haiku");
        Put(SkillScope.Global, "verse");
        var catalog = new SkillCatalog(() => _roots);
        catalog.Scan(external: false);
        int version = catalog.Version;

        SkillDisable.Set(Folder(SkillScope.Profile, "haiku"), true, At);
        catalog.Scan(external: false);

        Assert.Equal(["verse"], catalog.Skills.Select(s => s.Name));
        Assert.Equal([(SkillScope.Profile, "haiku")], catalog.Disabled.Select(s => (s.Scope, s.Name)));
        Assert.Equal(["haiku", "verse"], catalog.All.Select(s => s.Name));
        Assert.Equal([(SkillScope.Global, "haiku")], catalog.Shadowed.Select(s => (s.Scope, s.Name)));   // the user's pick: the global twin stays hidden
        Assert.Null(catalog.Find("haiku"));   // load_skill finds only what is offered
        Assert.NotEqual(version, catalog.Version);

        SkillDisable.Set(Folder(SkillScope.Profile, "haiku"), false, At);
        catalog.Scan(external: false);
        Assert.Equal(["haiku", "verse"], catalog.Skills.Select(s => s.Name));
        Assert.Empty(catalog.Disabled);
    }

    /// <summary>Content changes and a create of its name are refused; the pane's rename and move still act, the sidecar travelling.</summary>
    [Fact]
    public void TheEditor_RefusesItsContent_AndItsName_ButTheUserMayTidyIt()
    {
        Put(SkillScope.Profile, "haiku");
        File.WriteAllText(Path.Combine(Folder(SkillScope.Profile, "haiku"), "notes.md"), "old");
        SkillDisable.Set(Folder(SkillScope.Profile, "haiku"), true, At);
        var skill = SkillOf("haiku");
        string before = File.ReadAllText(skill.FilePath);
        var time = TimeProvider.System;

        Assert.Equal(SkillEditOutcome.Disabled, SkillEditor.Update(_roots, SkillScope.Profile, "haiku", "new", null, false).Outcome);
        Assert.Equal(SkillEditOutcome.Disabled, SkillEditor.Update(_roots, SkillScope.Global, "haiku", null, "body", false).Outcome);   // found where it lives
        Assert.Equal(SkillEditOutcome.Disabled, SkillEditor.WriteFile(_roots, SkillScope.Profile, "haiku", "more.md", "x", false, time).Outcome);
        Assert.Equal(SkillEditOutcome.Disabled, SkillEditor.EditFile(_roots, SkillScope.Profile, "haiku", "notes.md", "old", "new", false, false, time).Outcome);
        Assert.Equal(SkillEditOutcome.Disabled, SkillEditor.Restore(_roots, SkillScope.Profile, skill.Directory, "haiku", SkillCatalog.FileName, "x").Outcome);
        Assert.Equal(SkillEditOutcome.Disabled, SkillEditor.Create(_roots, SkillScope.Profile, "haiku", "d", "i", false).Outcome);
        var elsewhere = SkillEditor.Create(_roots, SkillScope.Global, "haiku", "d", "i", false);
        Assert.Equal((SkillEditOutcome.Disabled, SkillScope.Profile), (elsewhere.Outcome, elsewhere.Scope));
        Assert.Equal(before, File.ReadAllText(skill.FilePath));
        Assert.Equal("old", File.ReadAllText(Path.Combine(skill.Directory, "notes.md")));
        Assert.False(File.Exists(Path.Combine(skill.Directory, "more.md")));
        Assert.False(Directory.Exists(Folder(SkillScope.Global, "haiku")));

        Assert.Equal(SkillEditOutcome.Renamed, SkillEditor.Rename(_roots, skill, "verse").Outcome);
        var renamed = SkillOf("verse");
        Assert.True(renamed.Disabled);
        Assert.Equal(SkillEditOutcome.Moved, SkillEditor.Move(_roots, renamed, SkillScope.Global).Outcome);
        Assert.True(SkillDisable.IsDisabled(Folder(SkillScope.Global, "verse")));
        Assert.Equal(SkillEditOutcome.Deleted, SkillEditor.Delete(_roots, SkillOf("verse")).Outcome);
    }

    /// <summary>The sidecar is the app's: a file action never writes it, and load_skill never lists it.</summary>
    [Fact]
    public void TheSidecar_IsProtected_AndUnlisted()
    {
        Put(SkillScope.Profile, "haiku");
        File.WriteAllText(Path.Combine(Folder(SkillScope.Profile, "haiku"), "notes.md"), "n");

        Assert.Equal(SkillEditOutcome.ProtectedFile, SkillEditor.WriteFile(_roots, SkillScope.Profile, "haiku", SkillDisable.FileName, "x", false, TimeProvider.System).Outcome);
        Assert.False(SkillDisable.IsDisabled(Folder(SkillScope.Profile, "haiku")));

        SkillDisable.Set(Folder(SkillScope.Profile, "haiku"), true, At);
        Assert.Equal(["notes.md"], SkillCatalog.Resources(SkillOf("haiku"), out _));
    }

    /// <summary>The main prompt never lists it; a reflection's catalog does, with the one tag (over the lock's), and its guard says why first.</summary>
    [Fact]
    public void TheReflection_SeesIt_Tagged_AndIsRefusedWithTheReason()
    {
        Put(SkillScope.Profile, "haiku", "Writes haiku.");
        Put(SkillScope.Profile, "verse", "Writes verse.");
        SkillDisable.Set(Folder(SkillScope.Profile, "haiku"), true, At);
        SkillLock.Set(Folder(SkillScope.Profile, "haiku"), true, At);
        var catalog = Scanned();

        Assert.DoesNotContain("haiku", SkillsPrompt.Section(catalog.Skills));

        var request = SkillLearner.Build([new ChatMessage(ChatRole.User, "hi")], catalog.All, null);
        string system = request[0].Text;
        Assert.Contains("<name>haiku</name>\n    <description>Writes haiku.</description>\n    " + SkillsPrompt.DisabledTag + "\n  </skill>", system);
        Assert.DoesNotContain(SkillsPrompt.LockedTag, system);
        Assert.Contains("<name>verse</name>", system);
        Assert.Equal("<disabled>the user turned this skill off: it is not offered and cannot be loaded or changed; do not create another skill that covers its topic</disabled>", SkillsPrompt.DisabledTag);

        var guard = new ReflectionWriteGuard(() => _roots, false, ReflectionInstalledPolicy.AllowAndMark);
        Assert.Equal(SkillText.Disabled("haiku"), guard.Refusal("haiku", rewrite: false));
        Assert.Null(guard.Refusal("verse", rewrite: false));
        Assert.Equal(SkillText.Disabled("haiku"), SkillText.Edited(new SkillEditResult(SkillEditOutcome.Disabled, "haiku", SkillScope.Profile)));
        Assert.Equal("Error: skill 'haiku' is disabled by the user: it is not offered and cannot be changed until they enable it on /skills. Do not write another skill for the same topic", SkillText.Disabled("haiku"));
    }

    /// <summary>The Offered tab (the user's picks): ⛔ in the lock's place, over the lock when both, the row dim whole, the footer naming both.</summary>
    [Fact]
    public void TheOfferedTab_LeadsADisabledSkillWithItsGlyph_AndDimsItsRow()
    {
        Assert.Equal(2, TextCells.Width(SkillRecordText.DisabledGlyph));
        var disabled = new Skill("pdf-tools", "d", SkillScope.Profile, "p") { Disabled = true };
        var both = new Skill("docx", "d", SkillScope.Profile, "x") { Disabled = true, Locked = true };
        var locked = new Skill("verse", "d", SkillScope.Profile, "v") { Locked = true };
        var open = new Skill("haiku", "Writes haiku.", SkillScope.Profile, "h");
        var versions = new Dictionary<string, int> { ["pdf-tools"] = 4, ["docx"] = 2, ["verse"] = 3, ["haiku"] = 1 };
        SkillsFacts Facts(params Skill[] skills) => new(true, skills, [], [], _roots, s => versions.TryGetValue(s.Name, out int v) ? v : null);

        var all = Facts(disabled, both, locked, open);
        Assert.Equal("⛔v4", SkillsText.VersionText(all, disabled));
        Assert.Equal("⛔v2", SkillsText.VersionText(all, both));
        Assert.Equal("🔒v3", SkillsText.VersionText(all, locked));
        Assert.Equal("  v1", SkillsText.VersionText(all, open));
        Assert.Equal(4, SkillsText.VersionWidth(all));
        Assert.Equal("  v1", SkillsText.VersionText(Facts(disabled, open), open));   // a disabled one alone opens the lead too
        Assert.Equal("v1", SkillsText.VersionText(Facts(open), open));

        var rows = SkillsText.LoadedRows(Facts(disabled, open));
        Assert.Equal(Theme.DimMarkup("pdf-tools  profile  ⛔v4  d"), rows[0].Markup);
        Assert.StartsWith("[", rows[1].Markup, StringComparison.Ordinal);
        Assert.DoesNotContain(Theme.DimMarkup("haiku"), rows[1].Markup);

        Assert.Equal(SkillRecordText.DisabledFooter, SkillsMenu.LockedLast(disabled));
        Assert.Equal("w · " + SkillRecordText.DisabledFooter + " · " + SkillRecordText.LockedFooter, SkillsMenu.LockedLast(both with { Warning = "w" }));
        Assert.Equal("⛔ Disabled: never offered; a reflection sees it but cannot change it. Enable it on its page.", SkillRecordText.DisabledFooter);
        Assert.Equal("(⛔ haiku is disabled: enable it first)", SkillRecordText.DisabledNotice("haiku"));
        Assert.Equal("(⛔ disabled haiku)", SkillRecordText.DisableSetNotice("haiku", true));
        Assert.Equal("(✅ enabled haiku)", SkillRecordText.DisableSetNotice("haiku", false));
        Assert.Equal("Skill disabled: profile/haiku", SkillRecordText.DisabledLogLine(SkillScope.Profile, "haiku", true));
        Assert.Equal("disable  [#9A8BB8]stop offering it; a reflection still sees it, and changes nothing[/]", SkillsMenu.DisableRow(false));
        Assert.Equal("enable   [#9A8BB8]offer it to the model again[/]", SkillsMenu.DisableRow(true));
    }
}
