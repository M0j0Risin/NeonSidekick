using NeonSidekick.App;
using NeonSidekick.Claude;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;
using Spectre.Console.Testing;

namespace NeonSidekick.Tests;

/// <summary>The Claude tab's model pickers (2026-09-27, the user's ask: pick, don't type).</summary>
public partial class ToolsMenuTests
{
    /// <summary>The Claude tab (<see cref="ToTab"/>; after Oracle since 2026-10-03), then the row (2 the command's model, 7 the advisor's) and Enter: its picker.</summary>
    private void OpenClaudeRow(int row) => Push([.. ToTab(ToolsText.ClaudeCliTabTitle), .. Enumerable.Repeat(Keys.Down, row), Keys.Enter]);

    [Fact]
    public async Task OnThePane_ClaudeCommandModel_IsAPicker_TheAliasSaves()
    {
        var (menu, pane, _) = PaneMenu();
        OpenClaudeRow(2);
        Push(Keys.Down, Keys.Down, Keys.Enter);   // the default, fable, opus
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal("opus", _settings.Current.ClaudeCliModel);
        Assert.Contains("fable   " + "the latest Fable", Output);
        Assert.Contains(SettingsMenu.ClaudeModelOtherWord + "  type a full model name", Output);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_ClaudeAdvisorModel_OpensOnTheSavedAlias_TheBlankRowFollowsTheCommand()
    {
        _settings.Update(d => d.ClaudeCliAdvisorModel = "Sonnet");
        var (menu, pane, _) = PaneMenu();
        OpenClaudeRow(7);
        Push(Keys.Up, Keys.Up, Keys.Up, Keys.Enter);   // sonnet → opus → fable → the blank row
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal("", _settings.Current.ClaudeCliAdvisorModel);
        Assert.Contains("▸ sonnet  ", Output);   // opened on the saved alias, any case
        Assert.Contains(SettingsMenu.ClaudeAdvisorModelLabel, Output);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_ClaudeModel_OtherTypesAFullName_AndReopensOnOther()
    {
        var (menu, pane, _) = PaneMenu();
        OpenClaudeRow(2);
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter);   // Other…: the typed slot
        Type("claude-opus-5-5");
        Push(Keys.Enter);                                                           // the picker again, on Other…
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal("claude-opus-5-5", _settings.Current.ClaudeCliModel);
        Assert.Contains("▸ " + SettingsMenu.ClaudeModelOtherWord + "  (claude-opus-5-5)", Output);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_ClaudeModel_EscKeepsTheSavedValue()
    {
        _settings.Update(d => d.ClaudeCliModel = "haiku");
        var (menu, pane, _) = PaneMenu();
        OpenClaudeRow(2);
        Push(Keys.Down, Keys.Escape);
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal("haiku", _settings.Current.ClaudeCliModel);
        pane.Dispose();
    }

    [Fact]
    public void ClaudeModels_TheAliasesAndTheirRows()
    {
        Assert.Equal(["fable", "opus", "sonnet", "haiku"], ClaudeModels.Aliases);
        Assert.Equal("opus", ClaudeModels.AliasOf(" OPUS "));
        Assert.Null(ClaudeModels.AliasOf("claude-opus-5-5"));
        Assert.Null(ClaudeModels.AliasOf(""));
        Assert.Equal("haiku   [#9A8BB8]the latest Haiku: fast, the cheapest[/]", SettingsMenu.ClaudeModelLabel("haiku"));
        Assert.Equal("(Claude Code's default)", SettingsMenu.ClaudeModelLabel(""));
        Assert.Equal("Other…  [#9A8BB8](claude-x)[/]", SettingsMenu.ClaudeModelOtherLabel("claude-x"));
        Assert.Equal("Other…  [#9A8BB8]type a full model name[/]", SettingsMenu.ClaudeModelOtherLabel("opus"));
    }
}
