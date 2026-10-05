using System.Collections.Concurrent;
using PhotoSauce.MagicScaler;

namespace NeonSidekick.Images;

/// <summary>One picture format <c>image_edit</c> may write: its name as the model gives it, its MIME type, and its extensions (the first the one a new file gets).</summary>
public sealed record ImageFormat(string Name, string MimeType, string[] Extensions, bool Lossy)
{
    /// <summary>The extension a new file of this format gets, with the dot.</summary>
    public string Extension => Extensions[0];

    /// <summary>The name shown in a result: <c>PNG</c>, <c>JPEG</c>.</summary>
    public string Label => Name.ToUpperInvariant();
}

/// <summary>
/// The formats <c>image_edit</c> knows (2026-10-04, the user's pick: Windows' own WIC codecs only, no new native DLL). Reading
/// takes whatever WIC decodes (WebP and AVIF too, when Windows has their codecs); writing takes what WIC can encode, which
/// <see cref="CanWrite"/> finds out once per run by encoding a 1×1 picture and reading back what came out. Windows ships no
/// WebP or AVIF encoder, so those two read but never write. JPEG XL and HEIF write when their Store extensions are installed.
/// </summary>
public static class ImageFormats
{
    public static readonly ImageFormat Png = new("png", "image/png", [".png"], Lossy: false);
    public static readonly ImageFormat Jpeg = new("jpeg", "image/jpeg", [".jpg", ".jpeg"], Lossy: true);
    public static readonly ImageFormat Gif = new("gif", "image/gif", [".gif"], Lossy: false);
    public static readonly ImageFormat Bmp = new("bmp", "image/bmp", [".bmp"], Lossy: false);
    public static readonly ImageFormat Tiff = new("tiff", "image/tiff", [".tif", ".tiff"], Lossy: false);
    public static readonly ImageFormat Jxl = new("jxl", "image/jxl", [".jxl"], Lossy: true);
    public static readonly ImageFormat Heif = new("heif", "image/heic", [".heic", ".heif"], Lossy: true);

    /// <summary>Every format in the order a list names them. Pinned.</summary>
    public static readonly IReadOnlyList<ImageFormat> All = [Png, Jpeg, Gif, Bmp, Tiff, Jxl, Heif];

    /// <summary>The choices <c>format</c> takes, for the schema and <c>FileText.BadChoice</c>. Pinned.</summary>
    public static readonly string Choices = string.Join(", ", All.Select(f => f.Name));

    private static readonly ConcurrentDictionary<string, bool> Writable = new(StringComparer.Ordinal);

    /// <summary>A format by its name (<c>jpg</c> and <c>tif</c> too, any case, a leading dot ignored); null for anything else. Pure.</summary>
    public static ImageFormat? ByName(string? name)
    {
        string key = (name ?? "").Trim().TrimStart('.').ToLowerInvariant();
        return key switch
        {
            "" => null,
            "jpg" => Jpeg,
            "tif" => Tiff,
            "heic" => Heif,
            _ => All.FirstOrDefault(f => f.Name == key),
        };
    }

    /// <summary>The format a path's extension names; null for none or one this table does not know. Pure.</summary>
    public static ImageFormat? ByExtension(string? path)
    {
        string extension = Path.GetExtension(path ?? "");
        return extension.Length == 0 ? null : All.FirstOrDefault(f => f.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>The format a decoder's MIME type names (<c>ImageFileInfo.MimeType</c>); null for one this table does not write (WebP, AVIF). Pure.</summary>
    public static ImageFormat? ByMimeType(string? mimeType) =>
        All.FirstOrDefault(f => string.Equals(f.MimeType, mimeType, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether this Windows can encode <paramref name="format"/>: a 1×1 picture encoded and read back, the answer kept for the
    /// run. A MIME type with no encoder can still be "set" (the pipeline may pick another encoder), so the probe checks the
    /// bytes that came out are that format.
    /// </summary>
    public static bool CanWrite(ImageFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        return Writable.GetOrAdd(format.Name, _ => Probe(format));
    }

    /// <summary>The formats this Windows can write, in <see cref="All"/>'s order.</summary>
    public static IReadOnlyList<ImageFormat> WritableFormats() => All.Where(CanWrite).ToList();

    private static bool Probe(ImageFormat format)
    {
        try
        {
            var settings = new ProcessImageSettings { Width = 1, Height = 1, ResizeMode = CropScaleMode.Stretch };
            if (!settings.TrySetEncoderFormat(format.MimeType))
            {
                return false;
            }

            using var output = new MemoryStream();
            _ = MagicImageProcessor.ProcessImage(App.SmokeChecks.SolidBmp(1, 1), output, settings);
            var info = ImageFileInfo.Load(output.ToArray());
            return string.Equals(info.MimeType, format.MimeType, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return false;
        }
    }
}
