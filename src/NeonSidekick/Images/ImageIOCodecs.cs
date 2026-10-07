using System.Drawing;
using System.Runtime.Versioning;
using NeonSidekick.Diagnostics;
using PhotoSauce.MagicScaler;

namespace NeonSidekick.Images;

/// <summary>
/// Apple's ImageIO as MagicScaler's codecs on macOS (2026-10-07, pictures on the Mac; the user's pick of this over a decode/encode
/// seam of the app's own). MagicScaler's pipeline is managed code that runs anywhere — the spike that settled it ran crop, scale,
/// pad, turn, colour matrix and blur through a plug-in under NativeAOT on osx-arm64 — and only its codecs are Windows' (WIC).
/// On a Mac its codec list starts empty, so <see cref="Register"/> fills it through <see cref="CodecManager.Configure"/>: one
/// decoder for everything ImageIO reads (routed by <see cref="ImageIOFormats.Patterns"/>) and one encoder per format this Mac's
/// ImageIO writes. Every caller then takes the road it takes on Windows. Windows never calls this: WIC stays its only backend.
///
/// <para>Decoding: <c>CGImageSourceCreateImageAtIndex</c> gives the stored pixels (for HEIF too: its <c>irot</c>/<c>imir</c> are
/// not applied), vImage converts them to 8-bit sRGB — colour-matching a Display P3 iPhone photo, so MagicScaler is offered no
/// ICC profile — as straight BGRA, or BGRX handed on as BGR for a picture without alpha. ImageIO's resolved orientation goes to
/// MagicScaler as a bare EXIF block (<see cref="ImageIOFormats.OrientationExif"/>) and MagicScaler turns the picture upright
/// itself, as it does for WIC's. A frame decodes on its first pixel read: <c>ImageFileInfo</c> reads only the size.</para>
///
/// <para>Encoding: MagicScaler's pixels become a CGImage tagged sRGB and go through <c>CGImageDestination</c> with the quality, the
/// resolution, PNG interlace, and the source's metadata the names ask for (<see cref="ImageIOFormats.MetadataKeys"/>; MagicScaler
/// passes the source frame's own metadata types through to the encoder untouched, so the frame offers its ImageIO properties).
/// Native and excluded from coverage; its decisions are in <see cref="ImageIOFormats"/>, and the smoke's <c>image:imageio</c>,
/// <c>image:resize</c>, <c>image:edit</c>, <c>splash:decode</c> and <c>camera:encode</c> prove it on the published binary.</para>
/// </summary>
public static class ImageIOCodecs
{
    private static readonly Lock Gate = new();
    private static volatile bool s_registered;

    /// <summary>True once <see cref="Register"/> has put ImageIO behind MagicScaler (macOS only).</summary>
    public static bool Registered => s_registered;

    /// <summary>The formats this Mac's ImageIO encodes, as registered; empty until <see cref="Register"/>.</summary>
    public static IReadOnlyList<ImageIOFormat> Writers { get; private set; } = [];

    /// <summary>
    /// ImageIO registered as MagicScaler's codecs, once per process (Program.cs first thing on a Mac, and the test assembly's
    /// ModuleInit): it must run before anything touches MagicScaler, which fixes its codec list on first use. False, logged,
    /// when it could not be (then <c>ImageCodecs.Available</c> stays false and pictures stay off, as before).
    /// </summary>
    [SupportedOSPlatform("macos")]
    public static bool Register()
    {
        lock (Gate)
        {
            if (s_registered)
            {
                return true;
            }

            try
            {
                var writes = WritableTypes();
                var writers = ImageIOFormats.All.Where(f => f.Writes && writes.Contains(f.Uti)).ToList();
                CodecManager.Configure(codecs =>
                {
                    codecs.Add(new DecoderInfo(
                        "ImageIO",
                        ImageIOFormats.All.SelectMany(f => f.MimeTypes).Distinct().ToList(),
                        ImageIOFormats.All.SelectMany(f => f.Extensions).Distinct().ToList(),
                        ImageIOFormats.Patterns.Select(p => new ContainerPattern(p.Offset, p.Signature, p.Mask)).ToList(),
                        null,
                        (stream, _) => new Container(stream)));
                    foreach (var format in writers)
                    {
                        codecs.Add(new EncoderInfo(
                            "ImageIO " + format.Uti,
                            format.MimeTypes,
                            format.Extensions,
                            format.EncoderPixelFormats,
                            null,
                            (stream, options) => new Encoder(stream, format, ImageIOFormats.Read(options)),
                            SupportsMultiFrame: false,
                            SupportsAnimation: false,
                            SupportsColorProfile: false));
                    }
                });
                Writers = writers;
                s_registered = true;
                DiagnosticLog.Debug("Image", "ImageIO registered: writes " + string.Join(", ", writers.Select(w => w.Uti)) + ".");
                return true;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                DiagnosticLog.Warn("Image", "ImageIO could not be registered as the picture codecs; pictures stay off.", e);
                return false;
            }
        }
    }

    // The type identifiers this Mac's ImageIO can write.
    [SupportedOSPlatform("macos")]
    private static HashSet<string> WritableTypes()
    {
        var types = new HashSet<string>(StringComparer.Ordinal);
        nint array = ImageIONative.CGImageDestinationCopyTypeIdentifiers();
        if (array == 0)
        {
            return types;
        }

        try
        {
            nint count = ImageIONative.CFArrayGetCount(array);
            for (nint i = 0; i < count; i++)
            {
                types.Add(ImageIONative.Text(ImageIONative.CFArrayGetValueAtIndex(array, i)));
            }
        }
        finally
        {
            ImageIONative.CFRelease(array);
        }

        return types;
    }

    /// <summary>The source frame's ImageIO properties, offered to the encoder through MagicScaler's metadata pass-through.</summary>
    public sealed class SourceProperties(nint dictionary) : IMetadata
    {
        /// <summary>The <c>CFDictionaryRef</c>, owned by the frame (alive while the pipeline is).</summary>
        public nint Dictionary { get; } = dictionary;
    }

    private sealed class Exif(byte[] tiff) : IExifSource
    {
        public int ExifLength => tiff.Length;

        public void CopyExif(Span<byte> dest) => tiff.CopyTo(dest);
    }

    [SupportedOSPlatform("macos")]
    private sealed unsafe class Container : IImageContainer
    {
        private nint _source;

        public Container(Stream stream)
        {
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            byte[] bytes = copy.GetBuffer();
            nint data;
            fixed (byte* p = bytes)
            {
                data = ImageIONative.CFDataCreate(0, p, (nint)copy.Length);
            }

            try
            {
                _source = data == 0 ? 0 : ImageIONative.CGImageSourceCreateWithData(data, 0);
            }
            finally
            {
                if (data != 0)
                {
                    ImageIONative.CFRelease(data);   // the source keeps its own reference
                }
            }

            FrameCount = _source == 0 ? 0 : (int)ImageIONative.CGImageSourceGetCount(_source);
            if (FrameCount < 1)
            {
                Dispose();
                throw new InvalidDataException("ImageIO could not read the picture.");
            }

            MimeType = ImageIOFormats.MimeTypeOf(ImageIONative.Text(ImageIONative.CGImageSourceGetType(_source)));
        }

        public string? MimeType { get; }

        public int FrameCount { get; }

        public IImageFrame GetFrame(int index)
        {
            ObjectDisposedException.ThrowIf(_source == 0, this);
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, FrameCount);
            return new Frame(_source, index);
        }

        public void Dispose()
        {
            if (_source != 0)
            {
                ImageIONative.CFRelease(_source);
                _source = 0;
            }
        }
    }

    [SupportedOSPlatform("macos")]
    private sealed unsafe class Frame : IImageFrame, IMetadataSource
    {
        private readonly byte[]? _exif;
        private nint _image;
        private nint _properties;

        public Frame(nint source, int index)
        {
            _properties = ImageIONative.CGImageSourceCopyPropertiesAtIndex(source, index, 0);
            _image = ImageIONative.CGImageSourceCreateImageAtIndex(source, index, 0);
            if (_image == 0)
            {
                Dispose();
                throw new InvalidDataException("ImageIO could not decode the picture.");
            }

            int orientation = _properties == 0 ? 1 : ImageIONative.Int(ImageIONative.CFDictionaryGetValue(_properties, ImageIONative.ImageIOKey("kCGImagePropertyOrientation"))) ?? 1;
            _exif = ImageIOFormats.OrientationExif(orientation);
            int width = (int)ImageIONative.CGImageGetWidth(_image);
            int height = (int)ImageIONative.CGImageGetHeight(_image);
            bool alpha = ImageIOFormats.HasAlpha(ImageIONative.CGImageGetAlphaInfo(_image));
            PixelSource = new Pixels(this, width, height, alpha);
        }

        public IPixelSource PixelSource { get; }

        public bool TryGetMetadata<T>(out T metadata) where T : IMetadata
        {
            if (typeof(T) == typeof(IExifSource) && _exif is not null)
            {
                metadata = (T)(object)new Exif(_exif);
                return true;
            }

            if (typeof(T) == typeof(SourceProperties) && _properties != 0)
            {
                metadata = (T)(object)new SourceProperties(_properties);
                return true;
            }

            metadata = default!;
            return false;
        }

        // The whole frame as 8-bit sRGB, straight BGRA or BGRX, through vImage.
        public byte[] Decode(int width, int height, bool alpha)
        {
            ObjectDisposedException.ThrowIf(_image == 0, this);
            byte[] pixels = GC.AllocateUninitializedArray<byte>(checked(width * height * 4));
            nint space = ImageIONative.CGColorSpaceCreateWithName(ImageIONative.SrgbName);
            try
            {
                var format = new ImageIONative.VImageFormat
                {
                    BitsPerComponent = 8,
                    BitsPerPixel = 32,
                    ColorSpace = space,
                    BitmapInfo = alpha ? ImageIONative.BgraStraight : ImageIONative.Bgrx,
                };
                fixed (byte* p = pixels)
                {
                    var buffer = new ImageIONative.VImageBuffer { Data = p, Height = (nuint)height, Width = (nuint)width, RowBytes = (nuint)(width * 4) };
                    nint error = ImageIONative.vImageBuffer_InitWithCGImage(&buffer, &format, null, _image, ImageIONative.NoAllocate);
                    if (error != 0)
                    {
                        throw new InvalidDataException("vImage could not convert the picture (" + error.ToString(System.Globalization.CultureInfo.InvariantCulture) + ").");
                    }
                }
            }
            finally
            {
                ImageIONative.CGColorSpaceRelease(space);
            }

            return pixels;
        }

        public void Dispose()
        {
            if (_image != 0)
            {
                ImageIONative.CGImageRelease(_image);
                _image = 0;
            }

            if (_properties != 0)
            {
                ImageIONative.CFRelease(_properties);
                _properties = 0;
            }
        }
    }

    [SupportedOSPlatform("macos")]
    private sealed class Pixels(Frame frame, int width, int height, bool alpha) : IPixelSource
    {
        private byte[]? _decoded;

        public Guid Format => alpha ? PixelFormats.Bgra32bpp : PixelFormats.Bgr24bpp;

        public int Width => width;

        public int Height => height;

        public void CopyPixels(Rectangle sourceArea, int cbStride, Span<byte> buffer)
        {
            _decoded ??= frame.Decode(width, height, alpha);
            int bytes = alpha ? 4 : 3;
            for (int y = 0; y < sourceArea.Height; y++)
            {
                var row = _decoded.AsSpan(((((sourceArea.Y + y) * width) + sourceArea.X) * 4), sourceArea.Width * 4);
                var target = buffer.Slice(y * cbStride, sourceArea.Width * bytes);
                if (alpha)
                {
                    row.CopyTo(target);
                }
                else
                {
                    ImageIOFormats.BgrxToBgr(row, target, sourceArea.Width);
                }
            }
        }
    }

    [SupportedOSPlatform("macos")]
    private sealed unsafe class Encoder(Stream stream, ImageIOFormat format, ImageIOEncoderOptions options) : IImageEncoder
    {
        public void WriteFrame(IPixelSource source, IMetadataSource metadata, Rectangle sourceArea)
        {
            if (sourceArea.IsEmpty)
            {
                sourceArea = new Rectangle(0, 0, source.Width, source.Height);
            }

            int width = sourceArea.Width, height = sourceArea.Height;
            var (pixels, bitmapInfo) = Read(source, sourceArea);
            var made = new List<nint>();
            nint Keep(nint cf)
            {
                if (cf != 0)
                {
                    made.Add(cf);
                }

                return cf;
            }

            nint space = ImageIONative.CGColorSpaceCreateWithName(ImageIONative.SrgbName);
            nint provider = 0, image = 0, destination = 0;
            try
            {
                nint data;
                fixed (byte* p = pixels)
                {
                    data = Keep(ImageIONative.CFDataCreate(0, p, pixels.Length));
                }

                provider = ImageIONative.CGDataProviderCreateWithCFData(data);
                image = ImageIONative.CGImageCreate((nuint)width, (nuint)height, 8, 32, (nuint)(width * 4), space, bitmapInfo, provider, null, 1, 0);
                if (image == 0)
                {
                    throw new InvalidOperationException("CoreGraphics could not make the picture.");
                }

                nint properties = Keep(ImageIONative.CreateDictionary());
                AddOptions(properties, Keep);
                if (options.MetadataNames is { Count: > 0 } names && metadata.TryGetMetadata<SourceProperties>(out var sourceProperties))
                {
                    CopyMetadata(sourceProperties.Dictionary, properties, ImageIOFormats.KeysFor(names), Keep);
                }

                nint output = Keep(ImageIONative.CFDataCreateMutable(0, 0));
                nint type = Keep(ImageIONative.CreateString(format.Uti));
                destination = ImageIONative.CGImageDestinationCreateWithData(output, type, 1, 0);
                if (destination == 0)
                {
                    throw new InvalidOperationException("ImageIO has no " + format.Uti + " encoder.");
                }

                ImageIONative.CGImageDestinationAddImage(destination, image, properties);
                if (ImageIONative.CGImageDestinationFinalize(destination) == 0)
                {
                    throw new InvalidOperationException("ImageIO could not write the " + format.Uti + " picture.");
                }

                var written = new ReadOnlySpan<byte>(ImageIONative.CFDataGetBytePtr(output), (int)ImageIONative.CFDataGetLength(output));
                if (options.MetadataNames is not { Count: > 0 } && MetadataStripper.Strip(written.ToArray()).Result is { } clean)
                {
                    // ImageIO adds metadata nobody asked for (an EXIF block of the size and colour space, an empty Photoshop/IPTC
                    // block in a JPEG, eXIf in a PNG); WIC writes none. With no names asked, MetadataStripper takes it out
                    // losslessly, so "none" means none on both. A format it cannot walk (HEIC, TIFF, BMP) is written as it came.
                    stream.Write(clean.Bytes);
                }
                else
                {
                    stream.Write(written);
                }
            }
            finally
            {
                if (destination != 0)
                {
                    ImageIONative.CFRelease(destination);
                }

                if (image != 0)
                {
                    ImageIONative.CGImageRelease(image);
                }

                if (provider != 0)
                {
                    ImageIONative.CGDataProviderRelease(provider);
                }

                ImageIONative.CGColorSpaceRelease(space);
                made.ForEach(ImageIONative.CFRelease);
            }
        }

        public void Commit()
        {
        }

        public void Dispose()
        {
        }

        // MagicScaler's pixels as 32-bit rows CoreGraphics takes: BGRA kept straight, BGR and grey widened to BGRX.
        private static (byte[] Pixels, uint BitmapInfo) Read(IPixelSource source, Rectangle area)
        {
            int count = area.Width * area.Height;
            var pixels = GC.AllocateUninitializedArray<byte>(checked(count * 4));
            if (source.Format == PixelFormats.Bgra32bpp)
            {
                source.CopyPixels(area, area.Width * 4, pixels);
                return (pixels, ImageIONative.BgraStraight);
            }

            if (source.Format == PixelFormats.Bgr24bpp)
            {
                var bgr = GC.AllocateUninitializedArray<byte>(count * 3);
                source.CopyPixels(area, area.Width * 3, bgr);
                ImageIOFormats.BgrToBgrx(bgr, pixels, count);
                return (pixels, ImageIONative.Bgrx);
            }

            if (source.Format == PixelFormats.Grey8bpp)
            {
                var grey = GC.AllocateUninitializedArray<byte>(count);
                source.CopyPixels(area, area.Width, grey);
                for (int i = 0; i < count; i++)
                {
                    pixels[i * 4] = pixels[(i * 4) + 1] = pixels[(i * 4) + 2] = grey[i];
                    pixels[(i * 4) + 3] = 0xFF;
                }

                return (pixels, ImageIONative.Bgrx);
            }

            throw new NotSupportedException("The ImageIO encoder takes BGRA, BGR or grey pixels.");
        }

        private void AddOptions(nint properties, Func<nint, nint> keep)
        {
            if (format.Uti is "public.jpeg" or "public.heic" && ImageIOFormats.Compression(options.Quality) is { } quality)
            {
                Set(properties, "kCGImageDestinationLossyCompressionQuality", keep(ImageIONative.CreateNumber(quality)));
            }

            if (options.Dpi > 0)
            {
                nint dpi = keep(ImageIONative.CreateNumber(options.Dpi));
                Set(properties, "kCGImagePropertyDPIWidth", dpi);
                Set(properties, "kCGImagePropertyDPIHeight", dpi);
            }

            if (options.Interlace && format.Uti == "public.png")
            {
                nint png = keep(ImageIONative.CreateDictionary());
                Set(png, "kCGImagePropertyPNGInterlaceType", keep(ImageIONative.CreateNumber(1)));
                Set(properties, "kCGImagePropertyPNGDictionary", png);
            }
        }

        // Each asked-for property the source has, into the same sub-dictionary of the output's properties.
        private static void CopyMetadata(nint source, nint properties, IReadOnlyList<(string Dictionary, string Key)> keys, Func<nint, nint> keep)
        {
            var made = new Dictionary<string, nint>(StringComparer.Ordinal);
            foreach (var (dictionaryName, keyName) in keys)
            {
                nint dictionaryKey = ImageIONative.ImageIOKey(dictionaryName), key = ImageIONative.ImageIOKey(keyName);
                if (dictionaryKey == 0 || key == 0)
                {
                    continue;
                }

                nint from = ImageIONative.CFDictionaryGetValue(source, dictionaryKey);
                nint value = ImageIONative.IsDictionary(from) ? ImageIONative.CFDictionaryGetValue(from, key) : 0;
                if (value == 0)
                {
                    continue;
                }

                if (!made.TryGetValue(dictionaryName, out nint to))
                {
                    to = keep(ImageIONative.CreateDictionary());
                    made[dictionaryName] = to;
                    ImageIONative.CFDictionarySetValue(properties, dictionaryKey, to);
                }

                ImageIONative.CFDictionarySetValue(to, key, value);
            }
        }

        private static void Set(nint dictionary, string keyName, nint value)
        {
            nint key = ImageIONative.ImageIOKey(keyName);
            if (key != 0 && value != 0)
            {
                ImageIONative.CFDictionarySetValue(dictionary, key, value);
            }
        }
    }
}
