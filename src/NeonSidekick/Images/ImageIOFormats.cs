using PhotoSauce.MagicScaler;

namespace NeonSidekick.Images;

/// <summary>
/// One format Apple's ImageIO reads or writes, as MagicScaler's codec registry sees it (2026-10-07, pictures on macOS): the
/// uniform type identifier ImageIO names it by, the MIME types and extensions MagicScaler matches an encoder by (the first MIME
/// type is the one a decoded picture reports), and the pixel formats its encoder takes — none for a format ImageIO only reads.
/// </summary>
public sealed record ImageIOFormat(string Uti, string[] MimeTypes, string[] Extensions, Guid[] EncoderPixelFormats)
{
    /// <summary>Whether ImageIO writes this format at all (it may still be missing on an older macOS: <c>ImageIOCodecs</c> asks).</summary>
    public bool Writes => EncoderPixelFormats.Length > 0;

    /// <summary>The MIME type a decoded picture of this format reports.</summary>
    public string MimeType => MimeTypes[0];
}

/// <summary>
/// The pure half of the macOS picture backend (2026-10-07): MagicScaler's pipeline is managed code and runs anywhere, only its
/// codecs are Windows' own (WIC), so on a Mac <c>ImageIOCodecs</c> registers Apple's ImageIO as MagicScaler's decoder and
/// encoders, and every caller (<c>ImageFile</c>, <c>ImageThumbnail</c>, <c>ImageEditor</c>, <c>CameraJpeg</c>) takes the road it
/// takes on Windows — the same scaler, sharpening, blur and colour matrices, so a Mac edit looks like a Windows one by
/// construction. This file holds what that backend decides without calling ImageIO, so the tests cover it on every OS: the
/// format table, the magic bytes MagicScaler routes a stream by, the option mapping, which source metadata a policy keeps, and
/// the pixel shuffles at the boundary.
/// </summary>
public static class ImageIOFormats
{
    private static readonly Guid[] Opaque = [PixelFormats.Bgr24bpp, PixelFormats.Grey8bpp];
    private static readonly Guid[] WithAlpha = [PixelFormats.Bgra32bpp, PixelFormats.Bgr24bpp, PixelFormats.Grey8bpp];

    public static readonly ImageIOFormat Png = new("public.png", ["image/png"], [".png"], WithAlpha);

    /// <summary>JPEG and BMP take no alpha: MagicScaler flattens a transparent picture onto the matte first, as it does for WIC's.</summary>
    public static readonly ImageIOFormat Jpeg = new("public.jpeg", ["image/jpeg"], [".jpg", ".jpeg"], Opaque);

    public static readonly ImageIOFormat Gif = new("com.compuserve.gif", ["image/gif"], [".gif"], WithAlpha);
    public static readonly ImageIOFormat Bmp = new("com.microsoft.bmp", ["image/bmp"], [".bmp"], Opaque);
    public static readonly ImageIOFormat Tiff = new("public.tiff", ["image/tiff"], [".tif", ".tiff"], WithAlpha);

    /// <summary>
    /// HEIC, what an iPhone shoots: <c>image/heic</c> first since that is <see cref="ImageFormats.Heif"/>'s MIME type, the one
    /// <c>image_edit</c>'s writable probe reads back.
    /// </summary>
    public static readonly ImageIOFormat Heic = new("public.heic", ["image/heic", "image/heif"], [".heic", ".heif"], WithAlpha);

    /// <summary>A HEIF that is not HEVC-coded inside (<c>mif1</c> without <c>heic</c>): read only.</summary>
    public static readonly ImageIOFormat Heif = new("public.heif", ["image/heif"], [".heif"], []);

    public static readonly ImageIOFormat WebP = new("org.webmproject.webp", ["image/webp"], [".webp"], []);
    public static readonly ImageIOFormat Avif = new("public.avif", ["image/avif"], [".avif"], []);
    public static readonly ImageIOFormat Jxl = new("public.jpeg-xl", ["image/jxl"], [".jxl"], []);

    /// <summary>Every format the backend knows, the writers first. Pinned.</summary>
    public static readonly IReadOnlyList<ImageIOFormat> All = [Png, Jpeg, Gif, Bmp, Tiff, Heic, Heif, WebP, Avif, Jxl];

    /// <summary>A format by ImageIO's type identifier (<c>CGImageSourceGetType</c>); null for one the table does not know. Pure.</summary>
    public static ImageIOFormat? ByUti(string? uti) => All.FirstOrDefault(f => string.Equals(f.Uti, uti, StringComparison.Ordinal));

    /// <summary>
    /// The MIME type a decoded picture reports for ImageIO's type identifier: the table's, else null — an image ImageIO reads that
    /// the app has no name for (a camera RAW, a PSD) still decodes, and <c>ImageInfo.Label</c> says <c>unknown</c>. Pure.
    /// </summary>
    public static string? MimeTypeOf(string? uti) => ByUti(uti)?.MimeType;

    /// <summary>The ISO media brands (at offset 8, after <c>ftyp</c>) that mean a picture ImageIO reads. Declared before <see cref="Patterns"/>, which is built from it.</summary>
    public static readonly IReadOnlyList<string> Brands = ["heic", "heix", "hevc", "hevx", "heim", "heis", "mif1", "msf1", "avif", "avis"];

    /// <summary>
    /// The magic bytes MagicScaler hands a stream to ImageIO by (offset, bytes, mask), one decoder for all of them: PNG, JPEG,
    /// GIF, BMP, TIFF either byte order, WebP's <c>RIFF....WEBP</c>, the ISO media <c>ftyp</c> brands of HEIC/HEIF/AVIF, and JPEG
    /// XL bare or boxed. Bytes nothing here matches never reach ImageIO, and MagicScaler's "no decoder" is the caller's "could not be
    /// read". Pinned.
    /// </summary>
    public static IReadOnlyList<(int Offset, byte[] Signature, byte[] Mask)> Patterns { get; } = BuildPatterns();

    private static List<(int, byte[], byte[])> BuildPatterns()
    {
        static byte[] Ones(int length) => Enumerable.Repeat((byte)0xFF, length).ToArray();
        var patterns = new List<(int, byte[], byte[])>
        {
            (0, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], Ones(8)),
            (0, [0xFF, 0xD8, 0xFF], Ones(3)),
            (0, "GIF8"u8.ToArray(), Ones(4)),
            (0, "BM"u8.ToArray(), Ones(2)),
            (0, "II*\0"u8.ToArray(), Ones(4)),
            (0, "MM\0*"u8.ToArray(), Ones(4)),
            (0, [.. "RIFF"u8, 0, 0, 0, 0, .. "WEBP"u8], [0xFF, 0xFF, 0xFF, 0xFF, 0, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF]),
            (0, [0xFF, 0x0A], Ones(2)),
            (0, [0, 0, 0, 0x0C, .. "JXL "u8, 0x0D, 0x0A, 0x87, 0x0A], Ones(12)),
        };
        foreach (string brand in Brands)
        {
            patterns.Add((4, [.. "ftyp"u8, .. System.Text.Encoding.ASCII.GetBytes(brand)], Ones(8)));
        }

        return patterns;
    }

    /// <summary>
    /// ImageIO's lossy compression quality (0–1) for MagicScaler's JPEG-normalised 1–100; null for 0, MagicScaler's "automatic",
    /// which leaves ImageIO's own default. Linear: ImageIO's JPEG at 0.85 is close to libjpeg's 85, as WIC's is. Pure.
    /// </summary>
    public static double? Compression(int quality) => quality <= 0 ? null : Math.Clamp(quality, 1, 100) / 100.0;

    /// <summary>
    /// What the ImageIO encoder takes from MagicScaler's encoder options: the quality of any lossy options (<c>JpegEncoderOptions</c>
    /// from <c>ImageFile</c> and <c>CameraJpeg</c>), PNG's interlace, and everything from <see cref="ImageIOEncoderOptions"/>
    /// (<c>ImageEditor</c>'s on a Mac). Pure.
    /// </summary>
    public static ImageIOEncoderOptions Read(IEncoderOptions? options) => options switch
    {
        ImageIOEncoderOptions mine => mine,
        ILossyEncoderOptions lossy => new ImageIOEncoderOptions(lossy.Quality),
        IPngEncoderOptions png => new ImageIOEncoderOptions(Interlace: png.Interlace),
        _ => new ImageIOEncoderOptions(),
    };

    /// <summary>
    /// Where each of <c>image_edit</c>'s metadata names (WIC's photo-metadata policy names, <see cref="ImageEditor.BasicMetadata"/>
    /// and <see cref="ImageEditor.GpsMetadata"/>) lives in ImageIO's properties: the exported names of the sub-dictionary key and
    /// the property key, read with <c>NativeLibrary.GetExport</c>. <c>System.Subject</c> has no ImageIO property (it is Windows'
    /// XPSubject) and is left out. Pinned.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, (string Dictionary, string Key)> MetadataKeys = new Dictionary<string, (string, string)>(StringComparer.Ordinal)
    {
        ["System.Author"] = ("kCGImagePropertyTIFFDictionary", "kCGImagePropertyTIFFArtist"),
        ["System.Copyright"] = ("kCGImagePropertyTIFFDictionary", "kCGImagePropertyTIFFCopyright"),
        ["System.Title"] = ("kCGImagePropertyTIFFDictionary", "kCGImagePropertyTIFFImageDescription"),
        ["System.Comment"] = ("kCGImagePropertyExifDictionary", "kCGImagePropertyExifUserComment"),
        ["System.Keywords"] = ("kCGImagePropertyIPTCDictionary", "kCGImagePropertyIPTCKeywords"),
        ["System.Photo.DateTaken"] = ("kCGImagePropertyExifDictionary", "kCGImagePropertyExifDateTimeOriginal"),
        ["System.Photo.CameraManufacturer"] = ("kCGImagePropertyTIFFDictionary", "kCGImagePropertyTIFFMake"),
        ["System.Photo.CameraModel"] = ("kCGImagePropertyTIFFDictionary", "kCGImagePropertyTIFFModel"),
        ["System.Photo.ExposureTime"] = ("kCGImagePropertyExifDictionary", "kCGImagePropertyExifExposureTime"),
        ["System.Photo.FNumber"] = ("kCGImagePropertyExifDictionary", "kCGImagePropertyExifFNumber"),
        ["System.Photo.ISOSpeed"] = ("kCGImagePropertyExifDictionary", "kCGImagePropertyExifISOSpeedRatings"),
        ["System.Photo.FocalLength"] = ("kCGImagePropertyExifDictionary", "kCGImagePropertyExifFocalLength"),
        ["System.GPS.Latitude"] = ("kCGImagePropertyGPSDictionary", "kCGImagePropertyGPSLatitude"),
        ["System.GPS.LatitudeRef"] = ("kCGImagePropertyGPSDictionary", "kCGImagePropertyGPSLatitudeRef"),
        ["System.GPS.Longitude"] = ("kCGImagePropertyGPSDictionary", "kCGImagePropertyGPSLongitude"),
        ["System.GPS.LongitudeRef"] = ("kCGImagePropertyGPSDictionary", "kCGImagePropertyGPSLongitudeRef"),
        ["System.GPS.Altitude"] = ("kCGImagePropertyGPSDictionary", "kCGImagePropertyGPSAltitude"),
        ["System.GPS.AltitudeRef"] = ("kCGImagePropertyGPSDictionary", "kCGImagePropertyGPSAltitudeRef"),
    };

    /// <summary>The (sub-dictionary, key) pairs a list of metadata names copies, unmapped names dropped, each pair once. Pure.</summary>
    public static IReadOnlyList<(string Dictionary, string Key)> KeysFor(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        return names.Where(MetadataKeys.ContainsKey).Select(n => MetadataKeys[n]).Distinct().ToList();
    }

    /// <summary>
    /// The EXIF block a decoded frame offers MagicScaler for ImageIO's resolved orientation (<c>kCGImagePropertyOrientation</c>,
    /// which for HEIF folds in its <c>irot</c>/<c>imir</c> boxes): the bare TIFF <see cref="MetadataStripper.OrientationTiff"/>
    /// writes, so MagicScaler turns the stored pixels upright itself, as it does with WIC's; null for upright or a value out of
    /// EXIF's 1–8. Pure.
    /// </summary>
    public static byte[]? OrientationExif(int orientation) => orientation is > 1 and <= 8 ? MetadataStripper.OrientationTiff(orientation) : null;

    /// <summary>
    /// Whether a CGImage's <c>CGImageAlphaInfo</c> carries alpha: premultiplied or straight, first or last, or alpha only — not
    /// none or a skipped byte. A picture without is offered as BGR, so MagicScaler (and <c>ImageFile</c>'s photo rule) sees it as
    /// opaque, as WIC's are. Pure.
    /// </summary>
    public static bool HasAlpha(uint alphaInfo) => (alphaInfo & 0x1F) is 1 or 2 or 3 or 4 or 7;

    /// <summary>Four-byte BGRX rows to three-byte BGR, the X dropped (an opaque decode, offered as <c>Bgr24bpp</c>). Pure.</summary>
    public static void BgrxToBgr(ReadOnlySpan<byte> bgrx, Span<byte> bgr, int pixels)
    {
        for (int i = 0; i < pixels; i++)
        {
            bgr[i * 3] = bgrx[i * 4];
            bgr[(i * 3) + 1] = bgrx[(i * 4) + 1];
            bgr[(i * 3) + 2] = bgrx[(i * 4) + 2];
        }
    }

    /// <summary>Three-byte BGR to four-byte BGRX, X 255 (what CoreGraphics takes for an opaque picture). Pure.</summary>
    public static void BgrToBgrx(ReadOnlySpan<byte> bgr, Span<byte> bgrx, int pixels)
    {
        for (int i = 0; i < pixels; i++)
        {
            bgrx[i * 4] = bgr[i * 3];
            bgrx[(i * 4) + 1] = bgr[(i * 3) + 1];
            bgrx[(i * 4) + 2] = bgr[(i * 3) + 2];
            bgrx[(i * 4) + 3] = 0xFF;
        }
    }
}

/// <summary>
/// The ImageIO encoder's options (2026-10-07): the lossy quality (1–100, 0 for ImageIO's default), PNG interlace, the resolution
/// in dots per inch (0 to leave it), and the metadata names to copy from the source (<see cref="ImageEditor.MetadataNamesOf"/>).
/// <c>ImageEditor</c> passes these on a Mac; MagicScaler's own option types are read through <see cref="ImageIOFormats.Read"/>.
/// </summary>
public sealed record ImageIOEncoderOptions(int Quality = 0, bool Interlace = false, double Dpi = 0, IReadOnlyList<string>? MetadataNames = null) : IEncoderOptions;
