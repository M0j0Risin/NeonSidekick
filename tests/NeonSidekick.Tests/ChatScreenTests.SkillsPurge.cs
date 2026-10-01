using NeonSidekick.App;
using NeonSidekick.Skills;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary><c>/skills purge list|commit &lt;age&gt;</c> on the screen (2026-09-30).</summary>
public partial class ChatScreenTests
{
    /// <summary>A global skill folder whose <c>SKILL.md</c> was created and last written <paramref name="daysAgo"/> days before the fixture's clock.</summary>
    private string SeedSkill(string folder, int daysAgo)
    {
        string dir = Path.Combine(_settings.GlobalSkillsDirectory, folder);
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, SkillCatalog.FileName);
        File.WriteAllText(file, "---\nname: " + folder + "\ndescription: Does things.\n---\nSteps.\n");
        var when = _time.GetUtcNow().AddDays(-daysAgo).UtcDateTime;
        File.SetCreationTimeUtc(file, when);
        File.SetLastWriteTimeUtc(file, when);
        return dir;
    }

    private SkillRecordStore OpenSkillRecords() => new(_settings.StorageDirectory);

    /// <summary>
    /// <c>list</c> names the stale skill and deletes nothing; <c>commit</c> asks, and a yes deletes the stale one's folder and record
    /// alone. A recent change keeps a never-used skill.
    /// </summary>
    [Fact]
    public async Task SkillsPurge_ListShowsTheStaleOnes_CommitDeletesThemAfterAYes()
    {
        _settings.Update(d => d.TtsOutput = false);
        _geometry = new ScreenGeometry(() => null);
        string old = SeedSkill("old-skill", 60);
        string fresh = SeedSkill("fresh-skill", 2);
        StepsWhenIdle(Line("/skills purge list 30"), Line("/skills purge commit 30"), Key(Keys.Char('y')), Key(Keys.Enter), Line("/exit"));

        string output = await RunAsync();

        var days = TimeSpan.FromDays(30);
        Assert.Contains("· " + SkillRecordText.ListNotice(1, days, "30"), output);
        Assert.Contains("old-skill · global · never used, modified ", output);
        Assert.DoesNotContain("fresh-skill · global", output);
        Assert.Contains(Titled(SkillRecordText.CommitPrompt(1, days)), output);
        Assert.Contains("· " + SkillRecordText.PurgedNotice(["old-skill"], days), output);
        Assert.False(Directory.Exists(old));
        Assert.True(Directory.Exists(fresh));
        using var store = OpenSkillRecords();
        Assert.Equal("fresh-skill", Assert.Single(store.All()).Folder);
    }

    /// <summary>No keeps everything; nothing that old says so; the words that do not parse are the usage error.</summary>
    [Fact]
    public async Task SkillsPurge_NoKeeps_NothingOldSaysSo_BadWordsAreTheUsage()
    {
        _settings.Update(d => d.TtsOutput = false);
        _geometry = new ScreenGeometry(() => null);
        string old = SeedSkill("old-skill", 60);
        StepsWhenIdle(
            Line("/skills purge commit 30"), Key(Keys.Enter),   // the cursor starts on No
            Line("/skills purge list 90"),
            Line("/skills purge"), Line("/skills purge list soon"), Line("/skills purge older 30"),
            Line("/exit"));

        string output = await RunAsync();

        Assert.Contains("· " + ChatScreen.KeptNotice, output);
        Assert.True(Directory.Exists(old));
        Assert.Contains("· " + SkillRecordText.NoneNotice(TimeSpan.FromDays(90)), output);
        Assert.Equal(3, output.Split("✗ " + SkillRecordText.SkillsUsageError).Length - 1);
        using var store = OpenSkillRecords();
        Assert.Equal("old-skill", Assert.Single(store.All()).Folder);
    }
}
