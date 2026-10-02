using System.Net;
using NeonSidekick.App;
using NeonSidekick.Docker;
using NeonSidekick.Llm;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// The session's Docker servers branch (2026-10-02) over <see cref="FakeDockerServers"/>: a chosen container's URL switches to it
/// and connects to the port it answered on; any other server first stops the chosen ones; a switched-off URL is blank; the
/// <c>/server</c> rows and the dedupe; <c>/model</c>; a bot's link; the exit's stop.
/// </summary>
public sealed class LlmSessionDockerTests
{
    private readonly StubHttpMessageHandler _http = new();
    private readonly List<LlmEndpoint> _endpoints = [];
    private readonly FakeDockerServers _docker = new();

    /// <summary>No spinner: typed, since a bare null would fit the endpoint overload too.</summary>
    private static readonly Action<string>? NoPhase = null;

    private LlmSession Session() => new(
        new LlmEndpointProbe(new HttpClient(_http), TimeSpan.FromMilliseconds(500)),
        new ContextLengthProbe(new HttpClient(_http), TimeSpan.FromMilliseconds(500)),
        (endpoint, _) =>
        {
            _endpoints.Add(endpoint);
            return new FakeChatClient();
        },
        dockerServers: _docker);

    private static AppSettingsData Docker(string name, string model = "", bool on = true) => new()
    {
        DockerServers = on,
        DockerServerContainers = ["sglang_a", "vllm_b"],
        LlmUrl = "docker:" + name,
        LlmModel = model,
        LlmScanMode = "disabled",
    };

    [Fact]
    public async Task AChosenContainer_IsSwitchedTo_AndConnectedOnTheLiveUrl_TheSentinelShown()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var session = Session();
        var labels = new List<string>();

        Assert.True(await session.ConnectAsync(Docker("sglang_a"), labels.Add, CancellationToken.None));

        var endpoint = session.Endpoint!;
        Assert.Equal(DockerEndpoint.BaseUrl("sglang_a"), endpoint.BaseUrl);
        Assert.Equal(FakeDockerServers.LiveUrl, endpoint.LiveUrl);
        Assert.Equal("sglang-model", endpoint.ModelId);   // no saved model: the container's first
        Assert.Equal("docker sglang_a :30000", endpoint.Source);
        Assert.Equal("LLM: http://docker.localhost/sglang_a/v1 model=sglang-model (docker sglang_a :30000)", LlmSession.ConnectedLine(endpoint));
        Assert.Equal(32_768, session.ContextLength!.Value.Tokens);   // the list's window, no second request
        Assert.Equal(["switch sglang_a"], _docker.Calls);
        Assert.Equal(["starting sglang_a"], labels);
        Assert.Same(endpoint, _endpoints.Single());
        Assert.Equal("sglang_a", session.DockerInUse);
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task TheSavedModel_IsKept_WhenTheContainerListsIt_ElseItsFirst()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        _docker.Answer = _ => FakeDockerServers.Ready("a", "b");
        using var session = Session();
        await session.ConnectAsync(Docker("sglang_a", model: "b"), NoPhase, CancellationToken.None);
        Assert.Equal("b", session.Endpoint!.ModelId);
        await session.ConnectAsync(Docker("sglang_a", model: "gone"), NoPhase, CancellationToken.None);
        Assert.Equal("a", session.Endpoint!.ModelId);
    }

    [Fact]
    public async Task AFailedSwitch_BuildsNoClient_AndSaysWhy()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        _docker.Answer = name => DockerSwitch.Failed(DockerServerText.NotReady(name, 900), []);
        using var session = Session();
        var errors = new List<string>();
        Action<NeonSidekick.Diagnostics.DiagnosticEvent> capture = e => { if (e.Level == NeonSidekick.Diagnostics.DiagnosticLevel.Error) lock (errors) { errors.Add(e.Message); } };
        NeonSidekick.Diagnostics.DiagnosticLog.Emitted += capture;
        try
        {
            Assert.False(await session.ConnectAsync(Docker("vllm_b"), NoPhase, CancellationToken.None));
        }
        finally
        {
            NeonSidekick.Diagnostics.DiagnosticLog.Emitted -= capture;
        }

        Assert.Null(session.Assistant);
        Assert.Equal(DockerServerText.NotRunningSource, session.Endpoint!.Source);
        Assert.Contains(DockerServerText.NotReady("vllm_b", 900), errors);
        Assert.Empty(_endpoints);
    }

    [Fact]
    public async Task AnotherServer_StopsTheChosenOnesFirst_AnotherContainer_SwitchesOnly()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        _http.Map("http://127.0.0.1:1234/v1/models", HttpStatusCode.OK, StubHttpMessageHandler.ModelsJson("lm"));
        using var session = Session();
        await session.ConnectAsync(Docker("sglang_a"), NoPhase, CancellationToken.None);
        await session.ConnectAsync(Docker("vllm_b"), NoPhase, CancellationToken.None);
        Assert.Equal(["switch sglang_a", "switch vllm_b"], _docker.Calls);   // the switch itself stops the other

        var lm = Docker("vllm_b");
        lm.LlmUrl = "http://127.0.0.1:1234/v1";
        Assert.True(await session.ConnectAsync(lm, NoPhase, CancellationToken.None));
        Assert.Equal(["switch sglang_a", "switch vllm_b", "stop-all"], _docker.Calls);
        Assert.Null(session.DockerInUse);

        // A scan row picked (the other connect path) leaves too; nothing to stop once left.
        await session.ConnectAsync(Docker("sglang_a"), NoPhase, CancellationToken.None);
        await session.ConnectAsync(lm, LlmEndpointProbe.Endpoint(LlmServer.From(new Uri("http://127.0.0.1:1234/v1"), new ProbeResult(true, ["lm"], "1")), null, null, false), CancellationToken.None);
        Assert.Equal("stop-all", _docker.Calls[^1]);
        await session.ConnectAsync(lm, NoPhase, CancellationToken.None);
        Assert.Equal(5, _docker.Calls.Count);
    }

    [Fact]
    public async Task ASwitchedOffContainerUrl_IsBlank_NothingSwitched()
    {
        using var session = Session();
        Assert.False(await session.ConnectAsync(Docker("sglang_a", on: false), NoPhase, CancellationToken.None));
        Assert.Null(session.Endpoint);   // blank under scan mode disabled: nothing looked for
        Assert.Empty(_docker.Calls);

        var unticked = Docker("other");
        Assert.False(await session.ConnectAsync(unticked, NoPhase, CancellationToken.None));
        Assert.Empty(_docker.Calls);
    }

    [Fact]
    public async Task TheModelList_IsAskedWhereTheContainerAnswered()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        _http.Map("http://127.0.0.1:30000/v1/models", HttpStatusCode.OK, StubHttpMessageHandler.ModelsJson("sglang-model"));
        using var session = Session();
        await session.ConnectAsync(Docker("sglang_a"), NoPhase, CancellationToken.None);
        var listed = await session.ListModelsAsync(CancellationToken.None);
        Assert.Equal(["sglang-model"], listed!.Value.ModelIds);
        Assert.Equal("http://127.0.0.1:30000/v1/models", _http.Requests.Single().Uri.AbsoluteUri);
    }

    [Fact]
    public async Task ABot_SharesTheRunningContainer_NeverAnother()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var session = Session();
        var (none, noneProblem) = await session.LinkAsync(new AppSettingsData { LlmUrl = "docker:sglang_a" }, CancellationToken.None);
        Assert.Null(none);
        Assert.Equal(DockerServerText.BotNoneRunning, noneProblem);

        await session.ConnectAsync(Docker("sglang_a"), NoPhase, CancellationToken.None);
        var (link, problem) = await session.LinkAsync(new AppSettingsData { LlmUrl = "docker:sglang_a" }, CancellationToken.None);
        Assert.Null(problem);
        Assert.Equal(FakeDockerServers.LiveUrl, link!.Endpoint.LiveUrl);
        link.Dispose();

        var (other, otherProblem) = await session.LinkAsync(new AppSettingsData { LlmUrl = "docker:vllm_b" }, CancellationToken.None);
        Assert.Null(other);
        Assert.Equal(DockerServerText.BotOtherContainer("vllm_b", "sglang_a"), otherProblem);
        Assert.Equal(["switch sglang_a"], _docker.Calls);   // a botchat never starts or stops one

        var (borrowed, _) = await session.LinkAsync(new AppSettingsData { LlmModel = "ignored" }, CancellationToken.None);
        Assert.Equal("sglang-model", borrowed!.Endpoint.ModelId);   // the container's one model goes with it
        borrowed.Dispose();
    }

    [Fact]
    public async Task TheExit_StopsTheContainer_OnlyWithTheSettingOn()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var session = Session();
        await session.ConnectAsync(Docker("sglang_a"), NoPhase, CancellationToken.None);
        Assert.Empty(await session.StopDockerAtExitAsync(Docker("sglang_a"), CancellationToken.None));
        Assert.DoesNotContain("stop-all", _docker.Calls);

        var on = Docker("sglang_a");
        on.DockerServerStopOnExit = true;
        Assert.Equal(["sglang_a"], await session.StopDockerAtExitAsync(on, CancellationToken.None));
        Assert.Equal("stop-all", _docker.Calls[^1]);
        Assert.Null(session.DockerInUse);
    }

    [Fact]
    public async Task TheServerRows_AreTheChosenContainers_TheScansRowOnTheirPortGone()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        _http.Map("http://127.0.0.1:30000/v1/models", HttpStatusCode.OK, StubHttpMessageHandler.ModelsJson("sglang-model"));
        _http.Map("http://127.0.0.1:1234/v1/models", HttpStatusCode.OK, StubHttpMessageHandler.ModelsJson("lm"));
        _docker.List = new DockerServerList(
            [
                new DockerContainer("id-a", "sglang_a", "lmsysorg/sglang:latest", "sha", "running", "Up", 0, [new DockerPort("0.0.0.0", 30000, 30000, "tcp")], new Dictionary<string, string>(), [], []),
                new DockerContainer("id-b", "vllm_b", "vllm/vllm-openai:latest", "sha", "exited", "Exited (0)", 0, [], new Dictionary<string, string>(), [], []),
            ], [], null);
        using var session = Session();
        var effective = Docker("sglang_a");
        effective.LlmScanMode = "local";

        var rows = await session.ProbeServersAsync(effective, null, CancellationToken.None);

        Assert.Equal(["LM Studio", "Docker", "Docker"], rows.Select(r => r.Name));
        Assert.Equal(["http://127.0.0.1:1234/v1", "http://docker.localhost/sglang_a/v1", "http://docker.localhost/vllm_b/v1"], rows.Select(r => r.BaseUrl.AbsoluteUri));
        Assert.Equal("running · lmsysorg/sglang:latest · :30000", rows[1].Result.Detail);
        Assert.Contains("list", _docker.Calls);
    }
}
