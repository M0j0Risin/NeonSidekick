using System.Buffers.Binary;
using System.Text;

namespace NeonSidekick.Images;

/// <summary>The containers <see cref="MetadataStripper"/> walks: the four whose metadata sits in segments or chunks of its own.</summary>
public enum StripContainer
{
    Jpeg,
    Png,
    WebP,
    Gif,
}

/// <summary>The kinds of metadata a picture carries (<see cref="MetadataStripper.Survey"/>) or a strip took out (<see cref="StripResult.Removed"/>).</summary>
[Flags]
public enum MetadataKinds
{
    None = 0,
    Exif = 1,

    /// <summary>The EXIF block points at a GPS directory: the picture says where it was taken.</summary>
    Gps = 2,

    /// <summary>A preview picture inside the EXIF (IFD1) or a JFXX segment: a cropped photo's thumbnail can still show the whole scene.</summary>
    Thumbnail = 4,
    Xmp = 8,
    Iptc = 16,

    /// <summary>JPEG <c>COM</c>, PNG <c>tEXt</c>/<c>zTXt</c>/<c>iTXt</c>, GIF comment extensions.</summary>
    Comment = 32,

    /// <summary>PNG <c>tIME</c>.</summary>
    Time = 64,

    /// <summary>Any other application segment or ancillary chunk: maker notes, MPF, FPXR, Ducky, unknown chunks.</summary>
    Vendor = 128,

    /// <summary>Bytes after the picture's end: a motion photo's video, a Samsung trailer, an Ultra HDR gain map.</summary>
    Trailer = 256,
}

/// <summary>What a picture carries: its container, the kinds found, and how many bytes follow its end.</summary>
public sealed record MetadataSurvey(StripContainer Container, MetadataKinds Found, long TrailerBytes);

/// <summary>A picture stripped: the new bytes, what went, the bytes cut after its end, and whether a bare orientation was written back.</summary>
public sealed record StripResult(byte[] Bytes, StripContainer Container, MetadataKinds Removed, long TrailerBytes, bool OrientationKept);

/// <summary>
/// A lossless metadata strip (2026-10-05, the user's ask: <c>image_edit</c>'s strip re-encoded the pixels, so a JPEG lost a little and a
/// WebP came out as a PNG). The container is edited, never the picture: JPEG's marker segments, PNG's chunks, WebP's RIFF chunks and
/// GIF's blocks are walked, the metadata ones left out and every other byte copied as it was, so the compressed pixels are the
/// source's to the bit. What stays is what draws the picture — the tables, the scans, the colour profile (ICC, JPEG's Adobe
/// segment for CMYK, PNG's colour chunks), the animation — and what goes is EXIF (with its GPS and thumbnail), XMP, IPTC,
/// comments, timestamps, vendor segments and anything after the picture's end (motion-photo video, gain maps: the user's call,
/// privacy first). An orientation other than upright is written back as a bare EXIF block holding that one tag, or a phone photo
/// would show sideways. A segment or chunk that runs past the end is refused, never guessed at. Pure; no codec is asked.
/// </summary>
public static class MetadataStripper
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    // The PNG chunks that draw the picture (or its colour, or its animation): kept. Any other ancillary chunk goes.
    private static readonly HashSet<string> PngKept = new(StringComparer.Ordinal)
    {
        "IHDR", "PLTE", "IDAT", "IEND", "tRNS", "cHRM", "gAMA", "iCCP", "sBIT", "sRGB", "cICP", "mDCV", "cLLI", "bKGD", "hIST", "pHYs", "sPLT", "acTL", "fcTL", "fdAT",
    };

    // The WebP chunks that draw the picture: kept. EXIF, XMP and anything unknown go.
    private static readonly HashSet<string> WebPKept = new(StringComparer.Ordinal) { "VP8X", "ICCP", "ANIM", "ANMF", "ALPH", "VP8 ", "VP8L" };

    /// <summary>The container <paramref name="bytes"/> start as, or null for any other (BMP, TIFF, HEIF…). Pure.</summary>
    public static StripContainer? ContainerOf(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return StripContainer.Jpeg;
        }

        if (bytes.StartsWith(PngSignature))
        {
            return StripContainer.Png;
        }

        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8))
        {
            return StripContainer.WebP;
        }

        return bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8) ? StripContainer.Gif : null;
    }

    /// <summary>The container a path's extension names, for a menu row that has not read the file; null for any other. Pure.</summary>
    public static StripContainer? ContainerOfPath(string? path)
    {
        string extension = Path.GetExtension(path ?? "");
        foreach (var container in Enum.GetValues<StripContainer>())
        {
            if (Extensions(container).Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                return container;
            }
        }

        return null;
    }

    /// <summary>A container's extensions, the first the one a new file gets. Pure.</summary>
    public static IReadOnlyList<string> Extensions(StripContainer container) => container switch
    {
        StripContainer.Jpeg => [".jpg", ".jpeg", ".jfif", ".jpe"],
        StripContainer.Png => [".png"],
        StripContainer.WebP => [".webp"],
        _ => [".gif"],
    };

    /// <summary>The extension a stripped copy of <paramref name="source"/> gets: the source's own when it is one of the container's, else the first. Pure.</summary>
    public static string ExtensionFor(StripContainer container, string source)
    {
        string own = Path.GetExtension(source ?? "");
        var list = Extensions(container);
        return list.Contains(own, StringComparer.OrdinalIgnoreCase) ? own : list[0];
    }

    /// <summary>A container's name for a sentence: <c>JPEG</c>, <c>PNG</c>, <c>WEBP</c>, <c>GIF</c>.</summary>
    public static string Label(StripContainer container) => container switch
    {
        StripContainer.Jpeg => "JPEG",
        StripContainer.Png => "PNG",
        StripContainer.WebP => "WEBP",
        _ => "GIF",
    };

    /// <summary>What <paramref name="bytes"/> carry, or null for another container or a damaged one. Reads only.</summary>
    public static MetadataSurvey? Survey(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (ContainerOf(bytes) is not { } container)
        {
            return null;
        }

        var walk = Walk(container, bytes, null, null);
        return walk.Error is null ? new MetadataSurvey(container, walk.Found, walk.Trailer) : null;
    }

    /// <summary>
    /// <paramref name="source"/> without its metadata, or the refusal. <paramref name="orientation"/> is the EXIF orientation it
    /// displays with (1–8, PhotoSauce's <c>Orientation</c> numbers them as EXIF does); anything but 1 is written back as a bare EXIF
    /// block (GIF has nowhere to keep one, nor a simple WebP). <see cref="StripResult.Removed"/> is <see cref="MetadataKinds.None"/>
    /// when there was nothing to take out.
    /// </summary>
    public static (StripResult? Result, string? Error) Strip(byte[] source, int orientation = 1)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (ContainerOf(source) is not { } container)
        {
            return (null, ImageText.CannotStripLosslessly("this format"));
        }

        using var output = new MemoryStream(source.Length);
        byte[]? exif = orientation is > 1 and <= 8 ? OrientationTiff(orientation) : null;
        var walk = Walk(container, source, output, exif);
        if (walk.Error is not null)
        {
            return (null, ImageText.StripBroken(Label(container), walk.Error));
        }

        return (new StripResult(output.ToArray(), container, walk.Found, walk.Trailer, walk.OrientationWritten), null);
    }

    /// <summary>
    /// A TIFF block holding only the orientation tag (little-endian): the header, IFD0 with one SHORT entry 0x0112, no next IFD.
    /// 26 bytes. JPEG puts <c>Exif\0\0</c> before it; PNG's <c>eXIf</c> and WebP's <c>EXIF</c> take it bare.
    /// </summary>
    public static byte[] OrientationTiff(int orientation) =>
    [
        (byte)'I', (byte)'I', 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00,
        0x01, 0x00,
        0x12, 0x01, 0x03, 0x00, 0x01, 0x00, 0x00, 0x00, (byte)orientation, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
    ];

    private readonly record struct WalkResult(MetadataKinds Found, long Trailer, bool OrientationWritten, string? Error);

    private static WalkResult Walk(StripContainer container, byte[] s, MemoryStream? o, byte[]? exif) => container switch
    {
        StripContainer.Jpeg => Jpeg(s, o, exif),
        StripContainer.Png => Png(s, o, exif),
        StripContainer.WebP => WebP(s, o, exif),
        _ => Gif(s, o),
    };

    private const string RunsPastEnd = "a segment runs past the end of the file";

    // ---- JPEG ----

    private static WalkResult Jpeg(byte[] s, MemoryStream? o, byte[]? exif)
    {
        int n = s.Length;
        var found = MetadataKinds.None;
        bool pending = exif is not null, written = false;
        o?.Write([0xFF, 0xD8]);

        // The bare orientation APP1, written once: after a leading JFIF APP0, else before the first other segment kept.
        void Orientation()
        {
            if (pending && o is not null)
            {
                int length = 2 + 6 + exif!.Length;
                o.Write([0xFF, 0xE1, (byte)(length >> 8), (byte)length]);
                o.Write("Exif\0\0"u8);
                o.Write(exif);
                written = true;
            }

            pending = false;
        }

        int p = 2;
        while (true)
        {
            if (p >= n || s[p] != 0xFF)
            {
                return new(found, 0, written, p >= n ? "the file ends before its picture" : "a byte where a marker should be");
            }

            while (p < n && s[p] == 0xFF)
            {
                p++;   // fill bytes before a marker are padding, not picture
            }

            if (p >= n)
            {
                return new(found, 0, written, RunsPastEnd);
            }

            byte marker = s[p++];
            if (marker == 0xD9)
            {
                // An end before any scan: a picture of nothing, but the walk is done.
                Orientation();
                o?.Write([0xFF, 0xD9]);
                return Ended(found, n - p, written);
            }

            if (marker is 0x01 or (>= 0xD0 and <= 0xD7))
            {
                o?.Write([0xFF, marker]);   // standalone markers: no length
                continue;
            }

            if (p + 2 > n)
            {
                return new(found, 0, written, RunsPastEnd);
            }

            int length = BinaryPrimitives.ReadUInt16BigEndian(s.AsSpan(p));
            if (length < 2 || p + length > n)
            {
                return new(found, 0, written, RunsPastEnd);
            }

            var payload = s.AsSpan(p + 2, length - 2);
            int end = p + length;
            if (marker == 0xDA)
            {
                Orientation();
                return JpegScans(s, o, p - 2, end, found, written);
            }

            if (marker is >= 0xE0 and <= 0xEF || marker == 0xFE)
            {
                var kind = marker == 0xFE ? MetadataKinds.Comment : AppKind(marker, payload);
                if (kind == MetadataKinds.None)
                {
                    if (marker != 0xE0)
                    {
                        Orientation();
                    }

                    o?.Write([0xFF, marker]);
                    o?.Write(s.AsSpan(p, length));
                    if (marker == 0xE0)
                    {
                        Orientation();   // straight after JFIF, where readers look for it
                    }
                }
                else
                {
                    found |= kind;
                }
            }
            else
            {
                Orientation();
                o?.Write([0xFF, marker]);
                o?.Write(s.AsSpan(p, length));
            }

            p = end;
        }
    }

    // From the first SOS to EOI: the scans copied as they are, an APPn or COM between a progressive JPEG's scans left out, and
    // whatever follows EOI cut. A file with no EOI (cut short) is copied to its end.
    private static WalkResult JpegScans(byte[] s, MemoryStream? o, int start, int q, MetadataKinds found, bool written)
    {
        int n = s.Length, copyFrom = start;
        while (q + 1 < n)
        {
            if (s[q] != 0xFF)
            {
                q++;
                continue;
            }

            byte next = s[q + 1];
            if (next == 0x00 || next is >= 0xD0 and <= 0xD7)
            {
                q += 2;   // a stuffed 0xFF or a restart marker: still the scan
                continue;
            }

            if (next == 0xFF)
            {
                q++;   // fill before a marker
                continue;
            }

            if (next == 0xD9)
            {
                o?.Write(s.AsSpan(copyFrom, q + 2 - copyFrom));
                return Ended(found, n - (q + 2), written);
            }

            if (q + 4 > n)
            {
                break;
            }

            int length = BinaryPrimitives.ReadUInt16BigEndian(s.AsSpan(q + 2));
            if (length < 2 || q + 2 + length > n)
            {
                return new(found, 0, written, RunsPastEnd);
            }

            if (next is >= 0xE0 and <= 0xEF || next == 0xFE)
            {
                var kind = next == 0xFE ? MetadataKinds.Comment : AppKind(next, s.AsSpan(q + 4, length - 2));
                if (kind != MetadataKinds.None)
                {
                    found |= kind;
                    o?.Write(s.AsSpan(copyFrom, q - copyFrom));
                    copyFrom = q + 2 + length;
                }
            }

            q += 2 + length;   // a table, the next scan's header, or a kept segment: its bytes are copied with the run
        }

        o?.Write(s.AsSpan(copyFrom, n - copyFrom));
        return new(found, 0, written, null);
    }

    private static WalkResult Ended(MetadataKinds found, long trailer, bool written) =>
        new(trailer > 0 ? found | MetadataKinds.Trailer : found, trailer, written, null);

    // What an APPn segment is: None when it is kept (JFIF, the ICC profile, Adobe's colour transform), else the kind it carries.
    private static MetadataKinds AppKind(byte marker, ReadOnlySpan<byte> payload) => marker switch
    {
        0xE0 when payload.StartsWith("JFIF\0"u8) => MetadataKinds.None,
        0xE0 when payload.StartsWith("JFXX\0"u8) => MetadataKinds.Thumbnail,
        0xE1 when payload.StartsWith("Exif\0"u8) && payload.Length >= 6 => MetadataKinds.Exif | TiffKinds(payload[6..]),
        0xE1 when payload.StartsWith("http://ns.adobe.com/"u8) => MetadataKinds.Xmp,
        0xE2 when payload.StartsWith("ICC_PROFILE\0"u8) => MetadataKinds.None,
        0xED when payload.StartsWith("Photoshop 3.0\0"u8) => MetadataKinds.Iptc,
        0xEE when payload.StartsWith("Adobe"u8) => MetadataKinds.None,
        _ => MetadataKinds.Vendor,
    };

    // What an EXIF TIFF block holds beyond itself: a GPS directory (IFD0's 0x8825) and a thumbnail (a next IFD after IFD0). A bad
    // offset is "not there", never an exception.
    private static MetadataKinds TiffKinds(ReadOnlySpan<byte> tiff)
    {
        if (tiff.Length < 8)
        {
            return MetadataKinds.None;
        }

        bool little = tiff[0] == (byte)'I' && tiff[1] == (byte)'I';
        if (!little && !(tiff[0] == (byte)'M' && tiff[1] == (byte)'M'))
        {
            return MetadataKinds.None;
        }

        ushort U16(ReadOnlySpan<byte> b) => little ? BinaryPrimitives.ReadUInt16LittleEndian(b) : BinaryPrimitives.ReadUInt16BigEndian(b);
        uint U32(ReadOnlySpan<byte> b) => little ? BinaryPrimitives.ReadUInt32LittleEndian(b) : BinaryPrimitives.ReadUInt32BigEndian(b);
        uint ifd0 = U32(tiff[4..]);
        if (ifd0 > (uint)tiff.Length - 2)
        {
            return MetadataKinds.None;
        }

        int count = U16(tiff[(int)ifd0..]);
        long entriesEnd = ifd0 + 2L + (count * 12L);
        if (entriesEnd > tiff.Length)
        {
            return MetadataKinds.None;
        }

        var kinds = MetadataKinds.None;
        for (int i = 0; i < count; i++)
        {
            var entry = tiff[(int)(ifd0 + 2 + (i * 12))..];
            if (U16(entry) == 0x8825 && U32(entry[8..]) != 0)
            {
                kinds |= MetadataKinds.Gps;
            }
        }

        if (entriesEnd + 4 <= tiff.Length && U32(tiff[(int)entriesEnd..]) != 0)
        {
            kinds |= MetadataKinds.Thumbnail;
        }

        return kinds;
    }

    // ---- PNG ----

    private static WalkResult Png(byte[] s, MemoryStream? o, byte[]? exif)
    {
        int n = s.Length, p = PngSignature.Length;
        var found = MetadataKinds.None;
        bool pending = exif is not null, written = false;
        o?.Write(PngSignature);
        while (p < n)
        {
            if (p + 12 > n)
            {
                return new(found, 0, written, RunsPastEnd);
            }

            uint length = BinaryPrimitives.ReadUInt32BigEndian(s.AsSpan(p));
            if (length > int.MaxValue || p + 12L + length > n)
            {
                return new(found, 0, written, RunsPastEnd);
            }

            string type = Encoding.ASCII.GetString(s, p + 4, 4);
            int end = p + 12 + (int)length;
            bool critical = (s[p + 4] & 0x20) == 0;
            if (PngKept.Contains(type) || critical)
            {
                if (pending && type is "IDAT" or "IEND")
                {
                    // eXIf before the first IDAT, as the spec places it.
                    o?.Write(PngChunk("eXIf"u8, exif!));
                    written = o is not null;
                    pending = false;
                }

                o?.Write(s.AsSpan(p, end - p));
                if (type == "IEND")
                {
                    return Ended(found, n - end, written);
                }
            }
            else
            {
                found |= type switch
                {
                    "eXIf" => MetadataKinds.Exif | TiffKinds(s.AsSpan(p + 8, (int)length)),
                    "iTXt" when s.AsSpan(p + 8, (int)length).StartsWith("XML:com.adobe.xmp\0"u8) => MetadataKinds.Xmp,
                    "tEXt" or "zTXt" or "iTXt" => MetadataKinds.Comment,
                    "tIME" => MetadataKinds.Time,
                    _ => MetadataKinds.Vendor,
                };
            }

            p = end;
        }

        return new(found, 0, written, null);   // no IEND: cut short, copied as far as it goes
    }

    private static byte[] PngChunk(ReadOnlySpan<byte> type, byte[] data)
    {
        var chunk = new byte[12 + data.Length];
        BinaryPrimitives.WriteUInt32BigEndian(chunk, (uint)data.Length);
        type.CopyTo(chunk.AsSpan(4));
        data.CopyTo(chunk.AsSpan(8));
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8 + data.Length), Crc32(chunk.AsSpan(4, 4 + data.Length)));
        return chunk;
    }

    private static readonly uint[] CrcTable = MakeCrcTable();

    private static uint[] MakeCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }

    /// <summary>PNG's CRC-32 (ISO 3309) over a chunk's type and data.</summary>
    public static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        uint c = 0xFFFFFFFFu;
        foreach (byte b in bytes)
        {
            c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        }

        return c ^ 0xFFFFFFFFu;
    }

    // ---- WebP ----

    private static WalkResult WebP(byte[] s, MemoryStream? o, byte[]? exif)
    {
        int n = s.Length;
        long riffEnd = 8L + BinaryPrimitives.ReadUInt32LittleEndian(s.AsSpan(4));
        if (riffEnd > n)
        {
            return new(MetadataKinds.None, 0, false, RunsPastEnd);
        }

        var found = MetadataKinds.None;
        var kept = new List<(string Type, int Offset, int Length)>();
        int vp8x = -1;
        int p = 12;
        while (p < riffEnd)
        {
            if (p + 8 > riffEnd)
            {
                return new(found, 0, false, RunsPastEnd);
            }

            string type = Encoding.ASCII.GetString(s, p, 4);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(s.AsSpan(p + 4));
            long end = p + 8L + size + (size & 1);
            if (p + 8L + size > riffEnd)
            {
                return new(found, 0, false, RunsPastEnd);
            }

            end = Math.Min(end, riffEnd);   // a last odd chunk some writers leave unpadded
            if (WebPKept.Contains(type))
            {
                if (type == "VP8X")
                {
                    vp8x = kept.Count;
                }

                kept.Add((type, p, (int)(end - p)));
            }
            else
            {
                var data = s.AsSpan(p + 8, (int)size);
                found |= type switch
                {
                    "EXIF" => MetadataKinds.Exif | TiffKinds(data.StartsWith("Exif\0\0"u8) ? data[6..] : data),
                    "XMP " => MetadataKinds.Xmp,
                    _ => MetadataKinds.Vendor,
                };
            }

            p = (int)end;
        }

        // A simple (VP8/VP8L) file has no VP8X, so nowhere to say an EXIF chunk is there: the orientation cannot be kept, nor was any.
        bool written = exif is not null && vp8x >= 0 && kept[vp8x].Length > 8;
        if (o is not null)
        {
            using var body = new MemoryStream();
            body.Write("WEBP"u8);
            for (int i = 0; i < kept.Count; i++)
            {
                var (_, offset, length) = kept[i];
                if (i == vp8x && length > 8)
                {
                    var chunk = s.AsSpan(offset, length).ToArray();
                    // The flags byte: XMP (0x04) and EXIF (0x08) cleared, EXIF set again when the orientation goes back.
                    chunk[8] = (byte)((chunk[8] & ~0x0C) | (written ? 0x08 : 0));
                    body.Write(chunk);
                }
                else
                {
                    body.Write(s.AsSpan(offset, length));
                }
            }

            if (written)
            {
                var header = new byte[8];
                "EXIF"u8.CopyTo(header);
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), (uint)exif!.Length);
                body.Write(header);
                body.Write(exif);   // 26 bytes: even, no pad
            }

            var riff = new byte[8];
            "RIFF"u8.CopyTo(riff);
            BinaryPrimitives.WriteUInt32LittleEndian(riff.AsSpan(4), (uint)body.Length);
            o.Write(riff);
            body.Position = 0;
            body.CopyTo(o);
        }

        return Ended(found, n - riffEnd, written);
    }

    // ---- GIF ----

    private static WalkResult Gif(byte[] s, MemoryStream? o)
    {
        int n = s.Length;
        if (n < 13)
        {
            return new(MetadataKinds.None, 0, false, RunsPastEnd);
        }

        int p = 13 + ColourTable(s[10]);
        if (p > n)
        {
            return new(MetadataKinds.None, 0, false, RunsPastEnd);
        }

        o?.Write(s.AsSpan(0, p));
        var found = MetadataKinds.None;
        while (p < n)
        {
            byte block = s[p];
            int start = p;
            if (block == 0x3B)
            {
                o?.WriteByte(0x3B);
                return Ended(found, n - (p + 1), false);
            }

            if (block == 0x2C)
            {
                if (p + 11 > n)
                {
                    return new(found, 0, false, RunsPastEnd);
                }

                p += 10 + ColourTable(s[p + 9]) + 1;   // descriptor, local table, LZW minimum code size
                if (!SkipSubBlocks(s, ref p))
                {
                    return new(found, 0, false, RunsPastEnd);
                }

                o?.Write(s.AsSpan(start, p - start));
                continue;
            }

            if (block != 0x21 || p + 2 > n)
            {
                return new(found, 0, false, block == 0x21 ? RunsPastEnd : "a block that is neither picture nor extension");
            }

            byte label = s[p + 1];
            var kind = MetadataKinds.None;
            if (label == 0xFE)
            {
                kind = MetadataKinds.Comment;
            }
            else if (label == 0xFF && p + 3 <= n)
            {
                var id = s.AsSpan(p + 3, Math.Min(s[p + 2], n - (p + 3)));
                kind = id.StartsWith("NETSCAPE2.0"u8) || id.StartsWith("ANIMEXTS1.0"u8) || id.StartsWith("ICCRGBG1"u8)
                    ? MetadataKinds.None
                    : id.StartsWith("XMP DataXMP"u8) ? MetadataKinds.Xmp : MetadataKinds.Vendor;
            }

            p += 2;
            if (!SkipSubBlocks(s, ref p))
            {
                return new(found, 0, false, RunsPastEnd);
            }

            if (kind == MetadataKinds.None)
            {
                o?.Write(s.AsSpan(start, p - start));   // graphic control, plain text, the loop count, the colour profile
            }
            else
            {
                found |= kind;
            }
        }

        return new(found, 0, false, null);   // no trailer byte: cut short, copied as far as it goes
    }

    // A GIF colour table's bytes from its packed field: 3 × 2^(n+1) when the top bit says there is one.
    private static int ColourTable(byte packed) => (packed & 0x80) != 0 ? 3 * (1 << ((packed & 0x07) + 1)) : 0;

    private static bool SkipSubBlocks(byte[] s, ref int p)
    {
        while (true)
        {
            if (p >= s.Length)
            {
                return false;
            }

            int size = s[p];
            p += 1 + size;
            if (size == 0)
            {
                return p <= s.Length;
            }
        }
    }
}
