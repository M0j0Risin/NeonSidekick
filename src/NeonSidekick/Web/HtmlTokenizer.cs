using System.Net;
using System.Text;

namespace NeonSidekick.Web;

public enum HtmlTokenKind
{
    /// <summary>A start tag, <c>SelfClosing</c> for <c>&lt;br/&gt;</c> and the void elements.</summary>
    Open,
    Close,

    /// <summary>Text between tags, entities decoded; inside <c>script</c> / <c>style</c> the raw body.</summary>
    Text,
}

/// <summary>One token of a page. <see cref="Name"/> is the lower-cased tag name (empty for text).</summary>
public sealed record HtmlToken(HtmlTokenKind Kind, string Name, string Text, IReadOnlyDictionary<string, string>? Attributes, bool SelfClosing)
{
    /// <summary>The attribute's decoded value, or null.</summary>
    public string? Attribute(string name) => Attributes is not null && Attributes.TryGetValue(name, out var value) ? value : null;
}

/// <summary>
/// A hand-rolled HTML scanner — the ONE HTML reader in the app (the Markdown converter walks its tokens; the DuckDuckGo result parser
/// did too until the scrape went, 2026-11-07). It knows what it needs and no more: tags with
/// attributes (quoted or bare, entities decoded), text with entities decoded, comments / doctype /
/// CDATA / processing instructions skipped, the raw-text elements (<c>script</c>, <c>style</c>,
/// <c>textarea</c>, <c>title</c>) read to their end tag without looking for tags inside, the void
/// elements never expecting a close. It never throws on malformed input: a stray <c>&lt;</c> is
/// text, an unterminated tag runs to the end.
/// </summary>
public static class HtmlTokenizer
{
    /// <summary>The elements with no content and no end tag.</summary>
    public static readonly IReadOnlySet<string> VoidElements = new HashSet<string>(StringComparer.Ordinal)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr",
    };

    /// <summary>The elements whose body is read raw until the matching end tag (no tags, no comments inside).</summary>
    public static readonly IReadOnlySet<string> RawTextElements = new HashSet<string>(StringComparer.Ordinal)
    {
        "script", "style", "textarea", "title",
    };

    public static IEnumerable<HtmlToken> Tokenize(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        int i = 0;
        int n = html.Length;
        var text = new StringBuilder();
        while (i < n)
        {
            char c = html[i];
            if (c != '<')
            {
                text.Append(c);
                i++;
                continue;
            }

            // Comments, doctype, CDATA, processing instructions: skipped whole.
            if (StartsWith(html, i, "<!--"))
            {
                if (text.Length > 0) { yield return TextToken(text); }
                int end = html.IndexOf("-->", i + 4, StringComparison.Ordinal);
                i = end < 0 ? n : end + 3;
                continue;
            }

            if (StartsWith(html, i, "<![CDATA["))
            {
                if (text.Length > 0) { yield return TextToken(text); }
                int end = html.IndexOf("]]>", i + 9, StringComparison.Ordinal);
                i = end < 0 ? n : end + 3;
                continue;
            }

            if (i + 1 < n && (html[i + 1] == '!' || html[i + 1] == '?'))
            {
                if (text.Length > 0) { yield return TextToken(text); }
                int end = html.IndexOf('>', i + 2);
                i = end < 0 ? n : end + 1;
                continue;
            }

            bool closing = i + 1 < n && html[i + 1] == '/';
            int nameStart = closing ? i + 2 : i + 1;
            if (nameStart >= n || !IsNameStart(html[nameStart]))
            {
                // A lone '<' (as in "a < b") is text.
                text.Append(c);
                i++;
                continue;
            }

            if (text.Length > 0) { yield return TextToken(text); }

            int nameEnd = nameStart;
            while (nameEnd < n && IsNameChar(html[nameEnd]))
            {
                nameEnd++;
            }

            string name = html.Substring(nameStart, nameEnd - nameStart).ToLowerInvariant();
            i = nameEnd;
            if (closing)
            {
                int end = html.IndexOf('>', i);
                i = end < 0 ? n : end + 1;
                yield return new HtmlToken(HtmlTokenKind.Close, name, "", null, false);
                continue;
            }

            var attributes = ReadAttributes(html, ref i, out bool selfClosing);
            bool isVoid = VoidElements.Contains(name);
            yield return new HtmlToken(HtmlTokenKind.Open, name, "", attributes, selfClosing || isVoid);
            if (isVoid || selfClosing)
            {
                continue;
            }

            if (RawTextElements.Contains(name))
            {
                int end = IndexOfEndTag(html, i, name);
                string body = html.Substring(i, (end < 0 ? n : end) - i);
                // script/style bodies are code: kept raw; title/textarea are text: entities decoded.
                yield return new HtmlToken(HtmlTokenKind.Text, "", name is "script" or "style" ? body : WebUtility.HtmlDecode(body), null, false);
                if (end < 0)
                {
                    i = n;
                }
                else
                {
                    int close = html.IndexOf('>', end);
                    i = close < 0 ? n : close + 1;
                    yield return new HtmlToken(HtmlTokenKind.Close, name, "", null, false);
                }
            }
        }

        if (text.Length > 0)
        {
            yield return TextToken(text);
        }
    }

    private static HtmlToken TextToken(StringBuilder text)
    {
        var token = new HtmlToken(HtmlTokenKind.Text, "", WebUtility.HtmlDecode(text.ToString()), null, false);
        text.Clear();
        return token;
    }

    private static Dictionary<string, string>? ReadAttributes(string html, ref int i, out bool selfClosing)
    {
        int n = html.Length;
        Dictionary<string, string>? attributes = null;
        selfClosing = false;
        while (i < n)
        {
            while (i < n && char.IsWhiteSpace(html[i]))
            {
                i++;
            }

            if (i >= n)
            {
                break;
            }

            char c = html[i];
            if (c == '>')
            {
                i++;
                return attributes;
            }

            if (c == '/')
            {
                selfClosing = true;
                i++;
                continue;
            }

            int nameStart = i;
            while (i < n && !char.IsWhiteSpace(html[i]) && html[i] != '=' && html[i] != '>' && html[i] != '/')
            {
                i++;
            }

            if (i == nameStart)
            {
                // Something odd (a stray quote); step over it.
                i++;
                continue;
            }

            string name = html.Substring(nameStart, i - nameStart).ToLowerInvariant();
            string value = "";
            while (i < n && char.IsWhiteSpace(html[i]))
            {
                i++;
            }

            if (i < n && html[i] == '=')
            {
                i++;
                while (i < n && char.IsWhiteSpace(html[i]))
                {
                    i++;
                }

                if (i < n && (html[i] == '"' || html[i] == '\''))
                {
                    char quote = html[i];
                    int end = html.IndexOf(quote, i + 1);
                    value = html.Substring(i + 1, (end < 0 ? n : end) - i - 1);
                    i = end < 0 ? n : end + 1;
                }
                else
                {
                    int valueStart = i;
                    while (i < n && !char.IsWhiteSpace(html[i]) && html[i] != '>')
                    {
                        i++;
                    }

                    value = html.Substring(valueStart, i - valueStart);
                }
            }

            attributes ??= new Dictionary<string, string>(StringComparer.Ordinal);
            attributes.TryAdd(name, WebUtility.HtmlDecode(value));
        }

        return attributes;
    }

    private static int IndexOfEndTag(string html, int from, string name)
    {
        int i = from;
        while (true)
        {
            int lt = html.IndexOf("</", i, StringComparison.Ordinal);
            if (lt < 0)
            {
                return -1;
            }

            if (lt + 2 + name.Length <= html.Length
                && string.Compare(html, lt + 2, name, 0, name.Length, StringComparison.OrdinalIgnoreCase) == 0
                && (lt + 2 + name.Length == html.Length || !IsNameChar(html[lt + 2 + name.Length])))
            {
                return lt;
            }

            i = lt + 2;
        }
    }

    private static bool StartsWith(string html, int at, string what) =>
        at + what.Length <= html.Length && string.CompareOrdinal(html, at, what, 0, what.Length) == 0;

    private static bool IsNameStart(char c) => char.IsAsciiLetter(c);

    private static bool IsNameChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '-' || c == ':' || c == '_';
}
