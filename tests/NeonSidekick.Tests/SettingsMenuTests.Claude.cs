using NeonSidekick.App;
using NeonSidekick.Claude;
using NeonSidekick.Settings;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// <c>/claude</c>'s model picker (2026-09-27, the user's ask: pick, don't type) on <c>/settings</c>' Anthropic tab, where its four rows
/// sit under the Claude Code server's since 2026-10-10 (the user's call; <c>/tools</c>' ClaudeCode tab until then, which went with the
/// Claude Code advisor tool), the executable above the server's switch since later that day, each run under its heading.
/// </summary>
public partial class SettingsMenuTests
{
    /// <summary>The Anthropic tab, then the row (0 the executable, 1 the server, 2 the permissions, 3 the model, 4 the effort; the headings never count) and Enter: its picker.</summary>
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
            [SettingsField.ClaudeCodeExecutable, SettingsField.ClaudeCodeServer, SettingsField.ClaudeCodePermissions, SettingsField.ClaudeCodeModel, SettingsField.ClaudeCodeEffort,
             SettingsField.AnthropicApi, SettingsField.AnthropicApiKey, SettingsField.AnthropicApiMaxTokens, SettingsField.AnthropicApiPromptCaching],
            SettingsMenu.TabFields[(int)SettingsTab.Anthropic]);
        Assert.All([SettingsField.ClaudeCodeExecutable, SettingsField.ClaudeCodePermissions, SettingsField.ClaudeCodeModel, SettingsField.ClaudeCodeEffort],
            field => Assert.False(SettingsMenu.IsLlmField(field)));   // read at each /claude: no reconnect
        Assert.True(SettingsMenu.IsLlmField(SettingsField.ClaudeCodeServer));
        // The runs (later on 2026-10-10, the user's ask): what both read, the server's, /claude's, the API's.
        Assert.Equal(["Claude Code", "Claude Code server", "/claude command", "Anthropic API"], SettingsMenu.TabSections[SettingsTab.Anthropic].Select(s => s.Title));
        Assert.Equal([null, SettingsField.ClaudeCodeExecutable, null, SettingsField.ClaudeCodeServer, null, SettingsField.ClaudeCodePermissions],
            SettingsMenu.TabRows((int)SettingsTab.Anthropic).Take(6));
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
