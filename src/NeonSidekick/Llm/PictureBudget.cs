using System.Globalization;
using Microsoft.Extensions.AI;

namespace NeonSidekick.Llm;

/// <summary>
/// How many pictures, and how many megabytes of them, one request may carry (2026-10-03, the user's report: a session of
/// ComfyUI pictures — ~50 PNGs at ~2 MB of base64 each, every one sent again with every request — reached ~120 MB of
/// request body, and the embedded llama-server closed the connection mid-upload with the window only three quarters full;
/// <c>/compact</c> got it going again). Every check before this one counted tokens, and a picture is ~1,000 tokens however
/// many bytes it is, so nothing saw the body grow. The settings <c>LLM picture keep</c> (<see cref="Pictures"/>) and
/// <c>LLM picture megabytes</c> (<see cref="Megabytes"/>); 0 is no cap on that measure.
///
/// <para><see cref="Apply"/> walks the user-role messages (carriers and what the user sent) oldest first and, once
/// either measure is over its cap, takes the oldest pictures out until both are at <em>half</em> their caps: a drop in
/// batches rather than one picture a message, so the request's prefix stays the same for many turns after and a local
/// server's prompt cache is not thrown away every turn. The newest message that holds a picture is never touched — the
/// model sees what it just made or was just given. A picture taken out leaves <see cref="LeftOut"/> in its place, naming
/// its file (<see cref="ConversationHistory.PathKey"/>) so the model can look again with <c>view_image</c>; the message
/// keeps its tags, so a carrier is still one. Pure.</para>
/// </summary>
public readonly record struct PictureBudget(int Pictures, int Megabytes)
{
    /// <summary><c>LLM picture keep</c>'s default: twenty pictures, ~20k tokens on a vision model that charges ~1,000 each.</summary>
    public const int DefaultPictures = 20;

    /// <summary>
    /// <c>LLM picture megabytes</c>' default: under the Claude API's 32 MB request cap and well under the ~100 MB a
    /// llama-server body tops out at, with room for the rest of the request.
    /// </summary>
    public const int DefaultMegabytes = 24;

    /// <summary>The compiled defaults, what an assistant starts with until the shell sets the profile's.</summary>
    public static PictureBudget Default => new(DefaultPictures, DefaultMegabytes);

    /// <summary>Whether neither measure is capped.</summary>
    public bool IsNone => Pictures <= 0 && Megabytes <= 0;

    /// <summary>
    /// What a picture taken out becomes (a <see cref="TextContent"/> in its place): <c>(picture comfy_images\a.png taken
    /// out to keep the request small)</c>, or <c>(a picture, taken out to keep the request small)</c> when it had no path
    /// (a session stored before the path was). Pinned.
    /// </summary>
    public static string LeftOut(string? path) =>
        string.IsNullOrWhiteSpace(path) ? "(a picture, taken out to keep the request small)" : "(picture " + path + " taken out to keep the request small)";

    /// <summary>The bytes a picture costs on the wire: its data as base64, which is what a data URL or a media block carries.</summary>
    public static long WireBytes(DataContent picture)
    {
        ArgumentNullException.ThrowIfNull(picture);
        return (picture.Data.Length + 2L) / 3 * 4;
    }

    /// <summary>The pictures <paramref name="messages"/> carry (user-role messages only, where a picture can ride) and their <see cref="WireBytes"/>.</summary>
    public static (int Pictures, long Bytes) Measure(IEnumerable<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        int pictures = 0;
        long bytes = 0;
        foreach (var message in messages)
        {
            if (message.Role != ChatRole.User)
            {
                continue;
            }

            foreach (var content in message.Contents)
            {
                if (content is DataContent picture)
                {
                    pictures++;
                    bytes += WireBytes(picture);
                }
            }
        }

        return (pictures, bytes);
    }

    /// <summary>
    /// <paramref name="messages"/> within this budget: unchanged (the same instances, nothing taken out) while both measures
    /// are at or under their caps; else the oldest pictures taken out until both are at half their caps, by
    /// <see cref="TakeOut"/>.
    /// </summary>
    public PictureTrim Apply(IReadOnlyList<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var (pictures, bytes) = Measure(messages);
        bool overCount = Pictures > 0 && pictures > Pictures;
        bool overBytes = Megabytes > 0 && bytes > Megabytes * 1_000_000L;
        if (!overCount && !overBytes)
        {
            return new PictureTrim([.. messages], 0, 0);
        }

        int keepPictures = Pictures > 0 ? Pictures / 2 : int.MaxValue;
        long keepBytes = Megabytes > 0 ? Megabytes * 1_000_000L / 2 : long.MaxValue;
        return TakeOut(messages, keepPictures, keepBytes);
    }

    /// <summary>
    /// <paramref name="messages"/> with the oldest pictures taken out until at most <paramref name="keepPictures"/> and
    /// <paramref name="keepBytes"/> of them are left, the newest message holding a picture never touched (so fewer may go
    /// than asked). A message that loses a picture is a clone with <see cref="LeftOut"/> in each picture's place, its
    /// other parts and its tags as they were; every other message is the same instance. The retry after a dropped
    /// request asks for this directly, with half what that request carried.
    /// </summary>
    public static PictureTrim TakeOut(IReadOnlyList<ChatMessage> messages, int keepPictures, long keepBytes)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentOutOfRangeException.ThrowIfNegative(keepPictures);
        ArgumentOutOfRangeException.ThrowIfNegative(keepBytes);
        int newest = -1;
        for (int i = messages.Count - 1; i >= 0 && newest < 0; i--)
        {
            if (messages[i].Role == ChatRole.User && messages[i].Contents.Any(c => c is DataContent))
            {
                newest = i;
            }
        }

        var (pictures, bytes) = Measure(messages);
        var result = new List<ChatMessage>(messages);
        int taken = 0;
        long takenBytes = 0;
        for (int i = 0; i < newest && (pictures > keepPictures || bytes > keepBytes); i++)
        {
            var message = messages[i];
            if (message.Role != ChatRole.User || !message.Contents.Any(c => c is DataContent))
            {
                continue;
            }

            var contents = new List<AIContent>(message.Contents.Count);
            foreach (var content in message.Contents)
            {
                if (content is DataContent picture && (pictures > keepPictures || bytes > keepBytes))
                {
                    long size = WireBytes(picture);
                    contents.Add(new TextContent(LeftOut(ConversationHistory.PicturePath(picture))));
                    pictures--;
                    bytes -= size;
                    taken++;
                    takenBytes += size;
                    continue;
                }

                contents.Add(content);
            }

            var rebuilt = message.Clone();
            rebuilt.Contents = contents;
            result[i] = rebuilt;
        }

        return new PictureTrim(result, taken, takenBytes);
    }

    /// <summary>A size for the operator: <c>31 MB</c> (decimal megabytes, rounded; <c>0.4 MB</c> under one). Pinned.</summary>
    public static string FormatMegabytes(long bytes)
    {
        double megabytes = bytes / 1_000_000d;
        return megabytes >= 1
            ? Math.Round(megabytes).ToString("N0", CultureInfo.InvariantCulture) + " MB"
            : megabytes.ToString("0.0", CultureInfo.InvariantCulture) + " MB";
    }

    /// <summary>
    /// The notice a turn shows when the budget took pictures out before a request: <c>🖼 12 older pictures taken out
    /// (31 MB) to keep the request small</c>. Pinned. The glyph carries U+FE0F (2026-10-04, the user's catch: the bare 🖼
    /// defaults to text presentation, which Windows Terminal draws two cells wide in one, so it ran into the count — the
    /// picture viewer's fix of 2026-09-27, <c>ViewerText.Opened</c>).
    /// </summary>
    public static string TakenOutNotice(int pictures, long bytes) =>
        "🖼️ " + pictures.ToString(CultureInfo.InvariantCulture) + (pictures == 1 ? " older picture" : " older pictures") + " taken out (" + FormatMegabytes(bytes) + ") to keep the request small";
}

/// <summary>What <see cref="PictureBudget.Apply"/> or <see cref="PictureBudget.TakeOut"/> made: the messages, and the pictures taken out and their wire bytes.</summary>
public sealed record PictureTrim(List<ChatMessage> Messages, int Pictures, long Bytes);
