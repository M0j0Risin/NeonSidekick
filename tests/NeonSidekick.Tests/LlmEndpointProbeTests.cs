using System.Net;
using NeonSidekick.Diagnostics;
using NeonSidekick.Llm;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

public class LlmEndpointProbeTests
{
    private const string LmStudio = "http://127.0.0.1:1234/v1/models";

    private static (StubHttpMessageHandler Stub, LlmEndpointProbe Probe) Probe(TimeSpan? timeout = null)
    {
        var stub = new StubHttpMessageHandler();
        return (stub, new LlmEndpointProbe(new HttpClient(stub), timeout));
    }

    [Fact]
    public async Task Ok_ListsChatModels_SkippingEmbeddingsBlanksAndDuplicates()
    {
        var (stub, probe) = Probe();
        stub.Map(LmStudio, HttpStatusCode.OK, StubHttpMessageHandler.ModelsJson("text-embedding-nomic", "llama-3", "", "llama-3", "qwen"));

        var result = await probe.ProbeAsync(new Uri("http://127.0.0.1:1234"), "empty", CancellationToken.None);

        Assert.True(result.Exists);
        Assert.Equal(new[] { "llama-3", "qwen" }, result.ModelIds);
        Assert.Equal("2 chat models", result.Detail);
        var request = Assert.Single(stub.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(LmStudio, request.Uri.AbsoluteUri);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task KeyProtectedServer_StillExists(HttpStatusCode status)
    {
        var (stub, probe) = Probe();
        stub.Map(LmStudio, status, "{\"error\":\"key\"}");

        var result = await probe.ProbeAsync(new Uri("http://127.0.0.1:1234"), "empty", CancellationToken.None);

        Assert.True(result.Exists);
        Assert.Empty(result.ModelIds);
        Assert.Contains(((int)status).ToString(), result.Detail);
    }

    [Fact]
    public async Task OtherStatus_IsNotAServer()
    {
        var (stub, probe) = Probe();
        stub.Map(LmStudio, HttpStatusCode.NotFound, "nope", "text/plain");
        var result = await probe.ProbeAsync(new Uri("http://127.0.0.1:1234"), "empty", CancellationToken.None);
        Assert.False(result.Exists);
        Assert.Contains("404", result.Detail);
    }

    [Fact]
    public async Task RefusedConnection_IsNotAServer_AndDoesNotThrow()
    {
        var (_, probe) = Probe();
        var result = await probe.ProbeAsync(new Uri("http://127.0.0.1:1234"), "empty", CancellationToken.None);
        Assert.False(result.Exists);
        Assert.Contains("refused", result.Detail);
    }

    [Fact]
    public async Task SlowServer_TimesOutWithinTheBudget()
    {
        var (stub, probe) = Probe(TimeSpan.FromMilliseconds(100));
        stub.Map(LmStudio, async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, StubHttpMessageHandler.ModelsJson("late"));
        });

        var result = await probe.ProbeAsync(new Uri("http://127.0.0.1:1234"), "empty", CancellationToken.None);

        Assert.False(result.Exists);
        Assert.Contains("no answer within 0.1s", result.Detail);
    }

    [Fact]
    public async Task NonJsonBody_ExistsWithNoModels()
    {
        var (stub, probe) = Probe();
        stub.Map(LmStudio, HttpStatusCode.OK, "<html>hi</html>", "text/html");
        var result = await probe.ProbeAsync(new Uri("http://127.0.0.1:1234"), "empty", CancellationToken.None);
        Assert.True(result.Exists);
        Assert.Empty(result.ModelIds);
    }

    [Theory]
    [InlineData("secret", "Bearer secret")]
    [InlineData("  secret  ", "Bearer secret")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public async Task BearerHeader_OnlyWhenTheKeyIsNonBlank(string? key, string? expectedHeader)
    {
        var (stub, probe) = Probe();
        stub.Map(LmStudio, HttpStatusCode.OK, StubHttpMessageHandler.ModelsJson("m"));
        await probe.ProbeAsync(new Uri("http://127.0.0.1:1234"), key, CancellationToken.None);
        Assert.Equal(expectedHeader, Assert.Single(stub.Requests).Authorization);
    }

    [Fact]
    public async Task Discover_PrefersListOrder_EvenWhenALaterCandidateAnswersFirst()
    {
        var (stub, probe) = Probe();
        stub.Map("http://127.0.0.1:8000/v1/models", HttpStatusCode.OK, StubHttpMessageHandler.ModelsJson("vllm-model"));
        stub.Map(LmStudio, async (_, ct) =>
        {
            await Task.Delay(50, ct);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, StubHttpMessageHandler.ModelsJson("lm-model"));
        });

        var endpoint = await probe.DiscoverAsync("empty", ScanScope.Local, CancellationToken.None);

        Assert.NotNull(endpoint);
        Assert.Equal("http://127.0.0.1:1234/v1", endpoint.BaseUrl.AbsoluteUri);
        Assert.Equal("lm-model", endpoint.ModelId);
        Assert.Equal("probed http://127.0.0.1:1234/v1", endpoint.Source);
        Assert.Equal(LlmEndpointProbe.CandidatePorts.Length, stub.Requests.Count); // all probed, in parallel
    }

    [Fact]
    public async Task Discover_NothingListening_ReturnsNull()
    {
        var (stub, probe) = Probe();
        Assert.Null(await probe.DiscoverAsync("empty", ScanScope.Local, CancellationToken.None));
        Assert.Equal(LlmEndpointProbe.CandidatePorts.Length, stub.Requests.Count);
    }

    [Fact]
    public async Task DiscoverAll_ListsEveryResponder_InListOrder_Named()
    {
        var (stub, probe) = Probe();
        stub.Map("http://127.0.0.1:11434/v1/models", HttpStatusCode.OK, ModelsOwnedBy("library", "llama3"));
        stub.Map(LmStudio, async (_, ct) =>
        {
            await Task.Delay(50, ct);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, StubHttpMessageHandler.ModelsJson("lm-a", "lm-b"));
        });

        var servers = await probe.DiscoverAllAsync("empty", extra: null, ScanScope.Local, CancellationToken.None);

        Assert.Equal(new[] { "http://127.0.0.1:1234/v1", "http://127.0.0.1:11434/v1" }, servers.Select(s => s.BaseUrl.AbsoluteUri));
        Assert.Equal(new[] { "LM Studio", "Ollama" }, servers.Select(s => s.Name));
        Assert.Equal(new[] { "lm-a", "lm-b" }, servers[0].Result.ModelIds);
        Assert.Equal("1 chat model", servers[1].Result.Detail);
        Assert.Equal("library", servers[1].Result.OwnedBy);
        Assert.Equal(LlmEndpointProbe.CandidatePorts.Length, stub.Requests.Count);
    }

    [Fact]
    public async Task DiscoverAll_ExtraUrl_IsProbedOnce_AndListedLast()
    {
        var (stub, probe) = Probe();
        stub.Map("http://127.0.0.1:8000/v1/models", HttpStatusCode.OK, StubHttpMessageHandler.ModelsJson("vllm-model"));
        stub.Map("http://10.0.0.5:5000/v1/models", HttpStatusCode.OK, ModelsOwnedBy("sglang", "qwen"));

        var servers = await probe.DiscoverAllAsync("empty", new Uri("http://10.0.0.5:5000"), ScanScope.Local, CancellationToken.None);

        Assert.Equal(new[] { "http://127.0.0.1:8000/v1", "http://10.0.0.5:5000/v1" }, servers.Select(s => s.BaseUrl.AbsoluteUri));
        Assert.Equal(new[] { "vLLM", "SGLang" }, servers.Select(s => s.Name));
        Assert.Equal(LlmEndpointProbe.CandidatePorts.Length + 1, stub.Requests.Count);

        // An extra that is already a candidate is not asked twice.
        stub.Requests.Clear();
        servers = await probe.DiscoverAllAsync("empty", new Uri("http://127.0.0.1:8000/v1/"), ScanScope.Local, CancellationToken.None);
        Assert.Single(servers);
        Assert.Equal(LlmEndpointProbe.CandidatePorts.Length, stub.Requests.Count);
    }

    [Fact]
    public async Task DiscoverAll_NothingListening_IsEmpty()
    {
        var (stub, probe) = Probe();
        Assert.Empty(await probe.DiscoverAllAsync("empty", extra: null, ScanScope.Local, CancellationToken.None));
        Assert.Equal(LlmEndpointProbe.CandidatePorts.Length, stub.Requests.Count);
    }

    [Fact]
    public void Endpoint_BuildsModelKeyAndSource()
    {
        var url = new Uri("http://127.0.0.1:1234/v1");
        var listed = new ProbeResult(true, new[] { "first", "second" }, "2 chat models");
        var dead = ProbeResult.Missing("refused");

        var probed = LlmEndpointProbe.Endpoint(LlmServer.From(url, listed), " key ", configuredModel: null, configured: false);
        Assert.Equal(new LlmEndpoint(url, "first", "key", "probed http://127.0.0.1:1234/v1"), probed);

        var configured = LlmEndpointProbe.Endpoint(LlmServer.From(url, listed), "", "pinned", configured: true);
        Assert.Equal(new LlmEndpoint(url, "pinned", LlmEndpoint.DefaultApiKey, "configured"), configured);

        var firstListed = LlmEndpointProbe.Endpoint(LlmServer.From(url, listed), "", null, configured: true);
        Assert.Equal(new LlmEndpoint(url, "first", LlmEndpoint.DefaultApiKey, "first listed"), firstListed);

        var notAnswering = LlmEndpointProbe.Endpoint(LlmServer.From(url, dead), null, null, configured: true);
        Assert.Equal(new LlmEndpoint(url, LlmEndpoint.FallbackModelId, LlmEndpoint.DefaultApiKey, "configured, not answering"), notAnswering);
    }

    [Theory]
    [InlineData("http://127.0.0.1:1234/v1", null, "LM Studio")]
    [InlineData("http://127.0.0.1:8000/v1", null, "vLLM")]
    [InlineData("http://127.0.0.1:30000/v1", null, "SGLang")]
    [InlineData("http://127.0.0.1:8080/v1", null, "llama.cpp")]
    [InlineData("http://127.0.0.1:11434/v1", null, "Ollama")]
    [InlineData("http://127.0.0.1:8888/v1", null, "Unsloth")]
    [InlineData("http://127.0.0.1:8000/v1", "llamacpp", "llama.cpp")]       // what the server says wins over the port
    [InlineData("http://127.0.0.1:8000/v1", " Organization_Owner ", "LM Studio")]
    [InlineData("http://127.0.0.1:8000/v1", "somebody", "vLLM")]            // an unknown owner falls back to the port
    [InlineData("http://10.0.0.5:5000/v1", null, "10.0.0.5:5000")]
    [InlineData("http://10.0.0.5:5000/v1", "sglang", "SGLang")]
    public void ServerName_IsPinned(string url, string? ownedBy, string expected)
    {
        Assert.Equal(expected, LlmServer.NameFor(new Uri(url), ownedBy));
    }

    private static string ModelsOwnedBy(string owner, params string[] ids) =>
        "{\"object\":\"list\",\"data\":[" + string.Join(",", ids.Select(id => $"{{\"id\":\"{id}\",\"object\":\"model\",\"owned_by\":\"{owner}\"}}")) + "]}";

    [Fact]
    public void Candidates_AreIpv4Loopback_InPreferenceOrder()
    {
        Assert.Equal(new[] { 1234, 8000, 30000, 8080, 11434, 8888 }, LlmEndpointProbe.CandidatePorts);
        Assert.All(LlmEndpointProbe.CandidateBaseUrls, u => Assert.Equal("127.0.0.1", u.Host));
        Assert.Equal("1234, 8000, 30000, 8080, 11434, 8888", LlmEndpointProbe.CandidatePortList);
        Assert.Equal(TimeSpan.FromSeconds(3), LlmEndpointProbe.DefaultTimeout);
    }

    [Fact]
    public async Task Resolve_ConfiguredUrl_ProbesOnlyThatUrl_AndTakesItsFirstModel()
    {
        var (stub, probe) = Probe();
        stub.Map("http://myhost:5000/v1/models", HttpStatusCode.OK, StubHttpMessageHandler.ModelsJson("embed-1", "served"));

        var endpoint = await probe.ResolveAsync(new AppSettingsData { LlmUrl = "http://myhost:5000", LlmApiKey = "k" }, CancellationToken.None);

        Assert.NotNull(endpoint);
        Assert.Equal("http://myhost:5000/v1", endpoint.BaseUrl.AbsoluteUri);
        Assert.Equal("served", endpoint.ModelId);
        Assert.Equal("k", endpoint.ApiKey);
        Assert.Equal("first listed", endpoint.Source);
        Assert.All(stub.Requests, r => Assert.Equal("myhost", r.Uri.Host));
    }

    [Fact]
    public async Task Resolve_ConfiguredButDeadUrl_IsStillReturned_WithAWarning()
    {
        var (stub, probe) = Probe();
        var host = "dead-" + Guid.NewGuid().ToString("N");
        var warnings = new List<DiagnosticEvent>();
        Action<DiagnosticEvent> handler = e => { if (e.Message.Contains(host, StringComparison.Ordinal)) lock (warnings) warnings.Add(e); };
        DiagnosticLog.Emitted += handler;
        try
        {
            var endpoint = await probe.ResolveAsync(new AppSettingsData { LlmUrl = $"http://{host}:9" }, CancellationToken.None);

            Assert.NotNull(endpoint);
            Assert.Equal($"http://{host}:9/v1", endpoint.BaseUrl.AbsoluteUri);
            Assert.Equal(LlmEndpoint.FallbackModelId, endpoint.ModelId);
            Assert.Equal("configured, not answering", endpoint.Source);
            Assert.Single(stub.Requests);
            var warning = Assert.Single(warnings);
            Assert.Equal(DiagnosticLevel.Warning, warning.Level);
            Assert.Equal("Llm", warning.Category);
        }
        finally
        {
            DiagnosticLog.Emitted -= handler;
        }
    }

    [Fact]
    public async Task Resolve_ConfiguredModel_WinsOverTheListedOne()
    {
        var (stub, probe) = Probe();
        stub.Map(LmStudio, HttpStatusCode.OK, StubHttpMessageHandler.ModelsJson("listed"));

        var discovered = await probe.ResolveAsync(new AppSettingsData { LlmModel = " mine " }, CancellationToken.None);
        var configured = await probe.ResolveAsync(new AppSettingsData { LlmUrl = "http://127.0.0.1:1234", LlmModel = "mine" }, CancellationToken.None);

        Assert.Equal("mine", discovered!.ModelId);
        Assert.Equal("mine", configured!.ModelId);
    }

    [Fact]
    public async Task Resolve_BlankKey_BecomesEmpty()
    {
        var (stub, probe) = Probe();
        stub.Map(LmStudio, HttpStatusCode.OK, StubHttpMessageHandler.ModelsJson("m"));
        var endpoint = await probe.ResolveAsync(new AppSettingsData { LlmApiKey = "  " }, CancellationToken.None);
        Assert.Equal("empty", endpoint!.ApiKey);
    }

    [Fact]
    public async Task Resolve_AnEncryptedKey_IsSentDecrypted()
    {
        // 2026-09-28: the profile keeps the LLM API key DPAPI-encrypted; the server gets the key itself, configured or discovered.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string stored = NeonSidekick.Settings.SettingsSecrets.Protect("sk-real", out _);
        var (stub, probe) = Probe();
        stub.Map(LmStudio, HttpStatusCode.OK, StubHttpMessageHandler.ModelsJson("m"));
        stub.Map("http://myhost:5000/v1/models", HttpStatusCode.OK, StubHttpMessageHandler.ModelsJson("m"));

        var discovered = await probe.ResolveAsync(new AppSettingsData { LlmApiKey = stored }, CancellationToken.None);
        var configured = await probe.ResolveAsync(new AppSettingsData { LlmUrl = "http://myhost:5000", LlmApiKey = stored }, CancellationToken.None);

        Assert.Equal("sk-real", discovered!.ApiKey);
        Assert.Equal("sk-real", configured!.ApiKey);
    }

    [Fact]
    public async Task Resolve_UnusableConfiguredUrl_ReturnsNull_WithoutProbing()
    {
        var (stub, probe) = Probe();
        Assert.Null(await probe.ResolveAsync(new AppSettingsData { LlmUrl = "localhost:1234" }, CancellationToken.None));
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public void ParseChatModelIds_ToleratesEveryShape()
    {
        Assert.Empty(LlmEndpointProbe.ParseChatModelIds(""));
        Assert.Empty(LlmEndpointProbe.ParseChatModelIds("not json"));
        Assert.Empty(LlmEndpointProbe.ParseChatModelIds("{\"data\":\"x\"}"));
        Assert.Empty(LlmEndpointProbe.ParseChatModelIds("{\"data\":[1,{\"id\":5},{\"name\":\"x\"}]}"));
        Assert.Equal(new[] { "a" }, LlmEndpointProbe.ParseChatModelIds("{\"data\":[{\"id\":\"a\"},{\"id\":\"my-EMBED-model\"}]}"));
    }

    [Fact]
    public void ParseModels_ReadsTheFirstOwner_EmbeddingsIncluded()
    {
        Assert.Null(LlmEndpointProbe.ParseModels("").OwnedBy);
        Assert.Null(LlmEndpointProbe.ParseModels("{\"data\":[{\"id\":\"a\"}]}").OwnedBy);
        Assert.Null(LlmEndpointProbe.ParseModels("{\"data\":[{\"id\":\"a\",\"owned_by\":\" \"}]}").OwnedBy);
        Assert.Null(LlmEndpointProbe.ParseModels("{\"data\":[{\"id\":\"a\",\"owned_by\":7}]}").OwnedBy);

        var (ids, ownedBy) = LlmEndpointProbe.ParseModels("{\"data\":[{\"id\":\"my-embed\",\"owned_by\":\"vllm\"},{\"id\":\"a\",\"owned_by\":\"other\"}]}");
        Assert.Equal(new[] { "a" }, ids);
        Assert.Equal("vllm", ownedBy);
    }

    // ── The scan scope ──────────────────────────────────────────────────────

    private static LanHosts TwoHosts() =>
        new(new[] { IPAddress.Parse("10.0.0.5"), IPAddress.Parse("10.0.0.6") }, new[] { "10.0.0.0/24" });

    [Fact]
    public async Task DiscoverAll_Remote_ProbesEveryPortOnEveryNetworkHost_HostMajor_NeverThisMachine()
    {
        var stub = new StubHttpMessageHandler();
        var probe = new LlmEndpointProbe(new HttpClient(stub), TimeSpan.FromMilliseconds(500), TwoHosts);
        stub.Map("http://10.0.0.6:8000/v1/models", HttpStatusCode.OK, ModelsOwnedBy("vllm", "qwen"));
        stub.Map(LmStudio, HttpStatusCode.OK, StubHttpMessageHandler.ModelsJson("lm-model"));   // this machine: not asked

        var servers = await probe.DiscoverAllAsync("empty", extra: null, ScanScope.Remote, CancellationToken.None);

        var server = Assert.Single(servers);
        Assert.Equal("http://10.0.0.6:8000/v1", server.BaseUrl.AbsoluteUri);
        Assert.Equal("vLLM", server.Name);
        Assert.Equal(2 * LlmEndpointProbe.CandidatePorts.Length, stub.Requests.Count);
        Assert.DoesNotContain(stub.Requests, r => r.Uri.Host == "127.0.0.1");
        // Host-major: the six ports of .5, then the six of .6, each in port order.
        Assert.Equal(
            new[] { "10.0.0.5:1234", "10.0.0.5:8000", "10.0.0.5:30000", "10.0.0.5:8080", "10.0.0.5:11434", "10.0.0.5:8888", "10.0.0.6:1234", "10.0.0.6:8000", "10.0.0.6:30000", "10.0.0.6:8080", "10.0.0.6:11434", "10.0.0.6:8888" },
            stub.Requests.Select(r => r.Uri.Authority));
    }

    [Fact]
    public async Task DiscoverAll_Both_ThisMachineFirst_ThenTheNetwork_TheExtraLast()
    {
        var stub = new StubHttpMessageHandler();
        var probe = new LlmEndpointProbe(new HttpClient(stub), TimeSpan.FromMilliseconds(500), TwoHosts);
        stub.Map("http://10.0.0.5:1234/v1/models", HttpStatusCode.OK, StubHttpMessageHandler.ModelsJson("remote-lm"));
        stub.Map("http://127.0.0.1:11434/v1/models", HttpStatusCode.OK, ModelsOwnedBy("library", "llama3"));
        stub.Map("http://10.0.0.9:5000/v1/models", HttpStatusCode.OK, ModelsOwnedBy("sglang", "qwen"));

        var servers = await probe.DiscoverAllAsync("empty", new Uri("http://10.0.0.9:5000"), ScanScope.Both, CancellationToken.None);

        Assert.Equal(new[] { "http://127.0.0.1:11434/v1", "http://10.0.0.5:1234/v1", "http://10.0.0.9:5000/v1" }, servers.Select(s => s.BaseUrl.AbsoluteUri));
        Assert.Equal(new[] { "Ollama", "LM Studio", "SGLang" }, servers.Select(s => s.Name));
        Assert.Equal(3 * LlmEndpointProbe.CandidatePorts.Length + 1, stub.Requests.Count);
    }

    [Theory]
    [InlineData("http://127.0.0.1:30000/v1")]
    [InlineData("http://localhost:30000")]
    [InlineData("http://[::1]:30000/v1")]
    public async Task DiscoverAll_Remote_DropsALoopbackExtra_TheScopeWins(string extra)
    {
        // The endpoint in use is this machine's SGLang; under remote it is neither probed nor listed.
        var stub = new StubHttpMessageHandler();
        var probe = new LlmEndpointProbe(new HttpClient(stub), TimeSpan.FromMilliseconds(500), TwoHosts);
        stub.Map("http://127.0.0.1:30000/v1/models", HttpStatusCode.OK, ModelsOwnedBy("sglang", "qwen"));
        stub.Map("http://localhost:30000/v1/models", HttpStatusCode.OK, ModelsOwnedBy("sglang", "qwen"));
        stub.Map("http://[::1]:30000/v1/models", HttpStatusCode.OK, ModelsOwnedBy("sglang", "qwen"));
        stub.Map("http://10.0.0.5:1234/v1/models", HttpStatusCode.OK, StubHttpMessageHandler.ModelsJson("remote-lm"));

        var servers = await probe.DiscoverAllAsync("empty", new Uri(extra), ScanScope.Remote, CancellationToken.None);

        var server = Assert.Single(servers);
        Assert.Equal("http://10.0.0.5:1234/v1", server.BaseUrl.AbsoluteUri);
        Assert.Equal(2 * LlmEndpointProbe.CandidatePorts.Length, stub.Requests.Count);
        Assert.DoesNotContain(stub.Requests, r => r.Uri.IsLoopback);

        // Under both the same extra is a candidate already: asked once, listed first.
        stub.Requests.Clear();
        servers = await probe.DiscoverAllAsync("empty", new Uri("http://127.0.0.1:30000/v1"), ScanScope.Both, CancellationToken.None);
        Assert.Equal(new[] { "http://127.0.0.1:30000/v1", "http://10.0.0.5:1234/v1" }, servers.Select(s => s.BaseUrl.AbsoluteUri));
        Assert.Equal(3 * LlmEndpointProbe.CandidatePorts.Length, stub.Requests.Count);
    }

    [Fact]
    public async Task DiscoverAll_Remote_KeepsAnExtraOnAnotherHost()
    {
        var stub = new StubHttpMessageHandler();
        var probe = new LlmEndpointProbe(new HttpClient(stub), TimeSpan.FromMilliseconds(500), TwoHosts);
        stub.Map("http://10.0.0.9:5000/v1/models", HttpStatusCode.OK, ModelsOwnedBy("sglang", "qwen"));

        var servers = await probe.DiscoverAllAsync("empty", new Uri("http://10.0.0.9:5000"), ScanScope.Remote, CancellationToken.None);

        var server = Assert.Single(servers);
        Assert.Equal("http://10.0.0.9:5000/v1", server.BaseUrl.AbsoluteUri);
        Assert.Equal(2 * LlmEndpointProbe.CandidatePorts.Length + 1, stub.Requests.Count);
    }

    [Fact]
    public async Task DiscoverAll_Local_NeverReadsTheNetwork()
    {
        int asked = 0;
        var stub = new StubHttpMessageHandler();
        var probe = new LlmEndpointProbe(new HttpClient(stub), TimeSpan.FromMilliseconds(500), () => { asked++; return TwoHosts(); });

        Assert.Empty(await probe.DiscoverAllAsync("empty", extra: null, ScanScope.Local, CancellationToken.None));

        Assert.Equal(0, asked);
        Assert.Equal(LlmEndpointProbe.CandidatePorts.Length, stub.Requests.Count);
        Assert.All(stub.Requests, r => Assert.Equal("127.0.0.1", r.Uri.Host));
    }

    [Fact]
    public void Candidates_Disabled_IsEmpty_AndNeverReadsTheNetwork()
    {
        int asked = 0;
        var probe = new LlmEndpointProbe(new HttpClient(new StubHttpMessageHandler()), TimeSpan.FromMilliseconds(500), () => { asked++; return TwoHosts(); });

        var (urls, network) = probe.Candidates(ScanScope.Disabled);

        Assert.Empty(urls);
        Assert.Same(LanHosts.None, network);
        Assert.Equal(0, asked);
    }

    [Fact]
    public async Task DiscoverAll_Disabled_AsksNothing_NotEvenTheExtra()
    {
        // Disabled entirely (2026-09-15): the endpoint in use a bare /server hands over as `extra` is not probed either, and no log line is left.
        var stub = new StubHttpMessageHandler();
        var probe = new LlmEndpointProbe(new HttpClient(stub), TimeSpan.FromMilliseconds(500), TwoHosts);
        stub.Map(LmStudio, HttpStatusCode.OK, StubHttpMessageHandler.ModelsJson("lm-model"));
        var events = new List<DiagnosticEvent>();
        Action<DiagnosticEvent> capture = e => { if (e.Category == "Llm") events.Add(e); };
        DiagnosticLog.Emitted += capture;
        try
        {
            Assert.Empty(await probe.DiscoverAllAsync("empty", extra: new Uri("http://127.0.0.1:1234"), ScanScope.Disabled, CancellationToken.None));
        }
        finally
        {
            DiagnosticLog.Emitted -= capture;
        }

        Assert.Empty(stub.Requests);
        Assert.Empty(events);
    }

    [Fact]
    public async Task Resolve_BlankUrl_Disabled_ReturnsNull_WithNoRequest()
    {
        var stub = new StubHttpMessageHandler();
        var probe = new LlmEndpointProbe(new HttpClient(stub), TimeSpan.FromMilliseconds(500), TwoHosts);
        stub.Map(LmStudio, HttpStatusCode.OK, StubHttpMessageHandler.ModelsJson("lm-model"));

        Assert.Null(await probe.ResolveAsync(new AppSettingsData { LlmScanMode = "disabled" }, CancellationToken.None));
        Assert.Empty(stub.Requests);

        // A configured URL is an instruction whatever the scan mode says: probed once and used.
        var configured = await probe.ResolveAsync(new AppSettingsData { LlmScanMode = "disabled", LlmUrl = "http://127.0.0.1:1234" }, CancellationToken.None);
        Assert.NotNull(configured);
        Assert.Equal("lm-model", configured.ModelId);
        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task DiscoverAll_Remote_WithNoNetwork_AsksNothing()
    {
        var stub = new StubHttpMessageHandler();
        var probe = new LlmEndpointProbe(new HttpClient(stub), TimeSpan.FromMilliseconds(500), () => LanHosts.None);

        Assert.Empty(await probe.DiscoverAllAsync("empty", extra: null, ScanScope.Remote, CancellationToken.None));
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task DiscoverAll_NetworkScan_LogsOneSummary_NotAMissPerHost()
    {
        var stub = new StubHttpMessageHandler();
        var probe = new LlmEndpointProbe(new HttpClient(stub), TimeSpan.FromMilliseconds(500), TwoHosts);
        stub.Map("http://10.0.0.6:8000/v1/models", HttpStatusCode.OK, ModelsOwnedBy("vllm", "qwen"));
        var events = new List<DiagnosticEvent>();
        Action<DiagnosticEvent> capture = e => { if (e.Category == "Llm") events.Add(e); };
        DiagnosticLog.Emitted += capture;
        try
        {
            await probe.DiscoverAllAsync("empty", extra: null, ScanScope.Both, CancellationToken.None);
        }
        finally
        {
            DiagnosticLog.Emitted -= capture;
        }

        Assert.Contains(events, e => e.Level == DiagnosticLevel.Info && e.Message == "Scanned 2 hosts on 10.0.0.0/24 (ports 1234, 8000, 30000, 8080, 11434, 8888): 1 answered.");
        Assert.Contains(events, e => e.Level == DiagnosticLevel.Info && e.Message.StartsWith("Found an OpenAI-compatible server at http://10.0.0.6:8000/v1", StringComparison.Ordinal));
        Assert.DoesNotContain(events, e => e.Message.StartsWith("http://10.0.0.", StringComparison.Ordinal));   // no per-host miss
        Assert.Equal(LlmEndpointProbe.CandidatePorts.Length, events.Count(e => e.Level == DiagnosticLevel.Debug && e.Message.StartsWith("http://127.0.0.1:", StringComparison.Ordinal)));
        Assert.Equal("Scanned 0 hosts on no local network (ports 1234, 8000, 30000, 8080, 11434, 8888): 0 answered.", LlmEndpointProbe.ScanSummary(LanHosts.None, 0));
    }

    [Fact]
    public async Task Resolve_BlankUrl_FollowsTheSavedScanMode()
    {
        var stub = new StubHttpMessageHandler();
        var probe = new LlmEndpointProbe(new HttpClient(stub), TimeSpan.FromMilliseconds(500), TwoHosts);
        stub.Map("http://10.0.0.5:1234/v1/models", HttpStatusCode.OK, StubHttpMessageHandler.ModelsJson("remote-lm"));
        stub.Map(LmStudio, HttpStatusCode.OK, StubHttpMessageHandler.ModelsJson("lm-model"));

        var remote = await probe.ResolveAsync(new AppSettingsData { LlmScanMode = "remote" }, CancellationToken.None);
        Assert.NotNull(remote);
        Assert.Equal("http://10.0.0.5:1234/v1", remote.BaseUrl.AbsoluteUri);
        Assert.Equal("remote-lm", remote.ModelId);

        var local = await probe.ResolveAsync(new AppSettingsData(), CancellationToken.None);
        Assert.NotNull(local);
        Assert.Equal("http://127.0.0.1:1234/v1", local.BaseUrl.AbsoluteUri);

        var both = await probe.ResolveAsync(new AppSettingsData { LlmScanMode = "both" }, CancellationToken.None);
        Assert.NotNull(both);
        Assert.Equal("http://127.0.0.1:1234/v1", both.BaseUrl.AbsoluteUri);   // this machine first
    }

    [Fact]
    public void CandidateUrl_IsTheHostAndPortUnderV1()
    {
        Assert.Equal("http://10.0.0.5:8000/v1", LlmEndpointProbe.CandidateUrl(IPAddress.Parse("10.0.0.5"), 8000).AbsoluteUri);
    }
}
