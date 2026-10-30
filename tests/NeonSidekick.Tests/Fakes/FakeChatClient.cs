using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace NeonSidekick.Tests.Fakes;

/// <summary>
/// A scripted <see cref="IChatClient"/>: each call dequeues one response, a list of updates
/// streamed in order. Records every request so tests can assert what the model was sent.
/// </summary>
public sealed class FakeChatClient : IChatClient
{
    private readonly Queue<IReadOnlyList<ChatResponseUpdate>> _scripts = new();

    /// <summary>The messages of every request, in call order.</summary>
    public List<IReadOnlyList<ChatMessage>> Requests { get; } = new();

    /// <summary>The options of every request, in call order.</summary>
    public List<ChatOptions?> Options { get; } = new();

    /// <summary>Index of the update before which the stream throws; <c>int.MaxValue</c> never throws. Equal to the update count throws after the last one.</summary>
    public int ThrowAt { get; set; } = int.MaxValue;

    /// <summary>What <see cref="ThrowAt"/> throws; defaults to an <see cref="HttpRequestException"/>.</summary>
    public Exception? Failure { get; set; }

    /// <summary>Called before each update is yielded, with its index; tests cancel or advance clocks here.</summary>
    public Func<int, CancellationToken, Task>? BeforeUpdate { get; set; }

    /// <summary>
    /// <see cref="BeforeUpdate"/> with the request's index in <see cref="Requests"/> first: two
    /// streams can be in flight at once (a turn and a skill-learning reflection), and a test that
    /// gates one of them needs to know which one it is serving.
    /// </summary>
    public Func<int, int, CancellationToken, Task>? BeforeUpdateOf { get; set; }

    /// <summary>
    /// Called after the last update was yielded and before the stream ends, WITHOUT a token check
    /// afterwards: the stream then ends normally even if the turn was cancelled meanwhile, which
    /// is what a real server does when its last token was already on the wire. Tests use it to
    /// hold the stream's end until something else (the speech queue) has reacted to a cancellation.
    /// </summary>
    public Func<CancellationToken, Task>? AfterLastUpdate { get; set; }

    public bool Disposed { get; private set; }

    public FakeChatClient Enqueue(params ChatResponseUpdate[] updates)
    {
        _scripts.Enqueue(updates);
        return this;
    }

    public FakeChatClient EnqueueText(params string[] deltas)
    {
        _scripts.Enqueue(deltas.Select(Text).ToArray());
        return this;
    }

    public static ChatResponseUpdate Text(string text) => new(ChatRole.Assistant, text);

    /// <summary>The server's usage report: the last chunk of a stream, as the OpenAI adapter delivers it.</summary>
    public static ChatResponseUpdate Usage(long input, long output, long? reasoning = null) =>
        new(ChatRole.Assistant, new List<AIContent> { new UsageContent(new UsageDetails { InputTokenCount = input, OutputTokenCount = output, TotalTokenCount = input + output, ReasoningTokenCount = reasoning }) });

    /// <summary>
    /// A contents-free update marked <see cref="NeonSidekick.Llm.StreamProgress.Marker"/>: what a real client yields while the model
    /// produces what it holds back (a tool call's arguments, hidden thinking). <see cref="Call"/> stays one whole update, as the
    /// real clients send it.
    /// </summary>
    public static ChatResponseUpdate Progress() =>
        new(ChatRole.Assistant, new List<AIContent>()) { RawRepresentation = NeonSidekick.Llm.StreamProgress.Marker };

    /// <summary>
    /// A contents-free update carrying one tool-call delta as the OpenAI SDK's chunk, as the M.E.AI adapter yields it while a call
    /// streams (2026-10-29): the id and name on a call's first chunk, then its arguments' pieces. What <see cref="NeonSidekick.Llm.ToolArgumentWatch"/> reads.
    /// </summary>
    public static ChatResponseUpdate ArgumentDelta(int index, string? callId, string? name, string? arguments) =>
        ArgumentDeltas((index, callId, name, arguments));

    /// <summary><see cref="ArgumentDelta"/> with several calls' deltas in one chunk, as a server that sends its calls whole does.</summary>
    public static ChatResponseUpdate ArgumentDeltas(params (int Index, string? CallId, string? Name, string? Arguments)[] deltas) =>
        new(ChatRole.Assistant, new List<AIContent>())
        {
            // All thirteen arguments of the stable overload: with fewer, the compiler picks the newer one marked OPENAI001 (evaluation only).
            RawRepresentation = OpenAI.Chat.OpenAIChatModelFactory.StreamingChatCompletionUpdate(
                null,
                null,
                null,
                [.. deltas.Select(d => OpenAI.Chat.OpenAIChatModelFactory.StreamingChatToolCallUpdate(d.Index, d.CallId, OpenAI.Chat.ChatToolCallKind.Function, d.Name, d.Arguments is null ? null : BinaryData.FromString(d.Arguments)))],
                null,
                null,
                null,
                null,
                null,
                default,
                null,
                (string?)null,
                null),
        };

    /// <summary>How many updates every stream so far has yielded: a test of a stream left early reads what was never read.</summary>
    public int Yielded { get; private set; }

    public static ChatResponseUpdate Call(string callId, string name, IDictionary<string, object?>? arguments = null) =>
        new(ChatRole.Assistant, new List<AIContent> { new FunctionCallContent(callId, name, arguments) })
        {
            FinishReason = ChatFinishReason.ToolCalls,
        };

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        int request = Requests.Count;
        Requests.Add(messages.ToList());
        Options.Add(options);

        if (_scripts.Count == 0)
        {
            throw new InvalidOperationException("FakeChatClient has no scripted response left.");
        }

        var updates = _scripts.Dequeue();
        for (int i = 0; i <= updates.Count; i++)
        {
            if (i == ThrowAt)
            {
                throw Failure ?? new HttpRequestException("scripted failure");
            }

            if (i == updates.Count)
            {
                break;
            }

            if (BeforeUpdate is not null)
            {
                await BeforeUpdate(i, cancellationToken);
            }

            if (BeforeUpdateOf is not null)
            {
                await BeforeUpdateOf(request, i, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            Yielded++;
            yield return updates[i];
        }

        if (AfterLastUpdate is not null)
        {
            await AfterLastUpdate(cancellationToken);
        }
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var collected = new List<ChatResponseUpdate>();
        await foreach (var update in GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            collected.Add(update);
        }

        return collected.ToChatResponse();
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() => Disposed = true;
}
