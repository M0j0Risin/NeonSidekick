using NeonSidekick.Llm.Tools;
using NeonSidekick.Skills;

namespace NeonSidekick.Tests;

public class SkillsPromptTests
{
    private static readonly Skill Haiku = new("haiku", "Writes haiku. Use when the user asks for one.", SkillScope.Profile, @"D:\home\profiles\default\skills\haiku");
    private static readonly Skill Pdf = new("pdf-processing", "Extracts text & tables from <PDF> files.", SkillScope.External, @"C:\Users\x\.agents\skills\pdf-processing");

    [Fact]
    public void Directives_ArePinned_AndNameTheTools()
    {
        Assert.Equal(
            "You have skills: folders of instructions for specific tasks, listed below with what each does and when to use it. " +
            "When a task matches a skill's description, call load_skill with its name to read its full instructions before proceeding, " +
            "and read a file it bundles with load_skill's file argument. " +
            "To keep a procedure for later sessions, or improve one, call skill_editor: the profile scope is for this profile only, global for every profile.",
            SkillsPrompt.Directive);
        Assert.Equal(
            "No skills are installed yet. A skill is a folder of instructions for a specific task, kept for later sessions. " +
            "To keep a procedure for later sessions, or improve one, call skill_editor: the profile scope is for this profile only, global for every profile.",
            SkillsPrompt.DirectiveWithoutSkills);
        Assert.Contains(LoadSkillTool.ToolName, SkillsPrompt.Directive);
        Assert.Contains(SkillEditorTool.ToolName, SkillsPrompt.Directive);
        Assert.DoesNotContain(LoadSkillTool.ToolName, SkillsPrompt.DirectiveWithoutSkills);
        Assert.Contains(SkillEditorTool.ToolName, SkillsPrompt.DirectiveWithoutSkills);
    }

    [Fact]
    public void LoadOnlySection_IsTheDirectiveLessTheEditorSentence_ThenTheCatalog()
    {
        Assert.Equal(SkillsPrompt.LoadOnlyDirective + " " + SkillsPrompt.EditorSentence, SkillsPrompt.Directive);
        Assert.Contains(LoadSkillTool.ToolName, SkillsPrompt.LoadOnlyDirective);
        Assert.DoesNotContain(SkillEditorTool.ToolName, SkillsPrompt.LoadOnlyDirective);
        Assert.Equal(SkillsPrompt.LoadOnlyDirective + "\n\n" + SkillsPrompt.Catalog([Haiku]), SkillsPrompt.LoadOnlySection([Haiku]));
    }

    [Fact]
    public void Section_WithNoSkill_IsTheShortDirectiveAlone()
    {
        Assert.Equal(SkillsPrompt.DirectiveWithoutSkills, SkillsPrompt.Section([]));
    }

    [Fact]
    public void Section_IsTheDirective_ABlankLine_AndTheCatalog_OneEntryEach_Escaped()
    {
        string section = SkillsPrompt.Section([Haiku, Pdf]);

        Assert.Equal(SkillsPrompt.Directive + "\n\n" + SkillsPrompt.Catalog([Haiku, Pdf]), section);
        Assert.Equal(
            "<available_skills>\n" +
            "  <skill>\n    <name>haiku</name>\n    <description>Writes haiku. Use when the user asks for one.</description>\n  </skill>\n" +
            "  <skill>\n    <name>pdf-processing</name>\n    <description>Extracts text &amp; tables from &lt;PDF&gt; files.</description>\n  </skill>\n" +
            "</available_skills>",
            SkillsPrompt.Catalog([Haiku, Pdf]));
        Assert.DoesNotContain(Haiku.Directory, section);   // no location: the tool result names the folder
    }

    [Fact]
    public void Catalog_WithUsageLines_PutsOneInsideTheSkillsThatHaveOne_Escaped()
    {
        // The reflection's catalog (2026-09-19): a <usage> child after the description; the main prompt passes none.
        var usage = new Dictionary<string, string> { ["haiku"] = "loaded in 2 turns across 1 session; last loaded 2026-09-18 <14:05>", ["pdf-processing"] = "" };
        Assert.Equal(
            "<available_skills>\n" +
            "  <skill>\n    <name>haiku</name>\n    <description>Writes haiku. Use when the user asks for one.</description>\n    <usage>loaded in 2 turns across 1 session; last loaded 2026-09-18 &lt;14:05&gt;</usage>\n  </skill>\n" +
            "  <skill>\n    <name>pdf-processing</name>\n    <description>Extracts text &amp; tables from &lt;PDF&gt; files.</description>\n  </skill>\n" +
            "</available_skills>",
            SkillsPrompt.Catalog([Haiku, Pdf], usage));
        Assert.Equal(SkillsPrompt.Catalog([Haiku, Pdf]), SkillsPrompt.Catalog([Haiku, Pdf], null));
        Assert.Equal(SkillsPrompt.Catalog([Haiku, Pdf]), SkillsPrompt.Catalog([Haiku, Pdf], new Dictionary<string, string>()));
    }

    [Fact]
    public void Catalog_OfNone_IsTheTwoTagsAlone()
    {
        Assert.Equal("<available_skills>\n</available_skills>", SkillsPrompt.Catalog([]));
    }

    [Fact]
    public void Escape_IsTheThreeCharacters()
    {
        Assert.Equal("a &amp; b &lt;c&gt; \"d\"", SkillsPrompt.Escape("a & b <c> \"d\""));
    }

    [Fact]
    public void NullList_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => SkillsPrompt.Section(null!));
        Assert.Throws<ArgumentNullException>(() => SkillsPrompt.Catalog(null!));
    }
}
