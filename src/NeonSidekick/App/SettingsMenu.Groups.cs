using NeonSidekick.Settings;
using NeonSidekick.UI;

namespace NeonSidekick.App;

// ── /tools' group tabs, Media and Database (2026-10-09) ─────────────────────

internal sealed partial class SettingsMenu
{
    /// <summary>
    /// A group tab of <c>/tools</c> (2026-10-09, the user's call: the five database tabs on one Database tab, the five media tabs on one
    /// Media tab): one row per menu of <paramref name="menus"/> (indexes into <see cref="ToolsTabFields"/>), its title padded as a
    /// row's label and its switch's value after it — the menu's first row, drawn as <see cref="FieldsTab"/> draws it, so a value
    /// not the default reads in the secondary accent and a variable's override shows. Space flips the switch, Enter opens the menu
    /// (<see cref="ToolsMenu"/>); the strip's badge counts every row of the five whose value is not the default.
    /// </summary>
    internal MenuTab MenusTab(string title, IReadOnlyList<int> menus, AppSettingsData saved)
    {
        ArgumentNullException.ThrowIfNull(menus);
        ArgumentNullException.ThrowIfNull(saved);
        string? located = _locateBrowser("");
        int width = menus.Max(m => ToolsText.MenuTitles[m].Length) + LabelGap;
        var rows = menus.Select(m => LiveLabel(ToolsTabFields[m][0], saved, width, located, ToolsText.MenuTitles[m])).ToList();
        string? badge = ChangedBadge(menus.Sum(m => ToolsTabFields[m].Count(f => IsChanged(f, saved, _settings.ProfileDirectory))));
        return new MenuTab(title, rows) { SpaceToggles = true, Badge = badge };
    }
}
