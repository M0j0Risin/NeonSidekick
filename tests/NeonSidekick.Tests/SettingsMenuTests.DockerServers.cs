using NeonSidekick.App;
using NeonSidekick.Docker;
using NeonSidekick.Settings;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>The Docker tab of <c>/settings</c> (2026-10-02): its six rows, their values and ranges, and the containers' checklist.</summary>
public partial class SettingsMenuTests
{
    private static DockerContainer Container(string name, string state, int port = 0) =>
        new("id-" + name, name, name + "/image:latest", "sha", state, state, 0, port == 0 ? [] : [new DockerPort("0.0.0.0", port, port, "tcp")], new Dictionary<string, string>(), [], []);

    [Fact]
    public void TheDockerTab_IsAfterEmbedded_WithItsSixRows_OnlyTheSwitchReconnecting()
    {
        int tab = (int)SettingsTab.Docker;
        Assert.Equal("Docker", SettingsMenu.TabTitles[tab]);
        Assert.Equal("Embedded", SettingsMenu.TabTitles[tab - 1]);
        Assert.Equal([SettingsField.DockerServers, SettingsField.DockerServerContainers, SettingsField.DockerServerStopTimeoutSeconds, SettingsField.DockerServerPostStopDelaySeconds, SettingsField.DockerServerReadyTimeoutSeconds, SettingsField.DockerServerStopOnExit], SettingsMenu.TabFields[tab]);
        Assert.Equal(["Docker servers enabled", "Docker server containers", "Docker server stop timeout (s)", "Docker server post-stop delay (s)", "Docker server ready timeout (s)", "Docker server stop on exit"], SettingsMenu.TabFields[tab].Select(SettingsMenu.FieldName));
        Assert.True(SettingsMenu.IsLlmField(SettingsField.DockerServers));
        Assert.All(SettingsMenu.TabFields[tab].Skip(1), f => Assert.False(SettingsMenu.IsLlmField(f), f.ToString()));   // read at the next switch
        Assert.True(SettingsMenu.IsToggle(SettingsField.DockerServers) && SettingsMenu.IsToggle(SettingsField.DockerServerStopOnExit));
        Assert.False(SettingsMenu.IsToggle(SettingsField.DockerServerContainers));

        var data = new AppSettingsData();
        Assert.Equal(["off", "none", "30", "2", "900", "off"], SettingsMenu.TabFields[tab].Select(f => SettingsMenu.FieldValue(f, data, "C:\\p")));
        Assert.Equal("2: sglang_a, vllm_b", SettingsMenu.DockerServerContainersValue([" sglang_a", "vllm_b", "sglang_a", ""]));
        Assert.EndsWith("…", SettingsMenu.DockerServerContainersValue(Enumerable.Range(0, 20).Select(i => "container_" + i).ToList()));
        var copy = AppSettings.Copy(new AppSettingsData { DockerServers = true, DockerServerContainers = ["a"], DockerServerStopTimeoutSeconds = 5, DockerServerPostStopDelaySeconds = 0, DockerServerReadyTimeoutSeconds = 60, DockerServerStopOnExit = true });
        Assert.Equal((true, 5, 0, 60, true), (copy.DockerServers, copy.DockerServerStopTimeoutSeconds, copy.DockerServerPostStopDelaySeconds, copy.DockerServerReadyTimeoutSeconds, copy.DockerServerStopOnExit));
        Assert.Equal(["a"], copy.DockerServerContainers);
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
        Down(2);
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
        _settings.Update(d => d.DockerServerContainers = ["rebuilding"]);
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
}
