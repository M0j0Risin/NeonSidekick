using System.Globalization;
using System.Text;
using System.Xml;
using NeonSidekick.Llm.Tools;

namespace NeonSidekick.Office;

/// <summary>
/// What the three readers share over <see cref="XmlReader"/> (2026-10-28, the Office plan): a namespace known by a short key whichever
/// dialect wrote it (Transitional <c>http://schemas.openxmlformats.org/…/2006/…</c> and Strict <c>http://purl.oclc.org/ooxml/…</c> read
/// alike), attributes found by local name and key, a subtree skipped, and Excel's <c>_xHHHH_</c> escapes undone.
/// </summary>
public sealed class OfficeXml
{
    public const string W = "wordprocessingml/main";
    public const string A = "drawingml/main";
    public const string P = "presentationml/main";
    public const string S = "spreadsheetml/main";
    public const string R = "officeDocument/relationships";
    public const string M = "officeDocument/math";
    public const string Mc = "markup-compatibility";
    public const string Wp = "drawingml/wordprocessingDrawing";
    public const string Vml = "urn:schemas-microsoft-com:vml";
    public const string VmlOffice = "urn:schemas-microsoft-com:office:office";

    private const string Transitional = "http://schemas.openxmlformats.org/";
    private const string Strict = "http://purl.oclc.org/ooxml/";

    private readonly Dictionary<string, string> _keys = new(StringComparer.Ordinal);

    /// <summary>The namespace's key: the Transitional and Strict URIs' common part, any other URI as it is.</summary>
    public string Key(string uri)
    {
        if (_keys.TryGetValue(uri, out string? key))
        {
            return key;
        }

        key = uri.StartsWith(Transitional, StringComparison.Ordinal) ? uri[Transitional.Length..].Replace("/2006", "", StringComparison.Ordinal)
            : uri.StartsWith(Strict, StringComparison.Ordinal) ? uri[Strict.Length..]
            : uri;
        _keys[uri] = key;
        return key;
    }

    /// <summary>Whether the reader stands on an element <paramref name="local"/> in namespace <paramref name="ns"/>.</summary>
    public bool Is(XmlReader reader, string ns, string local) =>
        reader.NodeType == XmlNodeType.Element && reader.LocalName == local && Key(reader.NamespaceURI) == ns;

    /// <summary>The attribute <paramref name="local"/> in namespace <paramref name="ns"/> (<c>""</c> for an unqualified one); null when absent. The reader stays on the element.</summary>
    public string? Attr(XmlReader reader, string local, string ns = "")
    {
        if (!reader.HasAttributes)
        {
            return null;
        }

        string? found = null;
        while (reader.MoveToNextAttribute())
        {
            if (reader.LocalName == local && (ns.Length == 0 ? reader.NamespaceURI.Length == 0 : Key(reader.NamespaceURI) == ns))
            {
                found = reader.Value;
                break;
            }
        }

        reader.MoveToElement();
        return found;
    }

    /// <summary>Whether an on/off attribute (<c>w:val</c>, <c>hidden</c>) says on: absent counts as <paramref name="absent"/>.</summary>
    public static bool On(string? value, bool absent = true) =>
        value is null ? absent : value is "1" or "true" or "on" || value.Equals("true", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The children of the element the reader stands on, one call per child element, the reader on it; each call must leave the reader on
    /// that child's end (or on it when empty), as <see cref="Skip"/> does. Returns on the parent's end.
    /// </summary>
    public static void Children(XmlReader reader, Action child)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (reader.IsEmptyElement)
        {
            return;
        }

        int depth = reader.Depth;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
            {
                return;
            }

            if (reader.NodeType == XmlNodeType.Element)
            {
                child();
            }
        }

        throw new XmlException("unexpected end");
    }

    /// <summary>Moves past the element the reader stands on, its subtree unread: on its end tag (or itself when empty).</summary>
    public static void Skip(XmlReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (reader.IsEmptyElement)
        {
            return;
        }

        int depth = reader.Depth;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
            {
                return;
            }
        }

        throw new XmlException("unexpected end");
    }

    /// <summary>The text of the element the reader stands on (its text and spaces, children's too), the reader left on its end.</summary>
    public static string Text(XmlReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (reader.IsEmptyElement)
        {
            return "";
        }

        var sb = new StringBuilder();
        int depth = reader.Depth;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
            {
                return sb.ToString();
            }

            if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace)
            {
                sb.Append(reader.Value);
            }
        }

        throw new XmlException("unexpected end");
    }

    /// <summary>
    /// Excel's <c>_xHHHH_</c> escapes undone (OOXML's way of carrying a character XML can't: <c>_x000D_</c>, <c>_x0009_</c>), <c>_x005F_</c>
    /// read as a literal <c>_</c> so the escape after it stays as written. A decoded tab or line break stays (a carriage return before a
    /// line feed dropped, a lone one a line feed); any other control character goes through <see cref="UntrustedText.Scrub"/>, so a
    /// workbook can't put a terminal escape sequence into an answer.
    /// </summary>
    public static string Unescape(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!text.Contains("_x", StringComparison.Ordinal))
        {
            return text;
        }

        var sb = new StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            if (i + 6 < text.Length && text[i] == '_' && text[i + 1] == 'x' && text[i + 6] == '_'
                && int.TryParse(text.AsSpan(i + 2, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int code))
            {
                char c = (char)code;
                if (c == '\r')
                {
                    bool lineFeedNext = (i + 7 < text.Length && text[i + 7] == '\n') || text.AsSpan(i + 7).StartsWith("_x000A_", StringComparison.OrdinalIgnoreCase);
                    if (!lineFeedNext)
                    {
                        sb.Append('\n');
                    }
                }
                else if (c is '\t' or '\n' || c >= ' ' && c != '\x7F' && c is < '\x80' or > '\x9F')
                {
                    sb.Append(c);
                }
                else
                {
                    sb.Append(UntrustedText.Scrub(c.ToString()));
                }

                i += 7;
                continue;
            }

            sb.Append(text[i]);
            i++;
        }

        return sb.ToString();
    }
}
