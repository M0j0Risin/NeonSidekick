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
}
