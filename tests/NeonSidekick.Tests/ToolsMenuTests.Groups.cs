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
        Assert.Equal("         │ ↳ Camera · Screen · Print ", SubStrip(ToolsText.DevicesTabTitle));   // the user's order since 2026-10-16; ComfyUI and YouTube on Integrations since 2026-10-10
        Assert.Equal("         │ ↳ File · Image · UNC · GitLib · Obsidian ", SubStrip(ToolsText.FilesTabTitle));   // 2026-10-10; Image later that day
        Assert.Equal("         │ ↳ Docker · ComfyUI · YouTube ", SubStrip(ToolsText.AppsTabTitle));   // EventLog 2026-10-15 until later that day (Admin); 2026-10-10; ClaudeCode first until later that day; HomeAssistant until 2026-10-11
        Assert.Equal("         │ ↳ Network · EventLog · Services · Tasks · Directory ", SubStrip(ToolsText.AdminTabTitle));   // Network first since 2026-10-16 (the user's order); later still on 2026-10-15 (the admin-tools plan)
        Assert.Equal("         │ ↳ Web · Shell · Timers · Ask · Options ", SubStrip(ToolsText.CoreTabTitle));   // the user's order since 2026-10-16
        Assert.Equal("         │ ↳ HomeAssistant · Shopping ", SubStrip(ToolsText.HomeTabTitle));   // 2026-10-11 (the user's ask)
    }

    [Fact]
    public async Task OnThePane_TheDatabaseTab_ShowsTheSecondStrip_AndTheFirstMenusRows()
    {
        var (menu, pane, _) = PaneMenu();
        Push(ToTab(ToolsText.DatabaseTabTitle));
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(LeafIndex(ToolsText.SqlTabTitle), LeafIndex(ToolsText.DatabaseTabTitle));
        Assert.Contains(Strips(ToolsText.DatabaseTabTitle) + HeadingOver(ToolsText.SqlTabTitle, SettingsField.SqlTools) + "▸ " + SettingsMenu.FieldName(SettingsField.SqlTools) + "  ", Output);   // under its first heading (2026-10-10)
        Assert.DoesNotContain("\n" + Titled(Strip) + "\n \n▸ SQL  ", Output);   // no row per menu any more
        pane.Dispose();
    }

    /// <summary>The Files and Apps (Integrations until 2026-10-11) groups (2026-10-10, the user's pick): each a second strip, its first menu's rows under it.</summary>
    [Fact]
    public async Task OnThePane_TheFilesAndAppsTabs_ShowTheSecondStrip_AndTheFirstMenusRows()
    {
        var (menu, pane, _) = PaneMenu();
        Push(ToTab(ToolsText.FilesTabTitle));
        Push(Keys.Tab);                              // Apps, on Docker (ClaudeCode until later on 2026-10-10)
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(LeafIndex(ToolsText.FileMenuTitle), LeafIndex(ToolsText.FilesTabTitle));
        Assert.Contains(Strips(ToolsText.FilesTabTitle) + HeadingOver(ToolsText.FileMenuTitle, SettingsField.FileTools) + "▸ " + SettingsMenu.FieldName(SettingsField.FileTools) + "  ", Output);
        Assert.Contains(Strips(ToolsText.AppsTabTitle) + HeadingOver(ToolsText.DockerTabTitle, TabFields(ToolsText.DockerTabTitle)[0]) + "▸ " + SettingsMenu.FieldName(TabFields(ToolsText.DockerTabTitle)[0]) + "  ", Output);
        Assert.Equal(LeafIndex(ToolsText.DockerTabTitle) + 1, LeafIndex(ToolsText.ComfyTabTitle));   // EventLog between them on 2026-10-15 until later that day (Admin);   // ComfyUI and YouTube after Docker since 2026-10-11 (HomeAssistant between them, off to Home), off Media
        Assert.Equal(LeafIndex(ToolsText.HomeAssistantTabTitle) + 1, LeafIndex(ToolsText.ShoppingMenuTitle));   // Home: HomeAssistant, then Shopping (2026-10-11)
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_TheMediaTab_ShowsTheSecondStrip_AndTheFirstMenusRows()
    {
        var (menu, pane, _) = PaneMenu();
        Push(ToTab(ToolsText.DevicesTabTitle));
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains(Strips(ToolsText.DevicesTabTitle) + HeadingOver(ToolsText.CameraTabTitle, SettingsField.CameraTools) + "▸ " + SettingsMenu.FieldName(SettingsField.CameraTools) + "  ", Output);   // Camera first since 2026-10-16 (Print until then)
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_TheArrowsWalkEveryMenu_FromOneGroupIntoTheNext_AndBack()
    {
        var (menu, pane, _) = PaneMenu();
        Push(ToTab(ToolsText.OracleTabTitle));      // Database's last menu
        Push(Keys.Right);                           // Devices' first, Camera (Print until 2026-10-16)
        Push(Keys.Left);                            // Oracle again
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        string oracle = Strips(ToolsText.DatabaseTabTitle) + HeadingOver(ToolsText.OracleTabTitle, SettingsField.OracleTools) + "▸ " + SettingsMenu.FieldName(SettingsField.OracleTools) + "  ";
        string camera = Strips(ToolsText.DevicesTabTitle) + HeadingOver(ToolsText.CameraTabTitle, SettingsField.CameraTools) + "▸ " + SettingsMenu.FieldName(SettingsField.CameraTools) + "  ";
        Assert.Contains(camera, Output);
        Assert.True(Output.LastIndexOf(oracle, StringComparison.Ordinal) > Output.IndexOf(camera, StringComparison.Ordinal));
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_TabStepsPastAGroup_AndShiftTabComesBackToTheMenuLastShown()
    {
        var (menu, pane, _) = PaneMenu();
        Push(ToTab(ToolsText.SqliteTabTitle));      // Database, SQLite
        Push(Keys.Tab);                             // Devices, on its first
        Push(Keys.Tab);                             // Home, on its first
        Push(Keys.ShiftTab, Keys.ShiftTab);         // Devices, then Databases on SQLite
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains("\n" + Titled(Strip) + "\n \n" + Heading("── Clock · 3") + "\n", Output);
        string sqlite = Strips(ToolsText.DatabaseTabTitle) + HeadingOver(ToolsText.SqliteTabTitle, SettingsField.SqliteTools) + "▸ " + SettingsMenu.FieldName(SettingsField.SqliteTools) + "  ";
        Assert.True(Output.LastIndexOf(sqlite, StringComparison.Ordinal) > Output.LastIndexOf(Strips(ToolsText.DevicesTabTitle), StringComparison.Ordinal));
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
        Assert.Contains("\n" + Titled(Strip) + "\n" + SubStrip(ToolsText.DatabaseTabTitle) + "\n  · " + name + ": on\n" + HeadingOver(ToolsText.MySqlTabTitle, SettingsField.MySqlTools) + "▸ " + name + "  ", Output);   // the rebuilt page kept MySQL
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

        Assert.Contains("\n" + SubStrip(ToolsText.DatabaseTabTitle) + "\n \n" + HeadingOver(ToolsText.PostgresTabTitle, SettingsField.PostgresTools) + "▸ " + SettingsMenu.FieldName(SettingsField.PostgresTools) + "  ", Output);
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
        Assert.Equal(ToolsText.Label + " › Devices", ToolsMenu.GroupCrumb(TabIndex(ToolsText.DevicesTabTitle)));
        string picker = "\n" + Titled(ToolsText.Label + " › Devices › Camera shutter") + "\n";
        Assert.Contains(picker, Output);
        string after = Output[Output.LastIndexOf(picker, StringComparison.Ordinal)..];
        Assert.Contains("\n" + SubStrip(ToolsText.DevicesTabTitle) + "\n", after);   // back on Camera's rows
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
        Push(Enumerable.Repeat(Keys.Down, TabFields(ToolsText.YouTubeTabTitle).ToList().IndexOf(SettingsField.YouTubeAutoplay)).ToArray());   // YouTube autoplay (after the search rows since 2026-10-10)
        Push(Space);                                    // off
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
        int media = TabIndex(ToolsText.DevicesTabTitle);
        var subs = new int[ToolsText.TabTitles.Count];
        subs[database] = 4;
        var page = ToolsMenu.Page(ToolsText.OfferedRows(Facts()), saved, settings, database, subs: subs);

        Assert.Equal(["SQL", "MySQL", "SQLite", "PostgreSQL", "Oracle"], page.Tabs![database].SubTabs!.Select(t => t.Title));
        Assert.Equal(["Camera", "Screen", "Print"], page.Tabs[media].SubTabs!.Select(t => t.Title));   // the user's order since 2026-10-16; ComfyUI and YouTube on Integrations since 2026-10-10
        Assert.Equal(4, page.Sub);
        Assert.Equal(TabFields(ToolsText.OracleTabTitle).Count + SettingsMenu.ToolsMenuSections[ToolsText.MenuIndex(ToolsText.OracleTabTitle)].Count, page.Rows.Count);   // its headings too (2026-10-10)
        Assert.Null(page.Tabs[database].Shown.Footer!(0));   // the first heading says nothing
        Assert.Equal(SettingsMenu.FieldFooter(SettingsField.OracleTools).Text, page.Tabs[database].Shown.Footer!(1)!.Text);
        Assert.Null(page.Tabs[database].Shown.Footer!(page.Rows.Count));
        Assert.Equal(SettingsMenu.FieldFooter(SettingsField.CameraShutter).Text, page.Tabs[media].SubTabs![0].Footer!(2)!.Text);   // Camera first since 2026-10-16
        Assert.True(page.Tabs[media].SubTabs!.All(t => t.SpaceToggles));
        Assert.Equal(settings.GroupBadge(ToolsText.TabMenus[database - 1], saved), page.Tabs[database].Badge);
        Assert.NotNull(page.Tabs[database].Badge);
        Assert.Equal(0, ToolsMenu.Page(ToolsText.OfferedRows(Facts()), saved, settings, media).Sub);   // no subs: each group's first
        pane.Dispose();
    }

    /// <summary>
    /// A menu whose switch is off is <see cref="MenuTab.Off"/> (2026-10-16, the user's ask: its title dim and italic on the strip), and a
    /// group when every menu of it with a switch is; Core's Options has none and is never off, Shell is off under the policy <c>off</c>.
    /// </summary>
    [Fact]
    public void AMenuWhoseSwitchIsOff_IsOff_AndAGroupWhenAllItsMenusAre()
    {
        var (_, pane, settings) = PaneMenu();
        int database = TabIndex(ToolsText.DatabaseTabTitle);
        int core = TabIndex(ToolsText.CoreTabTitle);
        MenuPage PageOf() => ToolsMenu.Page(ToolsText.OfferedRows(Facts()), _settings.Current, settings, database);
        MenuTab Sub(MenuPage page, int tab, string title) => page.Tabs![tab].SubTabs!.Single(t => t.Title == title);

        _settings.Update(d =>
        {
            d.SqlTools = false;
            d.MySqlTools = d.SqliteTools = d.PostgresTools = d.OracleTools = true;
            d.WebTools = d.TimerTools = d.AskUser = true;
            d.ShellCommandPolicy = "ask";
        });
        var page = PageOf();
        Assert.Equal([true, false, false, false, false], page.Tabs![database].SubTabs!.Select(t => t.Off));
        Assert.False(page.Tabs[database].Off);
        Assert.False(page.Tabs[core].Off);
        Assert.False(Sub(page, core, ToolsText.ShellTabTitle).Off);
        Assert.False(page.Tabs[0].Off);   // Offered has no switch

        _settings.Update(d => d.MySqlTools = d.SqliteTools = d.PostgresTools = d.OracleTools = false);
        Assert.True(PageOf().Tabs![database].Off);

        _settings.Update(d =>
        {
            d.WebTools = d.TimerTools = d.AskUser = false;
            d.ShellCommandPolicy = "off";
        });
        page = PageOf();
        Assert.True(page.Tabs![core].Off);
        Assert.False(Sub(page, core, ToolsText.OptionsTabTitle).Off);
        Assert.True(Sub(page, core, ToolsText.ShellTabTitle).Off);

        _settings.Update(d => d.ShellCommandPolicy = "nonsense");   // an unknown word reads off, as Resolve falls back to off
        Assert.True(Sub(PageOf(), core, ToolsText.ShellTabTitle).Off);
        _settings.Update(d => d.ShellCommandPolicy = "yolo");
        page = PageOf();
        Assert.False(Sub(page, core, ToolsText.ShellTabTitle).Off);
        Assert.False(page.Tabs![core].Off);

        // The strip draws it: the group's second strip carries TabOff's markup on SQL's title, the rest stay idle.
        _settings.Update(d => d.SqlTools = false);
        _settings.Update(d => d.MySqlTools = true);
        string strips = string.Concat(MenuPane.TopRows(PageOf(), 240));
        Assert.Contains($"[{Theme.TabActiveOff.ToMarkup()}]SQL[/]", strips);   // the sub-tab on show
        Assert.Contains($"[{Theme.TabIdle.ToMarkup()}]MySQL[/]", strips);
        Assert.Contains($"[{Theme.TabOff.ToMarkup()}]SQLite[/]", strips);
        pane.Dispose();
    }

    /// <summary>Every <c>/tools</c> menu but Options has a switch the strip can read (2026-10-16): a new menu without one first fails here.</summary>
    [Fact]
    public void EveryMenuButOptions_HasASwitch()
    {
        var saved = new AppSettingsData();
        for (int m = 0; m < ToolsText.MenuTitles.Count; m++)
        {
            Assert.True((ToolsText.MenuSwitchOn(m, saved) is null) == (ToolsText.MenuTitles[m] == ToolsText.OptionsTabTitle), ToolsText.MenuTitles[m]);
        }
    }

    /// <summary>
    /// The Tools pane keeps its height on a group: the second strip's row is taken out of the padding under the list. Offered is the one
    /// plain tab since later on 2026-10-10 (Ask under Standard), so the group's menu is held to its frame.
    /// </summary>
    [Fact]
    public async Task OnThePane_AGroupsMenu_IsAsTallAsAPlainTab()
    {
        var (menu, pane, _) = PaneMenu();
        Push(ToTab(ToolsText.SqlTabTitle));
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        string sqlRow = HeadingOver(ToolsText.SqlTabTitle, SettingsField.SqlTools) + "▸ " + SettingsMenu.FieldName(SettingsField.SqlTools);
        Assert.Equal(FrameRows(Output, Titled(Strip), Heading("── Clock · 3")), FrameRows(Output, Titled(Strip) + "\n" + SubStrip(ToolsText.DatabaseTabTitle), sqlRow));
        pane.Dispose();
    }

    /// <summary>
    /// The rows of the first frame drawn under <paramref name="strip"/> whose list starts with <paramref name="firstRow"/>: from the strip
    /// down to the hint row, the band included. A rule is a row of nothing but the rule glyph (a heading has its title in it).
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
            if (lines[i].Length > 0 && lines[i].All(c => c == '─') && ++ruleCount == 2)
            {
                return i + 2;
            }
        }

        throw new InvalidOperationException("no hint rule under " + strip);
    }
}
