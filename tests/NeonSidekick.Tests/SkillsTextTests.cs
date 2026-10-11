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

    /// <summary>
    /// The version column (2026-10-07, the user's ask): between the scope and the description, padded to the widest, blank for an external
    /// skill; the lines and the menu rows alike; none at all without a version source (the rows above).
    /// </summary>
    [Fact]
    public void WithVersions_AColumn_SitsBetweenTheScopeAndTheDescription()
    {
        Skill global = new("csv", "Tidies CSV.", SkillScope.Global, @"D:\home\skills\csv");
        var facts = Facts(skills: [Haiku, global, Pdf]) with { Version = s => s.Scope == SkillScope.External ? null : s == Haiku ? 12 : 1 };

        Assert.Equal(
            [
                "haiku           profile  v12  Writes haiku.",
                "csv             global   v1   Tidies CSV.",
                "pdf-processing  external      Extracts PDF text.",
                "                (name 'pdf-processing' does not match the folder 'pdf')",
            ],
            SkillsText.LoadedLines(facts));
        Assert.Equal(Cyan("haiku         ") + "  profile  v12  Writes haiku.", SkillsText.LoadedRows(facts)[1].Markup);   // under the catalog's heading (2026-10-10)
        Assert.Equal(Cyan("csv           ") + "  global   v1   Tidies CSV.", SkillsText.LoadedRows(facts, "csv")[1].Markup);   // a filter keeps the column's width

        Assert.Equal("v4", SkillsText.VersionLabel(4));
        Assert.Equal("", SkillsText.VersionLabel(null));
        Assert.Equal(2, SkillsText.VersionWidth(Facts(skills: [Pdf]) with { Version = _ => null }));
        Assert.Equal(0, SkillsText.VersionWidth(Facts(skills: [Haiku])));
    }

    private static string Cyan(string text) => $"[{Theme.AccentSecondary.ToMarkup()}]{text}[/]";
    private static string Dim(string text) => $"[#9A8BB8]{text}[/]";

    // A row of the Offered tab: a heading's rule (2026-10-10), a gap, or a plain row with its skill.
    private static (string, Skill?, bool) Rule(string title, int count, string? note = null) => (SectionRule.Markup(title, count.ToString(System.Globalization.CultureInfo.InvariantCulture), note), null, true);
    private static readonly (string, Skill?, bool) Gap = ("", null, false);
    private static (string, Skill?, bool) Row(string markup, Skill? skill = null) => (markup, skill, false);

    /// <summary>
    /// The Offered tab (Loaded until 2026-09-19) as menu rows (2026-09-18): the name column in the label colour, the skill beside a catalog
    /// or shadowed row and null elsewhere. Since 2026-10-10 (the user's ask: the <c>/settings</c> look) each block under a rule with its
    /// count — Skills, Shadowed with its note, Skipped — a gap between blocks, the rows under them no longer indented.
    /// </summary>
    [Fact]
    public void LoadedRows_AreTheBlocksUnderTheirRules_TheSkillOnCatalogAndShadowedRowsAlone()
    {
        var rows = SkillsText.LoadedRows(Facts(skills: [Haiku, Pdf], shadowed: [Hidden], problems: [Broken]));

        Assert.Equal(
            [
                Rule("Skills", 2),
                Row(Cyan("haiku         ") + "  profile  Writes haiku.", Haiku),
                Row(Cyan("pdf-processing") + "  external Extracts PDF text.", Pdf),
                Row(Dim("                (name 'pdf-processing' does not match the folder 'pdf')")),
                Gap,
                Rule("Shadowed", 1, "a higher root holds the name"),
                Row(Dim("haiku           global   shadowed by the profile skills"), Hidden),
                Gap,
                Rule("Skipped", 1),
                Row(Dim(@"D:\home\skills\broken: the frontmatter has no description")),
            ],
            rows);
        Assert.Equal(new HashSet<int> { 0, 5, 8 }, SkillsText.HeadingRows(rows));
        Assert.Equal([Row(Dim(SkillsText.OffLine))], SkillsText.LoadedRows(Facts(enabled: false, skills: [Haiku])));
        Assert.Equal([Row(Dim(SkillsText.NoneLine))], SkillsText.LoadedRows(Facts()));
    }

    /// <summary>Under a filter (2026-10-03, the user's ask): the skills whose name or description holds it, a warning with its skill, a heading only over what is left, its count the whole block's.</summary>
    [Fact]
    public void LoadedRows_UnderAFilter_KeepTheMatches_TheWarningWithItsSkill_AndAHeadingOnlyOverWhatIsLeft()
    {
        var facts = Facts(skills: [Haiku, Pdf], shadowed: [Hidden], problems: [Broken]);

        Assert.Equal(
            [
                Rule("Skills", 2),
                Row(Cyan("haiku         ") + "  profile  Writes haiku.", Haiku),
                Gap,
                Rule("Shadowed", 1, "a higher root holds the name"),
                Row(Dim("haiku           global   shadowed by the profile skills"), Hidden),
            ],
            SkillsText.LoadedRows(facts, "HAIKU"));
        Assert.Equal(
            [
                Rule("Skills", 2),
                Row(Cyan("pdf-processing") + "  external Extracts PDF text.", Pdf),
                Row(Dim("                (name 'pdf-processing' does not match the folder 'pdf')")),
            ],
            SkillsText.LoadedRows(facts, "extracts"));   // the description's word
        Assert.Equal(
            [
                Rule("Skipped", 1),
                Row(Dim(@"D:\home\skills\broken: the frontmatter has no description")),
            ],
            SkillsText.LoadedRows(facts, "broken"));
        Assert.Equal([Row(Dim(MenuFilter.NoMatchLine("sonnet")))], SkillsText.LoadedRows(facts, "sonnet"));
        Assert.Equal(SkillsText.LoadedRows(facts), SkillsText.LoadedRows(facts, ""));
        Assert.Equal([Row(Dim(SkillsText.OffLine))], SkillsText.LoadedRows(Facts(enabled: false, skills: [Haiku]), "x"));
    }

    /// <summary>A description is the author's: brackets are escaped, never markup.</summary>
    [Fact]
    public void LoadedRows_EscapeTheDescription()
    {
        var odd = new Skill("odd", "Uses [bold] tags.", SkillScope.Global, @"D:\home\skills\odd");
        Assert.Equal(Cyan("odd ") + "  global   Uses [[bold]] tags.", SkillsText.LoadedRows(Facts(skills: [odd]))[1].Markup);
    }

    /// <summary>
    /// The Offered tab's scope sub-tabs (2026-10-10, the user's ask): each keeps its root's skills, the shadowed ones of that root and the
    /// skipped folders under it, the columns the whole catalog's, each heading counting its scope's; a scope with none says so, or the no-match row under a filter.
    /// </summary>
    [Fact]
    public void LoadedRows_ForAScope_KeepThatRootsSkills_ShadowedAndSkippedFolders()
    {
        Skill csv = new("csv", "Tidies CSV.", SkillScope.Global, @"D:\home\skills\csv");
        var facts = Facts(skills: [Haiku, csv, Pdf], shadowed: [Hidden], problems: [Broken]);

        Assert.Equal([Rule("Skills", 1), Row(Cyan("haiku         ") + "  profile  Writes haiku.", Haiku)], SkillsText.LoadedRows(facts, "", SkillScope.Profile));
        Assert.Equal(
            [
                Rule("Skills", 1),
                Row(Cyan("csv           ") + "  global   Tidies CSV.", csv),
                Gap,
                Rule("Shadowed", 1, "a higher root holds the name"),
                Row(Dim("haiku           global   shadowed by the profile skills"), Hidden),
                Gap,
                Rule("Skipped", 1),
                Row(Dim(@"D:\home\skills\broken: the frontmatter has no description")),
            ],
            SkillsText.LoadedRows(facts, "", SkillScope.Global));
        Assert.Equal(
            [
                Rule("Skills", 1),
                Row(Cyan("pdf-processing") + "  external Extracts PDF text.", Pdf),
                Row(Dim("                (name 'pdf-processing' does not match the folder 'pdf')")),
            ],
            SkillsText.LoadedRows(facts, "", SkillScope.External));
        Assert.Equal(SkillsText.LoadedRows(facts), SkillsText.LoadedRows(facts, "", null));

        // The filter on a scope; a scope with nothing of its own.
        Assert.Equal([Rule("Skills", 1), Row(Cyan("csv           ") + "  global   Tidies CSV.", csv)], SkillsText.LoadedRows(facts, "tidies", SkillScope.Global));
        Assert.Equal([Row(Dim(MenuFilter.NoMatchLine("tidies")))], SkillsText.LoadedRows(facts, "tidies", SkillScope.Profile));
        Assert.Equal([Row(Dim("(no global skills)"))], SkillsText.LoadedRows(Facts(skills: [Haiku]), "", SkillScope.Global));
        Assert.Equal([Row(Dim(SkillsText.OffLine))], SkillsText.LoadedRows(Facts(enabled: false, skills: [Haiku]), "", SkillScope.Profile));
    }

    [Fact]
    public void ScopeTabs_AreAllProfileGlobal_ExternalWhileOneIsListed_NoneWhileOffOrEmpty()
    {
        Assert.Equal([null, SkillScope.Profile, SkillScope.Global], SkillsText.ScopeTabs(Facts(skills: [Haiku])));
        Assert.Equal([null, SkillScope.Profile, SkillScope.Global, SkillScope.External], SkillsText.ScopeTabs(Facts(skills: [Haiku, Pdf])));
        Assert.Empty(SkillsText.ScopeTabs(Facts()));
        Assert.Empty(SkillsText.ScopeTabs(Facts(enabled: false, skills: [Haiku])));

        Assert.Equal(["all", "profile", "global", "external"], new SkillScope?[] { null, SkillScope.Profile, SkillScope.Global, SkillScope.External }.Select(SkillsText.ScopeTitle));
        Assert.Equal("(no profile skills)", SkillsText.ScopeNoneLine(SkillScope.Profile));
        var facts = Facts(skills: [Haiku, Pdf], shadowed: [Hidden]);
        Assert.Equal("³", SkillsText.ScopeBadge(facts, null));
        Assert.Equal("¹", SkillsText.ScopeBadge(facts, SkillScope.Global));   // the shadowed one counted under its own root
        Assert.Null(SkillsText.ScopeBadge(Facts(skills: [Haiku]), SkillScope.Global));
    }

    [Fact]
    public void ProblemScope_IsTheRootTheFolderLiesUnder_CaseAside()
    {
        Assert.Equal(SkillScope.Global, SkillsText.ProblemScope(Roots, Broken));
        Assert.Equal(SkillScope.Profile, SkillsText.ProblemScope(Roots, new(@"d:\HOME\profiles\default\skills\x", "r")));
        Assert.Equal(SkillScope.External, SkillsText.ProblemScope(Roots, new(@"C:\Users\x\.agents\skills\y", "r")));
        Assert.Null(SkillsText.ProblemScope(Roots, new(@"D:\home\skillset\z", "r")));   // a folder beside the root, not under it
        Assert.Null(SkillsText.ProblemScope(Roots, new(@"D:\home\skills", "r")));
    }

    [Fact]
    public void NameWidth_IsTheLongestOfBothLists_AtLeastFour()
    {
        Assert.Equal(4, SkillsText.NameWidth(Facts()));
        Assert.Equal(5, SkillsText.NameWidth(Facts(skills: [Haiku])));
        Assert.Equal(14, SkillsText.NameWidth(Facts(skills: [Haiku, Pdf])));
    }
}
