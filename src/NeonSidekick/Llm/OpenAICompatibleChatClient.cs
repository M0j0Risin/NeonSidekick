using System.Buffers;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace NeonSidekick.Llm;

/// <summary>
/// The production <see cref="IChatClient"/>: Microsoft.Extensions.AI over the OpenAI SDK, pointed
/// at an OpenAI-compatible server (LM Studio, vLLM, SGLang, llama.cpp, Ollama).
///
/// <para>The SDK serialises <c>ChatOptions.Tools</c> and parses streamed <c>tool_calls</c> back
/// into <see cref="FunctionCallContent"/> — fully reassembled, one final update per call — so the
/// turn loop never sees a fragment.</para>
/// </summary>
public sealed class OpenAICompatibleChatClient : IChatClient
{
    /// <summary>Non-null only when this instance created its own transport, and must dispose it.</summary>
    private readonly HttpClient? _ownedHttpClient;

    private readonly TimeSpan _requestTimeout;
    private readonly ChatClient _innerClient;

    // The MEAI wrapper is allocated once. AsIChatClient() returns a new wrapper on every call, so
    // caching it is what makes Dispose() and GetService() refer to the object this class owns.
    private readonly IChatClient _wrapped;

    /// <summary>The endpoint actually in use, base URL normalised to <c>/v1</c>.</summary>
    public LlmEndpoint Endpoint { get; }

    /// <summary>
    /// The per-request ceiling in force on the transport.
    ///
    /// <para>Exposed because "the setting was saved" and "the setting is in force" are different
    /// claims. Applied twice: as <see cref="HttpClient.Timeout"/> on an owned transport,
    /// and as the SDK's <c>NetworkTimeout</c> on every path — the SDK's own default (~100 s) would
    /// otherwise cap a request the user had allowed to run longer.</para>
    /// </summary>
    internal TimeSpan AppliedRequestTimeout => _requestTimeout;

    /// <summary>Whether this instance owns (and will dispose) its <see cref="HttpClient"/>.</summary>
    internal bool OwnsTransport => _ownedHttpClient is not null;

    /// <param name="endpoint">Where to post; the base URL is normalised to <c>/v1</c>.</param>
    /// <param name="requestTimeout">Per-request ceiling, already interlocked by <see cref="LlmTimeouts.Resolve"/>.</param>
    /// <param name="httpClient">Optional transport. Tests pass one over a stub handler; it stays the caller's to dispose.</param>
    public OpenAICompatibleChatClient(LlmEndpoint endpoint, TimeSpan requestTimeout, HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (string.IsNullOrWhiteSpace(endpoint.ModelId))
        {
            throw new ArgumentException("Model id must not be blank.", nameof(endpoint));
        }

        if (requestTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(requestTimeout), requestTimeout, "The request timeout must be positive.");
        }

        var v1 = LlmEndpoint.NormalizeBaseUrl(endpoint.BaseUrl);
        var key = string.IsNullOrWhiteSpace(endpoint.ApiKey) ? LlmEndpoint.DefaultApiKey : endpoint.ApiKey.Trim();
        Endpoint = endpoint with { BaseUrl = v1, ApiKey = key };
        _requestTimeout = requestTimeout;

        var options = new OpenAIClientOptions
        {
            Endpoint = v1,
            NetworkTimeout = requestTimeout,

            // One attempt per request. The SDK default retries three times with back-off, which
            // (a) turns a refused connection into a six-second wait and a four-way repeated error,
            // and (b) multiplies the request ceiling: four attempts at 75 s each outlive the 90 s
            // turn budget the ceiling exists to stay under. A transient failure surfaces as a
            // Notice and the user resends; that is cheaper than a silent five-minute stall.
            RetryPolicy = new ClientRetryPolicy(maxRetries: 0),
        };

        if (httpClient is not null)
        {
            options.Transport = new HttpClientPipelineTransport(httpClient);
        }
        else
        {
            // Fixed for the life of the transport, which is why the request ceiling is
            // restart-scoped while the turn budget can be applied live.
            _ownedHttpClient = new HttpClient { Timeout = requestTimeout };
            options.Transport = new HttpClientPipelineTransport(_ownedHttpClient);
        }

        _innerClient = new ChatClient(Endpoint.ModelId, new ApiKeyCredential(key), options);
        _wrapped = _innerClient.AsIChatClient();
    }

    /// <inheritdoc/>
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => _wrapped.GetResponseAsync(messages, WithThinkingOff(options), cancellationToken);

    /// <inheritdoc/>
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in _wrapped.GetStreamingResponseAsync(messages, WithThinkingOff(options), cancellationToken).ConfigureAwait(false))
        {
            FillTopLevelReasoningTokens(update);
            FillReasoningField(update);
            yield return update;
        }
    }

    /// <summary>
    /// The usage report's reasoning count from the shape the SDK does not model. The adapter maps
    /// OpenAI's <c>completion_tokens_details.reasoning_tokens</c> (LM Studio, vLLM) to
    /// <see cref="UsageDetails.ReasoningTokenCount"/> itself; SGLang puts <c>reasoning_tokens</c> at
    /// the top of <c>usage</c>, which the SDK keeps but names nowhere. Read only when the adapter
    /// left the count empty, so the nested shape wins where a server sends both.
    /// </summary>
    private static void FillTopLevelReasoningTokens(ChatResponseUpdate update)
    {
        foreach (var content in update.Contents)
        {
            if (content is UsageContent { Details.ReasoningTokenCount: null, RawRepresentation: ChatTokenUsage raw } usage)
            {
                usage.Details.ReasoningTokenCount = TopLevelReasoningTokens(raw);
            }
        }
    }

    /// <summary>
    /// SGLang's <c>usage.reasoning_tokens</c>, or null. The SDK keeps a property it does not know and
    /// writes it back through its <see cref="IJsonModel{T}"/> contract, so the report is written out
    /// again and scanned for the field at depth one — the nested <c>completion_tokens_details</c>
    /// object has one of the same name a level down. The SDK's <c>Patch</c> accessor would answer the
    /// same question, but it is gated behind an experimental diagnostic and this project suppresses nothing.
    /// </summary>
    internal static long? TopLevelReasoningTokens(ChatTokenUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            ((IJsonModel<ChatTokenUsage>)usage).Write(writer, ModelReaderWriterOptions.Json);
        }

        var reader = new Utf8JsonReader(buffer.WrittenSpan);
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 1 && reader.ValueTextEquals(ReasoningTokensProperty))
            {
                return reader.Read() && reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out long count) ? count : null;
            }
        }

        return null;
    }

    /// <summary>The top-level field SGLang reports the thinking's share of the completion under.</summary>
    internal static ReadOnlySpan<byte> ReasoningTokensProperty => "reasoning_tokens"u8;

    /// <summary>
    /// The thinking from the field the adapter does not read (2026-09-26, with thinking shown in the
    /// transcript). The adapter maps <c>delta.reasoning_content</c> (SGLang, LM Studio, older vLLM) to
    /// <see cref="TextReasoningContent"/> itself; newer vLLM and Ollama stream <c>delta.reasoning</c>
    /// instead, which the SDK keeps but names nowhere. Looked for only in an update that carries no
    /// text and no reasoning already — a thinking chunk carries neither — so the adapter's mapping
    /// wins where a server sends both, and a reply's chunks are never written out again.
    /// </summary>
    private static void FillReasoningField(ChatResponseUpdate update)
    {
        if (update.RawRepresentation is not StreamingChatCompletionUpdate raw
            || update.Contents.Any(c => c is TextReasoningContent || c is TextContent { Text.Length: > 0 }))
        {
            return;
        }

        if (DeltaReasoning(raw) is { Length: > 0 } reasoning)
        {
            update.Contents.Add(new TextReasoningContent(reasoning));
        }
    }

    /// <summary>
    /// <c>choices[0].delta.reasoning</c> of one streamed chunk as a string, or null. Written out
    /// through the SDK's <see cref="IJsonModel{T}"/> contract, as <see cref="TopLevelReasoningTokens"/>
    /// does, and read back as a document: the chunk is small.
    /// </summary>
    internal static string? DeltaReasoning(StreamingChatCompletionUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            ((IJsonModel<StreamingChatCompletionUpdate>)update).Write(writer, ModelReaderWriterOptions.Json);
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.TryGetProperty("choices"u8, out var choices)
            && choices.ValueKind == JsonValueKind.Array
            && choices.GetArrayLength() > 0
            && choices[0].TryGetProperty("delta"u8, out var delta)
            && delta.ValueKind == JsonValueKind.Object
            && delta.TryGetProperty(ReasoningProperty, out var reasoning)
            && reasoning.ValueKind == JsonValueKind.String
                ? reasoning.GetString()
                : null;
    }

    /// <summary>The delta field newer vLLM and Ollama stream thinking under.</summary>
    internal static ReadOnlySpan<byte> ReasoningProperty => "reasoning"u8;

    /// <summary>
    /// The one piece of server-specific request shaping. <see cref="ChatOptions.Reasoning"/> goes
    /// out as <c>reasoning_effort</c> through the adapter on its own; but a Qwen-style chat
    /// template (vLLM, SGLang, llama.cpp) ignores that field and switches thinking off only
    /// through <c>chat_template_kwargs.enable_thinking=false</c>. So <see cref="ReasoningEffort.None"/>
    /// also hands the adapter a pre-built <see cref="ChatCompletionOptions"/> carrying the kwarg
    /// (the adapter overlays tools and the effort on it afterwards). Servers that know neither
    /// field ignore both. A caller-supplied factory is left alone; every other level passes through.
    /// </summary>
    internal static ChatOptions? WithThinkingOff(ChatOptions? options)
    {
        if (options?.Reasoning?.Effort != ReasoningEffort.None || options.RawRepresentationFactory is not null)
        {
            return options;
        }

        var shaped = options.Clone();
        shaped.RawRepresentationFactory = ThinkingOffFactory;
        return shaped;
    }

    /// <summary>
    /// Built by reading JSON through the SDK's own <see cref="IJsonModel{T}"/> contract: a
    /// property the model does not know is kept and written back on the request. The SDK's
    /// <c>Patch</c> accessor would say the same thing, but it is gated behind an experimental
    /// diagnostic and this project suppresses nothing.
    /// </summary>
    private static readonly Func<IChatClient, object?> ThinkingOffFactory = _ =>
    {
        var reader = new Utf8JsonReader(ThinkingOffJson);
        return ((IJsonModel<ChatCompletionOptions>)new ChatCompletionOptions()).Create(ref reader, ModelReaderWriterOptions.Json);
    };

    /// <summary>The Qwen-family switch. Pinned by the wire test.</summary>
    internal static ReadOnlySpan<byte> ThinkingOffJson => "{\"chat_template_kwargs\":{\"enable_thinking\":false}}"u8;

    /// <inheritdoc/>
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        if (serviceKey is null && serviceType == typeof(IChatClient))
        {
            return this;
        }

        return _wrapped.GetService(serviceType, serviceKey);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _wrapped.Dispose();

        // Only ours to dispose. A caller-supplied HttpClient belongs to the caller — tests reuse
        // one across several clients.
        _ownedHttpClient?.Dispose();
    }
}
