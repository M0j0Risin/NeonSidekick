// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Globalization;

namespace SearXNG.Net.Results;

/// <summary>
/// A URL split as Python's <c>urllib.parse.urlparse</c> splits it. <see cref="Uri"/> isn't used because it rewrites what it
/// parses (escaping, default ports, dot segments), and duplicates are detected by comparing these parts as the engine sent them.
/// </summary>
internal sealed record ParsedUrl(string Scheme, string Netloc, string Path, string Params, string Query, string Fragment)
{
    /// <summary>Python's <c>uses_params</c>, limited to the schemes results can carry here.</summary>
    private static bool UsesParams(string scheme) => scheme is "" or "http" or "https" or "ftp" or "sftp";

    public static ParsedUrl Parse(string url)
    {
        // Python strips leading C0 controls and spaces, and removes tab, CR and LF anywhere.
        url = url.TrimStart(LeadingStrip).Replace("\t", "", StringComparison.Ordinal).Replace("\r", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal);

        var scheme = "";
        int colon = url.IndexOf(':', StringComparison.Ordinal);
        if (colon > 0 && char.IsAsciiLetter(url[0]) && url.AsSpan(0, colon).IndexOfAnyExcept(SchemeChars) < 0)
        {
            scheme = url[..colon].ToLowerInvariant();
            url = url[(colon + 1)..];
        }

        var netloc = "";
        if (url.StartsWith("//", StringComparison.Ordinal))
        {
            int end = url.AsSpan(2).IndexOfAny('/', '?', '#');
            end = end < 0 ? url.Length : end + 2;
            netloc = url[2..end];
            url = url[end..];
        }

        var fragment = "";
        int hash = url.IndexOf('#', StringComparison.Ordinal);
        if (hash >= 0)
        {
            fragment = url[(hash + 1)..];
            url = url[..hash];
        }

        var query = "";
        int question = url.IndexOf('?', StringComparison.Ordinal);
        if (question >= 0)
        {
            query = url[(question + 1)..];
            url = url[..question];
        }

        var parameters = "";
        if (UsesParams(scheme))
        {
            // Params belong to the last path segment only.
            int lastSlash = url.LastIndexOf('/');
            int semicolon = url.IndexOf(';', lastSlash < 0 ? 0 : lastSlash);
            if (semicolon >= 0)
            {
                parameters = url[(semicolon + 1)..];
                url = url[..semicolon];
            }
        }

        return new ParsedUrl(scheme, netloc, url, parameters, query, fragment);
    }

    /// <summary>The URL put back together, as Python's <c>geturl</c> does for a URL with a host.</summary>
    public string GetUrl()
    {
        var path = Params.Length > 0 ? Path + ";" + Params : Path;
        if (path.Length > 0 && path[0] != '/')
        {
            path = "/" + path;
        }

        var url = string.Create(CultureInfo.InvariantCulture, $"{Scheme}://{Netloc}{path}");
        if (Query.Length > 0)
        {
            url += "?" + Query;
        }

        if (Fragment.Length > 0)
        {
            url += "#" + Fragment;
        }

        return url;
    }

    private static readonly char[] LeadingStrip = Enumerable.Range(0, 0x21).Select(i => (char)i).ToArray();

    private static readonly System.Buffers.SearchValues<char> SchemeChars =
        System.Buffers.SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789+-.");
}
