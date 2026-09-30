using System.Text.Json;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.Claude;

/// <summary>What one line of the Claude CLI server's <c>stream-json</c> says (<see cref="ClaudeServerStream"/>, 2026-09-30).</summary>
public abstract record ClaudeServerEvent
{
    private ClaudeServerEvent()
    {
    }

    /// <summary>
    /// A turn's <c>system</c> / <c>init</c> line (one per user message, after it): the session the CLI runs, the tools it
    /// offers the model (only <c>mcp__neon__…</c> when the arguments hold), and whether it takes an interrupt.
    /// </summary>
    public sealed record Init(string? SessionId, IReadOnlyList<string> Tools, bool Interrupts) : ClaudeServerEvent;

    /// <summary>One model request began (<c>message_start</c>): what it carried in, both cache figures counted.</summary>
    public sealed record MessageStart(long InputTokens) : ClaudeServerEvent;

    /// <summary>A piece of the reply's text; a separator between two text blocks is already in it.</summary>
    public sealed record TextDelta(string Text) : ClaudeServerEvent;

    /// <summary>A piece of the model's thinking.</summary>
    public sealed record ThinkingDelta(string Text) : ClaudeServerEvent;

    /// <summary>The model called a tool: the call's id (the MCP call carries it too), the tool's name as the CLI knows it, and its input as JSON.</summary>
    public sealed record ToolUse(string Id, string Name, string InputJson) : ClaudeServerEvent;

    /// <summary>A model request's output tokens (<c>message_delta</c>).</summary>
    public sealed record MessageOutput(long OutputTokens) : ClaudeServerEvent;

    /// <summary>A model request ended (<c>message_stop</c>): with tool calls since its start, the app's loop runs them now.</summary>
    public sealed record MessageStop : ClaudeServerEvent;

    /// <summary>The CLI answered a <c>control_request</c> (an interrupt): its id, and whether it succeeded.</summary>
    public sealed record ControlResponse(string? RequestId, bool Success) : ClaudeServerEvent;

    /// <summary>
    /// The turn's end, the <c>result</c> line: the session, whether it failed and why, and how it ended
    /// (<c>terminal_reason</c>: <c>completed</c>, <c>aborted_tools</c>, <c>aborted_streaming</c>…). <paramref name="TotalCostUsd"/>
    /// is the process's whole spend so far, not the turn's; <paramref name="ContextWindow"/> the model's window, 0 when unsaid.
    /// </summary>
    public sealed record Result(string? SessionId, bool IsError, string? Error, string? TerminalReason, decimal TotalCostUsd, int ContextWindow) : ClaudeServerEvent;
}

/// <summary>
/// Reads the Claude CLI server's <c>stream-json</c> one line at a time (2026-09-30; the shapes captured from CLI 2.1.285 by
/// the spike are the fixtures <c>server-*.jsonl</c>). Only the main conversation is read: a line with a
/// <c>parent_tool_use_id</c> (a tool's <c>tool_progress</c> heartbeat, a subagent's) is skipped. Unknown kinds are
/// skipped, never a failure; a line that is not JSON is logged and skipped. <see cref="JsonDocument"/>, as
/// <see cref="ClaudeStreamParser"/>: nothing bound to a type, no reflection.
/// </summary>
public sealed class ClaudeServerStream
{
    private bool _wroteText;
    private bool _pendingBreak;

    /// <summary>The events <paramref name="line"/> carries: none, one, or (an assistant message with several tool calls) more.</summary>
    public IReadOnlyList<ClaudeServerEvent> Read(string line)
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

            switch (type)
            {
                case "result":
                    _wroteText = false;
                    _pendingBreak = false;
                    return [ReadResult(root)];
                case "control_response":
                    return [ReadControl(root)];
            }

            if (root.TryGetProperty("parent_tool_use_id", out var parent) && parent.ValueKind == JsonValueKind.String)
            {
                return [];
            }

            return type switch
            {
                "system" when Text(root, "subtype") == "init" => [ReadInit(root)],
                "stream_event" when root.TryGetProperty("event", out var evt) => ReadStreamEvent(evt),
                "assistant" when root.TryGetProperty("message", out var message) => ReadToolUses(message),
                _ => [],
            };
        }
    }

    private static ClaudeServerEvent.Init ReadInit(JsonElement root)
    {
        var tools = new List<string>();
        if (root.TryGetProperty("tools", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var tool in list.EnumerateArray())
            {
                if (tool.ValueKind == JsonValueKind.String && tool.GetString() is { Length: > 0 } name)
                {
                    tools.Add(name);
                }
            }
        }

        bool interrupts = false;
        if (root.TryGetProperty("capabilities", out var capabilities) && capabilities.ValueKind == JsonValueKind.Array)
        {
            foreach (var capability in capabilities.EnumerateArray())
            {
                interrupts |= capability.ValueKind == JsonValueKind.String && capability.GetString() is { } word && word.StartsWith("interrupt", StringComparison.Ordinal);
            }
        }

        return new ClaudeServerEvent.Init(Text(root, "session_id"), tools, interrupts);
    }

    private IReadOnlyList<ClaudeServerEvent> ReadStreamEvent(JsonElement evt)
    {
        switch (Text(evt, "type"))
        {
            case "message_start":
                long input = evt.TryGetProperty("message", out var message) && message.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object
                    ? Number(usage, "input_tokens") + Number(usage, "cache_creation_input_tokens") + Number(usage, "cache_read_input_tokens")
                    : 0;
                return [new ClaudeServerEvent.MessageStart(input)];
            case "content_block_start" when evt.TryGetProperty("content_block", out var block) && Text(block, "type") == "text":
                _pendingBreak = _wroteText;
                return [];
            case "content_block_delta" when evt.TryGetProperty("delta", out var delta):
                switch (Text(delta, "type"))
                {
                    case "text_delta":
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
                        return [new ClaudeServerEvent.TextDelta(text)];
                    case "thinking_delta" when Text(delta, "thinking") is { Length: > 0 } thinking:
                        return [new ClaudeServerEvent.ThinkingDelta(thinking)];
                    default:
                        return [];
                }

            case "message_delta" when evt.TryGetProperty("usage", out var output) && output.ValueKind == JsonValueKind.Object:
                return [new ClaudeServerEvent.MessageOutput(Number(output, "output_tokens"))];
            case "message_stop":
                return [new ClaudeServerEvent.MessageStop()];
            default:
                return [];
        }
    }

    private static IReadOnlyList<ClaudeServerEvent> ReadToolUses(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var events = new List<ClaudeServerEvent>();
        foreach (var part in content.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.Object && Text(part, "type") == "tool_use" && Text(part, "id") is { } id && Text(part, "name") is { } name)
            {
                string input = part.TryGetProperty("input", out var value) && value.ValueKind == JsonValueKind.Object ? value.GetRawText() : "{}";
                events.Add(new ClaudeServerEvent.ToolUse(id, name, input));
            }
        }

        return events;
    }

    private static ClaudeServerEvent.ControlResponse ReadControl(JsonElement root)
    {
        if (!root.TryGetProperty("response", out var response) || response.ValueKind != JsonValueKind.Object)
        {
            return new ClaudeServerEvent.ControlResponse(null, false);
        }

        return new ClaudeServerEvent.ControlResponse(Text(response, "request_id"), Text(response, "subtype") == "success");
    }

    private static ClaudeServerEvent.Result ReadResult(JsonElement root)
    {
        bool isError = root.TryGetProperty("is_error", out var flag) && flag.ValueKind == JsonValueKind.True;
        decimal cost = root.TryGetProperty("total_cost_usd", out var usd) && usd.ValueKind == JsonValueKind.Number && usd.TryGetDecimal(out var value) ? value : 0m;
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

            string text = Text(root, "result") ?? "";
            error = errors.Count > 0 ? string.Join("; ", errors)
                : text.Length > 0 ? text
                : Text(root, "subtype") ?? ClaudeText.UnknownFailure;
        }

        int window = 0;
        if (root.TryGetProperty("modelUsage", out var models) && models.ValueKind == JsonValueKind.Object)
        {
            foreach (var model in models.EnumerateObject())
            {
                if (model.Value.ValueKind == JsonValueKind.Object)
                {
                    window = Math.Max(window, (int)Math.Min(int.MaxValue, Number(model.Value, "contextWindow")));
                }
            }
        }

        return new ClaudeServerEvent.Result(Text(root, "session_id"), isError, error, Text(root, "terminal_reason"), cost, window);
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number) ? number : 0;
}
