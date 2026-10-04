using System.Buffers;
using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Llm.Anthropic;

namespace NeonSidekick.Llm.OpenAIPlatform;

/// <summary>
/// One Responses API request body (<c>POST /v1/responses</c>) from the conversation as <see cref="Assistant"/> holds it
/// (2026-10-03, the user's call after the live sweep: Chat Completions refuses function tools beside any reasoning on
/// GPT-5.4 and newer, and every tool at all on GPT-6 Sol, 6.1 Sol and Astra, which have no <c>none</c>; the Responses API
/// takes both). Written with a <see cref="Utf8JsonWriter"/>, no serializer, as <see cref="AnthropicRequest"/> is — the SDK's
/// own Responses client and the MEAI adapter over it are evaluation-only, and this project suppresses nothing. Stateless
/// (<c>store: false</c>): every request carries the whole conversation, the shape every other server gets.
/// <list type="bullet">
/// <item>The system messages become <c>instructions</c>, joined in order (<see cref="AnthropicRequest.SystemText"/>).</item>
/// <item>A user message is a <c>message</c> item of <c>input_text</c>, <c>input_image</c> (a data URI or a URL) and
/// <c>input_file</c> (a PDF) parts; a tool message's results are <c>function_call_output</c> items.</item>
/// <item>An assistant message is its items in order: <c>reasoning</c> (only in the turn in flight, its encrypted content as
/// the stream gave it — <see cref="ConversationHistory.InFlightStart"/>, as the Anthropic API's signed thinking), the text as a
/// <c>message</c> item, each call as a <c>function_call</c>. The text's <c>phase</c> is worked out rather than kept: text in a
/// reply that also calls a tool is the preamble (<c>commentary</c>), text in one that does not is the <c>final_answer</c> — what
/// OpenAI asks a caller that replays the history itself to send back, so GPT-5.4 on do not stop early.</item>
/// <item>Every call is answered and every answer has its call: a call left without a result (a turn cut short) gets
/// <see cref="AnthropicRequest.MissingResult"/>, and a result whose call is not in the conversation goes as user text.</item>
/// <item>Tool names the API refuses are sanitised as the Anthropic API's are (<see cref="AnthropicRequest.ToolName"/>).</item>
/// <item>The reasoning level goes out as <c>reasoning.effort</c>, the model's word (<see cref="OpenAIModelRules"/>), with
/// <c>summary: auto</c> so the transcript's fold has something to show; no sampling field at all (the reasoning models
/// refuse a temperature).</item>
/// </list>
/// </summary>
public static class OpenAIRequest
{
    /// <summary>
    /// The <see cref="TextReasoningContent.ProtectedData"/> prefix of a reasoning item's encrypted content, so neither wire
    /// sends the other's: the Anthropic API's signature carries none (<see cref="AnthropicRequest"/> skips this one).
    /// </summary>
    public const string EncryptedPrefix = "openai-reasoning:";

    /// <summary>The <c>phase</c> of the text in a reply that also calls tools.</summary>
    public const string CommentaryPhase = "commentary";

    /// <summary>The <c>phase</c> of the text in a reply that calls none.</summary>
    public const string FinalAnswerPhase = "final_answer";

    /// <summary>
    /// The request body, UTF-8 JSON. <paramref name="maxTokens"/> 0 sends no <c>max_output_tokens</c>.
    /// <paramref name="withoutReasoning"/> sends no reasoning item at all — the one-time recovery when the API refused the
    /// encrypted content sent back (<see cref="OpenAIApiChatClient"/>).
    /// </summary>
    public static byte[] Write(IReadOnlyList<ChatMessage> messages, ChatOptions? options, string modelId, int maxTokens, bool withoutReasoning = false)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(modelId);
        var rules = OpenAIModelRules.For(modelId);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("model"u8, modelId);
            writer.WriteBoolean("stream"u8, true);
            writer.WriteBoolean("store"u8, false);
            if (maxTokens > 0)
            {
                writer.WriteNumber("max_output_tokens"u8, maxTokens);
            }

            string system = AnthropicRequest.SystemText(messages);
            if (system.Length > 0)
            {
                writer.WriteString("instructions"u8, system);
            }

            if (rules.Reasons)
            {
                writer.WriteStartArray("include"u8);
                writer.WriteStringValue("reasoning.encrypted_content");
                writer.WriteEndArray();
                writer.WriteStartObject("reasoning"u8);
                if (rules.EffortWord(options?.Reasoning?.Effort) is { } effort)
                {
                    writer.WriteString("effort"u8, effort);
                }

                writer.WriteString("summary"u8, "auto");
                writer.WriteEndObject();
            }

            WriteTools(writer, options?.Tools);
            WriteFormat(writer, options?.ResponseFormat);

            writer.WriteStartArray("input"u8);
            foreach (var item in Shape(messages, keepReasoning: rules.Reasons && !withoutReasoning))
            {
                item.Write(writer);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
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
            writer.WriteString("type"u8, "function");
            writer.WriteString("name"u8, AnthropicRequest.ToolName(function.Name));
            if (!string.IsNullOrWhiteSpace(function.Description))
            {
                writer.WriteString("description"u8, function.Description);
            }

            writer.WritePropertyName("parameters"u8);
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

            // Not strict: strict mode wants every property required and no additional ones, which the app's schemas are not.
            writer.WriteBoolean("strict"u8, false);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    /// <summary>A JSON response format (<c>/test</c>'s structured tests) as <c>text.format</c>: the schema as it came, not strict, or plain JSON.</summary>
    private static void WriteFormat(Utf8JsonWriter writer, ChatResponseFormat? format)
    {
        if (format is not ChatResponseFormatJson json)
        {
            return;
        }

        writer.WriteStartObject("text"u8);
        writer.WriteStartObject("format"u8);
        if (json.Schema is { } schema)
        {
            writer.WriteString("type"u8, "json_schema");
            writer.WriteString("name"u8, string.IsNullOrWhiteSpace(json.SchemaName) ? "response" : AnthropicRequest.ToolName(json.SchemaName));
            if (!string.IsNullOrWhiteSpace(json.SchemaDescription))
            {
                writer.WriteString("description"u8, json.SchemaDescription);
            }

            writer.WritePropertyName("schema"u8);
            schema.WriteTo(writer);
            writer.WriteBoolean("strict"u8, false);
        }
        else
        {
            writer.WriteString("type"u8, "json_object");
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    /// <summary>The conversation as the API's input items (the class summary's rules).</summary>
    internal static List<InputItem> Shape(IReadOnlyList<ChatMessage> messages, bool keepReasoning = true)
    {
        int inFlight = keepReasoning ? ConversationHistory.InFlightStart(messages) : int.MaxValue;
        var items = new List<InputItem>(messages.Count * 2);
        var answered = new HashSet<string>(StringComparer.Ordinal);
        var called = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            foreach (var result in message.Contents.OfType<FunctionResultContent>())
            {
                answered.Add(result.CallId);
            }

            foreach (var call in message.Contents.OfType<FunctionCallContent>())
            {
                called.Add(call.CallId);
            }
        }

        var open = new List<string>();
        for (int i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            if (message.Role == ChatRole.System)
            {
                continue;
            }

            if (message.Role == ChatRole.Assistant)
            {
                AddAssistant(items, message, keepReasoning: i > inFlight, answered, open);
                continue;
            }

            // A user message after unanswered calls: their missing results first, so each call is answered before the next input.
            CloseOpen(items, open);
            var parts = new List<Part>();
            foreach (var content in message.Contents)
            {
                switch (content)
                {
                    case FunctionResultContent result when called.Contains(result.CallId):
                        items.Add(new OutputItem(result.CallId, AnthropicRequest.ResultText(result)));
                        break;
                    case FunctionResultContent orphan:
                        parts.Add(new TextPart("Tool result: " + AnthropicRequest.ResultText(orphan)));
                        break;
                    case TextContent text when !string.IsNullOrWhiteSpace(text.Text):
                        parts.Add(new TextPart(text.Text));
                        break;
                    case DataContent data when IsImage(data.MediaType):
                        parts.Add(new ImagePart("data:" + data.MediaType + ";base64," + Convert.ToBase64String(data.Data.Span)));
                        break;
                    case DataContent pdf when pdf.MediaType == "application/pdf":
                        parts.Add(new FilePart(pdf.Name ?? "document.pdf", "data:application/pdf;base64," + Convert.ToBase64String(pdf.Data.Span)));
                        break;
                    case UriContent uri when IsImage(uri.MediaType):
                        parts.Add(new ImagePart(uri.Uri.AbsoluteUri));
                        break;
                }
            }

            if (parts.Count > 0)
            {
                items.Add(new UserItem(parts));
            }
        }

        CloseOpen(items, open);
        return items;
    }

    private static void AddAssistant(List<InputItem> items, ChatMessage message, bool keepReasoning, HashSet<string> answered, List<string> open)
    {
        bool calls = message.Contents.Any(c => c is FunctionCallContent);
        string phase = calls ? CommentaryPhase : FinalAnswerPhase;
        var text = new System.Text.StringBuilder();
        void FlushText()
        {
            if (text.ToString().Trim() is { Length: > 0 } said)
            {
                items.Add(new AssistantItem(said, phase));
            }

            text.Clear();
        }

        foreach (var content in message.Contents)
        {
            switch (content)
            {
                case TextReasoningContent { ProtectedData: { } data } when data.StartsWith(EncryptedPrefix, StringComparison.Ordinal):
                    if (keepReasoning)
                    {
                        FlushText();
                        items.Add(new ReasoningItem(data[EncryptedPrefix.Length..]));
                    }

                    break;
                case TextContent { Text: { } said }:
                    text.Append(said);
                    break;
                case FunctionCallContent call:
                    FlushText();
                    items.Add(new CallItem(call.CallId, AnthropicRequest.ToolName(call.Name), call.Arguments));
                    if (!answered.Contains(call.CallId))
                    {
                        open.Add(call.CallId);
                    }

                    break;
            }
        }

        FlushText();
    }

    private static void CloseOpen(List<InputItem> items, List<string> open)
    {
        foreach (string id in open)
        {
            items.Add(new OutputItem(id, AnthropicRequest.MissingResult));
        }

        open.Clear();
    }

    private static bool IsImage(string? mediaType) =>
        mediaType is "image/png" or "image/jpeg" or "image/gif" or "image/webp";

    /// <summary>One input item.</summary>
    internal abstract record InputItem
    {
        public abstract void Write(Utf8JsonWriter writer);
    }

    internal sealed record UserItem(List<Part> Parts) : InputItem
    {
        public override void Write(Utf8JsonWriter writer)
        {
            writer.WriteStartObject();
            writer.WriteString("type"u8, "message");
            writer.WriteString("role"u8, "user");
            writer.WriteStartArray("content"u8);
            foreach (var part in Parts)
            {
                part.Write(writer);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }
    }

    internal sealed record AssistantItem(string Text, string Phase) : InputItem
    {
        public override void Write(Utf8JsonWriter writer)
        {
            writer.WriteStartObject();
            writer.WriteString("type"u8, "message");
            writer.WriteString("role"u8, "assistant");
            writer.WriteString("phase"u8, Phase);
            writer.WriteStartArray("content"u8);
            writer.WriteStartObject();
            writer.WriteString("type"u8, "output_text");
            writer.WriteString("text"u8, Text);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
    }

    internal sealed record ReasoningItem(string EncryptedContent) : InputItem
    {
        public override void Write(Utf8JsonWriter writer)
        {
            writer.WriteStartObject();
            writer.WriteString("type"u8, "reasoning");
            writer.WriteStartArray("summary"u8);
            writer.WriteEndArray();
            writer.WriteString("encrypted_content"u8, EncryptedContent);
            writer.WriteEndObject();
        }
    }

    internal sealed record CallItem(string CallId, string Name, IDictionary<string, object?>? Arguments) : InputItem
    {
        public override void Write(Utf8JsonWriter writer)
        {
            writer.WriteStartObject();
            writer.WriteString("type"u8, "function_call");
            writer.WriteString("call_id"u8, CallId);
            writer.WriteString("name"u8, Name);
            writer.WriteString("arguments"u8, ArgumentsJson(Arguments));
            writer.WriteEndObject();
        }
    }

    internal sealed record OutputItem(string CallId, string Output) : InputItem
    {
        public override void Write(Utf8JsonWriter writer)
        {
            writer.WriteStartObject();
            writer.WriteString("type"u8, "function_call_output");
            writer.WriteString("call_id"u8, CallId);
            writer.WriteString("output"u8, Output);
            writer.WriteEndObject();
        }
    }

    /// <summary>The call's arguments as the JSON string the API takes.</summary>
    internal static string ArgumentsJson(IDictionary<string, object?>? arguments)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            if (arguments is null)
            {
                writer.WriteStartObject();
                writer.WriteEndObject();
            }
            else
            {
                AnthropicRequest.WriteValue(writer, arguments);
            }
        }

        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>One part of a user message.</summary>
    internal abstract record Part
    {
        public abstract void Write(Utf8JsonWriter writer);
    }

    internal sealed record TextPart(string Text) : Part
    {
        public override void Write(Utf8JsonWriter writer)
        {
            writer.WriteStartObject();
            writer.WriteString("type"u8, "input_text");
            writer.WriteString("text"u8, Text);
            writer.WriteEndObject();
        }
    }

    internal sealed record ImagePart(string Url) : Part
    {
        public override void Write(Utf8JsonWriter writer)
        {
            writer.WriteStartObject();
            writer.WriteString("type"u8, "input_image");
            writer.WriteString("image_url"u8, Url);
            writer.WriteEndObject();
        }
    }

    internal sealed record FilePart(string FileName, string DataUrl) : Part
    {
        public override void Write(Utf8JsonWriter writer)
        {
            writer.WriteStartObject();
            writer.WriteString("type"u8, "input_file");
            writer.WriteString("filename"u8, FileName);
            writer.WriteString("file_data"u8, DataUrl);
            writer.WriteEndObject();
        }
    }
}
