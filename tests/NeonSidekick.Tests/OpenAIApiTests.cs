using System.Net;
using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Anthropic;
using NeonSidekick.Llm.OpenAIPlatform;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>The OpenAI API as a server (2026-10-03): the Responses request, the stream, the client, the usage and cost, the probe, the session, the settings.</summary>
public class OpenAIApiTests
{
    private const string Responses = "https://api.openai.com/v1/responses";

    private static LlmEndpoint Endpoint(string model = "gpt-5.6-sol") => new(OpenAIApi.BaseUrl, model, "sk-openai-test", "configured");

    /// <summary>The events as the API streams them (the shapes captured from gpt-5.4-nano on 2026-10-03), one per entry.</summary>
    private static string Sse(params string[] events) =>
        string.Concat(events.Select(e => "event: " + JsonDocument.Parse(e).RootElement.GetProperty("type").GetString() + "\ndata: " + e + "\n\n"));

    private const string Created = "{\"type\":\"response.created\",\"response\":{\"id\":\"resp_1\",\"model\":\"gpt-5.6-sol-2026-06-01\",\"status\":\"in_progress\"}}";

    private static string Completed(string usage = "{\"input_tokens\":10,\"input_tokens_details\":{\"cached_tokens\":0},\"output_tokens\":2,\"output_tokens_details\":{\"reasoning_tokens\":0},\"total_tokens\":12}", string type = "response.completed", string incomplete = "null") =>
        "{\"type\":\"" + type + "\",\"response\":{\"id\":\"resp_1\",\"model\":\"gpt-5.6-sol-2026-06-01\",\"status\":\"completed\",\"incomplete_details\":" + incomplete + ",\"usage\":" + usage + "}}";

    private static string TextDelta(string text) => "{\"type\":\"response.output_text.delta\",\"item_id\":\"msg_1\",\"output_index\":0,\"content_index\":0,\"delta\":" + "\"" + JsonEncodedText.Encode(text) + "\"" + "}";

    /// <summary>A reasoning item with a two-part summary, then a call: what a tool turn at a reasoning level streams.</summary>
    private static string ToolTurn() => Sse(
        Created,
        "{\"type\":\"response.output_item.added\",\"item\":{\"id\":\"rs_1\",\"type\":\"reasoning\",\"summary\":[]},\"output_index\":0}",
        "{\"type\":\"response.reasoning_summary_part.added\",\"item_id\":\"rs_1\",\"output_index\":0,\"summary_index\":0,\"part\":{\"type\":\"summary_text\",\"text\":\"\"}}",
        "{\"type\":\"response.reasoning_summary_text.delta\",\"item_id\":\"rs_1\",\"output_index\":0,\"summary_index\":0,\"delta\":\"**Checking**\"}",
        "{\"type\":\"response.reasoning_summary_part.added\",\"item_id\":\"rs_1\",\"output_index\":0,\"summary_index\":1,\"part\":{\"type\":\"summary_text\",\"text\":\"\"}}",
        "{\"type\":\"response.reasoning_summary_text.delta\",\"item_id\":\"rs_1\",\"output_index\":0,\"summary_index\":1,\"delta\":\"Now the clock.\"}",
        "{\"type\":\"response.output_item.done\",\"item\":{\"id\":\"rs_1\",\"type\":\"reasoning\",\"encrypted_content\":\"ENC123\",\"summary\":[{\"type\":\"summary_text\",\"text\":\"**Checking**\"}]},\"output_index\":0}",
        "{\"type\":\"response.output_item.added\",\"item\":{\"id\":\"fc_1\",\"type\":\"function_call\",\"call_id\":\"call_1\",\"name\":\"get_current_time\",\"arguments\":\"\"},\"output_index\":1}",
        "{\"type\":\"response.function_call_arguments.delta\",\"item_id\":\"fc_1\",\"output_index\":1,\"delta\":\"{\\\"timezone\\\":\"}",
        "{\"type\":\"response.function_call_arguments.done\",\"item_id\":\"fc_1\",\"output_index\":1,\"arguments\":\"{\\\"timezone\\\":\\\"Asia/Tokyo\\\"}\"}",
        "{\"type\":\"response.output_item.done\",\"item\":{\"id\":\"fc_1\",\"type\":\"function_call\",\"status\":\"completed\",\"call_id\":\"call_1\",\"name\":\"get_current_time\",\"arguments\":\"{\\\"timezone\\\":\\\"Asia/Tokyo\\\"}\"},\"output_index\":1}",
        Completed("{\"input_tokens\":10000,\"input_tokens_details\":{\"cached_tokens\":8000,\"cache_write_tokens\":1000},\"output_tokens\":500,\"output_tokens_details\":{\"reasoning_tokens\":300},\"total_tokens\":10500}"));

    private static string Answer(string text = "Four.") => Sse(Created, TextDelta(text), Completed());

    private static (OpenAIApiChatClient Client, StubHttpMessageHandler Stub) Client(string model = "gpt-5.6-sol", int maxTokens = 0, params string[] streams)
    {
        var queue = new Queue<string>(streams.Length == 0 ? [Answer()] : streams);
        var stub = new StubHttpMessageHandler();
        stub.Map(Responses, (_, _) => Task.FromResult(StubHttpMessageHandler.Json(HttpStatusCode.OK, queue.Count > 1 ? queue.Dequeue() : queue.Peek(), "text/event-stream")));
        return (new OpenAIApiChatClient(Endpoint(model), TimeSpan.FromSeconds(10), maxTokens, httpClient: new HttpClient(stub), time: new ManualTimeProvider()), stub);
    }

    private static JsonElement Body(IReadOnlyList<ChatMessage> messages, ChatOptions? options = null, string model = "gpt-5.6-sol", int maxTokens = 0, bool withoutReasoning = false)
    {
        using var document = JsonDocument.Parse(OpenAIRequest.Write(messages, options, model, maxTokens, withoutReasoning));
        return document.RootElement.Clone();
    }

    private static string[] Types(JsonElement body) =>
        body.GetProperty("input").EnumerateArray().Select(i => i.GetProperty("type").GetString() + (i.TryGetProperty("role", out var role) ? ":" + role.GetString() : "")).ToArray();

    // ── The request ─────────────────────────────────────────────────────────

    [Fact]
    public void Request_IsStateless_TheSystemPromptIsTheInstructions_AndNoSamplingGoes()
    {
        var options = new ChatOptions
        {
            Temperature = 0.7f,
            TopP = 0.9f,
            TopK = 40,
            Reasoning = new ReasoningOptions { Effort = ReasoningEffort.High },
            AdditionalProperties = new() { [OpenAICompatibleChatClient.PreserveThinkingKey] = true },
        };

        var body = Body([new(ChatRole.System, "You are Neon."), new(ChatRole.User, "hi"), new(ChatRole.System, "Be brief.")], options, maxTokens: 4096);

        Assert.Equal("gpt-5.6-sol", body.GetProperty("model").GetString());
        Assert.True(body.GetProperty("stream").GetBoolean());
        Assert.False(body.GetProperty("store").GetBoolean());
        Assert.Equal("You are Neon.\n\nBe brief.", body.GetProperty("instructions").GetString());
        Assert.Equal(4096, body.GetProperty("max_output_tokens").GetInt32());
        Assert.Equal("high", body.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.Equal("auto", body.GetProperty("reasoning").GetProperty("summary").GetString());
        Assert.Equal(["reasoning.encrypted_content"], body.GetProperty("include").EnumerateArray().Select(e => e.GetString()));
        foreach (string field in new[] { "temperature", "top_p", "top_k", "chat_template_kwargs", "messages" })
        {
            Assert.False(body.TryGetProperty(field, out _), field);
        }

        Assert.Equal(["message:user"], Types(body));
    }

    [Fact]
    public void Request_NoLevel_SendsNoEffort_ButAsksForTheSummary_AndNoCapNoField()
    {
        var body = Body([new(ChatRole.User, "x")]);
        Assert.False(body.GetProperty("reasoning").TryGetProperty("effort", out _));
        Assert.Equal("auto", body.GetProperty("reasoning").GetProperty("summary").GetString());
        Assert.False(body.TryGetProperty("max_output_tokens", out _));
        Assert.False(body.TryGetProperty("instructions", out _));
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
    public void Request_ShapesTheReasoningLevelForTheModel(string model, ReasoningEffort effort, string expected)
    {
        Assert.Equal(expected, OpenAIModelRules.For(model).EffortWord(effort));
        Assert.Equal(expected, Body([new(ChatRole.User, "x")], new ChatOptions { Reasoning = new ReasoningOptions { Effort = effort } }, model).GetProperty("reasoning").GetProperty("effort").GetString());
    }

    [Fact]
    public void Request_AModelThatDoesNotReason_GetsNoReasoningAtAll()
    {
        var body = Body([new(ChatRole.User, "x")], new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.High } }, "gpt-4.1-mini");
        Assert.False(body.TryGetProperty("reasoning", out _));
        Assert.False(body.TryGetProperty("include", out _));
    }

    [Fact]
    public void Request_Tools_AreFunctions_NotStrict_AndANameTheApiRefusesIsSanitised()
    {
        var tool = AIFunctionFactoryFree("weird.tool/name", "Does it.");
        var body = Body([new(ChatRole.User, "x")], new ChatOptions { Tools = [new GetCurrentTimeTool(TimeProvider.System), tool] });

        var tools = body.GetProperty("tools").EnumerateArray().ToList();
        Assert.Equal("function", tools[0].GetProperty("type").GetString());
        Assert.Equal(GetCurrentTimeTool.ToolName, tools[0].GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Object, tools[0].GetProperty("parameters").ValueKind);
        Assert.False(tools[0].GetProperty("strict").GetBoolean());
        Assert.Equal(AnthropicRequest.ToolName("weird.tool/name"), tools[1].GetProperty("name").GetString());
    }

    [Fact]
    public void Request_TheToolLoop_ReasoningOnlyInFlight_CallsAnswered_AndThePhaseWorkedOut()
    {
        ChatMessage[] history =
        [
            new(ChatRole.User, "first"),
            new(ChatRole.Assistant, [new TextReasoningContent("old"), new TextReasoningContent("") { ProtectedData = OpenAIRequest.EncryptedPrefix + "OLD" }, new TextContent("Done before.")]),
            new(ChatRole.User, "now"),
            new(ChatRole.Assistant, [new TextReasoningContent("hm"), new TextReasoningContent("") { ProtectedData = OpenAIRequest.EncryptedPrefix + "NEW" }, new TextContent("Let me look."), new FunctionCallContent("call_1", "get_current_time", new Dictionary<string, object?> { ["timezone"] = "UTC" })]),
            new(ChatRole.Tool, [new FunctionResultContent("call_1", "12:00")]),
            new(ChatRole.Assistant, [new TextReasoningContent("") { ProtectedData = "SIG-FROM-CLAUDE" }, new FunctionCallContent("call_2", "get_current_time")]),
        ];

        var body = Body(history);

        Assert.Equal(["message:user", "message:assistant", "message:user", "reasoning", "message:assistant", "function_call", "function_call_output", "function_call", "function_call_output"], Types(body));
        var input = body.GetProperty("input").EnumerateArray().ToList();
        Assert.Equal(OpenAIRequest.FinalAnswerPhase, input[1].GetProperty("phase").GetString());
        Assert.Equal("NEW", input[3].GetProperty("encrypted_content").GetString());   // the old turn's went, as the Claude API's signed thinking
        Assert.Equal(OpenAIRequest.CommentaryPhase, input[4].GetProperty("phase").GetString());   // text beside a call is the preamble
        Assert.Equal("{\"timezone\":\"UTC\"}", input[5].GetProperty("arguments").GetString());
        Assert.Equal(("call_1", "12:00"), (input[6].GetProperty("call_id").GetString(), input[6].GetProperty("output").GetString()));
        Assert.Equal(("call_2", AnthropicRequest.MissingResult), (input[8].GetProperty("call_id").GetString(), input[8].GetProperty("output").GetString()));
        Assert.DoesNotContain("SIG-FROM-CLAUDE", body.GetRawText());   // the Claude API's signature is not this wire's

        var stripped = Body(history, withoutReasoning: true);
        Assert.DoesNotContain("reasoning", Types(stripped));
    }

    [Fact]
    public void Request_AResultWithNoCall_IsText_AndPicturesArePartsOfTheUserMessage()
    {
        var body = Body(
        [
            new(ChatRole.Tool, [new FunctionResultContent("ghost", "late")]),
            new(ChatRole.User, [new TextContent("look"), new DataContent(new byte[] { 1, 2, 3 }, "image/png"), new DataContent(new byte[] { 4 }, "application/pdf") { Name = "a.pdf" }]),
        ]);

        var input = body.GetProperty("input").EnumerateArray().ToList();
        Assert.Equal("Tool result: late", input[0].GetProperty("content")[0].GetProperty("text").GetString());
        var parts = input[1].GetProperty("content").EnumerateArray().ToList();
        Assert.Equal(["input_text", "input_image", "input_file"], parts.Select(p => p.GetProperty("type").GetString()));
        Assert.Equal("data:image/png;base64,AQID", parts[1].GetProperty("image_url").GetString());
        Assert.Equal(("a.pdf", "data:application/pdf;base64,BA=="), (parts[2].GetProperty("filename").GetString(), parts[2].GetProperty("file_data").GetString()));
    }

    [Fact]
    public void Request_AJsonSchema_IsTheTextFormat()
    {
        using var schema = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{\"n\":{\"type\":\"integer\"}}}");
        var body = Body([new(ChatRole.User, "x")], new ChatOptions { ResponseFormat = ChatResponseFormat.ForJsonSchema(schema.RootElement.Clone(), "answer") });

        var format = body.GetProperty("text").GetProperty("format");
        Assert.Equal(("json_schema", "answer", false), (format.GetProperty("type").GetString(), format.GetProperty("name").GetString(), format.GetProperty("strict").GetBoolean()));
        Assert.Equal("integer", format.GetProperty("schema").GetProperty("properties").GetProperty("n").GetProperty("type").GetString());
    }

    // ── The client and the stream ───────────────────────────────────────────

    [Fact]
    public async Task Client_SendsTheKeyAsABearer_AndTheOrganizationAndProject()
    {
        string? authorization = null, organization = null, project = null;
        var stub = new StubHttpMessageHandler();
        stub.Map(Responses, (request, _) =>
        {
            authorization = request.Headers.Authorization?.ToString();
            organization = request.Headers.TryGetValues(OpenAIApi.OrganizationHeader, out var o) ? o.Single() : null;
            project = request.Headers.TryGetValues(OpenAIApi.ProjectHeader, out var p) ? p.Single() : null;
            return Task.FromResult(StubHttpMessageHandler.Json(HttpStatusCode.OK, Answer(), "text/event-stream"));
        });
        using var client = new OpenAIApiChatClient(Endpoint(), TimeSpan.FromSeconds(10), 0, " org-1 ", "proj-1", new HttpClient(stub));

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "x")]);

        Assert.Equal(("Bearer sk-openai-test", "org-1", "proj-1"), (authorization, organization, project));
        Assert.Equal("Four.", response.Text);
        Assert.Equal(OpenAIApi.BaseUrl, client.Endpoint.BaseUrl);

    }

    [Fact]
    public async Task Stream_TheSummaryTheCallAndTheUsage_BecomeTheUpdatesTheTurnLoopReads()
    {
        var (client, _) = Client(streams: ToolTurn());
        using var _client = client;

        var updates = await client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "go")]).ToListAsync();

        string thinking = string.Concat(updates.SelectMany(u => u.Contents).OfType<TextReasoningContent>().Select(r => r.Text));
        Assert.Equal("**Checking**\n\nNow the clock.", thinking);
        var signed = Assert.Single(updates.SelectMany(u => u.Contents).OfType<TextReasoningContent>(), r => r.ProtectedData is not null);
        Assert.Equal(OpenAIRequest.EncryptedPrefix + "ENC123", signed.ProtectedData);
        var call = Assert.Single(updates.SelectMany(u => u.Contents).OfType<FunctionCallContent>());
        Assert.Equal(("call_1", GetCurrentTimeTool.ToolName), (call.CallId, call.Name));
        Assert.Equal("Asia/Tokyo", ((JsonElement)call.Arguments!["timezone"]!).GetString());
        Assert.Equal(ChatFinishReason.ToolCalls, updates[^1].FinishReason);

        var tokens = TokenUsage.From(Assert.Single(updates.SelectMany(u => u.Contents).OfType<UsageContent>()).Details, TimeSpan.Zero, TimeSpan.Zero);
        Assert.Equal((10_000L, 500L, 8000L, 1000L, 300L), (tokens.Input, tokens.Output, tokens.CacheRead, tokens.CacheWrite, tokens.Reasoning));
        // gpt-5.6-sol: 1,000 uncached at $4, 8,000 read at $0.40, 1,000 written at $5, 500 out at $20 (per million).
        Assert.Equal(0.004m + 0.0032m + 0.005m + 0.01m, tokens.CostUsd);
    }

    [Fact]
    public async Task Stream_TheReasoningGoesBack_InTheNextRequestOfTheTurn()
    {
        var (client, stub) = Client("gpt-5.6-sol", 0, ToolTurn(), Answer("12:00 in Tokyo."));
        using var _client = client;
        var history = new List<ChatMessage> { new(ChatRole.User, "go") };

        var first = await client.GetResponseAsync(history);
        history.AddRange(first.Messages);
        history.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call_1", "12:00")]));
        var second = await client.GetResponseAsync(history);

        Assert.Equal("12:00 in Tokyo.", second.Text);
        using var sent = JsonDocument.Parse(stub.Requests[1].Body!);
        Assert.Equal(["message:user", "reasoning", "function_call", "function_call_output"], Types(sent.RootElement));
        Assert.Contains("\"encrypted_content\":\"ENC123\"", stub.Requests[1].Body);
    }

    [Fact]
    public async Task Stream_TheCap_IsALengthFinish_AFailureThrows_AndACutStreamThrows()
    {
        var (capped, _) = Client(streams: Sse(Created, TextDelta("Once upon"), Completed(type: "response.incomplete", incomplete: "{\"reason\":\"max_output_tokens\"}")));
        using (capped)
        {
            var response = await capped.GetResponseAsync([new ChatMessage(ChatRole.User, "x")]);
            Assert.Equal(ChatFinishReason.Length, response.FinishReason);
            Assert.Equal("Once upon", response.Text);
        }

        var (failed, _) = Client(streams: Sse(Created, "{\"type\":\"response.failed\",\"response\":{\"id\":\"resp_1\",\"error\":{\"code\":\"server_error\",\"message\":\"Something broke.\"}}}"));
        using (failed)
        {
            var ex = await Assert.ThrowsAsync<OpenAIApiException>(() => failed.GetResponseAsync([new ChatMessage(ChatRole.User, "x")]));
            Assert.Equal("server_error: Something broke.", ex.Message);
        }

        var (cut, _) = Client(streams: Sse(Created, TextDelta("Once")));
        using (cut)
        {
            var ex = await Assert.ThrowsAsync<IOException>(() => cut.GetResponseAsync([new ChatMessage(ChatRole.User, "x")]));
            Assert.Equal(OpenAIApiChatClient.StreamCutMessage, ex.Message);
        }
    }

    [Fact]
    public async Task Client_ABadKey_SaysWhereTheKeyIsSet_AndIsNotRetried()
    {
        var stub = new StubHttpMessageHandler().Map(Responses, HttpStatusCode.Unauthorized, "{\"error\":{\"message\":\"Incorrect API key provided: sk-bad.\",\"type\":\"invalid_request_error\",\"code\":\"invalid_api_key\",\"param\":null},\"status\":401}");
        using var client = new OpenAIApiChatClient(Endpoint(), TimeSpan.FromSeconds(10), httpClient: new HttpClient(stub));

        var ex = await Assert.ThrowsAsync<OpenAIApiException>(() => client.GetResponseAsync([new ChatMessage(ChatRole.User, "x")]));

        Assert.Equal((401, "invalid_api_key"), (ex.Status, ex.ErrorType));
        Assert.Equal("HTTP 401: Incorrect API key provided: sk-bad." + OpenAIApiException.KeyHint, ex.Message);
        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task Client_ABusyApi_IsTriedOnceMore_ASpentQuotaIsNot()
    {
        var answers = new Queue<HttpResponseMessage>([StubHttpMessageHandler.Json((HttpStatusCode)429, "{\"error\":{\"code\":\"rate_limit_exceeded\",\"message\":\"Slow down.\"}}"), StubHttpMessageHandler.Json(HttpStatusCode.OK, Answer(), "text/event-stream")]);
        var stub = new StubHttpMessageHandler();
        stub.Map(Responses, (_, _) => Task.FromResult(answers.Dequeue()));
        var time = new ManualTimeProvider();
        using var client = new OpenAIApiChatClient(Endpoint(), TimeSpan.FromSeconds(10), httpClient: new HttpClient(stub), time: time);

        var pending = client.GetResponseAsync([new ChatMessage(ChatRole.User, "x")]);
        while (!pending.IsCompleted)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(10);
        }

        Assert.Equal("Four.", (await pending).Text);
        Assert.Equal(2, stub.Requests.Count);
        Assert.False(OpenAIApiChatClient.IsRetryable(429, "{\"error\":{\"code\":\"insufficient_quota\"}}"));
        Assert.True(OpenAIApiChatClient.IsRetryable(503, ""));
        Assert.False(OpenAIApiChatClient.IsRetryable(400, ""));
    }

    [Fact]
    public async Task Client_RefusedReasoning_IsDroppedOnce_AndTheRequestSentAgain()
    {
        var answers = new Queue<HttpResponseMessage>([StubHttpMessageHandler.Json(HttpStatusCode.BadRequest, "{\"error\":{\"code\":\"invalid_encrypted_content\",\"message\":\"The encrypted content could not be verified.\"}}"), StubHttpMessageHandler.Json(HttpStatusCode.OK, Answer(), "text/event-stream")]);
        var stub = new StubHttpMessageHandler();
        stub.Map(Responses, (_, _) => Task.FromResult(answers.Dequeue()));
        using var client = new OpenAIApiChatClient(Endpoint(), TimeSpan.FromSeconds(10), httpClient: new HttpClient(stub));
        ChatMessage[] history =
        [
            new(ChatRole.User, "go"),
            new(ChatRole.Assistant, [new TextReasoningContent("") { ProtectedData = OpenAIRequest.EncryptedPrefix + "ENC" }, new FunctionCallContent("call_1", "get_current_time")]),
            new(ChatRole.Tool, [new FunctionResultContent("call_1", "12:00")]),
        ];

        await client.GetResponseAsync(history);

        Assert.Equal(2, stub.Requests.Count);
        Assert.Contains("ENC", stub.Requests[0].Body);
        using var resent = JsonDocument.Parse(stub.Requests[1].Body!);
        Assert.Equal(["message:user", "function_call", "function_call_output"], Types(resent.RootElement));
    }

    [Fact]
    public void Usage_AModelTheTableDoesNotName_HasNoCost()
    {
        using var usage = JsonDocument.Parse("{\"input_tokens\":100,\"output_tokens\":10}");
        var details = OpenAIStream.Usage(usage.RootElement, "gpt-unknown");
        Assert.False(details.AdditionalCounts?.ContainsKey(AnthropicStream.CostKey) ?? false);
        Assert.False(details.AdditionalCounts?.ContainsKey(AnthropicStream.CacheWriteKey) ?? false);
        Assert.Equal(110, details.TotalTokenCount);
    }

    /// <summary>A declaration with a name of the caller's choosing, for the name sanitising (no <c>AIFunctionFactory</c>: reflection).</summary>
    private static AIFunctionDeclaration AIFunctionFactoryFree(string name, string description) => new NamedDeclaration(name, description);

    private sealed class NamedDeclaration(string name, string description) : AIFunctionDeclaration
    {
        public override string Name => name;

        public override string Description => description;
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
    [InlineData("gpt-5.3-chat-latest", false)]   // listed but retired: a 404 (the live sweep, 2026-10-03)
    [InlineData("gpt-live-1", false)]            // not a chat model
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
