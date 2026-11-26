using NeonSidekick.App;
using NeonSidekick.Services;
using NeonSidekick.Sql;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// The connection wizards' unsaved-changes question and cursor (2026-11-26, the user's asks): an edit's Esc or Cancel with a change asks
/// Keep editing, Save or Discard, Save keeping the entry offered or hidden as it is, a new entry's Cancel asks a yes/no, and Enter on a summary
/// row comes back to that row rather than to a save row.
/// </summary>
public partial class ToolsMenuTests
{
    private const string AwOnly = """{ "connections": { "aw": { "server": "x", "database": "db", "auth": "windows" } } }""";

    private string WriteAw()
    {
        string path = SqlConfigFile.ProfilePath(_settings.ProfileDirectory);
        Directory.CreateDirectory(NeonSidekick.Settings.Profiles.ConfigsDirectory(_settings.ProfileDirectory));
        File.WriteAllText(path, AwOnly);
        return path;
    }

    /// <summary>aw's Name changed to adventure from its summary (aw not offered: the cursor opens on Save, hidden).</summary>
    private void RenameAwToAdventure()
    {
        OpenSqlWizard();
        Push(Keys.Down, Keys.Enter);                                  // aw, under + New connection
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter); // from Save hidden to the Name row
        Push(Keys.Backspace, Keys.Backspace);
        Type("adventure");
    }

    [Fact]
    public void ConnectionStamp_ChangesWithWhatSaveWrites_AndNeverHoldsThePassword()
    {
        static SqlConnectionConfig Config() => new() { Server = "x", Database = "db", Auth = SqlConnectionConfig.SqlAuth, User = "reader" };
        static string Stamp(string name, bool global, SqlConnectionConfig config) =>
            SettingsMenu.ConnectionStamp(name, global, config, SqlJsonContext.Default.SqlConnectionConfig, x => x.Password = null);

        string opened = Stamp("aw", false, Config());
        Assert.Equal(opened, Stamp("aw", false, Config()));
        Assert.NotEqual(opened, Stamp("adventure", false, Config()));                 // a rename
        Assert.NotEqual(opened, Stamp("aw", true, Config()));                         // the home's file
        var moved = Config();
        moved.Server = "y";
        Assert.NotEqual(opened, Stamp("aw", false, moved));
        var explicitEncrypt = Config();
        explicitEncrypt.Encrypt = "optional";
        Assert.NotEqual(opened, Stamp("aw", false, explicitEncrypt));                 // an implicit default made explicit is a change Save writes
        var withPassword = Config();
        withPassword.Password = "s3cret-pw";
        string stamped = Stamp("aw", false, withPassword);
        Assert.DoesNotContain("s3cret-pw", stamped, StringComparison.Ordinal);
        Assert.Equal(opened, stamped);
        Assert.Equal("s3cret-pw", withPassword.Password);                             // the copy dropped it, not the config
        Assert.Contains("aw", SettingsMenu.LeaveQuestion("aw"), StringComparison.Ordinal);
        Assert.Contains("share", SettingsMenu.NewDiscardQuestion("share"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnThePane_TheSqlWizard_AnEditsEscWithAChange_AsksKeepSaveOrDiscard()
    {
        string path = WriteAw();
        var (menu, _, _) = PaneMenu();
        RenameAwToAdventure();
        Push(Keys.Escape, Keys.Enter);                                // asks; Keep editing
        Push(Keys.Escape, Keys.Escape);                               // asks; Esc keeps editing too
        Push(Keys.Escape, Keys.Down, Keys.Down, Keys.Enter);          // asks; Discard: back to the list
        Push(Keys.Escape);                                            // the list: the visit ends
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(AwOnly, File.ReadAllText(path));
        Assert.Contains(SettingsMenu.LeaveQuestion("aw"), Output);  // the saved name, not the draft's new one
        Assert.DoesNotContain(SettingsMenu.LeaveQuestion("adventure"), Output);
        Assert.Contains(SettingsMenu.ConnectionWizardDiscardedNotice, Output);
        Assert.Contains(SettingsMenu.SqlWizardCancelledNotice, Output);
    }

    /// <summary>Save on the question keeps the entry offered or hidden as it is now (2026-11-26, the user's pick).</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnThePane_TheSqlWizard_SaveFromTheQuestion_KeepsItsOfferedState(bool offered)
    {
        string path = WriteAw();
        _settings.Update(d => d.SqlConnectionsOffered = offered ? ["aw"] : null);
        var (menu, _, _) = PaneMenu();
        OpenSqlWizard();
        Push(Keys.Down, Keys.Enter);                                  // aw
        Push([.. Enumerable.Repeat(Keys.Down, offered ? 5 : 4), Keys.Enter]);   // the Name row, from the save the cursor opened on
        Push(Keys.Backspace, Keys.Backspace);
        Type("adventure");
        Push(Keys.Escape, Keys.Down, Keys.Enter);                     // asks; Save
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(["adventure"], SqlConfigFile.Load(path).Connections.Select(c => c.Name));
        Assert.Contains("Saved 'adventure' in ", Output);
        Assert.Equal(offered ? ["adventure"] : [], _settings.Current.SqlConnectionsOffered ?? []);
    }

    [Fact]
    public async Task OnThePane_TheSqlWizard_AnEditsCancelWithAChange_Asks()
    {
        string path = WriteAw();
        var (menu, _, _) = PaneMenu();
        RenameAwToAdventure();
        Push(Keys.Up, Keys.Up, Keys.Enter);                           // Cancel, over File (the cursor stayed on Name)
        Push(Keys.Down, Keys.Down, Keys.Enter);                       // Discard
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(AwOnly, File.ReadAllText(path));
        Assert.Contains(SettingsMenu.LeaveQuestion("aw"), Output);
        Assert.Contains(SettingsMenu.ConnectionWizardDiscardedNotice, Output);
    }

    [Fact]
    public async Task OnThePane_TheSqlWizard_AChangeUndone_LeavesWithoutAsking()
    {
        string path = WriteAw();
        var (menu, _, _) = PaneMenu();
        RenameAwToAdventure();
        Push(Keys.Enter);                                             // Name again (the cursor stayed on it)
        Push([.. Enumerable.Repeat(Keys.Backspace, "adventure".Length)]);
        Type("aw");
        Push(Keys.Escape);                                            // no question: back to the list
        Push(Keys.Escape);                                            // the visit ends
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(AwOnly, File.ReadAllText(path));
        Assert.DoesNotContain(SettingsMenu.LeaveQuestion("aw"), Output);
        Assert.DoesNotContain(SettingsMenu.ConnectionWizardDiscardedNotice, Output);
    }

    /// <summary>Enter on a row and the change made, the cursor is back on that row (2026-11-26, the user's report: it jumped to a save row).</summary>
    [Fact]
    public async Task OnThePane_TheSqlWizard_AfterARowsChange_TheCursorStaysOnIt()
    {
        string path = WriteAw();
        var (menu, _, _) = PaneMenu();
        RenameAwToAdventure();
        Push(Keys.Enter);                                             // on Name: Name again, not a save
        Push([.. Enumerable.Repeat(Keys.Backspace, "adventure".Length)]);
        Type("again");
        Push(Keys.Home, Keys.Down, Keys.Enter);                       // Save, hidden
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(["again"], SqlConfigFile.Load(path).Connections.Select(c => c.Name));
    }

    [Fact]
    public async Task OnThePane_TheSqliteWizard_AnEditsEscWithAChange_Asks_AndTheCursorStays()
    {
        string path = NeonSidekick.Sqlite.SqliteConfigFile.ProfilePath(_settings.ProfileDirectory);
        Directory.CreateDirectory(NeonSidekick.Settings.Profiles.ConfigsDirectory(_settings.ProfileDirectory));
        const string Before = """{ "databases": { "old": { "path": "notes.db", "description": "the notes" } } }""";
        File.WriteAllText(path, Before);
        var (menu, _, _) = PaneMenu();
        OpenSqliteRow(5);
        Push(Keys.Down, Keys.Enter);                                  // old, under + New
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter); // from Save hidden to the Name row
        Push(Keys.Backspace, Keys.Backspace, Keys.Backspace);
        Type("renamed");
        Push(Keys.Enter);                                             // on Name: Name again
        Push([.. Enumerable.Repeat(Keys.Backspace, "renamed".Length)]);
        Type("again");
        Push(Keys.Escape, Keys.Down, Keys.Down, Keys.Enter);          // asks; Discard
        Push(Keys.Escape, Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(Before, File.ReadAllText(path));
        Assert.Contains(SettingsMenu.LeaveQuestion("old"), Output);
        Assert.Contains(SettingsMenu.ConnectionWizardDiscardedNotice, Output);
    }

    [WindowsFact]   // a Mac shows no Services menu
    public async Task OnThePane_TheComputerWizard_AnEditsEscWithAChange_Asks()
    {
        string path = ServiceConfigFile.Family.ProfilePath(_settings.ProfileDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        const string Before = """{ "computers": { "app01": { "host": "app01.corp.example" } } }""";
        File.WriteAllText(path, Before);
        _settings.Update(d => d.ServiceComputersOffered = ["local"]);
        var (menu, _, _) = PaneMenu();
        OpenMenuRow(ToolsText.ServicesMenuTitle, SettingsField.ServiceAddComputer);
        Push(Keys.Down, Keys.Down, Keys.Enter);                       // app01, under + New computer and local (listed first, not in the file)
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter); // from Save hidden to the Name row
        Push([.. Enumerable.Repeat(Keys.Backspace, "app01".Length)]);
        Type("app02");
        Push(Keys.Escape, Keys.Down, Keys.Down, Keys.Enter);          // asks; Discard
        Push(Keys.Escape, Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(Before, File.ReadAllText(path));
        Assert.Contains(SettingsMenu.LeaveQuestion("app01"), Output);
        Assert.Contains(SettingsMenu.ConnectionWizardDiscardedNotice, Output);
    }

    [Fact]
    public async Task OnThePane_TheDirectoryWizard_AnEditsEscWithAChange_Asks()
    {
        string path = NeonSidekick.ActiveDirectory.DirectoryConfigFile.ProfilePath(_settings.ProfileDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        const string Before = """{ "domains": { "lab": { "domain": "lab.example", "server": "localhost" } } }""";
        File.WriteAllText(path, Before);
        var (menu, _, _) = PaneMenu();
        OpenMenuRow(ToolsText.DirectoryMenuTitle, SettingsField.DirectoryAddDomain);
        Push(Keys.Down, Keys.Enter);                                  // lab, under + New domain
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter); // from Save hidden (lab isn't offered) to the Name row
        Push([.. Enumerable.Repeat(Keys.Backspace, "lab".Length)]);
        Type("lab2");
        Push(Keys.Escape, Keys.Down, Keys.Down, Keys.Enter);          // asks; Discard
        Push(Keys.Escape, Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(Before, File.ReadAllText(path));
        Assert.Contains(SettingsMenu.LeaveQuestion("lab"), Output);
        Assert.Contains(SettingsMenu.ConnectionWizardDiscardedNotice, Output);
    }
}
