using NeonSidekick.App;
using NeonSidekick.DuckDb;
using NeonSidekick.Settings;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>The DuckDB tab of <c>/tools</c> (2026-10-25): after SQLite, its rows, the wizard over a real file, the checklists and the mode.</summary>
public partial class ToolsMenuTests
{
    /// <summary>The DuckDB tab (<see cref="ToTab"/>), then the row and Enter.</summary>
    private void OpenDuckDbRow(int row) => Push([.. ToTab(ToolsText.DuckDbTabTitle), .. Enumerable.Repeat(Keys.Down, row), Keys.Enter]);

    private string MakeDuckDbFile(string name)
    {
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, name);
        DuckDbToolsTests.Make(path, "CREATE TABLE a (x INTEGER); CREATE TABLE b (y INTEGER); CREATE VIEW v AS SELECT 1 AS z;");
        return path;
    }

    [Fact]
    public void TheDuckDbTab_IsAfterSqlite_WithItsRows_AndItsWordsArePinned()
    {
        var databases = ToolsText.TabMenus[ToolsText.TabOfMenu(ToolsText.MenuIndex(ToolsText.DuckDbTabTitle)) - 1].ToList();
        Assert.Equal(databases.IndexOf(ToolsText.MenuIndex(ToolsText.SqliteTabTitle)) + 1, databases.IndexOf(ToolsText.MenuIndex(ToolsText.DuckDbTabTitle)));
        Assert.True(InGroup(ToolsText.DuckDbTabTitle));
        Assert.Equal(
            [SettingsField.DuckDbTools, SettingsField.DuckDbMode, SettingsField.DuckDbStatementsAllowed, SettingsField.DuckDbDatabasesOffered, SettingsField.DuckDbSandboxFiles, SettingsField.DuckDbAddDatabase, SettingsField.DuckDbPercentMention, SettingsField.DuckDbQueryMaxRows, SettingsField.DuckDbQueryTimeoutSeconds, SettingsField.DuckDbDatabasesProfile, SettingsField.DuckDbDatabasesGlobal],
            TabFields(ToolsText.DuckDbTabTitle));
        Assert.Equal("Enter to start database wizard", SettingsMenu.DuckDbAddDatabaseLabel);
        Assert.Equal("Opened 'lake' read-only: 3 tables and views.", SettingsMenu.DuckDbWizardTestOk("lake", 3));
        Assert.Equal("none of 2", SettingsMenu.DuckDbOfferedValue(null, new DuckDbCatalog([new("a", new() { Path = "a.duckdb" }, "x.json"), new("b", new() { Path = "b.duckdb" }, "x.json")], [])));
        Assert.Equal("(none) · Enter edits duckdb.json", SettingsMenu.DuckDbDatabasesLabel(Path.Combine(_dir, "missing.json")));
    }

    /// <summary><c>DuckDB add database</c>: the profile's file, a name, the file, no description; Test opens it read-only; Save offers it.</summary>
    [Fact]
    public async Task OnThePane_TheDuckDbWizard_WalksADatabase_TestsIt_AndSavesItOffered()
    {
        string db = MakeDuckDbFile("lake.duckdb");
        string path = DuckDbConfigFile.ProfilePath(_settings.ProfileDirectory);
        var (menu, _, _) = PaneMenu();
        OpenDuckDbRow(5);
        Push(Keys.Enter);                         // the profile's file
        Type("my lake");                          // refused: a space
        Push([.. Enumerable.Repeat(Keys.Backspace, 7)]);
        Type("lake");
        Type(db);
        Push(Keys.Enter);                         // no description
        Push(Keys.Down, Keys.Down, Keys.Enter);   // Test
        Push(Keys.Up, Keys.Up, Keys.Enter);       // Save, and offer it
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        var lake = Assert.Single(DuckDbConfigFile.Load(path).Databases);
        Assert.Equal(("lake", db), (lake.Name, lake.FullPath));
        Assert.Contains(SettingsMenu.SqlWizardNameSpaces, Output);
        Assert.Contains(SettingsMenu.DuckDbWizardTestOk("lake", 3), Output);
        Assert.Contains(SettingsMenu.DuckDbWizardAdded("lake", path), Output);
        Assert.Equal(["lake"], _settings.Current.DuckDbDatabasesOffered);
    }

    [Fact]
    public async Task OnThePane_TheDuckDbOffered_ListsTheDatabasesAToZ()
    {
        Directory.CreateDirectory(Profiles.ConfigsDirectory(_settings.ProfileDirectory));
        File.WriteAllText(DuckDbConfigFile.ProfilePath(_settings.ProfileDirectory), """{ "databases": { "zeta": { "path": "z.duckdb" }, "Alpha": { "path": "a.duckdb" } } }""");
        var (menu, _, _) = PaneMenu();
        OpenDuckDbRow(3);
        Push(Keys.Enter);                         // Alpha on, the first row
        Push(Keys.Escape);
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(["Alpha"], _settings.Current.DuckDbDatabasesOffered);
    }

    [Fact]
    public async Task OnThePane_TheDuckDbMode_PicksReadWrite()
    {
        Assert.Equal("read-only", _settings.Current.DuckDbMode);
        Assert.StartsWith("read-write  ", SettingsMenu.DuckDbModeLabel("read-write"), StringComparison.Ordinal);
        var (menu, _, _) = PaneMenu();
        OpenDuckDbRow(1);
        Push(Keys.Down, Keys.Enter);              // read-write
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal("read-write", _settings.Current.DuckDbMode);
        Assert.Contains(DuckDbModes.Describe("read-only"), Output);
    }

    [Fact]
    public async Task OnThePane_TheDuckDbStatementsAllowed_TicksAKind()
    {
        Assert.Equal(["data", "create", "read"], _settings.Current.DuckDbStatementsAllowed);
        var (menu, _, _) = PaneMenu();
        OpenDuckDbRow(2);
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter);   // dropping, after deleting
        Push(Keys.Escape);
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(["data", "create", "drop", "read"], _settings.Current.DuckDbStatementsAllowed);
        Assert.Contains("DROP, CREATE OR REPLACE", Output);
    }

    [Fact]
    public async Task OnThePane_TheDuckDbStatementsAllowed_DefaultPutsTheDefaultBack()
    {
        _settings.Update(d => d.DuckDbStatementsAllowed = ["delete", "drop", "pragma"]);
        var (menu, _, _) = PaneMenu();
        OpenDuckDbRow(2);
        Push(Keys.Char('d'));
        Push(Keys.Escape);
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(["data", "create", "read"], _settings.Current.DuckDbStatementsAllowed);
    }

    [Fact]
    public void TheDuckDbStatementsAllowed_Value_NamesTwo_CountsMore_AndSaysWhenUnused()
    {
        Assert.Equal("changing data", SettingsMenu.DuckDbStatementsValue(["data"], "read-write"));
        Assert.Equal("4 of 8", SettingsMenu.DuckDbStatementsValue(["data", "create", "drop", "read"], "read-write"));
        Assert.Equal("none (used under read-write)", SettingsMenu.DuckDbStatementsValue([], "read-only"));
        Assert.Equal("changing data, creating, reading (used under read-write)", SettingsMenu.DuckDbStatementsValue(null, null));
    }

    /// <summary>The checklist ticks a named database, Esc out of the wizard's first page writes nothing.</summary>
    [Fact]
    public async Task OnThePane_TheDuckDbChecklist_AndEscOutOfTheWizard()
    {
        Assert.Null(DuckDbConfigFile.AddDatabase(DuckDbConfigFile.GlobalPath(_settings.StorageDirectory), "notes", new DuckDbDatabaseConfig { Path = MakeDuckDbFile("notes.duckdb") }));
        var (menu, _, _) = PaneMenu();
        OpenDuckDbRow(3);
        Push(Keys.Enter);                         // tick notes
        Push(Keys.Escape);
        Push(Keys.Down, Keys.Down, Keys.Enter);   // the wizard
        Push(Keys.Escape);                        // out of its first page: nothing
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(["notes"], _settings.Current.DuckDbDatabasesOffered);
        Assert.Contains(SettingsMenu.DuckDbWizardCancelledNotice, Output);
        Assert.False(File.Exists(DuckDbConfigFile.ProfilePath(_settings.ProfileDirectory)));
    }
}
