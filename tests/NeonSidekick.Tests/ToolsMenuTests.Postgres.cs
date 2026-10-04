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
            [SettingsField.PostgresTools, SettingsField.PostgresConnectionsOffered, SettingsField.PostgresDefaultConnection, SettingsField.PostgresSetPassword, SettingsField.PostgresAddConnection, SettingsField.PostgresPercentMention, SettingsField.PostgresQueryMaxRows, SettingsField.PostgresQueryTimeoutSeconds, SettingsField.PostgresConnectionsProfile, SettingsField.PostgresConnectionsGlobal],
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
        OpenPostgresRow(4);
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
        Type("the sample shop");
        Push(Keys.Down, Keys.Down, Keys.Enter);   // Test
        Push(Keys.Up, Keys.Up, Keys.Enter);       // Save, and offer it
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal("s3cret", Assert.Single(tested).Config.Password);
        var shop = Assert.Single(PostgresConfigFile.Load(path).Connections);
        Assert.Equal(("localhost", 5433, "shop", "postgres", "require"), (shop.Config.Host, shop.Config.Port, shop.Config.Database, shop.Config.User, shop.Config.SslMode));
        Assert.Equal("s3cret", PostgresSecrets.Resolve(shop).Value);
        Assert.Contains(PostgresText.TestOk("shop", "postgres", "shop", "PostgreSQL 17.2"), _console.Output);
        Assert.Contains("This account can change data (superuser).", _console.Output);
        Assert.Equal(["shop"], _settings.Current.PostgresConnectionsOffered);
        Assert.DoesNotContain("s3cret", _console.Output);
    }
}
