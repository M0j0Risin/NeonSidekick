using NeonSidekick.App;
using NeonSidekick.Settings;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>The Claude and OpenAI tabs of <c>/settings</c> (2026-10-03): after Docker, every row a reconnect, the keys typed masked.</summary>
public partial class SettingsMenuTests
{
    [Fact]
    public void TheClaudeAndOpenAITabs_AreAfterDocker_BeforeAdvisors()
    {
        // Advisors before TTS and STT (the Voice group) later still on 2026-10-10 (the user's order).
        // Chat (General until then), Sessions and Botchat first, the Client group, since later on 2026-10-10 (the user's call); Common and Http in its place since 2026-10-10
        // (the user's call: the LLM tab split); LLM second since 2026-10-04 (the user's order); after OpenAI from 2026-10-03 until then.
        Assert.Equal(["Chat", "Sessions", "Botchat", "Web UI", "Common", "Http", "Embedded", "Docker", "Anthropic", "OpenAI", "Advisors", "Automation", "TTS", "STT"], SettingsMenu.TabTitles);   // Automation between Advisors and Voice since 2026-10-21 (the user's ask)
        Assert.Equal(SettingsMenu.AnthropicTabTitle, SettingsMenu.TabTitles[(int)SettingsTab.Anthropic]);
        Assert.Equal(SettingsMenu.OpenAITabTitle, SettingsMenu.TabTitles[(int)SettingsTab.OpenAI]);
        Assert.Equal(33, SettingsMenu.LabelWidthOf(SettingsMenu.TabFields[(int)SettingsTab.Anthropic]));   // "Claude Code command permissions" since later on 2026-10-10 ("slash command", 39, from 2026-10-10, /claude's rows here); "Anthropic API prompt caching" (30) from 2026-10-04, "Claude API prompt caching" (27) before
        Assert.Equal(25, SettingsMenu.LabelWidthOf(SettingsMenu.TabFields[(int)SettingsTab.OpenAI]));   // "OpenAI API organization"
        Assert.Equal("must be 0 (the model's own) or 1024 to 128000 tokens", SettingsMenu.OpenAIApiMaxTokensRangeError);
        // The OpenAI tab keeps its rows' whole names (2026-10-10, the user's call: "API key" said nothing of whose); the others shorten.
        Assert.True(SettingsMenu.KeepsWholeNames(SettingsMenu.OpenAITabTitle));
        Assert.False(SettingsMenu.KeepsWholeNames("Botchat") || SettingsMenu.KeepsWholeNames(SettingsMenu.AnthropicTabTitle));
        // Common's and Http's rows all start "LLM ", never their tab's title, so they show whole (2026-10-10).
        Assert.Equal("LLM URL", SettingsMenu.RowName(SettingsField.LlmUrl, SettingsMenu.CommonTabTitle));
        Assert.Equal("LLM server scan mode", SettingsMenu.RowName(SettingsField.LlmScanMode, SettingsMenu.HttpTabTitle));
    }

    [Fact]
    public async Task OnThePane_TheOpenAITab_ShowsTheWholeNames()
    {
        var (menu, pane) = PaneMenu();
        GoTo(SettingsTab.OpenAI);
        Push(Keys.Escape);

        Assert.Equal(SettingsChanges.None, await menu.ShowAsync(CancellationToken.None));

        Assert.Contains("▸ " + SettingsMenu.FieldName(SettingsField.OpenAIApi) + " ", Output);
        Assert.Contains(SettingsMenu.FieldName(SettingsField.OpenAIApiOrganization), Output);
        Assert.DoesNotContain("▸ API ", Output);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_TheClaudeApiSwitch_OnTheClaudeTab_AsksForAReconnect()
    {
        // The Anthropic API's rows were /tools' from 2026-09-29 until 2026-10-03 (the user's call: /settings' own Claude tab);
        // a save of one is a reconnect, which the screen makes once the pane closes.
        var (menu, pane) = PaneMenu();
        GoTo(SettingsTab.Anthropic);
        Down(5);                                   // the Claude Code server first since 2026-10-10 (the user's call), /claude's four under it later that day
        Push(Keys.Enter, Keys.Up, Keys.Enter);     // Anthropic API: the page opens on off, on picked
        Push(Keys.Escape);

        Assert.Equal(SettingsChanges.Llm, await menu.ShowAsync(CancellationToken.None));

        Assert.True(_settings.Current.AnthropicApi);
        Assert.Contains("  · Anthropic API enabled: on", Output);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_TheOpenAITab_TheKeyIsMasked_TheCapRefusedOutOfRange_AndTheHeadersSave()
    {
        var (menu, pane) = PaneMenu();
        GoTo(SettingsTab.OpenAI);
        Down(1);
        Push(Keys.Enter);                                            // the key: a masked slot
        _console.Input.PushText("sk-openai-secret");
        Push(Keys.Enter);
        Down(1);
        Push(Keys.Enter, Keys.Ctrl(ConsoleKey.A), Keys.Backspace);   // the cap
        _console.Input.PushText("500");
        Push(Keys.Enter);                                            // refused: under 1,024 and not 0
        Push(Keys.Enter, Keys.Ctrl(ConsoleKey.A), Keys.Backspace);
        _console.Input.PushText("8192");
        Push(Keys.Enter);
        Down(1);
        Push(Keys.Enter);                                            // the organization
        _console.Input.PushText(" org-1 ");
        Push(Keys.Enter);
        Push(Keys.Escape);

        Assert.Equal(SettingsChanges.Llm, await menu.ShowAsync(CancellationToken.None));

        var saved = _settings.Current;
        Assert.Equal("sk-openai-secret", SettingsSecrets.Reveal(saved.OpenAIApiKey));
        Assert.DoesNotContain("sk-openai-secret", Output);
        if (OperatingSystem.IsWindows())
        {
            Assert.StartsWith(Sql.WindowsCredentials.ProtectedPrefix, saved.OpenAIApiKey);
            Assert.Contains(SettingsMenu.ApiKeyEncryptedLabel, Output);
        }

        Assert.Contains("  ✗ OpenAI API max tokens " + SettingsMenu.OpenAIApiMaxTokensRangeError + "; keeping 0.", Output);
        Assert.Equal(8192, saved.OpenAIApiMaxTokens);
        Assert.Equal("org-1", saved.OpenAIApiOrganization);
        pane.Dispose();
    }
}
