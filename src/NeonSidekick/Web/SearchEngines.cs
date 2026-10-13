using System.Globalization;
using System.Net;
using System.Text.Json;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.Web;

/// <summary>One hit: what the engine listed.</summary>
public sealed record SearchResult(string Title, string Url, string Snippet);

/// <summary>A search's answer: the hits, or an <c>Error:</c> sentence.</summary>
public sealed record SearchOutcome(IReadOnlyList<SearchResult> Results, string Error)
{
    public bool Ok => Error.Length == 0;

    /// <summary>
    /// The results page a person could open in the Browser window to get the search past a bot check (2026-10-13, <see cref="BotCheck"/>),
    /// with why it was refused: DuckDuckGo's own, when it answered with its "bots use DuckDuckGo too" page or a refusal that names no
    /// wait; null otherwise.
    /// </summary>
    public BotCheckRequest? Gate { get; init; }

    public static SearchOutcome Failed(string error) => new([], error);
}

/// <summary>The search seam: the built-in DuckDuckGo scrape or a SearXNG instance; a fake in tests.</summary>
public interface ISearchEngine
{
    /// <summary>The name on the result header (<see cref="WebText.DuckDuckGoName"/> / <see cref="WebText.SearxngName"/>).</summary>
    string Name { get; }

    /// <summary>
    /// What a browser sends as <c>Referer</c> when its user clicks one of the engine's hits (2026-10-11, <see cref="ResultReferers"/>);
    /// null when it sends none.
    /// </summary>
    Uri? ResultReferer { get; }

    /// <summary>At most <paramref name="max"/> hits for <paramref name="query"/>; <paramref name="options"/> is how the engine's own page is fetched.</summary>
    Task<SearchOutcome> SearchAsync(string query, int max, FetchOptions options, CancellationToken cancellationToken);
}

/// <summary>
/// The built-in engine: DuckDuckGo's HTML endpoint (<c>html.duckduckgo.com/html/</c>, the one the
/// scrapers use) read like any other page through <see cref="WebFetcher"/> — the browser headers,
/// and under the <c>default</c> / <c>chromium</c> modes the headless browser when the endpoint
/// refuses — and parsed off <see cref="HtmlTokenizer"/>: the <c>result__a</c> anchors (title, and
/// the real URL out of the <c>uddg=</c> redirect) with the <c>result__snippet</c> under each. Ads
/// are skipped. Requests are spaced <see cref="MinInterval"/> apart: the endpoint rate-limits, and a
/// polite pace is most of what keeps it answering.
/// </summary>
public sealed class DuckDuckGoSearch : ISearchEngine
{
    public static readonly Uri Endpoint = new("https://html.duckduckgo.com/html/");
    public static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(1500);

    private const string Category = "Web";
    private readonly WebFetcher _fetcher;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private DateTimeOffset _last = DateTimeOffset.MinValue;

    public DuckDuckGoSearch(WebFetcher fetcher, TimeProvider time)
    {
        _fetcher = fetcher ?? throw new ArgumentNullException(nameof(fetcher));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    public string Name => WebText.DuckDuckGoName;

    /// <summary>The results page's origin: its links leave with that alone (referrer policy <c>origin</c>).</summary>
    public static readonly Uri Origin = new("https://html.duckduckgo.com/");

    public Uri? ResultReferer => Origin;

    /// <summary>The results page for <paramref name="query"/>. Pinned.</summary>
    public static Uri QueryUrl(string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return new Uri(Endpoint, "?q=" + Uri.EscapeDataString(query.Trim()) + "&kl=us-en");
    }

    /// <summary>The endpoint's "bots use DuckDuckGo too" page, served with nothing to parse. Pinned.</summary>
    public static bool LooksRefused(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        return html.Contains("anomaly-modal", StringComparison.OrdinalIgnoreCase)
            || html.Contains("bots use DuckDuckGo too", StringComparison.OrdinalIgnoreCase)
            || html.Contains("challenge-form", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The refusal's detail for the endpoint's rate limit (2026-10-12): an HTTP 202 with no hits, a page none of
    /// <see cref="LooksRefused"/>'s marks are on, so it read as "no results" — the model told nothing exists, and
    /// <c>LiveWebTests.Search_DuckDuckGo_OverTheRealClient</c> failing now and then. The scrapers read a 202 as the rate limit
    /// too; one that does carry hits is still the answer. Pinned.
    /// </summary>
    public const string RateLimitDetail = "HTTP 202, its rate limit";

    /// <summary>The real URL behind a result link: the <c>uddg</c> parameter of the redirect, else the link itself. Pinned.</summary>
    public static string? ResolveHref(string? href)
    {
        if (string.IsNullOrWhiteSpace(href))
        {
            return null;
        }

        href = href.Trim();
        if (href.StartsWith("//", StringComparison.Ordinal))
        {
            href = "https:" + href;
        }

        int at = href.IndexOf("uddg=", StringComparison.Ordinal);
        if (at >= 0)
        {
            int end = href.IndexOf('&', at);
            string encoded = end < 0 ? href[(at + 5)..] : href[(at + 5)..end];
            string decoded = Uri.UnescapeDataString(encoded);
            return Uri.TryCreate(decoded, UriKind.Absolute, out var target) ? target.AbsoluteUri : null;
        }

        if (href.Contains("/y.js", StringComparison.Ordinal) || href.Contains("ad_provider", StringComparison.Ordinal))
        {
            return null;
        }

        return Uri.TryCreate(href, UriKind.Absolute, out var url) && WebFetcher.IsHttp(url) ? url.AbsoluteUri : null;
    }

    /// <summary>The hits on a results page, in order, ads left out. Pure; pinned.</summary>
    public static IReadOnlyList<SearchResult> Parse(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        var results = new List<SearchResult>();
        string? capture = null;     // "title" or "snippet"
        string captureElement = "";
        int captureDepth = 0;
        var text = new System.Text.StringBuilder();
        string? href = null;
        foreach (var token in HtmlTokenizer.Tokenize(html))
        {
            if (capture is not null)
            {
                switch (token.Kind)
                {
                    case HtmlTokenKind.Text:
                        text.Append(token.Text);
                        continue;
                    case HtmlTokenKind.Open when token.Name == captureElement && !token.SelfClosing:
                        captureDepth++;
                        continue;
                    case HtmlTokenKind.Close when token.Name == captureElement:
                        if (--captureDepth > 0)
                        {
                            continue;
                        }

                        string captured = HtmlToMarkdown.Collapse(text.ToString());
                        if (capture == "title")
                        {
                            var url = ResolveHref(href);
                            if (url is not null && captured.Length > 0)
                            {
                                results.Add(new SearchResult(captured, url, ""));
                            }
                        }
                        else if (results.Count > 0 && results[^1].Snippet.Length == 0)
                        {
                            results[^1] = results[^1] with { Snippet = captured };
                        }

                        capture = null;
                        text.Clear();
                        continue;
                    default:
                        continue;
                }
            }

            if (token.Kind != HtmlTokenKind.Open || token.SelfClosing)
            {
                continue;
            }

            if (token.Name == "a" && token.HasClass("result__a"))
            {
                capture = "title";
                captureElement = "a";
                captureDepth = 1;
                href = token.Attribute("href");
            }
            else if (token.HasClass("result__snippet"))
            {
                capture = "snippet";
                captureElement = token.Name;
                captureDepth = 1;
            }
        }

        return results;
    }

    public async Task<SearchOutcome> SearchAsync(string query, int max, FetchOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(options);
        await PaceAsync(cancellationToken).ConfigureAwait(false);
        // Judged like any fetch (2026-09-18): under local_area_network the public endpoint is refused — the user's call.
        var url = QueryUrl(query);
        var page = await _fetcher.FetchAsync(url, options with { Referer = Endpoint }, cancellationToken).ConfigureAwait(false);
        if (!page.Ok)
        {
            return SearchOutcome.Failed(page.Outcome switch
            {
                FetchOutcome.Blocked => WebText.SearchRefused(Name, page.Detail),
                // The network mode's own sentence names the host and the mode; nothing to add.
                FetchOutcome.LanRefused or FetchOutcome.InternetRefused => page.Error,
                _ => WebText.SearchFailed(Name, page.Detail.Length > 0 ? page.Detail : page.Error),
            }) with { Gate = BotCheck.Offers(page) ? new BotCheckRequest(url, BotCheckUse.Search, BotCheck.WhyOf(page.Detail)) : null };
        }

        if (LooksRefused(page.Text))
        {
            DiagnosticLog.Info(Category, "DuckDuckGo answered with its bot page.");
            return SearchOutcome.Failed(WebText.SearchRefused(Name, BotCheckDetail)) with { Gate = new BotCheckRequest(url, BotCheckUse.Search, BotCheckDetail) };
        }

        return Outcome(query, max, page.Text, page.Engine, page.Status);
    }

    /// <summary>The refusal's detail for DuckDuckGo's "bots use DuckDuckGo too" page. Pinned.</summary>
    public const string BotCheckDetail = "a bot check";

    /// <summary>
    /// The hits on <paramref name="html"/>, a results page the endpoint answered (or, since 2026-10-13, the one a person let through
    /// its bot check in the Browser window), at most <paramref name="max"/>; a 202 with none is the rate limit's refusal.
    /// </summary>
    public SearchOutcome Outcome(string query, int max, string html, string engine, int status)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(html);
        var results = Parse(html);
        DiagnosticLog.Info(Category, $"DuckDuckGo \"{query}\": {results.Count.ToString(CultureInfo.InvariantCulture)} results ({engine}, HTTP {status.ToString(CultureInfo.InvariantCulture)}, {html.Length.ToString(CultureInfo.InvariantCulture)} chars).");
        if (results.Count == 0 && status == (int)HttpStatusCode.Accepted)
        {
            return SearchOutcome.Failed(WebText.SearchRefused(Name, RateLimitDetail));
        }

        return new SearchOutcome(results.Take(max).ToArray(), "");
    }

    private async Task PaceAsync(CancellationToken cancellationToken)
    {
        TimeSpan wait;
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            wait = MinInterval - (now - _last);
            _last = now + (wait > TimeSpan.Zero ? wait : TimeSpan.Zero);
        }

        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait, _time, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// A SearXNG instance (the setting <c>Web SearXNG URL</c>): its JSON API, <c>/search?q=…&amp;format=json</c>,
/// which the instance must have enabled (<c>search.formats: [html, json]</c> in <c>settings.yml</c>;
/// without it the instance answers 403). The app's own request — <see cref="LanPolicy.ExemptKey"/>
/// set, so the instance may sit on this machine with <c>Browser allow LAN</c> off — and the plain
/// client, not the browser: an instance is not a site that blocks.
/// </summary>
public sealed class SearxngSearch : ISearchEngine
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private const string Category = "Web";
    private readonly HttpClient _http;
    private readonly Uri _baseUrl;

    public SearxngSearch(HttpClient http, Uri baseUrl)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _baseUrl = baseUrl ?? throw new ArgumentNullException(nameof(baseUrl));
    }

    public string Name => WebText.SearxngName;

    /// <summary>None: SearXNG's pages send no referer (policy <c>no-referrer</c>), and the instance's address — often a LAN name — is no other site's business.</summary>
    public Uri? ResultReferer => null;

    /// <summary>The instance's search URL for <paramref name="query"/>: the base with <c>/search</c> and the JSON format. Pinned.</summary>
    public static Uri QueryUrl(Uri baseUrl, string query)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(query);
        string root = baseUrl.GetLeftPart(UriPartial.Path).TrimEnd('/');
        if (root.EndsWith("/search", StringComparison.OrdinalIgnoreCase))
        {
            root = root[..^"/search".Length];
        }

        return new Uri(root + "/search?q=" + Uri.EscapeDataString(query.Trim()) + "&format=json&language=en");
    }

    /// <summary>The hits out of the JSON body: <c>results[].title / url / content</c>. Pure; pinned.</summary>
    public static IReadOnlyList<SearchResult> Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var results = new List<SearchResult>();
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("results", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                return results;
            }

            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string url = Read(item, "url");
                if (url.Length == 0)
                {
                    continue;
                }

                results.Add(new SearchResult(HtmlToMarkdown.Collapse(Read(item, "title")), url, HtmlToMarkdown.Collapse(Read(item, "content"))));
            }
        }
        catch (JsonException)
        {
            // Not JSON at all: no results, and the caller says why.
        }

        return results;
    }

    private static string Read(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    public async Task<SearchOutcome> SearchAsync(string query, int max, FetchOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var url = QueryUrl(_baseUrl, query);
        string shown = _baseUrl.GetLeftPart(UriPartial.Authority);
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(Timeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            request.Headers.TryAddWithoutValidation("User-Agent", BrowserIdentity.Fallback.UserAgent);
            request.Options.Set(LanPolicy.ExemptKey, true);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, budget.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                return SearchOutcome.Failed(WebText.SearxngRefused(shown));
            }

            if (!response.IsSuccessStatusCode)
            {
                return SearchOutcome.Failed(WebText.SearchFailed(Name, $"HTTP {((int)response.StatusCode).ToString(CultureInfo.InvariantCulture)} from {shown}"));
            }

            string body = await response.Content.ReadAsStringAsync(budget.Token).ConfigureAwait(false);
            var results = Parse(body);
            DiagnosticLog.Info(Category, $"SearXNG \"{query}\": {results.Count.ToString(CultureInfo.InvariantCulture)} results from {shown}.");
            return new SearchOutcome(results.Take(max).ToArray(), "");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return SearchOutcome.Failed(WebText.SearxngUnreachable(shown, $"no answer within {Llm.LlmTimeouts.Format(Timeout)}"));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException)
        {
            return SearchOutcome.Failed(WebText.SearxngUnreachable(shown, ex.Message));
        }
    }
}
