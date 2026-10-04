using NeonSidekick.App;
using NeonSidekick.Settings;
using NeonSidekick.Sqlite;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.Tests;

/// <summary>
/// The offered checklists drop a saved name they no longer list as they open (2026-10-04, the user's call: such a name rode along
/// unseen at every save, even N kept it, so <c>profile.json</c> held workflows long deleted). The status line names what went; a
/// whole file that cannot be read drops nothing, and a name a load problem still names (a broken entry, a workflow that failed to
/// load) keeps its tick.
/// </summary>
public partial class ToolsMenuTests
{
    /// <summary>Seeds <paramref name="field"/> with "gone" before <paramref name="kept"/>, opens its checklist and leaves it at once: the gone name is dropped and named, and no "unchanged" follows.</summary>
    private async Task AssertDroppedAsItOpensAsync(SettingsField field, Action<AppSettingsData, List<string>> seed, Func<AppSettingsData, List<string>?> read, Action open, params string[] kept)
    {
        _settings.Update(d => seed(d, ["gone", .. kept]));
        var (menu, _, _) = PaneMenu();
        open();
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(kept, read(_settings.Current));
        Assert.Contains(SettingsMenu.StaleDroppedNotice(field, ["gone"]), _console.Output);
        Assert.DoesNotContain(SettingsMenu.UnchangedNotice, _console.Output);
    }

    private void WriteProfileFile(string path, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }

    [Fact]
    public async Task TheSqlOffered_DropsAGoneName_AsItOpens()
    {
        WriteProfileFile(NeonSidekick.Sql.SqlConfigFile.ProfilePath(_settings.ProfileDirectory), """{ "connections": { "aw": { "server": "x", "auth": "windows" } } }""");
        await AssertDroppedAsItOpensAsync(SettingsField.SqlConnectionsOffered, (d, v) => d.SqlConnectionsOffered = v, d => d.SqlConnectionsOffered,
            () => Push([.. ToTab(ToolsText.SqlTabTitle), Keys.Down, Keys.Enter]), "aw");
    }

    [Fact]
    public async Task TheOracleOffered_DropsAGoneName_AsItOpens()
    {
        WriteProfileFile(NeonSidekick.Oracle.OracleConfigFile.ProfilePath(_settings.ProfileDirectory), """{ "connections": { "free": { "dataSource": "x:1521/y", "user": "u" } } }""");
        await AssertDroppedAsItOpensAsync(SettingsField.OracleConnectionsOffered, (d, v) => d.OracleConnectionsOffered = v, d => d.OracleConnectionsOffered, () => OpenOracleRow(1), "free");
    }

    [Fact]
    public async Task TheMySqlOffered_DropsAGoneName_AsItOpens()
    {
        WriteProfileFile(NeonSidekick.MySql.MySqlConfigFile.ProfilePath(_settings.ProfileDirectory), """{ "connections": { "shop": { "host": "localhost", "user": "reader" } } }""");
        await AssertDroppedAsItOpensAsync(SettingsField.MySqlConnectionsOffered, (d, v) => d.MySqlConnectionsOffered = v, d => d.MySqlConnectionsOffered, () => OpenMySqlRow(1), "shop");
    }

    [Fact]
    public async Task ThePostgresOffered_DropsAGoneName_AsItOpens()
    {
        WriteProfileFile(NeonSidekick.Postgres.PostgresConfigFile.ProfilePath(_settings.ProfileDirectory), """{ "connections": { "shop": { "host": "localhost", "database": "shop", "user": "reader" } } }""");
        await AssertDroppedAsItOpensAsync(SettingsField.PostgresConnectionsOffered, (d, v) => d.PostgresConnectionsOffered = v, d => d.PostgresConnectionsOffered, () => OpenPostgresRow(1), "shop");
    }

    [Fact]
    public async Task TheSqliteOffered_DropsAGoneName_AsItOpens()
    {
        Assert.Null(SqliteConfigFile.AddDatabase(SqliteConfigFile.GlobalPath(_settings.StorageDirectory), "notes", new SqliteDatabaseConfig { Path = MakeSqliteFile("notes.db") }));
        await AssertDroppedAsItOpensAsync(SettingsField.SqliteDatabasesOffered, (d, v) => d.SqliteDatabasesOffered = v, d => d.SqliteDatabasesOffered, () => OpenSqliteRow(1), "notes");
    }

    [Fact]
    public async Task TheUncOffered_DropsAGoneName_AsItOpens()
    {
        WriteProfileFile(NeonSidekick.Unc.UncConfigFile.ProfilePath(_settings.ProfileDirectory), """{ "shares": { "eng": { "path": "//fs01/eng" } } }""");
        await AssertDroppedAsItOpensAsync(SettingsField.UncSharesOffered, (d, v) => d.UncSharesOffered = v, d => d.UncSharesOffered, () => OpenUncRow(2), "eng");
    }

    [Fact]
    public async Task TheComfyOffered_DropsAGoneName_AsItOpens()
    {
        ComfyWorkflowFile(_settings.ProfileComfyDirectory, "pony-txt2img");
        await AssertDroppedAsItOpensAsync(SettingsField.ComfyWorkflowsOffered, (d, v) => d.ComfyWorkflowsOffered = v, d => d.ComfyWorkflowsOffered, () => OpenImagesRow(2), "pony-txt2img");
    }

    /// <summary>A broken entry's name keeps its tick through the opening and through a save; the gone one goes.</summary>
    [Fact]
    public async Task TheSqlOffered_KeepsABrokenEntrysName()
    {
        WriteProfileFile(NeonSidekick.Sql.SqlConfigFile.ProfilePath(_settings.ProfileDirectory), """{ "connections": { "aw": { "server": "x", "auth": "windows" }, "broken": { "auth": "windows" } } }""");
        _settings.Update(d => d.SqlConnectionsOffered = ["gone", "broken"]);
        var (menu, _, _) = PaneMenu();
        Push([.. ToTab(ToolsText.SqlTabTitle), Keys.Down, Keys.Enter]);
        Push(Keys.Enter);                                    // aw on
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(["aw", "broken"], _settings.Current.SqlConnectionsOffered);
        Assert.Contains(SettingsMenu.StaleDroppedNotice(SettingsField.SqlConnectionsOffered, ["gone"]), _console.Output);
    }

    /// <summary>A profile file that cannot be read hides its names, so nothing is dropped while it is broken.</summary>
    [Fact]
    public async Task TheSqlOffered_DropsNothing_WhileAFileCannotBeRead()
    {
        WriteProfileFile(NeonSidekick.Sql.SqlConfigFile.ProfilePath(_settings.ProfileDirectory), "{ not json");
        WriteProfileFile(NeonSidekick.Sql.SqlConfigFile.GlobalPath(_settings.StorageDirectory), """{ "connections": { "aw": { "server": "x", "auth": "windows" } } }""");
        _settings.Update(d => d.SqlConnectionsOffered = ["mine", "aw"]);
        var (menu, _, _) = PaneMenu();
        Push([.. ToTab(ToolsText.SqlTabTitle), Keys.Down, Keys.Enter]);
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(["mine", "aw"], _settings.Current.SqlConnectionsOffered);
        Assert.DoesNotContain("no longer listed", _console.Output);
    }

    /// <summary>A workflow whose file failed to load keeps its tick; the gone one goes.</summary>
    [Fact]
    public async Task TheComfyOffered_KeepsAWorkflowThatFailedToLoad()
    {
        ComfyWorkflowFile(_settings.ProfileComfyDirectory, "pony-txt2img");
        File.WriteAllText(Path.Combine(_settings.ProfileComfyDirectory, "broken.json"), "{ not json");
        _settings.Update(d => d.ComfyWorkflowsOffered = ["gone", "broken", "pony-txt2img"]);
        var (menu, _, _) = PaneMenu();
        OpenImagesRow(2);
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(["broken", "pony-txt2img"], _settings.Current.ComfyWorkflowsOffered);
        Assert.Contains(SettingsMenu.StaleDroppedNotice(SettingsField.ComfyWorkflowsOffered, ["gone"]), _console.Output);
    }

    [Fact]
    public void StaleDroppedNotice_NamesTheFieldTheCountAndTheNames() =>
        Assert.Equal("ComfyUI workflows offered: dropped 2 no longer listed: a, b", SettingsMenu.StaleDroppedNotice(SettingsField.ComfyWorkflowsOffered, ["a", "b"]));

    /// <summary>The ComfyUI checklist's rows line up (2026-10-04, the user's ask): family, shape and size each start in one column, the size's <c>×</c> too.</summary>
    [Fact]
    public void TheComfyOfferedRows_LineUpTheirColumns()
    {
        ComfyWorkflowFile(_settings.ProfileComfyDirectory, "a");
        File.WriteAllText(Path.Combine(_settings.ProfileComfyDirectory, "image-to-image-long.json"),
            "{\"6\":{\"class_type\":\"CLIPTextEncode\",\"inputs\":{\"text\":\"{{prompt}}\"}},\"7\":{\"class_type\":\"LoadImage\",\"inputs\":{\"image\":\"{{image}}\"}}}");
        var installed = new NeonSidekick.Comfy.ComfyWorkflowCatalog(() => [_settings.ProfileComfyDirectory]).Workflows;
        var columns = SettingsMenu.ComfyOfferedColumns.Of(installed);
        var rows = installed.Select(w => Markup.Remove(SettingsMenu.ComfyOfferedRow(w, false, columns))).ToList();

        Assert.Equal(["a", "image-to-image-long"], installed.Select(w => w.Name));
        Assert.Equal("image-to-image-long".Length + 2, columns.Name);
        Assert.StartsWith("[ ] a" + new string(' ', columns.Name - 1), rows[0]);
        Assert.Equal(rows[0].IndexOf("text → image", StringComparison.Ordinal), rows[1].IndexOf("image → image", StringComparison.Ordinal));   // the shapes in one column
        Assert.Contains("text → image   ", rows[0]);                                                                                          // padded to image → image, two apart
        Assert.Equal(rows[0].IndexOf('×'), rows[1].IndexOf('×'));                                                                             // the sizes too
        Assert.DoesNotContain(" · ", rows[0]);
    }
}
