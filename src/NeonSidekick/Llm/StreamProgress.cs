using Microsoft.Extensions.AI;
using OpenAI.Chat;

namespace NeonSidekick.Llm;

/// <summary>
/// Whether a streamed update shows the model producing something (2026-10-26, the user's report: the hint row read well over
/// 100,000 tok/s). Every client sends a tool call whole — the M.E.AI adapter gathers the <c>tool_calls</c> deltas, the Anthropic and
/// Responses streams and the Claude Code server buffer the arguments — so a round that only called a tool started its decode clock at
/// the stream's end and read hundreds of tokens over a few milliseconds. The speed's clock starts at the first update that
/// <see cref="Shows"/> (<see cref="Assistant"/>'s three measuring sites), and the live meter counts the same updates.
///
/// <para>A client that buffers what the model produces yields a contents-free update carrying <see cref="Marker"/> as its
/// <see cref="ChatResponseUpdate.RawRepresentation"/> while it does: a tool call's arguments, and a block whose thinking is hidden
/// (the Anthropic API's omitted display, the Responses API without a summary, the Claude Code server always). Built by each stream's
/// own update code with an empty contents list, so its role and ids match and <c>ToChatResponse</c> keeps the reply one message;
/// <c>ToChatResponse</c> never reads a <c>RawRepresentation</c>, so nothing of the marker reaches the history. The OpenAI-compatible
/// servers need none: the adapter yields every chunk with the SDK's own as its raw representation, the tool calls' deltas in
/// <see cref="StreamingChatCompletionUpdate.ToolCallUpdates"/>. A new stream client keeps marking what it buffers: one marker as each
/// block it holds back starts (2026-10-26, the review's R25: one per delta made a tool call's arguments read as hundreds of updates), through
/// <see cref="Mark"/>.</para>
/// </summary>
public static class StreamProgress
{
    /// <summary>The sentinel a contents-free update carries as its raw representation while the model produces what the caller can't see yet.</summary>
    public static readonly object Marker = new();

    /// <summary>
    /// <paramref name="update"/> (a stream's own contents-free update, its role and ids set) marked: the model producing what the stream holds
    /// back (2026-10-26, the review's R26: one helper for the three streams' copies).
    /// </summary>
    public static ChatResponseUpdate Mark(ChatResponseUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        update.RawRepresentation = Marker;
        return update;
    }

    /// <summary>Whether <paramref name="update"/> is a marker (<see cref="Mark"/>).</summary>
    public static bool IsMarker(ChatResponseUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        return ReferenceEquals(update.RawRepresentation, Marker);
    }

    /// <summary>
    /// True when <paramref name="update"/> carries something the model produced: a content that holds something, the
    /// <see cref="Marker"/>, or the adapter's chunk of tool-call deltas. A role-only chunk, a server's <c>content: ""</c> role
    /// chunk and a usage-only chunk show nothing.
    /// </summary>
    public static bool Shows(ChatResponseUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (ReferenceEquals(update.RawRepresentation, Marker)
            || update.RawRepresentation is StreamingChatCompletionUpdate { ToolCallUpdates.Count: > 0 })
        {
            return true;
        }

        foreach (var content in update.Contents)
        {
            if (Carries(content))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether one content holds something: not usage, not empty text, not empty reasoning — a signature or an encrypted block
    /// (<see cref="TextReasoningContent.ProtectedData"/>) is the end of real thinking, so it counts.
    /// </summary>
    private static bool Carries(AIContent content) => content switch
    {
        UsageContent => false,
        TextContent text => !string.IsNullOrEmpty(text.Text),
        TextReasoningContent reasoning => !string.IsNullOrEmpty(reasoning.Text) || !string.IsNullOrEmpty(reasoning.ProtectedData),
        _ => true,
    };
}
