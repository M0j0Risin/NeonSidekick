using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Audits;
using NeonSidekick.Llm;

namespace NeonSidekick.App;

/// <summary>
/// What <c>/usage sysprompt</c> writes (2026-11-23, the user's ask): the next request as the app would send it, read as the Context tab reads
/// it. <paramref name="Messages"/> are the history after the picture budget (<see cref="PictureBudget.Apply"/>), as the request carries them;
/// the thinking of a message before <paramref name="ThinkingFrom"/> isn't sent and is left out. Null <paramref name="Messages"/> leaves the
/// conversation out (a bare <c>/usage sysprompt</c>, 2026-11-23, the user's ask). The token figures are the Context tab's estimates.
/// </summary>
public sealed record UsageExportFacts(
    DateTimeOffset At,
    string? Model,
    string Profile,
    long? Window,
    string SystemPrompt,
    long SystemTokens,
    IReadOnlyList<AIFunction> Tools,
    long ToolTokens,
    IReadOnlyList<ChatMessage>? Messages,
    int ThinkingFrom,
    long HistoryTokens,
    string? Folder = null);

/// <summary>
/// <c>/usage sysprompt</c>'s Markdown (2026-11-23, the user's ask): the system message, every tool offered (name, description, schema) and the
/// history, each text word for word in a fence longer than any run of backticks inside it (<see cref="AuditReport.FenceFor"/>), pictures as
/// placeholders. It is the app's request, each message's role and contents, not one API's JSON: each client and chat template format it again.
/// Pure, so tests need no screen.
/// </summary>
public static class UsageExport
{
    /// <summary>The file's heading with the conversation in (<c>/usage sysprompt conversation</c>). Pinned.</summary>
    public const string Heading = "# Neon Sidekick context";

    /// <summary>The file's heading without it (a bare <c>/usage sysprompt</c>, 2026-11-23, the user's ask). Pinned.</summary>
    public const string PromptHeading = "# Neon Sidekick system prompt";

    /// <summary>The line under the heading's facts. Pinned.</summary>
    public const string AsSentLine = "As the app sends it; the server's API and chat template format it for the model.";

    /// <summary>The line under the Tools heading. Pinned.</summary>
    public const string SchemaLine = "Each schema is indented here for reading; the request sends it minified.";

    /// <summary>The first section's heading. Pinned: <see cref="ContentsLine"/> links to its anchor.</summary>
    public const string SystemSection = "System message";

    /// <summary>The second section's heading. Pinned.</summary>
    public const string ToolsSection = "Tools";

    /// <summary>The third section's heading (Conversation, not History: 2026-11-23, the user's ask). Pinned.</summary>
    public const string ConversationSection = "Conversation";

    /// <summary>
    /// The contents line under the header (2026-11-23, the user's ask: the tools run to thousands of lines), a link to each section by the
    /// anchor GitHub and VS Code make from its bare heading. Pinned.
    /// </summary>
    public const string ContentsLine = PromptContentsLine + " · [" + ConversationSection + "](#conversation)";

    /// <summary>The contents line without the conversation (a bare <c>/usage sysprompt</c>). Pinned.</summary>
    public const string PromptContentsLine = "**Contents:** [" + SystemSection + "](#system-message) · [" + ToolsSection + "](#tools)";

    /// <summary>The Conversation section's line before the first message (2026-11-23, the user's ask). Pinned.</summary>
    public const string NoMessagesLine = "No messages yet: the next request carries only the message you send.";

    /// <summary>The line under the Conversation heading when earlier thinking isn't sent. Pinned.</summary>
    public const string ThinkingLeftOutLine = "Thinking from earlier turns isn't sent (LLM preserve thinking is off), so it is left out.";

    /// <summary>The model's fact while no model is known yet. Pinned.</summary>
    public const string NoModelYet = "not known yet";

    /// <summary>The Markdown for <paramref name="facts"/>, LF line endings.</summary>
    public static string Markdown(UsageExportFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var md = new StringBuilder();
        var messages = facts.Messages;
        md.Append(messages is null ? PromptHeading : Heading).Append("\n\n");
        Header(md, facts);
        md.Append('\n').Append('*').Append(AsSentLine).Append("*\n");
        md.Append('\n').Append(messages is null ? PromptContentsLine : ContentsLine).Append('\n');

        // The three sections' headings are bare, so their anchors (#system-message, #tools, #conversation) hold whatever the counts are:
        // the counts go on the line under each (2026-11-23, the user's ask for a contents line).
        md.Append("\n## ").Append(SystemSection).Append("\n~").Append(UsageText.CompactNumber(facts.SystemTokens)).Append(" tokens.\n");
        Block(md, facts.SystemPrompt, "text");

        md.Append("\n## ").Append(ToolsSection).Append('\n').Append(UsageText.Plural(facts.Tools.Count, "tool", "tools")).Append(", ~")
            .Append(UsageText.CompactNumber(facts.ToolTokens)).Append(" tokens.\n");
        if (facts.Tools.Count > 0)
        {
            md.Append(SchemaLine).Append('\n');
        }

        foreach (var tool in facts.Tools)
        {
            md.Append("\n### ").Append(tool.Name).Append('\n');
            Block(md, tool.Description ?? "", "text");
            Block(md, IndentedSchema(tool.JsonSchema), "json");
        }

        if (messages is null)
        {
            return md.ToString();
        }

        md.Append("\n## ").Append(ConversationSection).Append('\n').Append(UsageText.Plural(messages.Count, "message", "messages")).Append(", ~")
            .Append(UsageText.CompactNumber(facts.HistoryTokens)).Append(" tokens.\n");
        bool thinkingLeftOut = facts.ThinkingFrom > 0 && messages.Take(facts.ThinkingFrom).Any(m => m.Contents.OfType<TextReasoningContent>().Any());
        if (messages.Count == 0)
        {
            md.Append(NoMessagesLine).Append('\n');
        }

        if (thinkingLeftOut)
        {
            md.Append(ThinkingLeftOutLine).Append('\n');
        }

        var callNames = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            md.Append("\n### ").Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append(" · ").Append(message.Role.Value).Append('\n');
            foreach (var content in message.Contents)
            {
                switch (content)
                {
                    case TextReasoningContent thinking when i >= facts.ThinkingFrom && !string.IsNullOrEmpty(thinking.Text):
                        Block(md, thinking.Text, "thinking");
                        break;
                    case TextReasoningContent:
                        break;
                    case TextContent text:
                        Block(md, text.Text ?? "", "text");
                        break;
                    case FunctionCallContent call:
                        callNames[call.CallId] = call.Name;
                        md.Append("Calls `").Append(call.Name).Append("` (").Append(call.CallId).Append("):\n");
                        Block(md, IndentedJson(Assistant.SerializeArguments(call.Arguments)), "json");
                        break;
                    case FunctionResultContent result:
                        md.Append("Result of `").Append(callNames.GetValueOrDefault(result.CallId, "?")).Append("` (").Append(result.CallId).Append("):\n");
                        Block(md, ResultText(result.Result), "text");
                        break;
                    case DataContent data:
                        md.Append(Placeholder(data.MediaType, data.Data.Length)).Append('\n');
                        break;
                    default:
                        md.Append('[').Append(content.GetType().Name).Append("]\n");
                        break;
                }
            }
        }

        return md.ToString();
    }

    /// <summary>
    /// The facts under the heading (2026-11-23, the user's ask: one run-on paragraph read badly in a preview), one labeled list item each, so
    /// every viewer keeps them apart: when, the model, the profile, the working folder, and the context with its three parts. Without the
    /// conversation the last is the Size, the system message and the tools alone, with no conversation figure (2026-11-23, the user's ask).
    /// </summary>
    private static void Header(StringBuilder md, UsageExportFacts facts)
    {
        bool conversation = facts.Messages is not null;
        long total = facts.SystemTokens + facts.ToolTokens + (conversation ? facts.HistoryTokens : 0);
        md.Append("- **Exported:** ").Append(facts.At.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append('\n');
        md.Append("- **Model:** ").Append(facts.Model is { Length: > 0 } model ? Code(model) : NoModelYet).Append('\n');
        md.Append("- **Profile:** ").Append(Code(facts.Profile)).Append('\n');
        if (facts.Folder is { Length: > 0 } folder)
        {
            md.Append("- **Working folder:** ").Append(Code(folder)).Append('\n');
        }

        md.Append(conversation ? "- **Context:** ~" : "- **Size:** ~").Append(UsageText.CompactNumber(total));
        if (facts.Window is long window and > 0)
        {
            long percent = (long)Math.Round(total * 100.0 / window, MidpointRounding.AwayFromZero);
            md.Append(" of ").Append(UsageText.CompactNumber(window)).Append(" tokens (").Append(percent.ToString(CultureInfo.InvariantCulture)).Append("%)");
        }
        else
        {
            md.Append(" tokens");
        }

        md.Append(": system message ~").Append(UsageText.CompactNumber(facts.SystemTokens))
            .Append(", tools ~").Append(UsageText.CompactNumber(facts.ToolTokens));
        if (conversation)
        {
            md.Append(", conversation ~").Append(UsageText.CompactNumber(facts.HistoryTokens));
        }

        md.Append('\n');
    }

    // Inline code no backtick inside can close: a run of backticks one longer than the longest inside, padded when it starts or ends with one.
    private static string Code(string text)
    {
        int longest = 0;
        int run = 0;
        foreach (char c in text)
        {
            run = c == '`' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }

        string ticks = new('`', longest + 1);
        string pad = longest > 0 ? " " : "";
        return ticks + pad + text + pad + ticks;
    }

    /// <summary>A picture or a document in the history: <c>[picture: image/png, 412 KB]</c>, never its bytes. Pinned.</summary>
    public static string Placeholder(string mediaType, long bytes)
    {
        string kind = mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ? "picture" : "file";
        long kb = Math.Max(1, (bytes + 512) / 1024);
        return $"[{kind}: {mediaType}, {kb.ToString(CultureInfo.InvariantCulture)} KB]";
    }

    // A fenced block whose fence no run of backticks inside can close.
    private static void Block(StringBuilder md, string text, string info)
    {
        string fence = AuditReport.FenceFor(text);
        md.Append(fence).Append(info).Append('\n').Append(text.Replace("\r\n", "\n", StringComparison.Ordinal));
        if (!text.EndsWith('\n'))
        {
            md.Append('\n');
        }

        md.Append(fence).Append('\n');
    }

    // A tool result as the request carries it: a string as is, JSON as its text, anything else as it prints.
    private static string ResultText(object? result) => result switch
    {
        null => "",
        string text => text,
        JsonElement element => element.ValueKind == JsonValueKind.String ? element.GetString() ?? "" : element.GetRawText(),
        _ => result.ToString() ?? "",
    };

    // A schema indented for reading; an empty one as the request sends it (ChatScreen.EmptySchema).
    private static string IndentedSchema(JsonElement schema) =>
        schema.ValueKind == JsonValueKind.Object ? Indented(schema) : ChatScreen.EmptySchema;

    private static string IndentedJson(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return Indented(document.RootElement);
        }
        catch (JsonException)
        {
            return json;
        }
    }

    private static string Indented(JsonElement element)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            element.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan).Replace("\r\n", "\n", StringComparison.Ordinal);
    }
}
