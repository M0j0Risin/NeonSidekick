// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Globalization;
using System.Text;
using SearXNG.Net.Html;
using SearXNG.Net.Locales;
using SearXNG.Net.Net;

namespace SearXNG.Net.Engines;

/// <summary>
/// Brave's web results: a port of SearXNG's <c>searx/engines/brave.py</c>, as its settings configure it. The results are in a
/// JavaScript literal in the page. Its video results and related queries aren't main results, so they're left out.
/// </summary>
public sealed class BraveEngine : IEngine
{
    private const string BaseUrl = "https://search.brave.com/";
    private const string AllRegions = "all";
    private const string DefaultInterfaceLanguage = "en-us";
    private const string DataMarker = "data: [";
    private const int MaximumPage = 10;

    /// <inheritdoc/>
    public string Name => "brave";

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
        if (query.PageNumber > MaximumPage)
        {
            return [];
        }

        var response = await EngineHttp.GetAsync(
            http,
            BuildUrl(query),
            [
                new("Accept", EngineHttp.PageAccept),
                new("Accept-Language", EngineHttp.AcceptLanguage(query.Locale)),
                new("Cookie", BuildCookie(query)),
                new("User-Agent", EngineHttp.ChromeUserAgent),
            ],
            cancellationToken).ConfigureAwait(false);
        return Parse(response.Text);
    }

    internal static Uri BuildUrl(EngineQuery query)
    {
        var url = new StringBuilder(BaseUrl).Append("search?q=").Append(UrlEncoding.QuotePlus(query.Query)).Append("&source=web");
        if (query.PageNumber > 1)
        {
            url.Append("&offset=").Append((query.PageNumber - 1).ToString(CultureInfo.InvariantCulture));
        }

        return new Uri(url.ToString());
    }

    internal static string BuildCookie(EngineQuery query)
    {
        var region = query.Locale == EngineQuery.AllLocales ? AllRegions : EngineLocale.Resolve(query.Locale, BraveLocales.Regions, AllRegions)!;
        var language = EngineLocale.Resolve(query.Locale, BraveLocales.InterfaceLanguages, DefaultInterfaceLanguage);
        var safeSearch = query.SafeSearch switch
        {
            SafeSearch.Moderate => "moderate",
            SafeSearch.Strict => "strict",
            _ => "off",
        };
        return $"safesearch={safeSearch}; useLocation=0; summarizer=0; country={region.Split('-')[^1].ToLowerInvariant()}; ui_lang={language}";
    }

    internal static IReadOnlyList<EngineResult> Parse(string page)
    {
        int marker = page.IndexOf(DataMarker, StringComparison.Ordinal);
        if (marker < 0)
        {
            throw new EngineException(EngineFailureKind.UnexpectedResponse, "Brave's page had no data.");
        }

        List<object?> elements;
        try
        {
            elements = JsLiteral.ParseArrayOfCalls(page, marker + DataMarker.Length - 1);
        }
        catch (FormatException e)
        {
            throw new EngineException(EngineFailureKind.UnexpectedResponse, "Brave's data couldn't be read: " + e.Message);
        }

        // Where SearXNG reads: data[1].data.body.response.web.
        if (elements.Count < 2 || Field(elements[1], "data") is not Dictionary<string, object?> data)
        {
            throw new EngineException(EngineFailureKind.UnexpectedResponse, "Brave's data had an unexpected shape.");
        }

        if (data.GetValueOrDefault("noResults") is true)
        {
            return [];
        }

        if (Field(data, "body") is not Dictionary<string, object?> body || Field(body, "response") is not Dictionary<string, object?> responseData || !responseData.TryGetValue("web", out var web))
        {
            throw new EngineException(EngineFailureKind.UnexpectedResponse, "Brave's data had an unexpected shape.");
        }

        if (web is null || web is Dictionary<string, object?> { Count: 0 })
        {
            return [];
        }

        if (Field(web, "results") is not List<object?> items)
        {
            throw new EngineException(EngineFailureKind.UnexpectedResponse, "Brave's data had no results list.");
        }

        var results = new List<EngineResult>(items.Count);
        foreach (var item in items)
        {
            var url = Field(item, "url") as string;
            if (!string.IsNullOrEmpty(url))
            {
                results.Add(new EngineResult(url, Field(item, "title") as string ?? "", HtmlText.FromFragment(Field(item, "description") as string)));
            }
        }

        return results;
    }

    private static object? Field(object? value, string name) =>
        value is Dictionary<string, object?> fields ? fields.GetValueOrDefault(name) : null;
}
