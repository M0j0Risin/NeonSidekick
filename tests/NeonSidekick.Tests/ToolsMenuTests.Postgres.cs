using NeonSidekick.App;
using NeonSidekick.Postgres;
using NeonSidekick.Settings;
using NeonSidekick.Sql;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>The PostgreSQL tab of <c>/tools</c> (2026-10-04): after SQLite, its rows, the wizard through the pane with a faked test.</summary>
public partial class ToolsMenuTests
{
    private void OpenPostgresRow(int row) => Push([.. ToTab(ToolsText.PostgresTabTitle), .. Enumerable.Repeat(Keys.Down, row), Keys.Enter]);

    [Fact]
    public void ThePostgresTab_IsAfterSqlite_WithMySqlsRows()
    {
        Assert.Equal(TabIndex(ToolsText.SqliteTabTitle) + 1, TabIndex(ToolsText.PostgresTabTitle));
        Assert.Equal(
            [SettingsField.PostgresTools, SettingsField.PostgresMode, SettingsField.PostgresStatementsAllowed, SettingsField.PostgresConnectionsOffered, SettingsField.PostgresDefaultConnection, SettingsField.PostgresSetPassword, SettingsField.PostgresAddConnection, SettingsField.PostgresPercentMention, SettingsField.PostgresQueryMaxRows, SettingsField.PostgresQueryTimeoutSeconds, SettingsField.PostgresConnectionsProfile, SettingsField.PostgresConnectionsGlobal],
            TabFields(ToolsText.PostgresTabTitle));
    }

    /// <summary><c>PostgreSQL add connection</c>: every page, the draft tested (a superuser warned of, not refused), saved and offered, the password encrypted.</summary>
    [Fact]
    public async Task OnThePane_ThePostgresWizard_WalksAConnection_WarnsOfASuperuser_AndSavesIt()
    {
        string path = PostgresConfigFile.ProfilePath(_settings.ProfileDirectory);
        var tested = new List<PostgresNamedConnection>();
        var (menu, _, settings) = PaneMenu();
        settings.TestPostgresConnection = (c, _) =>
        {
            tested.Add(c);
            return Task.FromResult(new SqlRun(SqlOutcome.Ok, "", c.Name, "shop",
                [new SqlGrid(["user", "database", "version"], [["postgres", "shop", "PostgreSQL 17.2"]], false), new SqlGrid(["p"], [["superuser"]], false)], TimeSpan.Zero));
        };
        OpenPostgresRow(6);
        Push(Keys.Enter);                         // the profile's file
        Type("shop");
        Type("localhost");
        Type("5433");
        Type("shop");
        Type("postgres");
        Push(Keys.Enter);                         // file
        Type("s3cret");
        Push(Keys.Down, Keys.Enter);              // require
        Push(Keys.Enter);                         // the default timeout
        Push(Keys.Down, Keys.Enter);              // readwrite (2026-10-05), while PostgreSQL mode is read-only
        Type("the sample shop");
        Push(Keys.Down, Keys.Down, Keys.Enter);   // Test
        Push(Keys.Up, Keys.Up, Keys.Enter);       // Save, and offer it
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal("s3cret", Assert.Single(tested).Config.Password);
        var shop = Assert.Single(PostgresConfigFile.Load(path).Connections);
        Assert.Equal(("localhost", 5433, "shop", "postgres", "require"), (shop.Config.Host, shop.Config.Port, shop.Config.Database, shop.Config.User, shop.Config.SslMode));
        Assert.Equal("s3cret", PostgresSecrets.Resolve(shop).Value);
        Assert.Contains(PostgresText.TestOk("shop", "postgres", "shop", "PostgreSQL 17.2"), Output);
        Assert.Contains("This account can change data (superuser). As readwrite, postgres_execute may use those powers", Output);
        Assert.Equal("readwrite", shop.Config.Access);
        Assert.Equal("readwrite", Assert.Single(tested).Config.Access);   // the test tries the draft as it will be saved
        Assert.Contains(SettingsMenu.DatabaseWizardModeOffNotice(PostgresStatementKinds.Family), Output);
        Assert.Contains(SettingsMenu.DatabaseWizardAccessQuestion, Output);
        Assert.Equal(["shop"], _settings.Current.PostgresConnectionsOffered);
        Assert.DoesNotContain("s3cret", Output);
    }

    /// <summary><c>PostgreSQL mode</c> (2026-10-05): read-only by default, the pick lists both with their hints, read-write saved.</summary>
    [Fact]
    public async Task OnThePane_ThePostgresMode_PicksReadWrite()
    {
        Assert.Equal("read-only", _settings.Current.PostgresMode);
        Assert.StartsWith("read-write  ", SettingsMenu.WriteModeLabel("read-write", PostgresStatementKinds.Family), StringComparison.Ordinal);
        var (menu, _, _) = PaneMenu();
        OpenPostgresRow(1);
        Push(Keys.Down, Keys.Enter);              // read-write
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal("read-write", _settings.Current.PostgresMode);
        Assert.Contains(DatabaseWriteModes.Describe("read-only", PostgresStatementKinds.Family), Output);
    }

    /// <summary><c>PostgreSQL statements allowed</c> (2026-10-05): changing data, creating and reading by default; Enter ticks dropping; D puts the default back; N then A.</summary>
    [Fact]
    public async Task OnThePane_ThePostgresStatementsAllowed_TicksDefaultsNoneAndAll()
    {
        Assert.Equal(["data", "create", "read"], _settings.Current.PostgresStatementsAllowed);
        var (menu, _, _) = PaneMenu();
        OpenPostgresRow(2);
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter);   // dropping
        Push(Keys.Escape);
        Push(Keys.Escape);
        await menu.ShowAsync(CancellationToken.None);
        Assert.Equal(["data", "create", "drop", "read"], _settings.Current.PostgresStatementsAllowed);
        Assert.Contains("DROP TABLE, INDEX, VIEW", Output);
        Assert.Contains("procedures and triggers", Output);

        (menu, _, _) = PaneMenu();
        OpenPostgresRow(2);
        Push(Keys.Char('d'));
        Push(Keys.Escape);
        Push(Keys.Escape);
        await menu.ShowAsync(CancellationToken.None);
        Assert.Equal(["data", "create", "read"], _settings.Current.PostgresStatementsAllowed);

        (menu, _, _) = PaneMenu();
        OpenPostgresRow(2);
        Push(Keys.Char('n'));
        Push(Keys.Char('a'));
        Push(Keys.Escape);
        Push(Keys.Escape);
        await menu.ShowAsync(CancellationToken.None);
        Assert.Equal(ServerStatementKinds.Names, _settings.Current.PostgresStatementsAllowed);
    }

    [Fact]
    public void TheWriteStatementsValue_NamesThree_CountsMore_AndSaysWhenUnused()
    {
        Assert.Equal("changing data", SettingsMenu.WriteStatementsValue(["data"], "read-write"));
        Assert.Equal("changing data, creating, reading", SettingsMenu.WriteStatementsValue(null, "read-write"));
        Assert.Equal("4 of 8", SettingsMenu.WriteStatementsValue(["data", "create", "drop", "read"], "read-write"));
        Assert.Equal("none (used under read-write)", SettingsMenu.WriteStatementsValue([], "read-only"));
        Assert.Equal("changing data, creating, reading (used under read-write)", SettingsMenu.WriteStatementsValue(null, null));
    }
}
