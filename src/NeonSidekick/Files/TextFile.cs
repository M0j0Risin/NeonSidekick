using System.Text;

namespace NeonSidekick.Files;

/// <summary>The encodings a text file is read and written back in (<see cref="TextFile"/>).</summary>
public enum TextForm
{
    Utf8,
    Utf8Bom,
    Utf16Le,
    Utf16Be,
    Utf32Le,
    Utf32Be,

    /// <summary>Neither valid UTF-8 nor marked: a single-byte code page (<see cref="TextFile.LegacyName"/>) that keeps every byte as it was.</summary>
    Legacy,
}

/// <summary>
/// How one file's text was read and is written back (2026-10-22, <see cref="TextFile"/>): its encoding, the byte-order mark it had, and
/// whether that encoding is a guess.
/// </summary>
public sealed class TextCodec
{
    private readonly Encoding _encoding;
    private readonly byte[] _preamble;

    private TextCodec(TextForm form, Encoding encoding, byte[] preamble)
    {
        Form = form;
        _encoding = encoding;
        _preamble = preamble;
    }

    /// <summary>Plain UTF-8, no mark: what a new file is written in.</summary>
    public static readonly TextCodec Utf8 = new(TextForm.Utf8, Strict(new UTF8Encoding(false)), []);

    internal static readonly TextCodec Utf8Bom = new(TextForm.Utf8Bom, Strict(new UTF8Encoding(false)), [0xEF, 0xBB, 0xBF]);

    internal static TextCodec Utf16(bool bigEndian, bool bom) =>
        new(bigEndian ? TextForm.Utf16Be : TextForm.Utf16Le, Strict(new UnicodeEncoding(bigEndian, byteOrderMark: false)), bom ? (bigEndian ? [0xFE, 0xFF] : [0xFF, 0xFE]) : []);

    internal static TextCodec Utf32(bool bigEndian) =>
        new(bigEndian ? TextForm.Utf32Be : TextForm.Utf32Le, Strict(new UTF32Encoding(bigEndian, byteOrderMark: false)), bigEndian ? [0x00, 0x00, 0xFE, 0xFF] : [0xFF, 0xFE, 0x00, 0x00]);

    internal static TextCodec Legacy(Encoding encoding) => new(TextForm.Legacy, encoding, []);

    /// <summary>The form read.</summary>
    public TextForm Form { get; }

    /// <summary>Whether the file began with a byte-order mark.</summary>
    public bool HasBom => _preamble.Length > 0;

    /// <summary>Whether the encoding is a guess (a legacy code page: the bytes don't say which one).</summary>
    public bool Guessed => Form == TextForm.Legacy;

    /// <summary>
    /// The encoding as a read's header names it — null for plain UTF-8, with or without a mark, which says nothing worth a line: "UTF-16 LE",
    /// "UTF-32 BE", "Windows-1252 (a guess)".
    /// </summary>
    public string? Label => Form switch
    {
        TextForm.Utf16Le => "UTF-16 LE",
        TextForm.Utf16Be => "UTF-16 BE",
        TextForm.Utf32Le => "UTF-32 LE",
        TextForm.Utf32Be => "UTF-32 BE",
        TextForm.Legacy => TextFile.LegacyName + " (a guess)",
        _ => null,
    };

    /// <summary>The bytes after the mark as text; a legacy code page keeps every byte (<see cref="TextFile.LegacyName"/>).</summary>
    public string Decode(ReadOnlySpan<byte> bytes)
    {
        var body = bytes[Math.Min(_preamble.Length, bytes.Length)..];
        try
        {
            return _encoding.GetString(body);
        }
        catch (DecoderFallbackException)
        {
            // Only a strict UTF form throws: a malformed tail is shown as U+FFFD rather than refused.
            return Form switch
            {
                TextForm.Utf16Le or TextForm.Utf16Be => new UnicodeEncoding(Form == TextForm.Utf16Be, false).GetString(body),
                TextForm.Utf32Le or TextForm.Utf32Be => new UTF32Encoding(Form == TextForm.Utf32Be, false).GetString(body),
                _ => Encoding.UTF8.GetString(body),
            };
        }
    }

    /// <summary>
    /// <paramref name="text"/> in this encoding, the mark first when the file had one; null when the encoding can't hold a character of it (a
    /// legacy code page and an emoji): the caller refuses with <see cref="FileText.CannotEncode"/> rather than write a <c>?</c>.
    /// </summary>
    public byte[]? Encode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        try
        {
            byte[] body = _encoding.GetBytes(text);
            return _preamble.Length == 0 ? body : [.. _preamble, .. body];
        }
        catch (EncoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>The UTF form that throws on a malformed byte or a lone surrogate, rather than replacing it.</summary>
    private static Encoding Strict(Encoding encoding) => encoding switch
    {
        UTF8Encoding => new UTF8Encoding(false, throwOnInvalidBytes: true),
        UnicodeEncoding unicode => new UnicodeEncoding(unicode.CodePage == 1201, false, throwOnInvalidBytes: true),
        UTF32Encoding utf32 => new UTF32Encoding(utf32.CodePage == 12001, false, throwOnInvalidCharacters: true),
        _ => encoding,
    };
}

/// <summary>
/// A text file's encoding found and kept (2026-10-22, the fix plan's mechanism 2; review G4, Q1, Q3, Q4, G12): everything was read as UTF-8,
/// so a Windows-1252, GB18030 or Shift-JIS file's every non-ASCII character came back as U+FFFD — and a <c>patch_file</c> or a modeless
/// <c>write_file</c> then wrote those over the real bytes — while UTF-16 text (PowerShell 5.1's <c>&gt;</c>, even with its mark) was refused
/// as binary for its NULs. Now: a mark says UTF-8, UTF-16 or UTF-32; a NUL pattern says BOM-less UTF-16; valid UTF-8 is UTF-8; anything else
/// is read in a single-byte code page that round-trips every byte (Windows-1252 where all 256 bytes survive, else ISO-8859-1), the header
/// saying so. A write back re-encodes in the file's own form and mark; new text a code page can't hold is refused, never written as
/// <c>?</c>. No CJK code page is guessed: such a file round-trips as bytes, and the header calls its encoding a guess. Pure but for the
/// code pages' one registration.
/// </summary>
public static class TextFile
{
    /// <summary>How far into a file the binary and UTF-16 probes look (<see cref="WorkingDirectory.BinaryProbeBytes"/>'s reach).</summary>
    private const int ProbeBytes = 8192;

    private static readonly Lazy<Encoding> s_legacy = new(PickLegacy);

    /// <summary>The single-byte code page a file that is neither UTF-8 nor marked is read in.</summary>
    public static string LegacyName => s_legacy.Value.CodePage == 1252 ? "Windows-1252" : "ISO-8859-1";

    /// <summary>
    /// Whether <paramref name="bytes"/> are not text: a NUL in the probe that no UTF-16 or UTF-32 form explains, or a PDF's header (the rule
    /// <see cref="WorkingDirectory.LooksBinary"/> kept, now with the encodings, review Q4).
    /// </summary>
    public static bool LooksBinary(ReadOnlySpan<byte> bytes)
    {
        if (Marked(bytes) is not null || Utf16Pattern(bytes) is not null)
        {
            return false;
        }

        return bytes[..Math.Min(ProbeBytes, bytes.Length)].IndexOf((byte)0) >= 0 || PdfFile.LooksLikePdf(bytes);
    }

    /// <summary><paramref name="bytes"/> as text, with how it was read; call after <see cref="LooksBinary"/> said text.</summary>
    public static string Decode(ReadOnlySpan<byte> bytes, out TextCodec codec)
    {
        codec = Detect(bytes);
        return codec.Decode(bytes);
    }

    /// <summary>How <paramref name="bytes"/> read: the mark, a UTF-16 NUL pattern, valid UTF-8, or the legacy code page.</summary>
    public static TextCodec Detect(ReadOnlySpan<byte> bytes)
    {
        if (Marked(bytes) is { } marked)
        {
            return marked;
        }

        if (Utf16Pattern(bytes) is { } bigEndian)
        {
            return TextCodec.Utf16(bigEndian, bom: false);
        }

        return System.Text.Unicode.Utf8.IsValid(bytes) ? TextCodec.Utf8 : TextCodec.Legacy(s_legacy.Value);
    }

    private static TextCodec? Marked(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0 && bytes[3] == 0)
        {
            return TextCodec.Utf32(bigEndian: false);
        }

        if (bytes.Length >= 4 && bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0xFE && bytes[3] == 0xFF)
        {
            return TextCodec.Utf32(bigEndian: true);
        }

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return TextCodec.Utf8Bom;
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return TextCodec.Utf16(bigEndian: false, bom: true);
        }

        return bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF ? TextCodec.Utf16(bigEndian: true, bom: true) : null;
    }

    /// <summary>
    /// Whether the probe is BOM-less UTF-16 by its NULs: at least 16 pairs, the high byte of most characters zero (Latin text) and the low
    /// byte almost never — true for big-endian, false for little-endian, null for neither.
    /// </summary>
    private static bool? Utf16Pattern(ReadOnlySpan<byte> bytes)
    {
        var probe = bytes[..Math.Min(ProbeBytes, bytes.Length & ~1)];
        int pairs = probe.Length / 2;
        if (pairs < 16)
        {
            return null;
        }

        int evenZero = 0, oddZero = 0;
        for (int i = 0; i < probe.Length; i += 2)
        {
            evenZero += probe[i] == 0 ? 1 : 0;
            oddZero += probe[i + 1] == 0 ? 1 : 0;
        }

        if (oddZero * 10 >= pairs * 4 && evenZero * 20 <= pairs)
        {
            return false;
        }

        return evenZero * 10 >= pairs * 4 && oddZero * 20 <= pairs ? true : null;
    }

    /// <summary>Windows-1252 when every byte decodes and encodes back to itself, else ISO-8859-1, which always does.</summary>
    private static Encoding PickLegacy()
    {
        Web.PageCharset.EnsureCodePages();
        try
        {
            var cp1252 = Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            byte[] all = new byte[256];
            for (int i = 0; i < 256; i++)
            {
                all[i] = (byte)i;
            }

            if (cp1252.GetBytes(cp1252.GetString(all)).AsSpan().SequenceEqual(all))
            {
                return cp1252;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or DecoderFallbackException or EncoderFallbackException)
        {
        }

        return Encoding.GetEncoding("iso-8859-1", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }
}
