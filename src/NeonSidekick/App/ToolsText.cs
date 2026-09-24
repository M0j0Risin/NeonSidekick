using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>
/// What <c>/tools</c>' Offered tab shows, read when it opens and again after every flip: the groups
/// as <c>/sys</c> lists them (<see cref="SystemPromptSummary.ToolGroups"/>, over the whole web
/// list and with <c>load_skill</c> noted when no skill is installed), the setting <c>LLM offer tools</c>,
/// and the names switched off on the tab (<c>ToolsDisabled</c>, as a set).
/// </summary>
public sealed record ToolsFacts(IReadOnlyList<ToolGroup> Groups, bool ToolsEnabled, IReadOnlySet<string> Disabled);

/// <summary>
/// The words for <c>/tools</c> (2026-09-19, the user's ask): the pane's first tab — every tool the app
/// has, under its group's heading, with <c>on</c> or <c>off</c> beside it — as menu rows and as plain
/// lines for a console without the pane; the four settings tabs after it (Options, Ask, Files, Web) are
/// <see cref="SettingsMenu"/>'s own rows (<see cref="SettingsMenu.ToolsTabFields"/>). The
/// <see cref="SkillsText"/> shape: pure statics, every string pinned; <see cref="OfferedRows"/> carries
/// the tool a row stands for, so Enter or Space on it flips the tool. The saved list is edited by
/// <see cref="Flip"/> alone and read through <see cref="DisabledSet"/>.
/// </summary>
public static class ToolsText
{
    /// <summary>The pane's strip label: the toolbar's glyph, then the name (the glyph since later on 2026-09-21).</summary>
    public const string Label = ChatScreen.ToolsToolGlyph + " Tools";

    /// <summary>The first tab: <c>Offered</c> (the same word heads the Skills pane's catalog since 2026-09-19, the user's call).</summary>
    public const string OfferedTabTitle = "Offered";

    /// <summary>The last tab (second from later on 2026-09-19, the user's ask, until 2026-09-22, the user's ask again): the pane's own settings — the <c>$</c>-mention switch and the two folds — in the <c>/skills</c> Options tab's shape (<see cref="SettingsMenu.ToolsTabFields"/>' last list).</summary>
    public const string OptionsTabTitle = "Options";
    public const string AskTabTitle = "Ask";
    public const string FilesTabTitle = "Files";
    /// <summary><c>Git</c> until 2026-09-21, when the user asked for <c>Git (native)</c>: the in-process LibGit2Sharp tools as against git through the shell.</summary>
    public const string GitTabTitle = "Git (native)";
    public const string ShellTabTitle = "Shell";
    public const string WebTabTitle = "Web";

    /// <summary>The vault tools' tab and group (2026-09-22), the last but one in the strip, before Options.</summary>
    public const string ObsidianTabTitle = "Obsidian";

    /// <summary>The SQL tools' tab and group (2026-09-23), after Obsidian, before Options.</summary>
    public const string SqlTabTitle = "SQL";

    /// <summary>The image tools' tab and group (2026-09-24), after SQL, before Options; "Images" until later that day (the user's call: it is ComfyUI's tab).</summary>
    public const string ComfyTabTitle = "ComfyUI";

    /// <summary>The nine tabs in strip order: Offered, Web, Files, Shell, Ask, Git (native), Obsidian, SQL (2026-09-23), Options — Options last since later on 2026-09-22 (the user's ask; second, after Offered, before), Obsidian added that day; the user's order of the rest since later on 2026-09-21 (alphabetical before: Ask, Files, Git, Shell, Web); the last eight index <see cref="SettingsMenu.ToolsTabFields"/> one down.</summary>
    public static readonly IReadOnlyList<string> TabTitles = [OfferedTabTitle, WebTabTitle, FilesTabTitle, ShellTabTitle, AskTabTitle, GitTabTitle, ObsidianTabTitle, SqlTabTitle, ComfyTabTitle, OptionsTabTitle];

    /// <summary>The Offered tab's hint row. Pinned.</summary>
    public const string OfferedKeys = "Enter / Space = on or off · ←/→ tabs · ESC = close";

    /// <summary>The first row of the Offered tab while the setting <c>LLM offer tools</c> is off; every row under it dim. Pinned.</summary>
    public const string OffLine = "LLM offer tools is off (the LLM tab of /settings): nothing is offered; a switch here saves for when it is on again.";

    /// <summary>After the Questions heading while the bottom pane is off (<c>ask_user</c> has nowhere to draw). Pinned.</summary>
    public const string NoPaneSuffix = "(off: no pane)";

    /// <summary>After the Obsidian heading while the group is not offered: the switch is off or no vault is set (2026-09-22). Pinned.</summary>
    public const string ObsidianOffSuffix = "(off: Obsidian tools is off or no Obsidian vault is set)";

    /// <summary>After the SQL heading while the group is not offered: the switch is off or <c>sql.json</c> holds no connection (2026-09-23). Pinned.</summary>
    public const string SqlOffSuffix = "(off: SQL tools is off or no connection is set in sql.json)";

    /// <summary>After the ComfyUI heading while the group is not offered: the switch is off, no ComfyUI URL is set or no workflow is in a comfy folder (2026-09-24). Pinned.</summary>
    public const string ComfyOffSuffix = "(off: ComfyUI tools is off, no ComfyUI URL is set or no workflow is in a comfy folder)";

    /// <summary>The name column of a tool row: <see cref="SystemPromptSummary.ToolNameWidth"/>, the plain lines' column.</summary>
    public const int NameWidth = SystemPromptSummary.ToolNameWidth;

    /// <summary>The on/off column of a tool row: <c>on</c> or <c>off</c> padded to it.</summary>
    public const int StateWidth = 5;

    /// <summary>After a group's heading while its switch is off: <c>(off: File tools is off)</c>, the shape of the Skills pane's old <c>(off: external skills disabled)</c> note. Pinned.</summary>
    public static string GroupOffSuffix(string switchLabel) => $"(off: {switchLabel} is off)";

    /// <summary>The status line after a flip: <c>read_file: off</c>, the <see cref="SettingsMenu.SavedNotice"/> shape. Pinned.</summary>
    public static string FlippedNotice(string tool, bool on) => $"{tool}: {State(on)}";

    /// <summary>The saved word for a tool's state.</summary>
    public static string State(bool on) => on ? "on" : "off";

    /// <summary>The saved list as a set, ordinal: what <c>PrepareTurn</c> and the summaries test against.</summary>
    public static IReadOnlySet<string> DisabledSet(IReadOnlyList<string> saved)
    {
        ArgumentNullException.ThrowIfNull(saved);
        return saved.Count == 0 ? Empty : new HashSet<string>(saved, StringComparer.Ordinal);
    }

    private static readonly IReadOnlySet<string> Empty = new HashSet<string>(0, StringComparer.Ordinal);

    /// <summary>
    /// The saved list with <paramref name="tool"/> flipped: added when absent (off now), removed when
    /// present (on again). Sorted ordinal, no duplicates — a hand-edited file's doubles go at the first flip.
    /// </summary>
    public static List<string> Flip(IReadOnlyList<string> saved, string tool)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(tool);
        var set = new HashSet<string>(saved, StringComparer.Ordinal);
        if (!set.Remove(tool))
        {
            set.Add(tool);
        }

        var list = set.ToList();
        list.Sort(StringComparer.Ordinal);
        return list;
    }

    /// <summary>Whether <paramref name="tool"/> is on in <paramref name="facts"/> (not in the saved list; its group's switch is another matter).</summary>
    public static bool IsOn(ToolsFacts facts, string tool)
    {
        ArgumentNullException.ThrowIfNull(facts);
        return !facts.Disabled.Contains(tool);
    }

    /// <summary>
    /// What follows a group's heading: nothing while the group is offered or the whole list is off
    /// (<see cref="OffLine"/> says it once); <see cref="NoPaneSuffix"/> for the Questions group with no
    /// pane; else <see cref="GroupOffSuffix"/> with the group's switch label (<c>File tools</c>).
    /// </summary>
    public static string HeadingSuffix(ToolGroup group, bool toolsEnabled)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (group.Offered || !toolsEnabled)
        {
            return "";
        }

        if (group.Note == SystemPromptSummary.NotOffered(SystemPromptSummary.NoPaneSuffix))
        {
            return NoPaneSuffix;
        }

        if (group.Switch == SettingsField.ObsidianTools)
        {
            // Two things keep the vault group off (2026-09-22): the switch, or no vault set — the switch alone would mislead.
            return ObsidianOffSuffix;
        }

        if (group.Switch == SettingsField.ComfyTools)
        {
            // The SQL shape (2026-09-24): the switch, the server or the workflows.
            return ComfyOffSuffix;
        }

        if (group.Switch == SettingsField.SqlTools)
        {
            // The vault's two-reason shape (2026-09-23): the switch, or no connection defined.
            return SqlOffSuffix;
        }

        return group.Switch is { } field ? GroupOffSuffix(SettingsMenu.FieldName(field)) : "";
    }

    /// <summary>
    /// The Offered tab as menu rows: <see cref="OffLine"/> dim first while <c>LLM offer tools</c> is off;
    /// then per group its name (<c>Files (15)</c>, <c>Files (13 of 15)</c>) in the section colour — whether
    /// the group is offered or not (a heading never dims, later on 2026-09-20, the user's call) — with
    /// <see cref="HeadingSuffix"/> dim after it, and a row per tool — the name in the label colour
    /// padded to <see cref="NameWidth"/>, <c>on</c> / <c>off</c> padded to <see cref="StateWidth"/>, the
    /// description dim with the tool's note after it when it has one — the whole row dim while the
    /// turn would not offer it (the group off, the list off, or a note). The tool's name beside every
    /// tool row, null beside a heading and the off line.
    /// </summary>
    public static IReadOnlyList<(string Markup, string? Tool)> OfferedRows(ToolsFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var rows = new List<(string, string?)>(48);
        if (!facts.ToolsEnabled)
        {
            rows.Add((Theme.DimMarkup(OffLine), null));
        }

        foreach (var group in facts.Groups)
        {
            string suffix = HeadingSuffix(group, facts.ToolsEnabled);
            string heading = Styled(Theme.SectionHeading, group.Name);
            rows.Add((suffix.Length > 0 ? heading + Theme.DimMarkup(" " + suffix) : heading, null));
            foreach (var tool in group.Tools)
            {
                bool on = IsOn(facts, tool.Name);
                string note = group.ToolNotes.TryGetValue(tool.Name, out var why) ? "  " + why : "";
                bool offered = facts.ToolsEnabled && group.Offers(tool.Name);
                string row = offered
                    ? Styled(Theme.AccentSecondary, tool.Name.PadRight(NameWidth)) + Theme.ColorMarkup(Theme.Ink, State(on).PadRight(StateWidth)) + Theme.DimMarkup(tool.Description)
                    : Theme.DimMarkup(tool.Name.PadRight(NameWidth) + State(on).PadRight(StateWidth) + tool.Description + note);
                rows.Add((row, tool.Name));
            }
        }

        return rows;
    }

    /// <summary>The first tool row of <paramref name="rows"/> (the cursor's opening place, past the first heading); 0 when there is none.</summary>
    public static int FirstToolRow(IReadOnlyList<(string Markup, string? Tool)> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].Tool is not null)
            {
                return i;
            }
        }

        return 0;
    }

    /// <summary>The Offered tab as plain lines: <see cref="OffLine"/> first while the list is off, each group's title (the name, its suffix or note) then <c>name  on/off  description — note</c>.</summary>
    public static IEnumerable<string> OfferedLines(ToolsFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (!facts.ToolsEnabled)
        {
            yield return OffLine;
        }

        foreach (var group in facts.Groups)
        {
            string suffix = HeadingSuffix(group, facts.ToolsEnabled);
            yield return suffix.Length > 0 ? group.Name + " " + suffix : group.Name;
            foreach (var tool in group.Tools)
            {
                string note = group.ToolNotes.TryGetValue(tool.Name, out var why) ? " — " + why : "";
                yield return "  " + tool.Name.PadRight(NameWidth) + State(IsOn(facts, tool.Name)).PadRight(StateWidth) + tool.Description + note;
            }
        }
    }

    private static string Styled(Style style, string text) => $"[{style.ToMarkup()}]{Markup.Escape(text)}[/]";
}
