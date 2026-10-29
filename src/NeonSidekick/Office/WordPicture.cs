using NeonSidekick.Images;

namespace NeonSidekick.Office;

/// <summary>
/// A picture ready to go in a Word document (2026-10-29, the Office writing plan): its bytes with no metadata, its format and its upright
/// size in pixels. <see cref="From"/> is the one door: the format judged from the bytes, a turned photo's pixels turned, the rest stripped.
/// </summary>
public sealed record WordPicture(byte[] Bytes, ImageFormat Format, int Width, int Height)
{
    /// <summary>
    /// <paramref name="bytes"/> made fit for a document, or null for the placeholder. Only PNG, JPEG and GIF, judged from the bytes
    /// (<see cref="ImageEditor.Info"/>), so a <c>.png</c> that is really WebP is refused. Word doesn't apply a JPEG's EXIF orientation, so a
    /// picture turned by it (<see cref="MetadataStripper.ExifOrientation"/> above 1) is re-encoded upright through
    /// <see cref="ImageEditor.Apply"/>, whose request keeps no metadata; every other picture goes through the lossless
    /// <see cref="MetadataStripper.Strip"/> (GPS, camera and maker data out: a document is made to be shared), re-encoded if the strip
    /// refuses. Past <c>Apply</c>'s own limits, or unreadable, it is null.
    /// </summary>
    public static WordPicture? From(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (ImageEditor.Info(bytes) is not { Width: > 0, Height: > 0 } info
            || ImageFormats.ByMimeType(info.MimeType) is not { } format
            || (format != ImageFormats.Png && format != ImageFormats.Jpeg && format != ImageFormats.Gif))
        {
            return null;
        }

        if ((MetadataStripper.ExifOrientation(bytes) ?? 1) <= 1 && MetadataStripper.Strip(bytes).Result is { } stripped)
        {
            return new WordPicture(stripped.Bytes, format, info.Width, info.Height);
        }

        if (bytes.LongLength > ImageEditor.MaxSourceBytes || (long)info.Width * info.Height > ImageEditor.MaxPixels)
        {
            return null;
        }

        var (made, _) = ImageEditor.Apply(bytes, new ImageEditRequest(), format);
        return made is null ? null : new WordPicture(made.Bytes, made.Format, made.Width, made.Height);
    }
}
