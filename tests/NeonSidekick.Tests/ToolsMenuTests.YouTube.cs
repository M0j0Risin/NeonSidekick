using NeonSidekick.App;
using NeonSidekick.Settings;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// The YouTube tab of <c>/tools</c> (2026-10-05, the YouTube plan): after ComfyUI, before GitLib, the switch, the API key typed masked and saved
/// encrypted, the typed max results, autoplay; the key's other homes (the log's mask, the encryption at rest, the variable).
/// </summary>
public partial class ToolsMenuTests
{
    private void OpenYouTubeRow(int row) => Push([.. ToTab(ToolsText.YouTubeTabTitle), .. Enumerable.Repeat(Keys.Down, row), Keys.Enter]);

    [Fact]
    public void TheYouTubeTab_SitsAfterComfyUI_BeforeGitLib_WithItsTenRows()
    {
        int tab = ToolsText.MenuIndex(ToolsText.YouTubeTabTitle) + 1;   // a menu on Media since 2026-10-09, its rows ToolsTabFields' as before

        Assert.Equal(ToolsText.MenuIndex(ToolsText.ComfyTabTitle) + 2, tab);   // the user's place, later on 2026-10-05 (after Screen until then); last on Media since 2026-10-09
        Assert.Equal(ToolsText.AppsTabTitle, ToolsText.TabTitles[TabIndex(ToolsText.YouTubeTabTitle)]);   // on Apps (Integrations until 2026-10-11) since 2026-10-10 (Media from 2026-10-09)
        Assert.Equal(["YouTube tools", "YouTube API key", "YouTube search max results", "YouTube search order", "YouTube safe search", "YouTube region", "YouTube language", "YouTube open links in", "YouTube autoplay", "YouTube while speaking"], SettingsMenu.ToolsTabFields[tab - 1].Select(SettingsMenu.FieldName));   // open links in on 2026-10-16
        Assert.Equal([SettingsField.YouTubeTools, SettingsField.YouTubeAutoplay], SettingsMenu.ToolsTabFields[tab - 1].Where(SettingsMenu.IsToggle));
        Assert.DoesNotContain(SettingsMenu.ToolsTabFields[tab - 1], SettingsMenu.RefusedMidTurn);
        Assert.DoesNotContain(SettingsMenu.ToolsTabFields[tab - 1], SettingsMenu.IsLlmField);   // no reconnect: read at each turn and search

        var data = new AppSettingsData();
        Assert.Equal(["off", "(none)", "8 results", "relevance", "moderate", "(any region)", "(any language)", "youtube-window", "on", "pause"], SettingsMenu.ToolsTabFields[tab - 1].Select(f => SettingsMenu.FieldValue(f, data, _settings.ProfileDirectory)));
        Assert.Equal(["views", "strict", "GB", "pt-BR"], new[] { SettingsField.YouTubeSearchOrder, SettingsField.YouTubeSafeSearch, SettingsField.YouTubeRegion, SettingsField.YouTubeLanguage }
            .Select(f => SettingsMenu.FieldValue(f, new AppSettingsData { YouTubeSearchOrder = "Views", YouTubeSafeSearch = "strict", YouTubeRegion = "gb", YouTubeLanguage = "PT-BR" }, _settings.ProfileDirectory)));
        Assert.Equal("relevance", SettingsMenu.FieldValue(SettingsField.YouTubeSearchOrder, new AppSettingsData { YouTubeSearchOrder = "newest" }, _settings.ProfileDirectory));   // unreadable: YouTube's own
        Assert.Equal(SettingsMenu.Mask("AIza-plain"), SettingsMenu.FieldValue(SettingsField.YouTubeApiKey, new AppSettingsData { YouTubeApiKey = "AIza-plain" }, _settings.ProfileDirectory));
        Assert.Equal(SettingsMenu.ApiKeyEncryptedLabel, SettingsMenu.FieldValue(SettingsField.YouTubeApiKey, new AppSettingsData { YouTubeApiKey = Sql.WindowsCredentials.ProtectedPrefix + "AQAA" }, _settings.ProfileDirectory));
        Assert.Equal("", SettingsMenu.EditableValue(SettingsField.YouTubeApiKey, new AppSettingsData { YouTubeApiKey = "AIza-plain" }));   // never put back on the line
        Assert.Equal("must be 1 to 20 results", SettingsMenu.YouTubeSearchMaxResultsRangeError);
        Assert.Equal("YouTube API key saved unencrypted: no DPAPI.", SettingsMenu.YouTubeApiKeyPlainWarning("no DPAPI"));
    }

    /// <summary>
    /// YouTube open links in (2026-10-16, the user's ask and words): first under Playing, a picker over the four words on youtube-window, the
    /// default; a hand-edited value reads as the default it acts as, not as typed.
    /// </summary>
    [Fact]
    public async Task OnThePane_YouTubeOpenLinksIn_IsFirstUnderPlaying_APicker()
    {
        var (menu, pane, _) = PaneMenu();
        OpenYouTubeRow(7);                         // the picker, on youtube-window
        Push(Keys.Down, Keys.Down, Keys.Down, Keys.Enter);   // webui-window
        Push(Keys.Escape, Keys.Escape);   // one more for the group menu

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(NeonSidekick.YouTube.YouTubeOpenLinksIn.WebUiWindow, _settings.Current.YouTubeOpenLinksIn);
        Assert.Contains("▸ " + Spectre.Console.Markup.Remove(SettingsMenu.YouTubeOpenLinksInLabel(NeonSidekick.YouTube.YouTubeOpenLinksIn.YouTubeWindow)) + "\n", Output);
        Assert.Contains("  · YouTube open links in: webui-window\n", Output);
        Assert.False(SettingsMenu.IsToggle(SettingsField.YouTubeOpenLinksIn));
        Assert.Equal("youtube-window", SettingsMenu.FieldValue(SettingsField.YouTubeOpenLinksIn, new AppSettingsData { YouTubeOpenLinksIn = "phone" }, _settings.ProfileDirectory));
        Assert.Equal("browser-window", SettingsMenu.FieldValue(SettingsField.YouTubeOpenLinksIn, new AppSettingsData { YouTubeOpenLinksIn = " Browser-Window " }, _settings.ProfileDirectory));
        Assert.Equal("youtube-window  ", Spectre.Console.Markup.Remove(SettingsMenu.YouTubeOpenLinksInLabel("youtube-window"))[..16]);   // padded to sixteen, as Web open links in's
        pane.Dispose();
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
        Push(Keys.Escape, Keys.Escape);   // one more for the group menu (2026-10-09)

        await menu.ShowAsync(CancellationToken.None);

        var saved = _settings.Current;
        Assert.True(saved.YouTubeTools);
        Assert.Equal("AIza-youtube-secret", SettingsSecrets.Reveal(saved.YouTubeApiKey));
        Assert.DoesNotContain("AIza-youtube-secret", Output);
        if (OperatingSystem.IsWindows())
        {
            Assert.StartsWith(Sql.WindowsCredentials.ProtectedPrefix, saved.YouTubeApiKey);
            Assert.Contains("  · YouTube API key: " + SettingsMenu.ApiKeyEncryptedLabel, Output);
        }

        Assert.Contains("  ✗ YouTube search max results " + SettingsMenu.YouTubeSearchMaxResultsRangeError + "; keeping 8.", Output);
        Assert.Equal(12, saved.YouTubeSearchMaxResults);
        pane.Dispose();
    }

    /// <summary>
    /// The filters' saved defaults (2026-10-08, the user's ask): the order and safe search on pickers with the saved one under the cursor;
    /// the region and language typed, refused when they are no code, kept as YouTube reads them.
    /// </summary>
    [Fact]
    public async Task OnThePane_TheYouTubeTab_TheFilterDefaults_PickAndType()
    {
        var (menu, pane, _) = PaneMenu();
        OpenYouTubeRow(3);                                    // YouTube search order: relevance under the cursor (under the search max since 2026-10-10, autoplay and voice after the filters)
        Push(Keys.Down, Keys.Enter);                          // date
        Push(Keys.Down, Keys.Enter, Keys.Down, Keys.Enter);   // YouTube safe search: moderate under the cursor, then strict
        Push(Keys.Down, Keys.Enter);                          // YouTube region
        _console.Input.PushText("USA");
        Push(Keys.Enter);                                     // refused
        Push(Keys.Enter);
        _console.Input.PushText("us");
        Push(Keys.Enter);
        Push(Keys.Down, Keys.Enter);                          // YouTube language
        _console.Input.PushText("english");
        Push(Keys.Enter);                                     // refused
        Push(Keys.Enter);
        _console.Input.PushText("zh-Hans");
        Push(Keys.Enter);
        Push(Keys.Escape, Keys.Escape);   // one more for the group menu (2026-10-09)

        await menu.ShowAsync(CancellationToken.None);

        var saved = _settings.Current;
        Assert.Equal(("date", "strict", "US", "zh-Hans"), (saved.YouTubeSearchOrder, saved.YouTubeSafeSearch, saved.YouTubeRegion, saved.YouTubeLanguage));
        Assert.Contains("  ✗ YouTube region " + SettingsMenu.YouTubeRegionError + "; keeping " + SettingsMenu.NoYouTubeRegionLabel + ".", Output);
        Assert.Contains("  ✗ YouTube language " + SettingsMenu.YouTubeLanguageError + "; keeping " + SettingsMenu.NoYouTubeLanguageLabel + ".", Output);
        Assert.Contains("newest first", Output);                                    // the order picker's notes
        Assert.Contains(SettingsMenu.YouTubeSafeSearchNote("strict"), Output);
        pane.Dispose();
    }

    /// <summary>
    /// A Mac's Docker engine socket row (code review of 9c145d1, 2026-10-11): a typed value naming no socket (a remote engine, a
    /// relative path) is refused, never saved blank as "find it" unsaid; a <c>unix://</c> path saves as its path.
    /// </summary>
    [MacFact]
    public async Task OnAMac_TheDockerSocketRow_RefusesWhatNamesNoSocket()
    {
        var (menu, pane, _) = PaneMenu();
        Push([.. ToTab(ToolsText.DockerTabTitle), Keys.Down, Keys.Down, Keys.Enter, Keys.Ctrl(ConsoleKey.A), Keys.Backspace]);
        _console.Input.PushText("tcp://127.0.0.1:2375");
        Push(Keys.Enter);                                     // refused
        Push(Keys.Enter, Keys.Ctrl(ConsoleKey.A), Keys.Backspace);
        _console.Input.PushText("unix://~/.orbstack/run/docker.sock");
        Push(Keys.Enter);
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains("Docker engine socket " + SettingsMenu.DockerEngineSocketError + "; keeping auto: ", Output);
        Assert.Equal("~/.orbstack/run/docker.sock", _settings.Current.DockerEnginePipe);
        pane.Dispose();
    }

    [Fact]
    public void TheFilterDefaults_AreCopied_AndReadAsTheSearchsOwn()
    {
        var copy = AppSettings.Copy(new AppSettingsData { YouTubeSearchOrder = "date", YouTubeSafeSearch = "none", YouTubeRegion = "DE", YouTubeLanguage = "de" });
        Assert.Equal(("date", "none", "DE", "de"), (copy.YouTubeSearchOrder, copy.YouTubeSafeSearch, copy.YouTubeRegion, copy.YouTubeLanguage));

        var filters = YouTube.YouTubeSearchFilters.Defaults(copy);
        Assert.Equal(("date", "none", "DE", "de", YouTube.YouTubeKind.Video), (filters.Order, filters.SafeSearch, filters.Region, filters.Language, filters.Kind));
        Assert.Equal(YouTube.YouTubeSearchFilters.None, YouTube.YouTubeSearchFilters.Defaults(new AppSettingsData()));
        Assert.Equal(YouTube.YouTubeSearchFilters.None, YouTube.YouTubeSearchFilters.Defaults(new AppSettingsData { YouTubeRegion = "Texas", YouTubeLanguage = "!!", YouTubeSafeSearch = "max" }));
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
