using System.Globalization;
using System.Text;

namespace NeonSidekick.Web;

/// <summary>Why a fetch did not produce a page (<see cref="WebFetcher.FetchAsync"/>).</summary>
public enum FetchOutcome
{
    Ok,

    /// <summary>No URL was given.</summary>
    NoUrl,

    /// <summary>Not an <c>http</c> / <c>https</c> URL (a file path, <c>ftp:</c>, a bare word).</summary>
    NotHttp,

    /// <summary>The host is this machine or the local network and <c>Web browser network mode</c> is <c>internet</c>.</summary>
    LanRefused,

    /// <summary>The host is on the internet and <c>Web browser network mode</c> is <c>local_area_network</c> (2026-09-18).</summary>
    InternetRefused,

    /// <summary>No answer within <see cref="WebFetcher.FetchTimeout"/>.</summary>
    Timeout,

    /// <summary>The server refused (403 / 429 / 503, a challenge page) and nothing else could be tried.</summary>
    Blocked,

    /// <summary>A PDF, an image, an archive: not a page and not text.</summary>
    Binary,

    /// <summary>The mode wants the headless browser and none was found.</summary>
    NoBrowser,

    /// <summary>The headless browser ran and produced nothing.</summary>
    BrowserFailed,

    /// <summary>Anything else: DNS, a refused connection, TLS.</summary>
    Failed,

    /// <summary>A file over <see cref="WebFetcher.MaxFileDownloadBytes"/> (<see cref="WebFetcher.DownloadAsync"/>, 2026-09-18): refused, never cut.</summary>
    TooBig,
}

/// <summary>
/// The sentences the web tools answer with: pure statics, every string pinned, the
/// <see cref="Files.FileText"/> pattern. A result is what the model reads and, flattened to its first
/// 200 characters, the one dim <c>🛠️</c> line the transcript shows, so a multi-line answer starts with a
/// header line that stands on its own. Every error starts with <c>Error:</c>. Invariant culture.
/// </summary>
public static class WebText
{
    public const string NoUrl = "Error: give the URL to fetch";
    public const string NoDownloadUrl = "Error: give the URL to download";
    public const string NoQuery = "Error: give the text to search for";
    public const string NegativeOffset = "Error: offset must be 0 or more";
    public const string Untitled = "(untitled)";

    /// <summary>The engine names on a fetch header.</summary>
    public const string HttpEngine = "http";
    public const string BrowserEngine = "headless browser";

    /// <summary>The search engine names on a search header.</summary>
    public const string DuckDuckGoName = "DuckDuckGo";
    public const string SearxngName = "SearXNG";

    public static readonly string NoBrowser =
        "Error: no headless browser (Edge, Chrome or Brave) was found; set Web browser path in /tools";

    public static readonly string TooManyLinks =
        "Error: " + Llm.Tools.OpenUrlTool.ToolName + " takes at most " + Llm.Tools.OpenUrlTool.MaxUrls.ToString(CultureInfo.InvariantCulture) + " links at a time";

    public static readonly string BadResultCount =
        "Error: max_results must be " + Llm.Tools.WebSearchTool.MinResults.ToString(CultureInfo.InvariantCulture) + " to " + Llm.Tools.WebSearchTool.MaxResults.ToString(CultureInfo.InvariantCulture);

    // ---- errors ----

    public static string NotHttp(string url) => $"Error: '{url}' is not an http or https URL";
    public static string LanRefused(string host) => $"Error: '{host}' is on this machine or the local network, which Web browser network mode 'internet' keeps off limits";
    public static string InternetRefused(string host) => $"Error: '{host}' is on the internet, which Web browser network mode 'local_area_network' keeps off limits";
    public static string Timeout(string url, TimeSpan after) => $"Error: '{url}' did not answer within {Llm.LlmTimeouts.Format(after)}";
    public static string Blocked(string url, string detail) => $"Error: '{url}' refused the request ({detail})";
    public static string Binary(string url, string mediaType, long bytes) => $"Error: '{url}' is {mediaType} ({Size(bytes)}); only web pages and text are readable";

    /// <summary>A download over the cap (2026-09-18): the size when the server declared one, else the cap alone. Pinned.</summary>
    public static string TooBig(string url, long bytes, long cap) =>
        bytes > 0 ? $"Error: '{url}' is {Size(bytes)}, over the {Size(cap)} download limit" : $"Error: '{url}' is over the {Size(cap)} download limit";

    /// <summary>A streamed download whose server sent nothing for <paramref name="after"/> (<see cref="WebFetcher.DownloadStallTimeout"/>, 2026-10-01): dropped, its partial file deleted. Pinned.</summary>
    public static string Stalled(string url, TimeSpan after) => $"Error: '{url}' sent nothing for {Llm.LlmTimeouts.Format(after)}; the download was dropped";

    /// <summary>A download whose URL ends in no file name and came with none (2026-09-18). Pinned.</summary>
    public static string NoFileName(string url) => $"Error: '{url}' names no file; give a path to save it as";
    public static string BrowserFailed(string url, string detail) => $"Error: the headless browser could not load '{url}': {detail}";
    public static string CouldNot(string url, string detail) => $"Error: could not fetch '{url}': {detail}";
    public static string OffsetPastEnd(int offset, int total) =>
        $"Error: offset {offset.ToString(CultureInfo.InvariantCulture)} is past the end of the page ({total.ToString("N0", CultureInfo.InvariantCulture)} characters)";
    public static string SearchRefused(string engine, string detail) => $"Error: {engine} refused the search ({detail}); try again in a minute";
    public static string SearxngRefused(string url) => $"Error: SearXNG at {url} refused the JSON format (HTTP 403); add json to search.formats in its settings.yml";
    public static string SearxngUnreachable(string url, string detail) => $"Error: SearXNG at {url} did not answer ({detail})";
    public static string SearchFailed(string engine, string detail) => $"Error: {engine} could not be searched ({detail})";
    public static string OpenFailed(string url, string detail) => $"Error: could not open '{url}' in your browser: {detail}";
    public static string BadUrlList(string argument, string raw) => $"Error: '{raw.Trim()}' is not a list of links for '{argument}'";
    public static string NoneOpened(int count) => $"Error: none of the {count.ToString(CultureInfo.InvariantCulture)} links could be opened";

    // ---- download_file ----

    /// <summary>
    /// A saved download (2026-09-18): <c>downloaded images/cat.png (213.4 KB, image/png) from https://example.com/cat.png</c>
    /// — <c>replaced</c> over a file that was there, <c>(no type)</c> for a server that named none, and for a picture
    /// <see cref="ViewImageNote"/>; a file outcome that is not Ok is <see cref="Files.FileText.Error"/>'s
    /// sentence with the verb <c>download</c>. Pinned.
    /// </summary>
    public static string Downloaded(Files.WriteResult result, string url, string mediaType)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(mediaType);
        if (result.Outcome != Files.FileOutcome.Ok)
        {
            return Files.FileText.Error(result.Outcome, result.Relative, "download", result.Detail);
        }

        return (result.Replaced ? "replaced " : "downloaded ") + result.Relative + " (" + Size(result.Bytes) + ", " + (mediaType.Length == 0 ? "(no type)" : mediaType) + ") from " + url
            + (Files.ImageFile.IsImagePath(result.Relative) ? ViewImageNote : "");
    }

    /// <summary>What a downloaded picture's sentence ends with. Pinned.</summary>
    public const string ViewImageNote = "; view_image shows it";

    // ---- open_url ----

    public static string OpenedOne(string url) => $"Opened {url} in your browser";
    public static string OpenedHeader(int count) => $"Opened {count.ToString(CultureInfo.InvariantCulture)} links in your browser:";
    public static string OpenedLine(string url) => "- " + url;

    /// <summary>What no headless browser adds to a blocked http result under the <c>default</c> mode. Pinned.</summary>
    public const string NoBrowserToTry = "; no headless browser was found to try instead";

    /// <summary>The sentence for a non-<see cref="FetchOutcome.Ok"/> fetch.</summary>
    public static string Error(FetchOutcome outcome, string url, string detail)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(detail);
        return outcome switch
        {
            FetchOutcome.NoUrl => NoUrl,
            FetchOutcome.NotHttp => NotHttp(url),
            FetchOutcome.LanRefused => LanRefused(detail),
            FetchOutcome.InternetRefused => InternetRefused(detail),
            FetchOutcome.Timeout => detail.Length == 0 ? Timeout(url, WebFetcher.FetchTimeout) : detail,   // a download's body under its own ceiling carries its sentence
            FetchOutcome.Blocked => Blocked(url, detail),
            FetchOutcome.Binary => detail,
            FetchOutcome.TooBig => detail,
            FetchOutcome.NoBrowser => NoBrowser,
            FetchOutcome.BrowserFailed => BrowserFailed(url, detail),
            _ => CouldNot(url, detail),
        };
    }

    // ---- results ----

    /// <summary>
    /// The first line of a fetched page: the title, the final URL, the window in characters and the
    /// engine — <c>Example Domain — https://example.com/ (chars 1–2,100 of 2,100, http)</c>; a longer
    /// page adds <c>; continue with offset 32000</c> before the engine.
    /// </summary>
    public static string FetchHeader(string title, string url, int from, int to, int total, string engine, int? nextOffset)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(engine);
        var sb = new StringBuilder();
        sb.Append(string.IsNullOrWhiteSpace(title) ? Untitled : title).Append(" — ").Append(url);
        sb.Append(" (chars ").Append(from.ToString("N0", CultureInfo.InvariantCulture)).Append('–').Append(to.ToString("N0", CultureInfo.InvariantCulture))
            .Append(" of ").Append(total.ToString("N0", CultureInfo.InvariantCulture));
        if (nextOffset is { } next)
        {
            sb.Append("; continue with offset ").Append(next.ToString(CultureInfo.InvariantCulture));
        }

        return sb.Append(", ").Append(engine).Append(')').ToString();
    }

    /// <summary>An empty page: the header alone says so.</summary>
    public static string EmptyPage(string title, string url, string engine) =>
        $"{(string.IsNullOrWhiteSpace(title) ? Untitled : title)} — {url} (no readable text, {engine})";

    public static string SearchHeader(string query, int count, string engine) =>
        $"Searched \"{query}\" ({count.ToString(CultureInfo.InvariantCulture)} {(count == 1 ? "result" : "results")}, {engine}):";

    public static string NoResults(string query, string engine) => $"Searched \"{query}\" ({engine}): no results";

    /// <summary>The numbered results under the header: <c>1. Title — url</c> and the snippet indented under it.</summary>
    public static string Results(string query, IReadOnlyList<SearchResult> results, string engine)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(results);
        if (results.Count == 0)
        {
            return NoResults(query, engine);
        }

        var sb = new StringBuilder(SearchHeader(query, results.Count, engine));
        for (int i = 0; i < results.Count; i++)
        {
            var r = results[i];
            sb.Append('\n').Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append(". ")
                .Append(string.IsNullOrWhiteSpace(r.Title) ? Untitled : r.Title).Append(" — ").Append(r.Url);
            if (!string.IsNullOrWhiteSpace(r.Snippet))
            {
                sb.Append("\n   ").Append(r.Snippet);
            }
        }

        return sb.ToString();
    }

    /// <summary>A byte count as a size: <c>213.4 KB</c>, <c>1.2 MB</c>, <c>512 B</c>. Pinned.</summary>
    public static string Size(long bytes)
    {
        if (bytes >= 1_000_000)
        {
            return (bytes / 1_000_000.0).ToString("0.#", CultureInfo.InvariantCulture) + " MB";
        }

        if (bytes >= 1_000)
        {
            return (bytes / 1_000.0).ToString("0.#", CultureInfo.InvariantCulture) + " KB";
        }

        return bytes.ToString(CultureInfo.InvariantCulture) + " B";
    }
}
