namespace NeonSidekick.Images;

/// <summary>
/// Where <c>image_edit</c> writes and in what format (2026-10-04), the <c>PdfEngine.OutputFor</c> shape: pure, the sandbox asked only
/// whether a path is a folder.
/// </summary>
public static class ImageOutput
{
    /// <summary>What a new name gets after the source's stem when the pixels change: <c>photo-edited.png</c>. Pinned.</summary>
    public const string EditedSuffix = "-edited";

    /// <summary>
    /// The format to write: <paramref name="format"/> when given, else the one <paramref name="to"/>'s extension names (when <c>to</c>
    /// names a file), else the source's when this Windows writes it, else PNG — so a WebP source comes out as a lossless PNG. Pure
    /// apart from <paramref name="canWrite"/>.
    /// </summary>
    public static ImageFormat FormatFor(ImageFormat? format, string? to, bool toIsFolder, string? sourceMimeType, Func<ImageFormat, bool> canWrite)
    {
        ArgumentNullException.ThrowIfNull(canWrite);
        if (format is not null)
        {
            return format;
        }

        if (!toIsFolder && ImageFormats.ByExtension(to) is { } named)
        {
            return named;
        }

        return ImageFormats.ByMimeType(sourceMimeType) is { } source && canWrite(source) ? source : ImageFormats.Png;
    }

    /// <summary>
    /// The output's path, relative to the working directory, or the refusal:
    /// <list type="bullet">
    /// <item><paramref name="to"/> naming a file is kept, the format's extension added when it has none; an extension of another
    /// format is refused, an unknown one too.</item>
    /// <item><paramref name="to"/> naming a folder, or none: the source's stem in that folder — or in <paramref name="defaultFolder"/>
    /// (<c>Image edit output folder</c>), or beside the source when that is empty — as <c>photo.png</c> for a bare format change
    /// (<paramref name="onlyFormatChanged"/>) and <c>photo-edited.png</c> otherwise.</item>
    /// </list>
    /// </summary>
    public static (string? Path, string? Error) OutputFor(string? to, bool toIsFolder, string sourceRelative, ImageFormat format, bool onlyFormatChanged, string? defaultFolder)
    {
        ArgumentNullException.ThrowIfNull(sourceRelative);
        ArgumentNullException.ThrowIfNull(format);
        string target = (to ?? "").Trim();
        if (!IntoFolder(target, toIsFolder))
        {
            string extension = Path.GetExtension(target);
            if (extension.Length == 0)
            {
                return (target + format.Extension, null);
            }

            if (ImageFormats.ByExtension(target) is not { } named)
            {
                return (null, ImageText.UnknownFormat(target));
            }

            return named == format ? (target, null) : (null, ImageText.FormatExtensionClash(target, format));
        }

        return (InFolder(target, sourceRelative, (onlyFormatChanged ? "" : EditedSuffix) + format.Extension, defaultFolder), null);
    }

    /// <summary>
    /// A lossless strip's output (2026-10-05): <paramref name="to"/> naming a file is kept (<paramref name="extension"/> added when it
    /// has none; the caller has checked any other is the container's), else <c>photo-edited.jpg</c> in the folder <see cref="OutputFor"/>
    /// would pick — the source's own extension kept, so a WebP stays a WebP. Pure.
    /// </summary>
    public static string LosslessOutputFor(string? to, bool toIsFolder, string sourceRelative, string extension, string? defaultFolder)
    {
        ArgumentNullException.ThrowIfNull(sourceRelative);
        ArgumentNullException.ThrowIfNull(extension);
        string target = (to ?? "").Trim();
        if (!IntoFolder(target, toIsFolder))
        {
            return Path.GetExtension(target).Length == 0 ? target + extension : target;
        }

        return InFolder(target, sourceRelative, EditedSuffix + extension, defaultFolder);
    }

    /// <summary>Whether <paramref name="to"/> means a folder (none, <c>.</c>, a trailing slash, or one that exists) rather than a file. Pure.</summary>
    public static bool IntoFolder(string? to, bool toIsFolder)
    {
        string target = (to ?? "").Trim();
        return target.Length == 0 || target is "." || target.EndsWith('/') || target.EndsWith('\\') || toIsFolder;
    }

    // The source's stem and tail in the folder named by target, else defaultFolder, else beside the source.
    private static string InFolder(string target, string sourceRelative, string tail, string? defaultFolder)
    {
        string folder = target.Length > 0
            ? target
            : !string.IsNullOrWhiteSpace(defaultFolder)
                ? defaultFolder.Trim()
                : Path.GetDirectoryName(sourceRelative)?.Replace('\\', '/') ?? "";
        folder = folder is "." ? "" : folder.Replace('\\', '/').TrimEnd('/');
        string stem = Path.GetFileNameWithoutExtension(sourceRelative);
        return (folder.Length == 0 ? "" : folder + "/") + stem + tail;
    }

    /// <summary>The <paramref name="n"/>th name to try for <paramref name="path"/> on a clash: the path itself first, then <c>photo-edited-2.png</c>, <c>-3</c>… Pure.</summary>
    public static string Numbered(string path, int n)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (n <= 1)
        {
            return path;
        }

        string extension = Path.GetExtension(path);
        return path[..^extension.Length] + "-" + n.ToString(System.Globalization.CultureInfo.InvariantCulture) + extension;
    }

    /// <summary>Whether two relative paths name the same file (case-insensitive, either slash). Pure.</summary>
    public static bool SamePath(string a, string b) =>
        string.Equals(a.Replace('\\', '/').Trim('/'), b.Replace('\\', '/').Trim('/'), StringComparison.OrdinalIgnoreCase);
}
