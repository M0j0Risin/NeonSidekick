using NeonSidekick.App;
using NeonSidekick.Settings;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.Tests;

/// <summary>
/// <c>/tools</c>' Media and Database tabs (2026-10-09, the user's call): five menus each. Until 2026-10-10 a row per menu with its switch,
/// Enter opening the five as a page of their own; since then sub-tabs (the user's call: reaching a menu through its switch was not
/// intuitive) — a second strip under the first, the menu's rows under it, ←/→ walking every menu, Tab / Shift+Tab stepping past a group.
/// </summary>
public partial class ToolsMenuTests
{
    private static readonly ConsoleKeyInfo Space = new(' ', ConsoleKey.Spacebar, false, false, false);

    /// <summary>A group's second strip as the pane prints it (badges cut as <see cref="Output"/> cuts them): the hook, then its menus' titles.</summary>
    private static string SubStrip(string group) =>
        Markup.Remove(InfoPane.SubStripRows(ToolsText.Label, ToolsText.TabMenus[TabIndex(group) - 1].Select(m => ToolsText.MenuTitles[m]).ToList(), 0, 240)[0]);

    /// <summary>The strips over a group's menu: the first, the second under it, then the spacer.</summary>
    private string Strips(string group) => "\n" + Titled(Strip) + "\n" + SubStrip(group) + "\n \n";

    [Fact]
    public void TheSubStrips_ArePinned()
    {
        Assert.Equal("         │ ↳ SQL · MySQL · SQLite · PostgreSQL · Oracle ", SubStrip(ToolsText.DatabaseTabTitle));
        Assert.Equal("         │ ↳ Print · Camera · Screen ", SubStrip(ToolsText.MediaTabTitle));   // ComfyUI and YouTube on Integrations since 2026-10-10
        Assert.Equal("         │ ↳ File · Image · UNC · GitLib · Obsidian ", SubStrip(ToolsText.FilesTabTitle));   // 2026-10-10; Image later that day
        Assert.Equal("         │ ↳ ClaudeCode · Docker · HomeAssistant · ComfyUI · YouTube ", SubStrip(ToolsText.IntegrationsTabTitle));   // 2026-10-10
    }

    [Fact]
    public async Task OnThePane_TheDatabaseTab_ShowsTheSecondStrip_AndTheFirstMenusRows()
    {
        var (menu, pane, _) = PaneMenu();
        Push(ToTab(ToolsText.DatabaseTabTitle));
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(LeafIndex(ToolsText.SqlTabTitle), LeafIndex(ToolsText.DatabaseTabTitle));
        Assert.Contains(Strips(ToolsText.DatabaseTabTitle) + "▸ " + SettingsMenu.FieldName(SettingsField.SqlTools) + "  ", Output);
        Assert.DoesNotContain("\n" + Titled(Strip) + "\n \n▸ SQL  ", Output);   // no row per menu any more
        pane.Dispose();
    }

    /// <summary>The Files and Integrations groups (2026-10-10, the user's pick): each a second strip, its first menu's rows under it.</summary>
    [Fact]
    public async Task OnThePane_TheFilesAndIntegrationsTabs_ShowTheSecondStrip_AndTheFirstMenusRows()
    {
        var (menu, pane, _) = PaneMenu();
        Push(ToTab(ToolsText.FilesTabTitle));
        Push(Keys.Tab);                              // Integrations, on ClaudeCode
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(LeafIndex(ToolsText.FileMenuTitle), LeafIndex(ToolsText.FilesTabTitle));
        Assert.Contains(Strips(ToolsText.FilesTabTitle) + "▸ " + SettingsMenu.FieldName(SettingsField.FileTools) + "  ", Output);
        Assert.Contains(Strips(ToolsText.IntegrationsTabTitle) + "▸ " + SettingsMenu.FieldName(TabFields(ToolsText.ClaudeCodeTabTitle)[0]) + "  ", Output);
        Assert.Equal(LeafIndex(ToolsText.HomeAssistantTabTitle) + 1, LeafIndex(ToolsText.ComfyTabTitle));   // ComfyUI and YouTube after Home, off Media
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_TheMediaTab_ShowsTheSecondStrip_AndTheFirstMenusRows()
    {
        var (menu, pane, _) = PaneMenu();
        Push(ToTab(ToolsText.MediaTabTitle));
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains(Strips(ToolsText.MediaTabTitle) + "▸ " + SettingsMenu.FieldName(SettingsField.PrintTools) + "  ", Output);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_TheArrowsWalkEveryMenu_FromOneGroupIntoTheNext_AndBack()
    {
        var (menu, pane, _) = PaneMenu();
        Push(ToTab(ToolsText.OracleTabTitle));      // Database's last menu
        Push(Keys.Right);                           // Media's first
        Push(Keys.Left);                            // Oracle again
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        string oracle = Strips(ToolsText.DatabaseTabTitle) + "▸ " + SettingsMenu.FieldName(SettingsField.OracleTools) + "  ";
        string print = Strips(ToolsText.MediaTabTitle) + "▸ " + SettingsMenu.FieldName(SettingsField.PrintTools) + "  ";
        Assert.Contains(print, Output);
        Assert.True(Output.LastIndexOf(oracle, StringComparison.Ordinal) > Output.IndexOf(print, StringComparison.Ordinal));
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_TabStepsPastAGroup_AndShiftTabComesBackToTheMenuLastShown()
    {
        var (menu, pane, _) = PaneMenu();
        Push(ToTab(ToolsText.SqliteTabTitle));      // Database, SQLite
        Push(Keys.Tab);                             // Media, on its first
        Push(Keys.Tab);                             // Options: past Media's five
        Push(Keys.ShiftTab, Keys.ShiftTab);         // Media, then Database on SQLite
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains("\n" + Titled(Strip) + "\n \n▸ " + SettingsMenu.FieldName(SettingsField.ToolsDollarMention) + "  ", Output);
        string sqlite = Strips(ToolsText.DatabaseTabTitle) + "▸ " + SettingsMenu.FieldName(SettingsField.SqliteTools) + "  ";
        Assert.True(Output.LastIndexOf(sqlite, StringComparison.Ordinal) > Output.LastIndexOf(Strips(ToolsText.MediaTabTitle), StringComparison.Ordinal));
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_SpaceOnAMenusSwitch_FlipsIt_AndTheMenuStaysOnShow()
    {
        _settings.Update(d => d.MySqlTools = false);
        var (menu, pane, _) = PaneMenu();
        Push(ToTab(ToolsText.MySqlTabTitle));
        Push(Space);                                // MySQL tools on
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.True(_settings.Current.MySqlTools);
        string name = SettingsMenu.FieldName(SettingsField.MySqlTools);
        Assert.Contains("\n" + Titled(Strip) + "\n" + SubStrip(ToolsText.DatabaseTabTitle) + "\n  · " + name + ": on\n▸ " + name + "  ", Output);   // the rebuilt page kept MySQL
        pane.Dispose();
    }

    /// <summary>A click on a menu's title in the second strip shows it: Postgres, the strip at row 101 under the first at 100.</summary>
    [Fact]
    public async Task OnThePane_AClickOnAMenusTitle_ShowsIt()
    {
        var (menu, pane, input) = ClickablePaneMenu(100);
        var titles = ToolsText.TabMenus[TabIndex(ToolsText.DatabaseTabTitle) - 1].Select(m => ToolsText.MenuTitles[m]).ToList();
        var (_, places) = InfoPane.SubStripLayout(ToolsText.Label, titles, 240);
        input.Push(ToTab(ToolsText.DatabaseTabTitle));
        input.PushClick(places[3].Column + 1, 101);     // Postgres
        input.Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains("\n" + SubStrip(ToolsText.DatabaseTabTitle) + "\n \n▸ " + SettingsMenu.FieldName(SettingsField.PostgresTools) + "  ", Output);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_AMenusPicker_IsTitledUnderTheGroup_AndItSaves()
    {
        var (menu, pane, settings) = PaneMenu();
        Push(ToTab(ToolsText.CameraTabTitle));      // Media, Camera
        Push(Keys.Down, Keys.Enter);                // Camera shutter: the picker
        Push(Keys.Down, Keys.Enter);                // the second choice
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.NotEqual(new AppSettingsData().CameraShutter, _settings.Current.CameraShutter);
        Assert.Equal(ToolsText.Label + " › Media", ToolsMenu.GroupCrumb(TabIndex(ToolsText.MediaTabTitle)));
        string picker = "\n" + Titled(ToolsText.Label + " › Media › Camera shutter") + "\n";
        Assert.Contains(picker, Output);
        string after = Output[Output.LastIndexOf(picker, StringComparison.Ordinal)..];
        Assert.Contains("\n" + SubStrip(ToolsText.MediaTabTitle) + "\n", after);   // back on Camera's rows
        Assert.Contains("\n▸ " + SettingsMenu.FieldName(SettingsField.CameraShutter) + "  ", after);
        Assert.Equal(SettingsMenu.Title, settings.Root);   // restored for /settings
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_SpaceInAMenu_FlipsTheRow_AsOnAPlainTab()
    {
        _settings.Update(d => d.YouTubeAutoplay = true);
        var (menu, pane, _) = PaneMenu();
        Push(ToTab(ToolsText.YouTubeTabTitle));
        Push(Keys.Down, Keys.Down, Keys.Down, Space);   // YouTube autoplay off
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.False(_settings.Current.YouTubeAutoplay);
        pane.Dispose();
    }

    [Fact]
    public void AGroupTab_HoldsItsMenusAsSubTabs_EachWithItsOwnFooter_AndTheBadgeCountsThemAll()
    {
        _settings.Update(d => { d.SqlTools = true; d.SqliteTools = true; });
        var saved = _settings.Current;
        var (_, pane, settings) = PaneMenu();
        int database = TabIndex(ToolsText.DatabaseTabTitle);
        int media = TabIndex(ToolsText.MediaTabTitle);
        var subs = new int[ToolsText.TabTitles.Count];
        subs[database] = 4;
        var page = ToolsMenu.Page(ToolsText.OfferedRows(Facts()), saved, settings, database, subs: subs);

        Assert.Equal(["SQL", "MySQL", "SQLite", "PostgreSQL", "Oracle"], page.Tabs![database].SubTabs!.Select(t => t.Title));
        Assert.Equal(["Print", "Camera", "Screen"], page.Tabs[media].SubTabs!.Select(t => t.Title));   // ComfyUI and YouTube on Integrations since 2026-10-10
        Assert.Equal(4, page.Sub);
        Assert.Equal(TabFields(ToolsText.OracleTabTitle).Count, page.Rows.Count);
        Assert.Equal(SettingsMenu.FieldFooter(SettingsField.OracleTools).Text, page.Tabs[database].Shown.Footer!(0)!.Text);
        Assert.Null(page.Tabs[database].Shown.Footer!(TabFields(ToolsText.OracleTabTitle).Count));
        Assert.Equal(SettingsMenu.FieldFooter(SettingsField.CameraShutter).Text, page.Tabs[media].SubTabs![1].Footer!(1)!.Text);
        Assert.True(page.Tabs[media].SubTabs!.All(t => t.SpaceToggles));
        Assert.Equal(settings.GroupBadge(ToolsText.TabMenus[database - 1], saved), page.Tabs[database].Badge);
        Assert.NotNull(page.Tabs[database].Badge);
        Assert.Equal(0, ToolsMenu.Page(ToolsText.OfferedRows(Facts()), saved, settings, media).Sub);   // no subs: each group's first
        pane.Dispose();
    }

    /// <summary>The Tools pane keeps its height on a group: the second strip's row is taken out of the padding under the list.</summary>
    [Fact]
    public async Task OnThePane_AGroupsMenu_IsAsTallAsAPlainTab()
    {
        var (menu, pane, _) = PaneMenu();
        Push(ToTab(ToolsText.AskTabTitle));
        Push(Enumerable.Repeat(Keys.Right, LeafIndex(ToolsText.SqlTabTitle) - LeafIndex(ToolsText.AskTabTitle)).ToArray());
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        string askRow = "▸ " + SettingsMenu.FieldName(SettingsField.AskUser);
        string sqlRow = "▸ " + SettingsMenu.FieldName(SettingsField.SqlTools);
        Assert.Equal(FrameRows(Output, Titled(Strip), askRow), FrameRows(Output, Titled(Strip) + "\n" + SubStrip(ToolsText.DatabaseTabTitle), sqlRow));
        pane.Dispose();
    }

    /// <summary>
    /// The rows of the first frame drawn under <paramref name="strip"/> whose list starts with <paramref name="firstRow"/> (a tab without
    /// headings, whose rules would read as the band's): from the strip down to the hint row, the band included.
    /// </summary>
    private static int FrameRows(string output, string strip, string firstRow)
    {
        int start = output.IndexOf("\n" + strip + "\n \n" + firstRow, StringComparison.Ordinal);
        Assert.True(start >= 0, "no frame under " + strip);
        var lines = output[(start + 1)..].Split('\n');
        int ruleCount = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            // The footer's rule, then the hint row's: the frame ends on the line after the second rule.
            if (lines[i].StartsWith('─') && ++ruleCount == 2)
            {
                return i + 2;
            }
        }

        throw new InvalidOperationException("no hint rule under " + strip);
    }
}
