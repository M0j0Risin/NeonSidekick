// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text.Json;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using SearXNG.Net.Html;
using SearXNG.Net.Locales;
using SearXNG.Net.Net;

namespace SearXNG.Net.Engines;

/// <summary>
/// Startpage's web results, which come from Google: a port of SearXNG's <c>searx/engines/startpage.py</c>. A search posts Startpage's
/// own form, including a code from its home page. SearXNG also solves the proof-of-work challenge Startpage can answer with. This
/// reports it as a CAPTCHA instead, so the engine rests.
/// </summary>
public sealed partial class StartpageEngine : IEngine
{
    private const string BaseUrl = "https://www.startpage.com";
    private const string CaptchaUrl = "https://www.startpage.com/sp/captcha";
    private const string ChallengeMarker = "id=\"anubis_challenge\"";
    private const string ResultsStart = "React.createElement(UIStartpage.AppSerpWeb, {";
    private const string DefaultRegion = "en_US";
    private const string DefaultLanguage = "english";
    private const int MaximumPage = 18;

    private static readonly TimeSpan CodeLifetime = TimeSpan.FromHours(1);

    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private (string Code, DateTimeOffset Expires)? _code;

    /// <summary>Creates the engine.</summary>
    /// <param name="time">The clock for the form-code cache; the system clock when null.</param>
    public StartpageEngine(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <inheritdoc/>
    public string Name => "startpage";

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

        var code = await GetCodeAsync(query, http, cancellationToken).ConfigureAwait(false);
        try
        {
            var response = await EngineHttp.PostFormAsync(
                http,
                new Uri(BaseUrl + "/sp/search"),
                BuildForm(query, code),
                [
                    .. PageHeaders(query),
                    new("Cookie", "preferences=" + BuildPreferences(query)),
                    new("Origin", BaseUrl),
                    new("Referer", BaseUrl + "/"),
                ],
                cancellationToken).ConfigureAwait(false);

            ThrowIfChallenged(response);
            return Parse(response.Text);
        }
        catch (EngineException)
        {
            // Only the code this search used: an overlapping search may have stored a newer one.
            lock (_gate)
            {
                if (_code is { } stored && stored.Code == code)
                {
                    _code = null;
                }
            }

            throw;
        }
    }

    private async Task<string> GetCodeAsync(EngineQuery query, HttpClient http, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_code is { } cached && cached.Expires > _time.GetUtcNow())
            {
                return cached.Code;
            }
        }

        var response = await EngineHttp.GetAsync(http, new Uri(BaseUrl + "/"), PageHeaders(query), cancellationToken).ConfigureAwait(false);
        ThrowIfChallenged(response);
        var code = ParseCode(response.Text);
        if (code is null)
        {
            // SearXNG's own reading of this: https://github.com/searxng/searxng/pull/695
            throw new EngineException(EngineFailureKind.Captcha, "Startpage's home page had no form code", EngineHttp.CaptchaRest, response.Url);
        }

        lock (_gate)
        {
            _code = (code, _time.GetUtcNow() + CodeLifetime);
        }

        return code;
    }

    private static KeyValuePair<string, string>[] PageHeaders(EngineQuery query) =>
    [
        new("Accept", EngineHttp.PageAccept),
        new("Accept-Language", EngineHttp.AcceptLanguage(query.Locale)),
        new("User-Agent", EngineHttp.ChromeUserAgent),
    ];

    private static void ThrowIfChallenged(EngineResponse response)
    {
        var location = response.Location?.IsAbsoluteUri == true ? response.Location.AbsoluteUri : "";
        if (response.Url.AbsoluteUri.StartsWith(CaptchaUrl, StringComparison.Ordinal) || location.StartsWith(CaptchaUrl, StringComparison.Ordinal))
        {
            throw new EngineException(EngineFailureKind.Captcha, "Startpage sent the request to its CAPTCHA", EngineHttp.CaptchaRest, response.Url);
        }

        if (response.Text.Contains(ChallengeMarker, StringComparison.Ordinal))
        {
            throw new EngineException(EngineFailureKind.Captcha, "Startpage answered with its proof-of-work challenge", EngineHttp.CaptchaRest, response.Url);
        }
    }

    internal static string? ParseCode(string page)
    {
        var doc = HtmlText.NewDocument();
        doc.LoadHtml(page);
        var value = doc.DocumentNode.SelectSingleNode("//form[@id=\"search\"]//input[@name=\"sc\"]")?.GetAttributeValue("value", "");
        return string.IsNullOrEmpty(value) ? null : HtmlText.Unescape(value);
    }

    internal static string RegionFor(string locale) => EngineLocale.Resolve(locale, StartpageLocales.Regions, DefaultRegion)!;

    internal static string LanguageFor(string locale) => EngineLocale.Resolve(locale, StartpageLocales.Languages, DefaultLanguage)!;

    private static string SafeSearchValue(SafeSearch level) => level switch
    {
        SafeSearch.Moderate => "moderate",
        SafeSearch.Strict => "heavy",
        _ => "none",
    };

    internal static IReadOnlyList<KeyValuePair<string, string>> BuildForm(EngineQuery query, string code)
    {
        var language = LanguageFor(query.Locale);
        var form = new List<KeyValuePair<string, string>>
        {
            new("query", query.Query),
            new("cat", "web"),
            new("t", "device"),
            new("sc", code),
            new("with_date", ""),
            new("abd", "1"),
            new("abe", "1"),
            new("qsr", "all"),
            new("qadf", SafeSearchValue(query.SafeSearch)),
            new("language", language),
            new("lui", language),
            new("segment", "startpage.udog"),
        };
        if (query.PageNumber > 1)
        {
            form.Add(new("page", query.PageNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        return form;
    }

    /// <summary>Startpage's preferences cookie: the region lives here, the language also in the form.</summary>
    internal static string BuildPreferences(EngineQuery query)
    {
        var language = LanguageFor(query.Locale);
        (string Name, string Value)[] preferences =
        [
            ("date_time", "world"),
            ("disable_family_filter", SafeSearchValue(query.SafeSearch)),
            ("disable_open_in_new_window", "0"),
            ("enable_post_method", "1"),
            ("enable_proxy_safety_suggest", "1"),
            ("enable_stay_control", "1"),
            ("instant_answers", "1"),
            ("lang_homepage", "s/device/en/"),
            ("num_of_results", "10"),
            ("suggestions", "1"),
            ("wt_unit", "celsius"),
            ("language", language),
            ("language_ui", language),
            ("search_results_region", RegionFor(query.Locale)),
        ];
        return string.Join("N1N", preferences.Select(p => p.Name + "EEE" + p.Value));
    }

    internal static IReadOnlyList<EngineResult> Parse(string page)
    {
        int start = page.IndexOf(ResultsStart, StringComparison.Ordinal);
        if (start < 0)
        {
            throw new EngineException(EngineFailureKind.UnexpectedResponse, "Startpage's page had no results data.");
        }

        int brace = start + ResultsStart.Length - 1;
        int end;
        try
        {
            end = JsLiteral.FindClosing(page, brace, '{', '}');
        }
        catch (FormatException)
        {
            throw new EngineException(EngineFailureKind.UnexpectedResponse, "Startpage's page had no results data.");
        }

        var results = new List<EngineResult>();
        try
        {
            using var json = JsonDocument.Parse(page.AsMemory(brace, end - brace + 1));
            if (!TryGet(json.RootElement, "render", out var render)
                || !TryGet(render, "presenter", out var presenter)
                || !TryGet(presenter, "regions", out var regions)
                || !TryGet(regions, "mainline", out var mainline)
                || mainline.ValueKind != JsonValueKind.Array)
            {
                // Unlike SearXNG, which reads this as no results: a page that lost its structure shouldn't look like one.
                throw new EngineException(EngineFailureKind.UnexpectedResponse, "Startpage's results data had no results region.");
            }

            foreach (var region in mainline.EnumerateArray())
            {
                if (EngineJson.Text(region, "display_type") != "web-google" || !TryGet(region, "results", out var items) || items.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var item in items.EnumerateArray())
                {
                    var url = EngineJson.Text(item, "clickUrl");
                    if (!string.IsNullOrEmpty(url))
                    {
                        results.Add(new EngineResult(url, HtmlText.FromFragment(EngineJson.Text(item, "title")), RemoveLeadingDate(HtmlText.FromFragment(EngineJson.Text(item, "description")))));
                    }
                }
            }
        }
        catch (JsonException e)
        {
            throw new EngineException(EngineFailureKind.UnexpectedResponse, "Startpage's results data couldn't be read: " + e.Message);
        }

        return results;
    }

    private static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value);
    }

    /// <summary>Snippets can start with a date, such as "2 Sep 2014 ... " or "5 days ago ... ", which isn't part of the snippet.</summary>
    internal static string RemoveLeadingDate(string content)
    {
        if (!LeadingDate().IsMatch(content))
        {
            return content;
        }

        return content[(content.IndexOf("...", StringComparison.Ordinal) + 4)..];
    }

    [GeneratedRegex(@"\A(?:(?:[1-9]|[1-2][0-9]|3[0-1]) [A-Z][a-z]{2} [0-9]{4}|[0-9]+ days? ago) \.\.\. ")]
    private static partial Regex LeadingDate();
}
