using NeonSidekick.App;
using NeonSidekick.Sql;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>The server families' write rows on their tabs (2026-10-05): each mode picks read-write, each checklist puts the default back.</summary>
public partial class ToolsMenuTests
{
    public static TheoryData<string, SettingsField> WriteTabs() => new()
    {
        { ToolsText.MySqlTabTitle, SettingsField.MySqlMode },
        { ToolsText.PostgresTabTitle, SettingsField.PostgresMode },
    };

    [Theory]
    [MemberData(nameof(WriteTabs))]
    public async Task OnThePane_EachFamilysMode_PicksReadWrite_AndItsChecklistTakesTheDefault(string tab, SettingsField mode)
    {
        _settings.Update(d => d.MySqlStatementsAllowed = d.PostgresStatementsAllowed = ["drop"]);
        var (menu, _, _) = PaneMenu();
        Push([.. ToTab(tab), Keys.Down, Keys.Enter]);   // the mode, second on the tab
        Push(Keys.Down, Keys.Enter);                     // read-write
        Push(Keys.Down, Keys.Enter);                     // the statements, third
        Push(Keys.Char('d'));                            // the default
        Push(Keys.Escape);
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        var (saved, kinds) = mode switch
        {
            SettingsField.MySqlMode => (_settings.Current.MySqlMode, _settings.Current.MySqlStatementsAllowed),
            _ => (_settings.Current.PostgresMode, _settings.Current.PostgresStatementsAllowed),
        };
        Assert.Equal("read-write", saved);
        Assert.Equal(ServerStatementKinds.Default(), kinds);
    }
}
