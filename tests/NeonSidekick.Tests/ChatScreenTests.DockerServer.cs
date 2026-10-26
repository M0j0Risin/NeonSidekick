using NeonSidekick.App;
using NeonSidekick.Docker;
using NeonSidekick.Llm;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary><c>/server</c> and the Docker servers (2026-10-02): a chosen container by name or from its rows, the refusals, and the exit's stop.</summary>
public partial class ChatScreenTests
{
    /// <summary>The screen over a session with a <see cref="FakeDockerServers"/>, two containers chosen, the switch on or off.</summary>
    private FakeDockerServers UseDockerServers(bool on = true)
    {
        var docker = new FakeDockerServers
        {
            List = new DockerServerList(
                [
                    new DockerContainer("id-a", "sglang_a", "lmsysorg/sglang:latest", "sha", "exited", "Exited (0)", 0, [], new Dictionary<string, string>(), [], []),
                    new DockerContainer("id-b", "vllm_b", "vllm/vllm-openai:latest", "sha", "exited", "Exited (0)", 0, [], new Dictionary<string, string>(), [], []),
                ], [], null),
        };
        _settings.Update(d => { d.TtsOutput = false; d.DockerServers = on; d.DockerServerContainers = ["sglang_a", "vllm_b"]; });
        _session.Dispose();
        _session = new LlmSession(new LlmEndpointProbe(new HttpClient(_http), TimeSpan.FromMilliseconds(500)), new ContextLengthProbe(new HttpClient(_http), TimeSpan.FromMilliseconds(500)),
            (endpoint, _) => { _endpoints.Add(endpoint); return _chat; }, _time, dockerServers: docker);
        return docker;
    }

    [Fact]
    public async Task Server_DockerByName_SavesTheSentinel_SwitchesToIt_AndConnectsWhereItAnswered()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var docker = UseDockerServers();
        PushLine("/server docker:vllm_b");
        _console.Input.PushKey(Keys.Escape);   // keep the reasoning
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Equal("http://docker.localhost/vllm_b/v1", _settings.Current.LlmUrl);
        Assert.Equal(["list", "switch vllm_b"], docker.Calls);   // the launch's server is on this computer: listed, nothing running to stop
        Assert.Equal(FakeDockerServers.LiveUrl, _endpoints[^1].LiveUrl);
        Assert.Contains("LLM: http://docker.localhost/vllm_b/v1 model=sglang-model (docker vllm_b :30000)", output);
        Assert.Equal("vllm_b", _session.DockerInUse);
    }

    [Fact]
    public async Task Server_Docker_SavesTheContainersOneModel_AsTheLlmModel()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        UseDockerServers();
        PushLine("/server docker:vllm_b");
        _console.Input.PushKey(Keys.Escape);   // keep the reasoning
        PushLine("/exit");

        await RunAsync();

        Assert.Equal("sglang-model", _settings.Current.LlmModel);   // the fake's one model, where (first listed) stood
    }

    [Fact]
    public async Task Server_Docker_EscUnderTheLoad_CancelsIt()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // 2026-10-24, the user's report: "loading the model in <container> · Esc to stop", and Esc did nothing. The line and the
        // picker's Esc go through the idle read, never ahead of the run: the load's watch would take a typed /exit, which cancels too.
        var docker = UseDockerServers();
        LoadOnThePane();
        var input = WhenIdle(
            i => { PushLine(i, "/server docker:vllm_b"); i.Push(Keys.Escape); },   // the Esc keeps the reasoning
            i => PushLine(i, "/exit"));
        docker.Gate = async ct =>
        {
            input.Push(Keys.Escape);
            await Task.Delay(TimeSpan.FromSeconds(10), ct);   // the cancel ends it; without one the switch answers and the asserts fail, never a hang
        };

        string output = await RunAsync(input);

        Assert.Contains(ChatScreen.ConnectCancelledNotice(NoticeGlyphs.Llm), output);
        Assert.Equal(["list", "switch vllm_b"], docker.Calls);   // the launch's list: nothing running to stop
        Assert.Null(_session.Assistant);
    }

    [Fact]
    public async Task Server_Docker_TwoModelsListed_SavesNone()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var docker = UseDockerServers();
        docker.Answer = _ => FakeDockerServers.Ready("a", "b");
        PushLine("/server docker:vllm_b");
        _console.Input.PushKey(Keys.Escape);
        PushLine("/exit");

        await RunAsync();

        Assert.Equal("", _settings.Current.LlmModel);   // nothing to name: the URL's change cleared it, and it stays (first listed)
    }

    [Fact]
    public async Task Server_Docker_ReplacesAStaleModel_WithTheOneListed()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // The same container again, the URL unchanged so the save kept the stale id: the connect puts the listed one in.
        var docker = UseDockerServers();
        _settings.Update(d => { d.LlmUrl = DockerEndpoint.BaseUrl("sglang_a").ToString(); d.LlmModel = "stale"; });
        docker.Answer = _ => FakeDockerServers.Ready("only");
        PushLine("/server docker:sglang_a");
        _console.Input.PushKey(Keys.Escape);
        PushLine("/exit");

        await RunAsync();

        Assert.Equal("only", _settings.Current.LlmModel);
    }

    [Fact]
    public async Task Server_Docker_UnderAModelOverride_SavesNothing()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        UseDockerServers();
        _overriddenBy = f => f == SettingsField.LlmModel ? SidekickOptions.ModelFlag : null;
        PushLine("/server docker:vllm_b");
        _console.Input.PushKey(Keys.Escape);
        PushLine("/exit");

        await RunAsync();

        Assert.Equal("", _settings.Current.LlmModel);   // --model names the model; the container's is never saved over it
    }

    [Fact]
    public async Task Server_Docker_ListsTheChosenRowsAlone_APickSwitches_AndTheExitStopsItWithTheSettingOn()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var docker = UseDockerServers();
        _settings.Update(d => d.DockerServerStopOnExit = true);
        PushLine("/server docker");
        _console.Input.PushKey(Keys.Enter);    // the first row: sglang_a
        _console.Input.PushKey(Keys.Escape);   // keep the reasoning
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains("exited · lmsysorg/sglang:latest", output);
        Assert.Contains("exited · vllm/vllm-openai:latest", output);
        Assert.Equal(["list", "list", "switch sglang_a", "stop-all"], docker.Calls);   // the launch's list (nothing running to stop), then /server docker's
        Assert.Equal("http://docker.localhost/sglang_a/v1", _settings.Current.LlmUrl);
        Assert.Null(_session.DockerInUse);   // stopped at the exit
    }

    [Fact]
    public async Task Server_Docker_Refuses_AnUnchosenName_AndTheSwitchedOffRows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string url = _settings.Current.LlmUrl;
        var docker = UseDockerServers();
        PushLine("/server docker:mysql_dev");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(DockerServerText.NotChosen("mysql_dev"), output);
        Assert.Equal(url, _settings.Current.LlmUrl);
        Assert.Equal(["list"], docker.Calls);   // the launch's alone: nothing running to stop
    }

    [Fact]
    public async Task Launch_StopsALeftoverContainer_AndSaysSo()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // 2026-10-26, the user's report: a chosen container an earlier run left running stayed beside the model this run connected to.
        // The launch's server is on this computer (127.0.0.1:1234), so the container stops first, and the transcript says so.
        var docker = UseDockerServers();
        docker.List = new DockerServerList(
            [
                new DockerContainer("id-a", "sglang_a", "lmsysorg/sglang:latest", "sha", "running", "Up", 0, [new DockerPort("0.0.0.0", 30000, 30000, "tcp")], new Dictionary<string, string>(), [], []),
                new DockerContainer("id-b", "vllm_b", "vllm/vllm-openai:latest", "sha", "exited", "Exited (0)", 0, [], new Dictionary<string, string>(), [], []),
            ], [], null);
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Equal(["list", "stop-all"], docker.Calls);
        string notice = DockerServerText.LeftForThisComputer(["sglang_a"], embedded: false);
        Assert.Contains(notice, output);
        Assert.True(output.IndexOf(notice, StringComparison.Ordinal) < output.IndexOf("LLM: http://127.0.0.1:1234", StringComparison.Ordinal));
        Assert.Null(_session.TakeLeftoverStopped());   // said once
    }

    [Fact]
    public async Task Server_Docker_SwitchedOff_SaysHowToTurnItOn()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var docker = UseDockerServers(on: false);
        PushLine("/server docker");
        PushLine("/server docker:sglang_a");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Equal(2, output.Split(DockerServerText.SwitchedOffError).Length - 1);
        Assert.Empty(docker.Calls);
    }
}
