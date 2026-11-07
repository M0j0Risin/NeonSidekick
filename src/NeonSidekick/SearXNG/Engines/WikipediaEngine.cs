// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Net;
using System.Text;
using System.Text.Json;
using SearXNG.Net.Html;
using SearXNG.Net.Locales;
using SearXNG.Net.Net;

namespace SearXNG.Net.Engines;

/// <summary>
/// The Wikipedia article titled by the query, from Wikipedia's official API: a port of SearXNG's <c>searx/engines/wikipedia.py</c>.
/// SearXNG shows it as an infobox, which isn't ported, so this uses that engine's <c>list</c> form: an ordinary result. A query that
/// doesn't name an article gets nothing.
/// </summary>
public sealed class WikipediaEngine : IEngine
{
    private const string DefaultLanguage = "en";
    private const string DefaultHost = "en.wikipedia.org";
    private const string InvalidTitleType = "https://mediawiki.org/wiki/HyperSwitch/errors/bad_request";
    private const string InvalidTitleDetail = "title-invalid-characters";

    /// <inheritdoc/>
    public string Name => "wikipedia";

    /// <inheritdoc/>
    public string Category => "general";

    /// <inheritdoc/>
    public double Weight => 1.0;

    /// <inheritdoc/>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <inheritdoc/>
    public bool Paging => false;

    /// <summary>
    /// The client's name. Wikimedia's policy asks API clients to say who they are and how to reach them, so a host should set this to
    /// its own name and a contact address.
    /// </summary>
    public string UserAgent { get; init; } = "SearXNG.Net/1.0";

    /// <inheritdoc/>
    public async Task<IReadOnlyList<EngineResult>> SearchAsync(EngineQuery query, HttpClient http, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(http);

        // A path segment of . or .. would be resolved away, asking for another address.
        if (query.Query is "." or "..")
        {
            return [];
        }

        var response = await EngineHttp.GetWithoutRaisingAsync(
            http,
            BuildUrl(query),
            [
                new("Accept", "application/json"),
                new("Accept-Language", EngineHttp.AcceptLanguage(query.Locale)),
                new("User-Agent", UserAgent),
            ],
            cancellationToken).ConfigureAwait(false);

        return IsNoArticle(response.Status, response.Text) ? [] : ParseChecked(response);
    }

    private static IReadOnlyList<EngineResult> ParseChecked(EngineResponse response)
    {
        EngineHttp.RaiseForHttpError((int)response.Status, response.Server, response.Text, response.Url);
        return Parse(response.Text);
    }

    internal static Uri BuildUrl(EngineQuery query)
    {
        var title = IsLowerCase(query.Query) ? ToTitleCase(query.Query) : query.Query;
        return new Uri("https://" + HostFor(query.Locale) + "/api/rest_v1/page/summary/" + UrlEncoding.Quote(title));
    }

    internal static string HostFor(string locale)
    {
        var language = EngineLocale.Resolve(locale, WikipediaLocales.Languages, DefaultLanguage)!;
        var tag = EngineLocale.Resolve(locale, WikipediaLocales.Regions, language)!;
        return WikipediaLocales.Hosts.GetValueOrDefault(tag, DefaultHost);
    }

    /// <summary>A 404, or the 400 for a title Wikipedia can't have: neither means anything went wrong.</summary>
    internal static bool IsNoArticle(HttpStatusCode status, string text)
    {
        if (status == HttpStatusCode.NotFound)
        {
            return true;
        }

        if (status != HttpStatusCode.BadRequest)
        {
            return false;
        }

        try
        {
            using var json = JsonDocument.Parse(text);
            return EngineJson.Text(json.RootElement, "type") == InvalidTitleType && EngineJson.Text(json.RootElement, "detail") == InvalidTitleDetail;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static IReadOnlyList<EngineResult> Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var display = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("titles", out var titles) ? EngineJson.Text(titles, "display") : null;
            var title = HtmlText.FromFragment(string.IsNullOrEmpty(display) ? EngineJson.Text(root, "title") : display);
            var link = root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("content_urls", out var urls)
                && urls.ValueKind == JsonValueKind.Object
                && urls.TryGetProperty("desktop", out var desktop)
                    ? EngineJson.Text(desktop, "page")
                    : null;
            if (string.IsNullOrEmpty(link))
            {
                throw new EngineException(EngineFailureKind.UnexpectedResponse, "Wikipedia's answer had no link.");
            }

            return [new EngineResult(link, title, EngineJson.Text(root, "description") ?? "")];
        }
        catch (JsonException e)
        {
            throw new EngineException(EngineFailureKind.UnexpectedResponse, "Wikipedia's answer couldn't be read: " + e.Message);
        }
    }

    /// <summary>Python's <c>str.islower</c>: something is lowercase, and nothing is uppercase or titlecase.</summary>
    internal static bool IsLowerCase(string text)
    {
        bool any = false;
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsUpper(rune) || Rune.GetUnicodeCategory(rune) == System.Globalization.UnicodeCategory.TitlecaseLetter)
            {
                return false;
            }

            any |= Rune.IsLower(rune);
        }

        return any;
    }

    /// <summary>Python's <c>str.title</c>, for the lowercase text it's used on: each run of cased letters gets a capital, so <c>it's</c> gives <c>It'S</c>.</summary>
    internal static string ToTitleCase(string text)
    {
        var result = new StringBuilder(text.Length);
        bool afterCased = false;
        foreach (var rune in text.EnumerateRunes())
        {
            result.Append((afterCased ? rune : Rune.ToUpperInvariant(rune)).ToString());
            afterCased = Rune.IsUpper(rune) || Rune.IsLower(rune) || Rune.GetUnicodeCategory(rune) == System.Globalization.UnicodeCategory.TitlecaseLetter;
        }

        return result.ToString();
    }
}
