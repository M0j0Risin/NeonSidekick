using NeonSidekick.App;
using NeonSidekick.Sql;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// The connection wizards' edit (2026-10-05, the user's ask: "add/edit"): the first page of the entries, a pick opening the
/// summary prefilled, a save writing the entry back in its place — renamed, its password kept, its place in the offered list.
/// </summary>
public partial class ToolsMenuTests
{
    [Fact]
    public void ConnectionEdit_Strings_AndTheOfferedList_ArePinned()
    {
        Assert.Equal("+ New connection", SettingsMenu.ConnectionWizardNewRow("connection"));
        Assert.Equal("aw     profile C:\\p\\sql.json", SettingsMenu.ConnectionWizardEntryRow("aw", 5, global: false, @"C:\p\sql.json"));
        Assert.Equal("Nothing changed.", SettingsMenu.ConnectionWizardUnchangedNotice);
        Assert.Equal("•••••• (stored)", SettingsMenu.SqlWizardMaskedKept);
        Assert.Equal("Saved 'aw' in x.", SqlText.ConnectionChanged("aw", "x"));
        Assert.Equal("Could not save 'aw': why.", SqlText.ConnectionChangeFailed("aw", "why"));
        Assert.Equal("SQL add/edit connection", SettingsMenu.FieldName(SettingsField.SqlAddConnection));
        Assert.Equal("Oracle add/edit connection", SettingsMenu.FieldName(SettingsField.OracleAddConnection));
        Assert.Equal("MySQL add/edit connection", SettingsMenu.FieldName(SettingsField.MySqlAddConnection));
        Assert.Equal("PostgreSQL add/edit connection", SettingsMenu.FieldName(SettingsField.PostgresAddConnection));
        Assert.Equal("SQLite add/edit database", SettingsMenu.FieldName(SettingsField.SqliteAddDatabase));
        Assert.Equal("UNC add/edit share", SettingsMenu.FieldName(SettingsField.UncAddShare));

        // An add appends; a rename keeps the old name's place; a hidden save takes it out; the case of a match does not matter.
        Assert.Equal(["a", "new"], SettingsMenu.ConnectionOfferedAfterSave(["a"], null, "new", offer: true));
        Assert.Equal(["a"], SettingsMenu.ConnectionOfferedAfterSave(["a"], null, "new", offer: false));
        Assert.Equal(["new"], SettingsMenu.ConnectionOfferedAfterSave(null, null, "new", offer: true));
        Assert.Equal(["a", "renamed", "c"], SettingsMenu.ConnectionOfferedAfterSave(["a", "OLD", "c"], "old", "renamed", offer: true));
        Assert.Equal(["a", "c"], SettingsMenu.ConnectionOfferedAfterSave(["a", "old", "c"], "old", "renamed", offer: false));
        Assert.Equal(["a", "old"], SettingsMenu.ConnectionOfferedAfterSave(["a"], "old", "old", offer: true));   // an edit of one not offered, offered now
        Assert.Equal(0, SettingsMenu.ConnectionWizardStartCursor(null, []));
        Assert.Equal(0, SettingsMenu.ConnectionWizardStartCursor("aw", ["AW"]));
        Assert.Equal(1, SettingsMenu.ConnectionWizardStartCursor("aw", null));
    }

    /// <summary>
    /// SQL: the first page lists the saved ones after <c>+ New connection</c>; the pick opens the summary prefilled (the password
    /// masked as stored); Name and Server changed from it; the save renames the entry in its place, comments and the other entry
    /// kept, the password and the unasked <c>credential</c> carried, and the offered list keeps its order.
    /// </summary>
    [Fact]
    public async Task OnThePane_TheSqlWizard_EditsASavedConnection_RenamesIt_KeepsItsPassword_AndItsPlaceInTheOfferedList()
    {
        string path = SqlConfigFile.ProfilePath(_settings.ProfileDirectory);
        Directory.CreateDirectory(_settings.ProfileDirectory);
        File.WriteAllText(path, """
            {
              // the user's note
              "connections": {
                "aw": { "server": "old", "auth": "sql", "user": "reader", "password": "s3cret", "credential": "Team/aw", "description": "the sample" }, // kept
                "first": { "server": "a", "auth": "windows" }
              }
            }
            """);
        _settings.Update(d => d.SqlConnectionsOffered = ["aw", "first"]);
        var (menu, _, _) = PaneMenu();
        OpenSqlWizard();
        Push(Keys.Down, Keys.Enter);                                  // aw, under + New connection
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter);   // the Name row (four actions, File, Name)
        Push(Keys.Backspace, Keys.Backspace);
        Type("adventure");
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter);   // the Server row
        Push(Keys.Backspace, Keys.Backspace, Keys.Backspace);
        Type("new");
        Push(Keys.Enter);                                             // Save, and offer it (the cursor's start: it was offered)
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains(SettingsMenu.ConnectionWizardPickQuestion, Output);
        Assert.Contains("+ New connection", Output);
        Assert.Contains(SettingsMenu.SqlWizardMaskedKept, Output);
        Assert.Contains("Saved 'adventure' in ", Output);
        Assert.DoesNotContain("s3cret", Output);
        string text = File.ReadAllText(path);
        Assert.Contains("// the user's note", text);
        Assert.Contains("}, // kept", text);
        var loaded = SqlConfigFile.Load(path);
        Assert.Empty(loaded.Problems);
        Assert.Equal(["adventure", "first"], loaded.Connections.Select(c => c.Name));
        var adventure = loaded.Connections[0].Config;
        Assert.Equal(("new", "reader", "Team/aw", "the sample"), (adventure.Server, adventure.User, adventure.Credential, adventure.Description));
        Assert.Equal("s3cret", SqlSecrets.Resolve(loaded.Connections[0]).Value);
        Assert.Equal(["adventure", "first"], _settings.Current.SqlConnectionsOffered);
    }

    /// <summary>An edit's ESC on its summary is back to the list, Cancel there says nothing changed, ESC on the list ends the visit; the file untouched.</summary>
    [Fact]
    public async Task OnThePane_TheSqlWizard_AnEditsEscGoesBackToTheList_AndCancelChangesNothing()
    {
        string path = SqlConfigFile.ProfilePath(_settings.ProfileDirectory);
        Directory.CreateDirectory(_settings.ProfileDirectory);
        const string Before = """{ "connections": { "aw": { "server": "x", "auth": "windows" } } }""";
        File.WriteAllText(path, Before);
        var (menu, _, _) = PaneMenu();
        OpenSqlWizard();
        Push(Keys.Down, Keys.Enter);                     // aw
        Push(Keys.Escape);                               // its summary: back to the list
        Push(Keys.Down, Keys.Enter);                     // aw again
        Push(Keys.Down, Keys.Down, Keys.Enter);          // Cancel, from Save hidden (aw is not offered)
        Push(Keys.Enter);                                // the row again (the cursor stays on it)
        Push(Keys.Escape);                               // the list: the visit ends
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains(SettingsMenu.ConnectionWizardUnchangedNotice, Output);
        Assert.Contains(SettingsMenu.SqlWizardCancelledNotice, Output);
        Assert.Equal(Before, File.ReadAllText(path));
    }

    /// <summary>
    /// The other five: a saved entry picked, renamed from its summary and saved hidden (the cursor's start, since it is not
    /// offered); the entry renamed in its file, its comment kept, its password (where it has one) still read.
    /// </summary>
    [Theory]
    [InlineData("oracle")]
    [InlineData("mysql")]
    [InlineData("postgres")]
    [InlineData("sqlite")]
    [InlineData("unc")]
    public async Task OnThePane_EachWizard_RenamesASavedEntry_KeepingItsPasswordAndComment(string family)
    {
        Directory.CreateDirectory(_settings.ProfileDirectory);
        string path;
        Func<IReadOnlyList<string>> names;
        Func<string?> password;
        Action open;
        switch (family)
        {
            case "oracle":
                path = NeonSidekick.Oracle.OracleConfigFile.ProfilePath(_settings.ProfileDirectory);
                File.WriteAllText(path, "{ \"connections\": { // mine\n\"old\": { \"dataSource\": \"localhost:1521/FREEPDB1\", \"user\": \"neon\", \"password\": \"pw\" } } }");
                names = () => NeonSidekick.Oracle.OracleConfigFile.Load(path).Connections.Select(c => c.Name).ToList();
                password = () => NeonSidekick.Oracle.OracleSecrets.Resolve(NeonSidekick.Oracle.OracleConfigFile.Load(path).Connections[0]).Value;
                open = () => OpenOracleRow(6);
                break;
            case "mysql":
                path = NeonSidekick.MySql.MySqlConfigFile.ProfilePath(_settings.ProfileDirectory);
                File.WriteAllText(path, "{ \"connections\": { // mine\n\"old\": { \"host\": \"localhost\", \"user\": \"neon\", \"password\": \"pw\" } } }");
                names = () => NeonSidekick.MySql.MySqlConfigFile.Load(path).Connections.Select(c => c.Name).ToList();
                password = () => NeonSidekick.MySql.MySqlSecrets.Resolve(NeonSidekick.MySql.MySqlConfigFile.Load(path).Connections[0]).Value;
                open = () => OpenMySqlRow(6);
                break;
            case "postgres":
                path = NeonSidekick.Postgres.PostgresConfigFile.ProfilePath(_settings.ProfileDirectory);
                File.WriteAllText(path, "{ \"connections\": { // mine\n\"old\": { \"host\": \"localhost\", \"user\": \"neon\", \"password\": \"pw\" } } }");
                names = () => NeonSidekick.Postgres.PostgresConfigFile.Load(path).Connections.Select(c => c.Name).ToList();
                password = () => NeonSidekick.Postgres.PostgresSecrets.Resolve(NeonSidekick.Postgres.PostgresConfigFile.Load(path).Connections[0]).Value;
                open = () => OpenPostgresRow(6);
                break;
            case "sqlite":
                path = NeonSidekick.Sqlite.SqliteConfigFile.ProfilePath(_settings.ProfileDirectory);
                File.WriteAllText(path, "{ \"databases\": { // mine\n\"old\": { \"path\": \"notes.db\", \"description\": \"the notes\" } } }");
                names = () => NeonSidekick.Sqlite.SqliteConfigFile.Load(path).Databases.Select(d => d.Name).ToList();
                password = () => NeonSidekick.Sqlite.SqliteConfigFile.Load(path).Databases[0].Config.Description;   // no password: the unasked-for row carried
                open = () => OpenSqliteRow(6);
                break;
            default:
                path = NeonSidekick.Unc.UncConfigFile.ProfilePath(_settings.ProfileDirectory);
                File.WriteAllText(path, "{ \"shares\": { // mine\n\"old\": { \"path\": \"//fs02/fin\", \"auth\": \"runas\", \"user\": \"CORP\\\\svc\", \"password\": \"pw\" } } }");
                names = () => NeonSidekick.Unc.UncConfigFile.Load(path).Shares.Select(s => s.Name).ToList();
                password = () => NeonSidekick.Unc.UncSecrets.Resolve(NeonSidekick.Unc.UncConfigFile.Load(path).Shares[0]).Value;
                open = () => OpenUncRow(5);
                break;
        }

        var (menu, _, _) = PaneMenu();
        open();
        Push(Keys.Down, Keys.Enter);                                  // old, under + New
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter); // from Save hidden (not offered) to the Name row
        Push(Keys.Backspace, Keys.Backspace, Keys.Backspace);
        Type("renamed");
        Push(Keys.Enter);                                             // Save, hidden
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(["renamed"], names());
        Assert.Equal(family == "sqlite" ? "the notes" : "pw", password());
        Assert.Contains("// mine", File.ReadAllText(path));
        Assert.Contains("Saved 'renamed' in ", Output);
        Assert.DoesNotContain("Added 'renamed'", Output);
    }
}
