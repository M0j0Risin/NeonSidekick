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
/// through loads nothing. A default <c>@page</c> (the request's paper) comes before the page's own styles, whose own
/// <c>@page</c> still wins. Comments and the doctype's details go; a doctype comes back when the page had one. Pure.
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
                    // A style's body is CSS, kept as written; every other text is re-escaped.
                    output.Append(rawElement == "style" ? token.Text : WebUtility.HtmlEncode(token.Text));
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
                string text = Encoding.UTF8.GetString(css).Replace("</", "<\\/", StringComparison.Ordinal);
                output.Append("<style>").Append(text).Append("</style>");
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

        if (name == "href" && value.TrimStart().StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return value;
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
