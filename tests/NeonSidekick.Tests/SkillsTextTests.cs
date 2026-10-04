using NeonSidekick.App;
using NeonSidekick.Skills;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

public class SkillsTextTests
{
    private static readonly SkillRoots Roots = new(@"D:\home\profiles\default\skills", @"D:\home\skills", @"C:\Users\x\.agents\skills");
    private static readonly Skill Haiku = new("haiku", "Writes haiku.", SkillScope.Profile, @"D:\home\profiles\default\skills\haiku");
    private static readonly Skill Pdf = new("pdf-processing", "Extracts PDF text.", SkillScope.External, @"C:\Users\x\.agents\skills\pdf-processing", Warning: "name 'pdf-processing' does not match the folder 'pdf'");
    private static readonly Skill Hidden = new("haiku", "The global one.", SkillScope.Global, @"D:\home\skills\haiku", ShadowedBy: SkillScope.Profile);
    private static readonly SkillProblem Broken = new(@"D:\home\skills\broken", SkillFrontmatter.NoDescriptionProblem);

    private static SkillsFacts Facts(bool enabled = true, IReadOnlyList<Skill>? skills = null, IReadOnlyList<Skill>? shadowed = null, IReadOnlyList<SkillProblem>? problems = null) =>
        new(enabled, skills ?? [], shadowed ?? [], problems ?? [], Roots);

    [Fact]
    public void Labels_ArePinned()
    {
        Assert.Equal("🎓 Skills", SkillsText.Label);
    }

    [Fact]
    public void LoadedLines_ListTheCatalog_TheWarnings_TheShadowed_AndTheProblems_ABlankBetweenBlocks()
    {
        var lines = SkillsText.LoadedLines(Facts(skills: [Haiku, Pdf], shadowed: [Hidden], problems: [Broken]));

        Assert.Equal(
            [
                "haiku           profile  Writes haiku.",
                "pdf-processing  external Extracts PDF text.",
                "                (name 'pdf-processing' does not match the folder 'pdf')",
                "",
                "Shadowed (a higher root holds the name):",
                "  haiku           global   shadowed by the profile skills",
                "",
                "Skipped:",
                @"  D:\home\skills\broken: the frontmatter has no description",
            ],
            lines);
    }

    [Fact]
    public void LoadedLines_AreTheCatalogAlone_WithNothingShadowedOrSkipped()
    {
        Assert.Equal(["haiku  profile  Writes haiku."], SkillsText.LoadedLines(Facts(skills: [Haiku])));
    }

    [Fact]
    public void LoadedLines_WithNone_SayNone_Alone()
    {
        Assert.Equal([SkillsText.NoneLine], SkillsText.LoadedLines(Facts()));
    }

    [Fact]
    public void LoadedLines_Off_SaySo_Alone_TheCatalogNotListed()
    {
        Assert.Equal([SkillsText.OffLine], SkillsText.LoadedLines(Facts(enabled: false, skills: [Haiku], shadowed: [Hidden], problems: [Broken])));
    }

    [Fact]
    public void Lines_AreTheOfferedTab_TheTitleAsHeading_TheContentIndented()
    {
        // The Roots tab after Project until later on 2026-09-19 (the user's call); the Project tab went on 2026-10-01 (its toggle an Options row).
        var lines = SkillsText.Lines(Facts(skills: [Haiku])).ToList();

        Assert.Equal(
            [
                "Offered",
                "  haiku  profile  Writes haiku.",
            ],
            lines);
    }

    private static string Cyan(string text) => $"[{Theme.AccentSecondary.ToMarkup()}]{text}[/]";
    private static string Dim(string text) => $"[#9A8BB8]{text}[/]";

    /// <summary>The Offered tab (Loaded until 2026-09-19) as menu rows (2026-09-18): the lines without the blank separators, the name column in the label colour, the skill beside a catalog or shadowed row and null elsewhere.</summary>
    [Fact]
    public void LoadedRows_AreTheLinesWithoutTheBlanks_TheSkillOnCatalogAndShadowedRowsAlone()
    {
        var rows = SkillsText.LoadedRows(Facts(skills: [Haiku, Pdf], shadowed: [Hidden], problems: [Broken]));

        Assert.Equal(
            [
                (Cyan("haiku         ") + "  profile  Writes haiku.", Haiku),
                (Cyan("pdf-processing") + "  external Extracts PDF text.", Pdf),
                (Dim("                (name 'pdf-processing' does not match the folder 'pdf')"), null),
                (Cyan("Shadowed (a higher root holds the name):"), null),
                (Dim("  haiku           global   shadowed by the profile skills"), Hidden),
                (Cyan("Skipped:"), null),
                (Dim(@"  D:\home\skills\broken: the frontmatter has no description"), null),
            ],
            rows);
        Assert.Equal([(Dim(SkillsText.OffLine), (Skill?)null)], SkillsText.LoadedRows(Facts(enabled: false, skills: [Haiku])));
        Assert.Equal([(Dim(SkillsText.NoneLine), (Skill?)null)], SkillsText.LoadedRows(Facts()));
    }

    /// <summary>Under a filter (2026-10-03, the user's ask): the skills whose name or description holds it, a warning with its skill, a heading only over what is left.</summary>
    [Fact]
    public void LoadedRows_UnderAFilter_KeepTheMatches_TheWarningWithItsSkill_AndAHeadingOnlyOverWhatIsLeft()
    {
        var facts = Facts(skills: [Haiku, Pdf], shadowed: [Hidden], problems: [Broken]);

        Assert.Equal(
            [
                (Cyan("haiku         ") + "  profile  Writes haiku.", Haiku),
                (Cyan("Shadowed (a higher root holds the name):"), null),
                (Dim("  haiku           global   shadowed by the profile skills"), Hidden),
            ],
            SkillsText.LoadedRows(facts, "HAIKU"));
        Assert.Equal(
            [
                (Cyan("pdf-processing") + "  external Extracts PDF text.", Pdf),
                (Dim("                (name 'pdf-processing' does not match the folder 'pdf')"), null),
            ],
            SkillsText.LoadedRows(facts, "extracts"));   // the description's word
        Assert.Equal(
            [
                (Cyan("Skipped:"), null),
                (Dim(@"  D:\home\skills\broken: the frontmatter has no description"), null),
            ],
            SkillsText.LoadedRows(facts, "broken"));
        Assert.Equal([(Dim(MenuFilter.NoMatchLine("sonnet")), (Skill?)null)], SkillsText.LoadedRows(facts, "sonnet"));
        Assert.Equal(SkillsText.LoadedRows(facts), SkillsText.LoadedRows(facts, ""));
        Assert.Equal([(Dim(SkillsText.OffLine), (Skill?)null)], SkillsText.LoadedRows(Facts(enabled: false, skills: [Haiku]), "x"));
    }

    /// <summary>A description is the author's: brackets are escaped, never markup.</summary>
    [Fact]
    public void LoadedRows_EscapeTheDescription()
    {
        var odd = new Skill("odd", "Uses [bold] tags.", SkillScope.Global, @"D:\home\skills\odd");
        Assert.Equal(Cyan("odd ") + "  global   Uses [[bold]] tags.", SkillsText.LoadedRows(Facts(skills: [odd]))[0].Markup);
    }

    [Fact]
    public void NameWidth_IsTheLongestOfBothLists_AtLeastFour()
    {
        Assert.Equal(4, SkillsText.NameWidth(Facts()));
        Assert.Equal(5, SkillsText.NameWidth(Facts(skills: [Haiku])));
        Assert.Equal(14, SkillsText.NameWidth(Facts(skills: [Haiku, Pdf])));
    }
}
