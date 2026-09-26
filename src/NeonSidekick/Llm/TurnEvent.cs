using NeonSidekick.Files;

namespace NeonSidekick.Llm;

/// <summary>
/// What <see cref="Assistant.RunTurnAsync"/> yields while a turn runs. The end of the enumeration
/// is the end of the turn; there is no terminal event. Cancellation is not an event either: it
/// propagates as <see cref="OperationCanceledException"/> after the partial reply has been
/// committed, because on the voice path it means barge-in and must stay distinguishable from
/// every message-shaped outcome. <b>[scar]</b>
/// </summary>
public abstract record TurnEvent
{
    private TurnEvent()
    {
    }

    /// <summary>A piece of the reply, in order. Never empty.</summary>
    public sealed record TextDelta(string Text) : TurnEvent;

    /// <summary>
    /// A piece of the model's thinking, in order, ahead of the reply it leads to (2026-09-26, the
    /// user's ask: thinking shown in the transcript). Never empty. From the server's
    /// <c>reasoning_content</c> (or <c>reasoning</c>) deltas, or a <c>&lt;think&gt;</c> block streamed as
    /// content (<see cref="ThinkTagFilter.TakeThinking"/>). Not reply text: never spoken, never in
    /// <c>/copy</c> or the session log, and the assistant commits nothing of it to the history. A
    /// host that does not show thinking skips it.
    /// </summary>
    public sealed record ThinkingDelta(string Text) : TurnEvent;

    /// <summary>The model asked for a tool; raised before it is invoked.</summary>
    public sealed record ToolCall(string Name, string CallId, string ArgumentsJson) : TurnEvent;

    /// <summary>
    /// What went back to the model for one call, on every branch (success, unknown tool, error).
    /// <paramref name="Images"/> are the pictures a <c>view_image</c> call fetched — carried to the
    /// model in the message after the results (<see cref="ConversationHistory.AddToolImages"/>) and
    /// here so a host can draw them; null (read as none) for every other tool.
    /// </summary>
    public sealed record ToolResult(string Name, string CallId, string Text, IReadOnlyList<ImageAttachment>? Images = null) : TurnEvent;

    /// <summary>Something the user should see that is not reply text: a timeout, a server error, an iteration cap.</summary>
    public sealed record Notice(string Text, bool IsError) : TurnEvent;

    /// <summary>
    /// What one model request cost, raised after its stream completed and before its tool calls
    /// run: one per request the server reported usage for, so a turn with tool calls raises
    /// several and a host sums them. A cancelled or failed request raises none.
    /// </summary>
    public sealed record Usage(TokenUsage Tokens) : TurnEvent;
}
