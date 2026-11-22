using ModelContextProtocol.Protocol;

namespace NeonSidekick.Mcp;

/// <summary>
/// What a call through the session came to (2026-11-19, feature idea #30): the server's answer, rendered by
/// <see cref="McpTool.Render"/> and fenced as ever, or the app's own error text (a drop, a server that didn't come back, a
/// tool gone), returned as <see cref="McpText.ErrorPrefix"/> plus the text and unfenced since it's the app's. <see cref="Fenced"/>
/// says the text holds a part that can be the server's (a failed reconnect's detail), fenced on its own by the session, so the
/// quote note goes last.
/// </summary>
public sealed record McpCallOutcome
{
    private McpCallOutcome(CallToolResult? result, string? error, bool fenced)
    {
        Result = result;
        Error = error;
        Fenced = fenced;
    }

    /// <summary>The server's answer, or null for the app's own error.</summary>
    public CallToolResult? Result { get; }

    /// <summary>The app's own error text (without the prefix), or null for an answer.</summary>
    public string? Error { get; }

    /// <summary>Whether <see cref="Error"/> holds a fenced part.</summary>
    public bool Fenced { get; }

    public static McpCallOutcome Answered(CallToolResult result) => new(result ?? throw new ArgumentNullException(nameof(result)), null, false);

    public static McpCallOutcome Failed(string error, bool fenced = false) => new(null, error ?? throw new ArgumentNullException(nameof(error)), fenced);

    /// <summary>The model's one string, under <see cref="McpRenderContext.Plain"/> (a test's view of the text).</summary>
    public string Render() => Render(McpRenderContext.Plain("mcp")).Text;

    /// <summary>What the model is handed (2026-11-21, feature idea #31): the answer's text and pictures, or the app's error with none.</summary>
    public McpRendered Render(McpRenderContext context) => Result is { } result
        ? McpTool.Render(result, context)
        : new McpRendered(McpText.ErrorPrefix + Error + (Fenced ? "\n" + Llm.Tools.UntrustedText.QuoteNote : ""), []);
}
