using System.Net;
using System.Text;
using NeonSidekick.App;
using NeonSidekick.Docker;
using NeonSidekick.Llm;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// The Docker servers' switch (2026-10-02): DockerLlmPicker's one-at-a-time algorithm plus readiness, over a stateful stub
/// engine — a stop flips a container to exited, a start or unpause to running, every request logged so the order is asserted —
/// the readiness probe a func and the waits on a manual clock; the URL scheme, the inspect parse, the rows and the dedupe.
/// </summary>
public sealed class DockerServerTests : IDisposable
{
    private const string Host = DockerClient.Host;

    private readonly StubHttpMessageHandler _stub = new();
    private readonly ManualTimeProvider _time = new();
    private readonly Engine _engine = new();
    private readonly List<string> _phases = [];
    private readonly List<TimeSpan> _waits = [];
    private readonly List<string> _probed = [];
    private readonly AppSettingsData _settings = new() { DockerServers = true, DockerServerContainers = ["sglang_a", "vllm_b", "paused_c"] };
    private Func<Uri, int, ProbeResult> _answer = (_, _) => new ProbeResult(true, ["model-x"], "1 chat model");
    private readonly DockerServerHost _host;

    public DockerServerTests()
    {
        _engine.Add("sglang_a", "running", "lmsysorg/sglang:latest", 30000);
        _engine.Add("vllm_b", "exited", "vllm/vllm-openai:latest", 8000);
        _engine.Add("paused_c", "paused", "vllm/vllm-openai:latest", 8001);
        _engine.Add("mysql_dev", "running", "mysql:8.4", 3306);
        _stub.Map(Host, (request, _) => Task.FromResult(_engine.Answer(request)));
        var docker = new DockerSession(() => _settings, pipe => new DockerClient(pipe, new HttpClient(_stub)), _time);
        _host = new DockerServerHost(docker, Probe, _time, (span, _) =>
        {
            _waits.Add(span);
            _time.Advance(span);
            return Task.CompletedTask;
        });
    }

    public void Dispose() => _host.Dispose();

    private readonly Dictionary<string, int> _probeCounts = new(StringComparer.Ordinal);

    private Task<ProbeResult> Probe(Uri url, CancellationToken cancellationToken)
    {
        lock (_probed)
        {
            _probed.Add(url.AbsoluteUri);
            _probeCounts[url.AbsoluteUri] = _probeCounts.GetValueOrDefault(url.AbsoluteUri) + 1;
            return Task.FromResult(_answer(url, _probeCounts[url.AbsoluteUri]));
        }
    }

    private Task<DockerSwitch> Switch(string name) => _host.SwitchToAsync(name, _settings, _phases.Add, CancellationToken.None);

    private IReadOnlyList<string> Acts() => _engine.Log.Where(l => l.StartsWith("POST", StringComparison.Ordinal)).ToList();

    // ── the switch ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ASwitch_StopsEveryOtherChosenOne_InOrder_Settles_Starts_AndWaitsForTheApi()
    {
        var result = await Switch("vllm_b");

        Assert.True(result.Ok, result.Error);
        Assert.Equal(new Uri("http://127.0.0.1:8000/v1"), result.LiveUrl);
        Assert.Equal(8000, result.Port);
        Assert.Equal(["model-x"], result.Models.ModelIds);
        Assert.Equal(["sglang_a", "paused_c"], result.Stopped);
        Assert.Equal(
            [
                "GET /version",
                "GET /v1.47/containers/json?all=1",
                "POST /v1.47/containers/id-sglang_a/stop?t=30",
                "GET /v1.47/containers/id-sglang_a/json",
                "POST /v1.47/containers/id-paused_c/unpause",
                "POST /v1.47/containers/id-paused_c/stop?t=30",
                "GET /v1.47/containers/id-paused_c/json",
                "POST /v1.47/containers/id-vllm_b/start",
                "GET /v1.47/containers/id-vllm_b/json",
            ],
            _engine.Log);
        Assert.Equal("running", _engine.State("mysql_dev"));   // not chosen: never touched
        Assert.Equal(["stopping sglang_a", "stopping paused_c", "letting the GPU's memory settle", "starting vllm_b", "loading the model in vllm_b"], _phases);
        Assert.Equal([TimeSpan.FromSeconds(2)], _waits);
        Assert.Equal(["http://127.0.0.1:8000/v1"], _probed);
    }

    [Fact]
    public async Task ATargetAlreadyRunning_WithNoOtherRunning_IsNeitherStoppedNorStarted_NorSettled()
    {
        _engine.Set("paused_c", "exited");
        var result = await Switch("sglang_a");
        Assert.True(result.Ok);
        Assert.Empty(Acts());
        Assert.Empty(_waits);
        Assert.Empty(result.Stopped);
        Assert.Equal(new Uri("http://127.0.0.1:30000/v1"), result.LiveUrl);
    }

    [Fact]
    public async Task APausedTarget_IsUnpaused_NotStarted()
    {
        _engine.Set("sglang_a", "exited");
        var result = await Switch("paused_c");
        Assert.True(result.Ok);
        Assert.Equal(["POST /v1.47/containers/id-paused_c/unpause"], Acts());
    }

    [Fact]
    public async Task AContainerNotChosen_OrNotThere_IsRefused_BeforeAnythingIsPosted()
    {
        Assert.Equal(DockerServerText.NotChosen("mysql_dev"), (await Switch("mysql_dev")).Error);
        Assert.Empty(_engine.Log);

        _settings.DockerServerContainers = ["sglang_a", "ghost"];
        var missing = await Switch("ghost");
        Assert.Equal("There is no Docker container named ghost; untick it on the Docker tab of /settings, or create it again.", missing.Error);
        Assert.Empty(Acts());
    }

    [Fact]
    public async Task AStopTheEngineRefuses_LeavesTheTargetUnstarted()
    {
        _engine.RefuseStop = "id-sglang_a";
        var result = await Switch("vllm_b");
        Assert.False(result.Ok);
        Assert.StartsWith("Could not stop sglang_a, so vllm_b was not started (two models may not fit the GPU): Error: the Docker engine answered 500", result.Error);
        Assert.DoesNotContain(Acts(), a => a.Contains("/start", StringComparison.Ordinal));
        Assert.Equal("exited", _engine.State("vllm_b"));
    }

    [Fact]
    public async Task AContainerThatWillNotStop_TimesOut_AndTheTargetStaysDown()
    {
        _engine.IgnoreStop = "id-sglang_a";
        _settings.DockerServerStopTimeoutSeconds = 5;
        var result = await Switch("vllm_b");
        Assert.False(result.Ok);
        Assert.Contains("sglang_a was still running 20 s after the stop", result.Error);
        Assert.Equal(TimeSpan.FromSeconds(20), _waits.Aggregate(TimeSpan.Zero, (a, b) => a + b));   // polled every 500 ms on the clock, to the cap
        Assert.DoesNotContain(Acts(), a => a.Contains("/start", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AContainerThatExitsWhileLoading_SaysSo_WithItsLastLogLines()
    {
        _answer = (_, _) => ProbeResult.Missing("connection refused");
        _engine.CrashAfterStart = "id-vllm_b";
        var result = await Switch("vllm_b");
        Assert.False(result.Ok);
        Assert.Equal("vllm_b exited (code 1) before its model answered; its last lines: CUDA out of memory", result.Error);
        Assert.Contains("GET /v1.47/containers/id-vllm_b/logs?stdout=1&stderr=1&tail=5", _engine.Log);
    }

    [Fact]
    public async Task AModelThatNeverAnswers_TimesOut_TheContainerLeftRunning()
    {
        _answer = (_, _) => ProbeResult.Missing("503 on /v1/models; not an OpenAI-compatible server");
        _settings.DockerServerReadyTimeoutSeconds = 30;
        var result = await Switch("vllm_b");
        Assert.False(result.Ok);
        Assert.Equal("vllm_b did not answer /v1/models within 30 s; it is left running (raise Docker server ready timeout if its model needs longer).", result.Error);
        Assert.Equal("running", _engine.State("vllm_b"));
        Assert.True(_probed.Count >= 30);
    }

    [Fact]
    public async Task AModelThatLoadsSlowly_IsWaitedFor()
    {
        _answer = (_, n) => n < 4 ? ProbeResult.Missing("connection refused") : new ProbeResult(true, ["slow"], "1 chat model");
        var result = await Switch("vllm_b");
        Assert.True(result.Ok);
        Assert.Equal(4, _probed.Count);
        Assert.Equal(3, _waits.Count(w => w == DockerServerHost.ReadyPoll));
    }

    [Fact]
    public async Task NoPublishedPort_IsRefused_TheContainerLeftRunning()
    {
        _engine.NoPorts = "id-vllm_b";
        var result = await Switch("vllm_b");
        Assert.Equal(DockerServerText.NoPublishedPort("vllm_b"), result.Error);
        Assert.Equal("running", _engine.State("vllm_b"));
    }

    [Fact]
    public async Task OfTwoPorts_TheOneThatAnswersWins()
    {
        _engine.ExtraPort = ("id-vllm_b", 8100);
        _answer = (url, _) => url.Port == 8100 ? new ProbeResult(true, ["m"], "1 chat model") : ProbeResult.Missing("404 on /v1/models; not an OpenAI-compatible server");
        var result = await Switch("vllm_b");
        Assert.Equal(new Uri("http://127.0.0.1:8100/v1"), result.LiveUrl);
    }

    [Fact]
    public async Task StopAll_StopsTheRunningChosenOnes_AndNothingElse()
    {
        var left = await _host.StopAllAsync(_settings, except: null, _phases.Add, CancellationToken.None);
        Assert.Equal(["sglang_a", "paused_c"], left.Stopped);
        Assert.Empty(left.Errors);
        Assert.Equal("running", _engine.State("mysql_dev"));
        Assert.Equal("exited", _engine.State("sglang_a"));
        Assert.DoesNotContain(TimeSpan.FromSeconds(2), _waits);   // no settle: nothing starts after
    }

    [Fact]
    public async Task ACancel_EndsTheSwitch()
    {
        using var cancel = new CancellationTokenSource();
        _answer = (_, _) => { cancel.Cancel(); return ProbeResult.Missing("cancelled"); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _host.SwitchToAsync("vllm_b", _settings, null, cancel.Token));
    }

    [Fact]
    public async Task TheList_IsTheChosenOnes_InTheChosenOrder_WithTheMissingNamed()
    {
        _settings.DockerServerContainers = ["vllm_b", "ghost", "sglang_a"];
        var list = await _host.ListAsync(_settings, CancellationToken.None);
        Assert.Equal(["vllm_b", "sglang_a"], list.Chosen.Select(c => c.Name));
        Assert.Equal(["ghost"], list.Missing);
        var rows = LlmSession.DockerRows(list);
        Assert.Equal(["Docker", "Docker", "Docker"], rows.Select(r => r.Name));
        Assert.Equal(["http://docker.localhost/vllm_b/v1", "http://docker.localhost/sglang_a/v1", "http://docker.localhost/ghost/v1"], rows.Select(r => r.BaseUrl.AbsoluteUri));
        Assert.Equal("exited · vllm/vllm-openai:latest", rows[0].Result.Detail);
        Assert.Equal("running · lmsysorg/sglang:latest · :30000", rows[1].Result.Detail);
        Assert.Equal(DockerServerText.MissingDetail, rows[2].Result.Detail);
        Assert.False(rows[2].Result.Exists);

        // The scan's row on a running chosen container's port goes; another server's stays.
        var scanned = new[] { LlmServer.From(new Uri("http://127.0.0.1:30000/v1"), new ProbeResult(true, ["m"], "1")), LlmServer.From(new Uri("http://127.0.0.1:1234/v1"), new ProbeResult(true, ["m"], "1")) };
        Assert.Equal(["http://127.0.0.1:1234/v1"], LlmSession.WithoutDockerPorts(scanned, list).Select(s => s.BaseUrl.AbsoluteUri));
    }

    // ── the URL scheme, the parse ───────────────────────────────────────────

    [Fact]
    public void TheUrl_NamesTheContainer_TypedOrSaved()
    {
        Assert.Equal("http://docker.localhost/vllm_b/v1", DockerEndpoint.BaseUrl("vllm_b").AbsoluteUri);
        Assert.Equal("vllm_b", DockerEndpoint.ContainerOf(DockerEndpoint.BaseUrl("vllm_b")));
        Assert.Equal("vllm_b", DockerEndpoint.ContainerOf("docker:vllm_b"));
        Assert.Equal("vllm_b", DockerEndpoint.ContainerOf(" DOCKER:vllm_b "));
        Assert.Equal("vllm_b", DockerEndpoint.ContainerOf("http://docker.localhost/vllm_b/v1"));
        Assert.Null(DockerEndpoint.ContainerOf("docker:bad name"));
        Assert.Null(DockerEndpoint.ContainerOf("docker"));
        Assert.Null(DockerEndpoint.ContainerOf("http://127.0.0.1:8000/v1"));
        Assert.True(DockerEndpoint.IsName("a.b-c_9"));
        Assert.False(DockerEndpoint.IsName("_x"));
        Assert.Throws<ArgumentException>(() => DockerEndpoint.BaseUrl("no/slash"));
        Assert.Equal(DockerEndpoint.BaseUrl("vllm_b"), LlmEndpoint.NormalizeBaseUrl("docker:vllm_b"));
        Assert.Throws<ArgumentException>(() => LlmEndpoint.NormalizeBaseUrl("docker:"));
        Assert.Equal("Docker", LlmServer.NameFor(DockerEndpoint.BaseUrl("vllm_b"), null));

        var e = new AppSettingsData { DockerServers = true, DockerServerContainers = [" vllm_b ", "vllm_b", "bad name", "sglang_a"], LlmUrl = "docker:vllm_b" };
        Assert.Equal(["vllm_b", "sglang_a"], DockerEndpoint.ChosenNames(e));
        Assert.Equal(OperatingSystem.IsWindows(), DockerEndpoint.Chosen(e));
        Assert.True(DockerEndpoint.SwitchedOff(new AppSettingsData { DockerServers = false, DockerServerContainers = ["vllm_b"], LlmUrl = "docker:vllm_b" }));
        Assert.True(DockerEndpoint.SwitchedOff(new AppSettingsData { DockerServers = true, DockerServerContainers = ["sglang_a"], LlmUrl = "docker:vllm_b" }));
        Assert.False(DockerEndpoint.Offered(new AppSettingsData { DockerServers = true }));
    }

    [Fact]
    public void TheInspect_GivesTheStateAndTheTcpBindings_TheUrlsMapEveryAddressToLoopback()
    {
        const string json = """
            {"Id":"abc","Name":"/vllm_b","State":{"Status":"running","ExitCode":0},
             "HostConfig":{"NetworkMode":"bridge","PortBindings":{"8000/tcp":[{"HostIp":"","HostPort":"8000"}],"9000/tcp":[{"HostIp":"","HostPort":""}]}},
             "NetworkSettings":{"Ports":{"8000/tcp":[{"HostIp":"0.0.0.0","HostPort":"8000"},{"HostIp":"::","HostPort":"8000"}],"5353/udp":[{"HostIp":"0.0.0.0","HostPort":"5353"}],"7000/tcp":null}}}
            """;
        var inspected = DockerJson.Inspected(json)!;
        Assert.Equal(("abc", "vllm_b", "running", true), (inspected.Id, inspected.Name, inspected.Status, inspected.Active));
        Assert.Equal([new DockerBinding(8000, "0.0.0.0", 8000), new DockerBinding(8000, "::", 8000)], inspected.Published);
        Assert.Equal([new DockerBinding(8000, "", 8000)], inspected.Bound);   // a blank host port is skipped
        Assert.Equal([new Uri("http://127.0.0.1:8000/v1")], DockerServerHost.BaseUrlsOf(inspected));
        var stopped = inspected with { Published = [], Bound = [new DockerBinding(80, "::1", 8080), new DockerBinding(80, "192.168.1.5", 8081)] };
        Assert.Equal(["http://[::1]:8080/v1", "http://192.168.1.5:8081/v1"], DockerServerHost.BaseUrlsOf(stopped).Select(u => u.AbsoluteUri));
        Assert.Null(DockerJson.Inspected("[]"));
        Assert.Equal("docker vllm_b :8000", DockerServerText.Source("vllm_b", 8000));
    }

    /// <summary>A stub engine with state: the container list and each inspect read it, a stop/start/unpause changes it.</summary>
    private sealed class Engine
    {
        private readonly Dictionary<string, (string Name, string State, string Image, int Port)> _containers = new(StringComparer.Ordinal);

        public List<string> Log { get; } = [];

        public string? RefuseStop { get; set; }

        public string? IgnoreStop { get; set; }

        public string? CrashAfterStart { get; set; }

        public string? NoPorts { get; set; }

        public (string Id, int Port)? ExtraPort { get; set; }

        private int _inspectsSinceStart;

        public void Add(string name, string state, string image, int port) => _containers["id-" + name] = (name, state, image, port);

        public void Set(string name, string state) => _containers["id-" + name] = _containers["id-" + name] with { State = state };

        public string State(string name) => _containers["id-" + name].State;

        public HttpResponseMessage Answer(HttpRequestMessage request)
        {
            string path = request.RequestUri!.PathAndQuery;
            lock (Log)
            {
                Log.Add(request.Method.Method + " " + path);
            }

            if (path == "/version")
            {
                return Json("""{"Version":"29.7.2","ApiVersion":"1.55","MinAPIVersion":"1.40","Os":"linux","Arch":"amd64"}""");
            }

            if (path.StartsWith("/v1.47/containers/json", StringComparison.Ordinal))
            {
                return Json("[" + string.Join(",", _containers.Select(c =>
                    "{\"Id\":\"" + c.Key + "\",\"Names\":[\"/" + c.Value.Name + "\"],\"Image\":\"" + c.Value.Image + "\",\"State\":\"" + c.Value.State + "\",\"Status\":\"x\",\"Ports\":[" + (c.Value.State == "running" ? "{\"IP\":\"0.0.0.0\",\"PrivatePort\":" + c.Value.Port + ",\"PublicPort\":" + c.Value.Port + ",\"Type\":\"tcp\"}" : "") + "],\"Labels\":{}}")) + "]");
            }

            string[] parts = request.RequestUri.AbsolutePath.Split('/');   // "", "v1.47", "containers", id, verb
            string id = parts.Length > 3 ? parts[3] : "";
            if (!_containers.TryGetValue(id, out var c))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"message":"No such container"}""") };
            }

            string verb = parts.Length > 4 ? parts[4] : "";
            switch (verb)
            {
                case "stop":
                    if (id == RefuseStop)
                    {
                        return new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("""{"message":"cannot stop"}""") };
                    }

                    if (id != IgnoreStop)
                    {
                        _containers[id] = c with { State = "exited" };
                    }

                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                case "start":
                case "unpause":
                    _containers[id] = c with { State = "running" };
                    _inspectsSinceStart = 0;
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                case "logs":
                    var content = new ByteArrayContent(SmokeChecks.DockerFrame(2, "CUDA out of memory\n"));
                    content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(DockerLogStream.MultiplexedType);
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
                case "json":
                    if (id == CrashAfterStart && c.State == "running" && ++_inspectsSinceStart > 1)
                    {
                        _containers[id] = c = c with { State = "exited" };
                    }

                    string extraPort = ExtraPort is { } extra && extra.Id == id ? ",\"" + extra.Port + "/tcp\":[{\"HostIp\":\"0.0.0.0\",\"HostPort\":\"" + extra.Port + "\"}]" : "";
                    string published = c.State == "running" && id != NoPorts ? "{\"" + c.Port + "/tcp\":[{\"HostIp\":\"0.0.0.0\",\"HostPort\":\"" + c.Port + "\"}]" + extraPort + "}" : "{}";
                    string bound = id == NoPorts ? "{}" : $$"""{"{{c.Port}}/tcp":[{"HostIp":"","HostPort":"{{c.Port}}"}]}""";
                    int exit = c.State == "exited" && id == CrashAfterStart ? 1 : 0;
                    return Json("{\"Id\":\"" + id + "\",\"Name\":\"/" + c.Name + "\",\"State\":{\"Status\":\"" + c.State + "\",\"ExitCode\":" + exit + "},\"HostConfig\":{\"PortBindings\":" + bound + "},\"NetworkSettings\":{\"Ports\":" + published + "}}");
                default:
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
