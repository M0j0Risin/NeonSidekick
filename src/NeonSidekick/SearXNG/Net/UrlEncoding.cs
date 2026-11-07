// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Globalization;
using System.Text;

namespace SearXNG.Net.Net;

/// <summary>Python's URL encoding rules, which .NET's helpers don't match exactly.</summary>
internal static class UrlEncoding
{
    /// <summary>Python's <c>quote_plus</c>: letters, digits and <c>_.-~</c> stay, a space becomes <c>+</c>, the rest is UTF-8 percent-encoded.</summary>
    public static string QuotePlus(string text) => Encode(text, "+");

    /// <summary>Python's <c>quote</c> with <c>safe=""</c>: as <see cref="QuotePlus"/>, but a space becomes <c>%20</c>.</summary>
    public static string Quote(string text) => Encode(text, "%20");

    private static string Encode(string text, string space)
    {
        var encoded = new StringBuilder(text.Length);
        foreach (byte b in Encoding.UTF8.GetBytes(text))
        {
            char c = (char)b;
            if (char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-' or '~')
            {
                encoded.Append(c);
            }
            else if (c == ' ')
            {
                encoded.Append(space);
            }
            else
            {
                encoded.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return encoded.ToString();
    }

    /// <summary>The first non-blank value of <paramref name="name"/> in a query string, as <c>parse_qs(query).get(name)[0]</c>.</summary>
    public static string? FirstQueryValue(string query, string name)
    {
        foreach (var pair in query.Split('&'))
        {
            int equals = pair.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0 || equals == pair.Length - 1)
            {
                continue;
            }

            if (Unquote(pair[..equals]) == name)
            {
                return Unquote(pair[(equals + 1)..]);
            }
        }

        return null;
    }

    private static string Unquote(string text) => Uri.UnescapeDataString(text.Replace('+', ' '));
}
