using NeonSidekick.App;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// The offered button of the ComfyUI, SQL, Oracle, MySQL, SQLite, PostgreSQL and UNC tool pages (2026-10-05, the user's ask: the
/// police page's strings button for the "… offered" checklists): the count on the title row, O opening the row's own checklist,
/// the page coming back with the count read again.
/// </summary>
public partial class ToolsMenuTests
{
    [Fact]
    public void OfferedButtons_CarryTheRowsValue_LitUnlessNone()
    {
        Assert.Equal("☑ offered (2 of 5)", SettingsMenu.OfferedTitle("2 of 5"));   // a real space after the glyph (the user's ask)
        Assert.Equal("☑ offered (none of 3)", SettingsMenu.OfferedTitle("none of 3"));
        Assert.Equal([new MenuButton("☑ offered (2 of 5)", 'o', true)], SettingsMenu.OfferedButtons("2 of 5"));
        Assert.Equal([new MenuButton("☑ offered (none of 3)", 'o', false)], SettingsMenu.OfferedButtons("none of 3"));
        Assert.Equal(1, TextCells.Width(SettingsMenu.OfferedButton[..1]));
        Assert.Equal(' ', SettingsMenu.OfferedButton[1]);
        Assert.Equal("Enter = choose · O = offered · ESC = back", SettingsMenu.OfferedToggleKeys);
    }

    /// <summary>Every offered switch's page carries the button with its row's value; with nothing defined, O says so and the page comes back.</summary>
    [Theory]
    [InlineData(SettingsField.ComfyTools, SettingsField.ComfyWorkflowsOffered)]
    [InlineData(SettingsField.SqlTools, SettingsField.SqlConnectionsOffered)]
    [InlineData(SettingsField.OracleTools, SettingsField.OracleConnectionsOffered)]
    [InlineData(SettingsField.MySqlTools, SettingsField.MySqlConnectionsOffered)]
    [InlineData(SettingsField.SqliteTools, SettingsField.SqliteDatabasesOffered)]
    [InlineData(SettingsField.PostgresTools, SettingsField.PostgresConnectionsOffered)]
    [InlineData(SettingsField.UncTools, SettingsField.UncSharesOffered)]
    public async Task ShowSwitch_AnOfferedGroup_HasTheOfferedButton_AndComesBack(SettingsField toolSwitch, SettingsField offered)
    {
        var (menu, pane, _) = PaneMenu();
        bool was = SettingsMenu.FieldValue(toolSwitch, _settings.Current, _settings.ProfileDirectory) == "on";
        Push(Keys.Char('o'), Keys.Escape);

        await menu.ShowSwitchAsync(toolSwitch, CancellationToken.None);

        string value = SettingsMenu.FieldValue(offered, _settings.Current, _settings.ProfileDirectory);
        Assert.StartsWith("none of ", value, StringComparison.Ordinal);
        string page = "\n" + Titled(ToolsText.Label + " › " + SettingsMenu.FieldName(toolSwitch) + "   " + SettingsMenu.OfferedTitle(value) + " ") + "\n";
        int first = _console.Output.IndexOf(page, StringComparison.Ordinal);
        Assert.True(first >= 0, _console.Output);
        Assert.True(_console.Output.IndexOf(page, first + 1, StringComparison.Ordinal) > first, _console.Output);   // back on the page after the error
        Assert.Contains(SettingsMenu.OfferedToggleKeys, _console.Output);
        Assert.Equal(was, SettingsMenu.FieldValue(toolSwitch, _settings.Current, _settings.ProfileDirectory) == "on");
        Assert.False(pane.OverlayOpen);
        pane.Dispose();
    }

    /// <summary>O opens the checklist, a tick saves, ESC comes back to the page with the count lit, and no "unchanged" follows.</summary>
    [Fact]
    public async Task ShowSwitch_Sql_TheOfferedButton_TicksAConnection_AndTheCountFollows()
    {
        string path = NeonSidekick.Sql.SqlConfigFile.ProfilePath(_settings.ProfileDirectory);
        Directory.CreateDirectory(_settings.ProfileDirectory);
        File.WriteAllText(path, """{ "connections": { "aw": { "server": "x", "auth": "windows" }, "prod": { "server": "y", "auth": "windows" } } }""");
        var (menu, pane, _) = PaneMenu();
        Push(Keys.Char('o'), Keys.Enter, Keys.Escape, Keys.Escape);   // the button, aw ticked, back to the page, out

        await menu.ShowSwitchAsync(SettingsField.SqlTools, CancellationToken.None);

        Assert.Equal(["aw"], _settings.Current.SqlConnectionsOffered);
        string before = "\n" + Titled(ToolsText.Label + " › SQL tools   ☑ offered (none of 2) ") + "\n";
        string list = ToolsText.Label + " › " + SettingsMenu.FieldName(SettingsField.SqlConnectionsOffered) + "   ";   // the checklist, its own buttons after
        string after = "\n" + Titled(ToolsText.Label + " › SQL tools   ☑ offered (1 of 2) ") + "\n";
        Assert.True(_console.Output.IndexOf(before, StringComparison.Ordinal) < _console.Output.IndexOf(list, StringComparison.Ordinal), _console.Output);
        Assert.True(_console.Output.IndexOf(list, StringComparison.Ordinal) < _console.Output.LastIndexOf(after, StringComparison.Ordinal), _console.Output);
        Assert.DoesNotContain("  · " + SettingsMenu.UnchangedNotice + "\n", _console.Output);
        Assert.False(pane.OverlayOpen);
        pane.Dispose();
    }

    /// <summary>A switch with no offered checklist keeps the plain on/off page.</summary>
    [Fact]
    public async Task ShowSwitch_ATool_WithoutAChecklist_HasNoOfferedButton()
    {
        var (menu, pane, _) = PaneMenu();
        Push(Keys.Char('o'), Keys.Escape);

        await menu.ShowSwitchAsync(SettingsField.DockerTools, CancellationToken.None);

        Assert.DoesNotContain(SettingsMenu.OfferedButton, _console.Output);
        pane.Dispose();
    }
}
