using Microsoft.Extensions.AI;
using NeonSidekick.Comfy;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;

namespace NeonSidekick.App;

/// <summary>
/// The pictures of one turn of a restored history, as the replay draws them (2026-10-10, <see cref="RestoredPictures"/>):
/// <paramref name="Ordinal"/>, the store row the turn wrote (<see cref="ConversationHistory.TurnOrdinal"/>; null for a turn
/// never stamped); <paramref name="Imagined"/>, <c>/imagine</c>'s pictures, which rode this message and were drawn before it;
/// <paramref name="Own"/>, the user's own (pasted, dropped, a kept camera or screen shot); <paramref name="Tools"/>, every
/// picture a tool fetched or made in the turn; <paramref name="Generated"/>, those of <c>generate_image</c> among them.
/// </summary>
public sealed record TurnPictures(int? Ordinal, IReadOnlyList<DataContent> Imagined, IReadOnlyList<DataContent> Own, IReadOnlyList<DataContent> Tools, IReadOnlyList<DataContent> Generated)
{
    /// <summary>A turn with no pictures.</summary>
    public static TurnPictures None(int? ordinal) => new(ordinal, [], [], [], []);

    /// <summary>What the picture strip gathers from the turn: ComfyUI's, <c>/imagine</c>'s first (they were made before the message).</summary>
    public IReadOnlyList<DataContent> Strip => [.. Imagined, .. Generated];

    /// <summary>Whether the turn has any picture to draw.</summary>
    public bool Any => Imagined.Count + Own.Count + Tools.Count > 0;
}

/// <summary>
/// Where a restored conversation's pictures are (2026-10-10, the user's ask: a chat brought back by <c>/sessions</c> or
/// <c>/rewind</c> left its ComfyUI pictures out of the strip and every picture out of the transcript). The session keeps each
/// picture's bytes and path (<c>Sessions.SessionHistory</c>), so they are read back out of the messages:
/// <list type="bullet">
/// <item>A tool's pictures are the carrier after its result (<see cref="ConversationHistory.IsImageCarrier"/>), the tool named by
/// <see cref="ConversationHistory.CarrierSource"/>; <c>generate_image</c>'s are the ComfyUI ones.</item>
/// <item><c>/imagine</c>'s pictures rode the next message after the user's own, its text led by
/// <see cref="ComfyText.ImagineNotePrefix"/>. The user's own each left an <c>[Image #n]</c> label in the text, so in a message with
/// a note the first pictures, as many as the labels, are the user's and the rest <c>/imagine</c>'s.</item>
/// </list>
/// Not here: a <c>/botchat</c>'s pictures (never stored), camera and screen shots not kept (a line of text in the session), and
/// pictures the picture budget or a compact took out. Pure.
/// </summary>
public static class RestoredPictures
{
    /// <summary>
    /// One entry per turn of <paramref name="messages"/> (<see cref="ConversationHistory.IsTurnStart"/>), oldest first, a compact's
    /// summary turn too (with nothing), so the list lines up with <see cref="ConversationRewind.Replay"/> by index.
    /// </summary>
    public static IReadOnlyList<TurnPictures> Turns(IReadOnlyList<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var turns = new List<TurnPictures>();
        for (int i = 0; i < messages.Count; i++)
        {
            var start = messages[i];
            if (!ConversationHistory.IsTurnStart(start))
            {
                continue;
            }

            int? ordinal = ConversationHistory.TurnOrdinal(start);
            int end = i + 1;
            while (end < messages.Count && !ConversationHistory.IsTurnStart(messages[end]))
            {
                end++;
            }

            if (ConversationRewind.IsSummary(start))
            {
                turns.Add(TurnPictures.None(ordinal));
                continue;
            }

            var (own, imagined) = Split(start);
            var tools = new List<DataContent>();
            var generated = new List<DataContent>();
            for (int j = i + 1; j < end; j++)
            {
                if (!ConversationHistory.IsImageCarrier(messages[j]))
                {
                    continue;
                }

                var pictures = ConversationRewind.Images(messages[j]);
                tools.AddRange(pictures);
                if (string.Equals(ConversationHistory.CarrierSource(messages[j]), GenerateImageTool.ToolName, StringComparison.Ordinal))
                {
                    generated.AddRange(pictures);
                }
            }

            turns.Add(new TurnPictures(ordinal, imagined, own, tools, generated));
        }

        return turns;
    }

    /// <summary>A user message's pictures split into the user's own and <c>/imagine</c>'s (the class's rule).</summary>
    private static (IReadOnlyList<DataContent> Own, IReadOnlyList<DataContent> Imagined) Split(ChatMessage message)
    {
        var pictures = ConversationRewind.Images(message);
        string text = UI.PasteBlocks.WithoutSavedPaths(ConversationRewind.UserLine(message));
        if (pictures.Count == 0 || !text.StartsWith(ComfyText.ImagineNotePrefix, StringComparison.Ordinal))
        {
            return (pictures, []);
        }

        int own = Math.Min(ConversationRewind.ImageLabels(text).Count, pictures.Count);
        return (pictures.Take(own).ToList(), pictures.Skip(own).ToList());
    }
}
