using System.Net;
using System.Text;
using NeonSidekick.Web;

namespace NeonSidekick.Pdf;

/// <summary>
/// A user's HTML file made fit to print (2026-10-03, <c>convert_to_pdf</c> on an <c>.html</c> file): the browser would otherwise
/// render whatever the page reaches — a <c>file:///C:/…</c> frame or picture from outside the working directory, a script that
/// calls out past <c>Web browser network mode</c> — into a PDF inside it. So the page is rebuilt from
/// <see cref="HtmlTokenizer"/>'s tokens: scripts, frames, objects, embeds, <c>base</c> and <c>meta http-equiv</c> dropped, event
/// handlers and <c>javascript:</c> links dropped; its stylesheets and pictures inlined when they resolve inside the sandbox
/// (<paramref name="resolve"/>), dropped when not; and <see cref="PdfHtml.ContentSecurityPolicy"/> put first, so whatever slips
/// through loads nothing. A default <c>@page</c> (the request's paper) comes before the page's own styles, and its <c>size</c> again after
/// them (2026-10-22, review T4: a page's own <c>size: 1mm 1mm</c> won and made a PDF of countless pages), so the page's margins hold but its
/// paper is the request's. Comments and the doctype's details go; a doctype comes back when the page had one. Pure.
/// </summary>
public static class PdfHtmlSanitizer
{
    /// <summary>The elements dropped with their tags (and a script's or noscript's body).</summary>
    private static readonly HashSet<string> Dropped = new(StringComparer.Ordinal)
    {
        "script", "noscript", "iframe", "frame", "frameset", "object", "embed", "applet", "base", "portal",
    };

    /// <summary>The attributes that name something to fetch, beside <c>src</c> and <c>href</c>, dropped outright.</summary>
    private static readonly HashSet<string> FetchingAttributes = new(StringComparer.Ordinal)
    {
        "srcset", "imagesrcset", "poster", "background", "ping", "formaction", "action", "data", "codebase", "manifest", "xmlns:xlink",
    };

    /// <summary>
    /// <paramref name="html"/> rebuilt. <paramref name="resolve"/> takes an address as the page wrote it and returns the
    /// sandbox file's bytes (null when it is outside, missing or not a file); pictures become <c>data:</c> URIs, stylesheets
    /// <c>style</c> elements.
    /// </summary>
    public static string Rewrite(string html, PdfPaperSize paper, bool landscape, Func<string, byte[]?> resolve)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(resolve);
        var output = new StringBuilder(html.Length + 1024);
        if (html.TrimStart().StartsWith("<!doctype", StringComparison.OrdinalIgnoreCase))
        {
            output.Append("<!DOCTYPE html>\n");
        }

        output.Append("<meta charset=\"utf-8\">\n").Append(PdfHtml.PolicyMeta).Append('\n')
            .Append("<style>@page { size: ").Append(PdfPaper.Css(paper, landscape)).Append("; margin: 16mm; }</style>\n");

        string? skipping = null;
        string? rawElement = null;
        foreach (var token in HtmlTokenizer.Tokenize(html))
        {
            if (skipping is not null)
            {
                if (token.Kind == HtmlTokenKind.Close && token.Name == skipping)
                {
                    skipping = null;
                }

                continue;
            }

            switch (token.Kind)
            {
                case HtmlTokenKind.Text:
                    // A style's body is CSS, kept as written but for its '<'s (StyleText); every other text is re-escaped.
                    output.Append(rawElement == "style" ? StyleText(token.Text) : WebUtility.HtmlEncode(token.Text));
                    break;
                case HtmlTokenKind.Close:
                    rawElement = null;
                    if (!Dropped.Contains(token.Name) && IsName(token.Name))
                    {
                        output.Append("</").Append(token.Name).Append('>');
                    }

                    break;
                default:
                    rawElement = HtmlTokenizer.RawTextElements.Contains(token.Name) && !token.SelfClosing ? token.Name : null;
                    if (Dropped.Contains(token.Name))
                    {
                        if (!token.SelfClosing && !HtmlTokenizer.VoidElements.Contains(token.Name) && token.Name is "script" or "noscript")
                        {
                            skipping = token.Name;
                        }

                        rawElement = null;
                        break;
                    }

                    OpenTag(output, token, resolve);
                    break;
            }
        }

        // The request's paper after the page's own styles (review T4).
        output.Append("\n<style>@page { size: ").Append(PdfPaper.Css(paper, landscape)).Append(" !important; }</style>\n");
        return output.ToString();
    }

    private static void OpenTag(StringBuilder output, HtmlToken token, Func<string, byte[]?> resolve)
    {
        if (!IsName(token.Name))
        {
            return;
        }

        if (token.Name == "meta" && token.Attribute("http-equiv") is not null)
        {
            return;
        }

        if (token.Name == "link")
        {
            // A stylesheet from the sandbox becomes a style element; every other link (icons, preloads, imports) goes.
            if (IsStylesheet(token) && token.Attribute("href") is { } href && resolve(href) is { } css)
            {
                output.Append("<style>").Append(StyleText(Encoding.UTF8.GetString(css))).Append("</style>");
            }

            return;
        }

        output.Append('<').Append(token.Name);
        if (token.Attributes is not null)
        {
            foreach (var (name, value) in token.Attributes)
            {
                string? kept = Attribute(token.Name, name, value, resolve);
                if (kept is not null)
                {
                    output.Append(' ').Append(name).Append("=\"").Append(WebUtility.HtmlEncode(kept)).Append('"');
                }
            }
        }

        output.Append(token.SelfClosing ? " />" : ">");
    }

    /// <summary>An attribute's value as kept, or null to drop it.</summary>
    private static string? Attribute(string element, string name, string value, Func<string, byte[]?> resolve)
    {
        if (!IsName(name) || name.StartsWith("on", StringComparison.Ordinal) || FetchingAttributes.Contains(name))
        {
            return null;
        }

        if (name == "src")
        {
            // A picture from the sandbox is inlined; any other src (audio, video, a web picture) would be refused by the policy anyway.
            if (element == "img" && !value.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                && PdfHtml.ImageMediaType(PathOf(value)) is { } type && resolve(value) is { } bytes)
            {
                return PdfHtml.DataUri(bytes, type);
            }

            return value.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) && element == "img" ? value : null;
        }

        if ((name == "href" || name.EndsWith(":href", StringComparison.Ordinal)) && !SafeLink(value))
        {
            return null;
        }

        return value;
    }

    /// <summary>
    /// A style body as written back (2026-10-22, review S2): every <c>&lt;</c> becomes the CSS escape <c>\3c </c>, which means the same
    /// character in a CSS string and is no tag to a browser. Inside <c>&lt;svg&gt;</c> or <c>&lt;math&gt;</c> a <c>style</c> element's body
    /// is parsed as markup, not CSS, so <c>&lt;svg&gt;&lt;style&gt;&lt;meta http-equiv=refresh …&gt;</c> written back raw became a live
    /// meta tag that sent the print browser to an outside file. The tokenizer can't tell a foreign <c>style</c> from an HTML one; with
    /// no <c>&lt;</c> left, neither parse finds a tag. (The <c>&lt;/</c> → <c>&lt;\/</c> rewrite of an inlined sheet, before, stopped only a close.)
    /// </summary>
    private static string StyleText(string css) => css.Replace("<", "\\3c ", StringComparison.Ordinal);

    /// <summary>
    /// Whether a link may stay (2026-10-22, review S7): the scheme read as a browser reads it — tabs and line breaks anywhere removed,
    /// controls and spaces trimmed from the start — so <c>java&amp;#9;script:</c> (decoded to a tab) is caught, and <c>vbscript:</c> and
    /// a <c>data:</c> link that is not a picture go with <c>javascript:</c>. Applies to <c>href</c> and <c>xlink:href</c> (an SVG link
    /// in the PDF is a live link too). A relative address, <c>http(s):</c>, <c>mailto:</c> and a fragment stay.
    /// </summary>
    internal static bool SafeLink(string value)
    {
        var plain = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            if (c is '\t' or '\n' or '\r' || (plain.Length == 0 && c <= ' '))
            {
                continue;
            }

            plain.Append(c);
        }

        string url = plain.ToString();
        int colon = url.IndexOf(':');
        if (colon <= 0 || url.AsSpan(0, colon).IndexOfAny('/', '?', '#') >= 0)
        {
            return true;
        }

        string scheme = url[..colon];
        if (scheme.Equals("javascript", StringComparison.OrdinalIgnoreCase) || scheme.Equals("vbscript", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !scheme.Equals("data", StringComparison.OrdinalIgnoreCase)
            || url[(colon + 1)..].TrimStart().StartsWith("image/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsStylesheet(HtmlToken token) =>
        token.Attribute("rel") is { } rel && rel.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("stylesheet", StringComparer.OrdinalIgnoreCase);

    /// <summary>The address without its query or fragment, for the extension.</summary>
    private static string PathOf(string address)
    {
        int cut = address.IndexOfAny(['?', '#']);
        return cut >= 0 ? address[..cut] : address;
    }

    /// <summary>Whether a tag or attribute name is plain enough to write back: letters, digits, <c>-</c>, <c>_</c>, <c>:</c>, <c>.</c>.</summary>
    private static bool IsName(string name)
    {
        if (name.Length == 0 || !char.IsAsciiLetter(name[0]))
        {
            return false;
        }

        foreach (char c in name)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or ':' or '.'))
            {
                return false;
            }
        }

        return true;
    }
}
