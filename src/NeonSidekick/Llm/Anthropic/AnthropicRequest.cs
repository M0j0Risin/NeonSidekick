using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace NeonSidekick.Llm.Anthropic;

/// <summary>
/// One Messages API request body from the conversation as <see cref="Assistant"/> holds it (2026-09-27). Written with a
/// <see cref="Utf8JsonWriter"/> — no serializer, so nothing here needs a <c>JsonSerializerContext</c> — and shaped
/// where the two APIs differ:
/// <list type="bullet">
/// <item>every <see cref="ChatRole.System"/> message is lifted into the top-level <c>system</c> field, joined in order
/// (the history's prompt, the compactor's and the skill learner's instructions);</item>
/// <item>a <see cref="ChatRole.Tool"/> message becomes a user message of <c>tool_result</c> blocks, and consecutive
/// messages of one role are merged — the image carrier that follows the tool results (<see cref="ConversationHistory.AddToolImages"/>)
/// rides in the same user message, after them, where the API wants it;</item>
/// <item>a <c>tool_use</c> left without its result (a turn cut short) gets an error result, and a result whose call
/// is not in the message before it is sent as text: either would 400 the whole request;</item>
/// <item>thinking blocks go back only in the turn in flight (after the last message the user typed), signed, as the
/// API requires within a tool loop; older turns go without them, so trimming and compacting the history never edit a
/// signed block the API would check (<see cref="ConversationHistory.InFlightStart"/>);</item>
/// <item>tool names the API refuses (it takes <c>[a-zA-Z0-9_-]{1,64}</c>; an MCP tool's may be longer or dotted) are
/// sanitised, the same way every time (<see cref="ToolName"/>), and mapped back on the way in.</item>
/// </list>
/// </summary>
public static class AnthropicRequest
{
    /// <summary>The longest tool name the API takes.</summary>
    public const int MaxToolName = 64;

    /// <summary>What an interrupted call's result says, so the next request is still well formed. Pinned.</summary>
    public const string MissingResult = "(no result: the turn ended before this call ran)";

    /// <summary>What an empty tool result is sent as: the API refuses an empty block. Pinned.</summary>
    public const string EmptyResult = "(no output)";

    /// <summary>
    /// What stands first in a conversation that starts with the model's reply (a trimmed history): the API wants a user
    /// message first. Pinned.
    /// </summary>
    public const string ContinuedPlaceholder = "(conversation continues)";

    /// <summary>The <see cref="TextReasoningContent.ProtectedData"/> prefix of a <c>redacted_thinking</c> block's data.</summary>
    public const string RedactedPrefix = "redacted:";

    /// <summary>
    /// The request body, UTF-8 JSON. <paramref name="maxTokens"/> is required by the API; <paramref name="promptCaching"/>
    /// puts a breakpoint after the tools and the system prompt and turns on the automatic one over the conversation.
    /// <paramref name="withoutThinking"/> sends no thinking block at all — the one-time recovery when the API refused the
    /// ones in flight (the mid-turn prune edited the turn they were signed over; <see cref="AnthropicChatClient"/>).
    /// </summary>
    public static byte[] Write(IReadOnlyList<ChatMessage> messages, ChatOptions? options, string modelId, int maxTokens, bool promptCaching, bool withoutThinking = false)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(modelId);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("model"u8, modelId);
            writer.WriteNumber("max_tokens"u8, maxTokens);
            writer.WriteBoolean("stream"u8, true);

            string system = SystemText(messages);
            if (system.Length > 0)
            {
                if (promptCaching)
                {
                    writer.WriteStartArray("system"u8);
                    writer.WriteStartObject();
                    writer.WriteString("type"u8, "text");
                    writer.WriteString("text"u8, system);
                    WriteCacheControl(writer);
                    writer.WriteEndObject();
                    writer.WriteEndArray();
                }
                else
                {
                    writer.WriteString("system"u8, system);
                }
            }

            WriteTools(writer, options?.Tools);
            WriteThinking(writer, options?.Reasoning?.Effort, ClaudeModelRules.For(modelId), maxTokens);

            if (promptCaching)
            {
                // The automatic breakpoint: the API moves it to the end of the conversation on every request.
                WriteCacheControl(writer);
            }

            writer.WriteStartArray("messages"u8);
            foreach (var message in Shape(messages, withoutThinking))
            {
                writer.WriteStartObject();
                writer.WriteString("role"u8, message.Role);
                writer.WriteStartArray("content"u8);
                foreach (var block in message.Blocks)
                {
                    block.Write(writer);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>The system messages' text, in order, a blank line between.</summary>
    internal static string SystemText(IReadOnlyList<ChatMessage> messages) =>
        string.Join("\n\n", messages.Where(m => m.Role == ChatRole.System).Select(m => m.Text.Trim()).Where(t => t.Length > 0));

    private static void WriteCacheControl(Utf8JsonWriter writer)
    {
        writer.WriteStartObject("cache_control"u8);
        writer.WriteString("type"u8, "ephemeral");
        writer.WriteEndObject();
    }

    private static void WriteTools(Utf8JsonWriter writer, IList<AITool>? tools)
    {
        var functions = tools?.OfType<AIFunctionDeclaration>().ToList() ?? [];
        if (functions.Count == 0)
        {
            return;
        }

        writer.WriteStartArray("tools"u8);
        foreach (var function in functions)
        {
            writer.WriteStartObject();
            writer.WriteString("name"u8, ToolName(function.Name));
            if (!string.IsNullOrWhiteSpace(function.Description))
            {
                writer.WriteString("description"u8, function.Description);
            }

            writer.WritePropertyName("input_schema"u8);
            if (function.JsonSchema.ValueKind == JsonValueKind.Object)
            {
                function.JsonSchema.WriteTo(writer);
            }
            else
            {
                writer.WriteStartObject();
                writer.WriteString("type"u8, "object");
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    /// <summary>
    /// The <c>thinking</c> and <c>output_config</c> fields for one level on one model (<see cref="ClaudeModelRules"/>).
    /// No level (null) sends neither: the model's own default.
    /// </summary>
    internal static void WriteThinking(Utf8JsonWriter writer, ReasoningEffort? effort, ClaudeModelRules rules, int maxTokens)
    {
        if (effort is not { } level)
        {
            return;
        }

        if (rules.Family == ThinkingFamily.Budget)
        {
            if (level == ReasoningEffort.None)
            {
                return;
            }

            int budget = Math.Min(ClaudeModelRules.BudgetFor(level), maxTokens - ClaudeModelRules.MinBudget);
            if (budget < ClaudeModelRules.MinBudget)
            {
                return;
            }

            writer.WriteStartObject("thinking"u8);
            writer.WriteString("type"u8, "enabled");
            writer.WriteNumber("budget_tokens"u8, budget);
            writer.WriteEndObject();
            return;
        }

        if (level == ReasoningEffort.None)
        {
            if (rules.CanDisableThinking)
            {
                writer.WriteStartObject("thinking"u8);
                writer.WriteString("type"u8, "disabled");
                writer.WriteEndObject();
            }
            else
            {
                // Thinking cannot be switched off on this model: the least of it instead.
                writer.WriteStartObject("output_config"u8);
                writer.WriteString("effort"u8, "low");
                writer.WriteEndObject();
            }

            return;
        }

        writer.WriteStartObject("thinking"u8);
        writer.WriteString("type"u8, "adaptive");
        if (rules.SupportsDisplay)
        {
            // Readable thinking for the transcript's fold; the default on the newer models is none at all.
            writer.WriteString("display"u8, "summarized");
        }

        writer.WriteEndObject();
        writer.WriteStartObject("output_config"u8);
        writer.WriteString("effort"u8, rules.EffortWord(level));
        writer.WriteEndObject();
    }

    /// <summary>
    /// <paramref name="name"/> as the API takes it: itself when it already fits <c>[a-zA-Z0-9_-]{1,64}</c>; otherwise
    /// every other character an underscore and, when anything changed, the first 55 characters and a hash of the
    /// original — the same every time, so a call in the history maps to the tool it named.
    /// </summary>
    public static string ToolName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var builder = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            builder.Append(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_');
        }

        string clean = builder.ToString();
        if (clean.Length > 0 && clean.Length <= MaxToolName && string.Equals(clean, name, StringComparison.Ordinal))
        {
            return name;
        }

        uint hash = 2166136261;
        foreach (char c in name)
        {
            hash = (hash ^ c) * 16777619;
        }

        string head = clean.Length > MaxToolName - 9 ? clean[..(MaxToolName - 9)] : clean;
        return head + "_" + hash.ToString("x8", CultureInfo.InvariantCulture);
    }

    /// <summary>The conversation as the API's alternating user / assistant messages (the class summary's rules).</summary>
    internal static List<WireMessage> Shape(IReadOnlyList<ChatMessage> messages, bool withoutThinking = false)
    {
        int inFlight = withoutThinking ? int.MaxValue : ConversationHistory.InFlightStart(messages);
        var shaped = new List<WireMessage>(messages.Count);
        for (int i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            if (message.Role == ChatRole.System)
            {
                continue;
            }

            bool assistant = message.Role == ChatRole.Assistant;
            var blocks = assistant ? AssistantBlocks(message, keepThinking: i > inFlight) : UserBlocks(message);
            if (blocks.Count == 0)
            {
                continue;
            }

            string role = assistant ? "assistant" : "user";
            if (shaped.Count > 0 && shaped[^1].Role == role)
            {
                shaped[^1].Blocks.AddRange(blocks);
            }
            else
            {
                shaped.Add(new WireMessage(role, blocks));
            }
        }

        PairToolResults(shaped);
        if (shaped.Count > 0 && shaped[0].Role == "assistant")
        {
            shaped.Insert(0, new WireMessage("user", [new TextBlock(ContinuedPlaceholder)]));
        }

        return shaped;
    }

    /// <summary>
    /// Every <c>tool_use</c> answered in the next message, results first: a missing one gets <see cref="MissingResult"/>
    /// as an error, and a result with no call before it becomes text.
    /// </summary>
    private static void PairToolResults(List<WireMessage> shaped)
    {
        for (int i = 0; i < shaped.Count; i++)
        {
            var message = shaped[i];
            if (message.Role != "user")
            {
                continue;
            }

            var calls = i > 0 ? shaped[i - 1].Blocks.OfType<ToolUseBlock>().Select(b => b.Id).ToHashSet(StringComparer.Ordinal) : [];
            var results = new List<WireBlock>();
            var rest = new List<WireBlock>();
            foreach (var block in message.Blocks)
            {
                if (block is ToolResultBlock result)
                {
                    if (calls.Remove(result.Id))
                    {
                        results.Add(result);
                    }
                    else
                    {
                        rest.Add(new TextBlock("Tool result: " + result.Content));
                    }
                }
                else
                {
                    rest.Add(block);
                }
            }

            foreach (string id in calls)
            {
                results.Add(new ToolResultBlock(id, MissingResult, IsError: true));
            }

            message.Blocks.Clear();
            message.Blocks.AddRange(results);
            message.Blocks.AddRange(rest);
        }

        // A reply whose calls the conversation never answered at all (the last message is the model's, calls in it).
        if (shaped.Count > 0 && shaped[^1].Role == "assistant" && shaped[^1].Blocks.OfType<ToolUseBlock>().ToList() is { Count: > 0 } open)
        {
            shaped.Add(new WireMessage("user", open.Select(c => (WireBlock)new ToolResultBlock(c.Id, MissingResult, IsError: true)).ToList()));
        }
    }

    private static List<WireBlock> UserBlocks(ChatMessage message)
    {
        var blocks = new List<WireBlock>();
        foreach (var content in message.Contents)
        {
            switch (content)
            {
                case FunctionResultContent result:
                    blocks.Add(new ToolResultBlock(result.CallId, ResultText(result), result.Exception is not null));
                    break;
                case TextContent { Text.Length: > 0 } text when !string.IsNullOrWhiteSpace(text.Text):
                    blocks.Add(new TextBlock(text.Text));
                    break;
                case DataContent data when IsImage(data.MediaType) || data.MediaType == "application/pdf":
                    blocks.Add(new MediaBlock(data.MediaType, Convert.ToBase64String(data.Data.Span)));
                    break;
                case UriContent uri when IsImage(uri.MediaType):
                    blocks.Add(new ImageUrlBlock(uri.Uri.AbsoluteUri));
                    break;
            }
        }

        return blocks;
    }

    private static List<WireBlock> AssistantBlocks(ChatMessage message, bool keepThinking)
    {
        var blocks = new List<WireBlock>();
        var thinking = new StringBuilder();
        foreach (var content in message.Contents)
        {
            switch (content)
            {
                case TextReasoningContent reasoning:
                    // A block's text may arrive in several pieces; the one that carries the signature closes it.
                    thinking.Append(reasoning.Text);
                    if (reasoning.ProtectedData is { Length: > 0 } signature)
                    {
                        if (keepThinking)
                        {
                            blocks.Add(signature.StartsWith(RedactedPrefix, StringComparison.Ordinal)
                                ? new RedactedThinkingBlock(signature[RedactedPrefix.Length..])
                                : new ThinkingBlock(thinking.ToString(), signature));
                        }

                        thinking.Clear();
                    }

                    break;
                case TextContent text when !string.IsNullOrWhiteSpace(text.Text):
                    thinking.Clear();
                    blocks.Add(new TextBlock(text.Text));
                    break;
                case FunctionCallContent call:
                    thinking.Clear();
                    blocks.Add(new ToolUseBlock(call.CallId, ToolName(call.Name), call.Arguments));
                    break;
            }
        }

        // Trailing whitespace on the model's last text is refused; the reply is text we already showed.
        if (blocks.Count > 0 && blocks[^1] is TextBlock last)
        {
            blocks[^1] = new TextBlock(last.Text.TrimEnd());
        }

        return blocks;
    }

    private static bool IsImage(string? mediaType) =>
        mediaType is "image/png" or "image/jpeg" or "image/gif" or "image/webp";

    /// <summary>A tool result as the text the API carries.</summary>
    internal static string ResultText(FunctionResultContent result)
    {
        string text = result.Result switch
        {
            null => result.Exception?.Message ?? "",
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString() ?? "",
            JsonElement element => element.GetRawText(),
            var other => Convert.ToString(other, CultureInfo.InvariantCulture) ?? "",
        };
        return string.IsNullOrWhiteSpace(text) ? EmptyResult : text;
    }

    /// <summary>One argument value, written without a serializer: the adapters hand <see cref="JsonElement"/>s.</summary>
    internal static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null: writer.WriteNullValue(); break;
            case JsonElement element: element.WriteTo(writer); break;
            case string s: writer.WriteStringValue(s); break;
            case bool b: writer.WriteBooleanValue(b); break;
            case int n: writer.WriteNumberValue(n); break;
            case long n: writer.WriteNumberValue(n); break;
            case double n: writer.WriteNumberValue(n); break;
            case decimal n: writer.WriteNumberValue(n); break;
            case IDictionary<string, object?> map:
                writer.WriteStartObject();
                foreach (var (key, item) in map)
                {
                    writer.WritePropertyName(key);
                    WriteValue(writer, item);
                }

                writer.WriteEndObject();
                break;
            case IEnumerable<object?> list:
                writer.WriteStartArray();
                foreach (var item in list)
                {
                    WriteValue(writer, item);
                }

                writer.WriteEndArray();
                break;
            default: writer.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture)); break;
        }
    }

    /// <summary>One message as the API takes it.</summary>
    internal sealed record WireMessage(string Role, List<WireBlock> Blocks);

    /// <summary>One content block.</summary>
    internal abstract record WireBlock
    {
        public abstract void Write(Utf8JsonWriter writer);
    }

    internal sealed record TextBlock(string Text) : WireBlock
    {
        public override void Write(Utf8JsonWriter writer)
        {
            writer.WriteStartObject();
            writer.WriteString("type"u8, "text");
            writer.WriteString("text"u8, Text);
            writer.WriteEndObject();
        }
    }

    internal sealed record MediaBlock(string MediaType, string Base64) : WireBlock
    {
        public override void Write(Utf8JsonWriter writer)
        {
            writer.WriteStartObject();
            writer.WriteString("type"u8, MediaType == "application/pdf" ? "document" : "image");
            writer.WriteStartObject("source"u8);
            writer.WriteString("type"u8, "base64");
            writer.WriteString("media_type"u8, MediaType);
            writer.WriteString("data"u8, Base64);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
    }

    internal sealed record ImageUrlBlock(string Url) : WireBlock
    {
        public override void Write(Utf8JsonWriter writer)
        {
            writer.WriteStartObject();
            writer.WriteString("type"u8, "image");
            writer.WriteStartObject("source"u8);
            writer.WriteString("type"u8, "url");
            writer.WriteString("url"u8, Url);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
    }

    internal sealed record ToolResultBlock(string Id, string Content, bool IsError) : WireBlock
    {
        public override void Write(Utf8JsonWriter writer)
        {
            writer.WriteStartObject();
            writer.WriteString("type"u8, "tool_result");
            writer.WriteString("tool_use_id"u8, Id);
            writer.WriteString("content"u8, Content);
            if (IsError)
            {
                writer.WriteBoolean("is_error"u8, true);
            }

            writer.WriteEndObject();
        }
    }

    internal sealed record ToolUseBlock(string Id, string Name, IDictionary<string, object?>? Arguments) : WireBlock
    {
        public override void Write(Utf8JsonWriter writer)
        {
            writer.WriteStartObject();
            writer.WriteString("type"u8, "tool_use");
            writer.WriteString("id"u8, Id);
            writer.WriteString("name"u8, Name);
            writer.WritePropertyName("input"u8);
            if (Arguments is null)
            {
                writer.WriteStartObject();
                writer.WriteEndObject();
            }
            else
            {
                WriteValue(writer, Arguments);
            }

            writer.WriteEndObject();
        }
    }

    internal sealed record ThinkingBlock(string Thinking, string Signature) : WireBlock
    {
        public override void Write(Utf8JsonWriter writer)
        {
            writer.WriteStartObject();
            writer.WriteString("type"u8, "thinking");
            writer.WriteString("thinking"u8, Thinking);
            writer.WriteString("signature"u8, Signature);
            writer.WriteEndObject();
        }
    }

    internal sealed record RedactedThinkingBlock(string Data) : WireBlock
    {
        public override void Write(Utf8JsonWriter writer)
        {
            writer.WriteStartObject();
            writer.WriteString("type"u8, "redacted_thinking");
            writer.WriteString("data"u8, Data);
            writer.WriteEndObject();
        }
    }
}
