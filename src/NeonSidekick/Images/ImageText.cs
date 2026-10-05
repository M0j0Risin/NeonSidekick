using System.Drawing;
using System.Globalization;
using NeonSidekick.Files;
using PhotoSauce.MagicScaler;

namespace NeonSidekick.Images;

/// <summary>
/// The sentences <c>image_info</c> and <c>image_edit</c> answer with (2026-10-04): pure statics, pinned. Every error starts with
/// <c>Error:</c>, as the file tools' do. Invariant culture throughout.
/// </summary>
public static class ImageText
{
    public const string SourceTooBig = "Error: the picture is over 200 MB or 100 megapixels; too large to edit";
    public const string NotAnImage = "Error: the file could not be read as a picture";
    public const string ScaleAndSize = "Error: give scale, or width and/or height, not both";
    public const string NothingToDo = "Error: nothing to do: give a size, crop, rotate, flip, a colour step, pad, or a format to convert to";
    public const string NoPathError = "Error: give path (one picture) or paths (several)";
    public const string CropIncomplete = "Error: a crop needs all four of crop_x, crop_y, crop_width and crop_height";
    public const string OverwriteSource = "Error: that would write over the source picture; pass overwrite: true to replace it, or give another to";

    /// <summary>
    /// <c>image_edit</c>'s description's last sentence while <c>Image edit mode</c> is <c>overwrite-original</c> (later on 2026-10-04,
    /// the user's call). Pinned.
    /// </summary>
    public const string OverwriteModeSentence =
        "The user's Image edit mode is overwrite-original: without to, the result replaces the source picture, and a format change writes name.newext and deletes the source.";

    /// <summary>A menu edit on a picture that is no longer there (2026-10-04, the picture windows' menu). Pinned.</summary>
    public static string Gone(string name) => "Error: " + name + " is no longer there";

    /// <summary>A Fit preset on a picture already within it: nothing written. Pinned.</summary>
    public static string AlreadyFits(int side, int width, int height) =>
        "Error: the picture is " + Size(width, height) + ", already within " + N(side) + " px; nothing was written";

    /// <summary>A conversion to the format the picture already has: nothing written. Pinned.</summary>
    public static string AlreadyFormat(ImageFormat format) => "Error: the picture is already " + format.Label + "; nothing was written";

    /// <summary>A size cap the file is already under: nothing written. Pinned.</summary>
    public static string AlreadyUnder(int kb, long bytes) =>
        "Error: the file is " + FileText.Size(bytes) + ", already under " + N(kb) + " KB; nothing was written";

    /// <summary><see cref="Written"/>'s verb for a picture that replaced its source (Image edit mode overwrite-original). Pinned.</summary>
    public const string ReplacedVerb = "replaced";

    /// <summary>What a converted picture's line adds once its source is deleted (overwrite-original). Pinned.</summary>
    public static string SourceDeleted(string relative) => "; deleted " + relative + " (Image edit mode: overwrite-original)";

    /// <summary>What a converted picture's line adds when its source could not be deleted: both are left. Pinned.</summary>
    public static string SourceKept(string relative, string error) => "; " + relative + " was kept: " + error;

    /// <summary>What a written picture's line adds when the source had more frames than the one written. Pinned.</summary>
    public static string FirstOf(int frames) => "first of " + N(frames) + " frames";

    public static string CropOutside(Rectangle crop, int width, int height) =>
        "Error: the crop " + N(crop.X) + "," + N(crop.Y) + " " + N(crop.Width) + "×" + N(crop.Height) + " is not inside the picture, which is " + N(width) + "×" + N(height);

    public static string OutputTooBig(int width, int height) =>
        "Error: the result would be " + N(width) + "×" + N(height) + "; at most " + N(ImageEditor.MaxSide) + " a side and " + N(ImageEditor.MaxPixels / 1_000_000) + " megapixels";

    public static string NoEncoder(ImageFormat format, IReadOnlyList<ImageFormat> writable) =>
        "Error: this Windows has no " + format.Label + " encoder; it can write " + Names(writable);

    public static string OptionNotFor(string option, ImageFormat format, string formats) =>
        "Error: " + option + " does not apply to " + format.Label + "; it is for " + formats;

    public static string CannotFit(long maxKb, long smallest, ImageFormat format) =>
        "Error: could not get under " + N(maxKb) + " KB; the smallest tried was " + FileText.Size(smallest) + (format.Lossy ? "" : ". Try colors (fewer colours) or a lossy format such as jpeg") + "; nothing was written";

    public static string Failed(string detail) => "Error: the picture could not be edited: " + detail;

    public static string FormatExtensionClash(string to, ImageFormat format) =>
        "Error: to '" + to + "' has another extension than " + format.Label + " (" + format.Extension + "); give a matching name or leave format out";

    public static string UnknownFormat(string to) =>
        "Error: to '" + to + "' has an extension no picture format here writes; give format or use one of " + string.Join(", ", ImageFormats.All.Select(f => f.Extension));

    public static string OutOfRange(string argument, string given, double min, double max) =>
        "Error: '" + given.Trim() + "' is not between " + N(min) + " and " + N(max) + " for '" + argument + "'";

    public static string BadColour(string argument, string given) =>
        "Error: '" + given.Trim() + "' is not a colour for '" + argument + "'; give #rrggbb, #aarrggbb or a name such as white, black or transparent";

    public static string Rotation(string given) => "Error: '" + given.Trim() + "' is not 0, 90, 180 or 270 for 'rotate'; only quarter turns";

    /// <summary>
    /// A written picture: <c>wrote photos/cat-edited.png (PNG, 1024×768 from 4032×3024, 812 KB)</c>, then what <c>max_kb</c> took and the
    /// frame note when they apply.
    /// </summary>
    public static string Written(string relative, ImageEditResult result, int? maxKb = null, string verb = "wrote")
    {
        ArgumentNullException.ThrowIfNull(result);
        var sb = new System.Text.StringBuilder(verb).Append(' ');
        sb.Append(relative).Append(" (").Append(result.Format.Label).Append(", ").Append(Size(result.Width, result.Height));
        if (result.Width != result.SourceWidth || result.Height != result.SourceHeight)
        {
            sb.Append(" from ").Append(Size(result.SourceWidth, result.SourceHeight));
        }

        sb.Append(", ").Append(FileText.Size(result.Bytes.LongLength)).Append(')');
        if (result.FitTried && maxKb is { } kb)
        {
            sb.Append("; under ").Append(N(kb)).Append(" KB");
            if (result.Quality is { } q)
            {
                sb.Append(" at quality ").Append(N(q));
            }

            if (result.Shrunk)
            {
                sb.Append(", made smaller to fit");
            }
        }

        if (result.Frames > 1)
        {
            sb.Append("; ").Append(FirstOf(result.Frames));
        }

        return sb.ToString();
    }

    /// <summary>One <c>image_info</c> line: <c>cat.jpg: JPEG, 4032×3024, 3.2 MB, EXIF rotate 90 (applied when edited)</c>.</summary>
    public static string Info(string relative, ImageInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        var parts = new List<string> { info.Label, Size(info.Width, info.Height), FileText.Size(info.Bytes) };
        if (info.Frames > 1)
        {
            parts.Add(N(info.Frames) + " frames");
        }

        if (info.HasAlpha)
        {
            parts.Add("transparency");
        }

        if (info.Orientation != Orientation.Normal)
        {
            parts.Add("EXIF orientation " + OrientationName(info.Orientation) + " (the size shown is upright; an edit bakes it in)");
        }

        return relative + ": " + string.Join(", ", parts);
    }

    /// <summary>The line after the pictures: what this Windows writes, and the defaults in force.</summary>
    public static string Formats(IReadOnlyList<ImageFormat> writable, int quality, string metadata, string folder) =>
        "image_edit can write " + Names(writable) + " (WebP and AVIF read but never write). Defaults: quality " + N(quality) + ", metadata " + metadata
        + ", output " + (string.IsNullOrWhiteSpace(folder) ? "beside the source" : "into " + folder.Trim());

    private static string OrientationName(Orientation orientation) => orientation switch
    {
        Orientation.Rotate90 => "rotate 90",
        Orientation.Rotate180 => "rotate 180",
        Orientation.Rotate270 => "rotate 270",
        Orientation.FlipHorizontal => "mirrored",
        Orientation.FlipVertical => "flipped",
        Orientation.Transpose => "transposed",
        Orientation.Transverse => "transversed",
        _ => "normal",
    };

    private static string Names(IReadOnlyList<ImageFormat> formats) => formats.Count == 0 ? "nothing" : string.Join(", ", formats.Select(f => f.Name));

    private static string Size(int width, int height) => N(width) + "×" + N(height);

    private static string N(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string N(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
