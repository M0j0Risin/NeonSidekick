using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace NeonSidekick.EventLog;

/// <summary>
/// The values <c>EvtRender</c> writes (2026-10-15, the event-log plan): an array of 16-byte <c>EVT_VARIANT</c>s — the value or a
/// pointer at offset 0, the count at 8, the type at 12 (the array flag <c>0x80</c> on it) — with every string, SID, GUID, binary
/// and array they point at in the same buffer. Decoded here from a span and the address the buffer sat at, so a pointer is read as
/// an offset into the span and one outside it is no value, never a read of memory the buffer does not own. Portable: a test builds a
/// buffer by hand at a made-up address. A SID is formatted by hand (<c>S-1-5-21-…</c>), not by <c>SecurityIdentifier</c>, so it stays
/// portable too. Pure.
/// </summary>
public static class EventLogVariant
{
    /// <summary>The size of one <c>EVT_VARIANT</c>.</summary>
    public const int Size = 16;

    /// <summary>The array flag on a variant's type.</summary>
    public const int ArrayFlag = 0x80;

    // EVT_VARIANT_TYPE.
    public const int TypeNull = 0;
    public const int TypeString = 1;
    public const int TypeAnsiString = 2;
    public const int TypeSByte = 3;
    public const int TypeByte = 4;
    public const int TypeInt16 = 5;
    public const int TypeUInt16 = 6;
    public const int TypeInt32 = 7;
    public const int TypeUInt32 = 8;
    public const int TypeInt64 = 9;
    public const int TypeUInt64 = 10;
    public const int TypeSingle = 11;
    public const int TypeDouble = 12;
    public const int TypeBoolean = 13;
    public const int TypeBinary = 14;
    public const int TypeGuid = 15;
    public const int TypeSizeT = 16;
    public const int TypeFileTime = 17;
    public const int TypeSysTime = 18;
    public const int TypeSid = 19;
    public const int TypeHexInt32 = 20;
    public const int TypeHexInt64 = 21;

    /// <summary>The type of variant <paramref name="index"/>, the array flag included.</summary>
    public static int Type(ReadOnlySpan<byte> buffer, int index) => BinaryPrimitives.ReadInt32LittleEndian(buffer[(index * Size + 12)..]);

    /// <summary>The count of variant <paramref name="index"/> (an array's elements; a string's characters as <c>EvtRender</c> leaves it).</summary>
    public static int Count(ReadOnlySpan<byte> buffer, int index) => BinaryPrimitives.ReadInt32LittleEndian(buffer[(index * Size + 8)..]);

    /// <summary>
    /// Variant <paramref name="index"/> of <paramref name="buffer"/> (which sat at <paramref name="baseAddress"/>): a string, a
    /// whole number (<see cref="long"/> or <see cref="ulong"/>), a <see cref="double"/>, a <see cref="bool"/>, a UTC
    /// <see cref="DateTimeOffset"/>, a <see cref="Guid"/>, a SID as text, bytes, an array of those as <see cref="object"/>[], or null.
    /// </summary>
    public static object? Decode(ReadOnlySpan<byte> buffer, nint baseAddress, int index)
    {
        int at = index * Size;
        if (at < 0 || at + Size > buffer.Length)
        {
            return null;
        }

        int type = Type(buffer, index);
        int count = Count(buffer, index);
        var slot = buffer.Slice(at, 8);
        if ((type & ArrayFlag) != 0)
        {
            return Array(buffer, baseAddress, type & ~ArrayFlag, count, Pointer(slot));
        }

        return type switch
        {
            TypeNull => null,
            TypeString => Utf16(buffer, Offset(buffer, baseAddress, Pointer(slot))),
            TypeAnsiString => Ansi(buffer, Offset(buffer, baseAddress, Pointer(slot))),
            TypeSByte => (long)(sbyte)slot[0],
            TypeByte => (long)slot[0],
            TypeInt16 => (long)BinaryPrimitives.ReadInt16LittleEndian(slot),
            TypeUInt16 => (long)BinaryPrimitives.ReadUInt16LittleEndian(slot),
            TypeInt32 => (long)BinaryPrimitives.ReadInt32LittleEndian(slot),
            TypeUInt32 or TypeHexInt32 => (long)BinaryPrimitives.ReadUInt32LittleEndian(slot),
            TypeInt64 => BinaryPrimitives.ReadInt64LittleEndian(slot),
            TypeUInt64 or TypeHexInt64 => BinaryPrimitives.ReadUInt64LittleEndian(slot),
            TypeSizeT => (ulong)Pointer(slot),
            TypeSingle => (double)BinaryPrimitives.ReadSingleLittleEndian(slot),
            TypeDouble => BinaryPrimitives.ReadDoubleLittleEndian(slot),
            TypeBoolean => BinaryPrimitives.ReadInt32LittleEndian(slot) != 0,
            TypeFileTime => FileTime(BinaryPrimitives.ReadInt64LittleEndian(slot)),
            TypeSysTime => SystemTime(buffer, Offset(buffer, baseAddress, Pointer(slot))),
            TypeGuid => Guid(buffer, Offset(buffer, baseAddress, Pointer(slot))),
            TypeSid => Sid(buffer, Offset(buffer, baseAddress, Pointer(slot))),
            TypeBinary => Bytes(buffer, Offset(buffer, baseAddress, Pointer(slot)), count),
            _ => null,
        };
    }

    /// <summary>
    /// The values <paramref name="insert"/> changes, each with its new text (2026-10-20, a review: only a plain string was rewritten, so an
    /// ANSI string, a string array or a binary value under a secret's name reached the redacted message as it was): every value of
    /// <paramref name="count"/> is read as text (<see cref="Text"/>), handed to <paramref name="insert"/> with its position, and kept when it
    /// came back different — the caller writes each one back as a plain string. A value that decodes to nothing is handed over as empty. A null
    /// value is shown to <paramref name="insert"/> too (as empty, never rewritten), so the caller counts every value (2026-10-23, the branch
    /// review of DP7: a last null value made the count one short and withheld the message).
    /// </summary>
    public static IReadOnlyList<(int Index, string Text)> Rewrites(ReadOnlySpan<byte> buffer, nint baseAddress, int count, Func<int, string, string> insert)
    {
        ArgumentNullException.ThrowIfNull(insert);
        var rewrites = new List<(int, string)>();
        for (int i = 0; i < count && (i + 1) * Size <= buffer.Length; i++)
        {
            int type = Type(buffer, i);
            if (type == TypeNull)
            {
                _ = insert(i, "");
                continue;
            }

            string original = Text(Decode(buffer, baseAddress, i), type);
            string rewritten = insert(i, original);
            if (!string.Equals(rewritten, original, StringComparison.Ordinal))
            {
                rewrites.Add((i, rewritten));
            }
        }

        return rewrites;
    }

    /// <summary>A decoded value as text: a hex number as <c>0x…</c> where its type says so, a time in ISO 8601 UTC, bytes as hex, an array joined by commas.</summary>
    public static string Text(object? value, int type = TypeNull) => value switch
    {
        null => "",
        string s => s,
        long n when (type & ~ArrayFlag) == TypeHexInt32 => "0x" + n.ToString("X", CultureInfo.InvariantCulture),
        ulong n when (type & ~ArrayFlag) is TypeHexInt64 or TypeSizeT => "0x" + n.ToString("X", CultureInfo.InvariantCulture),
        long n => n.ToString(CultureInfo.InvariantCulture),
        ulong n => n.ToString(CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        DateTimeOffset t => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture),
        Guid g => g.ToString("B", CultureInfo.InvariantCulture).ToUpperInvariant(),
        byte[] bytes => Convert.ToHexString(bytes),
        object?[] items => string.Join(", ", items.Select(i => Text(i, type))),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
    };

    /// <summary>
    /// A SID's bytes as <c>S-R-I-S…</c>: the revision, the 48-bit authority (big-endian; past 32 bits written in hex as Windows does),
    /// each 32-bit sub-authority (little-endian). Null for bytes too short to be one.
    /// </summary>
    public static string? FormatSid(ReadOnlySpan<byte> sid)
    {
        if (sid.Length < 8)
        {
            return null;
        }

        int subCount = sid[1];
        if (sid.Length < 8 + subCount * 4)
        {
            return null;
        }

        ulong authority = 0;
        for (int i = 2; i < 8; i++)
        {
            authority = (authority << 8) | sid[i];
        }

        var text = new StringBuilder("S-");
        text.Append(sid[0].ToString(CultureInfo.InvariantCulture)).Append('-');
        text.Append(authority >= 1UL << 32 ? "0x" + authority.ToString("X12", CultureInfo.InvariantCulture) : authority.ToString(CultureInfo.InvariantCulture));
        for (int i = 0; i < subCount; i++)
        {
            text.Append('-').Append(BinaryPrimitives.ReadUInt32LittleEndian(sid[(8 + i * 4)..]).ToString(CultureInfo.InvariantCulture));
        }

        return text.ToString();
    }

    private static long Pointer(ReadOnlySpan<byte> slot) => BinaryPrimitives.ReadInt64LittleEndian(slot);

    /// <summary>A pointer as an offset into the buffer; −1 for null or one outside it.</summary>
    private static int Offset(ReadOnlySpan<byte> buffer, nint baseAddress, long pointer)
    {
        if (pointer == 0)
        {
            return -1;
        }

        long offset = pointer - baseAddress;
        return offset < 0 || offset >= buffer.Length ? -1 : (int)offset;
    }

    private static string? Utf16(ReadOnlySpan<byte> buffer, int offset)
    {
        if (offset < 0)
        {
            return null;
        }

        var tail = buffer[offset..];
        int end = 0;
        while (end + 1 < tail.Length && (tail[end] != 0 || tail[end + 1] != 0))
        {
            end += 2;
        }

        return Encoding.Unicode.GetString(tail[..end]);
    }

    private static string? Ansi(ReadOnlySpan<byte> buffer, int offset)
    {
        if (offset < 0)
        {
            return null;
        }

        var tail = buffer[offset..];
        int end = tail.IndexOf((byte)0);
        return Encoding.Latin1.GetString(end < 0 ? tail : tail[..end]);
    }

    private static DateTimeOffset? FileTime(long fileTime)
    {
        if (fileTime <= 0 || fileTime > DateTime.MaxValue.ToFileTimeUtc())
        {
            return null;
        }

        return new DateTimeOffset(DateTime.FromFileTimeUtc(fileTime), TimeSpan.Zero);
    }

    private static DateTimeOffset? SystemTime(ReadOnlySpan<byte> buffer, int offset)
    {
        if (offset < 0 || offset + 16 > buffer.Length)
        {
            return null;
        }

        var t = buffer.Slice(offset, 16);
        try
        {
            return new DateTimeOffset(
                BinaryPrimitives.ReadUInt16LittleEndian(t), BinaryPrimitives.ReadUInt16LittleEndian(t[2..]), BinaryPrimitives.ReadUInt16LittleEndian(t[6..]),
                BinaryPrimitives.ReadUInt16LittleEndian(t[8..]), BinaryPrimitives.ReadUInt16LittleEndian(t[10..]), BinaryPrimitives.ReadUInt16LittleEndian(t[12..]),
                BinaryPrimitives.ReadUInt16LittleEndian(t[14..]), TimeSpan.Zero);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static Guid? Guid(ReadOnlySpan<byte> buffer, int offset) =>
        offset < 0 || offset + 16 > buffer.Length ? null : new Guid(buffer.Slice(offset, 16));

    private static string? Sid(ReadOnlySpan<byte> buffer, int offset) => offset < 0 ? null : FormatSid(buffer[offset..]);

    private static byte[]? Bytes(ReadOnlySpan<byte> buffer, int offset, int count) =>
        offset < 0 || count < 0 || offset + count > buffer.Length ? null : buffer.Slice(offset, count).ToArray();

    /// <summary>An array variant's elements: fixed-size numbers in a run, strings (and SIDs) as a run of pointers.</summary>
    private static object?[]? Array(ReadOnlySpan<byte> buffer, nint baseAddress, int type, int count, long pointer)
    {
        int offset = Offset(buffer, baseAddress, pointer);
        if (offset < 0 || count < 0 || count > 65_536)
        {
            return null;
        }

        int width = type switch
        {
            TypeSByte or TypeByte => 1,
            TypeInt16 or TypeUInt16 => 2,
            TypeInt32 or TypeUInt32 or TypeHexInt32 or TypeSingle or TypeBoolean => 4,
            TypeGuid or TypeSysTime => 16,
            _ => 8,
        };
        if (type == TypeBinary)
        {
            // A binary array is one run of bytes, its count the bytes (2026-10-23, the admin review's C13: its elements read as nulls).
            return [Bytes(buffer, offset, count)];
        }

        if (offset + (long)count * width > buffer.Length)
        {
            return null;
        }

        var items = new object?[count];
        for (int i = 0; i < count; i++)
        {
            var cell = buffer.Slice(offset + i * width, width);
            items[i] = type switch
            {
                TypeString => Utf16(buffer, Offset(buffer, baseAddress, BinaryPrimitives.ReadInt64LittleEndian(cell))),
                TypeAnsiString => Ansi(buffer, Offset(buffer, baseAddress, BinaryPrimitives.ReadInt64LittleEndian(cell))),
                TypeSid => Sid(buffer, Offset(buffer, baseAddress, BinaryPrimitives.ReadInt64LittleEndian(cell))),
                TypeSByte => (long)(sbyte)cell[0],
                TypeByte => (long)cell[0],
                TypeInt16 => (long)BinaryPrimitives.ReadInt16LittleEndian(cell),
                TypeUInt16 => (long)BinaryPrimitives.ReadUInt16LittleEndian(cell),
                TypeInt32 => (long)BinaryPrimitives.ReadInt32LittleEndian(cell),
                TypeUInt32 or TypeHexInt32 => (long)BinaryPrimitives.ReadUInt32LittleEndian(cell),
                TypeInt64 => BinaryPrimitives.ReadInt64LittleEndian(cell),
                TypeUInt64 or TypeHexInt64 or TypeSizeT => BinaryPrimitives.ReadUInt64LittleEndian(cell),
                TypeSingle => (double)BinaryPrimitives.ReadSingleLittleEndian(cell),
                TypeDouble => BinaryPrimitives.ReadDoubleLittleEndian(cell),
                TypeBoolean => BinaryPrimitives.ReadInt32LittleEndian(cell) != 0,
                TypeFileTime => FileTime(BinaryPrimitives.ReadInt64LittleEndian(cell)),
                TypeGuid => new Guid(cell),
                TypeSysTime => SystemTime(buffer, offset + i * width),   // C13: inline, 16 bytes each
                _ => null,
            };
        }

        return items;
    }
}
