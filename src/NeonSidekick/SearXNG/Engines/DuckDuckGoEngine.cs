// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Globalization;
using System.Text.Json;
using HtmlAgilityPack;
using SearXNG.Net.Html;
using SearXNG.Net.Net;

namespace SearXNG.Net.Engines;

/// <summary>
/// DuckDuckGo's web results: a port of SearXNG's <c>searx/engines/duckduckgo_web.py</c>. The results API needs a token only its
/// HTML page has, so page 1 scrapes that page for the API's address, and each answer gives the address of the next page.
/// </summary>
public sealed class DuckDuckGoEngine : IEngine
{
    private const string BaseUrl = "https://duckduckgo.com";
    private const int MaximumQueryLength = 499;

    // Answers as well from HttpClient as from SearXNG's Firefox impersonation (measured 2026-11-06).
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:159.0) Gecko/20100101 Firefox/159.0";

    private static readonly TimeSpan FirstPageLinkLifetime = TimeSpan.FromHours(2);
    private static readonly TimeSpan NextPageLinkLifetime = TimeSpan.FromHours(1);
    private static readonly Uri ApiBase = new("https://links.duckduckgo.com");

    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, (string Url, DateTimeOffset Expires)> _links = new(StringComparer.Ordinal);

    /// <summary>Creates the engine.</summary>
    /// <param name="time">The clock for the page-link cache; the system clock when null.</param>
    public DuckDuckGoEngine(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <inheritdoc/>
    public string Name => "duckduckgo";

    /// <inheritdoc/>
    public string Category => "general";

    /// <inheritdoc/>
    public double Weight => 1.0;

    /// <inheritdoc/>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <inheritdoc/>
    public bool Paging => true;

    /// <inheritdoc/>
    public async Task<IReadOnlyList<EngineResult>> SearchAsync(EngineQuery query, HttpClient http, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(http);
        if (query.Query.Length > MaximumQueryLength)
        {
            return [];
        }

        var language = new KeyValuePair<string, string>("Accept-Language", EngineHttp.AcceptLanguage(query.Locale));
        var agent = new KeyValuePair<string, string>("User-Agent", UserAgent);
        var apiUrl = query.PageNumber > 1
            ? CachedLink(query.Query, query.PageNumber)
            : await FirstPageLinkAsync(query.Query, http, [language, agent], cancellationToken).ConfigureAwait(false);

        // A later page is only reachable through the page before it.
        if (apiUrl is null)
        {
            return [];
        }

        KeyValuePair<string, string>[] headers =
        [
            language,
            agent,
            new("Accept", "*/*"),
            new("Sec-Fetch-Dest", "script"),
            new("Sec-Fetch-Mode", "no-cors"),
            new("Sec-Fetch-Site", "same-site"),
            new("Referer", BaseUrl + "/"),
        ];
        try
        {
            var jsonUrl = Trusted(apiUrl.Replace("/d.js?", "/d.js?o=json&", StringComparison.Ordinal));
            var response = await EngineHttp.GetAsync(http, jsonUrl, headers, cancellationToken).ConfigureAwait(false);
            return ParseResults(response, query);
        }
        catch (EngineException) when (query.PageNumber == 1)
        {
            // Page 1 can fetch a new link. A later page is only reachable through this one, so it keeps its link to retry.
            Forget(query.Query, query.PageNumber);
            throw;
        }
    }

    private async Task<string> FirstPageLinkAsync(
        string query,
        HttpClient http,
        KeyValuePair<string, string>[] headers,
        CancellationToken cancellationToken)
    {
        var cached = CachedLink(query, 1);
        if (cached is not null)
        {
            return cached;
        }

        // SearXNG only logs a refused or changed page and returns nothing, which reads as "no results". This raises, so a
        // block or a site change shows up as a failure instead.
        var page = new Uri(BaseUrl + "/?q=" + UrlEncoding.QuotePlus(query) + "&t=h_&ia=web");
        var response = await EngineHttp.GetAsync(http, page, headers, cancellationToken).ConfigureAwait(false);
        var link = ExtractFirstPageLink(response.Text)
            ?? throw new EngineException(EngineFailureKind.UnexpectedResponse, "DuckDuckGo's page has no link to its results", null, page);
        Remember(query, 1, link, FirstPageLinkLifetime);
        return link;
    }

    internal static string? ExtractFirstPageLink(string html)
    {
        var doc = HtmlText.NewDocument();
        doc.LoadHtml(html);
        var href = doc.DocumentNode.SelectSingleNode("//link[@id='deep_preload_link']")?.GetAttributeValue("href", "");
        return string.IsNullOrEmpty(href) ? null : HtmlText.Unescape(href);
    }

    private IReadOnlyList<EngineResult> ParseResults(EngineResponse response, EngineQuery query)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(response.Text);
        }
        catch (JsonException)
        {
            if (response.Text.Contains("let jsa =", StringComparison.Ordinal))
            {
                // SearXNG works this out (_solve_jsa). This reports it and rests instead, as with Startpage's, or the next search would ask again.
                throw new EngineException(EngineFailureKind.Captcha, "DuckDuckGo sent its JavaScript challenge", EngineHttp.CaptchaRest, response.Url);
            }

            throw new EngineException(EngineFailureKind.UnexpectedResponse, "DuckDuckGo's answer isn't JSON");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("results", out var items) || items.ValueKind != JsonValueKind.Array)
            {
                throw new EngineException(EngineFailureKind.UnexpectedResponse, "DuckDuckGo's answer has no results");
            }

            var results = new List<EngineResult>();
            JsonElement last = default;
            foreach (var item in items.EnumerateArray())
            {
                last = item;
                if (EngineJson.Text(item, "u") is { } url)
                {
                    results.Add(new EngineResult(url, HtmlText.FromFragment(EngineJson.Text(item, "t")), HtmlText.FromFragment(EngineJson.Text(item, "a"))));
                }
            }

            if (EngineJson.Text(last, "n") is { Length: > 0 } next)
            {
                Remember(query.Query, query.PageNumber + 1, BaseUrl + next, NextPageLinkLifetime);
            }

            return results;
        }
    }

    /// <summary>
    /// These addresses come out of a scraped page, so unlike SearXNG this refuses to follow one anywhere but DuckDuckGo: a
    /// tampered page shouldn't be able to point the host's HTTP client at another site.
    /// </summary>
    private static Uri Trusted(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && (uri.Host == "duckduckgo.com" || uri.Host.EndsWith(".duckduckgo.com", StringComparison.Ordinal)))
        {
            return uri;
        }

        throw new EngineException(EngineFailureKind.UnexpectedResponse, "DuckDuckGo's page linked outside DuckDuckGo");
    }

    private static string CacheKey(string query, int page) => page.ToString(CultureInfo.InvariantCulture) + "|" + query;

    private void Forget(string query, int page)
    {
        lock (_gate)
        {
            _links.Remove(CacheKey(query, page));
        }
    }

    private string? CachedLink(string query, int page)
    {
        lock (_gate)
        {
            return _links.TryGetValue(CacheKey(query, page), out var link) && link.Expires > _time.GetUtcNow() ? link.Url : null;
        }
    }

    private void Remember(string query, int page, string url, TimeSpan lifetime)
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            // SearXNG's cache is a table with expiry. This one lives in memory, so it clears out what has expired as it goes.
            foreach (var key in _links.Where(entry => entry.Value.Expires <= now).Select(entry => entry.Key).ToList())
            {
                _links.Remove(key);
            }

            _links[CacheKey(query, page)] = (url, now + lifetime);
        }
    }
}
