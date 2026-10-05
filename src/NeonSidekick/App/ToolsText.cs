using NeonSidekick.Settings;
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

    /// <summary>
    /// <c>/tools &lt;group&gt;</c>'s words (2026-10-03, the user's ask: the toolbar's tool switches): each opens its group's
    /// switch straight, as <c>/police</c> opens Shell police's page — <c>shell</c> the <c>Shell command policy</c> picker, the
    /// others their on/off page. In the toolbar's order; each is also its toolbar item's id. Pinned.
    /// </summary>
    public static readonly string[] SwitchWords =
    [
        ToolbarItems.Shell, ToolbarItems.Files, ToolbarItems.Web, ToolbarItems.Claude, ToolbarItems.Docker, ToolbarItems.Obsidian,
        ToolbarItems.Sql, ToolbarItems.Oracle, ToolbarItems.MySql, ToolbarItems.Sqlite, ToolbarItems.Postgres, ToolbarItems.Unc, ToolbarItems.Ha, ToolbarItems.Comfy,
        ToolbarItems.Camera, ToolbarItems.Print,
    ];

    /// <summary>The setting a <see cref="SwitchWords"/> word opens (any case, trimmed); null for any other word. Pinned.</summary>
    public static SettingsField? SwitchField(string word) => word.Trim().ToLowerInvariant() switch
    {
        ToolbarItems.Shell => SettingsField.ShellCommandPolicy,
        ToolbarItems.Files => SettingsField.FileTools,
        ToolbarItems.Web => SettingsField.WebTools,
        ToolbarItems.Claude => SettingsField.ClaudeCliAdvisor,
        ToolbarItems.Docker => SettingsField.DockerTools,
        ToolbarItems.Obsidian => SettingsField.ObsidianTools,
        ToolbarItems.Sql => SettingsField.SqlTools,
        ToolbarItems.Oracle => SettingsField.OracleTools,
        ToolbarItems.MySql => SettingsField.MySqlTools,
        ToolbarItems.Sqlite => SettingsField.SqliteTools,
        ToolbarItems.Postgres => SettingsField.PostgresTools,
        ToolbarItems.Unc => SettingsField.UncTools,
        ToolbarItems.Ha => SettingsField.HomeAssistantTools,
        ToolbarItems.Comfy => SettingsField.ComfyTools,
        ToolbarItems.Camera => SettingsField.CameraTools,
        ToolbarItems.Print => SettingsField.PrintTools,
        _ => null,
    };

    /// <summary>The typed line that opens <paramref name="word"/>'s switch: <c>/tools web</c>. The toolbar item's double-click and its checklist note. Pinned.</summary>
    public static string SwitchLine(string word) => SlashCommands.ToolsWord + " " + word;

    /// <summary><c>/tools</c>' completion hint beside a group word: the setting it opens. Pinned.</summary>
    public static string DescribeSwitch(string word) => SwitchField(word) is { } field ? SettingsMenu.FieldName(field) : "";

    /// <summary>What <c>/tools &lt;group&gt;</c> prints without the pane: the setting and its value, <c>Web tools: on</c>. Pinned.</summary>
    public static string SwitchStateLine(SettingsField field, AppSettingsData saved)
    {
        ArgumentNullException.ThrowIfNull(saved);
        string value = field == SettingsField.ShellCommandPolicy ? saved.ShellCommandPolicy : State(SettingsMenu.IsOn(field, saved));
        return SettingsMenu.FieldName(field) + ": " + value;
    }

    /// <summary><c>/tools</c> given a word that is no group. Pinned.</summary>
    public static readonly string SwitchUsageError = "/tools takes one of " + string.Join(", ", SwitchWords) + ", or nothing for the pane.";

    /// <summary>The last tab (second from later on 2026-09-19, the user's ask, until 2026-09-22, the user's ask again): the pane's own settings — the <c>$</c>-mention switch and the two folds — in the <c>/skills</c> Options tab's shape (<see cref="SettingsMenu.ToolsTabFields"/>' last list).</summary>
    public const string OptionsTabTitle = "Options";
    public const string AskTabTitle = "Ask";
    public const string FilesTabTitle = "Files";
    /// <summary><c>Git</c> until 2026-09-21, when the user asked for <c>Git (native)</c> (the in-process LibGit2Sharp tools as against git through the shell), and <c>Git</c> again since 2026-09-29, the user's call: the tab and the Offered heading, the rows keep their <c>Git native …</c> labels. <c>GitLib</c> since 2026-09-30 (the user's ask), the tab, the heading, the rows and the tools' names (<c>gitlib_status</c> …) alike.</summary>
    public const string GitTabTitle = "GitLib";
    public const string ShellTabTitle = "Shell";
    public const string WebTabTitle = "Web";

    /// <summary>The vault tools' tab and group (2026-09-22), after Camera, before SQL, since 2026-10-03 (the user's order; after Claude, before ComfyUI, from 2026-09-27).</summary>
    public const string ObsidianTabTitle = "Obsidian";

    /// <summary>The SQL tools' tab and group (2026-09-23), after Obsidian, before MySQL, since 2026-10-03 (the user's order; after ComfyUI, before Git (native), from 2026-09-27).</summary>
    public const string SqlTabTitle = "SQL";

    /// <summary>The Oracle tools' tab and group (2026-09-30), after MySQL, before Claude, since 2026-10-03 (the user's order; right after SQL before): the database tabs together.</summary>
    public const string OracleTabTitle = "Oracle";

    /// <summary>The MySQL tools' tab and group (2026-09-30), after SQL, before Oracle, since 2026-10-03 (the user's order; after Oracle before): the database tabs together.</summary>
    public const string MySqlTabTitle = "MySQL";

    /// <summary>The SQLite tools' tab and group (2026-10-04), after MySQL, before Oracle.</summary>
    public const string SqliteTabTitle = "SQLite";

    /// <summary>The PostgreSQL tools' tab and group (2026-10-04), after SQLite, before Oracle; "Postgres" on the strip (the group's full name is the setting's).</summary>
    public const string PostgresTabTitle = "Postgres";

    /// <summary>The UNC tools' tab and group (2026-09-30), after Files, before Print, since 2026-10-03 (the user's order; after MySQL, before Docker, until then).</summary>
    public const string UncTabTitle = "UNC";

    /// <summary>The camera's tab and group (2026-10-02, <c>camera_capture</c> and the camera's rows), after Print, before Obsidian, since 2026-10-03 (the user's order; right after Ask until then).</summary>
    public const string CameraTabTitle = "Camera";

    /// <summary>The screen capture's tab and group (2026-10-04): <c>screen_capture</c> and <c>screen_list</c>, after Camera, before Obsidian.</summary>
    public const string ScreenTabTitle = "Screen";

    /// <summary>The Docker tools' tab and group (2026-10-02), after Claude, before HA, since 2026-10-03 (the user's order; after UNC, before GitLib, until then).</summary>
    public const string DockerTabTitle = "Docker";

    /// <summary>The image tools' tab and group (2026-09-24), after HA, before GitLib, since 2026-10-03 (the user's order; after Obsidian, before SQL, from 2026-09-27); "Images" until later on 2026-09-24 (the user's call: it is ComfyUI's tab).</summary>
    public const string ComfyTabTitle = "ComfyUI";

    /// <summary>The Claude tab and the advisor's group (2026-09-27): <c>/claude</c>'s rows (off <c>/settings</c>, the user's call) and <c>claude_advisor_cli</c>'s, after Oracle, before Docker, since 2026-10-03 (the user's order; after Ask, before Obsidian, from later on 2026-09-27; after ComfyUI before). Titled "Claude (CLI)" from later on 2026-09-27 (the user's call: the pair of /settings' Claude (API) tab) and "Claude" again since 2026-09-29, when that tab's four rows came here under the advisor's (the user's call: one Claude tab); "ClaudeCLI" since 2026-10-04, when <c>/settings</c>' Anthropic tab became Anthropic (the user's call: the names say which is which).</summary>
    public const string ClaudeCliTabTitle = "ClaudeCLI";

    /// <summary>The Home Assistant tools' tab (2026-09-28), after Docker, before ComfyUI, since 2026-10-03 (the user's order; second to last, after GitLib, before Options, from later on 2026-10-01; after Claude, before Print, until then). "HA" since 2026-10-01 (the user's call: strip width); "Home Assistant" until then, which the group keeps (<see cref="HomeAssistantGroupTitle"/>).</summary>
    public const string HomeAssistantTabTitle = "HA";

    /// <summary>The Home Assistant tools' group on <c>/sys</c> and the Offered tab (2026-09-28): the full name, the tab's short one being for the strip alone (2026-10-01).</summary>
    public const string HomeAssistantGroupTitle = "Home Assistant";

    /// <summary>The print tools' tab and group (2026-09-28), after UNC, before Camera, since 2026-10-03 (the user's order; after Claude, before Obsidian, from later on 2026-10-01; after Home Assistant until then).</summary>
    public const string PrintTabTitle = "Print";

    /// <summary>The tabs in strip order — Offered, Ask, Web, Shell, Files, UNC, Print, Camera, Screen (2026-10-04), Obsidian, SQL, MySQL, Oracle, ClaudeCLI, Docker, HA, ComfyUI, GitLib, Options, the user's order since 2026-10-03 (ClaudeCLI "Claude" until 2026-10-04); before it (Camera after Ask since 2026-10-02; Docker after UNC since 2026-10-02; Home Assistant second to last, before Options, since later on 2026-10-01, the user's ask, and Print after Claude with it; Home Assistant after Claude from 2026-09-28 and Print after it later that day; Oracle after SQL, MySQL after Oracle and UNC after MySQL since 2026-09-30): Offered, Web, Files, Shell, Ask, Claude, Obsidian, ComfyUI, SQL, Oracle, Git (native), Options — the user's order since 2026-09-27 (Ask, Git (native), Obsidian, SQL, ComfyUI, Claude before); Options last since later on 2026-09-22 (the user's ask; second, after Offered, before); alphabetical before 2026-09-21; the last ten index <see cref="SettingsMenu.ToolsTabFields"/> one down.</summary>
    public static readonly IReadOnlyList<string> TabTitles = [OfferedTabTitle, AskTabTitle, WebTabTitle, ShellTabTitle, FilesTabTitle, UncTabTitle, PrintTabTitle, CameraTabTitle, ScreenTabTitle, ObsidianTabTitle, SqlTabTitle, MySqlTabTitle, SqliteTabTitle, PostgresTabTitle, OracleTabTitle, ClaudeCliTabTitle, DockerTabTitle, HomeAssistantTabTitle, ComfyTabTitle, GitTabTitle, OptionsTabTitle];

    /// <summary>The Offered tab's hint row (<c>/mcp</c>'s Tools tab too); "type = filter" since 2026-10-03 (<see cref="MenuFilter"/>). Pinned.</summary>
    public const string OfferedKeys = "Enter / Space = on or off · ←/→ tabs · " + MenuFilter.TypeAndCloseKeys;

    /// <summary>The first row of the Offered tab while the setting <c>LLM offer tools</c> is off; every row under it dim. Pinned.</summary>
    public const string OffLine = "LLM offer tools is off (the LLM tab of /settings): nothing is offered; a switch here saves for when it is on again.";

    /// <summary>After the Questions heading while the bottom pane is off (<c>ask_user</c> has nowhere to draw). Pinned.</summary>
    public const string NoPaneSuffix = "(off: no pane)";

    /// <summary>After the Obsidian heading while the group is not offered: the switch is off or no vault is set (2026-09-22). Pinned.</summary>
    public const string ObsidianOffSuffix = "(off: Obsidian tools is off or no Obsidian vault is set)";

    /// <summary>After the SQL heading while the group is not offered: the switch is off or no connection of <c>sql.json</c> is offered (2026-09-23; "offered" since 2026-10-01, when nothing is until ticked). Pinned.</summary>
    public const string SqlOffSuffix = "(off: SQL tools is off or no connection of sql.json is offered)";

    /// <summary>After the Oracle heading while the group is not offered: the switch is off or no connection of <c>oracle.json</c> is offered (2026-09-30). Pinned.</summary>
    public const string OracleOffSuffix = "(off: Oracle tools is off or no connection of oracle.json is offered)";

    /// <summary>After the MySQL heading while the group is not offered: the switch is off or no connection of <c>mysql.json</c> is offered (2026-09-30). Pinned.</summary>
    public const string MySqlOffSuffix = "(off: MySQL tools is off or no connection of mysql.json is offered)";

    /// <summary>After the SQLite heading while the group is not offered: the switch is off, or no database of <c>sqlite.json</c> is offered and the sandbox's files are not allowed (2026-10-04). Pinned.</summary>
    public const string SqliteOffSuffix = "(off: SQLite tools is off, or no database of sqlite.json is offered and SQLite sandbox files is off)";

    /// <summary>After the Postgres heading while the group is not offered: the switch is off or no connection of <c>postgres.json</c> is offered (2026-10-04). Pinned.</summary>
    public const string PostgresOffSuffix = "(off: PostgreSQL tools is off or no connection of postgres.json is offered)";

    /// <summary>After the UNC heading while the group is not offered: the switch is off or no share of <c>unc.json</c> is offered (2026-09-30). Pinned.</summary>
    public const string UncOffSuffix = "(off: UNC tools is off or no share of unc.json is offered)";

    /// <summary>After the Camera heading while <c>camera_capture</c> is not offered: the switch is off, there is no camera support, or the model reads no pictures (2026-10-02). Pinned.</summary>
    public const string CameraOffSuffix = "(off: Camera tool is off, there is no camera support, or the model reads no pictures)";

    /// <summary>After the Screen heading while the screen tools are not offered: the switch is off, there is no screen capture support, or the model reads no pictures (2026-10-04). Pinned.</summary>
    public const string ScreenOffSuffix = "(off: Screen capture tool is off, there is no screen capture support, or the model reads no pictures)";

    /// <summary>After the ComfyUI heading while the group is not offered: the switch is off, no ComfyUI URL is set or no workflow is offered (2026-09-24). Pinned.</summary>
    public const string ComfyOffSuffix = "(off: ComfyUI tools is off, no ComfyUI URL is set or no workflow is offered)";

    /// <summary>After the Home Assistant heading while the group is not offered: the switch is off, or no URL or API key is set (2026-09-28). Pinned.</summary>
    public const string HomeAssistantOffSuffix = "(off: Home Assistant tools is off, or no Home Assistant URL or API key is set)";

    /// <summary>The name column of a tool row: <see cref="SystemPromptSummary.ToolNameWidth"/>, the plain lines' column.</summary>
    public const int NameWidth = SystemPromptSummary.ToolNameWidth;

    /// <summary>The on/off column of a tool row: <c>on</c> or <c>off</c> padded to it.</summary>
    public const int StateWidth = 5;

    /// <summary>After a group's heading while its switch is off: <c>(off: File tools is off)</c>, the shape of the Skills pane's old <c>(off: external skills disabled)</c> note. Pinned.</summary>
    public static string GroupOffSuffix(string switchLabel) => $"(off: {switchLabel} is off)";

    /// <summary>After the Memory group's heading while <c>Memory mode</c> is disabled (2026-10-04; <c>(off: Memory is off)</c> until then). Pinned.</summary>
    public const string MemoryOffSuffix = "(off: Memory mode is disabled)";

    /// <summary>The status line after a flip: <c>read_file: off</c>, the <see cref="SettingsMenu.SavedNotice"/> shape. Pinned.</summary>
    public static string FlippedNotice(string tool, bool on) => $"{tool}: {State(on)}";

    /// <summary>The saved word for a tool's state.</summary>
    public static string State(bool on) => on ? "on" : "off";

    /// <summary>
    /// The state column of a tool under an off group (2026-10-04, the UI review: such a row read <c>on</c> while nothing of the group was
    /// offered): <c>(on)</c> for a tool that is on here but waits on its group, <c>off</c> as ever. Pinned.
    /// </summary>
    public static string GroupOffState(bool on) => on ? "(on)" : "off";

    /// <summary>A group's switch off: <c>Camera tool is off</c> (2026-10-04, <see cref="ToolGroup.OffReason"/>). Pinned.</summary>
    public static string SwitchOffReason(SettingsField field) => SettingsMenu.FieldName(field) + " is off";

    /// <summary>The other single reasons an off group gives on its heading (2026-10-04, <see cref="ToolGroup.OffReason"/>). Pinned.</summary>
    public const string NoCameraReason = "there is no camera support";
    public const string NoScreenReason = "there is no screen capture support";
    public const string NoPaneReason = "no pane";
    public const string BlindReason = "the model reads no pictures";
    public const string NoVaultReason = "no Obsidian vault is set";
    public const string NoComfyUrlReason = "no ComfyUI URL is set";
    public const string NoWorkflowReason = "no workflow is offered";
    public const string NoHomeAssistantReason = "no Home Assistant URL or API key is set";
    public const string NoSqliteDatabaseReason = "no database of sqlite.json is offered and SQLite sandbox files is off";

    /// <summary>A connections file that offers nothing: <c>no connection of sql.json is offered</c>. Pinned.</summary>
    public static string NoneOfferedReason(string what, string file) => $"no {what} of {file} is offered";

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

        if (group.OffReason is { } reason)
        {
            // The one reason that holds (2026-10-04), when the screen could tell which.
            return "(off: " + reason + ")";
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

        if (group.Switch == SettingsField.HomeAssistantTools)
        {
            // The SQL shape (2026-09-28): the switch, the URL or the token.
            return HomeAssistantOffSuffix;
        }

        if (group.Switch == SettingsField.SqlTools)
        {
            // The vault's two-reason shape (2026-09-23): the switch, or no connection defined.
            return SqlOffSuffix;
        }

        if (group.Switch == SettingsField.OracleTools)
        {
            // The SQL shape (2026-09-30): the switch, or no connection defined.
            return OracleOffSuffix;
        }

        if (group.Switch == SettingsField.MySqlTools)
        {
            // The SQL shape (2026-09-30): the switch, or no connection defined.
            return MySqlOffSuffix;
        }

        if (group.Switch == SettingsField.PostgresTools)
        {
            // The SQL shape (2026-10-04).
            return PostgresOffSuffix;
        }

        if (group.Switch == SettingsField.SqliteTools)
        {
            // The SQL shape (2026-10-04): the switch, or nothing to open.
            return SqliteOffSuffix;
        }

        if (group.Switch == SettingsField.UncTools)
        {
            // The SQL shape (2026-09-30): the switch, or no share defined.
            return UncOffSuffix;
        }

        if (group.Switch == SettingsField.CameraTools)
        {
            // The SQL shape (2026-10-02): the switch, no camera layer here, or a model that cannot see.
            return CameraOffSuffix;
        }

        if (group.Switch == SettingsField.ScreenTools)
        {
            // The camera's shape (2026-10-04).
            return ScreenOffSuffix;
        }

        if (group.Switch == SettingsField.MemoryMode)
        {
            // A picker, not a switch (2026-10-04): the group is off only when the mode is disabled.
            return MemoryOffSuffix;
        }

        return group.Switch is { } field ? GroupOffSuffix(SettingsMenu.FieldName(field)) : "";
    }

    /// <summary>
    /// The Offered tab as menu rows: <see cref="OffLine"/> dim first while <c>LLM offer tools</c> is off;
    /// then per group an empty row (a gap, the first group none — 2026-10-03, the user's call) and its heading — a
    /// <see cref="SectionRule"/> of its label, its count (<c>15</c>, <c>13 of 15</c>) and <see cref="HeadingSuffix"/>
    /// without its brackets (<see cref="Bare"/>), the label never dimmed whether the group is offered or not (later on
    /// 2026-09-20, the user's call) — and a row per tool — the name in the label colour
    /// padded to <see cref="NameWidth"/>, <c>on</c> / <c>off</c> padded to <see cref="StateWidth"/>, the
    /// description dim with the tool's note after it when it has one — the whole row dim while the
    /// turn would not offer it (the group off, the list off, or a note). The tool's name beside every
    /// tool row, null beside a heading, a gap and the off line; <c>Heading</c> true beside a heading alone
    /// (<see cref="HeadingRows"/>, the pane's <see cref="MenuTab.Headings"/>). Under a <paramref name="filter"/> (2026-10-03, the
    /// user's ask, <see cref="MenuFilter"/>) only the tools whose name or description holds it, a group with none left out with its
    /// heading and gap, the heading's count still the whole group's; <see cref="MenuFilter.NoMatchRow"/> alone when none is left.
    /// </summary>
    public static IReadOnlyList<(string Markup, string? Tool, bool Heading)> OfferedRows(ToolsFacts facts, string filter = "")
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(filter);
        var rows = new List<(string, string?, bool)>(64);
        if (!facts.ToolsEnabled)
        {
            rows.Add((Theme.DimMarkup(OffLine), null, false));
        }

        int shownGroups = 0;
        foreach (var group in facts.Groups)
        {
            var tools = group.Tools.Where(t => MenuFilter.Matches(filter, t.Name, t.Description)).ToList();
            if (tools.Count == 0 && filter.Length > 0)
            {
                continue;
            }

            if (shownGroups++ > 0)
            {
                rows.Add(("", null, false));
            }

            rows.Add((SectionRule.Markup(group.Label, group.Count, Bare(HeadingSuffix(group, facts.ToolsEnabled))), null, true));
            foreach (var tool in tools)
            {
                bool on = IsOn(facts, tool.Name);
                string note = group.ToolNotes.TryGetValue(tool.Name, out var why) ? "  " + why : "";
                bool offered = facts.ToolsEnabled && group.Offers(tool.Name);
                string row = offered
                    ? Styled(Theme.AccentSecondary, tool.Name.PadRight(NameWidth)) + Theme.ColorMarkup(Theme.Ink, State(on).PadRight(StateWidth)) + Theme.DimMarkup(tool.Description)
                    : Theme.DimMarkup(tool.Name.PadRight(NameWidth) + (group.Offered || !facts.ToolsEnabled ? State(on) : GroupOffState(on)).PadRight(StateWidth) + tool.Description + note);
                rows.Add((row, tool.Name, false));
            }
        }

        if (shownGroups == 0 && filter.Length > 0)
        {
            rows.Add((MenuFilter.NoMatchRow(filter), null, false));
        }

        return rows;
    }

    /// <summary>The tool rows of <paramref name="rows"/> (those that name a tool), for the filter's count.</summary>
    public static int ToolCount(IReadOnlyList<(string Markup, string? Tool, bool Heading)> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return rows.Count(r => r.Tool is not null);
    }

    /// <summary>The indices of the heading rows of <paramref name="rows"/>: the pane's <see cref="MenuTab.Headings"/>.</summary>
    public static IReadOnlySet<int> HeadingRows(IReadOnlyList<(string Markup, string? Tool, bool Heading)> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var headings = new HashSet<int>();
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].Heading)
            {
                headings.Add(i);
            }
        }

        return headings;
    }

    /// <summary>A heading suffix without the one pair of brackets around it, for the rule (<c>(off: File tools is off)</c> → <c>off: File tools is off</c>); anything else as it is. Pure.</summary>
    public static string Bare(string suffix)
    {
        ArgumentNullException.ThrowIfNull(suffix);
        return suffix.Length >= 2 && suffix[0] == '(' && suffix[^1] == ')' ? suffix[1..^1] : suffix;
    }

    /// <summary>The first tool row of <paramref name="rows"/> (the cursor's opening place, past the first heading); 0 when there is none.</summary>
    public static int FirstToolRow(IReadOnlyList<(string Markup, string? Tool, bool Heading)> rows)
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
                yield return "  " + tool.Name.PadRight(NameWidth) + (group.Offered || !facts.ToolsEnabled ? State(IsOn(facts, tool.Name)) : GroupOffState(IsOn(facts, tool.Name))).PadRight(StateWidth) + tool.Description + note;
            }
        }
    }

    private static string Styled(Style style, string text) => $"[{style.ToMarkup()}]{Markup.Escape(text)}[/]";
}
