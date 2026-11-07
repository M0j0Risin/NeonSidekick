// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Buffers.Text;
using System.Text;
using HtmlAgilityPack;
using SearXNG.Net.Html;
using SearXNG.Net.Locales;
using SearXNG.Net.Net;
using SearXNG.Net.Results;

namespace SearXNG.Net.Engines;

/// <summary>Bing's web results: a port of SearXNG's <c>searx/engines/bing.py</c>.</summary>
public sealed class BingEngine : IEngine
{
    private const string BaseUrl = "https://www.bing.com";
    private const string RedirectPrefix = "https://www.bing.com/ck/a?";
    private const string AllRegions = "clear";

    /// <inheritdoc/>
    public string Name => "bing";

    /// <inheritdoc/>
    public string Category => "general";

    /// <inheritdoc/>
    public double Weight => 1.0;

    /// <inheritdoc/>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <inheritdoc/>
    public bool Paging => false;

    /// <inheritdoc/>
    public async Task<IReadOnlyList<EngineResult>> SearchAsync(EngineQuery query, HttpClient http, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(http);
        var response = await EngineHttp.GetAsync(
            http,
            BuildUrl(query),
            [
                new("Accept", EngineHttp.PageAccept),
                new("Accept-Language", EngineHttp.AcceptLanguage(query.Locale)),
                new("User-Agent", EngineHttp.ChromeUserAgent),
            ],
            cancellationToken).ConfigureAwait(false);
        return Parse(response.Text);
    }

    internal static Uri BuildUrl(EngineQuery query)
    {
        var url = new StringBuilder(BaseUrl).Append("/search?q=").Append(UrlEncoding.QuotePlus(query.Query)).Append("&adlt=").Append(query.SafeSearch switch
        {
            SafeSearch.Moderate => "moderate",
            SafeSearch.Strict => "strict",
            _ => "off",
        });

        var region = RegionFor(query.Locale);
        if (region != AllRegions)
        {
            var parts = region.Split('-', 2);

            // SearXNG leaves the country out for these because "bing just sends junk". mkt was as bad for ru and cn when measured.
            // SearXNG sends setlang and cc for the rest, but cc gave off-topic results where mkt didn't (2026-11-06).
            if (parts[1] is "us" or "cn" or "ru")
            {
                url.Append("&setlang=").Append(parts[0]);
            }
            else
            {
                url.Append("&mkt=").Append(parts[0]).Append('-').Append(parts[1].ToUpperInvariant());
            }
        }

        return new Uri(url.ToString());
    }

    /// <summary>The Bing market for a locale. A locale that leads to no market gets none.</summary>
    internal static string RegionFor(string locale) =>
        locale == EngineQuery.AllLocales ? AllRegions : EngineLocale.Resolve(locale, BingRegions.Table, AllRegions)!;

    internal static IReadOnlyList<EngineResult> Parse(string html)
    {
        var doc = HtmlText.NewDocument();
        doc.LoadHtml(html);

        // Unlike SearXNG, which reads this as no results: a page that lost its structure shouldn't look like one.
        if (doc.DocumentNode.SelectSingleNode("//ol[@id=\"b_results\"]") is null)
        {
            throw new EngineException(EngineFailureKind.UnexpectedResponse, "Bing's page had no results list.");
        }

        var items = doc.DocumentNode.SelectNodes("//ol[@id=\"b_results\"]/li[contains(@class, \"b_algo\")]");
        if (items is null)
        {
            return [];
        }

        var results = new List<EngineResult>(items.Count);
        foreach (var item in items)
        {
            var link = item.SelectSingleNode(".//h2/a");
            if (link is null)
            {
                continue;
            }

            // HtmlAgilityPack returns attribute values as written, entities and all, where lxml decodes them.
            var href = HtmlText.Unescape(link.GetAttributeValue("href", ""));
            var title = HtmlText.Extract(link);
            if (href.Length == 0 || title.Length == 0)
            {
                continue;
            }

            var paragraphs = item.SelectNodes(".//p");
            RemoveIcons(paragraphs);
            results.Add(new EngineResult(DecodeRedirect(href), title, HtmlText.Extract(paragraphs)));
        }

        return results;
    }

    /// <summary>
    /// Removes the decorative icons Bing puts in snippets. SearXNG uses lxml's <c>remove</c>, which also drops any text right
    /// after the icon. That looks unintended, so this keeps that text. No captured page had an icon, so this is only known from a
    /// constructed snippet.
    /// </summary>
    private static void RemoveIcons(HtmlNodeCollection? paragraphs)
    {
        foreach (var paragraph in paragraphs ?? Enumerable.Empty<HtmlNode>())
        {
            var icons = paragraph.SelectNodes(".//span[@class=\"algoSlug_icon\"]");
            foreach (var icon in icons?.ToList() ?? [])
            {
                icon.Remove();
            }
        }
    }

    /// <summary>Bing's <c>ck/a</c> redirects carry the real target, base64url-encoded after <c>a1</c>, in <c>u</c>.</summary>
    internal static string DecodeRedirect(string href)
    {
        if (!href.StartsWith(RedirectPrefix, StringComparison.Ordinal))
        {
            return href;
        }

        var value = UrlEncoding.FirstQueryValue(ParsedUrl.Parse(href).Query, "u");
        if (value is null || !value.StartsWith("a1", StringComparison.Ordinal))
        {
            return href;
        }

        try
        {
            return Encoding.UTF8.GetString(Base64Url.DecodeFromChars(value.AsSpan(2).TrimEnd('=')));
        }
        catch (FormatException)
        {
            // Keep the hit with its redirect address rather than losing it.
            return href;
        }
    }
}
