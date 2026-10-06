using NeonSidekick.App;
using NeonSidekick.Settings;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// The YouTube tab of <c>/tools</c> (2026-10-05, the YouTube plan): after Screen, the switch, the API key typed masked and saved
/// encrypted, the typed max results, autoplay; the key's other homes (the log's mask, the encryption at rest, the variable).
/// </summary>
public partial class ToolsMenuTests
{
    private void OpenYouTubeRow(int row) => Push([.. ToTab(ToolsText.YouTubeTabTitle), .. Enumerable.Repeat(Keys.Down, row), Keys.Enter]);

    [Fact]
    public void TheYouTubeTab_SitsAfterScreen_WithItsFiveRows()
    {
        int tab = ToolsText.TabTitles.ToList().IndexOf(ToolsText.YouTubeTabTitle);

        Assert.Equal(ToolsText.TabTitles.ToList().IndexOf(ToolsText.ScreenTabTitle) + 1, tab);
        Assert.Equal(["YouTube tools", "YouTube API key", "YouTube search max results", "YouTube autoplay", "YouTube while speaking"], SettingsMenu.ToolsTabFields[tab - 1].Select(SettingsMenu.FieldName));
        Assert.Equal([SettingsField.YouTubeTools, SettingsField.YouTubeAutoplay], SettingsMenu.ToolsTabFields[tab - 1].Where(SettingsMenu.IsToggle));
        Assert.DoesNotContain(SettingsMenu.ToolsTabFields[tab - 1], SettingsMenu.RefusedMidTurn);
        Assert.DoesNotContain(SettingsMenu.ToolsTabFields[tab - 1], SettingsMenu.IsLlmField);   // no reconnect: read at each turn and search

        var data = new AppSettingsData();
        Assert.Equal(["off", "(none)", "8 results", "on", "pause"], SettingsMenu.ToolsTabFields[tab - 1].Select(f => SettingsMenu.FieldValue(f, data, _settings.ProfileDirectory)));
        Assert.Equal(SettingsMenu.Mask("AIza-plain"), SettingsMenu.FieldValue(SettingsField.YouTubeApiKey, new AppSettingsData { YouTubeApiKey = "AIza-plain" }, _settings.ProfileDirectory));
        Assert.Equal(SettingsMenu.ApiKeyEncryptedLabel, SettingsMenu.FieldValue(SettingsField.YouTubeApiKey, new AppSettingsData { YouTubeApiKey = Sql.WindowsCredentials.ProtectedPrefix + "AQAA" }, _settings.ProfileDirectory));
        Assert.Equal("", SettingsMenu.EditableValue(SettingsField.YouTubeApiKey, new AppSettingsData { YouTubeApiKey = "AIza-plain" }));   // never put back on the line
        Assert.Equal("must be 1 to 20 results", SettingsMenu.YouTubeSearchMaxResultsRangeError);
        Assert.Equal("YouTube API key saved unencrypted: no DPAPI.", SettingsMenu.YouTubeApiKeyPlainWarning("no DPAPI"));
    }

    [Fact]
    public async Task OnThePane_TheYouTubeTab_TheSwitchSaves_TheKeyIsMasked_TheCountRefusedOutOfRange()
    {
        var (menu, pane, _) = PaneMenu();
        OpenYouTubeRow(0);                         // YouTube tools: the on/off page, off under the cursor
        Push(Keys.Up, Keys.Enter);                 // on
        Push(Keys.Down, Keys.Enter);               // the key: a masked slot
        _console.Input.PushText("AIza-youtube-secret");
        Push(Keys.Enter);
        Push(Keys.Down, Keys.Enter, Keys.Ctrl(ConsoleKey.A), Keys.Backspace);
        _console.Input.PushText("40");
        Push(Keys.Enter);                          // refused: over 20
        Push(Keys.Enter, Keys.Ctrl(ConsoleKey.A), Keys.Backspace);
        _console.Input.PushText("12");
        Push(Keys.Enter);
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        var saved = _settings.Current;
        Assert.True(saved.YouTubeTools);
        Assert.Equal("AIza-youtube-secret", SettingsSecrets.Reveal(saved.YouTubeApiKey));
        Assert.DoesNotContain("AIza-youtube-secret", _console.Output);
        if (OperatingSystem.IsWindows())
        {
            Assert.StartsWith(Sql.WindowsCredentials.ProtectedPrefix, saved.YouTubeApiKey);
            Assert.Contains("  · YouTube API key: " + SettingsMenu.ApiKeyEncryptedLabel, _console.Output);
        }

        Assert.Contains("  ✗ YouTube search max results " + SettingsMenu.YouTubeSearchMaxResultsRangeError + "; keeping 8.", _console.Output);
        Assert.Equal(12, saved.YouTubeSearchMaxResults);
        pane.Dispose();
    }

    [Fact]
    public void TheYouTubeKey_IsMaskedInTheLog_EncryptedAtRest_AndCopied()
    {
        Assert.Equal(["YouTubeApiKey: " + SettingsDiff.Redacted], SettingsDiff.Changes(new AppSettingsData(), new AppSettingsData { YouTubeApiKey = "AIza-secret" }));
        var copy = AppSettings.Copy(new AppSettingsData { YouTubeTools = true, YouTubeApiKey = "AIza-k", YouTubeSearchMaxResults = 3, YouTubeAutoplay = false });
        Assert.Equal((true, "AIza-k", 3, false), (copy.YouTubeTools, copy.YouTubeApiKey, copy.YouTubeSearchMaxResults, copy.YouTubeAutoplay));
        Assert.Contains(nameof(AppSettingsData.YouTubeApiKey), Profiles.ResetKeptSettings);   // a plain /profile reset keeps it (the user's ask)

        if (OperatingSystem.IsWindows())
        {
            var data = new AppSettingsData { YouTubeApiKey = "AIza-hand-edit" };   // a plain value typed into profile.json
            Assert.True(SettingsSecrets.ProtectAtRest(data));
            Assert.True(Sql.WindowsCredentials.IsProtected(data.YouTubeApiKey));
            Assert.Equal("AIza-hand-edit", SettingsSecrets.Reveal(data.YouTubeApiKey));
        }
    }

    /// <summary>NEONSIDEKICK_YOUTUBE_API_KEY: laid over the saved key for the run, logged as set, never by value.</summary>
    [Fact]
    public void TheYouTubeKeyVariable_OverridesForTheRun_AndIsNeverShown()
    {
        var environment = new EnvironmentOverrides(name => name == EnvironmentOverrides.YouTubeApiKeyVariable ? " AIza-env " : null);

        Assert.Equal("NEONSIDEKICK_YOUTUBE_API_KEY", EnvironmentOverrides.YouTubeApiKeyVariable);
        Assert.Contains(EnvironmentOverrides.YouTubeApiKeyVariable, EnvironmentOverrides.AllVariables);
        Assert.Equal("AIza-env", environment.ApplyTo(new AppSettingsData { YouTubeApiKey = "saved" }).YouTubeApiKey);
        Assert.Equal($"{EnvironmentOverrides.YouTubeApiKeyVariable}={EnvironmentOverrides.SecretSet}", environment.Describe());
    }
}
