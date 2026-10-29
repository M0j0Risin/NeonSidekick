using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Xml;
using NeonSidekick.Files;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Sql;

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
    /// How deep <see cref="Children"/> calls may nest (2026-10-28, review P3-01): real files nest about 40 deep; a crafted one nesting
    /// content controls, text boxes or groups a hundred thousand deep overflowed the stack, which no catch survives and which ended the app.
    /// </summary>
    public const int MaxDepth = 256;

    // Counted per thread, since every recursive read in the three readers goes through Children, a ReadSubtree reader's too (whose own
    // Depth starts again at 0, so the reader's Depth can't be the count).
    [ThreadStatic]
    private static int t_depth;

    /// <summary>
    /// The children of the element the reader stands on, one call per child element, the reader on it; each call must leave the reader on
    /// that child's end (or on it when empty), as <see cref="Skip"/> does. Returns on the parent's end. Past <see cref="MaxDepth"/> nested
    /// calls it throws <see cref="XmlException"/> (2026-10-28, review P3-01), which <see cref="OfficePackage.Guard"/> makes damage, so the text
    /// read before it stands; the execution stack is checked too, as a backstop.
    /// </summary>
    public static void Children(XmlReader reader, Action child)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (reader.IsEmptyElement)
        {
            return;
        }

        if (++t_depth > MaxDepth)
        {
            t_depth--;
            throw new XmlException("nested too deeply");
        }

        try
        {
            RuntimeHelpers.EnsureSufficientExecutionStack();
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
        finally
        {
            t_depth--;
        }
    }

    /// <summary>
    /// An <c>mc:AlternateContent</c>'s first <c>Choice</c>: its children read by <paramref name="child"/>, every other choice and the
    /// <c>Fallback</c> skipped, so nothing reads twice (2026-10-28, review P8-10: the three readers' copies made one).
    /// </summary>
    public void FirstChoice(XmlReader reader, Action child)
    {
        ArgumentNullException.ThrowIfNull(reader);
        bool chosen = false;
        Children(reader, () =>
        {
            if (!chosen && Is(reader, Mc, "Choice"))
            {
                chosen = true;
                Children(reader, child);
            }
            else
            {
                Skip(reader);
            }
        });
    }

    /// <summary>A drawing's alt text from the element the reader stands on (<c>descr</c>, else <c>title</c>), on one line (review P8-10: one copy).</summary>
    public static string AltText(XmlReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        string described = reader.GetAttribute("descr") ?? "";
        string titled = reader.GetAttribute("title") ?? "";
        return Plain(described.Length > 0 ? described : titled);
    }

    /// <summary>An attribute's text for one line: line breaks as spaces.</summary>
    public static string Plain(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' ').Trim();
    }

    /// <summary>
    /// One Markdown table row of <paramref name="cells"/>, padded to <paramref name="width"/>, each made safe by <see cref="SqlText.TableCell"/>
    /// and cut past <see cref="ExcelReader.MaxCellChars"/> (2026-10-28, review P8-10: Word's and PowerPoint's tables write rows one way;
    /// <c>SqlText.Table</c>'s separator, header escaping and column advice don't fit a document's table).
    /// </summary>
    public static void TableRow(StringBuilder sb, IReadOnlyList<string> cells, int width)
    {
        ArgumentNullException.ThrowIfNull(sb);
        ArgumentNullException.ThrowIfNull(cells);
        sb.Append('|');
        for (int c = 0; c < Math.Max(width, cells.Count); c++)
        {
            string cell = c < cells.Count ? cells[c] : "";
            sb.Append(' ').Append(SqlText.TableCell(cell.Length <= ExcelReader.MaxCellChars ? cell : TextCut.Safe(cell, ExcelReader.MaxCellChars) + "…")).Append(" |");
        }
    }

    /// <summary>The line under a table's header row.</summary>
    public static void TableSeparator(StringBuilder sb, int width)
    {
        ArgumentNullException.ThrowIfNull(sb);
        sb.Append('|').Append(string.Concat(Enumerable.Repeat(" --- |", width)));
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
    public static string Text(XmlReader reader) => Text(reader, int.MaxValue);

    /// <summary>
    /// The text of the element the reader stands on, at most <paramref name="max"/> characters of it, the rest read past unkept (2026-10-28,
    /// review P5-03: a megabyte file whose one compressible <c>&lt;w:t&gt;</c> inflates to a gigabyte built a multi-gigabyte string before any
    /// limit was looked at). Read in chunks where the reader can (<see cref="XmlReader.ReadValueChunk"/>), so a long run is never held whole.
    /// The callers pass what is left of their budget. The reader is left on the element's end.
    /// </summary>
    public static string Text(XmlReader reader, int max)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (reader.IsEmptyElement)
        {
            return "";
        }

        max = Math.Max(max, 0);
        var sb = new StringBuilder();
        char[]? chunk = null;
        int depth = reader.Depth;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
            {
                if (sb.Length == max && max > 0 && char.IsHighSurrogate(sb[^1]))
                {
                    sb.Length--;   // a pair the cap split
                }

                return sb.ToString();
            }

            if (reader.NodeType is not (XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace))
            {
                continue;
            }

            if (!reader.CanReadValueChunk)
            {
                string value = reader.Value;
                sb.Append(value.AsSpan(0, Math.Min(value.Length, Math.Max(max - sb.Length, 0))));
                continue;
            }

            chunk ??= new char[4096];
            int read;
            while ((read = reader.ReadValueChunk(chunk, 0, chunk.Length)) > 0)
            {
                int keep = Math.Min(read, Math.Max(max - sb.Length, 0));
                sb.Append(chunk, 0, keep);
            }
        }

        throw new XmlException("unexpected end");
    }

    /// <summary>
    /// Excel's <c>_xHHHH_</c> escapes undone (OOXML's way of carrying a character XML can't: <c>_x000D_</c>, <c>_x0009_</c>), <c>_x005F_</c>
    /// read as a literal <c>_</c> so the escape after it stays as written. A decoded tab or line break stays (a carriage return before a
    /// line feed dropped, a lone one a line feed). Control characters are kept here: since 2026-10-28 (review P2-11) every answer goes
    /// through <see cref="UntrustedText.Scrub"/> once at the end, which also catches the literal C1 controls no escape carried. A high and a
    /// low surrogate escaped one after the other make their pair; a lone one is U+FFFD (review P9-09: it made the answer's UTF-8 throw).
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
                else if (char.IsHighSurrogate(c) && LowSurrogateAt(text, i + 7) is { } low)
                {
                    sb.Append(c).Append(low);
                    i += 14;
                    continue;
                }
                else if (char.IsSurrogate(c))
                {
                    sb.Append('�');
                }
                else
                {
                    sb.Append(c);
                }

                i += 7;
                continue;
            }

            sb.Append(text[i]);
            i++;
        }

        return sb.ToString();
    }

    private static char? LowSurrogateAt(string text, int at) =>
        at + 6 < text.Length && text[at] == '_' && text[at + 1] == 'x' && text[at + 6] == '_'
        && int.TryParse(text.AsSpan(at + 2, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int code) && char.IsLowSurrogate((char)code)
            ? (char)code
            : null;
}
