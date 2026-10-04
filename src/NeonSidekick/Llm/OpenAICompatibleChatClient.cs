using System.Buffers;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using NeonSidekick.Diagnostics;
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

    /// <summary>The transport, the caller's or the owned one: the <c>/tokenize</c> request of a reasoning estimate goes over it too.</summary>
    private readonly HttpClient _http;

    /// <summary>The setting <c>LLM reasoning estimate</c>, read at each request (2026-09-29); null estimates nothing.</summary>
    private readonly Func<ReasoningEstimate>? _reasoningEstimate;

    /// <summary>Set once <c>/tokenize</c> failed: this client estimates by the characters from then on, asking no more.</summary>
    private bool _tokenizeUnavailable;

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
    /// <param name="reasoningEstimate">How a reasoning count the server did not report is estimated (<see cref="ReasoningEstimates"/>), read at each request; null estimates nothing.</param>
    /// <param name="organization">The <c>OpenAI-Organization</c> header (2026-10-03, the OpenAI API's alone), or null for none.</param>
    /// <param name="project">The <c>OpenAI-Project</c> header (2026-10-03, the OpenAI API's alone), or null for none.</param>
    public OpenAICompatibleChatClient(LlmEndpoint endpoint, TimeSpan requestTimeout, HttpClient? httpClient = null, Func<ReasoningEstimate>? reasoningEstimate = null, string? organization = null, string? project = null)
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

        // The wire URL (2026-09-29): the embedded model's sentinel base names it, its loopback LiveUrl is where it listens.
        var v1 = LlmEndpoint.NormalizeBaseUrl(endpoint.WireUrl);
        var key = string.IsNullOrWhiteSpace(endpoint.ApiKey) ? LlmEndpoint.DefaultApiKey : endpoint.ApiKey.Trim();
        Endpoint = endpoint.LiveUrl is null ? endpoint with { BaseUrl = v1, ApiKey = key } : endpoint with { LiveUrl = v1, ApiKey = key };
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
            OrganizationId = string.IsNullOrWhiteSpace(organization) ? null : organization.Trim(),
            ProjectId = string.IsNullOrWhiteSpace(project) ? null : project.Trim(),
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
        _http = httpClient ?? _ownedHttpClient!;
        _reasoningEstimate = reasoningEstimate;
    }

    /// <inheritdoc/>
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var response = await _wrapped.GetResponseAsync(WithReasoningBack(messages, PreservesThinking(options)), WithRawFields(options), cancellationToken).ConfigureAwait(false);
        if (response.Usage is { } usage)
        {
            var thinking = new StringBuilder();
            var tags = new ThinkTagFilter();
            foreach (var content in response.Messages.SelectMany(m => m.Contents))
            {
                if (content is TextReasoningContent reasoning)
                {
                    thinking.Append(reasoning.Text);
                }
                else if (content is TextContent { Text.Length: > 0 } text)
                {
                    tags.Push(text.Text);
                    thinking.Append(tags.TakeThinking());
                }
            }

            await FillReasoningEstimateAsync(usage, thinking, cancellationToken).ConfigureAwait(false);
        }

        return response;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // This request's thinking (2026-09-29), for a reasoning count the server leaves out: the reasoning field's text,
        // and what a server left in <think> tags (a filter of its own that only watches; the update goes on unchanged).
        var thinking = new StringBuilder();
        ThinkTagFilter? tags = null;
        await foreach (var update in _wrapped.GetStreamingResponseAsync(WithReasoningBack(messages, PreservesThinking(options)), WithRawFields(options), cancellationToken).ConfigureAwait(false))
        {
            FillTopLevelReasoningTokens(update);
            FillReasoningField(update);
            foreach (var content in update.Contents)
            {
                if (content is TextReasoningContent reasoning)
                {
                    thinking.Append(reasoning.Text);
                }
                else if (content is TextContent { Text.Length: > 0 } text)
                {
                    tags ??= new ThinkTagFilter();
                    tags.Push(text.Text);
                    thinking.Append(tags.TakeThinking());
                }
            }

            foreach (var content in update.Contents)
            {
                if (content is UsageContent usage)
                {
                    await FillReasoningEstimateAsync(usage.Details, thinking, cancellationToken).ConfigureAwait(false);
                }
            }

            yield return update;
        }
    }

    /// <summary>
    /// The <see cref="UsageDetails.AdditionalCounts"/> key that marks <see cref="UsageDetails.ReasoningTokenCount"/> as this
    /// client's estimate, not the server's count (2026-09-29); <see cref="TokenUsage.From"/> reads it into
    /// <see cref="TokenUsage.ReasoningEstimated"/>.
    /// </summary>
    public const string ReasoningEstimatedKey = "neon.reasoning_estimated";

    /// <summary>How long the <c>/tokenize</c> request of a reasoning estimate may take before the characters stand in.</summary>
    internal static readonly TimeSpan TokenizeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The reasoning count of <paramref name="details"/> estimated from <paramref name="thinking"/> (2026-09-29, the setting
    /// <c>LLM reasoning estimate</c>) when the server reported none — llama.cpp and Ollama stream the thinking but do not
    /// count it — and marked with <see cref="ReasoningEstimatedKey"/>. A reported count, <c>0</c> included, is left alone,
    /// and so is a request that streamed no thinking.
    /// </summary>
    private async Task FillReasoningEstimateAsync(UsageDetails details, StringBuilder thinking, CancellationToken cancellationToken)
    {
        if (details.ReasoningTokenCount is not null || thinking.Length == 0 || _reasoningEstimate?.Invoke() is not { } mode || mode == ReasoningEstimate.Off)
        {
            return;
        }

        details.ReasoningTokenCount = await EstimateReasoningAsync(thinking.ToString(), mode, cancellationToken).ConfigureAwait(false);
        details.AdditionalCounts ??= new AdditionalPropertiesDictionary<long>();
        details.AdditionalCounts[ReasoningEstimatedKey] = 1;
    }

    /// <summary>
    /// The token count of <paramref name="thinking"/>: under <see cref="ReasoningEstimate.Tokenize"/> llama.cpp's
    /// <c>POST /tokenize</c> on the server's root (the live URL for the embedded LLM, with the endpoint's key), without the
    /// special tokens; else, or when that does not answer, the characters over <see cref="ConversationCompactor.CharsPerToken"/>,
    /// rounded up. A <c>/tokenize</c> that failed once is not asked again by this client.
    /// </summary>
    internal async Task<long> EstimateReasoningAsync(string thinking, ReasoningEstimate mode, CancellationToken cancellationToken)
    {
        if (mode == ReasoningEstimate.Tokenize && !_tokenizeUnavailable)
        {
            var url = ContextLengthProbe.Under(LlmEndpoint.RootUrl(Endpoint.WireUrl), "tokenize");
            string? answer = await NativeRequest.TextAsync(_http, url, Endpoint.ApiKey, TokenizeTimeout, "Llm", cancellationToken, TokenizeBody(thinking)).ConfigureAwait(false);
            if (TokenCount(answer) is { } counted)
            {
                return counted;
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                _tokenizeUnavailable = true;
                DiagnosticLog.Debug("Llm", $"{url} did not count the thinking; estimating it by its characters from now on.");
            }
        }

        return CharacterEstimate(thinking);
    }

    /// <summary>The characters' estimate: the length over <see cref="ConversationCompactor.CharsPerToken"/>, rounded up.</summary>
    internal static long CharacterEstimate(string thinking) =>
        (thinking.Length + ConversationCompactor.CharsPerToken - 1) / ConversationCompactor.CharsPerToken;

    /// <summary><c>{"content":…,"add_special":false,"parse_special":false}</c>: the text alone, no BOS, its tags as plain text.</summary>
    internal static string TokenizeBody(string text)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("content"u8, text);
            writer.WriteBoolean("add_special"u8, false);
            writer.WriteBoolean("parse_special"u8, false);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>The length of <c>tokens</c> in a <c>/tokenize</c> answer, or null for anything else.</summary>
    internal static long? TokenCount(string? answer)
    {
        if (string.IsNullOrWhiteSpace(answer))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(answer);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("tokens"u8, out var tokens)
                && tokens.ValueKind == JsonValueKind.Array
                    ? tokens.GetArrayLength()
                    : null;
        }
        catch (JsonException)
        {
            return null;
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

    /// <summary>The request field a JSON-schema <see cref="ChatOptions.ResponseFormat"/> is written under (<see cref="RawFieldsJson"/>, 2026-09-28).</summary>
    public const string ResponseFormatField = "response_format";

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
    /// the extra body (<see cref="RawFieldsJson"/>). A JSON-schema <see cref="ChatOptions.ResponseFormat"/> (2026-09-28,
    /// <c>/test</c>'s structured-output tests) goes the same way, written as it came: the adapter's own mapping reshapes the
    /// schema for OpenAI's strict mode (<c>minimum</c> moved into a <c>description</c>, found on the published exe) and sends
    /// no <c>strict</c>, where llama.cpp, vLLM and SGLang compile the schema's keywords into their grammar and LLMTester
    /// sends it verbatim. Servers and templates that know none of these ignore them. A caller-supplied factory is left
    /// alone; with nothing to add, the options pass through as they came, the keys only taken off.
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
        var schema = options.ResponseFormat is ChatResponseFormatJson { Schema: not null } requested ? requested : null;
        bool raw = (off || preserve || sampling is { HasRawFields: true } || schema is not null) && options.RawRepresentationFactory is null;
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
            byte[] json = RawFieldsJson(off, preserve, sampling, schema);
            if (schema is not null)
            {
                shaped.ResponseFormat = null;   // written above as it came; the adapter would reshape it
            }

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
    internal static byte[] RawFieldsJson(bool thinkingOff, bool preserve, LlmSampling? sampling, ChatResponseFormatJson? format = null)
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

                    if (format is not null && name == ResponseFormatField)
                    {
                        continue;   // the request's own schema wins
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

            if (format is { Schema: { } schema })
            {
                // {"type":"json_schema","json_schema":{"name":…,"strict":true,"schema":…}}: LLMTester's shape, strict as it sends it.
                writer.WriteStartObject(ResponseFormatField);
                writer.WriteString("type"u8, "json_schema"u8);
                writer.WriteStartObject("json_schema"u8);
                writer.WriteString("name"u8, format.SchemaName ?? "response");
                if (format.SchemaDescription is { } description)
                {
                    writer.WriteString("description"u8, description);
                }

                writer.WriteBoolean("strict"u8, true);
                writer.WritePropertyName("schema"u8);
                schema.WriteTo(writer);
                writer.WriteEndObject();
                writer.WriteEndObject();
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
