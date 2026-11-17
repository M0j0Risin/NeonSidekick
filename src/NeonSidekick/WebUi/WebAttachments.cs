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

    /// <summary>
    /// The session's attach epoch when it was held (2026-11-16, the review's #5): a give-back across a profile switch (the epoch bumped) is
    /// dropped, never held again under the new profile.
    /// </summary>
    public long Epoch { get; init; }

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
        bytes.Length >= 12 && bytes[4..8].SequenceEqual("ftyp"u8) && Images.ImageIOFormats.HeifBrands.Contains(System.Text.Encoding.ASCII.GetString(bytes[8..12]));

    /// <summary>
    /// A picture container's media type by its first bytes: what the picture server sniffs (PNG, JPEG, GIF, WebP, AVIF, BMP), else a HEIF brand's
    /// <c>image/heic</c>; null for anything else (2026-11-16, the review's #69: one sniffer, AVIF included).
    /// </summary>
    public static string? SourceType(ReadOnlySpan<byte> bytes) => PictureServer.Sniff(bytes) ?? (IsHeif(bytes) ? "image/heic" : null);

    /// <summary>The chip's small picture as a data URL (<see cref="PictureSize.Chip"/>), or null when the codecs won't shrink it.</summary>
    public static string? Thumb(ImageAttachment picture)
    {
        ArgumentNullException.ThrowIfNull(picture);
        return PictureServer.Encode(picture.Bytes, PictureSize.Chip) is { } chip
            ? "data:" + chip.ContentType + ";base64," + Convert.ToBase64String(chip.Bytes)
            : null;
    }
}
