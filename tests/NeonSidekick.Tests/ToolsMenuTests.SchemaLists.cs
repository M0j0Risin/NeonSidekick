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
        settings.ListPostgresSchemas = (c, _, _) => Task.FromResult(new SqlRun(SqlOutcome.Ok, "", c.Name, "shop", [new SqlGrid(["nspname"], [["public"], ["sales"]], false)], TimeSpan.Zero));
        OpenPostgresRow(5);
        Push(Keys.Down, Keys.Enter);                                  // old, under + New
        Push([.. Enumerable.Repeat(Keys.Down, 13), Keys.Enter]);      // from Save hidden to the Schemas row
        Push(Keys.Enter);                                             // sales ✓, gone ✓ (missing), public, Continue (the saved first, #31): sales off
        Push(Keys.Down, Keys.Char(' '));                              // gone off
        Push(Keys.Down, Keys.Down, Keys.Enter);                       // Continue: none ticked, every schema
        Push(Keys.Home, Keys.Down, Keys.Enter);                       // Save, hidden: Home, then one down (the cursor stayed on the row changed)
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
        settings.ListOracleSchemas = (c, _, _) => Task.FromResult(SqlRun.Refused(SqlOutcome.ConnectFailed, "ORA-12541: no listener", c.Name));
        OpenOracleRow(5);
        Push(Keys.Down, Keys.Enter);                                  // old, under + New
        Push([.. Enumerable.Repeat(Keys.Down, 10), Keys.Enter]);      // from Save hidden to the Schemas row
        Type("hr, oe, hr");                                           // refused: the same schema twice
        Push([.. Enumerable.Repeat(Keys.Backspace, 4)]);
        Push(Keys.Enter);                                             // hr, oe
        Push(Keys.Home, Keys.Down, Keys.Enter);                       // Save, hidden: Home, then one down (the cursor stayed on the row changed)
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains(SettingsMenu.WizardSchemasUnlisted(NeonSidekick.Oracle.OracleText.ConnectFailed("old", "ORA-12541: no listener")), Output);
        Assert.Contains(NeonSidekick.Oracle.OracleText.RepeatedSchema("hr"), Output);
        var old = Assert.Single(NeonSidekick.Oracle.OracleConfigFile.Load(path).Connections).Config;
        Assert.Null(old.Schema);
        Assert.Equal(["hr", "oe"], old.Schemas);
        Assert.Equal(["HR", "OE"], NeonSidekick.Oracle.OracleAccess.Places(old));
    }

    /// <summary>
    /// 2026-11-15, the database-tools review's #31/#33/#36/#53: on the pane the checklist orders. The saved names lead, a missing one is marked
    /// and left out of <c>a</c>, the account's own Oracle schema is marked and refused when ticked, and Shift+↑ then Shift+Home move a row;
    /// the ticked rows top to bottom are the saved order.
    /// </summary>
    [Fact]
    public async Task OnThePane_TheOracleWizard_OrdersByMoves_MarksTheMissingAndTheRefused_AndATicksOnlyTheListed()
    {
        string path = NeonSidekick.Oracle.OracleConfigFile.ProfilePath(_settings.ProfileDirectory);
        Directory.CreateDirectory(NeonSidekick.Settings.Profiles.ConfigsDirectory(_settings.ProfileDirectory));
        File.WriteAllText(path, "{ \"connections\": { \"old\": { \"dataSource\": \"localhost:1521/FREEPDB1\", \"user\": \"system\", \"password\": \"pw\", \"schemas\": [\"HR\", \"GONE\"] } } }");
        var (menu, _, settings) = PaneMenu();
        IReadOnlyList<string>? asked = null;
        settings.ListOracleSchemas = (c, saved, _) =>
        {
            asked = saved;
            return Task.FromResult(new SqlRun(SqlOutcome.Ok, "", c.Name, "SYSTEM", [new SqlGrid(["username"], [["HR"], ["OE"], ["SH"]], false), new SqlGrid(["username", "m"], [["HR", "N"]], false)], TimeSpan.Zero));
        };
        OpenOracleRow(5);
        Push(Keys.Down, Keys.Enter);                                  // old, under + New
        Push([.. Enumerable.Repeat(Keys.Down, 10), Keys.Enter]);      // from Save hidden to the Schemas row
        Push(Keys.Down, Keys.Down, Keys.Enter);                       // HR ✓, GONE ✓ (missing), SYSTEM (not allowed), OE, SH: SYSTEM refused
        Push(Keys.Char('n'), Keys.Char('a'));                         // none, then all: HR, OE and SH, never GONE or SYSTEM
        Push(Keys.Down, Keys.Down);                                   // SH
        Push(Keys.Shift(ConsoleKey.UpArrow));                         // above OE
        Push(Keys.Shift(ConsoleKey.Home));                            // to the top: SH, HR, GONE, SYSTEM, OE
        Push([.. Enumerable.Repeat(Keys.Down, 5), Keys.Enter]);       // Continue
        Push(Keys.Home, Keys.Down, Keys.Enter);                       // Save, hidden: Home, then one down (the cursor stayed on the row changed)
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(["HR", "GONE"], asked);                          // the saved names, looked up beside the listing (#146)
        Assert.Contains(SettingsMenu.WizardSchemaMissing, Output);
        Assert.Contains(SettingsMenu.WizardSchemaNotAllowed, Output);
        Assert.Contains(NeonSidekick.Oracle.OracleText.SystemSchema("SYSTEM", "schema"), Output);
        var old = Assert.Single(NeonSidekick.Oracle.OracleConfigFile.Load(path).Connections).Config;
        Assert.Equal(["SH", "HR", "OE"], old.Schemas);
        Assert.Null(old.Schema);
    }

    /// <summary>
    /// #146: a listing cut at its cap says so, and a saved schema past it, found by name, is no "(missing)"; #29: a one-name list saved stays a list.
    /// </summary>
    [Fact]
    public async Task OnThePane_ThePostgresWizard_FindsASavedSchemaPastACutListing()
    {
        string path = NeonSidekick.Postgres.PostgresConfigFile.ProfilePath(_settings.ProfileDirectory);
        Directory.CreateDirectory(NeonSidekick.Settings.Profiles.ConfigsDirectory(_settings.ProfileDirectory));
        File.WriteAllText(path, "{ \"connections\": { \"old\": { \"host\": \"localhost\", \"database\": \"shop\", \"user\": \"neon\", \"password\": \"pw\", \"schemas\": [\"far\", \"sales\"] } } }");
        var (menu, _, settings) = PaneMenu();
        settings.ListPostgresSchemas = (c, saved, _) => Task.FromResult(new SqlRun(SqlOutcome.Ok, "", c.Name, "shop",
            [new SqlGrid(["nspname"], [["public"], ["sales"]], true), new SqlGrid(["nspname"], [.. saved.Where(s => s == "far").Select(s => new[] { s })], false)], TimeSpan.Zero));
        OpenPostgresRow(5);
        Push(Keys.Down, Keys.Enter);                                  // old, under + New
        Push([.. Enumerable.Repeat(Keys.Down, 13), Keys.Enter]);      // from Save hidden to the Schemas row
        Push(Keys.Down, Keys.Enter);                                  // far ✓, sales ✓, public: sales off
        Push(Keys.Down, Keys.Down, Keys.Enter);                       // Continue: far alone
        Push(Keys.Home, Keys.Down, Keys.Enter);                       // Save, hidden: Home, then one down (the cursor stayed on the row changed)
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains(SettingsMenu.WizardSchemaListCut(2), Output);
        Assert.DoesNotContain(SettingsMenu.WizardSchemaMissing, Output);
        var old = Assert.Single(NeonSidekick.Postgres.PostgresConfigFile.Load(path).Connections).Config;
        Assert.Equal(["far"], old.Schemas);
        Assert.Null(old.Schema);
    }

    /// <summary>#163: a summary edit of the user while a lock is held goes on to Schemas, listed again over the new user, then back to the summary.</summary>
    [Fact]
    public async Task OnThePane_ThePostgresWizard_AUserEditFromTheSummary_GoesOnToSchemas()
    {
        string path = NeonSidekick.Postgres.PostgresConfigFile.ProfilePath(_settings.ProfileDirectory);
        Directory.CreateDirectory(NeonSidekick.Settings.Profiles.ConfigsDirectory(_settings.ProfileDirectory));
        File.WriteAllText(path, "{ \"connections\": { \"old\": { \"host\": \"localhost\", \"database\": \"shop\", \"user\": \"neon\", \"password\": \"pw\", \"schema\": \"sales\" } } }");
        var (menu, _, settings) = PaneMenu();
        var users = new List<string?>();
        settings.ListPostgresSchemas = (c, _, _) =>
        {
            users.Add(c.Config.User);
            return Task.FromResult(new SqlRun(SqlOutcome.Ok, "", c.Name, "shop", [new SqlGrid(["nspname"], [["public"], ["sales"]], false)], TimeSpan.Zero));
        };
        OpenPostgresRow(5);
        Push(Keys.Down, Keys.Enter);                                  // old, under + New
        Push([.. Enumerable.Repeat(Keys.Down, 8), Keys.Enter]);       // from Save hidden to the User row
        Push([.. Enumerable.Repeat(Keys.Backspace, 4)]);
        Type("reader");                                               // on to Schemas: sales ✓, public
        Push(Keys.Down, Keys.Down, Keys.Enter);                       // Continue
        Push(Keys.Home, Keys.Down, Keys.Enter);                       // Save, hidden: Home, then one down (the cursor stayed on the row changed)
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(["reader"], users);
        var old = Assert.Single(NeonSidekick.Postgres.PostgresConfigFile.Load(path).Connections).Config;
        Assert.Equal("reader", old.User);
        Assert.Equal("sales", old.Schema);
    }

    /// <summary>The settings menu with no pane: every list is the Spectre prompt, so the schema order is asked a page at a time (#31/#33).</summary>
    private SettingsMenu PanelessSettings(Func<NeonSidekick.Oracle.OracleNamedConnection, CancellationToken, Task<SqlRun>>? testOracle = null)
    {
        _paneOn = false;
        var pane = new ScreenPane(_console, geometry: null, _time);
        var keys = new KeySource(_console.Input, TimeSpan.FromMilliseconds(1));
        return new SettingsMenu(_console, _settings, _ => null, new InputLine(_console, keys), new TranscriptRenderer(_console), _speech, new MenuPane(pane, keys), _ => FakeBrowserPath, testOracleConnection: testOracle);
    }

    [Fact]
    public async Task WithoutThePane_TheOracleWizard_AsksWhichIsCurrent_ThenWhichComesNext()
    {
        string path = NeonSidekick.Oracle.OracleConfigFile.ProfilePath(_settings.ProfileDirectory);
        var settings = PanelessSettings();
        settings.ListOracleSchemas = (c, _, _) => Task.FromResult(new SqlRun(SqlOutcome.Ok, "", c.Name, "NEON", [new SqlGrid(["username"], [["HR"], ["OE"], ["lab"]], false)], TimeSpan.Zero));
        Push(Keys.Enter);                                             // the profile's file
        Type("free");
        Type("localhost:1521/FREEPDB1");
        Type("neon");
        Push(Keys.Enter);                                             // file
        Type("s3cret");
        Push(Keys.Enter);                                             // the default timeout
        Push(Keys.Down, Keys.Enter);                                  // NEON, HR, OE, "lab", Continue: HR
        Push(Keys.Down, Keys.Enter);                                  // OE
        Push(Keys.Down, Keys.Enter);                                  // "lab"
        Push(Keys.Down, Keys.Enter);                                  // Continue
        Push(Keys.Down, Keys.Down, Keys.Enter);                       // which is current: "lab"
        Push(Keys.Down, Keys.Enter);                                  // which comes next: OE (HR last)
        Push(Keys.Enter);                                             // read
        Push(Keys.Enter);                                             // no description
        Push(Keys.Enter);                                             // Save, and offer it

        await settings.EditAsync(SettingsField.OracleAddConnection, _settings.Current, new MenuPage("Tools", [], ""), 0, CancellationToken.None);

        Assert.Contains(SettingsMenu.OracleWizardCurrentQuestion, Output);
        Assert.Contains(SettingsMenu.WizardSchemaNextQuestion, Output);
        Assert.Equal(1, CountOf(Output, SettingsMenu.OracleWizardSchemaQuestion));   // the caption once a step (#174), not once a tick
        var free = Assert.Single(NeonSidekick.Oracle.OracleConfigFile.Load(path).Connections).Config;
        Assert.Equal(["\"lab\"", "OE", "HR"], free.Schemas);
    }

    [Fact]
    public async Task WithoutThePane_ThePostgresWizard_AsksWhichComesFirst_ThenWhichComesNext()
    {
        string path = NeonSidekick.Postgres.PostgresConfigFile.ProfilePath(_settings.ProfileDirectory);
        Directory.CreateDirectory(NeonSidekick.Settings.Profiles.ConfigsDirectory(_settings.ProfileDirectory));
        File.WriteAllText(path, "{ \"connections\": { \"old\": { \"host\": \"localhost\", \"database\": \"shop\", \"user\": \"neon\", \"password\": \"pw\", \"schemas\": [\"sales\", \"public\"] } } }");
        var settings = PanelessSettings();
        settings.ListPostgresSchemas = (c, _, _) => Task.FromResult(new SqlRun(SqlOutcome.Ok, "", c.Name, "shop", [new SqlGrid(["nspname"], [["archive"], ["public"], ["sales"]], false)], TimeSpan.Zero));
        Push(Keys.Down, Keys.Enter);                                  // old, under + New
        Push([.. Enumerable.Repeat(Keys.Down, 13), Keys.Enter]);      // from Save hidden to the Schemas row
        Push(Keys.Down, Keys.Down, Keys.Enter);                       // sales ✓, public ✓, archive, Continue: archive
        Push(Keys.Down, Keys.Enter);                                  // Continue
        Push(Keys.Down, Keys.Down, Keys.Enter);                       // which comes first: archive
        Push(Keys.Down, Keys.Enter);                                  // which comes next: public (sales last)
        Push([.. Enumerable.Repeat(Keys.Up, 13), Keys.Enter]);        // Save, hidden: thirteen up (the cursor stayed on Schemas, 2026-11-26)

        await settings.EditAsync(SettingsField.PostgresAddConnection, _settings.Current, new MenuPage("Tools", [], ""), 0, CancellationToken.None);

        Assert.Contains(SettingsMenu.PostgresWizardFirstQuestion, Output);
        Assert.Contains(SettingsMenu.WizardSchemaNextQuestion, Output);
        var old = Assert.Single(NeonSidekick.Postgres.PostgresConfigFile.Load(path).Connections).Config;
        Assert.Equal(["archive", "public", "sales"], old.Schemas);
    }

    private static int CountOf(string text, string part)
    {
        int count = 0;
        for (int at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    [Theory]
    [InlineData("hr, oe", new[] { "hr", "oe" })]
    [InlineData("\"a,b\", HR", new[] { "\"a,b\"", "HR" })]
    [InlineData(" , x ,, ", new[] { "x" })]
    [InlineData("\"a\"\"b,c\", d", new[] { "\"a\"\"b,c\"", "d" })]
    public void TheTypedSchemas_SplitOnCommasOutsideQuotes(string text, string[] names) =>
        Assert.Equal(names, SettingsMenu.WizardSchemaNames(text));   // #121
}
