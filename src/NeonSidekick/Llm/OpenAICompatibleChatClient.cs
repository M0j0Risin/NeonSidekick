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
        => _wrapped.GetResponseAsync(WithReasoningBack(messages, PreservesThinking(options)), WithRawFields(options), cancellationToken);

    /// <inheritdoc/>
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in _wrapped.GetStreamingResponseAsync(WithReasoningBack(messages, PreservesThinking(options)), WithRawFields(options), cancellationToken).ConfigureAwait(false))
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
    /// The <see cref="ChatOptions.AdditionalProperties"/> key <see cref="Assistant.PreserveThinking"/> sets (2026-09-28,
    /// the setting <c>LLM preserve thinking</c>): send every turn's thinking back, not only the turn in flight's, and ask
    /// the chat template to keep it (<see cref="WithRawFields"/>). Read here and taken off the options before the
    /// adapter sees them; the Claude API client ignores it.
    /// </summary>
    public const string PreserveThinkingKey = "neonsidekick.preserve_thinking";

    /// <summary>Whether <paramref name="options"/> carries <see cref="PreserveThinkingKey"/> set to true.</summary>
    internal static bool PreservesThinking(ChatOptions? options) =>
        options?.AdditionalProperties is { } properties && properties.TryGetValue(PreserveThinkingKey, out object? on) && on is true;

    /// <summary>
    /// The <see cref="ChatOptions.AdditionalProperties"/> key <see cref="LlmSampling.ApplyTo"/> sets (2026-09-28, the setting
    /// <c>LLM sampling</c>): the request's <see cref="LlmSampling"/>, whose top_k, min_p, repetition penalty and extra body
    /// have no slot in the OpenAI schema and go out as raw fields (<see cref="WithRawFields"/>). Read here and taken off
    /// the options before the adapter sees them; the Claude API client ignores it.
    /// </summary>
    public const string SamplingKey = "neonsidekick.sampling";

    /// <summary>The request field the template switches go under, and the one extra-body field merged rather than written as it is.</summary>
    public const string TemplateKwargsField = "chat_template_kwargs";

    /// <summary>The <see cref="LlmSampling"/> <paramref name="options"/> carries under <see cref="SamplingKey"/>, or null.</summary>
    internal static LlmSampling? SamplingOf(ChatOptions? options) =>
        options?.AdditionalProperties is { } properties && properties.TryGetValue(SamplingKey, out object? sampling) ? sampling as LlmSampling : null;

    /// <summary>
    /// The server-specific request shaping, the fields the adapter has no slot for. <see cref="ChatOptions.Reasoning"/>
    /// goes out as <c>reasoning_effort</c> through the adapter on its own; but a Qwen-style chat template (vLLM, SGLang,
    /// llama.cpp) ignores that field and switches thinking off only through <c>chat_template_kwargs.enable_thinking=false</c>.
    /// So <see cref="ReasoningEffort.None"/> hands the adapter a pre-built <see cref="ChatCompletionOptions"/> carrying the
    /// kwarg (the adapter overlays tools, the effort and the standard sampling fields on it afterwards). <see cref="PreserveThinkingKey"/>
    /// (2026-09-28) adds <c>preserve_thinking=true</c> (Qwen3.6: the template renders the thinking of earlier turns too,
    /// where it drops them by default) and <c>clear_thinking=false</c> (GLM's name for the same switch) to the same object.
    /// <see cref="SamplingKey"/> (later on 2026-09-28, <c>LLM sampling</c>) adds top_k, min_p, the repetition penalty and
    /// the extra body (<see cref="RawFieldsJson"/>). Servers and templates that know none of these ignore them. A
    /// caller-supplied factory is left alone; with nothing to add, the options pass through as they came, the keys only
    /// taken off.
    /// </summary>
    internal static ChatOptions? WithRawFields(ChatOptions? options)
    {
        if (options is null)
        {
            return null;
        }

        bool preserve = PreservesThinking(options);
        bool off = options.Reasoning?.Effort == ReasoningEffort.None;
        var sampling = SamplingOf(options);
        bool keyed = options.AdditionalProperties is { } properties && (properties.ContainsKey(PreserveThinkingKey) || properties.ContainsKey(SamplingKey));
        bool raw = (off || preserve || sampling is { HasRawFields: true }) && options.RawRepresentationFactory is null;
        if (!keyed && !raw)
        {
            return options;
        }

        var shaped = options.Clone();
        if (keyed)
        {
            var rest = new AdditionalPropertiesDictionary();
            foreach (var (key, value) in options.AdditionalProperties!)
            {
                if (key is not PreserveThinkingKey and not SamplingKey)
                {
                    rest[key] = value;
                }
            }

            shaped.AdditionalProperties = rest.Count > 0 ? rest : null;
        }

        if (raw)
        {
            byte[] json = RawFieldsJson(off, preserve, sampling);
            shaped.RawRepresentationFactory = _ =>
            {
                var reader = new Utf8JsonReader(json);
                return ((IJsonModel<ChatCompletionOptions>)new ChatCompletionOptions()).Create(ref reader, ModelReaderWriterOptions.Json);
            };
        }

        return shaped;
    }

    /// <summary>
    /// The pre-built request's JSON. The extra body's fields first, as they are; then <c>top_k</c>, <c>min_p</c> and the
    /// repetition penalty under both its names (<c>repetition_penalty</c> for vLLM and SGLang, <c>repeat_penalty</c> for
    /// llama.cpp and LM Studio: each server ignores the name it does not know, so no server is told apart); then
    /// <c>{"chat_template_kwargs":{…}}</c> with the extra body's own kwargs, then <c>enable_thinking=false</c> for
    /// <paramref name="thinkingOff"/> and <c>preserve_thinking=true, clear_thinking=false</c> for <paramref name="preserve"/>
    /// — the app's switches win a key the extra body names too. Read through the SDK's own <see cref="IJsonModel{T}"/>
    /// contract: a property the model does not know is kept and written back on the request. The SDK's <c>Patch</c>
    /// accessor would say the same thing, but it is gated behind an experimental diagnostic and this project suppresses
    /// nothing. Pinned by the wire tests.
    /// </summary>
    internal static byte[] RawFieldsJson(bool thinkingOff, bool preserve, LlmSampling? sampling)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            JsonElement? extraKwargs = null;
            if (sampling is not null)
            {
                foreach (var (name, value) in sampling.Extra)
                {
                    if (name == TemplateKwargsField)
                    {
                        extraKwargs = value;
                        continue;
                    }

                    writer.WritePropertyName(name);
                    value.WriteTo(writer);
                }

                if (sampling.TopK is { } topK)
                {
                    writer.WriteNumber("top_k"u8, topK);
                }

                if (sampling.MinP is { } minP)
                {
                    writer.WriteNumber("min_p"u8, minP);
                }

                if (sampling.RepetitionPenalty is { } repetition)
                {
                    writer.WriteNumber("repetition_penalty"u8, repetition);
                    writer.WriteNumber("repeat_penalty"u8, repetition);
                }
            }

            if (thinkingOff || preserve || extraKwargs is not null)
            {
                writer.WriteStartObject(TemplateKwargsField);
                if (extraKwargs is { } kwargs)
                {
                    foreach (var property in kwargs.EnumerateObject())
                    {
                        bool ours = (thinkingOff && property.NameEquals("enable_thinking"u8))
                            || (preserve && (property.NameEquals("preserve_thinking"u8) || property.NameEquals("clear_thinking"u8)));
                        if (!ours)
                        {
                            property.WriteTo(writer);
                        }
                    }
                }

                if (thinkingOff)
                {
                    writer.WriteBoolean("enable_thinking"u8, false);
                }

                if (preserve)
                {
                    writer.WriteBoolean("preserve_thinking"u8, true);
                    writer.WriteBoolean("clear_thinking"u8, false);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// The model's thinking back on its own messages (2026-09-28, the user's question: "what about preserve thinking?").
    /// The history keeps a reply's thinking as <see cref="TextReasoningContent"/>, but the adapter writes no field for it,
    /// so every request went out without it — the thinking of the turn's earlier tool steps included, which a Qwen3
    /// template renders back by default and DeepSeek and Kimi expect. So an assistant message with thinking in the turn in
    /// flight (after <see cref="ConversationHistory.InFlightStart"/>), or in any turn with <paramref name="preserveAll"/>,
    /// goes out as a clone whose <see cref="ChatMessage.RawRepresentation"/> is the SDK's own message built here with
    /// <c>reasoning_content</c> beside its text and calls: the adapter passes a raw message through as it is. A block
    /// carrying <see cref="TextReasoningContent.ProtectedData"/> is the Claude API's and is not this wire's. Every other
    /// message is untouched, and the history's own messages are never changed.
    /// </summary>
    internal static IEnumerable<ChatMessage> WithReasoningBack(IEnumerable<ChatMessage> messages, bool preserveAll)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var list = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        int inFlight = preserveAll ? -1 : ConversationHistory.InFlightStart(list);
        List<ChatMessage>? shaped = null;
        for (int i = 0; i < list.Count; i++)
        {
            var message = list[i];
            if (i > inFlight && message.Role == ChatRole.Assistant && message.RawRepresentation is not OpenAI.Chat.ChatMessage
                && ThinkingOf(message) is { Length: > 0 } thinking)
            {
                shaped ??= list.Take(i).ToList();
                var clone = message.Clone();
                clone.RawRepresentation = AssistantWithThinking(message, thinking);
                shaped.Add(clone);
            }
            else
            {
                shaped?.Add(message);
            }
        }

        return shaped ?? list;
    }

    /// <summary>The unsigned thinking of <paramref name="message"/>, joined; empty when it has none.</summary>
    private static string ThinkingOf(ChatMessage message) =>
        string.Concat(message.Contents.OfType<TextReasoningContent>().Where(r => r.ProtectedData is null).Select(r => r.Text));

    /// <summary>
    /// <paramref name="message"/> as the SDK's assistant message plus <c>reasoning_content</c>: its text as one string
    /// (none when it has only calls), its calls as <c>tool_calls</c> with the arguments' JSON
    /// (<see cref="Assistant.SerializeArguments"/>), the adapter's own shape for both.
    /// </summary>
    internal static OpenAI.Chat.ChatMessage AssistantWithThinking(ChatMessage message, string thinking)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("role"u8, "assistant"u8);
            string text = string.Concat(message.Contents.OfType<TextContent>().Select(t => t.Text));
            if (text.Length > 0)
            {
                writer.WriteString("content"u8, text);
            }

            var calls = message.Contents.OfType<FunctionCallContent>().ToList();
            if (calls.Count > 0)
            {
                writer.WriteStartArray("tool_calls"u8);
                foreach (var call in calls)
                {
                    writer.WriteStartObject();
                    writer.WriteString("id"u8, call.CallId);
                    writer.WriteString("type"u8, "function"u8);
                    writer.WriteStartObject("function"u8);
                    writer.WriteString("name"u8, call.Name);
                    writer.WriteString("arguments"u8, Assistant.SerializeArguments(call.Arguments));
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }

            writer.WriteString(ReasoningContentProperty, thinking);
            writer.WriteEndObject();
        }

        var reader = new Utf8JsonReader(buffer.WrittenSpan);
        return ((IJsonModel<OpenAI.Chat.ChatMessage>)new AssistantChatMessage("")).Create(ref reader, ModelReaderWriterOptions.Json)
            ?? throw new InvalidOperationException("The SDK read no assistant message back.");
    }

    /// <summary>The message field the thinking goes back under: what Qwen and GLM templates read, and what llama.cpp, SGLang and vLLM take.</summary>
    internal static ReadOnlySpan<byte> ReasoningContentProperty => "reasoning_content"u8;

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
