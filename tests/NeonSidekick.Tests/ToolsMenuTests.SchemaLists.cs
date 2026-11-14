using NeonSidekick.App;
using NeonSidekick.Sql;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// The wizards' Schemas step on an edit (2026-11-14, feature idea #18): a saved schema the server no longer lists shown "(missing)" and
/// ticked, unticking it (and the rest) the fix; and the typed answer, a comma-separated list, when the schemas cannot be listed.
/// </summary>
public partial class ToolsMenuTests
{
    [Fact]
    public async Task OnThePane_ThePostgresWizard_ShowsAMissingSavedSchema_AndUntickingAllSavesNoLock()
    {
        string path = NeonSidekick.Postgres.PostgresConfigFile.ProfilePath(_settings.ProfileDirectory);
        Directory.CreateDirectory(NeonSidekick.Settings.Profiles.ConfigsDirectory(_settings.ProfileDirectory));
        File.WriteAllText(path, "{ \"connections\": { \"old\": { \"host\": \"localhost\", \"database\": \"shop\", \"user\": \"neon\", \"password\": \"pw\", \"schemas\": [\"sales\", \"gone\"] } } }");
        var (menu, _, settings) = PaneMenu();
        settings.ListPostgresSchemas = (c, _) => Task.FromResult(new SqlRun(SqlOutcome.Ok, "", c.Name, "shop", [new SqlGrid(["nspname"], [["public"], ["sales"]], false)], TimeSpan.Zero));
        OpenPostgresRow(5);
        Push(Keys.Down, Keys.Enter);                                  // old, under + New
        Push([.. Enumerable.Repeat(Keys.Down, 13), Keys.Enter]);      // from Save hidden to the Schemas row
        Push(Keys.Down, Keys.Enter);                                  // public, sales ✓, gone ✓ (missing), Continue: sales off
        Push(Keys.Down, Keys.Char(' '));                              // gone off
        Push(Keys.Down, Keys.Enter);                                  // Continue: none ticked, every schema
        Push(Keys.Enter);                                             // Save, hidden
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains(SettingsMenu.WizardSchemaMissing, Output);
        var old = Assert.Single(NeonSidekick.Postgres.PostgresConfigFile.Load(path).Connections).Config;
        Assert.Null(old.Schema);
        Assert.Null(old.Schemas);
        Assert.DoesNotContain("\"schemas\"", File.ReadAllText(path));
    }

    [Fact]
    public async Task OnThePane_TheOracleWizard_TakesATypedList_WhenTheSchemasCannotBeListed()
    {
        string path = NeonSidekick.Oracle.OracleConfigFile.ProfilePath(_settings.ProfileDirectory);
        Directory.CreateDirectory(NeonSidekick.Settings.Profiles.ConfigsDirectory(_settings.ProfileDirectory));
        File.WriteAllText(path, "{ \"connections\": { \"old\": { \"dataSource\": \"localhost:1521/FREEPDB1\", \"user\": \"neon\", \"password\": \"pw\" } } }");
        var (menu, _, settings) = PaneMenu();
        settings.ListOracleSchemas = (c, _) => Task.FromResult(SqlRun.Refused(SqlOutcome.ConnectFailed, "ORA-12541: no listener", c.Name));
        OpenOracleRow(5);
        Push(Keys.Down, Keys.Enter);                                  // old, under + New
        Push([.. Enumerable.Repeat(Keys.Down, 10), Keys.Enter]);      // from Save hidden to the Schemas row
        Type("hr, oe, hr");                                           // refused: the same schema twice
        Push([.. Enumerable.Repeat(Keys.Backspace, 4)]);
        Push(Keys.Enter);                                             // hr, oe
        Push(Keys.Enter);                                             // Save, hidden
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains(SettingsMenu.WizardSchemasUnlisted(NeonSidekick.Oracle.OracleText.ConnectFailed("old", "ORA-12541: no listener")), Output);
        Assert.Contains(NeonSidekick.Oracle.OracleText.RepeatedSchema("hr"), Output);
        var old = Assert.Single(NeonSidekick.Oracle.OracleConfigFile.Load(path).Connections).Config;
        Assert.Null(old.Schema);
        Assert.Equal(["hr", "oe"], old.Schemas);
        Assert.Equal(["HR", "OE"], NeonSidekick.Oracle.OracleAccess.Places(old));
    }
}
