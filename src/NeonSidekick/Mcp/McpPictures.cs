using NeonSidekick.Files;
using NeonSidekick.Llm.Tools;

namespace NeonSidekick.Mcp;

/// <summary>
/// The context an MCP result is rendered in (2026-11-21, feature ideas #31 and #33): <see cref="Label"/> names its pictures
/// (<c>&lt;label&gt; #n</c>), the tool's prefixed name or <c>mcp_read</c>; <see cref="ReadOffered"/> says whether a resource link and an
/// embedded text's cut note may point to <c>mcp_read</c>, true only when <c>mcp_read</c> is offered this turn for the tool's own server.
/// Otherwise a link reads as a plain reference and a cut note says the rest wasn't kept.
/// </summary>
public sealed record McpRenderContext(string Label, bool ReadOffered)
{
    /// <summary>A context that never names <c>mcp_read</c>: the plain shape tests and the smoke probe pin.</summary>
    public static McpRenderContext Plain(string label) => new(label, false);

    /// <summary>
    /// The conversation's picture numbers (2026-11-22, the review's #60): one counter for every MCP tool and <c>mcp_read</c>, so two calls'
    /// pictures never share a label. A fresh one by default (a test's, the smoke's), numbering from 1.
    /// </summary>
    public McpPictureCounter Pictures { get; init; } = new();

    /// <summary>
    /// Whether the model is shown the pictures (2026-11-22, the review's #131): false, a picture's marker says it was shown to the user, never
    /// that it comes in the next message.
    /// </summary>
    public bool PicturesSeen { get; init; } = true;
}

/// <summary>The numbers an MCP conversation's pictures take (2026-11-22, the review's #60). Thread-safe.</summary>
public sealed class McpPictureCounter
{
    private int _last;

    /// <summary>The next number, from 1.</summary>
    public int Next() => Interlocked.Increment(ref _last);

    /// <summary>Starts again past <paramref name="last"/>: 0 for a new conversation, a restored one's highest number for a resume.</summary>
    public void Reset(int last = 0) => Volatile.Write(ref _last, Math.Max(0, last));

    /// <summary>
    /// The highest <c>#N</c> among <paramref name="labels"/> (a restored history's MCP picture labels), or 0: a resume numbers past it, so a
    /// restored <c>browser_take_screenshot #2</c> is never given again.
    /// </summary>
    public static int Highest(IEnumerable<string> labels)
    {
        ArgumentNullException.ThrowIfNull(labels);
        int highest = 0;
        foreach (string label in labels)
        {
            int hash = label.LastIndexOf(" #", StringComparison.Ordinal);
            if (hash >= 0 && int.TryParse(label.AsSpan(hash + 2), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int n))
            {
                highest = Math.Max(highest, n);
            }
        }

        return highest;
    }
}

/// <summary>A rendered MCP result (2026-11-21, feature idea #31): the model's text and the pictures that come in the next message.</summary>
public sealed record McpRendered(string Text, IReadOnlyList<ImageAttachment> Pictures)
{
    /// <summary>What a tool hands the turn loop: a <see cref="ToolImageResult"/> when there are pictures, else the text.</summary>
    public object ToolResult() => Pictures.Count > 0 ? new ToolImageResult(Text, Pictures) : Text;
}

/// <summary>
/// One result's pictures as they are taken (2026-11-21, feature idea #31): a block's base64 decoded into an <see cref="ImageAttachment"/> through
/// <see cref="ImageFile.TryLoad(byte[], string, out ImageAttachment?, out ImageLoadFailure, bool, int)"/> (its caps, the metadata strip, no throw
/// on bad bytes), labelled <c>&lt;label&gt; #n</c> in <see cref="ImageAttachment.McpSource"/>, with no <see cref="ImageAttachment.Path"/>:
/// <c>TryLoad</c> makes its name the path, and a label there would read as a file to the transcript's links and the picture lookup. Up to
/// <see cref="McpText.PicturesPerCall"/>; the conversation-wide <c>LLM max images</c> and its megabytes apply afterwards, as for any
/// tool's pictures. Not thread-safe: one result's render.
/// </summary>
public sealed class McpPictures
{
    private readonly string _label;
    private readonly McpPictureCounter _counter;
    private readonly bool _seen;
    private readonly List<ImageAttachment> _taken = [];

    /// <param name="label">What names the pictures (<c>&lt;label&gt; #n</c>).</param>
    /// <param name="counter">The conversation's numbers (#60); a fresh one numbers from 1.</param>
    /// <param name="seen">Whether the model is shown them (#131).</param>
    public McpPictures(string label, McpPictureCounter? counter = null, bool seen = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        _label = label;
        _counter = counter ?? new McpPictureCounter();
        _seen = seen;
    }

    /// <summary>A context's pictures: its label, its counter, whether they're seen.</summary>
    public static McpPictures For(McpRenderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new McpPictures(context.Label, context.Pictures, context.PicturesSeen);
    }

    /// <summary>The pictures taken so far, in order.</summary>
    public IReadOnlyList<ImageAttachment> Taken => _taken;

    /// <summary>
    /// The block's piece of the text: <see cref="McpText.PictureAttached"/> for a picture taken, else why not. <paramref name="base64"/> is the
    /// block's own data (base64 text as UTF-8 bytes, the SDK's shape), measured before anything is decoded: three quarters of it past
    /// <see cref="ImageFile.MaxFileBytes"/> is refused as too large, so a huge block is never decoded only to be refused.
    /// <paramref name="decode"/> is the SDK's <c>DecodedData</c>, which decodes on first read; bad base64 there reads as unreadable, never a
    /// failed call.
    /// </summary>
    public string Add(ReadOnlyMemory<byte> base64, Func<ReadOnlyMemory<byte>> decode)
    {
        ArgumentNullException.ThrowIfNull(decode);
        if (_taken.Count >= McpText.PicturesPerCall)
        {
            return McpText.PictureLeftOut;
        }

        if (DecodedLength(base64) > ImageFile.MaxFileBytes)
        {
            return McpText.PictureUnreadable(ImageLoadFailure.TooLarge);
        }

        byte[] bytes;
        try
        {
            bytes = decode().ToArray();
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or ArgumentException)
        {
            return McpText.PictureUnreadable(ImageLoadFailure.CouldNotRead);
        }

        if (bytes.Length == 0)
        {
            return McpText.PictureUnreadable(ImageLoadFailure.CouldNotRead);
        }

        if (!ImageFile.TryLoad(bytes, _label, out var image, out var failure, photo: false) || image is null)
        {
            return McpText.PictureUnreadable(failure);
        }

        // Numbered only once taken, from the conversation's counter (#60): a refused block takes no number.
        int number = _counter.Next();
        string label = _label + " #" + number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _taken.Add(image with { Path = "", McpSource = label });
        return _seen ? McpText.PictureAttached(number, image.Width, image.Height) : McpText.PictureShownToUser(number, image.Width, image.Height);
    }

    /// <summary>Whether a blob's media type names a picture (<c>image/…</c>); one with none is not taken for one.</summary>
    public static bool IsPicture(string? mimeType) =>
        mimeType is not null && mimeType.TrimStart().StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    /// <summary>About how many bytes base64 of this length decodes to: three quarters of it.</summary>
    public static long DecodedLength(ReadOnlyMemory<byte> base64) => base64.Length / 4L * 3L;
}
