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
    /// <c>/copy</c> unless asked with <c>--thinking</c> (2026-09-26) or in the session log. The history
    /// keeps it as <see cref="Microsoft.Extensions.AI.TextReasoningContent"/>, never as reply text, and the
    /// client sends it back (2026-09-28: the turn in flight's always, every turn's with
    /// <see cref="Assistant.PreserveThinking"/>). A host that does not show thinking skips it.
    /// </summary>
    public sealed record ThinkingDelta(string Text) : TurnEvent;

    /// <summary>The model asked for a tool; raised before it is invoked.</summary>
    public sealed record ToolCall(string Name, string CallId, string ArgumentsJson) : TurnEvent;

    /// <summary>
    /// What went back to the model for one call, on every branch (success, unknown tool, error).
    /// <paramref name="Images"/> are the pictures a <c>view_image</c> call fetched — carried to the
    /// model in the message after the results (<see cref="ConversationHistory.AddToolImages"/>) and
    /// here so a host can draw them; null (read as none) for every other tool. <paramref name="Diff"/> is what a file-writing
    /// tool changed (2026-10-03, <see cref="Tools.ToolDiffResult"/>): the host's to draw, never in the history; null otherwise.
    /// </summary>
    public sealed record ToolResult(string Name, string CallId, string Text, IReadOnlyList<ImageAttachment>? Images = null, FileDiff? Diff = null) : TurnEvent;

    /// <summary>Something the user should see that is not reply text: a timeout, a server error, an iteration cap.</summary>
    public sealed record Notice(string Text, bool IsError) : TurnEvent;

    /// <summary>
    /// What one model request cost, raised after its stream completed and before its tool calls
    /// run: one per request the server reported usage for, so a turn with tool calls raises
    /// several and a host sums them. A cancelled or failed request raises none.
    /// </summary>
    public sealed record Usage(TokenUsage Tokens) : TurnEvent;

    /// <summary>
    /// The mid-turn guard is about to summarise (<see cref="ToolCompactMode.Compact"/>, 2026-09-28): raised before each
    /// summariser request, so a host can say what the wait is. <paramref name="Percent"/> is the estimated share that fired.
    /// Always followed by <see cref="Compacted"/>, unless the summary failed (a <see cref="Notice"/>) or was cancelled.
    /// </summary>
    public sealed record Compacting(int Percent) : TurnEvent;

    /// <summary>
    /// The mid-turn guard summarised (2026-09-28) and the history is the new one: the turns before this one
    /// (<paramref name="ThisTurn"/> false) or this turn's earlier iterations (true). The host bills
    /// <see cref="ConversationCompactor.Result.Usage"/> the way <c>/compact</c>'s is billed and shows the notice.
    /// </summary>
    public sealed record Compacted(ConversationCompactor.Result Result, int Percent, bool ThisTurn) : TurnEvent;
}
