using NeonSidekick.App;
using NeonSidekick.Mcp;
using NeonSidekick.Settings;

namespace NeonSidekick.Help;

/// <summary>A pane <c>neon_help</c> knows: the command that opens it, what it is for, and its tab strip in order.</summary>
public sealed record HelpPane(string Command, string Summary, IReadOnlyList<string> Tabs);

/// <summary>
/// Where a settings row lives: the pane's command, the tab's title and the row's label (<see cref="SettingsMenu.FieldName"/>);
/// on a group tab — <c>/tools</c>' Media and Database (2026-10-09), Files and Integrations, <c>/settings</c>' Client, Servers and Voice (2026-10-10) — the menu on the tab that holds it too, null elsewhere.
/// </summary>
public sealed record HelpRow(SettingsField Field, string Pane, string Tab, string Label, string? Menu = null)
{
    /// <summary><c>/tools › Devices › Camera › Camera watch interval (s)</c> (<c>/tools › Camera › …</c> until 2026-10-09; Media for Devices until later on 2026-10-10). Pinned.</summary>
    public string Path => Pane + HelpText.PathSeparator + Place + HelpText.PathSeparator + Label;

    /// <summary>The tab, and the menu on it when there is one: <c>Devices › Camera</c>.</summary>
    public string Place => Menu is null ? Tab : Tab + HelpText.PathSeparator + Menu;
}

/// <summary>
/// Where every setting lives and what it starts as, for <c>neon_help</c> (2026-10-02): worked out from the menus' own lists —
/// <see cref="SettingsMenu.TabFields"/> under <see cref="SettingsMenu.TabTitles"/>, <see cref="SettingsMenu.ToolsTabFields"/>
/// under <see cref="ToolsText.TabTitles"/> one down, <see cref="SettingsMenu.SkillsTabFields"/> under
/// <see cref="SkillsMenu.SettingsTabTitles"/>, <see cref="SettingsMenu.McpTabFields"/> as <c>/mcp</c>' Options — so a row
/// moved to another tab is found there with nothing written here. The four lists are every <see cref="SettingsField"/> once
/// (pinned by the settings tests), so every field has one row. <c>/tools</c>' lists are menus since 2026-10-09, placed on the strip by
/// <see cref="ToolsText.TabMenus"/>: a menu on a group tab (Devices, Databases…) names the tab and itself (<see cref="HelpRow.Menu"/>).
/// </summary>
internal static class HelpLocation
{
    /// <summary>
    /// What an empty working directory stands for in a default (<see cref="Default"/>): the profile's own folder is not
    /// known to the manual, and a made-up path would read as a real one.
    /// </summary>
    private const string ProfilePlaceholder = @"C:\neon-help\profiles\neon";

    private static readonly Lazy<IReadOnlyList<HelpRow>> s_rows = new(BuildRows);

    /// <summary>The four panes with settings rows, in the toolbar's order, each with its whole tab strip.</summary>
    public static readonly IReadOnlyList<HelpPane> Panes =
    [
        new("/settings", HelpText.SettingsPaneSummary, SettingsMenu.StripTitles),
        new("/tools", HelpText.ToolsPaneSummary, ToolsText.TabTitles),
        new("/skills", HelpText.SkillsPaneSummary, [SkillsText.OfferedTabTitle, SkillsText.ReflectionTabTitle, SkillsText.OptionsTabTitle]),
        new("/mcp", HelpText.McpPaneSummary, McpText.TabTitles),
    ];

    /// <summary>Every settings row, pane by pane and tab by tab in strip order.</summary>
    public static IReadOnlyList<HelpRow> Rows => s_rows.Value;

    /// <summary>The row <paramref name="field"/> is on.</summary>
    public static HelpRow Of(SettingsField field) => Rows.First(r => r.Field == field);

    /// <summary>The rows of one tab, in the order the tab shows them (a group tab's, every menu's in turn).</summary>
    public static IReadOnlyList<HelpRow> OnTab(string pane, string tab) =>
        Rows.Where(r => string.Equals(r.Pane, pane, StringComparison.Ordinal) && string.Equals(r.Tab, tab, StringComparison.Ordinal)).ToList();

    /// <summary>The menus on a pane's group tabs, each with its tab (2026-10-09): <c>/tools</c>' Core, Files, Integrations, Databases and Devices, <c>/settings</c>' Client, Servers and Voice (2026-10-10).</summary>
    public static IReadOnlyList<(string Tab, string Menu)> Menus(string pane) =>
        Rows.Where(r => string.Equals(r.Pane, pane, StringComparison.Ordinal) && r.Menu is not null).Select(r => (r.Tab, r.Menu!)).Distinct().ToList();

    /// <summary>The rows of one menu on a group tab, in its order.</summary>
    public static IReadOnlyList<HelpRow> InMenu(string pane, string menu) =>
        Rows.Where(r => string.Equals(r.Pane, pane, StringComparison.Ordinal) && string.Equals(r.Menu, menu, StringComparison.Ordinal)).ToList();

    /// <summary>
    /// The row's value in a fresh profile, as the menu shows it (<see cref="SettingsMenu.FieldValue(SettingsField, AppSettingsData, string)"/>
    /// over a new <see cref="AppSettingsData"/>): never this profile's, which the manual does not read (the user's call, 2026-10-02).
    /// The profile row's value is the root pointer's, not the profile's, so it reads <see cref="Profiles.DefaultName"/>; a value the
    /// menu shows as nothing reads <see cref="HelpText.EmptyDefault"/>.
    /// </summary>
    public static string Default(SettingsField field) => field switch
    {
        SettingsField.WorkingDirectory => HelpText.WorkingDirectoryDefault,
        SettingsField.Profile => Profiles.DefaultName,
        _ => SettingsMenu.FieldValue(field, new AppSettingsData(), ProfilePlaceholder) is { Length: > 0 } value ? value : HelpText.EmptyDefault,
    };

    private static IReadOnlyList<HelpRow> BuildRows()
    {
        var rows = new List<HelpRow>(256);
        for (int s = 0; s < SettingsMenu.StripTitles.Count; s++)
        {
            // A group's tabs (2026-10-10: Client, Servers and Voice) name their tab as the menu on it, /tools' groups' shape.
            var held = SettingsMenu.StripTabs[s];
            foreach (var tab in held)
            {
                string? menu = held.Count > 1 ? SettingsMenu.TabTitles[(int)tab] : null;
                rows.AddRange(SettingsMenu.TabFields[(int)tab].Select(field => new HelpRow(field, "/settings", SettingsMenu.StripTitles[s], SettingsMenu.FieldName(field), menu)));
            }
        }

        for (int t = 1; t < ToolsText.TabTitles.Count; t++)
        {
            // A plain tab is its menu; a group tab's rows name their menu too (2026-10-09).
            var menus = ToolsText.TabMenus[t - 1];
            foreach (int m in menus)
            {
                string? menu = menus.Count > 1 ? ToolsText.MenuTitles[m] : null;
                rows.AddRange(SettingsMenu.ToolsTabFields[m].Select(field => new HelpRow(field, "/tools", ToolsText.TabTitles[t], SettingsMenu.FieldName(field), menu)));
            }
        }

        Add(rows, "/skills", SkillsMenu.SettingsTabTitles, SettingsMenu.SkillsTabFields);
        Add(rows, "/mcp", [McpText.OptionsTabTitle], SettingsMenu.McpTabFields);
        return rows;
    }

    private static void Add(List<HelpRow> rows, string pane, IReadOnlyList<string> titles, IReadOnlyList<IReadOnlyList<SettingsField>> tabs)
    {
        for (int i = 0; i < tabs.Count; i++)
        {
            foreach (var field in tabs[i])
            {
                rows.Add(new HelpRow(field, pane, titles[i], SettingsMenu.FieldName(field)));
            }
        }
    }
}
