using NeonSidekick.App;

namespace NeonSidekick.Tests;

/// <summary>
/// The families only Windows has, left out elsewhere (2026-10-22, the user's ask): a Mac shows no Services or Tasks menu on <c>/tools</c>
/// › Admin, no <c>/tools services|tasks</c> word and no 🧩 or 📅 on the toolbar. The pure answers for both systems, so Windows tests the Mac's.
/// </summary>
public class WindowsOnlyMenusTests
{
    private static int Admin => ToolsText.TabTitles.ToList().IndexOf(ToolsText.AdminTabTitle);

    private static IEnumerable<string> Titles(IReadOnlyList<int> menus) => menus.Select(m => ToolsText.MenuTitles[m]);

    [Fact]
    public void TheWindowsOnlyMenus_AreServicesAndTasks()
    {
        // Monitors joined them on 2026-10-23 (the monitors plan: the CCD API and dxva2).
        Assert.Equal([ToolsText.MonitorsMenuTitle, ToolsText.ServicesMenuTitle, ToolsText.TasksMenuTitle], ToolsText.WindowsOnlyMenus.Order(StringComparer.Ordinal));
        Assert.All(ToolsText.MenuTitles, title => Assert.True(ToolsText.MenuHere(title, windows: true)));
        Assert.Equal(ToolsText.MenuTitles.Where(t => t is not (ToolsText.ServicesMenuTitle or ToolsText.TasksMenuTitle or ToolsText.MonitorsMenuTitle)), ToolsText.MenuTitles.Where(t => ToolsText.MenuHere(t, windows: false)));
    }

    [Fact]
    public void ShownMenus_OnWindows_IsTheWholeLayout_AndOffIt_AdminLosesServicesAndTasks()
    {
        for (int tab = 1; tab < ToolsText.TabTitles.Count; tab++)
        {
            Assert.Equal(ToolsText.TabMenus[tab - 1], ToolsText.ShownMenus(tab, windows: true));
            Assert.True(ToolsText.ShownMenus(tab, windows: false).Count > 1, ToolsText.TabTitles[tab] + " stays a group tab");
        }

        Assert.Equal(["Network", "EventLog", "Services", "Tasks", "Directory"], Titles(ToolsText.ShownMenus(Admin, windows: true)));
        Assert.Equal(["Network", "EventLog", "Directory"], Titles(ToolsText.ShownMenus(Admin, windows: false)));
        Assert.Equal(ToolsText.ShownMenus(Admin, OperatingSystem.IsWindows()), ToolsText.ShownMenus(Admin));
    }

    [Fact]
    public void SwitchWords_OffWindows_LeaveOutServicesAndTasks_AndTheirWordsAreTheUsageError()
    {
        Assert.Equal(ToolsText.SwitchWords, ToolsText.SwitchWordsFor(windows: true));
        Assert.Equal(ToolsText.SwitchWords.Where(w => w is not ("services" or "tasks" or "monitors")), ToolsText.SwitchWordsFor(windows: false));
        Assert.Equal(ToolsText.SwitchWordsFor(OperatingSystem.IsWindows()), ToolsText.SwitchWordsHere);
        Assert.Equal(SettingsField.SqlTools, ToolsText.SwitchFieldHere(" SQL "));
        Assert.Equal(OperatingSystem.IsWindows() ? SettingsField.ServiceTools : null, ToolsText.SwitchFieldHere("services"));
        Assert.Equal(OperatingSystem.IsWindows() ? SettingsField.TaskTools : null, ToolsText.SwitchFieldHere("Tasks"));
        Assert.Equal(SettingsField.ServiceTools, ToolsText.SwitchField("services"));   // the pinned map keeps every word
    }

    [Fact]
    public void ToolbarItems_OffWindows_HideOnlyTheServicesAndTasksSwitches()
    {
        Assert.All(ToolbarItems.Names, id => Assert.True(ToolbarItems.IsHere(id, windows: true)));
        Assert.Equal(ToolbarItems.Names.Where(id => id is not (ToolbarItems.Services or ToolbarItems.Tasks)), ToolbarItems.Names.Where(id => ToolbarItems.IsHere(id, windows: false)));
        Assert.Equal(OperatingSystem.IsWindows(), ToolbarItems.IsHere(ToolbarItems.Services));
    }

    [MacFact]
    public void OnAMac_TheSlashListSaysThereAreNone()
    {
        Assert.Equal("none on a Mac (Windows services)", SlashCommands.HelpEntries.Single(e => e.Command == "/services").Summary);
        Assert.Equal("none on a Mac (Windows' scheduled tasks)", SlashCommands.HelpEntries.Single(e => e.Command == "/tasks").Summary);
    }
}
