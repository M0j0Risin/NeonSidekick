using NeonSidekick.App;
using NeonSidekick.Docker;
using NeonSidekick.Settings;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>The Docker tab of <c>/settings</c> (2026-10-02): its seven rows, their values and ranges, the containers' checklist and its match words (2026-10-10).</summary>
public partial class SettingsMenuTests
{
    private static DockerContainer Container(string name, string state, int port = 0, string? image = null, string command = "", IReadOnlyDictionary<string, string>? labels = null) =>
        new("id-" + name, name, image ?? name + "/image:latest", "sha", state, state, 0, port == 0 ? [] : [new DockerPort("0.0.0.0", port, port, "tcp")], labels ?? new Dictionary<string, string>(), [], [], command);

    [Fact]
    public void TheDockerTab_IsAfterEmbedded_WithItsSevenRows_OnlyTheSwitchReconnecting()
    {
        int tab = (int)SettingsTab.Docker;
        Assert.Equal("Docker", SettingsMenu.TabTitles[tab]);
        Assert.Equal("Embedded", SettingsMenu.TabTitles[tab - 1]);
        Assert.Equal([SettingsField.DockerServers, SettingsField.DockerServerContainers, SettingsField.DockerServerMatchWords, SettingsField.DockerServerStopTimeoutSeconds, SettingsField.DockerServerPostStopDelaySeconds, SettingsField.DockerServerReadyTimeoutSeconds, SettingsField.DockerServerStopOnExit], SettingsMenu.TabFields[tab]);
        Assert.Equal(["Docker servers enabled", "Docker server containers", "Docker server match words", "Docker server stop timeout (s)", "Docker server post-stop delay (s)", "Docker server ready timeout (s)", "Docker server stop on exit"], SettingsMenu.TabFields[tab].Select(SettingsMenu.FieldName));
        Assert.True(SettingsMenu.IsLlmField(SettingsField.DockerServers));
        Assert.All(SettingsMenu.TabFields[tab].Skip(1), f => Assert.False(SettingsMenu.IsLlmField(f), f.ToString()));   // read at the next switch
        Assert.True(SettingsMenu.IsToggle(SettingsField.DockerServers) && SettingsMenu.IsToggle(SettingsField.DockerServerStopOnExit));
        Assert.False(SettingsMenu.IsToggle(SettingsField.DockerServerContainers) || SettingsMenu.IsToggle(SettingsField.DockerServerMatchWords));

        var data = new AppSettingsData();
        Assert.Equal(["off", "none", "12 words", "30", "2", "900", "off"], SettingsMenu.TabFields[tab].Select(f => SettingsMenu.FieldValue(f, data, "C:\\p")));
        Assert.Equal("none (every container)", SettingsMenu.DockerServerWordsValue([]));
        Assert.Equal("1 word", SettingsMenu.DockerServerWordsValue(["vllm", " VLLM ", ""]));
        Assert.Equal("2: sglang_a, vllm_b", SettingsMenu.DockerServerContainersValue([" sglang_a", "vllm_b", "sglang_a", ""]));
        Assert.EndsWith("…", SettingsMenu.DockerServerContainersValue(Enumerable.Range(0, 20).Select(i => "container_" + i).ToList()));
        var copy = AppSettings.Copy(new AppSettingsData { DockerServers = true, DockerServerContainers = ["a"], DockerServerMatchWords = ["tgi"], DockerServerStopTimeoutSeconds = 5, DockerServerPostStopDelaySeconds = 0, DockerServerReadyTimeoutSeconds = 60, DockerServerStopOnExit = true });
        Assert.Equal((true, 5, 0, 60, true), (copy.DockerServers, copy.DockerServerStopTimeoutSeconds, copy.DockerServerPostStopDelaySeconds, copy.DockerServerReadyTimeoutSeconds, copy.DockerServerStopOnExit));
        Assert.Equal(["a"], copy.DockerServerContainers);
        Assert.Equal(["tgi"], copy.DockerServerMatchWords);
        Assert.Equal("must be 0 to 120 seconds", SettingsMenu.DockerServerStopTimeoutRangeError);
        Assert.Equal("must be 0 to 60 seconds", SettingsMenu.DockerServerPostStopDelayRangeError);
        Assert.Equal("must be 30 to 3600 seconds", SettingsMenu.DockerServerReadyTimeoutRangeError);
    }

    [Fact]
    public async Task OnThePane_TheDockerTab_FlipsItsToggles_AndRefusesAWaitOutOfRange()
    {
        var (menu, pane) = PaneMenu();
        GoTo(SettingsTab.Docker);
        Push(Keys.Enter, Keys.Up, Keys.Enter);                       // Docker servers enabled: the page, on picked
        Down(3);
        Push(Keys.Enter, Keys.Ctrl(ConsoleKey.A), Keys.Backspace);   // the stop timeout
        _console.Input.PushText("500");
        Push(Keys.Enter);                                            // refused
        Push(Keys.Enter, Keys.Ctrl(ConsoleKey.A), Keys.Backspace);
        _console.Input.PushText("45");
        Push(Keys.Enter);
        Down(2);
        Push(Keys.Enter, Keys.Ctrl(ConsoleKey.A), Keys.Backspace);   // the ready timeout
        _console.Input.PushText("10");
        Push(Keys.Enter);                                            // refused: under 30, back on the list
        Down(1);
        Push(Keys.Enter, Keys.Up, Keys.Enter);                       // stop on exit: on
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.True(_settings.Current.DockerServers);
        Assert.Equal(45, _settings.Current.DockerServerStopTimeoutSeconds);
        Assert.Equal(900, _settings.Current.DockerServerReadyTimeoutSeconds);
        Assert.True(_settings.Current.DockerServerStopOnExit);
        Assert.Contains("Docker server stop timeout (s) " + SettingsMenu.DockerServerStopTimeoutRangeError + "; keeping 30.", Output);
        Assert.Contains("Docker server ready timeout (s) " + SettingsMenu.DockerServerReadyTimeoutRangeError + "; keeping 900.", Output);
        pane.Dispose();
    }

    /// <summary>The checklist ticks by name; a saved name the engine no longer lists is dropped as it opens, the status line naming it (2026-10-04, the user's call: it was kept until then and nothing could untick it).</summary>
    [Fact]
    public async Task OnThePane_TheContainersChecklist_TicksByName_DroppingASavedNameTheEngineNoLongerLists()
    {
        _settings.Update(d => { d.DockerServerContainers = ["rebuilding"]; d.DockerServerMatchWords = []; });   // every container (2026-10-10)
        var (menu, pane) = PaneMenu();
        menu.DockerContainers = _ => Task.FromResult<(IReadOnlyList<DockerContainer>?, string?)>(([Container("vllm_b", "exited"), Container("sglang_a", "running", 30000), Container("mysql_dev", "running", 3306)], null));
        GoTo(SettingsTab.Docker);
        Down(1);
        Push(Keys.Enter);                       // the checklist: running first, by name (mysql_dev, sglang_a, vllm_b)
        Push(Keys.Down, Keys.Enter);            // sglang_a
        Push(Keys.Down, Keys.Char(' '));        // vllm_b, by Space
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(["sglang_a", "vllm_b"], _settings.Current.DockerServerContainers);
        Assert.Contains(SettingsMenu.StaleDroppedNotice(SettingsField.DockerServerContainers, ["rebuilding"]), Output);
        Assert.Contains("[x] sglang_a", Output);
        Assert.Contains("running · sglang_a/image:latest · :30000", Output);
        Assert.Contains("exited · vllm_b/image:latest", Output);
        Assert.Equal("[[ ]] mysql_dev  " + NeonSidekick.UI.Theme.DimMarkup("running · mysql_dev/image:latest · :3306"), SettingsMenu.DockerServerRow(Container("mysql_dev", "running", 3306), false, 11));
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_TheContainersChecklist_SaysSo_WithNoEngine_AnEngineError_OrNoContainers()
    {
        var (menu, pane) = PaneMenu();
        GoTo(SettingsTab.Docker);
        Down(1);
        Push(Keys.Enter);   // no door at all
        Push(Keys.Escape);
        await menu.ShowAsync(CancellationToken.None);
        Assert.Contains(SettingsMenu.NoDockerEngineError, Output);

        menu.DockerContainers = _ => Task.FromResult<(IReadOnlyList<DockerContainer>?, string?)>((null, "Docker Desktop is not running."));
        GoTo(SettingsTab.Docker);
        Down(1);
        Push(Keys.Enter, Keys.Escape);
        await menu.ShowAsync(CancellationToken.None);
        Assert.Contains("Docker Desktop is not running.", Output);

        menu.DockerContainers = _ => Task.FromResult<(IReadOnlyList<DockerContainer>?, string?)>(([], null));
        GoTo(SettingsTab.Docker);
        Down(1);
        Push(Keys.Enter, Keys.Escape);
        await menu.ShowAsync(CancellationToken.None);
        Assert.Contains(SettingsMenu.NoDockerContainersNotice, Output);
        Assert.Null(_settings.Current.DockerServerContainers);
        pane.Dispose();
    }

    /// <summary>
    /// The checklist lists the LLM servers only (2026-10-10, the user's ask): by image, by command, by the label, and a ticked
    /// container whatever it is, so it can be unticked; a database is left out, and select all ticks the rows shown, never it.
    /// </summary>
    [Fact]
    public async Task OnThePane_TheContainersChecklist_ListsTheLlmServers_AndTheTicked_SelectAllTicksOnlyThose()
    {
        _settings.Update(d => d.DockerServerContainers = ["postgres_dev"]);
        var (menu, pane) = PaneMenu();
        menu.DockerContainers = _ => Task.FromResult<(IReadOnlyList<DockerContainer>?, string?)>((
        [
            Container("mysql_dev", "running", 3306, image: "mysql:8.4", command: "docker-entrypoint.sh mysqld"),
            Container("postgres_dev", "running", 5432, image: "postgres:17"),
            Container("sglang_a", "running", 30000, image: "lmsysorg/sglang:latest"),
            Container("qwen_custom", "exited", image: "my/server:1", command: "vllm serve Qwen/Qwen3-8B"),
            Container("tagged", "exited", image: "acme/runner", labels: new Dictionary<string, string> { [DockerServerWords.LlmLabel] = "true" }),
        ], null));
        GoTo(SettingsTab.Docker);
        Down(1);
        Push(Keys.Enter);                                  // the checklist: postgres_dev (ticked), sglang_a, qwen_custom, tagged
        Push(Keys.Enter);                                  // postgres_dev unticked: still listed
        Push(Keys.Char(SettingsMenu.SelectAllKey));        // every row shown
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(["postgres_dev", "sglang_a", "qwen_custom", "tagged"], _settings.Current.DockerServerContainers);
        Assert.DoesNotContain("mysql_dev", Output);
        Assert.Contains("[ ] postgres_dev", Output);
        Assert.Contains("[x] tagged", Output);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_TheContainersChecklist_SaysSo_WhenNoContainerIsAnLlmServer()
    {
        var (menu, pane) = PaneMenu();
        menu.DockerContainers = _ => Task.FromResult<(IReadOnlyList<DockerContainer>?, string?)>(([Container("mysql_dev", "running", 3306, image: "mysql:8.4"), Container("postgres_dev", "running", 5432, image: "postgres:17")], null));
        GoTo(SettingsTab.Docker);
        Down(1);
        Push(Keys.Enter, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains(SettingsMenu.NoLlmContainersNotice, Output);
        Assert.Equal("(no container matches Docker server match words; empty the list to see every container)", SettingsMenu.NoLlmContainersNotice);
        Assert.Null(_settings.Current.DockerServerContainers);
        pane.Dispose();
    }

    /// <summary>
    /// The match words (2026-10-10): the top row adds a typed word (trimmed, case kept), a duplicate ignoring case is refused,
    /// Enter on a word removes it after a yes, and R restores the known engines after a yes, then says when they already are.
    /// </summary>
    [Fact]
    public async Task OnThePane_TheMatchWords_AddRefuseRemove_AndRestoreTheDefaults()
    {
        _settings.Update(d => d.DockerServerMatchWords = ["vllm"]);
        var (menu, pane) = PaneMenu();
        GoTo(SettingsTab.Docker);
        Down(2);
        Push(Keys.Enter);                                  // the list, on its add row
        Push(Keys.Enter);
        Push([.. " tgi ".Select(Keys.Char), Keys.Enter]);  // added as "tgi"
        Push(Keys.Enter);
        Push([.. "VLLM".Select(Keys.Char), Keys.Enter]);   // already there
        Push(Keys.Down, Keys.Enter, Keys.Down, Keys.Enter);   // tgi removed (Yes)
        Push(Keys.Char('r'), Keys.Char('y'), Keys.Enter);  // asked, Yes: the defaults
        Push(Keys.Char('r'));                              // already the defaults: nothing asked
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        var defaults = DockerServerWords.Defaults;
        Assert.Equal(defaults, _settings.Current.DockerServerMatchWords);
        Assert.Contains("\n▸ " + SettingsMenu.AddDockerWordRow + "\n", Output);
        Assert.Contains("  · " + SettingsMenu.DockerWordAddedNotice("tgi") + "\n▸ " + SettingsMenu.AddDockerWordRow + "\n  tgi\n  vllm\n", Output);
        Assert.Contains("  · Docker server match words: VLLM is already in the list\n", Output);
        Assert.Contains("  · Docker server match words: tgi removed\n", Output);
        Assert.Contains(SettingsMenu.RestoreDockerWordsQuestion(1, defaults.Count), Output);
        int restored = Output.IndexOf("  · " + SettingsMenu.DockerWordsRestoredNotice(defaults.Count) + "\n", StringComparison.Ordinal);
        int already = Output.IndexOf("  · " + SettingsMenu.DockerWordsAlreadyDefaultsNotice + "\n", StringComparison.Ordinal);
        Assert.True(restored >= 0 && restored < already, Output);
        pane.Dispose();
    }

    [Fact]
    public void TheMatchWords_Wording()
    {
        Assert.Equal("Restore the 12 default match words?", SettingsMenu.RestoreDockerWordsQuestion(0, 12));
        Assert.Equal("Replace the list (3 words) with the 12 default words? Any you added are removed.", SettingsMenu.RestoreDockerWordsQuestion(3, 12));
        Assert.Equal("Docker server match words: 12 defaults restored", SettingsMenu.DockerWordsRestoredNotice(12));
        Assert.Equal("Docker server match words: already the defaults", SettingsMenu.DockerWordsAlreadyDefaultsNotice);
        Assert.Equal("Remove \"vllm\" from the match words? Containers only it matched are no longer listed.", SettingsMenu.RemoveDockerWordQuestion("vllm"));
    }
}
