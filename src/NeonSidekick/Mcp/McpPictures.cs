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
    private readonly List<ImageAttachment> _taken = [];

    public McpPictures(string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        _label = label;
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

        string label = _label + " #" + (_taken.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (bytes.Length == 0)
        {
            return McpText.PictureUnreadable(ImageLoadFailure.CouldNotRead);
        }

        if (!ImageFile.TryLoad(bytes, label, out var image, out var failure, photo: false) || image is null)
        {
            return McpText.PictureUnreadable(failure);
        }

        _taken.Add(image with { Path = "", McpSource = label });
        return McpText.PictureAttached(_taken.Count, image.Width, image.Height);
    }

    /// <summary>Whether a blob's media type names a picture (<c>image/…</c>); one with none is not taken for one.</summary>
    public static bool IsPicture(string? mimeType) =>
        mimeType is not null && mimeType.TrimStart().StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    /// <summary>About how many bytes base64 of this length decodes to: three quarters of it.</summary>
    public static long DecodedLength(ReadOnlyMemory<byte> base64) => base64.Length / 4L * 3L;
}
