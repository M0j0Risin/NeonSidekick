using NeonSidekick.Sessions;
using NeonSidekick.Skills;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>
/// What <c>/skills</c> shows, read when it opens: the skills switch, the catalog as of its last scan and the roots (for the scope page). The
/// <c>Project file</c> toggle and the notes on disk, for the Project tab, went with it (2026-10-01). <see cref="Version"/> (2026-10-07)
/// is each skill's version from the records (<c>SkillRecords.Versions</c>), null for an external one; without it, no version column.
/// <see cref="Changed"/> (2026-10-12) is when each last changed (<c>SkillRecords.ChangedAt</c>), shown in <see cref="Zone"/> (UTC without
/// one); without it, no changed column.
/// </summary>
public sealed record SkillsFacts(
    bool Enabled,
    IReadOnlyList<Skill> Skills,
    IReadOnlyList<Skill> Shadowed,
    IReadOnlyList<SkillProblem> Problems,
    SkillRoots Roots,
    Func<Skill, int?>? Version = null,
    Func<Skill, DateTimeOffset?>? Changed = null,
    TimeZoneInfo? Zone = null);

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
    /// without a version column), the changed cell (<see cref="ChangedCell"/>, likewise), the description. Pinned.
    /// </summary>
    public static string SkillLine(Skill skill, int nameWidth, string version = "", string changed = "")
    {
        ArgumentNullException.ThrowIfNull(skill);
        return skill.Name.PadRight(nameWidth) + "  " + SkillScopes.Name(skill.Scope).PadRight(9) + version + changed + skill.Description;
    }

    /// <summary>The changed column's width in cells: <c>yyyy-MM-dd HH:mm</c> (<see cref="SessionText.Moment"/>), the same for every row.</summary>
    public const int ChangedWidth = 16;

    /// <summary>When <paramref name="skill"/> last changed as the Offered tab shows it (2026-10-12, the user's pick: <c>2026-10-12 14:03</c>, <see cref="SessionText.Moment"/>); empty for none or without a source. Pinned.</summary>
    public static string ChangedText(SkillsFacts facts, Skill skill)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(skill);
        return facts.Changed?.Invoke(skill) is { } at ? SessionText.Moment(at, facts.Zone ?? TimeZoneInfo.Utc) : "";
    }

    /// <summary><paramref name="skill"/>'s changed cell (2026-10-12): <see cref="ChangedText"/> padded to <see cref="ChangedWidth"/> and two spaces; empty when there is no column.</summary>
    public static string ChangedCell(SkillsFacts facts, Skill skill)
    {
        ArgumentNullException.ThrowIfNull(facts);
        return facts.Changed is null ? "" : ChangedText(facts, skill).PadRight(ChangedWidth) + "  ";
    }

    /// <summary>A skill's version as the Offered tab shows it (2026-10-07, the user's ask): <c>v4</c>; empty for none (an external skill). Pinned.</summary>
    public static string VersionLabel(int? version) =>
        version is { } v ? "v" + v.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";

    /// <summary>
    /// What the version cell says for <paramref name="skill"/> (2026-10-07, the user's pick): its <see cref="VersionLabel"/>, led by the
    /// lock (<c>🔒 v4</c>) when it is locked, and by the lock's blank cells (<c>   v1</c>) when another listed skill is, so the numbers
    /// stay in one column; with none locked, the label alone. Empty without a version source. Pinned.
    /// <para>A disabled skill (2026-10-09, the user's pick) leads with <see cref="SkillRecordText.DisabledGlyph"/> in the same place, over
    /// the lock when it is both (the footer says both); the column's lead is there while any listed skill is either. A space between the
    /// glyph and the number since later that day (the user's ask: <c>⛔ v1</c>, not <c>⛔v1</c>).</para>
    /// </summary>
    public static string VersionText(SkillsFacts facts, Skill skill)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(skill);
        if (facts.Version is not { } version)
        {
            return "";
        }

        string lead = !facts.Skills.Any(s => s.Locked || s.Disabled) ? ""
            : skill.Disabled ? SkillRecordText.DisabledGlyph + " "
            : skill.Locked ? SkillRecordText.LockGlyph + " "
            : new string(' ', TextCells.Width(SkillRecordText.LockGlyph) + 1);
        return lead + VersionLabel(version(skill));
    }

    /// <summary>The version column's width in cells: the widest cell among the listed skills, at least two; 0 without a version source (no column).</summary>
    public static int VersionWidth(SkillsFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        return facts.Version is null ? 0 : Math.Max(2, facts.Skills.Select(s => TextCells.Width(VersionText(facts, s))).DefaultIfEmpty(0).Max());
    }

    /// <summary><paramref name="skill"/>'s version cell: <see cref="VersionText"/> padded to <paramref name="width"/> cells and two spaces; empty when there is no column.</summary>
    public static string VersionCell(SkillsFacts facts, Skill skill, int width)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (width == 0 || facts.Version is null)
        {
            return "";
        }

        string text = VersionText(facts, skill);
        return text + new string(' ', Math.Max(0, width - TextCells.Width(text))) + "  ";
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
            lines.Add(SkillLine(skill, width, VersionCell(facts, skill, versions), ChangedCell(facts, skill)));
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

    /// <summary>The catalog's heading on the Offered tab (2026-10-10, the user's ask: the <c>/settings</c> look). Pinned.</summary>
    public const string CatalogTitle = "Skills";

    /// <summary>The shadowed skills' heading on the Offered tab (2026-10-10), <see cref="ShadowedNote"/> on its rule. Pinned.</summary>
    public const string ShadowedTitle = "Shadowed";

    /// <summary>What the shadowed skills' rule says after its count (2026-10-10; <see cref="ShadowedHeading"/>'s brackets until then). Pinned.</summary>
    public const string ShadowedNote = "a higher root holds the name";

    /// <summary>The skipped folders' heading on the Offered tab (2026-10-10). Pinned.</summary>
    public const string ProblemsTitle = "Skipped";

    /// <summary>
    /// The Loaded tab as menu rows (2026-09-18): <see cref="LoadedLines"/>' content, each as markup — a catalog row with the name in
    /// the label colour, the scope and the description escaped after it, the <see cref="Skill"/> it stands for beside it; its warning
    /// dim under it; a shadowed row dim with its skill beside it (its scope is pickable too: the duplicate is the thing to clean up);
    /// a skipped folder dim; <see cref="OffLine"/> or <see cref="NoneLine"/> dim and alone. Null beside every row that is not a skill's.
    /// Under a <paramref name="filter"/> (2026-10-03, the user's ask, <see cref="MenuFilter"/>) only the skills whose name or description
    /// holds it, each with its warning, the shadowed ones likewise and the skipped folders whose line holds it, a heading left out with
    /// nothing under it; <see cref="MenuFilter.NoMatchRow"/> alone when none is left.
    /// </summary>
    /// <remarks>
    /// <paramref name="scope"/> (2026-10-10, the user's ask: the Offered tab's sub-tabs, <see cref="ScopeTabs"/>) keeps the skills and the
    /// shadowed ones of that root alone, and the skipped folders under it (<see cref="ProblemScope"/>); a scope holding none says
    /// <see cref="ScopeNoneLine"/>. Null is every root, as it was. The columns stay the whole catalog's either way.
    /// Later on 2026-10-10 (the user's ask: the <c>/settings</c> look) each block sits under a <see cref="SectionRule"/> —
    /// <see cref="CatalogTitle"/>, <see cref="ShadowedTitle"/>, <see cref="ProblemsTitle"/>, each with the count its scope holds,
    /// whatever the filter leaves — an empty row before every one but the first (the <c>/tools</c> Offered shape), the rows under them
    /// no longer indented; <c>Heading</c> true beside a heading alone (<see cref="HeadingRows"/>, the pane's <see cref="MenuTab.Headings"/>).
    /// </remarks>
    public static IReadOnlyList<(string Markup, Skill? Skill, bool Heading)> LoadedRows(SkillsFacts facts, string filter = "", SkillScope? scope = null)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(filter);
        if (!facts.Enabled)
        {
            return [(Theme.DimMarkup(OffLine), null, false)];
        }

        if (facts.Skills.Count == 0)
        {
            return [(Theme.DimMarkup(NoneLine), null, false)];
        }

        var rows = new List<(string, Skill?, bool)>();
        int width = NameWidth(facts);
        int versions = VersionWidth(facts);   // the version column (2026-10-07), sized on the whole list so a filter does not shift it
        var listed = facts.Skills.Where(s => InScope(s, scope) && MenuFilter.Matches(filter, s.Name, s.Description)).ToList();
        if (listed.Count > 0)
        {
            Heading(CatalogTitle, facts.Skills.Count(s => InScope(s, scope)), null);
            foreach (var skill in listed)
            {
                // A disabled skill's row is dim whole, its name too (2026-10-09, the user's ask: the shadowed rows' look).
                string rest = "  " + SkillScopes.Name(skill.Scope).PadRight(9) + VersionCell(facts, skill, versions) + ChangedCell(facts, skill) + skill.Description;
                rows.Add((skill.Disabled ? Theme.DimMarkup(skill.Name.PadRight(width) + rest) : Styled(Theme.AccentSecondary, skill.Name.PadRight(width)) + Markup.Escape(rest), skill, false));
                if (skill.Warning is { } warning)
                {
                    rows.Add((Theme.DimMarkup(new string(' ', width + 2) + "(" + warning + ")"), null, false));
                }
            }
        }

        var shadowed = facts.Shadowed.Where(s => InScope(s, scope) && MenuFilter.Matches(filter, s.Name, s.Description)).ToList();
        if (shadowed.Count > 0)
        {
            Heading(ShadowedTitle, facts.Shadowed.Count(s => InScope(s, scope)), ShadowedNote);
            foreach (var skill in shadowed)
            {
                rows.Add((Theme.DimMarkup(ShadowedLine(skill, width)), skill, false));
            }
        }

        var inScope = facts.Problems.Where(p => scope is null || ProblemScope(facts.Roots, p) == scope).Select(ProblemLine).ToList();
        var problems = inScope.Where(line => MenuFilter.Matches(filter, line, null)).ToList();
        if (problems.Count > 0)
        {
            Heading(ProblemsTitle, inScope.Count, null);
            foreach (var line in problems)
            {
                rows.Add((Theme.DimMarkup(line), null, false));
            }
        }

        if (rows.Count == 0)
        {
            rows.Add(filter.Length == 0 && scope is { } empty ? (Theme.DimMarkup(ScopeNoneLine(empty)), null, false) : (MenuFilter.NoMatchRow(filter), null, false));
        }

        return rows;

        void Heading(string title, int count, string? note)
        {
            if (rows.Count > 0)
            {
                rows.Add(("", null, false));
            }

            rows.Add((SectionRule.Markup(title, count.ToString(System.Globalization.CultureInfo.InvariantCulture), note), null, true));
        }
    }

    /// <summary>The indices of the heading rows of <paramref name="rows"/>: the pane's <see cref="MenuTab.Headings"/> (2026-10-10).</summary>
    public static IReadOnlySet<int> HeadingRows(IReadOnlyList<(string Markup, Skill? Skill, bool Heading)> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return Enumerable.Range(0, rows.Count).Where(i => rows[i].Heading).ToHashSet();
    }

    /// <summary>The Offered tab's first sub-tab (2026-10-10, the user's ask): every root's skills, the list as it was. Pinned.</summary>
    public const string AllSubTitle = "all";

    /// <summary>
    /// The Offered tab's sub-tabs (2026-10-10, the user's ask: a filter for the profile's skills or the global ones), null standing for
    /// <see cref="AllSubTitle"/>: all, profile and global always, external after them while a listed or shadowed skill is one; none while
    /// the skills are off or none is installed, the tab then a plain one with no second strip.
    /// </summary>
    public static IReadOnlyList<SkillScope?> ScopeTabs(SkillsFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (!facts.Enabled || facts.Skills.Count == 0)
        {
            return [];
        }

        var tabs = new List<SkillScope?> { null, SkillScope.Profile, SkillScope.Global };
        if (facts.Skills.Concat(facts.Shadowed).Any(s => s.Scope == SkillScope.External))
        {
            tabs.Add(SkillScope.External);
        }

        return tabs;
    }

    /// <summary>A scope sub-tab's title (2026-10-10): the scope's word (<see cref="SkillScopes.Name"/>), <see cref="AllSubTitle"/> for null. Pinned.</summary>
    public static string ScopeTitle(SkillScope? scope) => scope is { } s ? SkillScopes.Name(s) : AllSubTitle;

    /// <summary>A scope sub-tab's count in the strip (2026-10-10): the skills it lists, the shadowed ones among them, in superscript (<c>profile³</c>); null for none.</summary>
    public static string? ScopeBadge(SkillsFacts facts, SkillScope? scope)
    {
        ArgumentNullException.ThrowIfNull(facts);
        return SettingsMenu.ChangedBadge(facts.Skills.Concat(facts.Shadowed).Count(s => InScope(s, scope)));
    }

    /// <summary>What a scope sub-tab says with no skill of that root (2026-10-10). Pinned.</summary>
    public static string ScopeNoneLine(SkillScope scope) => $"(no {SkillScopes.Name(scope)} skills)";

    /// <summary>
    /// The root a skipped folder lies under (2026-10-10, the scope sub-tabs): the profile's, the global or the external one by its path,
    /// case aside; null for a folder under none. Pure.
    /// </summary>
    public static SkillScope? ProblemScope(SkillRoots roots, SkillProblem problem)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(problem);
        foreach (var scope in new[] { SkillScope.Profile, SkillScope.Global, SkillScope.External })
        {
            string root = roots.Of(scope).TrimEnd('\\', '/');
            string dir = problem.Directory;
            if (root.Length > 0 && dir.Length > root.Length && dir.StartsWith(root, StringComparison.OrdinalIgnoreCase) && dir[root.Length] is '\\' or '/')
            {
                return scope;
            }
        }

        return null;
    }

    private static bool InScope(Skill skill, SkillScope? scope) => scope is null || skill.Scope == scope;

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
