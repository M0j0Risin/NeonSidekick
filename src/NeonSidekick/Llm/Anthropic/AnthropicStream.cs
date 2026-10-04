using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.Llm.Anthropic;

/// <summary>
/// The Messages API's server-sent events as <see cref="ChatResponseUpdate"/>s (2026-09-27), one event at a time
/// (<see cref="Handle"/>), read with <see cref="JsonDocument"/> — no serializer. What the turn loop gets is what the
/// OpenAI adapter hands it: <see cref="TextContent"/> as it streams, <see cref="TextReasoningContent"/> for thinking,
/// one whole <see cref="FunctionCallContent"/> per call at its block's end, and one <see cref="UsageContent"/> with
/// the finish reason at <c>message_stop</c>.
///
/// <para>A thinking block streams as reasoning pieces and closes with an empty piece carrying the block's signature in
/// <see cref="TextReasoningContent.ProtectedData"/> (a redacted block, its data behind <see cref="AnthropicRequest.RedactedPrefix"/>):
/// the request side (<see cref="AnthropicRequest"/>) rebuilds the block from the pieces up to the signed one, whether
/// or not <c>ToChatResponse</c> coalesced them.</para>
///
/// <para>The usage: the API's <c>input_tokens</c> leaves out what the cache wrote and read, so the prompt count here
/// is the three summed — the context the request carried, which is what the auto-compact and <c>/usage</c>'s Context
/// section measure; the cache read goes in <see cref="UsageDetails.CachedInputTokenCount"/>, the write and the cost
/// (<see cref="ClaudePrice"/>, in billionths of a dollar) in <see cref="UsageDetails.AdditionalCounts"/>.</para>
/// </summary>
public sealed class AnthropicStream
{
    private const string Category = "Llm";

    /// <summary>The <see cref="UsageDetails.AdditionalCounts"/> key of the prompt tokens the cache wrote.</summary>
    public const string CacheWriteKey = "cache_creation_input_tokens";

    /// <summary>The <see cref="UsageDetails.AdditionalCounts"/> key of the request's cost, in billionths of a US dollar.</summary>
    public const string CostKey = "neon.cost_nano_usd";

    private readonly string _modelId;
    private readonly IReadOnlyDictionary<string, string> _toolNames;
    private readonly Dictionary<int, Block> _blocks = [];
    private string? _messageId;
    private string? _model;
    private long _input;
    private long _cacheWrite;
    private long _cacheRead;
    private long _output;
    private string? _stopReason;
    private string? _refusalCategory;

    /// <param name="modelId">The model asked for, priced when the stream does not name one.</param>
    /// <param name="toolNames">Wire name → the tool's own, for a name <see cref="AnthropicRequest.ToolName"/> changed.</param>
    public AnthropicStream(string modelId, IReadOnlyDictionary<string, string> toolNames)
    {
        _modelId = modelId ?? throw new ArgumentNullException(nameof(modelId));
        _toolNames = toolNames ?? throw new ArgumentNullException(nameof(toolNames));
    }

    /// <summary>Whether <c>message_stop</c> arrived: a stream that ends without it was cut.</summary>
    public bool Stopped { get; private set; }

    /// <summary>The stop reason <c>message_delta</c> gave, once it did.</summary>
    public string? StopReason => _stopReason;

    /// <summary>
    /// The text a reply ends with when the model declined it (<c>stop_reason: refusal</c>): said in the reply itself, so
    /// the transcript and the next turn both see it. Pinned.
    /// </summary>
    public static string RefusalNote(string? category) =>
        "\n\n[Claude declined to continue" + (string.IsNullOrWhiteSpace(category) ? "" : " (" + category + ")") + ".]";

    /// <summary>The log line for a reply cut at the output cap. Pinned.</summary>
    public static string MaxTokensWarning(string model) =>
        $"The Claude API reply ({model}) stopped at its output cap; raise Claude API max tokens in /settings › Claude if replies are cut short.";

    /// <summary>The updates for one event; <paramref name="data"/> is its JSON. An <c>error</c> event throws <see cref="AnthropicApiException"/>.</summary>
    public IEnumerable<ChatResponseUpdate> Handle(string? eventName, string data)
    {
        ArgumentNullException.ThrowIfNull(data);
        using var document = JsonDocument.Parse(data);
        var root = document.RootElement;
        string type = String(root, "type") ?? eventName ?? "";
        var updates = new List<ChatResponseUpdate>(1);
        switch (type)
        {
            case "message_start":
                if (root.TryGetProperty("message"u8, out var message) && message.ValueKind == JsonValueKind.Object)
                {
                    _messageId = String(message, "id");
                    _model = String(message, "model");
                    ReadUsage(message);
                }

                break;

            case "content_block_start":
                Start(root, updates);
                break;

            case "content_block_delta":
                Delta(root, updates);
                break;

            case "content_block_stop":
                Stop(root, updates);
                break;

            case "message_delta":
                if (root.TryGetProperty("delta"u8, out var delta) && delta.ValueKind == JsonValueKind.Object)
                {
                    _stopReason = String(delta, "stop_reason") ?? _stopReason;
                    if (delta.TryGetProperty("stop_details"u8, out var details) && details.ValueKind == JsonValueKind.Object)
                    {
                        _refusalCategory = String(details, "category");
                    }
                }

                ReadUsage(root);
                break;

            case "message_stop":
                Stopped = true;
                Finish(updates);
                break;

            case "error":
                var error = root.TryGetProperty("error"u8, out var e) && e.ValueKind == JsonValueKind.Object ? e : root;
                throw new AnthropicApiException(0, String(error, "type") ?? "error", String(error, "message") ?? data);
        }

        return updates;
    }

    private void Start(JsonElement root, List<ChatResponseUpdate> updates)
    {
        if (!root.TryGetProperty("content_block"u8, out var block) || block.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        int index = Index(root);
        string kind = String(block, "type") ?? "";
        switch (kind)
        {
            case "text":
                _blocks[index] = new Block(kind);
                if (String(block, "text") is { Length: > 0 } text)
                {
                    updates.Add(Update(new TextContent(text)));
                }

                break;
            case "thinking":
                _blocks[index] = new Block(kind);
                if (String(block, "thinking") is { Length: > 0 } thinking)
                {
                    updates.Add(Update(new TextReasoningContent(thinking)));
                }

                break;
            case "redacted_thinking":
                _blocks[index] = new Block(kind);
                updates.Add(Update(new TextReasoningContent("") { ProtectedData = AnthropicRequest.RedactedPrefix + (String(block, "data") ?? "") }));
                break;
            case "tool_use":
                _blocks[index] = new Block(kind) { Id = String(block, "id") ?? "", Name = String(block, "name") ?? "" };
                break;
        }
    }

    private void Delta(JsonElement root, List<ChatResponseUpdate> updates)
    {
        if (!root.TryGetProperty("delta"u8, out var delta) || delta.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        _blocks.TryGetValue(Index(root), out var block);
        switch (String(delta, "type"))
        {
            case "text_delta":
                if (String(delta, "text") is { Length: > 0 } text)
                {
                    updates.Add(Update(new TextContent(text)));
                }

                break;
            case "thinking_delta":
                if (String(delta, "thinking") is { Length: > 0 } thinking)
                {
                    updates.Add(Update(new TextReasoningContent(thinking)));
                }

                break;
            case "signature_delta":
                block?.Buffer.Append(String(delta, "signature"));
                break;
            case "input_json_delta":
                block?.Buffer.Append(String(delta, "partial_json"));
                break;
        }
    }

    private void Stop(JsonElement root, List<ChatResponseUpdate> updates)
    {
        int index = Index(root);
        if (!_blocks.Remove(index, out var block))
        {
            return;
        }

        if (block.Kind == "thinking" && block.Buffer.Length > 0)
        {
            updates.Add(Update(new TextReasoningContent("") { ProtectedData = block.Buffer.ToString() }));
        }
        else if (block.Kind == "tool_use")
        {
            string name = _toolNames.TryGetValue(block.Name, out var original) ? original : block.Name;
            updates.Add(Update(Call(block.Id, name, block.Buffer.ToString())));
        }
    }

    /// <summary>A whole call from its streamed input; input that does not parse is the call's <see cref="FunctionCallContent.Exception"/>, as the adapters do.</summary>
    internal static FunctionCallContent Call(string id, string name, string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return new FunctionCallContent(id, name, new Dictionary<string, object?>());
        }

        try
        {
            using var document = JsonDocument.Parse(input);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("The tool input is not a JSON object.");
            }

            var arguments = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                arguments[property.Name] = property.Value.Clone();
            }

            return new FunctionCallContent(id, name, arguments);
        }
        catch (JsonException ex)
        {
            return new FunctionCallContent(id, name) { Exception = ex };
        }
    }

    private void Finish(List<ChatResponseUpdate> updates)
    {
        if (_stopReason == "refusal")
        {
            DiagnosticLog.Warn(Category, $"The Claude API declined the request (stop reason refusal{(_refusalCategory is null ? "" : ", " + _refusalCategory)}).");
            updates.Add(Update(new TextContent(RefusalNote(_refusalCategory))));
        }
        else if (_stopReason == "max_tokens")
        {
            DiagnosticLog.Warn(Category, MaxTokensWarning(_model ?? _modelId));
        }

        long prompt = _input + _cacheWrite + _cacheRead;
        var usage = new UsageDetails
        {
            InputTokenCount = prompt,
            OutputTokenCount = _output,
            TotalTokenCount = prompt + _output,
            CachedInputTokenCount = _cacheRead,
            AdditionalCounts = new AdditionalPropertiesDictionary<long> { [CacheWriteKey] = _cacheWrite },
        };
        if (ClaudePrice.For(_model ?? _modelId) is { } price)
        {
            usage.AdditionalCounts[CostKey] = (long)Math.Round(price.Cost(_input, _cacheWrite, _cacheRead, _output) * 1_000_000_000m);
        }

        var update = Update(new UsageContent(usage));
        update.FinishReason = _stopReason switch
        {
            "tool_use" => ChatFinishReason.ToolCalls,
            "max_tokens" => ChatFinishReason.Length,
            "refusal" => ChatFinishReason.ContentFilter,
            _ => ChatFinishReason.Stop,
        };
        updates.Add(update);
    }

    /// <summary>The usage fields of <paramref name="holder"/>'s <c>usage</c>, when present: later events carry the running figures.</summary>
    private void ReadUsage(JsonElement holder)
    {
        if (!holder.TryGetProperty("usage"u8, out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        _input = Number(usage, "input_tokens") ?? _input;
        _cacheWrite = Number(usage, "cache_creation_input_tokens") ?? _cacheWrite;
        _cacheRead = Number(usage, "cache_read_input_tokens") ?? _cacheRead;
        _output = Number(usage, "output_tokens") ?? _output;
    }

    private ChatResponseUpdate Update(AIContent content) => new(ChatRole.Assistant, [content])
    {
        MessageId = _messageId,
        ResponseId = _messageId,
        ModelId = _model ?? _modelId,
    };

    private static int Index(JsonElement root) =>
        root.TryGetProperty("index"u8, out var index) && index.ValueKind == JsonValueKind.Number && index.TryGetInt32(out int value) ? value : 0;

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number) ? number : null;

    /// <summary>A content block while it streams: its kind, and the signature or the tool input as it accumulates.</summary>
    private sealed class Block(string kind)
    {
        public string Kind { get; } = kind;

        public string Id { get; init; } = "";

        public string Name { get; init; } = "";

        public StringBuilder Buffer { get; } = new();
    }
}

/// <summary>
/// A Claude API request that failed (2026-09-27): the HTTP status (0 for an <c>error</c> event mid-stream), the
/// API's error type and its message. The message is what <see cref="Assistant.Explain"/> shows; a key problem says
/// where the key is set.
/// </summary>
public sealed class AnthropicApiException : Exception
{
    public AnthropicApiException(int status, string errorType, string message)
        : base(Compose(status, errorType, message))
    {
        Status = status;
        ErrorType = errorType;
    }

    public AnthropicApiException()
    {
        ErrorType = "";
    }

    public AnthropicApiException(string message)
        : base(message)
    {
        ErrorType = "";
    }

    public AnthropicApiException(string message, Exception innerException)
        : base(message, innerException)
    {
        ErrorType = "";
    }

    /// <summary>The HTTP status, or 0 for an error the stream reported.</summary>
    public int Status { get; }

    /// <summary>The API's <c>error.type</c> (<c>overloaded_error</c>, <c>authentication_error</c>…).</summary>
    public string ErrorType { get; }

    /// <summary>What a key problem adds. Pinned.</summary>
    public const string KeyHint = " — check Claude API key in /settings › Claude";

    /// <summary><c>HTTP 401: invalid x-api-key — check …</c>; <c>overloaded_error: Overloaded</c> for a stream's error. Pinned.</summary>
    public static string Compose(int status, string errorType, string message)
    {
        string head = status > 0 ? "HTTP " + status.ToString(CultureInfo.InvariantCulture) : errorType;
        string text = head + ": " + message;
        return status is 401 or 403 || errorType is "authentication_error" or "permission_error" ? text + KeyHint : text;
    }
}
