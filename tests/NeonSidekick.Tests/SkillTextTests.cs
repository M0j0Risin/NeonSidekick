using NeonSidekick.Sessions;
using NeonSidekick.Skills;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

public class SkillTextTests
{
    private static readonly TimeZoneInfo Zone = ManualTimeProvider.DefaultZone;
    private static readonly DateTimeOffset At = ManualTimeProvider.DefaultUtcNow;   // 2026-09-11 14:05 in the zone

    [Fact]
    public void CleanSummary_CollapsesWhitespace_CutsAtTheCap_EmptyForBlank()
    {
        // The reflection's summary line (2026-09-19).
        Assert.Equal(300, SkillText.MaxSummaryChars);
        Assert.Equal("", SkillText.CleanSummary(null));
        Assert.Equal("", SkillText.CleanSummary(" \n\t "));
        Assert.Equal("Added the retry. Kept the rest.", SkillText.CleanSummary("  Added   the\r\nretry.\n\n  Kept the rest.\n"));
        string cut = SkillText.CleanSummary(new string('x', 299) + " " + new string('y', 20));
        Assert.Equal(300, cut.Length);
        Assert.Equal(new string('x', 299) + "…", cut);
        Assert.Equal(new string('x', 300), SkillText.CleanSummary(new string('x', 300)));   // exactly the cap: whole
    }

    private static SkillEvent Write(string kind) => new(1, 1, At, kind, SkillActors.Reflection, "neon", null, null, "");

    [Fact]
    public void UsageLine_IsTheFactsThatExist_JoinedBySemicolons()
    {
        // The reflection's catalog and the Skills › name page (2026-09-19; over the skill records since 2026-10-02).
        Assert.Equal("loaded 12 times across 6 sessions, 4 followed by errors; last loaded 2026-09-11 14:05; written by a reflection 2× (updated 2026-09-11 14:05)",
            SkillText.UsageLine(new SkillFacts(new SkillUseFacts(12, 6, 4, At), 2, Write(SkillEventKinds.File), null, null, 0), Zone));
        Assert.Equal("loaded 1 time; last loaded 2026-09-11 14:05", SkillText.UsageLine(new SkillFacts(new SkillUseFacts(1, 0, 0, At), 0, null, null, null, 0), Zone));
        Assert.Equal("written by a reflection 1× (created 2026-09-11 14:05)", SkillText.UsageLine(new SkillFacts(null, 1, Write(SkillEventKinds.Created), null, null, 0), Zone));
        Assert.Equal("edited by hand 2026-09-11 14:05; installed from owner/repo, changed by a reflection since",
            SkillText.UsageLine(new SkillFacts(null, 0, null, At, "owner/repo", 1), Zone));
        Assert.Equal("installed from owner/repo", SkillText.UsageLine(new SkillFacts(null, 0, null, null, "owner/repo", 0), Zone));
        var none = new SkillFacts(null, 0, null, null, null, 0);
        Assert.True(none.IsEmpty);
        Assert.Equal("never loaded", SkillText.UsageLine(none, Zone));
        Assert.Equal(SkillText.NeverLoaded, SkillText.UsageLine(new SkillFacts(null, 1, null, null, null, 0), Zone));   // a count with no mark is no fact
        Assert.Throws<ArgumentNullException>(() => SkillText.UsageLine(none, null!));
    }

    [Fact]
    public void GuardRefusals_AreErrorSentences_ThatSayWhatToDo()
    {
        // The reflection's write guard (2026-10-02).
        Assert.Equal("Error: load skill 'haiku' with load_skill before rewriting it, then keep what still holds", SkillText.LoadBeforeRewrite("haiku"));
        Assert.Equal("Error: skill 'haiku' changed since you loaded it; load it again and build on the new text", SkillText.ChangedSinceLoad("haiku"));
        Assert.StartsWith("Error: skill 'pdf' was installed from owner/repo, and a reflection leaves an installed skill as it is;", SkillText.InstalledReadOnly("pdf", "owner/repo"), StringComparison.Ordinal);
        Assert.Contains("installed from a skill source,", SkillText.InstalledReadOnly("pdf", ""), StringComparison.Ordinal);
    }
}
