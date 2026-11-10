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

    /// <summary>
    /// The model is writing a call to <paramref name="Name"/> (2026-10-29, session 832: seven minutes of a call's arguments read as
    /// "thinking"): raised as the request's first call is named, then at most once a <see cref="ToolArgumentWatch.ProgressInterval"/> as the call
    /// being written or its size changes (<paramref name="Chars"/>, its arguments' characters so far; 0 while the server holds them back, as SGLang does). Progress for the busy row alone: never in the history,
    /// the transcript, the session log or <c>/copy</c>; a host that does not show it skips it. The <see cref="ToolCall"/> follows once the call is whole.
    /// </summary>
    public sealed record ToolWriting(string Name, long Chars) : TurnEvent;

    /// <summary>The model asked for a tool; raised before it is invoked.</summary>
    public sealed record ToolCall(string Name, string CallId, string ArgumentsJson) : TurnEvent;

    /// <summary>
    /// What went back to the model for one call, on every branch (success, unknown tool, error).
    /// <paramref name="Images"/> are the pictures a <c>view_image</c> call fetched — carried to the
    /// model in the message after the results (<see cref="ConversationHistory.AddToolImages"/>) and
    /// here so a host can draw them; null (read as none) for every other tool. <paramref name="Diff"/> is what a file-writing
    /// tool changed (2026-10-03, <see cref="Tools.ToolDiffResult"/>): the host's to draw, never in the history; null otherwise.
    /// <paramref name="Shown"/> is a line for the user in the text's place (later on 2026-10-03, <see cref="Tools.ToolShownResult"/>:
    /// the forbidden string the model is not told); the history keeps the text; null otherwise. <paramref name="Took"/> (2026-11-02, the
    /// review: the reply footer's tool times) is the call's wall time, null for a call never run (cut off by the length limit), and
    /// <paramref name="Asked"/> the part of it spent waiting on the user (<see cref="ToolSpan"/>), which the time shown leaves out.
    /// </summary>
    public sealed record ToolResult(string Name, string CallId, string Text, IReadOnlyList<ImageAttachment>? Images = null, FileDiff? Diff = null, string? Shown = null, TimeSpan? Took = null, TimeSpan Asked = default) : TurnEvent;

    /// <summary>
    /// Something the user should see that is not reply text: a timeout, a server error, an iteration cap. <paramref name="AskAgain"/>: the
    /// host adds <see cref="TurnText.AskAgain"/> unless a loop's pass runs (2026-10-30, review F96: a stopped call in an audit pass said
    /// "ask again" over the audit's own resume line).
    /// </summary>
    public sealed record Notice(string Text, bool IsError, bool AskAgain = false) : TurnEvent;

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
    /// <see cref="ConversationCompactor.Result.Usage"/> the way <c>/compact</c>'s is billed and shows the notice. Since 2026-11-09 it also
    /// reports the overflow recovery's prune of the turns before this one: a <see cref="ConversationCompactor.Result"/> with
    /// <see cref="ConversationCompactor.Result.Summarised"/> false and no usage, not preceded by <see cref="Compacting"/>.
    /// </summary>
    public sealed record Compacted(ConversationCompactor.Result Result, int Percent, bool ThisTurn) : TurnEvent;

    /// <summary>
    /// The server named a context window smaller than the guard's, or one where none was known, in a "too long" error (2026-11-09):
    /// llama-server's per-slot <c>n_ctx</c>, or a wrong setting. The chat session's host remembers it until the next connect
    /// (<c>LlmSession.NoteServerLimit</c>) and says so once.
    /// </summary>
    public sealed record ServerLimit(int Tokens) : TurnEvent;
}
