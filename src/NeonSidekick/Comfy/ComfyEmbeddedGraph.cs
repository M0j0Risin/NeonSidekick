using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace NeonSidekick.Comfy;

/// <summary>
/// The API graph a ComfyUI picture carries (2026-11-25, the workflow manager's import from a picture): ComfyUI's save nodes write the graph it
/// ran as <c>prompt</c> beside the UI graph as <c>workflow</c>. A PNG holds them as <c>tEXt</c>, <c>zTXt</c> or <c>iTXt</c> chunks (an animated
/// PNG as a <c>comf</c> chunk after the image data, laid out as <c>tEXt</c>); a WebP in its <c>EXIF</c> chunk, the graph as tag 0x0110 reading
/// <c>prompt:{…}</c> (tag 0x010F and down the UI's), in either byte order (ComfyUI's library writes big-endian). Every length is held to the
/// file and the text to <see cref="MaxTextBytes"/> inflated, so a truncated file or a zip bomb is "no graph", never a throw. Pure.
/// </summary>
public static class ComfyEmbeddedGraph
{
    /// <summary>The most text one chunk or tag may hold once inflated.</summary>
    public const int MaxTextBytes = 8 * 1024 * 1024;

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Whether <paramref name="bytes"/> is a PNG or a WebP by its own header (the name's extension is never asked).</summary>
    public static bool IsPicture(ReadOnlySpan<byte> bytes) => IsPng(bytes) || IsWebp(bytes);

    private static bool IsPng(ReadOnlySpan<byte> bytes) => bytes.StartsWith(PngSignature);

    private static bool IsWebp(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8);

    /// <summary>
    /// The API graph's JSON text in the picture <paramref name="bytes"/>, or the problem as the end of a sentence about the file
    /// (<see cref="ComfyText.EmbeddedUiOnly"/> when only the UI graph is there, <see cref="ComfyText.EmbeddedNone"/> when neither is).
    /// </summary>
    public static (string? Json, string? Problem) Read(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var texts = IsPng(bytes) ? PngTexts(bytes) : IsWebp(bytes) ? WebpTexts(bytes) : [];
        if (texts.FirstOrDefault(t => t.Key == "prompt") is { Value: { } prompt } && prompt.TrimStart().StartsWith('{'))
        {
            return (prompt, null);
        }

        return texts.Any(t => t.Key == "workflow") ? (null, ComfyText.EmbeddedUiOnly) : (null, ComfyText.EmbeddedNone);
    }

    /// <summary>The PNG's text entries by keyword, in file order: <c>tEXt</c> and <c>comf</c> as Latin-1, <c>zTXt</c> inflated, <c>iTXt</c> as UTF-8.</summary>
    private static List<(string Key, string Value)> PngTexts(byte[] bytes)
    {
        var texts = new List<(string, string)>();
        int at = PngSignature.Length;
        while (at <= bytes.Length - 12)
        {
            uint length = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at, 4));
            if (length > (uint)(bytes.Length - at - 12))
            {
                break;   // truncated: what was read stands
            }

            string type = Encoding.ASCII.GetString(bytes, at + 4, 4);
            var data = bytes.AsSpan(at + 8, (int)length);
            if (type is "tEXt" or "comf" or "zTXt" or "iTXt" && PngText(type, data) is { } text)
            {
                texts.Add(text);
            }

            if (type == "IEND")
            {
                break;
            }

            at += 12 + (int)length;
        }

        return texts;
    }

    private static (string Key, string Value)? PngText(string type, ReadOnlySpan<byte> data)
    {
        int nul = data.IndexOf((byte)0);
        if (nul <= 0)
        {
            return null;
        }

        string key = Encoding.Latin1.GetString(data[..nul]);
        var rest = data[(nul + 1)..];
        switch (type)
        {
            case "tEXt" or "comf":
                return rest.Length <= MaxTextBytes ? (key, Encoding.Latin1.GetString(rest)) : null;
            case "zTXt":
                return rest.Length > 0 && rest[0] == 0 && Inflate(rest[1..]) is { } inflated ? (key, Encoding.Latin1.GetString(inflated)) : null;
            default:
                // iTXt: compression flag, method, language\0, translated keyword\0, then the text.
                if (rest.Length < 2)
                {
                    return null;
                }

                bool compressed = rest[0] == 1;
                var tail = rest[2..];
                for (int skip = 0; skip < 2; skip++)
                {
                    int end = tail.IndexOf((byte)0);
                    if (end < 0)
                    {
                        return null;
                    }

                    tail = tail[(end + 1)..];
                }

                byte[]? text = compressed ? (rest[1] == 0 ? Inflate(tail) : null) : tail.Length <= MaxTextBytes ? tail.ToArray() : null;
                return text is null ? null : (key, Encoding.UTF8.GetString(text));
        }
    }

    /// <summary>zlib data inflated, or null when it is broken or inflates past <see cref="MaxTextBytes"/>.</summary>
    private static byte[]? Inflate(ReadOnlySpan<byte> data)
    {
        try
        {
            using var input = new MemoryStream(data.ToArray());
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = zlib.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (output.Length + read > MaxTextBytes)
                {
                    return null;
                }

                output.Write(buffer, 0, read);
            }

            return output.ToArray();
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>The WebP's EXIF tags read as <c>key:value</c> texts (ComfyUI's form), from IFD0.</summary>
    private static List<(string Key, string Value)> WebpTexts(byte[] bytes)
    {
        var texts = new List<(string, string)>();
        int at = 12;
        while (at <= bytes.Length - 8)
        {
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at + 4, 4));
            if (size > (uint)(bytes.Length - at - 8))
            {
                break;
            }

            if (bytes.AsSpan(at, 4).SequenceEqual("EXIF"u8))
            {
                ExifTexts(bytes.AsSpan(at + 8, (int)size), texts);
            }

            at += 8 + (int)size + (int)(size & 1);
        }

        return texts;
    }

    private static void ExifTexts(ReadOnlySpan<byte> exif, List<(string, string)> texts)
    {
        if (exif.StartsWith("Exif\0\0"u8))
        {
            exif = exif[6..];
        }

        if (exif.Length < 8)
        {
            return;
        }

        bool big = exif[..2].SequenceEqual("MM"u8);
        if (!big && !exif[..2].SequenceEqual("II"u8))
        {
            return;
        }

        uint U32(ReadOnlySpan<byte> s) => big ? BinaryPrimitives.ReadUInt32BigEndian(s) : BinaryPrimitives.ReadUInt32LittleEndian(s);
        ushort U16(ReadOnlySpan<byte> s) => big ? BinaryPrimitives.ReadUInt16BigEndian(s) : BinaryPrimitives.ReadUInt16LittleEndian(s);

        uint ifd = U32(exif.Slice(4, 4));
        if (ifd > (uint)(exif.Length - 2))
        {
            return;
        }

        int count = U16(exif.Slice((int)ifd, 2));
        for (int e = 0; e < count; e++)
        {
            int entry = (int)ifd + 2 + (e * 12);
            if (entry > exif.Length - 12)
            {
                return;
            }

            ushort tag = U16(exif.Slice(entry, 2));
            ushort kind = U16(exif.Slice(entry + 2, 2));
            uint length = U32(exif.Slice(entry + 4, 4));
            if (tag is < 0x0100 or > 0x0110 || kind is not (1 or 2 or 7) || length > MaxTextBytes)
            {
                continue;
            }

            ReadOnlySpan<byte> value;
            if (length <= 4)
            {
                value = exif.Slice(entry + 8, (int)length);
            }
            else
            {
                uint offset = U32(exif.Slice(entry + 8, 4));
                if (offset > (uint)exif.Length || length > (uint)exif.Length - offset)
                {
                    continue;
                }

                value = exif.Slice((int)offset, (int)length);
            }

            string text = Encoding.UTF8.GetString(value).TrimEnd('\0');
            int colon = text.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0)
            {
                texts.Add((text[..colon], text[(colon + 1)..]));
            }
        }
    }
}
