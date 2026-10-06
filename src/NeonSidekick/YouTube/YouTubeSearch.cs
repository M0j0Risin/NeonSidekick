using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using NeonSidekick.Diagnostics;
using NeonSidekick.Web;

namespace NeonSidekick.YouTube;

/// <summary>Whether a hit is live: an ordinary video, a broadcast on air now, or one announced.</summary>
public enum YouTubeLive
{
    None,
    Live,
    Upcoming,
}

/// <summary>
/// One video a search found: its id, title and channel, when it was published, how long it runs (null for a live broadcast,
/// which has no length, or when the details call failed), how often it was watched (null when the owner hides it), and
/// whether it is live.
/// </summary>
public sealed record YouTubeHit(string Id, string Title, string Channel, DateTimeOffset? Published, TimeSpan? Duration, long? Views, YouTubeLive Live = YouTubeLive.None);

/// <summary>Why a search found nothing to show.</summary>
public enum YouTubeFailure
{
    None,

    /// <summary>No key set: nothing was asked.</summary>
    NoKey,

    /// <summary>The project's daily quota is spent (403 <c>quotaExceeded</c>).</summary>
    Quota,

    /// <summary>The key is not a key, or has expired (400 <c>keyInvalid</c>, <c>API_KEY_INVALID</c>).</summary>
    KeyInvalid,

    /// <summary>YouTube Data API v3 is not enabled on the key's project (403 <c>accessNotConfigured</c>, <c>SERVICE_DISABLED</c>).</summary>
    NotEnabled,

    /// <summary>The key's restrictions refuse this caller (an HTTP referrer, IP or app restriction, <c>forbidden</c>).</summary>
    KeyRestricted,

    /// <summary>The network mode keeps the internet off limits.</summary>
    Refused,

    /// <summary>No answer within <see cref="YouTubeDataApi.Timeout"/>.</summary>
    Timeout,

    /// <summary>Anything else: a status and YouTube's own message.</summary>
    Failed,
}

/// <summary>A search's answer: the hits, or why there are none (<see cref="Failure"/>, with YouTube's own words in <see cref="Detail"/>).</summary>
public sealed record YouTubeSearchOutcome(IReadOnlyList<YouTubeHit> Hits, YouTubeFailure Failure = YouTubeFailure.None, string Detail = "")
{
    public bool Ok => Failure == YouTubeFailure.None;

    public static YouTubeSearchOutcome Failed(YouTubeFailure failure, string detail = "") => new([], failure, detail);
}

/// <summary>The search seam (2026-10-05): <see cref="YouTubeDataApi"/>, the official API with the user's key; a stub in tests.</summary>
public interface IYouTubeSearch
{
    /// <summary>At most <paramref name="max"/> videos for <paramref name="query"/>, asked with <paramref name="apiKey"/>.</summary>
    Task<YouTubeSearchOutcome> SearchAsync(string query, int max, string apiKey, CancellationToken cancellationToken);
}

/// <summary>
/// The YouTube Data API v3 (2026-10-05, the user's call: the official API with a key, no keyless scraping): <c>search.list</c>
/// for the videos (<c>type=video</c>, and <c>videoEmbeddable=true</c> so every hit plays in the video window — YouTube's player
/// answers an embed it refuses, a private, age-restricted or missing video alike, with error 150), then <c>videos.list</c> for
/// their lengths and view counts. A search costs 100 units of the project's 10,000 a day, the details one more: about a
/// hundred searches a day.
///
/// <para>The key travels in the <c>X-Goog-Api-Key</c> header, which Google's APIs read as the <c>key</c> parameter, so it is in
/// no URL the log might show. Through the web tools' client (<see cref="WebAccess.Http"/>), not exempt from
/// <see cref="LanPolicy"/>: under the network mode <c>local_area_network</c> the internet, this API with it, is off limits. A
/// failed details call keeps the hits without their lengths and counts. JSON read by hand over <see cref="JsonDocument"/>;
/// the titles come HTML-escaped (<c>&amp;amp;</c>, <c>&amp;#39;</c>) and are decoded.</para>
/// </summary>
public sealed partial class YouTubeDataApi : IYouTubeSearch
{
    public static readonly Uri Endpoint = new("https://www.googleapis.com/youtube/v3/");
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <summary>The most one call returns: <c>search.list</c>'s own limit is 50; a tool shows fewer.</summary>
    public const int MaxResults = 50;

    private const string Category = "YouTube";
    private readonly HttpClient _http;

    public YouTubeDataApi(HttpClient http) => _http = http ?? throw new ArgumentNullException(nameof(http));

    /// <summary>The key to search with under <paramref name="effective"/>, decrypted at use (<c>OpenAIApi.Key</c>'s way); null with none set.</summary>
    public static string? Key(Settings.AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return Settings.SettingsSecrets.Reveal(effective.YouTubeApiKey) is { Length: > 0 } key && !string.IsNullOrWhiteSpace(key) ? key.Trim() : null;
    }

    /// <summary>The <c>search.list</c> address for <paramref name="query"/> (no key in it). Pinned.</summary>
    public static Uri SearchUrl(string query, int max) =>
        new(Endpoint, "search?part=snippet&type=video&videoEmbeddable=true&maxResults=" + Math.Clamp(max, 1, MaxResults).ToString(CultureInfo.InvariantCulture) + "&q=" + Uri.EscapeDataString(query.Trim()));

    /// <summary>The <c>videos.list</c> address for <paramref name="ids"/>. Pinned.</summary>
    public static Uri VideosUrl(IEnumerable<string> ids) =>
        new(Endpoint, "videos?part=contentDetails,statistics&id=" + string.Join(",", ids));

    public async Task<YouTubeSearchOutcome> SearchAsync(string query, int max, string apiKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return YouTubeSearchOutcome.Failed(YouTubeFailure.NoKey);
        }

        var (body, failure) = await GetAsync(SearchUrl(query, max), apiKey, cancellationToken).ConfigureAwait(false);
        if (failure is { } refused)
        {
            DiagnosticLog.Info(Category, $"search \"{query}\": {refused.Failure} {refused.Detail}");
            return refused;
        }

        var hits = ParseSearch(body!);
        if (hits.Count > 0)
        {
            var (details, detailFailure) = await GetAsync(VideosUrl(hits.Select(h => h.Id)), apiKey, cancellationToken).ConfigureAwait(false);
            if (detailFailure is null)
            {
                hits = WithDetails(hits, ParseVideos(details!));
            }
            else
            {
                DiagnosticLog.Info(Category, $"videos.list for \"{query}\": {detailFailure.Failure} {detailFailure.Detail}; the hits kept without lengths.");
            }
        }

        DiagnosticLog.Info(Category, $"search \"{query}\": {hits.Count.ToString(CultureInfo.InvariantCulture)} videos.");
        return new YouTubeSearchOutcome(hits);
    }

    // One GET: the body, or the outcome that says why there is none.
    private async Task<(string? Body, YouTubeSearchOutcome? Failure)> GetAsync(Uri url, string apiKey, CancellationToken cancellationToken)
    {
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(Timeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            request.Headers.TryAddWithoutValidation("X-Goog-Api-Key", apiKey.Trim());
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, budget.Token).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(budget.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? (body, null) : (null, ParseError(response.StatusCode, body));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, YouTubeSearchOutcome.Failed(YouTubeFailure.Timeout));
        }
        catch (HttpRequestException ex) when (ex is NetworkRefusedException || ex.InnerException is NetworkRefusedException)
        {
            var refused = ex as NetworkRefusedException ?? (NetworkRefusedException)ex.InnerException!;
            return (null, YouTubeSearchOutcome.Failed(YouTubeFailure.Refused, refused.Message));
        }
        catch (HttpRequestException ex)
        {
            return (null, YouTubeSearchOutcome.Failed(YouTubeFailure.Failed, ex.Message));
        }
    }

    /// <summary>
    /// An error answer read: Google's <c>{"error":{"code","message","errors":[{"reason"}],"details":[{"reason"}]}}</c> into a
    /// <see cref="YouTubeFailure"/>, YouTube's message as the detail. Pure; pinned.
    /// </summary>
    public static YouTubeSearchOutcome ParseError(HttpStatusCode status, string? body)
    {
        string message = "";
        var reasons = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(body ?? "");
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                message = Text(error, "message");
                foreach (string list in new[] { "errors", "details" })
                {
                    if (error.TryGetProperty(list, out var items) && items.ValueKind == JsonValueKind.Array)
                    {
                        reasons.AddRange(items.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Object).Select(i => Text(i, "reason")).Where(r => r.Length > 0));
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Not Google's JSON (a proxy's page): the status says what it can.
        }

        bool Has(params string[] names) => reasons.Any(r => names.Contains(r, StringComparer.OrdinalIgnoreCase));
        var failure = true switch
        {
            _ when Has("quotaExceeded", "dailyLimitExceeded", "rateLimitExceeded", "RATE_LIMIT_EXCEEDED") => YouTubeFailure.Quota,
            _ when Has("keyInvalid", "keyExpired", "API_KEY_INVALID", "API_KEY_EXPIRED") || (status == HttpStatusCode.BadRequest && message.Contains("API key", StringComparison.OrdinalIgnoreCase)) => YouTubeFailure.KeyInvalid,
            _ when Has("accessNotConfigured", "SERVICE_DISABLED") => YouTubeFailure.NotEnabled,
            _ when Has("ipRefererBlocked", "API_KEY_HTTP_REFERRER_BLOCKED", "API_KEY_IP_ADDRESS_BLOCKED", "API_KEY_SERVICE_BLOCKED", "API_KEY_ANDROID_APP_BLOCKED", "API_KEY_IOS_APP_BLOCKED") => YouTubeFailure.KeyRestricted,
            _ => YouTubeFailure.Failed,
        };
        string status3 = ((int)status).ToString(CultureInfo.InvariantCulture);
        return YouTubeSearchOutcome.Failed(failure, message.Length > 0 ? $"HTTP {status3}: {message}" : $"HTTP {status3}");
    }

    /// <summary><c>search.list</c>'s <c>items[]</c>: <c>id.videoId</c> and the snippet's title, channel, date and live state. Pure; pinned.</summary>
    public static IReadOnlyList<YouTubeHit> ParseSearch(string json)
    {
        var hits = new List<YouTubeHit>();
        foreach (var item in Items(json))
        {
            if (!item.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Object || Text(id, "videoId") is not { Length: > 0 } videoId
                || !item.TryGetProperty("snippet", out var snippet) || snippet.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            DateTimeOffset? published = DateTimeOffset.TryParse(Text(snippet, "publishedAt"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when) ? when : null;
            var live = Text(snippet, "liveBroadcastContent") switch
            {
                "live" => YouTubeLive.Live,
                "upcoming" => YouTubeLive.Upcoming,
                _ => YouTubeLive.None,
            };
            hits.Add(new YouTubeHit(videoId, WebUtility.HtmlDecode(Text(snippet, "title")), WebUtility.HtmlDecode(Text(snippet, "channelTitle")), published, null, null, live));
        }

        return hits;
    }

    /// <summary><c>videos.list</c>'s <c>items[]</c>: each id's length (<c>contentDetails.duration</c>, ISO 8601) and view count. Pure; pinned.</summary>
    public static IReadOnlyDictionary<string, (TimeSpan? Duration, long? Views)> ParseVideos(string json)
    {
        var details = new Dictionary<string, (TimeSpan?, long?)>(StringComparer.Ordinal);
        foreach (var item in Items(json))
        {
            if (Text(item, "id") is not { Length: > 0 } id)
            {
                continue;
            }

            TimeSpan? duration = item.TryGetProperty("contentDetails", out var content) && content.ValueKind == JsonValueKind.Object ? ParseDuration(Text(content, "duration")) : null;
            long? views = item.TryGetProperty("statistics", out var stats) && stats.ValueKind == JsonValueKind.Object
                && long.TryParse(Text(stats, "viewCount"), NumberStyles.None, CultureInfo.InvariantCulture, out long count) ? count : null;
            details[id] = (duration, views);
        }

        return details;
    }

    /// <summary>An ISO 8601 duration as YouTube writes it (<c>PT1H2M3S</c>, <c>P1DT2H</c>, <c>PT15S</c>); null for <c>P0D</c>/<c>PT0S</c> (a live broadcast) and anything else. Pure.</summary>
    public static TimeSpan? ParseDuration(string? text)
    {
        if (text is null || Duration().Match(text) is not { Success: true } match)
        {
            return null;
        }

        static int Part(Match m, string g) => m.Groups[g].Success ? int.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture) : 0;
        var span = new TimeSpan(Part(match, "d"), Part(match, "h"), Part(match, "m"), Part(match, "s"));
        return span > TimeSpan.Zero ? span : null;
    }

    private static IReadOnlyList<YouTubeHit> WithDetails(IReadOnlyList<YouTubeHit> hits, IReadOnlyDictionary<string, (TimeSpan? Duration, long? Views)> details) =>
        hits.Select(h => details.TryGetValue(h.Id, out var d) ? h with { Duration = d.Duration, Views = d.Views } : h).ToArray();

    private static List<JsonElement> Items(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array
                ? items.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Object).Select(i => i.Clone()).ToList()
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    [GeneratedRegex(@"^P(?:(?<d>\d+)D)?(?:T(?:(?<h>\d+)H)?(?:(?<m>\d+)M)?(?:(?<s>\d+)S)?)?$", RegexOptions.CultureInvariant)]
    private static partial Regex Duration();
}
