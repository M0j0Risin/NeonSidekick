// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Globalization;
using System.Text;
using System.Text.Json;
using SearXNG.Net.Html;
using SearXNG.Net.Net;

namespace SearXNG.Net.Engines;

/// <summary>
/// Brave's web results from the Brave Search API, which needs a subscription key: a port of SearXNG's <c>searx/engines/braveapi.py</c>.
/// The key is read at each search, so a host can change it without making a new <see cref="MetaSearch"/>, which would forget which
/// engines are resting. The key goes only to <see cref="BaseUrl"/>'s host, in a header, never in the address.
/// </summary>
public sealed class BraveApiEngine : IEngine
{
    /// <summary>Where every request goes.</summary>
    public const string BaseUrl = "https://api.search.brave.com/res/v1/web/search";

    /// <summary>The header that carries the key.</summary>
    public const string KeyHeader = "X-Subscription-Token";

    // SearXNG's results_per_page default, which is also the API's most.
    private const int ResultsPerPage = 20;

    // The API's largest offset is 9, so ten pages.
    private const int MaximumPage = 10;

    private readonly Func<string?> _apiKey;

    /// <summary>Creates the engine.</summary>
    /// <param name="apiKey">Reads the key at each search; an empty or blank key fails the search without sending anything.</param>
    public BraveApiEngine(Func<string?> apiKey)
    {
        ArgumentNullException.ThrowIfNull(apiKey);
        _apiKey = apiKey;
    }

    /// <inheritdoc/>
    public string Name => "braveapi";

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

        // SearXNG's setup refuses to start without one. Here the key can come later, so a search without one fails, with no rest, so
        // it's asked again as soon as a key arrives.
        var key = _apiKey()?.Trim();
        if (string.IsNullOrEmpty(key))
        {
            throw new EngineException(EngineFailureKind.AccessDenied, "No Brave Search API key.", TimeSpan.Zero);
        }

        var response = await EngineHttp.GetWithoutRaisingAsync(
            http,
            BuildUrl(query),
            [
                new(KeyHeader, key),
                new("Accept", "application/json"),
                new("Cache-Control", "no-cache"),
            ],
            cancellationToken).ConfigureAwait(false);
        RaiseForError((int)response.Status, response.Text);
        return Parse(response.Text);
    }

    internal static Uri BuildUrl(EngineQuery query)
    {
        var safeSearch = query.SafeSearch switch
        {
            SafeSearch.Moderate => "moderate",
            SafeSearch.Strict => "strict",
            _ => "off",
        };

        // SearXNG's urlencode writes Python's False; the API documents a JSON boolean, so this sends false.
        var url = new StringBuilder(BaseUrl)
            .Append("?q=").Append(UrlEncoding.QuotePlus(query.Query))
            .Append("&count=").Append(ResultsPerPage.ToString(CultureInfo.InvariantCulture))
            .Append("&offset=").Append((query.PageNumber - 1).ToString(CultureInfo.InvariantCulture))
            .Append("&text_decorations=false")
            .Append("&safesearch=").Append(safeSearch);
        return new Uri(url.ToString());
    }

    /// <summary>
    /// SearXNG's <c>raise_for_httperror</c>, with no refused page, as there is none a person could open, and with the API's word for a
    /// rejected key. A rejected key rests as a refusal does, so a bad key isn't sent on every search.
    /// </summary>
    internal static void RaiseForError(int status, string text)
    {
        if (status < 400)
        {
            return;
        }

        var code = status.ToString(CultureInfo.InvariantCulture);
        var detail = ErrorCode(text);
        throw status switch
        {
            401 or 422 when detail is "SUBSCRIPTION_TOKEN_INVALID" => new EngineException(EngineFailureKind.AccessDenied, "The Brave Search API rejected the key.", EngineHttp.AccessDeniedRest),
            401 or 402 or 403 => new EngineException(EngineFailureKind.AccessDenied, "HTTP error " + code + (detail is null ? "" : " (" + detail + ")"), EngineHttp.AccessDeniedRest),
            429 => new EngineException(EngineFailureKind.TooManyRequests, "Too many requests" + (detail is null ? "" : " (" + detail + ")"), EngineHttp.TooManyRequestsRest),
            _ => new EngineException(EngineFailureKind.HttpError, "HTTP error " + code + (detail is null ? "" : " (" + detail + ")")),
        };
    }

    /// <summary>The <c>error.code</c> of the API's error body, such as <c>RATE_LIMITED</c>, when it's a short word; null otherwise.</summary>
    private static string? ErrorCode(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            var code = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var error) ? EngineJson.Text(error, "code") : null;

            // It goes into a message, so only a short code made of letters, digits and underscores.
            return code is { Length: > 0 and <= 64 } && code.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') ? code : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static IReadOnlyList<EngineResult> Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new EngineException(EngineFailureKind.UnexpectedResponse, "The Brave Search API's answer had an unexpected shape.");
            }

            // (data.get("web") or {}).get("results", []): an answer with no web results is no results.
            if (!root.TryGetProperty("web", out var web) || web.ValueKind != JsonValueKind.Object || !web.TryGetProperty("results", out var items))
            {
                return [];
            }

            if (items.ValueKind != JsonValueKind.Array)
            {
                throw new EngineException(EngineFailureKind.UnexpectedResponse, "The Brave Search API's results weren't a list.");
            }

            // brave.py's parse_search_result, for the fields a main result here has.
            var results = new List<EngineResult>(items.GetArrayLength());
            foreach (var item in items.EnumerateArray())
            {
                var url = EngineJson.Text(item, "url");
                if (!string.IsNullOrEmpty(url))
                {
                    results.Add(new EngineResult(url, EngineJson.Text(item, "title") ?? "", HtmlText.FromFragment(EngineJson.Text(item, "description"))));
                }
            }

            return results;
        }
        catch (JsonException e)
        {
            throw new EngineException(EngineFailureKind.UnexpectedResponse, "The Brave Search API's answer couldn't be read: " + e.Message);
        }
    }
}
