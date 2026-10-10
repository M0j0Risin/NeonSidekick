using NeonSidekick.Settings;
using NeonSidekick.UI;

namespace NeonSidekick.App;

// ── /tools' group tabs, Media and Database (2026-10-09) ─────────────────────

internal sealed partial class SettingsMenu
{
    /// <summary>
    /// The strip badge of a group tab of <c>/tools</c> (2026-10-09, the user's call: the five database tabs on one Database tab, the five
    /// media tabs on one Media tab): every row of its <paramref name="menus"/> (indexes into <see cref="ToolsTabFields"/>) whose value is not
    /// the default, as <see cref="ChangedBadge"/> writes it; null for none. Until 2026-10-10 it came with a row per menu and its switch
    /// (<c>MenusTab</c>); the menus are sub-tabs since (<see cref="ToolsMenu"/>, <see cref="MenuTab.SubTabs"/>).
    /// </summary>
    internal string? GroupBadge(IReadOnlyList<int> menus, AppSettingsData saved)
    {
        ArgumentNullException.ThrowIfNull(menus);
        ArgumentNullException.ThrowIfNull(saved);
        return ChangedBadge(menus.Sum(m => ToolsTabFields[m].Count(f => IsChanged(f, saved, _settings.ProfileDirectory))));
    }
}
