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

    public static SearchOutcome Failed(string error) => new([], error);
}

/// <summary>The search seam: the built-in metasearch or a SearXNG instance; a fake in tests.</summary>
public interface ISearchEngine
{
    /// <summary>The name on the result header (<see cref="WebText.MetaSearchName"/> / <see cref="WebText.SearxngName"/>).</summary>
    string Name { get; }

    /// <summary>At most <paramref name="max"/> hits for <paramref name="query"/>; <paramref name="options"/> is how the engine's own page is fetched.</summary>
    Task<SearchOutcome> SearchAsync(string query, int max, FetchOptions options, CancellationToken cancellationToken);
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

    /// <summary>The instance's search URL for <paramref name="query"/>: the base with <c>/search</c> and the JSON format. Pinned.</summary>
    public static Uri QueryUrl(Uri baseUrl, string query)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(query);
        return new Uri(Root(baseUrl) + "/search?q=" + Uri.EscapeDataString(query.Trim()) + "&format=json&language=en");
    }

    /// <summary>
    /// The instance's own results page for <paramref name="query"/>, the one a person reads: <see cref="QueryUrl"/> without the JSON
    /// format (2026-10-13, the Browser window's address box searching words). Pinned.
    /// </summary>
    public static Uri PageUrl(Uri baseUrl, string query)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(query);
        return new Uri(Root(baseUrl) + "/search?q=" + Uri.EscapeDataString(query.Trim()));
    }

    // The base without its query, a trailing slash or a /search of its own.
    private static string Root(Uri baseUrl)
    {
        string root = baseUrl.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return root.EndsWith("/search", StringComparison.OrdinalIgnoreCase) ? root[..^"/search".Length] : root;
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
