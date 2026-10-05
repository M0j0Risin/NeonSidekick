using Microsoft.Data.Sqlite;
using NeonSidekick.App;
using NeonSidekick.Settings;
using NeonSidekick.Sqlite;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>The SQLite tab of <c>/tools</c> (2026-10-04): after MySQL, its rows, the wizard over a real file, the checklist and the default pick.</summary>
public partial class ToolsMenuTests
{
    /// <summary>The SQLite tab (<see cref="ToTab"/>), then the row and Enter.</summary>
    private void OpenSqliteRow(int row) => Push([.. ToTab(ToolsText.SqliteTabTitle), .. Enumerable.Repeat(Keys.Down, row), Keys.Enter]);

    private string MakeSqliteFile(string name)
    {
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, name);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ConnectionString))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE a (x); CREATE TABLE b (y); CREATE VIEW v AS SELECT 1;";
            command.ExecuteNonQuery();
        }

        return path;
    }

    [Fact]
    public void TheTab_IsAfterMySql_WithItsRows_AndItsWordsArePinned()
    {
        Assert.Equal(TabIndex(ToolsText.MySqlTabTitle) + 1, TabIndex(ToolsText.SqliteTabTitle));
        Assert.Equal(
            [SettingsField.SqliteTools, SettingsField.SqliteProtectionMode, SettingsField.SqliteStatementsAllowed, SettingsField.SqliteDatabasesOffered, SettingsField.SqliteDefaultDatabase, SettingsField.SqliteSandboxFiles, SettingsField.SqliteAddDatabase, SettingsField.SqlitePercentMention, SettingsField.SqliteQueryMaxRows, SettingsField.SqliteQueryTimeoutSeconds, SettingsField.SqliteDatabasesProfile, SettingsField.SqliteDatabasesGlobal],
            TabFields(ToolsText.SqliteTabTitle));
        Assert.Equal("Enter to start database wizard", SettingsMenu.SqliteAddDatabaseLabel);
        Assert.Equal("Opened 'shop' read-only: 3 tables and views.", SettingsMenu.SqliteWizardTestOk("shop", 3));
        Assert.Equal("none of 2", SettingsMenu.SqliteOfferedValue(null, new SqliteCatalog([new("a", new() { Path = "a.db" }, "x.json"), new("b", new() { Path = "b.db" }, "x.json")], [])));
        Assert.Equal("(none) · Enter edits sqlite.json", SettingsMenu.SqliteDatabasesLabel(Path.Combine(_dir, "missing.json")));
    }

    /// <summary><c>SQLite add database</c>: the profile's file, a name, the file, no description; Test opens it read-only; Save offers it.</summary>
    [Fact]
    public async Task OnThePane_TheSqliteWizard_WalksADatabase_TestsIt_AndSavesItOffered()
    {
        string db = MakeSqliteFile("shop.db");
        string path = SqliteConfigFile.ProfilePath(_settings.ProfileDirectory);
        var (menu, _, _) = PaneMenu();
        OpenSqliteRow(6);
        Push(Keys.Enter);                         // the profile's file
        Type("my shop");                          // refused: a space
        Push([.. Enumerable.Repeat(Keys.Backspace, 7)]);
        Type("shop");
        Type(db);
        Push(Keys.Enter);                         // no description
        Push(Keys.Down, Keys.Down, Keys.Enter);   // Test
        Push(Keys.Up, Keys.Up, Keys.Enter);       // Save, and offer it
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        var shop = Assert.Single(SqliteConfigFile.Load(path).Databases);
        Assert.Equal(("shop", db), (shop.Name, shop.FullPath));
        Assert.Contains(SettingsMenu.SqlWizardNameSpaces, _console.Output);
        Assert.Contains(SettingsMenu.SqliteWizardTestOk("shop", 3), _console.Output);
        Assert.Contains(SettingsMenu.SqliteWizardAdded("shop", path), _console.Output);
        Assert.Equal(["shop"], _settings.Current.SqliteDatabasesOffered);
    }

    /// <summary><c>SQLite protection mode</c> (2026-10-05): read-only by default, the pick lists both with their hints, read-write saved.</summary>
    [Fact]
    public async Task OnThePane_TheProtectionMode_PicksReadWrite()
    {
        Assert.Equal("read-only", _settings.Current.SqliteProtectionMode);
        Assert.StartsWith("read-write  ", SettingsMenu.SqliteProtectionLabel("read-write"), StringComparison.Ordinal);   // a gap past the longest name
        var (menu, _, _) = PaneMenu();
        OpenSqliteRow(1);
        Push(Keys.Down, Keys.Enter);              // read-write
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal("read-write", _settings.Current.SqliteProtectionMode);
        Assert.Contains(SqliteProtectionMode.Describe("read-only"), _console.Output);
    }

    /// <summary><c>SQLite statements allowed</c> (later on 2026-10-05): changing data by default; Enter ticks dropping beside it.</summary>
    [Fact]
    public async Task OnThePane_TheStatementsAllowed_TicksAKind()
    {
        Assert.Equal(["data"], _settings.Current.SqliteStatementsAllowed);
        var (menu, _, _) = PaneMenu();
        OpenSqliteRow(2);
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Enter);   // dropping
        Push(Keys.Escape);                                   // out of the checklist
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(["data", "drop"], _settings.Current.SqliteStatementsAllowed);
        Assert.Contains("DROP TABLE, INDEX, VIEW, TRIGGER", _console.Output);
    }

    /// <summary>N clears every kind and A ticks them all, saved in menu order.</summary>
    [Fact]
    public async Task OnThePane_TheStatementsAllowed_NoneThenAll()
    {
        var (menu, _, _) = PaneMenu();
        OpenSqliteRow(2);
        Push(Keys.Char('n'));
        Push(Keys.Char('a'));
        Push(Keys.Escape);
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(SqliteStatementKinds.Names, _settings.Current.SqliteStatementsAllowed);
    }

    [Fact]
    public void TheStatementsAllowed_Value_NamesTwo_CountsMore_AndSaysWhenUnused()
    {
        Assert.Equal("changing data", SettingsMenu.SqliteStatementsValue(["data"], "read-write"));
        Assert.Equal("changing data, dropping", SettingsMenu.SqliteStatementsValue(["DROP", "data", "nonsense"], "read-write"));
        Assert.Equal("3 of 7", SettingsMenu.SqliteStatementsValue(["data", "create", "read"], "read-write"));
        Assert.Equal("none (used under read-write)", SettingsMenu.SqliteStatementsValue([], "read-only"));
        Assert.Equal("changing data (used under read-write)", SettingsMenu.SqliteStatementsValue(null, null));
    }

    /// <summary>The checklist ticks a named database, the default pick lists the offered ones, ESC out of the wizard's first page writes nothing.</summary>
    [Fact]
    public async Task OnThePane_TheChecklist_TheDefault_AndEscOutOfTheWizard()
    {
        Assert.Null(SqliteConfigFile.AddDatabase(SqliteConfigFile.GlobalPath(_settings.StorageDirectory), "notes", new SqliteDatabaseConfig { Path = MakeSqliteFile("notes.db") }));
        var (menu, _, _) = PaneMenu();
        OpenSqliteRow(3);
        Push(Keys.Enter);                         // tick notes
        Push(Keys.Escape);
        Push(Keys.Down, Keys.Enter);              // SQLite default database, under the checklist
        Push(Keys.Down, Keys.Enter);              // notes
        Push(Keys.Down, Keys.Down, Keys.Enter);   // the wizard
        Push(Keys.Escape);                        // out of its first page: nothing
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(["notes"], _settings.Current.SqliteDatabasesOffered);
        Assert.Equal("notes", _settings.Current.SqliteDefaultDatabase);
        Assert.Contains(SettingsMenu.SqliteWizardCancelledNotice, _console.Output);
        Assert.False(File.Exists(SqliteConfigFile.ProfilePath(_settings.ProfileDirectory)));
    }
}
