// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace SearXNG.Net.Html;

/// <summary>Text out of HTML: the C# side of <c>extract_text</c> and <c>html_to_text</c> in SearXNG's <c>searx/utils.py</c>.</summary>
internal static partial class HtmlText
{
    /// <summary>How deep a page may nest. Saved real pages reach 21, and the limit is far above that without being deep enough to hurt.</summary>
    internal const int MaxNesting = 256;

    /// <summary>
    /// A document that stops at <see cref="MaxNesting"/>. HtmlAgilityPack has no limit by default, and <see cref="Extract"/> recurses over
    /// child nodes, so a hostile page could overflow the stack, which ends the whole process with no chance to catch it. Past the limit
    /// loading throws, and <see cref="MetaSearch"/> reports that as the one engine's failure.
    /// </summary>
    public static HtmlDocument NewDocument() => new() { OptionMaxNestedChildNodes = MaxNesting };

    /// <summary>
    /// <c>extract_text</c> for one element: its text nodes joined, with whitespace collapsed. HtmlAgilityPack's <c>InnerText</c>
    /// isn't enough: it leaves entities undecoded, where lxml decodes them (seen on a live Bing title, <c>&amp;#183;</c>).
    /// </summary>
    public static string Extract(HtmlNode? node)
    {
        if (node is null)
        {
            return "";
        }

        var text = new StringBuilder();
        Collect(node, text);
        return CollapseWhitespace(text.ToString());
    }

    /// <summary><c>extract_text</c> for a list: each element's text, concatenated without a separator, as SearXNG does.</summary>
    public static string Extract(IEnumerable<HtmlNode>? nodes) =>
        nodes is null ? "" : string.Concat(nodes.Select(Extract)).Trim(PythonSpace);

    /// <summary>Python's whitespace for <c>str.split</c> and <c>str.strip</c>: .NET's, plus the four separators it leaves out.</summary>
    internal static readonly char[] PythonSpace = "\t\n\u000B\f\r\u001C\u001D\u001E\u001F \u0085\u00A0\u1680\u2000\u2001\u2002\u2003\u2004\u2005\u2006\u2007\u2008\u2009\u200A\u2028\u2029\u202F\u205F\u3000".ToCharArray();

    /// <summary>
    /// <c>html_to_text</c>, following Python 3.13.14's <c>html/parser.py</c>: text out of an HTML fragment, such as DuckDuckGo's <c>&lt;b&gt;</c>-marked titles.
    /// A deliberate port, quirks included: an end tag that doesn't match the innermost open tag stays in the text, as Python's parser leaves it.
    /// </summary>
    public static string FromFragment(string? html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return "";
        }

        html = CollapseWhitespace(html);
        var text = new StringBuilder();
        var open = new List<string>();
        string? raw = null;
        bool escapable = true;
        int i = 0;
        while (i < html.Length)
        {
            int j;
            if (raw is null)
            {
                j = html.IndexOf('<', i);
                j = j < 0 ? html.Length : j;
            }
            else
            {
                j = raw == "plaintext" ? html.Length : FindRawEnd(html, raw, i);
                if (j < 0)
                {
                    AppendData(text, open, html[i..], escapable);
                    break;
                }
            }

            if (i < j)
            {
                AppendData(text, open, html[i..j], escapable);
            }

            i = j;
            if (i == html.Length)
            {
                break;
            }

            int k = ParseMarkup(html, i, text, open, ref raw, ref escapable);
            i = k < 0 ? html.Length : k;
        }

        return text.ToString().Trim(PythonSpace);
    }

    /// <summary>A raw element's end: <c>&lt;/name</c> in any ASCII case, then a space, <c>/</c> or <c>&gt;</c>. -1 when it has none.</summary>
    private static int FindRawEnd(string html, string name, int from)
    {
        for (int at = html.IndexOf("</", from, StringComparison.Ordinal); at >= 0; at = html.IndexOf("</", at + 2, StringComparison.Ordinal))
        {
            int after = at + 2 + name.Length;
            if (after < html.Length && html[after] is '\t' or '\n' or '\r' or '\f' or ' ' or '/' or '>' && Ascii.EqualsIgnoreCase(html.AsSpan(at + 2, name.Length), name))
            {
                return at;
            }
        }

        return -1;
    }

    /// <summary>Python's <c>html.unescape</c>: numeric references with or without the semicolon, and every HTML5 name.</summary>
    public static string Unescape(string text) => text.Contains('&') ? CharRef().Replace(text, ReplaceCharRef) : text;

    private static void Collect(HtmlNode node, StringBuilder text)
    {
        foreach (var child in node.ChildNodes)
        {
            switch (child.NodeType)
            {
                case HtmlNodeType.Text:
                    text.Append(Unescape(((HtmlTextNode)child).Text));
                    break;
                case HtmlNodeType.Element:
                    Collect(child, text);
                    break;
            }
        }
    }

    private static string CollapseWhitespace(string text) =>
        string.Join(' ', text.Split(PythonSpace, StringSplitOptions.RemoveEmptyEntries));

    private static void AppendData(StringBuilder text, List<string> open, string data, bool escapable)
    {
        if (data.Length > 0 && (open.Count == 0 || !IsBlocked(open[^1])))
        {
            text.Append(escapable ? Unescape(data) : data);
        }
    }

    private static bool IsBlocked(string tag) => tag is "script" or "style";

    /// <summary>The markup at <paramref name="lt"/>: where to carry on, or -1 when it runs off the end, which drops the rest.</summary>
    private static int ParseMarkup(string html, int lt, StringBuilder text, List<string> open, ref string? raw, ref bool escapable)
    {
        char next = lt + 1 < html.Length ? html[lt + 1] : '\0';
        if (char.IsAsciiLetter(next))
        {
            return ParseStartTag(html, lt, text, open, ref raw, ref escapable);
        }

        var rest = html.AsSpan(lt);
        if (next == '/')
        {
            int k = ParseEndTag(html, lt, text, open, ref raw, ref escapable);
            if (k < 0 && lt + 2 == html.Length)
            {
                AppendData(text, open, "</", escapable: true);
            }

            return k;
        }

        if (rest.StartsWith("<!--", StringComparison.Ordinal))
        {
            return ParseComment(html, lt);
        }

        if (next == '?')
        {
            return After(html.IndexOf('>', lt + 2), 1);
        }

        if (next == '!')
        {
            if (rest.StartsWith("<![CDATA[", StringComparison.Ordinal))
            {
                return After(html.IndexOf("]]>", lt + 9, StringComparison.Ordinal), 3);
            }

            return rest.Length >= 9 && Ascii.EqualsIgnoreCase(rest[..9], "<!doctype")
                ? After(html.IndexOf('>', lt + 9), 1)
                : After(html.IndexOf('>', lt + 2), 1);
        }

        AppendData(text, open, "<", escapable: true);
        return lt + 1;
    }

    private static int After(int found, int length) => found < 0 ? -1 : found + length;

    private static int ParseComment(string html, int lt)
    {
        var close = CommentClose().Match(html, lt + 4);
        if (close.Success)
        {
            return close.Index + close.Length;
        }

        var abrupt = CommentAbruptClose().Match(html, lt + 4);
        return abrupt.Success ? abrupt.Index + abrupt.Length : -1;
    }

    private static int ParseEndTag(string html, int lt, StringBuilder text, List<string> open, ref string? raw, ref bool escapable)
    {
        if (html.IndexOf('>', lt + 2) < 0)
        {
            return -1;
        }

        // A bogus comment, or the empty </>.
        if (!char.IsAsciiLetter(html[lt + 2]))
        {
            return After(html.IndexOf('>', lt + 2), 1);
        }

        var located = LocateTagEnd().Match(html, lt + 2);
        int end = located.Index + located.Length;
        if (html[end - 1] != '>')
        {
            return -1;
        }

        var name = TagFind().Match(html, lt + 2).Groups[1].Value.ToLowerInvariant();
        if (open.Count > 0)
        {
            if (name != open[^1])
            {
                text.Append("</").Append(name).Append('>');
            }
            else
            {
                open.RemoveAt(open.Count - 1);
            }
        }

        raw = null;
        escapable = true;
        return end;
    }

    private static int ParseStartTag(string html, int lt, StringBuilder text, List<string> open, ref string? raw, ref bool escapable)
    {
        var located = LocateTagEnd().Match(html, lt + 1);
        int end = located.Index + located.Length;
        if (html[end - 1] != '>')
        {
            return -1;
        }

        var tag = TagFind().Match(html, lt + 1);
        var name = tag.Groups[1].Value.ToLowerInvariant();
        int k = tag.Index + tag.Length;
        while (k < end)
        {
            var attribute = AttrFind().Match(html, k);
            if (!attribute.Success)
            {
                break;
            }

            k = attribute.Index + attribute.Length;
        }

        if (name == "br")
        {
            text.Append(' ');
        }

        if (html.AsSpan(k, end - k).Trim(PythonSpace).EndsWith("/>", StringComparison.Ordinal))
        {
            return end;
        }

        open.Add(name);
        if (name is "script" or "style" or "xmp" or "iframe" or "noembed" or "noframes" or "plaintext")
        {
            raw = name;
            escapable = false;
        }
        else if (name is "textarea" or "title")
        {
            raw = name;
            escapable = true;
        }

        return end;
    }

    [GeneratedRegex(@"\G([a-zA-Z][^\t\n\r\f />]*)(?:[\t\n\r\f ]|/(?!>))*")]
    private static partial Regex TagFind();

    [GeneratedRegex(@"\G((?<=['""\t\n\r\f /])[^\t\n\r\f />][^\t\n\r\f /=>]*)([\t\n\r\f ]*=[\t\n\r\f ]*('[^']*'|""[^""]*""|(?!['""])[^>\t\n\r\f ]*))?(?:[\t\n\r\f ]|/(?!>))*")]
    private static partial Regex AttrFind();

    [GeneratedRegex(@"\G[a-zA-Z][^\t\n\r\f />]*[\t\n\r\f /]*(?:(?<=['""\t\n\r\f /])[^\t\n\r\f />][^\t\n\r\f /=>]*(?:[\t\n\r\f ]*=[\t\n\r\f ]*(?:'[^']*'|""[^""]*""|(?!['""])[^>\t\n\r\f ]*))?[\t\n\r\f /]*)*>?")]
    private static partial Regex LocateTagEnd();

    [GeneratedRegex(@"--!?>")]
    private static partial Regex CommentClose();

    [GeneratedRegex(@"\G-?>")]
    private static partial Regex CommentAbruptClose();

    private static string ReplaceCharRef(Match match)
    {
        var body = match.Groups[1].Value;
        if (body[0] != '#')
        {
            return ReplaceNamed(body);
        }

        var digits = body.TrimEnd(';');
        bool hex = digits[1] is 'x' or 'X';
        var number = hex ? digits[2..] : digits[1..];
        if (!int.TryParse(number, hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None, CultureInfo.InvariantCulture, out int code) || code < 0)
        {
            // Past int's range, so past Unicode's too.
            return Replacement;
        }

        if (code == 0)
        {
            return Replacement;
        }

        if (code == 0x0D)
        {
            return "\r";
        }

        if (code is >= 0x80 and <= 0x9F)
        {
            return Windows1252[code - 0x80].ToString();
        }

        if (code is > 0x10FFFF or (>= 0xD800 and <= 0xDFFF))
        {
            return Replacement;
        }

        return IsInvalidCodePoint(code) ? "" : char.ConvertFromUtf32(code);
    }

    /// <summary>The body of Python's <c>_replace_charref</c> for names: the whole name, or else its longest known beginning.</summary>
    private static string ReplaceNamed(string body)
    {
        if (HtmlEntityTable.Names.TryGetValue(body, out var whole))
        {
            return whole;
        }

        for (int length = body.Length - 1; length > 1; length--)
        {
            if (HtmlEntityTable.Names.TryGetValue(body[..length], out var start))
            {
                return start + body[length..];
            }
        }

        return "&" + body;
    }

    /// <summary>Python's <c>_invalid_codepoints</c>, less the C1 range handled above: these references become nothing.</summary>
    private static bool IsInvalidCodePoint(int code) =>
        code is (>= 0x01 and <= 0x08) or 0x0B or (>= 0x0E and <= 0x1F) or 0x7F or (>= 0xFDD0 and <= 0xFDEF) || (code & 0xFFFE) == 0xFFFE;

    private const string Replacement = "�";

    /// <summary>What browsers, and Python, make of the C1 range: Windows-1252, so <c>&amp;#150;</c> is an en dash.</summary>
    private const string Windows1252 =
        "€\u0081‚ƒ„…†‡ˆ‰Š‹Œ\u008DŽ\u008F" +
        "\u0090‘’“”•–—˜™š›œ\u009DžŸ";

    [GeneratedRegex("&(#[0-9]+;?|#[xX][0-9a-fA-F]+;?|[^\\t\\n\\f <&#;]{1,32};?)")]
    private static partial Regex CharRef();
}
