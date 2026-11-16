using NeonSidekick.Files;

namespace NeonSidekick.WebUi;

/// <summary>What a phone's upload is (2026-11-16, plan lucid-roaming-lantern #39), from its bytes, never its header or name.</summary>
public enum WebAttachKind
{
    Picture,
    Pdf,
}

/// <summary>
/// An upload the session holds for a page's next message (2026-11-16, plan lucid-roaming-lantern #39): the server's id, its kind, the bytes as
/// they came (the 60 MB counts these), a picture's attachment as <see cref="ImageFile.TryLoad(byte[], string, out ImageAttachment?, out ImageLoadFailure, bool)"/>
/// made it (stripped and fitted) and its chip's small picture, the page it is held under, and when it is let go. <see cref="Saved"/> is the
/// file it was saved as once its line went to a turn: a give-back keeps it, so a resend uses that file and saves no second copy.
/// </summary>
public sealed record WebHeldAttachment(string Id, WebAttachKind Kind, byte[] Bytes, ImageAttachment? Picture, string? Thumb, string Page)
{
    /// <summary>When it is let go (a <see cref="TimeProvider.GetTimestamp"/> reading).</summary>
    public long Expires { get; init; }

    /// <summary>The full path it was saved as under <i>Pasted images folder</i>; null until then, or when the save failed.</summary>
    public string? Saved { get; init; }

    /// <summary>The sniffed media type of <see cref="Bytes"/> (a picture's source, <c>application/pdf</c> for a PDF): the saved file's extension.</summary>
    public string MediaType { get; init; } = "";

    /// <summary>The page's chip for it.</summary>
    public WebChip Chip => new(Id, Kind == WebAttachKind.Pdf ? "pdf" : "picture", FileText.Size(Bytes.LongLength), Thumb);
}

/// <summary>
/// An upload read (2026-11-16, plan lucid-roaming-lantern #39): a PDF by its header; anything else through the picture loader, which strips
/// its metadata and fits it, as a photo. A refusal is chosen by the bytes: a HEIF brand the PC's codecs don't read gets
/// <see cref="WebUiText.AttachHeic"/>, a picture over <see cref="ImageFile.MaxPixels"/> <see cref="WebUiText.AttachTooManyPixels"/>, a picture
/// container that won't decode <see cref="WebUiText.AttachUnreadable"/> (each a 422), and anything else <see cref="WebUiText.AttachNotPicture"/>
/// (a 415). Pure apart from the codecs.
/// </summary>
public static class WebAttachSniff
{
    /// <summary>The <c>ftyp</c> brands of a HEIF picture (an iPhone's HEIC among them).</summary>
    public static readonly IReadOnlyList<string> HeifBrands = ["heic", "heix", "mif1", "msf1"];

    /// <summary>
    /// <paramref name="bytes"/> read: on success the kind, a picture's attachment (named <c>phone</c> and the sniffed extension, never the phone's
    /// name) and the sniffed media type, with a 0 status; else the status and the page's words.
    /// </summary>
    public static (WebAttachKind Kind, ImageAttachment? Picture, string MediaType, int Status, string? Message) Read(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (PdfFile.HasPdfHeader(bytes))
        {
            return (WebAttachKind.Pdf, null, PdfFile.MediaType, 0, null);
        }

        string? source = SourceType(bytes);
        bool heif = IsHeif(bytes);
        if (ImageFile.TryLoad(bytes, "phone" + ImageFile.ExtensionFor(source ?? "", bytes), out var picture, out var failure, photo: true) && picture is not null)
        {
            return (WebAttachKind.Picture, picture, source ?? picture.MediaType, 0, null);
        }

        // The failure kinds one by one, never a catch-all: over the bytes can't reach here (the body cap is the same 20 MB), so TooLarge is the pixels.
        return failure switch
        {
            ImageLoadFailure.TooLarge => (WebAttachKind.Picture, null, "", 422, WebUiText.AttachTooManyPixels),
            ImageLoadFailure.CouldNotRead or ImageLoadFailure.Unsupported or ImageLoadFailure.Unreadable when heif && !ImageFile.Extensions.Contains(".heic") =>
                (WebAttachKind.Picture, null, "", 422, WebUiText.AttachHeic),
            ImageLoadFailure.CouldNotRead or ImageLoadFailure.Unsupported or ImageLoadFailure.Unreadable when source is not null || heif =>
                (WebAttachKind.Picture, null, "", 422, WebUiText.AttachUnreadable),
            _ => (WebAttachKind.Picture, null, "", 415, WebUiText.AttachNotPicture),
        };
    }

    /// <summary>Whether <paramref name="bytes"/> open with an <c>ftyp</c> box naming a HEIF brand (bytes 4 to 11).</summary>
    public static bool IsHeif(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 12 && bytes[4..8].SequenceEqual("ftyp"u8) && HeifBrands.Contains(System.Text.Encoding.ASCII.GetString(bytes[8..12]));

    /// <summary>A picture container's media type by its first bytes (JPEG, PNG, GIF, WebP, BMP, a HEIF brand); null for anything else.</summary>
    public static string? SourceType(ReadOnlySpan<byte> bytes) =>
        bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]) ? ImageFile.Jpeg
        : bytes.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]) ? ImageFile.Png
        : bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8) ? "image/gif"
        : bytes.Length >= 12 && bytes.StartsWith("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8) ? "image/webp"
        : bytes.StartsWith("BM"u8) ? "image/bmp"
        : IsHeif(bytes) ? "image/heic"
        : null;

    /// <summary>The chip's small picture as a data URL (<see cref="PictureSize.Chip"/>), or null when the codecs won't shrink it.</summary>
    public static string? Thumb(ImageAttachment picture)
    {
        ArgumentNullException.ThrowIfNull(picture);
        return PictureServer.Encode(picture.Bytes, PictureSize.Chip) is { } chip
            ? "data:" + chip.ContentType + ";base64," + Convert.ToBase64String(chip.Bytes)
            : null;
    }
}
