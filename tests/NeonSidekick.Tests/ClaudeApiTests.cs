using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Files;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Anthropic;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>The Claude API as a server (2026-09-27): the request shaping, the stream, the client, the probe, the settings.</summary>
public class ClaudeApiTests
{
    private static readonly LlmEndpoint Endpoint = new(ClaudeApi.BaseUrl, "claude-sonnet-5", "sk-ant-test", "configured");

    // ── Request shaping ─────────────────────────────────────────────────────

    private static JsonElement Body(IReadOnlyList<ChatMessage> messages, ChatOptions? options = null, string model = "claude-sonnet-5", int maxTokens = 32_000, bool caching = false)
    {
        using var document = JsonDocument.Parse(AnthropicRequest.Write(messages, options, model, maxTokens, caching));
        return document.RootElement.Clone();
    }

    private static string[] Roles(JsonElement body) => body.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("role").GetString()!).ToArray();

    private static string[] BlockTypes(JsonElement message) => message.GetProperty("content").EnumerateArray().Select(b => b.GetProperty("type").GetString()!).ToArray();

    [Fact]
    public void Write_LiftsTheSystemMessages_AndCarriesTheRequiredFields()
    {
        var body = Body([new(ChatRole.System, "You are Neon."), new(ChatRole.User, "hi"), new(ChatRole.System, "Summarise.")], maxTokens: 4096);

        Assert.Equal("claude-sonnet-5", body.GetProperty("model").GetString());
        Assert.Equal(4096, body.GetProperty("max_tokens").GetInt32());
        Assert.True(body.GetProperty("stream").GetBoolean());
        Assert.Equal("You are Neon.\n\nSummarise.", body.GetProperty("system").GetString());
        Assert.Equal(["user"], Roles(body));
        Assert.False(body.TryGetProperty("tools", out _));
        Assert.False(body.TryGetProperty("cache_control", out _));
    }

    [Fact]
    public void Write_PromptCaching_PutsABreakpointOnTheSystemPrompt_AndTheAutomaticOne()
    {
        var body = Body([new(ChatRole.System, "sys"), new(ChatRole.User, "hi")], caching: true);

        var system = Assert.Single(body.GetProperty("system").EnumerateArray());
        Assert.Equal("sys", system.GetProperty("text").GetString());
        Assert.Equal("ephemeral", system.GetProperty("cache_control").GetProperty("type").GetString());
        Assert.Equal("ephemeral", body.GetProperty("cache_control").GetProperty("type").GetString());
    }

    [Fact]
    public void Write_ToolResultsAndTheImageCarrier_AreOneUserMessage_ResultsFirst()
    {
        var history = new ConversationHistory("sys");
        history.AddUser("look at it");
        history.AddMessage(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("toolu_1", "view_image", new Dictionary<string, object?> { ["path"] = "a.png" })]));
        history.AddToolResults([new FunctionResultContent("toolu_1", "attached")]);
        history.AddToolImages([new ImageAttachment("a.png", [1, 2, 3], "image/png", 1, 1)]);

        var body = Body(history.BuildRequest());

        Assert.Equal(["user", "assistant", "user"], Roles(body));
        var last = body.GetProperty("messages")[2];
        Assert.Equal(["tool_result", "text", "image"], BlockTypes(last));
        var result = last.GetProperty("content")[0];
        Assert.Equal("toolu_1", result.GetProperty("tool_use_id").GetString());
        Assert.Equal("attached", result.GetProperty("content").GetString());
        var image = last.GetProperty("content")[2].GetProperty("source");
        Assert.Equal("base64", image.GetProperty("type").GetString());
        Assert.Equal("image/png", image.GetProperty("media_type").GetString());
        Assert.Equal(Convert.ToBase64String([1, 2, 3]), image.GetProperty("data").GetString());

        var call = body.GetProperty("messages")[1].GetProperty("content")[0];
        Assert.Equal("tool_use", call.GetProperty("type").GetString());
        Assert.Equal("a.png", call.GetProperty("input").GetProperty("path").GetString());
    }

    [Fact]
    public void Write_AnUnansweredCall_GetsAnErrorResult_AndAnOrphanResultBecomesText()
    {
        var body = Body(
        [
            new(ChatRole.User, "go"),
            new(ChatRole.Assistant, [new FunctionCallContent("toolu_a", "clock"), new FunctionCallContent("toolu_b", "clock")]),
            new(ChatRole.Tool, [new FunctionResultContent("toolu_a", "12:00"), new FunctionResultContent("toolu_zz", "stray")]),
        ]);

        var results = body.GetProperty("messages")[2];
        Assert.Equal(["tool_result", "tool_result", "text"], BlockTypes(results));
        var missing = results.GetProperty("content")[1];
        Assert.Equal("toolu_b", missing.GetProperty("tool_use_id").GetString());
        Assert.Equal(AnthropicRequest.MissingResult, missing.GetProperty("content").GetString());
        Assert.True(missing.GetProperty("is_error").GetBoolean());
        Assert.Equal("Tool result: stray", results.GetProperty("content")[2].GetProperty("text").GetString());
    }

    [Fact]
    public void Write_AHistoryThatStartsWithTheModel_GetsAUserMessageFirst()
    {
        var body = Body([new(ChatRole.Assistant, "earlier reply  "), new(ChatRole.User, "and now?")]);

        Assert.Equal(["user", "assistant", "user"], Roles(body));
        Assert.Equal(AnthropicRequest.ContinuedPlaceholder, body.GetProperty("messages")[0].GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal("earlier reply", body.GetProperty("messages")[1].GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public void Write_ThinkingGoesBack_OnlyInTheTurnInFlight()
    {
        static ChatMessage Thought(string text, string signature, string callId) =>
            new(ChatRole.Assistant, [new TextReasoningContent(text[..3]), new TextReasoningContent(text[3..]), new TextReasoningContent("") { ProtectedData = signature }, new FunctionCallContent(callId, "clock")]);

        var body = Body(
        [
            new(ChatRole.User, "first"),
            Thought("old thought", "sig-old", "toolu_1"),
            new(ChatRole.Tool, [new FunctionResultContent("toolu_1", "12:00")]),
            new(ChatRole.Assistant, "It is noon."),
            new(ChatRole.User, "second"),
            Thought("new thought", "sig-new", "toolu_2"),
            new(ChatRole.Tool, [new FunctionResultContent("toolu_2", "12:01")]),
        ]);

        var messages = body.GetProperty("messages");
        Assert.Equal(["tool_use"], BlockTypes(messages[1]));
        Assert.Equal(["thinking", "tool_use"], BlockTypes(messages[5]));
        var thinking = messages[5].GetProperty("content")[0];
        Assert.Equal("new thought", thinking.GetProperty("thinking").GetString());
        Assert.Equal("sig-new", thinking.GetProperty("signature").GetString());
    }

    [Fact]
    public void Write_ARedactedBlock_GoesBackAsRedactedThinking()
    {
        var body = Body(
        [
            new(ChatRole.User, "go"),
            new(ChatRole.Assistant, [new TextReasoningContent("") { ProtectedData = AnthropicRequest.RedactedPrefix + "opaque" }, new FunctionCallContent("toolu_1", "clock")]),
            new(ChatRole.Tool, [new FunctionResultContent("toolu_1", "12:00")]),
        ]);

        var block = body.GetProperty("messages")[1].GetProperty("content")[0];
        Assert.Equal("redacted_thinking", block.GetProperty("type").GetString());
        Assert.Equal("opaque", block.GetProperty("data").GetString());
    }

    [Fact]
    public void Write_Tools_CarryTheirSchema_AndANameTheApiRefusesIsSanitised()
    {
        var clock = new GetCurrentTimeTool(TimeProvider.System);
        var body = Body([new(ChatRole.User, "hi")], new ChatOptions { Tools = [clock] });

        var tool = Assert.Single(body.GetProperty("tools").EnumerateArray());
        Assert.Equal(clock.Name, tool.GetProperty("name").GetString());
        Assert.Equal("object", tool.GetProperty("input_schema").GetProperty("type").GetString());

        string odd = "github.server__" + new string('x', 80);
        string wire = AnthropicRequest.ToolName(odd);
        Assert.Matches("^[a-zA-Z0-9_-]{1,64}$", wire);
        Assert.Equal(wire, AnthropicRequest.ToolName(odd));
        Assert.Equal("already_fine-1", AnthropicRequest.ToolName("already_fine-1"));
        Assert.NotEqual(AnthropicRequest.ToolName("a.b"), AnthropicRequest.ToolName("a_b"));
    }

    // ── Thinking per model ──────────────────────────────────────────────────

    private static string Thinking(string model, ReasoningEffort? effort, int maxTokens = 32_000)
    {
        var body = Body([new(ChatRole.User, "hi")], effort is { } e ? new ChatOptions { Reasoning = new ReasoningOptions { Effort = e } } : null, model, maxTokens);
        string thinking = body.TryGetProperty("thinking", out var t) ? t.GetRawText() : "-";
        string config = body.TryGetProperty("output_config", out var c) ? c.GetRawText() : "-";
        return thinking + " " + config;
    }

    [Theory]
    [InlineData("claude-sonnet-5", ReasoningEffort.None, "{\"type\":\"disabled\"} -")]
    [InlineData("claude-opus-5", ReasoningEffort.None, "{\"type\":\"disabled\"} -")]
    [InlineData("claude-opus-5-5", ReasoningEffort.None, "- {\"effort\":\"low\"}")]
    [InlineData("claude-fable-5-1", ReasoningEffort.None, "- {\"effort\":\"low\"}")]
    [InlineData("claude-sonnet-5", ReasoningEffort.Medium, "{\"type\":\"adaptive\",\"display\":\"summarized\"} {\"effort\":\"medium\"}")]
    [InlineData("claude-opus-5-5", ReasoningEffort.ExtraHigh, "{\"type\":\"adaptive\",\"display\":\"summarized\"} {\"effort\":\"xhigh\"}")]
    [InlineData("claude-opus-4-6", ReasoningEffort.ExtraHigh, "{\"type\":\"adaptive\"} {\"effort\":\"high\"}")]
    [InlineData("claude-haiku-4-5", ReasoningEffort.None, "- -")]
    [InlineData("claude-haiku-4-5", ReasoningEffort.High, "{\"type\":\"enabled\",\"budget_tokens\":12288} -")]
    [InlineData("claude-some-future-model", ReasoningEffort.None, "- {\"effort\":\"low\"}")]
    public void Write_ShapesTheReasoningLevelForTheModel(string model, ReasoningEffort effort, string expected) =>
        Assert.Equal(expected, Thinking(model, effort));

    [Fact]
    public void Write_NoLevel_SendsNeither_AndABudgetStaysUnderTheCap()
    {
        Assert.Equal("- -", Thinking("claude-opus-5", null));
        Assert.Equal("{\"type\":\"enabled\",\"budget_tokens\":3072} -", Thinking("claude-haiku-4-5", ReasoningEffort.ExtraHigh, maxTokens: 4096));
        Assert.Equal("- -", Thinking("claude-haiku-4-5", ReasoningEffort.Low, maxTokens: 1500));
    }

    // ── The stream ──────────────────────────────────────────────────────────

    private static string Sse(params (string Event, string Data)[] events)
    {
        var text = new StringBuilder();
        foreach (var (name, data) in events)
        {
            text.Append("event: ").Append(name).Append('\n').Append("data: ").Append(data).Append("\n\n");
        }

        return text.ToString();
    }

    private static readonly (string, string) MessageStart = ("message_start", "{\"type\":\"message_start\",\"message\":{\"id\":\"msg_1\",\"model\":\"claude-sonnet-5\",\"usage\":{\"input_tokens\":100,\"cache_creation_input_tokens\":2000,\"cache_read_input_tokens\":10000,\"output_tokens\":1}}}");

    private static (string, string) Stop(string reason, int output = 50) =>
        ("message_delta", "{\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"" + reason + "\"},\"usage\":{\"output_tokens\":" + output + "}}");

    private static readonly (string, string) MessageStop = ("message_stop", "{\"type\":\"message_stop\"}");

    /// <summary>A thinking block, a text block and a tool call, the way the API streams them.</summary>
    private static string ToolTurn() => Sse(
        MessageStart,
        ("content_block_start", "{\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"thinking\",\"thinking\":\"\"}}"),
        ("content_block_delta", "{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"thinking_delta\",\"thinking\":\"Let me \"}}"),
        ("content_block_delta", "{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"thinking_delta\",\"thinking\":\"check.\"}}"),
        ("content_block_delta", "{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"signature_delta\",\"signature\":\"SIG\"}}"),
        ("content_block_stop", "{\"type\":\"content_block_stop\",\"index\":0}"),
        ("content_block_start", "{\"type\":\"content_block_start\",\"index\":1,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}"),
        ("content_block_delta", "{\"type\":\"content_block_delta\",\"index\":1,\"delta\":{\"type\":\"text_delta\",\"text\":\"One moment.\"}}"),
        ("content_block_stop", "{\"type\":\"content_block_stop\",\"index\":1}"),
        ("ping", "{\"type\":\"ping\"}"),
        ("content_block_start", "{\"type\":\"content_block_start\",\"index\":2,\"content_block\":{\"type\":\"tool_use\",\"id\":\"toolu_9\",\"name\":\"get_current_time\",\"input\":{}}}"),
        ("content_block_delta", "{\"type\":\"content_block_delta\",\"index\":2,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"{\\\"timezone\\\": \\\"Eur\"}}"),
        ("content_block_delta", "{\"type\":\"content_block_delta\",\"index\":2,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"ope/Paris\\\"}\"}}"),
        ("content_block_stop", "{\"type\":\"content_block_stop\",\"index\":2}"),
        Stop("tool_use"),
        MessageStop);

    private static (AnthropicChatClient Client, StubHttpMessageHandler Stub) Client(params Func<HttpResponseMessage>[] responses)
    {
        var stub = new StubHttpMessageHandler();
        int next = 0;
        stub.Map("https://api.anthropic.com/v1/messages", (_, _) => Task.FromResult(responses[Math.Min(next++, responses.Length - 1)]()));
        return (new AnthropicChatClient(Endpoint, TimeSpan.FromSeconds(30), 32_000, promptCaching: true, new HttpClient(stub)), stub);
    }

    private static HttpResponseMessage Stream(string sse) => StubHttpMessageHandler.Json(HttpStatusCode.OK, sse, "text/event-stream");

    [Fact]
    public async Task Stream_TextThinkingAndACall_BecomeTheUpdatesTheTurnLoopReads()
    {
        var (client, _) = Client(() => Stream(ToolTurn()));
        using var _client = client;

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "time?")]))
        {
            updates.Add(update);
        }

        Assert.Equal("Let me check.", string.Concat(updates.SelectMany(u => u.Contents).OfType<TextReasoningContent>().Select(r => r.Text)));
        Assert.Equal("SIG", updates.SelectMany(u => u.Contents).OfType<TextReasoningContent>().Single(r => r.ProtectedData is not null).ProtectedData);
        Assert.Equal("One moment.", string.Concat(updates.Select(u => u.Text)));
        var call = Assert.Single(updates.SelectMany(u => u.Contents).OfType<FunctionCallContent>());
        Assert.Equal(("toolu_9", "get_current_time"), (call.CallId, call.Name));
        Assert.Equal("Europe/Paris", ((JsonElement)call.Arguments!["timezone"]!).GetString());

        var last = updates[^1];
        Assert.Equal(ChatFinishReason.ToolCalls, last.FinishReason);
        var usage = Assert.IsType<UsageContent>(Assert.Single(last.Contents)).Details;
        Assert.Equal(12_100, usage.InputTokenCount);   // the uncached, the written and the read, together: the context sent
        Assert.Equal(50, usage.OutputTokenCount);
        Assert.Equal(10_000, usage.CachedInputTokenCount);
        Assert.Equal(2000, usage.AdditionalCounts![AnthropicStream.CacheWriteKey]);

        // Sonnet 5 at $2 / $10: 100 × 2 + 2000 × 2.5 + 10,000 × 0.2 + 50 × 10, per million.
        var tally = TokenUsage.From(usage, TimeSpan.Zero, TimeSpan.Zero);
        Assert.Equal(0.0077m, tally.CostUsd);
        Assert.Equal((10_000L, 2000L), (tally.CacheRead!.Value, tally.CacheWrite!.Value));
    }

    [Fact]
    public async Task Stream_TheThinkingSurvivesToChatResponse_AndGoesBackSigned()
    {
        var (client, stub) = Client(() => Stream(ToolTurn()), () => Stream(Sse(MessageStart, Stop("end_turn"), MessageStop)));
        using var _client = client;
        var history = new List<ChatMessage> { new(ChatRole.User, "time?") };

        var response = await client.GetStreamingResponseAsync(history).ToChatResponseAsync();
        history.AddRange(response.Messages);
        history.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent("toolu_9", "09:00")]));
        await client.GetResponseAsync(history);

        using var sent = JsonDocument.Parse(stub.Requests[1].Body!);
        var assistant = sent.RootElement.GetProperty("messages")[1];
        Assert.Equal(["thinking", "text", "tool_use"], BlockTypes(assistant));
        Assert.Equal("Let me check.", assistant.GetProperty("content")[0].GetProperty("thinking").GetString());
        Assert.Equal("SIG", assistant.GetProperty("content")[0].GetProperty("signature").GetString());
    }

    [Fact]
    public async Task Stream_ARefusal_EndsTheReplyWithANote()
    {
        var (client, _) = Client(() => Stream(Sse(MessageStart,
            ("message_delta", "{\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"refusal\",\"stop_details\":{\"type\":\"refusal\",\"category\":\"cyber\"}},\"usage\":{\"output_tokens\":3}}"),
            MessageStop)));
        using var _client = client;

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "x")]);

        Assert.Equal(AnthropicStream.RefusalNote("cyber"), response.Text);
        Assert.Equal(ChatFinishReason.ContentFilter, response.FinishReason);
    }

    [Fact]
    public async Task Stream_AnErrorEvent_OrACutStream_Throws()
    {
        var (overloaded, _) = Client(() => Stream(Sse(MessageStart, ("error", "{\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"message\":\"Overloaded\"}}"))));
        using (overloaded)
        {
            var ex = await Assert.ThrowsAsync<AnthropicApiException>(() => overloaded.GetResponseAsync([new ChatMessage(ChatRole.User, "x")]));
            Assert.Equal("overloaded_error: Overloaded", ex.Message);
        }

        var (cut, _) = Client(() => Stream(Sse(MessageStart)));
        using (cut)
        {
            var ex = await Assert.ThrowsAsync<IOException>(() => cut.GetResponseAsync([new ChatMessage(ChatRole.User, "x")]));
            Assert.Equal(AnthropicChatClient.StreamCutMessage, ex.Message);
        }
    }

    // ── The client ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Client_SendsTheKeyHeaders_NotABearer()
    {
        HttpRequestMessage? seen = null;
        string? key = null, version = null;
        var stub = new StubHttpMessageHandler();
        stub.Map("https://api.anthropic.com/v1/messages", (request, _) =>
        {
            seen = request;
            key = request.Headers.GetValues("x-api-key").Single();
            version = request.Headers.GetValues("anthropic-version").Single();
            return Task.FromResult(Stream(Sse(MessageStart, Stop("end_turn"), MessageStop)));
        });
        using var client = new AnthropicChatClient(Endpoint, TimeSpan.FromSeconds(30), 32_000, false, new HttpClient(stub));

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "x")]);

        Assert.Equal(("sk-ant-test", ClaudeApi.Version), (key, version));
        Assert.Null(seen!.Headers.Authorization);
    }

    [Fact]
    public async Task Client_ABadKey_SaysWhereTheKeyIsSet()
    {
        var (client, stub) = Client(() => StubHttpMessageHandler.Json(HttpStatusCode.Unauthorized, "{\"type\":\"error\",\"error\":{\"type\":\"authentication_error\",\"message\":\"invalid x-api-key\"}}"));
        using var _client = client;

        var ex = await Assert.ThrowsAsync<AnthropicApiException>(() => client.GetResponseAsync([new ChatMessage(ChatRole.User, "x")]));

        Assert.Equal(401, ex.Status);
        Assert.Equal("authentication_error", ex.ErrorType);
        Assert.Equal("HTTP 401: invalid x-api-key" + AnthropicApiException.KeyHint, ex.Message);
        Assert.Single(stub.Requests);   // a 401 is not retried
    }

    [Fact]
    public async Task Client_AnOverloadedApi_IsTriedOnceMore()
    {
        var busy = () =>
        {
            var response = StubHttpMessageHandler.Json((HttpStatusCode)529, "{\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"message\":\"Overloaded\"}}");
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
            return response;
        };
        var (client, stub) = Client(busy, () => Stream(Sse(MessageStart, Stop("end_turn"), MessageStop)));
        using var _client = client;

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "x")]);
        Assert.Equal(2, stub.Requests.Count);

        var (twice, twiceStub) = Client(busy);
        using (twice)
        {
            var ex = await Assert.ThrowsAsync<AnthropicApiException>(() => twice.GetResponseAsync([new ChatMessage(ChatRole.User, "x")]));
            Assert.Equal(529, ex.Status);
            Assert.Equal(2, twiceStub.Requests.Count);
        }
    }

    [Fact]
    public async Task Client_ThinkingTheApiRefuses_IsDroppedOnce_AndTheRequestSentAgain()
    {
        var refused = () => StubHttpMessageHandler.Json(HttpStatusCode.BadRequest, "{\"type\":\"error\",\"error\":{\"type\":\"invalid_request_error\",\"message\":\"thinking block signature does not match the conversation prefix\"}}");
        var (client, stub) = Client(refused, () => Stream(Sse(MessageStart, Stop("end_turn"), MessageStop)));
        using var _client = client;
        ChatMessage[] history =
        [
            new(ChatRole.User, "go"),
            new(ChatRole.Assistant, [new TextReasoningContent("hm"), new TextReasoningContent("") { ProtectedData = "SIG" }, new FunctionCallContent("toolu_1", "clock")]),
            new(ChatRole.Tool, [new FunctionResultContent("toolu_1", "(pruned)")]),
        ];

        await client.GetResponseAsync(history);

        Assert.Equal(2, stub.Requests.Count);
        Assert.Contains("\"thinking\"", stub.Requests[0].Body);
        Assert.DoesNotContain("\"signature\"", stub.Requests[1].Body);
        Assert.Contains("\"tool_use\"", stub.Requests[1].Body);

        // A 400 about something else, or one with no thinking to drop, is not retried.
        var (other, otherStub) = Client(refused);
        using (other)
        {
            await Assert.ThrowsAsync<AnthropicApiException>(() => other.GetResponseAsync([new ChatMessage(ChatRole.User, "x")]));
            Assert.Single(otherStub.Requests);
        }
    }

    /// <summary>2026-09-28, code review: the busy retry was the first attempt's alone, so a 529 after the stripped resend failed.</summary>
    [Fact]
    public async Task Client_ABusyApi_AfterTheThinkingWasDropped_IsStillTriedOnceMore()
    {
        var refused = () => StubHttpMessageHandler.Json(HttpStatusCode.BadRequest, "{\"type\":\"error\",\"error\":{\"type\":\"invalid_request_error\",\"message\":\"thinking block signature does not match the conversation prefix\"}}");
        var busy = () =>
        {
            var response = StubHttpMessageHandler.Json((HttpStatusCode)529, "{\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"message\":\"Overloaded\"}}");
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
            return response;
        };
        var (client, stub) = Client(refused, busy, () => Stream(Sse(MessageStart, Stop("end_turn"), MessageStop)));
        using var _client = client;
        ChatMessage[] history =
        [
            new(ChatRole.User, "go"),
            new(ChatRole.Assistant, [new TextReasoningContent("hm"), new TextReasoningContent("") { ProtectedData = "SIG" }, new FunctionCallContent("toolu_1", "clock")]),
            new(ChatRole.Tool, [new FunctionResultContent("toolu_1", "(pruned)")]),
        ];

        await client.GetResponseAsync(history);

        Assert.Equal(3, stub.Requests.Count);
        Assert.DoesNotContain("\"signature\"", stub.Requests[2].Body);
    }

    // ── The probe and the session ───────────────────────────────────────────

    private const string ModelsList = "{\"data\":[{\"type\":\"model\",\"id\":\"claude-opus-5-5\",\"display_name\":\"Claude Opus 5.5\",\"max_input_tokens\":1000000,\"max_tokens\":128000},{\"type\":\"model\",\"id\":\"claude-haiku-4-5\",\"display_name\":\"Claude Haiku 4.5\",\"max_input_tokens\":200000}],\"has_more\":false}";

    [Fact]
    public async Task Probe_AsksTheModelsWithTheKeyHeaders_AndNamesTheServer()
    {
        string? key = null, url = null;
        bool bearer = true;
        var stub = new StubHttpMessageHandler();
        stub.Map("https://api.anthropic.com/v1/models", (request, _) =>
        {
            url = request.RequestUri!.AbsoluteUri;
            key = request.Headers.GetValues("x-api-key").Single();
            bearer = request.Headers.Authorization is not null;
            return Task.FromResult(StubHttpMessageHandler.Json(HttpStatusCode.OK, ModelsList));
        });
        var probe = new LlmEndpointProbe(new HttpClient(stub));

        var result = await probe.ProbeAsync(ClaudeApi.BaseUrl, "sk-ant-test", CancellationToken.None);

        Assert.Equal("https://api.anthropic.com/v1/models?limit=1000", url);
        Assert.Equal("sk-ant-test", key);
        Assert.False(bearer);
        Assert.Equal(["claude-opus-5-5", "claude-haiku-4-5"], result.ModelIds);
        var server = LlmServer.From(ClaudeApi.BaseUrl, result);
        Assert.Equal(ClaudeApi.ServerName, server.Name);
        var endpoint = LlmEndpointProbe.Endpoint(server, "sk-ant-test", "claude-haiku-4-5", configured: true);
        Assert.Equal(new ContextLength(200_000, ContextLengthProbe.MaxInputTokensSource), endpoint.PublishedContextLength);
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
    public async Task Session_ListsTheClaudeApiLast_OnlyWhileItIsOffered()
    {
        var stub = new StubHttpMessageHandler()
            .Map("http://127.0.0.1:1234/v1/models", HttpStatusCode.OK, StubHttpMessageHandler.ModelsJson("llama"))
            .Map("https://api.anthropic.com/v1/models", HttpStatusCode.OK, ModelsList);
        using var session = Session(stub, []);

        var off = await session.ProbeServersAsync(new AppSettingsData { ClaudeApiKey = "sk-ant-test" }, null, CancellationToken.None);
        Assert.Equal(["LM Studio"], off.Select(s => s.Name));

        var keyless = await session.ProbeServersAsync(new AppSettingsData { ClaudeApi = true }, null, CancellationToken.None);
        Assert.Equal(["LM Studio"], keyless.Select(s => s.Name));

        var on = await session.ProbeServersAsync(new AppSettingsData { ClaudeApi = true, ClaudeApiKey = "sk-ant-test" }, null, CancellationToken.None);
        Assert.Equal(["LM Studio", ClaudeApi.ServerName], on.Select(s => s.Name));

        // The scan disabled still lists it.
        var alone = await session.ProbeServersAsync(new AppSettingsData { ClaudeApi = true, ClaudeApiKey = "sk-ant-test", LlmScanMode = "disabled" }, null, CancellationToken.None);
        Assert.Equal([ClaudeApi.ServerName], alone.Select(s => s.Name));
    }

    [Fact]
    public async Task Session_AConfiguredClaudeUrl_ConnectsWithTheClaudeKey_NotTheLlmKey()
    {
        var stub = new StubHttpMessageHandler().Map("https://api.anthropic.com/v1/models", HttpStatusCode.OK, ModelsList);
        var endpoints = new List<LlmEndpoint>();
        using var session = Session(stub, endpoints);

        Assert.True(await session.ConnectAsync(new AppSettingsData { ClaudeApi = true, ClaudeApiKey = "sk-ant-test", LlmApiKey = "local-key", LlmUrl = "https://api.anthropic.com" }, CancellationToken.None));

        var endpoint = Assert.Single(endpoints);
        Assert.Equal(("sk-ant-test", "claude-opus-5-5"), (endpoint.ApiKey, endpoint.ModelId));
        Assert.Equal(1_000_000, session.ContextLength!.Value.Tokens);
        Assert.Single(stub.Requests);   // no native context tiers asked of the API's host
    }

    [Fact]
    public async Task Session_AClaudeUrlWhileItIsOff_LooksForAServerInstead()
    {
        var stub = new StubHttpMessageHandler().Map("http://127.0.0.1:1234/v1/models", HttpStatusCode.OK, StubHttpMessageHandler.ModelsJson("llama"));
        var endpoints = new List<LlmEndpoint>();
        using var session = Session(stub, endpoints);

        Assert.True(await session.ConnectAsync(new AppSettingsData { ClaudeApiKey = "sk-ant-test", LlmUrl = "https://api.anthropic.com/v1" }, CancellationToken.None));

        Assert.Equal("http://127.0.0.1:1234/v1", Assert.Single(endpoints).BaseUrl.AbsoluteUri);
        Assert.DoesNotContain(stub.Requests, r => r.Uri.Host == ClaudeApi.Host);
    }

    // ── Keys and settings ───────────────────────────────────────────────────

    [Fact]
    public void Key_IsTheClaudeApisOwn_AndDecryptsWhatTheMenuSaved()
    {
        Assert.False(ClaudeApi.Offered(new AppSettingsData { ClaudeApi = true }));
        Assert.False(ClaudeApi.Offered(new AppSettingsData { ClaudeApiKey = "k" }));
        Assert.True(ClaudeApi.Offered(new AppSettingsData { ClaudeApi = true, ClaudeApiKey = "k" }));

        var data = new AppSettingsData { ClaudeApiKey = "claude-key", LlmApiKey = "local-key" };
        Assert.Equal("claude-key", ClaudeApi.KeyFor(data, ClaudeApi.BaseUrl));
        Assert.Equal("local-key", ClaudeApi.KeyFor(data, new Uri("http://127.0.0.1:1234/v1")));
        Assert.Equal("", ClaudeApi.KeyFor(new AppSettingsData(), ClaudeApi.BaseUrl));

        string stored = ClaudeApi.Protect("  sk-ant-secret ", out string? error);
        if (OperatingSystem.IsWindows())
        {
            Assert.Null(error);
            Assert.StartsWith(Sql.WindowsCredentials.ProtectedPrefix, stored);
            Assert.DoesNotContain("sk-ant-secret", stored);
            Assert.Equal("sk-ant-secret", ClaudeApi.Key(new AppSettingsData { ClaudeApiKey = stored }));
            Assert.Equal(SettingsMenu.ClaudeApiKeyEncryptedLabel, SettingsMenu.ClaudeApiKeyLabel(stored));
            Assert.Equal(stored, ClaudeApi.Protect(stored, out _));   // already encrypted: kept
        }

        Assert.Equal("", ClaudeApi.Protect("  ", out _));
        Assert.Null(ClaudeApi.Key(new AppSettingsData { ClaudeApiKey = Sql.WindowsCredentials.ProtectedPrefix + "bm90IGEgYmxvYg==" }));
    }

    [Fact]
    public void Settings_TheClaudeApiTab_SitsAfterStt_WithItsFourRows()
    {
        int tab = SettingsMenu.TabTitles.ToList().IndexOf(SettingsMenu.ClaudeApiTabTitle);
        Assert.Equal("STT", SettingsMenu.TabTitles[tab - 1]);
        Assert.Equal(SettingsMenu.LocalModelTabTitle, SettingsMenu.TabTitles[tab + 1]);   // the other server of the app's own (2026-09-29); Botchat until then
        Assert.Equal((int)SettingsTab.ClaudeApi, tab);
        Assert.Equal([SettingsField.ClaudeApi, SettingsField.ClaudeApiKey, SettingsField.ClaudeApiMaxTokens, SettingsField.ClaudeApiPromptCaching], SettingsMenu.TabFields[tab]);

        var data = new AppSettingsData();
        Assert.Equal("off", SettingsMenu.FieldValue(SettingsField.ClaudeApi, data, "C:\\p"));
        Assert.Equal("(none)", SettingsMenu.FieldValue(SettingsField.ClaudeApiKey, data, "C:\\p"));
        Assert.Equal("32,000 tokens", SettingsMenu.FieldValue(SettingsField.ClaudeApiMaxTokens, data, "C:\\p"));
        Assert.Equal("on", SettingsMenu.FieldValue(SettingsField.ClaudeApiPromptCaching, data, "C:\\p"));
        Assert.Equal("sk••••", SettingsMenu.ClaudeApiKeyLabel("sk-ant"));
        Assert.Equal("", SettingsMenu.EditableValue(SettingsField.ClaudeApiKey, new AppSettingsData { ClaudeApiKey = "sk-ant" }));
        Assert.All(SettingsMenu.TabFields[tab], f => Assert.True(SettingsMenu.IsLlmField(f)));
        Assert.True(SettingsMenu.IsToggle(SettingsField.ClaudeApi) && SettingsMenu.IsToggle(SettingsField.ClaudeApiPromptCaching));

        var copy = AppSettings.Copy(new AppSettingsData { ClaudeApi = true, ClaudeApiKey = "k", ClaudeApiMaxTokens = 4096, ClaudeApiPromptCaching = false });
        Assert.Equal((true, "k", 4096, false), (copy.ClaudeApi, copy.ClaudeApiKey, copy.ClaudeApiMaxTokens, copy.ClaudeApiPromptCaching));
        Assert.Equal(["ClaudeApiKey: " + SettingsDiff.Redacted], SettingsDiff.Changes(new AppSettingsData(), new AppSettingsData { ClaudeApiKey = "secret" }));
    }

    [Fact]
    public void Environment_OffersTheClaudeApi_AndNeverLogsItsKey()
    {
        var variables = new Dictionary<string, string>
        {
            [EnvironmentOverrides.ClaudeApiVariable] = "on",
            [EnvironmentOverrides.ClaudeApiKeyVariable] = "sk-ant-env",
        };
        var environment = new EnvironmentOverrides(name => variables.GetValueOrDefault(name));

        var effective = environment.ApplyTo(new AppSettingsData());

        Assert.True(ClaudeApi.Offered(effective));
        Assert.Equal("sk-ant-env", ClaudeApi.Key(effective));
        Assert.Equal($"{EnvironmentOverrides.ClaudeApiVariable}=on, {EnvironmentOverrides.ClaudeApiKeyVariable}={EnvironmentOverrides.SecretSet}", environment.Describe());
    }

    // ── /usage ──────────────────────────────────────────────────────────────

    [Fact]
    public void Usage_ShowsTheCacheAndTheCost_OnlyWhenReported()
    {
        var local = new TokenUsage(100, 10, 110, 1, TimeSpan.Zero, TimeSpan.Zero);
        Assert.DoesNotContain(UsageText.Rows(local, averaged: false), r => r.Label is UsageText.CacheLabel or UsageText.CostLabel);

        var claude = new TokenUsage(12_100, 50, 12_150, 1, TimeSpan.Zero, TimeSpan.Zero, CacheRead: 10_000, CacheWrite: 2000, CostUsd: 0.0077m);
        var rows = UsageText.Rows(claude + claude, averaged: true);
        Assert.Contains((UsageText.CacheLabel, "20,000 read · 4,000 written"), rows);
        Assert.Contains((UsageText.CostLabel, "$0.0154"), rows);
        Assert.Equal(0.0077m, (claude + local).CostUsd);
    }

    [Theory]
    [InlineData("claude-opus-5-5", 4, 20, 0.20)]
    [InlineData("claude-opus-5", 5, 25, 0.50)]
    [InlineData("claude-fable-5-1", 10, 50, 0.25)]
    [InlineData("claude-sonnet-4-6", 3, 15, 0.30)]
    [InlineData("claude-haiku-4-5-20251001", 1, 5, 0.10)]
    public void Price_IsTheModelsList(string model, double input, double output, double cacheRead)
    {
        var price = ClaudePrice.For(model)!;
        Assert.Equal(((decimal)input, (decimal)output, (decimal)cacheRead), (price.Input, price.Output, price.CacheRead));
        Assert.Null(ClaudePrice.For("claude-unknown"));
    }
}
