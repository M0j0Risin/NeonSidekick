using System.Net;
using Microsoft.Extensions.AI;
using NeonSidekick.Llm;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// A thinking model behind SGLang / vLLM streams <c>reasoning_content</c> deltas first and the
/// answer as <c>content</c> deltas after. The answer must reach the transcript; the reasoning must not.
/// </summary>
public class ReasoningStreamTests
{
    private static LlmEndpoint Endpoint() => new(new Uri("http://127.0.0.1:1234"), "my-model", "k", "test");

    private static string Chunk(string delta) =>
        "data: {\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"my-model\",\"choices\":[{\"index\":0,\"delta\":{" + delta + "},\"logprobs\":null,\"finish_reason\":null}]}\n\n";

    /// <summary>The shape SGLang 0.5 streamed for Qwen3.8 with a reasoning level (captured 2026-09-11).</summary>
    public const string Sse =
        "data: {\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"my-model\",\"choices\":[{\"index\":0,\"delta\":{\"reasoning_content\":null,\"role\":\"assistant\",\"content\":\"\"},\"logprobs\":null,\"finish_reason\":null}]}\n\n";

    private static string Stream() =>
        Sse
        + Chunk("\"reasoning_content\":\"The\"")
        + Chunk("\"reasoning_content\":\" user asks.\"")
        + Chunk("\"reasoning_content\":null,\"content\":\"\\n\\nThe capital\"")
        + Chunk("\"reasoning_content\":null,\"content\":\" of France is Paris.\"")
        + "data: {\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"my-model\",\"choices\":[{\"index\":0,\"delta\":{\"reasoning_content\":null},\"logprobs\":null,\"finish_reason\":\"stop\"}]}\n\n"
        + "data: [DONE]\n\n";

    [Fact]
    public async Task AnswerAfterReasoningDeltas_ReachesTheText()
    {
        var stub = new StubHttpMessageHandler().Map("http://127.0.0.1:1234/v1/chat/completions", HttpStatusCode.OK, Stream(), "text/event-stream");
        using var http = new HttpClient(stub);
        using var client = new OpenAICompatibleChatClient(Endpoint(), TimeSpan.FromSeconds(5), http);

        var texts = new List<string>();
        var kinds = new List<string>();
        await foreach (var update in client.GetStreamingResponseAsync(new[] { new ChatMessage(ChatRole.User, "q") }, new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.Medium } }))
        {
            texts.Add(update.Text);
            kinds.Add(string.Join("+", update.Contents.Select(c => c.GetType().Name + "(" + (c as TextContent)?.Text + (c as TextReasoningContent)?.Text + ")")));
        }

        string joined = string.Concat(texts);
        Assert.True(joined.Contains("The capital of France is Paris."), "updates: " + string.Join(" | ", kinds));
        Assert.DoesNotContain("user asks", joined);
    }

    [Fact]
    public async Task Assistant_RendersTheAnswer_NotTheReasoning()
    {
        var stub = new StubHttpMessageHandler().Map("http://127.0.0.1:1234/v1/chat/completions", HttpStatusCode.OK, Stream(), "text/event-stream");
        using var http = new HttpClient(stub);
        using var client = new OpenAICompatibleChatClient(Endpoint(), TimeSpan.FromSeconds(5), http);
        var history = new ConversationHistory("sys");
        var assistant = new Assistant(client, history, new LlmTimeouts(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)), reasoning: ReasoningEffort.Medium);

        var deltas = new List<string>();
        await foreach (var evt in assistant.RunTurnAsync("q"))
        {
            if (evt is TurnEvent.TextDelta d) deltas.Add(d.Text);
            Assert.IsNotType<TurnEvent.Notice>(evt);
        }

        Assert.Equal("\n\nThe capital of France is Paris.", string.Concat(deltas));
        Assert.Equal(2, history.Messages.Count);
        Assert.DoesNotContain("user asks", history.Messages[1].Text);
    }

    /// <summary>
    /// The 2026-09-11 field shape: the model skipped the opening tag, so SGLang's qwen3 parser
    /// streamed the thinking and the literal closing tag as content. The text before the tag is
    /// already out (nothing can take it back); the tag and the blank lines after it must not be.
    /// </summary>
    private static string LeakedStream() =>
        Sse
        + Chunk("\"reasoning_content\":null,\"content\":\"Got it.\"")
        + Chunk("\"reasoning_content\":null,\"content\":\" Noted.\\n\"")
        + Chunk("\"reasoning_content\":null,\"content\":\"</think>\"")
        + Chunk("\"reasoning_content\":null,\"content\":\"\\n\\n\"")
        + Chunk("\"reasoning_content\":null,\"content\":\"Done.\"")
        + "data: {\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"my-model\",\"choices\":[{\"index\":0,\"delta\":{\"reasoning_content\":null},\"logprobs\":null,\"finish_reason\":\"stop\"}]}\n\n"
        + "data: [DONE]\n\n";

    /// <summary>A server with no reasoning parser at all: the whole block arrives as content.</summary>
    private static string UnparsedStream() =>
        Sse
        + Chunk("\"content\":\"<think>\\n\"")
        + Chunk("\"content\":\"The user asks.\\n\"")
        + Chunk("\"content\":\"</th\"")
        + Chunk("\"content\":\"ink>\\n\\nThe capital\"")
        + Chunk("\"content\":\" of France is Paris.\"")
        + "data: {\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"my-model\",\"choices\":[{\"index\":0,\"delta\":{},\"logprobs\":null,\"finish_reason\":\"stop\"}]}\n\n"
        + "data: [DONE]\n\n";

    [Fact]
    public async Task Assistant_DropsAnOrphanCloseTag_FromTheTranscriptAndTheHistory()
    {
        var stub = new StubHttpMessageHandler().Map("http://127.0.0.1:1234/v1/chat/completions", HttpStatusCode.OK, LeakedStream(), "text/event-stream");
        using var http = new HttpClient(stub);
        using var client = new OpenAICompatibleChatClient(Endpoint(), TimeSpan.FromSeconds(5), http);
        var history = new ConversationHistory("sys");
        var assistant = new Assistant(client, history, new LlmTimeouts(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)), reasoning: ReasoningEffort.High);

        var deltas = new List<string>();
        await foreach (var evt in assistant.RunTurnAsync("q"))
        {
            if (evt is TurnEvent.TextDelta d) deltas.Add(d.Text);
            Assert.IsNotType<TurnEvent.Notice>(evt);
        }

        Assert.Equal("Got it. Noted.\nDone.", string.Concat(deltas));
        Assert.Equal(2, history.Messages.Count);
        Assert.Equal("Got it. Noted.\nDone.", history.Messages[1].Text);
        Assert.DoesNotContain("</think>", history.Messages[1].Text);
    }

    [Fact]
    public async Task Assistant_DropsAWholeThinkBlock_StreamedAsContent()
    {
        var stub = new StubHttpMessageHandler().Map("http://127.0.0.1:1234/v1/chat/completions", HttpStatusCode.OK, UnparsedStream(), "text/event-stream");
        using var http = new HttpClient(stub);
        using var client = new OpenAICompatibleChatClient(Endpoint(), TimeSpan.FromSeconds(5), http);
        var history = new ConversationHistory("sys");
        var assistant = new Assistant(client, history, new LlmTimeouts(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)));

        var deltas = new List<string>();
        await foreach (var evt in assistant.RunTurnAsync("q"))
        {
            if (evt is TurnEvent.TextDelta d) deltas.Add(d.Text);
            Assert.IsNotType<TurnEvent.Notice>(evt);
        }

        Assert.Equal("The capital of France is Paris.", string.Concat(deltas));
        Assert.Equal(2, history.Messages.Count);
        Assert.Equal("The capital of France is Paris.", history.Messages[1].Text);
    }

    /// <summary>Every event of one turn over <paramref name="sse"/>: the thinking and the reply apart, and the history after it.</summary>
    private static async Task<(string Thinking, string Reply, ConversationHistory History)> RunAsync(string sse)
    {
        var stub = new StubHttpMessageHandler().Map("http://127.0.0.1:1234/v1/chat/completions", HttpStatusCode.OK, sse, "text/event-stream");
        using var http = new HttpClient(stub);
        using var client = new OpenAICompatibleChatClient(Endpoint(), TimeSpan.FromSeconds(5), http);
        var history = new ConversationHistory("sys");
        var assistant = new Assistant(client, history, new LlmTimeouts(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)), reasoning: ReasoningEffort.Medium);

        var order = new List<string>();
        var thinking = new List<string>();
        var reply = new List<string>();
        await foreach (var evt in assistant.RunTurnAsync("q"))
        {
            Assert.IsNotType<TurnEvent.Notice>(evt);
            if (evt is TurnEvent.ThinkingDelta t)
            {
                Assert.NotEmpty(t.Text);
                thinking.Add(t.Text);
                Assert.Empty(reply);
            }
            else if (evt is TurnEvent.TextDelta d)
            {
                reply.Add(d.Text);
            }
        }

        return (string.Concat(thinking), string.Concat(reply), history);
    }

    [Fact]
    public async Task Assistant_YieldsTheReasoning_AsThinking_BeforeTheAnswer()
    {
        var (thinking, reply, history) = await RunAsync(Stream());

        Assert.Equal("The user asks.", thinking);
        Assert.Equal("\n\nThe capital of France is Paris.", reply);
        Assert.DoesNotContain("user asks", history.Messages[1].Text);
    }

    [Fact]
    public async Task Assistant_YieldsAThinkBlockStreamedAsContent_AsThinking()
    {
        var (thinking, reply, history) = await RunAsync(UnparsedStream());

        Assert.Equal("\nThe user asks.\n", thinking);
        Assert.Equal("The capital of France is Paris.", reply);
        Assert.Equal("The capital of France is Paris.", history.Messages[1].Text);
    }

    /// <summary>Newer vLLM and Ollama: the thinking under <c>delta.reasoning</c>, which the adapter does not read.</summary>
    private static string ReasoningFieldStream() =>
        Chunk("\"role\":\"assistant\",\"content\":\"\"")
        + Chunk("\"reasoning\":\"The\"")
        + Chunk("\"reasoning\":\" user asks.\"")
        + Chunk("\"content\":\"Paris.\"")
        + "data: {\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"my-model\",\"choices\":[{\"index\":0,\"delta\":{},\"logprobs\":null,\"finish_reason\":\"stop\"}]}\n\n"
        + "data: [DONE]\n\n";

    [Fact]
    public async Task Assistant_YieldsTheReasoningField_AsThinking()
    {
        var (thinking, reply, history) = await RunAsync(ReasoningFieldStream());

        Assert.Equal("The user asks.", thinking);
        Assert.Equal("Paris.", reply);
        Assert.Equal("Paris.", history.Messages[1].Text);
    }

    /// <summary>A server that sends both fields: the adapter's <c>reasoning_content</c> wins, the thinking is not doubled.</summary>
    [Fact]
    public async Task Assistant_TakesReasoningContent_OverTheReasoningField()
    {
        string sse = Chunk("\"role\":\"assistant\",\"content\":\"\"")
            + Chunk("\"reasoning_content\":\"Hm.\",\"reasoning\":\"Hm.\"")
            + Chunk("\"content\":\"Yes.\"")
            + "data: [DONE]\n\n";

        var (thinking, reply, _) = await RunAsync(sse);

        Assert.Equal("Hm.", thinking);
        Assert.Equal("Yes.", reply);
    }
}
