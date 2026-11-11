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

    private LlmSession Session(FakeEmbeddedLlm? embedded = null, bool claudeCode = false, TimeProvider? time = null) => new(
        new LlmEndpointProbe(new HttpClient(_http), TimeSpan.FromMilliseconds(500)),
        new ContextLengthProbe(new HttpClient(_http), TimeSpan.FromMilliseconds(500)),
        (endpoint, _) =>
        {
            _endpoints.Add(endpoint);
            return new FakeChatClient();
        },
        time: time,
        embedded: embedded,
        claudeCliOffered: _ => claudeCode,
        dockerServers: _docker);

    private static AppSettingsData Docker(string name, string model = "", bool on = true) => new()
    {
        DockerServers = on,
        DockerServerContainers = ["sglang_a", "vllm_b"],
        LlmUrl = "docker:" + name,
        LlmModel = model,
        LlmScanMode = "disabled",
    };

    [WindowsFact]
    public async Task AChosenContainer_IsSwitchedTo_AndConnectedOnTheLiveUrl_TheSentinelShown()
    {
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
        Assert.Equal(["🐳 starting sglang_a"], labels);
        Assert.Same(endpoint, _endpoints.Single());
        Assert.Equal("sglang_a", session.DockerInUse);
        Assert.Empty(_http.Requests);
    }

    [WindowsFact]
    public async Task TheSavedModel_IsKept_WhenTheContainerListsIt_ElseItsFirst()
    {
        _docker.Answer = _ => FakeDockerServers.Ready("a", "b");
        using var session = Session();
        await session.ConnectAsync(Docker("sglang_a", model: "b"), NoPhase, CancellationToken.None);
        Assert.Equal("b", session.Endpoint!.ModelId);
        await session.ConnectAsync(Docker("sglang_a", model: "gone"), NoPhase, CancellationToken.None);
        Assert.Equal("a", session.Endpoint!.ModelId);
    }

    [WindowsFact]
    public async Task TheServedModel_IsTheContainersOnlyOne_ElseNull()
    {
        using var session = Session();
        Assert.Null(session.DockerServedModel);

        _docker.Answer = _ => FakeDockerServers.Ready("only");
        await session.ConnectAsync(Docker("sglang_a", model: "stale"), NoPhase, CancellationToken.None);
        Assert.Equal("only", session.DockerServedModel);   // the saved id not listed: the one listed

        _docker.Answer = _ => FakeDockerServers.Ready("a", "b");
        await session.ConnectAsync(Docker("sglang_a"), NoPhase, CancellationToken.None);
        Assert.Null(session.DockerServedModel);   // two: nothing to name

        _docker.Answer = _ => FakeDockerServers.Ready("only");
        await session.ConnectAsync(Docker("sglang_a"), NoPhase, CancellationToken.None);
        _docker.Answer = name => DockerSwitch.Failed(DockerServerText.NotReady(name, 900, sideBySide: false), []);
        await session.ConnectAsync(Docker("vllm_b"), NoPhase, CancellationToken.None);
        Assert.Null(session.DockerServedModel);   // a failed switch clears the last one's

        _http.Map("http://127.0.0.1:1234/v1/models", HttpStatusCode.OK, StubHttpMessageHandler.ModelsJson("lm"));
        _docker.Answer = _ => FakeDockerServers.Ready("only");
        await session.ConnectAsync(Docker("sglang_a"), NoPhase, CancellationToken.None);
        var lm = Docker("sglang_a");
        lm.LlmUrl = "http://127.0.0.1:1234/v1";
        await session.ConnectAsync(lm, NoPhase, CancellationToken.None);
        Assert.Null(session.DockerServedModel);   // another server: none
    }

    [Fact]
    public void ModelSaved_IsPinned()
    {
        Assert.Equal("LLM model set to Qwen/Qwen3-8B, the one model sglang_a serves.", DockerServerText.ModelSaved("sglang_a", "Qwen/Qwen3-8B"));
    }

    [WindowsFact]
    public async Task AFailedSwitch_BuildsNoClient_AndSaysWhy()
    {
        _docker.Answer = name => DockerSwitch.Failed(DockerServerText.NotReady(name, 900, sideBySide: false), []);
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
        Assert.Contains(DockerServerText.NotReady("vllm_b", 900, sideBySide: false), errors);
        Assert.Empty(_endpoints);
    }

    [WindowsFact]
    public async Task AnotherServer_StopsTheChosenOnesFirst_AnotherContainer_SwitchesOnly()
    {
        _http.Map("http://127.0.0.1:1234/v1/models", HttpStatusCode.OK, StubHttpMessageHandler.ModelsJson("lm"));
        using var session = Session();
        await session.ConnectAsync(Docker("sglang_a"), NoPhase, CancellationToken.None);
        await session.ConnectAsync(Docker("vllm_b"), NoPhase, CancellationToken.None);
        Assert.Equal(["switch sglang_a", "switch vllm_b"], _docker.Calls);   // the switch itself stops the other

        var lm = Docker("vllm_b");
        lm.LlmUrl = "http://127.0.0.1:1234/v1";
        Assert.True(await session.ConnectAsync(lm, NoPhase, CancellationToken.None));
        // The container in use first, then the rest but the one that may be that server (2026-11-11, the review).
        Assert.Equal(["switch sglang_a", "switch vllm_b", "clear", "clear port 1234"], _docker.Calls);
        Assert.Null(session.DockerInUse);
        Assert.False(_docker.IsRunning("vllm_b"));

        // A scan row picked (the other connect path) leaves too; a reconnect after asks nothing (the clear covered this server).
        await session.ConnectAsync(Docker("sglang_a"), NoPhase, CancellationToken.None);
        // Since 2026-11-11 (the review): the container in use stops first, then the port-spared clear decides the rest, so a side-by-side
        // container serving that server would be spared.
        await session.ConnectAsync(lm, LlmEndpointProbe.Endpoint(LlmServer.From(new Uri("http://127.0.0.1:1234/v1"), new ProbeResult(true, ["lm"], "1")), null, null, false), CancellationToken.None);
        Assert.Equal(["clear", "clear port 1234"], _docker.Calls[^2..]);
        await session.ConnectAsync(lm, NoPhase, CancellationToken.None);
        Assert.Equal(7, _docker.Calls.Count);
    }

    [WindowsFact]
    public async Task AProfileSwitch_ToAContainerItsListAloneNames_StopsTheOldProfilesFirst_ByBothLists_AndSettles()
    {
        using var session = Session();
        await session.ConnectAsync(Docker("sglang_a"), NoPhase, CancellationToken.None);

        // Profile B chooses vllm_c alone: its own switch would never stop sglang_a.
        var other = new AppSettingsData { DockerServers = true, DockerServerContainers = ["vllm_c"], LlmUrl = "docker:vllm_c", LlmScanMode = "disabled" };
        Assert.True(await session.ConnectAsync(other, NoPhase, CancellationToken.None));

        Assert.Equal(["switch sglang_a", "clear except vllm_c", "switch vllm_c freed"], _docker.Calls);   // the switch settles, before its start
        Assert.Equal(["vllm_c", "sglang_a", "vllm_b"], _docker.ClearNames.Single());   // profile B's list, then A's
        Assert.Equal("vllm_c", session.DockerInUse);
        Assert.False(_docker.IsRunning("sglang_a"));
    }

    [EmbeddedHereFact]
    public async Task AProfileSwitch_ToTheEmbeddedModel_WithDockerOff_StopsTheOldProfilesContainer_BeforeTheLoad()
    {
        var embedded = StartLogged(new FakeEmbeddedLlm().Installed("gemma-4-e2b"));
        using var session = Session(embedded);
        await session.ConnectAsync(Docker("sglang_a"), NoPhase, CancellationToken.None);

        var gemma = new AppSettingsData { LlmUrl = "embedded", LlmModel = "gemma-4-e2b", LlmContextLength = 8192, LlmScanMode = "disabled", EmbeddedLlmServer = true };   // off by default since 2026-10-21
        Assert.True(await session.ConnectAsync(gemma, NoPhase, CancellationToken.None));

        Assert.Equal(["switch sglang_a", "clear settle", "embedded start"], _docker.Calls);   // the load's own clear asks nothing more
        Assert.Equal(["sglang_a", "vllm_b"], _docker.ClearNames.Single());   // profile A's list; B's is off
        Assert.Null(session.DockerInUse);
    }

    [EmbeddedHereFact]
    public async Task AProfileSwitch_ToTheEmbeddedModel_StopsTheNewProfilesLeftover_Too()
    {
        // 2026-10-26 (the review's R3): neon's container in use, then test's embedded model with test's own container left running. The
        // owned leave went by neon's list alone and the sweep was skipped after it, so test's container stayed beside the model.
        _docker.List = new DockerServerList([Running("vllm_b", 8000)], [], null);
        using var session = Session(StartLogged(new FakeEmbeddedLlm().Installed("gemma-4-e2b")));
        var neon = Docker("sglang_a");
        neon.DockerServerContainers = ["sglang_a"];
        await session.ConnectAsync(neon, NoPhase, CancellationToken.None);
        var test = Embedded();
        test.DockerServerContainers = ["vllm_b"];

        Assert.True(await session.ConnectAsync(test, NoPhase, CancellationToken.None));

        Assert.Equal(["switch sglang_a", "clear settle", "embedded start"], _docker.Calls);
        Assert.False(_docker.IsRunning("sglang_a"));
        Assert.False(_docker.IsRunning("vllm_b"));
        var leftover = Assert.Single(session.TakeLeftoverStopped());
        Assert.Equal(new DockerLeftover(["vllm_b"], true), leftover, LeftoverComparer.Instance);   // neon's own: left, not a leftover
    }

    [WindowsFact]
    public async Task AProfileSwitch_ToABlankUrl_StopsTheContainerBeforeTheScan()
    {
        using var session = Session();
        await session.ConnectAsync(Docker("sglang_a"), NoPhase, CancellationToken.None);

        // The scan would find the old container's port; it is stopped before anything is asked.
        await session.DiscoverAsync(new AppSettingsData { LlmScanMode = "disabled" }, CancellationToken.None);

        Assert.Equal(["switch sglang_a", "clear"], _docker.Calls);
        Assert.Equal(["sglang_a", "vllm_b"], _docker.ClearNames.Single());
        Assert.Null(session.DockerInUse);
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

    [WindowsFact]
    public async Task TheModelList_IsAskedWhereTheContainerAnswered()
    {
        _http.Map("http://127.0.0.1:30000/v1/models", HttpStatusCode.OK, StubHttpMessageHandler.ModelsJson("sglang-model"));
        using var session = Session();
        await session.ConnectAsync(Docker("sglang_a"), NoPhase, CancellationToken.None);
        var listed = await session.ListModelsAsync(CancellationToken.None);
        Assert.Equal(["sglang-model"], listed!.Value.ModelIds);
        Assert.Equal("http://127.0.0.1:30000/v1/models", _http.Requests.Single().Uri.AbsoluteUri);
    }

    /// <summary>
    /// A server that leaves this machine's GPU alone keeps the container running (2026-10-10, the user's call): the Claude Code server and
    /// another machine's server, by either connect path; a bot or an advisor still shares it, and a connect to this machine stops it then.
    /// </summary>
    [WindowsFact]
    public async Task AServerThatLeavesTheGpuAlone_KeepsTheContainer_StillShared_UntilALocalOne()
    {
        _http.Map("http://gpu-box:8000/v1/models", HttpStatusCode.OK, StubHttpMessageHandler.ModelsJson("remote"));
        _http.Map("http://127.0.0.1:1234/v1/models", HttpStatusCode.OK, StubHttpMessageHandler.ModelsJson("lm"));
        using var session = Session(claudeCode: true);
        await session.ConnectAsync(Docker("sglang_a"), NoPhase, CancellationToken.None);

        var claude = Docker("sglang_a");
        claude.LlmUrl = Claude.ClaudeCliEndpoint.BaseUrl.ToString();
        Assert.True(await session.ConnectAsync(claude, NoPhase, CancellationToken.None));
        var remote = Docker("sglang_a");
        remote.LlmUrl = "http://gpu-box:8000/v1";
        Assert.True(await session.ConnectAsync(remote, NoPhase, CancellationToken.None));
        Assert.Equal(["switch sglang_a"], _docker.Calls);   // nothing stopped
        Assert.Equal("sglang_a", session.DockerInUse);

        var (link, problem) = await session.LinkAsync(new AppSettingsData { LlmUrl = "docker:sglang_a" }, CancellationToken.None);
        Assert.Null(problem);
        Assert.Equal(FakeDockerServers.LiveUrl, link!.Endpoint.LiveUrl);   // shared, though the chat is elsewhere
        link.Dispose();
        var (other, otherProblem) = await session.LinkAsync(new AppSettingsData { LlmUrl = "docker:vllm_b" }, CancellationToken.None);
        Assert.Null(other);
        Assert.Equal(DockerServerText.BotOtherContainer("vllm_b", ["sglang_a"]), otherProblem);

        var local = Docker("sglang_a");
        local.LlmUrl = "http://127.0.0.1:1234/v1";
        Assert.True(await session.ConnectAsync(local, NoPhase, CancellationToken.None));
        Assert.Equal(["switch sglang_a", "clear", "clear port 1234"], _docker.Calls);   // this machine's server: the GPU freed first
        Assert.Null(session.DockerInUse);
        var (gone, goneProblem) = await session.LinkAsync(new AppSettingsData { LlmUrl = "docker:sglang_a" }, CancellationToken.None);
        Assert.Null(gone);
        Assert.Equal(DockerServerText.BotNoneRunning, goneProblem);
    }

    [WindowsFact]
    public async Task AClaudeCodeUrlSwitchedOff_IsABlankUrl_AndStopsTheContainer()
    {
        using var session = Session(claudeCode: false);
        await session.ConnectAsync(Docker("sglang_a"), NoPhase, CancellationToken.None);
        var claude = Docker("sglang_a");
        claude.LlmUrl = Claude.ClaudeCliEndpoint.BaseUrl.ToString();

        await session.ConnectAsync(claude, NoPhase, CancellationToken.None);

        Assert.Equal(["switch sglang_a", "clear"], _docker.Calls);   // it scans as a blank URL would, on this machine's ports
    }

    [Theory]
    [InlineData("http://127.0.0.1:1234/v1", false)]
    [InlineData("http://localhost:8000/v1", false)]
    [InlineData("http://[::1]:8000/v1", false)]
    [InlineData("http://0.0.0.0:8000/v1", false)]   // a server's every-address bind typed as its URL (the review's R35)
    [InlineData("http://[::]:8000/v1", false)]
    [InlineData("http://BOX:8000/v1", false)]   // this machine by name
    [InlineData("http://embedded.localhost/v1", false)]
    [InlineData("http://docker.localhost/sglang_a/v1", false)]
    [InlineData("http://claude-code.localhost/v1", true)]
    [InlineData("https://api.anthropic.com/v1", true)]
    [InlineData("https://api.openai.com/v1", true)]
    [InlineData("http://gpu-box:8000/v1", true)]
    [InlineData("http://192.168.1.20:8000/v1", true)]
    public void LeavesTheGpuAlone_IsAnotherMachine_OrAHostedServer(string url, bool alone)
    {
        Assert.Equal(alone, DockerEndpoint.LeavesTheGpuAlone(new Uri(url), machine: "box"));
    }

    [WindowsFact]
    public async Task ABot_SharesTheRunningContainer_NeverAnother()
    {
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
        Assert.Equal(DockerServerText.BotOtherContainer("vllm_b", ["sglang_a"]), otherProblem);
        Assert.Equal(["switch sglang_a"], _docker.Calls);   // a botchat never starts or stops one

        var (borrowed, _) = await session.LinkAsync(new AppSettingsData { LlmModel = "ignored" }, CancellationToken.None);
        Assert.Equal("sglang-model", borrowed!.Endpoint.ModelId);   // the container's one model goes with it
        borrowed.Dispose();
    }

    [WindowsFact]
    public async Task TheExit_StopsTheContainer_OnlyWithTheSettingOn()
    {
        using var session = Session();
        await session.ConnectAsync(Docker("sglang_a"), NoPhase, CancellationToken.None);
        Assert.Empty(await session.StopDockerAtExitAsync(Docker("sglang_a"), CancellationToken.None));
        Assert.DoesNotContain("clear", _docker.Calls);

        var on = Docker("sglang_a");
        on.DockerServerStopOnExit = true;
        Assert.Equal(["sglang_a"], await session.StopDockerAtExitAsync(on, CancellationToken.None));
        Assert.Equal("clear together", _docker.Calls[^1]);   // every stop asked at once (2026-11-10)
        Assert.Null(session.DockerInUse);
    }

    [WindowsFact]
    public async Task TheServerRows_AreTheChosenContainers_TheScansRowOnTheirPortGone()
    {
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

    // ── a container this run never started (2026-10-26, the user's report) ──────────────────────────

    private static DockerContainer Running(string name, int? port = null, IReadOnlyList<string>? networks = null) =>
        new("id-" + name, name, "lmsysorg/sglang:latest", "sha", "running", "Up", 0, port is { } p ? [new DockerPort("0.0.0.0", p, p, "tcp")] : [], new Dictionary<string, string>(), networks ?? [], []);

    private static DockerContainer Exited(string name) =>
        new("id-" + name, name, "vllm/vllm-openai:latest", "sha", "exited", "Exited (0)", 0, [], new Dictionary<string, string>(), [], []);

    private static AppSettingsData Embedded(bool docker = true) => new()
    {
        LlmUrl = "embedded",
        LlmModel = "gemma-4-e2b",
        LlmContextLength = 8192,
        LlmScanMode = "disabled",
        EmbeddedLlmServer = true,
        DockerServers = docker,
        DockerServerContainers = ["sglang_a", "vllm_b"],
    };

    private static AppSettingsData Local(string url)
    {
        var local = Docker("sglang_a");
        local.LlmUrl = url;
        local.LlmContextLength = 8192;
        return local;
    }

    private FakeEmbeddedLlm StartLogged(FakeEmbeddedLlm embedded)
    {
        embedded.StartGate = _ =>
        {
            _docker.Calls.Add("embedded start");
            return Task.CompletedTask;
        };
        return embedded;
    }

    /// <summary>The log's lines at <paramref name="level"/> or above while <paramref name="work"/> runs.</summary>
    private static async Task<List<string>> LoggedAsync(NeonSidekick.Diagnostics.DiagnosticLevel level, Func<Task> work)
    {
        var lines = new List<string>();
        Action<NeonSidekick.Diagnostics.DiagnosticEvent> capture = e =>
        {
            if (e.Level >= level)
            {
                lock (lines)
                {
                    lines.Add(e.Message);
                }
            }
        };
        NeonSidekick.Diagnostics.DiagnosticLog.Emitted += capture;
        try
        {
            await work();
        }
        finally
        {
            NeonSidekick.Diagnostics.DiagnosticLog.Emitted -= capture;
        }

        return lines;
    }

    [EmbeddedHereFact]
    public async Task TheEmbeddedLoad_StopsALeftoverContainer_AndSettles_SayingSoOnce()
    {
        _docker.List = new DockerServerList([Running("sglang_a", 30000), Exited("vllm_b")], [], null);
        using var session = Session(StartLogged(new FakeEmbeddedLlm().Installed("gemma-4-e2b")));
        var labels = new List<string>();
        var infos = await LoggedAsync(NeonSidekick.Diagnostics.DiagnosticLevel.Info,
            async () => Assert.True(await session.ConnectAsync(Embedded(), labels.Add, CancellationToken.None)));

        Assert.Equal(["clear settle", "embedded start"], _docker.Calls);
        Assert.Equal(["sglang_a", "vllm_b"], _docker.ClearNames.Single());   // the loaded profile's list
        Assert.Contains(DockerServerText.LeftForThisComputer(["sglang_a"], embedded: true), infos);
        var model = new FakeEmbeddedLlm().Catalog.Single(m => m.Id == "gemma-4-e2b");
        Assert.Equal([EmbeddedLlm.EmbeddedLlmText.StartingLabel(model), DockerServerText.Stopping("sglang_a"), DockerServerText.Settling,
            EmbeddedLlm.EmbeddedLlmText.StartingLabel(model)], labels);   // the spinner says the model again after the clear (the review's R39)
        Assert.Equal(new DockerLeftover(["sglang_a"], true), Assert.Single(session.TakeLeftoverStopped()), LeftoverComparer.Instance);
        Assert.Empty(session.TakeLeftoverStopped());   // once
        Assert.Null(session.DockerInUse);
    }

    [EmbeddedHereFact]
    public async Task TheEmbeddedLoad_WithNothingActive_AsksOnce_SayingNothing()
    {
        _docker.List = new DockerServerList([Exited("sglang_a"), Exited("vllm_b")], [], null);
        using var session = Session(StartLogged(new FakeEmbeddedLlm().Installed("gemma-4-e2b")));
        Assert.True(await session.ConnectAsync(Embedded(), NoPhase, CancellationToken.None));
        Assert.Equal(["clear settle", "embedded start"], _docker.Calls);
        Assert.Empty(session.TakeLeftoverStopped());   // nothing stopped: nothing to say
    }

    [EmbeddedHereFact]
    public async Task TheEmbeddedLoad_WithTheEngineDown_GoesOn_WithNoWarning()
    {
        _docker.List = new DockerServerList([], [], "The Docker engine is not running.");
        using var session = Session(StartLogged(new FakeEmbeddedLlm().Installed("gemma-4-e2b")));
        var warnings = await LoggedAsync(NeonSidekick.Diagnostics.DiagnosticLevel.Warning,
            async () => Assert.True(await session.ConnectAsync(Embedded(), NoPhase, CancellationToken.None)));

        Assert.Equal(["clear settle", "embedded start"], _docker.Calls);
        Assert.Empty(warnings);
    }

    [EmbeddedHereFact]
    public async Task TheEmbeddedLoad_WithDockerServersOff_AsksNothing()
    {
        _docker.List = new DockerServerList([Running("sglang_a", 30000)], [], null);
        using var session = Session(StartLogged(new FakeEmbeddedLlm().Installed("gemma-4-e2b")));
        Assert.True(await session.ConnectAsync(Embedded(docker: false), NoPhase, CancellationToken.None));
        Assert.Equal(["embedded start"], _docker.Calls);
    }

    [EmbeddedHereFact]
    public async Task TheSameEmbeddedModel_Running_IsNotAskedAgain_ARealReloadIs()
    {
        _docker.List = new DockerServerList([Exited("sglang_a")], [], null);
        var embedded = StartLogged(new FakeEmbeddedLlm().Installed("gemma-4-e2b"));
        using var session = Session(embedded);
        Assert.True(await session.ConnectAsync(Embedded(), NoPhase, CancellationToken.None));
        Assert.True(await session.ConnectAsync(Embedded(), NoPhase, CancellationToken.None));   // a /reasoning reconnect
        Assert.Single(_docker.Calls, "clear settle");

        // A changed launch reloads the same model (the review's R43): the GPU is cleared again, and a load always asks (no memo).
        embedded.KeepsRunning = (_, _) => false;
        Assert.True(await session.ConnectAsync(Embedded(), NoPhase, CancellationToken.None));
        Assert.Equal(2, _docker.Calls.Count(c => c == "clear settle"));
    }

    [EmbeddedHereFact]
    public async Task AnEmbeddedModelNotInstalled_AsksNothing()
    {
        _docker.List = new DockerServerList([Running("sglang_a", 30000)], [], null);
        using var session = Session(StartLogged(new FakeEmbeddedLlm()));
        Assert.False(await session.ConnectAsync(Embedded(), NoPhase, CancellationToken.None));
        Assert.Empty(_docker.Calls);
    }

    [EmbeddedHereFact]
    public async Task ABotsEmbeddedLoad_ClearsTheGpu_SparingTheMainSessionsContainer()
    {
        // 2026-10-26 (the review's R14): a bot's or an advisor's load starts llama-server as the main session's does.
        _docker.List = new DockerServerList([Running("vllm_b", 8000)], [], null);
        var embedded = new FakeEmbeddedLlm().Installed("gemma-4-e2b");
        using var session = Session(embedded);
        var main = Docker("sglang_a");
        main.EmbeddedLlmServer = true;
        main.DockerServerContainers = ["sglang_a"];   // the bot's profile names vllm_b too: the switch never stopped it
        await session.ConnectAsync(main, NoPhase, CancellationToken.None);

        var (link, problem) = await session.LinkAsync(Embedded(), CancellationToken.None);

        Assert.Null(problem);
        link!.Dispose();
        Assert.Equal(["switch sglang_a", "clear except sglang_a settle"], _docker.Calls);
        Assert.True(_docker.IsRunning("sglang_a"));   // the main session still talks to it
        Assert.False(_docker.IsRunning("vllm_b"));
        Assert.Equal("sglang_a", session.DockerInUse);
        Assert.Equal(new DockerLeftover(["vllm_b"], true), Assert.Single(session.TakeLeftoverStopped()), LeftoverComparer.Instance);
    }

    [WindowsFact]
    public async Task ALocalServer_StopsALeftoverContainer_OncePerServer_ARemoteOneNever()
    {
        _http.Map("http://127.0.0.1:1234/v1/models", HttpStatusCode.OK, StubHttpMessageHandler.ModelsJson("lm"));
        _http.Map("http://gpu-box:8000/v1/models", HttpStatusCode.OK, StubHttpMessageHandler.ModelsJson("remote"));
        _docker.List = new DockerServerList([Running("sglang_a", 30000), Exited("vllm_b")], [], null);
        using var session = Session();
        var labels = new List<string>();

        Assert.True(await session.ConnectAsync(Local("http://127.0.0.1:1234/v1"), labels.Add, CancellationToken.None));
        Assert.Equal(["clear port 1234"], _docker.Calls);
        Assert.Equal(new DockerLeftover(["sglang_a"], false), Assert.Single(session.TakeLeftoverStopped()), LeftoverComparer.Instance);
        Assert.Equal([DockerServerText.Stopping("sglang_a"), ConnectText.Connecting], labels);   // the spinner's word back (the review's R39)

        Assert.True(await session.ConnectAsync(Local("http://127.0.0.1:1234/v1"), NoPhase, CancellationToken.None));   // /model, /reasoning
        Assert.Equal(["clear port 1234"], _docker.Calls);

        Assert.True(await session.ConnectAsync(Local("http://gpu-box:8000/v1"), NoPhase, CancellationToken.None));
        Assert.Equal(["clear port 1234"], _docker.Calls);   // another machine's GPU

        Assert.True(await session.ConnectAsync(Local("http://127.0.0.1:1234/v1"), NoPhase, CancellationToken.None));
        Assert.Equal(["clear port 1234", "clear port 1234"], _docker.Calls);   // back again: asked again
    }

    [WindowsFact]
    public async Task ALocalServer_IsAskedAgain_WhenTheChosenListChanges()
    {
        // The memo goes by the names too (the review's R36): a container ticked since may be running.
        _http.Map("http://127.0.0.1:1234/v1/models", HttpStatusCode.OK, StubHttpMessageHandler.ModelsJson("lm"));
        _docker.List = new DockerServerList([Running("vllm_c", 9000)], [], null);
        using var session = Session();
        Assert.True(await session.ConnectAsync(Local("http://127.0.0.1:1234/v1"), NoPhase, CancellationToken.None));
        Assert.True(_docker.IsRunning("vllm_c"));   // not chosen: never touched

        var ticked = Local("http://127.0.0.1:1234/v1");
        ticked.DockerServerContainers = ["sglang_a", "vllm_b", "vllm_c"];
        Assert.True(await session.ConnectAsync(ticked, NoPhase, CancellationToken.None));
        Assert.Equal(["clear port 1234", "clear port 1234"], _docker.Calls);
        Assert.False(_docker.IsRunning("vllm_c"));
    }

    [WindowsFact]
    public async Task TheEngineDown_IsAskedOnceAMinute_AndNoMemoIsKept()
    {
        // The review's R21 and R11: Docker Desktop not running costs a pipe wait, so it is asked again only after a minute; a clear that got no
        // answer marks nothing cleared, so once the engine answers, the server's clear runs.
        _http.Map("http://127.0.0.1:1234/v1/models", HttpStatusCode.OK, StubHttpMessageHandler.ModelsJson("lm"));
        _docker.List = new DockerServerList([], [], "The Docker engine is not running.");
        var time = new ManualTimeProvider();
        using var session = Session(time: time);
        Assert.True(await session.ConnectAsync(Local("http://127.0.0.1:1234/v1"), NoPhase, CancellationToken.None));
        Assert.True(await session.ConnectAsync(Local("http://127.0.0.1:1234/v1"), NoPhase, CancellationToken.None));
        Assert.Equal(["clear port 1234"], _docker.Calls);

        time.Advance(LlmSession.EngineDownWait + TimeSpan.FromSeconds(1));
        _docker.List = new DockerServerList([Running("sglang_a", 30000)], [], null);
        Assert.True(await session.ConnectAsync(Local("http://127.0.0.1:1234/v1"), NoPhase, CancellationToken.None));
        Assert.Equal(["clear port 1234", "clear port 1234"], _docker.Calls);
        Assert.False(_docker.IsRunning("sglang_a"));
    }

    [WindowsFact]
    public async Task AnEscMidClear_KeepsTheStopsMade()
    {
        // The review's R6: each stop is reported as it happens, so an Esc between two stops still says the first.
        _http.Map("http://127.0.0.1:1234/v1/models", HttpStatusCode.OK, StubHttpMessageHandler.ModelsJson("lm"));
        _docker.List = new DockerServerList([Running("sglang_a", 30000), Running("vllm_b", 8000)], [], null);
        using var cts = new CancellationTokenSource();
        _docker.StopGate = (name, _) =>
        {
            if (name == "vllm_b")
            {
                cts.Cancel();
            }

            return Task.CompletedTask;
        };
        using var session = Session();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ConnectAsync(Local("http://127.0.0.1:1234/v1"), NoPhase, cts.Token));

        Assert.Equal(new DockerLeftover(["sglang_a"], false), Assert.Single(session.TakeLeftoverStopped()), LeftoverComparer.Instance);
        Assert.True(_docker.IsRunning("vllm_b"));
    }

    [WindowsFact]
    public async Task ALocalServer_OnAContainersOwnPort_SparesIt()
    {
        _http.Map("http://127.0.0.1:30000/v1/models", HttpStatusCode.OK, StubHttpMessageHandler.ModelsJson("sglang-model"));
        _docker.List = new DockerServerList([Running("sglang_a", 30000), Running("vllm_b", 8000)], [], null);
        using var session = Session();
        Assert.True(await session.ConnectAsync(Local("http://127.0.0.1:30000/v1"), NoPhase, CancellationToken.None));
        Assert.Equal(["clear port 30000"], _docker.Calls);
        Assert.True(_docker.IsRunning("sglang_a"));
        Assert.False(_docker.IsRunning("vllm_b"));
    }

    [WindowsFact]
    public async Task ALocalServer_OnTheOnlyActiveContainersPort_StopsNothing()
    {
        _http.Map("http://127.0.0.1:30000/v1/models", HttpStatusCode.OK, StubHttpMessageHandler.ModelsJson("sglang-model"));
        _docker.List = new DockerServerList([Running("sglang_a", 30000), Exited("vllm_b")], [], null);
        using var session = Session();
        Assert.True(await session.ConnectAsync(Local("http://127.0.0.1:30000/v1"), NoPhase, CancellationToken.None));
        Assert.Equal(["clear port 30000"], _docker.Calls);
        Assert.True(_docker.IsRunning("sglang_a"));
        Assert.Empty(session.TakeLeftoverStopped());
    }

    [WindowsFact]
    public async Task AHostNetworkContainer_IsSparedForALocalServer()
    {
        _http.Map("http://127.0.0.1:8000/v1/models", HttpStatusCode.OK, StubHttpMessageHandler.ModelsJson("vllm-model"));
        _docker.List = new DockerServerList([Running("sglang_a", 30000), Running("vllm_b", networks: ["host"])], [], null);
        using var session = Session();
        Assert.True(await session.ConnectAsync(Local("http://127.0.0.1:8000/v1"), NoPhase, CancellationToken.None));
        Assert.Equal(["clear port 8000"], _docker.Calls);
        Assert.True(_docker.IsRunning("vllm_b"));   // it publishes no port: it may be the server
        Assert.False(_docker.IsRunning("sglang_a"));
    }

    [EmbeddedHereFact]
    public async Task AHostNetworkContainer_IsStoppedForTheEmbeddedLoad()
    {
        _docker.List = new DockerServerList([Running("sglang_a", 30000), Running("vllm_b", networks: ["host"])], [], null);
        using var session = Session(StartLogged(new FakeEmbeddedLlm().Installed("gemma-4-e2b")));
        Assert.True(await session.ConnectAsync(Embedded(), NoPhase, CancellationToken.None));
        Assert.Equal(["clear settle", "embedded start"], _docker.Calls);
        Assert.False(_docker.IsRunning("vllm_b"));
        Assert.False(_docker.IsRunning("sglang_a"));
    }

    [WindowsFact]
    public async Task TheScansPick_IsCleared_ItsPortSpared()
    {
        _docker.List = new DockerServerList([Running("sglang_a", 30000), Running("vllm_b", 8000)], [], null);
        using var session = Session();
        var blank = Local("");
        await session.DiscoverAsync(blank, CancellationToken.None);
        Assert.Equal(["list"], _docker.Calls);   // the rows' list: nothing stopped before the scan, which may be the container

        var row = LlmEndpointProbe.Endpoint(LlmServer.From(new Uri("http://127.0.0.1:8000/v1"), new ProbeResult(true, ["vllm-model"], "1")), null, null, false);
        Assert.True(await session.ConnectAsync(blank, row, CancellationToken.None));
        Assert.Equal(["list", "clear port 8000"], _docker.Calls);
        Assert.True(_docker.IsRunning("vllm_b"));
    }

    [WindowsFact]
    public async Task TheScansClear_ThenThePick_AsksTheEngineOnce()
    {
        // The review's R44: with a container in use, the scan's clear stops them all; the pick's connect asks nothing more, nor a reconnect.
        _http.Map("http://127.0.0.1:1234/v1/models", HttpStatusCode.OK, StubHttpMessageHandler.ModelsJson("lm"));
        using var session = Session();
        await session.ConnectAsync(Docker("sglang_a"), NoPhase, CancellationToken.None);
        var blank = Local("");
        await session.DiscoverAsync(blank, CancellationToken.None);
        var row = LlmEndpointProbe.Endpoint(LlmServer.From(new Uri("http://127.0.0.1:1234/v1"), new ProbeResult(true, ["lm"], "1")), null, null, false);
        Assert.True(await session.ConnectAsync(blank, row, CancellationToken.None));
        Assert.True(await session.ConnectAsync(blank, row, CancellationToken.None));

        Assert.Equal(["switch sglang_a", "clear", "list"], _docker.Calls);
    }

    [WindowsFact]
    public async Task TheLeftover_OutlivesTheNextConnect_UntilTaken_AndAnOwnedStopIsNone()
    {
        // 2026-10-26 (the review's R14): a connect no longer drops an untaken stop: a bot's or an advisor's may be waiting to be said.
        _http.Map("http://127.0.0.1:1234/v1/models", HttpStatusCode.OK, StubHttpMessageHandler.ModelsJson("lm"));
        _docker.List = new DockerServerList([Running("sglang_a", 30000)], [], null);
        using var session = Session();
        Assert.True(await session.ConnectAsync(Local("http://127.0.0.1:1234/v1"), NoPhase, CancellationToken.None));
        Assert.True(await session.ConnectAsync(Docker("sglang_a"), NoPhase, CancellationToken.None));
        Assert.Equal(new DockerLeftover(["sglang_a"], false), Assert.Single(session.TakeLeftoverStopped()), LeftoverComparer.Instance);

        Assert.True(await session.ConnectAsync(Local("http://127.0.0.1:1234/v1"), NoPhase, CancellationToken.None));
        // Its own container left, then the port-spared clear (2026-11-11, the review), which finds nothing more.
        Assert.Equal(["clear port 1234", "switch sglang_a", "clear", "clear port 1234"], _docker.Calls);
        Assert.Empty(session.TakeLeftoverStopped());
    }

    [Fact]
    public void LeftForThisComputer_NamesTheContainers_AndWhatFor()
    {
        Assert.Equal("Stopped the Docker server sglang_a to free the GPU for the embedded model.", DockerServerText.LeftForThisComputer(["sglang_a"], embedded: true));
        Assert.Equal("Stopped the Docker servers sglang_a, vllm_b to free the GPU for a server on this computer.", DockerServerText.LeftForThisComputer(["sglang_a", "vllm_b"], embedded: false));
        Assert.Equal("Stopped the Docker server vllm_b to free the GPU for a server on this computer.", DockerServerText.LeftForThisComputer(["vllm_b"], embedded: false));
        Assert.Equal("Stopped the Docker servers sglang_a, vllm_b to free the GPU for the embedded model.", DockerServerText.LeftForThisComputer(["sglang_a", "vllm_b"], embedded: true));
    }

    private static AppSettingsData OneLocalOff(AppSettingsData settings)
    {
        settings.LlmOneLocalModel = false;
        return settings;
    }

    [EmbeddedHereFact]
    public async Task TheEmbeddedLoad_WithOneLocalModelOff_ClearsNothing()
    {
        _docker.List = new DockerServerList([Running("sglang_a", 30000), Exited("vllm_b")], [], null);
        using var session = Session(StartLogged(new FakeEmbeddedLlm().Installed("gemma-4-e2b")));

        Assert.True(await session.ConnectAsync(OneLocalOff(Embedded()), NoPhase, CancellationToken.None));

        Assert.Equal(["embedded start"], _docker.Calls);
        Assert.True(_docker.IsRunning("sglang_a"));
        Assert.Empty(session.TakeLeftoverStopped());
    }

    [WindowsFact]
    public async Task ALocalServer_WithOneLocalModelOff_StopsNoContainer_BackOnItAsksAgain()
    {
        _http.Map("http://127.0.0.1:1234/v1/models", HttpStatusCode.OK, StubHttpMessageHandler.ModelsJson("lm"));
        _docker.List = new DockerServerList([Running("sglang_a", 30000), Exited("vllm_b")], [], null);
        using var session = Session();

        Assert.True(await session.ConnectAsync(OneLocalOff(Local("http://127.0.0.1:1234/v1")), NoPhase, CancellationToken.None));
        Assert.Empty(_docker.Calls);
        Assert.True(_docker.IsRunning("sglang_a"));

        // Turned back on: no memo was written while off, so the next connect to it clears.
        Assert.True(await session.ConnectAsync(Local("http://127.0.0.1:1234/v1"), NoPhase, CancellationToken.None));
        Assert.Equal(["clear port 1234"], _docker.Calls);
        Assert.False(_docker.IsRunning("sglang_a"));
    }

    [WindowsFact]
    public async Task AnotherServer_WithOneLocalModelOff_KeepsTheContainerInUse_AnotherContainerStillSwitches()
    {
        _http.Map("http://127.0.0.1:1234/v1/models", HttpStatusCode.OK, StubHttpMessageHandler.ModelsJson("lm"));
        using var session = Session();
        await session.ConnectAsync(OneLocalOff(Docker("sglang_a")), NoPhase, CancellationToken.None);

        Assert.True(await session.ConnectAsync(OneLocalOff(Local("http://127.0.0.1:1234/v1")), NoPhase, CancellationToken.None));
        Assert.Equal(["switch sglang_a"], _docker.Calls);
        Assert.True(_docker.IsRunning("sglang_a"));
        Assert.Equal("sglang_a", session.DockerInUse);

        // Container to container is Docker server one at a time's, whatever this one says.
        await session.ConnectAsync(OneLocalOff(Docker("vllm_b")), NoPhase, CancellationToken.None);
        Assert.Equal(["switch sglang_a", "switch vllm_b"], _docker.Calls);
        Assert.False(_docker.IsRunning("sglang_a"));
        Assert.True(_docker.IsRunning("vllm_b"));
    }

    [WindowsFact]
    public async Task ABlankUrlsScan_WithOneLocalModelOff_StopsNothingFirst()
    {
        using var session = Session();
        await session.ConnectAsync(Docker("sglang_a"), NoPhase, CancellationToken.None);
        var blank = OneLocalOff(Docker("sglang_a"));
        blank.LlmUrl = "";

        await session.DiscoverAsync(blank, CancellationToken.None);

        Assert.Equal(["switch sglang_a", "list"], _docker.Calls);   // the scan's rows only: nothing stopped first
        Assert.Equal("sglang_a", session.DockerInUse);
    }

    [WindowsFact]
    public async Task WithOneLocalModelOff_TheContainerInUse_SwitchedOffOrUnticked_StillStops()
    {
        _http.Map("http://127.0.0.1:1234/v1/models", HttpStatusCode.OK, StubHttpMessageHandler.ModelsJson("lm"));
        using var session = Session();
        await session.ConnectAsync(Docker("sglang_a"), NoPhase, CancellationToken.None);
        var switchedOff = OneLocalOff(Local("http://127.0.0.1:1234/v1"));
        switchedOff.DockerServers = false;

        Assert.True(await session.ConnectAsync(switchedOff, NoPhase, CancellationToken.None));
        Assert.Equal(["switch sglang_a", "clear"], _docker.Calls);
        Assert.False(_docker.IsRunning("sglang_a"));
        Assert.Null(session.DockerInUse);

        await session.ConnectAsync(Docker("sglang_a"), NoPhase, CancellationToken.None);
        var unticked = OneLocalOff(Local("http://127.0.0.1:1234/v1"));
        unticked.DockerServerContainers = ["vllm_b"];
        Assert.True(await session.ConnectAsync(unticked, NoPhase, CancellationToken.None));
        Assert.Equal("clear", _docker.Calls[^1]);
        Assert.False(_docker.IsRunning("sglang_a"));
    }

    [EmbeddedHereFact]
    public async Task ABotsEmbeddedLoad_WithOneLocalModelOffNow_ClearsNothing_WithNoFunc_TheConnectsCopyGoverns()
    {
        _docker.List = new DockerServerList([Running("vllm_b", 8000)], [], null);
        var main = Docker("sglang_a");
        main.EmbeddedLlmServer = true;
        main.DockerServerContainers = ["sglang_a"];

        // The screen's func says off, though the connect was made with it on: no reconnect needed.
        using (var session = Session(new FakeEmbeddedLlm().Installed("gemma-4-e2b")))
        {
            session.SettingsNow = () => OneLocalOff(main);
            await session.ConnectAsync(main, NoPhase, CancellationToken.None);
            var (link, problem) = await session.LinkAsync(Embedded(), CancellationToken.None);
            Assert.Null(problem);
            link!.Dispose();
            Assert.Equal(["switch sglang_a"], _docker.Calls);
            Assert.True(_docker.IsRunning("vllm_b"));
        }

        // No func (headless): the connect's copy.
        _docker.Calls.Clear();
        using (var session = Session(new FakeEmbeddedLlm().Installed("gemma-4-e2b")))
        {
            await session.ConnectAsync(OneLocalOff(main), NoPhase, CancellationToken.None);
            var (link, problem) = await session.LinkAsync(Embedded(), CancellationToken.None);
            Assert.Null(problem);
            link!.Dispose();
            Assert.Equal(["switch sglang_a"], _docker.Calls);
            Assert.True(_docker.IsRunning("vllm_b"));
        }
    }

    [EmbeddedHereFact]
    public async Task AContainerPicked_WithOneLocalModelOff_KeepsTheEmbeddedModel_CtrlAltXUnloadsIt_TheContainerStays()
    {
        var embedded = StartLogged(new FakeEmbeddedLlm().Installed("gemma-4-e2b"));
        using var session = Session(embedded);
        await session.ConnectAsync(OneLocalOff(Embedded()), NoPhase, CancellationToken.None);
        var container = OneLocalOff(Docker("sglang_a"));
        container.EmbeddedLlmServer = true;

        Assert.True(await session.ConnectAsync(container, NoPhase, CancellationToken.None));
        Assert.Equal(["embedded start", "switch sglang_a"], _docker.Calls);
        Assert.NotNull(embedded.Running);
        Assert.Equal(0, embedded.Stops);

        Assert.Equal(["gemma-4-e2b"], session.KillEmbedded());
        session.EmbeddedUnloaded();
        Assert.Null(embedded.Running);
        Assert.Equal(DockerEndpoint.BaseUrl("sglang_a"), session.Endpoint!.BaseUrl);
        Assert.NotNull(session.Assistant);
    }

    [WindowsFact]
    public async Task AnotherContainer_WithOneAtATimeOff_LeavesTheFirstRunning_TheExitStopsBothTogether()
    {
        using var session = Session();
        var a = Docker("sglang_a");
        a.DockerServerOneAtATime = false;
        var b = Docker("vllm_b");
        b.DockerServerOneAtATime = false;
        await session.ConnectAsync(a, NoPhase, CancellationToken.None);
        await session.ConnectAsync(b, NoPhase, CancellationToken.None);

        Assert.Equal(["switch sglang_a", "switch vllm_b"], _docker.Calls);
        Assert.True(_docker.IsRunning("sglang_a"));
        Assert.True(_docker.IsRunning("vllm_b"));
        Assert.Equal("vllm_b", session.DockerInUse);

        b.DockerServerStopOnExit = true;
        Assert.Equal(["sglang_a", "vllm_b"], await session.StopDockerAtExitAsync(b, CancellationToken.None));
        Assert.Equal("clear together", _docker.Calls[^1]);
        Assert.False(_docker.IsRunning("sglang_a"));
        Assert.False(_docker.IsRunning("vllm_b"));
    }

    [WindowsFact]
    public async Task AProfileSwitch_ToAContainerItsListAloneNames_WithOneAtATimeOff_StillStopsTheOldProfiles()
    {
        // Kept running, the old profile's container would be lost track of: the new list never names it.
        using var session = Session();
        await session.ConnectAsync(Docker("sglang_a"), NoPhase, CancellationToken.None);
        var other = new AppSettingsData { DockerServers = true, DockerServerContainers = ["vllm_c"], LlmUrl = "docker:vllm_c", LlmScanMode = "disabled", DockerServerOneAtATime = false };

        Assert.True(await session.ConnectAsync(other, NoPhase, CancellationToken.None));

        Assert.Equal(["switch sglang_a", "clear except vllm_c", "switch vllm_c freed"], _docker.Calls);   // the switch settles, before its start
        Assert.False(_docker.IsRunning("sglang_a"));
    }

    // ── this run's containers, side by side (2026-11-11, the review of the one-local-model change) ───────────────

    private static AppSettingsData SideBySide(AppSettingsData settings)
    {
        settings.DockerServerOneAtATime = false;
        return settings;
    }

    /// <summary>sglang_a and then vllm_b switched to with Docker server one at a time off: both run, vllm_b in use.</summary>
    private async Task<LlmSession> TwoRunningAsync(FakeEmbeddedLlm? embedded = null, bool claudeCode = false)
    {
        _docker.List = new DockerServerList([Running("sglang_a", 30000), Running("vllm_b", 8000)], [], null);
        var session = Session(embedded, claudeCode);
        await session.ConnectAsync(SideBySide(Docker("sglang_a")), NoPhase, CancellationToken.None);
        await session.ConnectAsync(SideBySide(Docker("vllm_b")), NoPhase, CancellationToken.None);
        Assert.Equal(["sglang_a", "vllm_b"], session.DockerRunning);
        _docker.Calls.Clear();
        return session;
    }

    [EmbeddedHereFact]
    public async Task SideBySide_AnEmbeddedLoad_StopsEveryOne_EachLoggedAsLeft_NoneALeftover()
    {
        // #97, #23: both are this run's, so each is "left", never "freed for the embedded model".
        using var session = await TwoRunningAsync(StartLogged(new FakeEmbeddedLlm().Installed("gemma-4-e2b")));
        var infos = await LoggedAsync(NeonSidekick.Diagnostics.DiagnosticLevel.Info,
            async () => Assert.True(await session.ConnectAsync(Embedded(), NoPhase, CancellationToken.None)));

        Assert.Equal(["clear settle", "embedded start"], _docker.Calls);
        Assert.False(_docker.IsRunning("sglang_a"));
        Assert.False(_docker.IsRunning("vllm_b"));
        Assert.Empty(session.DockerRunning);
        Assert.Empty(session.TakeLeftoverStopped());
        Assert.Contains(DockerServerText.Left(["sglang_a"]), infos);
        Assert.Contains(DockerServerText.Left(["vllm_b"]), infos);
        Assert.DoesNotContain(infos, line => line.Contains("to free the GPU", StringComparison.Ordinal));
    }

    [WindowsFact]
    public async Task SideBySide_ALocalUrlOnAKeptContainersPort_SparesIt_TheOneInUseStops()
    {
        // #69: the container that may be the typed server is spared; the one in use stops, the row's own port or not.
        _http.Map("http://127.0.0.1:30000/v1/models", HttpStatusCode.OK, StubHttpMessageHandler.ModelsJson("sglang-model"));
        using var session = await TwoRunningAsync();

        Assert.True(await session.ConnectAsync(Local("http://127.0.0.1:30000/v1"), NoPhase, CancellationToken.None));

        Assert.Equal(["clear", "clear port 30000"], _docker.Calls);
        Assert.True(_docker.IsRunning("sglang_a"));
        Assert.False(_docker.IsRunning("vllm_b"));
        Assert.Null(session.DockerInUse);
        Assert.Equal(["sglang_a"], session.DockerRunning);
    }

    [WindowsFact]
    public async Task SideBySide_AProfileSwitch_SparesTheNewListsContainers_AndTheExitReachesTheKeptOne()
    {
        // #1, #89, #92: sglang_a is off profile B's list and stops; vllm_b is on it and keeps running; the exit stops both of this run's.
        using var session = await TwoRunningAsync();
        var other = SideBySide(new AppSettingsData { DockerServers = true, DockerServerContainers = ["vllm_b", "vllm_c"], LlmUrl = "docker:vllm_c", LlmScanMode = "disabled" });

        Assert.True(await session.ConnectAsync(other, NoPhase, CancellationToken.None));

        Assert.Equal(["clear except vllm_b,vllm_c", "switch vllm_c freed"], _docker.Calls);
        Assert.False(_docker.IsRunning("sglang_a"));
        Assert.True(_docker.IsRunning("vllm_b"));
        Assert.Equal(["vllm_b", "vllm_c"], session.DockerRunning);

        other.DockerServerStopOnExit = true;
        Assert.Equal(["vllm_b", "vllm_c"], await session.StopDockerAtExitAsync(other, CancellationToken.None));
        Assert.Empty(session.DockerRunning);
    }

    [WindowsFact]
    public async Task SideBySide_TheExit_StopsEveryRun_ByTheCallersSettings()
    {
        // #7, #128: every run named, and the stops go by the settings the exit was given (its timeout fits the exit's budget).
        using var session = await TwoRunningAsync();
        var exit = SideBySide(Docker("vllm_b"));
        exit.DockerServerStopOnExit = true;
        exit.DockerServerStopTimeoutSeconds = 7;

        Assert.Equal(["sglang_a", "vllm_b"], await session.StopDockerAtExitAsync(exit, CancellationToken.None));

        Assert.Same(exit, _docker.Requests[^1].Settings);
        Assert.True(_docker.Requests[^1].Together);
    }

    [WindowsFact]
    public async Task SideBySide_OneOffTheList_StopsAlone_LoggedAsSwitchedOff_TheOthersStay()
    {
        // #2, #15, #53: with LLM one local model off, only the unticked one stops, and the log says why.
        _http.Map("http://127.0.0.1:1234/v1/models", HttpStatusCode.OK, StubHttpMessageHandler.ModelsJson("lm"));
        using var session = await TwoRunningAsync();
        var lm = OneLocalOff(SideBySide(Local("http://127.0.0.1:1234/v1")));
        lm.DockerServerContainers = ["vllm_b"];

        var infos = await LoggedAsync(NeonSidekick.Diagnostics.DiagnosticLevel.Info,
            async () => Assert.True(await session.ConnectAsync(lm, NoPhase, CancellationToken.None)));

        Assert.Equal(["clear"], _docker.Calls);
        Assert.False(_docker.IsRunning("sglang_a"));
        Assert.True(_docker.IsRunning("vllm_b"));
        Assert.Equal("vllm_b", session.DockerInUse);
        Assert.Contains(DockerServerText.SwitchedOffLeft(["sglang_a"]), infos);
        Assert.DoesNotContain(DockerServerText.Left(["sglang_a"]), infos);
        Assert.Empty(session.TakeLeftoverStopped());
    }

    [WindowsFact]
    public async Task SideBySide_TheOneInUseStopped_TheExitStillStopsTheOneKeptBesideIt()
    {
        // #14: the one in use unticked and stopped; sglang_a, kept beside it, is still this run's to stop at the exit.
        _http.Map("http://127.0.0.1:1234/v1/models", HttpStatusCode.OK, StubHttpMessageHandler.ModelsJson("lm"));
        using var session = await TwoRunningAsync();
        var lm = OneLocalOff(SideBySide(Local("http://127.0.0.1:1234/v1")));
        lm.DockerServerContainers = ["sglang_a"];
        Assert.True(await session.ConnectAsync(lm, NoPhase, CancellationToken.None));
        Assert.Null(session.DockerInUse);
        Assert.Equal(["sglang_a"], session.DockerRunning);

        lm.DockerServerStopOnExit = true;
        Assert.Equal(["sglang_a"], await session.StopDockerAtExitAsync(lm, CancellationToken.None));
    }

    [WindowsFact]
    public async Task SideBySide_ABot_SharesAKeptContainer_AndNamesEveryOneThisSessionRuns()
    {
        // #16, #25, #86.
        using var session = await TwoRunningAsync();

        var (link, problem) = await session.LinkAsync(new AppSettingsData { LlmUrl = "docker:sglang_a" }, CancellationToken.None);
        Assert.Null(problem);
        Assert.Equal(DockerEndpoint.BaseUrl("sglang_a"), link!.Endpoint.BaseUrl);
        link.Dispose();

        var (other, otherProblem) = await session.LinkAsync(new AppSettingsData { LlmUrl = "docker:vllm_c" }, CancellationToken.None);
        Assert.Null(other);
        Assert.Equal(DockerServerText.BotOtherContainer("vllm_c", ["sglang_a", "vllm_b"]), otherProblem);
    }

    [WindowsFact]
    public async Task AFailedSwitch_LeavesNoLiveUrl_SoABotSharesNothing()
    {
        // #116: the switch stopped sglang_a, then vllm_b failed; neither is shared.
        using var session = Session();
        await session.ConnectAsync(Docker("sglang_a"), NoPhase, CancellationToken.None);
        _docker.Answer = name => DockerSwitch.Failed(DockerServerText.NotReady(name, 900, sideBySide: false), []);
        await session.ConnectAsync(Docker("vllm_b"), NoPhase, CancellationToken.None);

        Assert.Equal(["vllm_b"], session.DockerRunning);   // it may have started
        var (link, problem) = await session.LinkAsync(new AppSettingsData { LlmUrl = "docker:sglang_a" }, CancellationToken.None);
        Assert.Null(link);
        Assert.Equal(DockerServerText.BotNoneRunning, problem);
        var (failed, failedProblem) = await session.LinkAsync(new AppSettingsData { LlmUrl = "docker:vllm_b" }, CancellationToken.None);
        Assert.Null(failed);
        Assert.Equal(DockerServerText.BotNoneRunning, failedProblem);
    }

    [WindowsFact]
    public async Task AContainerKeptBehindAHostedServer_SwitchedOff_StopsAtTheReconnect()
    {
        // #5: KeepsDocker shields the GPU's clear only, never a switch-off.
        using var session = Session(claudeCode: true);
        await session.ConnectAsync(Docker("sglang_a"), NoPhase, CancellationToken.None);
        var claude = Docker("sglang_a");
        claude.LlmUrl = Claude.ClaudeCliEndpoint.BaseUrl.ToString();
        Assert.True(await session.ConnectAsync(claude, NoPhase, CancellationToken.None));
        Assert.Equal(["switch sglang_a"], _docker.Calls);

        claude.DockerServers = false;
        Assert.True(await session.ConnectAsync(claude, NoPhase, CancellationToken.None));
        Assert.Equal(["switch sglang_a", "clear"], _docker.Calls);
        Assert.False(_docker.IsRunning("sglang_a"));
        Assert.Empty(session.DockerRunning);
    }

    [WindowsFact]
    public async Task BackToTheContainerInUse_FromAKeptServer_TheHintRowsServerGoesWhileItSwitches()
    {
        // #24: the hint row's own container decides, not the one in use.
        _http.Map("http://gpu-box:8000/v1/models", HttpStatusCode.OK, StubHttpMessageHandler.ModelsJson("remote"));
        using var session = Session();
        await session.ConnectAsync(Docker("sglang_a"), NoPhase, CancellationToken.None);
        var remote = Docker("sglang_a");
        remote.LlmUrl = "http://gpu-box:8000/v1";
        await session.ConnectAsync(remote, NoPhase, CancellationToken.None);
        Assert.Equal("sglang_a", session.DockerInUse);

        LlmEndpoint? during = new(new Uri("http://x/v1"), "x", "x", "x");
        _docker.Gate = _ =>
        {
            during = session.Endpoint;
            return Task.CompletedTask;
        };
        await session.ConnectAsync(Docker("sglang_a"), NoPhase, CancellationToken.None);

        Assert.Null(during);
    }

    [WindowsFact]
    public async Task TheLocalMemo_IsDropped_WhileOneLocalModelIsOff_SoBackOnItAsksAgain()
    {
        // #32, #84: a memo written while on would otherwise skip the clear after the setting came back.
        _http.Map("http://127.0.0.1:1234/v1/models", HttpStatusCode.OK, StubHttpMessageHandler.ModelsJson("lm"));
        _docker.List = new DockerServerList([Running("sglang_a", 30000)], [], null);
        using var session = Session();
        Assert.True(await session.ConnectAsync(Local("http://127.0.0.1:1234/v1"), NoPhase, CancellationToken.None));
        Assert.Equal(["clear port 1234"], _docker.Calls);
        Assert.True(await session.ConnectAsync(Local("http://127.0.0.1:1234/v1"), NoPhase, CancellationToken.None));
        Assert.Single(_docker.Calls);   // the memo, while on

        Assert.True(await session.ConnectAsync(OneLocalOff(Local("http://127.0.0.1:1234/v1")), NoPhase, CancellationToken.None));
        Assert.True(await session.ConnectAsync(Local("http://127.0.0.1:1234/v1"), NoPhase, CancellationToken.None));

        Assert.Equal(["clear port 1234", "clear port 1234"], _docker.Calls);
    }

    [WindowsFact]
    public async Task ABlankScan_WithOneLocalModelOff_StopsAnUntickedRun_AndHidesAKeptOnesPort()
    {
        // #109: DiscoverAsync's switch-off path; #139: a kept container's port is the container's row, not the scan's.
        _http.Map("http://127.0.0.1:30000/v1/models", HttpStatusCode.OK, StubHttpMessageHandler.ModelsJson("sglang-model"));
        using var session = await TwoRunningAsync();
        var blank = OneLocalOff(SideBySide(Docker("sglang_a")));
        blank.LlmUrl = "";
        blank.LlmScanMode = "local";
        blank.DockerServerContainers = ["sglang_a"];

        var rows = await session.DiscoverAsync(blank, CancellationToken.None);

        Assert.Equal(["clear", "list"], _docker.Calls);
        Assert.False(_docker.IsRunning("vllm_b"));
        Assert.True(_docker.IsRunning("sglang_a"));
        Assert.DoesNotContain(rows, r => r.BaseUrl.AbsoluteUri == "http://127.0.0.1:30000/v1");
        Assert.Contains(rows, r => r.BaseUrl == DockerEndpoint.BaseUrl("sglang_a"));
    }

    [WindowsFact]
    public async Task AServerPick_WithOneLocalModelOff_StopsARunThatDockerServersNoLongerOffers()
    {
        // #48, #52: the /server pick's path (an endpoint already resolved) stops a switched-off run too.
        using var session = Session();
        await session.ConnectAsync(Docker("sglang_a"), NoPhase, CancellationToken.None);
        var off = OneLocalOff(Local("http://127.0.0.1:1234/v1"));
        off.DockerServers = false;
        var picked = LlmEndpointProbe.Endpoint(LlmServer.From(new Uri("http://127.0.0.1:1234/v1"), new ProbeResult(true, ["lm"], "1")), null, null, false);

        var infos = await LoggedAsync(NeonSidekick.Diagnostics.DiagnosticLevel.Info,
            async () => Assert.True(await session.ConnectAsync(off, picked, CancellationToken.None)));

        Assert.Equal(["switch sglang_a", "clear"], _docker.Calls);
        Assert.False(_docker.IsRunning("sglang_a"));
        Assert.Contains(DockerServerText.SwitchedOffLeft(["sglang_a"]), infos);
    }

    [EmbeddedHereFact]
    public async Task AContainerPicked_OffTheEmbeddedModel_TheSwitchIsToldTheGpuWasFreed()
    {
        // #141, #59: the embedded model stops first, so the switch settles before its start.
        var embedded = StartLogged(new FakeEmbeddedLlm().Installed("gemma-4-e2b"));
        using var session = Session(embedded);
        await session.ConnectAsync(Embedded(), NoPhase, CancellationToken.None);
        var container = Docker("sglang_a");
        container.EmbeddedLlmServer = true;

        Assert.True(await session.ConnectAsync(container, NoPhase, CancellationToken.None));

        Assert.Equal("switch sglang_a freed", _docker.Calls[^1]);
        Assert.Equal(1, embedded.Stops);
    }

    [EmbeddedHereFact]
    public async Task AnUntickedRun_StoppedBeforeTheModelAlreadyLoaded_DoesNotSettle()
    {
        // #39: with LLM one local model off the embedded model stayed loaded behind sglang_a; back to it, nothing loads.
        var embedded = StartLogged(new FakeEmbeddedLlm().Installed("gemma-4-e2b"));
        using var session = Session(embedded);
        await session.ConnectAsync(OneLocalOff(Embedded()), NoPhase, CancellationToken.None);
        var container = OneLocalOff(Docker("sglang_a"));
        container.EmbeddedLlmServer = true;
        await session.ConnectAsync(container, NoPhase, CancellationToken.None);
        var back = OneLocalOff(Embedded());
        back.DockerServerContainers = ["vllm_b"];

        Assert.True(await session.ConnectAsync(back, NoPhase, CancellationToken.None));

        Assert.Equal(["clear", "embedded start"], _docker.Calls[^2..]);   // no settle; the start returns the model running
        Assert.False(_docker.IsRunning("sglang_a"));
        Assert.Empty(session.DockerRunning);
    }

    [EmbeddedHereFact]
    public async Task ABotsEmbeddedLoad_WithOneLocalModelOnNow_Clears_ThoughTheConnectWasOff()
    {
        // #104, #19: the loaded profile's live value decides, the other way round from the off-now test.
        _docker.List = new DockerServerList([Running("vllm_b", 8000)], [], null);
        var main = OneLocalOff(Docker("sglang_a"));
        main.EmbeddedLlmServer = true;
        main.DockerServerContainers = ["sglang_a"];
        using var session = Session(new FakeEmbeddedLlm().Installed("gemma-4-e2b"));
        var now = Docker("sglang_a");
        now.DockerServerContainers = ["sglang_a"];
        session.SettingsNow = () => now;
        await session.ConnectAsync(main, NoPhase, CancellationToken.None);

        var (link, problem) = await session.LinkAsync(Embedded(), CancellationToken.None);

        Assert.Null(problem);
        link!.Dispose();
        Assert.Equal(["switch sglang_a", "clear except sglang_a settle"], _docker.Calls);
        Assert.False(_docker.IsRunning("vllm_b"));
    }

    [EmbeddedHereFact]
    public async Task ABotsEmbeddedLoad_WithNothingToClear_AsksNoSettingsNow()
    {
        // #79, #60: a Docker host, but neither the loaded profile nor the bot's offers a container.
        using var session = Session(new FakeEmbeddedLlm().Installed("gemma-4-e2b"));
        int asked = 0;
        session.SettingsNow = () =>
        {
            asked++;
            return new AppSettingsData();
        };
        await session.ConnectAsync(new AppSettingsData { EmbeddedLlmServer = true, LlmScanMode = "disabled" }, NoPhase, CancellationToken.None);

        var (link, problem) = await session.LinkAsync(Embedded(docker: false), CancellationToken.None);

        Assert.Null(problem);
        link!.Dispose();
        Assert.Equal(0, asked);
        Assert.Empty(_docker.Calls);
    }

    /// <summary>A leftover's names compared by sequence: the record's own equality compares the list by reference.</summary>
    private sealed class LeftoverComparer : IEqualityComparer<DockerLeftover?>
    {
        public static readonly LeftoverComparer Instance = new();

        public bool Equals(DockerLeftover? x, DockerLeftover? y) =>
            x is null ? y is null : y is not null && x.Embedded == y.Embedded && x.Names.SequenceEqual(y.Names);

        public int GetHashCode(DockerLeftover? obj) => 0;
    }
}
