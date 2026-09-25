using NeonSidekick.App;
using NeonSidekick.Llm;
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

    private static SkillsFacts Facts(bool enabled = true, bool projectFile = true, IReadOnlyList<Skill>? skills = null, IReadOnlyList<Skill>? shadowed = null, IReadOnlyList<SkillProblem>? problems = null, ProjectNotes? project = null) =>
        new(enabled, projectFile, skills ?? [], shadowed ?? [], problems ?? [], Roots, project);

    [Fact]
    public void Labels_ArePinned()
    {
        Assert.Equal("🎓 Skills", SkillsText.Label);
        Assert.Equal("Enter / Space = on or off · ←/→ tabs · ESC = close", SkillsText.ProjectKeys);
        Assert.Equal("Project file: off", SkillsText.ProjectFlippedNotice(false));
        Assert.Equal("Project file: on", SkillsText.ProjectFlippedNotice(true));
        Assert.Equal("none (NEON.md or AGENTS.md in the working directory)", SkillsText.NoNotesLine);   // the user's wording, 2026-09-16
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

    /// <summary>The Project tab's one row (later on 2026-09-19; the working directory over it until then): the toggle's state and the notes file on disk, whatever the toggle says; none while the skills are off.</summary>
    [Fact]
    public void ProjectRow_IsTheToggleAndTheNotesFile_OrNone()
    {
        Assert.Equal(("Project file", true, SkillsText.NoNotesLine), SkillsText.ProjectRow(Facts()));
        Assert.Equal(("Project file", true, "NEON.md (3 characters)"), SkillsText.ProjectRow(Facts(project: new ProjectNotes("NEON.md", "abc"))));
        Assert.Equal(("Project file", false, "NEON.md (3 characters)"), SkillsText.ProjectRow(Facts(projectFile: false, project: new ProjectNotes("NEON.md", "abc"))));   // off: the file on disk still named
        Assert.Equal(("Project file", true, SkillsText.NoNotesLine), SkillsText.ProjectRow(Facts(enabled: false, project: new ProjectNotes("NEON.md", "n"))));   // skills off: not read, whatever is there
        Assert.Equal(0, SkillsText.ProjectRowIndex(Facts()));
        Assert.Equal(1, SkillsText.ProjectRowIndex(Facts(enabled: false)));   // past the off line
        Assert.Equal(["Project file  on   " + SkillsText.NoNotesLine], SkillsText.ProjectLines(Facts()));
        Assert.Equal(["Project file  off  NEON.md (1,234 characters)"], SkillsText.ProjectLines(Facts(projectFile: false, project: new ProjectNotes("NEON.md", new string('n', 1234)))));
        Assert.Equal([SkillsText.OffLine, "Project file  on   " + SkillsText.NoNotesLine], SkillsText.ProjectLines(Facts(enabled: false, project: new ProjectNotes("NEON.md", "n"))));
    }

    [Fact]
    public void Lines_AreTheTwoTabs_TheTitlesAsHeadings_TheContentIndented()
    {
        // The Roots tab after Project until later on 2026-09-19 (the user's call).
        var lines = SkillsText.Lines(Facts(skills: [Haiku])).ToList();

        Assert.Equal(
            [
                "Offered",
                "  haiku  profile  Writes haiku.",
                "Project",
                "  Project file  on   " + SkillsText.NoNotesLine,
            ],
            lines);
    }

    private static string Cyan(string text) => $"[{Theme.AccentSecondary.ToMarkup()}]{text}[/]";
    private static string Dim(string text) => $"[#9A8BB8]{text}[/]";
    private static string Ink(string text) => Theme.ColorMarkup(Theme.Ink, text);

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

    /// <summary>A description is the author's: brackets are escaped, never markup.</summary>
    [Fact]
    public void LoadedRows_EscapeTheDescription()
    {
        var odd = new Skill("odd", "Uses [bold] tags.", SkillScope.Global, @"D:\home\skills\odd");
        Assert.Equal(Cyan("odd ") + "  global   Uses [[bold]] tags.", SkillsText.LoadedRows(Facts(skills: [odd]))[0].Markup);
    }

    /// <summary>The Project tab as menu rows (2026-09-18; the toggle later on 2026-09-19): the /tools Offered shape — the label in the label colour, on/off in the ink, the value dim; the whole row dim under the off line while the skills are off.</summary>
    [Fact]
    public void ProjectRowsMarkup_IsTheToggleRow_TheOffLineFirstWhileOff()
    {
        Assert.Equal(14, SkillsText.ProjectLabelWidth);
        Assert.Equal([Cyan("Project file  ") + Ink("on   ") + Dim(SkillsText.NoNotesLine)], SkillsText.ProjectRowsMarkup(Facts()));
        Assert.Equal([Cyan("Project file  ") + Ink("off  ") + Dim("NEON.md (3 characters)")], SkillsText.ProjectRowsMarkup(Facts(projectFile: false, project: new ProjectNotes("NEON.md", "abc"))));
        Assert.Equal([Dim(SkillsText.OffLine), Dim("Project file  on   " + SkillsText.NoNotesLine)], SkillsText.ProjectRowsMarkup(Facts(enabled: false)));
    }

    [Fact]
    public void NameWidth_IsTheLongestOfBothLists_AtLeastFour()
    {
        Assert.Equal(4, SkillsText.NameWidth(Facts()));
        Assert.Equal(5, SkillsText.NameWidth(Facts(skills: [Haiku])));
        Assert.Equal(14, SkillsText.NameWidth(Facts(skills: [Haiku, Pdf])));
    }
}
