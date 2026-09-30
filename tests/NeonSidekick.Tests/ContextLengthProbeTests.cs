using System.Net;
using NeonSidekick.Llm;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

public class ContextLengthProbeTests
{
    private const string Root = "http://127.0.0.1:1234";
    private static readonly Uri Base = new(Root + "/v1");

    /// <summary>The live SGLang answer of 2026-09-13, verbatim.</summary>
    private const string SgLangModels =
        "{\"object\":\"list\",\"data\":[{\"id\":\"gittensor-model-hub/Qwen3.8-27B-NVFP4-RTX5090\",\"object\":\"model\",\"created\":1789338333,\"owned_by\":\"sglang\",\"root\":\"gittensor-model-hub/Qwen3.8-27B-NVFP4-RTX5090\",\"parent\":null,\"max_model_len\":151427}]}";

    /// <summary>LM Studio's native list: a loaded model with both figures, an unloaded one with the ceiling alone.</summary>
    private const string LmStudioNative =
        "{\"object\":\"list\",\"data\":[" +
        "{\"id\":\"q6-model\",\"object\":\"model\",\"state\":\"loaded\",\"max_context_length\":262144,\"loaded_context_length\":174080}," +
        "{\"id\":\"other\",\"object\":\"model\",\"state\":\"not-loaded\",\"max_context_length\":40960}]}";

    private const string LlamaProps = "{\"default_generation_settings\":{\"n_ctx\":8192,\"n_predict\":-1},\"total_slots\":1,\"model_path\":\"/m.gguf\"}";

    private const string OllamaPs = "{\"models\":[{\"name\":\"llama3:latest\",\"model\":\"llama3:latest\",\"size\":1,\"context_length\":4096}]}";

    private const string OllamaShowNumCtx =
        "{\"license\":\"x\",\"parameters\":\"num_ctx                        16384\\nstop                           \\\"<|eot_id|>\\\"\",\"model_info\":{\"general.architecture\":\"llama\",\"llama.context_length\":131072}}";

    private const string OllamaShowCeiling =
        "{\"parameters\":\"stop                           \\\"<|eot_id|>\\\"\",\"model_info\":{\"general.architecture\":\"llama\",\"llama.context_length\":131072}}";

    private static (StubHttpMessageHandler Stub, ContextLengthProbe Probe) Make(TimeSpan? timeout = null)
    {
        var stub = new StubHttpMessageHandler();
        return (stub, new ContextLengthProbe(new HttpClient(stub), timeout ?? TimeSpan.FromMilliseconds(500)));
    }

    private static Task<ContextLength?> Detect(ContextLengthProbe probe, string modelId, string? apiKey = null) =>
        probe.DetectAsync(Base, modelId, apiKey, CancellationToken.None);

    [Fact]
    public async Task Tier1_IsTheEndpointProbes_TheListReadOnce_TheWindowOnTheEndpoint()
    {
        var (stub, _) = Make();
        stub.Map(Root + "/v1/models", HttpStatusCode.OK, SgLangModels);
        var endpointProbe = new LlmEndpointProbe(new HttpClient(stub), TimeSpan.FromMilliseconds(500));

        var result = await endpointProbe.ProbeAsync(Base, null, CancellationToken.None);
        Assert.Equal(SgLangModels, result.ModelsJson);

        var endpoint = LlmEndpointProbe.Endpoint(LlmServer.From(Base, result), null, null, configured: false);
        Assert.Equal(new ContextLength(151_427, "max_model_len on /v1/models"), endpoint.PublishedContextLength);
        Assert.Single(stub.Requests);

        // A configured id the list does not know still takes the one window on offer (an alias of the single loaded model); a dead server: no list at all.
        Assert.Equal(151_427, LlmEndpointProbe.Endpoint(LlmServer.From(Base, result), null, "other", configured: true).PublishedContextLength!.Value.Tokens);
        Assert.Null(LlmEndpointProbe.Endpoint(LlmServer.From(Base, ProbeResult.Missing("down")), null, "m", configured: true).PublishedContextLength);

        // ResolveAsync with a configured model looks the window up for that model, not the first listed.
        var two = new StubHttpMessageHandler().Map(Root + "/v1/models", HttpStatusCode.OK, "{\"data\":[{\"id\":\"a\",\"max_model_len\":1000},{\"id\":\"b\",\"max_model_len\":2000}]}");
        var resolved = await new LlmEndpointProbe(new HttpClient(two), TimeSpan.FromMilliseconds(500)).ResolveAsync(new NeonSidekick.Settings.AppSettingsData { LlmModel = "b", LlmScanMode = "local" }, CancellationToken.None);
        Assert.Equal("b", resolved!.ModelId);
        Assert.Equal(2000, resolved.PublishedContextLength!.Value.Tokens);
        Assert.Equal("probed http://127.0.0.1:1234/v1", resolved.Source);
    }

    [Fact]
    public void Tier1_PrefersContextLength_OverMaxModelLen()
    {
        string json = "{\"data\":[{\"id\":\"m\",\"context_length\":32000,\"max_model_len\":16000}]}";
        Assert.Equal(new ContextLength(32_000, "context_length on /v1/models"), ContextLengthProbe.ParseModelsWindow(json, "m"));
    }

    [Fact]
    public void Tier1_MatchesExactId_ThenASuffix_ThenAnyEntryWithAWindow()
    {
        string json = "{\"data\":[{\"id\":\"plain\",\"max_model_len\":1000},{\"id\":\"Qwen3\",\"max_model_len\":2000},{\"id\":\"exact\",\"max_model_len\":3000}]}";
        Assert.Equal(3000, ContextLengthProbe.ParseModelsWindow(json, "exact")!.Value.Tokens);
        Assert.Equal(2000, ContextLengthProbe.ParseModelsWindow(json, "org/Qwen3")!.Value.Tokens);
        Assert.Equal(1000, ContextLengthProbe.ParseModelsWindow(json, "unknown")!.Value.Tokens);

        // An entry without a window never matches, whatever its id.
        Assert.Null(ContextLengthProbe.ParseModelsWindow("{\"data\":[{\"id\":\"exact\",\"object\":\"model\"}]}", "exact"));
        Assert.Null(ContextLengthProbe.ParseModelsWindow("{\"data\":[{\"id\":\"exact\",\"max_model_len\":0}]}", "exact"));
        Assert.Null(ContextLengthProbe.ParseModelsWindow("{\"data\":[{\"id\":\"exact\",\"max_model_len\":\"8k\"}]}", "exact"));
        Assert.Null(ContextLengthProbe.ParseModelsWindow("not json", "exact"));
        Assert.Null(ContextLengthProbe.ParseModelsWindow("[]", "exact"));
    }

    [Fact]
    public async Task Tier2_LmStudio_LoadedWindowWins_TheCeilingWhenNotLoaded()
    {
        var (stub, probe) = Make();
        stub.Map(Root + "/api/v0/models", HttpStatusCode.OK, LmStudioNative);

        Assert.Equal(new ContextLength(174_080, "loaded_context_length on /api/v0/models"), await Detect(probe, "q6-model"));
        Assert.Equal(new ContextLength(40_960, "max_context_length on /api/v0/models"), await Detect(probe, "other"));
        Assert.All(stub.Requests, r => Assert.Equal(Root + "/api/v0/models", r.Uri.AbsoluteUri));   // the first tier answered both times
    }

    [Fact]
    public async Task Tier3_LlamaCpp_ReadsNCtxFromProps()
    {
        var (stub, probe) = Make();
        stub.Map(Root + "/api/v0/models", HttpStatusCode.NotFound, "{}");
        stub.Map(Root + "/props", HttpStatusCode.OK, LlamaProps);

        Assert.Equal(new ContextLength(8_192, "n_ctx on /props"), await Detect(probe, "/m.gguf"));
        Assert.Equal(2, stub.Requests.Count);
    }

    [Fact]
    public async Task Tier4_Ollama_PsFirst_ThenShow()
    {
        var (stub, probe) = Make();
        stub.Map(Root + "/api/ps", HttpStatusCode.OK, OllamaPs);
        stub.Map(Root + "/api/show", HttpStatusCode.OK, OllamaShowNumCtx);

        Assert.Equal(new ContextLength(4_096, "context_length on /api/ps"), await Detect(probe, "llama3:latest"));
        Assert.Equal(3, stub.Requests.Count);   // api/v0/models (refused), props (refused), ps
        Assert.DoesNotContain(stub.Requests, r => r.Uri.AbsolutePath == "/api/show");

        // Nothing loaded yet: /api/show, a POST naming the model.
        var (stub2, probe2) = Make();
        stub2.Map(Root + "/api/ps", HttpStatusCode.OK, "{\"models\":[]}");
        stub2.Map(Root + "/api/show", HttpStatusCode.OK, OllamaShowNumCtx);
        Assert.Equal(new ContextLength(16_384, "num_ctx on /api/show"), await Detect(probe2, "llama3:latest"));
        var show = stub2.Requests.Single(r => r.Uri.AbsolutePath == "/api/show");
        Assert.Equal(HttpMethod.Post, show.Method);
        Assert.Equal("{\"model\":\"llama3:latest\"}", show.Body);
    }

    [Fact]
    public void Tier4_ShowFallsBackToTheModelCeiling()
    {
        Assert.Equal(new ContextLength(131_072, "context_length on /api/show"), ContextLengthProbe.ParseOllamaShow(OllamaShowCeiling));
        Assert.Equal(16_384, ContextLengthProbe.ParseOllamaShow(OllamaShowNumCtx)!.Value.Tokens);
        Assert.Null(ContextLengthProbe.ParseOllamaShow("{\"parameters\":\"num_ctx abc\"}"));
        Assert.Null(ContextLengthProbe.ParseOllamaShow("{}"));
        Assert.Null(ContextLengthProbe.ParseLlamaProps("{\"default_generation_settings\":{}}"));
        Assert.Null(ContextLengthProbe.ParseOllamaPs("{\"models\":[{\"name\":\"x\"}]}", "x"));
    }

    [Fact]
    public void ShowBody_EscapesTheId()
    {
        Assert.Equal("{\"model\":\"a\\\"b\"}", ContextLengthProbe.ShowBody("a\"b"));
    }

    [Fact]
    public async Task NothingAnswers_IsNull_AfterEveryTier()
    {
        var (stub, probe) = Make();

        Assert.Null(await Detect(probe, "m"));
        Assert.Equal(
            new[] { "/api/v0/models", "/props", "/api/ps", "/api/show" },
            stub.Requests.Select(r => r.Uri.AbsolutePath));
    }

    [Fact]
    public async Task BadAnswers_AreNull_NeverThrown()
    {
        var (stub, probe) = Make();
        stub.Map(Root + "/api/v0/models", HttpStatusCode.OK, "<html>", "text/html");
        stub.Map(Root + "/props", (_, _) => throw new InvalidOperationException("odd handler"));

        Assert.Null(await Detect(probe, "m"));
    }

    [Fact]
    public async Task TheKey_RidesEveryRequest_AndTheTimeoutHolds()
    {
        var (stub, probe) = Make(TimeSpan.FromMilliseconds(100));
        stub.Map(Root + "/api/v0/models", async (_, ct) => { await Task.Delay(5_000, ct); return StubHttpMessageHandler.Json(HttpStatusCode.OK, LmStudioNative); });
        stub.Map(Root + "/props", HttpStatusCode.OK, LlamaProps);

        var found = await Detect(probe, "q6-model", "sk-1");

        Assert.Equal(8_192, found!.Value.Tokens);   // the slow tier gave way; the next answered
        Assert.All(stub.Requests, r => Assert.Equal("Bearer sk-1", r.Authorization));
    }

    [Fact]
    public async Task AnyBaseUrlShape_ReachesTheSameRoot()
    {
        var (stub, probe) = Make();
        stub.Map(Root + "/props", HttpStatusCode.OK, LlamaProps);

        Assert.Equal(8_192, (await probe.DetectAsync(new Uri(Root), "m", null, CancellationToken.None))!.Value.Tokens);
        Assert.Equal(8_192, (await probe.DetectAsync(new Uri(Root + "/v1/"), "m", null, CancellationToken.None))!.Value.Tokens);
        Assert.Equal(new Uri("http://127.0.0.1:1234/"), LlmEndpoint.RootUrl(new Uri("http://127.0.0.1:1234/v1")));
        Assert.Equal(new Uri("http://h/prefix"), LlmEndpoint.RootUrl(new Uri("http://h/prefix/v1")));
    }

    [Fact]
    public void Configured_IsItsOwnSource()
    {
        Assert.Equal(new ContextLength(32_768, "configured"), ContextLength.Configured(32_768));
    }

    [Fact]
    public void Constructor_RejectsANonPositiveTimeout()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ContextLengthProbe(new HttpClient(new StubHttpMessageHandler()), TimeSpan.Zero));
        Assert.Throws<ArgumentNullException>(() => new ContextLengthProbe(null!));
    }
}
