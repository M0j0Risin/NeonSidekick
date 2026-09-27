using System.Text.Json;
using NeonSidekick.Diagnostics;
using NeonSidekick.Llm;

namespace NeonSidekick.Claude;

/// <summary>
/// Reads the Claude Code CLI's <c>--output-format stream-json</c> one line at a time (2026-09-27; the shapes captured
/// from CLI 2.1.283 are the tests' fixtures). Only the top-level conversation is read — a line with a
/// <c>parent_tool_use_id</c> is a subagent's, and its text is not the reply. What it takes:
/// <list type="bullet">
/// <item><c>stream_event</c> / <c>content_block_delta</c> with a <c>text_delta</c>: a <see cref="ClaudeEvent.TextDelta"/>.
/// A text block that opens after text was already written starts with a blank line — the words before a tool call and
/// the words after it are two paragraphs, not one run-on line.</item>
/// <item><c>assistant</c> messages: each <c>tool_use</c> part a <see cref="ClaudeEvent.ToolActivity"/> (the whole input is
/// known there; the stream only has it in pieces).</item>
/// <item><c>result</c>: the <see cref="ClaudeEvent.Result"/> — session, <c>total_cost_usd</c>, tokens (the input counted with
/// both cache figures: what the request carried), <c>is_error</c> with its <c>errors</c>, and the denied tools.</item>
/// </list>
/// Everything else (<c>system</c>, <c>rate_limit_event</c>, <c>user</c> tool results, the other stream events) is skipped,
/// and so is a type this app has never seen — a newer CLI adds kinds, and none of them may fail a reply. A line that is
/// not JSON is logged and skipped. <see cref="JsonDocument"/>, not a serializer context: nothing is bound to a type, and
/// that path has no reflection to trip the AOT budget.
/// </summary>
public sealed class ClaudeStreamParser
{
    private bool _wroteText;
    private bool _pendingBreak;

    /// <summary>Whether a <see cref="ClaudeEvent.Result"/> was read.</summary>
    public bool SawResult { get; private set; }

    /// <summary>The events <paramref name="line"/> carries: none, one, or (an assistant message with several tool calls) more.</summary>
    public IReadOnlyList<ClaudeEvent> Read(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (string.IsNullOrWhiteSpace(line))
        {
            return [];
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException ex)
        {
            DiagnosticLog.Warn(ClaudeText.Category, ClaudeText.UnreadableLineLog(ex.Message));
            return [];
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Text(root, "type") is not { } type)
            {
                return [];
            }

            if (type == "result")
            {
                SawResult = true;
                return [ReadResult(root)];
            }

            // A subagent's lines carry the Task call's id; only the main thread's are the reply.
            if (root.TryGetProperty("parent_tool_use_id", out var parent) && parent.ValueKind == JsonValueKind.String)
            {
                return [];
            }

            return type switch
            {
                "stream_event" when root.TryGetProperty("event", out var evt) => ReadStreamEvent(evt),
                "assistant" when root.TryGetProperty("message", out var message) => ReadToolUses(message),
                _ => [],
            };
        }
    }

    private IReadOnlyList<ClaudeEvent> ReadStreamEvent(JsonElement evt)
    {
        switch (Text(evt, "type"))
        {
            case "content_block_start" when evt.TryGetProperty("content_block", out var block) && Text(block, "type") == "text":
                _pendingBreak = _wroteText;
                return [];
            case "content_block_delta" when evt.TryGetProperty("delta", out var delta) && Text(delta, "type") == "text_delta":
                string text = Text(delta, "text") ?? "";
                if (text.Length == 0)
                {
                    return [];
                }

                if (_pendingBreak)
                {
                    text = "\n\n" + text.TrimStart('\n');
                    _pendingBreak = false;
                }

                _wroteText = true;
                return [new ClaudeEvent.TextDelta(text)];
            default:
                return [];
        }
    }

    private static IReadOnlyList<ClaudeEvent> ReadToolUses(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var events = new List<ClaudeEvent>();
        foreach (var part in content.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.Object && Text(part, "type") == "tool_use" && Text(part, "name") is { } name)
            {
                events.Add(new ClaudeEvent.ToolActivity(name, part.TryGetProperty("input", out var input) ? Detail(input) : ""));
            }
        }

        return events;
    }

    /// <summary>The one input field that says what a tool call is about, the first found of the usual names; empty for none.</summary>
    internal static string Detail(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object)
        {
            return "";
        }

        foreach (string key in DetailKeys)
        {
            if (Text(input, key) is { Length: > 0 } value)
            {
                return value;
            }
        }

        return "";
    }

    /// <summary>The input fields <see cref="Detail"/> looks for, in order. Pinned.</summary>
    internal static readonly string[] DetailKeys = ["file_path", "path", "pattern", "command", "url", "query", "description", "prompt"];

    private static ClaudeEvent.Result ReadResult(JsonElement root)
    {
        bool isError = root.TryGetProperty("is_error", out var flag) && flag.ValueKind == JsonValueKind.True;
        decimal cost = root.TryGetProperty("total_cost_usd", out var usd) && usd.ValueKind == JsonValueKind.Number && usd.TryGetDecimal(out var value) ? value : 0m;
        string text = Text(root, "result") ?? "";
        string? error = null;
        if (isError)
        {
            var errors = new List<string>();
            if (root.TryGetProperty("errors", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in list.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } one)
                    {
                        errors.Add(one);
                    }
                }
            }

            error = errors.Count > 0 ? string.Join("; ", errors)
                : text.Length > 0 ? text
                : Text(root, "subtype") ?? ClaudeText.UnknownFailure;
        }

        var denied = new List<string>();
        if (root.TryGetProperty("permission_denials", out var denials) && denials.ValueKind == JsonValueKind.Array)
        {
            foreach (var denial in denials.EnumerateArray())
            {
                if (denial.ValueKind == JsonValueKind.Object && Text(denial, "tool_name") is { Length: > 0 } tool)
                {
                    denied.Add(tool);
                }
            }
        }

        return new ClaudeEvent.Result(Text(root, "session_id"), cost, ReadUsage(root), isError, error, text, denied);
    }

    /// <summary>The run's tokens as one <see cref="TokenUsage"/>: the input with both cache figures, the output, the wait to the first token and the rest of the run.</summary>
    private static TokenUsage ReadUsage(JsonElement root)
    {
        long input = 0, output = 0;
        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            input = Number(usage, "input_tokens") + Number(usage, "cache_creation_input_tokens") + Number(usage, "cache_read_input_tokens");
            output = Number(usage, "output_tokens");
        }

        var toFirst = TimeSpan.FromMilliseconds(Number(root, "ttft_ms"));
        var whole = TimeSpan.FromMilliseconds(Number(root, "duration_ms"));
        var generating = whole > toFirst ? whole - toFirst : TimeSpan.Zero;
        return new TokenUsage(input, output, input + output, 1, toFirst, generating);
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number) ? number : 0;
}
