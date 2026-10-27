using System.ClientModel.Primitives;
using System.Net;
using Microsoft.Extensions.AI;
using NeonSidekick.Llm;
using NeonSidekick.Tests.Fakes;
using OpenAI.Chat;

namespace NeonSidekick.Tests;

/// <summary>
/// The server's usage chunk through the real SDK and adapter: the reasoning count in the two wire
/// shapes in the wild — OpenAI's nested <c>completion_tokens_details.reasoning_tokens</c> (LM Studio,
/// vLLM) and SGLang's top-level <c>reasoning_tokens</c>, which the SDK keeps but names nowhere. The
/// bodies are what vLLM 0.28 and LM Studio streamed on 2026-09-14.
/// </summary>
public class UsageStreamTests
{
    private static LlmEndpoint Endpoint() => new(new Uri("http://127.0.0.1:1234"), "my-model", "k", "test");

    private const string Head = "data: {\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"my-model\",";

    /// <summary>A short answer, then the final chunk with no choices and the given <c>usage</c> object, as every server sends it.</summary>
    private static string Stream(string usage) =>
        Head + "\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"Four.\"},\"logprobs\":null,\"finish_reason\":null}]}\n\n"
        + Head + "\"choices\":[{\"index\":0,\"delta\":{},\"logprobs\":null,\"finish_reason\":\"stop\"}]}\n\n"
        + Head + "\"choices\":[],\"usage\":" + usage + "}\n\n"
        + "data: [DONE]\n\n";

    /// <summary>vLLM 0.28: the nested shape, the thinking inside the completion count.</summary>
    public const string VllmUsage = "{\"prompt_tokens\":22,\"total_tokens\":161,\"completion_tokens\":139,\"completion_tokens_details\":{\"reasoning_tokens\":135}}";

    /// <summary>LM Studio: the nested shape with its draft-token <c>stats</c> sibling.</summary>
    public const string LmStudioUsage = "{\"prompt_tokens\":22,\"completion_tokens\":273,\"total_tokens\":295,\"completion_tokens_details\":{\"reasoning_tokens\":269},\"stats\":{\"total_draft_tokens_count\":297,\"accepted_draft_tokens_count\":173,\"rejected_draft_tokens_count\":124}}";

    /// <summary>SGLang: the count at the top of <c>usage</c>.</summary>
    public const string SglangUsage = "{\"prompt_tokens\":10,\"total_tokens\":60,\"completion_tokens\":50,\"prompt_tokens_details\":null,\"reasoning_tokens\":37}";

    private static async Task<TokenUsage> Usage(string usage)
    {
        var stub = new StubHttpMessageHandler().Map("http://127.0.0.1:1234/v1/chat/completions", HttpStatusCode.OK, Stream(usage), "text/event-stream");
        using var http = new HttpClient(stub);
        using var client = new OpenAICompatibleChatClient(Endpoint(), TimeSpan.FromSeconds(5), http);
        var assistant = new Assistant(client, new ConversationHistory("sys"), new LlmTimeouts(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)));

        TurnEvent.Usage? reported = null;
        await foreach (var evt in assistant.RunTurnAsync("q"))
        {
            Assert.IsNotType<TurnEvent.Notice>(evt);
            if (evt is TurnEvent.Usage u) reported = u;
        }

        return Assert.IsType<TurnEvent.Usage>(reported).Tokens;
    }

    [Fact]
    public async Task TheNestedShape_ReachesTheCount_ThroughTheAdapter()
    {
        var vllm = await Usage(VllmUsage);
        Assert.Equal((22, 139, 161, 1, 135L), (vllm.Input, vllm.Output, vllm.Total, vllm.Requests, vllm.Reasoning));

        var lmStudio = await Usage(LmStudioUsage);
        Assert.Equal((22, 273, 295, 269L), (lmStudio.Input, lmStudio.Output, lmStudio.Total, lmStudio.Reasoning));
    }

    [Fact]
    public async Task TheTopLevelShape_IsReadOffTheRawReport()
    {
        var sglang = await Usage(SglangUsage);
        Assert.Equal((10, 50, 60, 37L), (sglang.Input, sglang.Output, sglang.Total, sglang.Reasoning));
    }

    [Fact]
    public async Task BothShapes_TheNestedOneWins()
    {
        var both = await Usage("{\"prompt_tokens\":10,\"completion_tokens\":50,\"total_tokens\":60,\"completion_tokens_details\":{\"reasoning_tokens\":37},\"reasoning_tokens\":99}");
        Assert.Equal(37, both.Reasoning);
    }

    [Fact]
    public async Task NoCount_StaysNull()
    {
        Assert.Null((await Usage("{\"prompt_tokens\":10,\"completion_tokens\":50,\"total_tokens\":60}")).Reasoning);

        // vLLM's null details objects.
        Assert.Null((await Usage("{\"prompt_tokens\":10,\"completion_tokens\":50,\"total_tokens\":60,\"prompt_tokens_details\":null,\"completion_tokens_details\":null}")).Reasoning);

        // A details object without the field is the SDK's call: its counts are not nullable, so
        // the object alone reads as a report of zero. Every server that sends the object sends the field.
        Assert.Equal(0, (await Usage("{\"prompt_tokens\":10,\"completion_tokens\":50,\"total_tokens\":60,\"completion_tokens_details\":{\"audio_tokens\":0}}")).Reasoning);

        // Thinking off: a count of zero is a report.
        Assert.Equal(0, (await Usage("{\"prompt_tokens\":10,\"completion_tokens\":4,\"total_tokens\":14,\"completion_tokens_details\":{\"reasoning_tokens\":0}}")).Reasoning);
    }

    /// <summary>
    /// 2026-10-26, the speed's clock: a tool call's argument deltas reach the caller as updates of their own (the SDK's chunk with its
    /// <c>ToolCallUpdates</c>, no contents) that <see cref="StreamProgress.Shows"/>, then the one whole call. Pins the adapter's
    /// per-chunk yield: an upgrade that changes it fails here. A server's <c>content: ""</c> role chunk shows nothing.
    /// </summary>
    [Fact]
    public async Task AToolCallsArgumentDeltas_ReachTheCallerAsProgress_BeforeTheOneWholeCall()
    {
        string body =
            Head + "\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"\"},\"logprobs\":null,\"finish_reason\":null}]}\n\n"
            + Head + "\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"type\":\"function\",\"function\":{\"name\":\"echo\",\"arguments\":\"\"}}]},\"logprobs\":null,\"finish_reason\":null}]}\n\n"
            + Head + "\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"{\\\"text\\\":\"}}]},\"logprobs\":null,\"finish_reason\":null}]}\n\n"
            + Head + "\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"\\\"hi\\\"}\"}}]},\"logprobs\":null,\"finish_reason\":null}]}\n\n"
            + Head + "\"choices\":[{\"index\":0,\"delta\":{},\"logprobs\":null,\"finish_reason\":\"tool_calls\"}]}\n\n"
            + Head + "\"choices\":[],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":12,\"total_tokens\":22}}\n\n"
            + "data: [DONE]\n\n";
        var stub = new StubHttpMessageHandler().Map("http://127.0.0.1:1234/v1/chat/completions", HttpStatusCode.OK, body, "text/event-stream");
        using var http = new HttpClient(stub);
        using var client = new OpenAICompatibleChatClient(Endpoint(), TimeSpan.FromSeconds(5), http);

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync([new Microsoft.Extensions.AI.ChatMessage(ChatRole.User, "q")]))
        {
            updates.Add(update);
        }

        Assert.False(StreamProgress.Shows(updates[0]));   // the role chunk, content ""
        int[] deltas = [.. updates.Select((u, i) => (u, i)).Where(p => p.u.RawRepresentation is StreamingChatCompletionUpdate { ToolCallUpdates.Count: > 0 }).Select(p => p.i)];
        int sent = body.Split("\"tool_calls\":[").Length - 1;   // the chunks the server sent with a delta: counted from the wire, not the predicate (R41)
        Assert.Equal(3, sent);
        Assert.Equal(sent, deltas.Length);
        Assert.All(deltas, i => Assert.True(StreamProgress.Shows(updates[i])));
        Assert.Equal(sent + 1, updates.Count(StreamProgress.Shows));   // each delta and the one whole call, nothing else
        int call = Assert.Single(updates.Select((u, i) => (u, i)).Where(p => p.u.Contents.OfType<FunctionCallContent>().Any()).Select(p => p.i));
        Assert.True(call > deltas[^1]);
        Assert.Equal("echo", updates[call].Contents.OfType<FunctionCallContent>().Single().Name);
    }

    private static ChatTokenUsage Parse(string json) =>
        ModelReaderWriter.Read<ChatTokenUsage>(BinaryData.FromString(json), ModelReaderWriterOptions.Json)!;

    [Fact]
    public void TopLevelReasoningTokens_ReadsDepthOneOnly()
    {
        Assert.Equal(37, OpenAICompatibleChatClient.TopLevelReasoningTokens(Parse(SglangUsage)));

        // The nested field is a level down: the adapter's job, never read twice.
        Assert.Null(OpenAICompatibleChatClient.TopLevelReasoningTokens(Parse(VllmUsage)));
        Assert.Null(OpenAICompatibleChatClient.TopLevelReasoningTokens(Parse(LmStudioUsage)));

        // Not a number: no count.
        Assert.Null(OpenAICompatibleChatClient.TopLevelReasoningTokens(Parse("{\"prompt_tokens\":1,\"completion_tokens\":1,\"total_tokens\":2,\"reasoning_tokens\":null}")));
        Assert.Null(OpenAICompatibleChatClient.TopLevelReasoningTokens(Parse("{\"prompt_tokens\":1,\"completion_tokens\":1,\"total_tokens\":2,\"reasoning_tokens\":\"37\"}")));
    }
}
