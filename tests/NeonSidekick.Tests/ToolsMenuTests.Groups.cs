using NeonSidekick.App;
using NeonSidekick.Settings;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// <c>/tools</c>' Media and Database tabs (2026-10-09, the user's call): a row per menu with its switch, Space flipping it, Enter
/// opening the five menus as a strip of their own under <c>Tools › Database</c>, ESC coming back to the tab on the menu last shown.
/// </summary>
public partial class ToolsMenuTests
{
    private static readonly ConsoleKeyInfo Space = new(' ', ConsoleKey.Spacebar, false, false, false);

    [Fact]
    public async Task OnThePane_TheDatabaseTab_IsARowPerMenu_WithItsSwitch()
    {
        _settings.Update(d => { d.SqlTools = true; d.MySqlTools = false; d.SqliteTools = true; d.PostgresTools = false; d.OracleTools = false; });
        var (menu, pane, _) = PaneMenu();
        Push(Enumerable.Repeat(Keys.Right, TabIndex(ToolsText.DatabaseTabTitle)).ToArray());
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        // The titles padded to the longest, Postgres's 8, and the gap: the switch's value after them, the menus in their old strip order.
        AssertTabEnds("\n" + Titled(Strip) + "\n \n▸ SQL       on\n  MySQL     off\n  SQLite    on\n  Postgres  off\n  Oracle    off\n", 100);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_TheMediaTab_IsARowPerMenu_WithItsSwitch()
    {
        _settings.Update(d => { d.PrintTools = false; d.CameraTools = true; d.ScreenTools = false; d.ComfyTools = true; d.YouTubeTools = false; });
        var (menu, pane, _) = PaneMenu();
        Push(Enumerable.Repeat(Keys.Right, TabIndex(ToolsText.MediaTabTitle)).ToArray());
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        AssertTabEnds("\n" + Titled(Strip) + "\n \n▸ Print    off\n  Camera   on\n  Screen   off\n  ComfyUI  on\n  YouTube  off\n", 100);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_SpaceOnAMenuRow_FlipsItsSwitch_AndTheRowFollows()
    {
        _settings.Update(d => d.MySqlTools = false);
        var (menu, pane, _) = PaneMenu();
        Push(Enumerable.Repeat(Keys.Right, TabIndex(ToolsText.DatabaseTabTitle)).ToArray());
        Push(Keys.Down, Space);                     // MySQL on
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.True(_settings.Current.MySqlTools);
        Assert.Contains("\n▸ MySQL     on\n", Output);   // the cursor kept on its row
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_EnterOnAMenu_OpensTheFiveAsAStrip_OnIt_AndEscComesBackToTheTab_OnTheMenuLastShown()
    {
        var (menu, pane, settings) = PaneMenu();
        Push(Enumerable.Repeat(Keys.Right, TabIndex(ToolsText.DatabaseTabTitle)).ToArray());
        Push(Keys.Down, Keys.Down, Keys.Enter);     // SQLite: its rows under the group's strip
        Push(Keys.Right);                           // Postgres, the next menu
        Push(Keys.Escape);                          // back on the Database tab, the cursor on Postgres
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        string strip = ToolsMenu.GroupCrumb(TabIndex(ToolsText.DatabaseTabTitle)) + " │ SQL · MySQL · SQLite · Postgres · Oracle ";
        Assert.Equal(ToolsText.Label + " › Database", ToolsMenu.GroupCrumb(TabIndex(ToolsText.DatabaseTabTitle)));
        Assert.Contains("\n" + Titled(strip) + "\n \n▸ SQLite tools  ", Output);
        Assert.Contains("\n" + Titled(strip) + "\n \n▸ PostgreSQL tools  ", Output);
        Assert.Contains("\n" + Titled(Strip) + "\n \n  SQL       ", Output);
        Assert.Contains("\n▸ Postgres  ", Output);
        Assert.Equal(SettingsMenu.Title, settings.Root);   // restored for /settings
        pane.Dispose();
    }

    /// <summary>A click on the group's crumb is ESC (later on 2026-10-09, the user's ask): back on the Database tab, the cursor on the menu shown.</summary>
    [Fact]
    public async Task OnThePane_AClickOnTheGroupsCrumb_ComesBackToTheTab_OnTheMenuLastShown()
    {
        var (menu, pane, input) = ClickablePaneMenu(100);
        input.Push(Enumerable.Repeat(Keys.Right, TabIndex(ToolsText.DatabaseTabTitle)).ToArray());
        input.Push(Keys.Down, Keys.Down, Keys.Enter);   // SQLite: its rows under the group's strip
        input.PushClick(2, 100);                        // the crumb, the strip's first row at 100
        input.Push(Keys.Escape);                        // the Tools pane closes

        await menu.ShowAsync(CancellationToken.None);

        string group = Titled(ToolsMenu.GroupCrumb(TabIndex(ToolsText.DatabaseTabTitle)) + " │ SQL · MySQL · SQLite · Postgres · Oracle ");
        Assert.Contains("\n" + group + "\n \n▸ SQLite tools  ", Output);
        string after = Output[Output.LastIndexOf(group, StringComparison.Ordinal)..];
        Assert.Contains("\n" + Titled(Strip) + "\n \n  SQL       ", after);
        Assert.Contains("\n▸ SQLite  ", after);
        pane.Dispose();
    }

    /// <summary>A click on one of the group's own titles still switches the menu: only the crumb goes back.</summary>
    [Fact]
    public async Task OnThePane_AClickOnAGroupsTitle_SwitchesTheMenu_AndStaysInTheGroup()
    {
        var (menu, pane, input) = ClickablePaneMenu(100);
        int database = TabIndex(ToolsText.DatabaseTabTitle);
        string crumb = ToolsMenu.GroupCrumb(database);
        var (_, places) = InfoPane.TabStripLayout(crumb, ["SQL", "MySQL", "SQLite", "Postgres", "Oracle"], 100);
        input.Push(Enumerable.Repeat(Keys.Right, database).ToArray());
        input.Push(Keys.Enter);                         // SQL's rows
        input.PushClick(places[3].Column + 1, 100);     // Postgres
        input.Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        string group = Titled(crumb + " │ SQL · MySQL · SQLite · Postgres · Oracle ");
        Assert.Contains("\n" + group + "\n \n▸ PostgreSQL tools  ", Output);
        Assert.Contains("\n▸ Postgres  ", Output);      // ESC back on the menu the click showed
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_AMenusPicker_IsTitledUnderTheGroupsCrumb_AndItSaves()
    {
        var (menu, pane, _) = PaneMenu();
        Push(ToTab(ToolsText.CameraTabTitle));      // Media, then Camera's rows
        Push(Keys.Down, Keys.Enter);                // Camera shutter: the picker
        Push(Keys.Down, Keys.Enter);                // the second choice
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.NotEqual(new AppSettingsData().CameraShutter, _settings.Current.CameraShutter);
        Assert.Contains("\n" + Titled(ToolsText.Label + " › Media › Camera shutter") + "\n", Output);
        Assert.Contains("\n" + Titled(ToolsMenu.GroupCrumb(TabIndex(ToolsText.MediaTabTitle)) + " │ Print · Camera · Screen · ComfyUI · YouTube ") + "\n", Output);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_SpaceInAMenu_FlipsTheRow_AsOnAPlainTab()
    {
        _settings.Update(d => d.YouTubeAutoplay = true);
        var (menu, pane, _) = PaneMenu();
        Push(ToTab(ToolsText.YouTubeTabTitle));
        Push(Keys.Down, Keys.Down, Keys.Down, Space);   // YouTube autoplay off
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.False(_settings.Current.YouTubeAutoplay);
        pane.Dispose();
    }

    [Fact]
    public void TheGroupTabs_FooterSaysTheMenusSwitch()
    {
        var saved = _settings.Current;
        var (_, pane, settings) = PaneMenu();
        var page = ToolsMenu.Page(ToolsText.OfferedRows(Facts()), saved, settings, TabIndex(ToolsText.DatabaseTabTitle));

        Assert.Equal(SettingsMenu.FieldFooter(SettingsField.OracleTools).Text, page.Footer!(TabIndex(ToolsText.DatabaseTabTitle), 4)!.Text);
        Assert.Null(page.Footer!(TabIndex(ToolsText.DatabaseTabTitle), 5));
        Assert.Equal(5, page.Tabs![TabIndex(ToolsText.MediaTabTitle)].Rows.Count);
        var group = ToolsMenu.GroupPage(TabIndex(ToolsText.MediaTabTitle), saved, settings, 1);
        Assert.Equal(["Print", "Camera", "Screen", "ComfyUI", "YouTube"], group.Tabs!.Select(t => t.Title));
        Assert.Equal(1, group.Tab);
        Assert.Equal(SettingsMenu.FieldFooter(SettingsField.CameraShutter).Text, group.Footer!(1, 1)!.Text);
        pane.Dispose();
    }

    /// <summary>The group's strip keeps the Tools pane's height (later on 2026-10-09, the user's ask: it shrank to its tallest menu).</summary>
    [Fact]
    public void AGroupPage_KeepsTheToolsPagesHeight()
    {
        var saved = _settings.Current;
        var (_, pane, settings) = PaneMenu();
        var tools = ToolsMenu.Page(ToolsText.OfferedRows(Facts()), saved, settings, TabIndex(ToolsText.DatabaseTabTitle));
        int media = TabIndex(ToolsText.MediaTabTitle);
        var alone = ToolsMenu.GroupPage(media, saved, settings, 0);
        var kept = ToolsMenu.GroupPage(media, saved, settings, 0, tools);

        Assert.Equal(alone.Tabs!.Max(t => t.Rows.Count), MenuPane.TabBodyRows(alone, 100));
        Assert.True(MenuPane.TabBodyRows(tools, 100) > MenuPane.TabBodyRows(alone, 100));   // the Offered list is the taller
        Assert.Equal(MenuPane.TabBodyRows(tools, 100), MenuPane.TabBodyRows(kept, 100));
        Assert.Same(tools, kept.HeightOf);
        pane.Dispose();
    }

    /// <summary>On the pane, the group's strip is padded down to where the Tools pane's list ended: the hint row stays put.</summary>
    [Fact]
    public async Task OnThePane_AGroupsStrip_IsAsTallAsTheToolsPane()
    {
        var (menu, pane, _) = PaneMenu();
        Push(ToTab(ToolsText.SqlTabTitle));     // Database, then SQL's rows
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        string group = Titled(ToolsMenu.GroupCrumb(TabIndex(ToolsText.DatabaseTabTitle)) + " │ SQL · MySQL · SQLite · Postgres · Oracle ");
        Assert.Equal(FrameRows(Output, Titled(Strip), "▸ SQL  "), FrameRows(Output, group, "▸ SQL tools"));   // the Database tab's frame, the cursor on SQL
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
