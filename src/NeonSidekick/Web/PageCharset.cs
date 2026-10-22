using System.Text;
using System.Text.RegularExpressions;

namespace NeonSidekick.Web;

/// <summary>
/// The bytes of a page as text: the <c>Content-Type</c> charset when the server named one, else a
/// byte-order mark, else a <c>&lt;meta charset&gt;</c> / <c>http-equiv</c> declaration in the first
/// <see cref="SniffBytes"/>, else UTF-8. The code-page encodings (windows-1252 and the rest) are
/// registered once here, the one place the app needs them. Pure; pinned.
/// </summary>
public static partial class PageCharset
{
    /// <summary>How far into the page a <c>&lt;meta&gt;</c> charset is looked for.</summary>
    public const int SniffBytes = 4096;

    private static readonly object Gate = new();
    private static bool _registered;

    /// <summary>The encoding <paramref name="name"/> names, or null when it is unknown or blank.</summary>
    public static Encoding? Lookup(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        EnsureCodePages();
        try
        {
            return Encoding.GetEncoding(name.Trim().Trim('"', '\''));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The charset a <c>&lt;meta&gt;</c> tag declares in <paramref name="head"/> (ASCII-decoded), or null.</summary>
    public static string? Sniff(string head)
    {
        ArgumentNullException.ThrowIfNull(head);
        var match = MetaCharset().Match(head);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>Decodes <paramref name="bytes"/> by the rules above; <paramref name="headerCharset"/> is the <c>Content-Type</c> charset or null.</summary>
    public static string Decode(ReadOnlySpan<byte> bytes, string? headerCharset)
    {
        var encoding = Lookup(headerCharset);
        if (encoding is null)
        {
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                return Encoding.UTF8.GetString(bytes[3..]);
            }

            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            {
                return Encoding.Unicode.GetString(bytes[2..]);
            }

            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            {
                return Encoding.BigEndianUnicode.GetString(bytes[2..]);
            }

            string head = Encoding.ASCII.GetString(bytes[..Math.Min(bytes.Length, SniffBytes)]);
            encoding = Lookup(Sniff(head));
        }

        encoding ??= Encoding.UTF8;
        return encoding.GetString(bytes);
    }

    internal static void EnsureCodePages()
    {
        lock (Gate)
        {
            if (!_registered)
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                _registered = true;
            }
        }
    }

    [GeneratedRegex("""<meta[^>]*charset\s*=\s*["']?\s*([A-Za-z0-9_.:-]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex MetaCharset();
}
