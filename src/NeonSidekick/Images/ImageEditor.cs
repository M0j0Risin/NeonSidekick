using System.Drawing;
using PhotoSauce.MagicScaler;
using PhotoSauce.MagicScaler.Transforms;

namespace NeonSidekick.Images;

/// <summary>What <see cref="ImageEditor.Info"/> reads from a picture: the size as it displays (the EXIF orientation applied), and the rest.</summary>
public sealed record ImageInfo(string? MimeType, int Width, int Height, int Frames, bool HasAlpha, Orientation Orientation, long Bytes)
{
    /// <summary>The format's label for a sentence: the table's (<c>PNG</c>), else the MIME type's subtype (<c>WEBP</c>).</summary>
    public string Label => ImageFormats.ByMimeType(MimeType)?.Label ?? (MimeType is { } m && m.Contains('/') ? m[(m.IndexOf('/') + 1)..].ToUpperInvariant() : "unknown");
}

/// <summary>A picture written: its bytes and format, its size and the source's, and what <c>max_kb</c> took to get there.</summary>
/// <param name="Frames">The source's frame count; over one, only the first was written.</param>
/// <param name="Quality">The quality the bytes were encoded at; null for a lossless format.</param>
/// <param name="FitTried">Whether <c>max_kb</c> ran (the result then names what it took).</param>
public sealed record ImageEditResult(byte[] Bytes, ImageFormat Format, int Width, int Height, int SourceWidth, int SourceHeight, int Frames, int? Quality, bool FitTried, bool Shrunk);

/// <summary>
/// Where the pixels go, worked out before anything is decoded (pure, so the tests can pin it): the crop out of the upright
/// source, the size the scaler makes, the fill a <see cref="ImageFit.Pad"/> adds around it (top, right, bottom, left), and the
/// final size after the turn and the border. Every size before the turn is in the source's orientation.
/// </summary>
public sealed record ImageGeometry(Rectangle Crop, int ResizeWidth, int ResizeHeight, int FillTop, int FillRight, int FillBottom, int FillLeft, int FinalWidth, int FinalHeight);

/// <summary>
/// <c>image_edit</c>'s engine (2026-10-04): every step of an <see cref="ImageEditRequest"/> in one MagicScaler pipeline — decode (the
/// EXIF orientation applied), crop, resize, the pad fill, rotate, flip, the colour matrix, blur, the border, and one encode — so a
/// lossy format is encoded once. Over Windows' WIC codecs, no new native DLL. The first frame of an animated picture only. Never
/// throws for a bad picture: every refusal is an <see cref="ImageText"/> sentence. Pure apart from the codecs.
/// </summary>
public static class ImageEditor
{
    /// <summary>The largest source read, in bytes: well past a camera's raw-free JPEG, short of filling memory.</summary>
    public const long MaxSourceBytes = 200_000_000;

    /// <summary>The most pixels a source may decode to, or an output be.</summary>
    public const long MaxPixels = 100_000_000;

    /// <summary>The longest side an output may have.</summary>
    public const int MaxSide = 32_768;

    /// <summary>The quality a lossy format is written at when neither the call nor <c>Image edit quality</c> says.</summary>
    public const int DefaultQuality = 90;

    /// <summary>The tint strength when <c>tint</c> is given without <c>tint_amount</c>.</summary>
    public const int DefaultTintAmount = 50;

    /// <summary>The lowest quality <c>max_kb</c> goes to before it shrinks the picture.</summary>
    public const int MinFitQuality = 30;

    /// <summary>How much each <c>max_kb</c> shrink step keeps of each side.</summary>
    public const double FitShrinkStep = 0.8;

    /// <summary>The shorter side <c>max_kb</c> never shrinks below.</summary>
    public const int MinFitSide = 64;

    /// <summary>The most encodes one <c>max_kb</c> call makes.</summary>
    public const int MaxFitEncodes = 16;

    /// <summary>The firm unsharp mask <c>sharpen: true</c> applies.</summary>
    public static readonly UnsharpMaskSettings FirmSharpen = new(60, 1.0, 0);

    /// <summary>The policy names each metadata choice copies (WIC's photo-metadata policies). Orientation never: it is baked into the pixels.</summary>
    public static readonly IReadOnlyList<string> BasicMetadata =
    [
        "System.Author", "System.Copyright", "System.Title", "System.Subject", "System.Comment", "System.Keywords",
        "System.Photo.DateTaken", "System.Photo.CameraManufacturer", "System.Photo.CameraModel",
        "System.Photo.ExposureTime", "System.Photo.FNumber", "System.Photo.ISOSpeed", "System.Photo.FocalLength",
    ];

    /// <summary>What <see cref="ImageMetadataPolicy.All"/> adds to <see cref="BasicMetadata"/>: the GPS position.</summary>
    public static readonly IReadOnlyList<string> GpsMetadata =
    [
        "System.GPS.Latitude", "System.GPS.LatitudeRef", "System.GPS.Longitude", "System.GPS.LongitudeRef",
        "System.GPS.Altitude", "System.GPS.AltitudeRef",
    ];

    /// <summary>A picture's facts, or null when the codecs cannot read it.</summary>
    public static ImageInfo? Info(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        try
        {
            var info = ImageFileInfo.Load(bytes);
            if (info.Frames.Count == 0)
            {
                return null;
            }

            var frame = info.Frames[0];
            var (width, height) = Upright(frame.Width, frame.Height, frame.ExifOrientation);
            return new ImageInfo(info.MimeType, width, height, info.Frames.Count, frame.HasAlpha, frame.ExifOrientation, bytes.LongLength);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>The size a picture displays at: the stored size, the sides swapped for an orientation that turns it a quarter.</summary>
    public static (int Width, int Height) Upright(int width, int height, Orientation orientation) =>
        orientation is Orientation.Transpose or Orientation.Rotate90 or Orientation.Transverse or Orientation.Rotate270 ? (height, width) : (width, height);

    /// <summary>
    /// The geometry for <paramref name="request"/> over an upright source of <paramref name="sourceWidth"/>×<paramref name="sourceHeight"/>,
    /// or the refusal. <c>width</c>/<c>height</c>/<c>scale</c> always mean the final picture's sides (so a quarter turn swaps them for
    /// the scaler), and one side alone keeps the aspect. Pure.
    /// </summary>
    public static (ImageGeometry? Geometry, string? Error) Plan(int sourceWidth, int sourceHeight, ImageEditRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var region = new Rectangle(0, 0, sourceWidth, sourceHeight);
        if (request.Crop is { } crop)
        {
            if (crop.Width <= 0 || crop.Height <= 0 || crop.X < 0 || crop.Y < 0 || crop.Right > sourceWidth || crop.Bottom > sourceHeight)
            {
                return (null, ImageText.CropOutside(crop, sourceWidth, sourceHeight));
            }

            region = crop;
        }

        bool quarter = request.Rotate is 90 or 270;
        // The box in the source's orientation: the final sides swapped back for a quarter turn.
        int? boxWidth = quarter ? request.Height : request.Width;
        int? boxHeight = quarter ? request.Width : request.Height;
        int rw = region.Width, rh = region.Height;
        int fillTop = 0, fillRight = 0, fillBottom = 0, fillLeft = 0;
        var cut = region;
        if (request.Scale is { } scale)
        {
            rw = Side(region.Width * scale);
            rh = Side(region.Height * scale);
        }
        else if (boxWidth is { } bw && boxHeight is { } bh)
        {
            double fx = (double)bw / region.Width, fy = (double)bh / region.Height;
            switch (request.Fit)
            {
                case ImageFit.Stretch:
                    (rw, rh) = (bw, bh);
                    break;
                case ImageFit.Cover:
                {
                    double f = Math.Max(fx, fy);
                    int cw = Math.Min(region.Width, Math.Max(1, (int)Math.Round(bw / f))), ch = Math.Min(region.Height, Math.Max(1, (int)Math.Round(bh / f)));
                    var (ax, ay) = SourceAnchor(request.Anchor, request.Rotate, request.Flip);
                    cut = new Rectangle(region.X + Offset(region.Width - cw, ax), region.Y + Offset(region.Height - ch, ay), cw, ch);
                    (rw, rh) = (bw, bh);
                    break;
                }

                case ImageFit.Pad:
                {
                    double f = Math.Min(fx, fy);
                    (rw, rh) = (Math.Min(bw, Side(region.Width * f)), Math.Min(bh, Side(region.Height * f)));
                    var (ax, ay) = SourceAnchor(request.Anchor, request.Rotate, request.Flip);
                    fillLeft = Offset(bw - rw, ax);
                    fillRight = bw - rw - fillLeft;
                    fillTop = Offset(bh - rh, ay);
                    fillBottom = bh - rh - fillTop;
                    break;
                }

                default:
                {
                    double f = Math.Min(fx, fy);
                    if (request.Fit == ImageFit.Shrink)
                    {
                        f = Math.Min(1, f);
                    }

                    (rw, rh) = (Side(region.Width * f), Side(region.Height * f));
                    break;
                }
            }
        }
        else if (boxWidth is { } onlyWidth)
        {
            double f = request.Fit == ImageFit.Shrink ? Math.Min(1, (double)onlyWidth / region.Width) : (double)onlyWidth / region.Width;
            (rw, rh) = (Side(region.Width * f), Side(region.Height * f));
        }
        else if (boxHeight is { } onlyHeight)
        {
            double f = request.Fit == ImageFit.Shrink ? Math.Min(1, (double)onlyHeight / region.Height) : (double)onlyHeight / region.Height;
            (rw, rh) = (Side(region.Width * f), Side(region.Height * f));
        }

        int width = rw + fillLeft + fillRight, height = rh + fillTop + fillBottom;
        if (quarter)
        {
            (width, height) = (height, width);
        }

        width += 2 * request.Pad;
        height += 2 * request.Pad;
        if (width > MaxSide || height > MaxSide || (long)width * height > MaxPixels || (long)rw * rh > MaxPixels)
        {
            return (null, ImageText.OutputTooBig(width, height));
        }

        return (new ImageGeometry(cut, rw, rh, fillTop, fillRight, fillBottom, fillLeft, width, height), null);
    }

    // A side from a scaled length: rounded, at least one pixel, and short of overflow.
    private static int Side(double length) => (int)Math.Clamp(Math.Round(length), 1, int.MaxValue / 4);

    // Where in a slack of n pixels the picture sits for a -1/0/1 anchor component.
    private static int Offset(int slack, int component) => component switch
    {
        < 0 => 0,
        > 0 => slack,
        _ => slack / 2,
    };

    /// <summary>
    /// An anchor given in the final picture's terms (its top is the top the user sees) as a direction in the source's orientation,
    /// before the turn and the flip: the flip undone, then the turn. -1 left/top, 1 right/bottom. Pure.
    /// </summary>
    public static (int X, int Y) SourceAnchor(ImageAnchor anchor, int rotate, ImageFlip flip)
    {
        int x = anchor is ImageAnchor.Left or ImageAnchor.TopLeft or ImageAnchor.BottomLeft ? -1 : anchor is ImageAnchor.Right or ImageAnchor.TopRight or ImageAnchor.BottomRight ? 1 : 0;
        int y = anchor is ImageAnchor.Top or ImageAnchor.TopLeft or ImageAnchor.TopRight ? -1 : anchor is ImageAnchor.Bottom or ImageAnchor.BottomLeft or ImageAnchor.BottomRight ? 1 : 0;
        if (flip == ImageFlip.Horizontal)
        {
            x = -x;
        }
        else if (flip == ImageFlip.Vertical)
        {
            y = -y;
        }

        // A clockwise quarter turn takes the source's left edge to the top, its top to the right.
        return rotate switch
        {
            90 => (y, -x),
            180 => (-x, -y),
            270 => (-y, x),
            _ => (x, y),
        };
    }

    /// <summary>
    /// The edit: <paramref name="source"/> through every step of <paramref name="request"/> into <paramref name="format"/>, or the
    /// refusal. With <see cref="ImageEditRequest.MaxKb"/> the encode repeats (lower quality, then smaller) until it fits, at most
    /// <see cref="MaxFitEncodes"/> times, and only the winner is returned. <paramref name="defaultQuality"/> is <c>Image edit quality</c>, used
    /// when the request names none. <paramref name="cancellationToken"/> is checked between encodes.
    /// </summary>
    public static (ImageEditResult? Result, string? Error) Apply(byte[] source, ImageEditRequest request, ImageFormat format, int defaultQuality = DefaultQuality, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(format);
        if (source.LongLength > MaxSourceBytes)
        {
            return (null, ImageText.SourceTooBig);
        }

        var info = Info(source);
        if (info is null)
        {
            return (null, ImageText.NotAnImage);
        }

        if ((long)info.Width * info.Height > MaxPixels)
        {
            return (null, ImageText.SourceTooBig);
        }

        if (OptionMismatch(request, format) is { } mismatch)
        {
            return (null, mismatch);
        }

        if (!ImageFormats.CanWrite(format))
        {
            return (null, ImageText.NoEncoder(format, ImageFormats.WritableFormats()));
        }

        var (geometry, planError) = Plan(info.Width, info.Height, request);
        if (geometry is null)
        {
            return (null, planError);
        }

        int quality = Math.Clamp(request.Quality ?? defaultQuality, 1, 100);
        try
        {
            if (request.MaxKb is not { } maxKb)
            {
                byte[] bytes = Encode(source, request, format, geometry, quality, 1.0);
                return (Result(bytes, format, info, format.Lossy ? quality : null, fitTried: false, shrunk: false), null);
            }

            return Fit(source, request, format, geometry, info, quality, maxKb * 1024L, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return (null, ImageText.Failed(e.Message));
        }
    }

    /// <summary>An option the chosen format cannot take, as its refusal; null when they all fit. Pure.</summary>
    public static string? OptionMismatch(ImageEditRequest request, ImageFormat format)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(format);
        if (request.Chroma != ImageChroma.Auto && format != ImageFormats.Jpeg)
        {
            return ImageText.OptionNotFor("chroma", format, "jpeg");
        }

        if (request.Colors is not null && format != ImageFormats.Png && format != ImageFormats.Gif)
        {
            return ImageText.OptionNotFor("colors", format, "png or gif");
        }

        if (request.Dither != DitherMode.Auto && format != ImageFormats.Gif && !(format == ImageFormats.Png && request.Colors is not null))
        {
            return ImageText.OptionNotFor("dither", format, "gif, or png with colors");
        }

        if (request.Interlace && format != ImageFormats.Png)
        {
            return ImageText.OptionNotFor("interlace", format, "png");
        }

        if (request.Quality is not null && !format.Lossy)
        {
            return ImageText.OptionNotFor("quality", format, "jpeg, jxl or heif");
        }

        return null;
    }

    // max_kb: the quality first (lossy only, a binary search down to MinFitQuality), then the size in FitShrinkStep steps.
    private static (ImageEditResult? Result, string? Error) Fit(byte[] source, ImageEditRequest request, ImageFormat format, ImageGeometry geometry, ImageInfo info, int quality, long limit, CancellationToken cancellationToken)
    {
        int encodes = 0;
        long smallest = long.MaxValue;
        byte[]? Try(int q, double factor)
        {
            cancellationToken.ThrowIfCancellationRequested();
            encodes++;
            byte[] bytes = Encode(source, request, format, geometry, q, factor);
            smallest = Math.Min(smallest, bytes.LongLength);
            return bytes.LongLength <= limit ? bytes : null;
        }

        if (Try(quality, 1.0) is { } first)
        {
            return (Result(first, format, info, format.Lossy ? quality : null, fitTried: true, shrunk: false), null);
        }

        int q = quality;
        if (format.Lossy && quality > MinFitQuality)
        {
            if (Try(MinFitQuality, 1.0) is { } floor)
            {
                // The best quality that fits, between the floor (fits) and the asked one (does not).
                int lo = MinFitQuality, hi = quality - 1;
                byte[] best = floor;
                while (lo < hi && encodes < MaxFitEncodes)
                {
                    int mid = (lo + hi + 1) / 2;
                    if (Try(mid, 1.0) is { } fits)
                    {
                        (lo, best) = (mid, fits);
                    }
                    else
                    {
                        hi = mid - 1;
                    }
                }

                return (Result(best, format, info, lo, fitTried: true, shrunk: false), null);
            }

            q = MinFitQuality;
        }

        double factor = 1.0;
        while (encodes < MaxFitEncodes)
        {
            factor *= FitShrinkStep;
            if (Math.Min(geometry.ResizeWidth, geometry.ResizeHeight) * factor < MinFitSide)
            {
                break;
            }

            if (Try(q, factor) is { } shrunk)
            {
                return (Result(shrunk, format, info, format.Lossy ? q : null, fitTried: true, shrunk: true), null);
            }
        }

        return (null, ImageText.CannotFit(limit / 1024, smallest, format));
    }

    private static ImageEditResult Result(byte[] bytes, ImageFormat format, ImageInfo source, int? quality, bool fitTried, bool shrunk)
    {
        var written = Info(bytes);
        return new ImageEditResult(bytes, format, written?.Width ?? 0, written?.Height ?? 0, source.Width, source.Height, source.Frames, quality, fitTried, shrunk);
    }

    // One decode, the steps, one encode. factor < 1 shrinks the scaler's size and the pad fill (max_kb); the border stays.
    private static byte[] Encode(byte[] source, ImageEditRequest request, ImageFormat format, ImageGeometry geometry, int quality, double factor)
    {
        int rw = geometry.ResizeWidth, rh = geometry.ResizeHeight;
        int fillTop = geometry.FillTop, fillRight = geometry.FillRight, fillBottom = geometry.FillBottom, fillLeft = geometry.FillLeft;
        if (factor < 1)
        {
            (rw, rh) = (Side(rw * factor), Side(rh * factor));
            (fillTop, fillRight, fillBottom, fillLeft) = ((int)(fillTop * factor), (int)(fillRight * factor), (int)(fillBottom * factor), (int)(fillLeft * factor));
        }

        var background = request.Background ?? Color.FromArgb(255, 255, 255, 255);
        bool opaqueFormat = format == ImageFormats.Jpeg || format == ImageFormats.Bmp;
        var settings = new ProcessImageSettings
        {
            Crop = geometry.Crop,
            Width = rw,
            Height = rh,
            ResizeMode = CropScaleMode.Stretch,
            HybridMode = HybridScaleMode.Off,
            Sharpen = request.Sharpen ?? true,
            DpiX = request.Dpi ?? 0,
            DpiY = request.Dpi ?? 0,
            EncoderOptions = EncoderOptions(request, format, quality),
        };
        if (request.Sharpen == true)
        {
            settings.UnsharpMask = FirmSharpen;
        }

        if (opaqueFormat)
        {
            // MagicScaler's matte is black unless set: a transparent PNG becomes a JPEG on white (or the background asked for).
            settings.MatteColor = background.A == 0 ? Color.FromArgb(255, 255, 255, 255) : background;
        }

        if (InterpolationOf(request.Interpolation) is { } interpolation)
        {
            settings.Interpolation = interpolation;
        }

        settings.MetadataNames = MetadataNamesOf(request.Metadata);

        if (!settings.TrySetEncoderFormat(format.MimeType))
        {
            throw new InvalidOperationException(ImageText.NoEncoder(format, ImageFormats.WritableFormats()));
        }

        using var input = new MemoryStream(source, writable: false);
        using var pipeline = MagicImageProcessor.BuildPipeline(input, settings);
        bool fills = fillTop + fillRight + fillBottom + fillLeft > 0 || request.Pad > 0;
        if (fills && background.A < 255 && !opaqueFormat)
        {
            // An opaque source has no alpha to pad into: a transparent border would come out black without it.
            pipeline.AddTransform(new FormatConversionTransform(PixelFormats.Bgra32bpp));
        }

        if (fillTop + fillRight + fillBottom + fillLeft > 0)
        {
            pipeline.AddTransform(new PadTransform(background, fillTop, fillRight, fillBottom, fillLeft));
        }

        if (request.Rotate is 90 or 180 or 270)
        {
            pipeline.AddTransform(new OrientationTransform(request.Rotate switch { 90 => Orientation.Rotate90, 180 => Orientation.Rotate180, _ => Orientation.Rotate270 }));
        }

        if (request.Flip != ImageFlip.None)
        {
            pipeline.AddTransform(new OrientationTransform(request.Flip == ImageFlip.Horizontal ? Orientation.FlipHorizontal : Orientation.FlipVertical));
        }

        var matrix = ImageColor.Matrix(request.Filter, request.Brightness, request.Contrast, request.Saturation, request.Hue, request.Tint, request.TintAmount);
        if (!matrix.IsIdentity)
        {
            pipeline.AddTransform(new ColorMatrixTransform(matrix));
        }

        if (request.Blur > 0)
        {
            pipeline.AddTransform(new GaussianBlurTransform(request.Blur));
        }

        if (request.Pad > 0)
        {
            pipeline.AddTransform(new PadTransform(background, request.Pad, request.Pad, request.Pad, request.Pad));
        }

        using var output = new MemoryStream();
        _ = pipeline.WriteOutput(output);
        return output.ToArray();
    }

    /// <summary>The policy names a metadata choice copies.</summary>
    public static IReadOnlyList<string> MetadataNamesOf(ImageMetadataPolicy policy) => policy switch
    {
        ImageMetadataPolicy.Basic => BasicMetadata,
        ImageMetadataPolicy.All => [.. BasicMetadata, .. GpsMetadata],
        _ => [],
    };

    private static IEncoderOptions? EncoderOptions(ImageEditRequest request, ImageFormat format, int quality)
    {
        if (format == ImageFormats.Jpeg)
        {
            var chroma = request.Chroma switch
            {
                ImageChroma.Subsample420 => ChromaSubsampleMode.Subsample420,
                ImageChroma.Subsample422 => ChromaSubsampleMode.Subsample422,
                ImageChroma.Subsample444 => ChromaSubsampleMode.Subsample444,
                _ => ChromaSubsampleMode.Default,
            };
            return new JpegEncoderOptions(quality, chroma, false);
        }

        if (format == ImageFormats.Png)
        {
            return request.Colors is { } colors
                ? new PngIndexedEncoderOptions(colors, null, request.Dither, PngFilter.Unspecified, request.Interlace)
                : new PngEncoderOptions(PngFilter.Unspecified, request.Interlace);
        }

        if (format == ImageFormats.Gif)
        {
            return new GifEncoderOptions(request.Colors ?? 256, null, request.Dither);
        }

        if (format == ImageFormats.Tiff)
        {
            return TiffEncoderOptions.Default;
        }

        return format.Lossy ? new LossyEncoderOptions(quality) : null;
    }

    /// <summary>An interpolation name (<see cref="ImageWords.TryParseInterpolation"/>'s canonical ones) as MagicScaler's settings; null for the scaler's default.</summary>
    public static InterpolationSettings? InterpolationOf(string? name) => name switch
    {
        "lanczos" => InterpolationSettings.Lanczos,
        "spline36" => InterpolationSettings.Spline36,
        "cubic" => InterpolationSettings.Cubic,
        "linear" => InterpolationSettings.Linear,
        "nearest" => InterpolationSettings.NearestNeighbor,
        _ => null,
    };
}
