using System.Net;
using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Anthropic;
using NeonSidekick.Llm.OpenAIPlatform;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>The OpenAI API as a server (2026-10-03): the request shaping, the client, the usage and cost, the probe, the session, the settings.</summary>
public class OpenAIApiTests
{
    private const string Completions = "https://api.openai.com/v1/chat/completions";

    private static LlmEndpoint Endpoint(string model = "gpt-5.6-sol") => new(OpenAIApi.BaseUrl, model, "sk-openai-test", "configured");

    private const string Head = "data: {\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"gpt-5.6-sol\",";

    /// <summary>A short answer, then the usage chunk, as the API streams it with <c>include_usage</c>.</summary>
    private static string Stream(string usage = "{\"prompt_tokens\":10,\"completion_tokens\":2,\"total_tokens\":12}") =>
        Head + "\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"Four.\"},\"finish_reason\":null}]}\n\n"
        + Head + "\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n"
        + Head + "\"choices\":[],\"usage\":" + usage + "}\n\n"
        + "data: [DONE]\n\n";

    private static (OpenAIApiChatClient Client, StubHttpMessageHandler Stub) Client(string model = "gpt-5.6-sol", int maxTokens = 0, string? organization = null, string? project = null, string? stream = null)
    {
        var stub = new StubHttpMessageHandler().Map(Completions, HttpStatusCode.OK, stream ?? Stream(), "text/event-stream");
        return (new OpenAIApiChatClient(Endpoint(model), TimeSpan.FromSeconds(10), maxTokens, organization, project, new HttpClient(stub)), stub);
    }

    private static async Task<JsonElement> SentBody(IEnumerable<ChatMessage> messages, ChatOptions? options, string model = "gpt-5.6-sol", int maxTokens = 0)
    {
        var (client, stub) = Client(model, maxTokens);
        using (client)
        {
            await foreach (var _ in client.GetStreamingResponseAsync(messages, options))
            {
            }
        }

        using var document = JsonDocument.Parse(Assert.Single(stub.Requests).Body!);
        return document.RootElement.Clone();
    }

    // ── The client ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Client_SendsTheKeyAsABearer_AndTheOrganizationAndProject()
    {
        string? authorization = null, organization = null, project = null;
        var stub = new StubHttpMessageHandler();
        stub.Map(Completions, (request, _) =>
        {
            authorization = request.Headers.Authorization?.ToString();
            organization = request.Headers.TryGetValues(OpenAIApi.OrganizationHeader, out var o) ? o.Single() : null;
            project = request.Headers.TryGetValues(OpenAIApi.ProjectHeader, out var p) ? p.Single() : null;
            return Task.FromResult(StubHttpMessageHandler.Json(HttpStatusCode.OK, Stream(), "text/event-stream"));
        });
        using var client = new OpenAIApiChatClient(Endpoint(), TimeSpan.FromSeconds(10), 0, "org-1", "proj-1", new HttpClient(stub));

        await foreach (var _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "x")]))
        {
        }

        Assert.Equal(("Bearer sk-openai-test", "org-1", "proj-1"), (authorization, organization, project));
        Assert.Equal(OpenAIApi.BaseUrl, client.Endpoint.BaseUrl);
    }

    [Fact]
    public async Task Client_NoOrganizationOrProject_SendsNeither()
    {
        bool any = true;
        var stub = new StubHttpMessageHandler();
        stub.Map(Completions, (request, _) =>
        {
            any = request.Headers.Contains(OpenAIApi.OrganizationHeader) || request.Headers.Contains(OpenAIApi.ProjectHeader);
            return Task.FromResult(StubHttpMessageHandler.Json(HttpStatusCode.OK, Stream(), "text/event-stream"));
        });
        using var client = new OpenAIApiChatClient(Endpoint(), TimeSpan.FromSeconds(10), httpClient: new HttpClient(stub));

        await client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "x")]).ToListAsync();

        Assert.False(any);
    }

    [Fact]
    public async Task Request_LeavesOutTheLocalServersKnobs()
    {
        var options = new ChatOptions
        {
            Temperature = 0.7f,
            TopP = 0.9f,
            TopK = 40,
            PresencePenalty = 0.1f,
            FrequencyPenalty = 0.2f,
            Reasoning = new ReasoningOptions { Effort = ReasoningEffort.None },
            AdditionalProperties = new() { [OpenAICompatibleChatClient.PreserveThinkingKey] = true },
        };

        var body = await SentBody([new ChatMessage(ChatRole.User, "x")], options, "gpt-5-mini");

        foreach (string field in new[] { "temperature", "top_p", "top_k", "presence_penalty", "frequency_penalty", "min_p", "repetition_penalty", "repeat_penalty", OpenAICompatibleChatClient.TemplateKwargsField, "max_completion_tokens", "max_tokens" })
        {
            Assert.False(body.TryGetProperty(field, out _), field);
        }

        Assert.Equal("minimal", body.GetProperty("reasoning_effort").GetString());   // gpt-5's none
        Assert.True(body.GetProperty("stream").GetBoolean());
    }

    [Fact]
    public async Task Request_TheOutputCap_IsMaxCompletionTokens()
    {
        var body = await SentBody([new ChatMessage(ChatRole.User, "x")], null, maxTokens: 4096);
        Assert.Equal(4096, body.GetProperty("max_completion_tokens").GetInt32());
        Assert.False(body.TryGetProperty("reasoning_effort", out _));   // no level asked: the model's own
    }

    [Theory]
    [InlineData("gpt-5.6-sol", ReasoningEffort.None, "none")]
    [InlineData("gpt-5.6-sol", ReasoningEffort.ExtraHigh, "xhigh")]
    [InlineData("gpt-6.1-sol", ReasoningEffort.None, "low")]       // no none: a 400 there
    [InlineData("gpt-6-astra", ReasoningEffort.ExtraHigh, "xhigh")]
    [InlineData("gpt-6-luna", ReasoningEffort.None, "none")]
    [InlineData("gpt-5.1", ReasoningEffort.ExtraHigh, "high")]
    [InlineData("gpt-5", ReasoningEffort.None, "minimal")]
    [InlineData("gpt-5-nano", ReasoningEffort.Medium, "medium")]
    [InlineData("o4-mini", ReasoningEffort.None, "low")]
    [InlineData("gpt-7-future", ReasoningEffort.None, "low")]      // unknown: the newest kind
    public async Task Request_ShapesTheReasoningLevelForTheModel(string model, ReasoningEffort effort, string expected)
    {
        Assert.Equal(expected, OpenAIModelRules.For(model).EffortWord(effort));
        var body = await SentBody([new ChatMessage(ChatRole.User, "x")], new ChatOptions { Reasoning = new ReasoningOptions { Effort = effort } }, model);
        Assert.Equal(expected, body.GetProperty("reasoning_effort").GetString());
    }

    [Fact]
    public async Task Request_AModelThatDoesNotReason_GetsNoEffort()
    {
        Assert.Null(OpenAIModelRules.For("gpt-4.1").EffortWord(ReasoningEffort.High));
        var body = await SentBody([new ChatMessage(ChatRole.User, "x")], new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.High } }, "gpt-4o-mini");
        Assert.False(body.TryGetProperty("reasoning_effort", out _));
    }

    [Fact]
    public async Task Request_SendsNoThinkingBack_AndDropsAnAllThinkingMessage()
    {
        ChatMessage[] history =
        [
            new(ChatRole.User, "go"),
            new(ChatRole.Assistant, [new TextReasoningContent("hm"), new TextContent("Done.")]),
            new(ChatRole.Assistant, [new TextReasoningContent("only thinking") { ProtectedData = "SIG" }]),
            new(ChatRole.User, "again"),
        ];

        var body = await SentBody(history, new ChatOptions { AdditionalProperties = new() { [OpenAICompatibleChatClient.PreserveThinkingKey] = true } });

        string text = body.GetRawText();
        Assert.DoesNotContain("reasoning_content", text);
        Assert.DoesNotContain("only thinking", text);
        Assert.DoesNotContain("\"hm\"", text);
        Assert.Equal(["user", "assistant", "user"], body.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("role").GetString()));
    }

    // ── Usage and cost ──────────────────────────────────────────────────────

    [Fact]
    public async Task Usage_CarriesTheCacheWriteAndTheCost()
    {
        const string Usage = "{\"prompt_tokens\":10000,\"completion_tokens\":500,\"total_tokens\":10500,\"prompt_tokens_details\":{\"cached_tokens\":8000,\"cache_write_tokens\":1000},\"completion_tokens_details\":{\"reasoning_tokens\":300}}";
        var (client, _) = Client(stream: Stream(Usage));
        using var _client = client;
        var assistant = new Assistant(client, new ConversationHistory("sys"), new LlmTimeouts(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)));

        TurnEvent.Usage? reported = null;
        await foreach (var evt in assistant.RunTurnAsync("q"))
        {
            if (evt is TurnEvent.Usage u) reported = u;
        }

        var tokens = Assert.IsType<TurnEvent.Usage>(reported).Tokens;
        Assert.Equal((10_000L, 500L, 8000L, 1000L, 300L), (tokens.Input, tokens.Output, tokens.CacheRead, tokens.CacheWrite, tokens.Reasoning));

        // gpt-5.6-sol: 1,000 uncached at $4, 8,000 read at $0.40, 1,000 written at $5, 500 out at $20 (per million).
        Assert.Equal(0.0004m * 10 + 0.0032m + 0.005m + 0.01m, tokens.CostUsd);
    }

    [Fact]
    public void Usage_AModelTheTableDoesNotName_HasNoCost()
    {
        var details = new UsageDetails { InputTokenCount = 100, OutputTokenCount = 10 };
        OpenAIApiChatClient.FillCost(details, null, "gpt-unknown");
        Assert.False(details.AdditionalCounts?.ContainsKey(AnthropicStream.CostKey) ?? false);
    }

    [Theory]
    [InlineData("gpt-6.1-sol", 2, 0.10, 10)]
    [InlineData("gpt-5.6-terra", 2, 0.20, 12)]
    [InlineData("gpt-5.5", 5, 0.50, 30)]
    [InlineData("gpt-5.4-mini", 0.75, 0.075, 4.50)]
    [InlineData("gpt-5-mini-2025-08-07", 0.25, 0.025, 2)]
    [InlineData("gpt-4.1", 2, 0.50, 8)]
    [InlineData("gpt-4o-mini", 0.15, 0.075, 0.60)]
    [InlineData("o3", 2, 0.50, 8)]
    public void Price_IsTheModelsList(string model, double input, double cached, double output)
    {
        var price = OpenAIPrice.For(model)!;
        Assert.Equal(((decimal)input, (decimal)cached, (decimal)output), (price.Input, price.CachedInput, price.Output));
        Assert.Null(OpenAIPrice.For("gpt-unknown"));
    }

    [Fact]
    public void Price_ALongPrompt_IsBilledAtTheLongTier()
    {
        var price = OpenAIPrice.For("gpt-5.4")!;
        Assert.Equal(5m * 0.3m + 22.5m * 0.1m, price.Cost(300_000, 0, 0, 100_000));         // over 272K: the long prices
        Assert.Equal(2.5m * 0.2m + 15m * 0.1m, price.Cost(200_000, 0, 0, 100_000));         // under: the short ones
        Assert.Equal(0.125m * 0.1m, OpenAIPrice.For("gpt-5")!.Cost(100_000, 100_000, 0, 0)); // all read from the cache
    }

    // ── The model list ──────────────────────────────────────────────────────

    private const string ModelsList = "{\"object\":\"list\",\"data\":["
        + "{\"id\":\"gpt-4o\",\"object\":\"model\",\"created\":1715367049,\"owned_by\":\"system\"},"
        + "{\"id\":\"text-embedding-3-small\",\"object\":\"model\",\"created\":1705948997,\"owned_by\":\"system\"},"
        + "{\"id\":\"gpt-6.1-sol\",\"object\":\"model\",\"created\":1790000000,\"owned_by\":\"system\"},"
        + "{\"id\":\"gpt-4o-realtime-preview\",\"object\":\"model\",\"created\":1727659998,\"owned_by\":\"system\"},"
        + "{\"id\":\"dall-e-3\",\"object\":\"model\",\"created\":1698785189,\"owned_by\":\"system\"},"
        + "{\"id\":\"gpt-5.5-pro\",\"object\":\"model\",\"created\":1780000000,\"owned_by\":\"system\"},"
        + "{\"id\":\"o4-mini\",\"object\":\"model\",\"created\":1744225308,\"owned_by\":\"system\"},"
        + "{\"id\":\"tts-1\",\"object\":\"model\",\"created\":1681940951,\"owned_by\":\"openai-internal\"},"
        + "{\"id\":\"gpt-5.6-sol\",\"object\":\"model\",\"created\":1785000000,\"owned_by\":\"system\"},"
        + "{\"id\":\"omni-moderation-latest\",\"object\":\"model\",\"created\":1731689265,\"owned_by\":\"system\"}"
        + "]}";

    [Fact]
    public void ChatModels_AreTheChatOnes_NewestFirst()
    {
        Assert.Equal(["gpt-6.1-sol", "gpt-5.6-sol", "o4-mini", "gpt-4o"], OpenAIApi.ChatModels(ModelsList));
        Assert.Empty(OpenAIApi.ChatModels("not json"));
        Assert.Empty(OpenAIApi.ChatModels(""));
    }

    [Theory]
    [InlineData("gpt-5.6-sol", true)]
    [InlineData("chatgpt-4o-latest", true)]
    [InlineData("o3-mini", true)]
    [InlineData("gpt-4o-mini-transcribe", false)]
    [InlineData("gpt-4o-audio-preview", false)]
    [InlineData("gpt-image-1", false)]
    [InlineData("gpt-5-pro", false)]
    [InlineData("gpt-5-codex", false)]
    [InlineData("whisper-1", false)]
    [InlineData("davinci-002", false)]
    [InlineData("omni-moderation-latest", false)]
    public void ChatModel_IsToldByItsId(string id, bool chat) => Assert.Equal(chat, OpenAIModelRules.IsChatModel(id));

    [Theory]
    [InlineData("gpt-6.1-sol", 922_000)]
    [InlineData("gpt-5.5", 1_050_000)]
    [InlineData("gpt-5.4-mini", 400_000)]
    [InlineData("gpt-5-nano", 400_000)]
    [InlineData("gpt-4.1-mini", 1_047_576)]
    [InlineData("gpt-4o", 128_000)]
    [InlineData("o3", 200_000)]
    [InlineData("gpt-unknown", OpenAIModelRules.FallbackContextWindow)]
    public void ContextWindow_IsTheTables(string model, int window) => Assert.Equal(window, OpenAIModelRules.ContextWindow(model));

    // ── The probe and the session ───────────────────────────────────────────

    [Fact]
    public async Task Probe_AsksWithTheKeyAndHeaders_ListsTheChatModels_AndNamesTheServer()
    {
        string? authorization = null, organization = null;
        var stub = new StubHttpMessageHandler();
        stub.Map("https://api.openai.com/v1/models", (request, _) =>
        {
            authorization = request.Headers.Authorization?.ToString();
            organization = request.Headers.TryGetValues(OpenAIApi.OrganizationHeader, out var o) ? o.Single() : null;
            return Task.FromResult(StubHttpMessageHandler.Json(HttpStatusCode.OK, ModelsList));
        });
        var probe = new LlmEndpointProbe(new HttpClient(stub));
        var effective = new AppSettingsData { OpenAIApiOrganization = " org-1 " };

        var result = await probe.ProbeAsync(OpenAIApi.BaseUrl, "sk-openai-test", CancellationToken.None, OpenAIApi.HeadersFor(effective, OpenAIApi.BaseUrl));

        Assert.Equal(("Bearer sk-openai-test", "org-1"), (authorization, organization));
        Assert.Equal(["gpt-6.1-sol", "gpt-5.6-sol", "o4-mini", "gpt-4o"], result.ModelIds);
        Assert.Equal(OpenAIApi.ServerName, LlmServer.From(OpenAIApi.BaseUrl, result).Name);

        // A local server is never told the organization.
        Assert.Empty(OpenAIApi.HeadersFor(effective, new Uri("http://127.0.0.1:1234/v1")));
    }

    [Fact]
    public async Task Probe_ABadKey_SaysWhereTheKeyIsSet()
    {
        var stub = new StubHttpMessageHandler().Map("https://api.openai.com/v1/models", HttpStatusCode.Unauthorized, "{\"error\":{\"message\":\"Incorrect API key provided\"}}");
        var probe = new LlmEndpointProbe(new HttpClient(stub));

        var result = await probe.ProbeAsync(OpenAIApi.BaseUrl, "bad", CancellationToken.None);

        Assert.True(result.Exists);
        Assert.Equal("401 on /v1/models; " + OpenAIApiText.KeyHint, result.Detail);
    }

    private static LlmSession Session(StubHttpMessageHandler stub, List<LlmEndpoint> endpoints) => new(
        new LlmEndpointProbe(new HttpClient(stub), TimeSpan.FromMilliseconds(500)),
        new ContextLengthProbe(new HttpClient(stub), TimeSpan.FromMilliseconds(500)),
        (endpoint, _) =>
        {
            endpoints.Add(endpoint);
            return new FakeChatClient();
        });

    [Fact]
    public async Task Session_ListsTheOpenAIApi_AfterTheClaudeApi_OnlyWhileItIsOffered()
    {
        var stub = new StubHttpMessageHandler()
            .Map("http://127.0.0.1:1234/v1/models", HttpStatusCode.OK, StubHttpMessageHandler.ModelsJson("llama"))
            .Map("https://api.anthropic.com/v1/models", HttpStatusCode.OK, "{\"data\":[{\"id\":\"claude-sonnet-5\"}]}")
            .Map("https://api.openai.com/v1/models", HttpStatusCode.OK, ModelsList);
        using var session = Session(stub, []);

        var off = await session.ProbeServersAsync(new AppSettingsData { OpenAIApiKey = "sk-openai-test", LlmScanMode = "local" }, null, CancellationToken.None);
        Assert.Equal(["LM Studio"], off.Select(s => s.Name));

        var keyless = await session.ProbeServersAsync(new AppSettingsData { OpenAIApi = true, LlmScanMode = "local" }, null, CancellationToken.None);
        Assert.Equal(["LM Studio"], keyless.Select(s => s.Name));

        var both = await session.ProbeServersAsync(new AppSettingsData { OpenAIApi = true, OpenAIApiKey = "sk-openai-test", ClaudeApi = true, ClaudeApiKey = "sk-ant-test", LlmScanMode = "local" }, null, CancellationToken.None);
        Assert.Equal(["LM Studio", ClaudeApi.ServerName, OpenAIApi.ServerName], both.Select(s => s.Name));

        var alone = await session.ProbeServersAsync(new AppSettingsData { OpenAIApi = true, OpenAIApiKey = "sk-openai-test", LlmScanMode = "disabled" }, null, CancellationToken.None);
        Assert.Equal(["gpt-6.1-sol", "gpt-5.6-sol", "o4-mini", "gpt-4o"], Assert.Single(alone).Result.ModelIds);
    }

    [Fact]
    public async Task Session_AConfiguredOpenAIUrl_ConnectsWithItsOwnKey_AndTheTablesWindow()
    {
        var stub = new StubHttpMessageHandler().Map("https://api.openai.com/v1/models", HttpStatusCode.OK, ModelsList);
        var endpoints = new List<LlmEndpoint>();
        using var session = Session(stub, endpoints);

        Assert.True(await session.ConnectAsync(new AppSettingsData { OpenAIApi = true, OpenAIApiKey = "sk-openai-test", ClaudeApiKey = "sk-ant-test", LlmApiKey = "local-key", LlmUrl = "https://api.openai.com" }, CancellationToken.None));

        var endpoint = Assert.Single(endpoints);
        Assert.Equal(("sk-openai-test", "gpt-6.1-sol"), (endpoint.ApiKey, endpoint.ModelId));
        Assert.Equal(new ContextLength(922_000, ContextLengthProbe.OpenAIModelTableSource), session.ContextLength);
        Assert.Single(stub.Requests);   // no native context tiers asked of the API's host
    }

    [Fact]
    public async Task Session_AnOpenAIUrlWhileItIsOff_LooksForAServerInstead()
    {
        var stub = new StubHttpMessageHandler().Map("http://127.0.0.1:1234/v1/models", HttpStatusCode.OK, StubHttpMessageHandler.ModelsJson("llama"));
        var endpoints = new List<LlmEndpoint>();
        using var session = Session(stub, endpoints);

        Assert.True(await session.ConnectAsync(new AppSettingsData { OpenAIApiKey = "sk-openai-test", LlmUrl = "https://api.openai.com/v1", LlmScanMode = "local" }, CancellationToken.None));

        Assert.Equal("http://127.0.0.1:1234/v1", Assert.Single(endpoints).BaseUrl.AbsoluteUri);
        Assert.DoesNotContain(stub.Requests, r => r.Uri.Host == OpenAIApi.Host);
    }

    // ── Keys and settings ───────────────────────────────────────────────────

    [Fact]
    public void Keys_NeverCross_BetweenTheApisAndALocalServer()
    {
        Assert.False(OpenAIApi.Offered(new AppSettingsData { OpenAIApi = true }));
        Assert.False(OpenAIApi.Offered(new AppSettingsData { OpenAIApiKey = "k" }));
        Assert.True(OpenAIApi.Offered(new AppSettingsData { OpenAIApi = true, OpenAIApiKey = "k" }));

        var data = new AppSettingsData { OpenAIApiKey = "openai-key", ClaudeApiKey = "claude-key", LlmApiKey = "local-key" };
        Assert.Equal("openai-key", ApiKeys.For(data, OpenAIApi.BaseUrl));
        Assert.Equal("claude-key", ApiKeys.For(data, ClaudeApi.BaseUrl));
        Assert.Equal("local-key", ApiKeys.For(data, new Uri("http://127.0.0.1:1234/v1")));
        Assert.Equal("", ApiKeys.For(new AppSettingsData { LlmApiKey = "local-key" }, OpenAIApi.BaseUrl));
        Assert.True(OpenAIApi.IsOpenAIApi("https://API.openai.com"));
        Assert.False(OpenAIApi.IsOpenAIApi("https://openai.com.example/v1"));
        Assert.False(OpenAIApi.IsOpenAIApi("not a url"));

        if (OperatingSystem.IsWindows())
        {
            string stored = OpenAIApi.Protect("  sk-openai-secret ", out string? error);
            Assert.Null(error);
            Assert.StartsWith(Sql.WindowsCredentials.ProtectedPrefix, stored);
            Assert.Equal("sk-openai-secret", OpenAIApi.Key(new AppSettingsData { OpenAIApiKey = stored }));
        }
    }

    [Fact]
    public void Settings_TheOpenAITab_HoldsItsFiveRows()
    {
        var tab = SettingsMenu.TabFields[(int)SettingsTab.OpenAI];
        Assert.Equal(SettingsMenu.OpenAITabTitle, SettingsMenu.TabTitles[(int)SettingsTab.OpenAI]);
        Assert.Equal([SettingsField.OpenAIApi, SettingsField.OpenAIApiKey, SettingsField.OpenAIApiMaxTokens, SettingsField.OpenAIApiOrganization, SettingsField.OpenAIApiProject], tab);
        Assert.Equal(["OpenAI API", "OpenAI API key", "OpenAI API max tokens", "OpenAI API organization", "OpenAI API project"], tab.Select(SettingsMenu.FieldName));
        Assert.All(tab, f => Assert.True(SettingsMenu.IsLlmField(f) && SettingsMenu.RefusedMidTurn(f)));
        Assert.True(SettingsMenu.IsToggle(SettingsField.OpenAIApi));

        var data = new AppSettingsData();
        Assert.Equal(["off", "(none)", SettingsMenu.OpenAIApiMaxTokensNoneLabel, SettingsMenu.OpenAIApiHeaderNoneLabel, SettingsMenu.OpenAIApiHeaderNoneLabel], tab.Select(f => SettingsMenu.FieldValue(f, data, "C:\\p")));
        var set = new AppSettingsData { OpenAIApi = true, OpenAIApiKey = "sk-openai", OpenAIApiMaxTokens = 4096, OpenAIApiOrganization = "org-1", OpenAIApiProject = "proj-1" };
        Assert.Equal(["on", SettingsMenu.Mask("sk-openai"), "4,096 tokens", "org-1", "proj-1"], tab.Select(f => SettingsMenu.FieldValue(f, set, "C:\\p")));
        Assert.Equal("", SettingsMenu.EditableValue(SettingsField.OpenAIApiKey, set));

        var copy = AppSettings.Copy(set);
        Assert.Equal((true, "sk-openai", 4096, "org-1", "proj-1"), (copy.OpenAIApi, copy.OpenAIApiKey, copy.OpenAIApiMaxTokens, copy.OpenAIApiOrganization, copy.OpenAIApiProject));
        Assert.Equal(["OpenAIApiKey: " + SettingsDiff.Redacted], SettingsDiff.Changes(new AppSettingsData(), new AppSettingsData { OpenAIApiKey = "secret" }));
    }

    [Fact]
    public void Environment_OffersTheOpenAIApi_AndNeverLogsItsKey()
    {
        var variables = new Dictionary<string, string>
        {
            [EnvironmentOverrides.OpenAIApiVariable] = "on",
            [EnvironmentOverrides.OpenAIApiKeyVariable] = "sk-openai-env",
        };
        var environment = new EnvironmentOverrides(name => variables.GetValueOrDefault(name));

        var effective = environment.ApplyTo(new AppSettingsData());

        Assert.True(OpenAIApi.Offered(effective));
        Assert.Equal("sk-openai-env", OpenAIApi.Key(effective));
        Assert.Equal($"{EnvironmentOverrides.OpenAIApiVariable}=on, {EnvironmentOverrides.OpenAIApiKeyVariable}={EnvironmentOverrides.SecretSet}", environment.Describe());
    }
}
