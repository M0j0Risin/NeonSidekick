// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Globalization;
using System.Text.RegularExpressions;

namespace SearXNG.Net.Results;

/// <summary>A port of the normalization in SearXNG's <c>searx/result_types/_base.py</c>.</summary>
internal static partial class ResultNormalizer
{
    public const int MaximumTitleLength = 200;
    public const int MaximumContentLength = 1200;

    private static readonly IdnMapping Idn = new();

    /// <summary>
    /// The result's URL, normalized, or null when it can't be used. SearXNG keeps any scheme and leaves filtering to its
    /// templates; a library has no templates, so a <c>javascript:</c> or <c>data:</c> link from a scraped page stops here.
    /// </summary>
    public static ParsedUrl? NormalizeUrl(string url)
    {
        var parsed = ParsedUrl.Parse(url);
        if (parsed.Scheme.Length == 0)
        {
            parsed = parsed with { Scheme = "http" };
        }

        if (parsed.Scheme is not ("http" or "https" or "ftp" or "ftps") || parsed.Netloc.Length == 0)
        {
            return null;
        }

        return parsed.Netloc.StartsWith("xn--", StringComparison.Ordinal) ? parsed with { Netloc = DecodeIdn(parsed.Netloc) } : parsed;
    }

    /// <summary>Collapses spaces, tabs and newlines, trims, and shortens long text at a word boundary.</summary>
    public static string NormalizeText(string text, int maxLength)
    {
        text = Whitespace().Replace(text, " ").Trim(Html.HtmlText.PythonSpace);
        if (CodePoints(text) <= maxLength)
        {
            return text;
        }

        var head = text[..CodePointOffset(text, maxLength)];
        int space = head.LastIndexOf(' ');
        return (space < 0 ? head : head[..space]) + " …";
    }

    /// <summary>Python's <c>len</c>: code points, so limits and "longer text wins" agree with SearXNG for emoji and the like.</summary>
    public static int CodePoints(string text)
    {
        int count = text.Length;
        for (int i = 0; i < text.Length - 1; i++)
        {
            if (char.IsSurrogatePair(text[i], text[i + 1]))
            {
                count--;
                i++;
            }
        }

        return count;
    }

    private static int CodePointOffset(string text, int codePoints)
    {
        int i = 0;
        for (int seen = 0; seen < codePoints && i < text.Length; seen++)
        {
            i += i + 1 < text.Length && char.IsSurrogatePair(text[i], text[i + 1]) ? 2 : 1;
        }

        return i;
    }

    private static string DecodeIdn(string netloc)
    {
        int colon = netloc.IndexOf(':', StringComparison.Ordinal);
        var host = colon < 0 ? netloc : netloc[..colon];
        var port = colon < 0 ? "" : netloc[colon..];

        // No DNS name is longer, and IdnMapping takes quadratic time before refusing one that is.
        if (host.Length > 253)
        {
            return netloc;
        }

        try
        {
            return Idn.GetUnicode(host) + port;
        }
        catch (ArgumentException)
        {
            // A malformed punycode label: compare it as sent rather than losing the result.
            return netloc;
        }
    }

    [GeneratedRegex("( |\t|\n)+")]
    private static partial Regex Whitespace();
}
