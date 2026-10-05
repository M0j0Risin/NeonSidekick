using System.Net;
using Microsoft.Extensions.AI;
using NeonSidekick.Llm;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

public class OpenAICompatibleChatClientTests
{
    private static LlmEndpoint Endpoint(string url = "http://127.0.0.1:1234", string model = "my-model", string key = "k") =>
        new(new Uri(url), model, key, "test");

    [Fact]
    public void Endpoint_IsNormalised_AndBlankKeyBecomesEmpty()
    {
        using var client = new OpenAICompatibleChatClient(Endpoint("http://127.0.0.1:1234/v1/", key: " "), TimeSpan.FromSeconds(5));
        Assert.Equal("http://127.0.0.1:1234/v1", client.Endpoint.BaseUrl.AbsoluteUri);
        Assert.Equal("empty", client.Endpoint.ApiKey);
        Assert.Equal("my-model", client.Endpoint.ModelId);
    }

    /// <summary>
    /// The setting has to reach the transport, not merely be saved.
    /// </summary>
    [Fact]
    public void RequestTimeout_ReachesTheOwnedTransport()
    {
        using var client = new OpenAICompatibleChatClient(Endpoint(), TimeSpan.FromSeconds(210));
        Assert.Equal(TimeSpan.FromSeconds(210), client.AppliedRequestTimeout);
        Assert.True(client.OwnsTransport);
    }

    [Fact]
    public void CallerSuppliedTransport_IsNotOwned_AndNotDisposed()
    {
        var stub = new StubHttpMessageHandler().Map("http://127.0.0.1:1234/v1/models", HttpStatusCode.OK, "{}");
        using var http = new HttpClient(stub);

        var client = new OpenAICompatibleChatClient(Endpoint(), TimeSpan.FromSeconds(5), http);
        Assert.False(client.OwnsTransport);
        Assert.Equal(TimeSpan.FromSeconds(5), client.AppliedRequestTimeout);
        client.Dispose();

        // Still usable after the client that borrowed it is gone.
        var ex = Record.Exception(() => http.GetAsync("http://127.0.0.1:1234/v1/models").GetAwaiter().GetResult().Dispose());
        Assert.Null(ex);
    }

    [Fact]
    public void GetService_IChatClient_ReturnsTheWrapper()
    {
        using var client = new OpenAICompatibleChatClient(Endpoint(), TimeSpan.FromSeconds(5));
        Assert.Same(client, client.GetService(typeof(IChatClient)));
    }

    [Fact]
    public void Constructor_RejectsBlankModel_AndNonPositiveTimeout()
    {
        Assert.Throws<ArgumentException>(() => new OpenAICompatibleChatClient(Endpoint(model: " "), TimeSpan.FromSeconds(5)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OpenAICompatibleChatClient(Endpoint(), TimeSpan.Zero));
        Assert.Throws<ArgumentNullException>(() => new OpenAICompatibleChatClient(null!, TimeSpan.FromSeconds(5)));
    }

    /// <summary>
    /// One attempt per request. The SDK's default three retries would multiply the request
    /// ceiling past the turn budget and repeat a refused connection four times over.
    /// </summary>
    [Fact]
    public async Task FailedRequest_IsNotRetried()
    {
        var stub = new StubHttpMessageHandler(); // nothing mapped: every request is refused
        using var http = new HttpClient(stub);
        using var client = new OpenAICompatibleChatClient(Endpoint(), TimeSpan.FromSeconds(5), http);

        await Assert.ThrowsAnyAsync<Exception>(() => client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "ping") }));

        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task GetResponseAsync_PostsToChatCompletions_WithModelAndBearer()
    {
        var stub = new StubHttpMessageHandler()
            .Map("http://127.0.0.1:1234/v1/chat/completions", HttpStatusCode.OK, StubHttpMessageHandler.CompletionJson("pong", "my-model"));
        using var http = new HttpClient(stub);
        using var client = new OpenAICompatibleChatClient(Endpoint(key: "sekrit"), TimeSpan.FromSeconds(5), http);

        var response = await client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "ping") });

        Assert.Equal("pong", response.Text);
        var request = Assert.Single(stub.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("http://127.0.0.1:1234/v1/chat/completions", request.Uri.AbsoluteUri);
        Assert.Equal("Bearer sekrit", request.Authorization);
        Assert.Contains("\"model\":\"my-model\"", request.Body);
        Assert.Contains("ping", request.Body);
        Assert.DoesNotContain("\"tools\"", request.Body);
        Assert.DoesNotContain("reasoning_effort", request.Body);       // no level asked for: the server decides
        Assert.DoesNotContain("chat_template_kwargs", request.Body);
    }

    /// <summary>
    /// The carrier a <c>view_image</c> result is followed by: on the wire it is a user message with
    /// a text part and an <c>image_url</c> data URL, and its tag never leaves the process. The tool
    /// message ahead of it goes out as text alone — the adapter has no other shape for one.
    /// </summary>
    [Fact]
    public async Task ACarrierMessage_GoesOutAsAnImageUrlPart_WithoutTheTag()
    {
        var stub = new StubHttpMessageHandler().Map("http://127.0.0.1:1234/v1/chat/completions", HttpStatusCode.OK, StubHttpMessageHandler.CompletionJson("a square", "my-model"));
        using var http = new HttpClient(stub);
        using var client = new OpenAICompatibleChatClient(Endpoint(), TimeSpan.FromSeconds(5), http);
        var history = new ConversationHistory("sys");
        history.AddUser("look");
        history.AddMessage(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c1", "view_image", new Dictionary<string, object?> { ["path"] = "pic.png" })]));
        history.AddToolResults([new FunctionResultContent("c1", "pic.png (2×2 image/png, 70 B): the picture is in the next message")]);
        history.AddToolImages([new NeonSidekick.Files.ImageAttachment("pic.png", [0x89, 0x50, 0x4E, 0x47], NeonSidekick.Files.ImageFile.Png, 2, 2)]);

        await client.GetResponseAsync(history.BuildRequest());

        string body = Assert.Single(stub.Requests).Body!;
        Assert.Contains("\"role\":\"tool\"", body);
        Assert.Contains("\"image_url\"", body);
        Assert.Contains("data:image/png;base64,iVBORw==", body);
        Assert.Contains(ConversationHistory.ImageCarrierText(["pic.png"]), body);
        Assert.DoesNotContain(ConversationHistory.CarrierKey, body);
        Assert.True(body.LastIndexOf("\"role\":\"tool\"", StringComparison.Ordinal) < body.IndexOf("image_url", StringComparison.Ordinal));
    }

    // ── Reasoning effort on the wire ────────────────────────────────────────

    private static async Task<string> BodyFor(ChatOptions? options)
    {
        var stub = new StubHttpMessageHandler().Map("http://127.0.0.1:1234/v1/chat/completions", HttpStatusCode.OK, StubHttpMessageHandler.CompletionJson("pong", "my-model"));
        using var http = new HttpClient(stub);
        using var client = new OpenAICompatibleChatClient(Endpoint(), TimeSpan.FromSeconds(5), http);

        await client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "ping") }, options);

        return Assert.Single(stub.Requests).Body!;
    }

    /// <summary>
    /// <c>none</c> is the level that has to reach two kinds of server: <c>reasoning_effort</c>
    /// for the ones that map it, and the Qwen template switch for the ones that do not.
    /// </summary>
    [Fact]
    public async Task ReasoningNone_SendsTheEffort_AndTheQwenTemplateSwitch()
    {
        string body = await BodyFor(new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.None } });

        Assert.Contains("\"reasoning_effort\":\"none\"", body);
        Assert.Contains("\"chat_template_kwargs\":{\"enable_thinking\":false}", body);
        Assert.Contains("ping", body);                    // the messages survived the pre-built options
        Assert.Contains("\"model\":\"my-model\"", body);
    }

    [Theory]
    [InlineData(ReasoningEffort.Low, "low")]
    [InlineData(ReasoningEffort.Medium, "medium")]
    [InlineData(ReasoningEffort.High, "high")]
    [InlineData(ReasoningEffort.ExtraHigh, "xhigh")]
    public async Task OtherLevels_SendOnlyTheEffort(ReasoningEffort effort, string wire)
    {
        string body = await BodyFor(new ChatOptions { Reasoning = new ReasoningOptions { Effort = effort } });

        Assert.Contains($"\"reasoning_effort\":\"{wire}\"", body);
        Assert.DoesNotContain("chat_template_kwargs", body);
    }

    [Fact]
    public async Task Streaming_ShapesTheRequestTheSameWay()
    {
        var stub = new StubHttpMessageHandler().Map("http://127.0.0.1:1234/v1/chat/completions", HttpStatusCode.OK, StubHttpMessageHandler.CompletionJson("pong", "my-model"));
        using var http = new HttpClient(stub);
        using var client = new OpenAICompatibleChatClient(Endpoint(), TimeSpan.FromSeconds(5), http);

        // The stub answers with a non-streamed body; the adapter may fail to parse it as SSE, which is not what this test is about.
        try
        {
            await foreach (var _ in client.GetStreamingResponseAsync(new[] { new ChatMessage(ChatRole.User, "ping") }, new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.None } })) { }
        }
        catch (Exception) { /* see above */ }

        string body = Assert.Single(stub.Requests).Body!;
        Assert.Contains("\"reasoning_effort\":\"none\"", body);
        Assert.Contains("\"chat_template_kwargs\":{\"enable_thinking\":false}", body);
    }

    /// <summary>
    /// A server that validates the level answers 400 with a reason (SGLang + Qwen3.8 rejects
    /// <c>high</c>: "Supported types are xhigh (default), medium, and low"); the turn's notice
    /// must carry that reason, not the SDK's "Service request failed".
    /// </summary>
    [Fact]
    public async Task RejectedRequest_ExplainsWithTheServersMessage()
    {
        const string body = "{\"object\":\"error\",\"message\":\"Unexpected reasoning effort high. Supported types are xhigh (default), medium, and low.\",\"type\":\"BadRequestError\",\"param\":null,\"code\":400}";
        var stub = new StubHttpMessageHandler().Map("http://127.0.0.1:1234/v1/chat/completions", HttpStatusCode.BadRequest, body);
        using var http = new HttpClient(stub);
        using var client = new OpenAICompatibleChatClient(Endpoint(), TimeSpan.FromSeconds(5), http);
        var assistant = new Assistant(client, new ConversationHistory("sys"), new LlmTimeouts(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)), reasoning: ReasoningEffort.High);

        var events = new List<TurnEvent>();
        await foreach (var evt in assistant.RunTurnAsync("hi")) events.Add(evt);

        var notice = Assert.IsType<TurnEvent.Notice>(Assert.Single(events));
        Assert.True(notice.IsError);
        Assert.Equal("Model error: ClientResultException: HTTP 400 (Bad Request): Unexpected reasoning effort high. Supported types are xhigh (default), medium, and low.", notice.Text);
        Assert.Contains("\"reasoning_effort\":\"high\"", Assert.Single(stub.Requests).Body);
    }

    /// <summary>
    /// A model whose chat template renders no <c>tool</c> role (Dolphin-Mistral-24B-Venice on SGLang,
    /// 2026-09-14) answers the opening call/result pairs with 400 and the template's own sentence; the
    /// notice names the setting that talks to it anyway.
    /// </summary>
    [Fact]
    public async Task ToolRoleRejected_NamesTheLlmToolsSetting()
    {
        const string body = "{\"object\":\"error\",\"message\":\"Only user, system and assistant roles are supported!\",\"type\":\"BadRequestError\",\"param\":null,\"code\":400}";
        var stub = new StubHttpMessageHandler().Map("http://127.0.0.1:1234/v1/chat/completions", HttpStatusCode.BadRequest, body);
        using var http = new HttpClient(stub);
        using var client = new OpenAICompatibleChatClient(Endpoint(), TimeSpan.FromSeconds(5), http);
        var assistant = new Assistant(client, new ConversationHistory("sys"), new LlmTimeouts(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)), tools: [new EchoTool()]);

        var events = new List<TurnEvent>();
        await foreach (var evt in assistant.RunTurnAsync("hi")) events.Add(evt);

        var notice = Assert.IsType<TurnEvent.Notice>(Assert.Single(events));
        Assert.True(notice.IsError);
        Assert.Equal(
            "Model error: ClientResultException: HTTP 400 (Bad Request): Only user, system and assistant roles are supported! — this model's chat template accepts no tool messages; turn the setting LLM offer tools off (the LLM tab of /settings) to talk to it without tools",
            notice.Text);
    }

    [Fact]
    public void WithRawFields_LeavesEveryOtherCaseAlone()
    {
        Assert.Null(OpenAICompatibleChatClient.WithRawFields(null));

        var plain = new ChatOptions();
        Assert.Same(plain, OpenAICompatibleChatClient.WithRawFields(plain));

        var high = new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.High } };
        Assert.Same(high, OpenAICompatibleChatClient.WithRawFields(high));

        Func<IChatClient, object?> mine = _ => null;
        var supplied = new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.None }, RawRepresentationFactory = mine };
        Assert.Same(supplied, OpenAICompatibleChatClient.WithRawFields(supplied));   // a caller's factory wins

        var none = new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.None }, Tools = new List<AITool>() };
        var shaped = OpenAICompatibleChatClient.WithRawFields(none)!;
        Assert.NotSame(none, shaped);
        Assert.Null(none.RawRepresentationFactory);              // the caller's instance is not mutated
        Assert.NotNull(shaped.RawRepresentationFactory);
        Assert.Equal(ReasoningEffort.None, shaped.Reasoning!.Effort);
        Assert.NotNull(shaped.Tools);
    }

    /// <summary>The preserve key is taken off the options, the caller's instance and its other properties left alone; a caller's factory still wins.</summary>
    [Fact]
    public void WithRawFields_TakesThePreserveKeyOff()
    {
        var options = new ChatOptions { AdditionalProperties = new AdditionalPropertiesDictionary { [OpenAICompatibleChatClient.PreserveThinkingKey] = true, ["other"] = 1 } };
        var shaped = OpenAICompatibleChatClient.WithRawFields(options)!;
        Assert.False(shaped.AdditionalProperties!.ContainsKey(OpenAICompatibleChatClient.PreserveThinkingKey));
        Assert.Equal(1, shaped.AdditionalProperties["other"]);
        Assert.True(options.AdditionalProperties.ContainsKey(OpenAICompatibleChatClient.PreserveThinkingKey));
        Assert.NotNull(shaped.RawRepresentationFactory);

        Func<IChatClient, object?> mine = _ => null;
        var supplied = new ChatOptions { RawRepresentationFactory = mine, AdditionalProperties = new AdditionalPropertiesDictionary { [OpenAICompatibleChatClient.PreserveThinkingKey] = true } };
        var kept = OpenAICompatibleChatClient.WithRawFields(supplied)!;
        Assert.Same(mine, kept.RawRepresentationFactory);
        Assert.Null(kept.AdditionalProperties);

        var offKey = new ChatOptions { AdditionalProperties = new AdditionalPropertiesDictionary { [OpenAICompatibleChatClient.PreserveThinkingKey] = false } };
        var plain = OpenAICompatibleChatClient.WithRawFields(offKey)!;
        Assert.Null(plain.RawRepresentationFactory);
        Assert.Null(plain.AdditionalProperties);
    }

    // ── Thinking sent back (2026-09-28) ─────────────────────────────────────

    private static ChatOptions Preserving(ReasoningEffort? effort = null) => new()
    {
        Reasoning = effort is { } e ? new ReasoningOptions { Effort = e } : null,
        AdditionalProperties = new AdditionalPropertiesDictionary { [OpenAICompatibleChatClient.PreserveThinkingKey] = true },
    };

    private static async Task<string> BodyFor(IEnumerable<ChatMessage> messages, ChatOptions? options = null)
    {
        var stub = new StubHttpMessageHandler().Map("http://127.0.0.1:1234/v1/chat/completions", HttpStatusCode.OK, StubHttpMessageHandler.CompletionJson("pong", "my-model"));
        using var http = new HttpClient(stub);
        using var client = new OpenAICompatibleChatClient(Endpoint(), TimeSpan.FromSeconds(5), http);

        await client.GetResponseAsync(messages, options);

        return Assert.Single(stub.Requests).Body!;
    }

    /// <summary>An earlier turn (thinking + text) and the turn in flight (thinking + a call, its result).</summary>
    private static List<ChatMessage> TwoTurns() =>
    [
        new(ChatRole.System, "sys"),
        new(ChatRole.User, "first question"),
        new(ChatRole.Assistant, [new TextReasoningContent("old musing"), new TextContent("first answer")]),
        new(ChatRole.User, "second question"),
        new(ChatRole.Assistant, [new TextReasoningContent("fresh musing"), new FunctionCallContent("c1", "echo", new Dictionary<string, object?> { ["text"] = "hi" })]),
        new(ChatRole.Tool, [new FunctionResultContent("c1", "hi")]),
    ];

    [Fact]
    public async Task TheTurnInFlight_SendsItsThinkingBack_WithItsCallsIntact()
    {
        var messages = TwoTurns();
        string body = await BodyFor(messages);

        using var document = System.Text.Json.JsonDocument.Parse(body);
        var wire = document.RootElement.GetProperty("messages");
        var inFlight = wire[4];
        Assert.Equal("assistant", inFlight.GetProperty("role").GetString());
        Assert.Equal("fresh musing", inFlight.GetProperty("reasoning_content").GetString());
        var call = Assert.Single(inFlight.GetProperty("tool_calls").EnumerateArray());
        Assert.Equal("c1", call.GetProperty("id").GetString());
        Assert.Equal("echo", call.GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("{\"text\":\"hi\"}", call.GetProperty("function").GetProperty("arguments").GetString());
        Assert.Equal("tool", wire[5].GetProperty("role").GetString());

        // The earlier turn goes as it always did, and nothing asks the template to keep it.
        Assert.False(wire[2].TryGetProperty("reasoning_content", out _));
        Assert.DoesNotContain("old musing", body);
        Assert.DoesNotContain("chat_template_kwargs", body);

        // The history's own messages are not touched.
        Assert.All(messages, m => Assert.Null(m.RawRepresentation));
    }

    [Fact]
    public async Task PreserveThinking_SendsEveryTurnsThinking_AndAsksTheTemplateToKeepIt()
    {
        string body = await BodyFor(TwoTurns(), Preserving());

        using var document = System.Text.Json.JsonDocument.Parse(body);
        var wire = document.RootElement.GetProperty("messages");
        Assert.Equal("old musing", wire[2].GetProperty("reasoning_content").GetString());
        Assert.Equal("first answer", wire[2].GetProperty("content").GetString());
        Assert.Equal("fresh musing", wire[4].GetProperty("reasoning_content").GetString());
        Assert.Contains("\"chat_template_kwargs\":{\"preserve_thinking\":true,\"clear_thinking\":false}", body);
        Assert.DoesNotContain(OpenAICompatibleChatClient.PreserveThinkingKey, body);
    }

    [Fact]
    public async Task PreserveThinking_WithReasoningNone_SendsBothSwitchesInOneObject()
    {
        string body = await BodyFor([new ChatMessage(ChatRole.User, "ping")], Preserving(ReasoningEffort.None));

        Assert.Contains("\"reasoning_effort\":\"none\"", body);
        Assert.Contains("\"chat_template_kwargs\":{\"enable_thinking\":false,\"preserve_thinking\":true,\"clear_thinking\":false}", body);
    }

    /// <summary>A message with no thinking, or only the Anthropic API's signed thinking, goes through the adapter as before.</summary>
    [Fact]
    public void WithReasoningBack_LeavesMessagesWithoutThinkingAlone()
    {
        List<ChatMessage> messages =
        [
            new(ChatRole.User, "q"),
            new(ChatRole.Assistant, [new TextContent("plain")]),
            new(ChatRole.Assistant, [new TextReasoningContent("signed") { ProtectedData = "sig" }, new TextContent("claude")]),
        ];

        Assert.Same(messages, OpenAICompatibleChatClient.WithReasoningBack(messages, preserveAll: true));
    }

    [Fact]
    public void AssistantWithThinking_TextOnly_HasNoToolCalls()
    {
        var raw = OpenAICompatibleChatClient.AssistantWithThinking(new ChatMessage(ChatRole.Assistant, [new TextReasoningContent("t"), new TextContent("answer")]), "t");
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new System.Text.Json.Utf8JsonWriter(buffer))
        {
            ((System.ClientModel.Primitives.IJsonModel<OpenAI.Chat.ChatMessage>)raw).Write(writer, System.ClientModel.Primitives.ModelReaderWriterOptions.Json);
        }

        string json = System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
        Assert.Contains("\"reasoning_content\":\"t\"", json);
        Assert.Contains("answer", json);
        Assert.DoesNotContain("tool_calls", json);
    }

    /// <summary>A refused key (2026-10-04, the UI review): one line with the way on, never the exception chain.</summary>
    [Fact]
    public async Task RefusedKey_IsOneLineWithTheWayOn()
    {
        var stub = new StubHttpMessageHandler().Map("http://127.0.0.1:1234/v1/chat/completions", HttpStatusCode.Unauthorized, "{\"error\":{\"message\":\"bad key\"}}");
        using var http = new HttpClient(stub);
        using var client = new OpenAICompatibleChatClient(Endpoint(), TimeSpan.FromSeconds(5), http);
        var assistant = new Assistant(client, new ConversationHistory("sys"), new LlmTimeouts(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)));

        var events = new List<TurnEvent>();
        await foreach (var evt in assistant.RunTurnAsync("hi")) events.Add(evt);

        var notice = Assert.IsType<TurnEvent.Notice>(Assert.Single(events));
        Assert.True(notice.IsError);
        Assert.StartsWith("The LLM server refused the key (HTTP 401", notice.Text);
        Assert.EndsWith(ModelErrorText.KeyNextStep, notice.Text);
    }
}
