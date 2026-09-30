using System.Buffers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Llm;

namespace NeonSidekick.Claude;

/// <summary>
/// A request as the Claude CLI server reads it (<see cref="ClaudeServerInput.Split"/>, 2026-09-30): the CLI keeps the
/// conversation itself, so of the whole history the app sends each round only what came after the model's last message
/// matters.
/// </summary>
/// <param name="SystemPrompt">The request's system messages, joined: the launch's prompt.</param>
/// <param name="Earlier">What came before, up to and with the model's last message: the preamble of a session that has to start over.</param>
/// <param name="NewContent">A new user message's blocks: the text (with the turn's seeded tool answers ahead of it, as context), then the pictures.</param>
/// <param name="Results">The app's answers to the model's tool calls, when the new part is those.</param>
/// <param name="ResultImages">The pictures those calls fetched (the history's image carrier after them).</param>
public sealed record ClaudeTurnInput(
    string SystemPrompt,
    IReadOnlyList<ChatMessage> Earlier,
    IReadOnlyList<AIContent> NewContent,
    IReadOnlyList<FunctionResultContent> Results,
    IReadOnlyList<DataContent> ResultImages)
{
    /// <summary>Whether the model spoke before: the session exists already (or should).</summary>
    public bool HasEarlierTurns => Earlier.Any(m => m.Role == ChatRole.Assistant);
}

/// <summary>
/// What the Claude CLI server's client writes to the CLI's stdin, and how a request is read for it (2026-09-30). Pure, and
/// tested as data; the shapes are the spike's (<c>server-*.stdin.jsonl</c>).
/// </summary>
public static class ClaudeServerInput
{
    /// <summary>The most characters of an earlier conversation a new session opens with (<see cref="Preamble"/>): its end is kept.</summary>
    public const int PreambleChars = 60_000;

    /// <summary>
    /// Reads <paramref name="messages"/> for the CLI: the system prompt, and the part after the model's last message — a
    /// message of its own, not one of the turn's seeded calls (<see cref="Assistant.IsOpeningCallId"/>), which are the app's
    /// and go to the model as context of the user's message.
    /// </summary>
    public static ClaudeTurnInput Split(IReadOnlyList<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        string system = string.Join("\n\n", messages.Where(m => m.Role == ChatRole.System).Select(m => m.Text).Where(t => !string.IsNullOrWhiteSpace(t)));
        var conversation = messages.Where(m => m.Role != ChatRole.System).ToList();
        int last = conversation.FindLastIndex(IsModelMessage);
        var earlier = conversation[..(last + 1)];
        var tail = conversation[(last + 1)..];

        var names = tail.SelectMany(m => m.Contents).OfType<FunctionCallContent>().ToDictionary(c => c.CallId, c => c.Name, StringComparer.Ordinal);
        var context = new List<string>();
        var text = new List<string>();
        var images = new List<AIContent>();
        var results = new List<FunctionResultContent>();
        var resultImages = new List<DataContent>();
        foreach (var message in tail)
        {
            bool carrier = ConversationHistory.IsImageCarrier(message);
            foreach (var content in message.Contents)
            {
                switch (content)
                {
                    case FunctionResultContent result when Assistant.IsOpeningCallId(result.CallId):
                        context.Add(ClaudeCliText.SeededLine(names.GetValueOrDefault(result.CallId, result.CallId), ResultText(result)));
                        break;
                    case FunctionResultContent result:
                        results.Add(result);
                        break;
                    case DataContent data when data.HasTopLevelMediaType("image"):
                        if (carrier)
                        {
                            resultImages.Add(data);
                        }

                        images.Add(data);
                        break;
                    case TextContent { Text.Length: > 0 } words when message.Role == ChatRole.User:
                        text.Add(words.Text);
                        break;
                }
            }
        }

        var blocks = new List<AIContent>();
        if (context.Count > 0)
        {
            blocks.Add(new TextContent(ClaudeCliText.SeededContext(context)));
        }

        if (text.Count > 0)
        {
            blocks.Add(new TextContent(string.Join("\n\n", text)));
        }

        blocks.AddRange(images);
        return new ClaudeTurnInput(system, earlier, blocks, results, resultImages);
    }

    /// <summary>Whether <paramref name="message"/> is the model's own: an assistant message that is not only the turn's seeded calls.</summary>
    private static bool IsModelMessage(ChatMessage message) =>
        message.Role == ChatRole.Assistant
        && (message.Contents.Count == 0 || message.Contents.Any(c => c is not FunctionCallContent call || !Assistant.IsOpeningCallId(call.CallId)));

    /// <summary>A tool result's text as the model reads it.</summary>
    public static string ResultText(FunctionResultContent result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Result switch
        {
            null => "",
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString() ?? "",
            var other => other.ToString() ?? "",
        };
    }

    /// <summary>Whether a tool result reads as a failure: the app's tools answer <c>Error: …</c> (the bridge reads them the same way).</summary>
    public static bool IsError(string text) => text.StartsWith("Error:", StringComparison.Ordinal);

    /// <summary>
    /// The conversation before a session that has to start over (the CLI lost it, or the chat came from another server):
    /// the words of each side, oldest first, the tools' traffic left out, its end kept when it is long. Empty for none.
    /// </summary>
    public static string Preamble(IReadOnlyList<ChatMessage> earlier)
    {
        ArgumentNullException.ThrowIfNull(earlier);
        var lines = new List<string>();
        foreach (var message in earlier)
        {
            if (message.Role == ChatRole.User && !ConversationHistory.IsImageCarrier(message) && !string.IsNullOrWhiteSpace(message.Text))
            {
                lines.Add(ClaudeCliText.PreambleUser(message.Text));
            }
            else if (message.Role == ChatRole.Assistant && !string.IsNullOrWhiteSpace(message.Text))
            {
                lines.Add(ClaudeCliText.PreambleAssistant(message.Text));
            }
        }

        if (lines.Count == 0)
        {
            return "";
        }

        string body = string.Join("\n\n", lines);
        if (body.Length > PreambleChars)
        {
            body = "…" + body[^PreambleChars..];
        }

        return ClaudeCliText.PreambleHeader + "\n\n" + body;
    }

    /// <summary>
    /// One user message as the CLI reads it on stdin: <c>{"type":"user","message":{"role":"user","content":[…]},
    /// "parent_tool_use_id":null,"session_id":…}</c>, each text a <c>text</c> block and each picture an <c>image</c> block
    /// with its base64 data. Pinned by the spike's stdin fixture.
    /// </summary>
    public static string UserLine(string sessionId, IReadOnlyList<AIContent> content)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(content);
        return Write(writer =>
        {
            writer.WriteString("type", "user");
            writer.WriteStartObject("message");
            writer.WriteString("role", "user");
            writer.WriteStartArray("content");
            foreach (var block in content)
            {
                switch (block)
                {
                    case TextContent text:
                        writer.WriteStartObject();
                        writer.WriteString("type", "text");
                        writer.WriteString("text", text.Text);
                        writer.WriteEndObject();
                        break;
                    case DataContent data when data.HasTopLevelMediaType("image"):
                        writer.WriteStartObject();
                        writer.WriteString("type", "image");
                        writer.WriteStartObject("source");
                        writer.WriteString("type", "base64");
                        writer.WriteString("media_type", data.MediaType);
                        writer.WriteBase64String("data", data.Data.Span);
                        writer.WriteEndObject();
                        writer.WriteEndObject();
                        break;
                }
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteNull("parent_tool_use_id");
            writer.WriteString("session_id", sessionId);
        });
    }

    /// <summary>The interrupt the CLI takes mid-turn: <c>{"type":"control_request","request_id":…,"request":{"subtype":"interrupt"}}</c>. Pinned.</summary>
    public static string InterruptLine(string requestId)
    {
        ArgumentNullException.ThrowIfNull(requestId);
        return Write(writer =>
        {
            writer.WriteString("type", "control_request");
            writer.WriteString("request_id", requestId);
            writer.WriteStartObject("request");
            writer.WriteString("subtype", "interrupt");
            writer.WriteEndObject();
        });
    }

    private static string Write(Action<Utf8JsonWriter> body)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            body(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
