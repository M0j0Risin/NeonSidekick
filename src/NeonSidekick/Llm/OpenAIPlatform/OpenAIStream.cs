using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Diagnostics;
using NeonSidekick.Llm.Anthropic;

namespace NeonSidekick.Llm.OpenAIPlatform;

/// <summary>
/// The Responses API's server-sent events as <see cref="ChatResponseUpdate"/>s (2026-10-03), one event at a time
/// (<see cref="Handle"/>), read with <see cref="JsonDocument"/> — no serializer; <see cref="AnthropicStream"/>'s shape, so the
/// turn loop gets what every other client hands it: <see cref="TextContent"/> as it streams, the reasoning summary as
/// <see cref="TextReasoningContent"/>, one whole <see cref="FunctionCallContent"/> per call at its item's end, and one
/// <see cref="UsageContent"/> with the finish reason at <c>response.completed</c> (or <c>response.incomplete</c>).
///
/// <para>A reasoning item streams its summary as reasoning pieces (a blank line between its parts) and closes with an empty
/// piece carrying its encrypted content behind <see cref="OpenAIRequest.EncryptedPrefix"/>, which the request side sends back
/// within the turn in flight. The usage: <c>input_tokens</c> is the whole prompt, the cache read and write inside it
/// (<c>input_tokens_details.cached_tokens</c>, <c>cache_write_tokens</c>), the reasoning inside <c>output_tokens</c>; the
/// cost (<see cref="OpenAIPrice"/>) and the cache write go under the keys <see cref="TokenUsage.From"/> reads for the Claude API.</para>
/// </summary>
public sealed class OpenAIStream
{
    private const string Category = "Llm";

    private readonly string _modelId;
    private readonly IReadOnlyDictionary<string, string> _toolNames;
    private string? _responseId;
    private string? _model;
    private bool _called;

    /// <param name="modelId">The model asked for, priced when the stream does not name one.</param>
    /// <param name="toolNames">Wire name → the tool's own, for a name <see cref="AnthropicRequest.ToolName"/> changed.</param>
    public OpenAIStream(string modelId, IReadOnlyDictionary<string, string> toolNames)
    {
        _modelId = modelId ?? throw new ArgumentNullException(nameof(modelId));
        _toolNames = toolNames ?? throw new ArgumentNullException(nameof(toolNames));
    }

    /// <summary>Whether the response's last event arrived: a stream that ends without it was cut.</summary>
    public bool Stopped { get; private set; }

    /// <summary>The updates for one event; <paramref name="data"/> is its JSON. A failure event throws <see cref="OpenAIApiException"/>.</summary>
    public IEnumerable<ChatResponseUpdate> Handle(string? eventName, string data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var updates = new List<ChatResponseUpdate>(1);
        if (data == "[DONE]")
        {
            return updates;
        }

        using var document = JsonDocument.Parse(data);
        var root = document.RootElement;
        switch (String(root, "type") ?? eventName ?? "")
        {
            case "response.created":
            case "response.in_progress":
                if (root.TryGetProperty("response"u8, out var started) && started.ValueKind == JsonValueKind.Object)
                {
                    _responseId = String(started, "id") ?? _responseId;
                    _model = String(started, "model") ?? _model;
                }

                break;

            case "response.output_text.delta":
            case "response.refusal.delta":
                if (String(root, "delta") is { Length: > 0 } text)
                {
                    updates.Add(Update(new TextContent(text)));
                }

                break;

            case "response.reasoning_summary_part.added":
                if (Number(root, "summary_index") is > 0)
                {
                    updates.Add(Update(new TextReasoningContent("\n\n")));
                }

                break;

            case "response.reasoning_summary_text.delta":
                if (String(root, "delta") is { Length: > 0 } thinking)
                {
                    updates.Add(Update(new TextReasoningContent(thinking)));
                }

                break;

            case "response.output_item.done":
                if (root.TryGetProperty("item"u8, out var item) && item.ValueKind == JsonValueKind.Object)
                {
                    ItemDone(item, updates);
                }

                break;

            case "response.completed":
            case "response.incomplete":
                Stopped = true;
                Finish(root.TryGetProperty("response"u8, out var finished) && finished.ValueKind == JsonValueKind.Object ? finished : root, updates);
                break;

            case "response.failed":
                var failed = root.TryGetProperty("response"u8, out var response) && response.ValueKind == JsonValueKind.Object
                    && response.TryGetProperty("error"u8, out var cause) && cause.ValueKind == JsonValueKind.Object ? cause : root;
                throw new OpenAIApiException(0, String(failed, "code") ?? "failed", String(failed, "message") ?? data);

            case "error":
                var error = root.TryGetProperty("error"u8, out var e) && e.ValueKind == JsonValueKind.Object ? e : root;
                throw new OpenAIApiException(0, String(error, "code") ?? String(error, "type") ?? "error", String(error, "message") ?? data);
        }

        return updates;
    }

    private void ItemDone(JsonElement item, List<ChatResponseUpdate> updates)
    {
        switch (String(item, "type"))
        {
            case "function_call":
                _called = true;
                string wire = String(item, "name") ?? "";
                string name = _toolNames.TryGetValue(wire, out var original) ? original : wire;
                updates.Add(Update(AnthropicStream.Call(String(item, "call_id") ?? String(item, "id") ?? "", name, String(item, "arguments") ?? "")));
                break;

            case "reasoning":
                if (String(item, "encrypted_content") is { Length: > 0 } encrypted)
                {
                    updates.Add(Update(new TextReasoningContent("") { ProtectedData = OpenAIRequest.EncryptedPrefix + encrypted }));
                }

                break;
        }
    }

    private void Finish(JsonElement response, List<ChatResponseUpdate> updates)
    {
        _model = String(response, "model") ?? _model;
        string? incomplete = response.TryGetProperty("incomplete_details"u8, out var details) && details.ValueKind == JsonValueKind.Object ? String(details, "reason") : null;
        if (incomplete == "max_output_tokens")
        {
            DiagnosticLog.Warn(Category, OpenAIApiText.MaxTokensWarning(_model ?? _modelId));
        }
        else if (incomplete is not null)
        {
            DiagnosticLog.Warn(Category, $"The OpenAI API reply ({_model ?? _modelId}) is incomplete: {incomplete}.");
        }

        var update = new ChatResponseUpdate(ChatRole.Assistant, [])
        {
            MessageId = _responseId,
            ResponseId = _responseId,
            ModelId = _model ?? _modelId,
            FinishReason = incomplete switch
            {
                "max_output_tokens" => ChatFinishReason.Length,
                "content_filter" => ChatFinishReason.ContentFilter,
                _ => _called ? ChatFinishReason.ToolCalls : ChatFinishReason.Stop,
            },
        };
        if (response.TryGetProperty("usage"u8, out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            update.Contents.Add(new UsageContent(Usage(usage, _model ?? _modelId)));
        }

        updates.Add(update);
    }

    /// <summary>The report as <see cref="UsageDetails"/>, the cache write and the cost in <see cref="UsageDetails.AdditionalCounts"/>.</summary>
    internal static UsageDetails Usage(JsonElement usage, string model)
    {
        long input = Number(usage, "input_tokens") ?? 0;
        long output = Number(usage, "output_tokens") ?? 0;
        long cached = 0;
        long? written = null;
        if (usage.TryGetProperty("input_tokens_details"u8, out var inputDetails) && inputDetails.ValueKind == JsonValueKind.Object)
        {
            cached = Number(inputDetails, "cached_tokens") ?? 0;
            written = Number(inputDetails, "cache_write_tokens");
        }

        long? reasoning = usage.TryGetProperty("output_tokens_details"u8, out var outputDetails) && outputDetails.ValueKind == JsonValueKind.Object
            ? Number(outputDetails, "reasoning_tokens")
            : null;
        var details = new UsageDetails
        {
            InputTokenCount = input,
            OutputTokenCount = output,
            TotalTokenCount = Number(usage, "total_tokens") ?? input + output,
            CachedInputTokenCount = cached,
            ReasoningTokenCount = reasoning,
            AdditionalCounts = [],
        };
        if (written is { } cacheWrite)
        {
            details.AdditionalCounts[AnthropicStream.CacheWriteKey] = cacheWrite;
        }

        if (OpenAIPrice.For(model) is { } price)
        {
            details.AdditionalCounts[AnthropicStream.CostKey] = (long)Math.Round(price.Cost(input, cached, written ?? 0, output) * 1_000_000_000m);
        }

        return details;
    }

    private ChatResponseUpdate Update(AIContent content) => new(ChatRole.Assistant, [content])
    {
        MessageId = _responseId,
        ResponseId = _responseId,
        ModelId = _model ?? _modelId,
    };

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number) ? number : null;
}

/// <summary>
/// An OpenAI API request that failed (2026-10-03): the HTTP status (0 for a failure the stream reported), the API's error
/// code or type and its message — <see cref="AnthropicApiException"/>'s twin. A key problem says where the key is set.
/// </summary>
public sealed class OpenAIApiException : Exception
{
    public OpenAIApiException(int status, string errorType, string message)
        : base(Compose(status, errorType, message))
    {
        Status = status;
        ErrorType = errorType;
    }

    public OpenAIApiException()
    {
        ErrorType = "";
    }

    public OpenAIApiException(string message)
        : base(message)
    {
        ErrorType = "";
    }

    public OpenAIApiException(string message, Exception innerException)
        : base(message, innerException)
    {
        ErrorType = "";
    }

    /// <summary>The HTTP status, or 0 for a failure the stream reported.</summary>
    public int Status { get; }

    /// <summary>The API's <c>error.code</c>, else its <c>error.type</c> (<c>invalid_api_key</c>, <c>rate_limit_exceeded</c>…).</summary>
    public string ErrorType { get; }

    /// <summary>What a key problem adds. Pinned.</summary>
    public const string KeyHint = " — " + OpenAIApiText.KeyHint;

    /// <summary><c>HTTP 401: Incorrect API key provided … — check …</c>; <c>server_error: …</c> for a stream's failure. Pinned.</summary>
    public static string Compose(int status, string errorType, string message)
    {
        string head = status > 0 ? "HTTP " + status.ToString(CultureInfo.InvariantCulture) : errorType;
        string text = head + ": " + message;
        return status is 401 or 403 || errorType is "invalid_api_key" ? text + KeyHint : text;
    }
}
