using NeonSidekick.App;
using NeonSidekick.Claude;
using NeonSidekick.Settings;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// <c>/claude</c>'s model picker (2026-09-27, the user's ask: pick, don't type) on <c>/settings</c>' Anthropic tab, where its four rows
/// sit under the Claude Code server's since 2026-10-10 (the user's call; <c>/tools</c>' ClaudeCode tab until then, which went with the
/// Claude Code advisor tool).
/// </summary>
public partial class SettingsMenuTests
{
    /// <summary>The Anthropic tab, then the row (1 the executable, 2 the permissions, 3 the model, 4 the effort) and Enter: its picker.</summary>
    private void OpenClaudeRow(int row)
    {
        GoTo(SettingsTab.Anthropic);
        Down(row);
        Push(Keys.Enter);
    }

    [Fact]
    public void TheAnthropicTab_ClaudeCodeFirst_ThenClaudesFour_ThenTheApi()
    {
        Assert.Equal(
            [SettingsField.ClaudeCodeServer, SettingsField.ClaudeCodeExecutable, SettingsField.ClaudeCodePermissions, SettingsField.ClaudeCodeModel, SettingsField.ClaudeCodeEffort,
             SettingsField.AnthropicApi, SettingsField.AnthropicApiKey, SettingsField.AnthropicApiMaxTokens, SettingsField.AnthropicApiPromptCaching],
            SettingsMenu.TabFields[(int)SettingsTab.Anthropic]);
        Assert.All(SettingsMenu.TabFields[(int)SettingsTab.Anthropic].Skip(1).Take(4), field => Assert.False(SettingsMenu.IsLlmField(field)));   // read at each /claude: no reconnect
    }

    [Fact]
    public async Task OnThePane_ClaudeCommandModel_IsAPicker_TheAliasSaves()
    {
        var (menu, pane) = PaneMenu();
        OpenClaudeRow(3);
        Push(Keys.Down, Keys.Down, Keys.Enter);   // the default, fable, opus
        Push(Keys.Escape);

        Assert.Equal(SettingsChanges.None, await menu.ShowAsync(CancellationToken.None));   // no reconnect

        Assert.Equal("opus", _settings.Current.ClaudeCodeModel);
        Assert.Contains("fable   " + "the latest Fable", Output);
        Assert.Contains(SettingsMenu.ClaudeModelOtherWord + "  type a full model name", Output);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_ClaudeModel_OtherTypesAFullName_AndReopensOnOther()
    {
        var (menu, pane) = PaneMenu();
        OpenClaudeRow(3);
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Down, Keys.Enter);   // Other…: the typed slot
        _console.Input.PushText("claude-opus-5-5");
        Push(Keys.Enter);
        Push(Keys.Enter);                                                           // the picker again, on Other…
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal("claude-opus-5-5", _settings.Current.ClaudeCodeModel);
        Assert.Contains("▸ " + SettingsMenu.ClaudeModelOtherWord + "  (claude-opus-5-5)", Output);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_ClaudeModel_EscKeepsTheSavedValue()
    {
        _settings.Update(d => d.ClaudeCodeModel = "haiku");
        var (menu, pane) = PaneMenu();
        OpenClaudeRow(3);
        Push(Keys.Down, Keys.Escape);
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal("haiku", _settings.Current.ClaudeCodeModel);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_ClaudeEffort_IsAPicker_TheWordSaves()
    {
        var (menu, pane) = PaneMenu();
        OpenClaudeRow(4);
        Push(Keys.Down, Keys.Enter);   // the default, low
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal("low", _settings.Current.ClaudeCodeEffort);
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
