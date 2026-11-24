using NeonSidekick.Llm;
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
/// <para>The repo's skills (2026-11-23, feature idea #16): <see cref="Pending"/> the project skills listed but not offered (<see cref="SkillCatalog.ProjectPending"/>),
/// <see cref="Folders"/> the walk's folders holding skills with their trust, <see cref="WorkingFolder"/> and <see cref="RepoRoot"/> for the folder labels
/// (<see cref="SkillsText.FolderLabel"/>). All null while <c>Project skills</c> is off.</para>
/// <para><see cref="ProjectTarget"/> (2026-11-23, the user's ask: a <c>project</c> row on a skill's scope page) is the level a profile or global skill
/// moves into, the working folder (its <c>.agents\skills</c>): null while project skills are off, when the working folder isn't the walk's first
/// level (a home), or when the guard refuses a write there; the row is hidden then.</para>
/// </summary>
public sealed record SkillsFacts(
    bool Enabled,
    IReadOnlyList<Skill> Skills,
    IReadOnlyList<Skill> Shadowed,
    IReadOnlyList<SkillProblem> Problems,
    SkillRoots Roots,
    Func<Skill, int?>? Version = null,
    Func<Skill, DateTimeOffset?>? Changed = null,
    TimeZoneInfo? Zone = null,
    IReadOnlyList<ProjectPendingSkill>? Pending = null,
    IReadOnlyList<ProjectFolder>? Folders = null,
    string? WorkingFolder = null,
    string? RepoRoot = null,
    string? ProjectTarget = null)
{
    /// <summary>The project skills not offered, each with its reason; empty for none.</summary>
    public IReadOnlyList<ProjectPendingSkill> ProjectPending => Pending ?? [];

    /// <summary>The walk's folders that hold skills, nearest first; empty for none.</summary>
    public IReadOnlyList<ProjectFolder> ProjectFolders => Folders ?? [];
}

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
        // A repo's unbounded name never widens the column (2026-11-23): it's shown cut (ShownName) and left out of the measure.
        return Math.Max(4, facts.Skills.Concat(facts.Shadowed).Concat(facts.ProjectPending.Select(p => p.Skill))
            .Where(s => SkillCatalog.IsBoundedName(s.Name)).Select(s => s.Name.Length).DefaultIfEmpty(0).Max());
    }

    /// <summary>A skill's name as a row shows it: a project skill's over 64 characters or with an unprintable character (control, format, separator) cut to 64 printable ones and <c>…</c>.</summary>
    public static string ShownName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (SkillCatalog.IsBoundedName(name))
        {
            return name;
        }

        string printable = new(name.Where(c => !SkillCatalog.IsUnprintable(c)).Take(SkillFrontmatter.MaxNameLength).ToArray());
        return printable + "…";
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

        if (facts.Skills.Count == 0 && facts.ProjectPending.Count == 0)
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

        if (facts.ProjectPending.Count > 0)
        {
            // The repo's skills not offered (2026-11-23): trusted only on the pane, so a console without one just lists them.
            lines.Add("");
            lines.Add(ProjectPendingHeading);
            foreach (var pending in facts.ProjectPending)
            {
                lines.Add("  " + ShownName(pending.Skill.Name).PadRight(width) + "  " + SkillText.PendingReason(pending.Reason));
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

    /// <summary>The plain lines' heading over the project skills not offered (2026-11-23). Pinned.</summary>
    public const string ProjectPendingHeading = "Project skills not offered (/skills › project at the PC):";

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

        if (facts.Skills.Count == 0 && facts.ProjectPending.Count == 0 && facts.ProjectFolders.Count == 0)
        {
            return [(Theme.DimMarkup(NoneLine), null, false)];
        }

        if (scope == SkillScope.Project)
        {
            // The repo's skills (2026-11-23): grouped by folder, with the trust and untrust rows (ProjectRows); the pane resolves Enter through them.
            return ProjectRows(facts, filter).Select(r => (r.Markup, r.Skill, r.Kind == ProjectRowKind.Heading)).ToList();
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

        var inScope = facts.Problems.Where(p => scope is null || ProblemScope(facts.Roots, p, facts.ProjectFolders) == scope).Select(ProblemLine).ToList();
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
            // Only the repo's untrusted skills (2026-11-23): the all tab says none is installed; they're on the project tab.
            rows.Add(filter.Length > 0 ? (MenuFilter.NoMatchRow(filter), null, false)
                : scope is { } empty ? (Theme.DimMarkup(ScopeNoneLine(empty)), null, false)
                : (Theme.DimMarkup(NoneLine), null, false));
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

    /// <summary>
    /// Whether <paramref name="rows"/> are one line standing in for a list (2026-11-07, <see cref="MenuTab.Placeholder"/>): skills off, none,
    /// none in the scope, none loaded, or the filter's no match. Real rows always come under a heading, so a lone row naming no skill is one.
    /// </summary>
    public static bool IsPlaceholder(IReadOnlyList<(string Markup, Skill? Skill, bool Heading)> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return rows is [(_, null, false)];
    }

    /// <summary>The Offered tab's first sub-tab (2026-10-10, the user's ask): every root's skills, the list as it was. Pinned.</summary>
    public const string AllSubTitle = "all";

    /// <summary>
    /// The Offered tab's sub-tabs (2026-10-10, the user's ask: a filter for the profile's skills or the global ones), null standing for
    /// <see cref="AllSubTitle"/>: all and profile always, global while the profile reads it (2026-10-13, <c>Global skills</c>), external
    /// after them while a listed or shadowed skill is one; none while the skills are off or none is installed, the tab then a plain one
    /// with no second strip.
    /// </summary>
    public static IReadOnlyList<SkillScope?> ScopeTabs(SkillsFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        // A repo whose only skills are untrusted (2026-11-23) still gets the strip: the project sub-tab is where they're trusted.
        if (!facts.Enabled || (facts.Skills.Count == 0 && facts.ProjectPending.Count == 0 && facts.ProjectFolders.Count == 0))
        {
            return [];
        }

        // No global sub-tab while the profile has its global root off (2026-10-13, Global skills): nothing of it is listed.
        var tabs = new List<SkillScope?> { null, SkillScope.Profile };
        if (facts.Roots.GlobalOn)
        {
            tabs.Add(SkillScope.Global);
        }

        if (facts.Skills.Concat(facts.Shadowed).Any(s => s.Scope == SkillScope.External))
        {
            tabs.Add(SkillScope.External);
        }

        // The repo's skills (2026-11-23, feature idea #16): after external while any folder in the walk holds some, trusted or not.
        if (facts.ProjectFolders.Count > 0 || facts.ProjectPending.Count > 0 || facts.Skills.Concat(facts.Shadowed).Any(s => s.Scope == SkillScope.Project))
        {
            tabs.Add(SkillScope.Project);
        }

        return tabs;
    }

    /// <summary>A scope sub-tab's title (2026-10-10): the scope's word (<see cref="SkillScopes.Name"/>), <see cref="AllSubTitle"/> for null. Pinned.</summary>
    public static string ScopeTitle(SkillScope? scope) => scope is { } s ? SkillScopes.Name(s) : AllSubTitle;

    /// <summary>A scope sub-tab's count in the strip (2026-10-10): the skills it lists, the shadowed ones among them, in superscript (<c>profile³</c>); null for none.</summary>
    public static string? ScopeBadge(SkillsFacts facts, SkillScope? scope)
    {
        ArgumentNullException.ThrowIfNull(facts);
        return SettingsMenu.ChangedBadge(ScopeCount(facts, scope));
    }

    /// <summary>The skills a scope sub-tab lists: its offered and shadowed ones, and on the project tab those not offered too (2026-11-23).</summary>
    public static int ScopeCount(SkillsFacts facts, SkillScope? scope)
    {
        ArgumentNullException.ThrowIfNull(facts);
        int count = facts.Skills.Concat(facts.Shadowed).Count(s => InScope(s, scope));
        return scope == SkillScope.Project ? count + facts.ProjectPending.Count : count;
    }

    /// <summary>What a scope sub-tab says with no skill of that root (2026-10-10). Pinned.</summary>
    public static string ScopeNoneLine(SkillScope scope) => $"(no {SkillScopes.Name(scope)} skills)";

    /// <summary>The Offered tab's last sub-tab (2026-10-12, the user's ask): the skills loaded into this session's conversation now. Pinned.</summary>
    public const string LoadedSubTitle = "loaded";

    /// <summary>The loaded sub-tab's heading (2026-10-12), over its rows as <see cref="CatalogTitle"/> is over the catalog's. Pinned.</summary>
    public const string LoadedTitle = "Loaded in this session";

    /// <summary>What the loaded sub-tab says with none loaded (2026-10-12, the user's ask: the tab stays and says so). Pinned.</summary>
    public const string NoneLoadedLine = "(no skills loaded in this session)";

    /// <summary>The note on a loaded skill the catalog no longer lists (2026-10-12): renamed, deleted or switched off since its load. Pinned.</summary>
    public const string NotInCatalogNote = "(not in the catalog now)";

    /// <summary>A loaded skill's count after its row when the history holds more than one of its loads (2026-10-12): <c>×2</c>. Pinned.</summary>
    public static string LoadsNote(int loads) => "×" + loads.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The loaded sub-tab's count in the strip (2026-10-12): the skills loaded, in superscript (<c>loaded²</c>); null for none.</summary>
    public static string? LoadedBadge(IReadOnlyList<LoadedSkill> loaded)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        return SettingsMenu.ChangedBadge(loaded.Count);
    }

    /// <summary>
    /// The loaded sub-tab as menu rows (2026-10-12, the user's ask): <see cref="LoadedTitle"/> with the count, then each skill loaded into the
    /// conversation now (<see cref="ConversationHistory.LoadedSkills"/>), in the order of its first load, drawn as the catalog draws it — the
    /// name, the scope, the version, the description — with <see cref="LoadsNote"/> dim after it for a skill loaded more than once, the
    /// <see cref="Skill"/> beside it so Enter opens its scope page as on any Offered row. A name the catalog no longer lists is a dim row with
    /// <see cref="NotInCatalogNote"/> and no skill. None loaded is <see cref="NoneLoadedLine"/> alone; the <paramref name="filter"/> keeps the
    /// skills whose name or description holds it, <see cref="MenuFilter.NoMatchRow"/> alone when none is left.
    /// </summary>
    public static IReadOnlyList<(string Markup, Skill? Skill, bool Heading)> LoadedSkillRows(SkillsFacts facts, IReadOnlyList<LoadedSkill> loaded, string filter = "")
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(loaded);
        ArgumentNullException.ThrowIfNull(filter);
        if (loaded.Count == 0)
        {
            return [(Theme.DimMarkup(NoneLoadedLine), null, false)];
        }

        int width = Math.Max(NameWidth(facts), loaded.Max(l => l.Name.Length));
        int versions = VersionWidth(facts);
        var rows = new List<(string, Skill?, bool)>
        {
            (SectionRule.Markup(LoadedTitle, loaded.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), null), null, true),
        };
        foreach (var entry in loaded)
        {
            var skill = facts.Skills.Concat(facts.Shadowed).FirstOrDefault(s => string.Equals(s.Name, entry.Name, StringComparison.OrdinalIgnoreCase));
            var pending = skill is null ? facts.ProjectPending.FirstOrDefault(p => string.Equals(p.Skill.Name, entry.Name, StringComparison.OrdinalIgnoreCase)) : null;
            if (!MenuFilter.Matches(filter, entry.Name, skill?.Description ?? pending?.Skill.Description))
            {
                continue;
            }

            string count = entry.Loads > 1 ? " " + Theme.DimMarkup(LoadsNote(entry.Loads)) : "";
            if (pending is not null)
            {
                // A repo's skill loaded, then untrusted or changed (2026-11-23): it stays in the conversation; the row says why it isn't offered now.
                rows.Add((Theme.DimMarkup(ShownName(entry.Name).PadRight(width) + "  " + ProjectNotOfferedNote(pending.Reason)) + count, null, false));
                continue;
            }

            if (skill is null)
            {
                rows.Add((Theme.DimMarkup(entry.Name.PadRight(width) + "  " + NotInCatalogNote) + count, null, false));
                continue;
            }

            string rest = "  " + SkillScopes.Name(skill.Scope).PadRight(9) + VersionCell(facts, skill, versions) + ChangedCell(facts, skill) + skill.Description;
            rows.Add(((skill.Disabled ? Theme.DimMarkup(skill.Name.PadRight(width) + rest) : Styled(Theme.AccentSecondary, skill.Name.PadRight(width)) + Markup.Escape(rest)) + count, skill, false));
        }

        if (rows.Count == 1)
        {
            rows.Add((MenuFilter.NoMatchRow(filter), null, false));
        }

        return rows;
    }

    /// <summary>The loaded skills as plain lines (2026-10-12), for a console without the pane: their names and counts, or <see cref="NoneLoadedLine"/>.</summary>
    public static IEnumerable<string> LoadedSkillLines(IReadOnlyList<LoadedSkill> loaded)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        if (loaded.Count == 0)
        {
            yield return NoneLoadedLine;
            yield break;
        }

        foreach (var entry in loaded)
        {
            yield return entry.Loads > 1 ? entry.Name + "  " + LoadsNote(entry.Loads) : entry.Name;
        }
    }

    /// <summary>
    /// The root a skipped folder lies under (2026-10-10, the scope sub-tabs): the profile's, the global or the external one by its path,
    /// case aside; null for a folder under none. Pure.
    /// </summary>
    public static SkillScope? ProblemScope(SkillRoots roots, SkillProblem problem, IReadOnlyList<ProjectFolder>? projectFolders = null)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(problem);
        foreach (var scope in new[] { SkillScope.Profile, SkillScope.Global, SkillScope.External })
        {
            if (Under(roots.Of(scope), problem.Directory))
            {
                return scope;
            }
        }

        // A repo's broken skill (2026-11-23): on the project sub-tab, under its folder's heading, trusted or not.
        return ProjectFolderOf(problem, projectFolders ?? []) is not null ? SkillScope.Project : null;
    }

    /// <summary>The project folder whose <c>.agents/skills</c> or <c>.claude/skills</c> holds <paramref name="problem"/>'s folder, or null.</summary>
    public static ProjectFolder? ProjectFolderOf(SkillProblem problem, IReadOnlyList<ProjectFolder> folders)
    {
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentNullException.ThrowIfNull(folders);
        return folders.FirstOrDefault(f => f.SkillsFolders.Any(root => Under(root, problem.Directory)));
    }

    // Whether dir lies inside root, case aside: the roots' own spelling rule, a separator after the root.
    private static bool Under(string root, string dir)
    {
        root = root.TrimEnd('\\', '/');
        return root.Length > 0 && dir.Length > root.Length && dir.StartsWith(root, StringComparison.OrdinalIgnoreCase) && dir[root.Length] is '\\' or '/';
    }

    // ── Project skills (2026-11-23, feature idea #16) ──────────────────────
    // Every folder label, skill name, description and problem here is the repo's: escaped into markup, never read as it.

    /// <summary>The working folder's label (2026-11-23). Pinned.</summary>
    public const string ThisFolderLabel = "This folder";

    /// <summary>What follows an ancestor's label when it is the repo root. Pinned.</summary>
    public const string RepoRootNote = " (repo root)";

    /// <summary>
    /// A project folder as every trust string names it (2026-11-23): the working folder as <see cref="ThisFolderLabel"/>, an ancestor by its path relative to
    /// the working folder (the system's separator: <c>..\..</c>, <c>../..</c>) with <see cref="RepoRootNote"/> when it is the repo root; the full path with no
    /// working folder. Pure apart from <see cref="Path.GetFullPath(string)"/>.
    /// </summary>
    public static string FolderLabel(string folder, string? workingFolder, string? repoRoot)
    {
        ArgumentNullException.ThrowIfNull(folder);
        string key = ProjectSkillTrust.KeyOf(folder);
        bool root = repoRoot is not null && Same(key, ProjectSkillTrust.KeyOf(repoRoot));
        if (workingFolder is null)
        {
            return key + (root ? RepoRootNote : "");
        }

        string working = ProjectSkillTrust.KeyOf(workingFolder);
        return Same(key, working) ? ThisFolderLabel : Path.GetRelativePath(working, key) + (root ? RepoRootNote : "");

        static bool Same(string a, string b) => string.Equals(a, b, NeonSidekick.Files.CaseRule.Comparison(a));
    }

    /// <summary><paramref name="folder"/>'s label from <paramref name="facts"/>' working folder and repo root.</summary>
    public static string FolderLabel(SkillsFacts facts, string folder)
    {
        ArgumentNullException.ThrowIfNull(facts);
        return FolderLabel(folder, facts.WorkingFolder, facts.RepoRoot);
    }

    /// <summary>A folder's group heading on the project sub-tab: <c>..\.. (repo root)'s skills</c>. Pinned.</summary>
    public static string ProjectFolderTitle(string label) => label + "'s skills";

    /// <summary>The group heading's note for each trust state. Pinned.</summary>
    public static string ProjectTrustNote(ProjectFolderTrust trust) => trust switch
    {
        ProjectFolderTrust.Trusted => "trusted",
        ProjectFolderTrust.Changed => "changed since you trusted them",
        _ => "not trusted",
    };

    /// <summary>The trust row's word, and the untrust row's. Pinned.</summary>
    public const string TrustWord = "trust";
    public const string UntrustWord = "untrust";

    /// <summary>What the trust row says after its word: a first trust, or one asked again after a change. Pinned.</summary>
    public static string TrustRowNote(bool changed) => changed ? "review what changed, then offer them again" : "review these skills, then offer them to the model";

    /// <summary>What the untrust row says after its word. Pinned.</summary>
    public const string UntrustRowNote = "stop offering these skills; trusting them again asks first";

    /// <summary>
    /// The project sub-tab (2026-11-23): a group per folder holding skills, nearest first, under <see cref="ProjectFolderTitle"/> with its trust; in each, a trust
    /// row while it isn't trusted or an untrust row while it is (both under the heading since later that day, the user's call), a bare rule
    /// (<see cref="SectionRule.Bare"/>) when skills follow, then its offered skills, the ones a higher root hides, the ones not offered (dim, with the
    /// reason) and its broken ones. The <paramref name="filter"/> keeps the skills and problems that hold it, a folder's rows with them; <see cref="MenuFilter.NoMatchRow"/>
    /// alone when none is left.
    /// </summary>
    public static IReadOnlyList<ProjectRow> ProjectRows(SkillsFacts facts, string filter = "")
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(filter);
        var rows = new List<ProjectRow>();
        int width = Math.Max(NameWidth(facts), UntrustWord.Length);
        foreach (var folder in facts.ProjectFolders)
        {
            bool In(Skill s) => s.Scope == SkillScope.Project && string.Equals(s.TrustFolder, folder.Level, StringComparison.Ordinal);
            var offered = facts.Skills.Where(In).ToList();
            var shadowed = facts.Shadowed.Where(In).ToList();
            var pending = facts.ProjectPending.Where(p => In(p.Skill)).ToList();
            var problems = facts.Problems.Where(p => ReferenceEquals(ProjectFolderOf(p, [folder]), folder)).ToList();
            var group = new List<ProjectRow>();
            foreach (var skill in offered.Where(s => MenuFilter.Matches(filter, s.Name, s.Description)))
            {
                string rest = "  " + skill.Description;
                group.Add(new ProjectRow(skill.Disabled ? Theme.DimMarkup(skill.Name.PadRight(width) + rest) : Styled(Theme.AccentSecondary, skill.Name.PadRight(width)) + Markup.Escape(rest), ProjectRowKind.Skill, skill, folder));
            }

            foreach (var skill in shadowed.Where(s => MenuFilter.Matches(filter, s.Name, s.Description)))
            {
                group.Add(new ProjectRow(Theme.DimMarkup(ShadowedLine(skill, width)), ProjectRowKind.Skill, skill, folder));
            }

            foreach (var item in pending.Where(p => MenuFilter.Matches(filter, p.Skill.Name, p.Skill.Description)))
            {
                string line = ShownName(item.Skill.Name).PadRight(width) + "  " + SkillText.PendingReason(item.Reason) + (item.Skill.Description.Length > 0 ? "  " + item.Skill.Description : "");
                group.Add(new ProjectRow(Theme.DimMarkup(line), ProjectRowKind.Pending, item.Skill, folder, item.Reason));
            }

            foreach (var problem in problems.Where(p => MenuFilter.Matches(filter, ProblemLine(p), null)))
            {
                group.Add(new ProjectRow(Theme.DimMarkup(ProblemLine(problem)), ProjectRowKind.Text, null, folder));
            }

            if (group.Count == 0 && filter.Length > 0)
            {
                continue;
            }

            if (rows.Count > 0)
            {
                rows.Add(new ProjectRow("", ProjectRowKind.Text));
            }

            string label = FolderLabel(facts, folder.Level);
            int count = offered.Count + shadowed.Count + pending.Count;
            rows.Add(new ProjectRow(SectionRule.Markup(ProjectFolderTitle(label), count.ToString(System.Globalization.CultureInfo.InvariantCulture), ProjectTrustNote(folder.Trust)), ProjectRowKind.Heading, null, folder));
            if (folder.Trust != ProjectFolderTrust.Trusted)
            {
                rows.Add(new ProjectRow(Styled(Theme.AccentSecondary, TrustWord.PadRight(width)) + Markup.Escape("  ") + Theme.DimMarkup(TrustRowNote(folder.Trust == ProjectFolderTrust.Changed)), ProjectRowKind.Trust, null, folder));
            }
            else
            {
                // Under the heading, as trust is (2026-11-23, hand check 1, the user's call): the folder's action always comes first.
                rows.Add(new ProjectRow(Styled(Theme.AccentSecondary, UntrustWord.PadRight(width)) + Markup.Escape("  ") + Theme.DimMarkup(UntrustRowNote), ProjectRowKind.Untrust, null, folder));
            }

            if (group.Count > 0)
            {
                // A bare rule between the action and the skills (2026-11-23, the user's ask): a heading row, never a stop.
                rows.Add(new ProjectRow(SectionRule.Bare, ProjectRowKind.Heading, null, folder));
            }

            rows.AddRange(group);
        }

        if (rows.Count == 0)
        {
            rows.Add(new ProjectRow(filter.Length == 0 ? Theme.DimMarkup(ScopeNoneLine(SkillScope.Project)) : MenuFilter.NoMatchRow(filter), ProjectRowKind.Text));
        }

        return rows;
    }

    /// <summary>The loaded sub-tab's note on a loaded project skill that isn't offered now (2026-11-23): <c>(not trusted now)</c>, or its reason. Pinned.</summary>
    public static string ProjectNotOfferedNote(ProjectPendingReason reason) =>
        "(" + (reason == ProjectPendingReason.NotTrusted ? "not trusted now" : SkillText.PendingReason(reason)) + ")";

    /// <summary>The project sub-tab's keys (2026-11-23; delete later that day, the user's ask). Pinned.</summary>
    public const string ProjectKeys = "Enter = trust, untrust, edit, copy, disable, delete · ←/→ tabs · " + MenuFilter.TypeAndCloseKeys;

    // ── The trust question ──

    /// <summary>The trust question's title: <c>Skills › trust</c> (no folder: a label is the repo's, unbounded; the caption names it). Pinned.</summary>
    public const string TrustTitle = Label + " › " + TrustWord;

    /// <summary>The trust question's keys. Pinned.</summary>
    public const string TrustKeys = "Enter = trust, or view a skill · Esc = cancel";

    /// <summary>The trust question's caption on a first trust (2026-11-23): what trusting does, read before the yes. Pinned.</summary>
    public static string TrustCaption(string label) =>
        $"Trust {label}'s skills? View each one first: once trusted, the model may load them and their files. Fields asking Claude Code for tools or hooks grant nothing here.";

    /// <summary>The trust question's caption for a folder trusted before whose skills changed (2026-11-23): what changed, counted. Pinned.</summary>
    public static string TrustChangedCaption(string label, int added, int changed, int removed) =>
        $"{label}'s skills changed since you trusted them: {added} new, {changed} changed, {removed} removed. View shows each change. Trust them again?";

    /// <summary>The question's view word, the cancel row and the status words. Pinned.</summary>
    public const string ViewWord = "view";
    public const string CancelWord = "cancel";
    public const string NewMark = "new";
    public const string ChangedMark = "changed";
    public const string FilesChangedMark = "files changed";
    public const string RemovedMark = "removed";

    /// <summary>A skill's line in the trust question when its frontmatter asks Claude Code for tools, hooks or a model (2026-11-23). Pinned.</summary>
    public const string ActingMark = "asks Claude Code for tools or hooks; ignored here";

    /// <summary>A skill's bundled-file count in the trust question: <c>3 files</c>, <c>50+ files</c> past the listed 50. Pinned.</summary>
    public static string FilesNote(int count, bool more) =>
        count.ToString(System.Globalization.CultureInfo.InvariantCulture) + (more ? "+" : "") + (count == 1 && !more ? " file" : " files");

    /// <summary>The trust question's trust row: the word, then the folder it trusts. Escaped.</summary>
    public static string TrustQuestionRow(string label) =>
        Markup.Escape(TrustWord.PadRight(9)) + Theme.DimMarkup("offer " + label + "'s skills to the model");

    /// <summary>A skill's row in the trust question: view, its name, its mark (new, changed…), then dim its description, file count and acting mark. Escaped.</summary>
    public static string TrustViewRow(string name, int nameWidth, string mark, string description, string files, bool acting)
    {
        ArgumentNullException.ThrowIfNull(name);
        // The notes ahead of the description (2026-11-23, hand check 1): the pane cuts a row at its width, and a long description pushed
        // the files count and the acting mark off the edge; now the description is what gets cut.
        string tail = string.Join(" · ", new[] { files, acting ? ActingMark : "", description }.Where(p => p.Length > 0));
        return Markup.Escape(ViewWord.PadRight(9) + ShownName(name).PadRight(nameWidth) + "  " + mark.PadRight(FilesChangedMark.Length) + "  ") + Theme.DimMarkup(tail);
    }

    /// <summary>A skill gone since the trust, named in the question (no view: there's nothing left to read). Escaped.</summary>
    public static string TrustRemovedRow(string name, int nameWidth) =>
        Theme.DimMarkup(new string(' ', 9) + ShownName(name).PadRight(nameWidth) + "  " + RemovedMark);

    /// <summary>The trust question's last row.</summary>
    public static string CancelRow => Markup.Escape(CancelWord.PadRight(9)) + Theme.DimMarkup("trust nothing");

    /// <summary>The info pane's label over a skill's <c>SKILL.md</c> or its diff in the trust question. Pinned.</summary>
    public static string ViewLabel(string name) => Label + " › " + ShownName(name);

    /// <summary>A skill the question can't show: no readable <c>SKILL.md</c> within the bound. Pinned.</summary>
    public const string NothingToViewText = "(this skill's SKILL.md couldn't be read; it is listed under Skipped)";

    /// <summary>
    /// Over a <see cref="FilesChangedMark"/> skill's text in the trust question's view (2026-11-23, hand check 1: the view showed the
    /// plain text with no hint of what changed): its SKILL.md is the one trusted, only its bundled files differ.
    /// </summary>
    public const string FilesChangedViewNote = "SKILL.md is the same as when you trusted it; only its bundled files changed";

    /// <summary>Trusted (2026-11-23). Pinned.</summary>
    public static string TrustedNotice(string label, int count) =>
        $"({NoticeGlyphs.Skill}trusted: {label}'s skills, {count.ToString(System.Globalization.CultureInfo.InvariantCulture)} of them)";

    /// <summary>The folder changed between the question's read and the yes (2026-11-23): nothing saved, and the question again on the new content. Pinned.</summary>
    public static string ChangedWhileReviewingNotice(string label) => $"({NoticeGlyphs.Skill}{label}'s skills changed while you were reviewing them; nothing was trusted)";

    /// <summary>A save that failed (the file held by the other copy, a disk error): nothing granted. Pinned.</summary>
    public static string TrustSaveFailedError(string label, string reason) => $"Couldn't save the trust for {label}: {reason}. Nothing changed";

    /// <summary>The untrust's yes/no. Pinned.</summary>
    public static string UntrustPrompt(string label) => $"Stop offering {label}'s skills? Trusting them again asks first.";

    /// <summary>Untrusted, naming the folder's skills this conversation already loaded (they stay in it). Pinned.</summary>
    public static string UntrustedNotice(string label, IReadOnlyList<string> loaded)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        return $"({NoticeGlyphs.Skill}untrusted: {label}'s skills"
            + (loaded.Count > 0 ? $"; already loaded here: {string.Join(", ", loaded)}; /clear or a new session drops it" : "") + ")";
    }

    /// <summary>After a trust, the folder's skills your own hide (2026-11-23). Pinned.</summary>
    public static string ProjectShadowedNotice(string label, IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        return $"({NoticeGlyphs.Skill}{names.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)} of {label}'s skills are hidden by yours: {string.Join(", ", names)})";
    }

    /// <summary>Enter on a skill that isn't offered for a reason trust can't change (over the cap, linked outside, manual only, a bad name). Pinned.</summary>
    public static string ProjectPendingNotice(string name, ProjectPendingReason reason) => $"({NoticeGlyphs.Skill}{ShownName(name)} isn't offered: {SkillText.PendingReason(reason)})";

    // ── A trusted project skill's page ──

    /// <summary>The project page's rows' words. Pinned.</summary>
    public const string CopyToProfileWord = "copy to profile";
    public const string CopyToGlobalWord = "copy to global";

    /// <summary>The project page's caption (2026-11-23; "read only here" went with the delete row later that day, the user's ask). Pinned.</summary>
    public static string ProjectPageCaption(string label) => $"This repo's skill, from {label}: the model never changes it. A copy in your skills is yours to change, and hides the repo's.";

    /// <summary>The project page's edit row: the user's editor, and what a change does. Escaped.</summary>
    public static string ProjectEditRow => Markup.Escape(SkillsMenu.EditWord.PadRight(16)) + Theme.DimMarkup("open its SKILL.md in your editor; a change asks for trust again");

    /// <summary>The project page's copy rows. Escaped.</summary>
    public static string ProjectCopyRow(SkillScope to) =>
        Markup.Escape((to == SkillScope.Global ? CopyToGlobalWord : CopyToProfileWord).PadRight(16)) + Theme.DimMarkup($"a copy in the {SkillScopes.Name(to)} skills to change as yours; the repo's stays");

    /// <summary>The project page's disable row (kept in the profile, never a marker in the repo). Escaped.</summary>
    public static string ProjectDisableRow(bool disabled) => disabled
        ? Markup.Escape(SkillsMenu.EnableWord.PadRight(16)) + Theme.DimMarkup("offer it to the model again")
        : Markup.Escape(SkillsMenu.DisableWord.PadRight(16)) + Theme.DimMarkup("stop offering it; kept in your profile, nothing written to the repo");

    /// <summary>The copy's yes/no. Pinned.</summary>
    public static string ProjectCopyPrompt(string name, SkillScope to) => $"Copy the repo's skill '{name}' into the {SkillScopes.Name(to)} skills? Your copy will hide the repo's.";

    /// <summary>Copied. Pinned.</summary>
    public static string ProjectCopiedNotice(string name, SkillScope to) => $"({NoticeGlyphs.Skill}copied: {name} → {SkillScopes.Name(to)} skills; yours hides the repo's now)";

    /// <summary>A copy refused for a name the editor could never take (2026-11-23). Pinned.</summary>
    public static string ProjectCopyBadNameError(string name) =>
        $"Could not copy skill '{ShownName(name)}': its name isn't a valid skill name here ({SkillFrontmatter.NameRule}); the repo's skill stays offered";

    /// <summary>A copy refused because the root holds a skill of that name already. Pinned.</summary>
    public static string ProjectCopyNameTakenError(string name, SkillScope to) => $"Could not copy skill '{name}': the {SkillScopes.Name(to)} skills already have a skill named '{name}'";

    /// <summary>A project skill changed since its folder was trusted (2026-11-23, review #102): only what you trusted is copied.</summary>
    public static string ProjectCopyChangedError(string name) =>
        $"Could not copy skill '{name}': it changed since you trusted it; review it in /skills › project first";

    /// <summary>
    /// A level's skills folder as the strings name it (2026-11-23): <c>.agents\skills</c>, or <c>.claude\skills</c> for a Claude Code one, in the system's
    /// separator (<c>.agents/skills</c> on a Mac).
    /// </summary>
    public static string ProjectSkillsFolder(bool claude) =>
        Path.Combine(claude ? ProjectSkillWalk.ClaudeFolderName : ProjectSkillWalk.AgentsFolderName, SkillRoots.DirectoryName);

    /// <summary>A project skill's delete row (2026-11-23, the user's ask): last on its page; to the bin, or for good while no bin is in use. Escaped.</summary>
    public static string ProjectDeleteRow(bool binned) =>
        Markup.Escape(SkillsMenu.DeleteWord.PadRight(16))
        + Theme.DimMarkup(binned ? $"remove it from the repo's folder (to the {NeonSidekick.Files.FileText.RecycleBinName})" : "remove it from the repo's folder for good");

    /// <summary>
    /// The delete's yes/no (2026-11-23): to the bin, or for good with the reason a bin can't take it (<paramref name="why"/>); a linked skill's says the
    /// link alone goes. Pinned.
    /// </summary>
    public static string ProjectDeletePrompt(string name, string label, bool claude, bool binned, string? why, bool link = false)
    {
        string where = $"{label}'s {ProjectSkillsFolder(claude)}";
        if (link)
        {
            return $"Remove the link '{name}' from {where}? Only the link goes; what it points at stays.";
        }

        return binned
            ? $"Delete skill '{name}' from {where}, folder and all? It goes to the {NeonSidekick.Files.FileText.RecycleBinName}."
            : $"Delete skill '{name}' from {where}, folder and all? This can't be undone" + (string.IsNullOrEmpty(why) ? "." : $" ({why}).");
    }

    /// <summary>
    /// Deleted (2026-11-23): how (<paramref name="way"/>, the menu's own record: to the bin, for good with <paramref name="why"/>, or for good), or the link
    /// removed; then whether the folder's trust was kept, and a note when this conversation already loaded the skill. Pinned.
    /// </summary>
    public static string ProjectDeletedNotice(string name, string label, NeonSidekick.Files.DeleteWay way, string? why, bool link, bool trusted, bool loaded)
    {
        string done = link ? $"removed the link {name} from {label}'s skills"
            : way == NeonSidekick.Files.DeleteWay.Recycled ? $"moved {name} from {label}'s skills to the {NeonSidekick.Files.FileText.RecycleBinName}"
            : $"deleted {name} from {label}'s skills for good" + (string.IsNullOrEmpty(why) ? "" : $" ({why})");
        return $"({NoticeGlyphs.Skill}{done}"
            + (trusted ? "; the folder's trust kept" : "; review the folder in /skills › project")
            + (loaded ? "; already loaded here; /clear or a new session drops it" : "") + ")";
    }

    /// <summary>A delete that failed (the bin refused, a file held): nothing else changed. Pinned.</summary>
    public static string ProjectDeleteFailedError(string name, string detail) => $"Could not delete skill '{name}': {detail}. Nothing else changed";

    // ── A skill moved into the working folder (2026-11-23, the user's ask) ──

    /// <summary>The scope page's <c>project</c> row: the word padded to nine, the working folder's <c>.agents\skills</c> dim after it. Escaped.</summary>
    public static string ProjectMoveRow(string level)
    {
        ArgumentNullException.ThrowIfNull(level);
        return Markup.Escape(SkillScopes.Name(SkillScope.Project).PadRight(9)) + Theme.DimMarkup(Path.Combine(level, ProjectSkillsFolder(claude: false)));
    }

    /// <summary>The move's yes/no. Pinned.</summary>
    public static string MoveToProjectPrompt(string name, string label) =>
        $"Move skill '{name}' into {label}'s {ProjectSkillsFolder(claude: false)}? It becomes the repo's skill, which the model never changes; its kept versions and records go.";

    /// <summary>The move refused ahead of the question: the level's skills hold the folder's name, or (<paramref name="byName"/>) a skill of that name. Pinned.</summary>
    public static string ProjectExistsError(string name, string folder, string label, bool byName) => byName
        ? $"Could not move skill '{name}': {label}'s skills already have a skill named '{name}'"
        : $"Could not move skill '{name}': {label}'s skills already hold '{folder}'";

    /// <summary>Moved, with where it stands now (<paramref name="state"/>, read from the list after the move). Pinned.</summary>
    public static string MovedToProjectNotice(string name, string label, ProjectMoveState state)
    {
        string tail = state switch
        {
            ProjectMoveState.Offered => "; offered here",
            ProjectMoveState.Disabled => "; trusted, disabled as it was",
            ProjectMoveState.Waiting => "; review it in /skills › project",
            ProjectMoveState.OverCap => $"; past the {ProjectSkillTrust.ProjectSkillCap.ToString(System.Globalization.CultureInfo.InvariantCulture)}-skill cap, not offered",
            ProjectMoveState.Shadowed => "; trusted",
            _ => "",
        };
        return $"({NoticeGlyphs.Skill}moved: {name} → {label}'s {ProjectSkillsFolder(claude: false)}{tail})";
    }

    /// <summary>
    /// The copy landed in the repo but the old folder wouldn't go: both stay, yours hiding the repo's. Says whether the repo's copy is trusted
    /// (2026-11-23, review #92), so you know which copy a later untrust or delete leaves.
    /// </summary>
    public static string MovedButKeptError(string name, SkillScope from, string detail, bool repoTrusted) =>
        $"Copied skill '{name}' into the repo, but could not remove it from the {SkillScopes.Name(from)} skills: {detail}. Both are there; yours hides the repo's"
        + (repoTrusted ? ", which is trusted" : ", which waits for your trust in /skills › project");

    // ── The notices at a turn ──

    /// <summary>A folder in the walk with skills not trusted yet (2026-11-23): once per folder and fingerprint in a run, at a turn; the page's feed shows it too. Pinned.</summary>
    public static string ProjectNotTrustedNotice(string label, int count) =>
        $"({NoticeGlyphs.Skill}{label} has {Skills(count)} not trusted yet: /skills › project at the PC to review)";

    /// <summary>A trusted folder whose skills changed (2026-11-23): not offered until trusted again. Pinned.</summary>
    public static string ProjectChangedNotice(string label) =>
        $"({NoticeGlyphs.Skill}{label}'s skills changed since you trusted them: /skills › project at the PC to review)";

    /// <summary>A trusted folder past the 50 cap. Pinned.</summary>
    public static string ProjectOverCapNotice(string label, int count) =>
        $"({NoticeGlyphs.Skill}{label} has {Skills(count)} past the {ProjectSkillTrust.ProjectSkillCap.ToString(System.Globalization.CultureInfo.InvariantCulture)} offered; /skills › project lists them)";

    /// <summary>Headless's form of <see cref="ProjectNotTrustedNotice"/> (no glyph, no pane to point to). Pinned.</summary>
    public static string HeadlessNotTrustedNotice(string label, int count) =>
        $"{label} has {Skills(count)} not trusted yet; trust them in the app to use them here";

    /// <summary>Headless's form of <see cref="ProjectChangedNotice"/>. Pinned.</summary>
    public static string HeadlessChangedNotice(string label) =>
        $"{label}'s skills changed since you trusted them; trust them again in the app to use them here";

    private static string Skills(int count) => count.ToString(System.Globalization.CultureInfo.InvariantCulture) + (count == 1 ? " skill" : " skills");

    /// <summary>
    /// The trust notices for the walk's folders (2026-11-23): one per folder not trusted yet (counting its untrusted skills), changed since its trust, or past
    /// the cap, each once per folder and fingerprint: <paramref name="noted"/> keeps what was said. <paramref name="headless"/> takes headless's forms. Pure
    /// apart from <paramref name="noted"/>.
    /// </summary>
    public static IReadOnlyList<string> ProjectTrustNotices(IReadOnlyList<ProjectFolder> folders, IReadOnlyList<ProjectPendingSkill> pending, string? workingFolder, string? repoRoot, ISet<string> noted, bool headless = false)
    {
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(pending);
        ArgumentNullException.ThrowIfNull(noted);
        var lines = new List<string>();
        foreach (var folder in folders)
        {
            string label = FolderLabel(folder.Level, workingFolder, repoRoot);
            var mine = pending.Where(p => string.Equals(p.Skill.TrustFolder, folder.Level, StringComparison.Ordinal)).ToList();
            if (folder.Trust == ProjectFolderTrust.Trusted)
            {
                int over = mine.Count(p => p.Reason is ProjectPendingReason.OverCap or ProjectPendingReason.PastFolderCap);
                if (over > 0 && !headless && noted.Add("cap\0" + folder.Level + "\0" + folder.Fingerprint))
                {
                    lines.Add(ProjectOverCapNotice(label, over));
                }

                continue;
            }

            if (!noted.Add(folder.Level + "\0" + folder.Fingerprint))
            {
                continue;
            }

            if (folder.Trust == ProjectFolderTrust.Changed)
            {
                lines.Add(headless ? HeadlessChangedNotice(label) : ProjectChangedNotice(label));
                continue;
            }

            // Nothing a trust would offer (a manual-only or unparseable skill alone, 2026-11-23, review #56): no notice.
            int count = mine.Count(p => p.Reason == ProjectPendingReason.NotTrusted);
            if (count == 0)
            {
                continue;
            }

            lines.Add(headless ? HeadlessNotTrustedNotice(label, count) : ProjectNotTrustedNotice(label, count));
        }

        return lines;
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

/// <summary>What a row of the <c>/skills</c> project sub-tab stands for (2026-11-23, feature idea #16).</summary>
public enum ProjectRowKind
{
    /// <summary>A gap, a problem, the none line: Enter does nothing.</summary>
    Text,

    /// <summary>A folder's group heading: never a stop.</summary>
    Heading,

    /// <summary>A trusted skill, offered, disabled or shadowed: Enter opens its page.</summary>
    Skill,

    /// <summary>A skill not offered: Enter on one whose folder isn't trusted asks to trust it; otherwise says why.</summary>
    Pending,

    /// <summary>A folder's trust row: Enter asks to trust it.</summary>
    Trust,

    /// <summary>A trusted folder's untrust row.</summary>
    Untrust,
}

/// <summary>Where a skill moved into the working folder stands after the move (2026-11-23), as <see cref="SkillsText.MovedToProjectNotice"/> says it.</summary>
public enum ProjectMoveState
{
    /// <summary>Not in the list (project skills off, or a broken <c>SKILL.md</c>): no tail.</summary>
    Unlisted,

    /// <summary>Trusted and offered.</summary>
    Offered,

    /// <summary>Trusted, and disabled as it was before the move.</summary>
    Disabled,

    /// <summary>The folder waits for a trust.</summary>
    Waiting,

    /// <summary>Past the 50-skill cap.</summary>
    OverCap,

    /// <summary>Trusted, but a skill of yours of the same name hides it.</summary>
    Shadowed,
}

/// <summary>A project sub-tab row (2026-11-23): its markup, what it stands for, the skill and folder it acts on, a pending skill's reason.</summary>
public sealed record ProjectRow(string Markup, ProjectRowKind Kind, Skill? Skill = null, ProjectFolder? Folder = null, ProjectPendingReason? Reason = null);
