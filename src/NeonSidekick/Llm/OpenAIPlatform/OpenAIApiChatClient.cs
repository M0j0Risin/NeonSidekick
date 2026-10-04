using System.Buffers;
using System.ClientModel.Primitives;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Diagnostics;
using NeonSidekick.Llm.Anthropic;
using OpenAI.Chat;
using ChatFinishReason = Microsoft.Extensions.AI.ChatFinishReason;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace NeonSidekick.Llm.OpenAIPlatform;

/// <summary>
/// The OpenAI API's <see cref="IChatClient"/> (2026-10-03): Chat Completions through <see cref="OpenAICompatibleChatClient"/>
/// — the wire every local server already speaks, tools, pictures and streamed usage included — with the request shaped for
/// OpenAI's own server, which refuses what a local one shrugs off:
/// <list type="bullet">
/// <item>No sampling: <c>LLM sampling</c>'s temperature, top_p, penalties and raw fields are local-server knobs, and the
/// reasoning models 400 on a temperature. Dropped, as the Claude API client ignores them.</item>
/// <item>No <c>chat_template_kwargs</c> (the inner client's <c>none</c> switch, <c>LLM preserve thinking</c>): the API 400s on an
/// unknown field. The reasoning level goes out as <c>reasoning_effort</c>, shaped per model (<see cref="OpenAIModelRules"/>)
/// on a pre-built request, so the inner client adds nothing of its own.</item>
/// <item>No <c>reasoning_content</c> sent back: the API streams no thinking text on Chat Completions, and thinking a local
/// server or the Claude API left in the history earlier is not this wire's.</item>
/// <item>The output cap (<c>OpenAI API max tokens</c>) as <c>max_completion_tokens</c>; 0 sends none.</item>
/// </list>
/// The usage report gains the cache write (GPT-5.6 on reports one) and the cost (<see cref="OpenAIPrice"/>) under
/// <see cref="AnthropicStream.CacheWriteKey"/> and <see cref="AnthropicStream.CostKey"/>, the keys <see cref="TokenUsage.From"/>
/// already reads for the Claude API.
/// </summary>
public sealed class OpenAIApiChatClient : IChatClient
{
    private const string Category = "LLM";

    private readonly OpenAICompatibleChatClient _inner;
    private readonly int _maxTokens;

    /// <param name="endpoint">The OpenAI API endpoint, its key the OpenAI API's own (<see cref="ApiKeys.For"/>).</param>
    /// <param name="requestTimeout">Per-request ceiling.</param>
    /// <param name="maxTokens"><c>OpenAI API max tokens</c>: 0 sends none, else clamped to the setting's range.</param>
    /// <param name="organization">The <c>OpenAI-Organization</c> header, or null for none.</param>
    /// <param name="project">The <c>OpenAI-Project</c> header, or null for none.</param>
    /// <param name="httpClient">Optional transport (tests); stays the caller's to dispose.</param>
    public OpenAIApiChatClient(LlmEndpoint endpoint, TimeSpan requestTimeout, int maxTokens = 0, string? organization = null, string? project = null, HttpClient? httpClient = null)
    {
        _inner = new OpenAICompatibleChatClient(endpoint, requestTimeout, httpClient, reasoningEstimate: null, organization: organization, project: project);
        _maxTokens = maxTokens <= 0 ? 0 : Math.Clamp(maxTokens, Settings.AppSettingsData.MinOpenAIApiMaxTokens, Settings.AppSettingsData.MaxOpenAIApiMaxTokens);
    }

    /// <summary>The endpoint in use, base URL normalised to <c>/v1</c>.</summary>
    public LlmEndpoint Endpoint => _inner.Endpoint;

    /// <summary>The output cap sent, or 0 for none.</summary>
    internal int MaxTokens => _maxTokens;

    /// <inheritdoc/>
    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var response = await _inner.GetResponseAsync(WithoutThinking(messages), Shape(options, Endpoint.ModelId, _maxTokens), cancellationToken).ConfigureAwait(false);
        if (response.Usage is { } usage)
        {
            FillCost(usage, response.RawRepresentation is ChatCompletion completion ? completion.Usage : null, response.ModelId ?? Endpoint.ModelId);
        }

        if (response.FinishReason == ChatFinishReason.Length)
        {
            DiagnosticLog.Warn(Category, OpenAIApiText.MaxTokensWarning(response.ModelId ?? Endpoint.ModelId));
        }

        return response;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in _inner.GetStreamingResponseAsync(WithoutThinking(messages), Shape(options, Endpoint.ModelId, _maxTokens), cancellationToken).ConfigureAwait(false))
        {
            foreach (var content in update.Contents)
            {
                if (content is UsageContent usage)
                {
                    FillCost(usage.Details, usage.RawRepresentation as ChatTokenUsage, update.ModelId ?? Endpoint.ModelId);
                }
            }

            if (update.FinishReason == ChatFinishReason.Length)
            {
                DiagnosticLog.Warn(Category, OpenAIApiText.MaxTokensWarning(update.ModelId ?? Endpoint.ModelId));
            }

            yield return update;
        }
    }

    /// <summary>
    /// The request's options for the OpenAI API: the app's keys and the sampling taken off, the output cap set, and the
    /// reasoning level moved onto a pre-built request as the model's word (none for a model that does not reason, or for no
    /// level asked). A caller's own pre-built request is kept as it is (none of the app's callers makes one).
    /// </summary>
    internal static ChatOptions Shape(ChatOptions? options, string modelId, int maxTokens)
    {
        var shaped = options?.Clone() ?? new ChatOptions();
        if (shaped.AdditionalProperties is { } properties)
        {
            var rest = new AdditionalPropertiesDictionary();
            foreach (var (key, value) in properties)
            {
                if (key is not OpenAICompatibleChatClient.PreserveThinkingKey and not OpenAICompatibleChatClient.SamplingKey)
                {
                    rest[key] = value;
                }
            }

            shaped.AdditionalProperties = rest.Count > 0 ? rest : null;
        }

        shaped.Temperature = null;
        shaped.TopP = null;
        shaped.TopK = null;
        shaped.PresencePenalty = null;
        shaped.FrequencyPenalty = null;
        if (maxTokens > 0)
        {
            shaped.MaxOutputTokens = maxTokens;
        }

        string? word = OpenAIModelRules.For(modelId).EffortWord(shaped.Reasoning?.Effort);
        shaped.Reasoning = null;
        if (shaped.RawRepresentationFactory is null)
        {
            byte[] json = RequestJson(word);
            shaped.RawRepresentationFactory = _ =>
            {
                var reader = new Utf8JsonReader(json);
                return ((IJsonModel<ChatCompletionOptions>)new ChatCompletionOptions()).Create(ref reader, ModelReaderWriterOptions.Json);
            };
        }

        return shaped;
    }

    /// <summary>
    /// The pre-built request: <c>{"reasoning_effort":"…"}</c>, or <c>{}</c> with no word. Read through the SDK's own
    /// <see cref="IJsonModel{T}"/> contract, as <see cref="OpenAICompatibleChatClient.RawFieldsJson"/> is: the SDK's typed
    /// <c>ReasoningEffortLevel</c> is behind an evaluation-only diagnostic, and this project suppresses nothing. Always one,
    /// so the inner client adds no raw fields of its own (it leaves a caller's pre-built request alone) and a JSON-schema
    /// response format goes through the adapter's own mapping, OpenAI's strict shape.
    /// </summary>
    internal static byte[] RequestJson(string? reasoningEffort)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            if (reasoningEffort is not null)
            {
                writer.WriteString("reasoning_effort"u8, reasoningEffort);
            }

            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// <paramref name="messages"/> without their thinking: every <see cref="TextReasoningContent"/> taken off, and an assistant
    /// message left with nothing at all dropped (one that was all thinking).
    /// </summary>
    internal static IEnumerable<ChatMessage> WithoutThinking(IEnumerable<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        foreach (var message in messages)
        {
            if (!message.Contents.Any(c => c is TextReasoningContent))
            {
                yield return message;
                continue;
            }

            var kept = message.Contents.Where(c => c is not TextReasoningContent).ToList();
            if (kept.Count == 0 && message.Role == ChatRole.Assistant)
            {
                continue;
            }

            var copy = message.Clone();
            copy.Contents = kept;
            copy.RawRepresentation = null;
            yield return copy;
        }
    }

    /// <summary>
    /// The cache write and the cost onto <paramref name="details"/>: the write from <c>prompt_tokens_details.cache_write_tokens</c>
    /// (kept only when reported), the cost from <see cref="OpenAIPrice"/> for <paramref name="modelId"/> (none for a model the
    /// table does not name).
    /// </summary>
    internal static void FillCost(UsageDetails details, ChatTokenUsage? raw, string? modelId)
    {
        ArgumentNullException.ThrowIfNull(details);
        long? cacheWrite = raw is null ? null : CacheWriteTokens(raw);
        if (cacheWrite is { } written)
        {
            (details.AdditionalCounts ??= [])[AnthropicStream.CacheWriteKey] = written;
        }

        if (OpenAIPrice.For(modelId) is { } price && details.InputTokenCount is not null)
        {
            decimal cost = price.Cost(details.InputTokenCount ?? 0, details.CachedInputTokenCount ?? 0, cacheWrite ?? 0, details.OutputTokenCount ?? 0);
            (details.AdditionalCounts ??= [])[AnthropicStream.CostKey] = (long)Math.Round(cost * 1_000_000_000m);
        }
    }

    /// <summary>
    /// <c>usage.prompt_tokens_details.cache_write_tokens</c>, or null: the SDK keeps a field it does not model and writes it
    /// back through its <see cref="IJsonModel{T}"/> contract (<see cref="OpenAICompatibleChatClient.TopLevelReasoningTokens"/>'s way).
    /// </summary>
    internal static long? CacheWriteTokens(ChatTokenUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            ((IJsonModel<ChatTokenUsage>)usage).Write(writer, ModelReaderWriterOptions.Json);
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.TryGetProperty("prompt_tokens_details"u8, out var details)
            && details.ValueKind == JsonValueKind.Object
            && details.TryGetProperty("cache_write_tokens"u8, out var written)
            && written.ValueKind == JsonValueKind.Number
            && written.TryGetInt64(out long count)
                ? count
                : null;
    }

    /// <inheritdoc/>
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceKey is null && serviceType.IsInstanceOfType(this))
        {
            return this;
        }

        return _inner.GetService(serviceType, serviceKey);
    }

    /// <inheritdoc/>
    public void Dispose() => _inner.Dispose();
}
