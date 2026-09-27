using System.Text;
using NeonSidekick.Llm.Tools;

namespace NeonSidekick.Skills;

/// <summary>
/// The skills block of the system prompt: the directive that says what a skill is and how to load
/// or write one, then the catalog — the Agent Skills specification's <c>&lt;available_skills&gt;</c>
/// list of name and description, the first tier of its progressive disclosure (the body comes by
/// <c>load_skill</c>, the second). No location: the tool result names the folder. Pure statics,
/// every string pinned; the <c>MemoryPrompt</c> shape.
/// </summary>
public static class SkillsPrompt
{
    /// <summary>
    /// How to use the list, with no way to add to it (2026-09-27): the <c>/botchat</c> bots', offered <c>load_skill</c>
    /// alone (<see cref="LoadOnlySection"/>). <see cref="Directive"/> is this and <see cref="EditorSentence"/>.
    /// </summary>
    public static readonly string LoadOnlyDirective =
        "You have skills: folders of instructions for specific tasks, listed below with what each does and when to use it. " +
        "When a task matches a skill's description, call " + LoadSkillTool.ToolName + " with its name to read its full instructions before proceeding, " +
        "and read a file it bundles with " + LoadSkillTool.ToolName + "'s " + LoadSkillTool.FileArgument + " argument.";

    /// <summary>With at least one skill installed: how to use the list, and how to add to it.</summary>
    public static readonly string Directive = LoadOnlyDirective + " " + EditorSentence;

    /// <summary>With none installed: only the way to write one.</summary>
    public static readonly string DirectiveWithoutSkills =
        "No skills are installed yet. A skill is a folder of instructions for a specific task, kept for later sessions. " +
        EditorSentence;

    /// <summary>The <c>skill_editor</c> sentence both directives end with.</summary>
    public const string EditorSentence =
        "To keep a procedure for later sessions, or improve one, call " + SkillEditorTool.ToolName + ": the " + SkillScopes.ProfileName + " scope is for this profile only, " + SkillScopes.GlobalName + " for every profile.";

    public const string CatalogOpen = "<available_skills>";
    public const string CatalogClose = "</available_skills>";

    /// <summary>The block: the directive, then (with any skill) a blank line and the catalog.</summary>
    public static string Section(IReadOnlyList<Skill> skills)
    {
        ArgumentNullException.ThrowIfNull(skills);
        return skills.Count == 0 ? DirectiveWithoutSkills : Directive + "\n\n" + Catalog(skills);
    }

    /// <summary>The load-only block (2026-09-27, the <c>/botchat</c> bots): <see cref="LoadOnlyDirective"/>, a blank line and the catalog. Only for a non-empty list.</summary>
    public static string LoadOnlySection(IReadOnlyList<Skill> skills)
    {
        ArgumentNullException.ThrowIfNull(skills);
        return LoadOnlyDirective + "\n\n" + Catalog(skills);
    }

    /// <summary>The specification's list: one <c>&lt;skill&gt;</c> per skill with its name and description, the description XML-escaped.</summary>
    public static string Catalog(IReadOnlyList<Skill> skills) => Catalog(skills, null);

    /// <summary>
    /// <see cref="Catalog(IReadOnlyList{Skill})"/> with a <c>&lt;usage&gt;</c> line inside each skill
    /// <paramref name="usage"/> has one for (2026-09-19: the reflection's catalog carries how the stored
    /// sessions used each skill, <c>SkillText.UsageLine</c>); the main prompt passes none.
    /// </summary>
    public static string Catalog(IReadOnlyList<Skill> skills, IReadOnlyDictionary<string, string>? usage)
    {
        ArgumentNullException.ThrowIfNull(skills);
        var sb = new StringBuilder();
        sb.Append(CatalogOpen);
        foreach (var skill in skills)
        {
            sb.Append("\n  <skill>");
            sb.Append("\n    <name>").Append(Escape(skill.Name)).Append("</name>");
            sb.Append("\n    <description>").Append(Escape(skill.Description)).Append("</description>");
            if (usage is not null && usage.TryGetValue(skill.Name, out var line) && line.Length > 0)
            {
                sb.Append("\n    <usage>").Append(Escape(line)).Append("</usage>");
            }

            sb.Append("\n  </skill>");
        }

        sb.Append('\n').Append(CatalogClose);
        return sb.ToString();
    }

    /// <summary>The three characters that would break the block.</summary>
    public static string Escape(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);
    }
}
