using System.Net;
using NeonSidekick.App;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Anthropic;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// The server's own sampling defaults (2026-09-28, the user's question: "is there a way to know what the server default
/// settings are?"): llama.cpp's <c>/props</c>, Ollama's <c>/api/show</c>, the Hugging Face model card behind
/// <c>LLM sampling from Hugging Face</c>; the session's cache; the pane's <c>(server)</c> values.
/// </summary>
public class ServerSamplingProbeTests
{
    private const string Root = "http://127.0.0.1:8080";

    private const string PropsNested = """
        {"default_generation_settings":{"n_ctx":4096,"params":{"temperature":0.800000011920929,"top_k":40,"top_p":0.949999988079071,
         "min_p":0.05000000074505806,"typical_p":1.0,"repeat_penalty":1.0,"presence_penalty":0.0,"frequency_penalty":0.0,"mirostat":0}}}
        """;

    private const string OllamaShow = """
        {"parameters":"stop                           \"<|im_start|>\"\nstop                           \"<|im_end|>\"\ntemperature                    0.6\ntop_k                          20\ntop_p                          0.95\nrepeat_penalty                 1\nnum_ctx                        8192"}
        """;

    private const string GenerationConfig = """
        {"bos_token_id":151643,"do_sample":true,"eos_token_id":[151645,151643],"temperature":0.6,"top_k":20,"top_p":0.95,"repetition_penalty":1.05,"transformers_version":"4.51.0"}
        """;

    private static readonly Uri QwenCard = new(ServerSamplingProbe.HuggingFaceBase + "Qwen/Qwen3-8B/resolve/main/generation_config.json");

    // ── Parsers ─────────────────────────────────────────────────────────────

    [Fact]
    public void Props_ReadsTheParams_RoundingTheFloat32Noise()
    {
        var server = ServerSamplingProbe.ParseLlamaProps(PropsNested)!;
        Assert.Equal(ServerSamplingSource.LlamaProps, server.Source);
        Assert.Equal(0.8, server.Value(SamplingKey.Temperature));
        Assert.Equal(40, server.Value(SamplingKey.TopK));
        Assert.Equal(0.95, server.Value(SamplingKey.TopP));
        Assert.Equal(0.05, server.Value(SamplingKey.MinP));
        Assert.Equal(1.0, server.Value(SamplingKey.RepetitionPenalty));   // repeat_penalty, llama.cpp's name
        Assert.Equal(0.0, server.Value(SamplingKey.PresencePenalty));
        Assert.Equal(7, server.Values.Count);   // typical_p and mirostat are no named field
        Assert.Equal("temperature 0.8 · top_p 0.95 · top_k 40 · min_p 0.05 · presence_penalty 0 · frequency_penalty 0 · repetition_penalty 1", server.Describe());
    }

    [Fact]
    public void Props_AnOlderFlatShape_AndAnswersWithNothing()
    {
        Assert.Equal(0.7, ServerSamplingProbe.ParseLlamaProps("""{"default_generation_settings":{"n_ctx":2048,"temperature":0.7}}""")!.Value(SamplingKey.Temperature));
        Assert.Null(ServerSamplingProbe.ParseLlamaProps("""{"default_generation_settings":{"n_ctx":2048}}"""));
        Assert.Null(ServerSamplingProbe.ParseLlamaProps("""{"model_path":"x"}"""));
        Assert.Null(ServerSamplingProbe.ParseLlamaProps("not json"));
        Assert.Null(ServerSamplingProbe.ParseLlamaProps(null));
    }

    [Fact]
    public void OllamaShow_ReadsTheModelfileLines_SkippingWhatIsNoField()
    {
        var server = ServerSamplingProbe.ParseOllamaShow(OllamaShow)!;
        Assert.Equal(ServerSamplingSource.OllamaShow, server.Source);
        Assert.Equal(0.6, server.Value(SamplingKey.Temperature));
        Assert.Equal(20, server.Value(SamplingKey.TopK));
        Assert.Equal(1, server.Value(SamplingKey.RepetitionPenalty));
        Assert.Equal(4, server.Values.Count);   // stop and num_ctx skipped
        Assert.Null(ServerSamplingProbe.ParseOllamaShow("""{"parameters":"num_ctx 8192"}"""));
        Assert.Null(ServerSamplingProbe.ParseOllamaShow("""{"modelfile":"FROM x"}"""));
    }

    [Fact]
    public void GenerationConfig_ReadsTheTopLevelFields_AnOutOfRangeOneDropped()
    {
        var server = ServerSamplingProbe.ParseGenerationConfig(GenerationConfig, "Qwen/Qwen3-8B")!;
        Assert.Equal(ServerSamplingSource.HuggingFace, server.Source);
        Assert.Equal("Qwen/Qwen3-8B", server.Detail);
        Assert.Equal(1.05, server.Value(SamplingKey.RepetitionPenalty));
        Assert.Equal(4, server.Values.Count);

        var odd = ServerSamplingProbe.ParseGenerationConfig("""{"temperature":9,"top_p":0.9}""", "a/b")!;
        Assert.Null(odd.Value(SamplingKey.Temperature));   // out of range: not shown
        Assert.Equal(0.9, odd.Value(SamplingKey.TopP));
    }

    [Theory]
    [InlineData("Qwen/Qwen3-8B", true)]
    [InlineData("meta-llama/Llama-3.1-8B-Instruct", true)]
    [InlineData("unsloth/Qwen3.5-9B_GGUF", true)]
    [InlineData("qwen3:8b", false)]
    [InlineData("qwen3-8b", false)]
    [InlineData("a/b/c", false)]
    [InlineData("/models/x", false)]
    [InlineData(@"C:\models\x", false)]
    [InlineData("../etc", false)]
    [InlineData("org/..", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void HuggingFaceUrl_OnlyForARepoShapedId(string? id, bool expected)
    {
        Assert.Equal(expected, ServerSamplingProbe.HuggingFaceUrl(id) is not null);
        Assert.Equal(QwenCard, ServerSamplingProbe.HuggingFaceUrl("Qwen/Qwen3-8B"));
    }

    // ── The tiers ───────────────────────────────────────────────────────────

    private static ServerSamplingProbe Probe(StubHttpMessageHandler stub) => new(new HttpClient(stub), TimeSpan.FromMilliseconds(500));

    [Fact]
    public async Task Props_AnsweringWins_NothingElseAsked()
    {
        var stub = new StubHttpMessageHandler().Map(Root + "/props", HttpStatusCode.OK, PropsNested);
        var server = await Probe(stub).DetectAsync(new Uri(Root + "/v1"), "Qwen/Qwen3-8B", "sk-local", huggingFace: true, CancellationToken.None);

        Assert.Equal(ServerSamplingSource.LlamaProps, server!.Source);
        var request = Assert.Single(stub.Requests);
        Assert.Equal("Bearer sk-local", request.Authorization);   // the server's own endpoint gets the server's key
    }

    [Fact]
    public async Task NoProps_FallsThroughToApiShow_WithTheModelInTheBody()
    {
        var stub = new StubHttpMessageHandler()
            .Map(Root + "/props", HttpStatusCode.NotFound, "{}")
            .Map(Root + "/api/show", HttpStatusCode.OK, OllamaShow);
        var server = await Probe(stub).DetectAsync(new Uri(Root), "qwen3:8b", null, huggingFace: true, CancellationToken.None);

        Assert.Equal(ServerSamplingSource.OllamaShow, server!.Source);
        Assert.Equal(HttpMethod.Post, stub.Requests[^1].Method);
        Assert.Equal("""{"model":"qwen3:8b"}""", stub.Requests[^1].Body);
    }

    [Fact]
    public async Task NeitherAnswering_TheModelCardOnlyWithTheSwitch_AndNeverWithTheKey()
    {
        var stub = new StubHttpMessageHandler().Map(QwenCard.AbsoluteUri, HttpStatusCode.OK, GenerationConfig);

        Assert.Null(await Probe(stub).DetectAsync(new Uri(Root), "Qwen/Qwen3-8B", "sk-local", huggingFace: false, CancellationToken.None));
        Assert.DoesNotContain(stub.Requests, r => r.Uri.Host == "huggingface.co");

        var server = await Probe(stub).DetectAsync(new Uri(Root), "Qwen/Qwen3-8B", "sk-local", huggingFace: true, CancellationToken.None);
        Assert.Equal(ServerSamplingSource.HuggingFace, server!.Source);
        var card = Assert.Single(stub.Requests, r => r.Uri.Host == "huggingface.co");
        Assert.Null(card.Authorization);   // the LLM's key is not huggingface.co's
    }

    [Fact]
    public async Task AGatedCard_OrAnIdThatIsNoRepo_IsNothing()
    {
        var gated = new StubHttpMessageHandler().Map(QwenCard.AbsoluteUri, HttpStatusCode.Unauthorized, "{}");
        Assert.Null(await Probe(gated).DetectAsync(new Uri(Root), "Qwen/Qwen3-8B", null, huggingFace: true, CancellationToken.None));

        var stub = new StubHttpMessageHandler();
        Assert.Null(await Probe(stub).DetectAsync(new Uri(Root), "qwen3-8b", null, huggingFace: true, CancellationToken.None));
        Assert.DoesNotContain(stub.Requests, r => r.Uri.Host == "huggingface.co");
    }

    // ── The session's cache ─────────────────────────────────────────────────

    private static LlmSession Session(StubHttpMessageHandler stub, ServerSamplingProbe? probe) => new(
        new LlmEndpointProbe(new HttpClient(stub), TimeSpan.FromMilliseconds(500)),
        new ContextLengthProbe(new HttpClient(stub), TimeSpan.FromMilliseconds(500)),
        (_, _) => new FakeChatClient(),
        samplingProbe: probe);

    private static LlmEndpoint Llama(string model = "qwen3-8b") => new(new Uri(Root + "/v1"), model, "empty", "configured");

    [Fact]
    public async Task Session_AsksOncePerModel_AgainAfterAReconnectOrAFlip()
    {
        var stub = new StubHttpMessageHandler().Map(Root + "/props", HttpStatusCode.OK, PropsNested);
        using var session = Session(stub, Probe(stub));
        var settings = new AppSettingsData();

        Assert.Null(await session.ServerSamplingAsync(settings, CancellationToken.None));   // nothing connected: nothing asked
        Assert.Empty(stub.Requests);

        session.Connect(settings, Llama());
        Assert.Equal(0.8, (await session.ServerSamplingAsync(settings, CancellationToken.None))!.Value(SamplingKey.Temperature));
        await session.ServerSamplingAsync(settings, CancellationToken.None);
        Assert.Single(stub.Requests);   // cached

        await session.ServerSamplingAsync(new AppSettingsData { LlmSamplingFromHuggingFace = true }, CancellationToken.None);
        Assert.Equal(2, stub.Requests.Count);   // the switch is part of the key

        session.Connect(settings, Llama("other"));
        await session.ServerSamplingAsync(settings, CancellationToken.None);
        Assert.Equal(3, stub.Requests.Count);   // a reconnect forgets
    }

    [Fact]
    public async Task Session_TheClaudeApi_OrNoProbe_AsksNothing()
    {
        var stub = new StubHttpMessageHandler();
        using var claude = Session(stub, Probe(stub));
        claude.Connect(new AppSettingsData(), new LlmEndpoint(ClaudeApi.BaseUrl, "claude-sonnet-5", "sk-ant", "configured"));
        Assert.Null(await claude.ServerSamplingAsync(new AppSettingsData(), CancellationToken.None));

        using var none = Session(stub, probe: null);
        none.Connect(new AppSettingsData(), Llama());
        Assert.Null(await none.ServerSamplingAsync(new AppSettingsData(), CancellationToken.None));
        Assert.Empty(stub.Requests);
    }

    // ── The pane ────────────────────────────────────────────────────────────

    [Fact]
    public void TheConnectedTab_ShowsTheServersValues_UnderOwnAndInherited_AndNamesTheSource()
    {
        var server = ServerSamplingProbe.ParseLlamaProps(PropsNested)!;
        var map = new Dictionary<string, LlmSamplingEntry>
        {
            ["qwen3-8b"] = new() { Temperature = 0.6 },
            ["*"] = new() { TopP = 0.9 },
        };

        var lines = SamplingMenu.Lines(map, "qwen3-8b", server).ToList();
        int w = SamplingText.LabelWidth;
        Assert.Equal("qwen3-8b", lines[0]);
        Assert.Equal("  " + SamplingText.SourceCaption(server), lines[1]);
        Assert.Equal("  " + "temperature".PadRight(w) + "0.6", lines[2]);            // its own wins
        Assert.Equal("  " + "top_p".PadRight(w) + "0.9 (from *)", lines[3]);         // then *
        Assert.Equal("  " + "top_k".PadRight(w) + "40 (server)", lines[4]);          // then the server
        int anyTab = lines.IndexOf(SamplingText.AnyModelTabTitle);
        Assert.Equal("  " + "top_k".PadRight(w) + SamplingText.ServerDefault, lines[anyTab + 3]);   // * is no one model: no server values

        var page = SamplingMenu.Page(map, SamplingMenu.TabKeys(map, "qwen3-8b"), 0, claudeApi: false, server);
        Assert.Equal(SamplingText.SourceCaption(server), page.Tabs![0].Caption);
        Assert.Null(page.Tabs[1].Caption);
        Assert.Contains("40 (server)", page.Rows[2]);
    }

    [Fact]
    public void Wording_IsPinned()
    {
        Assert.Equal("0.6 (Hugging Face)", SamplingText.ServerValue(0.6, ServerSamplingSource.HuggingFace));
        Assert.Equal("40 (server)", SamplingText.ServerValue(40, ServerSamplingSource.OllamaShow));
        Assert.Equal("Defaults from huggingface.co/Qwen/Qwen3-8B (generation_config.json).", SamplingText.SourceCaption(ServerSamplingProbe.ParseGenerationConfig(GenerationConfig, "Qwen/Qwen3-8B")!));
        Assert.Equal("Defaults from Ollama's /api/show (the Modelfile's parameters).", SamplingText.SourceCaption(ServerSamplingProbe.ParseOllamaShow(OllamaShow)!));
        Assert.Equal("LLM sampling from Hugging Face", SettingsMenu.FieldName(SettingsField.LlmSamplingFromHuggingFace));
        Assert.True(SettingsMenu.IsToggle(SettingsField.LlmSamplingFromHuggingFace));
        Assert.False(SettingsMenu.IsLlmField(SettingsField.LlmSamplingFromHuggingFace));
        Assert.False(SettingsMenu.RefusedMidTurn(SettingsField.LlmSamplingFromHuggingFace));
        Assert.False(new AppSettingsData().LlmSamplingFromHuggingFace);
    }
}
