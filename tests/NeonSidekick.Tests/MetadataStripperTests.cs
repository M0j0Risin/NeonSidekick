using System.Buffers.Binary;
using System.Text;
using NeonSidekick.Files;
using NeonSidekick.Images;
using PhotoSauce.MagicScaler;

namespace NeonSidekick.Tests;

/// <summary>Pictures with metadata spliced in by hand for the lossless strip's tests (2026-10-05), and the walks that check them.</summary>
internal static class StripFixtures
{
    /// <summary>A clean 16×16 picture encoded by WIC in <paramref name="mime"/>.</summary>
    public static byte[] Encoded(string mime)
    {
        var settings = new ProcessImageSettings();
        settings.TrySetEncoderFormat(mime);
        using var output = new MemoryStream();
        MagicImageProcessor.ProcessImage(ImageFixtures.Quadrants(16, 16), output, settings);
        return output.ToArray();
    }

    public static byte[] Jpeg() => Encoded("image/jpeg");

    public static byte[] Png() => Encoded("image/png");

    /// <summary>A JPEG marker segment: FF, the marker, the length, the payload.</summary>
    public static byte[] Segment(byte marker, ReadOnlySpan<byte> payload) =>
        [0xFF, marker, (byte)((payload.Length + 2) >> 8), (byte)(payload.Length + 2), .. payload];

    /// <summary><paramref name="segments"/> put straight after a JPEG's SOI.</summary>
    public static byte[] AfterSoi(byte[] jpeg, params byte[][] segments) => [0xFF, 0xD8, .. segments.SelectMany(s => s), .. jpeg.AsSpan(2).ToArray()];

    /// <summary>
    /// A little-endian TIFF block of SHORT/ASCII entries in IFD0, sorted as given: (tag, type, value) with an ASCII value stored
    /// after the IFD.
    /// </summary>
    public static byte[] Tiff(params (ushort Tag, ushort Orientation, string? Text)[] entries)
    {
        var bytes = new List<byte>("II*\0"u8.ToArray());
        void U16(int v) { bytes.Add((byte)v); bytes.Add((byte)(v >> 8)); }
        void U32(long v) { for (int i = 0; i < 4; i++) { bytes.Add((byte)(v >> (8 * i))); } }
        U32(8);
        U16(entries.Length);
        int data = 8 + 2 + (entries.Length * 12) + 4;
        var tail = new List<byte>();
        foreach (var (tag, orientation, text) in entries)
        {
            U16(tag);
            if (text is null)
            {
                U16(3); U32(1); U16(orientation); U16(0);
            }
            else
            {
                byte[] ascii = Encoding.ASCII.GetBytes(text + "\0");
                U16(2); U32(ascii.Length); U32(data + tail.Count);
                tail.AddRange(ascii);
            }
        }

        U32(0);
        bytes.AddRange(tail);
        return [.. bytes];
    }

    /// <summary>The TIFF block inside <see cref="ImageFixtures.ExifJpeg"/>: an artist, the copyright and a GPS position.</summary>
    public static byte[] GeoTiff()
    {
        byte[] jpeg = ImageFixtures.ExifJpeg();
        int length = (jpeg[4] << 8) | jpeg[5];
        return jpeg.AsSpan(12, length - 8).ToArray();
    }

    /// <summary>A JPEG's bytes from its first SOS through EOI, found by walking the segments' lengths.</summary>
    public static byte[] Scan(byte[] jpeg)
    {
        int p = 2;
        while (jpeg[p + 1] != 0xDA)
        {
            p += 2 + ((jpeg[p + 2] << 8) | jpeg[p + 3]);
        }

        int end = jpeg.AsSpan().LastIndexOf((ReadOnlySpan<byte>)[0xFF, 0xD9]);
        return jpeg.AsSpan(p, end + 2 - p).ToArray();
    }

    /// <summary>The JPEG's segment markers before the first SOS, with the first bytes of each payload: what a strip kept.</summary>
    public static List<(byte Marker, string Head)> Segments(byte[] jpeg)
    {
        var list = new List<(byte, string)>();
        int p = 2;
        while (jpeg[p + 1] != 0xDA)
        {
            int length = (jpeg[p + 2] << 8) | jpeg[p + 3];
            list.Add((jpeg[p + 1], Encoding.ASCII.GetString(jpeg, p + 4, Math.Min(5, length - 2))));
            p += 2 + length;
        }

        return list;
    }

    /// <summary>A PNG chunk with its CRC.</summary>
    public static byte[] Chunk(string type, ReadOnlySpan<byte> data)
    {
        var chunk = new byte[12 + data.Length];
        BinaryPrimitives.WriteUInt32BigEndian(chunk, (uint)data.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(chunk, 4);
        data.CopyTo(chunk.AsSpan(8));
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8 + data.Length), MetadataStripper.Crc32(chunk.AsSpan(4, 4 + data.Length)));
        return chunk;
    }

    /// <summary><paramref name="chunks"/> put before a PNG's IEND (its last 12 bytes).</summary>
    public static byte[] BeforeIend(byte[] png, params byte[][] chunks) => [.. png.AsSpan(0, png.Length - 12).ToArray(), .. chunks.SelectMany(c => c), .. png.AsSpan(png.Length - 12).ToArray()];

    /// <summary>A PNG's chunks: type, offset, data length, and whether the CRC is right.</summary>
    public static List<(string Type, int Offset, int Length, bool CrcOk)> Chunks(byte[] png)
    {
        var list = new List<(string, int, int, bool)>();
        int p = 8;
        while (p + 12 <= png.Length)
        {
            int length = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(p));
            string type = Encoding.ASCII.GetString(png, p + 4, 4);
            bool ok = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(p + 8 + length)) == MetadataStripper.Crc32(png.AsSpan(p + 4, 4 + length));
            list.Add((type, p, length, ok));
            p += 12 + length;
            if (type == "IEND")
            {
                break;
            }
        }

        return list;
    }

    /// <summary>A 1×1 lossless WebP's VP8L bitstream (the payload of the well-known 34-byte <c>UklGRhoAAABXRUJQVlA4TA0AAAAv…</c>).</summary>
    public static readonly byte[] Vp8l = [0x2F, 0x00, 0x00, 0x00, 0x10, 0x07, 0x10, 0x11, 0x11, 0x88, 0x88, 0xFE, 0x07];

    /// <summary>A RIFF chunk, padded to an even length.</summary>
    public static byte[] RiffChunk(string type, ReadOnlySpan<byte> data)
    {
        var chunk = new byte[8 + data.Length + (data.Length & 1)];
        Encoding.ASCII.GetBytes(type).CopyTo(chunk, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(4), (uint)data.Length);
        data.CopyTo(chunk.AsSpan(8));
        return chunk;
    }

    /// <summary>A WebP of <paramref name="chunks"/>, with the RIFF size right.</summary>
    public static byte[] WebP(params byte[][] chunks)
    {
        byte[] body = [.. "WEBP"u8.ToArray(), .. chunks.SelectMany(c => c)];
        var header = new byte[8];
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), (uint)body.Length);
        return [.. header, .. body];
    }

    /// <summary>A 1×1 extended WebP whose VP8X says EXIF and XMP are there (flags 0x0C).</summary>
    public static byte[] Vp8x(byte flags = 0x0C) => RiffChunk("VP8X", [flags, 0, 0, 0, 0, 0, 0, 0, 0, 0]);

    /// <summary>A WebP's chunk types, in order.</summary>
    public static List<string> WebPChunks(byte[] webp)
    {
        var list = new List<string>();
        int p = 12;
        while (p + 8 <= webp.Length)
        {
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(webp.AsSpan(p + 4));
            list.Add(Encoding.ASCII.GetString(webp, p, 4));
            p += 8 + (int)size + (int)(size & 1);
        }

        return list;
    }

    public static bool Contains(byte[] bytes, string text) => bytes.AsSpan().IndexOf(Encoding.ASCII.GetBytes(text)) >= 0;
}

/// <summary><see cref="MetadataStripper"/> (2026-10-05): each container walked, what goes, what stays to the byte, and what is refused.</summary>
public sealed class MetadataStripperTests
{
    private static StripResult Strip(byte[] source, int orientation = 1)
    {
        var (result, error) = MetadataStripper.Strip(source, orientation);
        Assert.True(result is not null, error);
        return result;
    }

    [Fact]
    public void ContainerOf_ByTheMagicBytes_AndByTheExtension()
    {
        Assert.Equal(StripContainer.Jpeg, MetadataStripper.ContainerOf(StripFixtures.Jpeg()));
        Assert.Equal(StripContainer.Png, MetadataStripper.ContainerOf(StripFixtures.Png()));
        Assert.Equal(StripContainer.WebP, MetadataStripper.ContainerOf(StripFixtures.WebP(StripFixtures.RiffChunk("VP8L", StripFixtures.Vp8l))));
        Assert.Equal(StripContainer.Gif, MetadataStripper.ContainerOf(ImageFixtures.AnimatedGif()));
        Assert.Null(MetadataStripper.ContainerOf(ImageFixtures.Solid(2, 2, ImageFixtures.Red)));
        Assert.Null(MetadataStripper.ContainerOf("not a picture"u8));

        Assert.Equal(StripContainer.Jpeg, MetadataStripper.ContainerOfPath(@"D:\p\cat.JPEG"));
        Assert.Equal(StripContainer.WebP, MetadataStripper.ContainerOfPath("cat.webp"));
        Assert.Null(MetadataStripper.ContainerOfPath("cat.tiff"));
        Assert.Equal(".jpeg", MetadataStripper.ExtensionFor(StripContainer.Jpeg, "cat.jpeg"));
        Assert.Equal(".jpg", MetadataStripper.ExtensionFor(StripContainer.Jpeg, "cat.bin"));
    }

    [Fact]
    public void Crc32_IsPngs()
    {
        Assert.Equal(0xAE426082u, MetadataStripper.Crc32("IEND"u8));
        Assert.All(StripFixtures.Chunks(StripFixtures.Png()), c => Assert.True(c.CrcOk, c.Type));
    }

    [Fact]
    public void Jpeg_TheExifAndItsGps_Go_TheScanIsTheSameToTheByte()
    {
        byte[] source = ImageFixtures.ExifJpeg();

        var result = Strip(source);

        Assert.False(ImageFixtures.HasCopyright(result.Bytes));
        Assert.False(ImageFixtures.HasGps(result.Bytes));
        Assert.Equal(StripFixtures.Scan(source), StripFixtures.Scan(result.Bytes));
        Assert.Equal(MetadataKinds.Exif | MetadataKinds.Gps, result.Removed);
        Assert.Equal(StripContainer.Jpeg, result.Container);
        Assert.False(result.OrientationKept);
        Assert.Equal(source.Length - (2 + ((source[4] << 8) | source[5])), result.Bytes.Length);
        Assert.Equal(ImageFixtures.Pixel(source, 12, 12), ImageFixtures.Pixel(result.Bytes, 12, 12));
    }

    [Fact]
    public void Jpeg_AnOrientation_IsWrittenBackAlone()
    {
        byte[] tiff = StripFixtures.Tiff((0x0112, 6, null), (0x8298, 0, ImageFixtures.Copyright));
        byte[] source = StripFixtures.AfterSoi(StripFixtures.Jpeg(), StripFixtures.Segment(0xE1, [.. "Exif\0\0"u8.ToArray(), .. tiff]));
        var info = ImageEditor.Info(source)!;
        Assert.Equal(Orientation.Rotate90, info.Orientation);

        var result = Strip(source, (int)info.Orientation);

        Assert.True(result.OrientationKept);
        Assert.False(ImageFixtures.HasCopyright(result.Bytes));
        Assert.Equal(Orientation.Rotate90, ImageEditor.Info(result.Bytes)!.Orientation);
        Assert.Equal(StripFixtures.Scan(source), StripFixtures.Scan(result.Bytes));
        // The bare block sits after JFIF, ahead of the tables.
        var segments = StripFixtures.Segments(result.Bytes);
        int exif = segments.FindIndex(s => s.Marker == 0xE1);
        Assert.Equal(1, segments.Count(s => s.Marker == 0xE1));
        Assert.True(segments.Take(exif).All(s => s.Marker == 0xE0), string.Join(",", segments.Select(s => s.Marker.ToString("X2"))));
    }

    [Fact]
    public void Jpeg_TheColourStays_EveryOtherSegmentAndTheTrailerGo()
    {
        byte[] clean = StripFixtures.Jpeg();
        byte[] trailer = [.. "....ftypmp42"u8.ToArray(), .. new byte[988]];
        byte[] source =
        [
            .. StripFixtures.AfterSoi(
                clean,
                StripFixtures.Segment(0xE2, [.. "ICC_PROFILE\0"u8.ToArray(), 1, 1, 9, 9, 9]),
                StripFixtures.Segment(0xEE, [.. "Adobe"u8.ToArray(), 0, 100, 0, 0, 0, 0, 1]),
                StripFixtures.Segment(0xFE, "a comment by neon"u8),
                StripFixtures.Segment(0xED, [.. "Photoshop 3.0\0"u8.ToArray(), 1, 2, 3]),
                StripFixtures.Segment(0xE2, [.. "MPF\0"u8.ToArray(), 1, 2, 3]),
                StripFixtures.Segment(0xE0, [.. "JFXX\0"u8.ToArray(), 0x10, 1, 2]),
                StripFixtures.Segment(0xE1, "http://ns.adobe.com/xap/1.0/\0<x:xmpmeta/>"u8),
                StripFixtures.Segment(0xEB, "JPXT HDR"u8)),
            .. trailer,
        ];

        var result = Strip(source);

        Assert.Equal(
            MetadataKinds.Thumbnail | MetadataKinds.Xmp | MetadataKinds.Iptc | MetadataKinds.Comment | MetadataKinds.Vendor | MetadataKinds.Trailer,
            result.Removed);
        Assert.Equal(1000, result.TrailerBytes);
        Assert.Equal(new MetadataSurvey(StripContainer.Jpeg, result.Removed, 1000), MetadataStripper.Survey(source));
        var heads = StripFixtures.Segments(result.Bytes).Select(s => s.Head).ToList();
        Assert.Contains("ICC_P", heads);
        Assert.Contains("Adobe", heads);
        foreach (var gone in new[] { "a comment", "Photoshop", "MPF", "JFXX", "ns.adobe.com", "JPXT", "ftypmp42" })
        {
            Assert.False(StripFixtures.Contains(result.Bytes, gone), gone);
        }

        Assert.Equal(StripFixtures.Scan(clean), StripFixtures.Scan(result.Bytes));
        Assert.True(result.Bytes.AsSpan().EndsWith((ReadOnlySpan<byte>)[0xFF, 0xD9]));
    }

    [Fact]
    public void Jpeg_ACommentAfterTheScan_Goes_TheScanStays()
    {
        byte[] clean = StripFixtures.Jpeg();
        byte[] source = [.. clean.AsSpan(0, clean.Length - 2).ToArray(), .. StripFixtures.Segment(0xFE, "late words"u8), 0xFF, 0xD9];

        var result = Strip(source);

        Assert.Equal(MetadataKinds.Comment, result.Removed);
        Assert.Equal(clean, result.Bytes);
    }

    [Fact]
    public void Jpeg_Clean_HasNothingToTake_AndADamagedOne_IsRefused()
    {
        byte[] clean = StripFixtures.Jpeg();
        Assert.Equal(MetadataKinds.None, Strip(clean).Removed);
        Assert.Equal(clean, Strip(clean).Bytes);
        Assert.Equal(MetadataKinds.None, MetadataStripper.Survey(clean)!.Found);

        byte[] damaged = StripFixtures.AfterSoi(clean, [0xFF, 0xE1, 0xFF, 0xF0, .. "Exif\0\0"u8.ToArray()]);
        var (result, error) = MetadataStripper.Strip(damaged);
        Assert.Null(result);
        Assert.Equal(ImageText.StripBroken("JPEG", "a segment runs past the end of the file"), error);
        Assert.Null(MetadataStripper.Survey(damaged));

        Assert.Equal(ImageText.CannotStripLosslessly("this format"), MetadataStripper.Strip(ImageFixtures.Solid(2, 2, ImageFixtures.Red)).Error);
        Assert.Null(MetadataStripper.Survey(ImageFixtures.Solid(2, 2, ImageFixtures.Red)));
    }

    [Fact]
    public void Png_TheTextExifAndTime_Go_ThePictureChunksStayToTheByte()
    {
        byte[] clean = StripFixtures.Png();
        byte[] source =
        [
            .. StripFixtures.BeforeIend(
                clean,
                StripFixtures.Chunk("tEXt", "Comment\0hello neon"u8),
                StripFixtures.Chunk("iTXt", "XML:com.adobe.xmp\0\0\0\0\0<x:xmpmeta/>"u8),
                StripFixtures.Chunk("eXIf", StripFixtures.GeoTiff()),
                StripFixtures.Chunk("tIME", [7, 234, 10, 5, 12, 0, 0]),
                StripFixtures.Chunk("prVt", "a vendor's own"u8)),
            .. "trailing"u8.ToArray(),
        ];

        var result = Strip(source);

        Assert.Equal(clean, result.Bytes);
        Assert.Equal(
            MetadataKinds.Comment | MetadataKinds.Xmp | MetadataKinds.Exif | MetadataKinds.Gps | MetadataKinds.Time | MetadataKinds.Vendor | MetadataKinds.Trailer,
            result.Removed);
        Assert.Equal(8, result.TrailerBytes);
        Assert.Equal(result.Removed, MetadataStripper.Survey(source)!.Found);
        Assert.NotNull(ImageEditor.Info(result.Bytes));
    }

    [Fact]
    public void Png_AnOrientation_IsABareExifChunkBeforeTheData()
    {
        byte[] source = StripFixtures.BeforeIend(StripFixtures.Png(), StripFixtures.Chunk("eXIf", StripFixtures.GeoTiff()));

        var result = Strip(source, 6);

        var chunks = StripFixtures.Chunks(result.Bytes);
        Assert.All(chunks, c => Assert.True(c.CrcOk, c.Type));
        int exif = chunks.FindIndex(c => c.Type == "eXIf");
        Assert.True(exif > 0 && exif < chunks.FindIndex(c => c.Type == "IDAT"));
        Assert.Equal(MetadataStripper.OrientationTiff(6), result.Bytes.AsSpan(chunks[exif].Offset + 8, chunks[exif].Length).ToArray());
        Assert.True(result.OrientationKept);
        Assert.False(ImageFixtures.HasGps(result.Bytes));
    }

    [Fact]
    public void WebP_ExifXmpAndTheUnknown_Go_TheFlagsFollow_TheBitstreamStays()
    {
        byte[] vp8l = StripFixtures.RiffChunk("VP8L", StripFixtures.Vp8l);
        byte[] source =
        [
            .. StripFixtures.WebP(
                StripFixtures.Vp8x(),
                StripFixtures.RiffChunk("ICCP", [1, 2, 3, 4]),
                vp8l,
                StripFixtures.RiffChunk("EXIF", StripFixtures.GeoTiff()),
                StripFixtures.RiffChunk("XMP ", "<x:xmpmeta/>!"u8),   // odd: padded
                StripFixtures.RiffChunk("ZZZZ", [9])),
            .. new byte[10],
        ];

        var result = Strip(source);

        Assert.Equal(["VP8X", "ICCP", "VP8L"], StripFixtures.WebPChunks(result.Bytes));
        Assert.Equal(0, result.Bytes[20] & 0x0C);
        Assert.Equal((uint)(result.Bytes.Length - 8), BinaryPrimitives.ReadUInt32LittleEndian(result.Bytes.AsSpan(4)));
        Assert.True(result.Bytes.AsSpan().EndsWith(vp8l));
        Assert.Equal(MetadataKinds.Exif | MetadataKinds.Gps | MetadataKinds.Xmp | MetadataKinds.Vendor | MetadataKinds.Trailer, result.Removed);
        Assert.Equal(10, result.TrailerBytes);

        var turned = Strip(source, 8);
        Assert.Equal(["VP8X", "ICCP", "VP8L", "EXIF"], StripFixtures.WebPChunks(turned.Bytes));
        Assert.Equal(0x08, turned.Bytes[20] & 0x0C);
        Assert.True(turned.OrientationKept);
        Assert.True(turned.Bytes.AsSpan().EndsWith(MetadataStripper.OrientationTiff(8)));
    }

    [Fact]
    public void WebP_ASimpleOne_HasNowhereForAnOrientation_AndDecodesWhenWindowsCan()
    {
        byte[] simple = StripFixtures.WebP(StripFixtures.RiffChunk("VP8L", StripFixtures.Vp8l));

        var result = Strip(simple, 6);

        Assert.Equal(simple, result.Bytes);
        Assert.Equal(MetadataKinds.None, result.Removed);
        Assert.False(result.OrientationKept);

        byte[] extended = StripFixtures.WebP(StripFixtures.Vp8x(), StripFixtures.RiffChunk("VP8L", StripFixtures.Vp8l), StripFixtures.RiffChunk("XMP ", "<x/>"u8));
        if (ImageEditor.Info(extended) is not null)
        {
            // Only where the WebP codec is installed: the stripped file still reads.
            Assert.NotNull(ImageEditor.Info(Strip(extended).Bytes));
        }
    }

    [Fact]
    public void Gif_CommentsAndXmp_Go_TheLoopAndTheFramesStay()
    {
        byte[] clean = ImageFixtures.AnimatedGif();
        byte[] source =
        [
            .. clean.AsSpan(0, 19).ToArray(),
            0x21, 0xFE, 5, .. "hello"u8.ToArray(), 0,
            0x21, 0xFF, 11, .. "XMP DataXMP"u8.ToArray(), 3, (byte)'a', (byte)'b', (byte)'c', 0,
            .. clean.AsSpan(19).ToArray(),
            .. "after"u8.ToArray(),
        ];

        var result = Strip(source);

        Assert.Equal(clean, result.Bytes);
        Assert.Equal(MetadataKinds.Comment | MetadataKinds.Xmp | MetadataKinds.Trailer, result.Removed);
        Assert.Equal(2, ImageEditor.Info(result.Bytes)!.Frames);
        Assert.Equal(MetadataKinds.None, Strip(clean).Removed);
    }

    [Fact]
    public void Survey_NamesTheKinds_AndTheWordsSayThem()
    {
        Assert.Equal(MetadataKinds.Exif | MetadataKinds.Gps, MetadataStripper.Survey(ImageFixtures.ExifJpeg())!.Found);

        Assert.Equal("EXIF with GPS and a thumbnail, XMP, comments, " + FileText.Size(1500) + " after the picture",
            ImageText.Kinds(MetadataKinds.Exif | MetadataKinds.Gps | MetadataKinds.Thumbnail | MetadataKinds.Xmp | MetadataKinds.Comment | MetadataKinds.Trailer, 1500));
        Assert.Equal("a thumbnail, IPTC, a timestamp, app data", ImageText.Kinds(MetadataKinds.Thumbnail | MetadataKinds.Iptc | MetadataKinds.Time | MetadataKinds.Vendor, 0));
        Assert.Equal("", ImageText.Kinds(MetadataKinds.None, 0));

        var result = Strip(ImageFixtures.ExifJpeg());
        string line = ImageText.Stripped("geo-edited.jpg", result, 5000);
        Assert.Equal("wrote geo-edited.jpg (JPEG, lossless: removed EXIF with GPS; " + FileText.Size(5000) + " → " + FileText.Size(result.Bytes.Length) + ")", line);
        Assert.EndsWith("; the orientation kept", ImageText.Stripped("x.jpg", result with { OrientationKept = true }, 5000, ImageText.ReplacedVerb), StringComparison.Ordinal);
    }
}
