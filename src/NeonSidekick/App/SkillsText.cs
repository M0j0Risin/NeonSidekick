using NeonSidekick.Skills;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>
/// What <c>/skills</c> shows, read when it opens: the skills switch, the catalog as of its last scan and the roots (for the scope page). The
/// <c>Project file</c> toggle and the notes on disk, for the Project tab, went with it (2026-10-01). <see cref="Version"/> (2026-10-07)
/// is each skill's version from the records (<c>SkillRecords.Versions</c>), null for an external one; without it, no version column.
/// </summary>
public sealed record SkillsFacts(
    bool Enabled,
    IReadOnlyList<Skill> Skills,
    IReadOnlyList<Skill> Shadowed,
    IReadOnlyList<SkillProblem> Problems,
    SkillRoots Roots,
    Func<Skill, int?>? Version = null);

/// <summary>
/// The words for <c>/skills</c>: the pane's Offered tab — the catalog, what is shadowed and what was
/// skipped (the Roots tab, the three folders in precedence order, went later on 2026-09-19 at the user's
/// call — the scope page names each root's folder; the Project tab, the <c>Project file</c> toggle, on
/// 2026-10-01, the toggle an Options row since) — the same as plain lines for a console without the pane;
/// the Reflection and Options tabs after it (2026-09-19, the settings rows that were <c>/settings</c>' Skills tab)
/// are <see cref="SettingsMenu"/>'s — <see cref="SettingsMenu.SkillsTabFields"/> — and only their
/// titles live here. Pure statics, every string pinned; the <see cref="AboutText"/> shape. Every
/// user-written or path string is escaped: a description is the skill author's.
///
/// <para>Since 2026-09-18 the pane is a <see cref="MenuPane"/> (<see cref="SkillsMenu"/>): each tab's
/// content is a list of markup rows — <see cref="LoadedRows"/> carries the <see cref="Skill"/> a row
/// stands for, so Enter on it opens the scope picker — cut at the edge by the menu, never wrapped;
/// the blank separators of <see cref="LoadedLines"/> are left out, a menu row being a cursor stop.
/// The plain lines are uncut: the console wraps them.</para>
/// </summary>
public static class SkillsText
{
    /// <summary>The pane's strip label: the toolbar's glyph, then the name (the glyph since later on 2026-09-21).</summary>
    public const string Label = ChatScreen.SkillsToolGlyph + " Skills";

    /// <summary>The first tab: <c>Offered</c> since 2026-09-19 (the user's call, the same word as <c>/tools</c>' first tab; <c>Loaded</c> before), not the pane's own word again.</summary>
    public const string OfferedTabTitle = "Offered";

    /// <summary>The last tab (2026-09-19, the user's ask; second until 2026-09-22): the skill settings, <c>/settings</c>' Skills tab until then (<see cref="SettingsMenu.SkillsTabFields"/>), the <c>Project file</c> toggle among them since 2026-10-01.</summary>
    public const string OptionsTabTitle = "Options";

    /// <summary>The second tab (later on 2026-09-19, the user's ask): the reflection's rows, the Options tab's tail until then (<see cref="SettingsMenu.SkillsTabFields"/>'s second list).</summary>
    public const string ReflectionTabTitle = "Reflection";

    /// <summary>The first line of every tab while the setting is off. Pinned.</summary>
    public const string OffLine = "Agent skills is off (the Options tab of /skills): no skill is listed, no skill tool offered, and the project notes are not read.";

    public const string NoneLine = "(no skills installed: add a folder with a SKILL.md under one of the roots, or ask the model to write one)";
    public const string ShadowedHeading = "Shadowed (a higher root holds the name):";
    public const string ProblemsHeading = "Skipped:";

    /// <summary>
    /// One catalog row: the name padded to the column, the scope padded to nine, the version cell (<see cref="VersionCell"/>, empty
    /// without a version column), the description. Pinned.
    /// </summary>
    public static string SkillLine(Skill skill, int nameWidth, string version = "")
    {
        ArgumentNullException.ThrowIfNull(skill);
        return skill.Name.PadRight(nameWidth) + "  " + SkillScopes.Name(skill.Scope).PadRight(9) + version + skill.Description;
    }

    /// <summary>A skill's version as the Offered tab shows it (2026-10-07, the user's ask): <c>v4</c>; empty for none (an external skill). Pinned.</summary>
    public static string VersionLabel(int? version) =>
        version is { } v ? "v" + v.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";

    /// <summary>The version column's width: the widest label among the listed skills, at least two; 0 without a version source (no column).</summary>
    public static int VersionWidth(SkillsFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        return facts.Version is not { } version ? 0 : Math.Max(2, facts.Skills.Select(s => VersionLabel(version(s)).Length).DefaultIfEmpty(0).Max());
    }

    /// <summary><paramref name="skill"/>'s version cell: its label padded to <paramref name="width"/> and two spaces; empty when there is no column.</summary>
    public static string VersionCell(SkillsFacts facts, Skill skill, int width)
    {
        ArgumentNullException.ThrowIfNull(facts);
        return width == 0 || facts.Version is not { } version ? "" : VersionLabel(version(skill)).PadRight(width) + "  ";
    }

    /// <summary>A shadowed skill's row: the name, its scope, the scope that hides it. Pinned.</summary>
    public static string ShadowedLine(Skill skill, int nameWidth)
    {
        ArgumentNullException.ThrowIfNull(skill);
        return skill.Name.PadRight(nameWidth) + "  " + SkillScopes.Name(skill.Scope).PadRight(9) + "shadowed by the " + SkillScopes.Name(skill.ShadowedBy ?? SkillScope.Profile) + " skills";
    }

    /// <summary>A skipped folder's row: the folder's path and the reason. Pinned.</summary>
    public static string ProblemLine(SkillProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);
        return problem.Directory + ": " + problem.Reason;
    }

    /// <summary>The name column: the longest name among the listed and the shadowed, at least four.</summary>
    public static int NameWidth(SkillsFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        return Math.Max(4, facts.Skills.Concat(facts.Shadowed).Select(s => s.Name.Length).DefaultIfEmpty(0).Max());
    }

    /// <summary>
    /// The Loaded tab as lines: the catalog (with a warning under a skill that has one), then the
    /// shadowed and the skipped as blocks under their headings, a blank line between blocks and none
    /// after the last; <see cref="OffLine"/> or <see cref="NoneLine"/> alone when that is the case.
    /// </summary>
    public static IReadOnlyList<string> LoadedLines(SkillsFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (!facts.Enabled)
        {
            return [OffLine];
        }

        if (facts.Skills.Count == 0)
        {
            return [NoneLine];
        }

        var lines = new List<string>();
        int width = NameWidth(facts);
        int versions = VersionWidth(facts);
        foreach (var skill in facts.Skills)
        {
            lines.Add(SkillLine(skill, width, VersionCell(facts, skill, versions)));
            if (skill.Warning is { } warning)
            {
                lines.Add(new string(' ', width + 2) + "(" + warning + ")");
            }
        }

        if (facts.Shadowed.Count > 0)
        {
            lines.Add("");
            lines.Add(ShadowedHeading);
            foreach (var skill in facts.Shadowed)
            {
                lines.Add("  " + ShadowedLine(skill, width));
            }
        }

        if (facts.Problems.Count > 0)
        {
            lines.Add("");
            lines.Add(ProblemsHeading);
            foreach (var problem in facts.Problems)
            {
                lines.Add("  " + ProblemLine(problem));
            }
        }

        return lines;
    }

    /// <summary>
    /// The Loaded tab as menu rows (2026-09-18): <see cref="LoadedLines"/>' content without its blank
    /// separators, each as markup — a catalog row with the name in the label colour, the scope and
    /// the description escaped after it, the <see cref="Skill"/> it stands for beside it; its warning
    /// dim under it; the two headings in the label colour; a shadowed row dim with its skill beside
    /// it (its scope is pickable too: the duplicate is the thing to clean up); a skipped folder dim;
    /// <see cref="OffLine"/> or <see cref="NoneLine"/> dim and alone. Null beside every row that is
    /// not a skill's. Under a <paramref name="filter"/> (2026-10-03, the user's ask, <see cref="MenuFilter"/>) only the skills
    /// whose name or description holds it, each with its warning, the shadowed ones likewise and the skipped folders whose line
    /// holds it, a heading left out with nothing under it; <see cref="MenuFilter.NoMatchRow"/> alone when none is left.
    /// </summary>
    public static IReadOnlyList<(string Markup, Skill? Skill)> LoadedRows(SkillsFacts facts, string filter = "")
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(filter);
        if (!facts.Enabled)
        {
            return [(Theme.DimMarkup(OffLine), null)];
        }

        if (facts.Skills.Count == 0)
        {
            return [(Theme.DimMarkup(NoneLine), null)];
        }

        var rows = new List<(string, Skill?)>();
        int width = NameWidth(facts);
        int versions = VersionWidth(facts);   // the version column (2026-10-07), sized on the whole list so a filter does not shift it
        foreach (var skill in facts.Skills.Where(s => MenuFilter.Matches(filter, s.Name, s.Description)))
        {
            rows.Add((Styled(Theme.AccentSecondary, skill.Name.PadRight(width)) + Markup.Escape("  " + SkillScopes.Name(skill.Scope).PadRight(9) + VersionCell(facts, skill, versions) + skill.Description), skill));
            if (skill.Warning is { } warning)
            {
                rows.Add((Theme.DimMarkup(new string(' ', width + 2) + "(" + warning + ")"), null));
            }
        }

        var shadowed = facts.Shadowed.Where(s => MenuFilter.Matches(filter, s.Name, s.Description)).ToList();
        if (shadowed.Count > 0)
        {
            rows.Add((Styled(Theme.AccentSecondary, ShadowedHeading), null));
            foreach (var skill in shadowed)
            {
                rows.Add((Theme.DimMarkup("  " + ShadowedLine(skill, width)), skill));
            }
        }

        var problems = facts.Problems.Select(ProblemLine).Where(line => MenuFilter.Matches(filter, line, null)).ToList();
        if (problems.Count > 0)
        {
            rows.Add((Styled(Theme.AccentSecondary, ProblemsHeading), null));
            foreach (var line in problems)
            {
                rows.Add((Theme.DimMarkup("  " + line), null));
            }
        }

        if (rows.Count == 0)
        {
            rows.Add((MenuFilter.NoMatchRow(filter), null));
        }

        return rows;
    }

    private static string Styled(Style style, string text) => $"[{style.ToMarkup()}]{Markup.Escape(text)}[/]";

    /// <summary>The content tab as plain lines, for a console without the pane: its title as a heading, its content indented; <see cref="SkillsMenu.Lines"/> adds the Reflection and Options tabs after it.</summary>
    public static IEnumerable<string> Lines(SkillsFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        yield return OfferedTabTitle;
        foreach (var line in LoadedLines(facts))
        {
            yield return "  " + line;
        }
    }
}
